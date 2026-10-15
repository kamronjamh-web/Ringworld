using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>
/// Shared deterministic baseline terrain definition. Voxel chunks and every far
/// LOD must sample this class so identical canonical coordinates produce the same
/// surface and subsurface blocks at every representation level.
/// </summary>
public sealed partial class RingTerrainSampler : IDisposable
{
    // Keep the Certified Ring generator/cache contract unchanged. The Mega
    // precision domain is keyed independently by its explicit settings flag.
    // Versioned so stale global-LOD and height-pyramid caches cannot be reused
    // after the coupled continental/climate/hydrology field revision.
    public const int GeneratorVersion = 16;
    public const int RuntimeSurfaceGeneratorVersion = 17;
    public const string RuntimeSurfaceGenerationContractId = "surface_runtime_generation_v1";
    public readonly record struct HydrologyAudit(
        int DrySamples,
        int OceanSamples,
        int LakeSamples,
        int RiverSamples,
        int MaximumOceanDepth,
        int MaximumLakeDepth,
        int MaximumRiverDepth);
    public readonly record struct BiomeTerrainAudit(
        int LandSamples,
        int TundraSamples,
        int DesertSamples,
        int GrasslandSamples,
        int ForestSamples,
        int AlpineSamples,
        int BeachSamples,
        int MountainSamples,
        int ValleySamples,
        int CliffSamples);
    /// <summary>
    /// Compact texture-category weights for natural terrain mesh vertices.
    /// RGB is desert/forest/tundra and alpha is alpine; grass is the remaining
    /// weight.  Keeping this coordinate-derived makes near meshes and LODs
    /// agree without a runtime texture lookup or a per-frame climate sample.
    /// </summary>
    public readonly record struct BiomeTextureWeights(float Desert, float Forest, float Tundra, float Alpine)
    {
        public Color ToVertexColor() => new(Desert, Forest, Tundra, Alpine);
    }
    private readonly record struct MountainShape(float RidgeStrength, float ValleyStrength);
    private readonly record struct TerrainFields(
        float Continentalness,
        float Erosion,
        float Temperature,
        float Moisture,
        MountainShape Mountain);
    private readonly record struct BiomeProfile(
        TerrainBiomeId Id,
        int Priority,
        float MinimumTemperature,
        float MaximumTemperature,
        float MinimumMoisture,
        float MaximumMoisture,
        float MinimumRidge,
        float MaximumRidge,
        Color Color,
        float BlendWidth,
        float SelectionWeightMultiplier,
        float LocalReliefMeters);
    private readonly record struct BiomeBlend(TerrainBiomeId Primary, TerrainBiomeId Secondary, float SecondaryWeight);
    private readonly record struct BiomeTerrainHeight(float Height, TerrainFields Climate, BiomeBlend Blend);
    private readonly record struct RegionalCoordinate(
        float PrimaryX,
        float PrimaryY,
        float SecondaryX,
        float SecondaryY,
        float Z,
        float SecondaryWeight,
        bool Periodic,
        bool HasSecondary);
    private readonly record struct HydrologySample(int TerrainHeight, SurfaceWaterKind Kind, int WaterSurfaceHeight);
    private readonly record struct LakeCandidate(
        bool Active,
        double CenterAround,
        double CenterAcross,
        float Radius,
        int Surface,
        float MaximumDepth);
    private readonly record struct WatershedRegion(
        long AroundRegion,
        int AcrossRegion,
        long CenterAround,
        int CenterAcross,
        int DryHeight,
        int OutletOffsetAround,
        int OutletOffsetAcross,
        long OutletAround,
        int OutletAcross,
        bool HasSource,
        long SourceAround,
        int SourceAcross);

    private readonly VoxelTerrainSettings _settings;
    private readonly bool _periodicAroundRing;
    private readonly long _aroundRingVoxelLength;
    private readonly double _ringRadiusMeters;
    private readonly int _ringAcrossWidthMeters;
    private readonly int _maxBuildGlobalY;
    private readonly FastNoiseLite _baseTerrainNoise;
    private readonly FastNoiseLite _detailTerrainNoise;
    private readonly FastNoiseLite _continentalNoise;
    private readonly FastNoiseLite _erosionNoise;
    private readonly FastNoiseLite _ridgeNoise;
    private readonly FastNoiseLite _temperatureNoise;
    private readonly FastNoiseLite _moistureNoise;
    private readonly FastNoiseLite _mountainWarpAroundNoise;
    private readonly FastNoiseLite _mountainWarpAcrossNoise;
    private readonly FastNoiseLite _valleyNoise;
    private readonly FastNoiseLite _escarpmentNoise;
    private readonly FastNoiseLite _riverNoise;
    private readonly FastNoiseLite _caveNoise;
    private readonly FastNoiseLite _overhangNoise;
    private readonly BiomeProfile[] _biomeProfiles;
    private readonly Dictionary<(long X, long Z), LakeCandidate> _lakeCandidateCache = new();
    private readonly Dictionary<(long Around, int Across), TerrainMacroRegion> _macroRegionCache = new();
    private readonly Queue<(long Around, int Across)> _macroRegionCacheOrder = new();
    private readonly Dictionary<(long Around, int Across), WatershedRegion> _watershedCache = new();
    private readonly Queue<(long Around, int Across)> _watershedCacheOrder = new();
    private readonly bool _hasExceptionalSummit;
    private readonly long _exceptionalSummitAround;
    private readonly int _exceptionalSummitAcross;
    private readonly object _noiseLock = new();
    private readonly TerrainGenerationParameters? _runtimeSurfaceParameters;
    private readonly TerrainGenerationHeightModel? _runtimeSurfaceModel;
    private readonly float _runtimeAltitudeTemperatureDropPerKilometre;
    private bool _disposed;

    // Float coordinates cannot represent metre-scale detail on a 75 Gm radius
    // cylinder. Keep a local domain below this size, vary it deterministically
    // per tile, and blend neighbouring tiles across a wide overlap.
    private const long PrecisionSafeNoiseTileMeters = 16_777_216L;
    private const long PrecisionSafeNoiseBlendMeters = 1_048_576L;

    public int Seed => _settings.Seed;
    /// <summary>
    /// Designed-cave key derived exclusively from this sampler's terrain seed.
    /// Its versioned identity remains separate from legacy terrain hashing.
    /// </summary>
    public CaveGenerationContract CaveGenerationContract { get; }
    /// <summary>Immutable future cave-topology ownership partition; excluded from terrain sampling/hash.</summary>
    public CaveTopologyPartition CaveTopologyPartition { get; }
    /// <summary>Immutable v1 designed-cave eligibility mask; excluded from terrain sampling/hash.</summary>
    public CaveProvinceDistribution CaveProvinceDistribution { get; }
    public CaveChamberAnchorGenerator CaveChamberAnchorGenerator { get; }
    public CaveChamberGraphGenerator CaveChamberGraphGenerator { get; }
    public CaveCellBoundaryConnectionMapGenerator CaveCellBoundaryConnectionMapGenerator { get; }
    public CaveRegionFeatureSnapshotGenerator CaveRegionFeatureSnapshotGenerator { get; }
    public CaveWallNoiseApplicator CaveWallNoiseApplicator { get; }
    public CaveFeaturePlacementPolicy CavePlacementPolicy { get; }
    public CaveTopologyDensityComposer CaveTopologyDensityComposer { get; }
    public int TerrainGeneratorVersion => UsesRuntimeSurfaceGeneration ? RuntimeSurfaceGeneratorVersion : GeneratorVersion;
    public bool UsesRuntimeSurfaceGeneration => _runtimeSurfaceModel != null;
    public string SurfaceGenerationRecipeJson => _runtimeSurfaceParameters?.CanonicalJson ?? string.Empty;
    public string SurfaceGenerationContractId => UsesRuntimeSurfaceGeneration ? RuntimeSurfaceGenerationContractId : string.Empty;
    public float SurfaceHeightOffset => UsesRuntimeSurfaceGeneration ? 0.0f : 0.5f;
    public ulong TerrainDefinitionHash { get; }
    public long AroundRingVoxelLength => _aroundRingVoxelLength;
    public double RingRadiusMeters => _ringRadiusMeters;
    public int BedrockY => UsesRuntimeSurfaceGeneration ? -8000 : _settings.BedrockY;
    /// <summary>Read-only natural-material depth contract for editable terrain snapshots.</summary>
    public int SoilDepthVoxels => Math.Max(1, _settings.SoilDepthVoxels);
    public int SeaLevelY => UsesRuntimeSurfaceGeneration ? 0 : _settings.SeaLevelY;
    public int RingAcrossWidthMeters => _ringAcrossWidthMeters;
    public int MaximumPossibleSurfaceHeight => UsesRuntimeSurfaceGeneration ? 8000 : ResolveMaximumSurfaceHeight();
    public int RoutineMountainCeilingY => UsesRuntimeSurfaceGeneration ? 8000 : ResolveRoutineMountainCeiling();
    public bool HasExceptionalSummit => !UsesRuntimeSurfaceGeneration && _hasExceptionalSummit;
    public (long Around, int Across) ExceptionalSummitCanonical =>
        (_exceptionalSummitAround, _exceptionalSummitAcross);
    public int ExceptionalSummitSurfaceHeight => _hasExceptionalSummit
        ? GetSurfaceHeight(_exceptionalSummitAround, _exceptionalSummitAcross)
        : RoutineMountainCeilingY;

    public RingTerrainSampler CreateIndependentCopy()
    {
        return new RingTerrainSampler(
            _settings,
            _periodicAroundRing,
            _aroundRingVoxelLength,
            _ringRadiusMeters,
            _ringAcrossWidthMeters,
            _maxBuildGlobalY,
            CaveGenerationContract,
            CavePlacementPolicy,
            CaveRegionFeatureSnapshotGenerator,
            _runtimeSurfaceParameters,
            _runtimeAltitudeTemperatureDropPerKilometre,
            UsesRuntimeSurfaceGeneration ? _biomeProfiles : null);
    }

    /// <summary>
    /// Gets the large, stable ownership region for a coordinate. Fine terrain
    /// is never quantized to this result; it is a shared context for systems
    /// that must agree on regional climate, feature and hydrology decisions.
    /// </summary>
    public TerrainMacroRegion GetMacroRegion(long globalAroundVoxel, int globalAcrossVoxel)
    {
        lock (_noiseLock)
        {
            return GetMacroRegionLocked(globalAroundVoxel, globalAcrossVoxel);
        }
    }

    public RingTerrainSampler(
        VoxelTerrainSettings settings,
        bool periodicAroundRing,
        long aroundRingVoxelLength,
        double ringRadiusMeters,
        int ringAcrossWidthMeters,
        int maxBuildGlobalY,
        CaveFeaturePlacementPolicy? caveFeaturePlacementPolicy = null)
        : this(
            settings,
            periodicAroundRing,
            aroundRingVoxelLength,
            ringRadiusMeters,
            ringAcrossWidthMeters,
            maxBuildGlobalY,
            null,
            caveFeaturePlacementPolicy)
    {
    }

    private RingTerrainSampler(
        VoxelTerrainSettings settings,
        bool periodicAroundRing,
        long aroundRingVoxelLength,
        double ringRadiusMeters,
        int ringAcrossWidthMeters,
        int maxBuildGlobalY,
        CaveGenerationContract? caveGenerationContractOverride,
        CaveFeaturePlacementPolicy? caveFeaturePlacementPolicy,
        CaveRegionFeatureSnapshotGenerator? sharedRegionFeatures = null,
        TerrainGenerationParameters? runtimeSurfaceParametersOverride = null,
        float? runtimeAltitudeTemperatureDropOverride = null,
        BiomeProfile[]? biomeProfilesOverride = null)
    {
        // The root runtime sampler freezes its Godot Resource once on the main
        // thread. Independent worker copies receive that immutable instance with
        // a runtime-parameter override, so they must not call Resource.Duplicate.
        bool freezeRuntimeSettings = runtimeSurfaceParametersOverride == null
            && !string.IsNullOrWhiteSpace(settings.SurfaceGenerationRecipeJson);
        _settings = freezeRuntimeSettings
            ? settings.Duplicate(true) as VoxelTerrainSettings
                ?? throw new InvalidOperationException("Runtime surface settings could not be frozen.")
            : settings;
        if (runtimeSurfaceParametersOverride != null)
        {
            _runtimeSurfaceParameters = runtimeSurfaceParametersOverride;
            _runtimeSurfaceModel = new TerrainGenerationHeightModel(runtimeSurfaceParametersOverride);
            _runtimeAltitudeTemperatureDropPerKilometre = runtimeAltitudeTemperatureDropOverride
                ?? _settings.AltitudeTemperatureDropPerKilometre;
        }
        else
        {
            (_runtimeSurfaceParameters, _runtimeSurfaceModel, _runtimeAltitudeTemperatureDropPerKilometre) =
                CreateRuntimeSurfaceGeneration(_settings, periodicAroundRing, aroundRingVoxelLength,
                    ringAcrossWidthMeters, maxBuildGlobalY);
        }
        if (caveGenerationContractOverride is { } overrideContract)
            overrideContract.ValidateTerrainSeed(_settings.Seed);
        // Validate and freeze the terrain-seed-derived cave identity before
        // allocating native noise resources. Copies retain this exact contract
        // even if an editor Resource is mutated after construction.
        CaveGenerationContract = caveGenerationContractOverride
            ?? _settings.DesignedCaveGenerationContractSettings?.CaptureContract(_settings.Seed)
            ?? CaveGenerationContract.Create(_settings.Seed);
        _periodicAroundRing = periodicAroundRing;
        _aroundRingVoxelLength = Math.Max(1L, aroundRingVoxelLength);
        CaveTopologyPartition = new CaveTopologyPartition(CaveGenerationContract, _aroundRingVoxelLength);
        CaveProvinceDistribution = new CaveProvinceDistribution(CaveTopologyPartition);
        CaveChamberAnchorGenerator = new CaveChamberAnchorGenerator(CaveProvinceDistribution);
        CaveChamberGraphGenerator = new CaveChamberGraphGenerator(CaveChamberAnchorGenerator);
        CaveCellBoundaryConnectionMapGenerator = new CaveCellBoundaryConnectionMapGenerator(CaveChamberGraphGenerator);
        CaveRegionFeatureSnapshotGenerator = sharedRegionFeatures
            ?? new CaveRegionFeatureSnapshotGenerator(CaveCellBoundaryConnectionMapGenerator);
        if (CaveRegionFeatureSnapshotGenerator.Maps.Identity != CaveCellBoundaryConnectionMapGenerator.Identity
            || CaveRegionFeatureSnapshotGenerator.Maps.Graphs.Anchors.Distribution.Partition.Contract != CaveGenerationContract)
        {
            throw new ArgumentException("Shared cave feature cache belongs to another cave-generation contract.", nameof(sharedRegionFeatures));
        }
        CaveWallNoiseApplicator = new CaveWallNoiseApplicator(CaveGenerationContract, _aroundRingVoxelLength);
        CavePlacementPolicy = caveFeaturePlacementPolicy
            ?? CaveFeaturePlacementPolicy.UnboundedForDiagnostics;
        if (CavePlacementPolicy.AcrossWidthMeters != ringAcrossWidthMeters
            && CavePlacementPolicy.IsEnforced)
        {
            throw new ArgumentException(
                "Cave placement width must match the terrain sampler ring width.",
                nameof(caveFeaturePlacementPolicy));
        }
        CaveTopologyDensityComposer = new CaveTopologyDensityComposer(
            CaveRegionFeatureSnapshotGenerator,
            CaveWallNoiseApplicator,
            CavePlacementPolicy);
        _ringRadiusMeters = Math.Max(1.0, ringRadiusMeters);
        _ringAcrossWidthMeters = Mathf.Max(1, ringAcrossWidthMeters);
        _maxBuildGlobalY = Mathf.Max(_settings.BedrockY + 1, maxBuildGlobalY);
        _baseTerrainNoise = new FastNoiseLite
        {
            Seed = _settings.Seed,
            Frequency = Mathf.Max(0.0001f, _settings.BaseFrequency)
        };
        _detailTerrainNoise = new FastNoiseLite
        {
            Seed = _settings.Seed + 1907,
            Frequency = Mathf.Max(0.0001f, _settings.DetailFrequency)
        };
        _continentalNoise = CreateRegionalNoise(_settings.Seed + 3109, _settings.ContinentalFrequency);
        _erosionNoise = CreateRegionalNoise(_settings.Seed + 4721, _settings.ErosionFrequency);
        _ridgeNoise = CreateRegionalNoise(_settings.Seed + 6151, _settings.RidgeFrequency);
        _temperatureNoise = CreateRegionalNoise(_settings.Seed + 7919, _settings.TemperatureFrequency);
        _moistureNoise = CreateRegionalNoise(_settings.Seed + 9341, _settings.MoistureFrequency);
        _mountainWarpAroundNoise = CreateRegionalNoise(_settings.Seed + 11003, _settings.MountainWarpFrequency);
        _mountainWarpAcrossNoise = CreateRegionalNoise(_settings.Seed + 12721, _settings.MountainWarpFrequency);
        _valleyNoise = CreateRegionalNoise(_settings.Seed + 14503, _settings.ValleyFrequency);
        _escarpmentNoise = CreateRegionalNoise(_settings.Seed + 15209, _settings.EscarpmentFrequency);
        _riverNoise = CreateRegionalNoise(_settings.Seed + 16381, _settings.RiverFrequency);
        _caveNoise = CreateRegionalNoise(_settings.Seed + 18121, _settings.CaveFrequency);
        _overhangNoise = CreateRegionalNoise(_settings.Seed + 19763, _settings.OverhangFrequency);
        _biomeProfiles = biomeProfilesOverride ?? CaptureBiomeProfiles(_settings.BiomeCatalog);
        (_hasExceptionalSummit, _exceptionalSummitAround, _exceptionalSummitAcross) = UsesRuntimeSurfaceGeneration
            ? (false, 0L, 0)
            : ResolveExceptionalSummit();
        TerrainDefinitionHash = CalculateTerrainDefinitionHash();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _baseTerrainNoise.Dispose();
        _detailTerrainNoise.Dispose();
        _continentalNoise.Dispose();
        _erosionNoise.Dispose();
        _ridgeNoise.Dispose();
        _temperatureNoise.Dispose();
        _moistureNoise.Dispose();
        _mountainWarpAroundNoise.Dispose();
        _mountainWarpAcrossNoise.Dispose();
        _valleyNoise.Dispose();
        _escarpmentNoise.Dispose();
        _riverNoise.Dispose();
        _caveNoise.Dispose();
        _overhangNoise.Dispose();
        _runtimeSurfaceModel?.Dispose();
    }

    private int ResolveMaximumSurfaceHeight()
    {
        if (UsesRuntimeSurfaceGeneration) return 8000;
        int ceiling = ResolveRoutineMountainCeiling();
        if (_hasExceptionalSummit)
        {
            ceiling = Mathf.Max(ceiling, ResolveExceptionalSummitCeiling());
        }

        return ceiling;
    }

    private int ResolveRoutineMountainCeiling()
    {
        if (UsesRuntimeSurfaceGeneration) return 8000;
        int safeBuildCeiling = Mathf.Max(
            _settings.BedrockY + 1,
            _maxBuildGlobalY - Mathf.Max(0, _settings.TerrainToBuildClearanceMeters));
        return Mathf.Clamp(_settings.RarePeakY, _settings.BedrockY + 1, safeBuildCeiling);
    }

    private int ResolveExceptionalSummitCeiling()
    {
        if (UsesRuntimeSurfaceGeneration) return 8000;
        int safeBuildCeiling = Mathf.Max(
            _settings.BedrockY + 1,
            _maxBuildGlobalY - Mathf.Max(0, _settings.TerrainToBuildClearanceMeters));
        return Mathf.Clamp(_settings.ExceptionalSummitY, _settings.BedrockY + 1, safeBuildCeiling);
    }

    public int GetSurfaceHeight(long globalAroundVoxel, int globalAcrossVoxel)
    {
        if (UsesRuntimeSurfaceGeneration)
            return Mathf.FloorToInt(GetPresentationSurfaceHeight(globalAroundVoxel, globalAcrossVoxel));
        lock (_noiseLock)
        {
            RegionalCoordinate coordinate = ResolveRegionalCoordinate(globalAroundVoxel, globalAcrossVoxel);
            TerrainFields fields = SampleTerrainFieldsLocked(globalAroundVoxel, globalAcrossVoxel, coordinate);
            float preliminaryHeight = GetContinentalSurfaceHeightContinuousLocked(
                globalAroundVoxel,
                globalAcrossVoxel,
                coordinate,
                fields.Continentalness,
                fields.Erosion,
                fields.Mountain);
            BiomeTerrainHeight terrain = GetBiomeTerrainHeightLocked(coordinate, fields, preliminaryHeight);
            return ResolveHydrologyLocked(
                globalAroundVoxel,
                globalAcrossVoxel,
                coordinate,
                Mathf.FloorToInt(terrain.Height),
                terrain.Climate).TerrainHeight;
        }
    }

    public float GetSmoothSurfaceHeight(long globalAroundVoxel, int globalAcrossVoxel)
    {
        if (UsesRuntimeSurfaceGeneration)
            return GetPresentationSurfaceHeight(globalAroundVoxel, globalAcrossVoxel);
        lock (_noiseLock)
        {
            RegionalCoordinate coordinate = ResolveRegionalCoordinate(globalAroundVoxel, globalAcrossVoxel);
            TerrainFields fields = SampleTerrainFieldsLocked(globalAroundVoxel, globalAcrossVoxel, coordinate);
            float preliminaryHeight = GetContinentalSurfaceHeightContinuousLocked(
                globalAroundVoxel,
                globalAcrossVoxel,
                coordinate,
                fields.Continentalness,
                fields.Erosion,
                fields.Mountain);
            BiomeTerrainHeight terrain = GetBiomeTerrainHeightLocked(coordinate, fields, preliminaryHeight);
            HydrologySample hydrology = ResolveHydrologyLocked(
                globalAroundVoxel,
                globalAcrossVoxel,
                coordinate,
                Mathf.FloorToInt(terrain.Height),
                terrain.Climate);
            return hydrology.Kind is SurfaceWaterKind.Lake or SurfaceWaterKind.River
                ? hydrology.TerrainHeight
                : terrain.Height;
        }
    }

    /// <summary>Exact presentation surface. Runtime recipes own this value; generation 16 keeps its historic half-voxel offset.</summary>
    public float GetPresentationSurfaceHeight(long globalAroundVoxel, int globalAcrossVoxel)
    {
        if (UsesRuntimeSurfaceGeneration)
        {
            lock (_noiseLock)
            {
                return GetRuntimePresentationSurfaceHeightLocked(globalAroundVoxel, globalAcrossVoxel);
            }
        }
        return GetSmoothSurfaceHeight(globalAroundVoxel, globalAcrossVoxel) + SurfaceHeightOffset;
    }

    /// <summary>
    /// Presentation-space surface query. Runtime generation reconstructs its
    /// canonical 2m triangle lattice, while generation 16 retains its historic
    /// one-metre bilinear presentation surface.
    /// </summary>
    public float GetPresentationSurfaceHeight(double globalAroundVoxel, double globalAcrossVoxel)
    {
        if (!double.IsFinite(globalAroundVoxel) || !double.IsFinite(globalAcrossVoxel))
            throw new ArgumentOutOfRangeException("Presentation surface coordinates must be finite.");
        if (UsesRuntimeSurfaceGeneration)
        {
            lock (_noiseLock)
            {
                return GetRuntimePresentationSurfaceHeightLocked(globalAroundVoxel, globalAcrossVoxel);
            }
        }

        return GetLegacyPresentationSurfaceHeight(globalAroundVoxel, globalAcrossVoxel);
    }

    private float GetLegacyPresentationSurfaceHeight(double globalAroundVoxel, double globalAcrossVoxel)
    {
        double across = Math.Clamp(globalAcrossVoxel, 0.0, _ringAcrossWidthMeters);
        long around0 = checked((long)Math.Floor(globalAroundVoxel));
        int across0 = (int)Math.Floor(across);
        long around1 = around0 + 1;
        int across1 = Math.Min(across0 + 1, _ringAcrossWidthMeters);
        double tx = globalAroundVoxel - Math.Floor(globalAroundVoxel);
        double tz = across - across0;
        float h00 = GetPresentationSurfaceHeight(around0, across0);
        float h10 = GetPresentationSurfaceHeight(around1, across0);
        float h01 = GetPresentationSurfaceHeight(around0, across1);
        float h11 = GetPresentationSurfaceHeight(around1, across1);
        double low = h00 + (h10 - h00) * tx;
        double high = h01 + (h11 - h01) * tx;
        return (float)(low + (high - low) * tz);
    }

    /// <summary>
    /// The authoritative, unedited signed-density field for natural terrain.
    /// Negative values are terrain and positive values are air.  Features such
    /// as caves are composed into this field in the next generation phase;
    /// callers should use this rather than rebuilding a surface-height SDF.
    /// </summary>
    public float SampleNaturalTerrainDensity(long globalAroundVoxel, int globalY, int globalAcrossVoxel)
    {
        float surface = GetSmoothSurfaceHeight(globalAroundVoxel, globalAcrossVoxel);
        return SampleNaturalTerrainDensityWithSurface(
            globalAroundVoxel,
            globalY,
            globalAcrossVoxel,
            surface);
    }

    /// <summary>
    /// Density query for callers that traverse a vertical terrain column.  The
    /// macro surface is expensive regional noise and is invariant across Y, so
    /// snapshot builders compute it once per X/Z column instead of once per
    /// voxel.
    /// </summary>
    internal float SampleNaturalTerrainDensityWithSurface(
        long globalAroundVoxel,
        int globalY,
        int globalAcrossVoxel,
        float surface)
    {
        float density = globalY - (surface + SurfaceHeightOffset);
        if (!_settings.EnableUnified3dDensityTerrain
            || globalAcrossVoxel < 0
            || globalAcrossVoxel > _ringAcrossWidthMeters)
        {
            return density;
        }

        // Most designed-cave lattices are hundreds or thousands of metres
        // below the natural feature bands. Avoid entering even this sampler's
        // private noise lock when neither legacy cave channels nor near-surface
        // overhangs can alter the value.
        float depth = surface - globalY;
        bool samplesProceduralCave = _settings.EnableProceduralCaves
            && depth >= _settings.CaveMinimumDepthMeters
            && depth <= _settings.CaveMaximumDepthMeters;
        bool samplesOverhang = _settings.EnableNaturalOverhangs
            && depth > 0.0f
            && depth <= 56.0f;
        if (!samplesProceduralCave && !samplesOverhang)
            return density;

        lock (_noiseLock)
        {
            return density + SampleNaturalFeatureCarveLocked(
                globalAroundVoxel,
                globalY,
                globalAcrossVoxel,
                surface);
        }
    }

    /// <summary>
    /// Conservative interval test used by volumetric LOD range rejection.
    /// False means every Y in the closed interval follows the exact planar
    /// surface SDF and can be classified without evaluating 3D feature noise.
    /// True only declines the fast path; it never removes a terrain feature.
    /// </summary>
    internal bool NaturalFeatureBandsCanAffect(float surface, int minimumY, int maximumY)
    {
        if (!_settings.EnableUnified3dDensityTerrain)
            return false;

        float shallowestDepth = surface - maximumY;
        float deepestDepth = surface - minimumY;
        bool caveBandIntersects = _settings.EnableProceduralCaves
            && deepestDepth >= _settings.CaveMinimumDepthMeters
            && shallowestDepth <= _settings.CaveMaximumDepthMeters;
        bool overhangBandIntersects = _settings.EnableNaturalOverhangs
            && deepestDepth > 0.0f
            && shallowestDepth <= 56.0f;
        return caveBandIntersects || overhangBandIntersects;
    }

    private float SampleNaturalFeatureCarveLocked(
        long globalAroundVoxel,
        int globalY,
        int globalAcrossVoxel,
        float surface)
    {
        float depth = surface - globalY;
        if (depth <= 0.0f) return 0.0f;

        float carve = 0.0f;
        if (_settings.EnableProceduralCaves
            && depth >= _settings.CaveMinimumDepthMeters
            && depth <= _settings.CaveMaximumDepthMeters)
        {
            float caveValue = Mathf.Abs(SampleFeatureNoiseLocked(
                _caveNoise,
                globalAroundVoxel,
                globalY,
                globalAcrossVoxel));
            float channel = 1.0f - SmoothStep(
                Mathf.Clamp(_settings.CaveChannelThreshold, 0.05f, 0.95f),
                1.0f,
                caveValue);
            float depthFade = SmoothStep(
                _settings.CaveMinimumDepthMeters,
                _settings.CaveMinimumDepthMeters + 12.0f,
                depth)
                * (1.0f - SmoothStep(
                    _settings.CaveMaximumDepthMeters - 64.0f,
                    _settings.CaveMaximumDepthMeters,
                    depth));
            carve += channel * depthFade * Mathf.Max(0.0f, _settings.CaveCarveStrengthMeters);
        }

        // Near-surface lateral carving produces occasional arches and shelf
        // overhangs without moving the authoritative macro surface itself.
        if (_settings.EnableNaturalOverhangs && depth <= 56.0f)
        {
            float overhangValue = SampleFeatureNoiseLocked(
                _overhangNoise,
                globalAroundVoxel,
                globalY,
                globalAcrossVoxel);
            float shelf = SmoothStep(0.58f, 0.92f, ToUnitRange(overhangValue));
            float depthBand = SmoothStep(7.0f, 22.0f, depth)
                * (1.0f - SmoothStep(34.0f, 56.0f, depth));
            carve += shelf * depthBand * Mathf.Max(0.0f, _settings.OverhangCarveStrengthMeters);
        }

        return carve;
    }

    public TerrainSample GetTerrainSample(long globalAroundVoxel, int globalAcrossVoxel)
    {
        lock (_noiseLock)
        {
            return GetTerrainSampleLocked(globalAroundVoxel, globalAcrossVoxel);
        }
    }

    private TerrainSample GetTerrainSampleLocked(long globalAroundVoxel, int globalAcrossVoxel)
    {
        if (UsesRuntimeSurfaceGeneration)
            return GetRuntimeTerrainSampleLocked(globalAroundVoxel, globalAcrossVoxel);
        RegionalCoordinate coordinate = ResolveRegionalCoordinate(globalAroundVoxel, globalAcrossVoxel);
        TerrainFields fields = SampleTerrainFieldsLocked(globalAroundVoxel, globalAcrossVoxel, coordinate);
        TerrainRegion region = ClassifyRegion(fields.Continentalness);
        float preliminaryHeight = GetContinentalSurfaceHeightContinuousLocked(
            globalAroundVoxel,
            globalAcrossVoxel,
            coordinate,
            fields.Continentalness,
            fields.Erosion,
            fields.Mountain);
        BiomeTerrainHeight terrain = GetBiomeTerrainHeightLocked(coordinate, fields, preliminaryHeight);
        TerrainFields climateFields = terrain.Climate;
        BiomeBlend blend = terrain.Blend;
        int drySurfaceHeight = Mathf.FloorToInt(terrain.Height);
        HydrologySample hydrology = ResolveHydrologyLocked(
            globalAroundVoxel,
            globalAcrossVoxel,
            coordinate,
            drySurfaceHeight,
            climateFields);
        TerrainFields finalClimate = ApplyWaterClimate(climateFields, hydrology.Kind);
        TerrainBiomeId biome = hydrology.Kind == SurfaceWaterKind.None
            ? blend.Primary
            : ResolveBiome(finalClimate, hydrology.Kind);

        return new TerrainSample(
            hydrology.TerrainHeight,
            region,
            fields.Continentalness,
            fields.Erosion,
            fields.Mountain.RidgeStrength,
            finalClimate.Temperature,
            finalClimate.Moisture,
            biome,
            hydrology.Kind,
            hydrology.WaterSurfaceHeight);
    }

    /// <summary>
    /// Produces the macro fields together rather than treating every noise map as
    /// unrelated.  This is deliberately inexpensive: it remains safe to call
    /// for every voxel/LOD sample, is purely coordinate-derived, and therefore
    /// cannot introduce streaming seams or worker-thread ordering differences.
    /// </summary>
    private TerrainFields SampleTerrainFieldsLocked(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        in RegionalCoordinate coordinate)
    {
        float continentalBase = SampleRegionalNoiseLocked(_continentalNoise, coordinate);
        float continentalSecondary = SampleRegionalNoiseLocked(_erosionNoise, coordinate);
        float secondaryStrength = Mathf.Clamp(_settings.ContinentalSecondaryStrength, 0.0f, 0.75f);
        float continentalness = Mathf.Clamp(
            (continentalBase + continentalSecondary * secondaryStrength)
                / (1.0f + secondaryStrength)
                * _settings.ContinentalContrast
                + _settings.ContinentalOceanBias,
            -1.0f,
            1.0f);

        MountainShape mountain = SampleMountainShapeLocked(globalAroundVoxel, globalAcrossVoxel, coordinate);
        float rawErosion = ToUnitRange(continentalSecondary);
        // Wet, strongly folded terrain erodes more than broad dry plateaus.
        float rawMoisture = ToUnitRange(SampleRegionalNoiseLocked(_moistureNoise, coordinate));
        float oceanProximity = 1.0f - SmoothStep(-0.08f, 0.62f, continentalness);
        float moisture = Mathf.Clamp(
            rawMoisture * 0.72f
                + oceanProximity * Mathf.Clamp(_settings.OceanMoistureInfluence, 0.0f, 1.0f)
                + mountain.ValleyStrength * 0.08f,
            0.0f,
            1.0f);
        float erosion = Mathf.Clamp(
            rawErosion * 0.70f
                + moisture * 0.20f
                + mountain.RidgeStrength * 0.10f,
            0.0f,
            1.0f);

        float latitude = _ringAcrossWidthMeters <= 1
            ? 0.0f
            : Mathf.Abs(globalAcrossVoxel / (float)_ringAcrossWidthMeters - 0.5f) * 2.0f;
        float climateNoise = ToUnitRange(SampleRegionalNoiseLocked(_temperatureNoise, coordinate));
        float latitudeInfluence = Mathf.Clamp(_settings.ClimateLatitudeInfluence, 0.0f, 1.0f);
        float temperature = Mathf.Clamp(
            climateNoise * (1.0f - latitudeInfluence) + (1.0f - latitude) * latitudeInfluence,
            0.0f,
            1.0f);
        return new TerrainFields(continentalness, erosion, temperature, moisture, mountain);
    }

    public Color GetBiomeColor(TerrainBiomeId biome)
    {
        foreach (BiomeProfile profile in _biomeProfiles)
            if (profile.Id == biome) return profile.Color;
        return new Color(0.22f, 0.34f, 0.14f, 1.0f);
    }

    /// <summary>Blended land colour for all distant representations.  The
    /// dominant enum remains available for gameplay and decoration decisions.</summary>
    public Color GetBiomeColor(in TerrainSample sample)
    {
        if (sample.HasSurfaceWater) return GetBiomeColor(sample.Biome);
        BiomeBlend blend = ResolveBiomeBlend(new TerrainFields(
            sample.Continentalness, sample.Erosion, sample.Temperature,
            sample.Moisture, new MountainShape(sample.RidgeStrength, 0.0f)), SurfaceWaterKind.None);
        return GetBiomeColor(blend.Primary).Lerp(GetBiomeColor(blend.Secondary), blend.SecondaryWeight);
    }

    /// <summary>Returns shader-ready natural-ground weights from the same
    /// climate and soft biome selection used for terrain relief.</summary>
    public BiomeTextureWeights GetBiomeTextureWeights(long globalAroundVoxel, int globalAcrossVoxel)
    {
        lock (_noiseLock)
        {
            TerrainSample sample = GetTerrainSampleLocked(globalAroundVoxel, globalAcrossVoxel);
            return GetBiomeTextureWeights(sample);
        }
    }

    /// <summary>
    /// Returns the same shader-ready biome blend for an already sampled point.
    /// This keeps global mesh construction from resampling climate and lets its
    /// macro colours match the close proxy shader's blended texture weights.
    /// </summary>
    public BiomeTextureWeights GetBiomeTextureWeights(in TerrainSample sample)
        => ApplySiteSurfaceMask(GetNaturalBiomeTextureWeights(sample), sample.SiteMask);

    /// <summary>Emitter-site rock (pads, cut faces) shifts weight into the Alpine channel and beach sand into the Desert channel.
    /// A default mask (rock and sand both zero) returns the natural weights untouched.</summary>
    private static BiomeTextureWeights ApplySiteSurfaceMask(BiomeTextureWeights w, in Terrain.Sites.SiteSurfaceMask m)
    {
        float rock = Mathf.Clamp(MathF.Max(m.Hardscape, m.Rock), 0.0f, 1.0f), sand = Mathf.Clamp(m.Sand, 0.0f, 1.0f);
        if (rock <= 0.0f && sand <= 0.0f) return w;
        w = new BiomeTextureWeights(w.Desert * (1.0f - rock), w.Forest * (1.0f - rock), w.Tundra * (1.0f - rock), w.Alpine * (1.0f - rock) + rock);
        return new BiomeTextureWeights(w.Desert * (1.0f - sand) + sand, w.Forest * (1.0f - sand), w.Tundra * (1.0f - sand), w.Alpine * (1.0f - sand));
    }

    private BiomeTextureWeights GetNaturalBiomeTextureWeights(in TerrainSample sample)
    {
        // Reuse the fine sand surface for beaches and submerged beds so
        // ocean/lake/river terrain never falls back to dry grass.
        if (sample.HasSurfaceWater) return new BiomeTextureWeights(1.0f, 0.0f, 0.0f, 0.0f);
        BiomeBlend blend = ResolveBiomeBlend(new TerrainFields(
            sample.Continentalness, sample.Erosion, sample.Temperature,
            sample.Moisture, new MountainShape(sample.RidgeStrength, 0.0f)),
            SurfaceWaterKind.None);
        float primary = 1.0f - blend.SecondaryWeight;
        float desert = 0.0f, forest = 0.0f, tundra = 0.0f, alpine = 0.0f;
        Add(blend.Primary, primary);
        Add(blend.Secondary, blend.SecondaryWeight);
        float beachWeight = SmoothStep(-0.10f, -0.055f, sample.Continentalness)
            * (1.0f - SmoothStep(-0.008f, 0.045f, sample.Continentalness))
            * Mathf.Clamp(_settings.CoastalBeachStrength, 0.0f, 1.0f);
        float inlandWeight = 1.0f - beachWeight;
        return new BiomeTextureWeights(
            desert * inlandWeight + beachWeight,
            forest * inlandWeight,
            tundra * inlandWeight,
            alpine * inlandWeight);

        void Add(TerrainBiomeId biome, float weight)
        {
            switch (biome)
            {
                case TerrainBiomeId.Desert: desert += weight; break;
                case TerrainBiomeId.Forest: forest += weight; break;
                case TerrainBiomeId.Tundra: tundra += weight; break;
                case TerrainBiomeId.Alpine: alpine += weight; break;
            }
        }
    }

    private TerrainBiomeId ResolveBiome(in TerrainFields fields, SurfaceWaterKind water)
    {
        if (water == SurfaceWaterKind.Ocean) return TerrainBiomeId.Ocean;
        if (water == SurfaceWaterKind.Lake) return TerrainBiomeId.Lake;
        if (water == SurfaceWaterKind.River) return TerrainBiomeId.River;
        return ResolveBiomeBlend(fields, water).Primary;
    }

    private BiomeBlend ResolveBiomeBlend(in TerrainFields fields, SurfaceWaterKind water)
    {
        if (water == SurfaceWaterKind.Ocean) return new(TerrainBiomeId.Ocean, TerrainBiomeId.Ocean, 0.0f);
        if (water == SurfaceWaterKind.Lake) return new(TerrainBiomeId.Lake, TerrainBiomeId.Lake, 0.0f);
        if (water == SurfaceWaterKind.River) return new(TerrainBiomeId.River, TerrainBiomeId.River, 0.0f);
        BiomeProfile? best = null;
        BiomeProfile? second = null;
        float bestWeight = -1.0f;
        float secondWeight = -1.0f;
        foreach (BiomeProfile profile in _biomeProfiles)
        {
            if (profile.Id is TerrainBiomeId.Ocean or TerrainBiomeId.Lake or TerrainBiomeId.River) continue;
            float width = Mathf.Max(0.01f, profile.BlendWidth);
            float weight = RangeWeight(fields.Temperature, profile.MinimumTemperature, profile.MaximumTemperature, width)
                * RangeWeight(fields.Moisture, profile.MinimumMoisture, profile.MaximumMoisture, width)
                * RangeWeight(fields.Mountain.RidgeStrength, profile.MinimumRidge, profile.MaximumRidge, width);
            // Priority only settles deliberately overlapping profiles; it no
            // longer turns a whole boundary into a hard switch.
            weight *= Mathf.Clamp(profile.SelectionWeightMultiplier, 0.01f, 2.0f)
                * (1.0f + Mathf.Max(0, profile.Priority) * 0.0025f);
            if (weight > bestWeight)
            {
                second = best; secondWeight = bestWeight;
                best = profile; bestWeight = weight;
            }
            else if (weight > secondWeight)
            {
                second = profile; secondWeight = weight;
            }
        }
        TerrainBiomeId primary = best?.Id ?? TerrainBiomeId.Grassland;
        TerrainBiomeId secondary = second?.Id ?? primary;
        float total = Mathf.Max(0.0001f, bestWeight + Mathf.Max(0.0f, secondWeight));
        return new BiomeBlend(primary, secondary, Mathf.Clamp(Mathf.Max(0.0f, secondWeight) / total, 0.0f, 0.49f));
    }

    private static float RangeWeight(float value, float minimum, float maximum, float width)
        => SmoothStep(minimum - width, minimum + width, value)
            * (1.0f - SmoothStep(maximum - width, maximum + width, value));

    private TerrainFields ApplyAltitudeClimate(in TerrainFields fields, float surfaceHeight)
    {
        float elevation = Mathf.Max(0.0f, surfaceHeight - _settings.SeaLevelY);
        float lapse = elevation / 1000.0f * Mathf.Clamp(_settings.AltitudeTemperatureDropPerKilometre, 0.0f, 0.35f);
        // High ridges also receive a small drying correction. This is a cheap,
        // deterministic climate proxy rather than a runtime weather simulation.
        float rainShadow = fields.Mountain.RidgeStrength * elevation / 6000.0f
            * Mathf.Clamp(_settings.MountainRainShadowStrength, 0.0f, 0.6f);
        return fields with
        {
            Temperature = Mathf.Clamp(fields.Temperature - lapse, 0.0f, 1.0f),
            Moisture = Mathf.Clamp(fields.Moisture - rainShadow, 0.0f, 1.0f)
        };
    }

    private TerrainFields ApplyWaterClimate(in TerrainFields fields, SurfaceWaterKind water)
    {
        if (water == SurfaceWaterKind.None) return fields;
        float bonus = water == SurfaceWaterKind.River ? 0.16f : 0.10f;
        return fields with { Moisture = Mathf.Clamp(fields.Moisture + bonus, 0.0f, 1.0f) };
    }

    private float GetBiomeLocalReliefLocked(in RegionalCoordinate coordinate, in BiomeBlend blend)
    {
        float primary = GetBiomeRelief(blend.Primary);
        float secondary = GetBiomeRelief(blend.Secondary);
        float relief = Mathf.Lerp(primary, secondary, blend.SecondaryWeight);
        if (relief <= 0.0f) return 0.0f;
        float detail = SampleRegionalNoiseLocked(_detailTerrainNoise, coordinate);
        return detail * relief;
    }

    private BiomeTerrainHeight GetBiomeTerrainHeightLocked(
        in RegionalCoordinate coordinate,
        in TerrainFields fields,
        float preliminaryHeight)
    {
        TerrainFields climate = ApplyAltitudeClimate(fields, preliminaryHeight);
        BiomeBlend blend = ResolveBiomeBlend(climate, SurfaceWaterKind.None);
        return new BiomeTerrainHeight(preliminaryHeight + GetBiomeLocalReliefLocked(coordinate, blend), climate, blend);
    }

    private float GetBiomeRelief(TerrainBiomeId id)
    {
        foreach (BiomeProfile profile in _biomeProfiles)
            if (profile.Id == id) return profile.LocalReliefMeters;
        return 0.0f;
    }

    private static BiomeProfile[] CaptureBiomeProfiles(TerrainBiomeCatalog? catalog)
    {
        var profiles = new List<BiomeProfile>();
        if (catalog != null)
        {
            foreach (TerrainBiomeDefinition? definition in catalog.Biomes)
            {
                if (definition == null) continue;
                profiles.Add(new BiomeProfile(definition.Id, definition.Priority,
                    definition.MinimumTemperature, definition.MaximumTemperature,
                    definition.MinimumMoisture, definition.MaximumMoisture,
                    definition.MinimumRidgeStrength, definition.MaximumRidgeStrength,
                    definition.SurfaceColor, definition.BlendWidth,
                    definition.SelectionWeightMultiplier, definition.LocalReliefMeters));
            }
        }
        if (profiles.Count > 0) return profiles.ToArray();
        return new[]
        {
            new BiomeProfile(TerrainBiomeId.Tundra, 30, 0f, .28f, 0f, 1f, 0f, .75f, new Color(.60f,.64f,.57f,1f), .12f, 1f, 20f),
            new BiomeProfile(TerrainBiomeId.Desert, 20, .35f, 1f, 0f, .30f, 0f, .70f, new Color(.51f,.42f,.22f,1f), .12f, 1f, 32f),
            new BiomeProfile(TerrainBiomeId.Forest, 20, .28f, .82f, .58f, 1f, 0f, .62f, new Color(.10f,.30f,.12f,1f), .12f, 1f, 16f),
            new BiomeProfile(TerrainBiomeId.Alpine, 40, 0f, 1f, 0f, 1f, .62f, 1f, new Color(.38f,.39f,.40f,1f), .10f, 1f, 52f),
            new BiomeProfile(TerrainBiomeId.Grassland, 0, 0f, 1f, 0f, 1f, 0f, 1f, new Color(.22f,.34f,.14f,1f), .16f, .22f, 10f)
        };
    }

    private TerrainMacroRegion GetMacroRegionLocked(long globalAroundVoxel, int globalAcrossVoxel)
    {
        if (UsesRuntimeSurfaceGeneration)
            return GetRuntimeMacroRegionLocked(globalAroundVoxel, globalAcrossVoxel);
        int size = Mathf.Max(16_000, _settings.MacroRegionSizeMeters);
        long around = _periodicAroundRing
            ? RingCoordMath.WrapAround(globalAroundVoxel, _aroundRingVoxelLength)
            : globalAroundVoxel;
        long aroundRegion = FloorDiv(around, size);
        int acrossRegion = (int)FloorDiv(globalAcrossVoxel, size);
        long regionCount = Math.Max(1L, (_aroundRingVoxelLength + size - 1L) / size);
        if (_periodicAroundRing)
            aroundRegion = PositiveModulo(aroundRegion, regionCount);
        var key = (aroundRegion, acrossRegion);
        if (_macroRegionCache.TryGetValue(key, out TerrainMacroRegion cached))
            return cached;

        long centerAround = aroundRegion * (long)size + size / 2L;
        if (_periodicAroundRing)
            centerAround = RingCoordMath.WrapAround(centerAround, _aroundRingVoxelLength);
        int centerAcross = acrossRegion * size + size / 2;
        RegionalCoordinate centerCoordinate = ResolveRegionalCoordinate(centerAround, centerAcross);
        TerrainFields fields = SampleTerrainFieldsLocked(centerAround, centerAcross, centerCoordinate);
        ulong flowHash = HashCell(aroundRegion, acrossRegion, unchecked((uint)_settings.Seed) ^ 0xB5297A4Du);
        float flowAngle = HashUnit(flowHash) * Mathf.Tau;
        TerrainMacroRegion created = new(
            aroundRegion,
            acrossRegion,
            centerAround,
            centerAcross,
            ClassifyRegion(fields.Continentalness),
            fields.Continentalness,
            fields.Erosion,
            fields.Temperature,
            fields.Moisture,
            fields.Mountain.RidgeStrength,
            Mathf.Cos(flowAngle),
            Mathf.Sin(flowAngle));
        _macroRegionCache[key] = created;
        _macroRegionCacheOrder.Enqueue(key);
        int capacity = Mathf.Max(16, _settings.MacroRegionCacheCapacity);
        while (_macroRegionCacheOrder.Count > capacity)
        {
            (long Around, int Across) expired = _macroRegionCacheOrder.Dequeue();
            _macroRegionCache.Remove(expired);
        }

        return created;
    }

    private bool TryResolveWatershedRiverLocked(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        int drySurfaceHeight,
        in TerrainFields fields,
        out float strength)
    {
        strength = 0.0f;
        int size = Mathf.Max(16_000, _settings.MacroRegionSizeMeters);
        WatershedRegion region = GetWatershedRegionLocked(globalAroundVoxel, globalAcrossVoxel);
        if (region.OutletOffsetAround == 0 && region.OutletOffsetAcross == 0)
            return false;

        float halfWidth = Mathf.Max(20.0f, _settings.WatershedRiverHalfWidthMeters);
        if (region.HasSource)
        {
            strength = Mathf.Max(strength, SampleRiverSegmentStrength(
                globalAroundVoxel, globalAcrossVoxel,
                region.SourceAround, region.SourceAcross,
                region.OutletAround, region.OutletAcross,
                halfWidth));
        }

        // Every higher adjacent region joins this region at its deterministic
        // edge crossing, then shares this region's outlet. This is the actual
        // confluence rule; water lines do not terminate at a random noise cell.
        foreach ((int dx, int dz) in CardinalDirections)
        {
            if (globalAcrossVoxel + dz * size < 0
                || globalAcrossVoxel + dz * size > _ringAcrossWidthMeters)
                continue;
            WatershedRegion neighbor = GetWatershedRegionLocked(
                globalAroundVoxel + dx * (long)size,
                globalAcrossVoxel + dz * size);
            if (neighbor.OutletOffsetAround != -dx || neighbor.OutletOffsetAcross != -dz)
                continue;
            strength = Mathf.Max(strength, SampleRiverSegmentStrength(
                globalAroundVoxel, globalAcrossVoxel,
                neighbor.OutletAround, neighbor.OutletAcross,
                region.OutletAround, region.OutletAcross,
                halfWidth));
        }

        if (strength <= 0.0f || drySurfaceHeight <= _settings.SeaLevelY + 2)
            return false;
        strength = Mathf.Pow(strength, Mathf.Max(1.0f, _settings.RiverBankSharpness));
        return strength > 0.01f;
    }

    private WatershedRegion GetWatershedRegionLocked(long globalAroundVoxel, int globalAcrossVoxel)
    {
        int size = Mathf.Max(16_000, _settings.MacroRegionSizeMeters);
        long around = _periodicAroundRing
            ? RingCoordMath.WrapAround(globalAroundVoxel, _aroundRingVoxelLength)
            : globalAroundVoxel;
        long aroundRegion = FloorDiv(around, size);
        int acrossRegion = (int)FloorDiv(globalAcrossVoxel, size);
        long regionCount = Math.Max(1L, (_aroundRingVoxelLength + size - 1L) / size);
        if (_periodicAroundRing) aroundRegion = PositiveModulo(aroundRegion, regionCount);
        var key = (aroundRegion, acrossRegion);
        if (_watershedCache.TryGetValue(key, out WatershedRegion cached)) return cached;

        long centerAround = aroundRegion * (long)size + size / 2L;
        if (_periodicAroundRing) centerAround = RingCoordMath.WrapAround(centerAround, _aroundRingVoxelLength);
        int centerAcross = acrossRegion * size + size / 2;
        int centerHeight = SampleDrySurfaceHeightLocked(centerAround, centerAcross);
        int bestHeight = centerHeight;
        int outletDx = 0, outletDz = 0;
        foreach ((int dx, int dz) in CardinalDirections)
        {
            int neighborAcross = centerAcross + dz * size;
            if (neighborAcross < 0 || neighborAcross > _ringAcrossWidthMeters) continue;
            int neighborHeight = SampleDrySurfaceHeightLocked(centerAround + dx * (long)size, neighborAcross);
            if (neighborHeight < bestHeight)
            {
                bestHeight = neighborHeight;
                outletDx = dx;
                outletDz = dz;
            }
        }

        ulong hash = HashCell(aroundRegion, acrossRegion, unchecked((uint)_settings.Seed) ^ 0x68E31DA4u);
        bool hasSource = outletDx != 0 || outletDz != 0
            ? HashUnit(hash) <= Mathf.Clamp(_settings.WatershedSourceChance, 0.0f, 1.0f)
            : false;
        (long outletAround, int outletAcross) = ResolveWatershedBoundary(
            aroundRegion, acrossRegion, outletDx, outletDz, size);
        long sourceAround = centerAround + Mathf.RoundToInt((HashUnit(HashMix(hash + 1UL)) - 0.5f) * size * 0.55f);
        int sourceAcross = centerAcross + Mathf.RoundToInt((HashUnit(HashMix(hash + 2UL)) - 0.5f) * size * 0.55f);
        sourceAcross = Mathf.Clamp(sourceAcross, 0, _ringAcrossWidthMeters);
        if (_periodicAroundRing) sourceAround = RingCoordMath.WrapAround(sourceAround, _aroundRingVoxelLength);
        WatershedRegion created = new(
            aroundRegion, acrossRegion, centerAround, centerAcross, centerHeight,
            outletDx, outletDz, outletAround, outletAcross,
            hasSource, sourceAround, sourceAcross);
        _watershedCache[key] = created;
        _watershedCacheOrder.Enqueue(key);
        int capacity = Mathf.Max(16, _settings.MacroRegionCacheCapacity);
        while (_watershedCacheOrder.Count > capacity)
            _watershedCache.Remove(_watershedCacheOrder.Dequeue());
        return created;
    }

    private (long Around, int Across) ResolveWatershedBoundary(
        long regionAround, int regionAcross, int dx, int dz, int size)
    {
        if (dx == 0 && dz == 0) return (0L, 0);
        long neighborAround = regionAround + dx;
        int neighborAcross = regionAcross + dz;
        long lowAround = Math.Min(regionAround, neighborAround);
        int lowAcross = Math.Min(regionAcross, neighborAcross);
        ulong hash = HashCell(lowAround, lowAcross, unchecked((uint)_settings.Seed) ^ 0x1B873593u);
        float along = 0.25f + HashUnit(hash) * 0.5f;
        long around = dx != 0
            ? (dx > 0 ? (regionAround + 1L) * size : regionAround * (long)size)
            : regionAround * (long)size + Mathf.RoundToInt(along * size);
        int across = dz != 0
            ? (dz > 0 ? (regionAcross + 1) * size : regionAcross * size)
            : regionAcross * size + Mathf.RoundToInt(along * size);
        if (_periodicAroundRing) around = RingCoordMath.WrapAround(around, _aroundRingVoxelLength);
        return (around, Mathf.Clamp(across, 0, _ringAcrossWidthMeters));
    }

    private int SampleDrySurfaceHeightLocked(long around, int across)
    {
        RegionalCoordinate coordinate = ResolveRegionalCoordinate(around, across);
        TerrainFields fields = SampleTerrainFieldsLocked(around, across, coordinate);
        return GetContinentalSurfaceHeightLocked(
            around, across, coordinate, fields.Continentalness, fields.Erosion, fields.Mountain);
    }

    private float SampleRiverSegmentStrength(
        long around, int across,
        long startAround, int startAcross,
        long endAround, int endAcross,
        float halfWidth)
    {
        double ax = SignedAroundDelta(around, startAround);
        double az = startAcross - across;
        double bx = SignedAroundDelta(around, endAround);
        double bz = endAcross - across;
        double abx = bx - ax, abz = bz - az;
        double lengthSquared = abx * abx + abz * abz;
        double t = lengthSquared <= 0.001 ? 0.0 : Math.Clamp(-(ax * abx + az * abz) / lengthSquared, 0.0, 1.0);
        double dx = ax + abx * t, dz = az + abz * t;
        float distance = (float)Math.Sqrt(dx * dx + dz * dz);
        return 1.0f - SmoothStep(halfWidth * 0.45f, halfWidth, distance);
    }

    private double SignedAroundDelta(long origin, long target)
    {
        long delta = target - origin;
        if (!_periodicAroundRing) return delta;
        long half = _aroundRingVoxelLength / 2L;
        if (delta > half) delta -= _aroundRingVoxelLength;
        else if (delta < -half) delta += _aroundRingVoxelLength;
        return delta;
    }

    private static readonly (int dx, int dz)[] CardinalDirections =
    {
        (-1, 0), (1, 0), (0, -1), (0, 1)
    };

    private HydrologySample ResolveHydrologyLocked(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        in RegionalCoordinate coordinate,
        int drySurfaceHeight,
        in TerrainFields fields)
    {
        if (drySurfaceHeight < _settings.SeaLevelY)
        {
            return new HydrologySample(
                drySurfaceHeight,
                SurfaceWaterKind.Ocean,
                _settings.SeaLevelY);
        }

        if (_settings.EnableLakes
            && TryResolveLakeLocked(globalAroundVoxel, globalAcrossVoxel, out int lakeBed, out int lakeSurface))
        {
            return new HydrologySample(
                Mathf.Max(_settings.BedrockY + 1, lakeBed),
                SurfaceWaterKind.Lake,
                lakeSurface);
        }

        if (_settings.EnableRivers
            && drySurfaceHeight > _settings.SeaLevelY + 2
            && drySurfaceHeight <= _settings.RiverMaximumSurfaceY
            && fields.Continentalness > -0.05f
            && fields.Moisture >= _settings.RiverMinimumMoisture
            && TryResolveWatershedRiverLocked(
                globalAroundVoxel,
                globalAcrossVoxel,
                drySurfaceHeight,
                fields,
                out float riverStrength))
        {
            float moistureGate = SmoothStep(
                _settings.RiverMinimumMoisture,
                Mathf.Min(1.0f, _settings.RiverMinimumMoisture + 0.22f),
                fields.Moisture);
            float terrainGate = Mathf.Lerp(0.65f, 1.0f, 1.0f - fields.Erosion);
            float strength = Mathf.Clamp(riverStrength * moistureGate * terrainGate, 0.0f, 1.0f);
            if (strength > 0.08f)
            {
                int depth = Mathf.RoundToInt(Mathf.Lerp(
                    _settings.RiverMinimumDepthMeters,
                    Mathf.Max(_settings.RiverMinimumDepthMeters, _settings.RiverMaximumDepthMeters),
                    strength));
                int bankCut = Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(1.0f, 5.0f, strength)));
                int waterSurface = drySurfaceHeight - bankCut;
                int riverBed = Mathf.Max(_settings.BedrockY + 1, waterSurface - depth);
                return new HydrologySample(riverBed, SurfaceWaterKind.River, waterSurface);
            }
        }

        return new HydrologySample(drySurfaceHeight, SurfaceWaterKind.None, drySurfaceHeight);
    }

    private bool TryResolveLakeLocked(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        out int lakeBed,
        out int lakeSurface)
    {
        lakeBed = 0;
        lakeSurface = 0;
        int cellSize = Mathf.Max(1000, _settings.LakeCellSizeMeters);
        long wrappedAround = _periodicAroundRing
            ? RingCoordMath.WrapAround(globalAroundVoxel, _aroundRingVoxelLength)
            : globalAroundVoxel;
        long aroundCellCount = Math.Max(1L, (_aroundRingVoxelLength + cellSize - 1L) / cellSize);
        long baseCellX = FloorDiv(wrappedAround, cellSize);
        long baseCellZ = FloorDiv(globalAcrossVoxel, cellSize);
        float bestInfluence = 0.0f;

        for (long dz = -1; dz <= 1; dz++)
        for (long dx = -1; dx <= 1; dx++)
        {
            long rawCellX = baseCellX + dx;
            long cellX = _periodicAroundRing
                ? PositiveModulo(rawCellX, aroundCellCount)
                : rawCellX;
            long cellZ = baseCellZ + dz;
            if (!_lakeCandidateCache.TryGetValue((cellX, cellZ), out LakeCandidate candidate))
            {
                candidate = BuildLakeCandidateLocked(cellX, cellZ, cellSize);
                _lakeCandidateCache[(cellX, cellZ)] = candidate;
            }
            if (!candidate.Active)
            {
                continue;
            }

            double deltaAround = Math.Abs(wrappedAround - candidate.CenterAround);
            if (_periodicAroundRing)
            {
                deltaAround = Math.Min(deltaAround, _aroundRingVoxelLength - deltaAround);
            }
            double deltaAcross = globalAcrossVoxel - candidate.CenterAcross;
            float normalizedDistance = (float)(Math.Sqrt(
                deltaAround * deltaAround + deltaAcross * deltaAcross) / Math.Max(1.0f, candidate.Radius));
            if (normalizedDistance >= 1.0f)
            {
                continue;
            }

            float influence = 1.0f - SmoothStep(0.72f, 1.0f, normalizedDistance);
            if (influence <= bestInfluence)
            {
                continue;
            }

            float bowl = Mathf.Pow(Mathf.Clamp(1.0f - normalizedDistance, 0.0f, 1.0f), 0.65f);
            lakeSurface = candidate.Surface;
            lakeBed = candidate.Surface - Mathf.Max(1, Mathf.RoundToInt(candidate.MaximumDepth * bowl));
            bestInfluence = influence;
        }

        return bestInfluence > 0.0f;
    }

    private LakeCandidate BuildLakeCandidateLocked(long cellX, long cellZ, int cellSize)
    {
        ulong hash = HashCell(cellX, cellZ, unchecked((uint)_settings.Seed) ^ 0x71C9A4D3u);
        if (HashUnit(hash) >= Mathf.Clamp(_settings.LakeSpawnChance, 0.0f, 1.0f))
        {
            return default;
        }

        float offsetX = 0.18f + HashUnit(HashMix(hash + 1UL)) * 0.64f;
        float offsetZ = 0.18f + HashUnit(HashMix(hash + 2UL)) * 0.64f;
        double centerAround = cellX * (double)cellSize + offsetX * cellSize;
        if (_periodicAroundRing)
        {
            centerAround %= _aroundRingVoxelLength;
        }
        double centerAcross = cellZ * (double)cellSize + offsetZ * cellSize;
        float radius = Mathf.Lerp(
            Mathf.Max(100.0f, _settings.LakeMinimumRadiusMeters),
            Mathf.Max(_settings.LakeMinimumRadiusMeters, _settings.LakeMaximumRadiusMeters),
            HashUnit(HashMix(hash + 3UL)));
        if (centerAcross < radius || centerAcross > _ringAcrossWidthMeters - radius)
        {
            return default;
        }

        long centerAroundVoxel = (long)Math.Round(centerAround);
        int centerAcrossVoxel = (int)Math.Round(centerAcross);
        RegionalCoordinate centerCoordinate = ResolveRegionalCoordinate(centerAroundVoxel, centerAcrossVoxel);
        TerrainFields centerFields = SampleTerrainFieldsLocked(
            centerAroundVoxel,
            centerAcrossVoxel,
            centerCoordinate);
        TerrainMacroRegion macroRegion = GetMacroRegionLocked(centerAroundVoxel, centerAcrossVoxel);
        if (!macroRegion.SupportsInlandWater
            || centerFields.Continentalness <= -0.04f
            || centerFields.Continentalness >= 0.66f
            || centerFields.Moisture < 0.36f
            || centerFields.Mountain.RidgeStrength > 0.55f)
        {
            return default;
        }

        int centerDryHeight = GetContinentalSurfaceHeightLocked(
            centerAroundVoxel,
            centerAcrossVoxel,
            centerCoordinate,
            centerFields.Continentalness,
            centerFields.Erosion,
            centerFields.Mountain);
        int quantum = Mathf.Max(1, _settings.LakeLevelQuantumMeters);
        int surface = Mathf.RoundToInt((centerDryHeight - 4) / (float)quantum) * quantum;
        if (surface <= _settings.SeaLevelY + 2 || surface > _settings.LakeMaximumSurfaceY)
        {
            return default;
        }

        float maximumDepth = Mathf.Lerp(
            _settings.LakeMinimumDepthMeters,
            Mathf.Max(_settings.LakeMinimumDepthMeters, _settings.LakeMaximumDepthMeters),
            HashUnit(HashMix(hash + 4UL)));
        return new LakeCandidate(true, centerAround, centerAcross, radius, surface, maximumDepth);
    }

    private int GetContinentalSurfaceHeightLocked(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        in RegionalCoordinate coordinate,
        float continentalness,
        float erosion,
        in MountainShape mountain) => Mathf.FloorToInt(GetContinentalSurfaceHeightContinuousLocked(
            globalAroundVoxel,
            globalAcrossVoxel,
            coordinate,
            continentalness,
            erosion,
            mountain));

    private float GetContinentalSurfaceHeightContinuousLocked(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        in RegionalCoordinate coordinate,
        float continentalness,
        float erosion,
        in MountainShape mountain)
    {
        float regionalHeight = continentalness switch
        {
            < -0.45f => RemapSmooth(continentalness, -1.0f, -0.45f, _settings.DeepOceanFloorY, _settings.OceanShelfFloorY),
            < -0.18f => RemapSmooth(continentalness, -0.45f, -0.18f, _settings.OceanShelfFloorY, _settings.CoastLowY),
            < -0.10f => RemapSmooth(continentalness, -0.18f, -0.10f, _settings.CoastLowY, _settings.SeaLevelY),
            < -0.05f => RemapSmooth(continentalness, -0.10f, -0.05f, _settings.SeaLevelY, _settings.CoastHighY),
            < 0.35f => RemapSmooth(continentalness, -0.05f, 0.35f, _settings.CoastHighY, _settings.LowlandHighY),
            < 0.62f => RemapSmooth(continentalness, 0.35f, 0.62f, _settings.LowlandHighY, _settings.HighlandHighY),
            _ => RemapSmooth(continentalness, 0.62f, 1.0f, _settings.HighlandHighY, _settings.MountainFoundationHighY)
        };

        float baseDetail = SampleRegionalNoiseLocked(_baseTerrainNoise, coordinate)
            * _settings.HeightAmplitudeVoxels;
        float fineDetail = SampleRegionalNoiseLocked(_detailTerrainNoise, coordinate)
            * _settings.DetailAmplitudeVoxels;
        float erosionDetailFactor = Mathf.Lerp(1.0f, 0.25f, erosion);
        // A broad sand/pebble shelf makes the coast read as a beach instead of
        // allowing inland noise to run straight into sea level.  Its smooth
        // continentalness mask is seam-safe and cannot make a height step.
        float beachInfluence = SmoothStep(-0.10f, -0.055f, continentalness)
            * (1.0f - SmoothStep(-0.008f, 0.045f, continentalness));
        float detail = (baseDetail + fineDetail)
            * erosionDetailFactor
            * Mathf.Clamp(_settings.RegionalDetailStrength, 0.0f, 1.0f)
            * Mathf.Lerp(1.0f, 0.18f, beachInfluence * Mathf.Clamp(_settings.CoastalBeachStrength, 0.0f, 1.0f));
        // Base-surface bias fades in above the shoreline, so sea level remains
        // an absolute world datum instead of moving with a terrain knob.
        float landBias = (_settings.BaseSurfaceHeightVoxels - 16)
            * SmoothStep(-0.05f, 0.35f, continentalness);
        float terrainHeight = regionalHeight + detail + landBias;
        terrainHeight = Mathf.Lerp(terrainHeight, Mathf.Max(_settings.SeaLevelY + 2.0f, regionalHeight),
            beachInfluence * Mathf.Clamp(_settings.CoastalBeachStrength, 0.0f, 1.0f));
        int routineMountainCeiling = ResolveRoutineMountainCeiling();
        float mountainMask = SmoothStep(_settings.MountainMaskStart, _settings.MountainMaskFull, continentalness);
        float erosionStrength = Mathf.Lerp(1.0f, _settings.ErodedMountainStrength, erosion);
        float ridgeUplift = Mathf.Max(0.0f, routineMountainCeiling - terrainHeight)
            * mountainMask
            * mountain.RidgeStrength
            * erosionStrength;
        float valleyCarve = _settings.ValleyDepthVoxels
            * mountainMask
            * mountain.ValleyStrength
            * Mathf.Lerp(1.0f, 0.45f, erosion);
        terrainHeight += ridgeUplift - valleyCarve;
        // A separate low-frequency field creates occasional cliff faces and
        // escarpments. Smooth thresholding keeps both the height and normal
        // continuous; the coast mask deliberately preserves beaches.
        float escarpmentField = ToUnitRange(SampleRegionalNoiseLocked(_escarpmentNoise, coordinate));
        float escarpmentHalfWidth = Mathf.Lerp(
            0.14f,
            0.012f,
            Mathf.Clamp(_settings.EscarpmentSharpness, 0.0f, 1.0f));
        float escarpmentBand = SmoothStep(
            0.70f - escarpmentHalfWidth,
            0.70f + escarpmentHalfWidth,
            escarpmentField);
        float escarpmentLand = SmoothStep(0.18f, 0.58f, continentalness)
            * (1.0f - beachInfluence);
        float ceilingRoom = Mathf.Max(0.0f, ResolveRoutineMountainCeiling() - terrainHeight);
        terrainHeight += Mathf.Min(ceilingRoom, Mathf.Max(0.0f, _settings.EscarpmentHeightMeters))
            * escarpmentBand * escarpmentLand;
        terrainHeight = ApplyExceptionalSummit(globalAroundVoxel, globalAcrossVoxel, terrainHeight);
        terrainHeight = ApplyEngineeredMacroTerrain(
            globalAroundVoxel,
            globalAcrossVoxel,
            terrainHeight);

        float safeMinimum = Mathf.Max(_settings.BedrockY + 1, _settings.DeepOceanFloorY);
        float safeMaximum = MaximumPossibleSurfaceHeight;
        return Mathf.Clamp(terrainHeight, safeMinimum, safeMaximum);
    }

    private float ApplyEngineeredMacroTerrain(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        float terrainHeight)
    {
        if (!_settings.EnableEngineeredMacroTerrain || _ringAcrossWidthMeters < 10_000)
        {
            return terrainHeight;
        }

        // One broad, intentionally placed inland sea makes a reliable visual
        // landmark. The rest of the ring remains predominantly continental.
        double aroundDistance = GetPeriodicAroundDistance(globalAroundVoxel, 0L);
        double acrossCenter = _ringAcrossWidthMeters * 0.5;
        double acrossDistance = globalAcrossVoxel - acrossCenter;
        double seaAlongRadius = Math.Max(1_000.0, _settings.InlandSeaHalfWidthMeters * 2.4);
        double seaAcrossRadius = Math.Max(1_000.0, _settings.InlandSeaHalfWidthMeters);
        double seaDistance = Math.Sqrt(
            aroundDistance * aroundDistance / (seaAlongRadius * seaAlongRadius)
            + acrossDistance * acrossDistance / (seaAcrossRadius * seaAcrossRadius));
        double shoreTransition = Math.Max(1_000.0, _settings.InlandSeaShoreTransitionMeters)
            / Math.Min(seaAlongRadius, seaAcrossRadius);
        float seaInfluence = 1.0f - SmoothStep(1.0f, 1.0f + (float)shoreTransition, (float)seaDistance);
        float basinFloor = Mathf.Max(
            _settings.BedrockY + 1,
            _settings.SeaLevelY - Mathf.Max(180, _settings.CoastalTerraceHeightMeters + 350));
        terrainHeight = Mathf.Lerp(terrainHeight, basinFloor, seaInfluence);

        // Raised shoreline terraces reserve broad, buildable habitat above the
        // waterline without looking artificial from walking distance.
        float terraceDistance = Mathf.Abs((float)Math.Abs(acrossDistance) - (float)seaAcrossRadius);
        float terraceHalfWidth = Mathf.Max(1_000.0f, _settings.CoastalTerraceHalfWidthMeters);
        float terraceInfluence = 1.0f - SmoothStep(
            terraceHalfWidth,
            terraceHalfWidth + Mathf.Max(1_000.0f, _settings.CoastalTerraceDistanceMeters * 0.25f),
            terraceDistance);
        terraceInfluence *= 1.0f - seaInfluence;
        float terraceTarget = _settings.SeaLevelY + Mathf.Max(0, _settings.CoastalTerraceHeightMeters);
        terrainHeight = Mathf.Lerp(terrainHeight, Mathf.Max(terrainHeight, terraceTarget), terraceInfluence * 0.72f);

        // Slow arc-aligned escarpments give flight-scale structure while the
        // pre-existing noise and erosion retain natural local variation.
        double wavelength = Math.Max(1_000_000.0, _settings.ArcEscarpmentWavelengthMeters);
        float arcWave = (float)((Math.Sin(globalAroundVoxel / wavelength * Math.Tau) + 1.0) * 0.5);
        float landInfluence = SmoothStep(0.0f, 1.0f, terrainHeight / Mathf.Max(1.0f, terraceTarget));
        terrainHeight += arcWave
            * Mathf.Max(0, _settings.ArcEscarpmentHeightMeters)
            * landInfluence
            * (1.0f - seaInfluence);
        return terrainHeight;
    }

    private long GetPeriodicAroundDistance(long around, long center)
    {
        long wrappedAround = _periodicAroundRing
            ? RingCoordMath.WrapAround(around, _aroundRingVoxelLength)
            : around;
        long distance = Math.Abs(wrappedAround - center);
        return _periodicAroundRing
            ? Math.Min(distance, _aroundRingVoxelLength - distance)
            : distance;
    }

    private MountainShape SampleMountainShapeLocked(
        long globalAroundVoxel,
        int globalAcrossVoxel,
        in RegionalCoordinate coordinate)
    {
        float warpDistance = Mathf.Max(0.0f, _settings.MountainWarpDistanceMeters);
        float aroundWarp = SampleRegionalNoiseLocked(_mountainWarpAroundNoise, coordinate) * warpDistance;
        float acrossWarp = SampleRegionalNoiseLocked(_mountainWarpAcrossNoise, coordinate) * warpDistance * 0.65f;
        long warpedAround = globalAroundVoxel + Mathf.RoundToInt(aroundWarp);
        int warpedAcross = globalAcrossVoxel + Mathf.RoundToInt(acrossWarp);
        RegionalCoordinate warpedCoordinate = ResolveRegionalCoordinate(warpedAround, warpedAcross);

        float ridgeNoise = SampleRegionalNoiseLocked(_ridgeNoise, warpedCoordinate);
        float ridgeBase = Mathf.Clamp(1.0f - Mathf.Abs(ridgeNoise), 0.0f, 1.0f);
        float sharpRidge = Mathf.Pow(ridgeBase, Mathf.Max(1.0f, _settings.PeakRarityExponent));
        float broadBelt = Mathf.Pow(
            ridgeBase,
            Mathf.Max(1.5f, _settings.PeakRarityExponent * 0.28f));
        float beltBroadness = Mathf.Clamp(_settings.MountainBeltBroadness, 0.0f, 1.0f);
        float ridgeStrength = Mathf.Lerp(sharpRidge, broadBelt, beltBroadness);

        float valleyNoise = SampleRegionalNoiseLocked(_valleyNoise, warpedCoordinate);
        float valleyBase = Mathf.Clamp(1.0f - Mathf.Abs(valleyNoise), 0.0f, 1.0f);
        float valleyStrength = Mathf.Pow(valleyBase, Mathf.Max(1.0f, _settings.ValleyNarrowness));
        return new MountainShape(ridgeStrength, valleyStrength);
    }

    private float ApplyExceptionalSummit(long globalAroundVoxel, int globalAcrossVoxel, float terrainHeight)
    {
        if (!_hasExceptionalSummit)
        {
            return terrainHeight;
        }

        long around = _periodicAroundRing
            ? RingCoordMath.WrapAround(globalAroundVoxel, _aroundRingVoxelLength)
            : globalAroundVoxel;
        long aroundDistance = Math.Abs(around - _exceptionalSummitAround);
        if (_periodicAroundRing)
        {
            aroundDistance = Math.Min(aroundDistance, _aroundRingVoxelLength - aroundDistance);
        }

        double acrossDistance = globalAcrossVoxel - _exceptionalSummitAcross;
        double distance = Math.Sqrt(aroundDistance * (double)aroundDistance + acrossDistance * acrossDistance);
        double radius = Math.Max(1.0, _settings.ExceptionalSummitRadiusMeters);
        if (distance >= radius)
        {
            return terrainHeight;
        }

        float falloff = 1.0f - (float)(distance / radius);
        falloff = SmoothStep(0.0f, 1.0f, falloff);
        falloff = Mathf.Pow(falloff, Mathf.Max(1.0f, _settings.ExceptionalSummitFalloffExponent));
        return Mathf.Lerp(terrainHeight, ResolveExceptionalSummitCeiling(), falloff);
    }

    private (bool Enabled, long Around, int Across) ResolveExceptionalSummit()
    {
        if (_settings.ExceptionalSummitCount <= 0)
        {
            return (false, 0, 0);
        }

        int radius = Mathf.CeilToInt(Mathf.Max(1.0f, _settings.ExceptionalSummitRadiusMeters));
        int margin = Mathf.Min(radius, Mathf.Max(0, _ringAcrossWidthMeters / 3));
        int acrossSpan = Mathf.Max(1, _ringAcrossWidthMeters - margin * 2);
        int candidateCount = Mathf.Clamp(_settings.ExceptionalSummitCandidateCount, 8, 256);
        uint state = unchecked((uint)_settings.Seed) ^ 0xA511E9B3u;
        float bestScore = float.NegativeInfinity;
        long bestAround = 0;
        int bestAcross = _ringAcrossWidthMeters / 2;

        for (int candidate = 0; candidate < candidateCount; candidate++)
        {
            uint aroundBits = NextDeterministic(ref state);
            uint acrossBits = NextDeterministic(ref state);
            long around = (long)(HashMix(aroundBits) % (ulong)_aroundRingVoxelLength);
            int across = margin + (int)(acrossBits % (uint)acrossSpan);
            float continentalness = SampleRegionalNoiseLocked(_continentalNoise, around, across);
            if (continentalness <= bestScore)
            {
                continue;
            }

            bestScore = continentalness;
            bestAround = around;
            bestAcross = across;
        }

        return (true, bestAround, bestAcross);
    }

    private static uint NextDeterministic(ref uint state)
    {
        state += 0x9E3779B9u;
        uint value = state;
        value = (value ^ (value >> 16)) * 0x85EBCA6Bu;
        value = (value ^ (value >> 13)) * 0xC2B2AE35u;
        return value ^ (value >> 16);
    }

    private static long FloorDiv(long value, long divisor)
    {
        long quotient = value / divisor;
        long remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static long PositiveModulo(long value, long modulus)
    {
        long result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static ulong HashCell(long x, long z, uint seed)
    {
        ulong value = unchecked((ulong)x) * 0x9E3779B185EBCA87UL;
        value ^= unchecked((ulong)z) * 0xC2B2AE3D27D4EB4FUL;
        value ^= seed;
        return HashMix(value);
    }

    private static ulong HashMix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private static float HashUnit(ulong value)
        => (float)((value >> 40) * (1.0 / (1UL << 24)));

    public BlockType GetBaselineBlock(long globalAroundVoxel, int globalVerticalVoxel, int globalAcrossVoxel)
    {
        if (globalVerticalVoxel <= BedrockY)
        {
            return BlockType.Bedrock;
        }

        int surfaceY = GetSurfaceHeight(globalAroundVoxel, globalAcrossVoxel);
        if (globalVerticalVoxel > surfaceY)
        {
            return BlockType.Air;
        }

        int depth = surfaceY - globalVerticalVoxel;
        return depth < Mathf.Max(1, _settings.SoilDepthVoxels) ? BlockType.Soil : BlockType.Rock;
    }

    public bool ValidateAroundRingSeam(int acrossVoxel)
    {
        if (!_periodicAroundRing)
        {
            return true;
        }

        TerrainSample start = GetTerrainSample(0, acrossVoxel);
        TerrainSample wrappedStart = GetTerrainSample(_aroundRingVoxelLength, acrossVoxel);
        TerrainSample end = GetTerrainSample(-1, acrossVoxel);
        TerrainSample wrappedEnd = GetTerrainSample(_aroundRingVoxelLength - 1, acrossVoxel);
        return SamplesMatch(start, wrappedStart) && SamplesMatch(end, wrappedEnd);
    }

    public bool ValidateElevationContract()
    {
        if (_settings.CoastLowY > _settings.SeaLevelY
            || _settings.CoastHighY < _settings.SeaLevelY
            || RoutineMountainCeilingY >= _maxBuildGlobalY
            || MaximumPossibleSurfaceHeight > _maxBuildGlobalY)
        {
            return false;
        }

        if (!_hasExceptionalSummit)
        {
            return MaximumPossibleSurfaceHeight == RoutineMountainCeilingY;
        }

        int summitHeight = GetSurfaceHeight(_exceptionalSummitAround, _exceptionalSummitAcross);
        long oppositeAround = RingCoordMath.WrapAround(
            _exceptionalSummitAround + _aroundRingVoxelLength / 2,
            _aroundRingVoxelLength);
        int farAcross = _exceptionalSummitAcross < _ringAcrossWidthMeters / 2
            ? _ringAcrossWidthMeters - 1
            : 0;
        return summitHeight == ResolveExceptionalSummitCeiling()
            && summitHeight > RoutineMountainCeilingY
            && GetSurfaceHeight(oppositeAround, _exceptionalSummitAcross) <= RoutineMountainCeilingY
            && GetSurfaceHeight(_exceptionalSummitAround, farAcross) <= RoutineMountainCeilingY;
    }

    public bool ValidateHydrologyContract()
    {
        const int aroundSamples = 64;
        const int acrossSamples = 16;
        for (int z = 0; z <= acrossSamples; z++)
        for (int x = 0; x < aroundSamples; x++)
        {
            long around = (long)Math.Round(x * (_aroundRingVoxelLength / (double)aroundSamples));
            int across = (int)Math.Round(z * (_ringAcrossWidthMeters / (double)acrossSamples));
            TerrainSample sample = GetTerrainSample(around, across);
            if (!sample.HasSurfaceWater)
            {
                continue;
            }

            if (sample.WaterSurfaceHeight <= sample.SurfaceHeight
                || sample.SurfaceHeight <= _settings.BedrockY)
            {
                return false;
            }

            if (sample.WaterKind == SurfaceWaterKind.Ocean
                && sample.WaterSurfaceHeight != _settings.SeaLevelY)
            {
                return false;
            }

            if (sample.WaterKind == SurfaceWaterKind.Lake
                && (sample.WaterDepth > Mathf.Max(_settings.LakeMinimumDepthMeters, _settings.LakeMaximumDepthMeters)
                    || sample.WaterSurfaceHeight > _settings.LakeMaximumSurfaceY))
            {
                return false;
            }

            if (sample.WaterKind == SurfaceWaterKind.River
                && (sample.WaterDepth > Mathf.Max(_settings.RiverMinimumDepthMeters, _settings.RiverMaximumDepthMeters)
                    || sample.WaterSurfaceHeight > _settings.RiverMaximumSurfaceY))
            {
                return false;
            }
        }

        return true;
    }

    public HydrologyAudit CaptureHydrologyAudit(int aroundSamples = 512, int acrossSamples = 64)
    {
        int dry = 0, ocean = 0, lake = 0, river = 0;
        int maximumOceanDepth = 0, maximumLakeDepth = 0, maximumRiverDepth = 0;
        int resolvedAroundSamples = Mathf.Max(1, aroundSamples);
        int resolvedAcrossSamples = Mathf.Max(1, acrossSamples);
        for (int z = 0; z < resolvedAcrossSamples; z++)
        for (int x = 0; x < resolvedAroundSamples; x++)
        {
            long around = (long)Math.Round((x + 0.5) * _aroundRingVoxelLength / resolvedAroundSamples);
            int across = (int)Math.Round((z + 0.5) * _ringAcrossWidthMeters / resolvedAcrossSamples);
            TerrainSample sample = GetTerrainSample(around, across);
            switch (sample.WaterKind)
            {
                case SurfaceWaterKind.Ocean:
                    ocean++;
                    maximumOceanDepth = Mathf.Max(maximumOceanDepth, sample.WaterDepth);
                    break;
                case SurfaceWaterKind.Lake:
                    lake++;
                    maximumLakeDepth = Mathf.Max(maximumLakeDepth, sample.WaterDepth);
                    break;
                case SurfaceWaterKind.River:
                    river++;
                    maximumRiverDepth = Mathf.Max(maximumRiverDepth, sample.WaterDepth);
                    break;
                default:
                    dry++;
                    break;
            }
        }

        return new HydrologyAudit(
            dry,
            ocean,
            lake,
            river,
            maximumOceanDepth,
            maximumLakeDepth,
            maximumRiverDepth);
    }

    /// <summary>Focused, allocation-free distribution audit for editor smoke
    /// tests. It samples the same public path used by voxel and LOD callers.</summary>
    public BiomeTerrainAudit CaptureBiomeTerrainAudit(int aroundSamples = 512, int acrossSamples = 64)
    {
        int land = 0, tundra = 0, desert = 0, grassland = 0, forest = 0, alpine = 0;
        int beach = 0, mountain = 0, valley = 0, cliffs = 0;
        int xs = Mathf.Max(1, aroundSamples), zs = Mathf.Max(1, acrossSamples);
        for (int z = 0; z < zs; z++)
        for (int x = 0; x < xs; x++)
        {
            long around = (long)Math.Round((x + 0.5) * _aroundRingVoxelLength / xs);
            int across = (int)Math.Round((z + 0.5) * _ringAcrossWidthMeters / zs);
            TerrainSample sample = GetTerrainSample(around, across);
            if (sample.HasSurfaceWater) continue;
            land++;
            switch (sample.Biome)
            {
                case TerrainBiomeId.Tundra: tundra++; break;
                case TerrainBiomeId.Desert: desert++; break;
                case TerrainBiomeId.Forest: forest++; break;
                case TerrainBiomeId.Alpine: alpine++; break;
                default: grassland++; break;
            }
            if (sample.Region == TerrainRegion.Coast) beach++;
            if (sample.Region == TerrainRegion.MountainBelt) mountain++;
            int east = GetSurfaceHeight(around + 256, across);
            int west = GetSurfaceHeight(around - 256, across);
            int north = GetSurfaceHeight(around, Mathf.Min(_ringAcrossWidthMeters, across + 256));
            int south = GetSurfaceHeight(around, Mathf.Max(0, across - 256));
            float meanNeighbor = (east + west + north + south) * 0.25f;
            if (sample.SurfaceHeight + 18 < meanNeighbor) valley++;
            float steepest = Mathf.Max(Mathf.Abs(east - west), Mathf.Abs(north - south)) / 512.0f;
            if (steepest >= 0.45f) cliffs++;
        }
        return new BiomeTerrainAudit(land, tundra, desert, grassland, forest, alpine, beach, mountain, valley, cliffs);
    }

    /// <summary>Checks the continuous/sampled surface contract at a compact,
    /// deterministic grid. Intended for smoke tests and editor diagnostics.</summary>
    public bool ValidateBiomeTerrainContract(int aroundSamples = 64, int acrossSamples = 16)
    {
        int xs = Mathf.Max(1, aroundSamples), zs = Mathf.Max(1, acrossSamples);
        for (int z = 0; z < zs; z++)
        for (int x = 0; x < xs; x++)
        {
            long around = (long)Math.Round((x + 0.5) * _aroundRingVoxelLength / xs);
            int across = (int)Math.Round((z + 0.5) * _ringAcrossWidthMeters / zs);
            TerrainSample sample = GetTerrainSample(around, across);
            int surface = GetSurfaceHeight(around, across);
            if (sample.SurfaceHeight != surface
                || Mathf.FloorToInt(GetSmoothSurfaceHeight(around, across)) != surface)
                return false;
        }
        return true;
    }

    private float SampleRegionalNoiseLocked(FastNoiseLite noise, long globalAroundVoxel, int globalAcrossVoxel)
        => SampleRegionalNoiseLocked(noise, ResolveRegionalCoordinate(globalAroundVoxel, globalAcrossVoxel));

    private RegionalCoordinate ResolveRegionalCoordinate(long globalAroundVoxel, int globalAcrossVoxel)
    {
        if (_periodicAroundRing)
        {
            long wrappedAround = RingCoordMath.WrapAround(globalAroundVoxel, _aroundRingVoxelLength);
            if (_settings.UsePrecisionSafePeriodicNoiseTiles
                && _aroundRingVoxelLength > PrecisionSafeNoiseTileMeters)
            {
                return ResolvePrecisionSafePeriodicCoordinate(wrappedAround, globalAcrossVoxel);
            }

            double angle = wrappedAround / (double)_aroundRingVoxelLength * Math.Tau;
            return new RegionalCoordinate(
                (float)(Math.Cos(angle) * _ringRadiusMeters),
                (float)(Math.Sin(angle) * _ringRadiusMeters),
                0.0f,
                0.0f,
                globalAcrossVoxel,
                0.0f,
                true,
                false);
        }

        return new RegionalCoordinate(
            globalAroundVoxel,
            0.0f,
            0.0f,
            0.0f,
            globalAcrossVoxel,
            0.0f,
            false,
            false);
    }

    private RegionalCoordinate ResolvePrecisionSafePeriodicCoordinate(long wrappedAround, int globalAcrossVoxel)
    {
        long tile = wrappedAround / PrecisionSafeNoiseTileMeters;
        long tileStart = tile * PrecisionSafeNoiseTileMeters;
        long tileSpan = Math.Min(PrecisionSafeNoiseTileMeters, _aroundRingVoxelLength - tileStart);
        long local = wrappedAround - tileStart;
        float z = globalAcrossVoxel;
        (float x, float y) current = ResolveNoiseTileCoordinate(tile, local);

        long blend = Math.Min(PrecisionSafeNoiseBlendMeters, Math.Max(1L, tileSpan / 4L));
        // Blend only at the trailing edge of a tile. The next tile starts with
        // its own unblended sample, which is exactly the value reached at this
        // boundary. Blending both sides of the boundary would use opposite
        // conventions at the canonical ring seam and introduce a visible split.
        if (local > tileSpan - blend)
        {
            long nextTile = tileStart + tileSpan >= _aroundRingVoxelLength ? 0L : tile + 1L;
            (float x, float y) next = ResolveNoiseTileCoordinate(nextTile, local - tileSpan);
            return new RegionalCoordinate(
                current.x,
                current.y,
                next.x,
                next.y,
                z,
                SmoothStep(tileSpan - blend, tileSpan, local),
                true,
                true);
        }

        return new RegionalCoordinate(
            current.x,
            current.y,
            0.0f,
            0.0f,
            z,
            0.0f,
            true,
            false);
    }

    private (float x, float y) ResolveNoiseTileCoordinate(long tile, long local)
    {
        // Keep both axes in a range where FastNoiseLite's float input retains
        // metre-level changes. The tile hash stops neighbouring tiles from
        // repeating while the overlap in ResolvePrecisionSafePeriodicCoordinate
        // removes seams at their boundaries.
        ulong key = HashMix(unchecked((ulong)tile) ^ unchecked((uint)_settings.Seed) ^ 0x9E3779B97F4A7C15UL);
        float offsetX = (HashUnit(key) - 0.5f) * 4_194_304.0f;
        float offsetY = (HashUnit(HashMix(key)) - 0.5f) * 4_194_304.0f;
        return ((float)local + offsetX, offsetY);
    }

    /// <summary>
    /// Samples a local 3D feature domain without feeding enormous canonical
    /// around-ring coordinates into float noise.  Tile blending uses the same
    /// convention as regional terrain, so caves cannot form a seam at the
    /// canonical ring wrap or at precision-safe tile boundaries.
    /// </summary>
    private float SampleFeatureNoiseLocked(
        FastNoiseLite noise,
        long globalAroundVoxel,
        int globalY,
        int globalAcrossVoxel)
    {
        long around = _periodicAroundRing
            ? RingCoordMath.WrapAround(globalAroundVoxel, _aroundRingVoxelLength)
            : globalAroundVoxel;
        long tile = FloorDiv(around, PrecisionSafeNoiseTileMeters);
        long tileStart = tile * PrecisionSafeNoiseTileMeters;
        long local = around - tileStart;
        float primary = SampleFeatureNoiseTile(noise, tile, local, globalY, globalAcrossVoxel);

        long tileSpan = _periodicAroundRing
            ? Math.Min(PrecisionSafeNoiseTileMeters, _aroundRingVoxelLength - tileStart)
            : PrecisionSafeNoiseTileMeters;
        long blend = Math.Min(PrecisionSafeNoiseBlendMeters, Math.Max(1L, tileSpan / 4L));
        if (local <= tileSpan - blend)
            return primary;

        long nextTile = _periodicAroundRing && tileStart + tileSpan >= _aroundRingVoxelLength
            ? 0L
            : tile + 1L;
        float secondary = SampleFeatureNoiseTile(
            noise,
            nextTile,
            local - tileSpan,
            globalY,
            globalAcrossVoxel);
        return Mathf.Lerp(primary, secondary, SmoothStep(tileSpan - blend, tileSpan, local));
    }

    private float SampleFeatureNoiseTile(
        FastNoiseLite noise,
        long tile,
        long localAround,
        int globalY,
        int globalAcross)
    {
        ulong key = HashMix(unchecked((ulong)tile) ^ unchecked((uint)_settings.Seed) ^ 0xD1B54A32D192ED03UL);
        float offsetAround = (HashUnit(key) - 0.5f) * 4_194_304.0f;
        float offsetVertical = (HashUnit(HashMix(key + 1UL)) - 0.5f) * 4_194_304.0f;
        float offsetAcross = (HashUnit(HashMix(key + 2UL)) - 0.5f) * 4_194_304.0f;
        return noise.GetNoise3D(
            (float)localAround + offsetAround,
            globalY + offsetVertical,
            globalAcross + offsetAcross);
    }

    private static float SampleRegionalNoiseLocked(FastNoiseLite noise, in RegionalCoordinate coordinate)
    {
        if (!coordinate.Periodic)
        {
            return noise.GetNoise2D(coordinate.PrimaryX, coordinate.Z);
        }

        float primary = noise.GetNoise3D(
            coordinate.PrimaryX,
            coordinate.PrimaryY,
            coordinate.Z);
        if (!coordinate.HasSecondary) return primary;

        float secondary = noise.GetNoise3D(
            coordinate.SecondaryX,
            coordinate.SecondaryY,
            coordinate.Z);
        return Mathf.Lerp(primary, secondary, coordinate.SecondaryWeight);
    }

    private static FastNoiseLite CreateRegionalNoise(int seed, float frequency)
    {
        return new FastNoiseLite
        {
            Seed = seed,
            Frequency = Mathf.Max(0.000001f, frequency)
        };
    }

    private static TerrainRegion ClassifyRegion(float continentalness)
    {
        return continentalness switch
        {
            < -0.45f => TerrainRegion.DeepOcean,
            < -0.10f => TerrainRegion.OceanShelf,
            < -0.05f => TerrainRegion.Coast,
            < 0.35f => TerrainRegion.Lowlands,
            < 0.62f => TerrainRegion.Highlands,
            _ => TerrainRegion.MountainBelt
        };
    }

    private static float ToUnitRange(float value) => Mathf.Clamp(value * 0.5f + 0.5f, 0.0f, 1.0f);

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Mathf.Clamp(
            Mathf.InverseLerp(edge0, Mathf.Max(edge0 + 0.0001f, edge1), value),
            0.0f,
            1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    private static float RemapSmooth(float value, float inputMin, float inputMax, float outputMin, float outputMax)
    {
        float t = Mathf.InverseLerp(inputMin, inputMax, value);
        t = t * t * (3.0f - 2.0f * t);
        return Mathf.Lerp(outputMin, outputMax, t);
    }

    private static bool SamplesMatch(in TerrainSample left, in TerrainSample right)
    {
        const float epsilon = 0.000001f;
        return left.SurfaceHeight == right.SurfaceHeight
            && left.Region == right.Region
            && Mathf.Abs(left.Continentalness - right.Continentalness) <= epsilon
            && Mathf.Abs(left.Erosion - right.Erosion) <= epsilon
            && Mathf.Abs(left.RidgeStrength - right.RidgeStrength) <= epsilon
            && Mathf.Abs(left.Temperature - right.Temperature) <= epsilon
            && Mathf.Abs(left.Moisture - right.Moisture) <= epsilon
            && left.Biome == right.Biome
            && left.WaterKind == right.WaterKind
            && left.WaterSurfaceHeight == right.WaterSurfaceHeight;
    }

    private ulong CalculateTerrainDefinitionHash()
    {
        const ulong offset = 14695981039346656037UL;
        ulong hash = offset;
        hash = MixHash(hash, GeneratorVersion);
        hash = MixHash(hash, _settings.Seed);
        // Preserve the exact Certified/cache hash sequence when this Mega-only
        // setting is disabled. Mega still receives a distinct cache identity.
        if (_settings.UsePrecisionSafePeriodicNoiseTiles)
            hash = MixHash(hash, 1);
        hash = MixHash(hash, _settings.BaseSurfaceHeightVoxels);
        hash = MixHash(hash, _settings.HeightAmplitudeVoxels);
        hash = MixHash(hash, _settings.BaseFrequency);
        hash = MixHash(hash, _settings.DetailAmplitudeVoxels);
        hash = MixHash(hash, _settings.DetailFrequency);
        hash = MixHash(hash, _settings.SoilDepthVoxels);
        hash = MixHash(hash, _settings.BedrockY);
        hash = MixHash(hash, _settings.MacroRegionSizeMeters);
        hash = MixHash(hash, _settings.EnableUnified3dDensityTerrain ? 1 : 0);
        hash = MixHash(hash, _settings.EnableProceduralCaves ? 1 : 0);
        hash = MixHash(hash, _settings.CaveFrequency);
        hash = MixHash(hash, _settings.CaveChannelThreshold);
        hash = MixHash(hash, _settings.CaveCarveStrengthMeters);
        hash = MixHash(hash, _settings.CaveMinimumDepthMeters);
        hash = MixHash(hash, _settings.CaveMaximumDepthMeters);
        hash = MixHash(hash, _settings.EnableNaturalOverhangs ? 1 : 0);
        hash = MixHash(hash, _settings.OverhangFrequency);
        hash = MixHash(hash, _settings.OverhangCarveStrengthMeters);
        hash = MixHash(hash, _settings.ContinentalFrequency);
        hash = MixHash(hash, _settings.ErosionFrequency);
        hash = MixHash(hash, _settings.RidgeFrequency);
        hash = MixHash(hash, _settings.TemperatureFrequency);
        hash = MixHash(hash, _settings.MoistureFrequency);
        hash = MixHash(hash, _settings.ContinentalSecondaryStrength);
        hash = MixHash(hash, _settings.ContinentalOceanBias);
        hash = MixHash(hash, _settings.ClimateLatitudeInfluence);
        hash = MixHash(hash, _settings.OceanMoistureInfluence);
        hash = MixHash(hash, _settings.AltitudeTemperatureDropPerKilometre);
        hash = MixHash(hash, _settings.MountainRainShadowStrength);
        hash = MixHash(hash, _settings.SeaLevelY);
        hash = MixHash(hash, _settings.DeepOceanFloorY);
        hash = MixHash(hash, _settings.OceanShelfFloorY);
        hash = MixHash(hash, _settings.CoastLowY);
        hash = MixHash(hash, _settings.CoastHighY);
        hash = MixHash(hash, _settings.CoastalBeachStrength);
        hash = MixHash(hash, _settings.LowlandHighY);
        hash = MixHash(hash, _settings.HighlandHighY);
        hash = MixHash(hash, _settings.MountainFoundationHighY);
        hash = MixHash(hash, _settings.ContinentalContrast);
        hash = MixHash(hash, _settings.RegionalDetailStrength);
        hash = MixHash(hash, _settings.EnableEngineeredMacroTerrain ? 1 : 0);
        hash = MixHash(hash, _settings.InlandSeaHalfWidthMeters);
        hash = MixHash(hash, _settings.InlandSeaShoreTransitionMeters);
        hash = MixHash(hash, _settings.CoastalTerraceDistanceMeters);
        hash = MixHash(hash, _settings.CoastalTerraceHalfWidthMeters);
        hash = MixHash(hash, _settings.CoastalTerraceHeightMeters);
        hash = MixHash(hash, _settings.ArcEscarpmentHeightMeters);
        hash = MixHash(hash, _settings.ArcEscarpmentWavelengthMeters);
        hash = MixHash(hash, _settings.RarePeakY);
        hash = MixHash(hash, _settings.AutoRaiseRarePeakToBuildLimit ? 1 : 0);
        hash = MixHash(hash, _settings.MountainMaskStart);
        hash = MixHash(hash, _settings.MountainMaskFull);
        hash = MixHash(hash, _settings.MountainWarpFrequency);
        hash = MixHash(hash, _settings.MountainWarpDistanceMeters);
        hash = MixHash(hash, _settings.PeakRarityExponent);
        hash = MixHash(hash, _settings.MountainBeltBroadness);
        hash = MixHash(hash, _settings.ErodedMountainStrength);
        hash = MixHash(hash, _settings.ValleyFrequency);
        hash = MixHash(hash, _settings.ValleyDepthVoxels);
        hash = MixHash(hash, _settings.ValleyNarrowness);
        hash = MixHash(hash, _settings.EscarpmentFrequency);
        hash = MixHash(hash, _settings.EscarpmentHeightMeters);
        hash = MixHash(hash, _settings.EscarpmentSharpness);
        hash = MixHash(hash, _settings.ExceptionalSummitCount);
        hash = MixHash(hash, _settings.ExceptionalSummitY);
        hash = MixHash(hash, _settings.ExceptionalSummitRadiusMeters);
        hash = MixHash(hash, _settings.ExceptionalSummitFalloffExponent);
        hash = MixHash(hash, _settings.ExceptionalSummitCandidateCount);
        hash = MixHash(hash, _settings.TerrainToBuildClearanceMeters);
        hash = MixHash(hash, _settings.EnableLakes ? 1 : 0);
        hash = MixHash(hash, _settings.LakeCellSizeMeters);
        hash = MixHash(hash, _settings.LakeSpawnChance);
        hash = MixHash(hash, _settings.LakeMinimumRadiusMeters);
        hash = MixHash(hash, _settings.LakeMaximumRadiusMeters);
        hash = MixHash(hash, _settings.LakeMinimumDepthMeters);
        hash = MixHash(hash, _settings.LakeMaximumDepthMeters);
        hash = MixHash(hash, _settings.LakeMaximumSurfaceY);
        hash = MixHash(hash, _settings.LakeLevelQuantumMeters);
        hash = MixHash(hash, _settings.EnableRivers ? 1 : 0);
        hash = MixHash(hash, _settings.WatershedRiverHalfWidthMeters);
        hash = MixHash(hash, _settings.WatershedSourceChance);
        hash = MixHash(hash, _settings.RiverFrequency);
        hash = MixHash(hash, _settings.RiverChannelThreshold);
        hash = MixHash(hash, _settings.RiverValleyInfluence);
        hash = MixHash(hash, _settings.RiverBankSharpness);
        hash = MixHash(hash, _settings.RiverMinimumDepthMeters);
        hash = MixHash(hash, _settings.RiverMaximumDepthMeters);
        hash = MixHash(hash, _settings.RiverMinimumMoisture);
        hash = MixHash(hash, _settings.RiverMaximumSurfaceY);
        hash = MixHash(hash, _periodicAroundRing ? 1 : 0);
        hash = MixHash(hash, _aroundRingVoxelLength);
        hash = MixHash(hash, _ringRadiusMeters);
        hash = MixHash(hash, _ringAcrossWidthMeters);
        hash = MixHash(hash, _maxBuildGlobalY);
        // Biome resources participate in surface height and distant colour, so
        // every captured value belongs to the cache identity.
        hash = MixHash(hash, _biomeProfiles.Length);
        foreach (BiomeProfile profile in _biomeProfiles)
        {
            hash = MixHash(hash, (int)profile.Id);
            hash = MixHash(hash, profile.Priority);
            hash = MixHash(hash, profile.MinimumTemperature);
            hash = MixHash(hash, profile.MaximumTemperature);
            hash = MixHash(hash, profile.MinimumMoisture);
            hash = MixHash(hash, profile.MaximumMoisture);
            hash = MixHash(hash, profile.MinimumRidge);
            hash = MixHash(hash, profile.MaximumRidge);
            hash = MixHash(hash, profile.Color.R);
            hash = MixHash(hash, profile.Color.G);
            hash = MixHash(hash, profile.Color.B);
            hash = MixHash(hash, profile.Color.A);
            hash = MixHash(hash, profile.BlendWidth);
            hash = MixHash(hash, profile.SelectionWeightMultiplier);
            hash = MixHash(hash, profile.LocalReliefMeters);
        }
        // Climate, soil and biome colour settings still feed production consumers.
        // Append the new recipe contract to their existing full identity, retaining
        // the exact generation-16 sequence for every legacy world.
        return UsesRuntimeSurfaceGeneration ? CalculateRuntimeTerrainDefinitionHash(hash) : hash;
    }

    private static ulong MixHash(ulong hash, int value) => MixHash(hash, unchecked((ulong)(uint)value));
    private static ulong MixHash(ulong hash, float value) => MixHash(hash, unchecked((ulong)(uint)BitConverter.SingleToInt32Bits(value)));
    private static ulong MixHash(ulong hash, double value) => MixHash(hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));

    private static ulong MixHash(ulong hash, ulong value)
    {
        const ulong prime = 1099511628211UL;
        for (int byteIndex = 0; byteIndex < sizeof(ulong); byteIndex++)
        {
            hash ^= (byte)(value >> (byteIndex * 8));
            hash *= prime;
        }
        return hash;
    }
}
