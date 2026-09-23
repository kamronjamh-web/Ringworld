using System;
using Godot;
using RingworldTechDemo.Diagnostics;

/// <summary>Owns the bounded GPU timestamp collection used by the production grass A/B profile.</summary>
public partial class GrassProductionTimingBridge : Node
{
    private readonly RingPerformanceTelemetry.Completed[] _drain = new RingPerformanceTelemetry.Completed[256];
    private readonly Godot.Collections.Array<Godot.Collections.Dictionary> _grass = new();
    private int _dropped;
    private bool _active;

    public void BeginTimedWindow()
    {
        // Reset after warmup so no pre-window marker or monitor event can enter this run.
        RingPerformanceGpu.Configure(false);
        RingPerformanceTelemetry.Configure(false);
        _grass.Clear(); _dropped = 0;
        RingPerformanceTelemetry.Configure(true, 2048);
        RingPerformanceGpu.Configure(true);
        _active = true;
    }

    /// <summary>Non-blocking: schedules a readback and drains only completed, delayed samples.</summary>
    public void SampleFrame()
    {
        if (!_active) return;
        RingPerformanceGpu.RequestReadback();
        Drain();
    }

    /// <summary>Call during the idle tail after grass and camera processing have been stopped.</summary>
    public Godot.Collections.Dictionary FinishTimedWindow()
    {
        if (_active) { RingPerformanceGpu.RequestReadback(); Drain(); }
        string status = RingPerformanceGpu.Status;
        RingPerformanceGpu.Configure(false);
        RingPerformanceTelemetry.Configure(false);
        _active = false;
        return new Godot.Collections.Dictionary { ["grass_culling"] = _grass.Duplicate(true), ["dropped"] = _dropped, ["status"] = status };
    }

    private void Drain()
    {
        int count = RingPerformanceTelemetry.Drain(_drain, out int dropped);
        _dropped += dropped;
        for (int i = 0; i < count; i++)
        {
            RingPerformanceTelemetry.Completed item = _drain[i];
            if (!item.Gpu || item.Name != "GPU." + RingPerformanceGpu.GrassCulling) continue;
            _grass.Add(new Godot.Collections.Dictionary {
                ["milliseconds"] = item.Milliseconds,
                ["source_frame"] = item.SourceFrameId,
                ["completed_seconds"] = item.CompletedSeconds
            });
        }
    }
}
