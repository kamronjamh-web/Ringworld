using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Bounded persistent scheduler for canonical, tile-local grass instances.</summary>
public partial class RingStandingGrassLayer : Node3D
{
    internal readonly record struct TileAudit(long Around, long Across, ulong PlacementHash, int InstanceCount, ulong RenderInstanceId, Transform3D Transform,
        int NearVisibleInstanceCount, int IntermediateVisibleInstanceCount, ulong IntermediateRenderInstanceId);
    private sealed class Resident
    {
        public required RingGrassTileData Data;
        public AdaptiveGroup? Group;
        public Vector4[]? GroupMetadata;
        public readonly TilePresentationCache Presentation = new();
        // Immutable source reduction computed when a tile becomes resident.
        // Presentation must never walk every root each camera frame.
        public float MaximumSourceHeight;
        public MultiMeshInstance3D? Renderer;
        // Both passes consume this one immutable, priority-sorted root buffer.
        // Changing camera LOD therefore never queues a terrain/placement rebuild.
        public MultiMeshInstance3D? IntermediateRenderer;
        public int GpuNearBatch = -1;
        public int GpuIntermediateBatch = -1;
        public bool NearWanted;
        public bool IntermediateWanted;
        public int PresentationPrefix;
        public long NearVisibilityRevision;
        public long IntermediateVisibilityRevision;
    }
    private sealed class Job
    {
        public required RingGrassTileKey Key;
        public required CancellationTokenSource Cts;
        public Task<RingGrassTileData> Task = null!;
        public required ulong Epoch, Revision;
        public SmoothTerrainVolumetricDensityIdentity? FieldIdentity;
        public SmoothTerrainVolumetricWorkerContext? FieldWorkerContext;
        public RingGrassExclusionSnapshot? Exclusions;
        public required int Reserve;
        public required float SourceFraction;
        public required byte SourceTier;
        public required bool Dirty;
        public RingGrassModifierSnapshot? Modifiers;
        public double WorkerMilliseconds;
        public long RequestTick, PreparationStartTick, PreparationEndTick, TaskQueuedTick, WorkerStartTick, WorkerFinishTick;
        public long DiagnosticSession;
        public GenerationTrace? DiagnosticTrace;
    }
    [Export] public bool Enabled { get; set; } = true;
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath SmoothTerrainPath { get; set; } = new("../SmoothTerrainGrid");
    [Export] public NodePath ConstructionOverlayPath { get; set; } = new("../SmoothConstructionOverlay");
    [Export(PropertyHint.Range, "4,32,1")] public float CellSizeMeters { get; set; } = 8;
    [Export(PropertyHint.Range, "32,350,8")] public float VisibilityRadiusMeters { get; set; } = 115;
    [Export(PropertyHint.Range, "16,128,8")] public float SpatialTileMeters { get; set; } = 48;
    [Export(PropertyHint.Range, "1,16,1")] public int CandidatesPerCell { get; set; } = 9;
    /// <summary>When non-zero, places one deterministic stratified blade per
    /// density cell. Zero retains the legacy candidate-per-cell placement.</summary>
    [Export(PropertyHint.Range, "0,96,1")] public float BladesPerSquareMeter { get; set; }
    /// <summary>Groups four dense grass sources under one render instance.</summary>
    [Export] public bool UseGrassClumps { get; set; } = true;
    [Export] public bool EnableMeadowTufts { get; set; } = true;
    [Export(PropertyHint.Range, "0.25,2,0.01")] public float MeadowTuftSpread { get; set; } = 1f;
    [Export(PropertyHint.Enum, "Mixed,Compact,Spreading,Leaning")] public int MeadowTuftVariant { get; set; }
    [Export(PropertyHint.Range, "0.03,2,0.01")] public float GrassMinimumHeightMeters { get; set; } = .10f;
    [Export(PropertyHint.Range, "0.03,2,0.01")] public float GrassMaximumHeightMeters { get; set; } = .20f;
    private readonly RingMeadowGrassMeshLibrary _meadowMeshes = new();
    private float EffectiveMeadowSpread => float.IsFinite(MeadowTuftSpread) ? Mathf.Clamp(MeadowTuftSpread, .25f, 2f) : 1f;
    public int GrassPlacementUnitsPerSource => UseGrassClumps && float.IsFinite(BladesPerSquareMeter) && BladesPerSquareMeter > 0 ? 4 : 1;
    [Export(PropertyHint.Range, "100,100000,100")] public int MaximumInstancesPerTile { get; set; } = 6000;
    [Export(PropertyHint.Range, "1000,250000,1000")] public int MaximumInstances { get; set; } = 18000;
    [Export(PropertyHint.Range, "16,1024,16")] public int MaximumResidentTiles { get; set; } = 128;
    [Export(PropertyHint.Range, "1,8,1")] public int MaximumTileBuildsInFlight { get; set; } = 2;
    [Export(PropertyHint.Range, "1,16,1")] public int MaximumTileUploadsPerFrame { get; set; } = 2;
    [Export(PropertyHint.Range, "0.1,20,0.1")] public float MaximumTileUploadMilliseconds { get; set; } = 3;
    /// <summary>Bounded shared exact-surface lookup cache. Zero disables it;
    /// values are capped at 16 MiB so grass never retains terrain indefinitely.</summary>
    [Export(PropertyHint.Range, "0,16,0.25")] public float SurfaceCacheMegabytes { get; set; } = 4;
    [Export(PropertyHint.Range, "0,60,1")] public float MaximumSlopeDegrees { get; set; } = 25;
    [Export] public float MaximumViewerAltitudeAboveTerrainMeters { get; set; } = 350;
    [Export] public float SuspendStreamingAboveSpeedMetersPerSecond { get; set; } = 180;
    [ExportGroup("Distance LOD")]
    [Export] public bool EnableDistanceLod { get; set; }
    [Export(PropertyHint.Range, "8,350,1")] public float LodNearDistanceMeters { get; set; } = 48;
    [Export(PropertyHint.Range, "16,1500,1")] public float LodFarDistanceMeters { get; set; } = 115;
    [Export] public bool EnableHierarchicalCoverage { get; set; } = true;
    [Export(PropertyHint.Range, "9,1499,1")] public float LodMidDistanceMeters { get; set; } = 250;
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float LodMidSourceDensity { get; set; } = .25f;
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float LodFarSourceDensity { get; set; } = .0625f;
    [Export(PropertyHint.Range, "1,64,1")] public float LodTransitionWidthMeters { get; set; } = 12;
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float LodNearDensity { get; set; } = 1;
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float LodFarDensity { get; set; } = .45f;
    [ExportGroup("GPU Culling")]
    /// <summary>Uses the GPU compact/indirect backend when the active renderer supports it.
    /// CPU placement and the existing vertex LOD remain the canonical fallback.</summary>
    [Export] public bool UseGpuCulling { get; set; } = true;
    /// <summary>Groups the independent all-grass compute phases into three passes.</summary>
    [Export] public bool GroupGrassComputePhases { get; set; } = true;
    /// <summary>Rejects whole grass tiles outside the active camera presentation volume.</summary>
    [Export] public bool CullGrassTiles { get; set; } = true;
    [Export] public bool GpuFrustumCulling { get; set; } = true;
    /// <summary>Extra world-space margin applied after the bounded grass bend envelope.
    /// Increase only when a custom grass material adds displacement beyond the shared shader.</summary>
    [Export(PropertyHint.Range, "0,8,0.01")] public float GrassFrustumPaddingMeters { get; set; } = .15f;
    /// <summary>Skips an identical GPU culling request after its initial indirect draw.
    /// Wind still updates in the material every frame.</summary>
    [Export] public bool SkipUnchangedGpuCulling { get; set; } = true;
    /// <summary>Upper bound for grass batches submitted to compute in one frame.
    /// The compute backend retains prior compacted output for deferred batches.</summary>
    [Export(PropertyHint.Range, "16,1024,16")] public int MaximumGpuCullingBatchesPerFrame { get; set; } = 96;
    /// <summary>Read-only runtime diagnostics consumed by the live graphics menu.</summary>
    public string GpuCullingStatus { get; private set; } = "Not initialized";
    public long GpuCullingBytes { get; private set; }
    /// <summary>Immutable GPU root uploads. Near/intermediate grass passes share
    /// one source per resident tile instead of uploading its roots twice.</summary>
    public long GpuCullingSharedSourceBytes { get; private set; }
    /// <summary>Indirect MultiMesh output storage, after intermediate sizing.</summary>
    public long GpuCullingOutputBytes { get; private set; }
    public int GpuCullingSharedSourceCount { get; private set; }
    public int GpuCullingBatchCount { get; private set; }
    public int GpuCullingRequestedBatchCount => _gpuCompute?.LastFrameRequestedBatchCount ?? 0;
    public int GpuCullingDispatchedBatchCount => _gpuCompute?.LastFrameDispatchedBatchCount ?? 0;
    public int GpuCullingDeferredBatchCount => _gpuCompute?.DeferredBatchCount ?? 0;
    public int GrassFrustumRejectedTileCount { get; private set; }
    public int GrassDistanceRejectedTileCount { get; private set; }
    public long GpuCullingCompletedUpdates => _gpuCompute?.CompletedUpdates ?? 0;
    public long GpuCullingSkippedUnchangedUpdates => _gpuCompute?.SkippedUnchangedUpdates ?? 0;
    private readonly Dictionary<RingGrassTileKey, Resident> _tiles = new();
    private readonly Dictionary<RingGrassTileKey, Job> _jobs = new();
    private readonly Queue<RingGrassTileKey> _requests = new(), _uploads = new();
    private readonly HashSet<RingGrassTileKey> _requested = new(), _uploadQueued = new(), _wanted = new();
    private readonly Dictionary<RingGrassTileKey, (float Fraction, byte Tier)> _wantedCoverage = new();
    private readonly List<RingGrassTileKey> _keys = new();
    private readonly List<(RingGrassTileKey, double)> _ordered = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Mesh? _mesh;
    private Mesh? _legacyMesh;
    private Mesh? _denseMesh;
    private Mesh? _clumpMesh;
    private Mesh? _intermediateMesh;
    private Mesh? _intermediateClumpMesh;
    private Mesh? _farMesh;
    private Mesh? _farClumpMesh;
    private ShaderMaterial? _material;
    private ShaderMaterial? _intermediateMaterial;
    private VoxelChunkGridManager? _grid;
    private RingVoxelExplorerController? _explorer;
    private SmoothTerrainGrid? _smooth;
    private SmoothConstructionOverlay? _construction;
    private RingGrassExclusionSnapshot? _previousExclusions;
    private SmoothTerrainVolumetricDensityIdentity? _fieldIdentity;
    private SmoothTerrainVolumetricDensitySnapshot? _fieldSnapshot;
    private RingGrassExclusionSnapshot? _cachedConstructionExclusions, _cachedMergedExclusions;
    private ulong _cachedExplicitExclusionRevision;
    private long _cachedExclusionCircumference;
    private readonly List<RingGrassExclusionBox> _explicitExclusions = new();
    private ulong _explicitExclusionRevision;
    private ulong _epoch, _revision = ulong.MaxValue, _settings;
    private long _focusA = long.MinValue, _focusB = long.MinValue;
    private RingCanonicalPosition _lastPlannedFocus;
    private bool _hasCachedPlan;
    private bool _requestQueueDirty = true;
    internal long RequestQueueScans { get; private set; }
    private ulong _coverageSettings;
    private float _cachedPlanReuseMargin;
    private int _reserved;
    private ulong _lodMaterialFingerprint;
    private RingGpuVegetationCompute? _gpuCompute;
    private bool _gpuFailureLatched;
    private readonly List<RingGpuVegetationCompute.BatchFrameParameters> _gpuFrames = new();
    private readonly HashSet<int> _gpuReadyBatches = new();
    private (bool Near, bool Intermediate)? _lastGpuGeometrySelection;
    private readonly RingGrassSurfaceCache _surfaceCache = new(4L * 1024 * 1024);
    private Vector4[]? _gpuFrustum;
    private Plane[]? _cameraFrustumPlanes;
    private GpuCameraState? _gpuFrustumState;
    private readonly record struct GpuCameraState(ulong Id, Transform3D Transform, Camera3D.ProjectionType Projection,
        Camera3D.KeepAspectEnum KeepAspect, float Fov, float Size, Vector2 FrustumOffset, float Near, float Far,
        float HOffset, float VOffset, Vector2 ViewportSize);
    public int ResidentGrassTileCount => _tiles.Count;
    public int RenderedGrassTileCount
    {
        get;
        private set;
    }
    public int InstalledGrassClumpCount
    {
        get;
        private set;
    }
    public int PendingGrassTileBuildCount => _jobs.Count + _requests.Count;
    public int PendingGrassUploadCount => _uploads.Count;
    public int ResidentGrassInstanceCount
    {
        get;
        private set;
    }
    public int TotalGrassBuildCount
    {
        get;
        private set;
    }
    public int TotalGrassUploadCount
    {
        get;
        private set;
    }
    public bool StreamingSuspended
    {
        get;
        private set;
    }
    public double LastWorkerBuildMilliseconds
    {
        get;
        private set;
    }
    public double LastInstallMilliseconds
    {
        get;
        private set;
    }
    private float TileSize => Mathf.Max(16, SpatialTileMeters);
    private float CellSize => Mathf.Max(4, CellSizeMeters);
    /// <summary>Blade-equivalent coverage represented by one source instance.</summary>
    public int GrassBladesPerSource => GrassPlacementUnitsPerSource > 1 ? EnableMeadowTufts ? 8 : 4 : 1;
    public int MaximumGrassSourceInstances => Math.Max(1, MaximumInstances / GrassPlacementUnitsPerSource);
    public int MaximumGrassSourcesPerTile => Math.Max(1, MaximumInstancesPerTile / GrassPlacementUnitsPerSource);
    public override void _Ready()
    {
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _smooth = GetNodeOrNull<SmoothTerrainGrid>(SmoothTerrainPath);
        _construction = GetNodeOrNull<SmoothConstructionOverlay>(ConstructionOverlayPath);
        EnsureResources();
    }
    public override void _Process(double d)
    {
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Grass update (inclusive)");
        RefreshCoordinatedGrassHandoffGlobals();
        _ = d;
        if (ShouldSuspendStreaming(_explorer?.CurrentAltitudeAboveTerrainMeters ?? 0,
            _explorer?.ActualTravelSpeedMetersPerSecond ?? 0))
        {
            SuspendStreaming();
            RefreshCoordinatedGrassHandoffGlobals();
            return;
        }
        // Lighting must update even while streaming remains stationary: the
        // canonical frame and primary sun change independently of tile work.
        UpdateGrassLightingMaterials();
        if (_grid?.TerrainSampler is RingTerrainSampler s && _explorer != null)
        {
            RingLodSurfaceEditSnapshot edits;
            RingLodGeometrySnapshot geometry;
            SmoothTerrainVolumetricDensitySnapshot? field;
            RingGrassExclusionSnapshot? exclusions;
            using (RingPerformanceTelemetry.MeasureCpu("Grass input snapshots"))
            {
                edits = _smooth?.CaptureLodSurfaceEditSnapshot() ?? _grid.CaptureLodSurfaceEditSnapshot();
                geometry = _grid.CaptureLodGeometrySnapshot();
                field = _smooth?.CaptureVolumetricDensitySnapshot();
                exclusions = CaptureExclusions(s);
            }
            StepStreaming(s, edits, _explorer.CanonicalPosition, geometry,
                _explorer.CurrentAltitudeAboveTerrainMeters, _explorer.ActualTravelSpeedMetersPerSecond,
                field, exclusions);
            UpdateGrassInteraction((float)d);
        }
    }
    internal void InitializeForTests() => EnsureResources();
    private void EnsureResources()
    {
        bool dense = BladesPerSquareMeter > 0 && float.IsFinite(BladesPerSquareMeter);
        _mesh = dense ? _denseMesh ??= CreateDenseBladeMesh() : _legacyMesh ??= CreateBladeMesh();
        if (GrassBladesPerSource > 1) _clumpMesh ??= RingGrassMeshFactory.CreateClump();
        if (_material == null)
        {
            _material = CreateGrassMaterial(0);
            _grassLightingStateValid = false;
        }
        if (EnableDistanceLod)
        {
            _intermediateMesh ??= RingGrassMeshFactory.CreateIntermediate();
            _farMesh ??= RingGrassMeshFactory.CreateFarCluster();
            if (GrassBladesPerSource > 1)
            {
                _intermediateClumpMesh ??= RingGrassMeshFactory.CreateIntermediateClump();
                _farClumpMesh ??= RingGrassMeshFactory.CreateFarClump();
            }
            if (_intermediateMaterial == null)
            {
                _intermediateMaterial = CreateGrassMaterial(1);
                _grassLightingStateValid = false;
            }
        }
        UpdateGrassLightingMaterials();
    }
    internal void StepForTests(RingTerrainSampler s, RingLodSurfaceEditSnapshot e, RingCanonicalPosition f, RingLodGeometrySnapshot g, float altitudeAboveTerrain = 0, float speed = 0, SmoothTerrainVolumetricDensitySnapshot? field = null, RingGrassExclusionSnapshot? exclusions = null) => StepStreaming(s, e, f, g, altitudeAboveTerrain, speed, field, exclusions);
    /// <summary>Test-only presentation refresh that cannot plan or schedule workers.</summary>
    internal void RefreshLodPresentationForTests(RingCanonicalPosition focus, RingLodGeometrySnapshot geometry, long circumference) => Transforms(geometry, focus, circumference);
    /// <summary>Runtime graphics hook. It changes presentation only; resident roots and worker epochs remain intact.</summary>
    public void ApplyGrassQuality(bool enabled, float nearDistanceMeters, float farDistanceMeters,
        float transitionWidthMeters, float nearDensity, float farDensity)
    {
        EnableDistanceLod = enabled;
        LodNearDistanceMeters = Mathf.Max(1, nearDistanceMeters);
        LodFarDistanceMeters = Mathf.Max(LodNearDistanceMeters + 1, farDistanceMeters);
        LodTransitionWidthMeters = Mathf.Max(1, transitionWidthMeters);
        LodNearDensity = Mathf.Clamp(nearDensity, .05f, 1);
        LodFarDensity = Mathf.Clamp(farDensity, .05f, LodNearDensity);
        EnsureResources();
    }
    public int ResidentGrassNearRootCount { get; private set; }
    public int ResidentGrassMidRootCount { get; private set; }
    public int ResidentGrassFarRootCount { get; private set; }
    public int GrassTierRebuildCount { get; private set; }
    public Godot.Collections.Dictionary SurfaceCacheDiagnostics
    {
        get
        {
            RingGrassSurfaceCache.Diagnostics d = _surfaceCache.CaptureDiagnostics();
            return new()
            {
                ["byte_cap"] = d.ByteCap,
                ["entries"] = d.Entries,
                ["estimated_bytes"] = d.EstimatedBytes,
                ["hits"] = d.Hits,
                ["misses"] = d.Misses,
                ["evictions"] = d.Evictions
            };
        }
    }
    /// <summary>Adds caller-owned canonical path or rock volumes to the immutable worker mask.</summary>
    public void SetExclusionRegions(IEnumerable<RingGrassExclusionBox> regions)
    {
        _explicitExclusions.Clear();
        if (regions != null) _explicitExclusions.AddRange(regions);
        _explicitExclusionRevision++;
    }
    private RingGrassExclusionSnapshot? CaptureExclusions(RingTerrainSampler sampler)
    {
        RingGrassExclusionSnapshot? construction = _construction?.CaptureGrassExclusionSnapshot();
        if (construction == null && _explicitExclusions.Count == 0) return null;
        if (ReferenceEquals(construction, _cachedConstructionExclusions)
            && _cachedExplicitExclusionRevision == _explicitExclusionRevision
            && _cachedExclusionCircumference == sampler.AroundRingVoxelLength)
            return _cachedMergedExclusions;
        var boxes = new List<RingGrassExclusionBox>(_explicitExclusions);
        if (construction != null) boxes.AddRange(construction.Boxes);
        ulong revision = unchecked((construction?.Revision ?? 0UL) * 0x9E3779B185EBCA87UL ^ _explicitExclusionRevision);
        _cachedConstructionExclusions = construction;
        _cachedExplicitExclusionRevision = _explicitExclusionRevision;
        _cachedExclusionCircumference = sampler.AroundRingVoxelLength;
        return _cachedMergedExclusions = new RingGrassExclusionSnapshot(revision, sampler.AroundRingVoxelLength, boxes);
    }
    private void StepStreaming(RingTerrainSampler s, RingLodSurfaceEditSnapshot e, RingCanonicalPosition f, RingLodGeometrySnapshot g, float altitude, float speed, SmoothTerrainVolumetricDensitySnapshot? field, RingGrassExclusionSnapshot? exclusions)
    {
        if (ShouldSuspendStreaming(altitude, speed))
        {
            SuspendStreaming();
            RefreshCoordinatedGrassHandoffGlobals();
            return;
        }
        StreamingSuspended = false;
        Visible = true;
        RefreshCoordinatedGrassHandoffGlobals();
        EnsureResources();
        UpdateGrassGusts(0, s);
        CaptureGrassModifiers(s);
        _surfaceCache.Configure((long)Math.Round(Mathf.Clamp(SurfaceCacheMegabytes, 0, 16) * 1024 * 1024));
        ulong fp = Fingerprint(s, g);
        if (fp != _settings)
        {
            _settings = fp;
            Reset();
        }
        ulong previousHintRevision = _revision;
        SmoothTerrainVolumetricDensityIdentity? previousFieldIdentity = _fieldIdentity;
        if (e.Version != _revision) Invalidate(e, s);
        bool fieldReplaced = field != null && _fieldSnapshot != null
            && !ReferenceEquals(field, _fieldSnapshot) && field.Identity.EditRevision == _fieldIdentity?.EditRevision;
        bool definitionChanged = field?.Identity.BaselineRevision != _fieldIdentity?.BaselineRevision;
        if (fieldReplaced || definitionChanged)
        {
            // A restored/replaced field may reuse a numeric edit revision.
            // Its full immutable identity is the authority in that case.
            _fieldIdentity = field?.Identity;
            _epoch++;
            ClearTiles();
            Cancel(null);
        }
        else if (_fieldIdentity != field?.Identity)
        {
            bool regionalAdvance = field != null && previousFieldIdentity != null
                && previousHintRevision == previousFieldIdentity.Value.EditRevision
                && e.Version == field.Identity.EditRevision
                && e.Version == previousHintRevision + 1
                && field.Identity.EditRevision == previousFieldIdentity.Value.EditRevision + 1
                && e.CanonicalDirtyColumns.Count > 0;
            if (regionalAdvance)
            {
                // The matching, sequential LOD projection proves that jobs
                // outside the dirty columns sampled an unchanged field.
                foreach (Job job in _jobs.Values) if (!job.Dirty) job.FieldIdentity = field?.Identity;
            }
            else
            {
                _epoch++;
                ClearTiles();
                Cancel(null);
            }
        }
        _fieldIdentity = field?.Identity;
        _fieldSnapshot = field;
        InvalidateExclusions(exclusions, s);
        Drain(e.Version, field, exclusions);
        Plan(f, s);
        Cancel(_wanted);
        Evict();
        Queue();
        Start(s, e, g, field, exclusions);
        RefreshGrassGeometrySelection();
        PrepareGrassPresentation(g, f, s.AroundRingVoxelLength);
        Install();
        Transforms(g, f, s.AroundRingVoxelLength, refreshSelection: false);
    }
    private void Plan(RingCanonicalPosition f, RingTerrainSampler s)
    {
        using var scope = RingPerformanceTelemetry.MeasureCpu("Grass coverage plan");
        ulong coverageSettings = CoverageFingerprint();
        // A stationary frame cannot change tile membership or conservative
        // tiers.  Keeping the prior nearest-first list avoids the full tile
        // scan/sort that previously happened every rendered frame.
        if (_hasCachedPlan && _coverageSettings == coverageSettings && CanReusePlan(f, s))
        {
            PruneUploads();
            return;
        }
        _lastPlannedFocus = f;
        InvalidateGrassWork();
        _requestQueueDirty = true;
        _hasCachedPlan = true;
        _coverageSettings = coverageSettings;
        long a = (long)Math.Floor(RingGrassCoordinates.Canonicalize(f.AroundMeters, s.AroundRingVoxelLength) / TileSize), b = (long)Math.Floor(f.AcrossMeters / TileSize);
        // Re-plan inside a tile as well: a viewer can move nearly one tile
        // width before its quantized focus changes, which otherwise exposes a
        // narrow uncovered strip at the streaming edge.
        _focusA = a;
        _focusB = b;
        _wanted.Clear(); _wantedCoverage.Clear();
        _ordered.Clear();
        _requests.Clear();
        _requested.Clear();
        long n = RingGrassCoordinates.AroundTileCount(s.AroundRingVoxelLength, TileSize);
        float coverageRadius = EnableDistanceLod
            ? Mathf.Max(VisibilityRadiusMeters, LodFarDistanceMeters + LodTransitionWidthMeters * .5f)
            : VisibilityRadiusMeters;
        // The halo lets a modest movement reuse the previous sorted plan with
        // no coverage hole at its outer edge.  We only reuse when every halo
        // tile fit the resident budget (see CanReusePlan).
        _cachedPlanReuseMargin = TileSize * .125f;
        float planningRadius = coverageRadius + _cachedPlanReuseMargin;
        int r = (int)Math.Ceiling(planningRadius / TileSize) + 2;
        for (long z = b - r; z <= b + r; z++) for (long x = a - r; x <= a + r; x++)
            {
                if (z < 0) continue;
                var k = new RingGrassTileKey(RingGrassCoordinates.WrapTileIndex(x, n), z);
                var q = RingGrassCoordinates.Bounds(k.Around, k.Across, TileSize, s.AroundRingVoxelLength, s.RingAcrossWidthMeters);
                double d = RingGrassCoordinates.TileDistanceSquared(q, f, s.AroundRingVoxelLength);
                if (q.AcrossMax > q.AcrossMin && d <= planningRadius * planningRadius) _ordered.Add((k, d));
            }
        _ordered.Sort((x, y) => x.Item2 != y.Item2 ? x.Item2.CompareTo(y.Item2)
            : x.Item1.Around != y.Item1.Around ? x.Item1.Around.CompareTo(y.Item1.Around)
            : x.Item1.Across.CompareTo(y.Item1.Across));
        for (int i = 0; i < _ordered.Count && i < Math.Max(1, MaximumResidentTiles); i++)
        {
            var entry = _ordered[i];
            _wanted.Add(entry.Item1);
            double meters = Math.Sqrt(entry.Item2);
            (float fraction, byte tier) = CoverageForDistance(meters);
            _wantedCoverage[entry.Item1] = (fraction, tier);
        }
        PruneUploads();
    }
    private void Queue()
    {
        using var scope = RingPerformanceTelemetry.MeasureCpu("Grass request queue");
        if (!_requestQueueDirty) return;
        _requestQueueDirty = false;
        RequestQueueScans++;
        // _wanted is a HashSet; enqueue in the canonical nearest-first order so
        // a full budget cannot repeatedly give distant tiles the first reservation.
        foreach (var entry in _ordered)
        {
            RingGrassTileKey k = entry.Item1;
            bool needsReplacement = _tiles.TryGetValue(k, out Resident? resident)
                && _wantedCoverage.TryGetValue(k, out var coverage)
                && (resident.Data.SourceTier != coverage.Tier || !Mathf.IsEqualApprox(resident.Data.SourceFraction, coverage.Fraction));
            // Do not enqueue an impossible new-tile request every frame at the
            // global hard cap.  Replacements remain eligible because their old
            // allocation is credited when the worker reservation is made.
            if (!_tiles.ContainsKey(k) && MaximumGrassSourceInstances - ResidentGrassInstanceCount - _reserved <= 0) continue;
            if (_wanted.Contains(k) && (!_tiles.ContainsKey(k) || needsReplacement) && !_jobs.ContainsKey(k) && _requested.Add(k)) { _requests.Enqueue(k); DiagnosticRequested(k); }
        }
    }
    private void Start(RingTerrainSampler s, RingLodSurfaceEditSnapshot e, RingLodGeometrySnapshot g, SmoothTerrainVolumetricDensitySnapshot? field, RingGrassExclusionSnapshot? exclusions)
    {
        using var scope = RingPerformanceTelemetry.MeasureCpu("Grass build preparation");
        while (_jobs.Count < Math.Max(1, MaximumTileBuildsInFlight) && _requests.Count > 0)
        {
            var k = _requests.Dequeue();
            _requested.Remove(k);
            if (!_wanted.Contains(k) || _jobs.ContainsKey(k)) { DiagnosticOutcome(k, "request_skipped"); continue; }
            var coverage = _wantedCoverage[k];
            bool replacement = _tiles.TryGetValue(k, out Resident? oldResident);
            if (replacement && oldResident!.Data.SourceTier == coverage.Tier && Mathf.IsEqualApprox(oldResident.Data.SourceFraction, coverage.Fraction)) { DiagnosticOutcome(k, "request_unchanged"); continue; }
            long preparationStart = DiagnosticTimestamp();
            int oldCredit = replacement ? oldResident!.Data.InstanceCount : 0;
            int reserve = Math.Min(MaximumGrassSourcesPerTile, Math.Max(0, MaximumGrassSourceInstances - ResidentGrassInstanceCount - _reserved + oldCredit));
            if (reserve == 0)
            {
                DiagnosticOutcome(k, "root_budget_reached");
                DiagnosticRootBudgetBlocked(k, _requests);
                _requests.Clear();
                _requested.Clear();
                break;
            }
            DiagnosticStarted(k);
            var c = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            RingTerrainSampler? ownedSampler = null;
            SmoothTerrainVolumetricWorkerContext? fieldWorkerContext = null;
            Job job;
            try
            {
                fieldWorkerContext = field?.CreateWorkerContext();
                // Production density contexts own a pooled sampler. Only allocate
                // a fallback when the context cannot provide one.
                if (fieldWorkerContext?.Sampler == null) ownedSampler = s.CreateIndependentCopy();
                var bounds = RingGrassCoordinates.Bounds(k.Around, k.Across, TileSize, s.AroundRingVoxelLength, s.RingAcrossWidthMeters);
                ulong epoch = _epoch;
                float cell = CellSize, slope = Mathf.Clamp(MaximumSlopeDegrees, 0, 60);
                int candidates = Mathf.Clamp(CandidatesPerCell, 1, 16);
                float density = Mathf.Clamp(BladesPerSquareMeter, 0, 96);
                bool useGrassClumps = GrassBladesPerSource > 1;
                bool meadowTufts = EnableMeadowTufts && useGrassClumps;
                float meadowSpread = EffectiveMeadowSpread;
                float minimumHeight = GrassMinimumHeightMeters, maximumHeight = GrassMaximumHeightMeters;
                RingGrassModifierSnapshot? modifiers = CaptureGrassModifiers(s);
                float modifierDensityStrength = Mathf.Clamp(GrassModifierDensityStrength, 0, 1);
                RingGrassAppearanceSnapshot appearance = GrassAppearanceProfile?.CapturePlacement() ?? RingGrassAppearanceSnapshot.Default;
                job = new Job
                {
                    Key = k,
                    Cts = c,
                    Epoch = epoch,
                    Revision = e.Version,
                    FieldIdentity = field?.Identity,
                    FieldWorkerContext = fieldWorkerContext,
                    Exclusions = exclusions,
                    Reserve = reserve,
                    SourceFraction = coverage.Fraction,
                    SourceTier = coverage.Tier,
                    Dirty = false,
                    Modifiers = modifiers
                };
                job.DiagnosticTrace = DiagnosticTrace(k);
                job.DiagnosticSession = job.DiagnosticTrace?.Session ?? 0;
                job.RequestTick = job.DiagnosticTrace?.RequestTick ?? 0;
                job.PreparationStartTick = preparationStart;
                job.PreparationEndTick = DiagnosticTimestamp();
                job.TaskQueuedTick = DiagnosticTimestamp();
                job.Task = Task.Run(() =>
                {
                    job.WorkerStartTick = job.DiagnosticSession != 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    try { return BuildWorker(k, bounds, cell, candidates, density, reserve, slope, ownedSampler, e, g, c.Token, fieldWorkerContext, exclusions, appearance, _surfaceCache, coverage.Fraction, coverage.Tier, useGrassClumps, modifiers, modifierDensityStrength, meadowTufts, meadowSpread, minimumHeight, maximumHeight); }
                    finally { watch.Stop(); job.WorkerMilliseconds = watch.Elapsed.TotalMilliseconds; job.WorkerFinishTick = job.DiagnosticSession != 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; }
                });
            }
            catch
            {
                fieldWorkerContext?.Dispose();
                ownedSampler?.Dispose();
                c.Dispose();
                throw;
            }
            _jobs[k] = job;
            _reserved += reserve;
            TotalGrassBuildCount++;
        }
    }
    private static RingGrassTileData BuildWorker(RingGrassTileKey key, RingGrassTileBounds bounds, float cell, int candidates, float density, int reserve, float slope, RingTerrainSampler? ownedSampler, RingLodSurfaceEditSnapshot edits, RingLodGeometrySnapshot geometry, CancellationToken cancellation, SmoothTerrainVolumetricWorkerContext? fieldWorkerContext, RingGrassExclusionSnapshot? exclusions, RingGrassAppearanceSnapshot appearance, RingGrassSurfaceCache surfaceCache, float sourceFraction, byte sourceTier, bool useGrassClumps, RingGrassModifierSnapshot? modifiers, float modifierDensityStrength, bool meadowTufts, float meadowSpread, float minimumHeight, float maximumHeight)
    {
        using (ownedSampler)
        using (fieldWorkerContext)
        {
            using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Grass build", worker: true);
            return RingGrassTileBuilder.Build(key, bounds, cell, candidates, density, reserve, slope,
                fieldWorkerContext?.Sampler ?? ownedSampler ?? throw new InvalidOperationException("Grass worker requires a sampler."), edits, geometry, cancellation,
                fieldWorkerContext?.Field, exclusions, appearance, surfaceCache: surfaceCache, sourceFraction: sourceFraction, sourceTier: sourceTier, useGrassClumps: useGrassClumps, modifiers: modifiers, modifierDensityStrength: modifierDensityStrength, meadowTufts: meadowTufts, meadowSpread: meadowSpread, minimumHeight: minimumHeight, maximumHeight: maximumHeight, useConfiguredHeightRange: true);
        }
    }
    private void Drain(ulong revision, SmoothTerrainVolumetricDensitySnapshot? field, RingGrassExclusionSnapshot? exclusions)
    {
        using var scope = RingPerformanceTelemetry.MeasureCpu("Grass completed builds");
        _keys.Clear();
        foreach (var p in _jobs) if (p.Value.Task.IsCompleted) _keys.Add(p.Key);
        if (_keys.Count > 0) _requestQueueDirty = true;
        foreach (var k in _keys)
        {
            var j = _jobs[k];
            DiagnosticDrained(k, j);
            _jobs.Remove(k);
            _reserved -= j.Reserve;
            LastWorkerBuildMilliseconds = j.WorkerMilliseconds;
            try
            {
                if (!StreamingSuspended && j.Task.Status == TaskStatus.RanToCompletion && j.Task.Result is { } d
                    && !j.Dirty && j.Epoch == _epoch && _wanted.Contains(k)
                    && j.Revision == revision && j.FieldIdentity == field?.Identity && SameExclusions(j.Exclusions, exclusions)
                    && SameModifiers(j.Modifiers, _modifierSnapshot)
                    && d.InstanceCount <= MaximumGrassSourceInstances - ResidentGrassInstanceCount
                        + (_tiles.TryGetValue(k, out Resident? previous) ? previous.Data.InstanceCount : 0))
                {
                    // Retain renderer nodes until a fully built replacement is
                    // accepted.  This makes tier changes a resource handoff,
                    // not a visible hole while the worker is running.
                    if (_tiles.TryGetValue(k, out Resident? prior))
                    {
                        ResidentGrassInstanceCount -= prior.Data.InstanceCount;
                        prior.Data = d;
                        prior.MaximumSourceHeight = MaximumSourceHeight(d);
                        _tiles[k] = prior;
                        GrassTierRebuildCount++;
                    }
                    else _tiles[k] = new Resident { Data = d, MaximumSourceHeight = MaximumSourceHeight(d) };
                    ResidentGrassInstanceCount += d.InstanceCount;
                    AdaptiveLeafChanged(k);
                    InvalidateGrassWork();
                    RecountTierRoots();
                    if (_uploadQueued.Add(k)) _uploads.Enqueue(k);
                    DiagnosticReady(k, j, d);
                }
                else DiagnosticOutcome(k, j, j.Task.IsCanceled ? "cancelled" : j.Task.IsFaulted ? "faulted" : "stale_or_rejected");
            }
            finally
            {
                j.Cts.Dispose();
            }
        }
    }
    private void Install()
    {
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Grass install");
        ulong start = Time.GetTicksUsec();
        int n = InstallAdaptiveOutputs(start);
        while (n < Math.Max(1, MaximumTileUploadsPerFrame) && _uploads.Count > 0 && (Time.GetTicksUsec() - start) / 1000.0 <= Math.Max(.1, MaximumTileUploadMilliseconds))
        {
            var k = _uploads.Dequeue();
            _uploadQueued.Remove(k);
            if (!_tiles.TryGetValue(k, out var t) || _mesh == null || _material == null) continue;
            if (_adaptiveLeafOwners.TryGetValue(k, out var owner) && owner.Active && !owner.Splitting) continue;
            bool installNear = !_presentationOutputCullingActive || t.NearWanted;
            bool installIntermediate = EnableDistanceLod && (!_presentationOutputCullingActive || t.IntermediateWanted);
            if (!installNear && !installIntermediate) continue;
            InstallGrassModifierTint(k, t.Data);
            long installStart = DiagnosticTimestamp();
            // A late intermediate-LOD upload must not reallocate a MultiMesh
            // still referenced by compute. Keep the node, replace its resource.
            if (installNear && t.GpuNearBatch >= 0)
            {
                _gpuCompute?.RemoveBatch(t.GpuNearBatch);
                ReplaceCpuMultiMesh(t.Renderer, NearMeshFor(t.Data), t.Data.LocalAabb, t.Data.Buffer);
                t.GpuNearBatch = -1;
                SetGpuCulled(t.Renderer, false, t.Presentation.Near);
            }
            if (installIntermediate && t.GpuIntermediateBatch >= 0)
            {
                _gpuCompute?.RemoveBatch(t.GpuIntermediateBatch);
                ReplaceCpuMultiMesh(t.IntermediateRenderer, IntermediateMeshFor(t.Data), LodAabb(t.Data.LocalAabb), t.Data.Buffer);
                t.GpuIntermediateBatch = -1;
                SetGpuCulled(t.IntermediateRenderer, false, t.Presentation.Intermediate);
            }
            var r = t.Renderer;
            if (installNear && r == null)
            {
                r = new GrassPresentationRenderer
                {
                    Name = $"StandingGrass_{k.Around}_{k.Across}",
                    MaterialOverride = _material,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    ExtraCullMargin = 6,
                    Visible = false,
                    Multimesh = new MultiMesh
                    {
                        TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                        UseCustomData = true,
                        UseColors = true,
                        Mesh = NearMeshFor(t.Data)
                    }
                };
                AddChild(r);
                t.Renderer = r;
            }
            if (installNear && r != null)
            {
                r.Multimesh!.InstanceCount = t.Data.InstanceCount;
                r.Multimesh.VisibleInstanceCount = t.Data.InstanceCount;
                r.Multimesh.Buffer = t.Data.Buffer;
                r.Multimesh.CustomAabb = t.Data.LocalAabb;
                r.Multimesh.Mesh = NearMeshFor(t.Data);
                ConfigureLodPass(r, t.Data.InstanceCount, false, k);
            }
            if (installIntermediate && _intermediateMesh != null && _intermediateMaterial != null)
            {
                Mesh lodMesh = IntermediateMeshFor(t.Data);
                MultiMeshInstance3D ir = t.IntermediateRenderer ??= CreateRenderer(k, lodMesh, _intermediateMaterial, "Intermediate");
                ir.Multimesh!.Mesh = lodMesh;
                ir.Multimesh!.InstanceCount = t.Data.InstanceCount;
                ir.Multimesh.VisibleInstanceCount = t.Data.InstanceCount;
                ir.Multimesh.Buffer = t.Data.Buffer;
                ir.Multimesh.CustomAabb = LodAabb(t.Data.LocalAabb);
                ConfigureLodPass(ir, t.Data.InstanceCount, true, k);
            }
            if (r is GrassPresentationRenderer nearPresentation)
                nearPresentation.RecordInstalledMultiMesh(r.Multimesh!, t.Data.InstanceCount);
            if (t.IntermediateRenderer is GrassPresentationRenderer intermediatePresentation)
                intermediatePresentation.RecordInstalledMultiMesh(intermediatePresentation.Multimesh!, t.Data.InstanceCount);
            TotalGrassUploadCount++;
            _adaptiveCandidateRetryNeeded = true;
            if (_adaptiveCandidateCursor >= _adaptiveCandidates.Count) _adaptiveCandidateCursor = 0;
            InvalidateGrassWork();
            DiagnosticInstalled(k, t.Data, installStart, DiagnosticTimestamp());
            n++;
        }
        LastInstallMilliseconds = (Time.GetTicksUsec() - start) / 1000.0;
    }
    private void Transforms(RingLodGeometrySnapshot g, RingCanonicalPosition focus, long circumference, bool refreshSelection = true)
    {
        using var scope = RingPerformanceTelemetry.MeasureCpu("Grass presentation (inclusive)");
        ConfigureLodMaterials();
        if (refreshSelection) PrepareGrassPresentation(g, focus, circumference);
        var transformKey = CaptureGrassTransformWorkKey(g);
        if (CanCacheGrassPresentation && _transformWorkKey == transformKey)
        {
            GrassPresentationCacheHits++;
            RequestGpuCulling();
            UpdateGrassDebugBounds();
            return;
        }
        GrassPresentationScans++;
        int tiles = 0, instances = 0;
        foreach (Resident resident in ActiveGrassResidents())
        {
            MultiMeshInstance3D? near = resident.Renderer;
            MultiMeshInstance3D? intermediate = resident.IntermediateRenderer;
            bool installed = false;
            if (near != null)
            {
                RefreshTilePresentation(resident.Presentation, near, resident.Data, g, _presentationInverseLayer, false);
                int prefix = Math.Min(resident.PresentationPrefix, PresentationInstanceCount(near));
                if (resident.GpuNearBatch < 0)
                {
                    SetPresentationPrefix(near, prefix);
                    SetRendererVisible(near, resident.NearWanted);
                }
                if (resident.NearWanted) { instances += prefix; installed = true; }
            }
            if (intermediate != null)
            {
                RefreshTilePresentation(resident.Presentation, intermediate, resident.Data, g, _presentationInverseLayer, true);
                int prefix = Math.Min(resident.PresentationPrefix, PresentationInstanceCount(intermediate));
                if (resident.GpuIntermediateBatch < 0)
                {
                    SetPresentationPrefix(intermediate, prefix);
                    SetRendererVisible(intermediate, resident.IntermediateWanted && AdaptiveIntermediateVisible(resident));
                }
                if (resident.IntermediateWanted) { instances += prefix; installed = true; }
            }
            if (installed) tiles++;
        }
        RenderedGrassTileCount = tiles;
        InstalledGrassClumpCount = instances;
        _transformWorkKey = CaptureGrassTransformWorkKey(g);
        RequestGpuCulling();
        UpdateGrassDebugBounds();
    }
    private void Invalidate(RingLodSurfaceEditSnapshot e, RingTerrainSampler s)
    {
        using var scope = RingPerformanceTelemetry.MeasureCpu("Grass terrain invalidation");
        bool sequential = _revision != ulong.MaxValue && e.Version == _revision + 1 && e.CanonicalDirtyColumns.Count > 0;
        _revision = e.Version;
        if (!sequential)
        {
            _epoch++;
            ClearTiles();
            Cancel(null);
            return;
        }
        _keys.Clear();
        long n = RingGrassCoordinates.AroundTileCount(s.AroundRingVoxelLength, TileSize);
        foreach (var d in e.CanonicalDirtyColumns)
        {
            long amin = (long)Math.Floor((d.AroundVoxel - 2) / (double)TileSize), amax = (long)Math.Floor((d.AroundVoxel + 2) / (double)TileSize), bmin = (long)Math.Floor((d.AcrossVoxel - 2) / (double)TileSize), bmax = (long)Math.Floor((d.AcrossVoxel + 2) / (double)TileSize);
            for (long z = bmin; z <= bmax; z++) for (long x = amin; x <= amax; x++) if (z >= 0) _keys.Add(new RingGrassTileKey(RingGrassCoordinates.WrapTileIndex(x, n), z));
        }
        foreach (var k in _keys) Remove(k);
        foreach (var p in _jobs)
        {
            if (_keys.Contains(p.Key))
            {
                p.Value.Dirty = true;
                p.Value.Cts.Cancel();
            }
            else p.Value.Revision = e.Version;
        }
    }
    private void InvalidateExclusions(RingGrassExclusionSnapshot? current, RingTerrainSampler sampler)
    {
        using var scope = RingPerformanceTelemetry.MeasureCpu("Grass exclusion invalidation");
        if (SameExclusions(_previousExclusions, current)) return;
        float clumpMargin = GrassBladesPerSource > 1 ? Mathf.Max(.3f, .082f * 3.7f) * (EnableMeadowTufts ? EffectiveMeadowSpread : 1f) : 0;
        RingGrassExclusionRegion[] dirty = current?.GetDirtyRegionsSince(_previousExclusions, clumpMargin)
            ?? _previousExclusions?.GetDirtyRegionsSince(null, clumpMargin) ?? Array.Empty<RingGrassExclusionRegion>();
        _previousExclusions = current;
        if (dirty.Length == 0)
        {
            // A new revision with identical occupancy also leaves running
            // builders valid; do not discard their unchanged placement.
            foreach (Job job in _jobs.Values) job.Exclusions = current;
            return;
        }
        _keys.Clear();
        var candidates = new HashSet<RingGrassTileKey>(_tiles.Keys);
        candidates.UnionWith(_jobs.Keys);
        candidates.UnionWith(_requested);
        candidates.UnionWith(_wanted);
        foreach (RingGrassTileKey key in candidates)
        {
            RingGrassTileBounds bounds = RingGrassCoordinates.Bounds(key.Around, key.Across, TileSize,
                sampler.AroundRingVoxelLength, sampler.RingAcrossWidthMeters);
            foreach (RingGrassExclusionRegion region in dirty)
                if (RegionOverlaps(bounds, region)) { _keys.Add(key); break; }
        }
        foreach (RingGrassTileKey key in _keys) Remove(key);
        foreach (var job in _jobs)
        {
            if (_keys.Contains(job.Key)) { job.Value.Dirty = true; job.Value.Cts.Cancel(); }
            else job.Value.Exclusions = current;
        }
    }
    private static bool RegionOverlaps(in RingGrassTileBounds bounds, in RingGrassExclusionRegion region)
        => bounds.AroundMin < region.AroundMax && bounds.AroundMax > region.AroundMin
            && bounds.AcrossMin < region.AcrossMax && bounds.AcrossMax > region.AcrossMin;
    private static bool SameExclusions(RingGrassExclusionSnapshot? left, RingGrassExclusionSnapshot? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null || left.Revision != right.Revision || left.AroundLength != right.AroundLength || left.Boxes.Count != right.Boxes.Count) return false;
        for (int i = 0; i < left.Boxes.Count; i++) if (left.Boxes[i] != right.Boxes[i]) return false;
        return true;
    }
    private void Evict()
    {
        using var scope = RingPerformanceTelemetry.MeasureCpu("Grass eviction");
        _keys.Clear();
        foreach (var k in _tiles.Keys) if (!_wanted.Contains(k)) _keys.Add(k);
        foreach (var k in _keys) Remove(k);
    }
    private void Remove(RingGrassTileKey k)
    {
        _requestQueueDirty = true;
        ReleaseGrassModifierTint(k);
        if (_tiles.Remove(k, out var t))
        {
            AdaptiveLeafChanged(k);
            InvalidateGrassWork();
            ResidentGrassInstanceCount -= t.Data.InstanceCount;
            RecountTierRoots();
            if (_gpuCompute != null)
            {
                if (t.GpuNearBatch >= 0) _gpuCompute.RemoveBatch(t.GpuNearBatch);
                if (t.GpuIntermediateBatch >= 0) _gpuCompute.RemoveBatch(t.GpuIntermediateBatch);
            }
            if (t.Renderer != null) t.Renderer.Visible = false;
            if (t.IntermediateRenderer != null) t.IntermediateRenderer.Visible = false;
            if (t.Renderer != null) t.Renderer.QueueFree();
            if (t.IntermediateRenderer != null) t.IntermediateRenderer.QueueFree();
        }
    }
    private void ClearTiles()
    {
        ClearAdaptiveGroups();
        _keys.Clear();
        _keys.AddRange(_tiles.Keys);
        foreach (var k in _keys) Remove(k);
        DisposeGrassModifierTint();
        _uploads.Clear();
        _uploadQueued.Clear();
        RecountTierRoots();
        UpdateGpuDiagnostics();
    }
    private void Cancel(HashSet<RingGrassTileKey>? wanted)
    {
        foreach (var p in _jobs) if (wanted == null || !wanted.Contains(p.Key)) p.Value.Cts.Cancel();
    }
    private void HideRenderers()
    {
        InvalidateGrassWork();
        foreach (var t in ActiveGrassResidents())
        {
            t.NearWanted = t.IntermediateWanted = false;
            SetRendererVisible(t.Renderer, false);
            SetRendererVisible(t.IntermediateRenderer, false);
        }
        RenderedGrassTileCount = InstalledGrassClumpCount = 0;
    }
    private bool ShouldSuspendStreaming(float altitude, float speed) => !Enabled
        || altitude > MaximumViewerAltitudeAboveTerrainMeters
        || speed > SuspendStreamingAboveSpeedMetersPerSecond;

    private void SuspendStreaming()
    {
        if (!StreamingSuspended)
        {
            StreamingSuspended = true;
            Visible = false;
            // Invalidate completed-but-undrained workers before they can
            // repopulate uploads, and retire native renderers rather than
            // leaving hidden MultiMeshes in the rendering scenario.
            Reset(clearSurfaceCache: false);
            _wanted.Clear();
            _wantedCoverage.Clear();
            _ordered.Clear();
            _gpuCompute?.Dispose();
            _gpuCompute = null;
            _gpuFrames.Clear();
            _gpuReadyBatches.Clear();
            UpdateGpuDiagnostics();
            GpuCullingStatus = "Streaming suspended";
            GrassFrustumRejectedTileCount = GrassDistanceRejectedTileCount = 0;
            HideRenderers();
            _interaction.Clear();
            PublishGrassInteraction();
        }
        // Cancellation can finish asynchronously. Reap jobs and their reserved
        // capacity without sampling terrain, planning, or installing results.
        Drain(_revision, _fieldSnapshot, _previousExclusions);
    }
    private void Reset(bool clearSurfaceCache = true)
    {
        _requestQueueDirty = true;
        _epoch++;
        ClearTiles();
        Cancel(null);
        _requests.Clear();
        _requested.Clear();
        _focusA = _focusB = long.MinValue;
        _hasCachedPlan = false;
        if (clearSurfaceCache) _surfaceCache.Clear();
    }
    private (float Fraction, byte Tier) CoverageForDistance(double distanceMeters)
    {
        // Without the complementary far presentation pass, thinning source
        // roots would make the legacy near mesh visibly sparse.  Preserve
        // exact legacy coverage until distance LOD is enabled.
        if (!EnableHierarchicalCoverage || !EnableDistanceLod) return (1, 0);
        float near = Mathf.Max(1, LodNearDistanceMeters);
        float mid = Mathf.Clamp(LodMidDistanceMeters, near + 1, Mathf.Max(near + 2, LodFarDistanceMeters - 1));
        if (distanceMeters <= near) return (1, 0);
        if (distanceMeters <= mid) return (Mathf.Clamp(LodMidSourceDensity, .01f, 1), 1);
        return (Mathf.Clamp(LodFarSourceDensity, .01f, 1), 2);
    }
    private ulong CoverageFingerprint()
    {
        ulong h = EnableHierarchicalCoverage ? 1UL : 0UL;
        h = unchecked(h * 1099511628211UL ^ (EnableDistanceLod ? 1UL : 0UL));
        h = unchecked(h * 1099511628211UL ^ (uint)BitConverter.SingleToInt32Bits(LodNearDistanceMeters));
        h = unchecked(h * 1099511628211UL ^ (uint)BitConverter.SingleToInt32Bits(LodMidDistanceMeters));
        h = unchecked(h * 1099511628211UL ^ (uint)BitConverter.SingleToInt32Bits(LodFarDistanceMeters));
        h = unchecked(h * 1099511628211UL ^ (uint)BitConverter.SingleToInt32Bits(LodMidSourceDensity));
        h = unchecked(h * 1099511628211UL ^ (uint)BitConverter.SingleToInt32Bits(LodFarSourceDensity));
        return unchecked(h * 1099511628211UL ^ (uint)BitConverter.SingleToInt32Bits(LodTransitionWidthMeters));
    }
    private bool CanReusePlan(RingCanonicalPosition focus, RingTerrainSampler sampler)
    {
        double around = Math.Abs(focus.AroundMeters - _lastPlannedFocus.AroundMeters);
        double circumference = Math.Max(1, sampler.AroundRingVoxelLength);
        around %= circumference;
        if (around > circumference * .5) around = circumference - around;
        double across = Math.Abs(focus.AcrossMeters - _lastPlannedFocus.AcrossMeters);
        double movement = Math.Sqrt(around * around + across * across);
        // A resident cap can prevent safe reuse while moving because a new
        // nearest tile may enter the selected prefix.  At the exact same
        // focus, however, membership and ordering are identical, so retain
        // the cached plan even when that cap is saturated.
        if (movement <= 1e-6) return true;
        if (_ordered.Count >= Math.Max(1, MaximumResidentTiles)) return false;
        if (movement > _cachedPlanReuseMargin) return false;
        // A tier boundary requires a replacement, so it deliberately exits
        // the fast path even though the tile set itself remains safe.
        foreach (var entry in _ordered)
        {
            if (!_wantedCoverage.TryGetValue(entry.Item1, out var previous)) return false;
            RingGrassTileBounds bounds = RingGrassCoordinates.Bounds(entry.Item1.Around, entry.Item1.Across,
                TileSize, sampler.AroundRingVoxelLength, sampler.RingAcrossWidthMeters);
            var current = CoverageForDistance(Math.Sqrt(RingGrassCoordinates.TileDistanceSquared(bounds, focus, sampler.AroundRingVoxelLength)));
            if (current.Tier != previous.Tier || !Mathf.IsEqualApprox(current.Fraction, previous.Fraction)) return false;
        }
        return true;
    }
    private void RecountTierRoots()
    {
        ResidentGrassNearRootCount = ResidentGrassMidRootCount = ResidentGrassFarRootCount = 0;
        foreach (Resident resident in _tiles.Values)
        {
            switch (resident.Data.SourceTier)
            {
                case 0: ResidentGrassNearRootCount += resident.Data.InstanceCount; break;
                case 1: ResidentGrassMidRootCount += resident.Data.InstanceCount; break;
                default: ResidentGrassFarRootCount += resident.Data.InstanceCount; break;
            }
        }
    }
    private void PruneUploads()
    {
        int count = _uploads.Count;
        for (int i = 0; i < count; i++)
        {
            var key = _uploads.Dequeue();
            if (_wanted.Contains(key) && _tiles.TryGetValue(key, out Resident? resident)
                && (!_presentationOutputCullingActive || resident.NearWanted || resident.IntermediateWanted)) _uploads.Enqueue(key);
            else _uploadQueued.Remove(key);
        }
    }
    private ulong Fingerprint(RingTerrainSampler s, RingLodGeometrySnapshot g)
    {
        ulong h = 1469598103934665603UL;
        void M(ulong v)
        {
            h ^= v;
            h *= 1099511628211UL;
        }
        M((ulong)s.Seed);
        M(s.TerrainDefinitionHash);
        M((ulong)s.AroundRingVoxelLength);
        M((ulong)s.RingAcrossWidthMeters);
        M((ulong)BitConverter.SingleToInt32Bits(TileSize));
        M((ulong)BitConverter.SingleToInt32Bits(CellSize));
        M((ulong)BitConverter.SingleToInt32Bits(Mathf.Max(0, VisibilityRadiusMeters)));
        M((ulong)CandidatesPerCell);
        M((ulong)BitConverter.SingleToInt32Bits(BladesPerSquareMeter));
        M(UseGrassClumps ? 1UL : 0UL);
        M((ulong)GrassBladesPerSource);
        M(EnableMeadowTufts ? 1UL : 0UL);
        M((ulong)Math.Clamp(MeadowTuftVariant, 0, 3));
        M((ulong)BitConverter.SingleToInt32Bits(EffectiveMeadowSpread));
        M((ulong)BitConverter.SingleToInt32Bits(GrassMinimumHeightMeters));
        M((ulong)BitConverter.SingleToInt32Bits(GrassMaximumHeightMeters));
        M((ulong)MaximumInstancesPerTile);
        M((ulong)MaximumInstances);
        M((ulong)MaximumResidentTiles);
        M((ulong)Math.Max(1, MaximumTileBuildsInFlight));
        M((ulong)Math.Max(1, MaximumTileUploadsPerFrame));
        M((ulong)BitConverter.SingleToInt32Bits(Mathf.Clamp(MaximumSlopeDegrees, 0, 60)));
        RingGrassAppearanceSnapshot appearance = GrassAppearanceProfile?.CapturePlacement() ?? RingGrassAppearanceSnapshot.Default;
        M(appearance.Enabled ? 1UL : 0UL);
        M((ulong)BitConverter.SingleToInt32Bits(appearance.GrasslandDensityMultiplier));
        M((ulong)BitConverter.SingleToInt32Bits(appearance.ForestDensityMultiplier));
        M((ulong)BitConverter.SingleToInt32Bits(appearance.SparseDensityMultiplier));
        M((ulong)BitConverter.SingleToInt32Bits(appearance.GrasslandHeightMultiplier));
        M((ulong)BitConverter.SingleToInt32Bits(appearance.ForestHeightMultiplier));
        M((ulong)BitConverter.SingleToInt32Bits(appearance.SparseHeightMultiplier));
        M((ulong)BitConverter.DoubleToInt64Bits(g.RingRadiusMeters));
        M((ulong)BitConverter.DoubleToInt64Bits(g.RingVerticalBaseOffsetMeters));
        return h;
    }
    internal TileAudit[] CaptureTileAuditsForTests()
    {
        var a = new TileAudit[_tiles.Count];
        int i = 0;
        foreach (var p in _tiles)
        {
            var r = p.Value.Renderer;
            MultiMeshInstance3D? intermediate = p.Value.IntermediateRenderer;
            a[i++] = new TileAudit(p.Key.Around, p.Key.Across, p.Value.Data.PlacementHash, p.Value.Data.InstanceCount,
                r?.GetInstanceId() ?? 0UL, r?.Transform ?? Transform3D.Identity,
                r?.Multimesh?.VisibleInstanceCount ?? 0, intermediate?.Multimesh?.VisibleInstanceCount ?? 0,
                intermediate?.GetInstanceId() ?? 0UL);
        }
        return a;
    }
    internal RingGrassTileData[] CaptureTileDataForTests()
    {
        var data = new RingGrassTileData[_tiles.Count];
        int index = 0;
        foreach (Resident resident in _tiles.Values) data[index++] = resident.Data;
        return data;
    }
    private Mesh NearMeshFor(RingGrassTileData data)
        => SelectNearGrassGeometry(data, data.BladesPerSource > 1 ? _clumpMesh ?? _mesh! : _mesh!);
    private Mesh IntermediateMeshFor(RingGrassTileData data)
        => SelectIntermediateGrassGeometry(data, data.SourceTier >= 2
            ? data.BladesPerSource > 1 ? _farClumpMesh ?? _farMesh ?? _intermediateMesh! : _farMesh ?? _intermediateMesh!
            : data.BladesPerSource > 1 ? _intermediateClumpMesh ?? _intermediateMesh! : _intermediateMesh!);
    private void RefreshGrassGeometrySelection()
    {
        var selection = (UseReducedNearGrassGeometry, IsSingleRibbonIntermediateGrassActive);
        if (_lastGpuGeometrySelection == selection) return;
        InvalidateGrassWork();
        _lastGpuGeometrySelection = selection;
        SplitAllAdaptiveGroups();
        // Rebind resident outputs through the established upload/retirement budget.
        // Geometry experiments do not change placement, reservations, or worker epochs.
        foreach (var key in _tiles.Keys)
            if (_uploadQueued.Add(key)) _uploads.Enqueue(key);
    }
    private void RegisterGpuBatches(RingGrassTileKey key, Resident resident)
    {
        int previousNearBatch = resident.GpuNearBatch, previousIntermediateBatch = resident.GpuIntermediateBatch;
        long gpuRegistrationStart = DiagnosticTimestamp();
        // Test/headless layers have no presentation camera. Leave their CPU
        // MultiMeshes untouched instead of allocating dormant indirect draws.
        if (resident.Data.InstanceCount <= 0 || !UseGpuCulling || _gpuFailureLatched || GetViewport()?.GetCamera3D() == null
            || (resident.Renderer?.Multimesh == null && resident.IntermediateRenderer?.Multimesh == null) || _mesh == null) return;
        _gpuCompute ??= new RingGpuVegetationCompute(GroupGrassComputePhases);
        _gpuCompute.GroupGrassPhases = GroupGrassComputePhases;
        _gpuCompute.Initialize();
        // Do not replace the CPU MultiMesh allocation until the render device
        // has a usable pipeline; this keeps the established fallback visible
        // throughout startup and on unsupported/headless renderers.
        if (!_gpuCompute.Ready) { GpuCullingStatus = _gpuCompute.Status; return; }
        if (resident.GpuNearBatch < 0 && resident.Renderer?.Multimesh != null
            && (!_presentationOutputCullingActive || resident.NearWanted))
            resident.GpuNearBatch = _gpuCompute.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration(
                $"grass-near-{key.Around}-{key.Across}", resident.Renderer.Multimesh, NearMeshFor(resident.Data),
                resident.Data.Buffer, null, resident.Data.InstanceCount, resident.Data.LocalAabb, true));
        Mesh? lodMesh = IntermediateMeshFor(resident.Data);
        if (resident.IntermediateRenderer?.Multimesh != null && lodMesh != null && resident.GpuIntermediateBatch < 0
            && (!_presentationOutputCullingActive || resident.IntermediateWanted))
            resident.GpuIntermediateBatch = _gpuCompute.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration(
                $"grass-intermediate-{key.Around}-{key.Across}", resident.IntermediateRenderer.Multimesh, lodMesh,
                resident.Data.Buffer, resident.GroupMetadata, IsAdaptiveGroup(resident) ? resident.Data.InstanceCount : IntermediateGpuCapacity(resident.Data.InstanceCount), LodAabb(resident.Data.LocalAabb), true));
        DiagnosticGpuRegistered(key, resident, gpuRegistrationStart);
        if (previousNearBatch != resident.GpuNearBatch || previousIntermediateBatch != resident.GpuIntermediateBatch)
            InvalidateGrassWork();
        GpuCullingStatus = _gpuCompute.Status;
        UpdateGpuDiagnostics();
    }
    private void RequestGpuCulling()
    {
        using var scope = RingPerformanceTelemetry.MeasureCpu("Grass GPU request preparation");
        RefreshAdaptivePresentations();
        if (!UseGpuCulling)
        {
            _gpuFailureLatched = false;
            if (_gpuCompute != null) RestoreCpuGrassPresentation();
            _gpuCompute?.Dispose();
            _gpuCompute = null;
            GpuCullingStatus = "CPU fallback";
            return;
        }
        if (_gpuFailureLatched) return;
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera == null) return;
        _gpuCompute ??= new RingGpuVegetationCompute(GroupGrassComputePhases);
        _gpuCompute.GroupGrassPhases = GroupGrassComputePhases;
        _gpuCompute.Initialize();
        if (_gpuCompute.Failed)
        {
            _gpuFailureLatched = true;
            string failure = _gpuCompute.Status;
            RestoreCpuGrassPresentation();
            _gpuCompute.Dispose();
            _gpuCompute = null;
            GpuCullingStatus = failure;
            return;
        }
        if (_gpuFailureLatched || _gpuCompute == null || !_gpuCompute.Ready) { GpuCullingStatus = _gpuCompute?.Status ?? "Initializing GPU backend"; return; }
        // Enabling GPU rendering on an already populated field uses the same
        // tile/time limits as streaming, instead of allocating every tile at once.
        ulong registrationStart = Time.GetTicksUsec();
        int registered = 0;
        bool registrationsRemaining = false;
        if (!_gpuRegistrationComplete) foreach (var pair in _activeGrassPresentations)
        {
            Resident resident = pair.Value;
            bool hasRoots = resident.Data.InstanceCount > 0;
            bool needsNear = hasRoots && resident.Renderer != null && resident.GpuNearBatch < 0
                && (!_presentationOutputCullingActive || resident.NearWanted);
            bool needsIntermediate = hasRoots && EnableDistanceLod && resident.IntermediateRenderer != null && resident.GpuIntermediateBatch < 0
                && (!_presentationOutputCullingActive || resident.IntermediateWanted);
            if (!needsNear && !needsIntermediate) continue;
            if (registered >= Math.Max(1, MaximumTileUploadsPerFrame)
                || (registered > 0 && (Time.GetTicksUsec() - registrationStart) / 1000.0 >= Math.Max(.1, MaximumTileUploadMilliseconds)))
            {
                registrationsRemaining = true;
                break;
            }
            RegisterGpuBatches(pair.Key, resident);
            if ((needsNear && resident.GpuNearBatch < 0) || (needsIntermediate && resident.GpuIntermediateBatch < 0))
                registrationsRemaining = true;
            registered++;
        }
        _gpuRegistrationComplete = !registrationsRemaining;
        Vector4[]? frustum = GpuFrustumCulling ? CachedCameraFrustum(camera) : null;
        var gpuKey = CaptureGrassGpuWorkKey(camera);
        bool reuseGpuFrames = CanCacheGrassPresentation && _gpuWorkKey == gpuKey;
        if (reuseGpuFrames) GrassGpuRequestCacheHits++;
        else
        {
            GrassGpuRequestScans++;
            _gpuFrames.Clear();
            foreach (Resident resident in ActiveGrassResidents())
            {
                AddGpuFrame(_gpuFrames, resident.Presentation.Near, resident.GpuNearBatch, resident.Renderer, 0, resident.NearWanted, resident.NearVisibilityRevision);
                AddGpuFrame(_gpuFrames, resident.Presentation.Intermediate, resident.GpuIntermediateBatch, resident.IntermediateRenderer, 1, resident.IntermediateWanted, resident.IntermediateVisibilityRevision);
            }
            _gpuWorkKey = CaptureGrassGpuWorkKey(camera);
        }
        long completedUpdates = _gpuCompute.CompletedUpdates;
        bool refreshGpuVisibility = !reuseGpuFrames || _lastGrassGpuCompletedUpdates != completedUpdates;
        _lastGrassGpuCompletedUpdates = completedUpdates;
        // Always service the backend, including unchanged requests that still
        // have batches deferred by the dispatch budget.
        if (refreshGpuVisibility) _gpuCompute.CaptureReadyBatches(_gpuFrames, _gpuReadyBatches);
        if (refreshGpuVisibility) CompleteAdaptiveHandoffs();
        bool firstVisibilityHandoff = _gpuReadyBatches.Count != _gpuFrames.Count;
        // Submit the empty transition too, so backend requested/deferred counts
        // describe this camera frame after whole-tile culling hides every pass.
        _gpuCompute.RequestFrame(new RingGpuVegetationCompute.FrameRequest(camera.GlobalPosition,
            Array.Empty<uint>(), _gpuFrames, SkipUnchanged: SkipUnchangedGpuCulling && !firstVisibilityHandoff,
            MaximumBatchesPerDispatch: Mathf.Clamp(MaximumGpuCullingBatchesPerFrame, 16, 1024),
            MaximumCandidatesPerDispatch: Mathf.Clamp(MaximumGpuCullingBatchesPerFrame, 16, 1024) * MaximumGrassSourcesPerTile));
        if (refreshGpuVisibility) foreach (Resident resident in ActiveGrassResidents())
        {
            // A compacted INSTANCE_ID is no longer the canonical root rank.
            // Bypass vertex/fragment LOD from the first indirect draw and keep
            // the output hidden until its first compute dispatch is ready.
            SetGpuCulled(resident.Renderer, resident.GpuNearBatch >= 0, resident.Presentation.Near);
            SetGpuCulled(resident.IntermediateRenderer, resident.GpuIntermediateBatch >= 0, resident.Presentation.Intermediate);
            SetRendererVisible(resident.Renderer, resident.NearWanted
                && (resident.GpuNearBatch < 0 || _gpuReadyBatches.Contains(resident.GpuNearBatch)));
            SetRendererVisible(resident.IntermediateRenderer, resident.IntermediateWanted && AdaptiveIntermediateVisible(resident)
                && (resident.GpuIntermediateBatch < 0 || _gpuReadyBatches.Contains(resident.GpuIntermediateBatch)));
        }
        GpuCullingStatus = _gpuCompute.Status;
        UpdateGpuDiagnostics();
        DiagnosticGpuReadiness();
        void AddGpuFrame(List<RingGpuVegetationCompute.BatchFrameParameters> target, RendererPresentationCache state, int id, MultiMeshInstance3D? renderer, int pass, bool wanted, long visibilityRevision)
        {
            if (id < 0 || renderer == null || !wanted) return;
            target.Add(new RingGpuVegetationCompute.BatchFrameParameters(id, CapturePresentationWorld(state, renderer), true, pass,
                Mathf.Min(LodNearDistanceMeters, EffectiveGrassDrawDistance() - .01f), EffectiveGrassDrawDistance(), LodTransitionWidthMeters, LodNearDensity, LodFarDensity,
                EnableDistanceLod, 1, false, frustum, GrassFrustumPaddingMeters, VisibilityRevision: visibilityRevision));
        }
    }
    private Vector4[] CachedCameraFrustum(Camera3D camera)
    {
        Vector2 viewportSize = GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero;
        var state = new GpuCameraState(camera.GetInstanceId(), camera.GlobalTransform, camera.Projection, camera.KeepAspect,
            camera.Fov, camera.Size, camera.FrustumOffset, camera.Near, camera.Far, camera.HOffset, camera.VOffset, viewportSize);
        if (_gpuFrustum is not null && _gpuFrustumState == state) return _gpuFrustum;
        Godot.Collections.Array<Plane> planes = camera.GetFrustum();
        if (_gpuFrustum is null || _gpuFrustum.Length != planes.Count) _gpuFrustum = new Vector4[planes.Count];
        if (_cameraFrustumPlanes is null || _cameraFrustumPlanes.Length != planes.Count) _cameraFrustumPlanes = new Plane[planes.Count];
        for (int i = 0; i < planes.Count; i++)
        {
            _cameraFrustumPlanes[i] = planes[i];
            _gpuFrustum[i] = new Vector4(planes[i].Normal.X, planes[i].Normal.Y, planes[i].Normal.Z, planes[i].D);
        }
        _gpuFrustumState = state;
        return _gpuFrustum;
    }
    private Plane[] CachedCameraFrustumPlanes(Camera3D camera)
    {
        _ = CachedCameraFrustum(camera);
        return _cameraFrustumPlanes!;
    }
    private static bool AabbIntersectsFrustum(Aabb aabb, IReadOnlyList<Plane> planes)
    {
        Vector3 end = aabb.End;
        for (int i = 0; i < planes.Count; i++)
        {
            Plane plane = planes[i];
            // Camera3D.GetFrustum() uses negative distances inside the camera
            // volume. The input AABB already includes the deformation envelope
            // and configurable padding, so no second guard belongs here.
            Vector3 support = new(
                plane.Normal.X >= 0 ? aabb.Position.X : end.X,
                plane.Normal.Y >= 0 ? aabb.Position.Y : end.Y,
                plane.Normal.Z >= 0 ? aabb.Position.Z : end.Z);
            if (plane.DistanceTo(support) > 0) return false;
        }
        return true;
    }

    private Aabb ExpandPresentationBounds(Aabb aabb, Transform3D transform, float maximumSourceHeight)
    {
        Vector3 x = transform.Basis.X, y = transform.Basis.Y, z = transform.Basis.Z;
        float transformBound = MathF.Sqrt(x.LengthSquared() + y.LengthSquared() + z.LengthSquared());
        if (!float.IsFinite(transformBound) || !float.IsFinite(maximumSourceHeight))
            return aabb.Grow(Math.Max(8, GrassFrustumPaddingMeters));
        // Tile bounds already include a two-height mesh/lean envelope from the
        // builder. Extend only the remaining .25-height conservative bend plus
        // the user-controlled material allowance.
        // The material floors MODEL_MATRIX height at .05 m. Apply that floor
        // after the parent bound, because it is a world-space shader clamp.
        float rawWorldHeight = maximumSourceHeight * transformBound;
        float worldHeight = Math.Max(rawWorldHeight, .05f);
        // Builder bounds include 2x the raw source height. A tiny source gets
        // a larger shader displacement from the .05m floor, so its original
        // bound cannot be credited with that missing extent.
        float envelopeRemainder = rawWorldHeight < .05f ? worldHeight * 2.25f : worldHeight * .25f;
        float padding = envelopeRemainder + Math.Max(0, GrassFrustumPaddingMeters);
        return float.IsFinite(padding) ? aabb.Grow(padding) : aabb.Grow(Math.Max(8, GrassFrustumPaddingMeters));
    }

    private static float MaximumSourceHeight(RingGrassTileData data)
    {
        float maximum = 0;
        for (int offset = 0; offset + RingGrassTileData.FloatsPerInstance <= data.Buffer.Length; offset += RingGrassTileData.FloatsPerInstance)
        {
            float x = data.Buffer[offset + 1], y = data.Buffer[offset + 5], z = data.Buffer[offset + 9];
            float height = MathF.Sqrt(x * x + y * y + z * z);
            if (!float.IsFinite(height)) return float.NaN;
            maximum = Math.Max(maximum, height);
        }
        return maximum;
    }
    private void RestoreCpuGrassPresentation()
    {
        RestoreAdaptiveFineOutputs();
        InvalidateGrassWork();
        foreach (Resident resident in _tiles.Values)
        {
            if (_gpuCompute != null)
            {
                if (resident.GpuNearBatch >= 0) _gpuCompute.RemoveBatch(resident.GpuNearBatch);
                if (resident.GpuIntermediateBatch >= 0) _gpuCompute.RemoveBatch(resident.GpuIntermediateBatch);
            }
            if (resident.GpuNearBatch >= 0) ReplaceCpuMultiMesh(resident.Renderer, NearMeshFor(resident.Data), resident.Data.LocalAabb, resident.Data.Buffer);
            if (resident.GpuIntermediateBatch >= 0) ReplaceCpuMultiMesh(resident.IntermediateRenderer, IntermediateMeshFor(resident.Data), LodAabb(resident.Data.LocalAabb), resident.Data.Buffer);
            resident.GpuNearBatch = resident.GpuIntermediateBatch = -1;
            SetGpuCulled(resident.Renderer, false, resident.Presentation.Near); SetGpuCulled(resident.IntermediateRenderer, false, resident.Presentation.Intermediate);
            if (resident.Renderer != null) resident.Renderer.Visible = resident.NearWanted;
            if (resident.IntermediateRenderer != null) resident.IntermediateRenderer.Visible = resident.IntermediateWanted;
        }
        GpuCullingStatus = "CPU fallback"; GpuCullingBatchCount = 0; GpuCullingBytes = 0;
        GpuCullingSharedSourceBytes = GpuCullingOutputBytes = 0; GpuCullingSharedSourceCount = 0;
    }
    private const int SmallIntermediateFullCapacityRoots = 4096;
    /// <summary>Intermediate grass accepts hash &lt; handoff and a density rank.
    /// The continuous expected demand is h * lerp(near, far, h). Small tiles
    /// stay full-sized, while larger tiles retain a 10% absolute hash-discrepancy
    /// headroom (also at least six sigma of a Bernoulli population). The exact
    /// deterministic sweep regression guards the production seed distribution.</summary>
    private int IntermediateGpuCapacity(int rootCount)
    {
        if (rootCount <= 0) return 1;
        if (!EnableDistanceLod || rootCount <= SmallIntermediateFullCapacityRoots) return rootCount;
        float near = Mathf.Clamp(LodNearDensity, 0.05f, 1f);
        float far = Mathf.Clamp(LodFarDensity, 0.05f, 1f);
        float maximum = far;
        if (near > far)
        {
            float handoff = near / (2f * (near - far));
            if (handoff is > 0f and < 1f)
                maximum = Mathf.Max(maximum, handoff * Mathf.Lerp(near, far, handoff));
        }
        float hashHeadroom = Mathf.Max(.10f, 6f / Mathf.Sqrt(rootCount));
        return Mathf.Clamp(Mathf.CeilToInt(rootCount * Mathf.Min(1f, maximum + hashHeadroom)), 1, rootCount);
    }
    internal int IntermediateGpuCapacityForTests(int rootCount) => IntermediateGpuCapacity(rootCount);
    internal bool IntermediateGpuCapacitySweepForTests(float[] candidates, int samples = 1024)
    {
        if (candidates.Length == 0 || candidates.Length % RingGrassTileData.FloatsPerInstance != 0) return false;
        int count = candidates.Length / RingGrassTileData.FloatsPerInstance;
        int capacity = IntermediateGpuCapacity(count);
        for (int sample = 0; sample <= samples; sample++)
        {
            float handoff = sample / (float)samples;
            float density = Mathf.Lerp(Mathf.Clamp(LodNearDensity, .05f, 1f), Mathf.Clamp(LodFarDensity, .05f, 1f), handoff);
            int accepted = 0;
            for (int id = 0; id < count; id++)
            {
                int offset = id * RingGrassTileData.FloatsPerInstance;
                float rank = (id + .5f) / count;
                float hash = GrassLodHash(candidates[offset + 16], candidates[offset + 17], id);
                if (rank <= density && hash < handoff) accepted++;
            }
            if (accepted > capacity) return false;
        }
        return true;
    }
    private static float GrassLodHash(float x, float y, int id)
    {
        float value = MathF.Sin(x * 12.9898f + y * 78.233f + id * .12345f) * 43758.5453f;
        return value - MathF.Floor(value);
    }
    private void UpdateGpuDiagnostics()
    {
        if (_gpuCompute == null)
        {
            GpuCullingBatchCount = GpuCullingSharedSourceCount = 0;
            GpuCullingBytes = GpuCullingSharedSourceBytes = GpuCullingOutputBytes = 0;
            return;
        }
        GpuCullingBatchCount = _gpuCompute.BatchCount;
        GpuCullingBytes = _gpuCompute.EstimatedBytes;
        GpuCullingSharedSourceBytes = _gpuCompute.SharedSourceBytes;
        GpuCullingOutputBytes = _gpuCompute.OutputBytes;
        GpuCullingSharedSourceCount = _gpuCompute.SharedSourceCount;
    }
    private static void ReplaceCpuMultiMesh(MultiMeshInstance3D? renderer, Mesh? mesh, Aabb aabb, float[] buffer)
    {
        if (renderer == null || mesh == null) return;
        var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = mesh };
        multi.InstanceCount = buffer.Length / RingGrassTileData.FloatsPerInstance;
        multi.VisibleInstanceCount = multi.InstanceCount;
        multi.CustomAabb = aabb;
        multi.Buffer = buffer;
        renderer.Multimesh = multi;
        if (renderer is GrassPresentationRenderer presentation)
            presentation.RecordInstalledMultiMesh(multi, multi.InstanceCount);
    }
    private static void SetRendererVisible(MultiMeshInstance3D? renderer, bool visible)
    {
        if (renderer is GrassPresentationRenderer tracked)
        {
            bool previous = tracked.PresentationVisible ?? renderer.Visible;
            if (previous != visible) renderer.Visible = visible;
            tracked.PresentationVisible = visible;
        }
        else if (renderer != null && renderer.Visible != visible) renderer.Visible = visible;
    }
    private static void SetGpuCulled(MultiMeshInstance3D? renderer, bool enabled, RendererPresentationCache state)
    {
        if (renderer == null) return;
        if (!ReferenceEquals(state.Renderer, renderer))
        {
            state.Renderer = renderer;
            state.Mapping = null;
            state.GustUv = null;
            state.GpuCulled = null;
            state.SnapshotRevision = -1;
        }
        if (state.GpuCulled == enabled) return;
        if (renderer is GrassPresentationRenderer presentation) presentation.CpuVisiblePrefix = null;
        renderer.SetInstanceShaderParameter("grass_gpu_culled", enabled);
        state.GpuCulled = enabled;
    }
    private MultiMeshInstance3D CreateRenderer(RingGrassTileKey key, Mesh mesh, ShaderMaterial material, string suffix)
    {
        var renderer = new GrassPresentationRenderer
        {
            Name = $"StandingGrass_{suffix}_{key.Around}_{key.Across}",
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // Includes the widened blade profile and bounded wind lateral travel.
            ExtraCullMargin = 8,
            Visible = false,
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, UseColors = true, Mesh = mesh }
        };
        AddChild(renderer);
        return renderer;
    }
    private void ConfigureLodPass(MultiMeshInstance3D renderer, int count, bool intermediate, RingGrassTileKey key)
    {
        if (renderer.MaterialOverride is not ShaderMaterial material) return;
        _ = material;
        renderer.SetInstanceShaderParameter("grass_lod_pass", intermediate ? 1 : 0);
        renderer.SetInstanceShaderParameter("grass_instance_count", (float)Math.Max(1, count));
        ConfigureGrassDebugMaterial(renderer, key);
    }
    private int VisiblePrefix(int count, float minimumTileDistanceMeters)
    {
        if (!EnableDistanceLod) return count;
        float width = Mathf.Max(1, LodTransitionWidthMeters);
        float nearDistance = Mathf.Min(LodNearDistanceMeters, EffectiveGrassDrawDistance() - .01f);
        float fraction = Mathf.SmoothStep(0, 1,
            (minimumTileDistanceMeters - (nearDistance - width * .5f)) / width);
        return Mathf.Clamp(Mathf.CeilToInt(count * Mathf.Lerp(LodNearDensity, LodFarDensity, fraction)), 0, count);
    }
    private void ConfigureLodMaterials()
    {
        ConfigureGrassGpuShadingMaterial(_material);
        ConfigureGrassGpuShadingMaterial(_intermediateMaterial);
        ConfigureGrassGustMaterial(_material);
        ConfigureGrassGustMaterial(_intermediateMaterial);
        ConfigureGrassCameraFacingMaterial(_material);
        ConfigureGrassCameraFacingMaterial(_intermediateMaterial);
        ConfigureGrassModifierMaterial(_material);
        ConfigureGrassModifierMaterial(_intermediateMaterial);
        ulong fingerprint = unchecked((ulong)BitConverter.SingleToInt32Bits(LodNearDistanceMeters)
            ^ ((ulong)BitConverter.SingleToInt32Bits(EffectiveGrassDrawDistance()) << 1)
            ^ ((ulong)BitConverter.SingleToInt32Bits(LodTransitionWidthMeters) << 2)
            ^ ((ulong)BitConverter.SingleToInt32Bits(LodNearDensity) << 3)
            ^ ((ulong)BitConverter.SingleToInt32Bits(LodFarDensity) << 4)
            ^ ((ulong)GrassDebugMode << 48)
            ^ (EnableDistanceLod ? 0x9E3779B97F4A7C15UL : 0));
        if (fingerprint == _lodMaterialFingerprint)
        {
            ConfigureGrassAppearance(_material);
            ConfigureGrassAppearance(_intermediateMaterial);
            return;
        }
        _lodMaterialFingerprint = fingerprint;
        ConfigureLodMaterial(_material);
        ConfigureLodMaterial(_intermediateMaterial);
        ConfigureGrassAppearance(_material);
        ConfigureGrassAppearance(_intermediateMaterial);
    }
    private void ConfigureLodMaterial(ShaderMaterial? material)
    {
        if (material == null) return;
        material.SetShaderParameter("grass_lod_enabled", EnableDistanceLod);
        material.SetShaderParameter("grass_lod_near_distance", Mathf.Min(LodNearDistanceMeters, EffectiveGrassDrawDistance() - .01f));
        material.SetShaderParameter("grass_lod_far_distance", EffectiveGrassDrawDistance());
        material.SetShaderParameter("grass_lod_transition_width", LodTransitionWidthMeters);
        material.SetShaderParameter("grass_lod_near_density", LodNearDensity);
        material.SetShaderParameter("grass_lod_far_density", LodFarDensity);
        material.SetShaderParameter("grass_debug_mode", (int)GrassDebugMode);
    }
    private static float ConservativePresentationDistance(RingGrassTileData data, Aabb bounds,
        Vector3? cameraWorld, RingCanonicalPosition focus, long circumference)
    {
        if (cameraWorld == null)
            return (float)Math.Sqrt(RingGrassCoordinates.TileDistanceSquared(data.Bounds, focus, circumference));
        Vector3 end = bounds.End;
        Vector3 point = new(
            Mathf.Clamp(cameraWorld.Value.X, bounds.Position.X, end.X),
            Mathf.Clamp(cameraWorld.Value.Y, bounds.Position.Y, end.Y),
            Mathf.Clamp(cameraWorld.Value.Z, bounds.Position.Z, end.Z));
        // Wind/lean margin prevents a camera just outside the root AABB from
        // losing the first visible blade in a handoff band.
        return Mathf.Max(0, cameraWorld.Value.DistanceTo(point) - 8);
    }
    private static void ConservativeAabbCameraDistances(Aabb bounds, Vector3 camera, out float minimum, out float maximum)
    {
        Vector3 end = bounds.End;
        Vector3 closest = new(
            Mathf.Clamp(camera.X, bounds.Position.X, end.X),
            Mathf.Clamp(camera.Y, bounds.Position.Y, end.Y),
            Mathf.Clamp(camera.Z, bounds.Position.Z, end.Z));
        minimum = Mathf.Max(0, camera.DistanceTo(closest) - 8);
        maximum = 0;
        for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
        {
            Vector3 corner = new(x == 0 ? bounds.Position.X : end.X, y == 0 ? bounds.Position.Y : end.Y,
                z == 0 ? bounds.Position.Z : end.Z);
            maximum = Mathf.Max(maximum, camera.DistanceTo(corner));
        }
        // Keep the same finite wind/lean allowance as the minimum distance.
        maximum += 8;
    }
    private static Aabb LodAabb(Aabb source)
    {
        // Preserve the root pin while widening the intermediate ribbon by the
        // configured finite horizontal allowance.
        Vector3 padding = new(.023f, 0, .023f);
        return new Aabb(source.Position - padding, source.Size + padding * 2);
    }
    internal bool ValidateLodInstanceParametersForTests()
    {
        foreach (Resident resident in _tiles.Values)
        {
            if (resident.Renderer == null || resident.IntermediateRenderer == null) continue;
            foreach (MultiMeshInstance3D renderer in new[] { resident.Renderer, resident.IntermediateRenderer })
            {
                Variant count = renderer.GetInstanceShaderParameter("grass_instance_count");
                if (count.VariantType != Variant.Type.Float
                    || Math.Abs(count.AsSingle() - Math.Max(1, resident.Data.InstanceCount)) > .01f)
                    return false;
            }
        }
        return true;
    }
    private static ShaderMaterial CreateGrassMaterial(int pass)
    {
        var m = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://materials/ring_standing_grass.gdshader")
        };
        global::RingworldTechDemo.RingCloudShadowProjector.Register(m);
        _ = pass;
        return m;
    }
    // Kept for the existing reflection-based GPU wind validation. It is the
    // production legacy clump, while dense placement uses one ribbon/root.
    private static Mesh CreateBladeMesh() => RingGrassMeshFactory.CreateLegacyClump();
    private static Mesh CreateDenseBladeMesh() => RingGrassMeshFactory.Create();
    public override void _ExitTree()
    {
        ReleaseCoordinatedGrassHandoffGlobals();
        _meadowMeshes.Dispose();
        DisposeGrassGustResources();
        DisposeGrassModifierTint();
        CancelGpuGenerationPrototype();
        // Retire render-thread compute resources before the MultiMeshes that
        // own their indirect command buffers are freed.
        _gpuCompute?.Dispose();
        _gpuCompute = null;
        _shutdown.Cancel();
        foreach (var j in _jobs.Values)
        {
            j.Cts.Cancel();
            if (j.Task.IsCompleted) j.Cts.Dispose();
            else _ = j.Task.ContinueWith(_ => j.Cts.Dispose(), TaskScheduler.Default);
        }
        _jobs.Clear();
        _shutdown.Dispose();
    }
}
