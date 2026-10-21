using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public enum UndergroundProxyTier { None, Freeze, Hide }

/// <summary>Coordinates the configurable nested terrain LOD stack.</summary>
public partial class RingTerrainLodCoordinator : Node
{
    public enum QualityPreset
    {
        Low,
        Medium,
        High,
        VeryHigh,
        UltraVideo,
        Cinematic5090
    }

    [ExportGroup("LOD Nodes")]
    [Export] public NodePath Lod1Path { get; set; } = new("../Lod1Terrain2m");
    [Export] public NodePath Lod2Path { get; set; } = new("../Lod2Terrain4m");
    [Export] public NodePath Lod3Path { get; set; } = new("../Lod3Terrain8m");
    [Export] public NodePath Lod4Path { get; set; } = new("../Lod4Terrain16m");
    [Export] public NodePath Lod5Path { get; set; } = new("../Lod5Terrain32m");
    [Export] public NodePath Lod6Path { get; set; } = new("../Lod6Terrain64m");
    [Export] public NodePath Lod7Path { get; set; } = new("../Lod7Terrain128m");
    [Export] public NodePath Lod8Path { get; set; } = new("../Lod8Terrain256m");
    // These bridge the historical 256m -> 2km transition without changing
    // any legacy LOD identity, quality-profile field, or saved setting.
    [Export] public NodePath Lod8Bridge512mPath { get; set; } = new("../Lod8BridgeTerrain512m");
    [Export] public NodePath Lod8Bridge1024mPath { get; set; } = new("../Lod8BridgeTerrain1024m");
    [Export] public NodePath Lod9Path { get; set; } = new("../Lod9Terrain2km");
    [Export] public NodePath Lod9Bridge4096mPath { get; set; } = new("../Lod9BridgeTerrain4096m");
    [Export] public NodePath Lod10Path { get; set; } = new("../Lod10Terrain8km");
    [Export] public NodePath Lod10Bridge16384mPath { get; set; } = new("../Lod10BridgeTerrain16384m");
    [Export] public NodePath Lod11Path { get; set; } = new("../Lod11Terrain32km");
    [Export] public NodePath GlobalLodPath { get; set; } = new("../GlobalTerrainLod3");
    [Export] public NodePath CameraPath { get; set; } = new("../Explorer/Head/Camera3D");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");

    [ExportGroup("Quality Presets")]
    [Export] public QualityPreset DefaultPreset { get; set; } = QualityPreset.Medium;
    [Export] public RingLodQualityProfile? LowProfile { get; set; }
    [Export] public RingLodQualityProfile? MediumProfile { get; set; }
    [Export] public RingLodQualityProfile? HighProfile { get; set; }
    [Export] public RingLodQualityProfile? VeryHighProfile { get; set; }
    [Export] public RingLodQualityProfile? UltraVideoProfile { get; set; }
    [Export] public RingLodQualityProfile? Cinematic5090Profile { get; set; }
    [Export] public bool AllowAdaptiveFallbackAfterManualSelection { get; set; } = false;

    [ExportGroup("Mega Regional Extension")]
    [Export] public bool EnableMegaRegionalExtension { get; set; }
    // The scaled, camera-relative viewport owns LOD9-11 on the Mega Ring. Do
    // not let absolute-world proxy sectors build the same ranges underneath it.
    [Export] public bool RouteMegaFarLodsToScaledViewport { get; set; }
    [Export] public RingLodQualityProfile? MegaRegionalProfile { get; set; }
    [Export] public bool EnableIntermediateLodTiers { get; set; } = true;

    [ExportGroup("Shared Budgets")]
    [Export(PropertyHint.Range, "1,16,1")] public int TotalSectorUploadsPerFrame { get; set; } = 3;
    [Export(PropertyHint.Range, "1,32,1")] public int MaxConcurrentWorkerBuilds { get; set; } = 2;
    [Export(PropertyHint.Range, "0.25,8.0,0.05")] public float MaxUploadMillisecondsPerFrame { get; set; } = 2.0f;
    [ExportGroup("LOD build starts")]
    // Starting a build costs main-thread time (edit snapshot, sampler copy, cache setup).
    // These bound how many starts happen per frame; starved levels jump the queue.
    [Export(PropertyHint.Range, "1,8,1")] public int MaxBuildStartsPerFrame { get; set; } = 1;
    [Export(PropertyHint.Range, "0.25,16.0,0.05")] public float MaxBuildStartMillisecondsPerFrame { get; set; } = 2.0f;
    [Export(PropertyHint.Range, "5,300,1")] public int BuildStartStarvationFrames { get; set; } = 30;

    [ExportGroup("Mesh memory pressure")]
    [Export] public bool EnableMeshMemoryPressureThrottle { get; set; } = true;
    [Export(PropertyHint.Range, "32,2048,16")] public int MaximumStagedMeshMemoryMiB { get; set; } = 160;
    [Export(PropertyHint.Range, "25,95,5")] public int StagedMeshRecoveryPercent { get; set; } = 65;
    [Export(PropertyHint.Range, "1,16,1")] public int MemoryPressureWorkerLimit { get; set; } = 2;
    // Legacy name retained for scene compatibility. When enabled, only the
    // active proxy tiers inside the local directional-light shadow bubble cast.
    // Distant regional tiers remain out of the hardware shadow pass.
    [Export] public bool RestrictShadowsToNearestActiveLevel { get; set; } = true;
    [Export(PropertyHint.Range, "100,50000,100")] public float TerrainShadowCoverageMeters { get; set; } = 4000.0f;

    [ExportGroup("Generated LOD Disk Cache")]
    [Export] public bool EnableGeneratedLodDiskCache { get; set; } = true;
    [Export] public bool ReadGeneratedLodDiskCache { get; set; } = true;
    [Export] public bool WriteGeneratedLodDiskCache { get; set; } = true;
    [Export(PropertyHint.Range, "1,8,1")] public int FirstPersistentLod { get; set; } = 3;
    [Export(PropertyHint.Range, "0,8192,64")] public int GeneratedLodDiskCacheBudgetMiB { get; set; } = 512;
    [Export(PropertyHint.Range, "25,100,1")] public int GeneratedLodDiskCacheTrimToPercent { get; set; } = 75;
    [Export(PropertyHint.Range, "10,3600,10")] public float GeneratedLodDiskCacheMaintenanceSeconds { get; set; } = 30.0f;

    [ExportGroup("Fast Travel")]
    // Terrain is authoritative gameplay coverage.  It must continue to follow
    // the player at every noclip speed; retaining a stale window causes the
    // world to appear to disappear at the travel threshold.
    [Export] public bool SuspendProxyRebuildsDuringFastTravel { get; set; }
    [Export(PropertyHint.Range, "100,1000000,100")] public float FastTravelSpeedThresholdMetersPerSecond { get; set; } = 750.0f;
    [Export(PropertyHint.Range, "0.0,100.0,0.1")] public float FastTravelStoppedSpeedThresholdMetersPerSecond { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0.0,2.0,0.05")] public float FastTravelResumeStoppedSeconds { get; set; } = 0.25f;
    // Retained so older scenes deserialize cleanly. Continuous proxy streaming
    // is now governed by SuspendProxyRebuildsDuringFastTravel; visibility is
    // never changed by these compatibility fields.
    [Export] public bool KeepCoarseProxiesStreamingDuringFastTravel { get; set; } = false;
    // Retained only so older scenes deserialize cleanly.  It is deliberately
    // ignored: no movement speed is allowed to suppress terrain rendering.
    [Export] public bool HideProxyMeshesDuringFastTravel { get; set; }
    [Export(PropertyHint.Range, "1,8,1")] public int FirstFastTravelContinuousLod { get; set; } = 1;

    [ExportGroup("High altitude proxy handoff")]
    // Retained only so older scenes deserialize cleanly. The fixed altitude
    // switch was replaced by per-level activation (a tier streams while its far
    // handoff exceeds the viewer altitude); these values are deliberately ignored.
    [Export] public bool SuspendRegionalProxiesAtHighAltitude { get; set; } = true;
    [Export(PropertyHint.Range, "1000,60000,250")]
    public float SuspendRegionalProxiesAboveAltitudeMeters { get; set; } = 20000.0f;
    [Export(PropertyHint.Range, "0,60000,250")]
    public float ResumeRegionalProxiesBelowAltitudeMeters { get; set; } = 16000.0f;
    [Export] public bool KeepCoarseProxiesStreamingAtHighAltitude { get; set; } = true;
    [Export(PropertyHint.Range, "1,8,1")] public int FirstHighAltitudeContinuousLod { get; set; } = 6;

    [ExportGroup("Global LOD guard")]
    [Export] public bool EnforceMinimumGlobalHandoffDistance { get; set; } = true;
    [Export(PropertyHint.Range, "1000,250000,1000")] public float MinimumGlobalHandoffDistanceMeters { get; set; } = 16000.0f;

    private readonly List<RingTerrainProxySector> _levels = new();
    // Physical indices for the original LOD1..LOD11 sequence. Inserted bridge
    // tiers deliberately do not consume a legacy profile/count/settings slot.
    private readonly List<int> _legacyLevelIndices = new();
    private readonly HashSet<int> _intermediateLevelIndices = new();
    private readonly List<bool> _inspectorShadowDefaults = new();
    private readonly List<Vector2I> _inspectorRadiusDefaults = new();
    private readonly List<Vector2> _runtimeBands = new();
    private RingTerrainGlobalLod? _globalLod;
    private RingTerrainProxySector? _globalMorphTargetRecipient;
    private RingTerrainGlobalMorphSnapshot.Identity? _lastGlobalMorphSnapshotIdentity;
    private Camera3D? _camera;
    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private RingLodQualityProfile? _currentProfile;
    private float _voxelFadeStart = 64.0f;
    private float _voxelFadeEnd = 104.0f;
    private int _mediumGlobalAroundSegments = 512;
    private int _mediumGlobalAcrossSegments = 32;
    private int _roundRobinStart;
    private BuildStartCandidate[] _buildStartCandidates = new BuildStartCandidate[16];
    private double _avgBuildStartMilliseconds;
    private float _lastViewportHeight;
    private float _lastCameraFov;
    private double _screenSpaceRefreshElapsed;
    private bool _manualPresetSelected;
    private bool _fastTravelStreamingActive;
    private double _fastTravelStoppedElapsedSeconds;
    // Per-level altitude activation state (hysteresis); true means the level
    // may stream. Levels whose far handoff lies well below the viewer altitude
    // are never visible and release their windows.
    private bool[] _altitudeActive = Array.Empty<bool>();
    private int _altitudeSuppressedLevelCount;
    private int _altitudePlanSignature;
    private float _viewerHeightAboveSeaMeters;
    private long _lastAltitudeAroundCell = long.MinValue;
    private int _lastAltitudeAcrossCell = int.MinValue;
    private ulong _lastAltitudeEditVersion = ulong.MaxValue;
    private float _cachedAltitudeSurfaceHeight;
    public bool MeshMemoryPressureActive { get; private set; }

    public int LastFrameBuildStarts { get; private set; }
    public double LastFrameBuildStartMilliseconds { get; private set; }
    public long DeferredBuildStartCount { get; private set; }
    public int LastFrameUploadCount { get; private set; }
    public double LastFrameUploadMilliseconds { get; private set; }
    public long EstimatedActiveMeshBytes
    {
        get
        {
            long total = 0;
            foreach (RingTerrainProxySector level in _levels) total += level.EstimatedActiveMeshBytes;
            return total;
        }
    }
    public long EstimatedPendingMeshBytes
    {
        get
        {
            long total = 0;
            foreach (RingTerrainProxySector level in _levels) total += level.EstimatedStagedMeshBytes;
            return total;
        }
    }

    public IReadOnlyList<RingTerrainProxySector> Levels => _levels;
    public QualityPreset CurrentPreset { get; private set; } = QualityPreset.Medium;
    public string CurrentPresetName => _currentProfile?.DisplayName ?? CurrentPreset.ToString();
    public int PendingUploadCount
    {
        get
        {
            int total = 0;
            foreach (RingTerrainProxySector level in _levels) total += level.PendingUploadCount;
            return total;
        }
    }

    public int ActiveWorkerCount
    {
        get
        {
            int total = 0;
            foreach (RingTerrainProxySector level in _levels)
                total += level.ActiveWorkerSlots;
            return total;
        }
    }

    public int AdaptiveFallbackCount { get; private set; }
    public bool FastTravelStreamingActive => _fastTravelStreamingActive;
    /// <summary>Underground-mode proxy tier. Freeze stops rebuilds and keeps meshes; Hide additionally hides surface sectors (never the structural sidewalls).</summary>
    public UndergroundProxyTier UndergroundTier { get; private set; }

    public void SetUndergroundTier(UndergroundProxyTier tier)
    {
        if (UndergroundTier == tier) return;
        UndergroundTier = tier;
        ApplyFastTravelTierPolicy();
    }
    /// <summary>True while at least one proxy level is released because the viewer is above its reach.</summary>
    public bool HighAltitudeProxySuspended => _altitudeSuppressedLevelCount > 0;
    public float CurrentViewerAltitudeAboveTerrainMeters { get; private set; }
    public float CurrentTargetPixelError => _currentProfile?.TargetPixelError ?? 0.0f;

    public void ConfigureMegaRegionalExtension()
    {
        EnableMegaRegionalExtension = true;
        RouteMegaFarLodsToScaledViewport = true;
    }

    public void SetIntermediateLodTiersEnabled(bool enabled)
    {
        if (EnableIntermediateLodTiers == enabled) return;
        EnableIntermediateLodTiers = enabled;
        if (!IsInsideTree() || _currentProfile == null) return;
        RebuildRuntimeBands(_currentProfile);
        ConfigureVoxelTransition(_voxelFadeStart, _voxelFadeEnd);
    }

    public bool ApplyMemoryPressureFallback()
    {
        if (_manualPresetSelected && !AllowAdaptiveFallbackAfterManualSelection)
            return false;
        QualityPreset next = CurrentPreset switch
        {
            QualityPreset.Cinematic5090 => QualityPreset.UltraVideo,
            QualityPreset.UltraVideo => QualityPreset.VeryHigh,
            QualityPreset.VeryHigh => QualityPreset.High,
            QualityPreset.High => QualityPreset.Medium,
            QualityPreset.Medium => QualityPreset.Low,
            _ => QualityPreset.Low
        };
        if (next == CurrentPreset) return false;
        string previous = CurrentPresetName;
        ApplyQualityPreset(next);
        AdaptiveFallbackCount++;
        GD.PushWarning($"Adaptive terrain fallback: {previous} -> {CurrentPresetName} due to memory pressure.");
        return true;
    }

    public override void _Ready()
    {
        RegisterLevels();
        ConfigureGeneratedLodDiskCaches();
        _globalLod = GetNodeOrNull<RingTerrainGlobalLod>(GlobalLodPath);
        _camera = GetNodeOrNull<Camera3D>(CameraPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        if (_globalLod != null)
        {
            _mediumGlobalAroundSegments = _globalLod.AroundSegments;
            _mediumGlobalAcrossSegments = _globalLod.AcrossSegments;
        }
        ApplyQualityPreset(DefaultPreset);
        ValidatePhaseCConfiguration();
    }

    private void RegisterLevels()
    {
        AddLevel(Lod1Path, legacyLevel: true);
        AddLevel(Lod2Path, legacyLevel: true);
        AddLevel(Lod3Path, legacyLevel: true);
        AddLevel(Lod4Path, legacyLevel: true);
        AddLevel(Lod5Path, legacyLevel: true);
        AddLevel(Lod6Path, legacyLevel: true);
        AddLevel(Lod7Path, legacyLevel: true);
        AddLevel(Lod8Path, legacyLevel: true);
        if (ShouldBuildAbsoluteWorldMegaProxyTiers(
                EnableMegaRegionalExtension,
                RouteMegaFarLodsToScaledViewport))
        {
            AddLevel(Lod8Bridge512mPath, legacyLevel: false, intermediateLevel: true);
            AddLevel(Lod8Bridge1024mPath, legacyLevel: false, intermediateLevel: true);
            AddLevel(Lod9Path, legacyLevel: true);
            AddLevel(Lod9Bridge4096mPath, legacyLevel: false, intermediateLevel: true);
            AddLevel(Lod10Path, legacyLevel: true);
            AddLevel(Lod10Bridge16384mPath, legacyLevel: false, intermediateLevel: true);
            AddLevel(Lod11Path, legacyLevel: true);
        }
    }

    private void ConfigureGeneratedLodDiskCaches()
    {
        int firstPersistentIndex = ResolvePhysicalIndexForLegacyCount(FirstPersistentLod) - 1;
        bool commandLineDisabled = Array.Exists(
            OS.GetCmdlineUserArgs(),
            argument => argument.Equals("--disable-lod-disk-cache", StringComparison.OrdinalIgnoreCase));
        for (int i = 0; i < _levels.Count; i++)
        {
            _levels[i].ConfigureGeneratedLodDiskCache(
                EnableGeneratedLodDiskCache && !commandLineDisabled && i >= firstPersistentIndex,
                ReadGeneratedLodDiskCache,
                WriteGeneratedLodDiskCache,
                GeneratedLodDiskCacheBudgetMiB,
                GeneratedLodDiskCacheTrimToPercent,
                GeneratedLodDiskCacheMaintenanceSeconds);
        }
    }

    public override void _Process(double delta)
    {
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Terrain LOD coordinator update (inclusive)");
        if (_levels.Count == 0) return;
        using (RingPerformanceTelemetry.MeasureCpu("Terrain LOD coordinator prelude"))
        {
            UpdateFastTravelStreamingState(delta);
            UpdateAltitudeLevelActivation();
            UpdateTerminalGlobalMorphTarget();
            RefreshScreenSpaceSelectionIfNeeded(delta);
        }
        bool reserveRenderQueueForVoxel = _grid?.VoxelUploadBackpressureRequestedThisFrame == true
            || _grid?.VoxelMeshUploadedThisFrame == true;
        int remaining = reserveRenderQueueForVoxel
            ? 0
            : Mathf.Max(1, TotalSectorUploadsPerFrame);
        ulong frameStart = Time.GetTicksUsec();
        int initialBudget = remaining;
        int start = _roundRobinStart % _levels.Count;
        for (int offset = 0; offset < _levels.Count; offset++)
        {
            RingTerrainProxySector level = _levels[(start + offset) % _levels.Count];
            int levelBudget = level.StreamingActive
                ? Mathf.Min(Mathf.Max(0, level.MaxSectorUploadsPerFrame), remaining)
                : 0;
            double elapsedMs = (Time.GetTicksUsec() - frameStart) / 1000.0;
            double remainingMs = Mathf.Max(0.0, MaxUploadMillisecondsPerFrame - elapsedMs);
            remaining -= level.ServiceStreaming(delta, levelBudget, remainingMs);
        }
        LastFrameUploadCount = initialBudget - remaining;
        LastFrameUploadMilliseconds = (Time.GetTicksUsec() - frameStart) / 1000.0;
        _roundRobinStart = (start + 1) % _levels.Count;
        using (RingPerformanceTelemetry.MeasureCpu("Terrain LOD coordinator worker starts"))
            StartPendingWorkers();
    }

    private void UpdateFastTravelStreamingState(double delta)
    {
        bool eligible = SuspendProxyRebuildsDuringFastTravel
            && _explorer is { NoclipEnabled: true };
        float actualSpeed = _explorer?.ActualTravelSpeedMetersPerSecond ?? 0.0f;
        bool next = false;
        if (eligible)
        {
            if (_fastTravelStreamingActive)
            {
                float stoppedThreshold = Mathf.Max(0.0f, FastTravelStoppedSpeedThresholdMetersPerSecond);
                if (actualSpeed <= stoppedThreshold)
                {
                    _fastTravelStoppedElapsedSeconds += Mathf.Max(0.0, delta);
                    next = _fastTravelStoppedElapsedSeconds
                        < Mathf.Max(0.0f, FastTravelResumeStoppedSeconds);
                }
                else
                {
                    _fastTravelStoppedElapsedSeconds = 0.0;
                    next = true;
                }
            }
            else
            {
                _fastTravelStoppedElapsedSeconds = 0.0;
                next = actualSpeed
                    >= Mathf.Max(100.0f, FastTravelSpeedThresholdMetersPerSecond);
            }
        }
        else
        {
            _fastTravelStoppedElapsedSeconds = 0.0;
        }
        if (next == _fastTravelStreamingActive) return;
        bool resumedAfterStoppedHold = _fastTravelStreamingActive && eligible && !next;
        _fastTravelStreamingActive = next;
        _fastTravelStoppedElapsedSeconds = 0.0;
        ApplyFastTravelTierPolicy();
        GD.Print(next
            ? "Fast-travel regional LOD rebuilds suspended; installed terrain remains visible."
            : resumedAfterStoppedHold
                ? "Fast-travel regional LODs resumed at the current canonical position "
                    + $"after {Mathf.Max(0.0f, FastTravelResumeStoppedSeconds):0.00}s stopped."
                : "Fast-travel fine LOD rebuilds resumed because noclip fast travel ended.");
    }

    /// <summary>
    /// Activates a level while the viewer is within its reach: a level whose far handoff
    /// is below the camera altitude can never be seen, so it must not stream or hold
    /// meshes. Hysteresis pre-warms on descent and delays release on ascent.
    /// </summary>
    private void UpdateAltitudeLevelActivation()
    {
        if (_explorer == null || _levels.Count == 0) return;
        CurrentViewerAltitudeAboveTerrainMeters = ResolveViewerAltitudeAboveTerrain(
            _explorer.CanonicalPosition);
        _viewerHeightAboveSeaMeters = (float)_explorer.CanonicalPosition.VerticalMeters;
        EnsureAltitudeStateCapacity();

        int signature = 17;
        int suppressed = 0;
        float altitude = CurrentViewerAltitudeAboveTerrainMeters;
        for (int i = 0; i < _levels.Count; i++)
        {
            float far = GetRuntimeBand(i).Y;
            bool next = RingTerrainLodChainPlanner.IsLevelActiveAtAltitude(far, altitude, _altitudeActive[i]);
            _altitudeActive[i] = next;
            if (!next) suppressed++;
            signature = signature * 31 + (next ? 1 : 0);
            if (next) signature = signature * 31 + QuantizedAltitudeReach(i, far);
        }
        _altitudeSuppressedLevelCount = suppressed;
        if (signature == _altitudePlanSignature) return;
        _altitudePlanSignature = signature;
        ConfigureVoxelTransition(_voxelFadeStart, _voxelFadeEnd);
        GD.Print($"Altitude LOD plan at {altitude:0}m: {_levels.Count - suppressed}/{_levels.Count} proxy levels streaming.");
    }

    private void EnsureAltitudeStateCapacity()
    {
        if (_altitudeActive.Length == _levels.Count) return;
        _altitudeActive = new bool[_levels.Count];
        Array.Fill(_altitudeActive, true);
        _altitudePlanSignature = 0;
    }

    private bool IsAltitudeActive(int levelIndex)
        => levelIndex < 0 || levelIndex >= _altitudeActive.Length || _altitudeActive[levelIndex];

    /// <summary>
    /// Horizontal reach a level needs at the current altitude, rounded up to whole
    /// sectors so the streaming window only changes when a sector boundary is crossed.
    /// </summary>
    private int QuantizedAltitudeReach(int levelIndex, float farHandoffMeters)
    {
        int size = Mathf.Max(1, _levels[levelIndex].SectorSizeMeters);
        float reach = RingTerrainLodChainPlanner.TrimmedHorizontalReach(
            farHandoffMeters, _viewerHeightAboveSeaMeters);
        int sectors = Mathf.Max(1, Mathf.CeilToInt(reach / size));
        return Mathf.Max(size, Mathf.Min(sectors * size, Mathf.CeilToInt(farHandoffMeters)));
    }

    private float ResolveViewerAltitudeAboveTerrain(in RingCanonicalPosition canonical)
    {
        if (_grid?.TerrainSampler == null)
            return Mathf.Max(0.0f, (float)canonical.VerticalMeters);

        const int cacheCellMeters = 256;
        double circumference = System.Math.Max(
            1.0,
            _grid.GetCanonicalRingCircumferenceMeters64());
        double wrappedAround = canonical.AroundMeters % circumference;
        if (wrappedAround < 0.0) wrappedAround += circumference;
        long around = (long)System.Math.Round(wrappedAround);
        int across = Mathf.Clamp(
            Mathf.RoundToInt((float)canonical.AcrossMeters),
            0,
            Mathf.Max(0, Mathf.RoundToInt(_grid.RingAcrossWidthMeters)));
        long aroundCell = (long)System.Math.Floor(around / (double)cacheCellMeters);
        int acrossCell = Mathf.FloorToInt(across / (float)cacheCellMeters);
        ulong editVersion = _grid.LodSurfaceEditVersion;
        if (aroundCell != _lastAltitudeAroundCell
            || acrossCell != _lastAltitudeAcrossCell
            || editVersion != _lastAltitudeEditVersion)
        {
            RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
            _cachedAltitudeSurfaceHeight = _grid.TerrainSampler.UsesRuntimeSurfaceGeneration || edits.UsesFloatSurface
                ? edits.GetPresentationSurfaceHeight(_grid.TerrainSampler, around, across)
                : edits.GetSurfaceHeight(_grid.TerrainSampler, around, across);
            _lastAltitudeAroundCell = aroundCell;
            _lastAltitudeAcrossCell = acrossCell;
            _lastAltitudeEditVersion = editVersion;
        }

        return Mathf.Max(
            0.0f,
            (float)(canonical.VerticalMeters - _cachedAltitudeSurfaceHeight));
    }

    private void RefreshScreenSpaceSelectionIfNeeded(double delta)
    {
        if (_currentProfile?.UseScreenSpaceError != true) return;
        _screenSpaceRefreshElapsed += delta;
        if (_screenSpaceRefreshElapsed < 0.5) return;
        _screenSpaceRefreshElapsed = 0.0;
        float viewportHeight = Mathf.Max(1.0f, GetViewport().GetVisibleRect().Size.Y);
        float cameraFov = _camera?.Fov ?? 80.0f;
        if (Mathf.Abs(viewportHeight - _lastViewportHeight) < 1.0f && Mathf.Abs(cameraFov - _lastCameraFov) < 0.05f) return;
        RebuildRuntimeBands(_currentProfile);
        ConfigureVoxelTransition(_voxelFadeStart, _voxelFadeEnd);
    }

    public void ApplyQualityPreset(QualityPreset preset)
    {
        RingLodQualityProfile? profile = ResolveProfile(preset);
        if (profile == null)
        {
            GD.PushWarning($"LOD quality profile {preset} is not assigned.");
            return;
        }

        CurrentPreset = preset;
        _currentProfile = profile;
        TotalSectorUploadsPerFrame = Mathf.Clamp(profile.SectorUploadsPerFrame, 1, 16);
        MaxConcurrentWorkerBuilds = Mathf.Clamp(profile.ConcurrentWorkers, 1, 32);
        MaxUploadMillisecondsPerFrame = Mathf.Clamp(profile.UploadBudgetMilliseconds, 0.25f, 8.0f);
        if (_levels.Count == 0)
        {
            DefaultPreset = preset;
            return;
        }
        RefreshBackdropSpacing(profile, _mediumGlobalAroundSegments, _mediumGlobalAcrossSegments);
        RebuildRuntimeBands(profile);
        ConfigureVoxelTransition(_voxelFadeStart, _voxelFadeEnd);

        GlobalBackdropPlan backdrop = PlanGlobalBackdrop(
            profile, _mediumGlobalAroundSegments, _mediumGlobalAcrossSegments);
        if (_globalLod != null)
        {
            _globalLod.ApplyRuntimeQuality(
                backdrop.AroundSegments,
                backdrop.AcrossSegments,
                backdrop.Band.X,
                backdrop.Band.Y);
        }
        ApplyFastTravelTierPolicy();
        UpdateTerminalGlobalMorphTarget();
    }

    private readonly record struct GlobalBackdropPlan(
        int ConfiguredCount, Vector2 Band, int AroundSegments, int AcrossSegments);

    /// <summary>Resolves the terminal proxy level, global handoff band and global mesh resolution for a profile.</summary>
    private GlobalBackdropPlan PlanGlobalBackdrop(
        RingLodQualityProfile profile, int mediumAroundSegments, int mediumAcrossSegments)
    {
        int configuredCount = ResolveConfiguredLevelCount();
        Vector2 globalBand = GetRuntimeBand(configuredCount - 1);
        if (EnforceMinimumGlobalHandoffDistance)
        {
            float minimum = Mathf.Max(1000.0f, MinimumGlobalHandoffDistanceMeters);
            globalBand.X = Mathf.Max(globalBand.X, minimum);
            globalBand.Y = Mathf.Max(globalBand.X + 1.0f, globalBand.Y);
        }
        ResolveRingDimensions(out double circumference, out double width);
        (int aroundSegments, int acrossSegments) = ResolveGlobalResolution(
            profile, mediumAroundSegments, mediumAcrossSegments, circumference, width);
        return new GlobalBackdropPlan(configuredCount, globalBand, aroundSegments, acrossSegments);
    }

    private (int Around, int Across) ResolveGlobalResolution(
        RingLodQualityProfile profile,
        int mediumAroundSegments,
        int mediumAcrossSegments,
        double circumferenceMeters,
        double widthMeters)
    {
        // The global backdrop must not be coarser than ~7.7 km x 5 km cells: at
        // lower resolutions its error is far over budget wherever it starts.
        (int floorAround, int floorAcross) = RingTerrainLodChainPlanner.GlobalResolutionFloor(
            circumferenceMeters, widthMeters);
        int aroundSegments = ScaleGlobalResolution(
            mediumAroundSegments,
            profile.GlobalAroundSegments,
            MediumProfile?.GlobalAroundSegments ?? 512,
            floorAround,
            4096);
        int acrossSegments = ScaleGlobalResolution(
            mediumAcrossSegments,
            profile.GlobalAcrossSegments,
            MediumProfile?.GlobalAcrossSegments ?? 32,
            floorAcross,
            256);
        return (aroundSegments, acrossSegments);
    }

    private void ResolveRingDimensions(out double circumferenceMeters, out double widthMeters)
    {
        if (_contractRingCircumference > 0.0)
        {
            circumferenceMeters = _contractRingCircumference;
            widthMeters = _contractRingWidth;
            return;
        }
        circumferenceMeters = _grid?.GetCanonicalRingCircumferenceMeters64() ?? 0.0;
        widthMeters = _grid?.RingAcrossWidthMeters ?? 0.0;
    }

    private double _contractRingCircumference;
    private double _contractRingWidth;

    private void RefreshBackdropSpacing(
        RingLodQualityProfile profile, int mediumAroundSegments, int mediumAcrossSegments)
    {
        ResolveRingDimensions(out double circumference, out double width);
        if (RouteMegaFarLodsToScaledViewport || circumference <= 0.0 || width <= 0.0)
        {
            _backdropEdgeSpacingMeters = 0.0f;
            return;
        }
        (int aroundSegments, int acrossSegments) = ResolveGlobalResolution(
            profile, mediumAroundSegments, mediumAcrossSegments, circumference, width);
        _backdropEdgeSpacingMeters = RingTerrainLodChainPlanner.GlobalEdgeSpacing(
            circumference, width, aroundSegments, acrossSegments);
    }

    /// <summary>
    /// Headless projected-error contract for every desktop quality preset. Requires the
    /// coordinator to be instanced inside the integration scene (levels resolve through
    /// relative node paths); it does not need to be in the scene tree.
    /// </summary>
    public Godot.Collections.Dictionary RunProjectedErrorContract(
        double circumferenceMeters,
        double widthMeters,
        float viewportHeight,
        float fovDegrees,
        int mediumAroundSegments,
        int mediumAcrossSegments)
    {
        if (_levels.Count == 0) RegisterLevels();
        _contractRingCircumference = circumferenceMeters;
        _contractRingWidth = widthMeters;
        var report = new StringBuilder();
        var violationLines = new Godot.Collections.Array<string>();
        var presets = new (QualityPreset Preset, RingLodQualityProfile? Profile)[]
        {
            (QualityPreset.Low, LowProfile),
            (QualityPreset.Medium, MediumProfile),
            (QualityPreset.High, HighProfile),
            (QualityPreset.VeryHigh, VeryHighProfile)
        };
        float[] altitudes = { 500.0f, 2000.0f, 8000.0f, 16000.0f, 23000.0f, 60000.0f };
        float projectionScale = RingTerrainLodChainPlanner.ProjectionScale(viewportHeight, fovDegrees);
        RingLodQualityProfile? previousProfile = _currentProfile;
        report.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"levels={_levels.Count} projectionScale={projectionScale:0.0} ring={circumferenceMeters:0}x{widthMeters:0}"));
        foreach ((QualityPreset preset, RingLodQualityProfile? profile) in presets)
        {
            if (profile == null) continue;
            _currentProfile = profile;
            RefreshBackdropSpacing(profile, mediumAroundSegments, mediumAcrossSegments);
            RebuildRuntimeBands(profile, viewportHeight, fovDegrees);
            GlobalBackdropPlan plan = PlanGlobalBackdrop(profile, mediumAroundSegments, mediumAcrossSegments);
            var tiers = new List<RingTerrainLodChainPlanner.ChainTier>();
            for (int i = 0; i < plan.ConfiguredCount && i < _levels.Count; i++)
            {
                if (!IsPhysicalLevelEnabled(i)) continue;
                tiers.Add(new RingTerrainLodChainPlanner.ChainTier(
                    _levels[i].LodLabel, _levels[i].SampleSpacingMeters, GetRuntimeBand(i).Y));
            }
            float backdropSpacing = RingTerrainLodChainPlanner.GlobalEdgeSpacing(
                circumferenceMeters, widthMeters, plan.AroundSegments, plan.AcrossSegments);
            List<string> violations = RingTerrainLodChainPlanner.EvaluateContract(
                tiers, _voxelFadeEnd, backdropSpacing, projectionScale, profile.TargetPixelError,
                altitudes, out string summary);
            report.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{profile.DisplayName}: e={profile.TargetPixelError:0.00} tiers={tiers.Count} terminal={(tiers.Count > 0 ? tiers[^1].Label : "-")} far={(tiers.Count > 0 ? tiers[^1].FarHandoffMeters : 0.0f):0}m global={plan.AroundSegments}x{plan.AcrossSegments} edge={backdropSpacing:0}m violations={violations.Count}{summary}"));
            foreach (string violation in violations)
                violationLines.Add($"{profile.DisplayName}: {violation}");
        }
        _currentProfile = previousProfile;
        _contractRingCircumference = 0.0;
        _contractRingWidth = 0.0;
        _backdropEdgeSpacingMeters = 0.0f;
        return new Godot.Collections.Dictionary
        {
            ["passed"] = violationLines.Count == 0,
            ["report"] = report.ToString(),
            ["violations"] = violationLines
        };
    }

    private void UpdateTerminalGlobalMorphTarget()
    {
        RingTerrainProxySector? terminal = null;
        for (int i = _levels.Count - 1; i >= 0; i--)
        {
            if (!_levels[i].StreamingActive) continue;
            terminal = _levels[i];
            break;
        }

        if (!ReferenceEquals(terminal, _globalMorphTargetRecipient))
        {
            _globalMorphTargetRecipient?.SetGlobalMorphTarget(null);
            _globalMorphTargetRecipient = terminal;
            _lastGlobalMorphSnapshotIdentity = null;
        }
        if (terminal == null) return;

        if (_globalLod != null && _globalLod.TryCaptureReadyMorphSnapshot(out RingTerrainGlobalMorphSnapshot snapshot))
        {
            if (_lastGlobalMorphSnapshotIdentity == snapshot.SnapshotIdentity) return;
            terminal.SetGlobalMorphTarget(snapshot);
            _lastGlobalMorphSnapshotIdentity = snapshot.SnapshotIdentity;
            return;
        }

        // No complete global receiver is available. Clear the optional source
        // so the proxy retains its normal coarser-tier fallback behavior.
        if (_lastGlobalMorphSnapshotIdentity == null) return;
        terminal.SetGlobalMorphTarget(null);
        _lastGlobalMorphSnapshotIdentity = null;
    }

    public void ApplyUserQualityPreset(QualityPreset preset)
    {
        _manualPresetSelected = true;
        ApplyQualityPreset(preset);
    }

    public void ApplyRingWidth(int ringWidthMeters)
    {
        int width = Mathf.Max(1, ringWidthMeters);
        foreach (RingTerrainProxySector level in _levels)
        {
            level.RingWidthMeters = width;
            level.BuildProxySector(force: true);
        }
    }

    /// <summary>
    /// Updates the local directional-shadow coverage and immediately reapplies
    /// the caster policy when a light changes range.
    /// </summary>
    public void SetTerrainShadowCoverageMeters(float coverageMeters)
    {
        float clamped = Mathf.Clamp(coverageMeters, 100.0f, 50000.0f);
        if (Mathf.IsEqualApprox(TerrainShadowCoverageMeters, clamped)) return;
        TerrainShadowCoverageMeters = clamped;
        ConfigureVoxelTransition(_voxelFadeStart, _voxelFadeEnd);
    }

    /// <summary>Pure caster selection contract for the active proxy chain.</summary>
    public static bool ShouldCastTerrainShadowForLevel(
        bool active,
        int levelIndex,
        int firstUsefulLevel,
        int coverageShadowLevel,
        bool restrictToLocalShadowCoverage,
        bool inspectorDefault)
    {
        if (!active) return false;
        if (!restrictToLocalShadowCoverage) return inspectorDefault;
        return levelIndex >= firstUsefulLevel && levelIndex <= coverageShadowLevel;
    }

    public void ConfigureVoxelTransition(float fadeStart, float fadeEnd)
    {
        _voxelFadeStart = Mathf.Max(0.0f, fadeStart);
        _voxelFadeEnd = Mathf.Max(_voxelFadeStart + 1.0f, fadeEnd);
        if (_levels.Count == 0 || _currentProfile == null) return;

        int configuredCount = ResolveConfiguredLevelCount();
        int firstUsefulLevel = 0;
        while (firstUsefulLevel < configuredCount - 1
            && (!IsPhysicalLevelEnabled(firstUsefulLevel)
                || GetRuntimeBand(firstUsefulLevel).Y <= _voxelFadeEnd))
        {
            firstUsefulLevel++;
        }

        int coverageShadowLevel = firstUsefulLevel;
        for (int i = firstUsefulLevel; i < configuredCount; i++)
        {
            coverageShadowLevel = i;
            if (GetRuntimeBand(i).Y >= TerrainShadowCoverageMeters)
                break;
        }

        EnsureAltitudeStateCapacity();
        RingTerrainProxySector? finerActiveLevel = null;
        int finerActiveLevelIndex = -1;
        bool finerSkippedForAltitude = false;
        for (int i = 0; i < _levels.Count; i++)
        {
            RingTerrainProxySector level = _levels[i];
            RingTerrainProxySector? coarserLevel = FindNextActiveLevel(
                i,
                configuredCount,
                firstUsefulLevel);
            level.SetMorphTargetSurfaceBase(
                coarserLevel?.RadialUnderlayOffsetMeters ?? level.RadialUnderlayOffsetMeters,
                coarserLevel != null && coarserLevel.SampleSpacingMeters is 2 or 4 or 8);
            level.SetMorphTargetSpacingMeters(coarserLevel?.SampleSpacingMeters ?? 0);
            bool inChain = IsPhysicalLevelEnabled(i)
                && i >= firstUsefulLevel
                && i < configuredCount;
            bool active = inChain && IsAltitudeActive(i);
            if (inChain && !active) finerSkippedForAltitude = true;
            // A null finer level is a voxel provider only in the normal
            // ground-level chain.  Once finer levels are released for altitude
            // the first retained level must keep its own guard surface rather
            // than trusting a small, stale voxel window beneath it.
            bool hasVoxelCoverageProvider = active
                && finerActiveLevel == null
                && !finerSkippedForAltitude;
            level.SetCoverageSources(
                active ? finerActiveLevel : null,
                _globalLod,
                hasVoxelCoverageProvider);
            level.SetLodActive(active);
            if (!active)
            {
                level.SetTerrainShadowCasting(false);
                continue;
            }

            Vector2 nearBand = ResolveNearBand(finerActiveLevelIndex);
            Vector2 farBand = GetRuntimeBand(i);
            // Slant reach shrinks to a smaller horizontal reach for a high viewer.
            float reach = _currentProfile.ExpandSectorWindowsToFadeEnd
                ? QuantizedAltitudeReach(i, farBand.Y)
                : float.MaxValue;
            level.SetOuterClipReach(reach);
            int radiusAround = _currentProfile.ExpandSectorWindowsToFadeEnd
                ? ResolveRequiredSectorRadius(level, reach)
                : _inspectorRadiusDefaults[i].X;
            int radiusAcross = _currentProfile.ExpandSectorWindowsToFadeEnd
                ? ResolveRequiredSectorRadius(level, Mathf.Min(reach, level.RingWidthMeters))
                : _inspectorRadiusDefaults[i].Y;
            level.ApplyRuntimeLodConfiguration(
                radiusAround,
                radiusAcross,
                nearBand.X,
                nearBand.Y,
                true,
                farBand.X,
                farBand.Y,
                nearBand.X);
            // Terrain can rely on fine voxel/smooth coverage near the camera;
            // structural cavity closure cannot. Keep exactly the innermost
            // active shell centered, then let coarser shells use their normal
            // near/far handoff bands.
            level.ConfigureStructuralShellHandoff(finerActiveLevel == null);

            bool castShadows = ShouldCastTerrainShadowForLevel(
                active,
                i,
                firstUsefulLevel,
                coverageShadowLevel,
                RestrictShadowsToNearestActiveLevel,
                _inspectorShadowDefaults[i]);
            level.SetTerrainShadowCasting(castShadows);
            finerActiveLevel = level;
            finerActiveLevelIndex = i;
        }
        ApplyFastTravelTierPolicy();
    }

    private RingLodQualityProfile? ResolveProfile(QualityPreset preset) => preset switch
    {
        _ when EnableMegaRegionalExtension && MegaRegionalProfile != null => MegaRegionalProfile,
        QualityPreset.Low => LowProfile,
        QualityPreset.Medium => MediumProfile,
        QualityPreset.High => HighProfile,
        QualityPreset.VeryHigh => VeryHighProfile,
        QualityPreset.UltraVideo => UltraVideoProfile,
        QualityPreset.Cinematic5090 => Cinematic5090Profile,
        _ => MediumProfile
    };

    private void RebuildRuntimeBands(RingLodQualityProfile profile)
    {
        float viewportHeight = Mathf.Max(1.0f, GetViewport().GetVisibleRect().Size.Y);
        float cameraFov = _camera?.Fov ?? 80.0f;
        RebuildRuntimeBands(profile, viewportHeight, cameraFov);
    }

    private void RebuildRuntimeBands(RingLodQualityProfile profile, float viewportHeight, float cameraFov)
    {
        _runtimeBands.Clear();
        _lastViewportHeight = viewportHeight;
        _lastCameraFov = cameraFov;
        float projectionScale = RingTerrainLodChainPlanner.ProjectionScale(viewportHeight, cameraFov);
        _projectionScale = projectionScale;

        for (int i = 0; i < _levels.Count; i++)
        {
            Vector2 authored = GetAuthoredBandForPhysicalLevel(profile, i);
            if (!profile.UseScreenSpaceError)
            {
                _runtimeBands.Add(authored);
                continue;
            }

            _runtimeBands.Add(RingTerrainLodChainPlanner.ScreenSpaceBand(
                _levels[i].SampleSpacingMeters,
                projectionScale,
                profile.TargetPixelError,
                profile.TransitionFraction));
        }
    }

    private Vector2 GetRuntimeBand(int levelIndex)
    {
        if (levelIndex >= 0 && levelIndex < _runtimeBands.Count) return _runtimeBands[levelIndex];
        return _currentProfile != null
            ? GetAuthoredBandForPhysicalLevel(_currentProfile, levelIndex)
            : new Vector2(1000.0f, 1200.0f);
    }

    private Vector2 ResolveNearBand(int finerActiveLevelIndex)
        => finerActiveLevelIndex < 0
            ? new Vector2(_voxelFadeStart, _voxelFadeEnd)
            : GetRuntimeBand(finerActiveLevelIndex);

    private int ResolveConfiguredLevelCount()
    {
        if (_levels.Count == 0 || _currentProfile == null) return 0;
        int legacyCount = Mathf.Clamp(
            _currentProfile.ActiveLevelCount,
            1,
            Mathf.Max(1, _legacyLevelIndices.Count));
        int configuredCount = ResolvePhysicalIndexForLegacyCount(legacyCount);
        bool extendForBackdrop = _backdropEdgeSpacingMeters > 0.0f && _currentProfile.UseScreenSpaceError;
        if (!EnforceMinimumGlobalHandoffDistance && !extendForBackdrop) return configuredCount;

        FillChainScratch();
        if (EnforceMinimumGlobalHandoffDistance)
        {
            float minimum = Mathf.Max(1000.0f, MinimumGlobalHandoffDistanceMeters);
            configuredCount = RingTerrainLodChainPlanner.ExtendCountForMinimumHandoff(
                configuredCount, _farHandoffScratch, _enabledScratch, minimum);
        }
        // The terminal proxy tier is the first whose far handoff lets the global
        // backdrop meet its (5x) pixel-error budget.
        if (extendForBackdrop)
        {
            configuredCount = RingTerrainLodChainPlanner.ExtendCountForBackdropError(
                configuredCount, _farHandoffScratch, _enabledScratch,
                _backdropEdgeSpacingMeters, _projectionScale, _currentProfile.TargetPixelError);
        }
        return configuredCount;
    }

    // Edge spacing of the global backdrop at the current quality; 0 disables
    // backdrop-driven chain extension (Mega, or ring size not known yet).
    private float _backdropEdgeSpacingMeters;
    private float _projectionScale = 1.0f;

    private float[] _farHandoffScratch = Array.Empty<float>();
    private bool[] _enabledScratch = Array.Empty<bool>();

    private void FillChainScratch()
    {
        if (_farHandoffScratch.Length != _levels.Count)
        {
            _farHandoffScratch = new float[_levels.Count];
            _enabledScratch = new bool[_levels.Count];
        }
        for (int i = 0; i < _levels.Count; i++)
        {
            _farHandoffScratch[i] = GetRuntimeBand(i).Y;
            _enabledScratch[i] = IsPhysicalLevelEnabled(i);
        }
    }

    private int ResolvePhysicalIndexForLegacyCount(int legacyCount)
    {
        if (_legacyLevelIndices.Count == 0) return 0;
        int legacyIndex = Mathf.Clamp(legacyCount - 1, 0, _legacyLevelIndices.Count - 1);
        return _legacyLevelIndices[legacyIndex] + 1;
    }

    private bool IsPhysicalLevelEnabled(int physicalIndex)
        => EnableIntermediateLodTiers || !_intermediateLevelIndices.Contains(physicalIndex);

    private RingTerrainProxySector? FindNextActiveLevel(
        int currentIndex,
        int configuredCount,
        int firstUsefulLevel)
    {
        for (int i = currentIndex + 1; i < configuredCount; i++)
        {
            if (IsAltitudeActive(i) && IsPhysicalLevelEnabled(i) && i >= firstUsefulLevel)
                return _levels[i];
        }
        return null;
    }

    private Vector2 GetAuthoredBandForPhysicalLevel(RingLodQualityProfile profile, int physicalIndex)
    {
        int upperLegacyPosition = _legacyLevelIndices.FindIndex(index => index >= physicalIndex);
        if (upperLegacyPosition < 0)
            return profile.GetOutgoingBand(_legacyLevelIndices.Count - 1);
        int upperPhysicalIndex = _legacyLevelIndices[upperLegacyPosition];
        if (upperPhysicalIndex == physicalIndex || upperLegacyPosition == 0)
            return profile.GetOutgoingBand(upperLegacyPosition);

        int lowerLegacyPosition = upperLegacyPosition - 1;
        int lowerPhysicalIndex = _legacyLevelIndices[lowerLegacyPosition];
        float lowerSpacing = Mathf.Max(1.0f, _levels[lowerPhysicalIndex].SampleSpacingMeters);
        float upperSpacing = Mathf.Max(lowerSpacing + 1.0f, _levels[upperPhysicalIndex].SampleSpacingMeters);
        float spacing = Mathf.Clamp(_levels[physicalIndex].SampleSpacingMeters, lowerSpacing, upperSpacing);
        float t = Mathf.Log(spacing / lowerSpacing) / Mathf.Log(upperSpacing / lowerSpacing);
        Vector2 lower = profile.GetOutgoingBand(lowerLegacyPosition);
        Vector2 upper = profile.GetOutgoingBand(upperLegacyPosition);
        return new Vector2(
            Mathf.Lerp(lower.X, upper.X, t),
            Mathf.Lerp(lower.Y, upper.Y, t));
    }

    private void ApplyFastTravelTierPolicy()
    {
        for (int i = 0; i < _levels.Count; i++)
        {
            // Rebuild throttling and rendering are independent.  A velocity
            // threshold must never make authoritative terrain vanish.
            _levels[i].SetFastTravelSuspended(_fastTravelStreamingActive || UndergroundTier != UndergroundProxyTier.None);
            _levels[i].SetTransitVisibility(ResolveTransitHidden(
                _fastTravelStreamingActive,
                HideProxyMeshesDuringFastTravel));
            _levels[i].SetUndergroundHidden(UndergroundTier == UndergroundProxyTier.Hide);
        }
    }

    internal static bool FastTravelVisibilityRegressionTest(out string summary)
    {
        bool defaultRemainsVisible = !ResolveTransitHidden(true, hideProxyMeshesDuringFastTravel: false);
        bool legacyValueRemainsVisible = !ResolveTransitHidden(true, hideProxyMeshesDuringFastTravel: true);
        bool idleRemainsVisible = !ResolveTransitHidden(false, hideProxyMeshesDuringFastTravel: true);
        summary = $"defaultVisible={defaultRemainsVisible}, legacyVisible={legacyValueRemainsVisible}, idleVisible={idleRemainsVisible}";
        return defaultRemainsVisible && legacyValueRemainsVisible && idleRemainsVisible;
    }

    internal static bool IntermediateTierTopologyRegressionTest(out string summary)
    {
        int[] spacings = { 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768 };
        var coordinator = new RingTerrainLodCoordinator
        {
            EnforceMinimumGlobalHandoffDistance = false,
            EnableIntermediateLodTiers = true,
            _currentProfile = new RingLodQualityProfile
            {
                ActiveLevelCount = 9,
                Lod8OutMeters = new Vector2(100.0f, 200.0f),
                Lod9OutMeters = new Vector2(800.0f, 1600.0f)
            }
        };
        try
        {
            for (int i = 0; i < spacings.Length; i++)
            {
                coordinator._levels.Add(new RingTerrainProxySector { SampleSpacingMeters = spacings[i] });
            }
            coordinator._legacyLevelIndices.AddRange(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 10, 12, 14 });
            coordinator._intermediateLevelIndices.UnionWith(new[] { 8, 9, 11, 13 });

            bool doubling = true;
            for (int i = 1; i < spacings.Length; i++)
                doubling &= spacings[i] == spacings[i - 1] * 2;

            int configuredCount = coordinator.ResolveConfiguredLevelCount();
            bool legacyCountMapsToLegacyTier = configuredCount == 11;
            RingTerrainProxySector? enabledParent = coordinator.FindNextActiveLevel(7, configuredCount, 0);
            Vector2 firstBridgeBand = coordinator.GetAuthoredBandForPhysicalLevel(coordinator._currentProfile, 8);
            Vector2 secondBridgeBand = coordinator.GetAuthoredBandForPhysicalLevel(coordinator._currentProfile, 9);
            bool logarithmicBands = Mathf.IsEqualApprox(firstBridgeBand.X, 100.0f + 700.0f / 3.0f)
                && Mathf.IsEqualApprox(secondBridgeBand.X, 100.0f + 1400.0f / 3.0f);
            coordinator.SetIntermediateLodTiersEnabled(false);
            RingTerrainProxySector? disabledParent = coordinator.FindNextActiveLevel(7, configuredCount, 0);
            Vector2 previousActiveBand = coordinator.ResolveNearBand(7);
            bool nextParentIsImmediate = enabledParent?.SampleSpacingMeters == 512
                && disabledParent?.SampleSpacingMeters == 2048;
            bool disabledBridgeKeepsPreviousActiveBand = previousActiveBand == coordinator._currentProfile.Lod8OutMeters;
            bool scaledViewportDoesNotAddProxyBridges = !ShouldBuildAbsoluteWorldMegaProxyTiers(
                    megaRegionalExtensionEnabled: true,
                    routeMegaFarLodsToScaledViewport: true)
                && ShouldBuildAbsoluteWorldMegaProxyTiers(
                    megaRegionalExtensionEnabled: false,
                    routeMegaFarLodsToScaledViewport: false);
            summary = $"spacings={spacings.Length}, legacyCount={configuredCount}, immediateParent={nextParentIsImmediate}, previousActiveBand={disabledBridgeKeepsPreviousActiveBand}, logarithmicBands={logarithmicBands}, scaledViewportExclusive={scaledViewportDoesNotAddProxyBridges}";
            return doubling && legacyCountMapsToLegacyTier && nextParentIsImmediate
                && disabledBridgeKeepsPreviousActiveBand && logarithmicBands && scaledViewportDoesNotAddProxyBridges;
        }
        finally
        {
            foreach (RingTerrainProxySector level in coordinator._levels) level.Free();
            coordinator.Free();
        }
    }

    public static string RunIntermediateTierTopologyRegressionTest()
    {
        if (!IntermediateTierTopologyRegressionTest(out string summary))
            throw new InvalidOperationException($"Intermediate LOD topology regression failed: {summary}");
        return summary;
    }

    private static bool ResolveTransitHidden(bool fastTravelActive, bool hideProxyMeshesDuringFastTravel)
        => false;

    /// <summary>
    /// The 512m-16km bridge tiers and LOD9-11 are absolute-world proxy sectors on
    /// every world except Mega, where the scaled viewport owns those ranges.
    /// </summary>
    internal static bool ShouldBuildAbsoluteWorldMegaProxyTiers(
        bool megaRegionalExtensionEnabled,
        bool routeMegaFarLodsToScaledViewport)
        => !routeMegaFarLodsToScaledViewport;

    private void ValidatePhaseCConfiguration()
    {
        if (_currentProfile == null || _runtimeBands.Count != _levels.Count)
        {
            GD.PushError("Phase C LOD self-test failed: runtime band count does not match the LOD stack.");
            return;
        }
        float previousEnd = 0.0f;
        for (int i = 0; i < _runtimeBands.Count; i++)
        {
            Vector2 band = _runtimeBands[i];
            if (!band.IsFinite() || band.X < 0.0f || band.Y <= band.X || band.Y <= previousEnd)
            {
                GD.PushError($"Phase C LOD self-test failed: invalid band {i + 1} = {band}.");
                return;
            }
            previousEnd = band.Y;
        }
        if (MaxUploadMillisecondsPerFrame <= 0.0f || MaxConcurrentWorkerBuilds <= 0)
        {
            GD.PushError("Phase C LOD self-test failed: shared budgets are not positive.");
            return;
        }
        GD.Print($"Phase C LOD self-test passed: levels={ResolveConfiguredLevelCount()}, pixelError={_currentProfile.TargetPixelError:0.00}, uploadBudget={MaxUploadMillisecondsPerFrame:0.00}ms");
    }

    private static int ResolveRequiredSectorRadius(RingTerrainProxySector level, float distance)
    {
        int size = Mathf.Max(1, level.SectorSizeMeters);
        return Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(0.0f, distance) / size), 1, 32);
    }

    private static int ScaleGlobalResolution(int mediumBaseline, int requested, int mediumReference, int minimum, int maximum)
    {
        float scale = requested / (float)Mathf.Max(1, mediumReference);
        return Mathf.Clamp(Mathf.RoundToInt(mediumBaseline * scale), minimum, maximum);
    }

    private void AddLevel(NodePath path, bool legacyLevel, bool intermediateLevel = false)
    {
        RingTerrainProxySector? level = GetNodeOrNull<RingTerrainProxySector>(path);
        if (level == null)
        {
            GD.PushWarning($"LOD coordinator could not resolve {path}.");
            return;
        }
        level.SetCoordinatorManaged(true);
        _levels.Add(level);
        if (legacyLevel) _legacyLevelIndices.Add(_levels.Count - 1);
        if (intermediateLevel) _intermediateLevelIndices.Add(_levels.Count - 1);
        _inspectorShadowDefaults.Add(level.CastTerrainShadows);
        _inspectorRadiusDefaults.Add(new Vector2I(level.SectorRadiusAround, level.SectorRadiusAcross));
    }

    internal readonly record struct BuildStartCandidate(int LevelIndex, ulong RequestedFrame);

    /// <summary>
    /// Orders pending build starts: levels that have waited at least
    /// <paramref name="starvationFrames"/> go first (oldest first), then the rest
    /// from finest (index 0) to coarsest. Allocation-free insertion sort.
    /// </summary>
    internal static void OrderBuildStartCandidates(Span<BuildStartCandidate> candidates, ulong frame, int starvationFrames)
    {
        ulong threshold = (ulong)Math.Max(1, starvationFrames);
        for (int i = 1; i < candidates.Length; i++)
        {
            BuildStartCandidate value = candidates[i];
            int j = i - 1;
            while (j >= 0 && BuildStartComesBefore(value, candidates[j], frame, threshold))
            {
                candidates[j + 1] = candidates[j];
                j--;
            }
            candidates[j + 1] = value;
        }
    }

    private static bool BuildStartComesBefore(BuildStartCandidate a, BuildStartCandidate b, ulong frame, ulong threshold)
    {
        ulong ageA = frame > a.RequestedFrame ? frame - a.RequestedFrame : 0UL;
        ulong ageB = frame > b.RequestedFrame ? frame - b.RequestedFrame : 0UL;
        bool starvedA = ageA >= threshold;
        bool starvedB = ageB >= threshold;
        if (starvedA != starvedB) return starvedA;
        if (starvedA && ageA != ageB) return ageA > ageB;
        return a.LevelIndex < b.LevelIndex;
    }

    internal static int PlanBuildStartSlots(int available, int planned, int started)
        => Math.Max(1, available / Math.Max(1, planned - started));

    private void StartPendingWorkers()
    {
        LastFrameBuildStarts = 0;
        LastFrameBuildStartMilliseconds = 0.0;
        UpdateMeshMemoryPressureState();
        long stagingBudgetBytes = Math.Max(32, MaximumStagedMeshMemoryMiB) * 1024L * 1024L;
        // Per-LOD queues are individually bounded by RingTerrainProxySector.
        // Do not feed new producer work while their combined staged output has
        // consumed the global allowance; uploads drain it on following frames.
        if (EnableMeshMemoryPressureThrottle
            && EstimatedPendingMeshBytes >= stagingBudgetBytes)
        {
            return;
        }
        int workerLimit = MeshMemoryPressureActive
            ? Mathf.Min(MaxConcurrentWorkerBuilds, Mathf.Max(1, MemoryPressureWorkerLimit))
            : MaxConcurrentWorkerBuilds;
        int available = Mathf.Max(0, workerLimit - ActiveWorkerCount);
        if (available == 0 || _levels.Count == 0) return;
        if (_buildStartCandidates.Length < _levels.Count)
        {
            _buildStartCandidates = new BuildStartCandidate[_levels.Count];
        }
        int count = 0;
        for (int i = 0; i < _levels.Count; i++)
        {
            RingTerrainProxySector level = _levels[i];
            if (!level.StreamingActive || !level.CoordinatorBuildRequested || !level.CanStartCoordinatorBuild) continue;
            _buildStartCandidates[count++] = new BuildStartCandidate(i, level.CoordinatorBuildRequestedFrame);
        }
        if (count == 0) return;
        ulong frame = Engine.GetProcessFrames();
        var candidates = new Span<BuildStartCandidate>(_buildStartCandidates, 0, count);
        OrderBuildStartCandidates(candidates, frame, BuildStartStarvationFrames);
        int planned = Mathf.Min(available, count);
        int limit = Mathf.Min(planned, Mathf.Max(1, MaxBuildStartsPerFrame));
        double budgetMs = Mathf.Max(0.25f, MaxBuildStartMillisecondsPerFrame);
        ulong startUsec = Time.GetTicksUsec();
        int started = 0;
        int attempts = 0;
        for (int i = 0; i < count && started < limit && available > 0; i++)
        {
            double elapsedMs = (Time.GetTicksUsec() - startUsec) / 1000.0;
            if (attempts > 0)
            {
                // The first start of a frame is never blocked; later ones wait
                // when their predicted cost would exceed the frame budget.
                double predicted = Math.Max(elapsedMs / attempts, _avgBuildStartMilliseconds);
                if (elapsedMs + predicted > budgetMs)
                {
                    DeferredBuildStartCount++;
                    break;
                }
            }
            RingTerrainProxySector level = _levels[candidates[i].LevelIndex];
            int slots = PlanBuildStartSlots(available, planned, started);
            attempts++;
            ulong levelStartUsec = Time.GetTicksUsec();
            bool didStart = level.StartCoordinatorBuildIfRequested(slots);
            double levelMs = (Time.GetTicksUsec() - levelStartUsec) / 1000.0;
            _avgBuildStartMilliseconds = _avgBuildStartMilliseconds <= 0.0
                ? levelMs
                : _avgBuildStartMilliseconds * 0.8 + levelMs * 0.2;
            if (didStart)
            {
                started++;
                available -= slots;
            }
        }
        LastFrameBuildStarts = started;
        LastFrameBuildStartMilliseconds = (Time.GetTicksUsec() - startUsec) / 1000.0;
    }

    /// <summary>Verifies per-level altitude activation hysteresis and window trimming without a scene.</summary>
    public static string RunAltitudeActivationContract()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException($"Altitude activation contract failed: {message}");
        }

        const float far = 10000.0f;
        // Descent: inactive until within 1.25x, then stays active up to 1.40x.
        Require(!RingTerrainLodChainPlanner.IsLevelActiveAtAltitude(far, 13000.0f, false), "must not activate at 1.3x");
        Require(RingTerrainLodChainPlanner.IsLevelActiveAtAltitude(far, 12000.0f, false), "must pre-warm below 1.25x");
        Require(RingTerrainLodChainPlanner.IsLevelActiveAtAltitude(far, 13500.0f, true), "must stay active below 1.40x");
        Require(!RingTerrainLodChainPlanner.IsLevelActiveAtAltitude(far, 14500.0f, true), "must release above 1.40x");
        // The band between the thresholds keeps whatever state it had (no flicker).
        bool state = false;
        int flips = 0;
        for (float altitude = 14000.0f; altitude >= 11000.0f; altitude -= 50.0f)
        {
            bool next = RingTerrainLodChainPlanner.IsLevelActiveAtAltitude(far, altitude, state);
            if (next != state) flips++;
            state = next;
        }
        for (float altitude = 11000.0f; altitude <= 14500.0f; altitude += 50.0f)
        {
            bool next = RingTerrainLodChainPlanner.IsLevelActiveAtAltitude(far, altitude, state);
            if (next != state) flips++;
            state = next;
        }
        Require(flips == 2, $"expected exactly one activation and one release, saw {flips} flips");
        // Window trim: untouched below the terrain ceiling, sqrt(f^2 - c^2) above it.
        Require(Mathf.IsEqualApprox(RingTerrainLodChainPlanner.TrimmedHorizontalReach(far, 4000.0f), far), "no trim below ceiling");
        float trimmed = RingTerrainLodChainPlanner.TrimmedHorizontalReach(far, RingTerrainLodChainPlanner.TerrainCeilingMeters + 6000.0f);
        Require(Mathf.IsEqualApprox(trimmed, 8000.0f), $"expected 8000 reach, got {trimmed}");
        Require(RingTerrainLodChainPlanner.TrimmedHorizontalReach(far, 40000.0f) == 0.0f, "fully clear level has no reach");
        return $"hysteresis=ok flips={flips}, trim=ok";
    }

    /// <summary>Verifies fair build-start ordering and slot planning without a scene.</summary>
    public static string RunBuildStartSchedulingContract()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException($"Build start scheduling contract failed: {message}");
        }

        // Finest first when nothing is starved.
        var fresh = new[]
        {
            new BuildStartCandidate(3, 100), new BuildStartCandidate(0, 100), new BuildStartCandidate(1, 100)
        };
        OrderBuildStartCandidates(fresh, 101, 30);
        Require(fresh[0].LevelIndex == 0 && fresh[1].LevelIndex == 1 && fresh[2].LevelIndex == 3,
            "fresh candidates must order finest first");

        // A starved coarse level beats a fresh fine level.
        var starved = new[] { new BuildStartCandidate(0, 190), new BuildStartCandidate(5, 100) };
        OrderBuildStartCandidates(starved, 200, 30);
        Require(starved[0].LevelIndex == 5, "starved coarse level must precede fresh fine level");

        // Two starved levels order by age, oldest first.
        var twoStarved = new[] { new BuildStartCandidate(1, 150), new BuildStartCandidate(6, 120), new BuildStartCandidate(0, 195) };
        OrderBuildStartCandidates(twoStarved, 200, 30);
        Require(twoStarved[0].LevelIndex == 6 && twoStarved[1].LevelIndex == 1 && twoStarved[2].LevelIndex == 0,
            "starved levels must order oldest first before fresh levels");

        // Fine level re-requests every frame with a budget of one start per frame.
        const int threshold = 30;
        ulong fineRequested = 0;
        ulong coarseRequested = 0;
        int coarseStartFrame = -1;
        var sim = new BuildStartCandidate[2];
        for (int frame = 1; frame <= 40 && coarseStartFrame < 0; frame++)
        {
            sim[0] = new BuildStartCandidate(0, fineRequested);
            sim[1] = new BuildStartCandidate(4, coarseRequested);
            OrderBuildStartCandidates(sim, (ulong)frame, threshold);
            if (sim[0].LevelIndex == 4) coarseStartFrame = frame;
            else fineRequested = (ulong)frame;
        }
        Require(coarseStartFrame > 0 && coarseStartFrame <= threshold + 1,
            $"coarse level starved past the threshold (started at frame {coarseStartFrame})");

        Require(PlanBuildStartSlots(3, 3, 0) == 1, "3 workers across 3 starts must plan 1 slot");
        Require(PlanBuildStartSlots(3, 1, 0) == 3, "3 workers for 1 start must plan 3 slots");
        Require(PlanBuildStartSlots(0, 1, 0) == 1, "slot plan must never be below one");
        return $"fresh=ok, starved=ok, age=ok, simStart={coarseStartFrame}, slots=ok";
    }

    private void UpdateMeshMemoryPressureState()
    {
        if (!EnableMeshMemoryPressureThrottle)
        {
            if (MeshMemoryPressureActive)
            {
                MeshMemoryPressureActive = false;
                _grid?.SetExternalMeshMemoryPressure(false);
            }
            return;
        }

        long budgetBytes = Math.Max(32, MaximumStagedMeshMemoryMiB) * 1024L * 1024L;
        long recoveryBytes = budgetBytes
            * Mathf.Clamp(StagedMeshRecoveryPercent, 25, 95) / 100L;
        long stagedBytes = EstimatedPendingMeshBytes;
        bool next = MeshMemoryPressureActive
            ? stagedBytes > recoveryBytes
            : stagedBytes >= budgetBytes;
        if (next == MeshMemoryPressureActive) return;

        MeshMemoryPressureActive = next;
        _grid?.SetExternalMeshMemoryPressure(next);
        GD.Print(next
            ? $"LOD mesh memory pressure enabled: staged={stagedBytes / (1024.0 * 1024.0):0.0}MiB, budget={MaximumStagedMeshMemoryMiB}MiB."
            : $"LOD mesh memory pressure cleared: staged={stagedBytes / (1024.0 * 1024.0):0.0}MiB.");
    }
}
