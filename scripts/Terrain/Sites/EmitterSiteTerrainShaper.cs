using System;
using System.Collections.Concurrent;
using System.Threading;
using RingworldTechDemo.Terrain.Generation;

namespace RingworldTechDemo.Terrain.Sites;

/// <summary>
/// Immutable, thread-safe pairing of the emitter-site registry and the surface operator for one gen-17 v5 terrain definition
/// (docs/emitter-site-terrain-spec.md section 2.7). One instance is shared by every height model of that definition
/// (root sampler, independent worker copies, preview bridges) through a static cache, so the registry is built once.
/// The registry is always built from the UNSHAPED natural height, so shaping never recurses.
/// For v6 (<see cref="TerrainGenerationParameters.HasClimateMachines"/>) the same shaper also carries the Wall Engine registry and its
/// forecourt operator (docs/emitter-grid-climate-machines-spec.md section C3). Both lookups are O(1) and their influence regions are
/// disjoint by the climate registry invariant, so the order (emitter first, climate second) never changes a result.
/// </summary>
public sealed class EmitterSiteTerrainShaper
{
    private static readonly ConcurrentDictionary<ulong, Lazy<EmitterSiteTerrainShaper>> Cache = new();

    public EmitterSiteRegistry Registry { get; }
    public EmitterSiteSurfaceOperator Operator { get; }
    /// <summary>Wall Engine registry (null unless the recipe is v6).</summary>
    public ClimateMachineRegistry? Climate { get; }
    /// <summary>Forecourt operator matching <see cref="Climate"/> (null unless v6).</summary>
    public ClimateForecourtOperator? ClimateOperator { get; }

    /// <summary>Weather Lung placement (null unless the recipe is v8).</summary>
    public WeatherLungPlacement? WeatherLung { get; }
    /// <summary>Basin operator matching <see cref="WeatherLung"/> (null unless v8).</summary>
    public WeatherLungBasinOperator? WeatherLungOperator { get; }

    private EmitterSiteTerrainShaper(EmitterSiteRegistry registry, ClimateMachineRegistry? climate, WeatherLungPlacement? weatherLung)
    {
        Registry = registry;
        Operator = new EmitterSiteSurfaceOperator(registry.Recipe, 0d);
        Climate = climate;
        ClimateOperator = climate == null ? null : new ClimateForecourtOperator(climate.Recipe, registry.Recipe, 0d);
        WeatherLung = weatherLung;
        WeatherLungOperator = weatherLung == null ? null : new WeatherLungBasinOperator(weatherLung.Recipe, registry.Recipe, 0d);
    }

    /// <summary>Recipe the v5 generator derives from the world seed (frozen by the algorithm version).</summary>
    public static EmitterSiteRecipe RecipeFor(TerrainGenerationParameters parameters)
        => EmitterSiteRecipe.Default(parameters.Seed);

    /// <summary>Recipe the v6 generator derives from the world seed (frozen by the algorithm version).</summary>
    public static ClimateMachineRecipe ClimateRecipeFor(TerrainGenerationParameters parameters)
        => ClimateMachineRecipe.For(RecipeFor(parameters));

    /// <summary>Shared shaper for a v5/v6 definition; builds the registries on first use (parallel, cached).</summary>
    public static EmitterSiteTerrainShaper GetOrCreate(TerrainGenerationParameters parameters)
    {
        if (parameters == null) throw new ArgumentNullException(nameof(parameters));
        if (!parameters.HasEmitterSites) throw new ArgumentException("Recipe does not enable emitter sites.", nameof(parameters));
        return Cache.GetOrAdd(parameters.DefinitionHash, _ => new Lazy<EmitterSiteTerrainShaper>(() =>
        {
            using var source = new Gen17NaturalHeightSource(parameters, parameters.DefinitionHash);
            EmitterSiteRegistry emitters = EmitterSiteRegistry.GetOrBuild(RecipeFor(parameters), source);
            ClimateMachineRegistry? climate = parameters.HasClimateMachines
                ? ClimateMachineRegistry.GetOrBuild(ClimateRecipeFor(parameters), source, emitters)
                : null;
            WeatherLungPlacement? weatherLung = parameters.HasWeatherLung
                ? WeatherLungPlacement.GetOrBuild(WeatherLungRecipe.For(emitters.Recipe), emitters, ClimateMachineRecipe.For(emitters.Recipe), source)
                : null;
            return new EmitterSiteTerrainShaper(emitters, climate, weatherLung);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public static void ClearCache() => Cache.Clear();

    /// <summary>Shaped height only (no mask, no extra work outside a site's or machine's influence region).</summary>
    public double ShapeHeight(double around, double across, double naturalHeight)
    {
        if (Registry.TryGetInfluencingSite(around, across, out int index, out double du, out double dw))
        {
            EmitterSite site = Registry[index];
            double shaped = Operator.Shape(in site, du, dw, naturalHeight);
            return Math.Clamp(shaped, TerrainGenerationParameters.MinimumHeightMeters, TerrainGenerationParameters.MaximumHeightMeters);
        }
        if (Climate != null && Climate.TryGetInfluencingMachine(around, across, out int machineIndex, out double mu, out double mw))
        {
            ClimateMachine machine = Climate[machineIndex];
            double shaped = ClimateOperator!.Shape(in machine, mu, mw, naturalHeight);
            return Math.Clamp(shaped, TerrainGenerationParameters.MinimumHeightMeters, TerrainGenerationParameters.MaximumHeightMeters);
        }
        if (WeatherLung != null && WeatherLung.TryGetInfluence(around, across, out double wu, out double ww))
        {
            WeatherLungSite lung = WeatherLung.Site;
            double shaped = WeatherLungOperator!.Shape(in lung, wu, ww, naturalHeight);
            return Math.Clamp(shaped, TerrainGenerationParameters.MinimumHeightMeters, TerrainGenerationParameters.MaximumHeightMeters);
        }
        return naturalHeight;
    }

    /// <summary>
    /// Shaped height plus site data. <paramref name="siteWeight"/> is 1 on the pad and apron and fades smoothly to 0 at the
    /// influence radius; <paramref name="siteIndex"/> is -1 outside every influence disc.
    /// </summary>
    public double ShapeHeight(double around, double across, double naturalHeight,
        out SiteSurfaceMask mask, out int siteIndex, out double siteWeight)
        => ShapeHeight(around, across, naturalHeight, out mask, out siteIndex, out siteWeight, out _);

    /// <summary>
    /// As the overload above, plus <paramref name="climateMachineIndex"/> (-1 outside every Wall Engine's influence region and on non-v6 worlds).
    /// Inside a Wall Engine region <paramref name="siteIndex"/> stays -1 (it means "emitter site" only), the mask and weight describe the forecourt.
    /// </summary>
    public double ShapeHeight(double around, double across, double naturalHeight,
        out SiteSurfaceMask mask, out int siteIndex, out double siteWeight, out int climateMachineIndex)
        => ShapeHeight(around, across, naturalHeight, out mask, out siteIndex, out siteWeight, out climateMachineIndex, out _);

    /// <summary>
    /// As above, plus <paramref name="weatherLungWeight"/> (v8 only: 1 on the Weather Lung pad, apron and basin, 0 at its influence distance and on every other world).
    /// Inside the Weather Lung region <paramref name="siteIndex"/> and <paramref name="climateMachineIndex"/> stay -1, the mask and <paramref name="siteWeight"/> describe the basin.
    /// </summary>
    public double ShapeHeight(double around, double across, double naturalHeight,
        out SiteSurfaceMask mask, out int siteIndex, out double siteWeight, out int climateMachineIndex, out double weatherLungWeight)
    {
        climateMachineIndex = -1;
        weatherLungWeight = 0d;
        if (!Registry.TryGetInfluencingSite(around, across, out siteIndex, out double du, out double dw))
        {
            siteIndex = -1;
            if (Climate != null && Climate.TryGetInfluencingMachine(around, across, out int machineIndex, out double mu, out double mw))
            {
                ClimateMachine machine = Climate[machineIndex];
                double climateShaped = ClimateOperator!.Shape(in machine, mu, mw, naturalHeight, out mask);
                climateMachineIndex = machineIndex;
                siteWeight = ClimateOperator.Weight(mu, mw);
                return Math.Clamp(climateShaped, TerrainGenerationParameters.MinimumHeightMeters, TerrainGenerationParameters.MaximumHeightMeters);
            }
            if (WeatherLung != null && WeatherLung.TryGetInfluence(around, across, out double wu, out double ww))
            {
                WeatherLungSite lung = WeatherLung.Site;
                double lungShaped = WeatherLungOperator!.Shape(in lung, wu, ww, naturalHeight, out mask);
                weatherLungWeight = WeatherLungOperator.Weight(in lung, wu, ww);
                siteWeight = weatherLungWeight;
                return Math.Clamp(lungShaped, TerrainGenerationParameters.MinimumHeightMeters, TerrainGenerationParameters.MaximumHeightMeters);
            }
            mask = default; siteWeight = 0d;
            return naturalHeight;
        }
        EmitterSite site = Registry[siteIndex];
        double shaped = Operator.Shape(in site, du, dw, naturalHeight, out mask);
        double rhoStart = Registry.Recipe.ShapeFor(site.Kind).RhoStartM;
        double r = Math.Sqrt(du * du + dw * dw);
        double t = (r - rhoStart) / Math.Max(1d, site.InfluenceRadius - rhoStart);
        t = Math.Clamp(t, 0d, 1d);
        siteWeight = 1d - t * t * (3d - 2d * t);
        return Math.Clamp(shaped, TerrainGenerationParameters.MinimumHeightMeters, TerrainGenerationParameters.MaximumHeightMeters);
    }
}
