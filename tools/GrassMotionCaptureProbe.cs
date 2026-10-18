using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Diagnostics;

/// <summary>
/// Summarises one grass motion capture phase from the live performance monitor (tools/grass_motion_capture.gd).
/// Read-only: it never changes settings, history or preferences.
/// </summary>
public partial class GrassMotionCaptureProbe : RefCounted
{
    private long _allocatedAtStart;
    private int _gen0AtStart, _gen1AtStart, _gen2AtStart;

    public void MarkPhaseStart()
    {
        _allocatedAtStart = GC.GetTotalAllocatedBytes(false);
        _gen0AtStart = GC.CollectionCount(0);
        _gen1AtStart = GC.CollectionCount(1);
        _gen2AtStart = GC.CollectionCount(2);
    }

    /// <summary>Canonical position of the explorer (its struct is not Variant-compatible).</summary>
    public Godot.Collections.Dictionary ExplorerState(Node explorerNode)
    {
        var explorer = (RingworldTechDemo.Voxel.Integration.RingVoxelExplorerController)explorerNode;
        var position = explorer.CanonicalPosition;
        return new Godot.Collections.Dictionary
        {
            ["around"] = position.AroundMeters, ["across"] = position.AcrossMeters, ["vertical"] = position.VerticalMeters,
            ["speed"] = explorer.ActualTravelSpeedMetersPerSecond
        };
    }

    /// <summary>Cumulative GPU grass counters that are not Godot-visible (internal test accessors).</summary>
    public Godot.Collections.Dictionary GrassGpuCounters(Node grassNode)
    {
        var grass = (RingworldTechDemo.Voxel.LOD.RingStandingGrassLayer)grassNode;
        return new Godot.Collections.Dictionary
        {
            ["GpuDispatches"] = grass.GpuDispatchCountForTests,
            ["GpuParameterUploads"] = grass.GpuParameterUploadsForTests,
            ["GrassCullingRelatchesProbe"] = grass.GrassCullingRelatches
        };
    }

    /// <summary>Returns a JSON object: frame percentiles, per-frame scope costs, GC deltas.</summary>
    public string Summarize(Node monitorNode, double collectionSeconds)
    {
        var monitor = (RingPerformanceMonitor)monitorNode;
        RingPerformanceSnapshot snapshot = monitor.GetSnapshot();
        IReadOnlyList<RingPerformanceFrame> frames = snapshot.History;
        int frameCount = frames.Count;
        object Stats(Func<RingPerformanceFrame, double?> select)
        {
            var values = new List<double>(frameCount);
            foreach (RingPerformanceFrame frame in frames)
            {
                double? value = select(frame);
                if (value is double v && double.IsFinite(v)) values.Add(v);
            }
            if (values.Count == 0) return new { count = 0 };
            values.Sort();
            double Pick(double p) => values[(int)Math.Clamp(Math.Ceiling(values.Count * p) - 1, 0, values.Count - 1)];
            return new { count = values.Count, mean = values.Average(), p50 = Pick(.5), p95 = Pick(.95), max = values[^1] };
        }
        double window = frameCount > 1 ? frames[^1].Seconds - frames[0].Seconds : collectionSeconds;
        double perFrameDivisor = Math.Max(1, frameCount);
        var rows = snapshot.Rows
            .OrderByDescending(row => row.Gpu ? 0 : row.MeanMilliseconds * row.Count)
            .Select(row => new
            {
                name = row.Name, worker = row.Worker, gpu = row.Gpu, count = row.Count,
                meanMs = row.MeanMilliseconds, p95Ms = row.P95Milliseconds, maxMs = row.MaxMilliseconds,
                // Main-thread cost per rendered frame in the window (calls x mean / frames); not meaningful for worker rows.
                perFrameMs = row.MeanMilliseconds * row.Count / perFrameDivisor,
                callsPerFrame = row.Count / perFrameDivisor
            }).ToArray();
        return JsonSerializer.Serialize(new
        {
            frames = frameCount,
            windowSeconds = window,
            fps = window > 0 ? frameCount / window : 0,
            frameMs = Stats(f => f.FrameMilliseconds),
            processMs = Stats(f => f.ProcessMilliseconds),
            physicsMs = Stats(f => f.PhysicsMilliseconds),
            renderSetupMs = Stats(f => f.SetupMilliseconds),
            mainViewportCpuMs = Stats(f => f.ViewportCpuMilliseconds),
            mainViewportGpuMs = Stats(f => f.ViewportGpuMilliseconds),
            outerFarViewportCpuMs = Stats(f => f.FarViewportCpuMilliseconds),
            outerFarViewportGpuMs = Stats(f => f.FarViewportGpuMilliseconds),
            regionalFarViewportCpuMs = Stats(f => f.RegionalFarViewportCpuMilliseconds),
            regionalFarViewportGpuMs = Stats(f => f.RegionalFarViewportGpuMilliseconds),
            drawCalls = Stats(f => f.DrawCalls >= 0 ? f.DrawCalls : null),
            primitives = Stats(f => f.RenderPrimitives),
            speedMps = Stats(f => f.SpeedMetersPerSecond),
            gc = new
            {
                allocatedBytes = GC.GetTotalAllocatedBytes(false) - _allocatedAtStart,
                gen0 = GC.CollectionCount(0) - _gen0AtStart,
                gen1 = GC.CollectionCount(1) - _gen1AtStart,
                gen2 = GC.CollectionCount(2) - _gen2AtStart
            },
            droppedSamples = snapshot.DroppedSamples,
            rows
        });
    }
}
