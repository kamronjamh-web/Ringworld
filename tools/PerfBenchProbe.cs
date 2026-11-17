// Helper for tools/perf_bench.gd (the reusable real-world performance benchmark, docs/perf-bench.md). Read-only except PlaceNoclip.
// Replaces the throwaway TmpSlot3PerfProbe.cs of the 2026-11 perf pass. Run through tools/perf_bench.ps1.
using System;
using System.Linq;
using Godot;
using RingworldTechDemo.Diagnostics;

namespace RingworldTechDemo.Tools;

public partial class PerfBenchProbe : RefCounted
{
    /// <summary>Places the explorer at an absolute canonical position in noclip with a target flight speed (m/s).</summary>
    public void PlaceNoclip(Node explorerNode, double around, double vertical, double across, float yawDeg, float pitchDeg, float speed)
    {
        if (explorerNode is RingworldTechDemo.Voxel.Integration.RingVoxelExplorerController explorer)
            explorer.RestoreSavedState(new RingworldTechDemo.Voxel.Core.RingCanonicalPosition(around, vertical, across), Vector3.Zero,
                Mathf.DegToRad(yawDeg), pitchDeg, true, 0, speed);
    }

    public Godot.Collections.Dictionary Gc() => new()
    {
        ["gen0"] = GC.CollectionCount(0), ["gen1"] = GC.CollectionCount(1), ["gen2"] = GC.CollectionCount(2),
        ["pause_ms"] = GC.GetTotalPauseDuration().TotalMilliseconds, ["allocated_mb"] = GC.GetTotalAllocatedBytes(false) / 1048576.0,
        ["heap_mb"] = GC.GetTotalMemory(false) / 1048576.0,
    };

    /// <summary>Every monitor row (CPU scopes and detailed GPU intervals) of the monitor's current history, plus the dropped-sample count.</summary>
    public Godot.Collections.Dictionary MonitorRows(Node monitorNode)
    {
        var result = new Godot.Collections.Dictionary();
        var rows = new Godot.Collections.Array();
        if (monitorNode is not RingPerformanceMonitor monitor) return result;
        var snapshot = monitor.GetSnapshot();
        foreach (var r in snapshot.Rows.OrderByDescending(r => r.MeanMilliseconds * r.Count))
            rows.Add(new Godot.Collections.Dictionary
            {
                ["name"] = r.Name, ["worker"] = r.Worker, ["gpu"] = r.Gpu, ["count"] = r.Count, ["mean_ms"] = r.MeanMilliseconds,
                ["p95_ms"] = r.P95Milliseconds, ["max_ms"] = r.MaxMilliseconds,
            });
        result["rows"] = rows;
        result["dropped"] = snapshot.DroppedSamples;
        result["gpu_available"] = snapshot.GpuAvailable;
        result["working_set_mb"] = snapshot.WorkingSetBytes / 1048576.0;
        result["managed_heap_mb"] = snapshot.ManagedHeapBytes / 1048576.0;
        return result;
    }

    /// <summary>Per-frame series of the monitor history (-1 when a value is unavailable).</summary>
    public Godot.Collections.Dictionary MonitorFrames(Node monitorNode)
    {
        var result = new Godot.Collections.Dictionary();
        if (monitorNode is not RingPerformanceMonitor monitor) return result;
        var frames = monitor.GetSnapshot().History;
        void Add(string name, Func<RingPerformanceFrame, double?> select)
        {
            var values = new Godot.Collections.Array();
            foreach (RingPerformanceFrame f in frames) { double? v = select(f); values.Add(v.HasValue && double.IsFinite(v.Value) ? v.Value : -1.0); }
            result[name] = values;
        }
        Add("frame", f => f.FrameMilliseconds); Add("process", f => f.ProcessMilliseconds); Add("physics", f => f.PhysicsMilliseconds);
        Add("setup", f => f.SetupMilliseconds); Add("vp_cpu", f => f.ViewportCpuMilliseconds); Add("vp_gpu", f => f.ViewportGpuMilliseconds);
        Add("far_cpu", f => f.FarViewportCpuMilliseconds); Add("far_gpu", f => f.FarViewportGpuMilliseconds);
        Add("prims", f => f.RenderPrimitives); Add("draws", f => f.DrawCalls);
        Add("vram_mb", f => f.ReportedVideoMemoryBytes >= 0 ? f.ReportedVideoMemoryBytes / 1048576.0 : null);
        return result;
    }
}
