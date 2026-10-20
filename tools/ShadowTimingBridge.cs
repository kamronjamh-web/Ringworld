using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Diagnostics;

/// <summary>
/// Shadow S0 harness bridge: collects RingPerformanceGpu marker intervals (CloudShadowNear/Far, TerrainCloudSunVisibility, ...) and, optionally,
/// Godot's own engine timestamps per frame. Inert until Begin() is called; End() turns everything back off. Used by tools/shadow_attribution_test.gd.
/// Engine timestamps in Godot 4.8-dev7 are only vp_begin_&lt;id&gt; / vp_end_&lt;id&gt; per measured viewport (no shadow/opaque/sky regions are exposed by the
/// RenderingDevice API); they are paired into one "vp_&lt;id&gt;" GPU interval per viewport. Unknown future names fall back to cumulative-timestamp deltas.
/// </summary>
public partial class ShadowTimingBridge : RefCounted
{
    private readonly RingPerformanceTelemetry.Completed[] _drain = new RingPerformanceTelemetry.Completed[256];
    private readonly Dictionary<string, List<double>> _window = new(StringComparer.Ordinal);
    private readonly List<string> _engineNames = new();
    private readonly HashSet<string> _engineNameSet = new(StringComparer.Ordinal);
    private readonly List<(string Name, ulong Time)> _scratch = new();
    private readonly HashSet<(string, long)> _seenMarkers = new();
    private long _lastEngineFrame = -1;
    private bool _active, _engine;
    private int _freshFrames, _dropped;

    public bool Begin(bool engineTimestamps)
    {
        End();
        RingPerformanceTelemetry.Configure(true, 4096);
        RingPerformanceGpu.Configure(true);
        RingPerformanceGpu.ConfigureEngineTimestamps(engineTimestamps);
        _engine = engineTimestamps; _active = true; _lastEngineFrame = -1;
        return true;
    }

    public void End()
    {
        if (!_active) return;
        RingPerformanceGpu.ConfigureEngineTimestamps(false);
        RingPerformanceGpu.Configure(false);
        RingPerformanceTelemetry.Configure(false);
        _active = false; _engine = false;
    }

    /// <summary>Clears the per-window accumulators (call at the start of each timed window, after warmup).</summary>
    public void ResetWindow()
    {
        _window.Clear(); _seenMarkers.Clear(); _freshFrames = 0; _dropped = 0;
    }

    /// <summary>Call once per frame: schedules a non-blocking readback and drains whatever completed earlier.</summary>
    public void Sample()
    {
        if (!_active) return;
        RingPerformanceGpu.RequestReadback();
        int count = RingPerformanceTelemetry.DrainGpu(_drain, out int dropped);
        _dropped += dropped;
        for (int i = 0; i < count; i++)
        {
            RingPerformanceTelemetry.Completed item = _drain[i];
            if (!item.Gpu) continue;
            Add(item.Name, item.Milliseconds);
        }
        if (_engine && RingPerformanceGpu.TryCopyEngineTimestamps(_lastEngineFrame, _scratch, out long frame))
        {
            _lastEngineFrame = frame; _freshFrames++;
            var regions = new Dictionary<string, double>(StringComparer.Ordinal);
            var viewportBegins = new Dictionary<string, ulong>(StringComparer.Ordinal);
            for (int i = 0; i < _scratch.Count; i++)
            {
                string name = _scratch[i].Name;
                if (_engineNameSet.Add(name)) _engineNames.Add(name);
                // 4.8-dev7 only captures "vp_begin_<viewport rid id>" / "vp_end_<id>" per measured viewport; pair them into one GPU interval per viewport.
                if (name.StartsWith("vp_begin_", StringComparison.Ordinal)) { viewportBegins[name.Substring(9)] = _scratch[i].Time; continue; }
                if (name.StartsWith("vp_end_", StringComparison.Ordinal))
                {
                    string id = name.Substring(7);
                    if (viewportBegins.TryGetValue(id, out ulong begin) && _scratch[i].Time >= begin) regions["vp_" + id] = regions.GetValueOrDefault("vp_" + id) + (_scratch[i].Time - begin) / 1_000_000d;
                    continue;
                }
                // Any other engine-named timestamp (none in 4.8-dev7): cumulative timestamp, the region is named after the one that ends it.
                if (i == 0) continue;
                ulong previous = _scratch[i - 1].Time, current = _scratch[i].Time;
                if (current >= previous) regions[name] = regions.GetValueOrDefault(name) + (current - previous) / 1_000_000d;
            }
            foreach ((string name, double ms) in regions) Add("ENG:" + name, ms);
        }
    }

    private void Add(string name, double milliseconds)
    {
        if (!_window.TryGetValue(name, out List<double>? list)) _window.Add(name, list = new List<double>(512));
        list.Add(milliseconds);
    }

    /// <summary>Every distinct engine timestamp name seen since Begin(), in first-seen order.</summary>
    public Godot.Collections.Array<string> ListEngineTimestampNames()
    {
        var names = new Godot.Collections.Array<string>();
        foreach (string name in _engineNames) names.Add(name);
        return names;
    }

    public string Status() => RingPerformanceGpu.Status;

    /// <summary>name -> samples (ms) for the current window, plus "_meta".</summary>
    public Godot.Collections.Dictionary Window()
    {
        var result = new Godot.Collections.Dictionary();
        foreach ((string name, List<double> values) in _window)
        {
            var packed = new double[values.Count]; values.CopyTo(packed);
            result[name] = packed;
        }
        result["_meta"] = new Godot.Collections.Dictionary { ["fresh_engine_frames"] = _freshFrames, ["dropped"] = _dropped, ["status"] = RingPerformanceGpu.Status };
        return result;
    }
}
