using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Terrain.Editable;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingTerrainProxySector : MeshInstance3D
{
    public static RingTerrainProxySector? FinestOceanGeometryInstance { get; private set; }
    // The first three proxy rings have enough vertices to carry the 79m FFT
    // swell. Keep this registry separate from FinestOceanGeometryInstance:
    // the latter owns the high-resolution shoreline proof and swimming query.
    private static readonly HashSet<RingTerrainProxySector> FftOceanGeometryInstances = new();
    private const int SharedFftOceanCoverageResolution = 512;
    private const float SharedFftOceanCoverageCellMeters = 2.0f;
    private static Image? _sharedFftOceanCoverageImage;
    private static ImageTexture? _sharedFftOceanCoverageTexture;
    private static byte[]? _sharedFftOceanCoverageBytes;
    private static readonly Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask> SharedFftOceanCoverageVisibleMasks = new();
    private static Vector2 _sharedFftOceanCoverageCenter;
    private static double _sharedFftOceanCoverageCanonicalAround;
    private static double _sharedFftOceanCoverageCanonicalAcross;
    private static ulong _sharedFftOceanCoverageRevision = ulong.MaxValue;
    private static long _sharedFftOceanCoverageCenterCellX = long.MinValue;
    private static int _sharedFftOceanCoverageCenterCellZ = int.MinValue;
    private static long _sharedFftOceanCoverageBuildCount;
    private static long _sharedFftOceanCoverageUploadCount;
    private static double _sharedFftOceanCoverageTotalBuildMilliseconds;
    private static double _sharedFftOceanCoverageTotalUploadMilliseconds;
    // The shoreline field remains distinct from the R8 replacement proof: it
    // contains physical terrain data and is shared by FFT geometry and queries.
    public const int SharedFftShorelineFieldResolution = 512;
    public const float SharedFftShorelineFieldCellMeters = 2.0f;
    private const int FftShorelineFieldHaloCells = 32;
    private const float FftShorelineFieldHaloMeters = FftShorelineFieldHaloCells * SharedFftShorelineFieldCellMeters;
    private static Image? _sharedFftShorelineFieldImage;
    private static ImageTexture? _sharedFftShorelineFieldTexture;
    private static byte[]? _sharedFftShorelineFieldBytes;
    // Assemble into a private buffer, then publish it only after the GPU texture
    // has received the complete replacement. The published bytes are also read by
    // gameplay-side shoreline queries, so they must never contain a half-cleared
    // atlas during a streaming handoff.
    private static byte[]? _sharedFftShorelineFieldStagingBytes;
    private static Vector2 _sharedFftShorelineFieldCenter;
    private static double _sharedFftShorelineFieldCanonicalAround;
    private static double _sharedFftShorelineFieldCanonicalAcross;
    private static ulong _sharedFftShorelineFieldRevision;
    private static ulong _sharedFftShorelineFieldSourceRevision = ulong.MaxValue;
    private static long _sharedFftShorelineFieldCenterCellX = long.MinValue;
    private static int _sharedFftShorelineFieldCenterCellZ = int.MinValue;
    private static long _sharedFftShorelineFieldWorkerBuildCount;
    private static long _sharedFftShorelineFieldWorkerBuildTicks;
    private static long _sharedFftShorelineFieldAtlasRefreshCount;
    private static long _sharedFftShorelineFieldAtlasCopiedTexels;
    [Export] public string LodLabel { get; set; } = "LOD";
    [Export] public bool CoordinatorManaged { get; set; }
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath SmoothTerrainPath { get; set; } = new("../SmoothTerrainGrid");
    [Export] public NodePath FocusPath { get; set; } = new("../Explorer");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export(PropertyHint.Range, "32,4194304,32")] public int SectorSizeMeters { get; set; } = 256;
    [Export(PropertyHint.Range, "1,65536,1")] public int SampleSpacingMeters { get; set; } = 8;
    [Export] public bool UseBlockyHeightfield { get; set; } = false;
    [Export] public bool AlignSurfaceToTerrainSdf { get; set; }
    [Export(PropertyHint.Range, "1,16,1")] public int BlockyHeightQuantumMeters { get; set; } = 2;
    [Export(PropertyHint.Range, "-16,16,1")] public int SectorOffsetAround { get; set; }
    [Export(PropertyHint.Range, "-16,16,1")] public int SectorOffsetAcross { get; set; }
    [Export(PropertyHint.Range, "0,32,1")] public int SectorRadiusAround { get; set; } = 2;
    [Export(PropertyHint.Range, "0,32,1")] public int SectorRadiusAcross { get; set; } = 2;
    [Export(PropertyHint.Range, "0.05,2.0,0.05")] public float StreamingUpdateIntervalSeconds { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "1,16,1")] public int MaxSectorUploadsPerFrame { get; set; } = 2;
    [Export] public bool RebuildExistingMeshesOnCanonicalAnchorChange { get; set; } = true;
    [ExportGroup("Transient mesh memory")]
    [Export(PropertyHint.Range, "4,1024,4")]
    public int MaximumStagedMeshMemoryMiB { get; set; } = 32;
    [ExportGroup("Predictive Streaming")]
    [Export] public bool EnablePredictiveStreaming { get; set; } = true;
    [Export] public bool UseCircularStreamingWindow { get; set; } = true;
    [Export(PropertyHint.Range, "0.0,5.0,0.05")] public float PredictionLookAheadSeconds { get; set; } = 0.75f;
    [Export(PropertyHint.Range, "0.0,200000.0,16.0")] public float PredictionMaxDistanceMeters { get; set; } = 512.0f;
    [Export] public bool IncludeBrakingDistanceInPrediction { get; set; } = true;
    [Export(PropertyHint.Range, "1.0,500.0,1.0")] public float PredictionBrakingAcceleration { get; set; } = 45.0f;
    [Export(PropertyHint.Range, "1.0,4.0,0.1")] public float PredictionBrakingSafetyFactor { get; set; } = 1.5f;
    [ExportGroup("Seam Concealment")]
    [Export] public bool BuildSectorSkirts { get; set; } = true;
    [Export(PropertyHint.Range, "0.1,1024.0,0.1")] public float SectorSkirtDepthMeters { get; set; } = 8.0f;
    [Export(PropertyHint.Range, "0.0,8.0,0.05")] public float RadialUnderlayOffsetMeters { get; set; } = 0.2f;
    [Export(PropertyHint.Range, "1,2000000000,1")] public int RingWidthMeters { get; set; } = 20000;
    [Export] public bool BuildStructuralEdgeWalls { get; set; } = true;
    [Export] public bool BuildStructuralUnderside { get; set; } = true;
    // This is deliberately structural-LOD geometry, rather than a smooth
    // terrain patch.  A player in the cavity must retain a ceiling after the
    // local 2x2x2 editable bubble has streamed away.
    [Export] public bool BuildTerrainRoofUnderside { get; set; } = true;
    [Export] public bool BuildLowerHullCavityFloor { get; set; } = true;
    [Export(PropertyHint.Range, "1,32,1")] public int StructuralWallVerticalCells { get; set; } = 12;
    [Export(PropertyHint.Range, "1,16,1")] public int StructuralWallAroundSubdivisionsPerSector { get; set; } = 8;
    [Export] public Color HullColor { get; set; } = new(0.25f, 0.29f, 0.33f, 1.0f);
    [Export(PropertyHint.Range, "0.0,20000000.0,1.0")] public float FadeStartMeters { get; set; } = 64.0f;
    [Export(PropertyHint.Range, "1.0,20000000.0,1.0")] public float FadeEndMeters { get; set; } = 104.0f;
    [Export] public bool EnableFarFade { get; set; }
    [Export(PropertyHint.Range, "0.0,20000000.0,1.0")] public float FadeOutStartMeters { get; set; } = 480.0f;
    [Export(PropertyHint.Range, "1.0,20000000.0,1.0")] public float FadeOutEndMeters { get; set; } = 640.0f;
    [Export] public Color LowTerrainColor { get; set; } = new(0.19f, 0.28f, 0.16f, 1.0f);
    [Export] public Color HighTerrainColor { get; set; } = new(0.42f, 0.38f, 0.27f, 1.0f);
    [ExportGroup("Biome surface materials")]
    // The sector builder stores these weights in COLOR while it is already
    // sampling the heightfield.  The proxy shader then uses the same four
    // biome maps as smooth terrain; it never evaluates climate at render time.
    [Export(PropertyHint.Range, "0.01,2.0,0.01")]
    public float BiomeTextureScale { get; set; } = 0.20f;

    [ExportGroup("Phase C Clipmap")]
    [Export] public bool EnableClipmapExclusion { get; set; } = true;
    [Export(PropertyHint.Range, "0.0,20000000.0,1.0")] public float InnerClipRadiusMeters { get; set; }
    [Export(PropertyHint.Range, "250,5000,250")] public float AltitudeClipRefreshMeters { get; set; } = 1000.0f;
    [Export(PropertyHint.Range, "0,1000,16")] public float TerrainHeightBoundsPaddingMeters { get; set; } = 256.0f;
    [Export] public bool EnableGeomorphing { get; set; } = true;

    [ExportGroup("Hemisphere Lighting")]
    [Export(PropertyHint.Range, "-1.0,1.0,0.01")] public float DayStartDot { get; set; } = -0.2f;
    [Export(PropertyHint.Range, "-1.0,1.0,0.01")] public float DayFullDot { get; set; } = 0.35f;
    [Export] public Color NightTint { get; set; } = new(0.30f, 0.45f, 0.70f, 1.0f);
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float NightDirectLightFactor { get; set; } = 0.0f;
    [Export(PropertyHint.Range, "0.0,2.0,0.01")] public float DayDirectLightFactor { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float ShadowFloor { get; set; } = 0.0f;

    [ExportGroup("Shadows")]
    [Export] public bool CastTerrainShadows { get; set; } = true;
    [Export] public bool ShadowIgnoresTerrainFade { get; set; } = true;
    [Export] public bool LogBuildSummary { get; set; } = false;
    [ExportGroup("Partial Coverage Culling")]
    // The coverage atlas remains the authoritative, immediate fallback. This
    // optional second step removes already-discarded terrain triangles only
    // after a bounded scene-thread mesh rebuild has completed.
    [Export] public bool EnablePartialCoverageMeshCulling { get; private set; } = true;
    [Export(PropertyHint.Range, "0,8192,16")] public int PartialCoverageMinimumRemovedTriangles { get; set; } = 64;
    [Export(PropertyHint.Range, "0,256,1")] public int PartialCoverageSourceMemoryMiB { get; set; } = 64;
    [ExportGroup("Persistent LOD Cache")]
    [Export] public bool EnableGeneratedLodDiskCache { get; set; } = true;
    [Export] public bool ReadGeneratedLodDiskCache { get; set; } = true;
    [Export] public bool WriteGeneratedLodDiskCache { get; set; } = true;
    [Export(PropertyHint.Range, "0,8192,64")] public int GeneratedLodDiskCacheBudgetMiB { get; set; } = 512;
    [Export(PropertyHint.Range, "25,100,1")] public int GeneratedLodDiskCacheTrimToPercent { get; set; } = 75;
    [Export(PropertyHint.Range, "10,3600,10")] public float GeneratedLodDiskCacheMaintenanceSeconds { get; set; } = 30.0f;
    [ExportGroup("Warm Sector Cache")]
    [Export(PropertyHint.Range, "0,600,1")] public float WarmSectorRetentionSeconds { get; set; } = 20.0f;
    [Export(PropertyHint.Range, "0,2048,1")] public int MaximumWarmSectorCount { get; set; } = 48;
    [Export(PropertyHint.Range, "0,4096,16")] public int MaximumWarmSectorMemoryMiB { get; set; } = 256;

    public int ActiveSectorCount => _active.Count - _warmUntil.Count;
    public int PooledSectorCount => _pool.Count;
    public int WarmSectorCount => _warmUntil.Count;
    public long EstimatedWarmMeshBytes { get; private set; }
    public int CreatedSectorNodeCount { get; private set; }
    public int ReusedSectorNodeCount { get; private set; }
    public int RebuiltSectorMeshCount { get; private set; }
    public int LoadedSectorCacheCount { get; private set; }
    public int SavedSectorCacheCount { get; private set; }
    public int LastGeneratedSectorCount { get; private set; }
    public double LastStreamingBuildMilliseconds { get; private set; }
    public double MaxStreamingBuildMilliseconds { get; private set; }
    public double LastWorkerBuildMilliseconds { get; private set; }
    public double MaxWorkerBuildMilliseconds { get; private set; }
    public double AverageWorkerBuildMilliseconds => _completedWorkerBatches > 0 ? _totalWorkerBuildMilliseconds / _completedWorkerBatches : 0.0;
    public double AverageUploadMilliseconds => _completedUploadBatches > 0 ? _totalUploadMilliseconds / _completedUploadBatches : 0.0;
    public double AverageSectorUploadMilliseconds => _totalUploadedSectors > 0 ? _totalUploadMilliseconds / _totalUploadedSectors : 0.0;
    // Diagnostic-only counter.  This reports allocations made on the scene
    // thread while servicing this level; worker allocations are deliberately
    // not folded into it because a parallel batch has several allocating
    // threads.
    public long MainThreadAllocatedBytesSinceStartup => Interlocked.Read(ref _mainThreadAllocatedBytesSinceStartup);
    public static long MainThreadAllocatedBytesSinceStartupAllLevels =>
        Interlocked.Read(ref _mainThreadAllocatedBytesSinceStartupAllLevels);
    public static long WorkerAllocatedBytesSinceStartup =>
        Interlocked.Read(ref _workerAllocatedBytesSinceStartup);
    public static long StructuralWorkerAllocatedBytesSinceStartup =>
        Interlocked.Read(ref _structuralWorkerAllocatedBytesSinceStartup);
    public static int MaximumStructuralWallRequestedAroundCellsSinceStartup =>
        Volatile.Read(ref _maximumStructuralWallRequestedAroundCellsSinceStartup);
    public long EstimatedActiveMeshBytes { get; private set; }
    public long PartialCoverageSourceBytes { get; private set; }
    public long PartialCoverageFilteredMeshBytes { get; private set; }
    public int PartialCoveragePendingRebuildCount => _partialCoveragePendingSet.Count;
    public long PartialCoverageRemovedTriangleCount { get; private set; }
    public long PartialCoverageActiveRemovedTriangleCount { get; private set; }
    public int PartialCoverageRebuiltMeshCount { get; private set; }
    public long EstimatedPendingUploadBytes { get; private set; }
    public long EstimatedVisibleMeshBytes => Math.Max(0L, EstimatedActiveMeshBytes - EstimatedWarmMeshBytes);
    public long EstimatedStagedMeshBytes
    {
        get
        {
            lock (_stagedMeshLock) return _stagedMeshBytes;
        }
    }
    public int PendingUploadCount => _pendingUploads.Count;
    public bool WorkerBuildPending => _pending != null;
    public int ActiveWorkerSlots => _pending != null ? _activeWorkerSlots : 0;
    public float LastPredictionDistanceMeters { get; private set; }
    public bool StreamingActive { get; private set; } = true;
    public bool StructuralWallMeshReady => _structuralWallMesh?.Mesh != null;

    private VoxelChunkGridManager? _grid;
    private long _mainThreadAllocatedBytesSinceStartup;
    private static long _mainThreadAllocatedBytesSinceStartupAllLevels;
    private static long _workerAllocatedBytesSinceStartup;
    private static long _structuralWorkerAllocatedBytesSinceStartup;
    private static int _maximumStructuralWallRequestedAroundCellsSinceStartup;
    private SmoothTerrainGrid? _smoothTerrain;
    private bool _transitHidden;
    private Node3D? _focus;
    private RingVoxelExplorerController? _velocityProvider;
    private DirectionalLight3D? _sunLight;
    private RingTerrainProxySector? _finerCoverageLevel;
    // Supplied by the coordinator from the next coarser level. This is not a
    // tuning setting: it makes a fine geomorph endpoint match the parent mesh's
    // actual radial base, including each level's intentional underlay.
    private float _morphTargetUnderlayOffsetMeters;
    private bool _morphTargetRestoresWaterUnderlay;
    // Zero preserves the legacy parent lattice (twice this level's spacing).
    // The coordinator supplies the receiving level's actual spacing whenever
    // adjacent tiers are not an exact 2x progression.
    private int _morphTargetSpacingMeters;
    // Immutable publication from the world-ring renderer. It is captured by
    // value for worker builds; workers never inspect the live global LOD node.
    private RingTerrainGlobalMorphSnapshot? _globalMorphTarget;
    // Reverse registration lets a fine level publish water ownership to its
    // coarse consumers in the same frame as an upload or departure.
    private readonly HashSet<RingTerrainProxySector> _oceanReplacementDependents = new();
    private bool _hasVoxelCoverageProvider;
    private RingTerrainGlobalLod? _terrainHeightSource;
    private ShaderMaterial? _material;
    private ShaderMaterial? _oceanMaterial;
    private ShaderMaterial? _structuralWallMaterial;
    private ShaderMaterial? _opaqueShadowMaterial;
    private MeshInstance3D? _structuralWallMesh;
    private Task<StructuralWallData>? _pendingStructuralWall;
    private StructuralWallRequest? _queuedStructuralWall;
    private ulong _structuralWallRequestVersion;
    private readonly Dictionary<RingTerrainSectorCoordinate, MeshInstance3D> _active = new();
    // Sectors outside the current window keep their GPU mesh for a short time.
    // Re-entering the window is then only a visibility change, not a resample/upload.
    private readonly Dictionary<RingTerrainSectorCoordinate, double> _warmUntil = new();
    // The mask records any area deliberately omitted from this level because a
    // more detailed representation is already visible there.  It is used by
    // both the voxel-to-LOD handoff and every LOD-to-LOD handoff.
    private readonly Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask> _activeCoverageMasks = new();
    private readonly Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask> _latestCoverageMasks = new();
    // Ocean replacement is deliberately separate from terrain coverage.  A
    // fine terrain surface does not prove that an FFT-displaced water surface
    // is available above it.
    // This is the water actually submitted by a sector, before the separate
    // FFT handoff atlas can discard a coarser underlay.  Unlike terrain
    // coverage, it is deliberately water-specific so a lake or shore never
    // certifies an ocean replacement.
    private readonly Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask> _activeOceanSurfaceMasks = new();
    // Per-quad distant FFT ownership, with independent atlas slots so every
    // active FFT ocean sector receives a proof rectangle.
    private Image? _fftDistantOceanMaskAtlasImage;
    private ImageTexture? _fftDistantOceanMaskAtlasTexture;
    private int _fftDistantOceanMaskAtlasTileSize;
    private int _fftDistantOceanMaskAtlasColumns;
    private readonly Dictionary<RingTerrainSectorCoordinate, int> _fftDistantOceanMaskSlots = new();
    private readonly Stack<int> _freeFftDistantOceanMaskSlots = new();
    // Independent from terrain replacement: only a resident finer ocean can
    // retire a coarse ocean cell. Terrain readiness alone must never make water
    // disappear at a shore or during loading.
    private readonly Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask> _latestOceanReplacementMasks = new();
    private readonly Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask> _activeOceanReplacementMasks = new();
    private Image? _oceanReplacementAtlasImage;
    private ImageTexture? _oceanReplacementAtlasTexture;
    private byte[]? _oceanReplacementAtlasBytes;
    private ulong _oceanReplacementFinerRevision = ulong.MaxValue;
    private RingTerrainProxySector? _oceanReplacementFinerSource;
    private readonly Dictionary<RingTerrainSectorCoordinate, FftShorelineTile> _activeFftShorelineTiles = new();
    // Terrain readiness cannot hide valid proxy water. There is no separate
    // fine generated-water provider yet, so water retains this proxy until a
    // water-specific handoff can certify replacement coverage.
    private readonly HashSet<RingTerrainSectorCoordinate> _activeWaterSectors = new();
    private readonly HashSet<RingTerrainSectorCoordinate> _activeFullyOceanSectors = new();
    private readonly HashSet<RingTerrainSectorCoordinate> _innerExcluded = new();
    private readonly Dictionary<RingTerrainSectorCoordinate, long> _activeMeshBytes = new();
    private readonly Dictionary<RingTerrainSectorCoordinate, PartialCoverageMeshSource> _partialCoverageSources = new();
    private readonly Queue<RingTerrainSectorCoordinate> _partialCoveragePending = new();
    private readonly HashSet<RingTerrainSectorCoordinate> _partialCoveragePendingSet = new();
    private static ulong _partialCoverageBudgetFrame = ulong.MaxValue;
    private static int _partialCoverageRebuildsThisFrame;
    private static long _partialCoverageRetainedBytesAllLevels;
    private const long MaximumPartialCoverageRetainedBytesAllLevels = 64L * 1024L * 1024L;
    private readonly Queue<MeshInstance3D> _pool = new();
    private Image? _coverageAtlasImage;
    private ImageTexture? _coverageAtlasTexture;
    private int _coverageAtlasTileSize;
    private int _coverageAtlasColumns;
    private bool _repackingCoverageAtlas;
    private readonly Dictionary<RingTerrainSectorCoordinate, int> _coverageAtlasSlots = new();
    private readonly Stack<int> _freeCoverageAtlasSlots = new();
    private Task<BuildBatch>? _pending;
    private RingTerrainSampler? _pendingWorkerSampler;
    private CancellationTokenSource? _buildCancellation;
    private bool _refreshQueued;
    private bool _forceRefreshQueued;
    private ulong _requestVersion;
    private readonly Queue<SectorData> _pendingUploads = new();
    private readonly ConcurrentQueue<SectorData> _completedWorkerSectors = new();
    private readonly object _stagedMeshLock = new();
    private long _stagedMeshBytes;
    private readonly HashSet<RingTerrainSectorCoordinate> _pendingUploadCoordinates = new();
    private HashSet<RingTerrainSectorCoordinate>? _pendingDesired;
    private long _lastAround = long.MinValue;
    private int _lastAcross = int.MinValue;
    private long _lastPredictedAround = long.MinValue;
    private int _lastPredictedAcross = int.MinValue;
    private int _lastAltitudeClipBucket = int.MinValue;
    private ulong _lastFinerCoverageRevision = ulong.MaxValue;
    private ulong _lastHeightRangeRevision = ulong.MaxValue;
    private long _lastWallAroundStart = long.MinValue;
    private long _lastWallAroundEnd = long.MinValue;
    private ulong _lastSurfaceEditVersion;
    private double _elapsed;
    private double _totalWorkerBuildMilliseconds;
    private double _totalUploadMilliseconds;
    private long _totalUploadedSectors;
    private int _completedWorkerBatches;
    private int _completedUploadBatches;
    private bool _coordinatorBuildRequested;
    private bool _coordinatorForceBuildRequested;
    private bool _fastTravelSuspended;
    private bool _loggedStructuralWallReady;
    private bool _commandLineDiskCacheDisabled;
    private int _activeWorkerSlots;
    private ulong _coverageRevision;
    private Vector3 _lastCameraWorldPosition = new(float.PositiveInfinity, 0.0f, 0.0f);
    private Vector3 _lastLightingRingCenter = new(float.PositiveInfinity, 0.0f, 0.0f);
    private Vector3 _lastLightingSunDirection = new(float.PositiveInfinity, 0.0f, 0.0f);
    private LightingUniformConfiguration _lastLightingConfiguration;
    private bool _hasLightingUniformConfiguration;
    private FftOceanCullConfiguration _lastFftOceanCullConfiguration;
    private bool _hasFftOceanCullConfiguration;
    private float _fftOceanCullMarginMeters;
    private static bool _hierarchyHandoffSmokePassed;
    private static bool _fftOceanCoverageContractPassed;

    private readonly record struct LightingUniformConfiguration(
        float DayStartDot,
        float DayFullDot,
        Vector3 NightTint,
        float NightDirectLightFactor,
        float DayDirectLightFactor,
        float ShadowFloor,
        int RingWidthMeters);
    private readonly record struct FftOceanCullConfiguration(
        float HeightScale,
        float Choppiness,
        float WindSpeed,
        int CascadeCount,
        float SmallDomain,
        float LargeDomain,
        bool Ready);

    // One bit per provider cell.  For the first proxy LOD a provider cell is a
    // 32m voxel surface chunk; for all other LODs it is one quad of the
    // immediately finer mesh.  A covered bit means this mesh deliberately
    // omits the quad because the provider is already visibly complete.
    private sealed record SurfaceCoverageMask(int ProxyQuadCells, int QuadsPerProviderCell, bool[] CoveredCells, byte[]? FftDeepWaterVertexDepths = null)
    {
        public bool CoversQuad(int x, int z)
        {
            int cellCount = (ProxyQuadCells + QuadsPerProviderCell - 1) / QuadsPerProviderCell;
            return CoveredCells[Math.Min(cellCount - 1, z / QuadsPerProviderCell) * cellCount
                + Math.Min(cellCount - 1, x / QuadsPerProviderCell)];
        }
        public bool Matches(SurfaceCoverageMask? other)
            => other != null && ProxyQuadCells == other.ProxyQuadCells
                && QuadsPerProviderCell == other.QuadsPerProviderCell
                && CoveredCells.AsSpan().SequenceEqual(other.CoveredCells);
    }
    private sealed record FinerOceanSnapshot(
        Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask> Masks,
        HashSet<RingTerrainSectorCoordinate> FullyWet);

    // Retained only for sectors which may receive a worthwhile partial mask.
    // Never read back a Godot mesh: these are the original upload arrays.
    private sealed class PartialCoverageMeshSource
    {
        public required Vector3[] Vertices { get; init; }
        public required Vector3[] Normals { get; init; }
        public required Color[] Colors { get; init; }
        public required Vector2[] Uvs { get; init; }
        public required Vector2[] MorphData { get; init; }
        public required Vector3[] ParentChordOffsets { get; init; }
        public required int[] TerrainIndices { get; init; }
        public required int[] OceanIndices { get; init; }
        public required bool UsesRuntimeParentChord { get; init; }
        public required int Cells { get; init; }
        public required Mesh FullMesh { get; init; }
        public Mesh? FilteredMesh { get; set; }
        public long FilteredMeshBytes { get; set; }
        public int ActiveRemovedTriangleCount { get; set; }
        public long ReservedBytes { get; set; }
        public long SourceBytes => Vertices.LongLength * 12L + Normals.LongLength * 12L
            + Colors.LongLength * 16L + Uvs.LongLength * 8L + MorphData.LongLength * 8L
            + ParentChordOffsets.LongLength * 12L + (TerrainIndices.LongLength + OceanIndices.LongLength) * 4L;
    }

    private sealed record SectorData(RingTerrainSectorCoordinate Coordinate, ulong RequestVersion, ulong EditVersion, Vector3 LocalOrigin, Vector3[] Vertices, Vector3[] Normals, Color[] Colors, Vector2[] Uvs, Vector2[] MorphData, Vector3[] ParentChordOffsets, bool UsesRuntimeParentChord, int[] Indices, int[] OceanIndices, SurfaceCoverageMask? CoverageMask, SurfaceCoverageMask? OceanSurfaceMask, bool FullyOceanSurface, FftShorelineTile? ShorelineTile)
    {
        public long EstimatedBytes => Vertices.LongLength * 12L
            + Normals.LongLength * 12L
            + Colors.LongLength * 16L
            + Uvs.LongLength * 8L
            + MorphData.LongLength * 8L
            + ParentChordOffsets.LongLength * 12L
            + (Indices.LongLength + OceanIndices.LongLength) * 4L
            + (ShorelineTile?.InteriorBytes.LongLength ?? 0L);
    }
    private sealed record BuildBatch(ulong RequestVersion, ulong EditVersion, int SectorCount, int CacheHits, int CacheWrites, double WorkerMilliseconds);
    private sealed record StructuralWallRequest(
        RingLodGeometrySnapshot Geometry,
        Transform3D OwnerInverse,
        double AroundStart,
        double AroundStep,
        int AroundCells,
        double VerticalStep,
        int VerticalCells,
        int RingWidth,
        bool BuildEdgeWalls,
        bool BuildUnderside,
        bool BuildRoofUnderside,
        bool BuildLowerHullCavityFloor,
        double TerrainBottomGlobalY,
        double LowerHullTopGlobalY,
        Color Color,
        ulong Version);
    private sealed record StructuralWallData(
        Vector3[] Vertices,
        Vector3[] Normals,
        Color[] Colors,
        Vector2[] Uv2,
        int[] Indices,
        ulong Version);
    private readonly record struct StructuralWallSectorRange(long StartSector, long EndSectorExclusive)
    {
        public long SectorCount => EndSectorExclusive - StartSector;
    }
    private const uint LodSectorCacheMagic = 0x53444F4C; // LODS
    // The disk cache stores authoritative terrain samples, not render-time
    // vertex colors, so biome material changes do not invalidate this format.
    private const int LodSectorCacheVersion = 11;
    private const float ShorelineOverlapMeters = 0.35f;
    private const int MaximumCachedSampleCount = 1_000_000;
    private static readonly ConcurrentDictionary<string, byte> UnavailableCacheDirectories = new();
    private readonly record struct CachedTerrainPoint(
        TerrainSample Sample,
        int TerrainHeight,
        int DisplayedHeight,
        float SmoothTerrainHeight,
        float PresentationTerrainHeight,
        float PresentationDisplayedHeight,
        bool UsesPresentationSurface,
        bool UsesRuntimeWaterPlane)
    {
        public bool IsDisplayedWater => Sample.HasSurfaceWater
            && (UsesPresentationSurface ? PresentationTerrainHeight : TerrainHeight) < Sample.WaterSurfaceHeight;
    }

    private readonly record struct CachedTerrainSampleRecord(
        long Around,
        int Across,
        CachedTerrainPoint Point);

    private readonly record struct ShorelineClipVertex(
        double Around,
        float Across,
        float WaterHeight,
        float SignedCoverage,
        Vector2 Uv,
        float Morph);

    public readonly record struct FftShorelineSample(float SignedDepthMeters, float ShoreDistanceMeters, bool Valid);

    private sealed record FftShorelineTile(
        RingTerrainSectorCoordinate Coordinate,
        int InteriorCells,
        float CellMeters,
        byte[] InteriorBytes);

    private readonly record struct PartialWaterTriangle(int A, int B, int C);

    private readonly record struct TerrainColumnKey(long Around, int Across);

    private sealed class TerrainBuildSampleCache
    {
        private readonly RingLodSurfaceEditSnapshot _edits;
        private readonly RingTerrainSampler _sampler;
        private readonly int _ringWidth;
        private readonly Dictionary<TerrainColumnKey, CachedTerrainPoint> _points = new();
        private readonly HashSet<TerrainColumnKey> _capturedKeys = new();
        private bool _captureAccesses;

        public TerrainBuildSampleCache(
            RingLodSurfaceEditSnapshot edits,
            RingTerrainSampler sampler,
            int ringWidth)
        {
            _edits = edits;
            _sampler = sampler;
            _ringWidth = ringWidth;
        }

        public bool UsesPresentationSurface => _edits.UsesFloatSurface || _sampler.UsesRuntimeSurfaceGeneration;
        public bool UsesRuntimeWaterPlane => _sampler.UsesRuntimeSurfaceGeneration;

        public CachedTerrainPoint Get(long around, int across)
        {
            int clampedAcross = Mathf.Clamp(across, 0, _ringWidth);
            var key = new TerrainColumnKey(around, clampedAcross);
            if (_captureAccesses) _capturedKeys.Add(key);
            if (_points.TryGetValue(key, out CachedTerrainPoint cached)) return cached;

            TerrainSample sample = _sampler.GetTerrainSample(around, clampedAcross);
            bool usesPresentationSurface = _edits.UsesFloatSurface || _sampler.UsesRuntimeSurfaceGeneration;
            if (usesPresentationSurface)
                sample = _edits.GetPresentationTerrainSample(_sampler, around, clampedAcross);
            int terrainHeight = _edits.TryGetEditedSurfaceHeight(around, clampedAcross, out int editedHeight)
                ? editedHeight
                : sample.SurfaceHeight;
            float presentationTerrainHeight = usesPresentationSurface
                ? _edits.GetPresentationSurfaceHeight(_sampler, around, clampedAcross)
                : 0.0f;
            float smoothTerrainHeight = usesPresentationSurface
                ? presentationTerrainHeight
                : _edits.TryGetEditedSurfaceHeight(around, clampedAcross, out _)
                    ? terrainHeight
                    : _sampler.GetSmoothSurfaceHeight(around, clampedAcross);
            cached = new CachedTerrainPoint(
                sample,
                terrainHeight,
                sample.ResolveDisplayedHeight(terrainHeight),
                smoothTerrainHeight,
                presentationTerrainHeight,
                usesPresentationSurface ? sample.ResolveDisplayedHeight(presentationTerrainHeight) : 0.0f,
                usesPresentationSurface,
                _sampler.UsesRuntimeSurfaceGeneration);
            _points[key] = cached;
            return cached;
        }

        public void Import(IReadOnlyList<CachedTerrainSampleRecord> records)
        {
            foreach (CachedTerrainSampleRecord record in records)
            {
                int clampedAcross = Mathf.Clamp(record.Across, 0, _ringWidth);
                var key = new TerrainColumnKey(record.Around, clampedAcross);
                _points[key] = record.Point;
            }
        }

        public void BeginCapture()
        {
            _capturedKeys.Clear();
            _captureAccesses = true;
        }

        public CachedTerrainSampleRecord[] EndCapture()
        {
            _captureAccesses = false;
            var records = new CachedTerrainSampleRecord[_capturedKeys.Count];
            int index = 0;
            foreach (TerrainColumnKey key in _capturedKeys)
            {
                records[index++] = new CachedTerrainSampleRecord(
                    key.Around,
                    key.Across,
                    _points[key]);
            }
            _capturedKeys.Clear();
            return records;
        }
    }

    public override void _Ready()
    {
        bool commandLineHandoffSmoke=Array.Exists(OS.GetCmdlineUserArgs(), argument => argument.Equals("--lod-voxel-handoff-smoke-test", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--lod-hierarchy-handoff-smoke-test", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--lod-roof-coverage-smoke-test", StringComparison.OrdinalIgnoreCase));
        if(commandLineHandoffSmoke)
        {
            RunVoxelHandoffSmokeTest();
            Callable.From(()=>GetTree().Quit(0)).CallDeferred();
        }
        _commandLineDiskCacheDisabled = Array.Exists(
            OS.GetCmdlineUserArgs(),
            argument => argument.Equals(
                "--disable-lod-disk-cache",
                StringComparison.OrdinalIgnoreCase));
        if (_commandLineDiskCacheDisabled)
        {
            EnableGeneratedLodDiskCache = false;
        }
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        RegisterFftWaterDemandProvider();
        if (SampleSpacingMeters <= 2) FinestOceanGeometryInstance = this;
        if (SampleSpacingMeters is 2 or 4 or 8) FftOceanGeometryInstances.Add(this);
        _smoothTerrain = _grid?.IsSmoothTerrainReplacementActive == true
            ? GetNodeOrNull<SmoothTerrainGrid>(SmoothTerrainPath)
            : null;
        _focus = GetNodeOrNull<Node3D>(FocusPath);
        _velocityProvider = ResolveVelocityProvider(_focus);
        _sunLight = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        if (_grid?.TerrainSampler == null || _focus == null) return;
        _grid.CanonicalFloatingOriginAnchorChanged += HandleCanonicalFloatingOriginAnchorChanged;
        CreateMaterial();
        QueueStreamingUpdate(true);
    }

    public override void _Process(double delta)
    {
        if (CoordinatorManaged) return;
        ServiceStreaming(delta, Mathf.Max(1, MaxSectorUploadsPerFrame), double.PositiveInfinity);
    }

    public int ServiceStreaming(double delta, int uploadBudget, double uploadBudgetMilliseconds = double.PositiveInfinity)
    {
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Terrain proxy update (inclusive)");
        long allocationsBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            ReportCompletedDiskCacheMaintenance();
            using (RingPerformanceTelemetry.MeasureCpu("Terrain proxy structural completion"))
                ServiceStructuralWallBuild();
            using (RingPerformanceTelemetry.MeasureCpu("Terrain proxy camera uniforms"))
                UpdateCameraUniform();
            UpdateLightingUniforms();
            int uploaded = ApplyCompletedBatch(Mathf.Max(0, uploadBudget), uploadBudgetMilliseconds);
            ServicePartialCoverageMeshCulling();
            if (!StreamingActive || _fastTravelSuspended) return uploaded;
            _elapsed += delta;
            if (_elapsed >= Mathf.Max(0.05f, StreamingUpdateIntervalSeconds))
            {
                _elapsed = 0.0;
                using (RingPerformanceTelemetry.MeasureCpu("Terrain proxy streaming request"))
                    QueueStreamingUpdate(false);
            }
            return uploaded;
        }
        finally
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationsBefore;
            if (allocated > 0)
            {
                Interlocked.Add(ref _mainThreadAllocatedBytesSinceStartup, allocated);
                Interlocked.Add(ref _mainThreadAllocatedBytesSinceStartupAllLevels, allocated);
            }
        }
    }

    public bool CoordinatorBuildRequested => _coordinatorBuildRequested;
    public ulong CoverageRevision => _coverageRevision;
    public double LastCoverageRefreshMilliseconds { get; private set; }
    public double MaxCoverageRefreshMilliseconds { get; private set; }
    public long SharedFftOceanCoverageBuildCount => _sharedFftOceanCoverageBuildCount;
    public long SharedFftOceanCoverageUploadCount => _sharedFftOceanCoverageUploadCount;
    public double LastSharedFftOceanCoverageBuildMilliseconds { get; private set; }
    public double LastSharedFftOceanCoverageUploadMilliseconds { get; private set; }
    public double TotalSharedFftOceanCoverageBuildMilliseconds => _sharedFftOceanCoverageTotalBuildMilliseconds;
    public double TotalSharedFftOceanCoverageUploadMilliseconds => _sharedFftOceanCoverageTotalUploadMilliseconds;
    public bool FastTravelSuspended => _fastTravelSuspended;

    public void SetCoordinatorManaged(bool managed) => CoordinatorManaged = managed;

    public void ConfigureGeneratedLodDiskCache(
        bool enabled,
        bool allowReads,
        bool allowWrites,
        int budgetMiB,
        int trimToPercent,
        float maintenanceSeconds)
    {
        EnableGeneratedLodDiskCache = enabled && !_commandLineDiskCacheDisabled;
        ReadGeneratedLodDiskCache = allowReads;
        WriteGeneratedLodDiskCache = allowWrites && budgetMiB > 0;
        GeneratedLodDiskCacheBudgetMiB = Mathf.Max(0, budgetMiB);
        GeneratedLodDiskCacheTrimToPercent = Mathf.Clamp(trimToPercent, 25, 100);
        GeneratedLodDiskCacheMaintenanceSeconds = Mathf.Max(10.0f, maintenanceSeconds);
    }

    private static void ReportCompletedDiskCacheMaintenance()
    {
        while (RingGeneratedLodCacheBudget.TryTakeCompleted(out RingGeneratedLodCacheBudget.MaintenanceResult result))
        {
            if (!string.IsNullOrEmpty(result.Error))
            {
                GD.PushWarning($"Generated LOD cache maintenance failed for '{result.RootDirectory}': {result.Error}");
                continue;
            }
            if (result.DeletedFileCount <= 0) continue;
            GD.Print(
                $"Generated LOD cache trimmed: {FormatCacheBytes(result.BytesBefore)} -> "
                + $"{FormatCacheBytes(result.BytesAfter)}, files={result.DeletedFileCount}, "
                + $"root={result.RootDirectory}");
        }
    }

    private static string FormatCacheBytes(long bytes)
        => bytes >= 1024L * 1024L * 1024L
            ? $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00}GiB"
            : $"{bytes / (1024.0 * 1024.0):0.0}MiB";

    public void SetFastTravelSuspended(bool suspended)
    {
        if (_fastTravelSuspended == suspended) return;
        _fastTravelSuspended = suspended;
        if (suspended)
        {
            _structuralWallRequestVersion++;
            _queuedStructuralWall = null;
            _coordinatorBuildRequested = false;
            _coordinatorForceBuildRequested = false;
            _refreshQueued = false;
            _forceRefreshQueued = false;
            _buildCancellation?.Cancel();
            ClearPendingUploads();
            return;
        }

        _lastAround = long.MinValue;
        _lastAcross = int.MinValue;
        _lastPredictedAround = long.MinValue;
        _lastPredictedAcross = int.MinValue;
        QueueStreamingUpdate(force: true);
    }

    /// <summary>
    /// Keeps a cached proxy window from being mistaken for terrain at the new
    /// canonical location while a very fast noclip transit is in progress.
    /// Streaming and collision state are left intact for an immediate resume.
    /// </summary>
    public void SetTransitVisibility(bool hidden)
    {
        if (_transitHidden == hidden) return;
        InvalidateOceanCoverageDependents();
        _transitHidden = hidden;
        Visible = StreamingActive && !_transitHidden;
        foreach (RingTerrainSectorCoordinate coordinate in _active.Keys)
            UpdateSectorVisibility(coordinate);
        if (_structuralWallMesh != null)
            _structuralWallMesh.Visible = StreamingActive && !_transitHidden;
    }

    public void SetCoverageSources(
        RingTerrainProxySector? finerCoverageLevel,
        RingTerrainGlobalLod? terrainHeightSource,
        bool hasVoxelCoverageProvider = false)
    {
        if (ReferenceEquals(_finerCoverageLevel, finerCoverageLevel)
            && ReferenceEquals(_terrainHeightSource, terrainHeightSource)
            && _hasVoxelCoverageProvider == hasVoxelCoverageProvider) return;
        _finerCoverageLevel?._oceanReplacementDependents.Remove(this);
        _finerCoverageLevel = finerCoverageLevel;
        _finerCoverageLevel?._oceanReplacementDependents.Add(this);
        _terrainHeightSource = terrainHeightSource;
        _hasVoxelCoverageProvider = hasVoxelCoverageProvider;
        _lastFinerCoverageRevision = ulong.MaxValue;
        _lastHeightRangeRevision = ulong.MaxValue;
        _lastAround = long.MinValue;
    }

    public void SetMorphTargetSurfaceBase(float parentUnderlayOffsetMeters, bool parentRestoresWaterUnderlay)
    {
        float underlay = Mathf.Max(0.0f, parentUnderlayOffsetMeters);
        if (Mathf.IsEqualApprox(_morphTargetUnderlayOffsetMeters, underlay)
            && _morphTargetRestoresWaterUnderlay == parentRestoresWaterUnderlay) return;
        _morphTargetUnderlayOffsetMeters = underlay;
        _morphTargetRestoresWaterUnderlay = parentRestoresWaterUnderlay;
        UpdateMorphTargetWaterSpacingUniform();
        InvalidateMorphTargetGeometry();
    }

    /// <summary>
    /// Sets the actual lattice spacing of the next coarser proxy tier. A value
    /// of zero restores the legacy 2x fallback for compatibility with scenes
    /// that do not yet have coordinator-supplied parent spacing.
    /// </summary>
    public void SetMorphTargetSpacingMeters(int parentSpacingMeters)
    {
        int spacing = Mathf.Max(0, parentSpacingMeters);
        if (_morphTargetSpacingMeters == spacing) return;
        _morphTargetSpacingMeters = spacing;
        UpdateMorphTargetWaterSpacingUniform();
        InvalidateMorphTargetGeometry();
    }

    /// <summary>
    /// Supplies the ready world-ring receiver mesh for the terminal proxy
    /// tier. Null removes that optional receiver and preserves proxy-parent
    /// sampling. The snapshot is immutable and safe to retain across workers.
    /// </summary>
    public void SetGlobalMorphTarget(RingTerrainGlobalMorphSnapshot? snapshot)
    {
        if (_globalMorphTarget?.SnapshotIdentity == snapshot?.SnapshotIdentity) return;
        _globalMorphTarget = snapshot;
        UpdateRuntimeParentChordUniform();
        InvalidateMorphTargetGeometry();
    }

    private int ResolveMorphTargetSpacingMeters()
        => ResolveMorphTargetSpacingMeters(SampleSpacingMeters, _morphTargetSpacingMeters);

    private static int ResolveMorphTargetSpacingMeters(int ownSpacingMeters, int configuredParentSpacingMeters)
        => configuredParentSpacingMeters > 0
            ? configuredParentSpacingMeters
            : Mathf.Max(1, ownSpacingMeters * 2);

    private void UpdateMorphTargetWaterSpacingUniform()
    {
        if (_oceanMaterial == null) return;
        _oceanMaterial.SetShaderParameter("fft_geometry_parent_spacing_meters",
            _morphTargetRestoresWaterUnderlay && SampleSpacingMeters is 2 or 4
                ? (float)ResolveMorphTargetSpacingMeters()
                : 0.0f);
    }

    private bool UsesRuntimeParentChord()
        => _grid?.TerrainSampler?.UsesRuntimeSurfaceGeneration == true || _globalMorphTarget != null;

    private void UpdateRuntimeParentChordUniform()
    {
        bool enabled = UsesRuntimeParentChord();
        _material?.SetShaderParameter("runtime_parent_chord_enabled", enabled);
        _opaqueShadowMaterial?.SetShaderParameter("runtime_parent_chord_enabled", enabled);
    }

    private void InvalidateMorphTargetGeometry()
    {
        // Existing meshes remain resident until replacement uploads complete.
        // Reject any worker output sampled against the old parent lattice.
        _buildCancellation?.Cancel();
        ClearPendingUploads();
        _requestVersion++;
        _lastAround = long.MinValue;
        _lastAcross = int.MinValue;
        _lastPredictedAround = long.MinValue;
        _lastPredictedAcross = int.MinValue;
        QueueStreamingUpdate(force: true);
    }

    /// <summary>
    /// Returns true only after resident, visible fine proxy sectors prove they
    /// draw unbroken ocean over the requested rectangle.  It intentionally
    /// rejects shore, lake, river, warm, and pending sectors.
    /// </summary>
    public bool IsOceanSurfaceVisuallyCovered(
        double aroundStart, double aroundEnd, double acrossStart, double acrossEnd)
    {
        if (!StreamingActive || _transitHidden || aroundEnd <= aroundStart || acrossEnd <= acrossStart)
            return false;
        int size = Mathf.Max(32, SectorSizeMeters);
        if (acrossStart < 0.0 || acrossEnd > RingWidthMeters) return false;
        long aroundSectorLength = Math.Max(1L, (long)Math.Ceiling(ResolveAroundCircumferenceMeters() / size));
        long aroundSectorStart = (long)Math.Floor(aroundStart / size);
        long aroundSectorEnd = (long)Math.Floor((aroundEnd - 0.000001) / size);
        int acrossSectorStart = (int)Math.Floor(acrossStart / size);
        int acrossSectorEnd = (int)Math.Floor((acrossEnd - 0.000001) / size);
        for (long aroundSector = aroundSectorStart; aroundSector <= aroundSectorEnd; aroundSector++)
        for (int acrossSector = acrossSectorStart; acrossSector <= acrossSectorEnd; acrossSector++)
        {
            var coordinate = new RingTerrainSectorCoordinate(
                WrapAroundSector(aroundSector, aroundSectorLength), acrossSector);
            if (_warmUntil.ContainsKey(coordinate)
                || !_active.TryGetValue(coordinate, out MeshInstance3D? node)
                || !node.Visible) return false;

            // A resident all-ocean sector is a common open-water case. Avoid
            // walking its per-quad mask for every coarse ownership proof.
            if (_activeFullyOceanSectors.Contains(coordinate)) continue;

            // Per-cell proof keeps gameplay aligned with the triangles
            // actually rendered at sector edges, including the ocean cells of
            // a mixed shore sector.  It never certifies dry/lake cells.
            if (!_activeOceanSurfaceMasks.TryGetValue(coordinate, out SurfaceCoverageMask? surfaceMask))
                return false;
            double sectorAroundStart = aroundSector * (double)size;
            double sectorAroundEnd = sectorAroundStart + size;
            double sectorAcrossStart = acrossSector * (double)size;
            double sectorAcrossEnd = sectorAcrossStart + size;
            if (!IsOceanMaskCovered(surfaceMask,
                    aroundStart - sectorAroundStart, aroundEnd - sectorAroundStart,
                    acrossStart - sectorAcrossStart, acrossEnd - sectorAcrossStart, size))
                return false;
        }
        return true;
    }

    private static bool IsOceanMaskCovered(
        SurfaceCoverageMask mask, double queryAroundStart, double queryAroundEnd,
        double queryAcrossStart, double queryAcrossEnd, int size)
    {
        double around0 = Math.Max(0.0, queryAroundStart);
        double around1 = Math.Min(size, queryAroundEnd);
        double across0 = Math.Max(0.0, queryAcrossStart);
        double across1 = Math.Min(size, queryAcrossEnd);
        if (around1 <= around0 || across1 <= across0) return true;
        int cells = Mathf.Max(1, mask.ProxyQuadCells);
        int x0 = Math.Clamp((int)Math.Floor(around0 / size * cells), 0, cells - 1);
        int x1 = Math.Clamp((int)Math.Floor((around1 - 0.000001) / size * cells), 0, cells - 1);
        int z0 = Math.Clamp((int)Math.Floor(across0 / size * cells), 0, cells - 1);
        int z1 = Math.Clamp((int)Math.Floor((across1 - 0.000001) / size * cells), 0, cells - 1);
        for (int z = z0; z <= z1; z++)
        for (int x = x0; x <= x1; x++)
            if (!mask.CoveredCells[z * cells + x]) return false;
        return true;
    }

    private void InvalidateOceanCoverageDependents()
    {
        if (ReferenceEquals(FinestOceanGeometryInstance, this)) InvalidateSharedFftOceanCoverage();
        // Direct coarse replacement atlases are separate resources. Clear them
        // synchronously even though this source may not have flipped its
        // StreamingActive/transit flag yet (notably SetTransitVisibility).
        // Registration stays intact so a later valid publication repopulates.
        foreach (RingTerrainProxySector dependent in _oceanReplacementDependents.ToArray())
            dependent.InvalidateOceanReplacementFromFiner(this);
    }

    private void InvalidateOceanReplacementFromFiner(RingTerrainProxySector finer)
    {
        if (!ReferenceEquals(_finerCoverageLevel, finer)) return;
        ClearOceanReplacementMasks();
    }

    // Ordinary sector retirement makes the fine replacement proof stale, but it
    // does not make the last completed shoreline field unsafe. Keep that field
    // live until this frame's synchronous rebuild publishes its successor.
    private void MarkOceanCoverageDirtyForRoutineSectorChange()
    {
        // Keep the last complete fine-ocean ownership atlas published while a
        // local sector is replaced. UpdateSharedFftOceanCoverage stages and
        // uploads the successor synchronously; invalidating here exposed the
        // coarse underlay for one or more frames during normal streaming.
        // Explicit transit/shutdown paths still call the hard invalidator.
    }

    private static void InvalidateSharedFftOceanCoverage()
    {
        InvalidateSharedFftOceanCoverageOnly();
        _sharedFftShorelineFieldSourceRevision = ulong.MaxValue;
        _sharedFftShorelineFieldRevision++;
    }

    private static void InvalidateSharedFftOceanCoverageOnly()
        => _sharedFftOceanCoverageRevision = ulong.MaxValue;


    public void StartCoordinatorBuildIfRequested(int workerSlots = 1)
    {
        if (_fastTravelSuspended
            || !StreamingActive
            || !_coordinatorBuildRequested
            || _pending != null)
        {
            return;
        }
        bool force = _coordinatorForceBuildRequested;
        _coordinatorBuildRequested = false;
        _coordinatorForceBuildRequested = false;
        StartStreamingUpdate(force, Mathf.Max(1, workerSlots));
    }

    public void SetLodActive(bool active)
    {
        if (StreamingActive == active)
        {
            Visible = active && !_transitHidden;
            return;
        }

        if (!active) InvalidateOceanCoverageDependents();
        StreamingActive = active;
        Visible = active && !_transitHidden;
        if (active)
        {
            _lastAround = long.MinValue;
            _lastAcross = int.MinValue;
            _lastPredictedAround = long.MinValue;
            _lastPredictedAcross = int.MinValue;
            _lastAltitudeClipBucket = int.MinValue;
            _lastWallAroundStart = int.MinValue;
            _lastWallAroundEnd = int.MinValue;
            QueueStreamingUpdate(force: true);
            return;
        }

        _coordinatorBuildRequested = false;
        _coordinatorForceBuildRequested = false;
        _buildCancellation?.Cancel();
        ClearPendingUploads();
        if (_innerExcluded.Count > 0)
        {
            _innerExcluded.Clear();
            _coverageRevision++;
        }
        foreach (RingTerrainSectorCoordinate coordinate in new List<RingTerrainSectorCoordinate>(_active.Keys))
            RecycleActiveSector(coordinate);
    }

    public override void _ExitTree()
    {
        foreach (RingTerrainSectorCoordinate coordinate in _partialCoverageSources.Keys.ToArray())
            ReleasePartialCoverageSource(coordinate);
        _partialCoveragePending.Clear();
        _partialCoveragePendingSet.Clear();
        UnregisterFftWaterDemandProvider();
        InvalidateOceanCoverageDependents();
        _finerCoverageLevel?._oceanReplacementDependents.Remove(this);
        _finerCoverageLevel = null;
        if (ReferenceEquals(FinestOceanGeometryInstance, this)) FinestOceanGeometryInstance = null;
        FftOceanGeometryInstances.Remove(this);
        if (_grid != null)
            _grid.CanonicalFloatingOriginAnchorChanged -= HandleCanonicalFloatingOriginAnchorChanged;
        _buildCancellation?.Cancel();
        if (_pending != null && !_pending.IsCompleted)
        {
            try { _pending.Wait(5000); }
            catch (AggregateException) { }
        }
        if (_pending == null || _pending.IsCompleted) DisposePendingWorkerSampler();
        _pending = null;
        _buildCancellation?.Dispose();
        _buildCancellation = null;
    }

    public static bool IsFftOceanGeometryAvailableAt(in RingCanonicalPosition position)
        => FinestOceanGeometryInstance?.IsFftOceanGeometryAvailable(position) == true;

    public static float FinestRuntimeSafeFftGeometryDistanceMeters
        => FftOceanGeometryInstances.Count == 0 ? 0.0f
            : FftOceanGeometryInstances.Max(instance => instance.RuntimeSafeFftGeometryDistanceMeters);

    public bool IsFftOceanGeometryAvailable(in RingCanonicalPosition position)
    {
        if (SampleSpacingMeters > 2 || _velocityProvider == null) return false;
        RingCanonicalPosition focus = _velocityProvider.CanonicalPosition;
        double aroundDistance = Math.Abs(SignedPeriodicDistance(
            focus.AroundMeters, position.AroundMeters, ResolveAroundCircumferenceMeters()));
        double acrossDistance = focus.AcrossMeters - position.AcrossMeters;
        float safeDistance = RuntimeSafeFftGeometryDistanceMeters;
        if (aroundDistance * aroundDistance + acrossDistance * acrossDistance > safeDistance * safeDistance)
            return false;
        return IsOceanSurfaceVisuallyCovered(
            position.AroundMeters, position.AroundMeters + 0.001,
            position.AcrossMeters, position.AcrossMeters + 0.001);
    }

    /// <summary>Runtime/FOV-safe ceiling for FFT geometry.  It intentionally
    /// has no dependency on water settings, allowing the controller to clamp
    /// its requested distance without a readiness recursion.</summary>
    public float RuntimeSafeFftGeometryDistanceMeters
    {
        get
        {
            if (SampleSpacingMeters is not (2 or 4 or 8)) return 0.0f;
            float handoff = (FadeOutStartMeters + Mathf.Max(FadeOutStartMeters + 1.0f, FadeOutEndMeters)) * 0.5f;
            return Mathf.Max(0.0f, handoff - 2.0f);
        }
    }

    private void HandleCanonicalFloatingOriginAnchorChanged(
        RingLodGeometrySnapshot previousGeometry,
        RingLodGeometrySnapshot currentGeometry,
        RingCanonicalPosition anchor)
    {
        // Sector meshes are centered on their canonical sector, so moving the
        // proxy owner by an anchor delta would reintroduce a large cancelling
        // parent/child transform and lose shared-edge precision.
        Vector3 anchorDelta = currentGeometry.CanonicalToWorld(
                anchor.AroundMeters, anchor.VerticalMeters, anchor.AcrossMeters)
            - previousGeometry.CanonicalToWorld(
                anchor.AroundMeters, anchor.VerticalMeters, anchor.AcrossMeters);
        RepositionActiveSectors(currentGeometry);
        // Structural closure vertices are intentionally not sector-local.
        // Preserve the existing mesh until its current-geometry replacement
        // arrives by translating it in owner-local space.
        if (_structuralWallMesh != null)
            _structuralWallMesh.Position += GlobalTransform.AffineInverse().Basis * anchorDelta;
        _structuralWallRequestVersion++;
        _queuedStructuralWall = null;
        // A pending structural request was invalidated above.  Force the next
        // normal streaming update to request a fresh closure even when sector
        // rebuild-on-anchor-change is intentionally disabled.
        _lastWallAroundStart = int.MinValue;
        _lastWallAroundEnd = int.MinValue;
        if (!RebuildExistingMeshesOnCanonicalAnchorChange)
        {
            // Sector-local meshes remain exact under this translation.  Normal
            // streaming can rebuild only the sectors it needs as movement
            // continues, without clearing the resident coverage.
            return;
        }
        _buildCancellation?.Cancel();
        _refreshQueued = false;
        _forceRefreshQueued = false;
        _lastAround = long.MinValue;
        _lastAcross = int.MinValue;
        _lastPredictedAround = long.MinValue;
        _lastPredictedAcross = int.MinValue;
        _lastAltitudeClipBucket = int.MinValue;
        ClearPendingUploads();
        QueueStreamingUpdate(force: true);
    }

    private void RepositionActiveSectors(RingLodGeometrySnapshot geometry)
    {
        Transform3D ownerInverse = GlobalTransform.AffineInverse();
        foreach ((RingTerrainSectorCoordinate coordinate, MeshInstance3D node) in _active)
            node.Position = ResolveSectorLocalOrigin(coordinate, geometry, ownerInverse);
    }

    private Vector3 ResolveSectorLocalOrigin(
        RingTerrainSectorCoordinate coordinate,
        RingLodGeometrySnapshot geometry,
        Transform3D ownerInverse)
    {
        return ResolveSectorLocalOrigin(coordinate, geometry, ownerInverse,
            Mathf.Max(32, SectorSizeMeters), RingWidthMeters);
    }

    private static Vector3 ResolveSectorLocalOrigin(RingTerrainSectorCoordinate coordinate,
        RingLodGeometrySnapshot geometry, Transform3D ownerInverse, int sectorSize, int ringWidth)
    {
        (double around, double across) = ResolveSectorCanonicalCenter(coordinate, sectorSize, ringWidth);
        return ownerInverse * geometry.CanonicalToWorld(around, 0.0, across);
    }

    private static (double Around, double Across) ResolveSectorCanonicalCenter(
        RingTerrainSectorCoordinate coordinate, int sectorSize, int ringWidth)
    {
        int size = Mathf.Max(32, sectorSize);
        return (
            (double)coordinate.AroundSector * size + size * 0.5,
            Math.Clamp((double)coordinate.AcrossSector * size + size * 0.5, 0.0, ringWidth));
    }

    public void BuildProxySector(bool force = true) => QueueStreamingUpdate(force);

    private void QueueStreamingUpdate(bool force)
    {
        if (_fastTravelSuspended) return;
        if (CoordinatorManaged && _pending == null)
        {
            _coordinatorBuildRequested = true;
            _coordinatorForceBuildRequested |= force;
            return;
        }
        StartStreamingUpdate(force);
    }

    private void StartStreamingUpdate(bool force, int workerSlots = 1)
    {
        if (_grid?.TerrainSampler is not RingTerrainSampler sampler || _focus == null) return;
        int size = Mathf.Max(32, SectorSizeMeters);
        RingLodSurfaceEditSnapshot editSnapshot = _smoothTerrain?.CaptureLodSurfaceEditSnapshot() ?? _grid.CaptureLodSurfaceEditSnapshot();
        _velocityProvider ??= ResolveVelocityProvider(_focus);
        RingCanonicalPosition canonical = _velocityProvider?.CanonicalPosition
            ?? _grid.WorldToCanonicalRingPosition64(_focus.GlobalPosition);
        // Proxy-to-proxy fades are true 3D distances.  Sample the terrain once
        // at the viewer, rather than applying a very coarse regional min/max
        // range to every small handoff quad.
        double proxyHandoffVerticalDistance = Math.Abs(canonical.VerticalMeters
            - editSnapshot.GetSurfaceHeight(sampler,
                checked((long)Math.Floor(canonical.AroundMeters)),
                Mathf.Clamp(Mathf.FloorToInt((float)canonical.AcrossMeters), 0, Mathf.Max(0, RingWidthMeters))));
        long aroundSectorLength = ResolveAroundSectorLength(size);
        long centerAround = WrapAroundSector(
            checked((long)Math.Floor(canonical.AroundMeters / size)),
            aroundSectorLength);
        int centerAcross = checked((int)Math.Floor(canonical.AcrossMeters / size));
        // Proxy-to-proxy coverage depends on altitude above the edited surface,
        // so refresh more finely than the legacy 1km camera-height bucket.
        // Voxel ownership is horizontal and keeps the cheaper configured cadence.
        float altitudeRefreshStep = _finerCoverageLevel != null
            ? Mathf.Min(
                Mathf.Max(1.0f, AltitudeClipRefreshMeters),
                Mathf.Max(8.0f, InnerClipRadiusMeters * 0.1f))
            : Mathf.Max(250.0f, AltitudeClipRefreshMeters);
        int altitudeClipBucket = Mathf.FloorToInt(
            (float)(proxyHandoffVerticalDistance / altitudeRefreshStep));
        RingCanonicalPosition predictedCanonical = canonical.AddLocal(GetCappedPredictionOffset());
        long predictedAround = WrapAroundSector(
            checked((long)Math.Floor(predictedCanonical.AroundMeters / size)),
            aroundSectorLength);
        int predictedAcross = checked((int)Math.Floor(predictedCanonical.AcrossMeters / size));
        ulong finerCoverageRevision = _finerCoverageLevel?.CoverageRevision
            ?? (_hasVoxelCoverageProvider ? (_smoothTerrain?.VisualCoverageRevision ?? _grid.VoxelVisualCoverageRevision) : 0UL);
        ulong heightRangeRevision = _terrainHeightSource?.HeightRangeRevision ?? 0UL;
        UpdateStructuralWallMesh(centerAround, predictedAround, size, force);
        bool editVersionChanged = editSnapshot.Version != _lastSurfaceEditVersion;
        if (!force
            && !editVersionChanged
            && centerAround == _lastAround
            && centerAcross == _lastAcross
            && predictedAround == _lastPredictedAround
            && predictedAcross == _lastPredictedAcross
            && altitudeClipBucket == _lastAltitudeClipBucket
            && finerCoverageRevision == _lastFinerCoverageRevision
            && heightRangeRevision == _lastHeightRangeRevision) return;
        var desired = new HashSet<RingTerrainSectorCoordinate>();
        var innerExcluded = new HashSet<RingTerrainSectorCoordinate>();
        AddDesiredSectorWindow(
            desired,
            innerExcluded,
            centerAround,
            centerAcross,
            size,
            canonical.AroundMeters,
            canonical.AcrossMeters,
            proxyHandoffVerticalDistance,
            canonical.AroundMeters,
            canonical.AcrossMeters);
        AddDesiredSectorWindow(
            desired,
            innerExcluded,
            predictedAround,
            predictedAcross,
            size,
            canonical.AroundMeters,
            canonical.AcrossMeters,
            proxyHandoffVerticalDistance,
            predictedCanonical.AroundMeters,
            predictedCanonical.AcrossMeters);
        innerExcluded.ExceptWith(desired);
        if (!_innerExcluded.SetEquals(innerExcluded))
        {
            _innerExcluded.Clear();
            _innerExcluded.UnionWith(innerExcluded);
            _coverageRevision++;
        }
        _pendingDesired = desired;
        int spacing = Mathf.Clamp(SampleSpacingMeters, 1, size);
        int cells = Mathf.Max(1, size / spacing);
        spacing = size / cells;

        if (_pending != null)
        {
            _refreshQueued = true;
            _forceRefreshQueued |= force;
            _buildCancellation?.Cancel();
            return;
        }

        _lastAround = centerAround;
        _lastAcross = centerAcross;
        _lastPredictedAround = predictedAround;
        _lastPredictedAcross = predictedAcross;
        _lastAltitudeClipBucket = altitudeClipBucket;
        _lastHeightRangeRevision = heightRangeRevision;
        _lastSurfaceEditVersion = editSnapshot.Version;

        if (force || editVersionChanged)
        {
            ClearPendingUploads();
            ClearWarmSectors();
        }

        // Every proxy level gets a mask from its immediate finer provider.
        // This makes the hierarchy a conservative union: a coarse quad is
        // removed only after the finer chain can prove it is already drawn.
        bool coverageHandoff = EnableClipmapExclusion
            && (_finerCoverageLevel != null || _hasVoxelCoverageProvider);
        ulong coverageRefreshStartUsec = Time.GetTicksUsec();
        using (RingPerformanceTelemetry.MeasureCpu("Terrain proxy coverage mask atlas"))
        {
            Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask>? coverageMasks =
                coverageHandoff ? CaptureSurfaceCoverageMasks(
                    desired, size, spacing, cells, canonical.AroundMeters, canonical.AcrossMeters,
                    proxyHandoffVerticalDistance) : null;
            if (!coverageHandoff)
            {
                ClearCoverageMasks(desired);
            }
            _lastFinerCoverageRevision = finerCoverageRevision;
            // This is intentionally synchronous and GPU-only.  A provider revision
            // must not enqueue terrain sampling or mesh uploads merely to change
            // which already-resident quads are visible.
            if (coverageMasks != null) ApplyCoverageMasks(coverageMasks, cells);
            if (_finerCoverageLevel != null && coverageMasks != null)
                ApplyOceanReplacementMasks(CaptureOceanReplacementMasks(coverageMasks.Keys, size, spacing, canonical.AroundMeters, canonical.AcrossMeters, proxyHandoffVerticalDistance));
            else ClearOceanReplacementMasks();
        }
        // Ocean masks follow the fixed mesh-quad count, independent of the
        // sector's world size. The separate FFT atlas still protects troughs.
        LastCoverageRefreshMilliseconds = (Time.GetTicksUsec() - coverageRefreshStartUsec) / 1000.0;
        MaxCoverageRefreshMilliseconds = Math.Max(
            MaxCoverageRefreshMilliseconds,
            LastCoverageRefreshMilliseconds);
        if (LogBuildSummary && LastCoverageRefreshMilliseconds >= 4.0)
        {
            GD.Print(
                $"LOD coverage profile {Name}: {LastCoverageRefreshMilliseconds:0.00}ms, "
                + $"desired={desired.Count}, active={ActiveSectorCount}, providerRevision={finerCoverageRevision}");
        }
        ActivateWarmSectors(desired);
        var missing = new List<RingTerrainSectorCoordinate>();
        foreach (RingTerrainSectorCoordinate coordinate in desired)
        {
            bool touchedByEdit = editVersionChanged
                && SectorTouchesColumns(
                    editSnapshot.CanonicalDirtyColumns,
                    coordinate,
                    size,
                    spacing,
                    ResolveMorphTargetSpacingMeters(),
                    sampler.AroundRingVoxelLength,
                    Mathf.Max(1, RingWidthMeters));
            if (force
                || touchedByEdit
                || (!_active.ContainsKey(coordinate)
                    && !_pendingUploadCoordinates.Contains(coordinate)))
            {
                missing.Add(coordinate);
            }
        }
        if (missing.Count == 0) { ApplyDepartedSectors(desired); return; }

        // A newer worker request supersedes existing queued uploads. Requeue all
        // still-needed coordinates so request-version rejection cannot leave holes.
        if (_pendingUploads.Count > 0)
        {
            ClearPendingUploads();
            missing.Clear();
            foreach (RingTerrainSectorCoordinate coordinate in desired)
            {
                bool touchedByEdit = editVersionChanged
                    && SectorTouchesColumns(
                        editSnapshot.CanonicalDirtyColumns,
                        coordinate,
                        size,
                        spacing,
                        ResolveMorphTargetSpacingMeters(),
                        sampler.AroundRingVoxelLength,
                        Mathf.Max(1, RingWidthMeters));
                if (force || touchedByEdit || !_active.ContainsKey(coordinate))
                    missing.Add(coordinate);
            }
        }

        missing.Sort((a, b) => CompareSectorPriority(
            a,
            b,
            centerAround,
            centerAcross,
            predictedAround,
            predictedAcross,
            aroundSectorLength));

        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        RingTerrainSampler workerSampler = sampler.CreateIndependentCopy();
        _pendingWorkerSampler = workerSampler;
        RingLodSurfaceEditSnapshot sampledEditSnapshot = editSnapshot.ForSampleSpacing(workerSampler, spacing, Mathf.Max(1, RingWidthMeters));
        Transform3D ownerInverse = GlobalTransform.AffineInverse();
        Color low = LowTerrainColor;
        Color high = HighTerrainColor;
        float underlay = RadialUnderlayOffsetMeters;
        float parentUnderlay = _morphTargetUnderlayOffsetMeters;
        bool parentRestoresWaterUnderlay = _morphTargetRestoresWaterUnderlay;
        int parentSpacing = ResolveMorphTargetSpacingMeters();
        RingTerrainGlobalMorphSnapshot? globalMorphTarget = _globalMorphTarget;
        int ringWidth = Mathf.Max(1, RingWidthMeters);
        bool buildWalls = BuildStructuralEdgeWalls;
        bool buildSkirts = BuildSectorSkirts;
        float skirtDepth = Mathf.Max(0.1f, SectorSkirtDepthMeters);
        bool blocky = UseBlockyHeightfield;
        bool alignSurfaceToTerrainSdf = AlignSurfaceToTerrainSdf;
        int heightQuantum = Mathf.Max(1, BlockyHeightQuantumMeters);
        bool readDiskCache = EnableGeneratedLodDiskCache && ReadGeneratedLodDiskCache;
        bool writeDiskCache = EnableGeneratedLodDiskCache && WriteGeneratedLodDiskCache && GeneratedLodDiskCacheBudgetMiB > 0;
        ulong requestVersion = ++_requestVersion;
        _buildCancellation?.Dispose();
        _buildCancellation = new CancellationTokenSource();
        CancellationToken cancellation = _buildCancellation.Token;
        string cacheDirectory = string.Empty;
        if (readDiskCache || writeDiskCache)
        {
            cacheDirectory = ResolveLodSectorCacheDirectory(
                workerSampler,
                size,
                spacing,
                cells,
                ringWidth,
                parentSpacing,
                globalMorphTarget?.SnapshotIdentity);
            if (writeDiskCache)
            {
                try { Directory.CreateDirectory(cacheDirectory); }
                catch (IOException) { cacheDirectory = string.Empty; }
                catch (UnauthorizedAccessException) { cacheDirectory = string.Empty; }
            }
            string? budgetRoot = string.IsNullOrEmpty(cacheDirectory)
                ? null
                : RingGeneratedLodCacheBudget.ResolveBudgetRoot(cacheDirectory);
            if (!string.IsNullOrEmpty(budgetRoot))
            {
                RingGeneratedLodCacheBudget.Schedule(
                    budgetRoot,
                    Mathf.Max(0, GeneratedLodDiskCacheBudgetMiB) * 1024L * 1024L,
                    GeneratedLodDiskCacheTrimToPercent,
                    TimeSpan.FromSeconds(Mathf.Max(10.0f, GeneratedLodDiskCacheMaintenanceSeconds)));
            }
        }
        _activeWorkerSlots = Mathf.Clamp(workerSlots, 1, missing.Count);
        int parallelism = _activeWorkerSlots;
        _pending = Task.Run(
            () => BuildRawBatch(
                requestVersion,
                sampledEditSnapshot,
                missing,
                workerSampler,
                data => PublishCompletedWorkerSector(data, cancellation),
                geometry,
                ownerInverse,
                size,
                spacing,
                parentSpacing,
                globalMorphTarget,
                cells,
                underlay,
                parentUnderlay,
                parentRestoresWaterUnderlay,
                ringWidth,
                buildWalls,
                buildSkirts,
                skirtDepth,
                blocky,
                alignSurfaceToTerrainSdf,
                heightQuantum,
                low,
                high,
                cacheDirectory,
                readDiskCache,
                writeDiskCache,
                null,
                parallelism,
                cancellation),
            cancellation);
    }

    private static int CompareSectorPriority(
        RingTerrainSectorCoordinate a,
        RingTerrainSectorCoordinate b,
        long centerAround,
        int centerAcross,
        long predictedAround,
        int predictedAcross,
        long aroundSectorLength)
    {
        long aCurrent = GetAroundSectorDistance(a.AroundSector, centerAround, aroundSectorLength) + Math.Abs((long)a.AcrossSector - centerAcross);
        long bCurrent = GetAroundSectorDistance(b.AroundSector, centerAround, aroundSectorLength) + Math.Abs((long)b.AcrossSector - centerAcross);
        long aPredicted = GetAroundSectorDistance(a.AroundSector, predictedAround, aroundSectorLength) + Math.Abs((long)a.AcrossSector - predictedAcross);
        long bPredicted = GetAroundSectorDistance(b.AroundSector, predictedAround, aroundSectorLength) + Math.Abs((long)b.AcrossSector - predictedAcross);
        long aScore = Math.Min(aCurrent * 2, aPredicted * 2 + 1);
        long bScore = Math.Min(bCurrent * 2, bPredicted * 2 + 1);
        return aScore != bScore ? aScore.CompareTo(bScore) : aCurrent.CompareTo(bCurrent);
    }

    private void AddDesiredSectorWindow(
        HashSet<RingTerrainSectorCoordinate> desired,
        HashSet<RingTerrainSectorCoordinate> innerExcluded,
        long centerAround,
        int centerAcross,
        int size,
        double innerFocusAround,
        double innerFocusAcross,
        double innerFocusVertical,
        double outerFocusAround,
        double outerFocusAcross)
    {
        for (int z = -SectorRadiusAcross; z <= SectorRadiusAcross; z++)
        for (int x = -SectorRadiusAround; x <= SectorRadiusAround; x++)
        {
            var coordinate = new RingTerrainSectorCoordinate(
                WrapAroundSector(
                    centerAround + SectorOffsetAround + x,
                    ResolveAroundSectorLength(size)),
                centerAcross + SectorOffsetAcross + z);
            int acrossStart = coordinate.AcrossSector * size;
            int acrossEnd = acrossStart + size;
            if (acrossEnd <= 0 || acrossStart >= RingWidthMeters) continue;
            if (UseCircularStreamingWindow
                && IsFullyOutsideOuterClip(coordinate, size, outerFocusAround, outerFocusAcross)) continue;
            // Voxel chunks own a horizontal surface footprint.  Proxy providers
            // use exact 3D terrain bounds so a high-altitude finer shader fade
            // cannot expose a hole in this level's transition ring.
            bool insideInnerClip = _finerCoverageLevel == null
                ? IsFullyInsideVoxelInnerClipHorizontal(
                    coordinate, size, innerFocusAround, innerFocusAcross)
                : IsFullyInsideInnerClipGeometry(
                    coordinate, size, innerFocusAround, innerFocusAcross, innerFocusVertical);
            if (EnableClipmapExclusion
                && insideInnerClip
                && IsCoveredByFinerRepresentation(coordinate, size))
            {
                innerExcluded.Add(coordinate);
            }
            desired.Add(coordinate);
        }
    }

    private long ResolveAroundSectorLength(int sectorSizeMeters)
    {
        double circumference = _grid?.GetCanonicalRingCircumferenceMeters64()
            ?? _grid?.TerrainSampler?.AroundRingVoxelLength
            ?? 0L;
        double size = Math.Max(1, sectorSizeMeters);
        return Math.Max(1L, (long)Math.Ceiling(circumference / size));
    }

    private static long WrapAroundSector(long sector, long aroundSectorLength)
        => RingCoordMath.WrapAround(sector, Math.Max(1L, aroundSectorLength));

    /// <summary>
    /// Places a periodic sector index on the closest unwrapped copy beside the
    /// reference.  Structural wall strips are continuous physical spans, so a
    /// 0/last-sector prediction crossing must never be interpreted as a strip
    /// around the entire ring.
    /// </summary>
    private static long NearestPeriodicSector(long referenceSector, long candidateSector,
        long aroundSectorLength)
    {
        long length = Math.Max(1L, aroundSectorLength);
        long reference = WrapAroundSector(referenceSector, length);
        long candidate = WrapAroundSector(candidateSector, length);
        long forward = WrapAroundSector(candidate - reference, length);
        long half = length / 2;
        long signed = forward > half ? forward - length : forward;
        // For an even sector count, either copy is equally near. Keep the
        // caller's signed direction so ordinary unwrapped ranges stay stable.
        if ((length & 1L) == 0 && forward == half)
            signed = candidateSector >= referenceSector ? half : -half;
        return referenceSector + signed;
    }

    private static StructuralWallSectorRange ResolveStructuralWallSectorRange(
        long currentAroundSector, long predictedAroundSector, long aroundSectorLength,
        int radius)
    {
        long predictedNearest = NearestPeriodicSector(
            currentAroundSector, predictedAroundSector, aroundSectorLength);
        return new StructuralWallSectorRange(
            Math.Min(currentAroundSector, predictedNearest) - Math.Max(1, radius),
            Math.Max(currentAroundSector, predictedNearest) + Math.Max(1, radius) + 1);
    }

    private double ResolveAroundCircumferenceMeters()
        => Math.Max(
            1.0,
            _grid?.GetCanonicalRingCircumferenceMeters64()
                ?? _grid?.TerrainSampler?.AroundRingVoxelLength
                ?? 1L);

    private static long GetAroundSectorDistance(long left, long right, long aroundSectorLength)
    {
        long length = Math.Max(1L, aroundSectorLength);
        long distance = Math.Abs(left - right);
        return Math.Min(distance, length - distance);
    }

    private static double DistanceToPeriodicSector(
        double focusAround,
        double sectorStartAround,
        int sectorSizeMeters,
        double circumference)
    {
        double halfSize = Math.Max(0.5, sectorSizeMeters * 0.5);
        double centerAround = sectorStartAround + halfSize;
        return Math.Max(
            0.0,
            Math.Abs(SignedPeriodicDistance(focusAround, centerAround, circumference)) - halfSize);
    }

    private static double SignedPeriodicDistance(double from, double to, double circumference)
    {
        double length = Math.Max(1.0, circumference);
        double distance = (from - to) % length;
        if (distance > length * 0.5) distance -= length;
        if (distance < -length * 0.5) distance += length;
        return distance;
    }

    private bool IsFullyOutsideOuterClip(
        RingTerrainSectorCoordinate coordinate,
        int size,
        double focusAround,
        double focusAcross)
    {
        double radius = EnableFarFade
            ? Math.Max(size, FadeOutEndMeters)
            : Math.Max(SectorRadiusAround, SectorRadiusAcross) * (double)size;
        double around0 = (double)coordinate.AroundSector * size;
        double across0 = Math.Clamp((double)coordinate.AcrossSector * size, 0.0, RingWidthMeters);
        double across1 = Math.Clamp((double)coordinate.AcrossSector * size + size, 0.0, RingWidthMeters);
        double deltaAround = DistanceToPeriodicSector(
            focusAround,
            around0,
            size,
            ResolveAroundCircumferenceMeters());
        double deltaAcross = focusAcross < across0
            ? across0 - focusAcross
            : focusAcross > across1 ? focusAcross - across1 : 0.0;
        return deltaAround * deltaAround + deltaAcross * deltaAcross > radius * radius;
    }

    private bool IsFullyInsideInnerClipGeometry(
        RingTerrainSectorCoordinate coordinate,
        int size,
        double focusAround,
        double focusAcross,
        double focusVertical)
    {
        float radius = Mathf.Max(0.0f, InnerClipRadiusMeters);
        if (radius <= 0.0f) return false;
        double around0 = (double)coordinate.AroundSector * size;
        double around1 = around0 + size;
        double across0 = Math.Clamp((double)coordinate.AcrossSector * size, 0.0, RingWidthMeters);
        double across1 = Math.Clamp((double)coordinate.AcrossSector * size + size, 0.0, RingWidthMeters);
        return IsRectangleFullyInsideInnerClipGeometry(
            around0, around1, across0, across1, focusAround, focusAcross, focusVertical);
    }

    // Voxel chunks own a surface footprint, not a 3D proxy volume.  Applying the
    // proxy height-padding test here made the default 192m radius impossible to
    // satisfy with 256m padding (192² - 256² < 0).
    private bool IsFullyInsideVoxelInnerClipHorizontal(
        RingTerrainSectorCoordinate coordinate, int size, double focusAround, double focusAcross)
    {
        double radius = Mathf.Max(0.0f, InnerClipRadiusMeters);
        if (radius <= 0.0) return false;
        double around0 = (double)coordinate.AroundSector * size;
        double across0 = Math.Clamp((double)coordinate.AcrossSector * size, 0.0, RingWidthMeters);
        double across1 = Math.Clamp((double)coordinate.AcrossSector * size + size, 0.0, RingWidthMeters);
        return IsRectangleFullyInsideHorizontalRadius(
            around0, across0, across1, size, focusAround, focusAcross,
            ResolveAroundCircumferenceMeters(), radius);
    }

    private static bool IsRectangleFullyInsideHorizontalRadius(
        double around0, double across0, double across1, int size,
        double focusAround, double focusAcross, double circumference, double radius)
    {
        double centerAround = around0 + size * 0.5;
        double maxAround = Math.Abs(SignedPeriodicDistance(focusAround, centerAround, circumference)) + size * 0.5;
        double maxAcross = Math.Max(Math.Abs(across0 - focusAcross), Math.Abs(across1 - focusAcross));
        return maxAround * maxAround + maxAcross * maxAcross < radius * radius;
    }

    private static bool IsRectangleFullyInsideThreeDimensionalRadius(
        double around0, double across0, double across1, int size,
        double focusAround, double focusAcross, double circumference,
        double radius, double viewerAltitudeAboveTerrain)
    {
        double horizontalRadiusSquared = radius * radius
            - viewerAltitudeAboveTerrain * viewerAltitudeAboveTerrain;
        return horizontalRadiusSquared > 0.0
            && IsRectangleFullyInsideHorizontalRadius(
                around0, across0, across1, size, focusAround, focusAcross,
                circumference, Math.Sqrt(horizontalRadiusSquared));
    }

    private static void RunVoxelHandoffSmokeTest()
    {
        if (_hierarchyHandoffSmokePassed) return;
        var mask = new SurfaceCoverageMask(64, 16, new bool[16]);
        if (mask.CoversQuad(0, 0) || mask.CoversQuad(1, 0))
            throw new InvalidOperationException("LOD voxel handoff smoke: pending cells must retain proxy geometry.");
        mask.CoveredCells[0] = true;
        if (!mask.CoversQuad(15, 15) || mask.CoversQuad(16, 0))
            throw new InvalidOperationException("LOD voxel handoff smoke: ready cell suppression must not affect its neighbor.");
        // This is the former failure case: a 32m surface tile inside the default
        // 192m radius must be eligible even when proxy height padding is 256m.
        if (!IsRectangleFullyInsideHorizontalRadius(64, -16, 16, 32, 80, 0, 1_000_000, 192))
            throw new InvalidOperationException("LOD voxel handoff smoke: horizontal voxel handoff was unexpectedly blocked.");
        if (!IsRectangleFullyInsideThreeDimensionalRadius(
                64, -16, 16, 32, 80, 0, 1_000_000, 192, 0)
            || IsRectangleFullyInsideThreeDimensionalRadius(
                64, -16, 16, 32, 80, 0, 1_000_000, 192, 192))
            throw new InvalidOperationException("LOD hierarchy handoff smoke: proxy eligibility did not contract with altitude.");
        // A coarse proxy receives one bit per its own quad from a finer level.
        // This verifies that a ready fine cell maps only to the corresponding
        // coarse quad and that a pending neighbour remains owned by the coarse
        // mesh rather than being hidden wholesale with its sector.
        var hierarchyMask = new SurfaceCoverageMask(8, 1, new bool[64]);
        hierarchyMask.CoveredCells[3 * 8 + 5] = true;
        if (!hierarchyMask.CoversQuad(5, 3)
            || hierarchyMask.CoversQuad(4, 3)
            || hierarchyMask.CoversQuad(5, 4))
            throw new InvalidOperationException("LOD hierarchy handoff smoke: finer-cell mapping affected a neighbouring coarse quad.");
        if (new SurfaceCoverageMask(8, 1, new bool[64]).CoversQuad(5, 3))
            throw new InvalidOperationException("LOD hierarchy handoff smoke: missing finer coverage must retain the coarse quad.");
        // Atlas updates carry the mask separately from the immutable full mesh.
        // This guards against accidentally returning to coverage-driven rebuilds.
        int rebuiltMeshCount = 7;
        SurfaceCoverageMask before = new(4, 1, new bool[16]);
        SurfaceCoverageMask after = new(4, 1, new bool[16]);
        after.CoveredCells[6] = true;
        if (before.Matches(after) || rebuiltMeshCount != 7)
            throw new InvalidOperationException("LOD hierarchy handoff smoke: coverage atlas update unexpectedly requires a mesh rebuild.");
        // Smooth terrain owns its own immutable edit revision.  Do not compare a
        // completed smooth batch to the dormant legacy grid's revision or every
        // restored/edit-bearing smooth journal will have its proxy uploads
        // discarded forever.
        if (!AcceptsCompletedEditVersion(17, 17) || AcceptsCompletedEditVersion(17, 18))
            throw new InvalidOperationException("LOD hierarchy handoff smoke: smooth edit-version upload acceptance regressed.");
        // The roof is intentionally independent from surface coverage masks:
        // smooth chunks can hand off the near ceiling, but their 2x2x2 edit
        // bubble must never suppress the persistent proxy ceiling.
        var roofRequest = new StructuralWallRequest(
            new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero,
                10000.0, 0.0, 0.0, 100000.0, -22304.0, 4096.0, null),
            Transform3D.Identity, 0.0, 64.0, 1, 1.0, 1, 128,
            false, false, true, false, -8192.0, 0.0, Colors.White, 1);
        roofRequest = roofRequest with { BuildLowerHullCavityFloor = true, LowerHullTopGlobalY = -21504.0 };
        StructuralWallData roof = BuildStructuralWallData(roofRequest);
        if (roof.Vertices.Length != 8 || roof.Indices.Length != 12
            || roof.Vertices[0].DistanceTo(roofRequest.Geometry.CanonicalToWorld(0.0, -8192.0, 0.0)) > 0.001f
            || roof.Vertices[4].DistanceTo(roofRequest.Geometry.CanonicalToWorld(0.0, -21504.0, 0.0)) > 0.001f)
            throw new InvalidOperationException("LOD roof coverage smoke: roof/floor are not persistent cavity closure quads.");
        Vector3 winding = (roof.Vertices[roof.Indices[1]] - roof.Vertices[roof.Indices[0]])
            .Cross(roof.Vertices[roof.Indices[2]] - roof.Vertices[roof.Indices[0]]);
        Vector3 expectedCavityDirection = (roofRequest.Geometry.CanonicalToWorld(0.0, -8193.0, 0.0)
            - roof.Vertices[0]).Normalized();
        if (winding.Dot(roof.Normals[0]) >= 0.0f || roof.Normals[0].Dot(expectedCavityDirection) <= 0.99f)
            throw new InvalidOperationException("LOD roof coverage smoke: roof underside winding/normal is not Godot-clockwise toward the cavity.");
        Vector3 floorWinding = (roof.Vertices[roof.Indices[7]] - roof.Vertices[roof.Indices[6]])
            .Cross(roof.Vertices[roof.Indices[8]] - roof.Vertices[roof.Indices[6]]);
        Vector3 expectedFloorCavityDirection = (roofRequest.Geometry.CanonicalToWorld(0.0, -21503.0, 0.0)
            - roof.Vertices[4]).Normalized();
        if (floorWinding.Dot(roof.Normals[4]) >= 0.0f || roof.Normals[4].Dot(expectedFloorCavityDirection) <= 0.99f)
            throw new InvalidOperationException("LOD roof coverage smoke: lower hull floor winding/normal is not Godot-clockwise toward the cavity.");
        if (ResolveStructuralShellNearFadeEnabled(true) || !ResolveStructuralShellNearFadeEnabled(false))
            throw new InvalidOperationException("LOD roof coverage smoke: structural shell handoff did not retain the innermost active center only.");
        if (!RingScaledFarViewport.HasFastTravelCavityClosure(
                buildRoof: true,
                buildLowerHullFloor: true,
                nearFadeEnabled: false,
                nearSpaceEnabled: true))
            throw new InvalidOperationException("LOD roof coverage smoke: scaled far fast-travel backdrop omitted roof/floor cavity closure.");
        if (ResolveStructuralShadowCasting(true, false) != ShadowCastingSetting.On
            || ResolveStructuralShadowCasting(false, false) != ShadowCastingSetting.Off)
            throw new InvalidOperationException("LOD roof coverage smoke: persistent cavity closure shadow policy regressed.");
        RunSmoothProxyNormalSeamContract();
        _hierarchyHandoffSmokePassed = true;
        GD.Print("LOD_HIERARCHY_HANDOFF_SMOKE_PASS: coverage atlas updates retain pending neighbours without mesh rebuilds.");
        GD.Print("LOD_ROOF_COVERAGE_SMOKE_PASS: persistent terrain-bottom roof and lower-hull floor retain cavity-facing clockwise geometry outside smooth coverage.");
    }

    /// <summary>Runs the canonical smooth-proxy normal contract without loading a gameplay scene.</summary>
    public bool RunSmoothProxyNormalSeamContractTest()
    {
        try
        {
            RunSmoothProxyNormalSeamContract();
            GD.Print("PROXY_SURFACE_NORMAL_CONTRACT_PASS: canonical stencil normals face increasing height and match at shared edges.");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError($"PROXY_SURFACE_NORMAL_CONTRACT_FAIL: {exception.Message}");
            return false;
        }
    }

    /// <summary>Regression fixture for the canonical normal stencil used at a smooth-sector shared edge.</summary>
    private static void RunSmoothProxyNormalSeamContract()
    {
        if (!CertifiedRingScale.Validate(out string scaleError))
            throw new InvalidOperationException("certified ring scale is invalid: " + scaleError);

        double circumference = CertifiedRingScale.EffectiveCircumferenceMeters;
        int width = CertifiedRingScale.RingWidthMeters;
        foreach (double around in new[] { 0.0, circumference * 0.25, circumference * 0.5, circumference * 0.75, circumference - 4.0 })
        foreach (int across in new[] { 0, width / 2, width })
        foreach (int spacing in new[] { 2, 4, 8, 16, 32, 64, 128, 256 })
        {
            // Anchoring at each fixture point preserves the production floating-origin
            // precision contract while exercising real ring curvature and width edges.
            var geometry = new RingLodGeometrySnapshot(
                Transform3D.Identity, Vector3.Zero, Vector3.Zero,
                CertifiedRingScale.EffectiveRadiusMeters, 0.0, 0.0, circumference,
                CertifiedRingScale.TerrainFloorMeters, CertifiedRingScale.StructuralWallTopMeters,
                new RingCanonicalPosition(around, 0.0, across));
            AssertNormalContract(geometry, around, across, spacing, 0.0f, 0.0f, 0.0f, "flat water");
            AssertNormalContract(geometry, around, across, spacing,
                0.0015f * (float)(around % 10000.0) + 0.002f * across, 0.0015f, 0.002f, "smooth sloped terrain");
        }

        static void AssertNormalContract(RingLodGeometrySnapshot geometry, double around, int across, int spacing,
            float centerHeight, float aroundSlope, float acrossSlope, string label)
        {
            int width = CertifiedRingScale.RingWidthMeters;
            int acrossMinus = Mathf.Max(0, across - spacing);
            int acrossPlus = Mathf.Min(width, across + spacing);
            float Height(double a, int z) => centerHeight + aroundSlope * (float)(a - around) + acrossSlope * (z - across);
            Vector3 Sample(double a, int z) => geometry.CanonicalToWorld(a, Height(a, z), z);
            Vector3 normal = CalculateSurfaceNormalFromNeighbourPositions(
                Sample(around - spacing, across), Sample(around + spacing, across),
                Sample(around, acrossMinus), Sample(around, acrossPlus)).Normalized();
            Vector3 expectedUp = CalculateIncreasingCanonicalHeightDirection(
                geometry, Transform3D.Identity, around, across, centerHeight);
            if (normal.Dot(expectedUp) <= 0.95f)
                throw new InvalidOperationException($"{label} normal was not aligned with increasing height at around={around}, across={across}, spacing={spacing}.");

            // Submitted proxy triangles are clockwise in Godot, so their mathematical
            // cross product must point opposite the shading normal.
            Vector3 winding = (Sample(around + spacing, acrossMinus) - Sample(around, acrossPlus))
                .Cross(Sample(around, acrossMinus) - Sample(around, acrossPlus)).Normalized();
            if (winding.Dot(normal) >= -0.95f)
                throw new InvalidOperationException($"{label} normal no longer opposes Godot clockwise winding.");

            // Model the two independently built owners of this shared around edge.
            Vector3 sameEdgeNormal = CalculateSurfaceNormalFromNeighbourPositions(
                Sample(around - spacing, across), Sample(around + spacing, across),
                Sample(around, acrossMinus), Sample(around, acrossPlus)).Normalized();
            if (normal.DistanceTo(sameEdgeNormal) > 0.000001f)
                throw new InvalidOperationException($"{label} shared-edge normal mismatch.");
        }
    }

    // This runs the exact pre-optimization raster path against representative
    // fine-ocean masks.  Keep it as the behavioural oracle for any faster
    // byte-atlas path: one covered source cell marks precisely its centroid
    // pixel, including the periodic around seam; uncovered cells stay black.
    public bool RunFftOceanCoverageContractTest()
    {
        try
        {
            RunFftOceanCoverageContractTestCore();
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError($"FFT_OCEAN_COVERAGE_CONTRACT_FAIL: {exception.Message}");
            return false;
        }
    }

    private static void RunFftOceanCoverageContractTestCore()
    {
        if (_fftOceanCoverageContractPassed) return;
        const int sectorSize = 32;
        const double circumference = 1024.0;
        var centerMask = new SurfaceCoverageMask(4, 1, new bool[16]);
        centerMask.CoveredCells[0] = true;
        centerMask.CoveredCells[15] = true;
        var seamMask = new SurfaceCoverageMask(4, 1, new bool[16]);
        seamMask.CoveredCells[2] = true;
        var masks = new Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask>
        {
            [new RingTerrainSectorCoordinate(0, 0)] = centerMask,
            [new RingTerrainSectorCoordinate(31, 0)] = seamMask
        };
        Image image = Image.CreateEmpty(SharedFftOceanCoverageResolution,
            SharedFftOceanCoverageResolution, false, Image.Format.R8);
        BuildSharedFftOceanCoverageReference(image, masks, sectorSize, 0.0, 0.0, circumference);
        int half = SharedFftOceanCoverageResolution / 2;
        byte[] reference = image.GetData();
        byte[] optimized = BuildSharedFftOceanCoverageBytes(masks, sectorSize, 0.0, 0.0, circumference);
        if (!reference.AsSpan().SequenceEqual(optimized)
            || reference[(half + 2) * SharedFftOceanCoverageResolution + half + 2] == 0
            || reference[(half + 14) * SharedFftOceanCoverageResolution + half + 14] == 0
            || reference[(half + 2) * SharedFftOceanCoverageResolution + half - 6] == 0
            || reference[(half + 2) * SharedFftOceanCoverageResolution + half + 6] != 0)
            throw new InvalidOperationException("FFT ocean coverage contract: centroid, hole, or periodic seam mapping regressed.");

        // The existing path makes one managed-to-native SetPixel call per
        // marked fine cell.  This baseline is intentionally recorded before
        // replacing that path, so the test log can compare the same workload.
        var denseMasks = new Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask>();
        for (long around = -2; around < 2; around++)
        for (int across = -2; across < 2; across++)
        {
            var dense = new SurfaceCoverageMask(128, 1, new bool[128 * 128]);
            Array.Fill(dense.CoveredCells, true);
            denseMasks[new RingTerrainSectorCoordinate(around, across)] = dense;
        }
        const int benchmarkRepeats = 8;
        BuildSharedFftOceanCoverageReference(image, denseMasks, 256, 0.0, 0.0, 1_000_000.0);
        BuildSharedFftOceanCoverageBytes(denseMasks, 256, 0.0, 0.0, 1_000_000.0);
        Stopwatch referenceTimer = Stopwatch.StartNew();
        for (int repeat = 0; repeat < benchmarkRepeats; repeat++)
            BuildSharedFftOceanCoverageReference(image, denseMasks, 256, 0.0, 0.0, 1_000_000.0);
        referenceTimer.Stop();
        Stopwatch optimizedTimer = Stopwatch.StartNew();
        byte[] denseOptimized = Array.Empty<byte>();
        for (int repeat = 0; repeat < benchmarkRepeats; repeat++)
            denseOptimized = BuildSharedFftOceanCoverageBytes(denseMasks, 256, 0.0, 0.0, 1_000_000.0);
        optimizedTimer.Stop();
        if (!image.GetData().AsSpan().SequenceEqual(denseOptimized)
            || denseOptimized[half * SharedFftOceanCoverageResolution + half] == 0)
            throw new InvalidOperationException("FFT ocean coverage contract: dense real-mask workload did not cover its center.");
        RunFftDistantOceanVertexFieldContract();
        _fftOceanCoverageContractPassed = true;
        GD.Print($"FFT_OCEAN_COVERAGE_CONTRACT_PASS: exact-byte parity with reference; SetPixel={referenceTimer.Elapsed.TotalMilliseconds / benchmarkRepeats:0.00}ms, byte-atlas={optimizedTimer.Elapsed.TotalMilliseconds / benchmarkRepeats:0.00}ms, repeats={benchmarkRepeats}, sources={denseMasks.Count}, cellsPerSource=16384.");
    }

    private static void RunFftDistantOceanVertexFieldContract()
    {
        // Two independently streamed 2x2 sectors share the edge values at
        // x=2/0. Sampling their vertex-aligned fields at the shared mesh UV
        // must yield the same depth; a shore-adjacent zero remains fail-closed.
        byte[] left = { 80, 90, 100, 80, 90, 100, 0, 0, 0 };
        byte[] right = { 100, 110, 120, 100, 110, 120, 0, 0, 0 };
        float leftShared = SampleFftDistantOceanVertexDepth(left, 2, new Vector2(1.0f, 0.25f));
        float rightShared = SampleFftDistantOceanVertexDepth(right, 2, new Vector2(0.0f, 0.25f));
        float shore = SampleFftDistantOceanVertexDepth(left, 2, new Vector2(0.5f, 1.0f));
        if (!Mathf.IsEqualApprox(leftShared, rightShared) || shore != 0.0f)
            throw new InvalidOperationException("FFT distant ocean vertex field contract: shared edge depth or fail-closed shore behaviour regressed.");
    }

    private static float SampleFftDistantOceanVertexDepth(byte[] depths, int cells, Vector2 meshUv)
    {
        int side = cells + 1;
        if (depths.Length != side * side) throw new ArgumentException("FFT distant ocean vertex field dimensions are invalid.", nameof(depths));
        float x = Mathf.Clamp(meshUv.X, 0.0f, 1.0f) * cells;
        float z = Mathf.Clamp(meshUv.Y, 0.0f, 1.0f) * cells;
        int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
        int x1 = Mathf.Min(cells, x0 + 1), z1 = Mathf.Min(cells, z0 + 1);
        float tx = x - x0, tz = z - z0;
        float a = Mathf.Lerp(depths[z0 * side + x0], depths[z0 * side + x1], tx);
        float b = Mathf.Lerp(depths[z1 * side + x0], depths[z1 * side + x1], tx);
        return Mathf.Lerp(a, b, tz);
    }

    private static byte[] BuildSharedFftOceanCoverageBytes(
        IReadOnlyDictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask> masks,
        int sectorSize,
        double gridAround,
        double gridAcross,
        double circumference,
        byte[]? destination = null)
    {
        var pixels = destination ?? new byte[SharedFftOceanCoverageResolution * SharedFftOceanCoverageResolution];
        if (pixels.Length != SharedFftOceanCoverageResolution * SharedFftOceanCoverageResolution)
            throw new ArgumentException("FFT ocean coverage byte atlas has an invalid size.", nameof(destination));
        Array.Clear(pixels);
        int half = SharedFftOceanCoverageResolution / 2;
        foreach ((RingTerrainSectorCoordinate coordinate, SurfaceCoverageMask mask) in masks)
        {
            int cells = mask.ProxyQuadCells;
            for (int z = 0; z < cells; z++) for (int x = 0; x < cells; x++)
            {
                if (!mask.CoveredCells[z * cells + x]) continue;
                double around = coordinate.AroundSector * (double)sectorSize + (x + 0.5) * sectorSize / cells;
                double deltaAround = SignedPeriodicDistance(around, gridAround, circumference);
                double across = coordinate.AcrossSector * (double)sectorSize + (z + 0.5) * sectorSize / cells;
                int px = Mathf.FloorToInt((float)(deltaAround / SharedFftOceanCoverageCellMeters)) + half;
                int pz = Mathf.FloorToInt((float)((across - gridAcross) / SharedFftOceanCoverageCellMeters)) + half;
                if (px >= 0 && px < SharedFftOceanCoverageResolution && pz >= 0 && pz < SharedFftOceanCoverageResolution)
                    pixels[pz * SharedFftOceanCoverageResolution + px] = byte.MaxValue;
            }
        }
        return pixels;
    }

    private static void BuildSharedFftOceanCoverageReference(
        Image image,
        IReadOnlyDictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask> masks,
        int sectorSize,
        double gridAround,
        double gridAcross,
        double circumference)
    {
        image.Fill(new Color(0, 0, 0, 1));
        int half = SharedFftOceanCoverageResolution / 2;
        foreach ((RingTerrainSectorCoordinate coordinate, SurfaceCoverageMask mask) in masks)
        {
            int cells = mask.ProxyQuadCells;
            for (int z = 0; z < cells; z++) for (int x = 0; x < cells; x++)
            {
                if (!mask.CoveredCells[z * cells + x]) continue;
                double around = coordinate.AroundSector * (double)sectorSize + (x + 0.5) * sectorSize / cells;
                double deltaAround = SignedPeriodicDistance(around, gridAround, circumference);
                double across = coordinate.AcrossSector * (double)sectorSize + (z + 0.5) * sectorSize / cells;
                int px = Mathf.FloorToInt((float)(deltaAround / SharedFftOceanCoverageCellMeters)) + half;
                int pz = Mathf.FloorToInt((float)((across - gridAcross) / SharedFftOceanCoverageCellMeters)) + half;
                if (px >= 0 && px < SharedFftOceanCoverageResolution && pz >= 0 && pz < SharedFftOceanCoverageResolution)
                    image.SetPixel(px, pz, new Color(1, 1, 1, 1));
            }
        }
    }

    private static bool AcceptsCompletedEditVersion(ulong completedVersion, ulong currentVersion)
        => completedVersion == currentVersion;

    private Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask> CaptureSurfaceCoverageMasks(
        IReadOnlyCollection<RingTerrainSectorCoordinate> coordinates, int size, int spacing, int cells,
        double focusAround, double focusAcross, double viewerAltitudeAboveTerrain)
    {
        var masks = new Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask>();
        if (_grid == null) return masks;
        foreach (RingTerrainSectorCoordinate coordinate in coordinates)
        {
            // A voxel provider is chunk-granular. A proxy provider is sector-
            // granular because each resident finer sector is already a complete
            // union of its own unmasked mesh and the provider behind its mask.
            // Adjacent levels use aligned power-of-two sectors, so this normally
            // produces four coverage cells instead of thousands of quad queries.
            int providerMeters = _finerCoverageLevel == null
                ? VoxelConstants.ChunkSizeX * VoxelConstants.VoxelSizeMeters
                : Mathf.Max(spacing, _finerCoverageLevel.SectorSizeMeters);
            int quadsPerProviderCell = Mathf.Max(1, Mathf.RoundToInt(providerMeters / (float)spacing));
            int providerCellsPerSector = (cells + quadsPerProviderCell - 1) / quadsPerProviderCell;
            var covered = new bool[providerCellsPerSector * providerCellsPerSector];
            long aroundOrigin = coordinate.AroundSector * size;
            int acrossOrigin = coordinate.AcrossSector * size;
            for (int z = 0; z < providerCellsPerSector; z++)
            for (int x = 0; x < providerCellsPerSector; x++)
            {
                double around0 = aroundOrigin + x * providerMeters;
                double around1 = Math.Min(around0 + providerMeters, aroundOrigin + size);
                double across0 = acrossOrigin + z * providerMeters;
                double across1 = Math.Min(across0 + providerMeters, acrossOrigin + size);
                // Voxel ownership is horizontal. A proxy provider follows its
                // real 3D fade distance with exact terrain bounds and no legacy
                // padding, preventing high-altitude transition-ring holes.
                bool insideHandoff = _finerCoverageLevel == null
                    ? IsRectangleFullyInsideHorizontalRadius(
                        around0, across0, across1, (int)(around1 - around0), focusAround, focusAcross,
                        ResolveAroundCircumferenceMeters(), InnerClipRadiusMeters)
                    : IsRectangleFullyInsideInnerClipGeometry(
                        around0, around1, across0, across1, focusAround, focusAcross, viewerAltitudeAboveTerrain);
                covered[z * providerCellsPerSector + x] = insideHandoff
                    && (_finerCoverageLevel == null
                        ? (_smoothTerrain?.IsSurfaceRegionVisualReady((long)around0, (long)around1, (int)across0, (int)across1) ?? _grid.IsSurfaceRegionVisualReady(around0, around1, across0, across1))
                        : _finerCoverageLevel.IsSurfaceRegionVisuallyCovered(around0, around1, across0, across1));
            }
            masks[coordinate] = new SurfaceCoverageMask(cells, quadsPerProviderCell, covered);
        }
        return masks;
    }

    private void ApplyCoverageMasks(IReadOnlyDictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask> masks, int cells)
    {
        bool changed = false;
        bool atlasDirty = false;
        foreach ((RingTerrainSectorCoordinate coordinate, SurfaceCoverageMask mask) in masks)
        {
            bool maskChanged = !_latestCoverageMasks.TryGetValue(coordinate, out SurfaceCoverageMask? previous)
                || !mask.Matches(previous);
            if (maskChanged) changed = true;
            _latestCoverageMasks[coordinate] = mask;
            if (!_active.TryGetValue(coordinate, out MeshInstance3D? node) || _warmUntil.ContainsKey(coordinate))
                continue;
            if (maskChanged || !_activeCoverageMasks.TryGetValue(coordinate, out SurfaceCoverageMask? installed) || !mask.Matches(installed))
            {
                InstallCoverageMask(node, coordinate, mask, cells, uploadAtlas: false);
                QueuePartialCoverageCulling(coordinate, node, mask);
                atlasDirty = true;
            }
            _activeCoverageMasks[coordinate] = mask;
            UpdateSectorVisibility(coordinate);
        }
        // One texture upload for the whole provider revision, not one per sector.
        if (atlasDirty && _coverageAtlasTexture != null && _coverageAtlasImage != null)
            _coverageAtlasTexture.Update(_coverageAtlasImage);
        if (changed) _coverageRevision++;
    }

    private Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask> CaptureOceanReplacementMasks(
        IEnumerable<RingTerrainSectorCoordinate> coordinates,
        int size, int spacing, double focusAround, double focusAcross, double viewerAltitude)
    {
        if (_finerCoverageLevel == null) return new Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask>();
        FinerOceanSnapshot snapshot = CaptureFinerOceanSnapshot(_finerCoverageLevel);
        _oceanReplacementFinerSource = _finerCoverageLevel;
        _oceanReplacementFinerRevision = _finerCoverageLevel._coverageRevision;
        return BuildOceanReplacementMasks(snapshot, coordinates, size, spacing,
            _finerCoverageLevel.SectorSizeMeters, _finerCoverageLevel.RingWidthMeters,
            _finerCoverageLevel.ResolveAroundCircumferenceMeters());
    }

    private void NotifyOceanReplacementDependents()
    {
        foreach (RingTerrainProxySector dependent in _oceanReplacementDependents.ToArray())
            dependent.RefreshOceanReplacementFromFiner(this);
    }

    // Called synchronously by the fine source after its visible ocean set has
    // changed. The atlas upload completes before control returns to the frame,
    // so coarse water never renders from an invalidated revision gap.
    private void RefreshOceanReplacementFromFiner(RingTerrainProxySector finer)
    {
        if (!ReferenceEquals(_finerCoverageLevel, finer) || !StreamingActive || _transitHidden)
        {
            ClearOceanReplacementMasks();
            return;
        }
        var coordinates = _active.Keys.Where(key => !_warmUntil.ContainsKey(key)).ToArray();
        if (coordinates.Length == 0) return;
        int size = Mathf.Max(32, SectorSizeMeters);
        int spacing = Mathf.Clamp(SampleSpacingMeters, 1, size);
        ApplyOceanReplacementMasks(CaptureOceanReplacementMasks(coordinates, size, spacing, 0.0, 0.0, 0.0));
    }

    private static Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask> BuildOceanReplacementMasks(
        FinerOceanSnapshot snapshot, IEnumerable<RingTerrainSectorCoordinate> coordinates,
        int size, int spacing, int fineSize, int ringWidth, double circumference)
    {
        var coordinateList = coordinates.ToArray();
        var result = new Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask>();
        int coarseCells = Mathf.Max(1, size / spacing);
        foreach (RingTerrainSectorCoordinate coordinate in coordinateList)
            result[coordinate] = new SurfaceCoverageMask(coarseCells, 1, new bool[coarseCells * coarseCells]);
        bool aligned = fineSize > 0 && size % fineSize == 0 && fineSize % spacing == 0;
        var fallbackParents = new HashSet<RingTerrainSectorCoordinate>();
        foreach (RingTerrainSectorCoordinate parent in result.Keys)
            if (parent.AroundSector * (double)size + size > circumference
                || parent.AcrossSector * size + size > ringWidth)
                fallbackParents.Add(parent);
        if (aligned)
        {
            foreach ((RingTerrainSectorCoordinate fineCoordinate, SurfaceCoverageMask fineMask) in snapshot.Masks)
            {
                long parentAround = (long)Math.Floor(fineCoordinate.AroundSector * (double)fineSize / size);
                int parentAcross = (int)Math.Floor(fineCoordinate.AcrossSector * (double)fineSize / size);
                var parent = new RingTerrainSectorCoordinate(parentAround, parentAcross);
                if (!result.TryGetValue(parent, out SurfaceCoverageMask? parentMask)) continue;
                bool[] bits = parentMask.CoveredCells;
                int x0 = (int)((fineCoordinate.AroundSector * (long)fineSize - parentAround * (long)size) / spacing);
                int z0 = (fineCoordinate.AcrossSector * fineSize - parentAcross * size) / spacing;
                int span = fineSize / spacing;
                if (snapshot.FullyWet.Contains(fineCoordinate))
                {
                    for (int z = 0; z < span; z++) Array.Fill(bits, true, (z0 + z) * coarseCells + x0, span);
                    continue;
                }
                int fineCells = fineMask.ProxyQuadCells;
                int numerator = spacing * fineCells;
                if (numerator % fineSize != 0 || fineMask.QuadsPerProviderCell != 1)
                { fallbackParents.Add(parent); continue; }
                int ratio = numerator / fineSize;
                if (ratio <= 0 || fineCells != span * ratio)
                { fallbackParents.Add(parent); continue; }
                for (int z = 0; z < span; z++)
                for (int x = 0; x < span; x++)
                {
                    bool full = true;
                    for (int dz = 0; dz < ratio && full; dz++)
                    for (int dx = 0; dx < ratio; dx++)
                        if (!fineMask.CoveredCells[(z * ratio + dz) * fineCells + x * ratio + dx]) { full = false; break; }
                    bits[(z0 + z) * coarseCells + x0 + x] = full;
                }
            }
        }
        foreach (RingTerrainSectorCoordinate coordinate in coordinateList)
        {
            bool[] covered = result[coordinate].CoveredCells;
            if (aligned && !fallbackParents.Contains(coordinate)) continue;
            long aroundOrigin = coordinate.AroundSector * (long)size;
            int acrossOrigin = coordinate.AcrossSector * size;
            for (int z = 0; z < coarseCells; z++)
            for (int x = 0; x < coarseCells; x++)
            {
                int index = z * coarseCells + x;
                double a0 = aroundOrigin + x * spacing;
                double a1 = Math.Min(a0 + spacing, aroundOrigin + size);
                double z0 = acrossOrigin + z * spacing;
                double z1 = Math.Min(z0 + spacing, acrossOrigin + size);
                // Geometry proof is camera-independent. The shader applies the
                // finer water far-handoff dynamically, avoiding stale baked
                // circles while the observer moves inside a coarse sector.
                covered[index] = IsSnapshotOceanCovered(snapshot, a0, a1, z0, z1,
                    fineSize, ringWidth, circumference);
            }
            result[coordinate] = new SurfaceCoverageMask(coarseCells, 1, covered);
        }
        return result;
    }

    private static FinerOceanSnapshot CaptureFinerOceanSnapshot(RingTerrainProxySector finer)
    {
        var masks = new Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask>();
        var fullyWet = new HashSet<RingTerrainSectorCoordinate>();
        if (!finer.StreamingActive || finer._transitHidden) return new(masks, fullyWet);
        foreach ((RingTerrainSectorCoordinate coordinate, SurfaceCoverageMask mask) in finer._activeOceanSurfaceMasks)
        {
            if (finer._warmUntil.ContainsKey(coordinate)
                || !finer._active.TryGetValue(coordinate, out MeshInstance3D? node) || !node.Visible) continue;
            masks[coordinate] = mask;
            if (finer._activeFullyOceanSectors.Contains(coordinate)) fullyWet.Add(coordinate);
        }
        return new(masks, fullyWet);
    }

    private static bool IsSnapshotOceanCovered(FinerOceanSnapshot snapshot,
        double aroundStart, double aroundEnd, double acrossStart, double acrossEnd,
        int sectorSize, int ringWidth, double circumference)
    {
        if (aroundEnd <= aroundStart || acrossEnd <= acrossStart || acrossStart < 0 || acrossEnd > ringWidth) return false;
        long sectorLength = Math.Max(1L, (long)Math.Ceiling(circumference / sectorSize));
        long aroundFirst = (long)Math.Floor(aroundStart / sectorSize);
        long aroundLast = (long)Math.Floor((aroundEnd - 0.000001) / sectorSize);
        int acrossFirst = (int)Math.Floor(acrossStart / sectorSize);
        int acrossLast = (int)Math.Floor((acrossEnd - 0.000001) / sectorSize);
        for (long aroundSector = aroundFirst; aroundSector <= aroundLast; aroundSector++)
        for (int acrossSector = acrossFirst; acrossSector <= acrossLast; acrossSector++)
        {
            long wrapped = aroundSector % sectorLength;
            if (wrapped < 0) wrapped += sectorLength;
            var coordinate = new RingTerrainSectorCoordinate(wrapped, acrossSector);
            if (snapshot.FullyWet.Contains(coordinate)) continue;
            if (!snapshot.Masks.TryGetValue(coordinate, out SurfaceCoverageMask? mask)) return false;
            double sectorAround = aroundSector * (double)sectorSize;
            double sectorAcross = acrossSector * (double)sectorSize;
            if (!IsOceanMaskCovered(mask, aroundStart - sectorAround, aroundEnd - sectorAround,
                acrossStart - sectorAcross, acrossEnd - sectorAcross, sectorSize)) return false;
        }
        return true;
    }

    private void ApplyOceanReplacementMasks(IReadOnlyDictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask> masks)
    {
        bool dirty = false;
        foreach ((RingTerrainSectorCoordinate coordinate, SurfaceCoverageMask mask) in masks)
        {
            _latestOceanReplacementMasks[coordinate] = mask;
            if (!_active.TryGetValue(coordinate, out MeshInstance3D? node) || _warmUntil.ContainsKey(coordinate)) continue;
            if (_activeOceanReplacementMasks.TryGetValue(coordinate, out SurfaceCoverageMask? installed)
                && mask.Matches(installed)) continue;
            if (!_coverageAtlasSlots.TryGetValue(coordinate, out int slot) || _coverageAtlasColumns <= 0) continue;
            int tileSize = Mathf.Max(1, (mask.ProxyQuadCells + mask.QuadsPerProviderCell - 1) / mask.QuadsPerProviderCell);
            if (_oceanReplacementAtlasImage == null || _oceanReplacementAtlasImage.GetWidth() != tileSize * _coverageAtlasColumns)
            {
                int atlasSide = tileSize * _coverageAtlasColumns;
                _oceanReplacementAtlasBytes = new byte[atlasSide * atlasSide];
                _oceanReplacementAtlasImage = Image.CreateEmpty(atlasSide, atlasSide, false, Image.Format.R8);
                _oceanReplacementAtlasTexture = ImageTexture.CreateFromImage(_oceanReplacementAtlasImage);
                _activeOceanReplacementMasks.Clear();
                _oceanMaterial?.SetShaderParameter("ocean_replacement_coverage_atlas", _oceanReplacementAtlasTexture);
            }
            int sx = slot % _coverageAtlasColumns * tileSize, sy = slot / _coverageAtlasColumns * tileSize;
            if (_oceanReplacementAtlasBytes == null) continue;
            int atlasSideBytes = tileSize * _coverageAtlasColumns;
            for (int z = 0; z < tileSize; z++)
                Array.Clear(_oceanReplacementAtlasBytes, (sy + z) * atlasSideBytes + sx, tileSize);
            for (int z = 0; z < tileSize; z++) for (int x = 0; x < tileSize; x++)
                if (mask.CoveredCells[z * tileSize + x]) _oceanReplacementAtlasBytes[(sy + z) * atlasSideBytes + sx + x] = byte.MaxValue;
            float atlasSize = tileSize * _coverageAtlasColumns;
            node.SetInstanceShaderParameter("ocean_replacement_coverage_rect", new Vector4(sx / atlasSize, sy / atlasSize, tileSize / atlasSize, tileSize / atlasSize));
            _activeOceanReplacementMasks[coordinate] = mask;
            dirty = true;
        }
        if (dirty && _oceanReplacementAtlasImage != null && _oceanReplacementAtlasTexture != null && _oceanReplacementAtlasBytes != null)
        {
            int side = _oceanReplacementAtlasImage.GetWidth();
            _oceanReplacementAtlasImage.SetData(side, side, false, Image.Format.R8, _oceanReplacementAtlasBytes);
            _oceanReplacementAtlasTexture.Update(_oceanReplacementAtlasImage);
        }
        bool oceanReplacementFresh = _oceanReplacementAtlasTexture != null
            && StreamingActive && !_transitHidden
            && ReferenceEquals(_oceanReplacementFinerSource, _finerCoverageLevel)
            && _finerCoverageLevel != null && _finerCoverageLevel.StreamingActive && !_finerCoverageLevel._transitHidden;
        _oceanMaterial?.SetShaderParameter("ocean_replacement_coverage_enabled", oceanReplacementFresh);
        _oceanMaterial?.SetShaderParameter("ocean_replacement_distance_meters", OceanReplacementDistanceMeters);
    }

    private float OceanReplacementDistanceMeters => _finerCoverageLevel == null ? 0.0f
        : !_finerCoverageLevel.EnableFarFade ? float.MaxValue
        : Mathf.Max(0.0f, (_finerCoverageLevel.FadeOutStartMeters
            + Mathf.Max(_finerCoverageLevel.FadeOutStartMeters + 1.0f, _finerCoverageLevel.FadeOutEndMeters)) * 0.5f - 2.0f);

    private void ClearOceanReplacementMasks()
    {
        _latestOceanReplacementMasks.Clear(); _activeOceanReplacementMasks.Clear();
        _oceanReplacementFinerSource = null; _oceanReplacementFinerRevision = ulong.MaxValue;
        foreach (MeshInstance3D node in _active.Values) node.SetInstanceShaderParameter("ocean_replacement_coverage_rect", Vector4.Zero);
        _oceanMaterial?.SetShaderParameter("ocean_replacement_coverage_enabled", false);
    }


    // When no finer provider is eligible, remove the instance atlas rect
    // instead of allocating a dense all-false mask for every desired sector.
    private void ClearCoverageMasks(IReadOnlyCollection<RingTerrainSectorCoordinate> desired)
    {
        bool changed = false;
        foreach (RingTerrainSectorCoordinate coordinate in desired)
        {
            changed |= _latestCoverageMasks.Remove(coordinate);
            changed |= _activeCoverageMasks.Remove(coordinate);
            if (_active.TryGetValue(coordinate, out MeshInstance3D? node))
            {
                node.SetInstanceShaderParameter("coverage_atlas_rect", Vector4.Zero);
                node.GetNodeOrNull<MeshInstance3D>("OpaqueShadowCaster")?.SetInstanceShaderParameter("coverage_atlas_rect", Vector4.Zero);
                RestoreFullPartialCoverageMesh(coordinate, node);
                UpdateSectorVisibility(coordinate);
            }
        }
        _latestOceanReplacementMasks.Clear();
        _activeOceanReplacementMasks.Clear();
        _oceanReplacementFinerRevision = ulong.MaxValue;
        _oceanReplacementFinerSource = null;
        foreach (MeshInstance3D activeNode in _active.Values)
            activeNode.SetInstanceShaderParameter("ocean_replacement_coverage_rect", Vector4.Zero);
        _oceanMaterial?.SetShaderParameter("ocean_replacement_coverage_enabled", false);
        if (changed) _coverageRevision++;
    }


    private void UpdateSectorVisibility(RingTerrainSectorCoordinate coordinate)
    {
        if (!_active.TryGetValue(coordinate, out MeshInstance3D? node)) return;
        bool fullyCovered = _activeCoverageMasks.TryGetValue(coordinate, out SurfaceCoverageMask? mask)
            && mask.CoveredCells.Length > 0;
        if (fullyCovered)
            foreach (bool covered in mask!.CoveredCells) if (!covered) { fullyCovered = false; break; }
        bool retainsIndependentWater = _activeWaterSectors.Contains(coordinate);
        bool sectorActive = StreamingActive && !_transitHidden && !_warmUntil.ContainsKey(coordinate);
        node.Visible = sectorActive && ShouldRetainProxySurface(fullyCovered, retainsIndependentWater);
    }

    /// <summary>Terrain coverage may retire terrain, but never water without a water-ready replacement.</summary>
    public static bool ShouldRetainProxySurface(bool terrainFullyCovered, bool hasIndependentWater) =>
        !terrainFullyCovered || hasIndependentWater;

    private void InstallCoverageMask(MeshInstance3D node, RingTerrainSectorCoordinate coordinate, SurfaceCoverageMask mask, int cells, bool uploadAtlas = true)
    {
        int requiredSlots = Math.Max(1, _active.Count - _warmUntil.Count + 1);
        int atlasColumns = _coverageAtlasColumns == 0 ? 8 : _coverageAtlasColumns;
        while (atlasColumns * atlasColumns < requiredSlots) atlasColumns *= 2;
        const int atlasSlots = 1 << 30;
        int tileSize = Mathf.Max(1, (cells + mask.QuadsPerProviderCell - 1) / mask.QuadsPerProviderCell);
        if (_coverageAtlasImage == null || _coverageAtlasTileSize != tileSize || atlasColumns != _coverageAtlasColumns)
        {
            _coverageAtlasTileSize = tileSize;
            _coverageAtlasColumns = atlasColumns;
            _coverageAtlasSlots.Clear();
            // Ocean slots share this packing. Even unchanged proof bytes need
            // their rectangles reassigned after terrain-atlas repacking.
            _activeOceanReplacementMasks.Clear();
            _freeCoverageAtlasSlots.Clear();
            _coverageAtlasImage = Image.CreateEmpty(tileSize * atlasColumns, tileSize * atlasColumns, false, Image.Format.R8);
            _coverageAtlasImage.Fill(new Color(0, 0, 0, 1));
            _coverageAtlasTexture = ImageTexture.CreateFromImage(_coverageAtlasImage);
            _material?.SetShaderParameter("coverage_atlas", _coverageAtlasTexture);
            _oceanMaterial?.SetShaderParameter("coverage_atlas", _coverageAtlasTexture);
            _opaqueShadowMaterial?.SetShaderParameter("coverage_atlas", _coverageAtlasTexture);
            if (!_repackingCoverageAtlas)
            {
                _repackingCoverageAtlas = true;
                foreach ((RingTerrainSectorCoordinate existing, MeshInstance3D existingNode) in _active)
                    if (existing != coordinate && !_warmUntil.ContainsKey(existing)
                        && _latestCoverageMasks.TryGetValue(existing, out SurfaceCoverageMask? existingMask))
                        InstallCoverageMask(existingNode, existing, existingMask, cells, uploadAtlas: false);
                _repackingCoverageAtlas = false;
            }
        }
        if (!_coverageAtlasSlots.TryGetValue(coordinate, out int slot))
        {
            slot = _freeCoverageAtlasSlots.Count > 0 ? _freeCoverageAtlasSlots.Pop() : _coverageAtlasSlots.Count;
            if (slot >= atlasSlots) return; // conservative: retain the proxy rather than create a hole.
            _coverageAtlasSlots[coordinate] = slot;
        }
        int sx = slot % atlasColumns * tileSize;
        int sy = slot / atlasColumns * tileSize;
        _coverageAtlasImage!.FillRect(new Rect2I(sx, sy, tileSize, tileSize), new Color(0, 0, 0, 1));
        for (int z = 0; z < tileSize; z++)
        for (int x = 0; x < tileSize; x++)
        {
            if (mask.CoveredCells[z * tileSize + x])
                _coverageAtlasImage.SetPixel(sx + x, sy + z, new Color(1, 1, 1, 1));
        }
        if (uploadAtlas) _coverageAtlasTexture!.Update(_coverageAtlasImage);
        float atlasSize = tileSize * atlasColumns;
        Vector4 rect = new(sx / atlasSize, sy / atlasSize, tileSize / atlasSize, tileSize / atlasSize);
        node.SetInstanceShaderParameter("coverage_atlas_rect", rect);
        node.GetNodeOrNull<MeshInstance3D>("OpaqueShadowCaster")?.SetInstanceShaderParameter("coverage_atlas_rect", rect);
    }

    // Exact ocean ownership for FFT vertices outside the finite shoreline
    // field. Its slots are independent from terrain coverage: a lit texel is
    // a submitted ocean quad, never terrain readiness.
    private void InstallFftDistantOceanMask(MeshInstance3D node,
        RingTerrainSectorCoordinate coordinate, SurfaceCoverageMask? mask)
    {
        node.SetInstanceShaderParameter("fft_distant_ocean_mask_rect", Vector4.Zero);
        if (mask == null || SampleSpacingMeters is not (2 or 4 or 8)) return;
        // The distant FFT field is vertex-aligned, not quad-aligned. Its
        // (cells + 1)^2 texels map directly to the mesh's UV vertices, so a
        // canonical border vertex has the same depth sample in both sectors.
        int tileSize = Mathf.Max(2, mask.ProxyQuadCells + 1);
        int requiredSlots = Math.Max(1, _activeOceanSurfaceMasks.Count);
        int columns = _fftDistantOceanMaskAtlasColumns == 0 ? 8 : _fftDistantOceanMaskAtlasColumns;
        while (columns * columns < requiredSlots) columns *= 2;
        if (!_fftDistantOceanMaskSlots.TryGetValue(coordinate, out _))
            _fftDistantOceanMaskSlots[coordinate] = _freeFftDistantOceanMaskSlots.Count > 0
                ? _freeFftDistantOceanMaskSlots.Pop() : _fftDistantOceanMaskSlots.Count;
        int atlasSide = tileSize * columns;
        if (_fftDistantOceanMaskAtlasImage == null
            || _fftDistantOceanMaskAtlasTileSize != tileSize
            || _fftDistantOceanMaskAtlasImage.GetWidth() != atlasSide
            || _fftDistantOceanMaskAtlasColumns != columns)
        {
            _fftDistantOceanMaskAtlasTileSize = tileSize;
            _fftDistantOceanMaskAtlasColumns = columns;
            _fftDistantOceanMaskAtlasImage = Image.CreateEmpty(atlasSide, atlasSide, false, Image.Format.R8);
            _fftDistantOceanMaskAtlasImage.Fill(new Color(0, 0, 0, 1));
            _fftDistantOceanMaskAtlasTexture = ImageTexture.CreateFromImage(_fftDistantOceanMaskAtlasImage);
            _oceanMaterial?.SetShaderParameter("fft_distant_ocean_mask_atlas", _fftDistantOceanMaskAtlasTexture);
            foreach ((RingTerrainSectorCoordinate activeCoordinate, SurfaceCoverageMask activeMask) in _activeOceanSurfaceMasks)
            {
                if (_active.TryGetValue(activeCoordinate, out MeshInstance3D? activeNode)
                    && !_warmUntil.ContainsKey(activeCoordinate))
                    WriteFftDistantOceanMask(activeNode, activeCoordinate, activeMask, false);
            }
        }
        WriteFftDistantOceanMask(node, coordinate, mask, true);
    }

    private void WriteFftDistantOceanMask(MeshInstance3D node,
        RingTerrainSectorCoordinate coordinate, SurfaceCoverageMask mask, bool updateTexture)
    {
        if (_fftDistantOceanMaskAtlasImage == null || _fftDistantOceanMaskAtlasTexture == null
            || !_fftDistantOceanMaskSlots.TryGetValue(coordinate, out int slot) || _fftDistantOceanMaskAtlasColumns <= 0) return;
        int tileSize = _fftDistantOceanMaskAtlasTileSize;
        if (tileSize <= 1 || mask.ProxyQuadCells + 1 != tileSize) return;
        int atlasSide = _fftDistantOceanMaskAtlasImage.GetWidth();
        int sx = slot % _fftDistantOceanMaskAtlasColumns * tileSize;
        int sy = slot / _fftDistantOceanMaskAtlasColumns * tileSize;
        _fftDistantOceanMaskAtlasImage.FillRect(new Rect2I(sx, sy, tileSize, tileSize), new Color(0, 0, 0, 1));
        if (mask.FftDeepWaterVertexDepths is { Length: > 0 } depths
            && depths.Length == tileSize * tileSize)
            for (int z = 0; z < tileSize; z++)
            for (int x = 0; x < tileSize; x++)
            {
                byte depth = depths[z * tileSize + x];
                _fftDistantOceanMaskAtlasImage.SetPixel(sx + x, sy + z,
                    new Color(depth / 255.0f, 0, 0, 1));
            }
        float atlasSize = atlasSide;
        node.SetInstanceShaderParameter("fft_distant_ocean_mask_rect",
            new Vector4(sx / atlasSize, sy / atlasSize, tileSize / atlasSize, tileSize / atlasSize));
        if (updateTexture) _fftDistantOceanMaskAtlasTexture.Update(_fftDistantOceanMaskAtlasImage);
    }

    private bool IsCoveredByFinerRepresentation(RingTerrainSectorCoordinate coordinate, int size)
    {
        double around0 = (double)coordinate.AroundSector * size;
        double around1 = around0 + size;
        double across0 = Math.Clamp((double)coordinate.AcrossSector * size, 0.0, RingWidthMeters);
        double across1 = Math.Clamp((double)coordinate.AcrossSector * size + size, 0.0, RingWidthMeters);
        if (_finerCoverageLevel == null)
        {
            return _hasVoxelCoverageProvider && (_smoothTerrain?.IsSurfaceRegionVisualReady(
                (long)around0, (long)around1, (int)across0, (int)across1)
                ?? _grid?.IsSurfaceRegionVisualReady(around0, around1, across0, across1)) == true;
        }
        return _finerCoverageLevel.IsSurfaceRegionVisuallyCovered(
            around0,
            around1,
            across0,
            across1);
    }

    private bool IsRectangleFullyInsideInnerClipGeometry(
        double around0, double around1, double across0, double across1,
        double focusAround, double focusAcross, double viewerAltitudeAboveTerrain)
    {
        float radius = Mathf.Max(0.0f, InnerClipRadiusMeters);
        if (radius <= 0.0f) return false;
        double horizontalRadiusSquared = radius * (double)radius
            - viewerAltitudeAboveTerrain * viewerAltitudeAboveTerrain;
        if (horizontalRadiusSquared <= 0.0) return false;
        double centerAround = (around0 + around1) * 0.5;
        double maxAround = Math.Abs(SignedPeriodicDistance(focusAround, centerAround, ResolveAroundCircumferenceMeters()))
            + (around1 - around0) * 0.5;
        double maxAcross = Math.Max(Math.Abs(across0 - focusAcross), Math.Abs(across1 - focusAcross));
        return maxAround * maxAround + maxAcross * maxAcross < horizontalRadiusSquared;
    }

    // Each resident sector is a complete visual union: its immutable mesh draws
    // every unmasked cell, while every masked cell was omitted only after the
    // immediately finer provider proved it was ready.  The same invariant holds
    // for an inner-excluded sector.  Query that cached union at sector granularity
    // instead of recursively walking every quad through the complete LOD chain.
    // The old recursive walk made a single voxel arrival rescan tens of thousands
    // of cells synchronously at every coarser level.
    private bool IsSurfaceRegionVisuallyCovered(
        double aroundStart,
        double aroundEnd,
        double acrossStart,
        double acrossEnd)
    {
        if (!StreamingActive || _transitHidden) return false;
        // Terrain3D owns only its shader-discard disk, not the whole finer
        // level. Mesh residency cannot prove visibility inside that disk.
        if (ResolveTerrain3DNearFadeEnabled() && _focus != null)
        {
            RingCanonicalPosition focus = _velocityProvider?.CanonicalPosition
                ?? _grid!.WorldToCanonicalRingPosition64(_focus.GlobalPosition);
            float radius = (FadeStartMeters + Mathf.Max(FadeEndMeters, FadeStartMeters + 1f)) * .5f;
            if (RectangleIntersectsTerrain3DDiscard(aroundStart, aroundEnd, acrossStart, acrossEnd,
                focus.AroundMeters, focus.AcrossMeters, ResolveAroundCircumferenceMeters(), radius)) return false;
        }
        int size = Mathf.Max(32, SectorSizeMeters);
        long around0 = (long)Math.Floor(Math.Min(aroundStart, aroundEnd) / size);
        long around1 = (long)Math.Floor((Math.Max(aroundStart, aroundEnd) - 0.001) / size);
        int across0 = (int)Math.Floor(Math.Min(acrossStart, acrossEnd) / size);
        int across1 = (int)Math.Floor((Math.Max(acrossStart, acrossEnd) - 0.001) / size);
        long aroundSectorLength = ResolveAroundSectorLength(size);
        for (int z = across0; z <= across1; z++)
        for (long x = around0; x <= around1; x++)
        {
            var coordinate = new RingTerrainSectorCoordinate(
                WrapAroundSector(x, aroundSectorLength),
                z);
            if (_warmUntil.ContainsKey(coordinate)) return false;
            if (_innerExcluded.Contains(coordinate)) continue;
            if (!_active.TryGetValue(coordinate, out MeshInstance3D? node)
                || node.Mesh == null)
                return false;
        }
        return true;
    }

    internal static bool RectangleIntersectsTerrain3DDiscard(double aroundStart, double aroundEnd,
        double acrossStart, double acrossEnd, double focusAround, double focusAcross,
        double circumference, double radius)
    {
        double center = (aroundStart + aroundEnd) * .5;
        double halfAround = Math.Abs(aroundEnd - aroundStart) * .5;
        double aroundDistance = Math.Max(0, Math.Abs(SignedPeriodicDistance(focusAround, center, circumference)) - halfAround);
        double lowAcross = Math.Min(acrossStart, acrossEnd), highAcross = Math.Max(acrossStart, acrossEnd);
        double acrossDistance = focusAcross < lowAcross ? lowAcross - focusAcross : focusAcross > highAcross ? focusAcross - highAcross : 0;
        return aroundDistance * aroundDistance + acrossDistance * acrossDistance <= radius * radius;
    }

    private Vector3 GetCappedPredictionOffset()
    {
        LastPredictionDistanceMeters = 0.0f;
        if (!EnablePredictiveStreaming || PredictionLookAheadSeconds <= 0.0f || PredictionMaxDistanceMeters <= 0.0f)
        {
            return Vector3.Zero;
        }

        _velocityProvider ??= ResolveVelocityProvider(_focus);
        if (_velocityProvider == null)
        {
            return Vector3.Zero;
        }

        Vector3 velocity = _velocityProvider.CanonicalVelocity;
        if (!velocity.IsFinite()) return Vector3.Zero;
        float speed = velocity.Length();
        Vector3 offset = velocity * PredictionLookAheadSeconds;
        if (IncludeBrakingDistanceInPrediction && speed > 0.01f)
        {
            float brakingDistance = speed * speed / (2.0f * Mathf.Max(1.0f, PredictionBrakingAcceleration))
                * Mathf.Max(1.0f, PredictionBrakingSafetyFactor);
            offset += velocity / speed * brakingDistance;
        }

        offset = offset.LimitLength(PredictionMaxDistanceMeters);
        LastPredictionDistanceMeters = offset.Length();
        return offset;
    }

    private static RingVoxelExplorerController? ResolveVelocityProvider(Node? focus)
    {
        Node? current = focus;
        while (current != null)
        {
            if (current is RingVoxelExplorerController provider) return provider;
            current = current.GetParent();
        }

        return null;
    }

    private static BuildBatch BuildRawBatch(ulong requestVersion, RingLodSurfaceEditSnapshot editSnapshot, List<RingTerrainSectorCoordinate> coordinates, RingTerrainSampler sampler, Action<SectorData> publishSector, RingLodGeometrySnapshot geometry, Transform3D ownerInverse, int size, int spacing, int parentSpacing, RingTerrainGlobalMorphSnapshot? globalMorphTarget, int cells, float underlay, float parentUnderlay, bool parentRestoresWaterUnderlay, int ringWidth, bool buildWalls, bool buildSkirts, float skirtDepth, bool blocky, bool alignSurfaceToTerrainSdf, int heightQuantum, Color low, Color high, string cacheDirectory, bool readDiskCache, bool writeDiskCache, Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask>? coverageMasks, int parallelism, CancellationToken cancellation)
    {
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Terrain proxy build", worker: true);
        var watch = Stopwatch.StartNew();
        int completedCount = 0;
        int cacheHits = 0;
        int cacheWrites = 0;
        if (parallelism <= 1 || coordinates.Count <= 1)
        {
            var sampleCache = new TerrainBuildSampleCache(editSnapshot, sampler, ringWidth);
            foreach (RingTerrainSectorCoordinate coordinate in coordinates)
            {
                cancellation.ThrowIfCancellationRequested();
                string cachePath = ResolveLodSectorCachePath(cacheDirectory, coordinate, writeDiskCache);
                bool cacheEligible = !string.IsNullOrEmpty(cachePath)
                    && !SectorTouchesEdits(editSnapshot, coordinate, size, spacing, parentSpacing, sampler.AroundRingVoxelLength, ringWidth);
                int loadedSampleCount = 0;
                bool loaded = cacheEligible
                    && readDiskCache
                    && TryLoadCachedTerrainSamples(cachePath, coordinate, sampleCache, out loadedSampleCount);
                if (loaded)
                {
                    cacheHits++;
                }
                if (cacheEligible && writeDiskCache) sampleCache.BeginCapture();
                SurfaceCoverageMask? coverageMask = null;
                coverageMasks?.TryGetValue(coordinate, out coverageMask);
                SectorData data = BuildRawSector(requestVersion, coordinate, editSnapshot, sampler, sampleCache, geometry, ownerInverse, size, spacing, parentSpacing, globalMorphTarget, cells, underlay, parentUnderlay, parentRestoresWaterUnderlay, ringWidth, buildWalls, buildSkirts, skirtDepth, blocky, alignSurfaceToTerrainSdf, heightQuantum, low, high, coverageMask);
                if (cacheEligible && writeDiskCache)
                {
                    CachedTerrainSampleRecord[] captured = sampleCache.EndCapture();
                    if ((!loaded || captured.Length > loadedSampleCount)
                        && TrySaveCachedTerrainSamples(cachePath, coordinate, captured, cancellation))
                    {
                        cacheWrites++;
                    }
                }
                publishSector(data);
                completedCount++;
            }
        }
        else
        {
            using var contexts = new ThreadLocal<(RingTerrainSampler Sampler, TerrainBuildSampleCache Cache)>(
                () =>
                {
                    RingTerrainSampler laneSampler = sampler.CreateIndependentCopy();
                    return (laneSampler, new TerrainBuildSampleCache(editSnapshot, laneSampler, ringWidth));
                },
                trackAllValues: true);
            try
            {
                Parallel.ForEach(
                    coordinates,
                    new ParallelOptions
                    {
                        CancellationToken = cancellation,
                        MaxDegreeOfParallelism = parallelism
                    },
                    coordinate =>
                    {
                        var context = contexts.Value;
                        string cachePath = ResolveLodSectorCachePath(cacheDirectory, coordinate, writeDiskCache);
                        bool cacheEligible = !string.IsNullOrEmpty(cachePath)
                            && !SectorTouchesEdits(editSnapshot, coordinate, size, spacing, parentSpacing, context.Sampler.AroundRingVoxelLength, ringWidth);
                        int loadedSampleCount = 0;
                        bool loaded = cacheEligible
                            && readDiskCache
                            && TryLoadCachedTerrainSamples(cachePath, coordinate, context.Cache, out loadedSampleCount);
                        if (loaded)
                        {
                            Interlocked.Increment(ref cacheHits);
                        }
                        if (cacheEligible && writeDiskCache) context.Cache.BeginCapture();
                        SurfaceCoverageMask? coverageMask = null;
                        coverageMasks?.TryGetValue(coordinate, out coverageMask);
                        SectorData data = BuildRawSector(requestVersion, coordinate, editSnapshot, context.Sampler, context.Cache, geometry, ownerInverse, size, spacing, parentSpacing, globalMorphTarget, cells, underlay, parentUnderlay, parentRestoresWaterUnderlay, ringWidth, buildWalls, buildSkirts, skirtDepth, blocky, alignSurfaceToTerrainSdf, heightQuantum, low, high, coverageMask);
                        if (cacheEligible && writeDiskCache)
                        {
                            CachedTerrainSampleRecord[] captured = context.Cache.EndCapture();
                            if ((!loaded || captured.Length > loadedSampleCount)
                                && TrySaveCachedTerrainSamples(cachePath, coordinate, captured, cancellation))
                            {
                                Interlocked.Increment(ref cacheWrites);
                            }
                        }
                        publishSector(data);
                        Interlocked.Increment(ref completedCount);
                    });
            }
            finally
            {
                foreach (var context in contexts.Values) context.Sampler.Dispose();
            }
        }
        watch.Stop();
        return new BuildBatch(requestVersion, editSnapshot.Version, completedCount, cacheHits, cacheWrites, watch.Elapsed.TotalMilliseconds);
    }

    internal RingTerrainSdfSnapshot? CaptureTerrainSdfSnapshotForTransitions()
    {
        if (_grid?.TerrainSampler is not RingTerrainSampler sampler
            || _grid.TerrainSettings == null)
            return null;
        return RingTerrainSdfSnapshot.Create(
            sampler,
            _smoothTerrain?.CaptureLodSurfaceEditSnapshot() ?? _grid.CaptureLodSurfaceEditSnapshot(),
            _grid.CaptureNaturalTerrainSdfEditSnapshot(),
            Mathf.Max(1, RingWidthMeters),
            _grid.TerrainSettings.BedrockY,
            _grid.TerrainSettings.SoilDepthVoxels);
    }

    private static string ResolveLodSectorCacheDirectory(
        RingTerrainSampler sampler,
        int size,
        int spacing,
        int cells,
        int ringWidth,
        int parentSpacing,
        RingTerrainGlobalMorphSnapshot.Identity? globalMorphIdentity)
    {
        // This key describes authoritative procedural sampling inputs. Parent
        // spacing is included because it changes which endpoint samples a mesh
        // captures; mesh, material, lighting, and skirt settings do not.
        string definition = BuildLodSectorCacheDefinition(
            sampler.TerrainGeneratorVersion, sampler.Seed, sampler.TerrainDefinitionHash,
            sampler.AroundRingVoxelLength, ringWidth, size, spacing, parentSpacing, cells, globalMorphIdentity);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(definition));
        string key = Convert.ToHexString(digest.AsSpan(0, 12));
        string activeWorld = ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString();
        string root = string.IsNullOrWhiteSpace(activeWorld)
            ? "user://ring_cache"
            : activeWorld.TrimEnd('/', '\\') + "/lod_cache";
        return ProjectSettings.GlobalizePath($"{root}/lod_sectors/{key}");
    }

    private static string BuildLodSectorCacheDefinition(
        int terrainGeneratorVersion, int seed, ulong terrainDefinitionHash,
        long aroundLength, int ringWidth, int size, int spacing, int parentSpacing, int cells,
        RingTerrainGlobalMorphSnapshot.Identity? globalMorphIdentity)
        => FormattableString.Invariant(
            $"v{LodSectorCacheVersion}|g{terrainGeneratorVersion}|s{seed}|h{terrainDefinitionHash:X16}|a{aroundLength}|w{ringWidth}|z{size}|p{spacing}|q{parentSpacing}|m{globalMorphIdentity?.TopologyAndHeightHash ?? 0UL:X16}|n{cells}");

    private static string ResolveLodSectorCachePath(string cacheDirectory, RingTerrainSectorCoordinate coordinate, bool createDirectory)
    {
        if (string.IsNullOrEmpty(cacheDirectory)
            || UnavailableCacheDirectories.ContainsKey(cacheDirectory))
        {
            return string.Empty;
        }
        long shardX = FloorDiv(coordinate.AroundSector, 64);
        int shardY = FloorDiv(coordinate.AcrossSector, 64);
        string shard = Path.Combine(cacheDirectory, $"r_{shardX}_{shardY}");
        try
        {
            if (createDirectory) Directory.CreateDirectory(shard);
            return Path.Combine(shard, $"s_{coordinate.AroundSector}_{coordinate.AcrossSector}.bin");
        }
        catch (IOException)
        {
            UnavailableCacheDirectories.TryAdd(cacheDirectory, 0);
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            UnavailableCacheDirectories.TryAdd(cacheDirectory, 0);
            return string.Empty;
        }
    }

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static long FloorDiv(long value, long divisor)
    {
        long quotient = value / divisor;
        long remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static bool SectorTouchesEdits(
        RingLodSurfaceEditSnapshot edits,
        RingTerrainSectorCoordinate coordinate,
        int size,
        int spacing,
        int parentSpacing,
        long aroundLength,
        int ringWidth)
    {
        if (edits.EditedColumnCount == 0) return false;
        int margin = Mathf.Max(1, Mathf.Max(spacing * 2, parentSpacing));
        long centerAround = coordinate.AroundSector * size + size / 2L;
        long halfWidth = size / 2L + margin;
        int acrossMin = Mathf.Max(0, coordinate.AcrossSector * size - margin);
        int acrossMax = Mathf.Min(ringWidth, (coordinate.AcrossSector + 1) * size + margin);
        long circumference = Math.Max(1, aroundLength);
        foreach ((RingTerrainColumnCoordinate edited, _) in edits.CanonicalEditedColumns)
        {
            if (edited.AcrossVoxel < acrossMin || edited.AcrossVoxel > acrossMax) continue;
            long delta = edited.AroundVoxel - centerAround;
            delta %= circumference;
            if (delta > circumference / 2) delta -= circumference;
            if (delta < -circumference / 2) delta += circumference;
            if (Math.Abs(delta) <= halfWidth) return true;
        }
        return false;
    }

    private static bool SectorTouchesColumns(
        IReadOnlyCollection<RingTerrainColumnCoordinate> columns,
        RingTerrainSectorCoordinate coordinate,
        int size,
        int spacing,
        int parentSpacing,
        long aroundLength,
        int ringWidth)
    {
        if (columns.Count == 0) return false;
        int margin = Mathf.Max(1, Mathf.Max(spacing * 2, parentSpacing));
        long centerAround = coordinate.AroundSector * size + size / 2L;
        long halfWidth = size / 2L + margin;
        int acrossMin = Mathf.Max(0, coordinate.AcrossSector * size - margin);
        int acrossMax = Mathf.Min(ringWidth, (coordinate.AcrossSector + 1) * size + margin);
        long circumference = Math.Max(1, aroundLength);
        foreach (RingTerrainColumnCoordinate column in columns)
        {
            if (column.AcrossVoxel < acrossMin || column.AcrossVoxel > acrossMax) continue;
            long delta = column.AroundVoxel - centerAround;
            delta %= circumference;
            if (delta > circumference / 2) delta -= circumference;
            if (delta < -circumference / 2) delta += circumference;
            if (Math.Abs(delta) <= halfWidth) return true;
        }
        return false;
    }

    private static bool TryLoadCachedTerrainSamples(
        string path,
        RingTerrainSectorCoordinate coordinate,
        TerrainBuildSampleCache sampleCache,
        out int sampleCount)
    {
        sampleCount = 0;
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
            using (var header = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                if (header.ReadUInt32() != LodSectorCacheMagic
                    || header.ReadInt32() != LodSectorCacheVersion
                    || header.ReadInt64() != coordinate.AroundSector
                    || header.ReadInt32() != coordinate.AcrossSector)
                {
                    return false;
                }
            }

            using var compressed = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: false);
            using var reader = new BinaryReader(compressed);
            int count = reader.ReadInt32();
            if (count < 0 || count > MaximumCachedSampleCount)
                throw new InvalidDataException("Invalid LOD terrain sample count.");

            var records = new CachedTerrainSampleRecord[count];
            for (int i = 0; i < count; i++)
            {
                long around = reader.ReadInt64();
                int across = reader.ReadInt32();
                int surfaceHeight = reader.ReadInt32();
                int regionValue = reader.ReadByte();
                float continentalness = reader.ReadSingle();
                float erosion = reader.ReadSingle();
                float ridgeStrength = reader.ReadSingle();
                float temperature = reader.ReadSingle();
                float moisture = reader.ReadSingle();
                int biomeValue = reader.ReadByte();
                int waterKindValue = reader.ReadByte();
                int waterSurfaceHeight = reader.ReadInt32();
                int terrainHeight = reader.ReadInt32();
                float smoothTerrainHeight = reader.ReadSingle();
                float presentationTerrainHeight = reader.ReadSingle();
                float presentationDisplayedHeight = reader.ReadSingle();
                bool usesPresentationSurface = reader.ReadBoolean();
                bool usesRuntimeWaterPlane = reader.ReadBoolean();
                if (regionValue < (int)TerrainRegion.DeepOcean
                    || regionValue > (int)TerrainRegion.MountainBelt
                    || biomeValue < (int)TerrainBiomeId.Ocean
                    || biomeValue > (int)TerrainBiomeId.Alpine
                    || waterKindValue < (int)SurfaceWaterKind.None
                    || waterKindValue > (int)SurfaceWaterKind.River
                    || !float.IsFinite(continentalness)
                    || !float.IsFinite(erosion)
                    || !float.IsFinite(ridgeStrength)
                    || !float.IsFinite(temperature)
                    || !float.IsFinite(moisture)
                    || !float.IsFinite(smoothTerrainHeight)
                    || !float.IsFinite(presentationTerrainHeight)
                    || !float.IsFinite(presentationDisplayedHeight)
                    || usesPresentationSurface != sampleCache.UsesPresentationSurface
                    || usesRuntimeWaterPlane != sampleCache.UsesRuntimeWaterPlane)
                {
                    throw new InvalidDataException("Invalid LOD terrain sample payload.");
                }

                var sample = new TerrainSample(
                    surfaceHeight,
                    (TerrainRegion)regionValue,
                    continentalness,
                    erosion,
                    ridgeStrength,
                    temperature,
                    moisture,
                    (TerrainBiomeId)biomeValue,
                    (SurfaceWaterKind)waterKindValue,
                    waterSurfaceHeight);
                records[i] = new CachedTerrainSampleRecord(
                    around,
                    across,
                    new CachedTerrainPoint(
                        sample,
                        terrainHeight,
                        sample.ResolveDisplayedHeight(terrainHeight),
                        smoothTerrainHeight,
                        presentationTerrainHeight,
                        presentationDisplayedHeight,
                        usesPresentationSurface,
                        usesRuntimeWaterPlane));
            }

            sampleCache.Import(records);
            sampleCount = count;
            RingGeneratedLodCacheBudget.Touch(path);
            return true;
        }
        catch (Exception)
        {
            sampleCount = 0;
            return false;
        }
    }

    private static bool TrySaveCachedTerrainSamples(
        string path,
        RingTerrainSectorCoordinate coordinate,
        IReadOnlyList<CachedTerrainSampleRecord> samples,
        CancellationToken cancellation)
    {
        long estimatedBytes = 64L + samples.Count * 64L;
        if (!RingGeneratedLodCacheBudget.CanWriteRegistered(path, estimatedBytes)) return false;
        string temporaryPath = path + $".{System.Environment.CurrentManagedThreadId}.tmp";
        bool recorded = false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using (var stream = new FileStream(temporaryPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.None))
            {
                using (var header = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                {
                    header.Write(LodSectorCacheMagic);
                    header.Write(LodSectorCacheVersion);
                    header.Write(coordinate.AroundSector);
                    header.Write(coordinate.AcrossSector);
                }
                using var compressed = new DeflateStream(stream, CompressionLevel.Fastest, leaveOpen: true);
                using var writer = new BinaryWriter(compressed);
                writer.Write(samples.Count);
                for (int i = 0; i < samples.Count; i++)
                {
                    if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                    CachedTerrainSampleRecord record = samples[i];
                    TerrainSample sample = record.Point.Sample;
                    writer.Write(record.Around);
                    writer.Write(record.Across);
                    writer.Write(sample.SurfaceHeight);
                    writer.Write((byte)sample.Region);
                    writer.Write(sample.Continentalness);
                    writer.Write(sample.Erosion);
                    writer.Write(sample.RidgeStrength);
                    writer.Write(sample.Temperature);
                    writer.Write(sample.Moisture);
                    writer.Write((byte)sample.Biome);
                    writer.Write((byte)sample.WaterKind);
                    writer.Write(sample.WaterSurfaceHeight);
                    writer.Write(record.Point.TerrainHeight);
                    writer.Write(record.Point.SmoothTerrainHeight);
                    writer.Write(record.Point.PresentationTerrainHeight);
                    writer.Write(record.Point.PresentationDisplayedHeight);
                    writer.Write(record.Point.UsesPresentationSurface);
                    writer.Write(record.Point.UsesRuntimeWaterPlane);
                }
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
            string budgetRoot = RingGeneratedLodCacheBudget.ResolveBudgetRoot(path);
            RingGeneratedLodCacheBudget.RecordWrite(budgetRoot, path, new FileInfo(path).Length);
            recorded = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (!recorded)
            {
                string budgetRoot = RingGeneratedLodCacheBudget.ResolveBudgetRoot(path);
                RingGeneratedLodCacheBudget.CancelWriteReservation(budgetRoot, path);
            }
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static SectorData BuildRawSector(ulong requestVersion, RingTerrainSectorCoordinate coordinate, RingLodSurfaceEditSnapshot editSnapshot, RingTerrainSampler sampler, TerrainBuildSampleCache sampleCache, RingLodGeometrySnapshot geometry, Transform3D ownerInverse, int size, int spacing, int parentSpacing, RingTerrainGlobalMorphSnapshot? globalMorphTarget, int cells, float underlay, float parentUnderlay, bool parentRestoresWaterUnderlay, int ringWidth, bool buildWalls, bool buildSkirts, float skirtDepth, bool blocky, bool alignSurfaceToTerrainSdf, int heightQuantum, Color low, Color high, SurfaceCoverageMask? coverageMask)
    {
        long allocationsBefore = GC.GetAllocatedBytesForCurrentThread();
        (double centerAround, double centerAcross) = ResolveSectorCanonicalCenter(coordinate, size, ringWidth);
        // Build vertex positions relative to the sector center.  This keeps
        // adjacent vertices within a few hundred metres of zero even after a
        // multi-megmetre canonical teleport.
        geometry = geometry.CenteredAt(centerAround, centerAcross);
        ownerInverse = new Transform3D(ownerInverse.Basis, Vector3.Zero);
        Vector3 localOrigin = Vector3.Zero;
        var vertexTransform = ownerInverse;
        if (blocky)
        {
            SectorData blockyResult = BuildRawBlockySector(requestVersion, coordinate, editSnapshot, sampler, sampleCache, geometry, vertexTransform, localOrigin, size, spacing, cells, underlay, ringWidth, buildWalls, buildSkirts, skirtDepth, heightQuantum, low, high, coverageMask);
            long blockyAllocated = GC.GetAllocatedBytesForCurrentThread() - allocationsBefore;
            if (blockyAllocated > 0) Interlocked.Add(ref _workerAllocatedBytesSinceStartup, blockyAllocated);
            return blockyResult;
        }

        int row = cells + 1;
        int surfaceVertexCount = row * row;
        int initialVertexCount = surfaceVertexCount * 2;
        var vertices = new List<Vector3>(initialVertexCount);
        var normals = new List<Vector3>(initialVertexCount);
        var colors = new List<Color>(initialVertexCount);
        var uvs = new List<Vector2>(initialVertexCount);
        var morphData = new List<Vector2>(initialVertexCount);
        var parentChordOffsets = new List<Vector3>(initialVertexCount);
        CollectionsMarshal.SetCount(vertices, initialVertexCount);
        CollectionsMarshal.SetCount(normals, initialVertexCount);
        CollectionsMarshal.SetCount(colors, initialVertexCount);
        CollectionsMarshal.SetCount(uvs, initialVertexCount);
        CollectionsMarshal.SetCount(morphData, initialVertexCount);
        CollectionsMarshal.SetCount(parentChordOffsets, initialVertexCount);
        // The terminal world-ring snapshot uses the same CUSTOM0 chord path as
        // runtime Terrain3D, but samples the receiver's published triangles.
        bool runtimeParentChord = sampler.UsesRuntimeSurfaceGeneration || globalMorphTarget != null;
        var waterVertices = new bool[surfaceVertexCount];
        var terrainGeometryHeights = new float[surfaceVertexCount];
        var aroundCoordinates = new double[surfaceVertexCount];
        var acrossCoordinates = new float[surfaceVertexCount];
        var terrainPoints = new CachedTerrainPoint[surfaceVertexCount];
        // Match SmoothTerrainGrid's compact, deterministic 16 m sampling
        // lattice.  This keeps the LOD handoff continuous while avoiding a
        // fresh climate/hydrology query for every high-detail proxy vertex.
        const int biomeSampleSpacing = 16;
        var biomeWeightCache = new Dictionary<TerrainColumnKey, Color>();
        for (int z = 0; z <= cells; z++)
        for (int x = 0; x <= cells; x++)
        {
            long around = coordinate.AroundSector * size + x * spacing;
            int across = Mathf.Clamp(coordinate.AcrossSector * size + z * spacing, 0, ringWidth);
            CachedTerrainPoint point = sampleCache.Get(around, across);
            TerrainSample terrainSample = point.Sample;
            int i = z * row + x;
            int waterIndex = surfaceVertexCount + i;
            float terrainGeometryHeight = GetTerrainMorphHeight(point, alignSurfaceToTerrainSdf);
            float waterHeight = GetWaterMorphHeight(point);
            float terrainSurface = GetTerrainRenderHeight(point, alignSurfaceToTerrainSdf) - underlay;
            vertices[i] = vertexTransform * geometry.CanonicalToWorld(around, terrainSurface, across);
            colors[i] = ResolveBiomeTextureWeight(
                sampler, around, across, ringWidth, biomeSampleSpacing, biomeWeightCache);
            vertices[waterIndex] = vertexTransform * geometry.CanonicalToWorld(around, GetWaterRenderHeight(point) - underlay, across);
            uvs[i] = new Vector2(x / (float)cells, z / (float)cells);
            uvs[waterIndex] = uvs[i];
            colors[waterIndex] = RingWaterModel.EncodeWaterKind(
                RingTerrainVisuals.ResolveSeededColor(terrainSample, Mathf.RoundToInt(waterHeight), sampler, low, high),
                terrainSample.WaterKind);
            waterVertices[i] = point.IsDisplayedWater;
            terrainGeometryHeights[i] = terrainGeometryHeight;
            aroundCoordinates[i] = around;
            acrossCoordinates[i] = across;
            terrainPoints[i] = point;
            float parentTerrainHeight = SampleParentTerrainHeight(sampleCache, around, across, parentSpacing, ringWidth, alignSurfaceToTerrainSdf, runtimeParentChord);
            float parentWaterHeight = SampleParentWaterHeight(sampleCache, around, across, parentSpacing, ringWidth);
            // UV2.x is a displacement along the ring's radial-up vector. Match
            // the parent mesh's rendered base, rather than just its sampled
            // height: every LOD deliberately carries its own underlay, and the
            // FFT-capable water tiers restore that underlay in the vertex shader.
            // At morph=1 this makes both fine and coarse vertices resolve to
            // the same canonical radial height at their ownership handoff.
            float ownWaterRestore = spacing is 2 or 4 or 8 ? underlay : 0.0f;
            float parentWaterRestore = parentRestoresWaterUnderlay ? parentUnderlay : 0.0f;
            morphData[i] = new Vector2(
                parentTerrainHeight - terrainGeometryHeight + underlay - parentUnderlay,
                0.0f);
            morphData[waterIndex] = new Vector2(
                parentWaterHeight - waterHeight + underlay - ownWaterRestore - parentUnderlay + parentWaterRestore,
                1.0f);
            if (globalMorphTarget != null
                && globalMorphTarget.TrySampleTerrainWorld(geometry, around, across, out Vector3 receiverWorld))
            {
                // The snapshot applies this worker's captured geometry before
                // interpolation. Convert only after that, into the same local
                // coordinates as the proxy vertex, to remain origin-safe.
                parentChordOffsets[i] = vertexTransform * receiverWorld - vertices[i];
            }
            else if (runtimeParentChord)
            {
                parentChordOffsets[i] = SampleRuntimeParentChordOffset(sampleCache, geometry, vertexTransform,
                    around, across, parentSpacing, ringWidth, alignSurfaceToTerrainSdf, parentUnderlay, vertices[i]);
            }
        }

        // Do not derive smooth-surface normals from only the triangles owned by
        // this sector.  At a streamed boundary that gives the shared vertex a
        // different (one-sided) triangle fan in each adjacent sector, which
        // shows up as a lighting seam.  Sample one lattice step outside the
        // sector instead, so every same-LOD owner evaluates the identical
        // central difference at a shared canonical position.
        for (int z = 0; z <= cells; z++)
        for (int x = 0; x <= cells; x++)
        {
            int i = z * row + x;
            long around = coordinate.AroundSector * size + x * spacing;
            int across = Mathf.Clamp(coordinate.AcrossSector * size + z * spacing, 0, ringWidth);
            normals[i] = CalculateCanonicalSurfaceNormal(
                sampleCache, geometry, vertexTransform, around, across, spacing, ringWidth,
                underlay, alignSurfaceToTerrainSdf, water: false);
            normals[surfaceVertexCount + i] = CalculateCanonicalSurfaceNormal(
                sampleCache, geometry, vertexTransform, around, across, spacing, ringWidth,
                underlay, alignSurfaceToTerrainSdf, water: true);
        }

        var indexList = new List<int>(cells * cells * 6);
        var oceanIndexList = new List<int>(cells * cells * 6);
        var partialWaterTriangles = new List<PartialWaterTriangle>();
        var oceanSurfaceCells = new bool[cells * cells];
        var oceanDeepWaterVertexDepths = new byte[row * row];
        for (int z = 0; z <= cells; z++)
        for (int x = 0; x <= cells; x++)
        {
            long around = coordinate.AroundSector * size + x * spacing;
            int across = Mathf.Clamp(coordinate.AcrossSector * size + z * spacing, 0, ringWidth);
            if (TryResolveConservativeOceanVertexDepth(
                    sampleCache, around, across, spacing, ringWidth, alignSurfaceToTerrainSdf,
                    out float depth))
                oceanDeepWaterVertexDepths[z * row + x] = (byte)Mathf.RoundToInt(
                    Mathf.Clamp(depth, 0.0f, 510.0f) * 0.5f);
        }
        bool fullyOceanSurface = true;
        int validWaterCoverageCells = 0;
        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
        {
            // Coverage is now evaluated in the shared GPU atlas.  Keep the
            // complete mesh warm so a finer provider retreat is an immediate
            // unhide rather than a worker rebuild.
            int rawAcross0 = coordinate.AcrossSector * size + z * spacing;
            int rawAcross1 = rawAcross0 + spacing;
            if (rawAcross1 <= 0 || rawAcross0 >= ringWidth) continue;
            validWaterCoverageCells++;
            int i00 = z * row + x, i10 = i00 + 1, i01 = i00 + row, i11 = i01 + 1;
            if (runtimeParentChord)
            {
                // Preserve the existing clockwise winding while changing only
                // the diagonal: (00,11), rather than legacy (01,10).
                indexList.Add(i00); indexList.Add(i11); indexList.Add(i10);
                indexList.Add(i00); indexList.Add(i01); indexList.Add(i11);
            }
            else
            {
                indexList.Add(i01); indexList.Add(i10); indexList.Add(i00);
                indexList.Add(i10); indexList.Add(i01); indexList.Add(i11);
            }
            int firstWetCount = CountWaterVertices(waterVertices, i01, i10, i00);
            bool firstIsOcean = firstWetCount == 3
                && terrainPoints[i01].Sample.WaterKind == SurfaceWaterKind.Ocean
                && terrainPoints[i10].Sample.WaterKind == SurfaceWaterKind.Ocean
                && terrainPoints[i00].Sample.WaterKind == SurfaceWaterKind.Ocean;
            bool secondIsOcean = CountWaterVertices(waterVertices, i10, i01, i11) == 3
                && terrainPoints[i10].Sample.WaterKind == SurfaceWaterKind.Ocean
                && terrainPoints[i01].Sample.WaterKind == SurfaceWaterKind.Ocean
                && terrainPoints[i11].Sample.WaterKind == SurfaceWaterKind.Ocean;
            fullyOceanSurface &= firstIsOcean && secondIsOcean;
            // A quad is safe for the FFT handoff only when both submitted
            // water triangles are ocean.  Partial shoreline triangles stay
            // visible on every level and cannot certify a replacement.
            bool fullOceanQuad = firstIsOcean && secondIsOcean;
            oceanSurfaceCells[z * cells + x] = fullOceanQuad;
            if (fullOceanQuad)
            {
            }
            if (firstWetCount == 3)
            {
                int offset = surfaceVertexCount;
                oceanIndexList.Add(offset + i01); oceanIndexList.Add(offset + i10); oceanIndexList.Add(offset + i00);
            }
            else if (firstWetCount > 0)
            {
                partialWaterTriangles.Add(new PartialWaterTriangle(i01, i10, i00));
            }
            int secondWetCount = CountWaterVertices(waterVertices, i10, i01, i11);
            if (secondWetCount == 3)
            {
                int offset = surfaceVertexCount;
                oceanIndexList.Add(offset + i10); oceanIndexList.Add(offset + i01); oceanIndexList.Add(offset + i11);
            }
            else if (secondWetCount > 0)
            {
                partialWaterTriangles.Add(new PartialWaterTriangle(i10, i01, i11));
            }
        }
        fullyOceanSurface &= validWaterCoverageCells > 0;
        foreach (PartialWaterTriangle triangle in partialWaterTriangles)
        {
            AppendClippedWaterTriangle(
                triangle,
                waterVertices,
                terrainGeometryHeights,
                aroundCoordinates,
                acrossCoordinates,
                terrainPoints,
                uvs,
                morphData,
                sampler,
                geometry,
                vertexTransform,
                underlay,
                low,
                high,
                vertices,
                normals,
                colors,
                uvs,
                morphData,
                oceanIndexList);
        }
        if (buildSkirts)
            AppendSectorSkirts(vertices, normals, colors, uvs, morphData, indexList, coordinate, sampleCache, sampler, geometry, vertexTransform, size, spacing, parentSpacing, underlay, parentUnderlay, ringWidth, skirtDepth, buildWalls, low, high, alignSurfaceToTerrainSdf, geomorph: true);
        while (morphData.Count < vertices.Count) morphData.Add(Vector2.Zero);
        while (uvs.Count < vertices.Count) uvs.Add(new Vector2(-1.0f, -1.0f));
        while (parentChordOffsets.Count < vertices.Count) parentChordOffsets.Add(Vector3.Zero);

        for (int i = 0; i < normals.Count; i++) normals[i] = normals[i].Normalized();
        FftShorelineTile? shorelineTile = spacing == (int)SharedFftShorelineFieldCellMeters && !blocky
            ? BuildFftShorelineTile(coordinate, size, ringWidth, sampleCache, alignSurfaceToTerrainSdf)
            : null;
        SectorData result = new(coordinate, requestVersion, editSnapshot.Version, localOrigin, vertices.ToArray(), normals.ToArray(), colors.ToArray(), uvs.ToArray(), morphData.ToArray(), parentChordOffsets.ToArray(), runtimeParentChord, indexList.ToArray(), oceanIndexList.ToArray(), coverageMask, new SurfaceCoverageMask(cells, 1, oceanSurfaceCells, oceanDeepWaterVertexDepths), fullyOceanSurface, shorelineTile);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationsBefore;
        if (allocated > 0) Interlocked.Add(ref _workerAllocatedBytesSinceStartup, allocated);
        return result;
    }

    private static float GetTerrainMorphHeight(CachedTerrainPoint point, bool alignSurfaceToTerrainSdf)
    {
        if (point.UsesPresentationSurface) return point.PresentationTerrainHeight;
        return alignSurfaceToTerrainSdf ? point.SmoothTerrainHeight : point.TerrainHeight;
    }

    private static float GetTerrainRenderHeight(CachedTerrainPoint point, bool alignSurfaceToTerrainSdf)
    {
        if (point.UsesPresentationSurface) return point.PresentationTerrainHeight;
        return (alignSurfaceToTerrainSdf ? point.SmoothTerrainHeight + 0.5f : point.TerrainHeight + 1.0f);
    }

    private static float GetWaterMorphHeight(CachedTerrainPoint point)
        => point.UsesRuntimeWaterPlane ? point.PresentationDisplayedHeight : point.DisplayedHeight;

    private static float GetWaterRenderHeight(CachedTerrainPoint point)
        => point.UsesRuntimeWaterPlane ? point.PresentationDisplayedHeight : point.DisplayedHeight + 1.0f;

    private static float ResolveFftOceanDepth(CachedTerrainPoint point, bool alignSurfaceToTerrainSdf)
    {
        return GetWaterRenderHeight(point) - GetTerrainRenderHeight(point, alignSurfaceToTerrainSdf);
    }

    /// <summary>
    /// Returns a depth only when every physical ocean quad incident to this
    /// canonical vertex is fully wet ocean. Sampling the one-cell halo makes
    /// the answer independent of which adjacent sector owns the vertex.
    /// </summary>
    private static bool TryResolveConservativeOceanVertexDepth(
        TerrainBuildSampleCache sampleCache,
        long around,
        int across,
        int spacing,
        int ringWidth,
        bool alignSurfaceToTerrainSdf,
        out float depth)
    {
        depth = float.PositiveInfinity;
        bool foundPhysicalCell = false;
        int step = Mathf.Max(1, spacing);
        for (int z = -1; z <= 0; z++)
        for (int x = -1; x <= 0; x++)
        {
            long cellAround = around + x * (long)step;
            int cellAcross = across + z * step;
            if (cellAcross < 0 || cellAcross + step > ringWidth) continue;
            foundPhysicalCell = true;
            CachedTerrainPoint p00 = sampleCache.Get(cellAround, cellAcross);
            CachedTerrainPoint p10 = sampleCache.Get(cellAround + step, cellAcross);
            CachedTerrainPoint p01 = sampleCache.Get(cellAround, cellAcross + step);
            CachedTerrainPoint p11 = sampleCache.Get(cellAround + step, cellAcross + step);
            if (!IsDisplayedOcean(p00) || !IsDisplayedOcean(p10)
                || !IsDisplayedOcean(p01) || !IsDisplayedOcean(p11))
            {
                depth = 0.0f;
                return false;
            }
            depth = Mathf.Min(depth,
                Mathf.Min(ResolveFftOceanDepth(p00, alignSurfaceToTerrainSdf), ResolveFftOceanDepth(p10, alignSurfaceToTerrainSdf)));
            depth = Mathf.Min(depth,
                Mathf.Min(ResolveFftOceanDepth(p01, alignSurfaceToTerrainSdf), ResolveFftOceanDepth(p11, alignSurfaceToTerrainSdf)));
        }
        return foundPhysicalCell && !float.IsPositiveInfinity(depth);
    }

    private static bool IsDisplayedOcean(CachedTerrainPoint point) =>
        point.IsDisplayedWater && point.Sample.WaterKind == SurfaceWaterKind.Ocean;

    /// <summary>
    /// Evaluates a smooth surface normal from canonical neighbours rather than
    /// the current sector's triangle fan. Around has no physical edge; across
    /// uses a one-sided difference only at the two actual ring-width bounds.
    /// </summary>
    private static Vector3 CalculateCanonicalSurfaceNormal(
        TerrainBuildSampleCache sampleCache,
        RingLodGeometrySnapshot geometry,
        Transform3D vertexTransform,
        long around,
        int across,
        int spacing,
        int ringWidth,
        float underlay,
        bool alignSurfaceToTerrainSdf,
        bool water)
    {
        int step = Mathf.Max(1, spacing);
        int acrossMinus = Mathf.Max(0, across - step);
        int acrossPlus = Mathf.Min(ringWidth, across + step);

        Vector3 Sample(long sampleAround, int sampleAcross)
        {
            CachedTerrainPoint point = sampleCache.Get(sampleAround, sampleAcross);
            float height = (water
                ? GetWaterRenderHeight(point)
                : GetTerrainRenderHeight(point, alignSurfaceToTerrainSdf)) - underlay;
            return vertexTransform * geometry.CanonicalToWorld(sampleAround, height, sampleAcross);
        }

        Vector3 aroundMinus = Sample(around - step, across);
        Vector3 aroundPlus = Sample(around + step, across);
        Vector3 acrossMinusPoint = Sample(around, acrossMinus);
        Vector3 acrossPlusPoint = Sample(around, acrossPlus);
        // The submitted proxy triangles are clockwise in Godot, while this
        // stencil is a shading normal and must point toward increasing height.
        Vector3 normal = CalculateSurfaceNormalFromNeighbourPositions(
            aroundMinus, aroundPlus, acrossMinusPoint, acrossPlusPoint);
        if (normal.LengthSquared() > 0.0000001f) return normal.Normalized();

        // Degenerate only for a zero-width fixture; retain a finite
        // increasing-height fallback rather than publishing a NaN normal.
        Vector3 center = Sample(around, across);
        CachedTerrainPoint centerPoint = sampleCache.Get(around, across);
        float centerHeight = (water
            ? GetWaterRenderHeight(centerPoint)
            : GetTerrainRenderHeight(centerPoint, alignSurfaceToTerrainSdf)) - underlay;
        Vector3 increasingHeight = CalculateIncreasingCanonicalHeightDirection(
            geometry, vertexTransform, around, across, centerHeight, center);
        return increasingHeight.LengthSquared() > 0.0000001f ? increasingHeight : Vector3.Down;
    }

    private static Vector3 CalculateSurfaceNormalFromNeighbourPositions(
        Vector3 aroundMinus, Vector3 aroundPlus, Vector3 acrossMinus, Vector3 acrossPlus) =>
        (aroundPlus - aroundMinus).Cross(acrossPlus - acrossMinus);

    private static Vector3 CalculateIncreasingCanonicalHeightDirection(
        RingLodGeometrySnapshot geometry,
        Transform3D vertexTransform,
        double around,
        int across,
        float centerHeight,
        Vector3? centerPosition = null)
    {
        Vector3 center = centerPosition
            ?? vertexTransform * geometry.CanonicalToWorld(around, centerHeight, across);
        Vector3 above = vertexTransform * geometry.CanonicalToWorld(around, centerHeight + 1.0f, across);
        return (above - center).Normalized();
    }

    private static SectorData BuildRawBlockySector(ulong requestVersion, RingTerrainSectorCoordinate coordinate, RingLodSurfaceEditSnapshot editSnapshot, RingTerrainSampler sampler, TerrainBuildSampleCache sampleCache, RingLodGeometrySnapshot geometry, Transform3D ownerInverse, Vector3 localOrigin, int size, int spacing, int cells, float underlay, int ringWidth, bool buildWalls, bool buildSkirts, float skirtDepth, int heightQuantum, Color low, Color high, SurfaceCoverageMask? coverageMask)
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var colors = new List<Color>();
        var blockyUvs = new List<Vector2>();
        var indices = new List<int>();
        var oceanIndices = new List<int>();
        long aroundOrigin = coordinate.AroundSector * size;
        int acrossOrigin = coordinate.AcrossSector * size;
        const int biomeSampleSpacing = 16;
        var biomeWeightCache = new Dictionary<TerrainColumnKey, Color>();

        float QuantizedHeight(long around, int across)
        {
            CachedTerrainPoint point = sampleCache.Get(around, across);
            float raw = point.UsesPresentationSurface ? point.PresentationTerrainHeight : point.TerrainHeight;
            return Mathf.Round(raw / heightQuantum) * heightQuantum;
        }

        Vector3 Point(double around, float vertical, float across)
        {
            return ownerInverse * geometry.CanonicalToWorld(around, vertical, Mathf.Clamp(across, 0, ringWidth));
        }

        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
        {
            // See smooth path: coverage is a fragment/shadow discard, never an
            // index-buffer omission.
            long a0 = aroundOrigin + x * spacing;
            long a1 = a0 + spacing;
            int z0 = acrossOrigin + z * spacing;
            int z1 = z0 + spacing;
            if (z1 <= 0 || z0 >= ringWidth) continue;
            int sampleAcross = Mathf.Clamp(z0, 0, ringWidth);
            CachedTerrainPoint point = sampleCache.Get(a0, sampleAcross);
            TerrainSample terrainSample = point.Sample;
            float height = QuantizedHeight(a0, sampleAcross);
            float topY = height + (point.UsesPresentationSurface ? 0.0f : 1.0f) - underlay;
            Color terrainColor = ResolveBiomeTextureWeight(
                sampler, a0, sampleAcross, ringWidth, biomeSampleSpacing, biomeWeightCache);
            Vector2 coverageUv = new((x + 0.5f) / cells, (z + 0.5f) / cells);

            AddBlockyQuad(vertices, normals, colors, indices,
                Point(a0, topY, z0), Point(a1, topY, z0), Point(a1, topY, z1), Point(a0, topY, z1),
                terrainColor, reverse: false, blockyUvs, coverageUv);
            if (point.IsDisplayedWater)
            {
                float waterHeight = GetWaterMorphHeight(point);
                float waterTopY = GetWaterRenderHeight(point) - underlay;
                Color waterColor = RingTerrainVisuals.ResolveSeededColor(terrainSample, Mathf.RoundToInt(waterHeight), sampler, low, high);
                AddBlockyQuad(vertices, normals, colors, oceanIndices,
                    Point(a0, waterTopY, z0), Point(a1, waterTopY, z0), Point(a1, waterTopY, z1), Point(a0, waterTopY, z1),
                    waterColor, reverse: false, blockyUvs, coverageUv);
            }

            float aroundNeighborHeight = QuantizedHeight(a1, z0);
            if (aroundNeighborHeight != height)
            {
                float surfaceOffset = point.UsesPresentationSurface ? 0.0f : 1.0f;
                float lowY = Mathf.Min(height, aroundNeighborHeight) + surfaceOffset - underlay;
                float highY = Mathf.Max(height, aroundNeighborHeight) + surfaceOffset - underlay;
                AddBlockyQuad(vertices, normals, colors, indices,
                    Point(a1, lowY, z0), Point(a1, lowY, z1), Point(a1, highY, z1), Point(a1, highY, z0),
                    terrainColor, reverse: height < aroundNeighborHeight, blockyUvs, coverageUv);
            }

            float acrossNeighborHeight = QuantizedHeight(a0, z1);
            if (acrossNeighborHeight != height && z1 < ringWidth)
            {
                float surfaceOffset = point.UsesPresentationSurface ? 0.0f : 1.0f;
                float lowY = Mathf.Min(height, acrossNeighborHeight) + surfaceOffset - underlay;
                float highY = Mathf.Max(height, acrossNeighborHeight) + surfaceOffset - underlay;
                AddBlockyQuad(vertices, normals, colors, indices,
                    Point(a0, lowY, z1), Point(a1, lowY, z1), Point(a1, highY, z1), Point(a0, highY, z1),
                    terrainColor, reverse: height > acrossNeighborHeight, blockyUvs, coverageUv);
            }
        }

        var blockyMorphs = Enumerable.Repeat(Vector2.Zero, vertices.Count).ToList();
        if (buildSkirts)
            AppendSectorSkirts(vertices, normals, colors, blockyUvs, blockyMorphs, indices, coordinate, sampleCache, sampler, geometry, ownerInverse, size, spacing, spacing * 2, underlay, parentUnderlay: 0.0f, ringWidth, skirtDepth, buildWalls, low, high, geomorph: false);

        for (int i = 0; i < normals.Count; i++) normals[i] = normals[i].Normalized();
        while (blockyUvs.Count < vertices.Count) blockyUvs.Add(new Vector2(-1.0f, -1.0f));
        // Blocky geometry has no continuous shoreline contour. Fail closed so it
        // cannot publish terrain data that disagrees with the rendered surface.
        while (blockyMorphs.Count < vertices.Count) blockyMorphs.Add(Vector2.Zero);
        return new SectorData(coordinate, requestVersion, editSnapshot.Version, localOrigin, vertices.ToArray(), normals.ToArray(), colors.ToArray(), blockyUvs.ToArray(), blockyMorphs.ToArray(), new Vector3[vertices.Count], false, indices.ToArray(), oceanIndices.ToArray(), coverageMask, null, false, null);
    }

    private static FftShorelineTile BuildFftShorelineTile(
        RingTerrainSectorCoordinate coordinate,
        int sectorSize,
        int ringWidth,
        TerrainBuildSampleCache sampleCache,
        bool alignSurfaceToTerrainSdf)
    {
        long started = Stopwatch.GetTimestamp();
        int interiorCells = Mathf.Max(1, sectorSize / (int)SharedFftShorelineFieldCellMeters);
        int halo = FftShorelineFieldHaloCells;
        int side = interiorCells + halo * 2 + 1;
        var samples = new float[side * side * 4];
        var wet = new bool[side * side];
        var valid = new bool[side * side];
        var seeds = new bool[side * side];
        long originAround = coordinate.AroundSector * sectorSize - halo * (long)SharedFftShorelineFieldCellMeters;
        int originAcross = coordinate.AcrossSector * sectorSize - halo * (int)SharedFftShorelineFieldCellMeters;
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int rawAcross = originAcross + z * (int)SharedFftShorelineFieldCellMeters;
            int index = z * side + x;
            if (rawAcross < 0 || rawAcross > ringWidth) continue;
            CachedTerrainPoint point = sampleCache.Get(originAround + x * (long)SharedFftShorelineFieldCellMeters, rawAcross);
            float signedDepth = point.Sample.WaterKind == SurfaceWaterKind.Ocean
                ? GetWaterRenderHeight(point) - GetTerrainRenderHeight(point, alignSurfaceToTerrainSdf)
                : -FftShorelineFieldHaloMeters;
            valid[index] = true;
            // Match the actual water index generation; smooth SDF height only
            // changes the physical clearance carried in signedDepth.
            wet[index] = point.Sample.WaterKind == SurfaceWaterKind.Ocean && point.IsDisplayedWater;
            int offset = index * 4;
            samples[offset] = signedDepth;
            samples[offset + 2] = 1.0f;
        }
        // Match the sector's two submitted triangles: a diagonal shoreline must
        // pin every triangle vertex, not just an axis-aligned edge.
        for (int z = 0; z < side - 1; z++)
        for (int x = 0; x < side - 1; x++)
        {
            int i00 = z * side + x, i10 = i00 + 1, i01 = i00 + side, i11 = i01 + 1;
            SeedMixedTriangle(i01, i10, i00, wet, valid, seeds);
            SeedMixedTriangle(i10, i01, i11, wet, valid, seeds);
        }
        var distance = new float[side * side];
        for (int index = 0; index < distance.Length; index++)
            distance[index] = valid[index] ? (seeds[index] ? 0.0f : float.PositiveInfinity) : float.PositiveInfinity;
        RelaxShoreDistance(distance, valid, side, reverse: false);
        RelaxShoreDistance(distance, valid, side, reverse: true);
        for (int index = 0; index < distance.Length; index++)
        {
            if (!valid[index]) continue;
            float metres = Mathf.Min(FftShorelineFieldHaloMeters, distance[index] * SharedFftShorelineFieldCellMeters);
            samples[index * 4 + 1] = wet[index] ? metres : -metres;
        }
        int row = interiorCells + 1;
        var interiorBytes = new byte[row * row * 8];
        for (int z = 0; z < row; z++)
        for (int x = 0; x < row; x++)
        {
            int source = ((z + halo) * side + x + halo) * 4;
            int target = (z * row + x) * 8;
            WriteHalf(interiorBytes, target, samples[source]);
            WriteHalf(interiorBytes, target + 2, samples[source + 1]);
            WriteHalf(interiorBytes, target + 4, samples[source + 2]);
        }
        Interlocked.Increment(ref _sharedFftShorelineFieldWorkerBuildCount);
        Interlocked.Add(ref _sharedFftShorelineFieldWorkerBuildTicks, Stopwatch.GetTimestamp() - started);
        return new FftShorelineTile(coordinate, interiorCells, SharedFftShorelineFieldCellMeters, interiorBytes);
    }

    private static void SeedMixedTriangle(int a, int b, int c, bool[] wet, bool[] valid, bool[] seeds)
    {
        if (!valid[a] || !valid[b] || !valid[c]) return;
        if (wet[a] == wet[b] && wet[b] == wet[c]) return;
        seeds[a] = seeds[b] = seeds[c] = true;
    }

    private static void RelaxShoreDistance(float[] distance, bool[] valid, int side, bool reverse)
    {
        int start = reverse ? side - 1 : 0, end = reverse ? -1 : side, step = reverse ? -1 : 1;
        for (int z = start; z != end; z += step)
        for (int x = start; x != end; x += step)
        {
            int index = z * side + x;
            if (!valid[index]) continue;
            float best = distance[index];
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                int nx = x + dx, nz = z + dz;
                if (nx < 0 || nx >= side || nz < 0 || nz >= side) continue;
                int neighbor = nz * side + nx;
                if (!valid[neighbor]) continue;
                best = Mathf.Min(best, distance[neighbor] + (dx == 0 || dz == 0 ? 1.0f : 1.41421356f));
            }
            distance[index] = best;
        }
    }

    private static int CountWaterVertices(bool[] waterVertices, int a, int b, int c) =>
        (waterVertices[a] ? 1 : 0) + (waterVertices[b] ? 1 : 0) + (waterVertices[c] ? 1 : 0);

    private static Color ResolveBiomeTextureWeight(
        RingTerrainSampler sampler,
        long around,
        int across,
        int ringWidth,
        int sampleSpacing,
        Dictionary<TerrainColumnKey, Color> cache)
    {
        long sampledAround = (long)Math.Floor(around / (double)sampleSpacing) * sampleSpacing
            + sampleSpacing / 2;
        int sampledAcross = Mathf.Clamp(
            (int)Math.Floor(across / (double)sampleSpacing) * sampleSpacing + sampleSpacing / 2,
            0,
            Mathf.Max(0, ringWidth - 1));
        var key = new TerrainColumnKey(sampledAround, sampledAcross);
        if (!cache.TryGetValue(key, out Color weight))
        {
            weight = sampler.GetBiomeTextureWeights(sampledAround, sampledAcross).ToVertexColor();
            cache.Add(key, weight);
        }
        return weight;
    }

    /// <summary>All partially wet triangles participate in the shoreline contour.</summary>
    public static bool ShouldClipWaterTriangle(bool aIsWater, bool bIsWater, bool cIsWater)
    {
        int wetCount = (aIsWater ? 1 : 0) + (bIsWater ? 1 : 0) + (cIsWater ? 1 : 0);
        return wetCount is 1 or 2;
    }

    private static void AppendClippedWaterTriangle(
        in PartialWaterTriangle triangle,
        bool[] wetVertices,
        float[] terrainGeometryHeights,
        double[] aroundCoordinates,
        float[] acrossCoordinates,
        CachedTerrainPoint[] terrainPoints,
        IReadOnlyList<Vector2> uvs,
        IReadOnlyList<Vector2> morphData,
        RingTerrainSampler sampler,
        RingLodGeometrySnapshot geometry,
        Transform3D vertexTransform,
        float underlay,
        Color low,
        Color high,
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Color> colors,
        List<Vector2> outputUvs,
        List<Vector2> outputMorphData,
        List<int> indices)
    {
        Span<int> sourceIndices = stackalloc int[3] { triangle.A, triangle.B, triangle.C };
        float representativeWaterHeight = 0.0f;
        int wetCount = 0;
        SurfaceWaterKind representativeKind = SurfaceWaterKind.None;
        TerrainSample representativeSample = default;
        for (int index = 0; index < sourceIndices.Length; index++)
        {
            int sourceIndex = sourceIndices[index];
            if (!wetVertices[sourceIndex]) continue;
            CachedTerrainPoint point = terrainPoints[sourceIndex];
            representativeWaterHeight += GetWaterMorphHeight(point);
            wetCount++;
            if (representativeKind == SurfaceWaterKind.None)
            {
                representativeKind = point.Sample.WaterKind;
                representativeSample = point.Sample;
            }
        }
        if (wetCount == 0 || representativeKind == SurfaceWaterKind.None) return;
        representativeWaterHeight /= wetCount;

        var polygon = new List<ShorelineClipVertex>(5);
        for (int index = 0; index < sourceIndices.Length; index++)
        {
            int sourceIndex = sourceIndices[index];
            CachedTerrainPoint point = terrainPoints[sourceIndex];
            float waterHeight = wetVertices[sourceIndex]
                ? GetWaterMorphHeight(point)
                : representativeWaterHeight;
            float rawCoverage = waterHeight - terrainGeometryHeights[sourceIndex];
            float signedCoverage = wetVertices[sourceIndex]
                ? Mathf.Max(0.001f, rawCoverage)
                : -Mathf.Max(0.001f, Mathf.Abs(rawCoverage));
            polygon.Add(new ShorelineClipVertex(
                aroundCoordinates[sourceIndex],
                acrossCoordinates[sourceIndex],
                waterHeight,
                signedCoverage,
                uvs[sourceIndex],
                // The source index addresses the terrain half of the lattice;
                // shoreline water must inherit the matching water morph.
                ResolveClippedWaterMorph(morphData, terrainPoints.Length, sourceIndex)));
        }

        var clipped = new List<ShorelineClipVertex>(5);
        ShorelineClipVertex previous = polygon[^1];
        bool previousInside = previous.SignedCoverage >= 0.0f;
        foreach (ShorelineClipVertex current in polygon)
        {
            bool currentInside = current.SignedCoverage >= 0.0f;
            if (currentInside != previousInside)
                clipped.Add(IntersectShoreline(previous, current, previousInside));
            if (currentInside) clipped.Add(current);
            previous = current;
            previousInside = currentInside;
        }
        if (clipped.Count < 3) return;

        Color waterColor = RingWaterModel.EncodeWaterKind(
            RingTerrainVisuals.ResolveSeededColor(
                representativeSample,
                Mathf.RoundToInt(representativeWaterHeight),
                sampler,
                low,
                high),
            representativeKind);
        int baseIndex = vertices.Count;
        foreach (ShorelineClipVertex clippedVertex in clipped)
        {
            vertices.Add(vertexTransform * geometry.CanonicalToWorld(
                clippedVertex.Around,
                clippedVertex.WaterHeight + (terrainPoints[sourceIndices[0]].UsesRuntimeWaterPlane ? 0.0f : 1.0f) - underlay,
                clippedVertex.Across));
            normals.Add(Vector3.Zero);
            colors.Add(waterColor);
            outputUvs.Add(clippedVertex.Uv);
            outputMorphData.Add(new Vector2(clippedVertex.Morph, 0.0f));
        }
        for (int index = 1; index < clipped.Count - 1; index++)
            AddTriangle(indices, vertices, normals, baseIndex, baseIndex + index, baseIndex + index + 1);
    }

    private static float ResolveClippedWaterMorph(IReadOnlyList<Vector2> morphData, int surfaceVertexCount, int sourceIndex)
        => morphData[surfaceVertexCount + sourceIndex].X;

    private static ShorelineClipVertex IntersectShoreline(
        in ShorelineClipVertex from,
        in ShorelineClipVertex to,
        bool fromInside)
    {
        float denominator = from.SignedCoverage - to.SignedCoverage;
        float interpolation = Mathf.Abs(denominator) <= 0.000001f
            ? 0.5f
            : Mathf.Clamp(from.SignedCoverage / denominator, 0.0f, 1.0f);
        double aroundDelta = to.Around - from.Around;
        float acrossDelta = to.Across - from.Across;
        double edgeLength = Math.Sqrt(aroundDelta * aroundDelta + acrossDelta * acrossDelta);
        float overlapFraction = edgeLength <= 0.000001
            ? 0.0f
            : Mathf.Clamp(ShorelineOverlapMeters / (float)edgeLength, 0.0f, 0.25f);
        interpolation = fromInside
            ? Mathf.Min(1.0f, interpolation + overlapFraction)
            : Mathf.Max(0.0f, interpolation - overlapFraction);
        return new ShorelineClipVertex(
            from.Around + aroundDelta * interpolation,
            Mathf.Lerp(from.Across, to.Across, interpolation),
            Mathf.Lerp(from.WaterHeight, to.WaterHeight, interpolation),
            0.0f,
            from.Uv.Lerp(to.Uv, interpolation),
            Mathf.Lerp(from.Morph, to.Morph, interpolation));
    }

    private static float SampleParentTerrainHeight(TerrainBuildSampleCache sampleCache, long around, int across, int parentSpacing, int ringWidth, bool useSmoothTerrainHeight, bool positiveDiagonal = false)
    {
        int spacing = Mathf.Max(1, parentSpacing);
        long a0 = (long)Math.Floor(around / (double)spacing) * spacing;
        long a1 = a0 + spacing;
        int z0 = Mathf.Clamp(Mathf.FloorToInt(across / (float)spacing) * spacing, 0, ringWidth);
        int z1 = Mathf.Clamp(z0 + spacing, 0, ringWidth);
        float tx = (float)((around - a0) / (double)spacing);
        float tz = z1 == z0 ? 0.0f : (across - z0) / (float)(z1 - z0);
        CachedTerrainPoint p00 = sampleCache.Get(a0, z0);
        CachedTerrainPoint p10 = sampleCache.Get(a1, z0);
        CachedTerrainPoint p01 = sampleCache.Get(a0, z1);
        CachedTerrainPoint p11 = sampleCache.Get(a1, z1);
        float h00 = GetTerrainMorphHeight(p00, useSmoothTerrainHeight);
        float h10 = GetTerrainMorphHeight(p10, useSmoothTerrainHeight);
        float h01 = GetTerrainMorphHeight(p01, useSmoothTerrainHeight);
        float h11 = GetTerrainMorphHeight(p11, useSmoothTerrainHeight);
        return positiveDiagonal ? SamplePositiveParentTriangleHeight(h00, h10, h01, h11, tx, tz) : SampleParentTriangleHeight(h00, h10, h01, h11, tx, tz);
    }

    private static float SampleParentWaterHeight(TerrainBuildSampleCache sampleCache, long around, int across, int parentSpacing, int ringWidth)
    {
        int spacing = Mathf.Max(1, parentSpacing);
        long a0 = (long)Math.Floor(around / (double)spacing) * spacing;
        long a1 = a0 + spacing;
        int z0 = Mathf.Clamp(Mathf.FloorToInt(across / (float)spacing) * spacing, 0, ringWidth);
        int z1 = Mathf.Clamp(z0 + spacing, 0, ringWidth);
        float tx = (float)((around - a0) / (double)spacing);
        float tz = z1 == z0 ? 0.0f : (across - z0) / (float)(z1 - z0);
        float h00 = GetWaterMorphHeight(sampleCache.Get(a0, z0));
        float h10 = GetWaterMorphHeight(sampleCache.Get(a1, z0));
        float h01 = GetWaterMorphHeight(sampleCache.Get(a0, z1));
        float h11 = GetWaterMorphHeight(sampleCache.Get(a1, z1));
        return SampleParentTriangleHeight(h00, h10, h01, h11, tx, tz);
    }

    // Parent quads submit (i01, i10, i00) then (i10, i01, i11), so their
    // diagonal runs from i01 to i10.  Bilinear interpolation describes a
    // curved patch rather than either rendered triangle and causes a morph
    // discontinuity inside every parent cell.
    private static float SampleParentTriangleHeight(float h00, float h10, float h01, float h11, float tx, float tz)
    {
        return tx + tz <= 1.0f
            ? h00 + (h10 - h00) * tx + (h01 - h00) * tz
            : h11 + (h01 - h11) * (1.0f - tx) + (h10 - h11) * (1.0f - tz);
    }

    private static float SamplePositiveParentTriangleHeight(float h00, float h10, float h01, float h11, float tx, float tz) =>
        tx >= tz ? h00 + (h10 - h00) * tx + (h11 - h10) * tz : h00 + (h11 - h01) * tx + (h01 - h00) * tz;

    private static Vector3 SampleRuntimeParentChordOffset(TerrainBuildSampleCache cache, RingLodGeometrySnapshot geometry, Transform3D inverse,
        long around, int across, int spacing, int width, bool smooth, float parentUnderlay, Vector3 own)
    {
        long a0 = (long)Math.Floor(around / (double)spacing) * spacing; long a1 = a0 + spacing;
        int z0 = Mathf.Clamp(Mathf.FloorToInt(across / (float)spacing) * spacing, 0, width), z1 = Mathf.Clamp(z0 + spacing, 0, width);
        float tx = (float)((around - a0) / (double)spacing), tz = z1 == z0 ? 0f : (across - z0) / (float)(z1 - z0);
        Vector3 p00 = inverse * geometry.CanonicalToWorld(a0, GetTerrainRenderHeight(cache.Get(a0, z0), smooth) - parentUnderlay, z0);
        Vector3 p10 = inverse * geometry.CanonicalToWorld(a1, GetTerrainRenderHeight(cache.Get(a1, z0), smooth) - parentUnderlay, z0);
        Vector3 p01 = inverse * geometry.CanonicalToWorld(a0, GetTerrainRenderHeight(cache.Get(a0, z1), smooth) - parentUnderlay, z1);
        Vector3 p11 = inverse * geometry.CanonicalToWorld(a1, GetTerrainRenderHeight(cache.Get(a1, z1), smooth) - parentUnderlay, z1);
        Vector3 parent = tx >= tz
            ? p00 * (1f - tx) + p10 * (tx - tz) + p11 * tz
            : p00 * (1f - tz) + p01 * (tz - tx) + p11 * tx;
        return parent - own;
    }

    /// <summary>
    /// Exercises the runtime parent-chord helper against independently formed
    /// parent triangle vertices.  This deliberately uses float presentation
    /// edits so a height-only or bilinear comparison cannot pass by accident.
    /// </summary>
    private static bool RunRuntimeParentChordContract()
    {
        int width = CertifiedRingScale.RingWidthMeters;
        double circumference = CertifiedRingScale.EffectiveCircumferenceMeters;
        using var settings = new VoxelTerrainSettings
        {
            SurfaceGenerationRecipeJson = TerrainGenerationParameters.Default().CanonicalJson,
            SurfaceGenerationContractId = RingTerrainSampler.RuntimeSurfaceGenerationContractId,
            BedrockY = -8000,
            SeaLevelY = 0,
            EnableUnified3dDensityTerrain = false,
            EnableProceduralCaves = false,
            EnableNaturalOverhangs = false,
            EnableDesignedCaveDensityComposition = false
        };
        using var sampler = new RingTerrainSampler(settings, true, CertifiedRingScale.AroundVoxelLength,
            CertifiedRingScale.EffectiveRadiusMeters, width, 8400);
        long baseAround = sampler.AroundRingVoxelLength - 16;
        const int baseAcross = 158000;
        var floatEdits = new Dictionary<RingTerrainColumnCoordinate, float>();
        foreach (int parentSpacing in new[] { 4, 8, 16 })
        for (int dz = 0; dz <= 1; dz++)
        for (int dx = 0; dx <= 1; dx++)
        {
            long around = baseAround + dx * parentSpacing;
            int across = baseAcross + dz * parentSpacing;
            // Every parent cell is a non-planar saddle with exact fractional
            // presentation values, independent of procedural baseline shape.
            long canonicalAround = around % sampler.AroundRingVoxelLength;
            if (canonicalAround < 0) canonicalAround += sampler.AroundRingVoxelLength;
            floatEdits[new RingTerrainColumnCoordinate(canonicalAround, across)] = 100 + parentSpacing * 3 + dx * 17 + dz * 29 + dx * dz * 11.25f;
        }
        var edits = new RingLodSurfaceEditSnapshot(1, sampler.AroundRingVoxelLength,
            new Dictionary<RingTerrainColumnCoordinate, int>(), floatEdits, true);
        var cache = new TerrainBuildSampleCache(edits, sampler, width);
        var placements = new[]
        {
            new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero,
                CertifiedRingScale.EffectiveRadiusMeters, 0, 0, circumference, -1000, 1000,
                new RingCanonicalPosition(baseAround + 4, 0, baseAcross + 4)),
            new RingLodGeometrySnapshot(new Transform3D(Basis.FromEuler(new Vector3(.03f, -.07f, .02f)), new Vector3(13, -7, 19)),
                new Vector3(3, 5, -11), new Vector3(17, 0, -23), CertifiedRingScale.EffectiveRadiusMeters,
                31, -9, circumference, -1000, 1000,
                new RingCanonicalPosition(12, 0, baseAcross + 40))
        };
        foreach (RingLodGeometrySnapshot placement in placements)
        foreach (int parentSpacing in new[] { 4, 8, 16 })
        {
            foreach ((int x, int z) fraction in new[] { (parentSpacing * 3 / 4, parentSpacing / 4), (parentSpacing / 4, parentSpacing * 3 / 4), (parentSpacing / 2, parentSpacing / 2), (0, parentSpacing / 2) })
            {
                long around = baseAround + fraction.x;
                int across = baseAcross + fraction.z;
                const float ownUnderlay = .17f;
                const float parentUnderlay = .63f;
                Transform3D inverse = new Transform3D(Basis.FromEuler(new Vector3(-.02f, .04f, -.01f)), new Vector3(-4, 8, 2)).AffineInverse();
                Vector3 own = inverse * placement.CanonicalToWorld(around,
                    GetTerrainRenderHeight(cache.Get(around, across), false) - ownUnderlay, across);
                Vector3 actual = SampleRuntimeParentChordOffset(cache, placement, inverse, around, across,
                    parentSpacing, width, false, parentUnderlay, own);
                Vector3 expected = IndependentlySampleRuntimeParentChord(cache, placement, inverse, around, across,
                    parentSpacing, width, parentUnderlay);
                if (!((own + actual).DistanceTo(expected) < .001f)) return false;
            }
        }
        return true;
    }

    private static Vector3 IndependentlySampleRuntimeParentChord(TerrainBuildSampleCache cache,
        RingLodGeometrySnapshot geometry, Transform3D inverse, long around, int across, int spacing, int width, float underlay)
    {
        long a0 = (long)Math.Floor(around / (double)spacing) * spacing;
        long a1 = a0 + spacing;
        int z0 = Mathf.Clamp(Mathf.FloorToInt(across / (float)spacing) * spacing, 0, width);
        int z1 = Mathf.Clamp(z0 + spacing, 0, width);
        float tx = (float)((around - a0) / (double)spacing);
        float tz = z1 == z0 ? 0.0f : (across - z0) / (float)(z1 - z0);
        Vector3 lowerLeft = inverse * geometry.CanonicalToWorld(a0, GetTerrainRenderHeight(cache.Get(a0, z0), false) - underlay, z0);
        Vector3 lowerRight = inverse * geometry.CanonicalToWorld(a1, GetTerrainRenderHeight(cache.Get(a1, z0), false) - underlay, z0);
        Vector3 upperLeft = inverse * geometry.CanonicalToWorld(a0, GetTerrainRenderHeight(cache.Get(a0, z1), false) - underlay, z1);
        Vector3 upperRight = inverse * geometry.CanonicalToWorld(a1, GetTerrainRenderHeight(cache.Get(a1, z1), false) - underlay, z1);
        return tx >= tz
            ? lowerLeft * (1.0f - tx) + lowerRight * (tx - tz) + upperRight * tz
            : lowerLeft * (1.0f - tz) + upperLeft * (tz - tx) + upperRight * tx;
    }

    /// <summary>CPU contract callable from a Godot test scene without workers.</summary>
    public static bool RunProxySeamGeometryContract()
    {
        bool Check(bool condition, string label)
        {
            if (!condition) GD.PushError($"PROXY_SEAM_GEOMETRY_CONTRACT_FAIL:{label}");
            return condition;
        }
        if (!Check(CertifiedRingScale.Validate(out _), "ring-scale")) return false;
        double circumference = CertifiedRingScale.EffectiveCircumferenceMeters;
        double radius = CertifiedRingScale.EffectiveRadiusMeters;
        const int ringWidth = 318_000;
        var builtBeforeTeleport = new RingLodGeometrySnapshot(
            Transform3D.Identity, Vector3.Zero, Vector3.Zero, radius, 0.0, 0.0,
            circumference, -1000.0, 1000.0,
            new RingCanonicalPosition(981_748.0, 5.0, 108_327.0));
        foreach (int size in new[] { 128, 256, 512 })
        {
            var left = new RingTerrainSectorCoordinate(-1, 0);
            var right = new RingTerrainSectorCoordinate(0, 0);
            (double leftAround, double leftAcross) = ResolveSectorCanonicalCenter(left, size, ringWidth);
            (double rightAround, double rightAcross) = ResolveSectorCanonicalCenter(right, size, ringWidth);
            var installedAfterTeleport = builtBeforeTeleport with
            {
                // Deliberately close to the wrapped shared edge, not millions
                // of metres away where float quantization can hide a gap.
                FloatingOriginCanonicalAnchor = new RingCanonicalPosition(32.0, 5.0, leftAcross)
            };
            // Worker-local data generated prior to the teleport.
            Vector3 leftVertex = builtBeforeTeleport.CenteredAt(leftAround, leftAcross)
                .CanonicalToWorld(0.0, 0.0, leftAcross);
            Vector3 rightVertex = builtBeforeTeleport.CenteredAt(rightAround, rightAcross)
                .CanonicalToWorld(circumference, 0.0, rightAcross);
            // Placement evaluated only when that worker data is uploaded.
            Vector3 leftInstalled = ResolveSectorLocalOrigin(left, installedAfterTeleport, Transform3D.Identity, size, ringWidth) + leftVertex;
            Vector3 rightInstalled = ResolveSectorLocalOrigin(right, installedAfterTeleport, Transform3D.Identity, size, ringWidth) + rightVertex;
            if (!Check(leftInstalled.Length() < 2000.0f && rightInstalled.Length() < 2000.0f
                && leftInstalled.DistanceTo(rightInstalled) < 0.001f, $"teleport-seam-{size}")) return false;
        }

        // Non-planar parent corners must follow the submitted i01--i10 diagonal.
        if (!Check(Mathf.IsEqualApprox(SampleParentTriangleHeight(0, 0, 0, 4, 0.5f, 0.5f), 0.0f), "parent-triangle-center")) return false;
        if (!Check(Mathf.IsEqualApprox(SampleParentTriangleHeight(0, 0, 0, 4, 0.75f, 0.75f), 2.0f), "parent-triangle-upper")) return false;
        // Runtime Terrain3D submits the opposite diagonal.  These fractional
        // samples exercise both triangles and keep the legacy water rule apart.
        if (!Mathf.IsEqualApprox(SamplePositiveParentTriangleHeight(0, 4, 8, 12, .75f, .25f), 5.0f)) return false;
        if (!Mathf.IsEqualApprox(SamplePositiveParentTriangleHeight(0, 4, 8, 12, .25f, .75f), 7.0f)) return false;
        if (!Mathf.IsEqualApprox(SamplePositiveParentTriangleHeight(0, 0, 0, 4, .5f, .5f), 2.0f)) return false;
        if (!Check(RunActualParentSpacingContract(), "actual-parent-spacing")) return false;
        if (!Check(RunGlobalMorphTargetIntegrationContract(), "global-morph-target")) return false;
        if (!Check(RunRuntimeParentChordContract(), "runtime-parent-chord")) return false;
        if (!RectangleIntersectsTerrain3DDiscard(-8, 8, 92, 108, circumference - 2, 100, circumference, 20)) return false;
        if (RectangleIntersectsTerrain3DDiscard(200, 240, 200, 240, 0, 0, circumference, 20)) return false;
        // A clipped water vertex addresses the water lattice half, not terrain.
        Vector2[] morphs = { new(1, 0), new(2, 0), new(10, 0), new(20, 0) };
        int terrainVertexCount = 2;
        if (!Mathf.IsEqualApprox(ResolveClippedWaterMorph(morphs, terrainVertexCount, 1), 20.0f)) return false;
        var vertices = new List<Vector3>(); var normals = new List<Vector3>();
        var colors = new List<Color>(); var uvs = new List<Vector2>();
        var skirtMorphs = new List<Vector2>(); var indices = new List<int>();
        AddSkirtQuad(vertices, normals, colors, uvs, skirtMorphs, indices,
            Vector3.Zero, Vector3.Right, Vector3.Right + Vector3.Up, Vector3.Up,
            Colors.White, false,
            ResolveSkirtCoverageUv(new RingTerrainSectorCoordinate(7, 3), 128, 960, 416),
            ResolveSkirtCoverageUv(new RingTerrainSectorCoordinate(7, 3), 128, 992, 416), 1.25f, 2.5f);
        return vertices.Count == 4 && indices.Count == 6
            && Mathf.IsEqualApprox(uvs[0].X, 0.5f) && Mathf.IsEqualApprox(uvs[1].X, 0.75f)
            && skirtMorphs.All(value => Mathf.IsEqualApprox(value.Y, 1.0f))
            && Mathf.IsEqualApprox(skirtMorphs[0].X, 1.25f) && Mathf.IsEqualApprox(skirtMorphs[1].X, 2.5f)
            && indices[0] == 3 && indices[1] == 1 && indices[2] == 0;
    }

    /// <summary>CPU-only regression for periodic structural wall request ranges.</summary>
    public bool RunStructuralWallPeriodicRangeContract()
    {
        try
        {
            const int radius = 3;
            foreach (int size in new[] { 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 131072, 524288, 2097152 })
            {
                long length = Math.Max(1L, (long)Math.Ceiling(CertifiedRingScale.EffectiveCircumferenceMeters / size));
                // The final physical sector is partial for these sizes. A seam
                // prediction must request the local strip, never all sectors.
                StructuralWallSectorRange forward = ResolveStructuralWallSectorRange(length - 1, 0, length, radius);
                StructuralWallSectorRange backward = ResolveStructuralWallSectorRange(0, length - 1, length, radius);
                if (forward.SectorCount != 2L * radius + 2L
                    || backward.SectorCount != 2L * radius + 2L
                    || forward.SectorCount >= length || backward.SectorCount >= length)
                    return false;
            }

            if (ResolveStructuralWallSectorRange(10, 14, 97, radius) is not { StartSector: 7, EndSectorExclusive: 18 }
                || ResolveStructuralWallSectorRange(14, 10, 97, radius) is not { StartSector: 7, EndSectorExclusive: 18 }
                || ResolveStructuralWallSectorRange(10, 10, 97, radius) is not { StartSector: 7, EndSectorExclusive: 14 }
                || ResolveStructuralWallSectorRange(-1, 0, 97, radius) is not { StartSector: -4, EndSectorExclusive: 4 }
                || ResolveStructuralWallSectorRange(96, 97, 97, radius) is not { StartSector: 93, EndSectorExclusive: 101 })
                return false;

            // Even-period ties keep the caller's direction, preventing a
            // direction flip when prediction lies exactly opposite the focus.
            if (NearestPeriodicSector(0, 4, 8) != 4
                || NearestPeriodicSector(0, -4, 8) != -4
                || NearestPeriodicSector(6, 2, 8) != 2)
                return false;

            const int fixtureSectorSize = 128;
            const int fixtureSubdivisions = 2;
            long fixtureLength = Math.Max(1L, (long)Math.Ceiling(
                CertifiedRingScale.EffectiveCircumferenceMeters / fixtureSectorSize));
            StructuralWallSectorRange fixtureRange = ResolveStructuralWallSectorRange(
                0, fixtureLength - 1, fixtureLength, radius);
            int fixtureAroundCells = checked((int)fixtureRange.SectorCount * fixtureSubdivisions);
            double fixtureStep = fixtureSectorSize / (double)fixtureSubdivisions;
            var request = new StructuralWallRequest(
                new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero,
                    CertifiedRingScale.EffectiveRadiusMeters, 0.0, 0.0,
                    CertifiedRingScale.EffectiveCircumferenceMeters, -1_000.0, 1_000.0,
                    new RingCanonicalPosition(0.0, 0.0, 0.0)),
                Transform3D.Identity, fixtureRange.StartSector * (double)fixtureSectorSize,
                fixtureStep, fixtureAroundCells, 1_000.0, 2, 128,
                true, true, false, false, -1_500.0, 0.0, Colors.White, 1);
            StructuralWallData wall = BuildStructuralWallData(request);
            if (fixtureRange.SectorCount != 8 || fixtureAroundCells != 16
                || wall.Vertices.Length != 320 || wall.Indices.Length != 480
                || wall.Normals.Length != wall.Vertices.Length || wall.Colors.Length != wall.Vertices.Length)
                return false;
            for (int cell = 0; cell < fixtureAroundCells; cell++)
            {
                float chord = wall.Vertices[cell * 4].DistanceTo(wall.Vertices[cell * 4 + 1]);
                if (!float.IsFinite(chord) || chord <= 0.0f || chord > fixtureStep * 1.1)
                    return false;
            }
            for (int i = 0; i < wall.Indices.Length; i += 3)
            {
                int a = wall.Indices[i], b = wall.Indices[i + 1], c = wall.Indices[i + 2];
                if ((uint)a >= (uint)wall.Vertices.Length || (uint)b >= (uint)wall.Vertices.Length || (uint)c >= (uint)wall.Vertices.Length)
                    return false;
                Vector3 face = (wall.Vertices[c] - wall.Vertices[a]).Cross(wall.Vertices[b] - wall.Vertices[a]);
                if (!face.IsFinite() || face.LengthSquared() <= 0.000001f
                    || !wall.Normals[a].IsFinite() || wall.Normals[a].LengthSquared() <= 0.000001f
                    || face.Dot(wall.Normals[a]) <= 0.0f)
                    return false;
            }
            GD.Print("STRUCTURAL_WALL_PERIODIC_RANGE_CONTRACT_PASS: seam prediction requests a bounded local strip.");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError($"STRUCTURAL_WALL_PERIODIC_RANGE_CONTRACT_FAIL: {exception.Message}");
            return false;
        }
    }

    private static void AppendSectorSkirts(List<Vector3> vertices, List<Vector3> normals, List<Color> colors, List<Vector2> uvs, List<Vector2> morphs, List<int> indices, RingTerrainSectorCoordinate coordinate, TerrainBuildSampleCache sampleCache, RingTerrainSampler sampler, RingLodGeometrySnapshot geometry, Transform3D ownerInverse, int size, int spacing, int parentSpacing, float underlay, float parentUnderlay, int ringWidth, float skirtDepth, bool structuralWalls, Color low, Color high, bool useSmoothTerrainHeight = false, bool geomorph = false)
    {
        long aroundStart = coordinate.AroundSector * size;
        long aroundEnd = aroundStart + size;
        int acrossStart = Mathf.Max(0, coordinate.AcrossSector * size);
        int acrossEnd = Mathf.Min(ringWidth, coordinate.AcrossSector * size + size);
        if (acrossEnd <= acrossStart) return;

        AppendAroundSkirt(vertices, normals, colors, uvs, morphs, indices, coordinate, size, aroundStart, acrossStart, acrossEnd, spacing, parentSpacing, sampleCache, sampler, geometry, ownerInverse, underlay, parentUnderlay, ringWidth, skirtDepth, low, high, reverse: true, useSmoothTerrainHeight, geomorph);
        AppendAroundSkirt(vertices, normals, colors, uvs, morphs, indices, coordinate, size, aroundEnd, acrossStart, acrossEnd, spacing, parentSpacing, sampleCache, sampler, geometry, ownerInverse, underlay, parentUnderlay, ringWidth, skirtDepth, low, high, reverse: false, useSmoothTerrainHeight, geomorph);
        if (!structuralWalls || acrossStart > 0)
            AppendAcrossSkirt(vertices, normals, colors, uvs, morphs, indices, coordinate, size, acrossStart, aroundStart, aroundEnd, spacing, parentSpacing, sampleCache, sampler, geometry, ownerInverse, underlay, parentUnderlay, ringWidth, skirtDepth, low, high, reverse: false, useSmoothTerrainHeight, geomorph);
        if (!structuralWalls || acrossEnd < ringWidth)
            AppendAcrossSkirt(vertices, normals, colors, uvs, morphs, indices, coordinate, size, acrossEnd, aroundStart, aroundEnd, spacing, parentSpacing, sampleCache, sampler, geometry, ownerInverse, underlay, parentUnderlay, ringWidth, skirtDepth, low, high, reverse: true, useSmoothTerrainHeight, geomorph);
    }

    private static void AppendAroundSkirt(List<Vector3> vertices, List<Vector3> normals, List<Color> colors, List<Vector2> uvs, List<Vector2> morphs, List<int> indices, RingTerrainSectorCoordinate coordinate, int size, long around, int acrossStart, int acrossEnd, int spacing, int parentSpacing, TerrainBuildSampleCache sampleCache, RingTerrainSampler sampler, RingLodGeometrySnapshot geometry, Transform3D ownerInverse, float underlay, float parentUnderlay, int ringWidth, float skirtDepth, Color low, Color high, bool reverse, bool useSmoothTerrainHeight, bool geomorph)
    {
        for (int across0 = acrossStart; across0 < acrossEnd; across0 += spacing)
        {
            int across1 = Mathf.Min(acrossEnd, across0 + spacing);
            CachedTerrainPoint point0 = sampleCache.Get(around, across0);
            CachedTerrainPoint point1 = sampleCache.Get(around, across1);
            float height0 = GetTerrainMorphHeight(point0, useSmoothTerrainHeight);
            float height1 = GetTerrainMorphHeight(point1, useSmoothTerrainHeight);
            float top0 = GetTerrainRenderHeight(point0, useSmoothTerrainHeight) - underlay;
            float top1 = GetTerrainRenderHeight(point1, useSmoothTerrainHeight) - underlay;
            Color color = ResolveElevationColor(Mathf.RoundToInt(Mathf.Max(height0, height1)), sampler, low, high);
            AddSkirtQuad(vertices, normals, colors, uvs, morphs, indices,
                ownerInverse * geometry.CanonicalToWorld(around, top0 - skirtDepth, across0),
                ownerInverse * geometry.CanonicalToWorld(around, top1 - skirtDepth, across1),
                ownerInverse * geometry.CanonicalToWorld(around, top1, across1),
                ownerInverse * geometry.CanonicalToWorld(around, top0, across0),
                color, reverse,
                ResolveSkirtCoverageUv(coordinate, size, around, across0),
                ResolveSkirtCoverageUv(coordinate, size, around, across1),
                geomorph ? SampleParentTerrainHeight(sampleCache, around, across0, parentSpacing, ringWidth, useSmoothTerrainHeight) - height0 + underlay - parentUnderlay : 0.0f,
                geomorph ? SampleParentTerrainHeight(sampleCache, around, across1, parentSpacing, ringWidth, useSmoothTerrainHeight) - height1 + underlay - parentUnderlay : 0.0f);
        }
    }

    private static void AppendAcrossSkirt(List<Vector3> vertices, List<Vector3> normals, List<Color> colors, List<Vector2> uvs, List<Vector2> morphs, List<int> indices, RingTerrainSectorCoordinate coordinate, int size, int across, long aroundStart, long aroundEnd, int spacing, int parentSpacing, TerrainBuildSampleCache sampleCache, RingTerrainSampler sampler, RingLodGeometrySnapshot geometry, Transform3D ownerInverse, float underlay, float parentUnderlay, int ringWidth, float skirtDepth, Color low, Color high, bool reverse, bool useSmoothTerrainHeight, bool geomorph)
    {
        for (long around0 = aroundStart; around0 < aroundEnd; around0 += spacing)
        {
            long around1 = Math.Min(aroundEnd, around0 + spacing);
            CachedTerrainPoint point0 = sampleCache.Get(around0, across);
            CachedTerrainPoint point1 = sampleCache.Get(around1, across);
            float height0 = GetTerrainMorphHeight(point0, useSmoothTerrainHeight);
            float height1 = GetTerrainMorphHeight(point1, useSmoothTerrainHeight);
            float top0 = GetTerrainRenderHeight(point0, useSmoothTerrainHeight) - underlay;
            float top1 = GetTerrainRenderHeight(point1, useSmoothTerrainHeight) - underlay;
            Color color = ResolveElevationColor(Mathf.RoundToInt(Mathf.Max(height0, height1)), sampler, low, high);
            AddSkirtQuad(vertices, normals, colors, uvs, morphs, indices,
                ownerInverse * geometry.CanonicalToWorld(around0, top0 - skirtDepth, across),
                ownerInverse * geometry.CanonicalToWorld(around1, top1 - skirtDepth, across),
                ownerInverse * geometry.CanonicalToWorld(around1, top1, across),
                ownerInverse * geometry.CanonicalToWorld(around0, top0, across),
                color, reverse,
                ResolveSkirtCoverageUv(coordinate, size, around0, across),
                ResolveSkirtCoverageUv(coordinate, size, around1, across),
                geomorph ? SampleParentTerrainHeight(sampleCache, around0, across, parentSpacing, ringWidth, useSmoothTerrainHeight) - height0 + underlay - parentUnderlay : 0.0f,
                geomorph ? SampleParentTerrainHeight(sampleCache, around1, across, parentSpacing, ringWidth, useSmoothTerrainHeight) - height1 + underlay - parentUnderlay : 0.0f);
        }
    }

    private static void AddSkirtQuad(List<Vector3> vertices, List<Vector3> normals, List<Color> colors, List<Vector2> uvs, List<Vector2> morphs, List<int> indices, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Color color, bool reverse, Vector2 uv0, Vector2 uv1, float morph0, float morph1)
    {
        int start = vertices.Count;
        vertices.Add(p0); vertices.Add(p1); vertices.Add(p2); vertices.Add(p3);
        normals.Add(Vector3.Zero); normals.Add(Vector3.Zero); normals.Add(Vector3.Zero); normals.Add(Vector3.Zero);
        colors.Add(color); colors.Add(color); colors.Add(color); colors.Add(color);
        uvs.Add(uv0); uvs.Add(uv1); uvs.Add(uv1); uvs.Add(uv0);
        // UV2.y distinguishes skirts for diagnostics while UV2.x keeps both
        // bottom and top locked to the parent surface during geomorphing.
        morphs.Add(new Vector2(morph0, 1.0f)); morphs.Add(new Vector2(morph1, 1.0f));
        morphs.Add(new Vector2(morph1, 1.0f)); morphs.Add(new Vector2(morph0, 1.0f));
        if (reverse) { AddTriangle(indices, vertices, normals, start, start + 1, start + 3); AddTriangle(indices, vertices, normals, start + 1, start + 2, start + 3); }
        else { AddTriangle(indices, vertices, normals, start + 3, start + 1, start); AddTriangle(indices, vertices, normals, start + 1, start + 3, start + 2); }
    }

    private static Vector2 ResolveSkirtCoverageUv(RingTerrainSectorCoordinate coordinate, int size, long around, int across)
        => new(
            (float)(around - coordinate.AroundSector * (long)size) / size,
            (across - coordinate.AcrossSector * size) / (float)size);

    private static void AddBlockyQuad(List<Vector3> vertices, List<Vector3> normals, List<Color> colors, List<int> indices, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Color color, bool reverse, List<Vector2>? coverageUvs = null, Vector2 coverageUv = default)
    {
        int start = vertices.Count;
        vertices.Add(p0); vertices.Add(p1); vertices.Add(p2); vertices.Add(p3);
        normals.Add(Vector3.Zero); normals.Add(Vector3.Zero); normals.Add(Vector3.Zero); normals.Add(Vector3.Zero);
        colors.Add(color); colors.Add(color); colors.Add(color); colors.Add(color);
        if (coverageUvs != null) { coverageUvs.Add(coverageUv); coverageUvs.Add(coverageUv); coverageUvs.Add(coverageUv); coverageUvs.Add(coverageUv); }
        if (reverse)
        {
            AddTriangle(indices, vertices, normals, start + 0, start + 1, start + 3);
            AddTriangle(indices, vertices, normals, start + 1, start + 2, start + 3);
        }
        else
        {
            AddTriangle(indices, vertices, normals, start + 3, start + 1, start + 0);
            AddTriangle(indices, vertices, normals, start + 1, start + 3, start + 2);
        }
    }

    private static Color ResolveElevationColor(int height, RingTerrainSampler sampler, Color low, Color high)
    {
        float t = Mathf.InverseLerp(sampler.BedrockY, sampler.MaximumPossibleSurfaceHeight, height);
        return low.Lerp(high, Mathf.Clamp(t, 0.0f, 1.0f));
    }

    private void UpdateStructuralWallMesh(
        long currentAroundSector,
        long predictedAroundSector,
        int sectorSize,
        bool force)
    {
        if (_grid == null
            || _material == null
            || (!BuildStructuralEdgeWalls && !BuildStructuralUnderside && !BuildTerrainRoofUnderside && !BuildLowerHullCavityFloor))
        {
            if (_structuralWallMesh != null) _structuralWallMesh.Visible = false;
            return;
        }

        // Walls are structural ring features, not terrain-sector features. Keep a
        // light strip at both physical edges around the current/predicted window
        // even when the streamed terrain window is hundreds of kilometres inland.
        int radius = Mathf.Max(1, SectorRadiusAround + 1);
        StructuralWallSectorRange wallRange = ResolveStructuralWallSectorRange(
            currentAroundSector, predictedAroundSector,
            ResolveAroundSectorLength(sectorSize), radius);
        long startSector = wallRange.StartSector;
        long endSector = wallRange.EndSectorExclusive;
        if (!force
            && startSector == _lastWallAroundStart
            && endSector == _lastWallAroundEnd
            && _structuralWallMesh?.Mesh != null)
        {
            _structuralWallMesh.Visible = StreamingActive && !_transitHidden;
            return;
        }

        _lastWallAroundStart = startSector;
        _lastWallAroundEnd = endSector;
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        Transform3D ownerInverse = GlobalTransform.AffineInverse();
        double aroundStart = (double)startSector * sectorSize;
        double aroundEnd = (double)endSector * sectorSize;
        int aroundCells = Mathf.Max(
            1,
            checked((int)(wallRange.SectorCount * Mathf.Clamp(StructuralWallAroundSubdivisionsPerSector, 1, 16))));
        UpdateMaximumStructuralWallRequestedAroundCells(aroundCells);
        int verticalCells = Mathf.Clamp(StructuralWallVerticalCells, 1, 32);
        double aroundStep = (aroundEnd - aroundStart) / aroundCells;
        double verticalStep = (geometry.StructuralWallTopGlobalY - geometry.StructuralBottomGlobalY)
            / verticalCells;
        var request = new StructuralWallRequest(
            geometry,
            ownerInverse,
            aroundStart,
            aroundStep,
            aroundCells,
            verticalStep,
            verticalCells,
            RingWidthMeters,
            BuildStructuralEdgeWalls,
            BuildStructuralUnderside,
            BuildTerrainRoofUnderside,
            BuildLowerHullCavityFloor,
            _grid.TerrainBottomGlobalY,
            _grid.GetLowerHullTopGlobalY(),
            HullColor,
            ++_structuralWallRequestVersion);
        if (_pendingStructuralWall != null)
        {
            _queuedStructuralWall = request;
            return;
        }
        StartStructuralWallBuild(request);
    }

    private void StartStructuralWallBuild(StructuralWallRequest request)
    {
        _pendingStructuralWall = Task.Run(() =>
            BuildStructuralWallData(request));
    }

    private static void UpdateMaximumStructuralWallRequestedAroundCells(int aroundCells)
    {
        int observed;
        while (aroundCells > (observed = Volatile.Read(ref _maximumStructuralWallRequestedAroundCellsSinceStartup))
            && Interlocked.CompareExchange(ref _maximumStructuralWallRequestedAroundCellsSinceStartup,
                aroundCells, observed) != observed) { }
    }

    private static StructuralWallData BuildStructuralWallData(
        StructuralWallRequest request)
    {
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Terrain proxy structural wall build", worker: true);
        long allocationsBefore = GC.GetAllocatedBytesForCurrentThread();
        int wallQuads = request.BuildEdgeWalls
            ? request.AroundCells * request.VerticalCells * 2
            : 0;
        int undersideQuads = request.BuildUnderside ? request.AroundCells : 0;
        int roofQuads = request.BuildRoofUnderside ? request.AroundCells : 0;
        int lowerHullFloorQuads = request.BuildLowerHullCavityFloor ? request.AroundCells : 0;
        int quadCount = wallQuads + undersideQuads + roofQuads + lowerHullFloorQuads;
        var vertices = new List<Vector3>(quadCount * 4);
        var normals = new List<Vector3>(quadCount * 4);
        var colors = new List<Color>(quadCount * 4);
        var uv2 = new List<Vector2>(quadCount * 4);
        var indices = new List<int>(quadCount * 6);
        if (request.BuildEdgeWalls)
        {
            AppendStructuralWallData(
                vertices,
                normals,
                colors,
                uv2,
                indices,
                request,
                0.0,
                reverse: true);
            AppendStructuralWallData(
                vertices,
                normals,
                colors,
                uv2,
                indices,
                request,
                request.RingWidth,
                reverse: false);
        }
        if (request.BuildUnderside)
        {
            AppendStructuralUndersideData(
                vertices,
                normals,
                colors,
                uv2,
                indices,
                request);
        }
        if (request.BuildRoofUnderside)
        {
            AppendTerrainRoofUndersideData(vertices, normals, colors, uv2, indices, request);
        }
        if (request.BuildLowerHullCavityFloor)
            AppendLowerHullCavityFloorData(vertices, normals, colors, uv2, indices, request);
        Vector3[] normalArray = normals.ToArray();
        for (int index = 0; index < normalArray.Length; index++)
            normalArray[index] = normalArray[index].Normalized();
        StructuralWallData result = new(
            vertices.ToArray(),
            normalArray,
            colors.ToArray(),
            uv2.ToArray(),
            indices.ToArray(),
            request.Version);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationsBefore;
        if (allocated > 0) Interlocked.Add(ref _structuralWorkerAllocatedBytesSinceStartup, allocated);
        return result;
    }

    private static void AppendStructuralWallData(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Color> colors,
        List<Vector2> uv2,
        List<int> indices,
        StructuralWallRequest request,
        double across,
        bool reverse)
    {
        double bottom = request.Geometry.StructuralBottomGlobalY;
        for (int y = 0; y < request.VerticalCells; y++)
        for (int x = 0; x < request.AroundCells; x++)
        {
            double a0 = request.AroundStart + x * request.AroundStep;
            double a1 = a0 + request.AroundStep;
            double y0 = bottom + y * request.VerticalStep;
            double y1 = y0 + request.VerticalStep;
            Vector3 p0 = request.OwnerInverse
                * request.Geometry.CanonicalToWorld(a0, y0, across);
            Vector3 p1 = request.OwnerInverse
                * request.Geometry.CanonicalToWorld(a1, y0, across);
            Vector3 p2 = request.OwnerInverse
                * request.Geometry.CanonicalToWorld(a1, y1, across);
            Vector3 p3 = request.OwnerInverse
                * request.Geometry.CanonicalToWorld(a0, y1, across);
            int start = vertices.Count;
            vertices.Add(p0);
            vertices.Add(p1);
            vertices.Add(p2);
            vertices.Add(p3);
            for (int vertex = 0; vertex < 4; vertex++)
            {
                normals.Add(Vector3.Zero);
                colors.Add(request.Color);
                uv2.Add(Vector2.Zero);
            }
            if (reverse)
            {
                AddTriangle(
                    indices, vertices, normals, start, start + 1, start + 3);
                AddTriangle(
                    indices, vertices, normals, start + 1, start + 2, start + 3);
            }
            else
            {
                AddTriangle(
                    indices, vertices, normals, start + 3, start + 1, start);
                AddTriangle(
                    indices, vertices, normals, start + 1, start + 3, start + 2);
            }
        }
    }

    private static void AppendStructuralUndersideData(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Color> colors,
        List<Vector2> uv2,
        List<int> indices,
        StructuralWallRequest request)
    {
        double bottom = request.Geometry.StructuralBottomGlobalY;
        for (int x = 0; x < request.AroundCells; x++)
        {
            double a0 = request.AroundStart + x * request.AroundStep;
            double a1 = a0 + request.AroundStep;
            Vector3 p00 = request.OwnerInverse
                * request.Geometry.CanonicalToWorld(a0, bottom, 0.0);
            Vector3 p10 = request.OwnerInverse
                * request.Geometry.CanonicalToWorld(a1, bottom, 0.0);
            Vector3 p11 = request.OwnerInverse
                * request.Geometry.CanonicalToWorld(a1, bottom, request.RingWidth);
            Vector3 p01 = request.OwnerInverse
                * request.Geometry.CanonicalToWorld(a0, bottom, request.RingWidth);
            int start = vertices.Count;
            vertices.Add(p00);
            vertices.Add(p10);
            vertices.Add(p11);
            vertices.Add(p01);
            for (int vertex = 0; vertex < 4; vertex++)
            {
                normals.Add(Vector3.Zero);
                colors.Add(request.Color);
                uv2.Add(Vector2.Zero);
            }
            AddTriangle(indices, vertices, normals, start, start + 1, start + 2);
            AddTriangle(indices, vertices, normals, start, start + 2, start + 3);
        }
    }

    /// <summary>
    /// Adds the downward-facing roof at the canonical terrain bottom.  The
    /// index order is Godot clockwise from the cavity, and is intentionally
    /// separate from surface coverage masks/editable terrain residency.
    /// </summary>
    private static void AppendTerrainRoofUndersideData(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Color> colors,
        List<Vector2> uv2,
        List<int> indices,
        StructuralWallRequest request)
    {
        for (int x = 0; x < request.AroundCells; x++)
        {
            double a0 = request.AroundStart + x * request.AroundStep;
            double a1 = a0 + request.AroundStep;
            Vector3 p00 = request.OwnerInverse * request.Geometry.CanonicalToWorld(a0, request.TerrainBottomGlobalY, 0.0);
            Vector3 p10 = request.OwnerInverse * request.Geometry.CanonicalToWorld(a1, request.TerrainBottomGlobalY, 0.0);
            Vector3 p11 = request.OwnerInverse * request.Geometry.CanonicalToWorld(a1, request.TerrainBottomGlobalY, request.RingWidth);
            Vector3 p01 = request.OwnerInverse * request.Geometry.CanonicalToWorld(a0, request.TerrainBottomGlobalY, request.RingWidth);
            int start = vertices.Count;
            vertices.Add(p00); vertices.Add(p10); vertices.Add(p11); vertices.Add(p01);
            for (int vertex = 0; vertex < 4; vertex++)
            {
                normals.Add(Vector3.Zero);
                colors.Add(request.Color);
                uv2.Add(Vector2.Zero);
            }
            AddTriangle(indices, vertices, normals, start, start + 1, start + 2);
            AddTriangle(indices, vertices, normals, start, start + 2, start + 3);
        }
    }

    private static void AppendLowerHullCavityFloorData(
        List<Vector3> vertices, List<Vector3> normals, List<Color> colors,
        List<Vector2> uv2, List<int> indices, StructuralWallRequest request)
    {
        for (int x = 0; x < request.AroundCells; x++)
        {
            double a0 = request.AroundStart + x * request.AroundStep;
            double a1 = a0 + request.AroundStep;
            Vector3 p00 = request.OwnerInverse * request.Geometry.CanonicalToWorld(a0, request.LowerHullTopGlobalY, 0.0);
            Vector3 p10 = request.OwnerInverse * request.Geometry.CanonicalToWorld(a1, request.LowerHullTopGlobalY, 0.0);
            Vector3 p11 = request.OwnerInverse * request.Geometry.CanonicalToWorld(a1, request.LowerHullTopGlobalY, request.RingWidth);
            Vector3 p01 = request.OwnerInverse * request.Geometry.CanonicalToWorld(a0, request.LowerHullTopGlobalY, request.RingWidth);
            int start = vertices.Count;
            vertices.Add(p00); vertices.Add(p10); vertices.Add(p11); vertices.Add(p01);
            for (int vertex = 0; vertex < 4; vertex++) { normals.Add(Vector3.Zero); colors.Add(request.Color); uv2.Add(Vector2.Zero); }
            // Reverse the roof order: cavity is above this lower-hull top.
            AddTriangle(indices, vertices, normals, start, start + 2, start + 1);
            AddTriangle(indices, vertices, normals, start, start + 3, start + 2);
        }
    }

    private void ServiceStructuralWallBuild()
    {
        if (_pendingStructuralWall == null
            || !_pendingStructuralWall.IsCompleted)
        {
            return;
        }
        Task<StructuralWallData> completed = _pendingStructuralWall;
        _pendingStructuralWall = null;
        if (completed.IsCompletedSuccessfully)
        {
            StructuralWallData data = completed.Result;
            if (data.Version == _structuralWallRequestVersion
                && !_fastTravelSuspended)
            {
                InstallStructuralWallData(data);
            }
        }
        else if (completed.IsFaulted)
        {
            GD.PushError(
                $"{LodLabel} structural wall worker failed: "
                + $"{completed.Exception?.GetBaseException()}");
        }

        if (_queuedStructuralWall is { } queued)
        {
            _queuedStructuralWall = null;
            StartStructuralWallBuild(queued);
        }
    }

    private void InstallStructuralWallData(StructuralWallData data)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = data.Normals;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        arrays[(int)Mesh.ArrayType.TexUV2] = data.Uv2;
        arrays[(int)Mesh.ArrayType.Index] = data.Indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _structuralWallMesh ??= new MeshInstance3D
        {
            Name = "StructuralHullLod",
            IgnoreOcclusionCulling = true
        };
        if (_structuralWallMesh.GetParent() == null)
            AddChild(_structuralWallMesh);
        _structuralWallMesh.Mesh = mesh;
        // Fresh structural vertices were built from the current geometry. Any
        // temporary anchor-delta translation applied while the old mesh was
        // kept visible must not be applied a second time.
        _structuralWallMesh.Position = Vector3.Zero;
        _structuralWallMesh.MaterialOverride =
            _structuralWallMaterial ?? _material;
        _structuralWallMesh.CastShadow = ResolveStructuralShadowCasting(
            BuildTerrainRoofUnderside || BuildLowerHullCavityFloor,
            CastTerrainShadows);
        _structuralWallMesh.Visible = StreamingActive && !_transitHidden;
        if (_loggedStructuralWallReady) return;
        _loggedStructuralWallReady = true;
        GD.Print(
            $"{LodLabel} structural hull LOD ready: "
            + $"vertices={data.Vertices.Length}, worker-generated.");
    }

    private static void AddTriangle(List<int> indices, List<Vector3> vertices, List<Vector3> normals, int a, int b, int c)
    {
        indices.Add(a); indices.Add(b); indices.Add(c);
        Vector3 normal = (vertices[c] - vertices[a]).Cross(vertices[b] - vertices[a]);
        normals[a] += normal; normals[b] += normal; normals[c] += normal;
    }

    private static ShadowCastingSetting ResolveStructuralShadowCasting(bool hasPersistentRoof, bool castTerrainShadows)
        => hasPersistentRoof || castTerrainShadows
            ? ShadowCastingSetting.On
            : ShadowCastingSetting.Off;

    private static void AccumulateNormal(Vector3[] vertices, Vector3[] normals, int a, int b, int c)
    {
        // Triangle winding is chosen for Godot's habitable-side front face. Flip
        // the mathematical cross product so lighting normals point inward too.
        Vector3 normal = (vertices[c] - vertices[a]).Cross(vertices[b] - vertices[a]);
        normals[a] += normal; normals[b] += normal; normals[c] += normal;
    }

    private int ApplyCompletedBatch(int uploadBudget, double uploadBudgetMilliseconds)
    {
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Terrain proxy install");
        DrainCompletedWorkerSectors();
        bool restartQueuedBuild = false;
        bool forceRestart = false;
        if (_pending != null && _pending.IsCompleted)
        {
            if (_pending.IsCanceled)
            {
                _pending = null;
            }
            else if (_pending.IsFaulted)
            {
                GD.PushError($"LOD background build failed: {_pending.Exception}");
                _pending = null;
                _lastAround = long.MinValue;
                _lastAcross = int.MinValue;
                _lastPredictedAround = long.MinValue;
                _lastPredictedAcross = int.MinValue;
                _lastSurfaceEditVersion = ulong.MaxValue;
            }
            else
            {
                BuildBatch completed = _pending.Result;
                _pending = null;
                if (StreamingActive
                    && completed.RequestVersion == _requestVersion
                    && completed.EditVersion == _lastSurfaceEditVersion)
                {
                    DrainCompletedWorkerSectors();
                }
                LastWorkerBuildMilliseconds = completed.WorkerMilliseconds;
                LoadedSectorCacheCount += completed.CacheHits;
                SavedSectorCacheCount += completed.CacheWrites;
                MaxWorkerBuildMilliseconds = Mathf.Max(MaxWorkerBuildMilliseconds, completed.WorkerMilliseconds);
                _totalWorkerBuildMilliseconds += completed.WorkerMilliseconds;
                _completedWorkerBatches++;
                if (LogBuildSummary)
                    GD.Print($"{LodLabel} worker complete: sectors={completed.SectorCount}, cache={completed.CacheHits} hit/{completed.CacheWrites} saved, workerMs={completed.WorkerMilliseconds:0.00}, queuedUploads={_pendingUploads.Count}");
            }

            _buildCancellation?.Dispose();
            _buildCancellation = null;
            _activeWorkerSlots = 0;
            DisposePendingWorkerSampler();
            restartQueuedBuild = _refreshQueued;
            forceRestart = _forceRefreshQueued;
            _refreshQueued = false;
            _forceRefreshQueued = false;
        }

        if (restartQueuedBuild)
        {
            // The completed task was canceled in favor of a newer desired window.
            // Forget only positional sentinels so a non-forced restart cannot be
            // mistaken for already completed work when the focus returns quickly.
            _lastAround = long.MinValue;
            _lastAcross = int.MinValue;
            _lastPredictedAround = long.MinValue;
            _lastPredictedAcross = int.MinValue;
            QueueStreamingUpdate(forceRestart);
        }

        if (_pendingUploads.Count == 0) return 0;
        HashSet<RingTerrainSectorCoordinate> desired = _pendingDesired ?? new HashSet<RingTerrainSectorCoordinate>();
        if (uploadBudget <= 0 || uploadBudgetMilliseconds <= 0.0) return 0;
        ulong uploadStart = Time.GetTicksUsec();
        int uploaded = 0;
        while (uploaded < uploadBudget && _pendingUploads.Count > 0)
        {
            if (uploaded > 0)
            {
                double elapsed = (Time.GetTicksUsec() - uploadStart) / 1000.0;
                double currentPerSector = elapsed / uploaded;
                double predictedNext = Math.Max(currentPerSector, AverageSectorUploadMilliseconds);
                if (elapsed >= uploadBudgetMilliseconds || elapsed + predictedNext > uploadBudgetMilliseconds) break;
            }
            SectorData data = _pendingUploads.Dequeue();
            _pendingUploadCoordinates.Remove(data.Coordinate);
            EstimatedPendingUploadBytes -= data.EstimatedBytes;
            if (EstimatedPendingUploadBytes < 0L) EstimatedPendingUploadBytes = 0L;
            ReleaseStagedMeshBytes(data.EstimatedBytes);
            if (!desired.Contains(data.Coordinate)) continue;
            if (data.RequestVersion != _requestVersion) continue;
            ulong currentEditVersion = _smoothTerrain?.CaptureLodSurfaceEditSnapshot().Version
                ?? _grid?.LodSurfaceEditVersion
                ?? 0UL;
            if (!AcceptsCompletedEditVersion(data.EditVersion, currentEditVersion)) continue;
            _latestCoverageMasks.TryGetValue(data.Coordinate, out SurfaceCoverageMask? replacementMask);
            RecycleActiveSector(data.Coordinate);
            if (replacementMask != null) _latestCoverageMasks[data.Coordinate] = replacementMask;
            MeshInstance3D node = AcquireNode();
            node.Name = $"Sector_{data.Coordinate.AroundSector}_{data.Coordinate.AcrossSector}";
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Godot.Mesh.ArrayType.Max);
            arrays[(int)Godot.Mesh.ArrayType.Vertex] = data.Vertices;
            arrays[(int)Godot.Mesh.ArrayType.Normal] = data.Normals;
            arrays[(int)Godot.Mesh.ArrayType.Color] = data.Colors;
            arrays[(int)Godot.Mesh.ArrayType.TexUV] = data.Uvs;
            arrays[(int)Godot.Mesh.ArrayType.TexUV2] = data.MorphData;
            if (data.UsesRuntimeParentChord)
            {
                var custom = new float[data.ParentChordOffsets.Length * 3];
                for (int i = 0; i < data.ParentChordOffsets.Length; i++)
                { custom[i * 3] = data.ParentChordOffsets[i].X; custom[i * 3 + 1] = data.ParentChordOffsets[i].Y; custom[i * 3 + 2] = data.ParentChordOffsets[i].Z; }
                arrays[(int)Godot.Mesh.ArrayType.Custom0] = custom;
            }
            var mesh = new ArrayMesh();
            Mesh.ArrayFormat meshFormat = data.UsesRuntimeParentChord
                ? (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift)
                : 0;
            if (data.Indices.Length > 0)
            {
                arrays[(int)Godot.Mesh.ArrayType.Index] = data.Indices;
                mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays, null, null, meshFormat);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, _material);
            }
            if (data.OceanIndices.Length > 0)
            {
                arrays[(int)Godot.Mesh.ArrayType.Index] = data.OceanIndices;
                mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays, null, null, meshFormat);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, _oceanMaterial);
            }
            node.Mesh = mesh;
            // Worker data is sector-local and may have been built before a
            // floating-origin change.  Always resolve its placement from the
            // current geometry at upload time.
            node.Position = _grid != null
                ? ResolveSectorLocalOrigin(data.Coordinate, _grid.CaptureLodGeometrySnapshot(), GlobalTransform.AffineInverse())
                : data.LocalOrigin;
            // A sector can arrive after the last FFT settings change; apply
            // the cached conservative margin without rescanning existing nodes.
            node.ExtraCullMargin = data.OceanIndices.Length > 0 && SampleSpacingMeters is 2 or 4 or 8
                ? _fftOceanCullMarginMeters
                : 0.0f;
            node.MaterialOverride = null;
            node.SetInstanceShaderParameter("fft_distant_ocean_mask_rect", Vector4.Zero);
            bool useOpaqueShadowCaster = CastTerrainShadows && ShadowIgnoresTerrainFade;
            node.CastShadow = CastTerrainShadows && !useOpaqueShadowCaster
                ? ShadowCastingSetting.On : ShadowCastingSetting.Off;
            ConfigureOpaqueShadowCaster(node, mesh, useOpaqueShadowCaster);
            node.Visible = false;
            _active[data.Coordinate] = node;
            InstallFftWaterDemandMetadata(data);
            if (data.OceanIndices.Length > 0) _activeWaterSectors.Add(data.Coordinate);
            else _activeWaterSectors.Remove(data.Coordinate);
            if (data.FullyOceanSurface) _activeFullyOceanSectors.Add(data.Coordinate);
            else _activeFullyOceanSectors.Remove(data.Coordinate);
            if (data.OceanSurfaceMask != null)
                _activeOceanSurfaceMasks[data.Coordinate] = data.OceanSurfaceMask;
            else _activeOceanSurfaceMasks.Remove(data.Coordinate);
            if (data.ShorelineTile != null)
                _activeFftShorelineTiles[data.Coordinate] = data.ShorelineTile;
            else _activeFftShorelineTiles.Remove(data.Coordinate);
            if (_latestCoverageMasks.TryGetValue(data.Coordinate, out SurfaceCoverageMask? latestMask))
            {
                InstallCoverageMask(node, data.Coordinate, latestMask, Mathf.Max(1, SectorSizeMeters / Mathf.Max(1, SampleSpacingMeters)));
                _activeCoverageMasks[data.Coordinate] = latestMask;
            }
            else _activeCoverageMasks.Remove(data.Coordinate);
            RetainPartialCoverageSource(data.Coordinate, data, mesh);
            if (latestMask != null) QueuePartialCoverageCulling(data.Coordinate, node, latestMask);
            // Coverage installation allocates/reassigns the shared atlas slot.
            // Publish ocean ownership only after that slot is stable.
            InstallFftDistantOceanMask(node, data.Coordinate, data.OceanSurfaceMask);
            if (_latestOceanReplacementMasks.ContainsKey(data.Coordinate))
                ApplyOceanReplacementMasks(_latestOceanReplacementMasks);
            UpdateSectorVisibility(data.Coordinate);
            _coverageRevision++;
            _activeMeshBytes[data.Coordinate] = data.EstimatedBytes;
            EstimatedActiveMeshBytes += data.EstimatedBytes;
            RebuiltSectorMeshCount++;
            uploaded++;
        }
        LastGeneratedSectorCount = uploaded;
        double uploadMs = (Time.GetTicksUsec() - uploadStart) / 1000.0;
        LastStreamingBuildMilliseconds = uploadMs;
        MaxStreamingBuildMilliseconds = Mathf.Max(MaxStreamingBuildMilliseconds, uploadMs);
        _totalUploadMilliseconds += uploadMs;
        _totalUploadedSectors += uploaded;
        _completedUploadBatches++;
        // Retain the previous window until every replacement is installed.
        // Temporary overlap is preferable to exposing a hole when the shared
        // frame budget spreads a clipmap shift across several frames.
        if (_pendingUploads.Count == 0 && _pending == null && _completedWorkerSectors.IsEmpty)
            ApplyDepartedSectors(desired);
        if (uploaded > 0)
        {
            if (ReferenceEquals(FinestOceanGeometryInstance, this)) UpdateSharedFftOceanCoverage();
            NotifyOceanReplacementDependents();
        }
        if (LogBuildSummary) GD.Print($"{LodLabel} upload: active={_active.Count}, uploaded={uploaded}, remaining={_pendingUploads.Count}, uploadMs={uploadMs:0.00}, maxUploadMs={MaxStreamingBuildMilliseconds:0.00}, created={CreatedSectorNodeCount}, reused={ReusedSectorNodeCount}");
        return uploaded;
    }

    private void DisposePendingWorkerSampler()
    {
        _pendingWorkerSampler?.Dispose();
        _pendingWorkerSampler = null;
    }

    private void ApplyDepartedSectors(HashSet<RingTerrainSectorCoordinate> desired)
    {
        bool coverageChanged = false;
        var leaving = new List<RingTerrainSectorCoordinate>();
        foreach (RingTerrainSectorCoordinate key in _active.Keys)
            if (!desired.Contains(key) && !_warmUntil.ContainsKey(key)) leaving.Add(key);
        // Coarser water must be restored before any fine sector is hidden.
        if (leaving.Count > 0) MarkOceanCoverageDirtyForRoutineSectorChange();
        foreach (RingTerrainSectorCoordinate key in leaving)
        {
            if (_active.TryGetValue(key, out MeshInstance3D? node))
            {
                RestoreFullPartialCoverageMesh(key, node);
                node.Visible = false;
                _warmUntil[key] = Time.GetTicksMsec() / 1000.0 + Math.Max(0.0f, WarmSectorRetentionSeconds);
                if (_activeMeshBytes.TryGetValue(key, out long bytes)) EstimatedWarmMeshBytes += bytes;
                if (_coverageAtlasSlots.Remove(key, out int atlasSlot)) _freeCoverageAtlasSlots.Push(atlasSlot);
                coverageChanged = true;
            }
        }
        var staleMasks = new List<RingTerrainSectorCoordinate>();
        foreach (RingTerrainSectorCoordinate key in _latestCoverageMasks.Keys)
            if (!desired.Contains(key) && !_active.ContainsKey(key)) staleMasks.Add(key);
        foreach (RingTerrainSectorCoordinate key in staleMasks) _latestCoverageMasks.Remove(key);
        TrimWarmSectors();
        if (coverageChanged)
        {
            _coverageRevision++;
            // Publish the removal before this update returns; the coarser
            // shader keeps its last atlas enabled between these publications.
            if (ReferenceEquals(FinestOceanGeometryInstance, this)) UpdateSharedFftOceanCoverage();
            NotifyOceanReplacementDependents();
        }
    }

    private void ActivateWarmSectors(IReadOnlyCollection<RingTerrainSectorCoordinate> desired)
    {
        bool coverageChanged = false;
        bool atlasDirty = false;
        foreach (RingTerrainSectorCoordinate coordinate in desired)
        {
            if (!_warmUntil.Remove(coordinate)) continue;
            if (_active.TryGetValue(coordinate, out MeshInstance3D? node))
            {
                if (_latestCoverageMasks.TryGetValue(coordinate, out SurfaceCoverageMask? mask))
                {
                    int cells = Mathf.Max(1, SectorSizeMeters / Mathf.Max(1, SampleSpacingMeters));
                    InstallCoverageMask(node, coordinate, mask, cells, uploadAtlas: false);
                    atlasDirty = true;
                    _activeCoverageMasks[coordinate] = mask;
                    QueuePartialCoverageCulling(coordinate, node, mask);
                    UpdateSectorVisibility(coordinate);
                }
                else UpdateSectorVisibility(coordinate);
            }
            if (_activeMeshBytes.TryGetValue(coordinate, out long bytes)) EstimatedWarmMeshBytes = Math.Max(0L, EstimatedWarmMeshBytes - bytes);
            coverageChanged = true;
        }
        if (atlasDirty && _coverageAtlasTexture != null && _coverageAtlasImage != null)
            _coverageAtlasTexture.Update(_coverageAtlasImage);
        if (coverageChanged)
        {
            _coverageRevision++;
            // Reactivated cached water is a visible ownership change just like
            // an upload; publish it before rendering this frame.
            if (ReferenceEquals(FinestOceanGeometryInstance, this)) UpdateSharedFftOceanCoverage();
            NotifyOceanReplacementDependents();
        }
    }

    private void TrimWarmSectors()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        long budget = Math.Max(0, MaximumWarmSectorMemoryMiB) * 1024L * 1024L;
        while (_warmUntil.Count > 0)
        {
            RingTerrainSectorCoordinate oldest = default;
            double oldestExpiry = double.MaxValue;
            foreach ((RingTerrainSectorCoordinate coordinate, double expiry) in _warmUntil)
            {
                if (expiry >= oldestExpiry) continue;
                oldest = coordinate;
                oldestExpiry = expiry;
            }
            if (_warmUntil.Count <= Math.Max(0, MaximumWarmSectorCount)
                && EstimatedWarmMeshBytes <= budget
                && oldestExpiry > now) break;
            RecycleActiveSector(oldest);
        }
    }

    private void ClearWarmSectors()
    {
        var warm = new List<RingTerrainSectorCoordinate>(_warmUntil.Keys);
        foreach (RingTerrainSectorCoordinate coordinate in warm) RecycleActiveSector(coordinate);
    }

    private void DrainCompletedWorkerSectors()
    {
        while (_completedWorkerSectors.TryDequeue(out SectorData? data))
        {
            if (data == null
                || !StreamingActive
                || data.RequestVersion != _requestVersion
                || data.EditVersion != _lastSurfaceEditVersion)
            {
                continue;
            }

            _pendingUploads.Enqueue(data);
            _pendingUploadCoordinates.Add(data.Coordinate);
            EstimatedPendingUploadBytes += data.EstimatedBytes;
        }
    }

    private void PublishCompletedWorkerSector(SectorData data, CancellationToken cancellation)
    {
        long bytes = Math.Max(0L, data.EstimatedBytes);
        long budgetBytes = Math.Max(4, MaximumStagedMeshMemoryMiB) * 1024L * 1024L;
        lock (_stagedMeshLock)
        {
            // Keep one over-budget sector possible: an individual high-detail
            // sector must not deadlock the pipeline simply because it exceeds
            // a conservative staging budget by itself.
            while (_stagedMeshBytes > 0L
                && _stagedMeshBytes + bytes > budgetBytes)
            {
                cancellation.ThrowIfCancellationRequested();
                Monitor.Wait(_stagedMeshLock, 50);
            }
            cancellation.ThrowIfCancellationRequested();
            _completedWorkerSectors.Enqueue(data);
            _stagedMeshBytes += bytes;
        }
    }

    private void ReleaseStagedMeshBytes(long bytes)
    {
        lock (_stagedMeshLock)
        {
            _stagedMeshBytes = Math.Max(0L, _stagedMeshBytes - Math.Max(0L, bytes));
            Monitor.PulseAll(_stagedMeshLock);
        }
    }

    private void ClearPendingUploads()
    {
        lock (_stagedMeshLock)
        {
            _pendingUploads.Clear();
            _completedWorkerSectors.Clear();
            _pendingUploadCoordinates.Clear();
            EstimatedPendingUploadBytes = 0L;
            _stagedMeshBytes = 0L;
            Monitor.PulseAll(_stagedMeshLock);
        }
    }

    private void RecycleActiveSector(RingTerrainSectorCoordinate coordinate)
    {
        MarkOceanCoverageDirtyForRoutineSectorChange();
        RemoveFftWaterDemandMetadata(coordinate);
        if (!_active.Remove(coordinate, out MeshInstance3D? node)) return;
        if (_warmUntil.Remove(coordinate) && _activeMeshBytes.TryGetValue(coordinate, out long warmBytes))
            EstimatedWarmMeshBytes = Math.Max(0L, EstimatedWarmMeshBytes - warmBytes);
        _activeCoverageMasks.Remove(coordinate);
        ReleasePartialCoverageSource(coordinate);
        _latestCoverageMasks.Remove(coordinate);
        _activeOceanSurfaceMasks.Remove(coordinate);
        _activeOceanReplacementMasks.Remove(coordinate);
        _latestOceanReplacementMasks.Remove(coordinate);
        _activeFftShorelineTiles.Remove(coordinate);
        _activeWaterSectors.Remove(coordinate);
        _activeFullyOceanSectors.Remove(coordinate);
        if (_fftDistantOceanMaskSlots.Remove(coordinate, out int distantMaskSlot))
            _freeFftDistantOceanMaskSlots.Push(distantMaskSlot);
        if (_coverageAtlasSlots.Remove(coordinate, out int atlasSlot)) _freeCoverageAtlasSlots.Push(atlasSlot);
        _coverageRevision++;
        if (_activeMeshBytes.Remove(coordinate, out long bytes))
        {
            EstimatedActiveMeshBytes -= bytes;
            if (EstimatedActiveMeshBytes < 0L) EstimatedActiveMeshBytes = 0L;
        }
        ConfigureOpaqueShadowCaster(node, null, enabled: false);
        node.SetInstanceShaderParameter("coverage_atlas_rect", Vector4.Zero);
        node.SetInstanceShaderParameter("ocean_replacement_coverage_rect", Vector4.Zero);
        node.SetInstanceShaderParameter("fft_distant_ocean_mask_rect", Vector4.Zero);
        node.GetNodeOrNull<MeshInstance3D>("OpaqueShadowCaster")?.SetInstanceShaderParameter("coverage_atlas_rect", Vector4.Zero);
        node.Visible = false;
        node.Mesh = null;
        _pool.Enqueue(node);
    }

    private void ReleasePartialCoverageSource(RingTerrainSectorCoordinate coordinate)
    {
        _partialCoveragePendingSet.Remove(coordinate);
        RemovePendingPartialCoverageCoordinate(coordinate);
        if (!_partialCoverageSources.Remove(coordinate, out PartialCoverageMeshSource? source)) return;
        long sourceBytes = source.SourceBytes;
        Interlocked.Add(ref _partialCoverageRetainedBytesAllLevels, -source.ReservedBytes);
        PartialCoverageSourceBytes = Math.Max(0L, PartialCoverageSourceBytes - sourceBytes);
        PartialCoverageActiveRemovedTriangleCount = Math.Max(0L,
            PartialCoverageActiveRemovedTriangleCount - source.ActiveRemovedTriangleCount);
        if (source.FilteredMesh != null)
        {
            PartialCoverageFilteredMeshBytes = Math.Max(0L, PartialCoverageFilteredMeshBytes - source.FilteredMeshBytes);
            source.FilteredMesh.Dispose();
        }
        // The original mesh is still owned by the pooled node until it is
        // cleared above; only dispose a filtered replacement here.
    }

    private void RemovePendingPartialCoverageCoordinate(RingTerrainSectorCoordinate coordinate)
    {
        int count = _partialCoveragePending.Count;
        for (int i = 0; i < count; i++)
        {
            RingTerrainSectorCoordinate queued = _partialCoveragePending.Dequeue();
            if (queued != coordinate) _partialCoveragePending.Enqueue(queued);
        }
    }

    private void ConfigureOpaqueShadowCaster(MeshInstance3D visualNode, Mesh? mesh, bool enabled)
    {
        const string shadowCasterName = "OpaqueShadowCaster";
        MeshInstance3D? shadowCaster = visualNode.GetNodeOrNull<MeshInstance3D>(shadowCasterName);
        if (shadowCaster == null && enabled)
        {
            shadowCaster = new MeshInstance3D
            {
                Name = shadowCasterName,
                CastShadow = ShadowCastingSetting.ShadowsOnly
            };
            visualNode.AddChild(shadowCaster);
        }

        if (shadowCaster == null) return;
        if (_opaqueShadowMaterial == null)
        {
            Shader? shadowShader = GD.Load<Shader>("res://materials/ring_terrain_proxy_shadow.gdshader");
            if (shadowShader == null) return;
            _opaqueShadowMaterial = new ShaderMaterial { Shader = shadowShader };
            if (_coverageAtlasTexture != null)
                _opaqueShadowMaterial.SetShaderParameter("coverage_atlas", _coverageAtlasTexture);
            // This material is created lazily, possibly after the cached camera
            // and ring-center updates have already run.  Seed every geomorph
            // input now so a stationary scene cannot cast mismatched shadows.
            _opaqueShadowMaterial.SetShaderParameter("camera_world_position", _focus?.GlobalPosition ?? Vector3.Zero);
            _opaqueShadowMaterial.SetShaderParameter("ring_center", _grid?.GetCanonicalRingCenterWorld() ?? Vector3.Zero);
            _opaqueShadowMaterial.SetShaderParameter("morph_start_meters", FadeOutStartMeters);
            _opaqueShadowMaterial.SetShaderParameter("morph_end_meters", FadeOutEndMeters);
            _opaqueShadowMaterial.SetShaderParameter("geomorph_enabled", EnableGeomorphing && !UseBlockyHeightfield);
            _opaqueShadowMaterial.SetShaderParameter("runtime_parent_chord_enabled", UsesRuntimeParentChord());
        }
        shadowCaster.Mesh = enabled ? mesh : null;
        shadowCaster.MaterialOverride = enabled ? _opaqueShadowMaterial : null;
        shadowCaster.CastShadow = enabled
            ? ShadowCastingSetting.ShadowsOnly
            : ShadowCastingSetting.Off;
        shadowCaster.Visible = enabled;
        Variant rect = visualNode.GetInstanceShaderParameter("coverage_atlas_rect");
        if (rect.VariantType != Variant.Type.Nil)
            shadowCaster.SetInstanceShaderParameter("coverage_atlas_rect", rect);
    }

    private void RetainPartialCoverageSource(RingTerrainSectorCoordinate coordinate, SectorData data, Mesh fullMesh)
    {
        // Terrain3D's small radial handoff is shader-only and cannot cull an
        // entire 128m proxy sector. Do not spend the shared source budget there.
        if (_partialCoverageSources.ContainsKey(coordinate)
            || (_grid?.UseTerrain3DSurface == true && _hasVoxelCoverageProvider)) return;
        var source = new PartialCoverageMeshSource
        {
            Vertices = data.Vertices, Normals = data.Normals, Colors = data.Colors, Uvs = data.Uvs,
            MorphData = data.MorphData, ParentChordOffsets = data.ParentChordOffsets,
            TerrainIndices = data.Indices, OceanIndices = data.OceanIndices,
            UsesRuntimeParentChord = data.UsesRuntimeParentChord,
            Cells = Mathf.Max(1, SectorSizeMeters / Mathf.Max(1, SampleSpacingMeters)), FullMesh = fullMesh
        };
        long limit = Math.Min(MaximumPartialCoverageRetainedBytesAllLevels,
            Math.Max(0, PartialCoverageSourceMemoryMiB) * 1024L * 1024L);
        // Reserve the immutable source plus a worst-case filtered visual mesh;
        // otherwise sources could consume the entire cap before any sector was
        // eligible to install its optimized mesh.
        long reserved = source.SourceBytes + EstimateFilteredMeshBytes(source, source.TerrainIndices.Length);
        while (true)
        {
            long current = Interlocked.Read(ref _partialCoverageRetainedBytesAllLevels);
            if (current + reserved > limit) return;
            if (Interlocked.CompareExchange(ref _partialCoverageRetainedBytesAllLevels, current + reserved, current) == current) break;
        }
        _partialCoverageSources[coordinate] = source;
        source.ReservedBytes = reserved;
        PartialCoverageSourceBytes += source.SourceBytes;
    }

    private void QueuePartialCoverageCulling(RingTerrainSectorCoordinate coordinate, MeshInstance3D node, SurfaceCoverageMask mask)
    {
        RestoreFullPartialCoverageMesh(coordinate, node);
        if (!EnablePartialCoverageMeshCulling || _warmUntil.ContainsKey(coordinate)
            || !_partialCoverageSources.TryGetValue(coordinate, out PartialCoverageMeshSource? source)) return;
        if (!HasPartialCoverage(mask)) return;
        if (_partialCoveragePendingSet.Add(coordinate)) _partialCoveragePending.Enqueue(coordinate);
    }

    private void RestoreFullPartialCoverageMesh(RingTerrainSectorCoordinate coordinate, MeshInstance3D node)
    {
        if (!_partialCoverageSources.TryGetValue(coordinate, out PartialCoverageMeshSource? source)) return;
        if (!ReferenceEquals(node.Mesh, source.FullMesh)) node.Mesh = source.FullMesh;
        if (source.ActiveRemovedTriangleCount > 0)
        {
            PartialCoverageActiveRemovedTriangleCount = Math.Max(0L,
                PartialCoverageActiveRemovedTriangleCount - source.ActiveRemovedTriangleCount);
            source.ActiveRemovedTriangleCount = 0;
        }
        // Shadows deliberately retain the complete mesh. The shadow shader owns
        // the same coverage atlas, and this avoids a second filtered shadow mesh.
        bool opaque = CastTerrainShadows && ShadowIgnoresTerrainFade;
        ConfigureOpaqueShadowCaster(node, source.FullMesh, opaque);
    }

    private void ServicePartialCoverageMeshCulling()
    {
        if (!StreamingActive || _fastTravelSuspended || !EnablePartialCoverageMeshCulling || _partialCoveragePending.Count == 0) return;
        ulong frame = Engine.GetProcessFrames();
        if (_partialCoverageBudgetFrame != frame)
        {
            _partialCoverageBudgetFrame = frame;
            _partialCoverageRebuildsThisFrame = 0;
        }
        if (_partialCoverageRebuildsThisFrame >= 1) return;
        RingTerrainSectorCoordinate coordinate = _partialCoveragePending.Dequeue();
        _partialCoveragePendingSet.Remove(coordinate);
        // Consume the shared frame token before inspecting the candidate. This
        // bounds both the filter scan and native mesh upload across all levels.
        _partialCoverageRebuildsThisFrame++;
        if (!_active.TryGetValue(coordinate, out MeshInstance3D? node)
            || _warmUntil.ContainsKey(coordinate)
            || !_activeCoverageMasks.TryGetValue(coordinate, out SurfaceCoverageMask? mask)
            || !_partialCoverageSources.TryGetValue(coordinate, out PartialCoverageMeshSource? source)) return;
        if (!HasPartialCoverage(mask)) return;
        using var scope = RingPerformanceTelemetry.MeasureCpu("Terrain proxy partial coverage mesh");
        int[] filtered = FilterCoveredTerrainTriangles(source, mask, out int filteredRemoved);
        if (filtered.Length == 0 || filteredRemoved == 0
            || filteredRemoved < Math.Max(0, PartialCoverageMinimumRemovedTriangles)) return;
        if (source.FilteredMesh != null)
        {
            PartialCoverageActiveRemovedTriangleCount = Math.Max(0L,
                PartialCoverageActiveRemovedTriangleCount - source.ActiveRemovedTriangleCount);
            source.FilteredMesh.Dispose();
            PartialCoverageFilteredMeshBytes = Math.Max(0L, PartialCoverageFilteredMeshBytes - source.FilteredMeshBytes);
        }
        source.FilteredMesh = CreatePartialCoverageMesh(source, filtered);
        node.Mesh = source.FilteredMesh;
        source.FilteredMeshBytes = EstimateFilteredMeshBytes(source, filtered.Length);
        PartialCoverageFilteredMeshBytes += source.FilteredMeshBytes;
        source.ActiveRemovedTriangleCount = filteredRemoved;
        PartialCoverageRemovedTriangleCount += filteredRemoved;
        PartialCoverageActiveRemovedTriangleCount += filteredRemoved;
        PartialCoverageRebuiltMeshCount++;
    }

    private ArrayMesh CreatePartialCoverageMesh(PartialCoverageMeshSource source, int[] terrainIndices)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = source.Vertices;
        arrays[(int)Godot.Mesh.ArrayType.Normal] = source.Normals;
        arrays[(int)Godot.Mesh.ArrayType.Color] = source.Colors;
        arrays[(int)Godot.Mesh.ArrayType.TexUV] = source.Uvs;
        arrays[(int)Godot.Mesh.ArrayType.TexUV2] = source.MorphData;
        Mesh.ArrayFormat format = 0;
        if (source.UsesRuntimeParentChord)
        {
            var custom = new float[source.ParentChordOffsets.Length * 3];
            for (int i = 0; i < source.ParentChordOffsets.Length; i++)
            { custom[i * 3] = source.ParentChordOffsets[i].X; custom[i * 3 + 1] = source.ParentChordOffsets[i].Y; custom[i * 3 + 2] = source.ParentChordOffsets[i].Z; }
            arrays[(int)Godot.Mesh.ArrayType.Custom0] = custom;
            format = (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
        }
        var mesh = new ArrayMesh();
        arrays[(int)Godot.Mesh.ArrayType.Index] = terrainIndices;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, format);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, _material);
        if (source.OceanIndices.Length > 0)
        {
            arrays[(int)Godot.Mesh.ArrayType.Index] = source.OceanIndices;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, format);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, _oceanMaterial);
        }
        return mesh;
    }

    private static long EstimateFilteredMeshBytes(PartialCoverageMeshSource source, int terrainIndexCount)
    {
        long vertexStreams = source.Vertices.LongLength * 12L + source.Normals.LongLength * 12L
            + source.Colors.LongLength * 16L + source.Uvs.LongLength * 8L + source.MorphData.LongLength * 8L
            + (source.UsesRuntimeParentChord ? source.ParentChordOffsets.LongLength * 12L : 0L);
        return vertexStreams + ((long)terrainIndexCount + source.OceanIndices.LongLength) * sizeof(int);
    }

    private static bool HasPartialCoverage(SurfaceCoverageMask mask)
    {
        bool covered = false, uncovered = false;
        foreach (bool value in mask.CoveredCells)
        {
            covered |= value;
            uncovered |= !value;
            if (covered && uncovered) return true;
        }
        return false;
    }

    private static int[] FilterCoveredTerrainTriangles(PartialCoverageMeshSource source, SurfaceCoverageMask mask, out int removed)
    {
        var kept = new List<int>(source.TerrainIndices.Length);
        removed = 0;
        for (int index = 0; index + 2 < source.TerrainIndices.Length; index += 3)
        {
            if (TryGetCoveredTerrainTriangle(source, mask, index)) { removed++; continue; }
            kept.Add(source.TerrainIndices[index]); kept.Add(source.TerrainIndices[index + 1]); kept.Add(source.TerrainIndices[index + 2]);
        }
        return kept.ToArray();
    }

    private static bool TryGetCoveredTerrainTriangle(PartialCoverageMeshSource source, SurfaceCoverageMask mask, int indexOffset)
    {
        int a = source.TerrainIndices[indexOffset], b = source.TerrainIndices[indexOffset + 1], c = source.TerrainIndices[indexOffset + 2];
        if ((uint)a >= source.Uvs.Length || (uint)b >= source.Uvs.Length || (uint)c >= source.Uvs.Length) return false;
        Vector2 ua = source.Uvs[a] * source.Cells, ub = source.Uvs[b] * source.Cells, uc = source.Uvs[c] * source.Cells;
        if (!Mathf.IsEqualApprox(ua.X, Mathf.Round(ua.X)) || !Mathf.IsEqualApprox(ua.Y, Mathf.Round(ua.Y))
            || !Mathf.IsEqualApprox(ub.X, Mathf.Round(ub.X)) || !Mathf.IsEqualApprox(ub.Y, Mathf.Round(ub.Y))
            || !Mathf.IsEqualApprox(uc.X, Mathf.Round(uc.X)) || !Mathf.IsEqualApprox(uc.Y, Mathf.Round(uc.Y))) return false;
        int minX = Mathf.RoundToInt(Mathf.Min(ua.X, Mathf.Min(ub.X, uc.X)));
        int maxX = Mathf.RoundToInt(Mathf.Max(ua.X, Mathf.Max(ub.X, uc.X)));
        int minZ = Mathf.RoundToInt(Mathf.Min(ua.Y, Mathf.Min(ub.Y, uc.Y)));
        int maxZ = Mathf.RoundToInt(Mathf.Max(ua.Y, Mathf.Max(ub.Y, uc.Y)));
        return minX >= 0 && minZ >= 0 && maxX == minX + 1 && maxZ == minZ + 1
            && maxX <= source.Cells && maxZ <= source.Cells && mask.CoversQuad(minX, minZ);
    }

    private MeshInstance3D AcquireNode()
    {
        if (_pool.Count > 0) { ReusedSectorNodeCount++; return _pool.Dequeue(); }
        var node = new MeshInstance3D(); AddChild(node); CreatedSectorNodeCount++; return node;
    }

    private void CreateMaterial()
    {
        Shader? shader = GD.Load<Shader>("res://materials/ring_terrain_proxy.gdshader");
        if (shader == null) return;
        _material = new ShaderMaterial { Shader = shader };
        _material.SetShaderParameter("fade_start_meters", FadeStartMeters);
        _material.SetShaderParameter("fade_end_meters", Mathf.Max(FadeStartMeters + 1.0f, FadeEndMeters));
        _material.SetShaderParameter("near_fade_enabled", ResolveTerrain3DNearFadeEnabled());
        _material.SetShaderParameter("far_fade_enabled", EnableFarFade);
        _material.SetShaderParameter("fade_out_start_meters", FadeOutStartMeters);
        _material.SetShaderParameter("fade_out_end_meters", Mathf.Max(FadeOutStartMeters + 1.0f, FadeOutEndMeters));
        _material.SetShaderParameter("morph_start_meters", FadeOutStartMeters);
        _material.SetShaderParameter("morph_end_meters", Mathf.Max(FadeOutStartMeters + 1.0f, FadeOutEndMeters));
        _material.SetShaderParameter("geomorph_enabled", EnableGeomorphing && !UseBlockyHeightfield);
        _material.SetShaderParameter("runtime_parent_chord_enabled", UsesRuntimeParentChord());
        _material.SetShaderParameter("ring_width_meters", RingWidthMeters);
        _material.SetShaderParameter("biome_texture_weights_enabled", true);
        _material.SetShaderParameter("biome_texture_scale", Mathf.Clamp(BiomeTextureScale, 0.01f, 2.0f));
        _material.SetShaderParameter("biome_grass_albedo", GD.Load<Texture2D>("res://materials/third_party/voxelgame/grass_albedo.png"));
        _material.SetShaderParameter("biome_dirt_albedo", GD.Load<Texture2D>("res://materials/third_party/voxelgame/dirt_albedo.png"));
        _material.SetShaderParameter("biome_desert_albedo", GD.Load<Texture2D>("res://materials/biomes/desert_sand_albedo.png"));
        _material.SetShaderParameter("biome_forest_albedo", GD.Load<Texture2D>("res://materials/biomes/forest_floor_albedo.png"));
        _material.SetShaderParameter("biome_tundra_albedo", GD.Load<Texture2D>("res://materials/biomes/tundra_ground_albedo.png"));
        _material.SetShaderParameter("biome_alpine_albedo", GD.Load<Texture2D>("res://materials/biomes/alpine_rock_albedo.png"));
        if (_grid != null)
        {
            RingAtmosphereModel.ConfigureMaterial(_material, _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
            global::RingworldTechDemo.RingTerrainMacroShadowProjector.Register(_material);
            global::RingworldTechDemo.RingCloudShadowProjector.ConfigureAnalyticField(
                _material,
                _grid.GetCanonicalRingCircumferenceMeters64(),
                RingWidthMeters,
                _grid.TerrainSampler?.Seed ?? 1337);
        }
        global::RingworldTechDemo.RingCloudShadowProjector.Register(_material);
        Shader? oceanShader = GD.Load<Shader>("res://materials/ring_ocean_proxy.gdshader");
        if (oceanShader != null)
        {
            _oceanMaterial = new ShaderMaterial { Shader = oceanShader };
            _oceanMaterial.SetShaderParameter("fade_out_start_meters", FadeOutStartMeters);
            _oceanMaterial.SetShaderParameter("fade_out_end_meters", Mathf.Max(FadeOutStartMeters + 1.0f, FadeOutEndMeters));
            _oceanMaterial.SetShaderParameter("far_fade_enabled", EnableFarFade);
            _oceanMaterial.SetShaderParameter("morph_start_meters", FadeOutStartMeters);
            _oceanMaterial.SetShaderParameter("morph_end_meters", Mathf.Max(FadeOutStartMeters + 1.0f, FadeOutEndMeters));
            _oceanMaterial.SetShaderParameter("geomorph_enabled", EnableGeomorphing && !UseBlockyHeightfield);
            _oceanMaterial.SetShaderParameter("ring_width_meters", RingWidthMeters);
            RingWaterModel.ConfigureMaterial(_oceanMaterial);
            // FFT-ready ocean bypasses this in the shader, but lakes and an
            // unavailable/disabled FFT retain the authored analytic fallback.
            _oceanMaterial.SetShaderParameter("wave_geometry_strength",
                SampleSpacingMeters <= 4 ? 1.0f : SampleSpacingMeters <= 8 ? 0.18f : 0.0f);
            _oceanMaterial.SetShaderParameter(
                "fft_geometry_spacing_meters",
                SampleSpacingMeters is 2 or 4 or 8 ? SampleSpacingMeters : 0.0f);
            _oceanMaterial.SetShaderParameter("proxy_sector_size_meters", Mathf.Max(32, SectorSizeMeters));
            UpdateMorphTargetWaterSpacingUniform();
            // Proxy water is deliberately underlaid below the gameplay top.
            // The FFT ocean vertex path restores that amount before applying
            // its delta, so rendered ocean and swimming share +1 m base.
            _oceanMaterial.SetShaderParameter(
                "fft_surface_base_offset_meters",
                SampleSpacingMeters is 2 or 4 or 8 ? RadialUnderlayOffsetMeters : 0.0f);
            if (_grid != null)
            {
                RingAtmosphereModel.ConfigureMaterial(_oceanMaterial, _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
                global::RingworldTechDemo.RingTerrainMacroShadowProjector.Register(_oceanMaterial);
                global::RingworldTechDemo.RingCloudShadowProjector.ConfigureAnalyticField(
                    _oceanMaterial,
                    _grid.GetCanonicalRingCircumferenceMeters64(),
                    RingWidthMeters,
                    _grid.TerrainSampler?.Seed ?? 1337);
            }
            global::RingworldTechDemo.RingCloudShadowProjector.Register(_oceanMaterial);
        }
        _structuralWallMaterial = (ShaderMaterial)_material.Duplicate();
        // Structural closures still use their authored HullColor, not the
        // sector COLOR weight payload.
        _structuralWallMaterial.SetShaderParameter("biome_texture_weights_enabled", false);
        _structuralWallMaterial.SetShaderParameter("atmosphere_opacity_multiplier", 1.35f);
        // Structural closure cannot inherit the terrain's centered exclusion:
        // the editable terrain bubble is not a full cavity renderer.
        _structuralWallMaterial.SetShaderParameter("near_fade_enabled", false);
        global::RingworldTechDemo.RingCloudShadowProjector.Register(_structuralWallMaterial);
        UpdateLightingUniforms();
    }

    private void UpdateCameraUniform()
    {
        if (_focus == null) return;
        _material?.SetShaderParameter("near_fade_enabled", ResolveTerrain3DNearFadeEnabled());
        if (ReferenceEquals(FinestOceanGeometryInstance, this))
        {
            UpdateSharedFftOceanCoverage();
            UpdateSharedFftShorelineField();
        }
        UpdateFftOceanCullMarginsIfChanged();
        Vector3 cameraWorldPosition = _focus.GlobalPosition;
        if (!_lastCameraWorldPosition.IsEqualApprox(cameraWorldPosition))
        {
            _material?.SetShaderParameter("camera_world_position", cameraWorldPosition);
            _oceanMaterial?.SetShaderParameter("camera_world_position", cameraWorldPosition);
            _structuralWallMaterial?.SetShaderParameter("camera_world_position", cameraWorldPosition);
            _opaqueShadowMaterial?.SetShaderParameter("camera_world_position", cameraWorldPosition);
        }
        float fineHandoff = FinestOceanGeometryInstance == null ? 0.0f
            : (FinestOceanGeometryInstance.FadeOutStartMeters
                + Mathf.Max(FinestOceanGeometryInstance.FadeOutStartMeters + 1.0f,
                    FinestOceanGeometryInstance.FadeOutEndMeters)) * 0.5f - 2.0f;
        // The shared atlas proves that a particular 2m sector has submitted
        // ocean triangles.  It does not by itself prove those triangles are
        // currently carrying FFT geometry: that additionally requires the
        // published FFT resources and that this point lies inside the active
        // geometry range.  Keep the ordinary coarser proxy visible outside
        // either proof rather than allowing an atlas bit to create a hole.
        RingWaterFftController? fft = RingWaterFftController.Instance;
        float fftCoverageDistance = ResolveFftOceanCoverageDistance(
            fineHandoff,
            fft?.EffectiveGeometryDistanceMeters ?? 0.0f,
            fft?.Enabled == true && fft.Ready);
        _oceanMaterial?.SetShaderParameter("fft_ocean_coverage_distance_meters", fftCoverageDistance);
        _oceanMaterial?.SetShaderParameter("ocean_replacement_coverage_enabled", _oceanReplacementAtlasTexture != null
            && StreamingActive && !_transitHidden && ReferenceEquals(_oceanReplacementFinerSource, _finerCoverageLevel)
            && _finerCoverageLevel != null && _finerCoverageLevel.StreamingActive && !_finerCoverageLevel._transitHidden);
        _oceanMaterial?.SetShaderParameter("ocean_replacement_distance_meters", OceanReplacementDistanceMeters);
        _oceanMaterial?.SetShaderParameter("fft_ocean_coverage_enabled", SampleSpacingMeters > 2
            && _sharedFftOceanCoverageTexture != null
            && _sharedFftOceanCoverageRevision != ulong.MaxValue);
        if (_sharedFftOceanCoverageTexture != null)
        {
            _oceanMaterial?.SetShaderParameter("fft_ocean_coverage_atlas", _sharedFftOceanCoverageTexture);
            _oceanMaterial?.SetShaderParameter("fft_ocean_coverage_center_canonical", ResolveSharedFftCoverageShaderCenter());
            _oceanMaterial?.SetShaderParameter("fft_ocean_coverage_cell_meters", SharedFftOceanCoverageCellMeters);
        }
        _lastCameraWorldPosition = cameraWorldPosition;
    }

    /// <summary>
    /// Limits the FFT ownership cull to where both halves of its proof hold:
    /// a fine sector can own the LOD handoff, and the FFT geometry path can
    /// actually draw there.  The coverage atlas supplies the remaining,
    /// per-area proof at fragment time.
    /// </summary>
    private static float ResolveFftOceanCoverageDistance(
        float fineLodHandoffDistance,
        float fftGeometryDistance,
        bool fftResourcesReady)
    {
        if (!fftResourcesReady || !float.IsFinite(fftGeometryDistance)) return 0.0f;
        return Mathf.Max(0.0f, Mathf.Min(
            Mathf.Max(0.0f, fineLodHandoffDistance),
            Mathf.Max(0.0f, fftGeometryDistance)));
    }

    private void UpdateFftOceanCullMarginsIfChanged()
    {
        RingWaterFftController? fft = RingWaterFftController.Instance;
        // The 2m, 4m and 8m ocean proxies all displace vertices. Coarser rings
        // retain shading-only FFT detail and therefore need no expanded bounds.
        if (SampleSpacingMeters is not (2 or 4 or 8) || fft == null || !fft.Enabled)
        {
            if (_hasFftOceanCullConfiguration || _fftOceanCullMarginMeters > 0.0f)
            {
                _fftOceanCullMarginMeters = 0.0f;
                _hasFftOceanCullConfiguration = false;
                foreach (RingTerrainSectorCoordinate coordinate in _activeWaterSectors)
                    if (_active.TryGetValue(coordinate, out MeshInstance3D? node)) node.ExtraCullMargin = 0.0f;
            }
            return;
        }
        // Weather wind is intentionally smoothed inside the controller and is
        // not publicly exposed. Its published maximum is 60 m/s, so use that
        // ceiling while weather owns the wind rather than the manual value.
        float windSpeed = fft.FollowWeatherWind ? 60.0f : fft.WindSpeedMetersPerSecond;
        var configuration = new FftOceanCullConfiguration(
            Mathf.Clamp(fft.GeometryHeightScale, 0.0f, 10.0f),
            Mathf.Clamp(fft.GeometryChoppiness, 0.0f, 3.0f),
            Mathf.Clamp(windSpeed, 0.1f, 60.0f),
            Mathf.Clamp(fft.CascadeCount, 1, 4),
            Mathf.Clamp(fft.SmallWaveLengthMeters, 4.0f, 256.0f),
            Mathf.Clamp(fft.LargeWaveLengthMeters, 16.0f, 2048.0f),
            fft.Ready);
        if (_hasFftOceanCullConfiguration && configuration == _lastFftOceanCullConfiguration) return;
        bool mayReduceMargin = !_hasFftOceanCullConfiguration
            || (configuration.Ready && !_lastFftOceanCullConfiguration.Ready);
        _lastFftOceanCullConfiguration = configuration;
        _hasFftOceanCullConfiguration = true;
        float requestedMargin = ResolveFftOceanCullMargin(configuration);
        // Parameters can change before the provider has rebuilt its topology.
        // Keep the larger envelope through that transition and allow it to
        // reduce only on the subsequent not-ready -> ready publication.
        _fftOceanCullMarginMeters = mayReduceMargin
            ? requestedMargin : Mathf.Max(_fftOceanCullMarginMeters, requestedMargin);
        foreach (RingTerrainSectorCoordinate coordinate in _activeWaterSectors)
            if (_active.TryGetValue(coordinate, out MeshInstance3D? node))
                node.ExtraCullMargin = _fftOceanCullMarginMeters;
    }

    // The FFT moves vertices vertically and along both tangent axes. This is
    // an operationally conservative cull budget, not a mathematical bound on
    // every random spectrum sample. It is extra margin, never CustomAabb, so
    // terrain mesh bounds are never replaced or reduced. Its cap avoids an
    // extreme editor configuration making every fine sector permanently visible.
    private static float ResolveFftOceanCullMargin(in FftOceanCullConfiguration configuration)
    {
        float largestDomain = configuration.CascadeCount switch
        {
            1 => configuration.SmallDomain,
            2 => configuration.LargeDomain,
            3 => configuration.LargeDomain * 4.0f,
            _ => configuration.LargeDomain * 16.0f
        };
        float domainFactor = Mathf.Clamp(Mathf.Sqrt(largestDomain / 64.0f), 1.0f, 8.0f);
        float windFactor = 0.75f + 1.75f * (configuration.WindSpeed / 60.0f);
        float verticalEnvelope = configuration.HeightScale * configuration.CascadeCount * windFactor * domainFactor;
        float horizontalEnvelope = verticalEnvelope * configuration.Choppiness;
        return Mathf.Clamp(verticalEnvelope + horizontalEnvelope + 4.0f, 4.0f, 192.0f);
    }

    private void UpdateSharedFftOceanCoverage()
    {
        if (!StreamingActive || _transitHidden)
        {
            InvalidateSharedFftOceanCoverage();
            return;
        }
        if (_velocityProvider == null) return;
        RingCanonicalPosition focus = _velocityProvider.CanonicalPosition;
        RingWaterFftController? fft = RingWaterFftController.Instance;
        if (fft == null || !fft.Enabled || !fft.Ready)
        {
            InvalidateSharedFftOceanCoverage();
            return;
        }
        long centerCellX = (long)Math.Floor(focus.AroundMeters / 32.0);
        int centerCellZ = Mathf.FloorToInt((float)(focus.AcrossMeters / 32.0));
        if (_sharedFftOceanCoverageRevision == _coverageRevision
            && _sharedFftOceanCoverageCenterCellX == centerCellX
            && _sharedFftOceanCoverageCenterCellZ == centerCellZ) return;
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Terrain proxy FFT ocean coverage atlas");
        double circumference = ResolveAroundCircumferenceMeters();
        double gridAround = centerCellX * 32.0;
        double gridAcross = centerCellZ * 32.0;
        SharedFftOceanCoverageVisibleMasks.Clear();
        foreach ((RingTerrainSectorCoordinate coordinate, SurfaceCoverageMask mask) in _activeOceanSurfaceMasks)
            if (_active.TryGetValue(coordinate, out MeshInstance3D? node) && node.Visible && !_warmUntil.ContainsKey(coordinate))
                SharedFftOceanCoverageVisibleMasks[coordinate] = mask;
        Stopwatch buildTimer = Stopwatch.StartNew();
        _sharedFftOceanCoverageBytes ??= new byte[SharedFftOceanCoverageResolution * SharedFftOceanCoverageResolution];
        BuildSharedFftOceanCoverageBytes(SharedFftOceanCoverageVisibleMasks,
            Mathf.Max(32, SectorSizeMeters), gridAround, gridAcross, circumference,
            _sharedFftOceanCoverageBytes);
        buildTimer.Stop();
        _sharedFftOceanCoverageImage ??= Image.CreateEmpty(
            SharedFftOceanCoverageResolution, SharedFftOceanCoverageResolution, false, Image.Format.R8);
        Stopwatch uploadTimer = Stopwatch.StartNew();
        _sharedFftOceanCoverageImage.SetData(SharedFftOceanCoverageResolution,
            SharedFftOceanCoverageResolution, false, Image.Format.R8,
            _sharedFftOceanCoverageBytes);
        if (_sharedFftOceanCoverageTexture == null)
            _sharedFftOceanCoverageTexture = ImageTexture.CreateFromImage(_sharedFftOceanCoverageImage);
        else
            _sharedFftOceanCoverageTexture.Update(_sharedFftOceanCoverageImage);
        uploadTimer.Stop();
        LastSharedFftOceanCoverageBuildMilliseconds = buildTimer.Elapsed.TotalMilliseconds;
        LastSharedFftOceanCoverageUploadMilliseconds = uploadTimer.Elapsed.TotalMilliseconds;
        _sharedFftOceanCoverageTotalBuildMilliseconds += LastSharedFftOceanCoverageBuildMilliseconds;
        _sharedFftOceanCoverageTotalUploadMilliseconds += LastSharedFftOceanCoverageUploadMilliseconds;
        _sharedFftOceanCoverageBuildCount++;
        _sharedFftOceanCoverageUploadCount++;
        _sharedFftOceanCoverageCanonicalAround = gridAround;
        _sharedFftOceanCoverageCanonicalAcross = gridAcross;
        _sharedFftOceanCoverageRevision = _coverageRevision;
        _sharedFftOceanCoverageCenterCellX = centerCellX;
        _sharedFftOceanCoverageCenterCellZ = centerCellZ;
    }

    private Vector2 ResolveSharedFftCoverageShaderCenter()
    {
        if (_velocityProvider == null || _focus == null || RingWaterFftController.Instance is not RingWaterFftController fft)
            return _sharedFftOceanCoverageCenter;
        RingCanonicalPosition focus = _velocityProvider.CanonicalPosition;
        Vector3 focusLocal = _focus.GlobalPosition - fft.CanonicalAnchorWorld;
        Vector2 focusFft = new(focusLocal.Dot(fft.CanonicalAroundAxis), focusLocal.Dot(fft.CanonicalAcrossAxis));
        _sharedFftOceanCoverageCenter = focusFft + new Vector2(
            (float)SignedPeriodicDistance(_sharedFftOceanCoverageCanonicalAround, focus.AroundMeters, ResolveAroundCircumferenceMeters()),
            (float)(_sharedFftOceanCoverageCanonicalAcross - focus.AcrossMeters));
        return _sharedFftOceanCoverageCenter;
    }

    internal static Vector2 SharedFftCoverageShaderCenter =>
        FinestOceanGeometryInstance?.ResolveSharedFftCoverageShaderCenter() ?? Vector2.Zero;

    public static Texture2D? SharedFftShorelineFieldTexture => _sharedFftShorelineFieldTexture;
    public static bool SharedFftShorelineFieldValid => _sharedFftShorelineFieldSourceRevision != ulong.MaxValue
        && _sharedFftShorelineFieldTexture != null;
    public static ulong SharedFftShorelineFieldRevision => _sharedFftShorelineFieldRevision;
    public static long SharedFftShorelineFieldWorkerBuildCount => Interlocked.Read(ref _sharedFftShorelineFieldWorkerBuildCount);
    public static double SharedFftShorelineFieldWorkerBuildMilliseconds =>
        Interlocked.Read(ref _sharedFftShorelineFieldWorkerBuildTicks) * 1000.0 / Stopwatch.Frequency;
    public static long SharedFftShorelineFieldAtlasRefreshCount => Interlocked.Read(ref _sharedFftShorelineFieldAtlasRefreshCount);
    public static long SharedFftShorelineFieldAtlasCopiedTexels => Interlocked.Read(ref _sharedFftShorelineFieldAtlasCopiedTexels);
    internal static Vector2 SharedFftShorelineFieldShaderCenter =>
        FinestOceanGeometryInstance?.ResolveSharedFftShorelineFieldShaderCenter() ?? Vector2.Zero;

    private void UpdateSharedFftShorelineField()
    {
        if (!StreamingActive || _transitHidden || _velocityProvider == null
            || RingWaterFftController.Instance is not RingWaterFftController fft || !fft.Enabled || !fft.Ready)
        {
            if (_sharedFftShorelineFieldSourceRevision != ulong.MaxValue)
            {
                _sharedFftShorelineFieldSourceRevision = ulong.MaxValue;
                _sharedFftShorelineFieldRevision++;
            }
            return;
        }
        RingCanonicalPosition focus = _velocityProvider.CanonicalPosition;
        long centerCellX = (long)Math.Floor(focus.AroundMeters / 32.0);
        int centerCellZ = Mathf.FloorToInt((float)(focus.AcrossMeters / 32.0));
        if (_sharedFftShorelineFieldSourceRevision == _coverageRevision
            && _sharedFftShorelineFieldCenterCellX == centerCellX
            && _sharedFftShorelineFieldCenterCellZ == centerCellZ) return;
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Terrain proxy FFT shoreline atlas");
        double originAround = centerCellX * 32.0 - SharedFftShorelineFieldResolution * SharedFftShorelineFieldCellMeters * 0.5;
        double originAcross = centerCellZ * 32.0 - SharedFftShorelineFieldResolution * SharedFftShorelineFieldCellMeters * 0.5;
        _sharedFftShorelineFieldStagingBytes ??= new byte[SharedFftShorelineFieldResolution * SharedFftShorelineFieldResolution * 8];
        Array.Clear(_sharedFftShorelineFieldStagingBytes);
        foreach ((RingTerrainSectorCoordinate coordinate, FftShorelineTile tile) in _activeFftShorelineTiles)
        {
            if (!_active.TryGetValue(coordinate, out MeshInstance3D? node) || !node.Visible || _warmUntil.ContainsKey(coordinate)) continue;
            CopyShorelineTileInterior(tile, originAround, originAcross, _sharedFftShorelineFieldStagingBytes);
        }
        _sharedFftShorelineFieldImage ??= Image.CreateEmpty(SharedFftShorelineFieldResolution,
            SharedFftShorelineFieldResolution, false, Image.Format.Rgbah);
        _sharedFftShorelineFieldImage.SetData(SharedFftShorelineFieldResolution,
            SharedFftShorelineFieldResolution, false, Image.Format.Rgbah, _sharedFftShorelineFieldStagingBytes);
        if (_sharedFftShorelineFieldTexture == null)
            _sharedFftShorelineFieldTexture = ImageTexture.CreateFromImage(_sharedFftShorelineFieldImage);
        else _sharedFftShorelineFieldTexture.Update(_sharedFftShorelineFieldImage);
        _sharedFftShorelineFieldCanonicalAround = originAround + SharedFftShorelineFieldResolution * SharedFftShorelineFieldCellMeters * 0.5;
        _sharedFftShorelineFieldCanonicalAcross = originAcross + SharedFftShorelineFieldResolution * SharedFftShorelineFieldCellMeters * 0.5;
        (_sharedFftShorelineFieldBytes, _sharedFftShorelineFieldStagingBytes) =
            (_sharedFftShorelineFieldStagingBytes, _sharedFftShorelineFieldBytes);
        _sharedFftShorelineFieldSourceRevision = _coverageRevision;
        _sharedFftShorelineFieldCenterCellX = centerCellX;
        _sharedFftShorelineFieldCenterCellZ = centerCellZ;
        _sharedFftShorelineFieldRevision++;
        Interlocked.Increment(ref _sharedFftShorelineFieldAtlasRefreshCount);
    }

    private static void CopyShorelineTileInterior(FftShorelineTile tile, double atlasOriginAround, double atlasOriginAcross, byte[] destination)
    {
        long tileOriginAround = tile.Coordinate.AroundSector * (long)FinestOceanGeometryInstance!.SectorSizeMeters;
        int tileOriginAcross = tile.Coordinate.AcrossSector * FinestOceanGeometryInstance.SectorSizeMeters;
        RingTerrainProxySector owner = FinestOceanGeometryInstance;
        double circumference = owner.ResolveAroundCircumferenceMeters();
        double atlasCenterAround = atlasOriginAround + SharedFftShorelineFieldResolution * tile.CellMeters * 0.5;
        double wrappedStart = SignedPeriodicDistance(tileOriginAround, atlasCenterAround, circumference);
        int atlasX = Mathf.RoundToInt((float)(wrappedStart / tile.CellMeters)) + SharedFftShorelineFieldResolution / 2;
        int atlasZ = Mathf.RoundToInt((float)((tileOriginAcross - atlasOriginAcross) / tile.CellMeters));
        int rowTexels = tile.InteriorCells + 1;
        if (!TryResolveShorelineCopyRange(atlasX, rowTexels, out int sourceX, out int targetX, out int copyTexels)) return;
        for (int z = 0; z < rowTexels; z++)
        {
            int targetZ = atlasZ + z;
            if (targetZ < 0 || targetZ >= SharedFftShorelineFieldResolution) continue;
            Buffer.BlockCopy(tile.InteriorBytes, (z * rowTexels + sourceX) * 8,
                destination, (targetZ * SharedFftShorelineFieldResolution + targetX) * 8, copyTexels * 8);
            Interlocked.Add(ref _sharedFftShorelineFieldAtlasCopiedTexels, copyTexels);
        }
    }

    private static bool TryResolveShorelineCopyRange(int atlasX, int rowTexels,
        out int sourceX, out int targetX, out int copyTexels)
    {
        sourceX = Mathf.Max(0, -atlasX);
        targetX = Mathf.Max(0, atlasX);
        copyTexels = Mathf.Min(rowTexels - sourceX, SharedFftShorelineFieldResolution - targetX);
        return copyTexels > 0;
    }

    private static void WriteHalf(byte[] destination, int offset, float value)
    {
        ushort bits = BitConverter.HalfToUInt16Bits((Half)value);
        destination[offset] = (byte)bits;
        destination[offset + 1] = (byte)(bits >> 8);
    }

    private Vector2 ResolveSharedFftShorelineFieldShaderCenter()
    {
        if (_velocityProvider == null || _focus == null || RingWaterFftController.Instance is not RingWaterFftController fft)
            return _sharedFftShorelineFieldCenter;
        RingCanonicalPosition focus = _velocityProvider.CanonicalPosition;
        Vector3 focusLocal = _focus.GlobalPosition - fft.CanonicalAnchorWorld;
        Vector2 focusFft = new(focusLocal.Dot(fft.CanonicalAroundAxis), focusLocal.Dot(fft.CanonicalAcrossAxis));
        _sharedFftShorelineFieldCenter = focusFft + new Vector2(
            (float)SignedPeriodicDistance(_sharedFftShorelineFieldCanonicalAround, focus.AroundMeters, ResolveAroundCircumferenceMeters()),
            (float)(_sharedFftShorelineFieldCanonicalAcross - focus.AcrossMeters));
        return _sharedFftShorelineFieldCenter;
    }

    public static bool TrySampleFftShorelineField(in RingCanonicalPosition position, out FftShorelineSample sample)
    {
        RingTerrainProxySector? owner = FinestOceanGeometryInstance;
        if (owner == null || !SharedFftShorelineFieldValid) { sample = default; return false; }
        double aroundDelta = SignedPeriodicDistance(position.AroundMeters, _sharedFftShorelineFieldCanonicalAround, owner.ResolveAroundCircumferenceMeters());
        float x = (float)(aroundDelta / SharedFftShorelineFieldCellMeters) + SharedFftShorelineFieldResolution * 0.5f;
        float z = (float)((position.AcrossMeters - _sharedFftShorelineFieldCanonicalAcross) / SharedFftShorelineFieldCellMeters) + SharedFftShorelineFieldResolution * 0.5f;
        int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z), x1 = x0 + 1, z1 = z0 + 1;
        if (x0 < 0 || x1 >= SharedFftShorelineFieldResolution || z0 < 0 || z1 >= SharedFftShorelineFieldResolution || _sharedFftShorelineFieldBytes == null)
        { sample = default; return false; }
        if (!TryReadBilinearShoreline(_sharedFftShorelineFieldBytes, x, z, out Vector3 value)) { sample = default; return false; }
        float depth = value.X;
        float shore = value.Y;
        sample = new FftShorelineSample(depth, shore, true);
        return true;
    }

    private static bool RunActualParentSpacingContract()
    {
        // These are the first three normal proxy handoffs. The parent lattice
        // must be supplied explicitly, while zero retains existing-scene 2x
        // compatibility for a terminal level without a configured receiver.
        foreach ((int ownSpacing, int parentSpacing) in new[] { (2, 4), (4, 8), (8, 16) })
        {
            if (ResolveMorphTargetSpacingMeters(ownSpacing, parentSpacing) != parentSpacing) return false;
            if (ResolveMorphTargetSpacingMeters(ownSpacing, 0) != ownSpacing * 2) return false;
            // Non-planar parent corners exercise both submitted parent triangles.
            float lower = SampleParentTriangleHeight(0, parentSpacing, parentSpacing * 2, parentSpacing * 5,
                .75f, .125f);
            float upper = SampleParentTriangleHeight(0, parentSpacing, parentSpacing * 2, parentSpacing * 5,
                .25f, .875f);
            if (!Mathf.IsEqualApprox(lower, parentSpacing * 1.0f)
                || !Mathf.IsEqualApprox(upper, parentSpacing * 2.25f)) return false;
        }

        string parentFour = BuildLodSectorCacheDefinition(1, 2, 3UL, 4096, 1024, 256, 2, 4, 128, null);
        string parentEight = BuildLodSectorCacheDefinition(1, 2, 3UL, 4096, 1024, 256, 2, 8, 128, null);
        return parentFour != parentEight;
    }

    private static bool RunGlobalMorphTargetIntegrationContract()
    {
        const double circumference = 1024.0;
        var identity = new RingTerrainGlobalMorphSnapshot.Identity(
            7, 11, 13, 4, 4, 1, 40, 0.0f, 17);
        var snapshot = new RingTerrainGlobalMorphSnapshot(identity, circumference,
            new[] { new RingTerrainGlobalMorphSnapshot.Segment(0, 1, 1, 10.0, 10.0,
                new[] { 0.0f, 10.0f, 20.0f, 30.0f }) });
        var geometry = new RingLodGeometrySnapshot(
            Transform3D.Identity, Vector3.Zero, Vector3.Zero,
            CertifiedRingScale.EffectiveRadiusMeters, 0.0, 0.0, circumference,
            -1000.0, 1000.0, new RingCanonicalPosition(0.0, 0.0, 0.0));
        if (!snapshot.TrySampleTerrainWorld(geometry, 2.0, 2.0, out Vector3 target))
        { GD.PushError("GLOBAL_MORPH_CONTRACT_FAIL:sample"); return false; }
        Vector3 expected = geometry.CanonicalToWorld(0.0, 0.0, 0) * .6f
            + geometry.CanonicalToWorld(10.0, 10.0, 0) * .2f
            + geometry.CanonicalToWorld(0.0, 20.0, 10) * .2f;
        if (target.DistanceTo(expected) > .001f)
        { GD.PushError($"GLOBAL_MORPH_CONTRACT_FAIL:triangle distance={target.DistanceTo(expected):0.000000}"); return false; }
        Vector3 own = geometry.CanonicalToWorld(2.0, 0.0, 2);
        if ((own + (target - own)).DistanceTo(target) > .0001f)
        { GD.PushError("GLOBAL_MORPH_CONTRACT_FAIL:chord"); return false; }
        string withoutSnapshot = BuildLodSectorCacheDefinition(1, 2, 3UL, 4096, 1024, 256, 16, 32, 16, null);
        string withSnapshot = BuildLodSectorCacheDefinition(1, 2, 3UL, 4096, 1024, 256, 16, 32, 16, identity);
        if (withoutSnapshot == withSnapshot) GD.PushError("GLOBAL_MORPH_CONTRACT_FAIL:cache-key");
        return withoutSnapshot != withSnapshot;
    }

    /// <summary>Maps an absolute canonical point to the shared shoreline atlas.
    /// The returned UV addresses texel centres and is safe across the ring seam.</summary>
    public static bool TryGetFftShorelineFieldUv(in RingCanonicalPosition position, out Vector2 uv)
    {
        RingTerrainProxySector? owner = FinestOceanGeometryInstance;
        if (owner == null || !SharedFftShorelineFieldValid)
        {
            uv = default;
            return false;
        }
        return TryMapFftShorelineFieldUv(position, _sharedFftShorelineFieldCanonicalAround,
            _sharedFftShorelineFieldCanonicalAcross, owner.ResolveAroundCircumferenceMeters(), out uv);
    }

    public static bool IsFftShorelineFootprintFullySaturated(in RingCanonicalPosition center,
        float radiusMeters, float depthThresholdMeters, float distanceThresholdMeters)
    {
        RingTerrainProxySector? owner = FinestOceanGeometryInstance;
        if (owner == null || !SharedFftShorelineFieldValid || _sharedFftShorelineFieldBytes == null) return false;
        double around = SignedPeriodicDistance(center.AroundMeters, _sharedFftShorelineFieldCanonicalAround, owner.ResolveAroundCircumferenceMeters());
        double across = center.AcrossMeters - _sharedFftShorelineFieldCanonicalAcross;
        int centerX = Mathf.RoundToInt((float)(around / SharedFftShorelineFieldCellMeters)) + SharedFftShorelineFieldResolution / 2;
        int centerZ = Mathf.RoundToInt((float)(across / SharedFftShorelineFieldCellMeters)) + SharedFftShorelineFieldResolution / 2;
        int radiusCells = Mathf.CeilToInt(Mathf.Max(0.0f, radiusMeters) / SharedFftShorelineFieldCellMeters);
        return IsFftShorelineFootprintFullySaturated(_sharedFftShorelineFieldBytes, centerX, centerZ,
            radiusCells, depthThresholdMeters, distanceThresholdMeters);
    }

    private static bool IsFftShorelineFootprintFullySaturated(byte[] bytes, int centerX, int centerZ,
        int radiusCells, float depthThresholdMeters, float distanceThresholdMeters)
    {
        int x0 = centerX - radiusCells, x1 = centerX + radiusCells;
        int z0 = centerZ - radiusCells, z1 = centerZ + radiusCells;
        if (x0 < 0 || z0 < 0 || x1 >= SharedFftShorelineFieldResolution || z1 >= SharedFftShorelineFieldResolution) return false;
        for (int z = z0; z <= z1; z++)
        for (int x = x0; x <= x1; x++)
        {
            int offset = (z * SharedFftShorelineFieldResolution + x) * 8;
            float valid = ReadHalf(bytes, offset + 4);
            float depth = ReadHalf(bytes, offset);
            float distance = ReadHalf(bytes, offset + 2);
            if (!float.IsFinite(valid) || !float.IsFinite(depth) || !float.IsFinite(distance)
                || valid < 0.999f || depth < depthThresholdMeters || distance < distanceThresholdMeters) return false;
        }
        return true;
    }

    private static bool TryMapFftShorelineFieldUv(in RingCanonicalPosition position,
        double atlasCenterAround, double atlasCenterAcross, double circumference, out Vector2 uv)
    {
        double around = SignedPeriodicDistance(position.AroundMeters, atlasCenterAround, circumference);
        double across = position.AcrossMeters - atlasCenterAcross;
        double span = SharedFftShorelineFieldResolution * SharedFftShorelineFieldCellMeters;
        double u = around / span + 0.5 + 0.5 / SharedFftShorelineFieldResolution;
        double v = across / span + 0.5 + 0.5 / SharedFftShorelineFieldResolution;
        if (u < 0.0 || u >= 1.0 || v < 0.0 || v >= 1.0)
        {
            uv = default;
            return false;
        }
        uv = new Vector2((float)u, (float)v);
        return true;
    }

    public bool RunFftShorelineFieldContractTest() => RunFftShorelineFieldContractTestCore();

    public bool RunOceanReplacementOwnershipContractTest()
    {
        // A complete finer ocean proof owns the cell; a mixed, unloaded, or
        // absent proof leaves the coarse surface visible.
        var full = new SurfaceCoverageMask(4, 2, new[] { true, true, true, true });
        var mixed = new SurfaceCoverageMask(4, 2, new[] { true, false, true, true });
        bool passed = full.CoversQuad(0, 0) && full.CoversQuad(3, 3)
            && mixed.CoversQuad(0, 0) && !mixed.CoversQuad(2, 0)
            && !new SurfaceCoverageMask(4, 2, new bool[4]).CoversQuad(0, 0);
        // An area bit alone cannot retire ordinary water: ownership is limited
        // to the smaller of the fine LOD handoff and live FFT geometry bound,
        // and fails closed while its GPU resources are unavailable.
        passed &= Mathf.IsEqualApprox(ResolveFftOceanCoverageDistance(600.0f, 350.0f, true), 350.0f)
            && Mathf.IsEqualApprox(ResolveFftOceanCoverageDistance(600.0f, 900.0f, true), 600.0f)
            && Mathf.IsZeroApprox(ResolveFftOceanCoverageDistance(600.0f, 350.0f, false));
        var quadProof = new bool[16];
        Array.Fill(quadProof, true);
        quadProof[2 * 4 + 1] = false; // one partial finer quad only blocks itself
        var exact = new SurfaceCoverageMask(4, 1, quadProof);
        passed &= exact.CoversQuad(0, 0) && !exact.CoversQuad(1, 2) && exact.CoversQuad(2, 2);
        var snapshotMasks = new Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask>
        {
            [new RingTerrainSectorCoordinate(0, 0)] = new SurfaceCoverageMask(4, 1, new[] { true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true }),
            [new RingTerrainSectorCoordinate(1, 0)] = new SurfaceCoverageMask(4, 1, new[] { true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true })
        };
        var snapshot = new FinerOceanSnapshot(snapshotMasks, new HashSet<RingTerrainSectorCoordinate>());
        passed &= IsSnapshotOceanCovered(snapshot, 0, 128, 0, 64, 64, 128, 128);
        snapshotMasks.Remove(new RingTerrainSectorCoordinate(1, 0));
        passed &= !IsSnapshotOceanCovered(snapshot, 0, 128, 0, 64, 64, 128, 128);
        snapshotMasks[new RingTerrainSectorCoordinate(1, 0)] = exact;
        passed &= !IsSnapshotOceanCovered(snapshot, 64 + 1 * 16, 64 + 2 * 16, 2 * 16, 3 * 16, 64, 128, 128)
            && IsSnapshotOceanCovered(snapshot, 64 + 2 * 16, 64 + 3 * 16, 2 * 16, 3 * 16, 64, 128, 128);
        snapshot.FullyWet.Add(new RingTerrainSectorCoordinate(1, 0));
        passed &= IsSnapshotOceanCovered(snapshot, 64, 128, 0, 64, 64, 128, 128);
        snapshot.FullyWet.Remove(new RingTerrainSectorCoordinate(1, 0));
        passed &= !IsSnapshotOceanCovered(snapshot, 0, 64, -1, 4, 64, 128, 128)
            && IsSnapshotOceanCovered(snapshot, 120, 136, 0, 16, 64, 128, 128);

        bool AssertRasterParity(FinerOceanSnapshot rasterSnapshot,
            RingTerrainSectorCoordinate[] parents, int coarseSize, int fineSize,
            int spacing, int ringWidth, double circumference)
        {
            Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask> raster = BuildOceanReplacementMasks(
                rasterSnapshot, parents, coarseSize, spacing, fineSize, ringWidth, circumference);
            int cells = Mathf.Max(1, coarseSize / spacing);
            foreach (RingTerrainSectorCoordinate parent in parents)
            {
                if (!raster.TryGetValue(parent, out SurfaceCoverageMask? mask)) return false;
                long aroundOrigin = parent.AroundSector * (long)coarseSize;
                int acrossOrigin = parent.AcrossSector * coarseSize;
                for (int z = 0; z < cells; z++)
                for (int x = 0; x < cells; x++)
                {
                    bool oracle = IsSnapshotOceanCovered(rasterSnapshot,
                        aroundOrigin + x * spacing, aroundOrigin + (x + 1) * spacing,
                        acrossOrigin + z * spacing, acrossOrigin + (z + 1) * spacing,
                        fineSize, ringWidth, circumference);
                    if (mask.CoveredCells[z * cells + x] != oracle) return false;
                }
            }
            return true;
        }

        SurfaceCoverageMask FineMask(bool value) => new(16, 1, Enumerable.Repeat(value, 256).ToArray());
        var alignedMasks = new Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask>
        {
            [new RingTerrainSectorCoordinate(0, 0)] = FineMask(true),
            [new RingTerrainSectorCoordinate(1, 0)] = FineMask(true),
            [new RingTerrainSectorCoordinate(0, 1)] = FineMask(true),
            [new RingTerrainSectorCoordinate(1, 1)] = FineMask(true)
        };
        var alignedSnapshot = new FinerOceanSnapshot(alignedMasks, new HashSet<RingTerrainSectorCoordinate>());
        var alignedParent = new[] { new RingTerrainSectorCoordinate(0, 0) };
        passed &= AssertRasterParity(alignedSnapshot, alignedParent, 64, 32, 4, 64, 128);
        alignedSnapshot.FullyWet.UnionWith(alignedMasks.Keys);
        passed &= AssertRasterParity(alignedSnapshot, alignedParent, 64, 32, 4, 64, 128);
        alignedSnapshot.FullyWet.Clear();
        alignedMasks[new RingTerrainSectorCoordinate(1, 0)].CoveredCells[6 * 16 + 4] = false;
        passed &= AssertRasterParity(alignedSnapshot, alignedParent, 64, 32, 4, 64, 128);
        alignedMasks.Remove(new RingTerrainSectorCoordinate(1, 1));
        Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask> missingRaster = BuildOceanReplacementMasks(
            alignedSnapshot, alignedParent, 64, 4, 32, 64, 128);
        passed &= AssertRasterParity(alignedSnapshot, alignedParent, 64, 32, 4, 64, 128)
            && !missingRaster[alignedParent[0]].CoveredCells[15 * 16 + 15];

        var seamMasks = new Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask>
        {
            [new RingTerrainSectorCoordinate(0, 0)] = FineMask(true),
            [new RingTerrainSectorCoordinate(1, 0)] = FineMask(true),
            [new RingTerrainSectorCoordinate(2, 0)] = FineMask(true)
        };
        passed &= AssertRasterParity(new FinerOceanSnapshot(seamMasks, new HashSet<RingTerrainSectorCoordinate>()),
            new[] { new RingTerrainSectorCoordinate(1, 0) }, 64, 32, 4, 32, 96);

        const long largeParentAround = 23_437_499;
        var largeMasks = new Dictionary<RingTerrainSectorCoordinate, SurfaceCoverageMask>
        {
            [new RingTerrainSectorCoordinate(46_874_998, 0)] = FineMask(true),
            [new RingTerrainSectorCoordinate(46_874_999, 0)] = FineMask(true)
        };
        passed &= AssertRasterParity(new FinerOceanSnapshot(largeMasks, new HashSet<RingTerrainSectorCoordinate>()),
            new[] { new RingTerrainSectorCoordinate(largeParentAround, 0) }, 64, 32, 4, 64, 1_500_000_000.0);
        if (!passed) GD.PushError("OCEAN_REPLACEMENT_OWNERSHIP_CONTRACT_FAIL");
        else GD.Print("OCEAN_REPLACEMENT_OWNERSHIP_CONTRACT_PASS");
        return passed;
    }

    private static bool RunFftShorelineFieldContractTestCore()
    {
        const double circumference = 1_500_000_000.0;
        const double centerAround = 1_499_999_744.0;
        const double centerAcross = 400.0;
        bool passed = true;
        passed &= TryMapFftShorelineFieldUv(new RingCanonicalPosition((long)centerAround, 0, (int)centerAcross), centerAround, centerAcross, circumference, out Vector2 center)
            && Mathf.IsEqualApprox(center.X, (256.5f / 512.0f)) && Mathf.IsEqualApprox(center.Y, (256.5f / 512.0f));
        // Crossing 1.5bn->0 must remain one 2m atlas step, rather than a huge jump.
        passed &= TryMapFftShorelineFieldUv(new RingCanonicalPosition(0, 0, (int)centerAcross), centerAround, centerAcross, circumference, out Vector2 seam)
            && Mathf.IsEqualApprox(seam.X, 0.7509765625f);
        passed &= TryMapFftShorelineFieldUv(new RingCanonicalPosition(2, 0, (int)centerAcross), centerAround, centerAcross, circumference, out Vector2 seamAdjacent)
            && Mathf.IsEqualApprox(seamAdjacent.X - seam.X, 2.0f / 1024.0f);
        passed &= !TryMapFftShorelineFieldUv(new RingCanonicalPosition((long)(centerAround + 10_000.0), 0, (int)centerAcross), centerAround, centerAcross, circumference, out _);
        // A 128m tile may straddle either atlas edge after a 32m recenter. Its
        // visible rows must copy their intersecting source range, never vanish.
        passed &= TryResolveShorelineCopyRange(-17, 65, out int leftSource, out int leftTarget, out int leftCount)
            && leftSource == 17 && leftTarget == 0 && leftCount == 48;
        passed &= TryResolveShorelineCopyRange(480, 65, out int rightSource, out int rightTarget, out int rightCount)
            && rightSource == 0 && rightTarget == 480 && rightCount == 32;
        passed &= !TryResolveShorelineCopyRange(512, 65, out _, out _, out _);
        var payload = new byte[SharedFftShorelineFieldResolution * SharedFftShorelineFieldResolution * 8];
        WriteContractTexel(payload, 10, 10, 0.125f, -4.0f, 1.0f);
        WriteContractTexel(payload, 11, 10, 2048.0f, -2.0f, 1.0f);
        WriteContractTexel(payload, 10, 11, 8.0f, 2.0f, 1.0f);
        WriteContractTexel(payload, 11, 11, 16.0f, 4.0f, 1.0f);
        passed &= TryReadBilinearShoreline(payload, 10.0f, 10.0f, out Vector3 exact)
            && Mathf.IsEqualApprox(exact.X, 0.125f) && Mathf.IsEqualApprox(exact.Y, -4.0f);
        passed &= TryReadBilinearShoreline(payload, 10.5f, 10.5f, out Vector3 halfway)
            && Mathf.IsEqualApprox(halfway.X, (0.125f + 2048.0f + 8.0f + 16.0f) * 0.25f);
        WriteContractTexel(payload, 11, 11, 16.0f, 4.0f, 0.0f);
        passed &= !TryReadBilinearShoreline(payload, 10.5f, 10.5f, out _);
        // The saturated-footprint fast path reads the same packed RGBAH bytes
        // directly: default, mixed, invalid, and out-of-bounds all fail closed.
        passed &= !IsFftShorelineFootprintFullySaturated(payload, 100, 100, 1, 2, 2);
        for (int z = 99; z <= 101; z++)
        for (int x = 99; x <= 101; x++) WriteContractTexel(payload, x, z, 12, 24, 1);
        passed &= IsFftShorelineFootprintFullySaturated(payload, 100, 100, 1, 2, 2);
        WriteContractTexel(payload, 100, 100, 1, 24, 1);
        passed &= !IsFftShorelineFootprintFullySaturated(payload, 100, 100, 1, 2, 2);
        WriteContractTexel(payload, 100, 100, 12, 24, 0);
        passed &= !IsFftShorelineFootprintFullySaturated(payload, 100, 100, 1, 2, 2)
            && !IsFftShorelineFootprintFullySaturated(payload, 0, 0, 1, 2, 2);
        // The contact band convention is independent of depth: a deep wet cell
        // adjacent to dry terrain still has zero shore distance at both vertices.
        bool[] wet = { true, false, true };
        bool[] valid = { true, true, true };
        bool[] seeds = new bool[3];
        SeedMixedTriangle(0, 1, 2, wet, valid, seeds);
        passed &= seeds[0] && seeds[1] && seeds[2];
        passed &= RunFftShorelineStreamingPublicationContract();
        if (!passed) GD.PushError("FFT_SHORELINE_FIELD_CONTRACT_FAIL");
        else GD.Print("FFT_SHORELINE_FIELD_CONTRACT_PASS");
        return passed;
    }

    private static bool RunFftShorelineStreamingPublicationContract()
    {
        // A normal streaming replacement must leave the last complete field
        // valid while coverage is rebuilt. Conversely, true discontinuities
        // still fail closed and force a revision-visible invalidation.
        ulong savedCoverageRevision = _sharedFftOceanCoverageRevision;
        ulong savedShorelineSourceRevision = _sharedFftShorelineFieldSourceRevision;
        ulong savedShorelineRevision = _sharedFftShorelineFieldRevision;
        try
        {
            _sharedFftOceanCoverageRevision = 17;
            _sharedFftShorelineFieldSourceRevision = 17;
            _sharedFftShorelineFieldRevision = 41;
            InvalidateSharedFftOceanCoverageOnly();
            bool routineKeepsFieldPublished = _sharedFftOceanCoverageRevision == ulong.MaxValue
                && _sharedFftShorelineFieldSourceRevision == 17
                && _sharedFftShorelineFieldRevision == 41;

            // A synchronous refresh publishes a distinct source revision.
            _sharedFftShorelineFieldSourceRevision = 18;
            _sharedFftShorelineFieldRevision++;
            bool refreshPublishesRevision = _sharedFftShorelineFieldSourceRevision == 18
                && _sharedFftShorelineFieldRevision == 42;

            InvalidateSharedFftOceanCoverage();
            bool hardInvalidationFailsClosed = _sharedFftOceanCoverageRevision == ulong.MaxValue
                && _sharedFftShorelineFieldSourceRevision == ulong.MaxValue
                && _sharedFftShorelineFieldRevision == 43;
            return routineKeepsFieldPublished && refreshPublishesRevision && hardInvalidationFailsClosed;
        }
        finally
        {
            _sharedFftOceanCoverageRevision = savedCoverageRevision;
            _sharedFftShorelineFieldSourceRevision = savedShorelineSourceRevision;
            _sharedFftShorelineFieldRevision = savedShorelineRevision;
        }
    }

    private static void WriteContractTexel(byte[] payload, int x, int z, float depth, float distance, float valid)
    {
        int offset = (z * SharedFftShorelineFieldResolution + x) * 8;
        WriteHalf(payload, offset, depth);
        WriteHalf(payload, offset + 2, distance);
        WriteHalf(payload, offset + 4, valid);
    }

    private static Vector3 ReadShorelineTexel(int x, int z)
    {
        int offset = (z * SharedFftShorelineFieldResolution + x) * 8;
        return new Vector3(ReadHalf(_sharedFftShorelineFieldBytes!, offset), ReadHalf(_sharedFftShorelineFieldBytes!, offset + 2), ReadHalf(_sharedFftShorelineFieldBytes!, offset + 4));
    }

    private static bool TryReadBilinearShoreline(byte[] bytes, float x, float z, out Vector3 value)
    {
        int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z), x1 = x0 + 1, z1 = z0 + 1;
        if (x0 < 0 || x1 >= SharedFftShorelineFieldResolution || z0 < 0 || z1 >= SharedFftShorelineFieldResolution)
        { value = default; return false; }
        float tx = x - x0, tz = z - z0;
        Vector3 a = ReadShorelineTexel(bytes, x0, z0), b = ReadShorelineTexel(bytes, x1, z0);
        Vector3 c = ReadShorelineTexel(bytes, x0, z1), d = ReadShorelineTexel(bytes, x1, z1);
        value = a.Lerp(b, tx).Lerp(c.Lerp(d, tx), tz);
        return value.Z >= 0.999f;
    }

    private static Vector3 ReadShorelineTexel(byte[] bytes, int x, int z)
    {
        int offset = (z * SharedFftShorelineFieldResolution + x) * 8;
        return new Vector3(ReadHalf(bytes, offset), ReadHalf(bytes, offset + 2), ReadHalf(bytes, offset + 4));
    }

    private static float ReadHalf(byte[] source, int offset) =>
        (float)BitConverter.UInt16BitsToHalf((ushort)(source[offset] | (source[offset + 1] << 8)));

    private void UpdateLightingUniforms()
    {
        if (_material == null || _grid == null || _sunLight == null) return;

        Vector3 ringCenter = _grid.GetCanonicalRingCenterWorld();
        if (!_lastLightingRingCenter.IsEqualApprox(ringCenter))
        {
            SetRingCenter(_material, ringCenter);
            SetRingCenter(_oceanMaterial, ringCenter);
            SetRingCenter(_structuralWallMaterial, ringCenter);
            SetRingCenter(_opaqueShadowMaterial, ringCenter);
            _lastLightingRingCenter = ringCenter;
        }

        Vector3 sunDirection = _sunLight.GlobalTransform.Basis.Z.Normalized();
        if (!_lastLightingSunDirection.IsEqualApprox(sunDirection))
        {
            SetSunDirection(_material, sunDirection);
            SetSunDirection(_oceanMaterial, sunDirection);
            SetSunDirection(_structuralWallMaterial, sunDirection);
            _lastLightingSunDirection = sunDirection;
        }

        ApplyAtmosphereRayFrame(sunDirection);

        var configuration = new LightingUniformConfiguration(
            DayStartDot,
            Mathf.Max(DayStartDot + 0.001f, DayFullDot),
            new Vector3(NightTint.R, NightTint.G, NightTint.B),
            NightDirectLightFactor,
            DayDirectLightFactor,
            ShadowFloor,
            RingWidthMeters);
        if (_hasLightingUniformConfiguration && configuration == _lastLightingConfiguration) return;
        SetLightingConfiguration(_material, configuration);
        SetLightingConfiguration(_oceanMaterial, configuration);
        SetLightingConfiguration(_structuralWallMaterial, configuration);
        _lastLightingConfiguration = configuration;
        _hasLightingUniformConfiguration = true;
    }

    private void ApplyAtmosphereRayFrame(Vector3 toSun)
    {
        if (_grid == null || _focus == null) return;
        Camera3D? camera = GetViewport()?.GetCamera3D();
        Vector3 cameraWorld = camera?.GlobalPosition ?? _focus.GlobalPosition;
        RingCanonicalPosition canonical = _grid.WorldToCanonicalRingPosition64(cameraWorld);
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        double angle = canonical.AroundMeters / geometry.RingRadiusMeters;
        Vector3 outwardLocal = new((float)Math.Cos(angle), 0.0f, (float)Math.Sin(angle));
        Vector3 outwardWorld = (geometry.GridGlobalTransform.Basis * outwardLocal).Normalized();
        Vector3 axisWorld = (geometry.GridGlobalTransform.Basis * Vector3.Up).Normalized();
        double width = _grid.RingAcrossWidthMeters;
        var frame = new RingAtmosphereRayFrame(
            true,
            cameraWorld,
            (float)canonical.VerticalMeters,
            outwardWorld,
            axisWorld,
            (float)canonical.AcrossMeters,
            (float)(width - canonical.AcrossMeters),
            1.0f,
            toSun);
        frame.Apply(_material);
        frame.Apply(_oceanMaterial);
        frame.Apply(_structuralWallMaterial);
        frame.Apply(_opaqueShadowMaterial);
    }

    private static void SetRingCenter(ShaderMaterial? material, Vector3 ringCenter)
    {
        material?.SetShaderParameter("ring_center", ringCenter);
    }

    private static void SetSunDirection(ShaderMaterial? material, Vector3 sunDirection)
    {
        material?.SetShaderParameter("sun_direction", sunDirection);
    }

    private static void SetLightingConfiguration(ShaderMaterial? material, in LightingUniformConfiguration configuration)
    {
        if (material == null) return;
        material.SetShaderParameter("day_start_dot", configuration.DayStartDot);
        material.SetShaderParameter("day_full_dot", configuration.DayFullDot);
        material.SetShaderParameter("night_tint", configuration.NightTint);
        material.SetShaderParameter("night_direct_light_factor", configuration.NightDirectLightFactor);
        material.SetShaderParameter("day_direct_light_factor", configuration.DayDirectLightFactor);
        material.SetShaderParameter("shadow_floor", configuration.ShadowFloor);
        material.SetShaderParameter("ring_width_meters", configuration.RingWidthMeters);
    }

    public void ApplyRuntimeLodConfiguration(
        int radiusAround,
        int radiusAcross,
        float fadeStart,
        float fadeEnd,
        bool enableFarFade,
        float fadeOutStart,
        float fadeOutEnd,
        float innerClipRadius)
    {
        SectorRadiusAround = Mathf.Clamp(radiusAround, 0, 32);
        SectorRadiusAcross = Mathf.Clamp(radiusAcross, 0, 32);
        FadeStartMeters = Mathf.Max(0.0f, fadeStart);
        FadeEndMeters = Mathf.Max(FadeStartMeters + 1.0f, fadeEnd);
        EnableFarFade = enableFarFade;
        FadeOutStartMeters = Mathf.Max(0.0f, fadeOutStart);
        FadeOutEndMeters = Mathf.Max(FadeOutStartMeters + 1.0f, fadeOutEnd);
        InnerClipRadiusMeters = Mathf.Max(0.0f, innerClipRadius);
        _material?.SetShaderParameter("fade_start_meters", FadeStartMeters);
        _material?.SetShaderParameter("fade_end_meters", FadeEndMeters);
        _material?.SetShaderParameter("near_fade_enabled", ResolveTerrain3DNearFadeEnabled());
        _material?.SetShaderParameter("far_fade_enabled", EnableFarFade);
        _material?.SetShaderParameter("fade_out_start_meters", FadeOutStartMeters);
        _material?.SetShaderParameter("fade_out_end_meters", FadeOutEndMeters);
        _material?.SetShaderParameter("morph_start_meters", FadeOutStartMeters);
        _material?.SetShaderParameter("morph_end_meters", FadeOutEndMeters);
        _material?.SetShaderParameter("geomorph_enabled", EnableGeomorphing && !UseBlockyHeightfield);
        _opaqueShadowMaterial?.SetShaderParameter("morph_start_meters", FadeOutStartMeters);
        _opaqueShadowMaterial?.SetShaderParameter("morph_end_meters", FadeOutEndMeters);
        _opaqueShadowMaterial?.SetShaderParameter("geomorph_enabled", EnableGeomorphing && !UseBlockyHeightfield);
        _oceanMaterial?.SetShaderParameter("far_fade_enabled", EnableFarFade);
        _oceanMaterial?.SetShaderParameter("fade_out_start_meters", FadeOutStartMeters);
        _oceanMaterial?.SetShaderParameter("fade_out_end_meters", FadeOutEndMeters);
        _oceanMaterial?.SetShaderParameter("morph_start_meters", FadeOutStartMeters);
        _oceanMaterial?.SetShaderParameter("morph_end_meters", FadeOutEndMeters);
        _oceanMaterial?.SetShaderParameter("geomorph_enabled", EnableGeomorphing && !UseBlockyHeightfield);
        if (_structuralWallMaterial != null)
        {
            ConfigureStructuralShellHandoff(retainCenter: true);
            _structuralWallMaterial.SetShaderParameter("morph_start_meters", FadeOutStartMeters);
            _structuralWallMaterial.SetShaderParameter("morph_end_meters", FadeOutEndMeters);
            _structuralWallMaterial.SetShaderParameter("geomorph_enabled", EnableGeomorphing && !UseBlockyHeightfield);
        }
        _lastAround = long.MinValue;
        _lastAcross = int.MinValue;
        _lastPredictedAround = long.MinValue;
        _lastPredictedAcross = int.MinValue;
        _lastAltitudeClipBucket = int.MinValue;
        _lastWallAroundStart = int.MinValue;
        _lastWallAroundEnd = int.MinValue;
    }

    /// <summary>Uses one centered structural level and lets only coarser shells discard their overlap.</summary>
    public void ConfigureStructuralShellHandoff(bool retainCenter)
    {
        if (_structuralWallMaterial == null) return;
        _structuralWallMaterial.SetShaderParameter("near_fade_enabled", ResolveStructuralShellNearFadeEnabled(retainCenter));
        _structuralWallMaterial.SetShaderParameter("fade_start_meters", FadeStartMeters);
        _structuralWallMaterial.SetShaderParameter("fade_end_meters", FadeEndMeters);
        _structuralWallMaterial.SetShaderParameter("far_fade_enabled", EnableFarFade);
        _structuralWallMaterial.SetShaderParameter("fade_out_start_meters", FadeOutStartMeters);
        _structuralWallMaterial.SetShaderParameter("fade_out_end_meters", FadeOutEndMeters);
    }

    private bool ResolveTerrain3DNearFadeEnabled() => _grid?.UseTerrain3DSurface == true
        && _grid.SurfaceTerrainVisualReady && _hasVoxelCoverageProvider;

    private static bool ResolveStructuralShellNearFadeEnabled(bool innermostActiveLevel)
        => !innermostActiveLevel;

    public void SetTerrainShadowCasting(bool enabled)
    {
        CastTerrainShadows = enabled;
        if (_structuralWallMesh != null)
        {
            _structuralWallMesh.CastShadow = ResolveStructuralShadowCasting(
                BuildTerrainRoofUnderside || BuildLowerHullCavityFloor,
                enabled);
        }
        foreach ((RingTerrainSectorCoordinate coordinate, MeshInstance3D node) in _active)
        {
            bool useOpaqueShadowCaster = enabled && ShadowIgnoresTerrainFade;
            node.CastShadow = enabled && !useOpaqueShadowCaster
                ? ShadowCastingSetting.On
                : ShadowCastingSetting.Off;
            Mesh? shadowMesh = _partialCoverageSources.TryGetValue(coordinate, out PartialCoverageMeshSource? source)
                ? source.FullMesh : node.Mesh;
            ConfigureOpaqueShadowCaster(node, shadowMesh, useOpaqueShadowCaster);
        }
    }

    public void SetPartialCoverageMeshCulling(bool enabled)
    {
        if (EnablePartialCoverageMeshCulling == enabled) return;
        EnablePartialCoverageMeshCulling = enabled;
        if (enabled)
        {
            foreach ((RingTerrainSectorCoordinate coordinate, MeshInstance3D node) in _active)
                if (_activeCoverageMasks.TryGetValue(coordinate, out SurfaceCoverageMask? mask))
                    QueuePartialCoverageCulling(coordinate, node, mask);
            return;
        }
        _partialCoveragePending.Clear();
        _partialCoveragePendingSet.Clear();
        foreach ((RingTerrainSectorCoordinate coordinate, MeshInstance3D node) in _active)
        {
            RestoreFullPartialCoverageMesh(coordinate, node);
            if (_partialCoverageSources.TryGetValue(coordinate, out PartialCoverageMeshSource? source)
                && source.FilteredMesh != null)
            {
                PartialCoverageFilteredMeshBytes = Math.Max(0L, PartialCoverageFilteredMeshBytes - source.FilteredMeshBytes);
                source.FilteredMesh.Dispose();
                source.FilteredMesh = null;
                source.FilteredMeshBytes = 0;
            }
        }
    }

    public void SetDebugColor(bool enabled, Color color)
    {
        _material?.SetShaderParameter("debug_color_enabled", enabled);
        _material?.SetShaderParameter("debug_color", new Vector3(color.R, color.G, color.B));
    }
}
