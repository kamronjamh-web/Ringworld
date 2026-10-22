using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Complete, low-resolution terrain-aware ring surface for global visibility.</summary>
public partial class RingTerrainGlobalLod : MeshInstance3D
{
    // The launcher opts in before attachment. Each unit retains the original
    // geometry order; a native mesh commit can exceed the soft time budget.
    [Export] public bool UseStagedStartup { get; set; }
    private IEnumerator<bool>? _startupHullBuild;
    private IEnumerator<bool>? _startupOccluderBuild;
    public string StartupError { get; private set; } = string.Empty;
    public bool StartupComplete => StartupError.Length == 0 && (!UseStagedStartup || (_grid != null && _startupHullBuild == null && _startupOccluderBuild == null && HasReadyMorphSnapshot && !InitialGlobalMeshBuildPending));
    public Godot.Collections.Dictionary GetLoadingStatus() => new() { ["ready"] = StartupComplete, ["phase"] = _startupHullBuild != null ? "structural hull" : _startupOccluderBuild != null ? "occluders" : "global terrain", ["pending_segments"] = PendingInitialSegmentCount, ["error"] = StartupError };
    private void ServiceStartupGeometry()
    {
        if (!UseStagedStartup) return;
        try
        {
            long start = Stopwatch.GetTimestamp();
            for (int units = 0; units < 4; units++)
            {
                if (_startupHullBuild != null)
                {
                    if (!_startupHullBuild.MoveNext()) { _startupHullBuild.Dispose(); _startupHullBuild = null; }
                }
                else if (_startupOccluderBuild != null)
                {
                    if (!_startupOccluderBuild.MoveNext()) { _startupOccluderBuild.Dispose(); _startupOccluderBuild = null; }
                }
                else break;
                if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 3) break;
            }
        }
        catch (Exception error) { FailStartup(error.Message); }
    }
    private void FailStartup(string error)
    {
        if (!UseStagedStartup || StartupError.Length > 0) return;
        StartupError = "Global terrain startup failed: " + error;
        _startupHullBuild?.Dispose(); _startupHullBuild = null;
        _startupOccluderBuild?.Dispose(); _startupOccluderBuild = null;
        _heightPyramidCancellation?.Cancel();
        _pendingSamplingCancellation?.Cancel();
        SetProcess(false);
    }
    private readonly record struct SummaryBuildResult(
        RingTerrainMinMaxPyramid Pyramid,
        RingTerrainMinMaxPyramid.DirectionalShadowMask MacroShadowMask,
        bool LoadedFromCache,
        double BuildMilliseconds,
        double ShadowBuildMilliseconds,
        Vector3 MacroShadowSunDirection,
        bool MacroShadowsEnabled,
        bool IncludesStructuralWalls,
        int MacroTraceSteps,
        float MacroTraceDistance);
    private readonly record struct MacroShadowBuildResult(
        RingTerrainMinMaxPyramid.DirectionalShadowMask Mask,
        ulong RequestVersion,
        ulong EditVersion,
        Vector3 SunDirection,
        int TraceSteps,
        float TraceDistance,
        bool IncludesStructuralWalls,
        double BuildMilliseconds);
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath FocusPath { get; set; } = new("../Explorer");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export(PropertyHint.Range, "64,4096,64")] public int AroundSegments { get; set; } = 512;
    [Export(PropertyHint.Range, "4,256,1")] public int AcrossSegments { get; set; } = 32;
    [Export(PropertyHint.Range, "100,100000,100")] public int RingWidthMeters { get; set; } = 20000;
    [Export(PropertyHint.Range, "0,100,0.5")] public float RadialUnderlayOffsetMeters { get; set; } = 10.0f;
    [Export(PropertyHint.Range, "0,50000,100")] public float FadeStartMeters { get; set; } = 4000.0f;
    [Export(PropertyHint.Range, "100,100000,100")] public float FadeEndMeters { get; set; } = 5500.0f;
    [Export] public bool EnableFarDistanceFade { get; set; } = false;
    [Export(PropertyHint.Range, "10000,5000000,10000")] public float FarFadeStartMeters { get; set; } = 500000.0f;
    [Export(PropertyHint.Range, "10000,5000000,10000")] public float FarFadeEndMeters { get; set; } = 1000000.0f;
    [Export(PropertyHint.Range, "10000,5000000,10000")] public float RegionalVisibilityDistanceMeters { get; set; } = 1200000.0f;
    [Export] public Color LowTerrainColor { get; set; } = new(0.14f, 0.22f, 0.14f, 1.0f);
    [Export] public Color HighTerrainColor { get; set; } = new(0.34f, 0.31f, 0.24f, 1.0f);
    [Export] public bool BuildStructuralEdgeWalls { get; set; } = true;
    [Export] public bool BuildStructuralUnderside { get; set; } = true;
    [Export] public bool BuildTerrainRoofUnderside { get; set; } = true;
    [Export] public bool BuildLowerHullCavityFloor { get; set; } = true;
    [Export(PropertyHint.Range, "1000,50000,100")] public float StructuralDepthMeters { get; set; } = 22300.0f;
    [Export(PropertyHint.Range, "1,64,1")] public int HullVerticalSegments { get; set; } = 8;
    [Export] public Color HullColor { get; set; } = new(0.25f, 0.29f, 0.33f, 1.0f);
    [ExportGroup("Night readability")]
    [Export] public Color NightTint { get; set; } = new(0.30f, 0.45f, 0.70f, 1.0f);
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float TerrainNightBrightness { get; set; } = 0.72f;
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float HullNightBrightness { get; set; } = 0.75f;
    [ExportGroup("Structural scale cues")]
    [Export] public bool StructuralScaleCuesEnabled { get; set; } = true;
    [Export(PropertyHint.Range, "25000,1000000,5000")]
    public float StructuralLongitudinalSpacingMeters { get; set; } = 250000.0f;
    [Export(PropertyHint.Range, "100,10000,100")]
    public float StructuralLongitudinalHalfWidthMeters { get; set; } = 1200.0f;
    [Export(PropertyHint.Range, "5000,200000,1000")]
    public float StructuralCrossSpacingMeters { get; set; } = 50000.0f;
    [Export(PropertyHint.Range, "50,5000,50")]
    public float StructuralCrossHalfWidthMeters { get; set; } = 450.0f;
    [Export(PropertyHint.Range, "50,5000,50")]
    public float StructuralTopRailHalfWidthMeters { get; set; } = 350.0f;
    [Export] public Color StructuralCueColor { get; set; } = new(0.08f, 0.72f, 0.82f, 1.0f);
    [Export(PropertyHint.Range, "0,2,0.01")]
    public float StructuralCueDayEnergy { get; set; } = 0.16f;
    [Export(PropertyHint.Range, "0,4,0.01")]
    public float StructuralCueNightEnergy { get; set; } = 0.85f;
    [ExportGroup("Phase C HLOD")]
    [Export(PropertyHint.Range, "4,256,1")] public int GlobalSegmentCount { get; set; } = 64;
    [Export] public bool UseMinMaxFeaturePreservation { get; set; } = true;
    [Export(PropertyHint.Range, "64,4096,64")] public int MinMaxBaseCellSizeMeters { get; set; } = 512;
    [Export(PropertyHint.Range, "10000,2000000,10000")] public int MaxMinMaxBaseCellCount { get; set; } = 1000000;
    [Export] public bool EnableMinMaxDiskCache { get; set; } = true;
    // The min/max summary shares the same disposable lod_cache ceiling as
    // regional terrain samples and vegetation tiles.
    [Export(PropertyHint.Range, "0,8192,64")] public int MinMaxDiskCacheBudgetMiB { get; set; } = 512;
    [Export(PropertyHint.Range, "512,16384,256")] public int MinMaxDiskCacheMinimumFreeMiB { get; set; } = 2048;
    [Export(PropertyHint.Range, "1,8,1")] public int MaxSegmentRebuildsPerFrame { get; set; } = 1;
    [Export(PropertyHint.Range, "0.25,8.0,0.05")] public float MaxSegmentRebuildMillisecondsPerFrame { get; set; } = 2.0f;
    [ExportGroup("Shadows")]
    [Export] public bool CastTerrainShadows { get; set; } = false;
    [Export] public bool EnableMacroTerrainShadows { get; set; } = true;
    [Export] public bool EnableMacroTerrainWallShadows { get; set; } = true;
    [Export(PropertyHint.Range, "0.0,0.8,0.01")] public float MacroTerrainShadowStrength { get; set; } = 0.34f;
    [Export(PropertyHint.Range, "0,10000,100")] public float MacroTerrainShadowFadeStartMeters { get; set; } = 3200.0f;
    [Export(PropertyHint.Range, "100,20000,100")] public float MacroTerrainShadowFadeEndMeters { get; set; } = 6500.0f;
    [Export(PropertyHint.Range, "1,32,1")] public int MacroTerrainShadowTraceSteps { get; set; } = 10;
    [Export(PropertyHint.Range, "4000,320000,1000")] public float MacroTerrainShadowMaxTraceMeters { get; set; } = 96000.0f;

    [ExportGroup("Occlusion Culling")]
    [Export] public bool BuildTerrainOccluder { get; set; } = true;
    [Export(PropertyHint.Range, "32,512,1")] public int OccluderAroundSegments { get; set; } = 128;
    [Export(PropertyHint.Range, "1,256,1")] public float OccluderBedrockSafetyMarginMeters { get; set; } = 8.0f;

    private VoxelChunkGridManager? _grid;
    private Node3D? _focus;
    private DirectionalLight3D? _sunLight;
    private ShaderMaterial? _material;
    private ShaderMaterial? _oceanMaterial;
    private ShaderMaterial? _hullMaterial;
    private readonly List<MeshInstance3D> _hullSegments = new();
    // Roof underside and cavity floor are drawn opaque and would otherwise hide
    // the backdrop where a terrain segment is not built yet, so they are separate
    // instances gated on the matching terrain segment being published.
    private readonly List<MeshInstance3D> _hullRoofSegments = new();
    private int _hullRoofTotalCount = 1;
    private Node3D? _occluderRoot;
    private readonly List<MeshInstance3D> _terrainSegments = new();
    // During a partition-size change, old nodes remain installed until every
    // replacement node has published.  They are intentionally included in the
    // byte estimate through _segmentEstimatedBytes.
    private readonly List<MeshInstance3D> _retainedTerrainSegments = new();
    private readonly HashSet<RingTerrainColumnCoordinate> _knownEditedColumns = new();
    private readonly Queue<int> _dirtySegmentQueue = new();
    private readonly HashSet<int> _queuedDirtySegments = new();
    private RingTerrainMinMaxPyramid? _heightPyramid;
    private Task<SummaryBuildResult>? _heightPyramidTask;
    private Task<RingTerrainSampler.HydrologyAudit>? _hydrologyAuditTask;
    private CancellationTokenSource? _heightPyramidCancellation;
    private Task<MacroShadowBuildResult>? _macroShadowTask;
    private CancellationTokenSource? _macroShadowCancellation;
    private ulong _macroShadowRequestVersion;
    private ulong _macroShadowAppliedVersion;
    private ulong _macroShadowInvalidatedEditVersion;
    private Vector3 _lastMacroShadowRequestedSunDirection = new(float.PositiveInfinity, 0.0f, 0.0f);
    private readonly Queue<int> _initialSegmentQueue = new();
    // Sampling a global segment runs in one cancellable worker.  Only mesh
    // publication is indivisible on the main thread.
    private PendingSegmentPreparation? _pendingPreparation;
    private Task? _pendingSamplingTask;
    private CancellationTokenSource? _pendingSamplingCancellation;
    private ulong _pendingPreparationVersion;
    private bool _pendingSamplingReady;
    private readonly Dictionary<MeshInstance3D, long> _segmentEstimatedBytes = new();
    private RingLodSurfaceEditSnapshot? _initialSegmentEdits;
    private int _initialSegmentCount;
    private string _heightPyramidCachePath = string.Empty;
    private double _visibilityRefreshElapsed;
    // Regional culling is intentionally coarse (twice per second), but the
    // global tier can contain hundreds of terrain and hull nodes.  Remember
    // the input state so a stationary camera does not keep walking every
    // instance merely because the refresh timer elapsed.
    private Vector3 _lastRegionalVisibilityFocusPosition = new(float.PositiveInfinity, 0.0f, 0.0f);
    private float _lastRegionalVisibilityDistance = float.NaN;
    private bool _lastRegionalVisibilityEnabled;
    // The floating origin shifts this node (and every segment) without moving the
    // focus in world space, so an anchor change or a newly published terrain
    // segment must force an immediate refresh of the visible set.
    private bool _regionalVisibilityDirty;
    private Vector3 _lastRegionalVisibilityOrigin = new(float.PositiveInfinity, 0.0f, 0.0f);
    public int RegionalVisibilityRefreshCount { get; private set; }
    private RingLodSurfaceEditSnapshot? _pendingSegmentEdits;
    private ulong _observedEditVersion;
    // The terminal proxy reads only this immutable, fully published receiver
    // topology. A replacement generation stays private until every changed
    // segment has been installed, so a worker never targets mixed geometry.
    private RingTerrainGlobalMorphSnapshot? _readyMorphSnapshot;
    private Dictionary<int, RingTerrainGlobalMorphSnapshot.Segment>? _stagedMorphSegments;
    private int _stagedMorphSegmentCount;
    private ulong _morphGeneration;
    private ulong _stagedMorphEditVersion;
    private Vector3 _lastCameraWorldPosition = new(float.PositiveInfinity, 0.0f, 0.0f);
    private Vector3 _lastLightingRingCenter = new(float.PositiveInfinity, 0.0f, 0.0f);
    private Vector3 _lastLightingSunDirection = new(float.PositiveInfinity, 0.0f, 0.0f);

    public int ActiveSegmentCount => _terrainSegments.Count;
    public int PendingSegmentRebuildCount => _dirtySegmentQueue.Count;
    public int MinMaxLevelCount => _heightPyramid?.LevelCount ?? 0;
    public int MinMaxCellCount => _heightPyramid?.TotalCellCount ?? 0;
    public int LastDirtyMinMaxCellCount => _heightPyramid?.LastDirtyBaseCellCount ?? 0;
    public int LastSegmentRebuildCount { get; private set; }
    public double LastSegmentRebuildMilliseconds { get; private set; }
    public double LastInitialSegmentBuildMilliseconds { get; private set; }
    public double MaxInitialSegmentBuildMilliseconds { get; private set; }
    public bool GlobalSummaryBuildPending => _heightPyramidTask != null;
    public bool InitialGlobalMeshBuildPending => _initialSegmentQueue.Count > 0;
    public int PendingInitialSegmentCount => _initialSegmentQueue.Count;
    public int PendingSegmentSampleCount => _pendingPreparation == null ? 0 : _pendingPreparation.VertexCount - _pendingPreparation.NextVertex;
    public bool GlobalSegmentSamplingWorkerPending => _pendingSamplingTask != null && !_pendingSamplingTask.IsCompleted;
    public long EstimatedSummaryBytes => _heightPyramid?.EstimatedBytes ?? 0L;
    public long EstimatedGlobalMeshBytes { get; private set; }
    public ulong HeightRangeRevision => _heightPyramid == null ? 0UL : _heightPyramid.EditVersion + 1UL;
    public bool HasReadyMorphSnapshot => _readyMorphSnapshot != null;

    /// <summary>Captures the latest complete global receiver surface for a proxy worker.</summary>
    public bool TryCaptureReadyMorphSnapshot(out RingTerrainGlobalMorphSnapshot snapshot)
    {
        snapshot = _readyMorphSnapshot!;
        return snapshot != null;
    }

    public bool TryGetTerrainHeightRange(
        double aroundStartMeters,
        double aroundEndMeters,
        double acrossStartMeters,
        double acrossEndMeters,
        out RingTerrainMinMaxPyramid.HeightRange range)
    {
        if (_heightPyramid == null)
        {
            range = default;
            return false;
        }
        range = _heightPyramid.QueryRegion(
            aroundStartMeters,
            aroundEndMeters,
            acrossStartMeters,
            acrossEndMeters);
        return true;
    }

    public override void _Ready()
    {
        if (!UseStagedStartup) { InitializeStartupOwner(); return; }
        try { InitializeStartupOwner(); }
        catch (Exception error) { FailStartup(error.Message); }
    }

    private void InitializeStartupOwner()
    {
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _focus = GetNodeOrNull<Node3D>(FocusPath);
        _sunLight = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        ResolvePlanetMacroShadowLight();
        if (_grid?.TerrainSampler == null || _focus == null)
        {
            GD.PushWarning("RingTerrainGlobalLod could not resolve the shared sampler or focus.");
            FailStartup("The terrain sampler or player focus is unavailable.");
            return;
        }

        _grid.CanonicalFloatingOriginAnchorChanged += HandleCanonicalFloatingOriginAnchorChanged;

        CreateMaterial();
        if (UseStagedStartup && _material == null) { FailStartup("The global terrain material is unavailable."); return; }
        global::RingworldTechDemo.RingTerrainMacroShadowProjector.Disable();
        StartHeightPyramidBuild();
        if (BuildStructuralEdgeWalls || BuildStructuralUnderside || BuildTerrainRoofUnderside || BuildLowerHullCavityFloor) BuildGlobalHullWalls();
        if (BuildTerrainOccluder) BuildGlobalTerrainOccluder();
    }

    public override void _ExitTree()
    {
        _startupHullBuild?.Dispose(); _startupHullBuild = null;
        _startupOccluderBuild?.Dispose(); _startupOccluderBuild = null;
        InvalidatePendingPreparation();
        Task? samplingTask = _pendingSamplingTask;
        CancellationTokenSource? samplingCancellation = _pendingSamplingCancellation;
        _pendingSamplingTask = null;
        _pendingSamplingCancellation = null;
        _pendingSamplingReady = false;
        if (samplingTask != null)
        {
            _ = samplingTask.ContinueWith(
                static (completed, state) =>
                {
                    _ = completed.Exception;
                    ((CancellationTokenSource?)state)?.Dispose();
                },
                samplingCancellation,
                TaskScheduler.Default);
        }
        else samplingCancellation?.Dispose();
        if (_grid != null)
            _grid.CanonicalFloatingOriginAnchorChanged -= HandleCanonicalFloatingOriginAnchorChanged;
        _heightPyramidCancellation?.Cancel();
        _heightPyramidCancellation?.Dispose();
        _heightPyramidCancellation = null;
        _macroShadowCancellation?.Cancel();
        _macroShadowCancellation?.Dispose();
        _macroShadowCancellation = null;
        CancelPlanetMacroShadowBuild();
        DisablePlanetMacroShadows();
        _macroShadowImage = null;
        _macroShadowTexture = null;
        _planetMacroShadowImage = null;
        _planetMacroShadowTexture = null;
    }

    private void HandleCanonicalFloatingOriginAnchorChanged(
        RingLodGeometrySnapshot previousGeometry,
        RingLodGeometrySnapshot currentGeometry,
        RingCanonicalPosition anchor)
    {
        // Keep the segmented far tier visible and exact at the player while the
        // local proxy tiers refresh. Rebuilding every whole-ring segment here
        // caused a large main-thread spike and a temporary missing far field.
        Vector3 anchorDelta = currentGeometry.CanonicalToWorld(
                anchor.AroundMeters, anchor.VerticalMeters, anchor.AcrossMeters)
            - previousGeometry.CanonicalToWorld(
                anchor.AroundMeters, anchor.VerticalMeters, anchor.AcrossMeters);
        GlobalPosition += anchorDelta;
        _regionalVisibilityDirty = true;
        // Prepared vertices were expressed in the old local coordinate frame.
        // Keep the queue head and restart it from a coherent snapshot instead.
        InvalidatePendingPreparation();
    }

    public override void _Process(double delta)
    {
        if (!UseStagedStartup || StartupComplete) { ProcessGlobalTerrain(delta); return; }
        if (StartupError.Length > 0) return;
        try { ProcessGlobalTerrain(delta); }
        catch (Exception error) { FailStartup(error.Message); }
    }

    private void ProcessGlobalTerrain(double delta)
    {
        ServiceStartupGeometry();
        if (StartupError.Length > 0) return;
        // Underground-mode hide: surface segments are invisible, so skip lighting, shadow and rebuild service.
        if (UndergroundHidden && StartupComplete) return;
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Terrain global LOD update (inclusive)");
        _ = delta;
        UpdateCameraUniform();
        UpdateLightingUniforms();
        UpdateRegionalSegmentVisibility(delta);
        CompleteHeightPyramidBuildIfReady();
        CompleteHydrologyAuditIfReady();
        if (StartupError.Length > 0) return;
        CompleteMacroShadowBuildIfReady();
        CompletePlanetMacroShadowBuildIfReady();
        RequestMacroShadowRebuildForSunChange();
        RequestPlanetMacroShadowRebuildForLightChange();
        RefreshEditedSegments();
        ServiceInitialGlobalBuild();
        if (StartupError.Length > 0) return;
        ServiceSegmentRebuilds();
    }

    private void UpdateRegionalSegmentVisibility(double delta)
    {
        if (_focus == null || !EnableFarDistanceFade)
        {
            _lastRegionalVisibilityEnabled = false;
            if (_regionalVisibilityDirty)
            {
                _regionalVisibilityDirty = false;
                RegionalVisibilityRefreshCount++;
                for (int index = 0; index < _hullRoofSegments.Count; index++)
                    UpdateHullRoofVisibility(index);
            }
            return;
        }

        Vector3 focusPosition = _focus.GlobalPosition;
        Vector3 originPosition = GlobalPosition;
        bool configurationChanged = !_lastRegionalVisibilityEnabled
            || _regionalVisibilityDirty
            || !Mathf.IsEqualApprox(_lastRegionalVisibilityDistance, RegionalVisibilityDistanceMeters);
        bool focusChanged = focusPosition != _lastRegionalVisibilityFocusPosition
            || originPosition != _lastRegionalVisibilityOrigin;
        if (!configurationChanged && !focusChanged) return;

        _visibilityRefreshElapsed += delta;
        if (!configurationChanged && _visibilityRefreshElapsed < 0.5) return;
        _visibilityRefreshElapsed = 0.0;
        _regionalVisibilityDirty = false;
        _lastRegionalVisibilityOrigin = originPosition;
        RegionalVisibilityRefreshCount++;
        foreach (MeshInstance3D segment in _terrainSegments)
        {
            UpdateRegionalInstanceVisibility(segment);
        }
        foreach (MeshInstance3D segment in _retainedTerrainSegments)
        {
            UpdateRegionalInstanceVisibility(segment);
        }
        foreach (MeshInstance3D segment in _hullSegments)
            UpdateRegionalInstanceVisibility(segment);
        for (int index = 0; index < _hullRoofSegments.Count; index++)
            UpdateHullRoofVisibility(index);
        _lastRegionalVisibilityFocusPosition = focusPosition;
        _lastRegionalVisibilityDistance = RegionalVisibilityDistanceMeters;
        _lastRegionalVisibilityEnabled = true;
    }

    // Hull segment i is gated on the terrain segment covering the same ring arc.
    private bool IsTerrainBehindHullSegmentReady(int hullIndex)
    {
        if (_initialSegmentCount <= 0 && _terrainSegments.Count == 0) return _retainedTerrainSegments.Count > 0;
        // A quality rebuild keeps the retained terrain on screen as the fallback.
        if (_retainedTerrainSegments.Count > 0) return true;
        if (_initialSegmentQueue.Count == 0 && _initialSegmentCount > 0) return true;
        int hullCount = Math.Max(1, _hullRoofTotalCount);
        int terrainIndex = (int)((long)hullIndex * _initialSegmentCount / hullCount);
        return terrainIndex < _terrainSegments.Count && !_initialSegmentQueue.Contains(terrainIndex);
    }

    private void UpdateHullRoofVisibility(int index)
    {
        MeshInstance3D roof = _hullRoofSegments[index];
        if (!GodotObject.IsInstanceValid(roof)) return;
        if (!IsTerrainBehindHullSegmentReady(index)) { roof.Visible = false; return; }
        if (EnableFarDistanceFade) UpdateRegionalInstanceVisibility(roof);
        else roof.Visible = true;
    }

    private void UpdateRegionalInstanceVisibility(MeshInstance3D instance)
    {
        if (_focus == null || instance.Mesh == null) return;
        Aabb bounds = instance.Mesh.GetAabb();
        Vector3 center = instance.ToGlobal(bounds.GetCenter());
        float radius = bounds.Size.Length() * 0.5f;
        instance.Visible = center.DistanceTo(_focus.GlobalPosition) <= RegionalVisibilityDistanceMeters + radius;
    }

    private void StartHeightPyramidBuild()
    {
        if (_grid?.TerrainSampler is not RingTerrainSampler sampler) return;
        if (sampler.AroundRingVoxelLength > int.MaxValue)
        {
            GD.Print("Global terrain HLOD skipped: Mega Ring requires sparse global terrain summaries.");
            return;
        }
        _heightPyramidCancellation?.Cancel();
        _heightPyramidCancellation?.Dispose();
        _heightPyramidCancellation = new CancellationTokenSource();
        CancellationToken cancellation = _heightPyramidCancellation.Token;
        int resolvedCellSize = RingTerrainMinMaxPyramid.ResolveScaleAwareBaseCellSize(
            checked((int)sampler.AroundRingVoxelLength),
            RingWidthMeters,
            MinMaxBaseCellSizeMeters,
            MaxMinMaxBaseCellCount);
        _heightPyramidCachePath = ResolveHeightPyramidCachePath(sampler, resolvedCellSize);
        bool forceCacheRebuild = Array.Exists(
            OS.GetCmdlineUserArgs(),
            argument => argument == "--rebuild-terrain-cache");
        bool diskCacheDisabled = Array.Exists(
            OS.GetCmdlineUserArgs(),
            argument => argument.Equals("--disable-lod-disk-cache", StringComparison.OrdinalIgnoreCase));
        bool readDiskCache = EnableMinMaxDiskCache && !diskCacheDisabled;
        bool writeDiskCache = readDiskCache && MinMaxDiskCacheBudgetMiB > 0;
        string budgetRoot = RingGeneratedLodCacheBudget.ResolveBudgetRoot(_heightPyramidCachePath);
        if (writeDiskCache && !string.IsNullOrEmpty(budgetRoot))
        {
            RingGeneratedLodCacheBudget.Schedule(
                budgetRoot,
                MinMaxDiskCacheBudgetMiB * 1024L * 1024L,
                75,
                TimeSpan.FromSeconds(30));
        }
        RingTerrainSampler workerSampler = sampler.CreateIndependentCopy();
        Vector3 sunDirection = _sunLight?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
        bool buildMacroShadows = EnableMacroTerrainShadows;
        int macroTraceSteps = MacroTerrainShadowTraceSteps;
        float macroTraceDistance = MacroTerrainShadowMaxTraceMeters;
        bool includeStructuralWalls = BuildStructuralEdgeWalls && EnableMacroTerrainWallShadows;
        float structuralWallTopMeters = _grid.GetStructuralWallTopGlobalY();
        float structuralWallBottomMeters = _grid.GetStructuralBottomGlobalY() * VoxelConstants.VoxelSizeMeters;
        _heightPyramidTask = Task.Run(() =>
        {
            using (workerSampler)
            {
                var watch = Stopwatch.StartNew();
                RingTerrainMinMaxPyramid pyramid;
                bool loadedFromCache;
                if (readDiskCache
                    && !forceCacheRebuild
                    && RingTerrainMinMaxPyramid.TryLoadCache(
                        _heightPyramidCachePath,
                        workerSampler,
                        RingWidthMeters,
                        resolvedCellSize,
                        cancellation,
                        out RingTerrainMinMaxPyramid? cached)
                    && cached != null)
                {
                    pyramid = cached;
                    RingGeneratedLodCacheBudget.Touch(_heightPyramidCachePath);
                    loadedFromCache = true;
                }
                else
                {
                    pyramid = new RingTerrainMinMaxPyramid(
                        workerSampler,
                        RingWidthMeters,
                        resolvedCellSize,
                        cancellation);
                    if (writeDiskCache)
                    {
                        TrySaveHeightPyramidCache(
                            pyramid,
                            _heightPyramidCachePath,
                            budgetRoot,
                            MinMaxDiskCacheBudgetMiB * 1024L * 1024L,
                            MinMaxDiskCacheMinimumFreeMiB * 1024L * 1024L,
                            cancellation);
                    }
                    loadedFromCache = false;
                }
                watch.Stop();
                double summaryMilliseconds = watch.Elapsed.TotalMilliseconds;
                var shadowWatch = Stopwatch.StartNew();
                RingTerrainMinMaxPyramid.DirectionalShadowMask macroShadowMask =
                    buildMacroShadows
                        ? pyramid.BuildDirectionalShadowMask(
                            sunDirection,
                            cancellation,
                            macroTraceSteps,
                            macroTraceDistance,
                            includeStructuralWalls: includeStructuralWalls,
                            structuralWallTopMeters: structuralWallTopMeters,
                            structuralWallBottomMeters: structuralWallBottomMeters)
                        : new RingTerrainMinMaxPyramid.DirectionalShadowMask(
                            1,
                            1,
                            new byte[1],
                            0.0f);
                shadowWatch.Stop();
                return new SummaryBuildResult(
                    pyramid,
                    macroShadowMask,
                    loadedFromCache,
                    summaryMilliseconds,
                    shadowWatch.Elapsed.TotalMilliseconds,
                    sunDirection,
                    buildMacroShadows,
                    includeStructuralWalls,
                    macroTraceSteps,
                    macroTraceDistance);
            }
        }, cancellation);
        string cacheMode = !readDiskCache ? "disabled" : forceCacheRebuild ? "forced-rebuild" : "normal";
        GD.Print($"Global min/max summary queued: baseCell={resolvedCellSize}m, circumference={sampler.AroundRingVoxelLength}m, width={RingWidthMeters}m, cache={cacheMode}");
    }

    private static bool TrySaveHeightPyramidCache(
        RingTerrainMinMaxPyramid pyramid,
        string path,
        string budgetRoot,
        long budgetBytes,
        long minimumFreeBytes,
        CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(budgetRoot)
            || !RingGeneratedLodCacheBudget.CanWrite(
                budgetRoot,
                path,
                budgetBytes,
                minimumFreeBytes,
                pyramid.EstimatedCacheBytes))
        {
            return false;
        }

        bool recorded = false;
        try
        {
            pyramid.SaveCache(path, cancellation);
            RingGeneratedLodCacheBudget.RecordWrite(budgetRoot, path, new System.IO.FileInfo(path).Length);
            recorded = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (System.IO.IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (!recorded) RingGeneratedLodCacheBudget.CancelWriteReservation(budgetRoot, path);
        }
    }

    private void CompleteHydrologyAuditIfReady()
    {
        if (_hydrologyAuditTask == null || !_hydrologyAuditTask.IsCompleted) return;
        Task<RingTerrainSampler.HydrologyAudit> completed = _hydrologyAuditTask;
        _hydrologyAuditTask = null;
        if (completed.IsFaulted) { GD.PushError($"Certified hydrology audit failed: {completed.Exception?.GetBaseException().Message}"); return; }
        if (completed.IsCanceled) return;
        RingTerrainSampler.HydrologyAudit water = completed.Result;
        GD.Print($"Certified hydrology audit: dry={water.DrySamples}, ocean={water.OceanSamples} (maxDepth={water.MaximumOceanDepth}m), lake={water.LakeSamples} (maxDepth={water.MaximumLakeDepth}m), river={water.RiverSamples} (maxDepth={water.MaximumRiverDepth}m), samples={water.DrySamples + water.OceanSamples + water.LakeSamples + water.RiverSamples}");
    }

    private void CompleteHeightPyramidBuildIfReady()
    {
        if (_heightPyramidTask == null || !_heightPyramidTask.IsCompleted) return;
        Task<SummaryBuildResult> completed = _heightPyramidTask;
        _heightPyramidTask = null;
        if (completed.IsCanceled) return;
        if (completed.IsFaulted)
        {
            string error = $"Global min/max summary failed: {completed.Exception?.GetBaseException().Message}";
            GD.PushError(error);
            FailStartup(error);
            return;
        }

        SummaryBuildResult result = completed.Result;
        _heightPyramid = result.Pyramid;
        RingLodSurfaceEditSnapshot initialEdits = _grid!.CaptureLodSurfaceEditSnapshot();
        _heightPyramid.ApplyEdits(initialEdits);
        if (!_heightPyramid.ValidateHierarchy(out string hierarchyError))
        {
            GD.PushError($"Phase C min/max hierarchy self-test failed: {hierarchyError}");
            if (UseStagedStartup) { FailStartup(hierarchyError); return; }
        }
        else
            GD.Print($"Phase C min/max hierarchy self-test passed: levels={_heightPyramid.LevelCount}, cells={_heightPyramid.TotalCellCount}, base={_heightPyramid.BaseWidth}x{_heightPyramid.BaseHeight}@{_heightPyramid.BaseCellSize}m, memory={_heightPyramid.EstimatedBytes / (1024.0 * 1024.0):0.0}MiB, source={(result.LoadedFromCache ? "cache" : "generated")}, buildMs={result.BuildMilliseconds:0.00}");
        Vector3 currentMacroSun = _sunLight?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
        bool initialMaskMatchesCurrentSettings = result.MacroShadowsEnabled == EnableMacroTerrainShadows
            && result.IncludesStructuralWalls == (BuildStructuralEdgeWalls && EnableMacroTerrainWallShadows)
            && result.MacroTraceSteps == MacroTerrainShadowTraceSteps
            && Mathf.IsEqualApprox(result.MacroTraceDistance, MacroTerrainShadowMaxTraceMeters);
        bool initialMaskMatchesCurrentSun = currentMacroSun.Dot(result.MacroShadowSunDirection) > 0.9999999f;
        if (EnableMacroTerrainShadows && initialMaskMatchesCurrentSettings && initialMaskMatchesCurrentSun)
            InstallMacroShadowMask(result.MacroShadowMask, result.ShadowBuildMilliseconds);
        _lastMacroShadowRequestedSunDirection = currentMacroSun;
        if (EnableMacroTerrainShadows
            && (!initialMaskMatchesCurrentSettings
                || !initialMaskMatchesCurrentSun
                || initialEdits.HasEdits))
            QueueMacroShadowRebuild();
        if (_grid.AroundRingChunkLength * VoxelConstants.ChunkSizeX == CertifiedRingScale.AroundVoxelLength
            && Mathf.RoundToInt(_grid.RingAcrossWidthMeters) == CertifiedRingScale.RingWidthMeters)
        {
            RingTerrainSampler sampler = _grid.TerrainSampler!;
            RingTerrainMinMaxPyramid.ElevationAudit audit = sampler.UsesRuntimeSurfaceGeneration
                ? _heightPyramid.CaptureBaselineElevationAudit(8000, 8000)
                : _heightPyramid.CaptureBaselineElevationAudit(
                    CertifiedRingScale.RoutineMountainCeilingMeters,
                    7000);
            bool distributionValid = sampler.UsesRuntimeSurfaceGeneration
                ? audit.MinimumHeight >= -8000 && audit.MaximumHeight <= 8000
                : audit.MinimumHeight >= CertifiedRingScale.TerrainFloorMeters
                    && audit.MaximumHeight <= CertifiedRingScale.ExceptionalSummitMeters
                    && audit.CellsAboveRoutineCeiling <= 128
                    && audit.CellsAtOrAboveNearSummit <= 32;
            string message = sampler.UsesRuntimeSurfaceGeneration
                ? $"Runtime surface elevation audit: range={audit.MinimumHeight}..{audit.MaximumHeight}m, finiteRange=-8000..8000m, baseCells={audit.TotalBaseCells}"
                : $"Certified elevation audit: range={audit.MinimumHeight}..{audit.MaximumHeight}m, above5200={audit.CellsAboveRoutineCeiling}, atOrAbove7000={audit.CellsAtOrAboveNearSummit}, baseCells={audit.TotalBaseCells}";
            if (distributionValid) GD.Print(message);
            else GD.PushError($"{message} (distribution contract failed)");

            // Log-only audit: run it on a worker with its own sampler so it neither stalls the frame nor contends for the shared sampler's noise lock.
            _hydrologyAuditTask = Task.Run(() =>
            {
                using RingTerrainSampler independent = sampler.CreateIndependentCopy();
                return independent.CaptureHydrologyAudit();
            });
        }
        _observedEditVersion = initialEdits.Version;
        _knownEditedColumns.Clear();
        foreach (RingTerrainColumnCoordinate column in initialEdits.CanonicalDirtyColumns) _knownEditedColumns.Add(column);
        BuildGlobalRing();
    }

    private string ResolveHeightPyramidCachePath(RingTerrainSampler sampler, int baseCellSize)
    {
        string fileName = $"ring_summary_v{sampler.TerrainGeneratorVersion}_seed{sampler.Seed}_h{sampler.TerrainDefinitionHash:X16}_a{sampler.AroundRingVoxelLength}_w{RingWidthMeters}_c{baseCellSize}.bin";
        string activeWorld = ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString();
        string root = string.IsNullOrWhiteSpace(activeWorld)
            ? "user://ring_cache"
            : activeWorld.TrimEnd('/', '\\') + "/lod_cache";
        return ProjectSettings.GlobalizePath($"{root}/{fileName}");
    }

    private void BuildGlobalRing()
    {
        if (_grid?.TerrainSampler is not RingTerrainSampler sampler)
        {
            return;
        }
        if (UseMinMaxFeaturePreservation && _heightPyramid == null) return;

        BeginGlobalMorphGeneration(Mathf.Clamp(GlobalSegmentCount, 1, Mathf.Max(1, AroundSegments)), _grid.LodSurfaceEditVersion);
        InvalidatePendingPreparation();
        // Reuse existing nodes when the segment partition is unchanged.  This
        // keeps visible terrain alive until each replacement is published.
        bool reuseExisting = _terrainSegments.Count == Mathf.Clamp(GlobalSegmentCount, 1, Mathf.Max(1, AroundSegments));
        if (!reuseExisting && _terrainSegments.Count > 0)
        {
            if (_retainedTerrainSegments.Count == 0)
                _retainedTerrainSegments.AddRange(_terrainSegments);
            else
            {
                // A newer replacement supersedes incomplete replacement nodes;
                // retain only the last fully published fallback partition.
                foreach (MeshInstance3D segment in _terrainSegments)
                {
                    if (_segmentEstimatedBytes.Remove(segment, out long bytes)) EstimatedGlobalMeshBytes -= bytes;
                    RemoveChild(segment);
                    segment.QueueFree();
                }
            }
            _terrainSegments.Clear();
            _dirtySegmentQueue.Clear();
            _queuedDirtySegments.Clear();
            _pendingSegmentEdits = null;
            _initialSegmentQueue.Clear();
        }
        else if (!reuseExisting)
        {
            // The previous branch can leave a stable fallback with no current
            // replacement nodes yet.  Do not clear that fallback on a second
            // quality request before its first replacement publishes.
            _dirtySegmentQueue.Clear();
            _queuedDirtySegments.Clear();
            _pendingSegmentEdits = null;
            _initialSegmentQueue.Clear();
        }
        else
        {
            _dirtySegmentQueue.Clear();
            _queuedDirtySegments.Clear();
            _pendingSegmentEdits = null;
            _initialSegmentQueue.Clear();
        }
        RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
        _initialSegmentEdits = PrepareGlobalEditSnapshot(edits, sampler);
        _initialSegmentCount = Mathf.Clamp(GlobalSegmentCount, 1, Mathf.Max(1, AroundSegments));
        for (int segmentIndex = 0; segmentIndex < _initialSegmentCount; segmentIndex++)
            _initialSegmentQueue.Enqueue(segmentIndex);
        Mesh = null;
        GD.Print($"Segmented global terrain HLOD queued: segments={_initialSegmentCount}, around={AroundSegments}, across={AcrossSegments}, minmaxLevels={_heightPyramid?.LevelCount ?? 0}, seed={sampler.Seed}, terrainHash={sampler.TerrainDefinitionHash:X16}");
    }

    private void ServiceInitialGlobalBuild()
    {
        if (_initialSegmentEdits == null || _initialSegmentQueue.Count == 0 || _grid?.LodSurfaceEditVersion != _observedEditVersion) return;
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Terrain global initial segment service");
        ulong start = Time.GetTicksUsec();
        ulong deadline = start + (ulong)Math.Max(1.0, MaxSegmentRebuildMillisecondsPerFrame * 1000.0);
        int buildCount = 0;
        int buildBudget = Mathf.Max(1, MaxSegmentRebuildsPerFrame);
        while (buildCount < buildBudget && _initialSegmentQueue.Count > 0)
        {
            int index = _initialSegmentQueue.Peek();
            MeshInstance3D? existing = index < _terrainSegments.Count ? _terrainSegments[index] : null;
            if (!ServicePreparedSegment(index, _initialSegmentCount, existing, _initialSegmentEdits, deadline, out bool published)) break;
            if (!published) break;
            _initialSegmentQueue.Dequeue();
            buildCount++;
            LastInitialSegmentBuildMilliseconds = (Time.GetTicksUsec() - start) / 1000.0;
            MaxInitialSegmentBuildMilliseconds = Math.Max(MaxInitialSegmentBuildMilliseconds, LastInitialSegmentBuildMilliseconds);
        }

        if (_initialSegmentQueue.Count != 0) return;
        _initialSegmentEdits = null;
        TryPublishStagedMorphSnapshot();
        RetireRetainedTerrainSegments();
        GD.Print($"Segmented global terrain HLOD ready: segments={_terrainSegments.Count}, around={AroundSegments}, across={AcrossSegments}");
    }

    private sealed class PendingSegmentPreparation
    {
        public required int SegmentIndex, SegmentCount, AroundStartIndex, LocalAroundCount, AcrossCount;
        public required MeshInstance3D? Existing;
        public required RingTerrainSampler Sampler;
        public required RingLodSurfaceEditSnapshot Edits;
        public required double AroundSpacing, AcrossSpacing;
        public required bool UsesPresentationSurface, PreserveFeatures;
        public required float Underlay;
        public required Color LowColor, HighColor;
        public required Transform3D WorldToLocal;
        public required RingLodGeometrySnapshot Geometry;
        public required RingTerrainMinMaxPyramid? HeightPyramid;
        public required Vector3[] TerrainVertices, WaterVertices;
        public required float[] TerrainCanonicalHeights;
        // Raw terrain/displayed-water heights per vertex; shoreline clipping works on their difference.
        public required float[] RawTerrainHeights, RawWaterHeights;
        public required float WaterCanonicalOffset;
        public required Color[] TerrainColors, WaterColors;
        public required bool[] DisplayedWater;
        public required ulong Version;
        public int NextVertex;
        public int VertexCount => TerrainVertices.Length;
    }

    private void BeginGlobalMorphGeneration(int segmentCount, ulong editVersion)
    {
        _morphGeneration++;
        int count = Math.Max(0, segmentCount);
        Dictionary<int, RingTerrainGlobalMorphSnapshot.Segment>? inProgress =
            _stagedMorphSegmentCount == count ? _stagedMorphSegments : null;
        _stagedMorphSegmentCount = count;
        _stagedMorphEditVersion = editVersion;
        _stagedMorphSegments = new Dictionary<int, RingTerrainGlobalMorphSnapshot.Segment>(count);
        // An unchanged segment remains exact. Reusing its immutable data avoids
        // resampling the entire ring after a local edit, while the public
        // snapshot remains unavailable until all dirty replacements publish.
        if (_readyMorphSnapshot != null && _readyMorphSnapshot.SegmentCount == count)
        {
            RingTerrainGlobalMorphSnapshot.Segment[] previous = _readyMorphSnapshot.CopySegments();
            for (int index = 0; index < previous.Length; index++) _stagedMorphSegments[index] = previous[index];
        }
        // A generation can restart before the previous one published (an edit
        // during the initial build, or a second edit while the first edit's
        // segments rebuild). Its staged segments are newer than the last ready
        // snapshot; dropping them would leave indices that nothing re-stages,
        // so the snapshot could never complete. Segments the new edit dirtied
        // are overwritten when their rebuild publishes, and publication waits
        // for every queue to drain.
        if (inProgress != null)
            foreach (KeyValuePair<int, RingTerrainGlobalMorphSnapshot.Segment> staged in inProgress)
                _stagedMorphSegments[staged.Key] = staged.Value;
        _readyMorphSnapshot = null;
    }

    private void StageGlobalMorphSegment(PendingSegmentPreparation state)
    {
        if (_stagedMorphSegments == null || state.Version != _pendingPreparationVersion) return;
        // The copied height array is the receiver's exact published vertical
        // data. The snapshot owns it and never shares mutable worker buffers.
        float[] heights = (float[])state.TerrainCanonicalHeights.Clone();
        _stagedMorphSegments[state.SegmentIndex] = new RingTerrainGlobalMorphSnapshot.Segment(
            state.AroundStartIndex,
            state.LocalAroundCount,
            state.AcrossCount,
            state.AroundSpacing,
            state.AcrossSpacing,
            heights);
    }

    private void TryPublishStagedMorphSnapshot()
    {
        if (_grid?.TerrainSampler is not RingTerrainSampler sampler
            || _stagedMorphSegments == null
            || _stagedMorphSegmentCount <= 0
            || _stagedMorphSegments.Count != _stagedMorphSegmentCount
            || _initialSegmentQueue.Count != 0
            || _dirtySegmentQueue.Count != 0)
            return;

        var segments = new RingTerrainGlobalMorphSnapshot.Segment[_stagedMorphSegmentCount];
        for (int index = 0; index < segments.Length; index++)
            if (!_stagedMorphSegments.TryGetValue(index, out segments[index]!)) return;
        var identity = new RingTerrainGlobalMorphSnapshot.Identity(
            _morphGeneration,
            _stagedMorphEditVersion,
            sampler.TerrainDefinitionHash,
            Mathf.Max(64, AroundSegments),
            Mathf.Max(4, AcrossSegments),
            segments.Length,
            RingWidthMeters,
            RadialUnderlayOffsetMeters,
            ComputeMorphTopologyAndHeightHash(segments));
        _readyMorphSnapshot = new RingTerrainGlobalMorphSnapshot(
            identity,
            _grid.GetCanonicalRingCircumferenceMeters64(),
            segments);
        _stagedMorphSegments = null;
    }

    private static ulong ComputeMorphTopologyAndHeightHash(RingTerrainGlobalMorphSnapshot.Segment[] segments)
    {
        // FNV-1a gives the worker cache a stable identity across process runs.
        // Segment owns only immutable topology/heights by this point.
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (RingTerrainGlobalMorphSnapshot.Segment segment in segments)
        {
            hash = MixMorphHash(hash, (ulong)segment.LocalAroundCount, prime);
            hash = MixMorphHash(hash, (ulong)segment.AcrossCount, prime);
            hash = MixMorphHash(hash, BitConverter.DoubleToUInt64Bits(segment.AroundStartMeters), prime);
            hash = MixMorphHash(hash, BitConverter.DoubleToUInt64Bits(segment.AroundSpacing), prime);
            hash = MixMorphHash(hash, BitConverter.DoubleToUInt64Bits(segment.AcrossSpacing), prime);
            foreach (float height in segment.CopyCanonicalHeights())
            {
                hash = MixMorphHash(hash, BitConverter.SingleToUInt32Bits(height), prime);
            }
        }
        return hash;
    }

    private static ulong MixMorphHash(ulong hash, ulong value, ulong prime)
    {
        hash ^= value;
        return hash * prime;
    }

    private void InvalidatePendingPreparation()
    {
        _pendingPreparationVersion++;
        _pendingSamplingCancellation?.Cancel();
        _pendingPreparation = null;
    }

    private PendingSegmentPreparation CreatePendingSegment(int segmentIndex, int segmentCount, MeshInstance3D? existing, RingLodSurfaceEditSnapshot edits)
    {
        RingTerrainSampler sampler = _grid!.TerrainSampler!;
        int aroundCount = Mathf.Max(64, AroundSegments);
        int acrossCount = Mathf.Max(4, AcrossSegments);
        int aroundStartIndex = segmentIndex * aroundCount / segmentCount;
        int aroundEndIndex = (segmentIndex + 1) * aroundCount / segmentCount;
        int localAroundCount = Mathf.Max(1, aroundEndIndex - aroundStartIndex);
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        double aroundSpacing = circumference / aroundCount;
        double acrossSpacing = RingWidthMeters / (double)acrossCount;
        var terrainVertices = new Vector3[(localAroundCount + 1) * (acrossCount + 1)];
        return new PendingSegmentPreparation {
            SegmentIndex = segmentIndex, SegmentCount = segmentCount, AroundStartIndex = aroundStartIndex, LocalAroundCount = localAroundCount, AcrossCount = acrossCount,
            Existing = existing, Sampler = sampler.CreateIndependentCopy(), Edits = edits, AroundSpacing = aroundSpacing, AcrossSpacing = acrossSpacing,
            UsesPresentationSurface = edits.UsesFloatSurface || sampler.UsesRuntimeSurfaceGeneration,
            PreserveFeatures = UseMinMaxFeaturePreservation && _heightPyramid != null, Underlay = RadialUnderlayOffsetMeters,
            LowColor = LowTerrainColor, HighColor = HighTerrainColor, WorldToLocal = GlobalTransform.AffineInverse(),
            Geometry = _grid.CaptureLodGeometrySnapshot(), HeightPyramid = _heightPyramid,
            TerrainVertices = terrainVertices, TerrainCanonicalHeights = new float[terrainVertices.Length],
            RawTerrainHeights = new float[terrainVertices.Length], RawWaterHeights = new float[terrainVertices.Length],
            WaterCanonicalOffset = (sampler.UsesRuntimeSurfaceGeneration ? 0.0f : 1.0f) - RadialUnderlayOffsetMeters,
            TerrainColors = new Color[terrainVertices.Length], WaterVertices = new Vector3[terrainVertices.Length],
            WaterColors = new Color[terrainVertices.Length], DisplayedWater = new bool[terrainVertices.Length], Version = _pendingPreparationVersion };
    }

    private static void SamplePreparedVertex(PendingSegmentPreparation state, int index)
    {
        int row = state.LocalAroundCount + 1;
        int z = index / row, x = index % row;
        double around = (state.AroundStartIndex + x) * state.AroundSpacing;
        double across = z * state.AcrossSpacing;
        int sampleAround = (int)Math.Round(around), sampleAcross = (int)Math.Round(across);
        TerrainSample terrainSample = state.UsesPresentationSurface
            ? state.Edits.GetPresentationTerrainSample(state.Sampler, sampleAround, sampleAcross)
            : state.Sampler.GetTerrainSample(sampleAround, sampleAcross);
        float sampledHeight = state.UsesPresentationSurface
            ? state.Sampler.UsesRuntimeSurfaceGeneration
                ? state.Edits.GetPresentationSurfaceHeight(state.Sampler, around, across)
                : state.Edits.GetPresentationSurfaceHeight(state.Sampler, sampleAround, sampleAcross)
            : state.Edits.TryGetEditedSurfaceHeight(sampleAround, sampleAcross, out int editedHeight)
                ? editedHeight
                : terrainSample.SurfaceHeight;
        float sampleFootprint = (float)Math.Max(state.AroundSpacing, state.AcrossSpacing);
        float terrainHeight = state.PreserveFeatures
            ? state.UsesPresentationSurface
                ? state.HeightPyramid!.PreserveFeatureHeight(sampledHeight, (float)around, (float)across, sampleFootprint)
                : state.HeightPyramid!.PreserveFeatureHeight((int)sampledHeight, (float)around, (float)across, sampleFootprint)
            : sampledHeight;
        float waterHeight = terrainSample.ResolveDisplayedHeight(terrainHeight);
        state.TerrainColors[index] = RingTerrainVisuals.ResolveMacroTextureColor(terrainSample, Mathf.RoundToInt(terrainHeight), state.Sampler, state.LowColor, state.HighColor);
        state.WaterColors[index] = RingWaterModel.EncodeWaterKind(RingTerrainVisuals.ResolveMacroTextureColor(terrainSample, Mathf.RoundToInt(waterHeight), state.Sampler, state.LowColor, state.HighColor), terrainSample.WaterKind);
        state.DisplayedWater[index] = terrainSample.HasSurfaceWater && terrainHeight < terrainSample.WaterSurfaceHeight;
        float terrainCanonicalHeight = terrainHeight + (state.UsesPresentationSurface ? 0.0f : 1.0f) - state.Underlay;
        state.TerrainCanonicalHeights[index] = terrainCanonicalHeight;
        state.RawTerrainHeights[index] = terrainHeight;
        state.RawWaterHeights[index] = waterHeight;
        state.TerrainVertices[index] = state.WorldToLocal * state.Geometry.CanonicalToWorld(around, terrainCanonicalHeight, across);
        state.WaterVertices[index] = state.WorldToLocal * state.Geometry.CanonicalToWorld(around, waterHeight + (state.Sampler.UsesRuntimeSurfaceGeneration ? 0.0 : 1.0) - state.Underlay, across);
    }

    private bool ServicePreparedSegment(int segmentIndex, int segmentCount, MeshInstance3D? existing, RingLodSurfaceEditSnapshot edits, ulong deadline, out bool published)
    {
        published = false;
        if (_grid?.TerrainSampler == null) return false;
        if (_pendingPreparation == null || _pendingPreparation.SegmentIndex != segmentIndex || _pendingPreparation.SegmentCount != segmentCount || _pendingPreparation.Existing != existing)
        {
            if (_pendingSamplingTask != null && !_pendingSamplingTask.IsCompleted) return true;
            ObserveCompletedSamplingWorker();
            _pendingPreparation = CreatePendingSegment(segmentIndex, segmentCount, existing, edits);
            _pendingSamplingCancellation?.Dispose();
            _pendingSamplingCancellation = new CancellationTokenSource();
            _pendingSamplingReady = false;
            PendingSegmentPreparation prepared = _pendingPreparation;
            CancellationToken cancellation = _pendingSamplingCancellation.Token;
            _pendingSamplingTask = Task.Run(() =>
            {
                using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu(
                    "Terrain global segment sampling", worker: true);
                using (prepared.Sampler)
                {
                    while (prepared.NextVertex < prepared.VertexCount)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        SamplePreparedVertex(prepared, prepared.NextVertex++);
                        if ((prepared.NextVertex & 15) == 0) cancellation.ThrowIfCancellationRequested();
                    }
                }
            });
            return true;
        }
        PendingSegmentPreparation state = _pendingPreparation;
        if (state.Version != _pendingPreparationVersion || _pendingSamplingCancellation?.IsCancellationRequested == true) return true;
        if (!_pendingSamplingReady)
        {
            if (_pendingSamplingTask == null || !_pendingSamplingTask.IsCompleted) return true;
            try { _pendingSamplingTask.GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                _pendingPreparation = null;
                ObserveCompletedSamplingWorker();
                return true;
            }
            catch (Exception)
            {
                _pendingPreparation = null;
                ObserveCompletedSamplingWorker();
                return false;
            }
            _pendingSamplingReady = true;
        }
        if (Time.GetTicksUsec() >= deadline) return true;
        FinalizePreparedSegment(state);
        _pendingPreparation = null;
        _pendingSamplingTask = null;
        _pendingSamplingCancellation?.Dispose();
        _pendingSamplingCancellation = null;
        _pendingSamplingReady = false;
        published = true;
        return true;
    }

    private void ObserveCompletedSamplingWorker()
    {
        if (_pendingSamplingTask == null || !_pendingSamplingTask.IsCompleted) return;
        try { _pendingSamplingTask.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            string error = $"Global terrain segment sampling failed: {exception.GetBaseException().Message}";
            GD.PushError(error);
            if (!StartupComplete) FailStartup(error);
        }
        finally
        {
            _pendingSamplingTask = null;
            _pendingSamplingCancellation?.Dispose();
            _pendingSamplingCancellation = null;
            _pendingSamplingReady = false;
        }
    }

    private void FinalizePreparedSegment(PendingSegmentPreparation state)
    {
        MeshInstance3D? existing = state.Existing;
        int segmentIndex = state.SegmentIndex, localAroundCount = state.LocalAroundCount, acrossCount = state.AcrossCount;
        Vector3[] terrainVertices = state.TerrainVertices, waterVertices = state.WaterVertices;
        Color[] terrainColors = state.TerrainColors, waterColors = state.WaterColors;
        bool[] displayedWater = state.DisplayedWater;

        ArrayMesh mesh;
        using (RingPerformanceTelemetry.MeasureCpu("Terrain global segment mesh build"))
        {
            var terrainSurface = new SurfaceTool();
            var oceanSurface = new SurfaceTool();
            terrainSurface.Begin(Mesh.PrimitiveType.Triangles);
            oceanSurface.Begin(Mesh.PrimitiveType.Triangles);
            int terrainTriangleCount = 0;
            int oceanTriangleCount = 0;
            int row = localAroundCount + 1;
            for (int z = 0; z < acrossCount; z++)
            for (int x = 0; x < localAroundCount; x++)
            {
                int i00 = z * row + x, i10 = i00 + 1, i01 = i00 + row, i11 = i01 + 1;
                AddVertex(terrainSurface, terrainVertices[i01], terrainColors[i01]); AddVertex(terrainSurface, terrainVertices[i10], terrainColors[i10]); AddVertex(terrainSurface, terrainVertices[i00], terrainColors[i00]);
                AddVertex(terrainSurface, terrainVertices[i10], terrainColors[i10]); AddVertex(terrainSurface, terrainVertices[i01], terrainColors[i01]); AddVertex(terrainSurface, terrainVertices[i11], terrainColors[i11]);
                terrainTriangleCount += 2;
                oceanTriangleCount += AddClippedWaterTriangle(oceanSurface, state, i01, i10, i00);
                oceanTriangleCount += AddClippedWaterTriangle(oceanSurface, state, i10, i01, i11);
            }
            mesh = new ArrayMesh();
            if (terrainTriangleCount > 0)
            {
                terrainSurface.GenerateNormals();
                terrainSurface.Index();
                terrainSurface.Commit(mesh);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, _material);
            }
            if (oceanTriangleCount > 0)
            {
                oceanSurface.GenerateNormals();
                oceanSurface.Index();
                oceanSurface.Commit(mesh);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, _oceanMaterial);
            }
        }
        using (RingPerformanceTelemetry.MeasureCpu("Terrain global segment install"))
        {
            MeshInstance3D segment = existing ?? new MeshInstance3D { Name = $"GlobalTerrainSegment_{segmentIndex:D3}" };
            long estimatedBytes = terrainVertices.LongLength * 2L * (12L + 12L + 16L)
                + (long)localAroundCount * acrossCount * 12L * sizeof(int);
            if (_segmentEstimatedBytes.TryGetValue(segment, out long previousBytes))
                EstimatedGlobalMeshBytes -= previousBytes;
            _segmentEstimatedBytes[segment] = estimatedBytes;
            EstimatedGlobalMeshBytes += estimatedBytes;
            segment.Mesh = mesh;
            segment.MaterialOverride = null;
            segment.CastShadow = CastTerrainShadows ? ShadowCastingSetting.On : ShadowCastingSetting.Off;
            segment.IgnoreOcclusionCulling = true;
            if (existing == null)
            {
                AddChild(segment);
                _terrainSegments.Add(segment);
                _regionalVisibilityDirty = true;
            }
            if (EnableFarDistanceFade) UpdateRegionalInstanceVisibility(segment);
        }
        StageGlobalMorphSegment(state);
    }

    private void RefreshEditedSegments()
    {
        if (_grid == null
            || (UseMinMaxFeaturePreservation && _heightPyramid == null)
            || _grid.LodSurfaceEditVersion == _observedEditVersion) return;
        // A worker holds an immutable pyramid snapshot. Cancel it before
        // mutating that pyramid; it checks cancellation before every sample.
        if (_pendingSamplingTask != null)
        {
            InvalidatePendingPreparation();
            if (!_pendingSamplingTask.IsCompleted) return;
            ObserveCompletedSamplingWorker();
        }
        // The background trace reads the pyramid's hierarchy.  Defer mutation
        // until it has stopped, and reject its now-obsolete mask immediately.
        if (_macroShadowTask != null || _planetMacroShadowTask != null)
        {
            if (_macroShadowInvalidatedEditVersion != _grid.LodSurfaceEditVersion)
            {
                _macroShadowInvalidatedEditVersion = _grid.LodSurfaceEditVersion;
                _macroShadowRequestVersion++;
                _planetMacroShadowRequestVersion++;
            }
            return;
        }
        InvalidatePendingPreparation();
        RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
        var dirtyColumns = new HashSet<RingTerrainColumnCoordinate>(edits.CanonicalDirtyColumns);
        _heightPyramid?.ApplyEdits(edits);
        int segmentCount = _initialSegmentQueue.Count > 0 ? _initialSegmentCount : _terrainSegments.Count;
        int publishedSegmentCount = _terrainSegments.Count;
        int aroundLength = (int)Math.Max(1L, Math.Min(
            int.MaxValue,
            _grid.TerrainSampler?.AroundRingVoxelLength ?? 1L));
        foreach (RingTerrainColumnCoordinate column in dirtyColumns)
        {
            if (segmentCount <= 0) break;
            int around = Mathf.PosMod((int)(column.AroundVoxel % aroundLength), aroundLength);
            int segment = Mathf.Clamp(
                (int)((long)around * segmentCount / aroundLength),
                0,
                Mathf.Max(0, segmentCount - 1));
            if (segment < publishedSegmentCount) QueueDirtySegment(segment, publishedSegmentCount);
            int previous = Mathf.PosMod(segment - 1, segmentCount);
            int next = Mathf.PosMod(segment + 1, segmentCount);
            if (previous < publishedSegmentCount) QueueDirtySegment(previous, publishedSegmentCount);
            if (next < publishedSegmentCount) QueueDirtySegment(next, publishedSegmentCount);
        }
        // The staged generation always spans the whole partition. During the
        // initial build only some segments are published yet; sizing the
        // generation by that partial count made it unpublishable.
        if (_dirtySegmentQueue.Count > 0)
            BeginGlobalMorphGeneration(segmentCount, edits.Version);
        else if (_stagedMorphSegments != null)
            _stagedMorphEditVersion = edits.Version;
        if (_grid.TerrainSampler != null)
        {
            _pendingSegmentEdits = PrepareGlobalEditSnapshot(edits, _grid.TerrainSampler);
            if (_initialSegmentQueue.Count > 0)
                _initialSegmentEdits = _pendingSegmentEdits;
        }
        _knownEditedColumns.Clear();
        foreach (RingTerrainColumnCoordinate column in edits.CanonicalDirtyColumns) _knownEditedColumns.Add(column);
        _observedEditVersion = edits.Version;
        _macroShadowInvalidatedEditVersion = 0;
        QueueMacroShadowRebuild();
        QueuePlanetMacroShadowRebuild();
    }

    private RingLodSurfaceEditSnapshot PrepareGlobalEditSnapshot(RingLodSurfaceEditSnapshot edits, RingTerrainSampler sampler)
    {
        if (_grid == null) return edits;
        float circumference = _grid.GetCanonicalRingCircumferenceMeters();
        int aroundSpacing = Mathf.Max(1, Mathf.RoundToInt(circumference / Mathf.Max(64, AroundSegments)));
        int acrossSpacing = Mathf.Max(1, Mathf.RoundToInt(RingWidthMeters / (float)Mathf.Max(4, AcrossSegments)));
        return edits.ForSampleSpacing(sampler, Mathf.Max(aroundSpacing, acrossSpacing), RingWidthMeters);
    }

    private void QueueDirtySegment(int index, int segmentCount)
    {
        if (segmentCount <= 0 || index < 0 || index >= segmentCount || !_queuedDirtySegments.Add(index)) return;
        _dirtySegmentQueue.Enqueue(index);
    }

    private void ServiceSegmentRebuilds()
    {
        LastSegmentRebuildCount = 0;
        LastSegmentRebuildMilliseconds = 0.0;
        if (_grid?.LodSurfaceEditVersion != _observedEditVersion || _initialSegmentQueue.Count > 0 || _pendingSegmentEdits == null || _dirtySegmentQueue.Count == 0 || _terrainSegments.Count == 0) return;
        ulong start = Time.GetTicksUsec();
        ulong deadline = start + (ulong)Math.Max(1.0, MaxSegmentRebuildMillisecondsPerFrame * 1000.0);
        int rebuildBudget = Mathf.Max(1, MaxSegmentRebuildsPerFrame);
        while (LastSegmentRebuildCount < rebuildBudget && _dirtySegmentQueue.Count > 0)
        {
            int index = _dirtySegmentQueue.Peek();
            if (index < 0 || index >= _terrainSegments.Count)
            {
                _dirtySegmentQueue.Dequeue();
                _queuedDirtySegments.Remove(index);
                continue;
            }
            if (!ServicePreparedSegment(index, _terrainSegments.Count, _terrainSegments[index], _pendingSegmentEdits, deadline, out bool published) || !published) break;
            _dirtySegmentQueue.Dequeue();
            _queuedDirtySegments.Remove(index);
            LastSegmentRebuildCount++;
        }
        TryPublishStagedMorphSnapshot();
        LastSegmentRebuildMilliseconds = (Time.GetTicksUsec() - start) / 1000.0;
    }

    private void ClearTerrainSegments()
    {
        InvalidatePendingPreparation();
        _dirtySegmentQueue.Clear();
        _queuedDirtySegments.Clear();
        _pendingSegmentEdits = null;
        _initialSegmentQueue.Clear();
        _initialSegmentEdits = null;
        _initialSegmentCount = 0;
        foreach (MeshInstance3D segment in _terrainSegments)
        {
            RemoveChild(segment);
            segment.QueueFree();
        }
        _terrainSegments.Clear();
        foreach (MeshInstance3D segment in _retainedTerrainSegments)
        {
            RemoveChild(segment);
            segment.QueueFree();
        }
        _retainedTerrainSegments.Clear();
        _segmentEstimatedBytes.Clear();
        EstimatedGlobalMeshBytes = 0L;
        _lastRegionalVisibilityFocusPosition = new Vector3(float.PositiveInfinity, 0.0f, 0.0f);
    }

    private void RetireRetainedTerrainSegments()
    {
        foreach (MeshInstance3D segment in _retainedTerrainSegments)
        {
            if (_segmentEstimatedBytes.Remove(segment, out long bytes)) EstimatedGlobalMeshBytes -= bytes;
            RemoveChild(segment);
            segment.QueueFree();
        }
        _retainedTerrainSegments.Clear();
    }

    private void BuildGlobalTerrainOccluder()
    {
        if (UseStagedStartup)
        {
            _startupOccluderBuild?.Dispose();
            _startupOccluderBuild = EnumerateBuildGlobalTerrainOccluder().GetEnumerator();
            return;
        }
        foreach (bool _ in EnumerateBuildGlobalTerrainOccluder()) { }
    }

    private IEnumerable<bool> EnumerateBuildGlobalTerrainOccluder()
    {
        if (_grid?.TerrainSampler is not RingTerrainSampler sampler)
        {
            yield break;
        }

        int aroundCount = Mathf.Max(32, OccluderAroundSegments);
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        double aroundSpacing = circumference / aroundCount;
        float safetyMargin = Mathf.Max(1.0f, OccluderBedrockSafetyMarginMeters);

        // Each occluder is a straight chord. Push its endpoints outward far
        // enough that even the chord midpoint remains behind the curved
        // bedrock shell. This prevents the occluder from entering playable
        // space at low segment counts or very large ring radii.
        float ringRadius = (float)Math.Max(1.0, sampler.RingRadiusMeters);
        float halfSegmentAngle = (float)(0.5 * aroundSpacing / ringRadius);
        float chordScale = 1.0f / Mathf.Max(0.001f, Mathf.Cos(halfSegmentAngle));
        float safeCanonicalHeight = sampler.BedrockY - safetyMargin;
        float verticalBase = _grid.RingVerticalBaseOffsetMeters;
        float safeRadialDistance = ringRadius - (verticalBase + safeCanonicalHeight);
        float endpointRadialDistance = safeRadialDistance * chordScale;
        float endpointCanonicalHeight = ringRadius - verticalBase - endpointRadialDistance;
        float ringWidth = Mathf.Max(1.0f, RingWidthMeters);

        _occluderRoot = new Node3D { Name = "ConservativeBedrockOccluders" };
        AddChild(_occluderRoot);

        int[] indices = { 2, 1, 0, 1, 2, 3 };
        for (int aroundIndex = 0; aroundIndex < aroundCount; aroundIndex++)
        {
            double around0 = aroundIndex * aroundSpacing;
            double around1 = (aroundIndex + 1) * aroundSpacing;
            Vector3[] vertices =
            {
                _occluderRoot.ToLocal(_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(around0, endpointCanonicalHeight, 0.0))),
                _occluderRoot.ToLocal(_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(around1, endpointCanonicalHeight, 0.0))),
                _occluderRoot.ToLocal(_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(around0, endpointCanonicalHeight, ringWidth))),
                _occluderRoot.ToLocal(_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(around1, endpointCanonicalHeight, ringWidth)))
            };

            var occluderResource = new ArrayOccluder3D();
            occluderResource.SetArrays(vertices, indices);
            _occluderRoot.AddChild(new OccluderInstance3D
            {
                Name = $"BedrockOccluder_{aroundIndex:D4}",
                Occluder = occluderResource
            });
            yield return true;
        }

        GD.Print($"Conservative bedrock occluders: segments={aroundCount}, bedrock={sampler.BedrockY}, safety={safetyMargin:0.0}m, chordDepth={safeCanonicalHeight - endpointCanonicalHeight:0.0}m");
    }

    private static void AddVertex(SurfaceTool surface, Vector3 vertex, Color color)
    {
        surface.SetColor(color);
        surface.AddVertex(vertex);
    }

    /// <summary>
    /// Emits the water surface of one lattice triangle: whole when all vertices are wet, clipped exactly
    /// against the shoreline when one or two are (the rule shared with proxy sectors and the far viewport).
    /// Returns the number of triangles written.
    /// </summary>
    private static int AddClippedWaterTriangle(SurfaceTool surface, PendingSegmentPreparation state, int a, int b, int c)
    {
        bool[] wet = state.DisplayedWater;
        Vector3[] water = state.WaterVertices;
        Color[] colors = state.WaterColors;
        if (RingShorelineClip.IsFullyWet(wet[a], wet[b], wet[c]))
        {
            AddVertex(surface, water[a], colors[a]);
            AddVertex(surface, water[b], colors[b]);
            AddVertex(surface, water[c], colors[c]);
            return 1;
        }
        if (!RingShorelineClip.IsPartiallyWet(wet[a], wet[b], wet[c])) return 0;

        Span<int> corner = stackalloc int[3] { a, b, c };
        float representative = 0.0f;
        int wetCount = 0, firstWet = -1;
        Vector3 up = Vector3.Up;
        for (int k = 0; k < 3; k++)
        {
            int i = corner[k];
            if (!wet[i]) continue;
            representative += state.RawWaterHeights[i];
            wetCount++;
            if (firstWet < 0)
            {
                firstWet = i;
                Vector3 lift = water[i] - state.TerrainVertices[i];
                if (lift.LengthSquared() > 0.000001f) up = lift.Normalized();
            }
        }
        representative /= wetCount;

        Span<float> coverage = stackalloc float[3];
        Span<Vector3> position = stackalloc Vector3[3];
        for (int k = 0; k < 3; k++)
        {
            int i = corner[k];
            if (wet[i])
            {
                coverage[k] = RingShorelineClip.SignedCoverage(true, state.RawWaterHeights[i] - state.RawTerrainHeights[i]);
                position[k] = water[i];
            }
            else
            {
                coverage[k] = RingShorelineClip.SignedCoverage(false, representative - state.RawTerrainHeights[i]);
                // Water plane extended under the dry vertex at the shoreline's water level.
                position[k] = water[i] + up * (representative - state.RawWaterHeights[i]);
            }
        }
        Span<float> edgeLength = stackalloc float[3]
        {
            (state.TerrainVertices[b] - state.TerrainVertices[a]).Length(),
            (state.TerrainVertices[c] - state.TerrainVertices[b]).Length(),
            (state.TerrainVertices[a] - state.TerrainVertices[c]).Length()
        };
        Span<Vector3> weights = stackalloc Vector3[RingShorelineClip.MaxVertices];
        int count = RingShorelineClip.ClipTriangle(coverage, edgeLength, RingShorelineClip.OverlapMeters, weights);
        if (count < 3) return 0;

        Color color = colors[firstWet];
        Span<Vector3> clipped = stackalloc Vector3[RingShorelineClip.MaxVertices];
        for (int v = 0; v < count; v++)
            clipped[v] = position[0] * weights[v].X + position[1] * weights[v].Y + position[2] * weights[v].Z;
        for (int v = 1; v < count - 1; v++)
        {
            AddVertex(surface, clipped[0], color);
            AddVertex(surface, clipped[v], color);
            AddVertex(surface, clipped[v + 1], color);
        }
        return count - 2;
    }

    private void CreateMaterial()
    {
        RingTerrainVisuals.PreloadTexturePalette();
        Shader? shader = GD.Load<Shader>("res://materials/ring_terrain_global.gdshader");
        if (shader == null) return;
        _material = new ShaderMaterial { Shader = shader };
        _material.SetShaderParameter("surface_apertures_enabled", true);
        _material.SetShaderParameter("camera_world_position", _focus!.GlobalPosition);
        _material.SetShaderParameter("fade_start_meters", FadeStartMeters);
        _material.SetShaderParameter("fade_end_meters", Mathf.Max(FadeStartMeters + 1.0f, FadeEndMeters));
        _material.SetShaderParameter("far_fade_enabled", false);
        _material.SetShaderParameter("far_fade_enabled", EnableFarDistanceFade);
        _material.SetShaderParameter("far_fade_start_meters", FarFadeStartMeters);
        _material.SetShaderParameter("far_fade_end_meters", Mathf.Max(FarFadeStartMeters + 1.0f, FarFadeEndMeters));
        _material.SetShaderParameter("ring_width_meters", RingWidthMeters);
        _material.SetShaderParameter("night_brightness", TerrainNightBrightness);
        _material.SetShaderParameter("night_tint", new Vector3(NightTint.R, NightTint.G, NightTint.B));
        RingAtmosphereModel.ConfigureMaterial(_material, _grid!.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
        global::RingworldTechDemo.RingTerrainMacroShadowProjector.Register(_material);
        global::RingworldTechDemo.RingCloudShadowProjector.ConfigureAnalyticField(
            _material,
            _grid.GetCanonicalRingCircumferenceMeters64(),
            RingWidthMeters,
            _grid.TerrainSampler?.Seed ?? 1337);
        global::RingworldTechDemo.RingCloudShadowProjector.Register(_material);

        Shader? oceanShader = GD.Load<Shader>("res://materials/ring_ocean_global.gdshader");
        if (oceanShader != null)
        {
            _oceanMaterial = new ShaderMaterial { Shader = oceanShader };
            _oceanMaterial.SetShaderParameter("surface_apertures_enabled", true);
            _oceanMaterial.SetShaderParameter("camera_world_position", _focus!.GlobalPosition);
            _oceanMaterial.SetShaderParameter("fade_start_meters", FadeStartMeters);
            _oceanMaterial.SetShaderParameter("fade_end_meters", Mathf.Max(FadeStartMeters + 1.0f, FadeEndMeters));
            _oceanMaterial.SetShaderParameter("far_fade_enabled", EnableFarDistanceFade);
            _oceanMaterial.SetShaderParameter("far_fade_start_meters", FarFadeStartMeters);
            _oceanMaterial.SetShaderParameter("far_fade_end_meters", Mathf.Max(FarFadeStartMeters + 1.0f, FarFadeEndMeters));
            _oceanMaterial.SetShaderParameter("ring_width_meters", RingWidthMeters);
            _oceanMaterial.SetShaderParameter("night_brightness", TerrainNightBrightness);
            _oceanMaterial.SetShaderParameter("night_tint", new Vector3(NightTint.R, NightTint.G, NightTint.B));
            RingWaterModel.ConfigureMaterial(_oceanMaterial);
            _oceanMaterial.SetShaderParameter("wave_geometry_strength", 0.0f);
            RingAtmosphereModel.ConfigureMaterial(_oceanMaterial, _grid!.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
            global::RingworldTechDemo.RingTerrainMacroShadowProjector.Register(_oceanMaterial);
            global::RingworldTechDemo.RingCloudShadowProjector.ConfigureAnalyticField(
                _oceanMaterial,
                _grid.GetCanonicalRingCircumferenceMeters64(),
                RingWidthMeters,
                _grid.TerrainSampler?.Seed ?? 1337);
            global::RingworldTechDemo.RingCloudShadowProjector.Register(_oceanMaterial);
        }

        Shader? hullShader = GD.Load<Shader>("res://materials/ring_hull_global.gdshader");
        if (hullShader != null)
        {
            _hullMaterial = new ShaderMaterial { Shader = hullShader };
            _hullMaterial.SetShaderParameter("camera_world_position", _focus!.GlobalPosition);
            _hullMaterial.SetShaderParameter("fade_start_meters", FadeStartMeters);
            _hullMaterial.SetShaderParameter("fade_end_meters", Mathf.Max(FadeStartMeters + 1.0f, FadeEndMeters));
            _hullMaterial.SetShaderParameter("far_fade_enabled", EnableFarDistanceFade);
            _hullMaterial.SetShaderParameter("far_fade_start_meters", FarFadeStartMeters);
            _hullMaterial.SetShaderParameter("far_fade_end_meters", Mathf.Max(FarFadeStartMeters + 1.0f, FarFadeEndMeters));
            _hullMaterial.SetShaderParameter("ring_width_meters", RingWidthMeters);
            _hullMaterial.SetShaderParameter("night_brightness", HullNightBrightness);
            _hullMaterial.SetShaderParameter("night_tint", new Vector3(NightTint.R, NightTint.G, NightTint.B));
            _hullMaterial.SetShaderParameter("structural_wall_top_meters", _grid!.GetStructuralWallTopGlobalY());
            _hullMaterial.SetShaderParameter("structural_scale_cues_enabled", StructuralScaleCuesEnabled);
            _hullMaterial.SetShaderParameter("structural_longitudinal_spacing_meters", StructuralLongitudinalSpacingMeters);
            _hullMaterial.SetShaderParameter("structural_longitudinal_half_width_meters", StructuralLongitudinalHalfWidthMeters);
            _hullMaterial.SetShaderParameter("structural_cross_spacing_meters", StructuralCrossSpacingMeters);
            _hullMaterial.SetShaderParameter("structural_cross_half_width_meters", StructuralCrossHalfWidthMeters);
            _hullMaterial.SetShaderParameter("structural_top_rail_half_width_meters", StructuralTopRailHalfWidthMeters);
            _hullMaterial.SetShaderParameter(
                "structural_cue_color",
                new Vector3(StructuralCueColor.R, StructuralCueColor.G, StructuralCueColor.B));
            _hullMaterial.SetShaderParameter("structural_cue_day_energy", StructuralCueDayEnergy);
            _hullMaterial.SetShaderParameter("structural_cue_night_energy", StructuralCueNightEnergy);
            RingAtmosphereModel.ConfigureMaterial(
                _hullMaterial,
                _grid!.GetCanonicalRingCircumferenceMeters64() / Math.Tau,
                opacityMultiplier: 1.35f);
        }
        UpdateLightingUniforms();
    }

    private readonly RingAtmosphereRayFrame.AppliedCache _rayFrameCache = new();
    private Image? _macroShadowImage;
    private ImageTexture? _macroShadowTexture;

    private void UpdateLightingUniforms()
    {
        if (_grid == null)
        {
            return;
        }

        Vector3 ringCenter = _grid.GetCanonicalRingCenterWorld();
        Vector3 sunDirection = _sunLight?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera != null)
        {
            RingAtmosphereRayFrame frame = RingAtmosphereRayFrame.FromMainCamera(_grid, camera, sunDirection);
            // Writes only when the frame changed for a given material; new materials always receive it.
            _rayFrameCache.Apply(frame, _material);
            _rayFrameCache.Apply(frame, _oceanMaterial);
            _rayFrameCache.Apply(frame, _hullMaterial);
        }
        if (!_lastLightingRingCenter.IsEqualApprox(ringCenter))
        {
            _material?.SetShaderParameter("ring_center", ringCenter);
            _oceanMaterial?.SetShaderParameter("ring_center", ringCenter);
            _hullMaterial?.SetShaderParameter("ring_center", ringCenter);
            _lastLightingRingCenter = ringCenter;
        }
        if (!_lastLightingSunDirection.IsEqualApprox(sunDirection))
        {
            _material?.SetShaderParameter("sun_direction", sunDirection);
            _oceanMaterial?.SetShaderParameter("sun_direction", sunDirection);
            _hullMaterial?.SetShaderParameter("sun_direction", sunDirection);
            _lastLightingSunDirection = sunDirection;
        }
    }

    private void UpdateCameraUniform()
    {
        if (_focus == null) return;
        Vector3 cameraWorldPosition = _focus.GlobalPosition;
        if (_lastCameraWorldPosition.IsEqualApprox(cameraWorldPosition)) return;
        _material?.SetShaderParameter("camera_world_position", cameraWorldPosition);
        _oceanMaterial?.SetShaderParameter("camera_world_position", cameraWorldPosition);
        _hullMaterial?.SetShaderParameter("camera_world_position", cameraWorldPosition);
        _lastCameraWorldPosition = cameraWorldPosition;
    }

    private void BuildGlobalHullWalls()
    {
        if (UseStagedStartup)
        {
            _startupHullBuild?.Dispose();
            _startupHullBuild = EnumerateBuildGlobalHullWalls().GetEnumerator();
            return;
        }
        foreach (bool _ in EnumerateBuildGlobalHullWalls()) { }
    }

    private IEnumerable<bool> EnumerateBuildGlobalHullWalls()
    {
        if (_grid == null || _hullMaterial == null)
        {
            yield break;
        }

        ClearHullSegments();
        int aroundCount = Mathf.Max(64, AroundSegments);
        int segmentCount = Mathf.Clamp(GlobalSegmentCount, 1, aroundCount);
        _hullRoofTotalCount = segmentCount;
        int verticalCount = Mathf.Max(1, HullVerticalSegments);
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        double aroundSpacing = circumference / aroundCount;
        float hullBottom = _grid.GetStructuralBottomGlobalY()
            * VoxelConstants.VoxelSizeMeters;
        float hullTop = _grid.GetStructuralWallTopGlobalY();
        float verticalSpacing = (hullTop - hullBottom) / verticalCount;
        for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
        {
            int aroundStartIndex = segmentIndex * aroundCount / segmentCount;
            int aroundEndIndex = (segmentIndex + 1) * aroundCount / segmentCount;
            using var surface = new SurfaceTool();
            surface.Begin(Mesh.PrimitiveType.Triangles);
            if (BuildStructuralEdgeWalls)
            {
                AppendHullWallSegment(surface, 0.0f, aroundStartIndex, aroundEndIndex, verticalCount, aroundSpacing, verticalSpacing, hullBottom, reverse: true);
                AppendHullWallSegment(surface, RingWidthMeters, aroundStartIndex, aroundEndIndex, verticalCount, aroundSpacing, verticalSpacing, hullBottom, reverse: false);
                surface.GenerateNormals();
                surface.Index();
                var segment = new MeshInstance3D
                {
                    Name = $"GlobalStructuralEdgeWalls_{segmentIndex:D3}",
                    Mesh = surface.Commit(),
                    MaterialOverride = _hullMaterial,
                    CastShadow = (CastTerrainShadows || BuildTerrainRoofUnderside || BuildLowerHullCavityFloor)
                        ? ShadowCastingSetting.On
                        : ShadowCastingSetting.Off,
                    IgnoreOcclusionCulling = true
                };
                AddChild(segment);
                _hullSegments.Add(segment);
                if (EnableFarDistanceFade) UpdateRegionalInstanceVisibility(segment);
            }

            // Underside, roof underside and cavity floor are opaque cull_disabled
            // closure surfaces: seen from altitude through a not-yet-built terrain
            // segment they read as a pale gridded floor, so they wait for it.
            if (BuildStructuralUnderside || BuildTerrainRoofUnderside || BuildLowerHullCavityFloor)
            {
                using var roofSurface = new SurfaceTool();
                roofSurface.Begin(Mesh.PrimitiveType.Triangles);
                if (BuildStructuralUnderside)
                {
                    AppendHullUndersideSegment(
                        roofSurface,
                        aroundStartIndex,
                        aroundEndIndex,
                        aroundSpacing,
                        hullBottom);
                }
                if (BuildTerrainRoofUnderside)
                {
                    AppendHullRoofUndersideSegment(
                        roofSurface,
                        aroundStartIndex,
                        aroundEndIndex,
                        aroundSpacing,
                        _grid.TerrainBottomGlobalY);
                }
                if (BuildLowerHullCavityFloor)
                {
                    AppendHullLowerCavityFloorSegment(
                        roofSurface, aroundStartIndex, aroundEndIndex, aroundSpacing,
                        _grid.GetLowerHullTopGlobalY());
                }
                roofSurface.GenerateNormals();
                roofSurface.Index();
                var roof = new MeshInstance3D
                {
                    Name = $"GlobalStructuralRoof_{segmentIndex:D3}",
                    Mesh = roofSurface.Commit(),
                    MaterialOverride = _hullMaterial,
                    // The terrain-bottom roof is structural closure, not optional
                    // distant terrain decoration; preserve its shadow caster even
                    // when this global surface LOD has terrain shadows disabled.
                    CastShadow = ShadowCastingSetting.On,
                    IgnoreOcclusionCulling = true,
                    Visible = false
                };
                AddChild(roof);
                _hullRoofSegments.Add(roof);
                UpdateHullRoofVisibility(_hullRoofSegments.Count - 1);
            }
            // The roof count may now differ from before; re-evaluate earlier gates.
            _regionalVisibilityDirty = true;
            yield return true;
        }
        GD.Print($"Segmented regional structural hull HLOD: segments={segmentCount}, edges={(BuildStructuralEdgeWalls ? 2 : 0)}, underside={BuildStructuralUnderside}, roof={BuildTerrainRoofUnderside}, cavityFloor={BuildLowerHullCavityFloor}, around={aroundCount}, vertical={verticalCount}, depth={-hullBottom:0}m, fade={FadeStartMeters / 1000.0f:0.0}-{FarFadeEndMeters / 1000.0f:0.0}km");
    }

    private void AppendHullWallSegment(SurfaceTool surface, double across, int aroundStartIndex, int aroundEndIndex, int verticalCount, double aroundSpacing, float verticalSpacing, float hullBottom, bool reverse)
    {
        if (_grid == null) return;
        int localAroundCount = Mathf.Max(1, aroundEndIndex - aroundStartIndex);
        int row = localAroundCount + 1;
        var vertices = new Vector3[(verticalCount + 1) * row];
        for (int y = 0; y <= verticalCount; y++)
        for (int x = 0; x <= localAroundCount; x++)
        {
            var canonical = new RingCanonicalPosition((aroundStartIndex + x) * aroundSpacing, hullBottom + y * verticalSpacing, across);
            vertices[y * row + x] = ToLocal(_grid.CanonicalRingPositionToWorld(canonical));
        }

        for (int y = 0; y < verticalCount; y++)
        for (int x = 0; x < localAroundCount; x++)
        {
            int i00 = y * row + x, i10 = i00 + 1, i01 = i00 + row, i11 = i01 + 1;
            if (reverse)
            {
                AddHullVertex(surface, vertices[i00]); AddHullVertex(surface, vertices[i10]); AddHullVertex(surface, vertices[i01]);
                AddHullVertex(surface, vertices[i10]); AddHullVertex(surface, vertices[i11]); AddHullVertex(surface, vertices[i01]);
            }
            else
            {
                AddHullVertex(surface, vertices[i01]); AddHullVertex(surface, vertices[i10]); AddHullVertex(surface, vertices[i00]);
                AddHullVertex(surface, vertices[i10]); AddHullVertex(surface, vertices[i01]); AddHullVertex(surface, vertices[i11]);
            }
        }
    }

    private void AddHullVertex(SurfaceTool surface, Vector3 vertex)
    {
        surface.SetColor(HullColor);
        surface.AddVertex(vertex);
    }

    private void AppendHullUndersideSegment(
        SurfaceTool surface,
        int aroundStartIndex,
        int aroundEndIndex,
        double aroundSpacing,
        float hullBottom)
    {
        if (_grid == null) return;
        for (int aroundIndex = aroundStartIndex; aroundIndex < aroundEndIndex; aroundIndex++)
        {
            double a0 = aroundIndex * aroundSpacing;
            double a1 = (aroundIndex + 1) * aroundSpacing;
            Vector3 p00 = ToLocal(_grid.CanonicalRingPositionToWorld(
                new RingCanonicalPosition(a0, hullBottom, 0.0)));
            Vector3 p10 = ToLocal(_grid.CanonicalRingPositionToWorld(
                new RingCanonicalPosition(a1, hullBottom, 0.0)));
            Vector3 p11 = ToLocal(_grid.CanonicalRingPositionToWorld(
                new RingCanonicalPosition(a1, hullBottom, RingWidthMeters)));
            Vector3 p01 = ToLocal(_grid.CanonicalRingPositionToWorld(
                new RingCanonicalPosition(a0, hullBottom, RingWidthMeters)));
            AddHullVertex(surface, p00); AddHullVertex(surface, p11); AddHullVertex(surface, p10);
            AddHullVertex(surface, p00); AddHullVertex(surface, p01); AddHullVertex(surface, p11);
        }
    }

    // Global continuation of the proxy roof.  The hull shader's near/far
    // handoff owns visibility, so this starts only after the regional LOD
    // rings fade out and cannot depend on the editable smooth terrain bubble.
    private void AppendHullRoofUndersideSegment(
        SurfaceTool surface,
        int aroundStartIndex,
        int aroundEndIndex,
        double aroundSpacing,
        float terrainBottom)
    {
        if (_grid == null) return;
        for (int aroundIndex = aroundStartIndex; aroundIndex < aroundEndIndex; aroundIndex++)
        {
            double a0 = aroundIndex * aroundSpacing;
            double a1 = (aroundIndex + 1) * aroundSpacing;
            Vector3 p00 = ToLocal(_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(a0, terrainBottom, 0.0)));
            Vector3 p10 = ToLocal(_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(a1, terrainBottom, 0.0)));
            Vector3 p11 = ToLocal(_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(a1, terrainBottom, RingWidthMeters)));
            Vector3 p01 = ToLocal(_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(a0, terrainBottom, RingWidthMeters)));
            AddHullVertex(surface, p00); AddHullVertex(surface, p11); AddHullVertex(surface, p10);
            AddHullVertex(surface, p00); AddHullVertex(surface, p01); AddHullVertex(surface, p11);
        }
    }

    private void AppendHullLowerCavityFloorSegment(
        SurfaceTool surface, int aroundStartIndex, int aroundEndIndex,
        double aroundSpacing, float lowerHullTop)
    {
        if (_grid == null) return;
        for (int aroundIndex = aroundStartIndex; aroundIndex < aroundEndIndex; aroundIndex++)
        {
            double a0 = aroundIndex * aroundSpacing;
            double a1 = (aroundIndex + 1) * aroundSpacing;
            Vector3 p00 = ToLocal(_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(a0, lowerHullTop, 0.0)));
            Vector3 p10 = ToLocal(_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(a1, lowerHullTop, 0.0)));
            Vector3 p11 = ToLocal(_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(a1, lowerHullTop, RingWidthMeters)));
            Vector3 p01 = ToLocal(_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(a0, lowerHullTop, RingWidthMeters)));
            // Opposite of the roof: the cavity is above the lower hull.
            AddHullVertex(surface, p00); AddHullVertex(surface, p10); AddHullVertex(surface, p11);
            AddHullVertex(surface, p00); AddHullVertex(surface, p11); AddHullVertex(surface, p01);
        }
    }

    private void ClearHullSegments()
    {
        foreach (MeshInstance3D segment in _hullSegments)
        {
            RemoveChild(segment);
            segment.QueueFree();
        }
        _hullSegments.Clear();
        foreach (MeshInstance3D segment in _hullRoofSegments)
        {
            RemoveChild(segment);
            segment.QueueFree();
        }
        _hullRoofSegments.Clear();
    }

    public void ApplyRuntimeFade(float fadeStart, float fadeEnd)
    {
        FadeStartMeters = Mathf.Max(0.0f, fadeStart);
        FadeEndMeters = Mathf.Max(FadeStartMeters + 1.0f, fadeEnd);
        _material?.SetShaderParameter("fade_start_meters", FadeStartMeters);
        _material?.SetShaderParameter("fade_end_meters", FadeEndMeters);
        _oceanMaterial?.SetShaderParameter("fade_start_meters", FadeStartMeters);
        _oceanMaterial?.SetShaderParameter("fade_end_meters", FadeEndMeters);
        _hullMaterial?.SetShaderParameter("fade_start_meters", FadeStartMeters);
        _hullMaterial?.SetShaderParameter("fade_end_meters", FadeEndMeters);
    }

    public void ApplyRuntimeQuality(int aroundSegments, int acrossSegments, float fadeStart, float fadeEnd)
    {
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Terrain global runtime quality");
        int nextAround = Mathf.Clamp(aroundSegments, 64, 4096);
        int nextAcross = Mathf.Clamp(acrossSegments, 4, 256);
        bool aroundChanged = nextAround != AroundSegments;
        bool acrossChanged = nextAcross != AcrossSegments;
        AroundSegments = nextAround;
        AcrossSegments = nextAcross;
        ApplyRuntimeFade(fadeStart, fadeEnd);
        if (_grid?.TerrainSampler == null || (!aroundChanged && !acrossChanged)) return;

        Mesh = null;
        // Hull longitudinal resolution follows around segments; terrain across
        // quality does not change it. Occluder inputs are independent of both.
        if (aroundChanged)
        {
            ClearHullSegments();
        }

        BuildGlobalRing();
        if (aroundChanged
            && (BuildStructuralEdgeWalls || BuildStructuralUnderside || BuildTerrainRoofUnderside || BuildLowerHullCavityFloor))
            BuildGlobalHullWalls();
    }

    public void ApplyRuntimeShadowQuality(
        float strength,
        float fadeStartMeters,
        float fadeEndMeters,
        int traceSteps,
        float maximumTraceMeters)
    {
        float nextStrength = Mathf.Clamp(strength, 0.0f, 0.8f);
        float nextFadeStart = Mathf.Max(0.0f, fadeStartMeters);
        float nextFadeEnd = Mathf.Max(nextFadeStart + 1.0f, fadeEndMeters);
        int nextTraceSteps = Mathf.Clamp(traceSteps, 1, 32);
        float nextTraceDistance = Mathf.Clamp(maximumTraceMeters, 4000.0f, 320000.0f);
        bool changed = !Mathf.IsEqualApprox(MacroTerrainShadowStrength, nextStrength)
            || !Mathf.IsEqualApprox(MacroTerrainShadowFadeStartMeters, nextFadeStart)
            || !Mathf.IsEqualApprox(MacroTerrainShadowFadeEndMeters, nextFadeEnd)
            || MacroTerrainShadowTraceSteps != nextTraceSteps
            || !Mathf.IsEqualApprox(MacroTerrainShadowMaxTraceMeters, nextTraceDistance);

        MacroTerrainShadowStrength = nextStrength;
        MacroTerrainShadowFadeStartMeters = nextFadeStart;
        MacroTerrainShadowFadeEndMeters = nextFadeEnd;
        MacroTerrainShadowTraceSteps = nextTraceSteps;
        MacroTerrainShadowMaxTraceMeters = nextTraceDistance;
        if (changed)
        {
            QueueMacroShadowRebuild();
            QueuePlanetMacroShadowRebuild();
        }
    }

    public void ApplyRuntimeMacroShadowOptions(bool enabled, bool wallShadowsEnabled)
    {
        bool changed = EnableMacroTerrainShadows != enabled
            || EnableMacroTerrainWallShadows != wallShadowsEnabled;
        EnableMacroTerrainShadows = enabled;
        EnableMacroTerrainWallShadows = wallShadowsEnabled;
        if (!enabled)
        {
            _macroShadowCancellation?.Cancel();
            global::RingworldTechDemo.RingTerrainMacroShadowProjector.Disable();
            CancelPlanetMacroShadowBuild();
            return;
        }
        if (changed)
        {
            QueueMacroShadowRebuild();
            QueuePlanetMacroShadowRebuild();
        }
    }

    private void RequestMacroShadowRebuildForSunChange()
    {
        if (!EnableMacroTerrainShadows || _heightPyramid == null || _sunLight == null) return;
        // Eclipse hub sun (spec C.2, F17): the primary light follows the camera zenith, so it turns 0.51 degrees every 44 km of travel.
        // Keep the last mask; the shaders scale it by (1 - hub weight). The rebuild after the event waits until the light is exactly back on
        // the real sun (a few frames of weight 0), because the mask is built from the light direction at the moment the rebuild starts.
        // A build started during the hold (edits, settings, a queued restart) snapshots the zenith-aimed light, and the real sun may not have
        // moved 0.51 degrees since the last request (paused clock, debug override). Forget the last request so the first frame after the
        // release always rebuilds from the real sun.
        if (RingworldTechDemo.Celestial.Eclipse.EclipseHubLighting.HoldMacroShadows)
        {
            _lastMacroShadowRequestedSunDirection = new Vector3(float.PositiveInfinity, 0.0f, 0.0f);
            return;
        }
        Vector3 sunDirection = _sunLight.GlobalTransform.Basis.Z.Normalized();
        // A half-degree threshold avoids continuously discarding work while a
        // smooth day/night controller advances the directional light.
        if (_lastMacroShadowRequestedSunDirection.IsFinite()
            && sunDirection.Dot(_lastMacroShadowRequestedSunDirection) > 0.99996f)
            return;
        _lastMacroShadowRequestedSunDirection = sunDirection;
        QueueMacroShadowRebuild();
    }

    private void QueueMacroShadowRebuild()
    {
        _macroShadowRequestVersion++;
        if (!EnableMacroTerrainShadows || _heightPyramid == null || _macroShadowTask != null) return;
        StartMacroShadowBuild(_macroShadowRequestVersion);
    }

    private void StartMacroShadowBuild(ulong requestVersion)
    {
        if (_heightPyramid == null || _macroShadowTask != null || !EnableMacroTerrainShadows) return;
        _macroShadowCancellation?.Cancel();
        _macroShadowCancellation?.Dispose();
        _macroShadowCancellation = new CancellationTokenSource();
        CancellationToken cancellation = _macroShadowCancellation.Token;
        RingTerrainMinMaxPyramid pyramid = _heightPyramid;
        Vector3 sunDirection = _sunLight?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
        ulong editVersion = _grid?.LodSurfaceEditVersion ?? 0UL;
        int traceSteps = MacroTerrainShadowTraceSteps;
        float traceDistance = MacroTerrainShadowMaxTraceMeters;
        bool includeWalls = BuildStructuralEdgeWalls && EnableMacroTerrainWallShadows;
        float wallTop = _grid?.GetStructuralWallTopGlobalY() ?? 0.0f;
        float wallBottom = (_grid?.GetStructuralBottomGlobalY() ?? 0) * VoxelConstants.VoxelSizeMeters;
        _macroShadowTask = Task.Run(() =>
        {
            using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu(
                "Terrain macro shadow build", worker: true);
            var watch = Stopwatch.StartNew();
            RingTerrainMinMaxPyramid.DirectionalShadowMask mask = pyramid.BuildDirectionalShadowMask(
                sunDirection, cancellation, traceSteps, traceDistance,
                includeStructuralWalls: includeWalls,
                structuralWallTopMeters: wallTop,
                structuralWallBottomMeters: wallBottom);
            watch.Stop();
            return new MacroShadowBuildResult(
                mask,
                requestVersion,
                editVersion,
                sunDirection,
                traceSteps,
                traceDistance,
                includeWalls,
                watch.Elapsed.TotalMilliseconds);
        }, cancellation);
    }

    private void CompleteMacroShadowBuildIfReady()
    {
        if (_macroShadowTask == null || !_macroShadowTask.IsCompleted) return;
        Task<MacroShadowBuildResult> completed = _macroShadowTask;
        _macroShadowTask = null;
        if (!completed.IsCanceled && !completed.IsFaulted)
        {
            MacroShadowBuildResult result = completed.Result;
            bool editsChanged = _grid != null && _grid.LodSurfaceEditVersion != result.EditVersion;
            Vector3 currentSun = _sunLight?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
            bool settingsMatch = result.TraceSteps == MacroTerrainShadowTraceSteps
                && Mathf.IsEqualApprox(result.TraceDistance, MacroTerrainShadowMaxTraceMeters)
                && result.IncludesStructuralWalls == (BuildStructuralEdgeWalls && EnableMacroTerrainWallShadows);
            // A finished mask is still useful while a smooth sun has moved a
            // little.  Install it to avoid a perpetual reject/restart cycle,
            // then keep the newest request queued for the next completion.
            bool sunStillClose = currentSun.Dot(result.SunDirection) > 0.99939f;
            if (EnableMacroTerrainShadows && !editsChanged && settingsMatch
                && (result.RequestVersion == _macroShadowRequestVersion || sunStillClose))
            {
                _macroShadowAppliedVersion = result.RequestVersion;
                InstallMacroShadowMask(result.Mask, result.BuildMilliseconds);
            }
            else if (editsChanged)
            {
                _macroShadowRequestVersion++;
            }
        }
        else if (completed.IsFaulted)
        {
            GD.PushError($"Terrain macro shadow build failed: {completed.Exception?.GetBaseException().Message}");
        }

        bool pendingPyramidEdits = _grid != null && _grid.LodSurfaceEditVersion != _observedEditVersion;
        if (EnableMacroTerrainShadows && _heightPyramid != null && !pendingPyramidEdits
            && _macroShadowAppliedVersion != _macroShadowRequestVersion)
            StartMacroShadowBuild(_macroShadowRequestVersion);
    }

    private void InstallMacroShadowMask(
        in RingTerrainMinMaxPyramid.DirectionalShadowMask mask,
        double buildMilliseconds)
    {
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Terrain macro shadow install");
        if (mask.Width <= 0
            || mask.Height <= 0
            || mask.Pixels.Length != mask.Width * mask.Height)
        {
            return;
        }

        // Reuse the texture when the mask size is unchanged: updating in place
        // avoids allocating a new GPU texture and re-binding every receiver.
        ImageTexture texture;
        if (_macroShadowImage != null && _macroShadowTexture != null
            && _macroShadowImage.GetWidth() == mask.Width
            && _macroShadowImage.GetHeight() == mask.Height)
        {
            _macroShadowImage.SetData(mask.Width, mask.Height, false, Image.Format.R8, mask.Pixels);
            _macroShadowTexture.Update(_macroShadowImage);
            texture = _macroShadowTexture;
        }
        else
        {
            _macroShadowImage = Image.CreateFromData(
                mask.Width,
                mask.Height,
                false,
                Image.Format.R8,
                mask.Pixels);
            _macroShadowTexture = ImageTexture.CreateFromImage(_macroShadowImage);
            texture = _macroShadowTexture;
        }
        global::RingworldTechDemo.RingTerrainMacroShadowProjector.Update(
            texture,
            MacroTerrainShadowStrength,
            MacroTerrainShadowFadeStartMeters,
            MacroTerrainShadowFadeEndMeters);
        int receiverCount =
            global::RingworldTechDemo.RingTerrainMacroShadowProjector.ActiveReceiverCount;
        GD.Print(
            $"Terrain macro shadows ready: {mask.Width}x{mask.Height} R8, "
            + $"trace={mask.MaximumTraceDistanceMeters:0}m/"
            + $"{Mathf.Clamp(MacroTerrainShadowTraceSteps, 1, 32)} steps, "
            + $"receivers={receiverCount}, "
            + $"buildMs={buildMilliseconds:0.00}");
    }

    public void SetTerrainShadowCasting(bool enabled)
    {
        CastTerrainShadows = enabled;
        CastShadow = enabled ? ShadowCastingSetting.On : ShadowCastingSetting.Off;
        foreach (MeshInstance3D segment in _terrainSegments)
            segment.CastShadow = enabled ? ShadowCastingSetting.On : ShadowCastingSetting.Off;
        foreach (MeshInstance3D segment in _hullSegments)
            segment.CastShadow = enabled || BuildTerrainRoofUnderside || BuildLowerHullCavityFloor
                ? ShadowCastingSetting.On
                : ShadowCastingSetting.Off;
    }

    public void SetDebugColor(bool enabled, Color color)
    {
        _material?.SetShaderParameter("debug_color_enabled", enabled);
        _material?.SetShaderParameter("debug_color", new Vector3(color.R, color.G, color.B));
    }
}
