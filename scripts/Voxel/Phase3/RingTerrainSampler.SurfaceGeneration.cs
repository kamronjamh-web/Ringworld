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
        float presentationHeight,
        double footprintMeters = 0d)
    {
        TerrainFields climate = GetRuntimeTerrainFieldsLocked(
            globalAroundVoxel, globalAcrossVoxel, diagnostics, presentationHeight);
        int terrainHeight = Mathf.FloorToInt(presentationHeight);
        (SurfaceWaterKind water, int waterSurface, float exactLevel, bool hasExactLevel) = ClassifyRuntimeWater(diagnostics, presentationHeight, terrainHeight);
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
            waterSurface,
            diagnostics.SiteMask,
            exactLevel,
            hasExactLevel,
            HasRiverBeds ? CreateRiverBedMaskLocked(globalAroundVoxel, globalAcrossVoxel, diagnostics, presentationHeight, climate.Moisture, footprintMeters) : default);
    }

    /// <summary>True for a water.rivers world: its samples can carry inland water with a float level (S9).</summary>
    internal bool HasInlandWater => _runtimeSurfaceParameters?.HasRivers == true;

    // 0 unknown, 1 yes, -1 no: the recipe never changes after construction, so the feature lookup is done once.
    private sbyte _riverBedsState;

    /// <summary>T4-7 (D11): true for a world with a river graph and the ground.river_beds feature (bit 3). Only these worlds fill <see cref="TerrainSample.RiverBeds"/>, take land column weights on the proxies, write v14 cache records and route closed basins to the CPU.</summary>
    internal bool HasRiverBeds
    {
        get
        {
            if (_riverBedsState == 0)
                _riverBedsState = (sbyte)(_runtimeSurfaceParameters?.HasWorldFeature(RiverBedFeatureId) == true && RuntimeRiverGraph != null ? 1 : -1);
            return _riverBedsState > 0;
        }
    }

    internal const string RiverBedFeatureId = "ground.river_beds";

    // Rule P memo: the membership indicators J of the last 2 x 2 block of 1 km cells for one basin. Samplers are worker-confined and every sample is built under the noise lock.
    private bool _bedMemoValid;
    private int _bedMemoX, _bedMemoY, _bedMemoBasin;
    private int _bedMemoJ00, _bedMemoJ10, _bedMemoJ01, _bedMemoJ11;

    /// <summary>
    /// D11 decisions 4 and 5: the river/lake part from the operator's owner (kind, level, reach speed) and the playa cover of rule P, packed. Called only when <see cref="HasRiverBeds"/>.
    /// A sample with no owner and no basin costs two branches and gets the default mask.
    /// </summary>
    internal Terrain.Surface.RiverBedMask CreateRiverBedMaskLocked(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        in TerrainGenerationHeightModel.Diagnostics diagnostics,
        float presentationHeight,
        float moisture,
        double footprintMeters)
    {
        Terrain.Hydrology.InlandWaterKind kind = diagnostics.InlandWater;
        bool owned = kind != Terrain.Hydrology.InlandWaterKind.None && double.IsFinite(diagnostics.WaterLevel);
        if (!owned && diagnostics.DryBasin < 0) return default;
        Terrain.Hydrology.RiverGraph graph = RuntimeRiverGraph!;
        Terrain.Surface.RiverBedPart part = default;
        if (owned)
        {
            double g = 0d;
            if (kind == Terrain.Hydrology.InlandWaterKind.River && (uint)diagnostics.RiverReach < (uint)graph.Network.Sections.Length)
                g = Terrain.Surface.RiverBedSurfaceModel.Coarseness(graph.Network.Sections[diagnostics.RiverReach].Speed);
            part = Terrain.Surface.RiverBedSurfaceModel.RiverPart(kind, presentationHeight - diagnostics.WaterLevel, g, footprintMeters);
        }
        // Rule P: the basin of the sample's cell, else the basin of the lake there when the operator reports a Lake owner.
        int basin = diagnostics.DryBasin;
        if (basin < 0 && kind == Terrain.Hydrology.InlandWaterKind.Lake)
        {
            int lake = graph.LakeAt((double)globalAroundVoxel, (double)globalAcrossVoxel);
            basin = lake >= 0 ? graph.Lakes[lake].Basin : -1;
        }
        double playa = 0d;
        if (basin >= 0)
        {
            Terrain.Hydrology.RiverBasin b = graph.Basins[basin];
            playa = Terrain.Surface.RiverBedSurfaceModel.PlayaBand(presentationHeight, b.FloorLevel, b.SpillLevel, moisture);
            if (playa > 0d)
                playa *= Terrain.Surface.RiverBedSurfaceModel.SmoothStep(Terrain.Surface.RiverBedSurfaceModel.PlayaMembershipStart, Terrain.Surface.RiverBedSurfaceModel.PlayaMembershipEnd,
                    BasinMembershipLocked(graph, basin, globalAroundVoxel, globalAcrossVoxel));
        }
        return Terrain.Surface.RiverBedSurfaceModel.Pack(part, playa);
    }

    /// <summary>
    /// P_b of rule P: the bilinear interpolation, between the four surrounding 1 km cell centres, of J = +1 for a member cell of the basin (its dry floor or a lake of it) and -1 otherwise.
    /// Columns wrap the ring as the river index does, rows clamp to the first and last row. 0 on an edge between a member and a non-member cell.
    /// </summary>
    internal double BasinMembershipLocked(Terrain.Hydrology.RiverGraph graph, int basin, double globalAroundVoxel, double globalAcrossVoxel)
    {
        double circumference = graph.AroundStepMeters * graph.RingColumns, a = globalAroundVoxel;
        if (a < 0 || a >= circumference) { a %= circumference; if (a < 0) a += circumference; }
        double gx = a / graph.AroundStepMeters - 0.5, gy = globalAcrossVoxel / graph.AcrossStepMeters - 0.5;
        int x0 = (int)Math.Floor(gx), y0 = (int)Math.Floor(gy);
        double tx = gx - x0, ty = gy - y0;
        if (!_bedMemoValid || _bedMemoX != x0 || _bedMemoY != y0 || _bedMemoBasin != basin)
        {
            _bedMemoJ00 = BasinJ(graph, basin, x0, y0); _bedMemoJ10 = BasinJ(graph, basin, x0 + 1, y0);
            _bedMemoJ01 = BasinJ(graph, basin, x0, y0 + 1); _bedMemoJ11 = BasinJ(graph, basin, x0 + 1, y0 + 1);
            _bedMemoX = x0; _bedMemoY = y0; _bedMemoBasin = basin; _bedMemoValid = true;
        }
        return (_bedMemoJ00 * (1d - tx) + _bedMemoJ10 * tx) * (1d - ty) + (_bedMemoJ01 * (1d - tx) + _bedMemoJ11 * tx) * ty;
    }

    private static int BasinJ(Terrain.Hydrology.RiverGraph graph, int basin, int col, int row)
    {
        int rc = graph.RingColumns;
        col = ((col % rc) + rc) % rc;
        row = row < 0 ? 0 : row >= graph.Rows ? graph.Rows - 1 : row;
        if (graph.DryBasinAt(col, row) == basin) return 1;
        int lake = graph.LakeAt(col, row);
        return lake >= 0 && graph.Lakes[lake].Basin == basin ? 1 : -1;
    }

    /// <summary>
    /// S9: the one runtime water classifier, shared by the full, height-only, fused and lattice sample paths. Without rivers it is the sea rule, unchanged. With rivers, in order:
    /// operator water (River or Lake) where the presentation height is below its level, with the exact level; then the sea rule; then none. The river wins inside its channel where the natural
    /// ground is at or above 0 because the operator only owns water there (S7); beyond the natural coastline the sea rule applies at the same level 0.
    /// </summary>
    private (SurfaceWaterKind Kind, int WaterSurface, float ExactLevel, bool HasExactLevel) ClassifyRuntimeWater(
        in TerrainGenerationHeightModel.Diagnostics diagnostics,
        float presentationHeight,
        int terrainHeight)
        => ClassifyRuntimeWater(HasInlandWater, diagnostics.InlandWater, diagnostics.WaterLevel, presentationHeight, terrainHeight);

    internal static (SurfaceWaterKind Kind, int WaterSurface, float ExactLevel, bool HasExactLevel) ClassifyRuntimeWater(
        bool hasRivers,
        Terrain.Hydrology.InlandWaterKind inland,
        double operatorLevel,
        float presentationHeight,
        int terrainHeight)
    {
        if (hasRivers
            && inland != Terrain.Hydrology.InlandWaterKind.None
            && double.IsFinite(operatorLevel)
            && presentationHeight < (float)operatorLevel)
        {
            float level = (float)operatorLevel;
            return (inland == Terrain.Hydrology.InlandWaterKind.Lake ? SurfaceWaterKind.Lake : SurfaceWaterKind.River, Mathf.FloorToInt(level), level, true);
        }
        return presentationHeight < 0.0f
            ? (SurfaceWaterKind.Ocean, 0, 0.0f, false)
            : (SurfaceWaterKind.None, terrainHeight, 0.0f, false);
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
        // The constructor gives the root its own settings duplicate. Workers reuse the effective settings.
        if (parameters.IsWorldGenerationRecipe)
            ApplyRecipeClimate(settings, parameters);
        // TerrainGenerationParameters validates the full-range, Certified, periodic v1 contract.
        return (parameters, RequireRiverGraph(parameters, new TerrainGenerationHeightModel(parameters)), settings.AltitudeTemperatureDropPerKilometre);
    }

    /// <summary>S12: a water.rivers world needs its baked graph before a runtime sampler exists (the loading stage bakes it; tools install or bake one first).</summary>
    private static TerrainGenerationHeightModel RequireRiverGraph(TerrainGenerationParameters parameters, TerrainGenerationHeightModel model)
    {
        if (parameters.HasRivers && model.RiverOperator == null)
        {
            model.Dispose();
            throw new RiverGraphNotBakedException(Terrain.Hydrology.HydrologyBakeIdentity.Compute(parameters));
        }
        return model;
    }

    internal static void ApplyRecipeClimate(VoxelTerrainSettings settings, TerrainGenerationParameters recipe)
    {
        settings.ContinentalFrequency = (float)recipe.ContinentalFrequency;
        settings.ErosionFrequency = (float)recipe.ErosionFrequency;
        settings.RidgeFrequency = (float)recipe.RidgeFrequency;
        settings.TemperatureFrequency = (float)recipe.TemperatureFrequency;
        settings.MoistureFrequency = (float)recipe.MoistureFrequency;
        settings.MountainWarpFrequency = (float)recipe.MountainWarpFrequency;
        settings.ValleyFrequency = (float)recipe.ValleyFrequency;
        settings.ContinentalSecondaryStrength = (float)recipe.ContinentalSecondaryStrength;
        settings.ContinentalContrast = (float)recipe.ContinentalContrast;
        settings.ContinentalOceanBias = (float)recipe.ContinentalOceanBias;
        settings.MountainWarpDistanceMeters = (float)recipe.MountainWarpDistanceMeters;
        settings.PeakRarityExponent = (float)recipe.PeakRarityExponent;
        settings.MountainBeltBroadness = (float)recipe.MountainBeltBroadness;
        settings.ValleyNarrowness = (float)recipe.ValleyNarrowness;
        settings.OceanMoistureInfluence = (float)recipe.OceanMoistureInfluence;
        settings.ClimateLatitudeInfluence = (float)recipe.ClimateLatitudeInfluence;
        settings.CoastalBeachStrength = (float)recipe.CoastalBeachStrength;
        settings.AltitudeTemperatureDropPerKilometre = (float)recipe.AltitudeTemperatureDropPerKilometre;
        settings.MountainRainShadowStrength = (float)recipe.MountainRainShadowStrength;
        settings.MacroRegionSizeMeters = recipe.MacroRegionSizeMeters;
    }

    /// <summary>
    /// One model evaluation and one climate evaluation for a lattice column: the terrain sample and the unedited
    /// presentation height, exactly as <see cref="GetTerrainSample"/> and
    /// <see cref="GetPresentationSurfaceHeight(long, int)"/> return them separately (which costs two more model evaluations).
    /// Runtime surface generation only.
    /// </summary>
    internal void GetRuntimeTerrainSampleAndPresentationHeight(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        out TerrainSample sample,
        out float presentationHeight)
        => GetRuntimeTerrainSampleAndPresentationHeight(globalAroundVoxel, globalAcrossVoxel, 0d, out sample, out presentationHeight);

    /// <summary>
    /// S9 footprint overload: <paramref name="footprintMeters"/> is the sample spacing the caller stands for (0 = a point, the old signature; +infinity = the far views). For
    /// footprint above 0 the four lattice corners of the presentation height call the model with that footprint and the per-worker lattice memo is not used.
    /// </summary>
    internal void GetRuntimeTerrainSampleAndPresentationHeight(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        double footprintMeters,
        out TerrainSample sample,
        out float presentationHeight)
    {
        lock (_noiseLock)
        {
            sample = GetRuntimeTerrainSampleLocked(globalAroundVoxel, globalAcrossVoxel, footprintMeters, out presentationHeight);
        }
    }

    /// <summary>
    /// As <see cref="GetRuntimeTerrainSampleAndPresentationHeight"/> without the climate evaluation: the height, water and
    /// model-diagnostic fields are real, Erosion, Temperature, Moisture and Biome are left at default. For consumers
    /// that read only heights and water (halo, normal and shoreline samples).
    /// </summary>
    internal void GetRuntimeHeightOnlyTerrainSampleAndPresentationHeight(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        out TerrainSample sample,
        out float presentationHeight)
        => GetRuntimeHeightOnlyTerrainSampleAndPresentationHeight(globalAroundVoxel, globalAcrossVoxel, 0d, out sample, out presentationHeight);

    /// <summary>Footprint twin of the height-only sample (S9); the old signature means footprint 0.</summary>
    internal void GetRuntimeHeightOnlyTerrainSampleAndPresentationHeight(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        double footprintMeters,
        out TerrainSample sample,
        out float presentationHeight)
    {
        lock (_noiseLock)
        {
            TerrainGenerationHeightModel.Diagnostics diagnostics = _runtimeSurfaceModel!.SampleDiagnostics(globalAroundVoxel, globalAcrossVoxel, footprintMeters);
            RingProxyBuildCounters.AddModelEvaluations(1);
            presentationHeight = ResolveRuntimePresentationHeightLocked(globalAroundVoxel, globalAcrossVoxel, footprintMeters, diagnostics);
            int terrainHeight = Mathf.FloorToInt(presentationHeight);
            (SurfaceWaterKind water, int waterSurface, float exactLevel, bool hasExactLevel) = ClassifyRuntimeWater(diagnostics, presentationHeight, terrainHeight);
            sample = new TerrainSample(
                terrainHeight,
                ClassifyRegion((float)diagnostics.Continentalness),
                (float)diagnostics.Continentalness,
                0.0f,
                (float)diagnostics.RidgeWeight,
                0.0f,
                0.0f,
                default,
                water,
                waterSurface,
                diagnostics.SiteMask,
                exactLevel,
                hasExactLevel);
        }
    }

    private TerrainSample GetRuntimeTerrainSampleLocked(long globalAroundVoxel, int globalAcrossVoxel)
        => GetRuntimeTerrainSampleLocked(globalAroundVoxel, globalAcrossVoxel, 0d, out _);

    private float ResolveRuntimePresentationHeightLocked(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        double footprintMeters,
        in TerrainGenerationHeightModel.Diagnostics diagnostics)
    {
        // Presentation sampling wraps before calling the model. Reuse is exact
        // only when this diagnostic already received that canonical around value.
        return globalAroundVoxel >= 0
            && globalAroundVoxel < _aroundRingVoxelLength
            && (globalAroundVoxel & 1L) == 0
            && (globalAcrossVoxel & 1) == 0
            && globalAcrossVoxel >= 0
            && globalAcrossVoxel <= _ringAcrossWidthMeters - 2
            ? (float)diagnostics.Height
            : GetRuntimePresentationSurfaceHeightLocked((double)globalAroundVoxel, globalAcrossVoxel, footprintMeters);
    }

    private TerrainSample GetRuntimeTerrainSampleLocked(long globalAroundVoxel, int globalAcrossVoxel, double footprintMeters, out float presentationHeight)
    {
        TerrainGenerationHeightModel model = _runtimeSurfaceModel!;
        TerrainGenerationHeightModel.Diagnostics diagnostics = model.SampleDiagnostics(globalAroundVoxel, globalAcrossVoxel, footprintMeters);
        RingProxyBuildCounters.AddModelEvaluations(1);
        presentationHeight = ResolveRuntimePresentationHeightLocked(globalAroundVoxel, globalAcrossVoxel, footprintMeters, diagnostics);
        return CreateRuntimeTerrainSampleLocked(globalAroundVoxel, globalAcrossVoxel, diagnostics, presentationHeight, footprintMeters);
    }

    private float GetRuntimePresentationSurfaceHeightLocked(long globalAroundVoxel, int globalAcrossVoxel)
    {
        return GetRuntimePresentationSurfaceHeightLocked((double)globalAroundVoxel, globalAcrossVoxel);
    }

    private float GetRuntimePresentationSurfaceHeightLocked(double globalAroundVoxel, double globalAcrossVoxel, double footprintMeters = 0d)
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
        if (_presentationLatticeCacheEnabled && !(footprintMeters > 0d))
        {
            // Opt-in worker copies: same vertices, same (float) cast, memoised per 2 m lattice vertex.
            float c00 = GetLatticeVertexLocked(around0, across0);
            if (tx == 0.0 && tz == 0.0) return c00;
            float c10 = GetLatticeVertexLocked(around1, across0);
            float c01 = GetLatticeVertexLocked(around0, across1);
            float c11 = GetLatticeVertexLocked(around1, across1);
            return RingSurfaceLattice.TriangleInterpolate(c00, c10, c01, c11, tx, tz);
        }
        TerrainGenerationHeightModel model = _runtimeSurfaceModel!;
        // S9: footprint 0 takes the two-argument form (the old path, bit for bit); above 0 the four corners are evaluated at that footprint.
        float h00 = (float)(footprintMeters > 0d ? model.CalculateHeight(around0, across0, footprintMeters) : model.CalculateHeight(around0, across0));
        RingProxyBuildCounters.AddModelEvaluations(tx == 0.0 && tz == 0.0 ? 1 : 4);
        // Region generation and heightfield baselines query exact vertices.
        // Avoid three redundant noise evaluations for that common path.
        if (tx == 0.0 && tz == 0.0) return h00;
        float h10 = (float)(footprintMeters > 0d ? model.CalculateHeight(around1, across0, footprintMeters) : model.CalculateHeight(around1, across0));
        float h01 = (float)(footprintMeters > 0d ? model.CalculateHeight(around0, across1, footprintMeters) : model.CalculateHeight(around0, across1));
        float h11 = (float)(footprintMeters > 0d ? model.CalculateHeight(around1, across1, footprintMeters) : model.CalculateHeight(around1, across1));
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
        RingProxyBuildCounters.AddClimateEvaluation();
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
            if (sites.Climate is { } climate)
            {
                // v6 only, appended AFTER the emitter terms so the v5 hash is byte-identical: climate recipe, its algorithm version,
                // the forecourt operator version and the registry identity (pad heights, flags, offline set, anomalies).
                hash = MixHash(hash, climate.Recipe.Hash);
                hash = MixHash(hash, (ulong)climate.Recipe.AlgorithmVersion);
                hash = MixHash(hash, (ulong)Terrain.Sites.ClimateForecourtOperator.OperatorVersion);
                hash = MixHash(hash, climate.Identity);
            }
            if (sites.WeatherLung is { } weatherLung)
            {
                // v8 only, appended AFTER the climate terms so v5-v7 hashes are byte-identical: Weather Lung recipe, algorithm and operator versions and the placement identity.
                hash = MixHash(hash, weatherLung.Recipe.Hash);
                hash = MixHash(hash, (ulong)weatherLung.Recipe.AlgorithmVersion);
                hash = MixHash(hash, (ulong)Terrain.Sites.WeatherLungBasinOperator.OperatorVersion);
                hash = MixHash(hash, weatherLung.Identity);
            }
        }
        if (_runtimeSurfaceParameters.HasRivers)
        {
            // water.rivers only, appended AFTER the Weather Lung terms so every recipe without rivers keeps its hash: drainage and carve algorithm versions.
            hash = MixHash(hash, (ulong)Terrain.Hydrology.HydrologyVersions.DrainageAlgorithm);
            hash = MixHash(hash, (ulong)Terrain.Hydrology.HydrologyVersions.CarveOperator);
            // T2R-9: then the identity hash of the baked graph, so a different bake (a new drainage algorithm, other inputs) is a different definition.
            hash = MixHash(hash, _runtimeSurfaceModel.RiverOperator!.Graph.IdentityHash);
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

/// <summary>A water.rivers world's sampler was requested before its river graph was baked or installed (the loading stage bakes it; callers that build the sampler early catch this and build it on demand later).</summary>
public sealed class RiverGraphNotBakedException : InvalidOperationException
{
    /// <summary>The pre-graph input identity (D14) whose graph RiverGraphStore did not hold.</summary>
    public ulong Identity { get; }
    public RiverGraphNotBakedException(ulong identity) : base("river graph not baked") { Identity = identity; }
}
