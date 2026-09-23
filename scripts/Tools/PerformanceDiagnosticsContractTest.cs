using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Godot;
using RingworldTechDemo.Diagnostics;
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

        // The monitor's ring buffer must remain bounded even with many rows.
        monitor.ResetForTests();
        monitor.MemoryBudgetMiB = 2;
        monitor._Process(.016);
        for (var i = 0; i < 33_000; i++) monitor.InjectCompletedSampleForTests("cap." + (i % 129), 1, completedSeconds: i);
        var capped = monitor.GetSnapshot();
        if (capped.Rows.Count > 129 || capped.DroppedSamples <= 0 || capped.RetainedTelemetryBytes > 2L * 1024 * 1024)
            failures.Add("global telemetry buffer did not evict bounded synthetic categories");

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
        bool json = false;
        try { if (exists) { using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)); json = document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object; } } catch { }
        return new GodotDictionary { ["pass"] = exists && bytes > 0 && bytes <= 64L * 1024 * 1024 && json, ["exists"] = exists, ["bytes"] = bytes, ["json"] = json };
    }
}
