using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Complete, low-resolution terrain-aware ring surface for global visibility.</summary>
public partial class RingTerrainGlobalLod : MeshInstance3D
{
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
    private Node3D? _occluderRoot;
    private readonly List<MeshInstance3D> _terrainSegments = new();
    private readonly HashSet<Vector2I> _knownEditedColumns = new();
    private readonly Queue<int> _dirtySegmentQueue = new();
    private readonly HashSet<int> _queuedDirtySegments = new();
    private RingTerrainMinMaxPyramid? _heightPyramid;
    private Task<SummaryBuildResult>? _heightPyramidTask;
    private CancellationTokenSource? _heightPyramidCancellation;
    private Task<MacroShadowBuildResult>? _macroShadowTask;
    private CancellationTokenSource? _macroShadowCancellation;
    private ulong _macroShadowRequestVersion;
    private ulong _macroShadowAppliedVersion;
    private ulong _macroShadowInvalidatedEditVersion;
    private Vector3 _lastMacroShadowRequestedSunDirection = new(float.PositiveInfinity, 0.0f, 0.0f);
    private readonly Queue<int> _initialSegmentQueue = new();
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
    private RingLodSurfaceEditSnapshot? _pendingSegmentEdits;
    private ulong _observedEditVersion;
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
    public bool GlobalSummaryBuildPending => _heightPyramidTask != null;
    public bool InitialGlobalMeshBuildPending => _initialSegmentQueue.Count > 0;
    public long EstimatedSummaryBytes => _heightPyramid?.EstimatedBytes ?? 0L;
    public long EstimatedGlobalMeshBytes { get; private set; }
    public ulong HeightRangeRevision => _heightPyramid == null ? 0UL : _heightPyramid.EditVersion + 1UL;

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
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _focus = GetNodeOrNull<Node3D>(FocusPath);
        _sunLight = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        if (_grid?.TerrainSampler == null || _focus == null)
        {
            GD.PushWarning("RingTerrainGlobalLod could not resolve the shared sampler or focus.");
            return;
        }

        _grid.CanonicalFloatingOriginAnchorChanged += HandleCanonicalFloatingOriginAnchorChanged;

        CreateMaterial();
        global::RingworldTechDemo.RingTerrainMacroShadowProjector.Disable();
        StartHeightPyramidBuild();
        if (BuildStructuralEdgeWalls || BuildStructuralUnderside || BuildTerrainRoofUnderside || BuildLowerHullCavityFloor) BuildGlobalHullWalls();
        if (BuildTerrainOccluder) BuildGlobalTerrainOccluder();
    }

    public override void _ExitTree()
    {
        if (_grid != null)
            _grid.CanonicalFloatingOriginAnchorChanged -= HandleCanonicalFloatingOriginAnchorChanged;
        _heightPyramidCancellation?.Cancel();
        _heightPyramidCancellation?.Dispose();
        _heightPyramidCancellation = null;
        _macroShadowCancellation?.Cancel();
        _macroShadowCancellation?.Dispose();
        _macroShadowCancellation = null;
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
    }

    public override void _Process(double delta)
    {
        _ = delta;
        UpdateCameraUniform();
        UpdateLightingUniforms();
        UpdateRegionalSegmentVisibility(delta);
        CompleteHeightPyramidBuildIfReady();
        CompleteMacroShadowBuildIfReady();
        RequestMacroShadowRebuildForSunChange();
        ServiceInitialGlobalBuild();
        RefreshEditedSegments();
        ServiceSegmentRebuilds();
    }

    private void UpdateRegionalSegmentVisibility(double delta)
    {
        if (_focus == null || !EnableFarDistanceFade)
        {
            _lastRegionalVisibilityEnabled = false;
            return;
        }

        Vector3 focusPosition = _focus.GlobalPosition;
        bool configurationChanged = !_lastRegionalVisibilityEnabled
            || !Mathf.IsEqualApprox(_lastRegionalVisibilityDistance, RegionalVisibilityDistanceMeters);
        bool focusChanged = focusPosition != _lastRegionalVisibilityFocusPosition;
        if (!configurationChanged && !focusChanged) return;

        _visibilityRefreshElapsed += delta;
        if (!configurationChanged && _visibilityRefreshElapsed < 0.5) return;
        _visibilityRefreshElapsed = 0.0;
        foreach (MeshInstance3D segment in _terrainSegments)
        {
            UpdateRegionalInstanceVisibility(segment);
        }
        foreach (MeshInstance3D segment in _hullSegments)
            UpdateRegionalInstanceVisibility(segment);
        _lastRegionalVisibilityFocusPosition = focusPosition;
        _lastRegionalVisibilityDistance = RegionalVisibilityDistanceMeters;
        _lastRegionalVisibilityEnabled = true;
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

    private void CompleteHeightPyramidBuildIfReady()
    {
        if (_heightPyramidTask == null || !_heightPyramidTask.IsCompleted) return;
        Task<SummaryBuildResult> completed = _heightPyramidTask;
        _heightPyramidTask = null;
        if (completed.IsCanceled) return;
        if (completed.IsFaulted)
        {
            GD.PushError($"Global min/max summary failed: {completed.Exception?.GetBaseException().Message}");
            return;
        }

        SummaryBuildResult result = completed.Result;
        _heightPyramid = result.Pyramid;
        RingLodSurfaceEditSnapshot initialEdits = _grid!.CaptureLodSurfaceEditSnapshot();
        _heightPyramid.ApplyEdits(initialEdits);
        if (!_heightPyramid.ValidateHierarchy(out string hierarchyError))
            GD.PushError($"Phase C min/max hierarchy self-test failed: {hierarchyError}");
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
                || initialEdits.EditedColumns.Count > 0))
            QueueMacroShadowRebuild();
        if (_grid.AroundRingChunkLength * VoxelConstants.ChunkSizeX == CertifiedRingScale.AroundVoxelLength
            && Mathf.RoundToInt(_grid.RingAcrossWidthMeters) == CertifiedRingScale.RingWidthMeters)
        {
            RingTerrainMinMaxPyramid.ElevationAudit audit = _heightPyramid.CaptureBaselineElevationAudit(
                CertifiedRingScale.RoutineMountainCeilingMeters,
                7000);
            bool distributionValid = audit.MinimumHeight >= CertifiedRingScale.TerrainFloorMeters
                && audit.MaximumHeight <= CertifiedRingScale.ExceptionalSummitMeters
                && audit.CellsAboveRoutineCeiling <= 128
                && audit.CellsAtOrAboveNearSummit <= 32;
            string message = $"Certified elevation audit: range={audit.MinimumHeight}..{audit.MaximumHeight}m, above5200={audit.CellsAboveRoutineCeiling}, atOrAbove7000={audit.CellsAtOrAboveNearSummit}, baseCells={audit.TotalBaseCells}";
            if (distributionValid) GD.Print(message);
            else GD.PushError($"{message} (distribution contract failed)");

            RingTerrainSampler.HydrologyAudit water = _grid.TerrainSampler!.CaptureHydrologyAudit();
            GD.Print($"Certified hydrology audit: dry={water.DrySamples}, ocean={water.OceanSamples} (maxDepth={water.MaximumOceanDepth}m), lake={water.LakeSamples} (maxDepth={water.MaximumLakeDepth}m), river={water.RiverSamples} (maxDepth={water.MaximumRiverDepth}m), samples={water.DrySamples + water.OceanSamples + water.LakeSamples + water.RiverSamples}");
        }
        _observedEditVersion = initialEdits.Version;
        _knownEditedColumns.Clear();
        foreach (Vector2I column in initialEdits.EditedColumns.Keys) _knownEditedColumns.Add(column);
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

        ClearTerrainSegments();
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
        if (_initialSegmentEdits == null || _initialSegmentQueue.Count == 0) return;
        ulong start = Time.GetTicksUsec();
        int buildCount = 0;
        int buildBudget = Mathf.Max(1, MaxSegmentRebuildsPerFrame);
        while (buildCount < buildBudget && _initialSegmentQueue.Count > 0)
        {
            if (buildCount > 0
                && (Time.GetTicksUsec() - start) / 1000.0 >= MaxSegmentRebuildMillisecondsPerFrame)
                break;
            int index = _initialSegmentQueue.Dequeue();
            BuildGlobalSegment(index, _initialSegmentCount, null, _initialSegmentEdits);
            buildCount++;
        }

        if (_initialSegmentQueue.Count != 0) return;
        _initialSegmentEdits = null;
        GD.Print($"Segmented global terrain HLOD ready: segments={_terrainSegments.Count}, around={AroundSegments}, across={AcrossSegments}");
    }

    private void BuildGlobalSegment(int segmentIndex, int segmentCount, MeshInstance3D? existing, RingLodSurfaceEditSnapshot edits)
    {
        if (_grid?.TerrainSampler is not RingTerrainSampler sampler) return;
        int aroundCount = Mathf.Max(64, AroundSegments);
        int acrossCount = Mathf.Max(4, AcrossSegments);
        int aroundStartIndex = segmentIndex * aroundCount / segmentCount;
        int aroundEndIndex = (segmentIndex + 1) * aroundCount / segmentCount;
        int localAroundCount = Mathf.Max(1, aroundEndIndex - aroundStartIndex);
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        double aroundSpacing = circumference / aroundCount;
        double acrossSpacing = RingWidthMeters / (double)acrossCount;
        var terrainVertices = new Vector3[(localAroundCount + 1) * (acrossCount + 1)];
        var terrainColors = new Color[terrainVertices.Length];
        var waterVertices = new Vector3[terrainVertices.Length];
        var waterColors = new Color[terrainVertices.Length];
        var displayedWater = new bool[terrainVertices.Length];

        for (int z = 0; z <= acrossCount; z++)
        for (int x = 0; x <= localAroundCount; x++)
        {
            double around = (aroundStartIndex + x) * aroundSpacing;
            double across = z * acrossSpacing;
            int sampleAround = (int)Math.Round(around);
            int sampleAcross = (int)Math.Round(across);
            TerrainSample terrainSample = sampler.GetTerrainSample(sampleAround, sampleAcross);
            int sampledHeight = edits.TryGetEditedSurfaceHeight(sampleAround, sampleAcross, out int editedHeight)
                ? editedHeight
                : terrainSample.SurfaceHeight;
            int terrainHeight = UseMinMaxFeaturePreservation && _heightPyramid != null
                ? _heightPyramid.PreserveFeatureHeight(sampledHeight, (float)around, (float)across, (float)Math.Max(aroundSpacing, acrossSpacing))
                : sampledHeight;
            int waterHeight = terrainSample.ResolveDisplayedHeight(terrainHeight);
            int index = z * (localAroundCount + 1) + x;
            terrainColors[index] = RingTerrainVisuals.ResolveMacroTextureColor(
                terrainSample,
                terrainHeight,
                sampler,
                LowTerrainColor,
                HighTerrainColor);
            waterColors[index] = RingTerrainVisuals.ResolveMacroTextureColor(
                terrainSample,
                waterHeight,
                sampler,
                LowTerrainColor,
                HighTerrainColor);
            waterColors[index] = RingWaterModel.EncodeWaterKind(waterColors[index], terrainSample.WaterKind);
            displayedWater[index] = terrainSample.HasSurfaceWater && terrainHeight < terrainSample.WaterSurfaceHeight;
            terrainVertices[index] = ToLocal(_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(
                around,
                terrainHeight + 1.0 - RadialUnderlayOffsetMeters,
                across)));
            waterVertices[index] = ToLocal(_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(
                around,
                waterHeight + 1.0 - RadialUnderlayOffsetMeters,
                across)));
        }

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
            if (IsWaterTriangle(displayedWater, i01, i10, i00))
            {
                AddVertex(oceanSurface, waterVertices[i01], waterColors[i01]); AddVertex(oceanSurface, waterVertices[i10], waterColors[i10]); AddVertex(oceanSurface, waterVertices[i00], waterColors[i00]);
                oceanTriangleCount++;
            }
            if (IsWaterTriangle(displayedWater, i10, i01, i11))
            {
                AddVertex(oceanSurface, waterVertices[i10], waterColors[i10]); AddVertex(oceanSurface, waterVertices[i01], waterColors[i01]); AddVertex(oceanSurface, waterVertices[i11], waterColors[i11]);
                oceanTriangleCount++;
            }
        }
        var mesh = new ArrayMesh();
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
        }
        if (EnableFarDistanceFade) UpdateRegionalInstanceVisibility(segment);
    }

    private void RefreshEditedSegments()
    {
        if (_grid == null || _heightPyramid == null || _initialSegmentQueue.Count > 0 || _grid.LodSurfaceEditVersion == _observedEditVersion) return;
        // The background trace reads the pyramid's hierarchy.  Defer mutation
        // until it has stopped, and reject its now-obsolete mask immediately.
        if (_macroShadowTask != null)
        {
            if (_macroShadowInvalidatedEditVersion != _grid.LodSurfaceEditVersion)
            {
                _macroShadowInvalidatedEditVersion = _grid.LodSurfaceEditVersion;
                _macroShadowRequestVersion++;
            }
            return;
        }
        RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
        var dirtyColumns = new HashSet<Vector2I>(edits.DirtyColumns);
        _heightPyramid.ApplyEdits(edits);
        int segmentCount = _terrainSegments.Count;
        int aroundLength = (int)Math.Max(1L, Math.Min(
            int.MaxValue,
            _grid.TerrainSampler?.AroundRingVoxelLength ?? 1L));
        foreach (Vector2I column in dirtyColumns)
        {
            int around = Mathf.PosMod(column.X, aroundLength);
            int segment = Mathf.Clamp(around * segmentCount / aroundLength, 0, Mathf.Max(0, segmentCount - 1));
            QueueDirtySegment(segment, segmentCount);
            QueueDirtySegment(Mathf.PosMod(segment - 1, segmentCount), segmentCount);
            QueueDirtySegment(Mathf.PosMod(segment + 1, segmentCount), segmentCount);
        }
        if (_grid.TerrainSampler != null)
            _pendingSegmentEdits = PrepareGlobalEditSnapshot(edits, _grid.TerrainSampler);
        _knownEditedColumns.Clear();
        foreach (Vector2I column in edits.EditedColumns.Keys) _knownEditedColumns.Add(column);
        _observedEditVersion = edits.Version;
        _macroShadowInvalidatedEditVersion = 0;
        QueueMacroShadowRebuild();
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
        if (_pendingSegmentEdits == null || _dirtySegmentQueue.Count == 0 || _terrainSegments.Count == 0) return;
        ulong start = Time.GetTicksUsec();
        int rebuildBudget = Mathf.Max(1, MaxSegmentRebuildsPerFrame);
        while (LastSegmentRebuildCount < rebuildBudget && _dirtySegmentQueue.Count > 0)
        {
            if (LastSegmentRebuildCount > 0
                && (Time.GetTicksUsec() - start) / 1000.0 >= MaxSegmentRebuildMillisecondsPerFrame)
                break;
            int index = _dirtySegmentQueue.Dequeue();
            _queuedDirtySegments.Remove(index);
            if (index >= 0 && index < _terrainSegments.Count)
                BuildGlobalSegment(index, _terrainSegments.Count, _terrainSegments[index], _pendingSegmentEdits);
            LastSegmentRebuildCount++;
        }
        LastSegmentRebuildMilliseconds = (Time.GetTicksUsec() - start) / 1000.0;
    }

    private void ClearTerrainSegments()
    {
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
        _segmentEstimatedBytes.Clear();
        EstimatedGlobalMeshBytes = 0L;
        _lastRegionalVisibilityFocusPosition = new Vector3(float.PositiveInfinity, 0.0f, 0.0f);
    }

    private void BuildGlobalTerrainOccluder()
    {
        if (_grid?.TerrainSampler is not RingTerrainSampler sampler)
        {
            return;
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
        }

        GD.Print($"Conservative bedrock occluders: segments={aroundCount}, bedrock={sampler.BedrockY}, safety={safetyMargin:0.0}m, chordDepth={safeCanonicalHeight - endpointCanonicalHeight:0.0}m");
    }

    private static void AddVertex(SurfaceTool surface, Vector3 vertex, Color color)
    {
        surface.SetColor(color);
        surface.AddVertex(vertex);
    }

    private static bool IsWaterTriangle(bool[] waterVertices, int a, int b, int c) =>
        (waterVertices[a] ? 1 : 0) + (waterVertices[b] ? 1 : 0) + (waterVertices[c] ? 1 : 0) >= 2;

    private void CreateMaterial()
    {
        RingTerrainVisuals.PreloadTexturePalette();
        Shader? shader = GD.Load<Shader>("res://materials/ring_terrain_global.gdshader");
        if (shader == null) return;
        _material = new ShaderMaterial { Shader = shader };
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

    private void UpdateLightingUniforms()
    {
        if (_grid == null || _sunLight == null)
        {
            return;
        }

        Vector3 ringCenter = _grid.GetCanonicalRingCenterWorld();
        Vector3 sunDirection = _sunLight.GlobalTransform.Basis.Z.Normalized();
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
        if (_grid == null || _hullMaterial == null)
        {
            return;
        }

        ClearHullSegments();
        int aroundCount = Mathf.Max(64, AroundSegments);
        int segmentCount = Mathf.Clamp(GlobalSegmentCount, 1, aroundCount);
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
            var surface = new SurfaceTool();
            surface.Begin(Mesh.PrimitiveType.Triangles);
            if (BuildStructuralEdgeWalls)
            {
                AppendHullWallSegment(surface, 0.0f, aroundStartIndex, aroundEndIndex, verticalCount, aroundSpacing, verticalSpacing, hullBottom, reverse: true);
                AppendHullWallSegment(surface, RingWidthMeters, aroundStartIndex, aroundEndIndex, verticalCount, aroundSpacing, verticalSpacing, hullBottom, reverse: false);
            }
            if (BuildStructuralUnderside)
            {
                AppendHullUndersideSegment(
                    surface,
                    aroundStartIndex,
                    aroundEndIndex,
                    aroundSpacing,
                    hullBottom);
            }
            if (BuildTerrainRoofUnderside)
            {
                AppendHullRoofUndersideSegment(
                    surface,
                    aroundStartIndex,
                    aroundEndIndex,
                    aroundSpacing,
                    _grid.TerrainBottomGlobalY);
            }
            if (BuildLowerHullCavityFloor)
            {
                AppendHullLowerCavityFloorSegment(
                    surface, aroundStartIndex, aroundEndIndex, aroundSpacing,
                    _grid.GetLowerHullTopGlobalY());
            }
            surface.GenerateNormals();
            surface.Index();

            var segment = new MeshInstance3D
            {
                Name = $"GlobalStructuralEdgeWalls_{segmentIndex:D3}",
                Mesh = surface.Commit(),
                MaterialOverride = _hullMaterial,
                // The terrain-bottom roof is structural closure, not optional
                // distant terrain decoration; preserve its shadow caster even
                // when this global surface LOD has terrain shadows disabled.
                CastShadow = (CastTerrainShadows || BuildTerrainRoofUnderside || BuildLowerHullCavityFloor)
                    ? ShadowCastingSetting.On
                    : ShadowCastingSetting.Off,
                IgnoreOcclusionCulling = true
            };
            AddChild(segment);
            _hullSegments.Add(segment);
            if (EnableFarDistanceFade) UpdateRegionalInstanceVisibility(segment);
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
        AroundSegments = Mathf.Clamp(aroundSegments, 64, 4096);
        AcrossSegments = Mathf.Clamp(acrossSegments, 4, 256);
        ApplyRuntimeFade(fadeStart, fadeEnd);
        if (_grid?.TerrainSampler == null) return;

        Mesh = null;
        ClearHullSegments();
        if (_occluderRoot != null)
        {
            RemoveChild(_occluderRoot);
            _occluderRoot.QueueFree();
            _occluderRoot = null;
        }

        BuildGlobalRing();
        if (BuildStructuralEdgeWalls || BuildStructuralUnderside || BuildTerrainRoofUnderside || BuildLowerHullCavityFloor) BuildGlobalHullWalls();
        if (BuildTerrainOccluder) BuildGlobalTerrainOccluder();
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
        if (changed) QueueMacroShadowRebuild();
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
            return;
        }
        if (changed) QueueMacroShadowRebuild();
    }

    private void RequestMacroShadowRebuildForSunChange()
    {
        if (!EnableMacroTerrainShadows || _heightPyramid == null || _sunLight == null) return;
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
        if (mask.Width <= 0
            || mask.Height <= 0
            || mask.Pixels.Length != mask.Width * mask.Height)
        {
            return;
        }

        Image image = Image.CreateFromData(
            mask.Width,
            mask.Height,
            false,
            Image.Format.R8,
            mask.Pixels);
        ImageTexture texture = ImageTexture.CreateFromImage(image);
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
