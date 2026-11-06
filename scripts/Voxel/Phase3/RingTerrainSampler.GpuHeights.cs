using System;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Generation.Gpu;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>Recipe, climate and site-influence queries for the GPU proxy height backend (docs/gpu-offload-plan.md WO-G2).</summary>
public sealed partial class RingTerrainSampler
{
    /// <summary>The runtime-generation recipe, or null for worlds that do not use runtime surface generation.</summary>
    internal TerrainGenerationParameters? RuntimeSurfaceRecipe => _runtimeSurfaceParameters;

    /// <summary>Everything the GPU regional climate reads from this sampler, as the CPU path uses it.</summary>
    internal ProxyGpuClimateSettings? CreateGpuClimateSettings()
    {
        if (_runtimeSurfaceParameters == null) return null;
        VoxelTerrainSettings s = _settings;
        var c = new ProxyGpuClimateSettings
        {
            Seed = s.Seed, AroundRingLength = _aroundRingVoxelLength, RingRadius = _aroundRingVoxelLength / Math.Tau, RingWidth = _ringAcrossWidthMeters,
            ContinentalFrequency = s.ContinentalFrequency, ErosionFrequency = s.ErosionFrequency, RidgeFrequency = s.RidgeFrequency,
            TemperatureFrequency = s.TemperatureFrequency, MoistureFrequency = s.MoistureFrequency,
            MountainWarpFrequency = s.MountainWarpFrequency, ValleyFrequency = s.ValleyFrequency,
            ContinentalSecondaryStrength = s.ContinentalSecondaryStrength, ContinentalContrast = s.ContinentalContrast,
            ContinentalOceanBias = s.ContinentalOceanBias, MountainWarpDistanceMeters = s.MountainWarpDistanceMeters,
            PeakRarityExponent = s.PeakRarityExponent, MountainBeltBroadness = s.MountainBeltBroadness,
            ValleyNarrowness = s.ValleyNarrowness, OceanMoistureInfluence = s.OceanMoistureInfluence,
            ClimateLatitudeInfluence = s.ClimateLatitudeInfluence, CoastalBeachStrength = s.CoastalBeachStrength,
            AltitudeTemperatureDropPerKilometre = _runtimeAltitudeTemperatureDropPerKilometre,
        };
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
