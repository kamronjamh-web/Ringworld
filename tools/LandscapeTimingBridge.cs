using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Vegetation;
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
    /// Nearest large tree ahead of (around, across) for the LOD-switch dolly. Builds the 3 x 3 production tiles through the selected world around the point,
    /// takes the nearest tree at least <paramref name="minHeight"/> m tall whose offset has a positive component along the yaw and lies between minM and maxM, then picks the dolly
    /// bearing (the direction from the tree out to the camera, nearest to straight back toward the pose) with no other tree near the 0..lineM line. Returns {found, tree_around, tree_across,
    /// surface, height, width, variant, species, distance_to_pose, bearing_deg, clear_radius_m, tree_count, error}.
    /// </summary>
    public Godot.Collections.Dictionary NearestTreeAhead(LandscapeWorldBridge world, double around, double across, double yawDeg, double minM, double maxM, double minHeight, double lineM)
    {
        var result = new Godot.Collections.Dictionary { ["found"] = false };
        const double Circ = LandscapeWorld.Circumference;
        using var sampler = world.SelectedSpec.CreateSampler();
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

    /// <summary>
    /// TR-1 switch strip (docs/work-orders/terrain-vegetation/TR.md): for each species the best isolated tree within 600 m of one of the anchors that can be watched from every
    /// distance in <paramref name="distances"/> along one bearing with no other tree near the line and the terrain below the sight line. Only trees with a rank of at most
    /// <paramref name="maxRank"/> are taken, so the tree survives the far slots' rank gates. Returns {species name -> {found, tree_around, tree_across, height, width, variant, rank,
    /// bearing_deg, clearance_m, isolation_m, anchor}, tree_counts, line_m}. Tiles are built through the selected world exactly as NearestTreeAhead does.
    /// </summary>
    public Godot.Collections.Dictionary FindStripTrees(LandscapeWorldBridge world, double[] anchorsAround, double[] anchorsAcross, double[] distances, double maxRank, double minHeight)
    {
        var result = new Godot.Collections.Dictionary();
        const double Circ = LandscapeWorld.Circumference, Cell = 40d;
        double lineM = 0d;
        foreach (double d in distances) lineM = Math.Max(lineM, d);
        lineM += 10d;
        double scan = lineM + 620d;
        using var sampler = world.SelectedSpec.CreateSampler();
        long cells = (long)Math.Ceiling(Circ / 30d);
        var best = new Dictionary<TreeSpecies, (double Score, Godot.Collections.Dictionary Entry)>();
        var counts = new Godot.Collections.Array();
        for (int anchor = 0; anchor < anchorsAround.Length && anchor < anchorsAcross.Length; anchor++)
        {
            bool allGood = best.Count == 3;
            foreach (var entry in best.Values) if (entry.Score < 8d) allGood = false;
            if (allGood) break;
            double a0 = anchorsAround[anchor], z0 = anchorsAcross[anchor];
            var trees = new List<RingBiomeDecorationLayer.ImportedForestPlacementRecord>();
            for (long tx = (long)Math.Floor((a0 - scan) / 240d); tx <= (long)Math.Floor((a0 + scan) / 240d); tx++)
                for (long tz = (long)Math.Floor((z0 - scan) / 240d); tz <= (long)Math.Floor((z0 + scan) / 240d); tz++)
                    trees.AddRange(RingBiomeDecorationLayer.BuildImportedForestReviewTile(sampler, tx, tz, cells, 30d, LandscapeWorld.Seed, Circ, LandscapeWorld.Width));
            counts.Add(trees.Count);
            var grid = new Dictionary<(long, long), List<int>>();
            for (int i = 0; i < trees.Count; i++)
            {
                var key = ((long)Math.Floor((trees[i].Around - a0) / Cell), (long)Math.Floor((trees[i].Across - z0) / Cell));
                if (!grid.TryGetValue(key, out List<int>? list)) grid[key] = list = new List<int>();
                list.Add(i);
            }
            var candidates = new List<(double Isolation, int Index)>();
            for (int i = 0; i < trees.Count; i++)
            {
                var t = trees[i];
                if (t.Height < minHeight || (float)(t.Id >> 40) / 16777215.0f > maxRank) continue;
                double da = t.Around - a0, dz = t.Across - z0;
                if (da * da + dz * dz > 600d * 600d) continue;
                long cx = (long)Math.Floor(da / Cell), cz = (long)Math.Floor(dz / Cell);
                double isolation = 60d;
                for (long gx = cx - 2; gx <= cx + 2; gx++)
                    for (long gz = cz - 2; gz <= cz + 2; gz++)
                    {
                        if (!grid.TryGetValue((gx, gz), out List<int>? list)) continue;
                        foreach (int j in list)
                        {
                            if (j == i) continue;
                            double oa = trees[j].Around - t.Around, oz = trees[j].Across - t.Across;
                            isolation = Math.Min(isolation, Math.Sqrt(oa * oa + oz * oz));
                        }
                    }
                if (isolation >= 10d) candidates.Add((isolation, i));
            }
            candidates.Sort((x, y) => y.Isolation.CompareTo(x.Isolation));
            var tried = new Dictionary<TreeSpecies, int>();
            foreach ((double isolation, int index) in candidates)
            {
                var t = trees[index];
                if (best.TryGetValue(t.Species, out var current) && current.Score >= 12d) continue;
                int attempts = tried.GetValueOrDefault(t.Species);
                if (attempts >= 60) continue;
                tried[t.Species] = attempts + 1;
                double treeGround = world.GroundHeight(t.Around, t.Across), target = treeGround + 0.5d * t.Height;
                double bestScore = 0d, bestBearing = double.NaN;
                for (int k = 0; k < 72; k++)
                {
                    double bearing = k * Math.PI / 36d, ca = Math.Cos(bearing), cz = Math.Sin(bearing), worst = 30d;
                    for (double s = 0d; s <= lineM + 20d && worst > bestScore; s += 20d)
                    {
                        long gx0 = (long)Math.Floor((t.Around + ca * s - a0) / Cell), gz0 = (long)Math.Floor((t.Across + cz * s - z0) / Cell);
                        for (long gx = gx0 - 1; gx <= gx0 + 1; gx++)
                            for (long gz = gz0 - 1; gz <= gz0 + 1; gz++)
                            {
                                if (!grid.TryGetValue((gx, gz), out List<int>? list)) continue;
                                foreach (int j in list)
                                {
                                    if (j == index) continue;
                                    var o = trees[j];
                                    double oa = o.Around - t.Around, oz = o.Across - t.Across, along = oa * ca + oz * cz;
                                    if (along < 2d || along > lineM + 5d) continue;
                                    worst = Math.Min(worst, Math.Abs(-oa * cz + oz * ca) - (0.45d * o.Width + 2.5d));
                                }
                            }
                    }
                    if (worst <= bestScore) continue;
                    bool clear = true;
                    foreach (double d in distances)
                    {
                        double camA = t.Around + ca * d, camZ = t.Across + cz * d, eye = world.GroundHeight(camA, camZ) + 1.7d;
                        for (int step = 1; step <= 7 && clear; step++)
                        {
                            double f = step / 8d;
                            if (world.GroundHeight(camA + (t.Around - camA) * f, camZ + (t.Across - camZ) * f) + 1d > eye + (target - eye) * f) clear = false;
                        }
                        if (!clear) break;
                    }
                    if (!clear) continue;
                    bestScore = worst; bestBearing = bearing;
                }
                if (double.IsNaN(bestBearing)) continue;
                if (best.TryGetValue(t.Species, out var previous) && previous.Score >= bestScore) continue;
                best[t.Species] = (bestScore, new Godot.Collections.Dictionary
                {
                    ["found"] = true, ["species"] = t.Species.ToString(), ["tree_around"] = (double)t.Around, ["tree_across"] = (double)t.Across,
                    ["tree_ground"] = treeGround, ["height"] = (double)t.Height, ["width"] = (double)t.Width, ["variant"] = t.Variant,
                    ["rank"] = (double)((float)(t.Id >> 40) / 16777215.0f), ["bearing_deg"] = bestBearing * 180d / Math.PI,
                    ["clearance_m"] = bestScore, ["isolation_m"] = isolation, ["anchor"] = anchor
                });
            }
        }
        foreach (TreeSpecies species in Enum.GetValues<TreeSpecies>())
            result[species.ToString()] = best.TryGetValue(species, out var chosen) ? chosen.Entry : new Godot.Collections.Dictionary { ["found"] = false, ["species"] = species.ToString() };
        result["tree_counts"] = counts; result["line_m"] = lineM;
        return result;
    }
}
