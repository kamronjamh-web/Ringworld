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
/// </summary>
public sealed class EmitterSiteTerrainShaper
{
    private static readonly ConcurrentDictionary<ulong, Lazy<EmitterSiteTerrainShaper>> Cache = new();

    public EmitterSiteRegistry Registry { get; }
    public EmitterSiteSurfaceOperator Operator { get; }

    private EmitterSiteTerrainShaper(EmitterSiteRegistry registry)
    {
        Registry = registry;
        Operator = new EmitterSiteSurfaceOperator(registry.Recipe, 0d);
    }

    /// <summary>Recipe the v5 generator derives from the world seed (frozen by the algorithm version).</summary>
    public static EmitterSiteRecipe RecipeFor(TerrainGenerationParameters parameters)
        => EmitterSiteRecipe.Default(parameters.Seed);

    /// <summary>Shared shaper for a v5 definition; builds the registry on first use (parallel, cached).</summary>
    public static EmitterSiteTerrainShaper GetOrCreate(TerrainGenerationParameters parameters)
    {
        if (parameters == null) throw new ArgumentNullException(nameof(parameters));
        if (!parameters.HasEmitterSites) throw new ArgumentException("Recipe does not enable emitter sites.", nameof(parameters));
        return Cache.GetOrAdd(parameters.DefinitionHash, _ => new Lazy<EmitterSiteTerrainShaper>(() =>
        {
            using var source = new Gen17NaturalHeightSource(parameters, parameters.DefinitionHash);
            return new EmitterSiteTerrainShaper(EmitterSiteRegistry.GetOrBuild(RecipeFor(parameters), source));
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public static void ClearCache() => Cache.Clear();

    /// <summary>Shaped height only (no mask, no extra work outside a site's influence disc).</summary>
    public double ShapeHeight(double around, double across, double naturalHeight)
    {
        if (!Registry.TryGetInfluencingSite(around, across, out int index, out double du, out double dw)) return naturalHeight;
        EmitterSite site = Registry[index];
        double shaped = Operator.Shape(in site, du, dw, naturalHeight);
        return Math.Clamp(shaped, TerrainGenerationParameters.MinimumHeightMeters, TerrainGenerationParameters.MaximumHeightMeters);
    }

    /// <summary>
    /// Shaped height plus site data. <paramref name="siteWeight"/> is 1 on the pad and apron and fades smoothly to 0 at the
    /// influence radius; <paramref name="siteIndex"/> is -1 outside every influence disc.
    /// </summary>
    public double ShapeHeight(double around, double across, double naturalHeight,
        out SiteSurfaceMask mask, out int siteIndex, out double siteWeight)
    {
        if (!Registry.TryGetInfluencingSite(around, across, out siteIndex, out double du, out double dw))
        {
            mask = default; siteIndex = -1; siteWeight = 0d;
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
