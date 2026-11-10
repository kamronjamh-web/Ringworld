using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Landscape T0-7 harness bridge (docs/landscape-audit-t0-spec.md WO T0-7), the ShadowTimingBridge pattern with both queues drained: RingPerformanceGpu marker intervals
/// (TreeCulling, GrassCulling, Vegetation, ProxyHeights, HeightShadow, WaterFft ...), the CPU scopes (Trees build, Trees disk load, Grass build) and, optionally, Godot's engine
/// timestamps including the three opaque-bracket names written by tools/landscape_pass_timestamp_effect.gd. Inert until Begin() is called. Also finds the nearest tree ahead of
/// a pose for the LOD-switch capture. TEST ONLY.
/// </summary>
public partial class LandscapeTimingBridge : RefCounted
{
    public const string PreOpaque = "lp_pre_opaque", PostOpaque = "lp_post_opaque", PostTransparent = "lp_post_transparent";

    private readonly RingPerformanceTelemetry.Completed[] _drain = new RingPerformanceTelemetry.Completed[512];
    private readonly Dictionary<string, List<double>> _gpu = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<double>> _cpu = new(StringComparer.Ordinal);
    private readonly List<string> _engineNames = new();
    private readonly HashSet<string> _engineNameSet = new(StringComparer.Ordinal);
    private readonly List<(string Name, ulong Time)> _scratch = new();
    private long _lastEngineFrame = -1;
    private bool _active, _engine;
    private int _freshFrames, _droppedCpu, _droppedGpu, _bracketFrames;

    public bool Begin(bool engineTimestamps)
    {
        End();
        RingPerformanceTelemetry.Configure(true, 8192);
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

    /// <summary>Clears the accumulators (start of a fill or of a timed window).</summary>
    public void ResetWindow()
    {
        _gpu.Clear(); _cpu.Clear(); _freshFrames = 0; _droppedCpu = _droppedGpu = 0; _bracketFrames = 0;
    }

    /// <summary>Call once per frame: schedules a non-blocking GPU readback and drains whatever completed (GPU markers, CPU scopes, engine timestamps).</summary>
    public void Sample()
    {
        if (!_active) return;
        RingPerformanceGpu.RequestReadback();
        int count = RingPerformanceTelemetry.DrainGpu(_drain, out int droppedGpu);
        _droppedGpu += droppedGpu;
        for (int i = 0; i < count; i++) if (_drain[i].Gpu) Add(_gpu, _drain[i].Name, _drain[i].Milliseconds);
        do
        {
            count = RingPerformanceTelemetry.Drain(_drain, out int droppedCpu);
            _droppedCpu += droppedCpu;
            for (int i = 0; i < count; i++) if (!_drain[i].Gpu) Add(_cpu, _drain[i].Name, _drain[i].Milliseconds);
        } while (count == _drain.Length);
        if (_engine && RingPerformanceGpu.TryCopyEngineTimestamps(_lastEngineFrame, _scratch, out long frame))
        {
            _lastEngineFrame = frame; _freshFrames++;
            ulong preOpaque = 0, postOpaque = 0; bool havePre = false, havePost = false; int pairs = 0;
            for (int i = 0; i < _scratch.Count; i++)
            {
                string name = _scratch[i].Name;
                if (_engineNameSet.Add(name)) _engineNames.Add(name);
                if (name == PreOpaque) { preOpaque = _scratch[i].Time; havePre = true; havePost = false; }
                else if (name == PostOpaque && havePre && _scratch[i].Time >= preOpaque) { postOpaque = _scratch[i].Time; havePost = true; Add(_gpu, "BRACKET:opaque", (postOpaque - preOpaque) / 1_000_000d); pairs++; }
                else if (name == PostTransparent && havePost && _scratch[i].Time >= postOpaque) Add(_gpu, "BRACKET:post_opaque_to_transparent_end", (_scratch[i].Time - postOpaque) / 1_000_000d);
            }
            if (pairs > 0) _bracketFrames++;
        }
    }

    private static void Add(Dictionary<string, List<double>> into, string name, double milliseconds)
    {
        if (!into.TryGetValue(name, out List<double>? list)) into.Add(name, list = new List<double>(512));
        list.Add(milliseconds);
    }

    /// <summary>Every distinct engine timestamp name seen since Begin(), first-seen order (the opaque bracket needs lp_pre_opaque, lp_post_opaque, lp_post_transparent here).</summary>
    public Godot.Collections.Array<string> ListEngineTimestampNames()
    {
        var names = new Godot.Collections.Array<string>();
        foreach (string name in _engineNames) names.Add(name);
        return names;
    }

    public string Status() => RingPerformanceGpu.Status;

    private static Godot.Collections.Dictionary Pack(Dictionary<string, List<double>> source)
    {
        var result = new Godot.Collections.Dictionary();
        foreach ((string name, List<double> values) in source)
        {
            var packed = new double[values.Count]; values.CopyTo(packed);
            result[name] = packed;
        }
        return result;
    }

    /// <summary>GPU marker name -> samples (ms) of the current window, plus "_meta".</summary>
    public Godot.Collections.Dictionary Window()
    {
        Godot.Collections.Dictionary result = Pack(_gpu);
        result["_meta"] = new Godot.Collections.Dictionary { ["fresh_engine_frames"] = _freshFrames, ["bracket_frames"] = _bracketFrames, ["dropped_cpu"] = _droppedCpu, ["dropped_gpu"] = _droppedGpu, ["status"] = RingPerformanceGpu.Status };
        return result;
    }

    /// <summary>CPU scope name -> samples (ms) since the last ResetWindow(), plus "_meta".</summary>
    public Godot.Collections.Dictionary CpuWindow()
    {
        Godot.Collections.Dictionary result = Pack(_cpu);
        result["_meta"] = new Godot.Collections.Dictionary { ["dropped_cpu"] = _droppedCpu, ["dropped_gpu"] = _droppedGpu };
        return result;
    }

    /// <summary>
    /// Nearest large tree ahead of (around, across) for the LOD-switch dolly. Builds the 3 x 3 production tiles (BuildImportedForestReviewTile through the pinned world) around the point,
    /// takes the nearest tree at least <paramref name="minHeight"/> m tall whose offset has a positive component along the yaw and lies between minM and maxM, then picks the dolly
    /// bearing (the direction from the tree out to the camera, nearest to straight back toward the pose) with no other tree near the 0..lineM line. Returns {found, tree_around, tree_across,
    /// surface, height, width, variant, species, distance_to_pose, bearing_deg, clear_radius_m, tree_count, error}.
    /// </summary>
    public Godot.Collections.Dictionary NearestTreeAhead(double around, double across, double yawDeg, double minM, double maxM, double minHeight, double lineM)
    {
        var result = new Godot.Collections.Dictionary { ["found"] = false };
        const double Circ = LandscapeWorld.Circumference;
        using var sampler = LandscapeWorld.CreateSampler();
        long cells = (long)Math.Ceiling(Circ / 30d);
        long t0 = (long)Math.Floor((around - 200d) / 240d), t1 = (long)Math.Floor((around + 200d) / 240d);
        long u0 = (long)Math.Floor((across - 200d) / 240d), u1 = (long)Math.Floor((across + 200d) / 240d);
        var trees = new List<RingBiomeDecorationLayer.ImportedForestPlacementRecord>();
        for (long tx = t0; tx <= t1; tx++)
            for (long tz = u0; tz <= u1; tz++)
                trees.AddRange(RingBiomeDecorationLayer.BuildImportedForestReviewTile(sampler, tx, tz, cells, 30d, LandscapeWorld.Seed, Circ, LandscapeWorld.Width));
        result["tree_count"] = trees.Count;
        double yaw = yawDeg * Math.PI / 180d, fa = Math.Cos(yaw), fz = Math.Sin(yaw);
        var candidates = new List<(double Distance, int Index)>();
        for (int i = 0; i < trees.Count; i++)
        {
            var t = trees[i];
            if (t.Height < minHeight) continue;
            double da = t.Around - around, dz = t.Across - across, d = Math.Sqrt(da * da + dz * dz);
            if (d < minM || d > maxM || da * fa + dz * fz <= 0d) continue;
            candidates.Add((d, i));
        }
        if (candidates.Count == 0) { result["error"] = $"no tree of at least {minHeight} m between {minM} and {maxM} m ahead among {trees.Count}"; return result; }
        candidates.Sort((x, y) => x.Distance.CompareTo(y.Distance));
        // Try the nearest candidates; for each, scan 72 dolly bearings (5 degree steps) and keep the one whose 0..lineM line passes furthest from any other canopy
        // (0.4 x width + 1 m each). The first candidate with at least 2 m of clearance wins, else the best one seen.
        int best = -1; double bestDistance = 0d, chosen = double.NaN, clearance = double.NegativeInfinity;
        for (int c = 0; c < Math.Min(candidates.Count, 16); c++)
        {
            int index = candidates[c].Index; var tree = trees[index];
            double candidateBearing = double.NaN, candidateClearance = double.NegativeInfinity;
            for (int k = 0; k < 72; k++)
            {
                double bearing = k * Math.PI / 36d, ca = Math.Cos(bearing), cz = Math.Sin(bearing);
                double worst = double.MaxValue;
                for (int i = 0; i < trees.Count; i++)
                {
                    if (i == index) continue;
                    var o = trees[i];
                    double da = o.Around - tree.Around, dz = o.Across - tree.Across;
                    double along = da * ca + dz * cz;
                    if (along < 1d || along > lineM + 2d) continue;
                    double perp = Math.Abs(-da * cz + dz * ca) - (0.4d * o.Width + 1d);
                    worst = Math.Min(worst, perp);
                }
                if (worst == double.MaxValue) worst = lineM;
                if (worst > candidateClearance) { candidateClearance = worst; candidateBearing = bearing; }
            }
            if (candidateClearance > clearance) { clearance = candidateClearance; chosen = candidateBearing; best = index; bestDistance = candidates[c].Distance; }
            if (clearance >= 2d) break;
        }
        if (best < 0) { result["error"] = "no dolly bearing found"; return result; }
        var tree0 = trees[best];
        result["found"] = true;
        result["tree_around"] = (double)tree0.Around; result["tree_across"] = (double)tree0.Across;
        result["surface"] = (double)tree0.SurfaceHeight; result["height"] = (double)tree0.Height; result["width"] = (double)tree0.Width;
        result["variant"] = tree0.Variant; result["species"] = tree0.Species.ToString();
        result["distance_to_pose"] = bestDistance;
        result["bearing_deg"] = chosen * 180d / Math.PI; result["clear_radius_m"] = clearance;
        return result;
    }
}
