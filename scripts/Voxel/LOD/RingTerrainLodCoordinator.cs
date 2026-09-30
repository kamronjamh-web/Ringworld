using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

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
    private int _workerRoundRobinStart;
    private float _lastViewportHeight;
    private float _lastCameraFov;
    private double _screenSpaceRefreshElapsed;
    private bool _manualPresetSelected;
    private bool _fastTravelStreamingActive;
    private double _fastTravelStoppedElapsedSeconds;
    private bool _highAltitudeProxySuspended;
    private long _lastAltitudeAroundCell = long.MinValue;
    private int _lastAltitudeAcrossCell = int.MinValue;
    private ulong _lastAltitudeEditVersion = ulong.MaxValue;
    private float _cachedAltitudeSurfaceHeight;
    public bool MeshMemoryPressureActive { get; private set; }

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
    public bool HighAltitudeProxySuspended => _highAltitudeProxySuspended;
    public float CurrentViewerAltitudeAboveTerrainMeters { get; private set; }

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
        UpdateFastTravelStreamingState(delta);
        UpdateHighAltitudeProxySuspension();
        UpdateTerminalGlobalMorphTarget();
        RefreshScreenSpaceSelectionIfNeeded(delta);
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

    private void UpdateHighAltitudeProxySuspension()
    {
        if (_explorer == null) return;
        CurrentViewerAltitudeAboveTerrainMeters = ResolveViewerAltitudeAboveTerrain(
            _explorer.CanonicalPosition);
        bool next = _highAltitudeProxySuspended;
        if (!SuspendRegionalProxiesAtHighAltitude)
        {
            next = false;
        }
        else
        {
            float suspendAltitude = Mathf.Max(
                1000.0f,
                SuspendRegionalProxiesAboveAltitudeMeters);
            float resumeAltitude = Mathf.Clamp(
                ResumeRegionalProxiesBelowAltitudeMeters,
                0.0f,
                suspendAltitude - 1.0f);
            if (_highAltitudeProxySuspended)
                next = CurrentViewerAltitudeAboveTerrainMeters > resumeAltitude;
            else
                next = CurrentViewerAltitudeAboveTerrainMeters >= suspendAltitude;
        }

        if (next == _highAltitudeProxySuspended) return;
        _highAltitudeProxySuspended = next;
        ConfigureVoxelTransition(_voxelFadeStart, _voxelFadeEnd);
        GD.Print(next
            ? KeepCoarseProxiesStreamingAtHighAltitude
                ? $"High-altitude fine terrain/water proxies suspended at {CurrentViewerAltitudeAboveTerrainMeters:0}m; coarse regional guard tiers remain active."
                : $"High-altitude regional terrain/water proxies suspended at {CurrentViewerAltitudeAboveTerrainMeters:0}m; global LOD owns the surface."
            : $"Regional terrain/water proxies resumed at {CurrentViewerAltitudeAboveTerrainMeters:0}m for descent prewarming.");
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
        RebuildRuntimeBands(profile);
        ConfigureVoxelTransition(_voxelFadeStart, _voxelFadeEnd);

        int configuredCount = ResolveConfiguredLevelCount();
        Vector2 globalBand = GetRuntimeBand(configuredCount - 1);
        if (EnforceMinimumGlobalHandoffDistance)
        {
            float minimum = Mathf.Max(1000.0f, MinimumGlobalHandoffDistanceMeters);
            globalBand.X = Mathf.Max(globalBand.X, minimum);
            globalBand.Y = Mathf.Max(globalBand.X + 1.0f, globalBand.Y);
        }
        if (_globalLod != null)
        {
            int aroundSegments = ScaleGlobalResolution(
                _mediumGlobalAroundSegments,
                profile.GlobalAroundSegments,
                MediumProfile?.GlobalAroundSegments ?? 512,
                64,
                4096);
            int acrossSegments = ScaleGlobalResolution(
                _mediumGlobalAcrossSegments,
                profile.GlobalAcrossSegments,
                MediumProfile?.GlobalAcrossSegments ?? 32,
                4,
                256);
            _globalLod.ApplyRuntimeQuality(
                aroundSegments,
                acrossSegments,
                globalBand.X,
                globalBand.Y);
        }
        ApplyFastTravelTierPolicy();
        UpdateTerminalGlobalMorphTarget();
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

        int firstHighAltitudeContinuous = ResolveFirstContinuousLevelIndex(
            configuredCount,
            FirstHighAltitudeContinuousLod);
        RingTerrainProxySector? finerActiveLevel = null;
        int finerActiveLevelIndex = -1;
        for (int i = 0; i < _levels.Count; i++)
        {
            RingTerrainProxySector level = _levels[i];
            RingTerrainProxySector? coarserLevel = FindNextActiveLevel(
                i,
                configuredCount,
                firstUsefulLevel,
                firstHighAltitudeContinuous);
            level.SetMorphTargetSurfaceBase(
                coarserLevel?.RadialUnderlayOffsetMeters ?? level.RadialUnderlayOffsetMeters,
                coarserLevel != null && coarserLevel.SampleSpacingMeters is 2 or 4 or 8);
            level.SetMorphTargetSpacingMeters(coarserLevel?.SampleSpacingMeters ?? 0);
            bool allowedAtAltitude = !_highAltitudeProxySuspended
                || (KeepCoarseProxiesStreamingAtHighAltitude
                    && i >= firstHighAltitudeContinuous);
            bool active = allowedAtAltitude
                && IsPhysicalLevelEnabled(i)
                && i >= firstUsefulLevel
                && i < configuredCount;
            // A null finer level is a voxel provider only in the normal
            // ground-level chain.  During high-altitude suspension the first
            // retained coarse level must keep its own guard surface rather than
            // trusting a small, stale voxel window beneath it.
            bool hasVoxelCoverageProvider = active
                && finerActiveLevel == null
                && !_highAltitudeProxySuspended;
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
            int radiusAround = _currentProfile.ExpandSectorWindowsToFadeEnd
                ? ResolveRequiredSectorRadius(level, farBand.Y)
                : _inspectorRadiusDefaults[i].X;
            int radiusAcross = _currentProfile.ExpandSectorWindowsToFadeEnd
                ? ResolveRequiredSectorRadius(level, Mathf.Min(farBand.Y, level.RingWidthMeters))
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
        _runtimeBands.Clear();
        float viewportHeight = Mathf.Max(1.0f, GetViewport().GetVisibleRect().Size.Y);
        float cameraFov = _camera?.Fov ?? 80.0f;
        _lastViewportHeight = viewportHeight;
        _lastCameraFov = cameraFov;
        float fovRadians = Mathf.DegToRad(cameraFov);
        float projectionScale = viewportHeight / (2.0f * Mathf.Tan(fovRadians * 0.5f));
        float pixelError = Mathf.Max(0.5f, profile.TargetPixelError);
        float transitionFraction = Mathf.Clamp(profile.TransitionFraction, 0.05f, 0.4f);

        for (int i = 0; i < _levels.Count; i++)
        {
            Vector2 authored = GetAuthoredBandForPhysicalLevel(profile, i);
            if (!profile.UseScreenSpaceError)
            {
                _runtimeBands.Add(authored);
                continue;
            }

            float geometricError = Mathf.Max(1.0f, _levels[i].SampleSpacingMeters);
            float fadeEnd = Mathf.Max(geometricError * 4.0f, geometricError * projectionScale / pixelError);
            float fadeStart = fadeEnd * (1.0f - transitionFraction);
            _runtimeBands.Add(new Vector2(fadeStart, fadeEnd));
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
        if (!EnforceMinimumGlobalHandoffDistance) return configuredCount;

        float minimum = Mathf.Max(1000.0f, MinimumGlobalHandoffDistanceMeters);
        while (configuredCount < _levels.Count
            && GetRuntimeBand(FindLastEnabledLevelIndex(configuredCount)).Y < minimum)
        {
            configuredCount++;
        }
        return configuredCount;
    }

    private int ResolvePhysicalIndexForLegacyCount(int legacyCount)
    {
        if (_legacyLevelIndices.Count == 0) return 0;
        int legacyIndex = Mathf.Clamp(legacyCount - 1, 0, _legacyLevelIndices.Count - 1);
        return _legacyLevelIndices[legacyIndex] + 1;
    }

    private bool IsPhysicalLevelEnabled(int physicalIndex)
        => EnableIntermediateLodTiers || !_intermediateLevelIndices.Contains(physicalIndex);

    private int FindLastEnabledLevelIndex(int exclusiveCount)
    {
        for (int i = Mathf.Min(exclusiveCount, _levels.Count) - 1; i >= 0; i--)
        {
            if (IsPhysicalLevelEnabled(i)) return i;
        }
        return 0;
    }

    private RingTerrainProxySector? FindNextActiveLevel(
        int currentIndex,
        int configuredCount,
        int firstUsefulLevel,
        int firstHighAltitudeContinuous)
    {
        for (int i = currentIndex + 1; i < configuredCount; i++)
        {
            bool allowedAtAltitude = !_highAltitudeProxySuspended
                || (KeepCoarseProxiesStreamingAtHighAltitude && i >= firstHighAltitudeContinuous);
            if (allowedAtAltitude && IsPhysicalLevelEnabled(i) && i >= firstUsefulLevel)
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

    private int ResolveFirstContinuousLevelIndex(int configuredCount, int requestedLod)
    {
        int lastConfiguredIndex = Mathf.Max(0, configuredCount - 1);
        int requestedPhysicalIndex = ResolvePhysicalIndexForLegacyCount(requestedLod) - 1;
        return Mathf.Clamp(requestedPhysicalIndex, 0, lastConfiguredIndex);
    }

    private void ApplyFastTravelTierPolicy()
    {
        for (int i = 0; i < _levels.Count; i++)
        {
            // Rebuild throttling and rendering are independent.  A velocity
            // threshold must never make authoritative terrain vanish.
            _levels[i].SetFastTravelSuspended(_fastTravelStreamingActive);
            _levels[i].SetTransitVisibility(ResolveTransitHidden(
                _fastTravelStreamingActive,
                HideProxyMeshesDuringFastTravel));
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
            RingTerrainProxySector? enabledParent = coordinator.FindNextActiveLevel(7, configuredCount, 0, 0);
            Vector2 firstBridgeBand = coordinator.GetAuthoredBandForPhysicalLevel(coordinator._currentProfile, 8);
            Vector2 secondBridgeBand = coordinator.GetAuthoredBandForPhysicalLevel(coordinator._currentProfile, 9);
            bool logarithmicBands = Mathf.IsEqualApprox(firstBridgeBand.X, 100.0f + 700.0f / 3.0f)
                && Mathf.IsEqualApprox(secondBridgeBand.X, 100.0f + 1400.0f / 3.0f);
            coordinator.SetIntermediateLodTiersEnabled(false);
            RingTerrainProxySector? disabledParent = coordinator.FindNextActiveLevel(7, configuredCount, 0, 0);
            Vector2 previousActiveBand = coordinator.ResolveNearBand(7);
            bool nextParentIsImmediate = enabledParent?.SampleSpacingMeters == 512
                && disabledParent?.SampleSpacingMeters == 2048;
            bool disabledBridgeKeepsPreviousActiveBand = previousActiveBand == coordinator._currentProfile.Lod8OutMeters;
            bool scaledViewportDoesNotAddProxyBridges = !ShouldBuildAbsoluteWorldMegaProxyTiers(
                megaRegionalExtensionEnabled: true,
                routeMegaFarLodsToScaledViewport: true);
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

    internal static bool ShouldBuildAbsoluteWorldMegaProxyTiers(
        bool megaRegionalExtensionEnabled,
        bool routeMegaFarLodsToScaledViewport)
        => megaRegionalExtensionEnabled && !routeMegaFarLodsToScaledViewport;

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

    private void StartPendingWorkers()
    {
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
        int start = _workerRoundRobinStart % _levels.Count;
        var requesting = new List<RingTerrainProxySector>();
        for (int offset = 0; offset < _levels.Count; offset++)
        {
            RingTerrainProxySector level = _levels[(start + offset) % _levels.Count];
            if (!level.StreamingActive || !level.CoordinatorBuildRequested) continue;
            requesting.Add(level);
        }
        int startCount = Mathf.Min(available, requesting.Count);
        for (int i = 0; i < startCount; i++)
        {
            int remainingRings = startCount - i;
            int slots = Mathf.Max(1, available / remainingRings);
            requesting[i].StartCoordinatorBuildIfRequested(slots);
            available -= slots;
        }
        _workerRoundRobinStart = (start + 1) % _levels.Count;
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
