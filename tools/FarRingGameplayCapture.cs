using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using Godot;
using RingworldTechDemo;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Tools;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

/// <summary>Single real gameplay capture; all writes are isolated below .godot.</summary>
public partial class FarRingGameplayCapture : RefCounted
{
    public string Status { get; private set; } = "IDLE";
    public string BundleDirectory { get; private set; } = "";
    public string ErrorText { get; private set; } = "";
    private readonly List<object> _preferences = new();
    private string _sourceWorld = "";
    private string _captureDirectory = "", _captureCase = "baseline", _phaseRole = "single-run";
    private bool _clocksFrozen;
    private double _poseAltitudeOffset;
    public float ComputedAltitudeAboveTerrainMeters { get; private set; }
    public float CachedAltitudeAboveTerrainMeters { get; private set; }
    private static readonly PropertyInfo CaptureAltitudeProperty = typeof(RingVoxelExplorerController)
        .GetProperty(nameof(RingVoxelExplorerController.CurrentAltitudeAboveTerrainMeters))
        ?? throw new InvalidOperationException("Explorer altitude property unavailable.");

    // The capture holds physics stationary. Refresh only the cached altitude that
    // normal physics updates; do not invoke camera/horizon smoothing or gameplay.
    private void RefreshCaptureAltitude(RingVoxelExplorerController explorer)
    {
        float computed = explorer.GetCanonicalAtmosphereHeightMeters();
        if (!float.IsFinite(computed)) throw new InvalidOperationException("Non-finite canonical terrain altitude.");
        CaptureAltitudeProperty.GetSetMethod(nonPublic: true)!
            .Invoke(explorer, new object[] { computed });
        ComputedAltitudeAboveTerrainMeters = computed;
        CachedAltitudeAboveTerrainMeters = explorer.CurrentAltitudeAboveTerrainMeters;
        if (CachedAltitudeAboveTerrainMeters != computed)
            throw new InvalidOperationException("Capture altitude cache refresh did not take effect.");
    }
    public string AblationCases { get; private set; } = "baseline";
    public double SettleSeconds { get; private set; } = 1;
    public double WarmupSeconds { get; private set; } = 30;
    public double CollectionSeconds { get; private set; } = 10;
    public string ArtifactLabel { get; private set; } = "current-settings";
    private bool _detailedGpuRequested;
    private ulong _collectionStartedUsec, _warmupStartedUsec;
    private double _actualWarmupSeconds;
    private bool _warmupRecorded;
    public bool ConfigureArguments()
    {
        try
        {
            string[] arguments = OS.GetCmdlineUserArgs();
            for (int i = 0; i < arguments.Length; i++)
            {
                string key = arguments[i];
                if (key == "--detailed-gpu") { _detailedGpuRequested = true; continue; }
                if (key is not ("--artifact-label" or "--warmup-seconds" or "--collection-seconds" or "--cases" or "--settle-seconds")) continue;
                if (++i >= arguments.Length) throw new ArgumentException("Missing value for " + key);
                string value = arguments[i];
                if (key == "--cases")
                {
                    foreach (string name in value.Split(',')) if (name is not ("baseline" or "vsync-off" or "gpu-markers-off" or "outer-render-off" or "far-process-off" or "grass-process-off" or "trees-process-off" or "sunshine-off" or "cloud-shadow-off" or "terrain-cloud-shadow-off" or "grass-render-off" or "trees-render-off")) throw new ArgumentException("Unsupported ablation case: " + name);
                    AblationCases = value;
                }
                else if (key == "--artifact-label")
                {
                    if (!Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")) throw new ArgumentException("Artifact label requires 1?64 letters, numbers, underscores or hyphens.");
                    ArtifactLabel = value;
                }
                else
                {
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || !double.IsFinite(seconds)) throw new ArgumentException("Invalid seconds for " + key);
                    if (key == "--settle-seconds") { if (seconds < 1 || seconds > 10) throw new ArgumentOutOfRangeException(key); SettleSeconds = seconds; }
                    else if (key == "--warmup-seconds") { if (seconds < 0 || seconds > 90) throw new ArgumentOutOfRangeException(key); WarmupSeconds = seconds; }
                    else { if (seconds < 1 || seconds > 60) throw new ArgumentOutOfRangeException(key); CollectionSeconds = seconds; }
                }
            }
            return true;
        }
        catch (Exception error) { ErrorText = error.ToString(); Status = "FAILED"; return false; }
    }
    public bool Prepare(Node world, string directory)
    {
        try { PrepareCore(world, directory); return true; }
        catch (Exception error) { ErrorText = error.ToString(); Status = "FAILED"; return false; }
    }
    private void PrepareCore(Node world, string directory)
    {
        if (!directory.StartsWith("res://.godot/far-ring-gameplay/", StringComparison.Ordinal) || directory.Contains(".."))
            throw new ArgumentException("Capture directory must be isolated below .godot/far-ring-gameplay.");
        Directory.CreateDirectory(ProjectSettings.GlobalizePath(directory));
        _captureDirectory = directory;
        _sourceWorld = ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString();
        var paths = new Dictionary<string, string>
        {
            ["aa"] = GameplayAntiAliasingDefaults.UserPath, ["motion"] = GameplayMotionDefaults.UserPath,
            ["wind"] = GameplayWindDefaults.UserPath, ["rain-haze"] = GameplayRainHazeDefaults.UserPath,
            ["grass"] = GameplayGrassDefaults.UserPath, ["legacy-grass"] = GameplayGrassDefaults.UserPath,
            ["vegetation"] = GameplayVegetationDefaults.UserPath, ["shadows"] = GameplayShadowDefaults.UserPath,
            ["surface"] = GameplaySurfaceTerrainDefaults.UserPath, ["atmosphere"] = AtmosphereVisualDefaults.UserPath,
            ["resolution"] = GameplayRenderResolutionDefaults.UserPath, ["lod"] = GameplayLodTransitionDefaults.UserPath,
            ["coverage"] = GameplayProxyCoverageCullingDefaults.UserPath, ["telemetry"] = RingPerformanceMonitor.UserPath
        };
        foreach (var item in paths) CopyPreference(item.Key, item.Value, directory);
        var water = new GameplayWaterDefaults(); water.Load();
        SavePreference("water", water.Settings, directory, "factory + current user water overrides");
        var clouds = new GameplayCloudDefaults(); clouds.Load();
        SavePreference("clouds", clouds.Settings, directory, OS.HasFeature("template") ? "factory + current user cloud overrides" : "factory cloud overrides (editor runtime policy)");
        // Apply the source report's High baseline before Ready. Sparse copied
        // preferences then apply through the normal gameplay startup ordering.
        world.GetNode<RingGraphicsQualityController>("GraphicsQuality").DefaultPreset = RingTerrainLodCoordinator.QualityPreset.High;
        world.GetNode<RingTerrainLodCoordinator>("TerrainLodCoordinator").DefaultPreset = RingTerrainLodCoordinator.QualityPreset.High;
        var grid = world.GetNode<VoxelChunkGridManager>("ChunkGridManager");
        if (Math.Abs(grid.RingRadiusMeters - 5000000f) > 10f || Math.Abs(grid.RingAcrossWidthMeters - 318000f) > 1f || !grid.UseTerrain3DSurface)
            throw new InvalidOperationException("Certified launcher configuration was not installed before capture startup.");
        new MinimapWindContractTest().InstallIsolatedPreferences(world, directory);
        var monitor = world.GetNode<RingPerformanceMonitor>("RingPerformanceMonitor");
        monitor.InstallSettingsPathForTests(directory + "/telemetry.tres");
        monitor.InstallReportDirectoryForTests(directory + "/reports");
        ProjectSettings.SetSetting("ringworld/runtime/active_world_directory", "");
        world.GetNode<RingVoxelExplorerController>("Explorer").SetCanonicalSpawnMeters(8150667.255809009, 397.3149763485417, 119474.33742621914);
    }
    private void CopyPreference(string key, string source, string directory)
    {
        if (!ResourceLoader.Exists(source)) { _preferences.Add(new { key, source, present = false }); return; }
        var resource = ResourceLoader.Load<Resource>(source, cacheMode: ResourceLoader.CacheMode.Ignore)
            ?? throw new IOException("Cannot read current preference " + source);
        SavePreference(key, resource.Duplicate(true), directory, source);
    }
    private void SavePreference(string key, Resource resource, string directory, string source)
    {
        string target = directory + "/" + key + ".tres";
        if (ResourceSaver.Save(resource, target) != Error.Ok) throw new IOException("Cannot copy preference " + key);
        string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ProjectSettings.GlobalizePath(target))));
        _preferences.Add(new { key, source, target, present = true, sha256 });
    }
    public bool SetPose(Node world)
    {
        try { SetPoseCore(world); return true; }
        catch (Exception error) { ErrorText = error.ToString(); Status = "FAILED"; return false; }
    }
    /// <summary>Diagnostic relocation preserving the authoritative simulation clocks.</summary>
    public bool SetPoseAltitudeOffset(Node world, double meters)
    {
        try
        {
            if (!double.IsFinite(meters) || meters < -400 || meters > 5000)
                throw new ArgumentOutOfRangeException(nameof(meters));
            _poseAltitudeOffset = meters;
            var explorer = world.GetNode<RingVoxelExplorerController>("Explorer");
            explorer.RestoreSavedState(new RingCanonicalPosition(8150667.255809009,
                397.3149763485417 + meters, 119474.33742621914), Vector3.Zero,
                4.381515f, 2.0799365f, false, 0);
            RequireSourcePosition(explorer);
            RefreshCaptureAltitude(explorer);
            return true;
        }
        catch (Exception error) { ErrorText = error.ToString(); Status = "FAILED"; return false; }
    }
    private void SetPoseCore(Node world)
    {
        var explorer = world.GetNode<RingVoxelExplorerController>("Explorer");
        // Startup spawn settling snaps to the terrain. Restore the explicit
        // above-ground pose after Ready using the saved-state API, which keeps
        // its supplied altitude rather than invoking the spawn-height snap.
        explorer.RestoreSavedState(new RingCanonicalPosition(8150667.255809009, 397.3149763485417, 119474.33742621914),
            Vector3.Zero, 4.381515f, 2.0799365f, false, 0);
        RequireSourcePosition(explorer);
        RefreshCaptureAltitude(explorer);
        explorer.SetPhysicsProcess(false);
        explorer.SetProcessInput(false);
        explorer.SetProcessUnhandledInput(false);
        world.GetNode<RingDayNightCycle>("RingDayNightCycle").SetSimulationSeconds(4506.117868);
        world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds").CloudSimulationTimeSeconds = 4506.117868;
        // Normal celestial/cloud clocks advance during warmup and collection.
        var monitor = world.GetNode<RingPerformanceMonitor>("RingPerformanceMonitor");
        monitor.Enabled = true;
        monitor.HistorySeconds = 60;
        monitor.MemoryBudgetMiB = 16;
        if (_detailedGpuRequested) monitor.DetailedGpuEnabled = true;
        // Without opt-in keep copied DetailedGpuEnabled unchanged.
        _warmupStartedUsec = Time.GetTicksUsec();
    }
    private void RequireSourcePosition(RingVoxelExplorerController explorer)
    {
        var position = explorer.CanonicalPosition;
        if (Math.Abs(position.AroundMeters - 8150667.255809009) > .001
            || Math.Abs(position.AcrossMeters - 119474.33742621914) > .001
            || Math.Abs(position.VerticalMeters - (397.3149763485417 + _poseAltitudeOffset)) > .001)
            throw new InvalidOperationException($"Capture pose changed: around={position.AroundMeters:R}, across={position.AcrossMeters:R}, height={position.VerticalMeters:R}; expected around=8150667.255809009, across=119474.33742621914, height={397.3149763485417 + _poseAltitudeOffset:R}.");
    }
    private void RequireCaptureAltitude(RingVoxelExplorerController explorer)
    {
        float computed = explorer.GetCanonicalAtmosphereHeightMeters();
        float cached = explorer.CurrentAltitudeAboveTerrainMeters;
        if (!float.IsFinite(computed) || !float.IsFinite(cached) || Math.Abs(computed - cached) > .01f)
            throw new InvalidOperationException($"Capture cached altitude changed: computed={computed:R}, cached={cached:R}.");
        ComputedAltitudeAboveTerrainMeters = computed;
        CachedAltitudeAboveTerrainMeters = cached;
    }
    public bool BeginCollection(Node world)
    {
        try
        {
            RequireSourcePosition(world.GetNode<RingVoxelExplorerController>("Explorer"));
            RequireCaptureAltitude(world.GetNode<RingVoxelExplorerController>("Explorer"));
            world.GetNode<RingPerformanceMonitor>("RingPerformanceMonitor").ClearHistory();
            _collectionStartedUsec = Time.GetTicksUsec();
            if (!_warmupRecorded) { _actualWarmupSeconds = (_collectionStartedUsec - _warmupStartedUsec) / 1000000.0; _warmupRecorded = true; }
            return true;
        }
        catch (Exception error) { ErrorText = error.ToString(); Status = "FAILED"; return false; }
    }
    public bool SetCaseContext(Node world, string name, string role)
    {
        try
        {
            if (!Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,64}$") || !Regex.IsMatch(role, "^[A-Za-z0-9_-]{1,64}$")) throw new ArgumentException("Invalid case label.");
            _captureCase = name; _phaseRole = role; _clocksFrozen = true;
            world.GetNode<RingPerformanceMonitor>("RingPerformanceMonitor").InstallReportDirectoryForTests(_captureDirectory + "/reports/" + name + "-" + role);
            return true;
        }
        catch (Exception error) { ErrorText = error.ToString(); Status = "FAILED"; return false; }
    }
    public async void BeginCapture(Node world)
    {
        Status = "CAPTURING";
        try
        {
            RequireSourcePosition(world.GetNode<RingVoxelExplorerController>("Explorer"));
            RequireCaptureAltitude(world.GetNode<RingVoxelExplorerController>("Explorer"));
            var monitor = world.GetNode<RingPerformanceMonitor>("RingPerformanceMonitor");
            double actualCollectionSeconds = (Time.GetTicksUsec() - _collectionStartedUsec) / 1000000.0;
            double captureCloudSeconds = world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds").CloudSimulationTimeSeconds;
            // Snapshot main context before any image readback or runner metadata work.
            string mainReport = monitor.CaptureSlowdown();
            BundleDirectory = await monitor.CaptureFarRingViewports();
            if (!Directory.Exists(BundleDirectory)) throw new IOException("Far capture did not return a bundle directory: " + BundleDirectory);
            if (!File.Exists(mainReport)) throw new IOException("Main telemetry snapshot failed: " + mainReport);
            File.Copy(mainReport, Path.Combine(BundleDirectory, "main-telemetry.json"));
            var explorer = world.GetNode<RingVoxelExplorerController>("Explorer");
            var position = explorer.CanonicalPosition;
            File.WriteAllText(Path.Combine(BundleDirectory, "gameplay-run.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, mainReport, preferences = _preferences, poseAltitudeOffsetMeters = _poseAltitudeOffset, ComputedAltitudeAboveTerrainMeters, CachedAltitudeAboveTerrainMeters,
                altitudePolicy = "Physics held stationary; cached altitude refreshed from canonical terrain query after every tool pose relocation.",
                sourcePose = new { around = 8150667.255809009, across = 119474.33742621914, height = 397.3149763485417, yawRadians = 4.381515, pitchDegrees = 2.0799365, celestialSeconds = 4506.117868 },
                actualPose = new { around = position.AroundMeters, across = position.AcrossMeters, height = position.VerticalMeters, yawRadians = explorer.SavedYaw, pitchDegrees = explorer.SavedPitch, celestialSeconds = world.GetNode<RingDayNightCycle>("RingDayNightCycle").GetSimulationSeconds() },
                warmupSeconds = WarmupSeconds, actualWarmupSeconds = _actualWarmupSeconds, collectionSeconds = CollectionSeconds, actualCollectionSeconds, artifactLabel = ArtifactLabel, mode = _captureCase == "grass-lifecycle" ? "serial grass lifecycle at explicitly reported pose with current copied preferences" : _clocksFrozen ? "stationary matched A-B-A current copied preferences" : "stationary current copied preferences", captureCase = _captureCase, phaseRole = _phaseRole, clocksFrozen = _clocksFrozen, SettleSeconds, detailedGpuRequested = _detailedGpuRequested, captureCloudSeconds,
                runtime = new { maxFps = Engine.MaxFps, vsync = DisplayServer.WindowGetVsyncMode().ToString(), display = DisplayServer.GetName(), refreshHz = DisplayServer.ScreenGetRefreshRate(), timeScale = Engine.TimeScale, commandLine = OS.GetCmdlineUserArgs() },
                clockPolicy = _clocksFrozen ? "Celestial Paused and CloudSimulationPaused set after warmup; all presentation processes otherwise retained; exact pose checked at sample boundaries." : "Normal celestial and cloud clocks advance from synchronized supplied 4506.117868 time; exact canonical pose restored after Ready and verified at warmup/collection boundaries; physics/input held stationary; no renderer isolation or quality toggles.",
                sourceWorldDirectory = _sourceWorld,
                configuration = new { sourcePreset = "High (source performance report)", actualPreset = world.GetNode<RingGraphicsQualityController>("GraphicsQuality").CurrentPresetName,
                    ringRadiusMeters = world.GetNode<VoxelChunkGridManager>("ChunkGridManager").RingRadiusMeters,
                    ringWidthMeters = world.GetNode<VoxelChunkGridManager>("ChunkGridManager").RingAcrossWidthMeters,
                    terrain3D = world.GetNode<VoxelChunkGridManager>("ChunkGridManager").UseTerrain3DSurface,
                    monitor.HistorySeconds, monitor.MemoryBudgetMiB, monitor.DetailedGpuEnabled },
                limitations = "WorldSaveManager excluded to avoid writes. Saved world terrain generation/edits and other runtime slot state are not restored; Certified geometry/backend and source-report High baseline plus current sparse preferences and captured render state are authoritative. CPU/GPU history retention may differ; images/runner metadata collected after telemetry snapshot."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Status = "DONE";
        }
        catch (Exception error) { ErrorText = error.ToString(); Status = "FAILED"; }
    }
}
