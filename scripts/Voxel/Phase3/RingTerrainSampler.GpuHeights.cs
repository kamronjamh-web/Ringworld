using System;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Generation.Gpu;
using RingworldTechDemo.Terrain.Hydrology;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>Recipe, climate and site-influence queries for the GPU proxy height backend (docs/gpu-offload-plan.md WO-G2).</summary>
public sealed partial class RingTerrainSampler
{
    /// <summary>The runtime-generation recipe, or null for worlds that do not use runtime surface generation.</summary>
    internal TerrainGenerationParameters? RuntimeSurfaceRecipe => _runtimeSurfaceParameters;

    /// <summary>The baked river graph this sampler bound (null without the water.rivers feature).</summary>
    internal RiverGraph? RuntimeRiverGraph => _runtimeSurfaceModel?.RiverOperator?.Graph;

    /// <summary>Everything the GPU regional climate reads from this sampler, as the CPU path uses it.</summary>
    internal ProxyGpuClimateSettings? CreateGpuClimateSettings()
    {
        if (_runtimeSurfaceParameters == null) return null;
        ProxyGpuClimateSettings c = BuildGpuClimateSettings(_settings, _aroundRingVoxelLength, _ringAcrossWidthMeters, _runtimeAltitudeTemperatureDropPerKilometre);
        foreach (BiomeProfile p in _biomeProfiles)
        {
            if (p.Id is TerrainBiomeId.Ocean or TerrainBiomeId.Lake or TerrainBiomeId.River) continue;
            int channel = p.Id switch { TerrainBiomeId.Desert => 0, TerrainBiomeId.Forest => 1, TerrainBiomeId.Tundra => 2, TerrainBiomeId.Alpine => 3, _ => -1 };
            c.Profiles.Add(new ProxyGpuClimateSettings.Profile(channel, p.Priority, p.MinimumTemperature, p.MaximumTemperature, p.MinimumMoisture, p.MaximumMoisture,
                p.MinimumRidge, p.MaximumRidge, p.BlendWidth, p.SelectionWeightMultiplier));
        }
        return c;
    }

    /// <summary>
    /// The climate part of <see cref="CreateGpuClimateSettings"/> without biome profiles (moisture needs none), from effective settings (recipe climate applied) and no sampler (T2R-9, S12).
    /// </summary>
    internal static ProxyGpuClimateSettings BuildGpuClimateSettings(VoxelTerrainSettings s, long aroundLength, int width, float altitudeDrop)
        => new()
        {
            Seed = s.Seed, AroundRingLength = aroundLength, RingRadius = aroundLength / Math.Tau, RingWidth = width,
            ContinentalFrequency = s.ContinentalFrequency, ErosionFrequency = s.ErosionFrequency, RidgeFrequency = s.RidgeFrequency,
            TemperatureFrequency = s.TemperatureFrequency, MoistureFrequency = s.MoistureFrequency,
            MountainWarpFrequency = s.MountainWarpFrequency, ValleyFrequency = s.ValleyFrequency,
            ContinentalSecondaryStrength = s.ContinentalSecondaryStrength, ContinentalContrast = s.ContinentalContrast,
            ContinentalOceanBias = s.ContinentalOceanBias, MountainWarpDistanceMeters = s.MountainWarpDistanceMeters,
            PeakRarityExponent = s.PeakRarityExponent, MountainBeltBroadness = s.MountainBeltBroadness,
            ValleyNarrowness = s.ValleyNarrowness, OceanMoistureInfluence = s.OceanMoistureInfluence,
            ClimateLatitudeInfluence = s.ClimateLatitudeInfluence, CoastalBeachStrength = s.CoastalBeachStrength,
            AltitudeTemperatureDropPerKilometre = altitudeDrop,
        };

    /// <summary>
    /// Regional moisture at a point, read the way <see cref="GetTerrainSample"/> reads it, without the height model (T2R-9, S12). Meant for a climate-only sampler built in legacy mode
    /// (no recipe, no contract); on a runtime sampler it returns the same value as <c>GetTerrainSample(...).Moisture</c>.
    /// </summary>
    internal float SampleClimateMoisture(long around, int across)
    {
        lock (_noiseLock)
        {
            RegionalCoordinate coordinate = ResolveRegionalCoordinate(around, across);
            return SampleTerrainFieldsLocked(around, across, coordinate).Moisture;
        }
    }

    /// <summary>
    /// T2R-11 (S10): true when the (wrapped) rectangle may touch a river, lake or their reach: any 1 km cell overlapping it has Near set in the river index, which already holds the
    /// 650 m reach radius and the lake ring, so no segment work is needed. False without a graph. Such sectors build on the CPU model, which owns the river operator.
    /// </summary>
    internal bool IntersectsRiverInfluence(long aroundMin, long aroundMax, double acrossMin, double acrossMax)
        => RuntimeRiverGraph?.Index.AnyNear(aroundMin, aroundMax, acrossMin, acrossMax) ?? false;

    /// <summary>
    /// T2R-12 (S11): the far line coverage of a cell of the given size centred on (around, across): 0 on a world without a river graph (generation 1 and legacy rivers), so the far
    /// vertex alpha and the tile alpha keep their old values there.
    /// </summary>
    internal double RiverLineCoverage(double around, double across, double aroundSize, double acrossSize)
    {
        if (RuntimeRiverGraph is not { } graph) return 0d;
        double a0 = around - 0.5 * aroundSize, a1 = around + 0.5 * aroundSize, c0 = across - 0.5 * acrossSize, c1 = across + 0.5 * acrossSize;
        return Math.Min(1d, graph.LineArea(a0, a1, c0, c1, null, out _, out _) / (aroundSize * acrossSize));
    }

    /// <summary>
    /// T2R-12b (S11b): the valley-only channel area of the lattice of a proxy level of 256 m and coarser as coverage in [0, 1] per vertex (row-major). False without a river graph, for other
    /// spacings and where no river influences the rectangle; <paramref name="coverage"/> is then left untouched.
    /// </summary>
    internal bool TrySplatRiverLine(long aroundOrigin, long acrossOrigin, int spacing, int columns, int rows, Span<float> coverage)
    {
        if (RuntimeRiverGraph is not { } graph || !RiverFootprint.UsesProxyLine(spacing)) return false;
        double grow = spacing + RiverSpatialIndex.ReachRadiusMeters;
        long aroundMax = aroundOrigin + (long)(columns - 1) * spacing;
        double acrossMax = acrossOrigin + (double)(rows - 1) * spacing;
        if (!IntersectsRiverInfluence(aroundOrigin - (long)grow, aroundMax + (long)grow, acrossOrigin - grow, acrossMax + grow)) return false;
        return graph.SplatLineArea(aroundOrigin, acrossOrigin, spacing, columns, rows, RiverFootprint.ToMeters(RiverFootprint.ForProxySpacing(spacing)), coverage);
    }

    /// <summary>
    /// S10 as amended (T2R-12b): the far tier (far viewport, global LOD) keeps its point samples and drops the river owner, which gives the same classification as a footprint of
    /// <see cref="RiverFootprint.Far"/> (valley only, no River sample). Heights are unchanged; lakes and the ocean are untouched.
    /// </summary>
    internal TerrainSample WithoutFarRiverOwner(in TerrainSample sample)
        => RuntimeRiverGraph != null && sample.WaterKind == SurfaceWaterKind.River
            ? sample with { WaterKind = SurfaceWaterKind.None, WaterSurfaceHeight = sample.SurfaceHeight, ExactWaterLevel = 0f, HasExactWaterLevel = false }
            : sample;

    /// <summary>T2R-12: true when far meshes of this sampler carry the river line coverage in their alpha (the owners then set <c>river_line_enabled</c> on the terrain material).</summary>
    internal bool HasRiverLine => RuntimeRiverGraph != null;

    /// <summary>S11 vertex encoding: <c>alpha = 1 - c</c>, so a vertex colour of a world with no coverage keeps its alpha of 1.</summary>
    internal Godot.Color WithRiverLineAlpha(Godot.Color color, double around, double across, double aroundSize, double acrossSize)
    {
        double c = RiverLineCoverage(around, across, aroundSize, acrossSize);
        if (c > 0d) color.A = (float)(1d - c);
        return color;
    }

    /// <summary>S11 tile encoding: <c>255 - round(254 c)</c> (255 with no coverage).</summary>
    internal byte RiverLineTileAlpha(double around, double across, double aroundSize, double acrossSize)
        => (byte)(255 - (int)Math.Round(254d * RiverLineCoverage(around, across, aroundSize, acrossSize)));

    /// <summary>
    /// True when the (wrapped) rectangle [aroundMin, aroundMax] x [acrossMin, acrossMax] may touch an emitter site, Wall Engine or the
    /// Weather Lung influence region. Conservative: discs and the Weather Lung bound are tested as their bounding boxes.
    /// Such sectors stay on the CPU model, which owns the site operators (docs/gpu-offload-plan.md 4.1).
    /// </summary>
    internal bool IntersectsSiteInfluence(long aroundMin, long aroundMax, double acrossMin, double acrossMax)
    {
        if (_runtimeSurfaceModel?.EmitterSites is not { } shaper) return false;
        long circumference = _aroundRingVoxelLength;
        bool Overlaps(double centerAround, double centerAcross, double halfAround, double halfAcross)
        {
            if (centerAcross + halfAcross < acrossMin || centerAcross - halfAcross > acrossMax) return false;
            double mid = (aroundMin + aroundMax) * 0.5, half = (aroundMax - aroundMin) * 0.5;
            double delta = (centerAround - mid) % circumference;
            if (delta > circumference * 0.5) delta -= circumference;
            if (delta < -circumference * 0.5) delta += circumference;
            return Math.Abs(delta) <= half + halfAround;
        }
        foreach (RingworldTechDemo.Terrain.Sites.EmitterSite site in shaper.Registry.Sites)
            if (Overlaps(site.Around, site.Across, site.InfluenceRadius, site.InfluenceRadius)) return true;
        if (shaper.Climate != null)
            foreach (RingworldTechDemo.Terrain.Sites.ClimateMachine machine in shaper.Climate.Machines)
                if (Overlaps(machine.Around, machine.Across, machine.InfluenceRadius, machine.InfluenceRadius)) return true;
        if (shaper.WeatherLung != null)
        {
            RingworldTechDemo.Terrain.Sites.WeatherLungSite site = shaper.WeatherLung.Site;
            if (Overlaps(site.Around, site.Across, shaper.WeatherLung.Recipe.BoundHalfXM * 1.5, shaper.WeatherLung.Recipe.BoundHalfYM * 1.5)) return true;
        }
        return false;
    }
}
