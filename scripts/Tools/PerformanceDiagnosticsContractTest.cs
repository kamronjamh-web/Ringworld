using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using GodotDictionary = Godot.Collections.Dictionary;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Small deterministic contract probe used by the headless runner.  It only
/// observes the diagnostics node supplied by the runner; it never touches the
/// player's diagnostics preference file.
/// </summary>
public partial class PerformanceDiagnosticsContractTest : Node
{
    private RingCanonicalPosition? _landedPosition;
    /// <summary>Sets only the scene-local benchmark override; it does not save player state.</summary>
    public GodotDictionary ExerciseCanonicalPositions(Node scene)
    {
        var explorer = scene.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        if (explorer == null) return new GodotDictionary { ["pass"] = false, ["reason"] = "Explorer missing" };
        // Derive the position from the certified scene's spawned/surface-safe
        // canonical location instead of assuming a terrain height.
        var landed = explorer.CanonicalPosition;
        var flight = landed with { AroundMeters = landed.AroundMeters + 1_000, VerticalMeters = landed.VerticalMeters + 40 };
        explorer.SetBenchmarkCanonicalPosition(landed, 0, false);
        explorer.SetBenchmarkCanonicalPosition(flight, 280, true);
        explorer.SetBenchmarkCanonicalPosition(landed, 0, false);
        return new GodotDictionary { ["pass"] = true, ["landed_speed_mps"] = 0, ["flight_speed_mps"] = 280 };
    }

    /// <summary>One scene-local benchmark phase per call; callers render real frames between calls.</summary>
    public GodotDictionary SetBenchmarkPhase(Node scene, bool fastFlight)
    {
        var explorer = scene.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        if (explorer == null) return new GodotDictionary { ["pass"] = false, ["reason"] = "Explorer missing" };
        _landedPosition ??= explorer.CanonicalPosition;
        RingCanonicalPosition landed = _landedPosition.Value;
        RingCanonicalPosition target = fastFlight ? landed with { AroundMeters = landed.AroundMeters + 1_000, VerticalMeters = landed.VerticalMeters + 40 } : landed;
        explorer.SetBenchmarkCanonicalPosition(target, fastFlight ? 280 : 0, fastFlight);
        return new GodotDictionary { ["pass"] = true, ["phase"] = fastFlight ? "fast_flight" : "landed", ["speed_mps"] = fastFlight ? 280 : 0 };
    }

    public GodotDictionary Run(RingPerformanceMonitor monitor)
    {
        var failures = new List<string>();
        monitor.ResetForTests();
        monitor.SetGpuAvailabilityForTests(false);
        monitor.Enabled = false;
        RingPerformanceTelemetry.Configure(false);
        using (RingPerformanceTelemetry.MeasureCpu("disabled")) { }
        monitor._Process(0.016);
        if (monitor.GetSnapshot().Rows.Count != 0)
            failures.Add("disabled scopes emitted events");

        monitor.Enabled = true;
        RingPerformanceTelemetry.Configure(true);
        using (RingPerformanceTelemetry.MeasureCpu("main.cpu")) Thread.Sleep(1);
        using (RingPerformanceTelemetry.MeasureCpu("worker.cpu", worker: true)) Thread.Sleep(1);
        using (RingPerformanceTelemetry.MeasureCpu("nested.parent"))
        {
            using (RingPerformanceTelemetry.MeasureCpu("nested.child")) Thread.Sleep(1);
        }
        // This is deliberately synthetic: percentile and max must remain in
        // milliseconds and preserve the values supplied by the GPU producer.
        for (var i = 1; i <= 20; i++) monitor.InjectCompletedSampleForTests("synthetic.gpu", i, gpu: true, sourceFrameId: 91);
        monitor._Process(0.016);
        var rows = monitor.GetSnapshot().Rows;
        var main = rows.SingleOrDefault(r => r.Name == "main.cpu" && !r.Worker && !r.Gpu);
        var worker = rows.SingleOrDefault(r => r.Name == "worker.cpu" && r.Worker && !r.Gpu);
        var gpu = rows.SingleOrDefault(r => r.Name == "synthetic.gpu" && r.Gpu);
        var parent = rows.SingleOrDefault(r => r.Name == "nested.parent");
        var child = rows.SingleOrDefault(r => r.Name == "nested.child");
        if (main.Count != 1 || worker.Count != 1) failures.Add("main and worker CPU categories were not separate");
        if (gpu.Count != 20 || Math.Abs(gpu.P95Milliseconds - 19) > .001 || Math.Abs(gpu.MaxMilliseconds - 20) > .001)
            failures.Add("synthetic GPU p95/max are incorrect or incorrectly scaled");
        if (parent.Count != 1 || child.Count != 1 || parent.MeanMilliseconds < child.MeanMilliseconds)
            failures.Add("nested inclusive scopes were not recorded independently");

        // Simultaneous producers must not lose or merge worker rows.
        const int workers = 8;
        var threads = Enumerable.Range(0, workers).Select(i => new Thread(() =>
        {
            using var scope = RingPerformanceTelemetry.MeasureCpu("concurrent.worker", worker: true);
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();
        monitor._Process(0.016);
        var concurrent = monitor.GetSnapshot().Rows.SingleOrDefault(r => r.Name == "concurrent.worker" && r.Worker);
        if (concurrent.Count != workers) failures.Add("concurrent worker scopes were lost");

        // Completed history is intentionally retained until the user clears it.
        monitor.ClearHistory();

        var snapshot = monitor.GetSnapshot();
        if (!snapshot.GpuAvailable && snapshot.History.Any(f => f.ViewportGpuMilliseconds == 0))
            failures.Add("headless GPU timing was represented as measured zero");

        monitor.ResetForTests();
        monitor.HistorySeconds = 2;
        monitor.InjectFrameForTests(10, 1);
        monitor.InjectFrameForTests(11, 1);
        monitor.InjectFrameForTests(13.1, 1);
        monitor.InjectCompletedSampleForTests("expired", 1, completedSeconds: 10);
        monitor.InjectCompletedSampleForTests("live", 1, completedSeconds: 13.1);
        var bounded = monitor.GetSnapshot();
        if (bounded.History.Count != 1 || bounded.Rows.Any(r => r.Name == "expired") || !bounded.Rows.Any(r => r.Name == "live"))
            failures.Add("history/event time eviction did not use the monotonic frame clock");

        monitor.ResetForTests();
        monitor.HistorySeconds = 60;
        monitor.InjectFrameForTests(100, 1);
        monitor.RefreshSeconds = .1f;
        monitor.InjectFrameForTests(101, 1);
        if (monitor.GetSnapshot().History.Last().Seconds != 101)
            failures.Add("display refresh setting reset the monotonic frame clock");

        // Replay a minute at 1000 CPU samples/second, covering the gameplay
        // report's observed event rate without sleeping or using fake GPU data.
        monitor.ResetForTests();
        monitor.HistorySeconds = 60;
        monitor.MemoryBudgetMiB = 16;
        monitor._Process(.016);
        monitor.ResetForTests();
        double minuteStart = RingPerformanceMonitor.NowSeconds;
        for (int second = 0; second < 60; second++)
        {
            monitor.InjectFrameForTests(minuteStart + second, 58);
            for (int sample = 0; sample < 1000; sample++)
                monitor.InjectCompletedSampleForTests("minute.cpu", 1, completedSeconds: minuteStart + second + sample / 1000.0);
        }
        var minute = monitor.GetSnapshot();
        if (minute.Rows.FirstOrDefault(row => row.Name == "minute.cpu").Count != 60_000
            || minute.DroppedSamples != 0 || minute.RetainedTelemetryBytes > 16L * 1024 * 1024)
            failures.Add("16 MiB budget did not retain a minute at the observed CPU event rate");

        monitor.InjectCompletedSampleForTests("capture.only.old", 100, completedSeconds: minuteStart);
        var live = monitor.GetLiveSnapshot();
        int liveBuilds = monitor.LiveSummaryBuildCount;
        if (monitor.LastLiveSummarySampleCount > 5120 || live.History.Count > 300
            || live.Rows.Any(row => row.Name == "capture.only.old")
            || live.Rows.FirstOrDefault(row => row.Name == "minute.cpu").Count > 4096)
            failures.Add("live summary did not bound sample work or respect its recent window");
        monitor.InjectCompletedSampleForTests("live.new", 2, completedSeconds: minuteStart + 59);
        if (!ReferenceEquals(live, monitor.GetLiveSnapshot()) || monitor.LiveSummaryBuildCount != liveBuilds)
            failures.Add("new telemetry forced an immediate live summary rebuild");
        var fullAfterLive = monitor.GetSnapshot();
        if (!fullAfterLive.Rows.Any(row => row.Name == "capture.only.old")
            || !fullAfterLive.Rows.Any(row => row.Name == "live.new")
            || fullAfterLive.Rows.FirstOrDefault(row => row.Name == "minute.cpu").Count != 60_000)
            failures.Add("live summary modified or truncated full export statistics");
        monitor.ClearHistory();
        if (monitor.GetLiveSnapshot().Rows.Count != 0 || monitor.GetLiveSnapshot().History.Count != 0)
            failures.Add("history clear retained a stale live summary");

        // The monitor's ring buffer must remain bounded even with many rows.
        monitor.ResetForTests();
        monitor.MemoryBudgetMiB = 2;
        monitor._Process(.016);
        double cpuFloodNow = RingPerformanceMonitor.NowSeconds;
        for (var i = 0; i < 33_000; i++) monitor.InjectCompletedSampleForTests("cap." + (i % 129), 1, completedSeconds: cpuFloodNow + i * .00001);
        var capped = monitor.GetSnapshot();
        if (capped.Rows.Count > 129 || capped.DroppedSamples <= 0 || capped.RetainedTelemetryBytes > 2L * 1024 * 1024)
            failures.Add("global telemetry buffer did not evict bounded synthetic categories");

        monitor.ResetForTests();
        monitor.MemoryBudgetMiB = 2;
        double protectedGpuNow = RingPerformanceMonitor.NowSeconds;
        monitor.InjectCompletedSampleForTests("gpu.survives.cpu.flood", 1, gpu: true, sourceFrameId: 77, completedSeconds: protectedGpuNow);
        for (var i = 0; i < 33_000; i++)
            monitor.InjectCompletedSampleForTests("cpu.flood.unique." + i, 1, completedSeconds: protectedGpuNow + i * .00001);
        if (!monitor.GetSnapshot().Rows.Any(row => row.Name == "gpu.survives.cpu.flood" && row.Gpu))
            failures.Add("CPU unique-name flood evicted independent GPU retention");

        // GPU retention is deliberately separate: a flood of delayed GPU
        // completions must neither evict CPU rows nor exceed the same budget.
        monitor.ResetForTests();
        monitor.MemoryBudgetMiB = 2;
        double gpuFloodNow = RingPerformanceMonitor.NowSeconds;
        monitor.InjectCompletedSampleForTests("cpu.survives.gpu.flood", 1, completedSeconds: gpuFloodNow);
        for (var i = 0; i < 33_000; i++)
            monitor.InjectCompletedSampleForTests("gpu.flood." + (i % 97), 1, gpu: true, sourceFrameId: i / 3, completedSeconds: gpuFloodNow + i * .00001);
        var gpuCapped = monitor.GetSnapshot();
        if (!gpuCapped.Rows.Any(row => row.Name == "cpu.survives.gpu.flood" && !row.Gpu)
            || !gpuCapped.Rows.Any(row => row.Gpu) || gpuCapped.DroppedSamples <= 0
            || gpuCapped.RetainedTelemetryBytes > 2L * 1024 * 1024)
            failures.Add("GPU retention flood did not preserve CPU rows within the telemetry budget");
        monitor.ClearHistory();
        if (monitor.GetSnapshot().Rows.Count != 0 || monitor.GetSnapshot().History.Count != 0)
            failures.Add("history clear did not reset CPU and GPU retention together");

        // A scope opened in one generation cannot arrive after disable/re-enable.
        RingPerformanceTelemetry.Configure(true);
        monitor.ClearHistory();
        var oldScope = RingPerformanceTelemetry.MeasureCpu("late.scope");
        RingPerformanceTelemetry.Configure(false);
        RingPerformanceTelemetry.Configure(true);
        oldScope.Dispose();
        monitor._Process(.016);
        if (monitor.GetSnapshot().Rows.Any(row => row.Name == "late.scope")) failures.Add("late scope crossed disabled generation");

        monitor.Enabled = false; monitor.HistorySeconds = 999; monitor.MemoryBudgetMiB = -1; monitor.RefreshSeconds = float.NaN;
        monitor.MarkSettingsDirty();
        if (!monitor.SaveSettings()) failures.Add("disabled diagnostics settings did not save");
        else { monitor.ApplySettings(); if (monitor.Enabled || monitor.HistorySeconds != 60 || monitor.MemoryBudgetMiB != 2 || Math.Abs(monitor.RefreshSeconds - .2f) > .001f) failures.Add("saved settings were not clamped/reloaded"); }
        if (!VerifyPrepareCloudCapture()) failures.Add("prepare cloud slowdown capture control did not apply its complete diagnostic preset");
        if (!VerifyPrepareFarRingCapture()) failures.Add("prepare far-ring capture did not clear history, sync/persist settings, or preserve detailed GPU mode");
        RingPerformanceTelemetry.Configure(false);
        return new GodotDictionary
        {
            ["pass"] = failures.Count == 0,
            ["failures"] = failures.ToArray(),
            ["rows"] = snapshot.Rows.Count,
            ["gpu_available"] = snapshot.GpuAvailable,
            ["caveat"] = snapshot.Caveat,
        };
    }

    private static bool VerifyPrepareCloudCapture()
    {
        RingPerformanceMonitor monitor = new();
        RingLiveTuningDrawer drawer = new();
        TabContainer tabs = new();
        try
        {
            typeof(RingPerformanceMonitor).GetMethod("EnsureCapacity", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(monitor, null);
            monitor.InjectFrameForTests(1, 16);
            monitor.Enabled = false;
            monitor.DetailedGpuEnabled = false;
            monitor.HistorySeconds = 5;
            monitor.MemoryBudgetMiB = 2;
            typeof(RingLiveTuningDrawer).GetField("_performanceMonitor", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(drawer, monitor);
            typeof(RingLiveTuningDrawer).GetMethod("BuildDiagnosticsTab", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(drawer, new object[] { tabs });
            Button? prepare = tabs.FindChild("DiagnosticsPrepareCloudSlowdown", recursive: true, owned: false) as Button;
            if (prepare == null) return false;
            prepare.EmitSignal("pressed");
            return monitor.Enabled && monitor.DetailedGpuEnabled && monitor.HistorySeconds == 60 && monitor.MemoryBudgetMiB == 16
                && monitor.GetSnapshot().History.Count == 0;
        }
        catch { return false; }
        finally { tabs.Free(); drawer.Free(); monitor.Free(); }
    }

    private static bool VerifyPrepareFarRingCapture()
    {
        const string path = "res://.godot/performance-diagnostics-contract/far-prepare-settings.tres";
        RingPerformanceMonitor monitor = new(); RingLiveTuningDrawer drawer = new(); TabContainer tabs = new();
        try
        {
            monitor.InstallSettingsPathForTests(path);
            typeof(RingPerformanceMonitor).GetMethod("EnsureCapacity", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(monitor, null);
            typeof(RingLiveTuningDrawer).GetField("_performanceMonitor", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(drawer, monitor);
            typeof(RingLiveTuningDrawer).GetMethod("BuildDiagnosticsTab", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(drawer, new object[] { tabs });
            var prepare = tabs.FindChild("DiagnosticsPrepareFarRingCapture", true, false) as Button;
            var capture = tabs.FindChild("DiagnosticsCaptureFarRingViewports", true, false) as Button;
            var enabled = tabs.FindChild("DiagnosticsMonitoringToggle", true, false) as CheckButton;
            var detailed = tabs.FindChild("DiagnosticsGpuToggle", true, false) as CheckButton;
            var history = tabs.FindChild("DiagnosticsHistorySeconds", true, false) as HSlider;
            var memory = tabs.FindChild("DiagnosticsMemoryMiB", true, false) as HSlider;
            if (prepare == null || capture == null || enabled == null || detailed == null || history == null || memory == null || capture.Disabled) return false;
            foreach (bool detailedMode in new[] { false, true })
            {
                monitor.Enabled = false; monitor.DetailedGpuEnabled = detailedMode; monitor.HistorySeconds = 5; monitor.MemoryBudgetMiB = 2;
                monitor.InjectFrameForTests(1, 16);
                prepare.EmitSignal("pressed");
                bool dirty = (bool)(typeof(RingPerformanceMonitor).GetField("_settingsDirty", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(monitor) ?? false);
                if (!monitor.Enabled || monitor.DetailedGpuEnabled != detailedMode || monitor.HistorySeconds != 60 || monitor.MemoryBudgetMiB != 16
                    || monitor.GetSnapshot().History.Count != 0 || !dirty || !enabled.ButtonPressed || detailed.ButtonPressed != detailedMode || history.Value != 60 || memory.Value != 16) return false;
                if (!monitor.SaveSettings()) return false;
                monitor.Enabled = false; monitor.HistorySeconds = 5; monitor.MemoryBudgetMiB = 2; monitor.DetailedGpuEnabled = !detailedMode;
                monitor.ApplySettings();
                if (!monitor.Enabled || monitor.DetailedGpuEnabled != detailedMode || monitor.HistorySeconds != 60 || monitor.MemoryBudgetMiB != 16) return false;
            }
            return true;
        }
        catch { return false; }
        finally { tabs.Free(); drawer.Free(); monitor.Free(); File.Delete(ProjectSettings.GlobalizePath(path)); }
    }

    /// <summary>Typed snapshot bridge; records are not Godot Variants.</summary>
    public GodotDictionary ValidateRenderedSnapshot(RingPerformanceMonitor monitor)
    {
        var snapshot = monitor.GetSnapshot();
        bool cpu = snapshot.Rows.Any(row => !row.Gpu && row.Count > 0);
        bool frames = snapshot.History.Count > 0 && snapshot.History.Any(frame => frame.FrameMilliseconds > 0);
        bool gpu = !snapshot.GpuAvailable || snapshot.History.Any(frame => frame.ViewportGpuMilliseconds is > 0 and < 1000);
        return new GodotDictionary { ["pass"] = cpu && frames && gpu, ["cpu_rows"] = cpu, ["frame_history"] = frames, ["gpu_timing_valid_or_unavailable"] = gpu, ["gpu_available"] = snapshot.GpuAvailable };
    }

    public GodotDictionary ValidateGpuWaterFft(RingPerformanceMonitor monitor)
    {
        var snapshot = monitor.GetSnapshot();
        var water = snapshot.Rows.FirstOrDefault(row => row.Name == "GPU.WaterFft" && row.Gpu);
        bool valid = water.Count > 0 && double.IsFinite(water.MaxMilliseconds) && water.MaxMilliseconds > 0 && water.MaxMilliseconds < 100;
        return new GodotDictionary { ["pass"] = valid, ["count"] = water.Count, ["max_ms"] = water.MaxMilliseconds, ["p95_ms"] = water.P95Milliseconds, ["gpu_available"] = snapshot.GpuAvailable };
    }

    /// <summary>Requires actual timestamp rows; it never injects synthetic GPU samples.</summary>
    public GodotDictionary ValidateCloudGpuProfiler(RingPerformanceMonitor monitor)
    {
        var rows = monitor.GetSnapshot().Rows;
        string[] required = { "GPU.Clouds", "GPU.CloudPrepass", "GPU.CloudRaymarch", "GPU.CloudReconstruct", "GPU.CloudComposite", "GPU.CloudShadowNear", "GPU.CloudShadowFar" };
        string[] missing = required.Where(name => !rows.Any(row => row.Gpu && row.Name == name && row.Count > 0 && double.IsFinite(row.MaxMilliseconds) && row.MaxMilliseconds > 0)).ToArray();
        return new GodotDictionary { ["pass"] = missing.Length == 0, ["missing"] = missing, ["gpu_available"] = monitor.GetSnapshot().GpuAvailable, ["rows"] = rows.Count };
    }

    /// <summary>Fixture-only marker/cascade wiring for the real Sunshine resource.</summary>
    public bool ConfigureEffectProfiler(GodotObject effect)
    {
        if (effect == null || !effect.HasMethod("configure_gpu_cloud_shadow_cascade")) return false;
        effect.Set("gpu_timestamp_diagnostics_enabled", true);
        effect.Set("gpu_timestamp_begin_marker", RingPerformanceGpu.CloudBeginMarker);
        effect.Set("gpu_timestamp_end_marker", RingPerformanceGpu.CloudEndMarker);
        effect.Set("gpu_timestamp_cloud_shadow_near_begin_marker", RingPerformanceGpu.CloudShadowNearBeginMarker);
        effect.Set("gpu_timestamp_cloud_shadow_near_end_marker", RingPerformanceGpu.CloudShadowNearEndMarker);
        effect.Set("gpu_timestamp_cloud_shadow_far_begin_marker", RingPerformanceGpu.CloudShadowFarBeginMarker);
        effect.Set("gpu_timestamp_cloud_shadow_far_end_marker", RingPerformanceGpu.CloudShadowFarEndMarker);
        Vector3 center = Vector3.Zero, around = Vector3.Forward, across = Vector3.Right, up = Vector3.Up, sun = new Vector3(.4f, .75f, .5f).Normalized();
        for (int cascade = 0; cascade < 2; cascade++)
        {
            bool configured = effect.Call("configure_gpu_cloud_shadow_cascade", cascade, 64, center, around, across, up,
                Vector2.Zero, 5_000_000f, cascade == 0 ? 20_000f : 80_000f, sun, 2_000f, 8_000f, 8, .5f, true).AsBool();
            if (!configured) return false;
            effect.Call("request_gpu_cloud_shadow_update", cascade);
        }
        return true;
    }

    /// <summary>Supplies a read-only bridge resource to the monitor's cloud-history sampler.</summary>
    public bool AttachCloudHistoryBridgeForTests(GodotObject bridge, GodotObject effect)
    {
        FieldInfo? field = typeof(RingSunshineCloudBridge).GetField("_clouds", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) return false;
        field.SetValue(bridge, effect);
        return true;
    }

    public Node CreateCloudHistoryBridgeForTests(GodotObject effect)
    {
        var bridge = new FixtureCloudHistoryBridge { Name = "SunshineCloudBridge", Enabled = false };
        if (!AttachCloudHistoryBridgeForTests(bridge, effect)) { bridge.Free(); throw new InvalidOperationException("Cloud bridge resource field unavailable"); }
        return bridge;
    }

    public GodotDictionary ReadCloudHistoryDiagnostics(RingPerformanceMonitor monitor)
    {
        RingCloudHistoryDiagnostics diagnostics = monitor.CloudHistoryDiagnostics;
        return new GodotDictionary { ["count"] = diagnostics.Count, ["capacity"] = diagnostics.Capacity, ["status"] = diagnostics.Status, ["errors"] = diagnostics.CaptureErrors };
    }

    public GodotDictionary ReadContext(RingPerformanceMonitor monitor)
    {
        var context = monitor.GetSnapshot().Context;
        return new GodotDictionary { ["terrain"] = context.TerrainCoordinator, ["chunks"] = context.ChunkGrid, ["water"] = context.Water, ["memory"] = context.MemoryLabel, ["speed_mps"] = ContextSpeed(context.MemoryLabel) };
    }

    private static double ContextSpeed(string text)
    {
        const string marker = "speed "; int start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return -1; start += marker.Length; int end = text.IndexOf(' ', start);
        return double.TryParse(end < 0 ? text[start..] : text[start..end], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double speed) ? speed : -1;
    }

    public GodotDictionary ValidateExport(string path)
    {
        bool exists = File.Exists(path);
        long bytes = exists ? new FileInfo(path).Length : -1;
        bool json = false, renderState = false, missingNodesNull = false, contextErrorNull = false, v2 = false, timeBins = false, gpuGroups = false, retention = false;
        try
        {
            if (exists)
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                json = document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
                v2 = json && document.RootElement.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == System.Text.Json.JsonValueKind.Number && schema.GetInt32() == 2;
                if (v2 && document.RootElement.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == System.Text.Json.JsonValueKind.Object)
                    retention = metadata.TryGetProperty("retention", out var retained) && retained.ValueKind == System.Text.Json.JsonValueKind.Object
                        && metadata.TryGetProperty("gpuTimestampStatus", out var gpuStatus) && gpuStatus.ValueKind == System.Text.Json.JsonValueKind.String;
                if (v2 && document.RootElement.TryGetProperty("captureOnlyOneSecondBins", out var bins) && bins.ValueKind == System.Text.Json.JsonValueKind.Array)
                    timeBins = bins.GetArrayLength() > 0 && bins.EnumerateArray().All(bin => bin.ValueKind == System.Text.Json.JsonValueKind.Object
                        && bin.TryGetProperty("startSeconds", out var start) && start.ValueKind == System.Text.Json.JsonValueKind.Number
                        && bin.TryGetProperty("endSeconds", out var end) && end.ValueKind == System.Text.Json.JsonValueKind.Number
                        && end.GetDouble() - start.GetDouble() == 1
                        && bin.TryGetProperty("timestampQuality", out var quality) && quality.ValueKind == System.Text.Json.JsonValueKind.String);
                if (v2 && document.RootElement.TryGetProperty("gpuBySourceFrame", out var groups) && groups.ValueKind == System.Text.Json.JsonValueKind.Array)
                    gpuGroups = groups.EnumerateArray().All(group => group.ValueKind == System.Text.Json.JsonValueKind.Object
                        && group.TryGetProperty("sourceFrameId", out var source) && source.ValueKind == System.Text.Json.JsonValueKind.Number
                        && group.TryGetProperty("categories", out var categories) && categories.ValueKind == System.Text.Json.JsonValueKind.Array
                        && group.TryGetProperty("timestampQuality", out var quality) && quality.ValueKind == System.Text.Json.JsonValueKind.String);
                if (json && document.RootElement.TryGetProperty("renderState", out var state) && state.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    renderState = state.TryGetProperty("lights", out var lights) && lights.ValueKind == System.Text.Json.JsonValueKind.Object;
                    missingNodesNull = renderState && lights.TryGetProperty("star", out var star) && lights.TryGetProperty("planet", out var planet)
                        && (star.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Object)
                        && (planet.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Object);
                }
                contextErrorNull = document.RootElement.TryGetProperty("renderStateContextError", out var error) && error.ValueKind == System.Text.Json.JsonValueKind.Null;
            }
        }
        catch { }
        bool pass = exists && bytes > 0 && bytes <= 64L * 1024 * 1024 && json && v2 && retention && timeBins && gpuGroups && renderState && missingNodesNull && contextErrorNull;
        return new GodotDictionary { ["pass"] = pass, ["exists"] = exists, ["bytes"] = bytes, ["json"] = json, ["schema_v2"] = v2, ["retention"] = retention, ["one_second_bins"] = timeBins, ["gpu_source_groups"] = gpuGroups, ["render_state"] = renderState, ["missing_nodes_null_or_object"] = missingNodesNull, ["render_state_context_error_null"] = contextErrorNull };
    }
}

/// <summary>Fixture-only bridge keeps cloud-history type lookup without production scene discovery.</summary>
internal sealed partial class FixtureCloudHistoryBridge : global::RingworldTechDemo.RingSunshineCloudBridge
{
    public override void _Ready() { }
    public override void _ExitTree() { }
}
