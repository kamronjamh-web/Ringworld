using System;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>Opt-in generation-17 surface authority. Generation-16 members remain in the main partial unchanged.</summary>
public sealed partial class RingTerrainSampler
{
    /// <summary>
    /// Samples the height and shader biome weights together. Runtime terrain at
    /// exact canonical two-metre lattice vertices shares one model diagnostic
    /// evaluation; all other coordinates retain the established public calls.
    /// The streaming path can therefore use one runtime model sample per texel.
    /// The synchronous field cache can fuse the same field-owned baseline and
    /// biome calculation when it proves that this sampler owns the baseline.
    /// </summary>
    public void GetPresentationSurfaceHeightAndBiomeWeights(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        out float presentationHeight,
        out BiomeTextureWeights biomeWeights)
    {
        lock (_noiseLock)
        {
            if (UsesRuntimeSurfaceGeneration
                && (globalAroundVoxel & 1L) == 0
                && (globalAcrossVoxel & 1) == 0
                && globalAcrossVoxel >= 0
                && globalAcrossVoxel <= _ringAcrossWidthMeters - 2)
            {
                (presentationHeight, biomeWeights) = GetRuntimeLatticeHeightAndBiomeWeightsLocked(
                    RingCoordMath.WrapAround(globalAroundVoxel, _aroundRingVoxelLength), globalAcrossVoxel);
                return;
            }

            presentationHeight = GetPresentationSurfaceHeight(globalAroundVoxel, globalAcrossVoxel);
            biomeWeights = GetBiomeTextureWeights(GetTerrainSampleLocked(globalAroundVoxel, globalAcrossVoxel));
        }
    }

    private (float Height, BiomeTextureWeights Weights) GetRuntimeLatticeHeightAndBiomeWeightsLocked(
        long globalAroundVoxel,
        int globalAcrossVoxel)
    {
        TerrainGenerationHeightModel.Diagnostics diagnostics = _runtimeSurfaceModel!
            .SampleDiagnostics(globalAroundVoxel, globalAcrossVoxel);
        float presentationHeight = (float)diagnostics.Height;
        TerrainSample terrain = CreateRuntimeTerrainSampleLocked(
            globalAroundVoxel, globalAcrossVoxel, diagnostics, presentationHeight);
        return (presentationHeight, GetBiomeTextureWeights(terrain));
    }

    private TerrainSample CreateRuntimeTerrainSampleLocked(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        in TerrainGenerationHeightModel.Diagnostics diagnostics,
        float presentationHeight)
    {
        TerrainFields climate = GetRuntimeTerrainFieldsLocked(
            globalAroundVoxel, globalAcrossVoxel, diagnostics, presentationHeight);
        int terrainHeight = Mathf.FloorToInt(presentationHeight);
        SurfaceWaterKind water = presentationHeight < 0.0f ? SurfaceWaterKind.Ocean : SurfaceWaterKind.None;
        int waterSurface = water == SurfaceWaterKind.Ocean ? 0 : terrainHeight;
        TerrainBiomeId biome = ResolveBiome(climate, water);
        return new TerrainSample(
            terrainHeight,
            ClassifyRegion((float)diagnostics.Continentalness),
            (float)diagnostics.Continentalness,
            climate.Erosion,
            (float)diagnostics.RidgeWeight,
            climate.Temperature,
            climate.Moisture,
            biome,
            water,
            waterSurface);
    }

    private static (TerrainGenerationParameters? Parameters, TerrainGenerationHeightModel? Model, float AltitudeDrop)
        CreateRuntimeSurfaceGeneration(
            VoxelTerrainSettings settings,
            bool periodicAroundRing,
            long aroundRingVoxelLength,
            int ringAcrossWidthMeters,
            int maxBuildGlobalY)
    {
        if (string.IsNullOrWhiteSpace(settings.SurfaceGenerationRecipeJson))
        {
            if (!string.IsNullOrEmpty(settings.SurfaceGenerationContractId))
                throw new ArgumentException("Runtime surface generation recipe is missing.", nameof(settings));
            return (null, null, settings.AltitudeTemperatureDropPerKilometre);
        }

        if (settings.SurfaceGenerationContractId != RuntimeSurfaceGenerationContractId)
            throw new ArgumentException("Runtime surface generation contract is incompatible.", nameof(settings));
        if (!periodicAroundRing
            || aroundRingVoxelLength != (long)TerrainGenerationParameters.CertifiedCircumferenceMeters
            || ringAcrossWidthMeters != (int)TerrainGenerationParameters.CertifiedWidthMeters
            || maxBuildGlobalY != 8400)
        {
            throw new ArgumentException("Runtime surface generation requires the Certified Ring and an 8400m build ceiling.", nameof(settings));
        }
        if (settings.SeaLevelY != 0
            || settings.UsePrecisionSafePeriodicNoiseTiles
            || settings.EnableUnified3dDensityTerrain
            || settings.EnableProceduralCaves
            || settings.EnableNaturalOverhangs
            || settings.EnableDesignedCaveDensityComposition)
        {
            throw new ArgumentException("Runtime surface generation does not support legacy density, caves, or precision-safe Mega settings.", nameof(settings));
        }

        TerrainGenerationParameters parameters = TerrainGenerationParameters.FromJson(settings.SurfaceGenerationRecipeJson);
        if (unchecked((int)parameters.Seed) != settings.Seed)
            throw new ArgumentException("Surface recipe and world climate seeds must agree.", nameof(settings));
        // TerrainGenerationParameters validates the full-range, Certified, periodic v1 contract.
        return (parameters, new TerrainGenerationHeightModel(parameters), settings.AltitudeTemperatureDropPerKilometre);
    }

    private TerrainSample GetRuntimeTerrainSampleLocked(long globalAroundVoxel, int globalAcrossVoxel)
    {
        TerrainGenerationHeightModel model = _runtimeSurfaceModel!;
        TerrainGenerationHeightModel.Diagnostics diagnostics = model.SampleDiagnostics(globalAroundVoxel, globalAcrossVoxel);
        // Presentation sampling wraps before calling the model. Reuse is exact
        // only when this diagnostic already received that canonical around value.
        float presentationHeight = globalAroundVoxel >= 0
            && globalAroundVoxel < _aroundRingVoxelLength
            && (globalAroundVoxel & 1L) == 0
            && (globalAcrossVoxel & 1) == 0
            && globalAcrossVoxel >= 0
            && globalAcrossVoxel <= _ringAcrossWidthMeters - 2
            ? (float)diagnostics.Height
            : GetRuntimePresentationSurfaceHeightLocked(globalAroundVoxel, globalAcrossVoxel);
        return CreateRuntimeTerrainSampleLocked(globalAroundVoxel, globalAcrossVoxel, diagnostics, presentationHeight);
    }

    private float GetRuntimePresentationSurfaceHeightLocked(long globalAroundVoxel, int globalAcrossVoxel)
    {
        return GetRuntimePresentationSurfaceHeightLocked((double)globalAroundVoxel, globalAcrossVoxel);
    }

    private float GetRuntimePresentationSurfaceHeightLocked(double globalAroundVoxel, double globalAcrossVoxel)
    {
        const int spacing = 2;
        double wrappedAround = globalAroundVoxel % _aroundRingVoxelLength;
        if (wrappedAround < 0.0) wrappedAround += _aroundRingVoxelLength;
        double aroundSample = wrappedAround / spacing;
        long aroundIndex = checked((long)Math.Floor(aroundSample));
        double tx = aroundSample - Math.Floor(aroundSample);

        // The native gameplay field accepts canonical across coordinates through
        // width - spacing. Clamp the sampler's public border endpoint to the
        // same final lattice row so both authorities share a definition.
        double across = Math.Clamp(globalAcrossVoxel, 0.0, _ringAcrossWidthMeters - spacing);
        double acrossSample = across / spacing;
        int acrossIndex = (int)Math.Floor(acrossSample);
        double tz = acrossSample - acrossIndex;

        long around0 = RingCoordMath.WrapAround(aroundIndex * spacing, _aroundRingVoxelLength);
        long around1 = RingCoordMath.WrapAround((aroundIndex + 1L) * spacing, _aroundRingVoxelLength);
        int across0 = acrossIndex * spacing;
        int across1 = Math.Min(across0 + spacing, _ringAcrossWidthMeters - spacing);
        TerrainGenerationHeightModel model = _runtimeSurfaceModel!;
        float h00 = (float)model.CalculateHeight(around0, across0);
        // Region generation and heightfield baselines query exact vertices.
        // Avoid three redundant noise evaluations for that common path.
        if (tx == 0.0 && tz == 0.0) return h00;
        float h10 = (float)model.CalculateHeight(around1, across0);
        float h01 = (float)model.CalculateHeight(around0, across1);
        float h11 = (float)model.CalculateHeight(around1, across1);
        return RingSurfaceLattice.TriangleInterpolate(h00, h10, h01, h11, tx, tz);
    }

    private TerrainFields GetRuntimeTerrainFieldsLocked(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        in TerrainGenerationHeightModel.Diagnostics diagnostics,
        float? presentationHeight = null)
    {
        RegionalCoordinate coordinate = ResolveRegionalCoordinate(globalAroundVoxel, globalAcrossVoxel);
        TerrainFields legacyClimate = SampleTerrainFieldsLocked(globalAroundVoxel, globalAcrossVoxel, coordinate);
        float temperature = Mathf.Clamp(
            legacyClimate.Temperature
                - Mathf.Max(0.0f, presentationHeight ?? (float)diagnostics.Height) * _runtimeAltitudeTemperatureDropPerKilometre / 1000.0f,
            0.0f,
            1.0f);
        return new TerrainFields(
            (float)diagnostics.Continentalness,
            legacyClimate.Erosion,
            temperature,
            legacyClimate.Moisture,
            new MountainShape((float)diagnostics.RidgeWeight, (float)diagnostics.ValleyWeight));
    }

    private TerrainMacroRegion GetRuntimeMacroRegionLocked(long globalAroundVoxel, int globalAcrossVoxel)
    {
        int size = Mathf.Max(16_000, _settings.MacroRegionSizeMeters);
        long around = RingCoordMath.WrapAround(globalAroundVoxel, _aroundRingVoxelLength);
        long aroundRegion = PositiveModulo(FloorDiv(around, size), Math.Max(1L, (_aroundRingVoxelLength + size - 1L) / size));
        int acrossRegion = (int)FloorDiv(globalAcrossVoxel, size);
        var key = (aroundRegion, acrossRegion);
        if (_macroRegionCache.TryGetValue(key, out TerrainMacroRegion cached)) return cached;

        long centerAround = RingCoordMath.WrapAround(aroundRegion * (long)size + size / 2L, _aroundRingVoxelLength);
        int centerAcross = acrossRegion * size + size / 2;
        TerrainGenerationHeightModel.Diagnostics diagnostics = _runtimeSurfaceModel!.SampleDiagnostics(centerAround, centerAcross);
        TerrainFields fields = GetRuntimeTerrainFieldsLocked(centerAround, centerAcross, diagnostics);
        ulong flowHash = HashCell(aroundRegion, acrossRegion, unchecked((uint)_settings.Seed) ^ 0xB5297A4Du);
        float flowAngle = HashUnit(flowHash) * Mathf.Tau;
        TerrainMacroRegion created = new(
            aroundRegion, acrossRegion, centerAround, centerAcross,
            ClassifyRegion((float)diagnostics.Continentalness), (float)diagnostics.Continentalness,
            fields.Erosion, fields.Temperature, fields.Moisture, (float)diagnostics.RidgeWeight,
            Mathf.Cos(flowAngle), Mathf.Sin(flowAngle));
        _macroRegionCache[key] = created;
        _macroRegionCacheOrder.Enqueue(key);
        int capacity = Mathf.Max(16, _settings.MacroRegionCacheCapacity);
        while (_macroRegionCacheOrder.Count > capacity)
            _macroRegionCache.Remove(_macroRegionCacheOrder.Dequeue());
        return created;
    }

    private ulong CalculateRuntimeTerrainDefinitionHash(ulong sharedSettingsHash)
    {
        ulong hash = MixHash(sharedSettingsHash, RuntimeSurfaceGeneratorVersion);
        hash = MixHash(hash, _runtimeSurfaceParameters!.DefinitionHash);
        if (_runtimeSurfaceModel!.EmitterSites is { } sites)
        {
            // v5 only: the site recipe, its algorithm version and the registry identity (placement, pad heights, flags) all
            // define the terrain, so caches and saves keyed by this hash invalidate whenever any of them changes.
            hash = MixHash(hash, sites.Registry.Recipe.Hash);
            hash = MixHash(hash, (ulong)sites.Registry.Recipe.AlgorithmVersion);
            hash = MixHash(hash, sites.Registry.Identity);
        }
        foreach (char character in RuntimeSurfaceGenerationContractId)
            hash = MixHash(hash, character);
        hash = MixHash(hash, _aroundRingVoxelLength);
        hash = MixHash(hash, _ringAcrossWidthMeters);
        hash = MixHash(hash, _maxBuildGlobalY);
        foreach (char character in "triangle_lattice_2m_positive_diagonal_v1")
            hash = MixHash(hash, character);
        return hash == 0 ? 1UL : hash;
    }
}
