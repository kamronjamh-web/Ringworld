using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T3R-12 review tooling (docs/work-orders/terrain-vegetation/T3.md): picks the G2 bookmark places and a 10 km benchmark route on the river graph of the review world (seed, generation 1, review feature water.rivers only,
/// "review-rivers-s1337") and writes them to --out: places.json (the list tools/river_site_capture.gd reads), route.json (the T2R-14 travel benchmark) and bookmarks.md (the table for the user). Same recipe, settings and
/// graph path as RiverBedPoseQuery. The torrent place is chosen first (hash order); every other place is the passing candidate nearest to it, so the places sit together. Reads only; nothing in the world changes.
/// Run windowed (GPU graph source, as the game bakes it): tools/run_godot.ps1 -Class shared -Script res://tools/river_review_places.gd -UserArgs "--seed=1337 --out=&lt;absolute dir&gt;" -Marker RIVER_REVIEW_PLACES -TimeoutMin 20
/// </summary>
public partial class RiverReviewPlaces : RefCounted
{
    private const string ExpectedIdentity = "2FF3577786E4C137";
    private const double BankStandOffM = 7d, MarginM = 20_000d;
    private static void Say(string text) => Console.WriteLine("RIVER_REVIEW_PLACES " + text);
    private static string F(double v, string f = "F1") => v.ToString(f, CultureInfo.InvariantCulture);

    private sealed class Place
    {
        public string Name = "", Prefix = "", Kind = "";
        public int Stem = -1, Span = -1, Lake = -1;
        public double S, CentreA, CentreZ, AlongYaw, StandA, StandZ, Ground, Yaw, Width, DepthC = double.NaN, DepthQ1 = double.NaN, DepthQ3 = double.NaN, BedC = double.NaN, Dist;
        public double TargetA, TargetZ;
        public string Class = "", Detail = "";
        public double Danger, Q, Landing;
    }

    private RiverNetwork _net = null!;
    private RiverSpanTable _table = null!;
    private RingTerrainSampler _sampler = null!;
    private TerrainGenerationHeightModel _model = null!;
    private RiverExclusions? _zones;
    private bool[]? _excluded;
    private int _seed = 1337;
    private RiverGraph _graph = null!;

    private double Circ => EmitterSiteTestWorlds.Circumference;
    private double Wid => EmitterSiteTestWorlds.Width;

    private TerrainSample Sample(double a, double z) => _sampler.GetTerrainSample((long)Math.Floor(_net.WrapAround(a)), (int)Math.Floor(Math.Clamp(z, 0d, Wid - 1d)));
    private float Presentation(double a, double z) => _sampler.GetPresentationSurfaceHeight(_net.WrapAround(a), Math.Clamp(z, 0d, Wid - 1d));
    private double Ring(double a1, double z1, double a2, double z2) { double da = _net.WrapDelta(a1 - a2), dz = z1 - z2; return Math.Sqrt(da * da + dz * dz); }

    /// <summary>Depth of the river water at a point: the float water surface minus the float bed, NaN when the point is not River water.</summary>
    private double RiverDepth(double a, double z)
    {
        TerrainSample s = Sample(a, z);
        return s.WaterKind == SurfaceWaterKind.River ? s.WaterLevel - Presentation(a, z) : double.NaN;
    }

    /// <summary>True when any of 16 directions x 8 radii (25 to 200 m) around the point is a Lake sample (as RiverBedPoseQuery).</summary>
    private bool LakeNear(double a, double z)
    {
        if (Sample(a, z).WaterKind == SurfaceWaterKind.Lake) return true;
        for (int k = 0; k < 16; k++)
            for (double r = 25d; r <= 200d; r += 25d)
                if (Sample(a + r * Math.Cos(k * Math.PI / 8d), z + r * Math.Sin(k * Math.PI / 8d)).WaterKind == SurfaceWaterKind.Lake) return true;
        return false;
    }

    private static double Deg(double radians) => Math.Round(((radians * 180d / Math.PI) % 360d + 360d) % 360d, 1);

    /// <summary>The bank place of a river at stem arc position s: centreline checks, bank walk along the normal (either side), standing point 7 m beyond the water's edge, no Lake within 200 m, natural height above 8 m.</summary>
    private Place? EvaluateRiver(int stem, double s)
    {
        _net.CentreFrame(stem, s, out double a, out double z, out double nx, out double ny);
        _net.Centre(stem, s, out _, out _, out double tx, out double ty);
        if (z < MarginM || z > Wid - MarginM) return null;
        if (_model.CalculateHeight(a, z) <= 8d) return null;
        if (Sample(a, z).WaterKind != SurfaceWaterKind.River) return null;
        _net.ChannelAt(stem, s, out RiverChannel ch);
        int span = _table.SpanAt(_net.ReachAt(stem, s), s);
        foreach (double sign in new[] { 1d, -1d })
        {
            double bank = -1d;
            for (double d = 1d; d <= 300d; d += 1d)
                if (Sample(a + sign * nx * d, z + sign * ny * d).WaterKind != SurfaceWaterKind.River) { bank = d; break; }
            if (bank < 0d) continue;
            double sa = _net.WrapAround(a + sign * nx * (bank + BankStandOffM)), sz = z + sign * ny * (bank + BankStandOffM);
            if (sz < MarginM || sz > Wid - MarginM) continue;
            if (Sample(sa, sz).WaterKind != SurfaceWaterKind.None) continue;
            if (LakeNear(sa, sz)) continue;
            var p = new Place
            {
                Stem = stem, Span = span, S = s, CentreA = a, CentreZ = z, StandA = sa, StandZ = sz, Width = 2d * ch.HalfWidth,
                Yaw = Deg(Math.Atan2(-sign * ny, -sign * nx)), AlongYaw = Deg(Math.Atan2(ty, tx)), Class = _table.Class[span].ToString(), Danger = _table.DangerSpeed[span], Q = _table.Spans[span].Q, Landing = _table.Spans[span].Landing,
            };
            p.Ground = Math.Round(Presentation(sa, sz), 1);
            p.BedC = Presentation(a, z);
            p.DepthC = RiverDepth(a, z);
            double q = ch.HalfWidth * 0.5;
            p.DepthQ1 = RiverDepth(a + nx * q, z + ny * q);
            p.DepthQ3 = RiverDepth(a - nx * q, z - ny * q);
            p.TargetA = a; p.TargetZ = z;
            return p;
        }
        return null;
    }

    private bool SpanUsable(int i)
    {
        int stem = _net.Reaches[_table.Spans[i].Reach].Stem;
        if (stem < _net.InLakeEnd.Length && _net.InLakeEnd[stem] > 0d && _table.Spans[i].SEnd <= _net.InLakeEnd[stem] + 1e-3) return false;
        if (_excluded != null && _excluded[i]) return false;
        return true;
    }

    private (double A, double Z) SpanMid(int i)
    {
        int stem = _net.Reaches[_table.Spans[i].Reach].Stem;
        _net.Centre(stem, 0.5 * (_table.Spans[i].SStart + _table.Spans[i].SEnd), out double a, out double z, out _, out _);
        return (a, z);
    }

    /// <summary>First passing place among the spans accepted by <paramref name="spanOk"/>, in the given order, trying the given fractions of each span.</summary>
    private Place? Pick(IEnumerable<int> order, Func<int, bool> spanOk, Func<Place, bool> placeOk, double[] fractions, ref int tried)
    {
        foreach (int i in order)
        {
            if (!SpanUsable(i) || !spanOk(i)) continue;
            int stem = _net.Reaches[_table.Spans[i].Reach].Stem;
            foreach (double f in fractions)
            {
                tried++;
                double s = _table.Spans[i].SStart + f * (_table.Spans[i].SEnd - _table.Spans[i].SStart);
                Place? p = EvaluateRiver(stem, s);
                if (p != null && p.Span == i && placeOk(p)) return p;
            }
        }
        return null;
    }

    private IEnumerable<int> NearestFirst(Place torrent, Func<int, bool> spanOk)
    {
        var list = new List<(double D, int I)>();
        for (int i = 0; i < _table.Count; i++)
        {
            if (!SpanUsable(i) || !spanOk(i)) continue;
            (double a, double z) = SpanMid(i);
            if (z < MarginM || z > Wid - MarginM) continue;
            list.Add((Ring(a, z, torrent.CentreA, torrent.CentreZ), i));
        }
        list.Sort((x, y) => x.D != y.D ? x.D.CompareTo(y.D) : x.I.CompareTo(y.I));
        return list.Select(x => x.I);
    }

    public int Run()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string outDir = "";
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--seed=")) _seed = int.Parse(arg.Substring(7), CultureInfo.InvariantCulture);
            else if (arg.StartsWith("--out=")) outDir = arg.Substring(6);
        }
        if (string.IsNullOrWhiteSpace(outDir) || !Path.IsPathRooted(outDir)) { Console.Error.WriteLine("RIVER_REVIEW_PLACES=FAIL --out=<absolute dir> is required"); return 2; }
        string work = ProjectSettings.GlobalizePath($"res://.godot/river_review_places_{System.Environment.ProcessId}");
        try
        {
            Directory.CreateDirectory(outDir);
            TerrainGenerationParameters recipe = EmitterSiteTestWorlds.Gen17WorldRecipe(_seed, 1, new[] { "water.rivers" });
            Say($"review world review-rivers-s{_seed}: seed {_seed}, generation 1, review features [water.rivers]; hydrology identity {HydrologyBakeIdentity.Compute(recipe):X16}, definition hash {recipe.DefinitionHash:X16}");
            using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(_seed, recipe);
            Directory.CreateDirectory(work);
            bool gpu = RenderingServer.GetRenderingDevice() != null;
            HydrologyBakeInputs inputs = HydrologyBakeInputs.Capture(settings, recipe, HydrologyBakeInputs.DefaultWorkers, gpu ? HydrologySourcePreference.Gpu : HydrologySourcePreference.Cpu, work);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            RiverGraph graph = RiverGraphStore.GetOrBuild(inputs);
            string identity = graph.IdentityHash.ToString("X16");
            Say($"graph ({(gpu ? "GPU" : "CPU")} source) in {F(clock.Elapsed.TotalSeconds)} s: {graph.Network.Stems.Length} stems, {graph.Lakes.Length} lakes, {graph.Inlets.Length} inlets, input identity {graph.InputIdentity:X16}; graph.IdentityHash {identity} {(identity == ExpectedIdentity ? "matches" : "IDENTITY MISMATCH")} the 2026-11-21 GPU bake ({ExpectedIdentity})");
            _graph = graph;
            _net = graph.Network;
            _table = _net.SpanTable ?? throw new InvalidOperationException("the network has no span table");
            _zones = RiverExclusions.Build(recipe);
            _excluded = _zones?.Mark(_net, _table.Spans);
            Say($"spans {_table.Count}; exclusion zones {(_zones?.ZoneCount ?? 0)}, spans touching one {(_excluded?.Count(x => x) ?? 0)}");
            _sampler = new RingTerrainSampler(settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
            _model = new TerrainGenerationHeightModel(recipe);
            try { return Pick(graph, outDir); }
            finally { _sampler.Dispose(); _model.Dispose(); }
        }
        catch (Exception e) { Console.Error.WriteLine("RIVER_REVIEW_PLACES=FAIL " + e); return 1; }
        finally
        {
            RiverGraphStore.Clear();
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    private static readonly double[] Mid = { 0.5, 0.25, 0.75 };
    private static readonly double[] Wide = { 0.5, 0.25, 0.75, 0.1, 0.9, 0.35, 0.65 };

    private int Pick(RiverGraph graph, string outDir)
    {
        var places = new List<Place>();
        var misses = new List<string>();
        int tried = 0;
        RiverSpanTable t = _table;
        var hashOrder = Enumerable.Range(0, t.Count).OrderBy(i => RiverHash.Mix(_seed, i, 7)).ToArray();

        // 4. torrent first (hash order).
        Place? torrent = Pick(hashOrder, i => t.Class[i] == RiverClass.Torrent && t.Spans[i].Landing <= 1f, _ => true, Mid, ref tried);
        if (torrent == null) { Say("NO CANDIDATE g2_torrent"); return Fail(); }
        torrent.Prefix = "g2_torrent"; torrent.Dist = 0d;
        Say($"torrent place found after {tried} evaluations");

        void Add(string prefix, string what, Place? p, int count)
        {
            if (p == null) { misses.Add(prefix); Say($"NO CANDIDATE {prefix} ({what}); {count} evaluations"); return; }
            p.Prefix = prefix;
            p.Dist = Ring(p.CentreA, p.CentreZ, torrent.CentreA, torrent.CentreZ);
            places.Add(p);
        }
        Place? Find(string prefix, string what, Func<int, bool> spanOk, Func<Place, bool> placeOk, double[] fractions)
        {
            int n = 0;
            Place? p = Pick(NearestFirst(torrent, spanOk), spanOk, placeOk, fractions, ref n);
            if (p == null) Say($"NO CANDIDATE {prefix} ({what}): {n} evaluations");
            return p;
        }

        var calm = Find("g2_calm", "Calm, landing <= 1, width 20-120 m", i => t.Class[i] == RiverClass.Calm && t.Spans[i].Landing <= 1f, p => p.Width >= 20d && p.Width <= 120d, Mid);
        var swift = Find("g2_swift", "Swift, landing <= 1, width 10-80 m", i => t.Class[i] == RiverClass.Swift && t.Spans[i].Landing <= 1f, p => p.Width >= 10d && p.Width <= 80d, Mid);
        var fierce = Find("g2_fierce", "Fierce, landing <= 1, width >= 6 m", i => t.Class[i] == RiverClass.Fierce && t.Spans[i].Landing <= 1f, p => p.Width >= 6d, Mid);
        bool UpstreamHard(int i) => i > 0 && _net.Reaches[t.Spans[i - 1].Reach].Stem == _net.Reaches[t.Spans[i].Reach].Stem && t.Class[i - 1] >= RiverClass.Fierce;
        var landing = Find("g2_landing", "landing span after a fierce or torrent span", i => t.Spans[i].Landing > 1f && UpstreamHard(i), _ => true, Mid);
        var wade = Find("g2_wade", "Calm or Swift, width 4-30 m, depth <= 0.8 m at centre and quarter points",
            i => t.Class[i] <= RiverClass.Swift && t.Spans[i].Landing <= 1f && 2f * t.HalfWidth[i] >= 3.5f && 2f * t.HalfWidth[i] <= 32f,
            p => p.Width >= 4d && p.Width <= 30d && p.DepthC <= 0.8 && p.DepthQ1 <= 0.8 && p.DepthQ3 <= 0.8, Wide);
        if (calm != null) Add("g2_calm", "", calm, 0);
        if (swift != null) Add("g2_swift", "", swift, 0);
        if (fierce != null) Add("g2_fierce", "", fierce, 0);
        places.Add(torrent);
        if (landing != null) Add("g2_landing", "", landing, 0);
        if (wade != null) Add("g2_wade", "", wade, 0);
        foreach (string prefix in new[] { "g2_calm", "g2_swift", "g2_fierce", "g2_landing", "g2_wade" }) if (!places.Any(p => p.Prefix == prefix)) misses.Add(prefix);

        // 8. head: a source stem (no upstream lake) of 5 km or more, at s = 750 m.
        Place? head = null; double[]? headPoint = null;
        {
            var stems = new List<(double D, int Stem)>();
            for (int stem = 0; stem < _net.Stems.Length; stem++)
            {
                if (!_net.IsSource(stem) || _net.Curves[stem].Length < 5000d) continue;
                _net.Centre(stem, 750d, out double a, out double z, out _, out _);
                stems.Add((Ring(a, z, torrent.CentreA, torrent.CentreZ), stem));
            }
            stems.Sort((x, y) => x.D != y.D ? x.D.CompareTo(y.D) : x.Stem.CompareTo(y.Stem));
            int n = 0;
            foreach ((double _, int stem) in stems)
            {
                n++;
                int span = t.SpanAt(_net.ReachAt(stem, 750d), 750d);
                if (_excluded != null && _excluded[span]) continue;
                Place? p = EvaluateRiver(stem, 750d);
                if (p == null) continue;
                head = p;
                _net.Centre(stem, 0d, out double ha, out double hz, out _, out _);
                headPoint = new[] { _net.WrapAround(ha), hz };
                break;
            }
            if (head == null) { Say($"NO CANDIDATE g2_head: {n} source stems tried ({stems.Count} of 5 km or more)"); misses.Add("g2_head"); }
            else
            {
                head.Prefix = "g2_head"; head.Dist = Ring(head.CentreA, head.CentreZ, torrent.CentreA, torrent.CentreZ);
                head.Detail = $"head point s=0 at ARC {F(headPoint![0] / 1000d, "F3")} km ACROSS {F(headPoint[1] / 1000d, "F3")} km; stem length {F(_net.Curves[head.Stem].Length, "F0")} m";
                // Centre of the head taper (s 0 to 1500 m) for the top view.
                _net.Centre(head.Stem, 750d, out double ta, out double tz, out _, out _);
                head.TargetA = ta; head.TargetZ = tz;
                places.Add(head);
            }
        }

        // 7. inlet, 9. lake.
        var lakeRuns = new Dictionary<int, List<LakeRun>>();
        foreach (LakeRun run in graph.Runs) { if (!lakeRuns.TryGetValue(run.LakeId, out var l)) lakeRuns[run.LakeId] = l = new List<LakeRun>(); l.Add(run); }
        double cell = graph.AroundStepMeters * graph.AcrossStepMeters;
        double CellA(LakeRun r, double dc = 0.5) => (r.Col + dc) * graph.AroundStepMeters;
        double CellZ(LakeRun r) => (r.Row + 0.5) * graph.AcrossStepMeters;
        double LakeDist(int lake)
        {
            RiverLake L = graph.Lakes[lake];
            return Ring((L.DeepestCol + 0.5) * graph.AroundStepMeters, (L.DeepestRow + 0.5) * graph.AcrossStepMeters, torrent.CentreA, torrent.CentreZ);
        }
        Place? inlet = null;
        {
            int n = 0;
            foreach (int lake in Enumerable.Range(0, graph.Lakes.Length).Where(l => graph.Lakes[l].CellCount * cell >= 1e6).OrderBy(LakeDist))
            {
                RiverLake L = graph.Lakes[lake];
                for (int k = 0; k < L.InletCount && inlet == null; k++)
                {
                    RiverInlet inl = graph.Inlets[L.InletStart + k];
                    double s0 = Math.Max(0d, inl.S - 300d);
                    _net.ChannelAt(inl.Stem, s0, out RiverChannel ch);
                    if (2d * ch.HalfWidth < 15d) continue;
                    if (inl.Stem < _net.InLakeEnd.Length && _net.InLakeEnd[inl.Stem] > 0d && inl.S <= _net.InLakeEnd[inl.Stem] + 1e-3) continue;
                    int span = t.SpanAt(_net.ReachAt(inl.Stem, s0), s0);
                    if (_excluded != null && _excluded[span]) continue;
                    n++;
                    inlet = EvaluateInlet(inl, lake, ch, span);
                }
                if (inlet != null) break;
            }
            if (inlet == null) { Say($"NO CANDIDATE g2_inlet: {n} inlets of 15 m or more into lakes of 1 km^2 or more tried"); misses.Add("g2_inlet"); }
            else { inlet.Prefix = "g2_inlet"; inlet.Dist = Ring(inlet.CentreA, inlet.CentreZ, torrent.CentreA, torrent.CentreZ); places.Add(inlet); }
        }
        Place? lakePlace = null;
        {
            int n = 0;
            foreach (int lake in Enumerable.Range(0, graph.Lakes.Length).Where(l => graph.Lakes[l].CellCount * cell >= 5e6).OrderBy(LakeDist))
            {
                var runs = lakeRuns.TryGetValue(lake, out var list) ? list : new List<LakeRun>();
                if (runs.Count == 0) continue;
                // not a lake touching a keep-out: 100 runs spread over the lake must all be clear of the exclusion zones.
                bool clear = true;
                for (int i = 0; i < runs.Count && clear; i += Math.Max(1, runs.Count / 100))
                    if (_zones != null && _zones.Clearance(CellA(runs[i]), CellZ(runs[i])) <= 0d) clear = false;
                if (!clear) continue;
                n++;
                foreach (LakeRun run in runs.OrderBy(r => RiverHash.Mix(_seed, r.Row, r.Col)).Take(200))
                {
                    foreach (double side in new[] { -1d, 1d })
                    {
                        double a0 = side < 0 ? run.Col * graph.AroundStepMeters + 0.5 * graph.AroundStepMeters * 0.5 : (run.Col + run.Length - 0.25) * graph.AroundStepMeters, z0 = CellZ(run);
                        lakePlace = EvaluateLakeShore(lake, a0, z0, side);
                        if (lakePlace != null) break;
                    }
                    if (lakePlace != null) break;
                }
                if (lakePlace != null) break;
            }
            if (lakePlace == null) { Say($"NO CANDIDATE t2r16_lake: {n} clear lakes of 5 km^2 or more tried"); misses.Add("t2r16_lake"); }
            else { lakePlace.Prefix = "t2r16_lake"; lakePlace.Dist = Ring(lakePlace.CentreA, lakePlace.CentreZ, torrent.CentreA, torrent.CentreZ); places.Add(lakePlace); }
        }

        // Names, summaries.
        foreach (Place p in places)
            p.Name = p.Lake >= 0 ? $"{p.Prefix}_lake{p.Lake}" : $"{p.Prefix}_stem{p.Stem}";
        string[] order = { "g2_calm", "g2_swift", "g2_fierce", "g2_torrent", "g2_landing", "g2_wade", "g2_inlet", "g2_head", "t2r16_lake" };
        places = places.OrderBy(p => Array.IndexOf(order, p.Prefix)).ToList();
        foreach (Place p in places)
            Say($"place {p.Name}: ARC {F(p.StandA / 1000d, "F3")} km ACROSS {F(p.StandZ / 1000d, "F3")} km ground {F(p.Ground)} m yaw {F(p.Yaw)} class {p.Class} width {F(p.Width)} m centre depth {F(p.DepthC, "F2")} m (quarters {F(p.DepthQ1, "F2")} / {F(p.DepthQ3, "F2")}) danger {F(p.Danger, "F2")} m/s Q {F(p.Q, "F2")} landing {F(p.Landing, "F2")} stem {p.Stem} s {F(p.S, "F0")} distance to torrent {F(p.Dist / 1000d, "F2")} km {p.Detail}");

        Dictionary<string, object>? route = FindRoute(torrent, out string routeText);
        Say(routeText);
        if (route == null) misses.Add("route");

        WritePlaces(places, Path.Combine(outDir, "places.json"));
        if (route != null) File.WriteAllText(Path.Combine(outDir, "route.json"), JsonSerializer.Serialize(route, JsonOptions));
        WriteBookmarks(places, headPoint, route, routeText, Path.Combine(outDir, "bookmarks.md"));
        Say($"wrote {Path.Combine(outDir, "places.json")}, {(route != null ? Path.Combine(outDir, "route.json") : "(no route.json)")} and {Path.Combine(outDir, "bookmarks.md")}");
        if (misses.Count > 0) { Say("missing: " + string.Join(", ", misses)); return Fail(); }
        Console.WriteLine("RIVER_REVIEW_PLACES=PASS");
        return 0;
    }

    private static int Fail() { Console.Error.WriteLine("RIVER_REVIEW_PLACES=FAIL"); return 1; }

    private Place? EvaluateInlet(RiverInlet inl, int lake, RiverChannel ch, int span)
    {
        // The mouth: walk upstream from the inlet's end until the sample is River.
        double sm = -1d, ma = 0d, mz = 0d;
        for (double s = inl.S; s >= Math.Max(0d, inl.S - 800d); s -= 10d)
        {
            _net.Centre(inl.Stem, s, out double a, out double z, out _, out _);
            if (Sample(a, z).WaterKind == SurfaceWaterKind.River) { sm = s; ma = a; mz = z; break; }
        }
        if (sm < 0d || mz < MarginM || mz > Wid - MarginM) return null;
        Place? best = null; double bestShore = double.MaxValue;
        for (int k = 0; k < 16; k++)
        {
            double dx = Math.Cos(k * Math.PI / 8d), dz = Math.Sin(k * Math.PI / 8d);
            SurfaceWaterKind previous = SurfaceWaterKind.River;
            for (double r = 5d; r <= 400d; r += 5d)
            {
                SurfaceWaterKind kind = Sample(ma + dx * r, mz + dz * r).WaterKind;
                if (kind == SurfaceWaterKind.None)
                {
                    if (previous == SurfaceWaterKind.Lake && r <= 150d && r < bestShore)
                    {
                        double sa = _net.WrapAround(ma + dx * (r + BankStandOffM)), sz = mz + dz * (r + BankStandOffM);
                        if (sz >= MarginM && sz <= Wid - MarginM && Sample(sa, sz).WaterKind == SurfaceWaterKind.None && _model.CalculateHeight(sa, sz) > 8d)
                        {
                            _net.Centre(inl.Stem, sm, out _, out _, out double tx, out double ty);
                            best = new Place
                            {
                                Stem = inl.Stem, Span = span, Lake = lake, S = sm, CentreA = ma, CentreZ = mz, StandA = sa, StandZ = sz, Width = 2d * ch.HalfWidth,
                                Yaw = Deg(Math.Atan2(mz - sz, _net.WrapDelta(ma - sa))), AlongYaw = Deg(Math.Atan2(ty, tx)), Class = _table.Class[span].ToString(), Danger = _table.DangerSpeed[span], Q = _table.Spans[span].Q, Landing = _table.Spans[span].Landing,
                                Ground = Math.Round(Presentation(sa, sz), 1), BedC = Presentation(ma, mz), DepthC = RiverDepth(ma, mz), TargetA = ma, TargetZ = mz,
                                Detail = $"enters lake {lake} ({F(graph0Area(lake), "F1")} km^2), shore {F(r)} m from the mouth",
                            };
                            bestShore = r;
                        }
                    }
                    break;
                }
                if (kind == SurfaceWaterKind.Ocean) break;
                previous = kind;
            }
        }
        return best;
    }

    private double graph0Area(int lake) => _graph.Lakes[lake].CellCount * _graph.AroundStepMeters * _graph.AcrossStepMeters / 1e6;

    private Place? EvaluateLakeShore(int lake, double a0, double z0, double side)
    {
        if (z0 < MarginM || z0 > Wid - MarginM) return null;
        if (Sample(a0, z0).WaterKind != SurfaceWaterKind.Lake) return null;
        for (double d = 5d; d <= 1200d; d += 5d)
        {
            double a = a0 + side * d;
            SurfaceWaterKind kind = Sample(a, z0).WaterKind;
            if (kind == SurfaceWaterKind.Lake) continue;
            if (kind != SurfaceWaterKind.None) return null;
            double sa = _net.WrapAround(a0 + side * (d + BankStandOffM));
            if (Sample(sa, z0).WaterKind != SurfaceWaterKind.None || _model.CalculateHeight(sa, z0) <= 8d) return null;
            if (_zones != null && _zones.Clearance(sa, z0) <= 0d) return null;
            // Out over the lake: look back toward the water (opposite of the walk).
            double yaw = side > 0 ? 180d : 0d;
            RiverGraph g = _graph;
            return new Place
            {
                Lake = lake, CentreA = a0, CentreZ = z0, StandA = sa, StandZ = z0, Yaw = yaw, AlongYaw = yaw, Class = "lake", Ground = Math.Round(Presentation(sa, z0), 1),
                TargetA = _net.WrapAround(sa - side * 800d), TargetZ = z0,
                Detail = $"lake {lake}, {F(g.Lakes[lake].CellCount * g.AroundStepMeters * g.AcrossStepMeters / 1e6, "F1")} km^2, level {F(g.Lakes[lake].Level, "F0")} m, shore {F(d)} m from the water sample",
            };
        }
        return null;
    }

    // ---------------------------------------------------------------- route (T2R-14)

    // Offsets 0 to +-1,500 m decide; the farther ones (to 10 km) only report how near a miss is.
    private static readonly double[] LakeOffsets = Enumerable.Range(0, 21).SelectMany(k => k == 0 ? new[] { 0d } : new[] { -500d * k, 500d * k }).ToArray();

    private Dictionary<string, object>? FindRoute(Place torrent, out string text)
    {
        const double Length = 10_000d, Step = 10d, Window = 300_000d;
        int perLine = (int)(Length / Step) + 1;
        double originA = Math.Round(torrent.CentreA / 2000d) * 2000d;
        double firstA = originA - Window;
        int cols = (int)((2d * Window + Length) / Step) + 1;
        var rows = new Dictionary<int, (bool[] Land, bool[] River)>();
        (bool[] Land, bool[] River) Row(int zIndex)
        {
            if (rows.TryGetValue(zIndex, out var r)) return r;
            double z = zIndex * 4000d;
            var land = new bool[cols]; var river = new bool[cols];
            for (int c = 0; c < cols; c++)
            {
                TerrainSample s = Sample(firstA + c * Step, z);
                land[c] = s.WaterKind != SurfaceWaterKind.Ocean && s.WaterKind != SurfaceWaterKind.Lake;
                river[c] = s.WaterKind == SurfaceWaterKind.River;
            }
            return rows[zIndex] = (land, river);
        }
        var lakeCache = new Dictionary<(long, int), bool>();
        bool LakeAt(double a, double z)
        {
            var key = ((long)Math.Round(_net.WrapAround(a) / 250d), (int)Math.Round(z / 500d));
            if (lakeCache.TryGetValue(key, out bool v)) return v;
            return lakeCache[key] = Sample(a, z).WaterKind == SurfaceWaterKind.Lake;
        }
        // Candidate: (crossings, lake distance, distance to torrent place); better = more crossings, then nearer lake, then nearer torrent place.
        (double A, double Z, List<(double A, double W)> Cross, List<(double A, double W)> Along, double Lake, double Dist)? best = null, bestAny = null;
        bool Better(int n, double lake, double dist, (double A, double Z, List<(double A, double W)> Cross, List<(double A, double W)> Along, double Lake, double Dist)? cur)
            => cur is not { } c || n > c.Cross.Count || (n == c.Cross.Count && (lake < c.Lake || (lake == c.Lake && dist < c.Dist)));
        int evaluated = 0, landOk = 0, threeCross = 0, wideCross = 0, lakeOk = 0;
        for (int zk = (int)Math.Ceiling(MarginM / 4000d); zk * 4000d <= Wid - MarginM; zk++)
        {
            double z0 = zk * 4000d;
            for (int ai = -(int)(Window / 2000d); ai <= (int)(Window / 2000d); ai++)
            {
                double a0 = originA + ai * 2000d;
                int startCol = (int)Math.Round((a0 - firstA) / Step);
                var (land, river) = Row(zk);
                evaluated++;
                bool ok = true;
                for (int c = 0; c < perLine && ok; c += 5) if (!land[startCol + c]) ok = false;
                if (!ok) continue;
                landOk++;
                var cross = new List<(double A, double W)>(); var along = new List<(double A, double W)>();
                int run = 0;
                for (int c = 0; c <= perLine; c++)
                {
                    bool isRiver = c < perLine && river[startCol + c];
                    if (isRiver) run++;
                    else if (run > 0)
                    {
                        (double A, double W) item = (_net.WrapAround(a0 + (c - run * 0.5) * Step), run * Step);
                        (item.W <= 200d ? cross : along).Add(item);
                        run = 0;
                    }
                }
                if (cross.Count < 3) continue;
                threeCross++;
                if (!cross.Any(x => x.W >= 20d)) continue;
                wideCross++;
                double lake = double.MaxValue;
                for (double alongM = 0d; alongM <= Length; alongM += 250d)
                    foreach (double off in LakeOffsets)
                        if (Math.Abs(off) < lake && LakeAt(a0 + alongM, z0 + off)) lake = Math.Abs(off);
                double dist = Ring(a0 + 5000d, z0, torrent.CentreA, torrent.CentreZ);
                if (Better(cross.Count, lake, dist, bestAny)) bestAny = (_net.WrapAround(a0), z0, cross, along, lake, dist);
                if (lake > 1500d) continue;
                lakeOk++;
                if (Better(cross.Count, lake, dist, best)) best = (_net.WrapAround(a0), z0, cross, along, lake, dist);
            }
        }
        bool met = best != null;
        var chosen = best ?? bestAny;
        string stats = $"{evaluated} origins, {landOk} all-land lines, {threeCross} with 3 or more crossings (10 to 200 m runs), {wideCross} of those with one of 20 m or more, {lakeOk} of those with a lake within 1,500 m";
        if (chosen is not { } c0)
        {
            text = "NO CANDIDATE route: " + stats;
            return null;
        }
        var route = new Dictionary<string, object>
        {
            ["origin_around_m"] = Math.Round(c0.A, 1), ["origin_across_m"] = Math.Round(c0.Z, 1), ["length_m"] = Length, ["direction"] = "+around", ["lake_rule_met"] = met,
            ["crossings"] = c0.Cross.Select(x => new Dictionary<string, object> { ["around_m"] = Math.Round(x.A, 1), ["width_m"] = Math.Round(x.W, 1) }).ToList(),
            ["along_runs"] = c0.Along.Select(x => new Dictionary<string, object> { ["around_m"] = Math.Round(x.A, 1), ["length_m"] = Math.Round(x.W, 1) }).ToList(),
            ["nearest_lake_m"] = c0.Lake == double.MaxValue ? "none within 10 km" : c0.Lake, ["distance_to_torrent_place_km"] = Math.Round(c0.Dist / 1000d, 2),
            ["note"] = "land checked every 10 m (Ocean and Lake rejected); crossings are River runs of 10 to 200 m at 10 m resolution (width = run length), longer runs are along-runs; origins within 300 km of the torrent place every 2 km around and 4 km across; lake search every 250 m along the line at across offsets 0, +-500 .. +-10,000 m (the rule is 1,500 m)",
        };
        text = $"route{(met ? "" : " (LAKE RULE NOT MET, best line ignoring it)")}: origin ARC {F(c0.A / 1000d, "F3")} km ACROSS {F(c0.Z / 1000d, "F3")} km, +around 10,000 m; {c0.Cross.Count} crossings: {string.Join(", ", c0.Cross.Select(x => $"{F(x.A / 1000d, "F3")} km/{F(x.W, "F0")} m"))}; along runs: {(c0.Along.Count == 0 ? "none" : string.Join(", ", c0.Along.Select(x => $"{F(x.A / 1000d, "F3")} km/{F(x.W, "F0")} m")))}; nearest lake {(c0.Lake == double.MaxValue ? "none within 10 km" : F(c0.Lake, "F0") + " m")}; line midpoint {F(c0.Dist / 1000d, "F1")} km from the torrent place ({stats})";
        return route;
    }

    // ---------------------------------------------------------------- output

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static double R(double v, int digits = 1) => Math.Round(v, digits);

    private Dictionary<string, object> View(string tag, double lift, double yaw, double pitch, double offA = 0d, double offZ = 0d, bool ambience = false, string[]? variants = null, Dictionary<string, object>? time = null)
    {
        var v = new Dictionary<string, object> { ["tag"] = tag, ["lift_m"] = lift, ["yaw"] = R(yaw), ["pitch"] = pitch };
        if (offA != 0d || offZ != 0d) { v["offset_around_m"] = R(offA); v["offset_across_m"] = R(offZ); }
        if (ambience) v["ambience"] = true;
        if (variants != null) v["variants"] = variants;
        if (time != null) v["time"] = time;
        return v;
    }

    /// <summary>An overhead view of <paramref name="p"/>'s target: the camera backs off along the look direction by lift / tan(|pitch|) so the target is in the middle of the frame.</summary>
    private Dictionary<string, object> Air(Place p, string tag, double lift, double pitch, double yaw, string[]? variants, Dictionary<string, object>? time = null)
    {
        double back = lift / Math.Tan(Math.Abs(pitch) * Math.PI / 180d);
        double dirA = Math.Cos(yaw * Math.PI / 180d), dirZ = Math.Sin(yaw * Math.PI / 180d);
        double offA = _net.WrapDelta(p.TargetA - p.StandA) - back * dirA, offZ = p.TargetZ - p.StandZ - back * dirZ;
        return View(tag, lift, yaw, pitch, offA, offZ, false, variants, time);
    }

    private void WritePlaces(List<Place> places, string path)
    {
        var list = new List<Dictionary<string, object>>();
        foreach (Place p in places)
        {
            var views = new List<Dictionary<string, object>> { View("bank", 1.7, p.Yaw, -8d, ambience: true) };
            bool lake = p.Prefix == "t2r16_lake";
            double airYaw = lake ? p.Yaw : p.AlongYaw;
            var low = new Dictionary<string, object> { ["mode"] = "elevation", ["value"] = 8.0, ["rising"] = false };
            if (p.Prefix is "g2_calm" or "g2_torrent" or "t2r16_lake")
            {
                views.Add(Air(p, "air2km", 2000d, -55d, airYaw, new[] { "levels" }));
                views.Add(Air(p, "air18km", 18000d, -60d, airYaw, new[] { "levels" }));
            }
            if (p.Prefix is "g2_calm" or "t2r16_lake")
            {
                views.Add(View("bank_lowsun", 1.7, p.Yaw, -8d, ambience: true, time: low));
                views.Add(Air(p, "air2km_lowsun", 2000d, -55d, airYaw, new[] { "levels" }, low));
            }
            if (p.Prefix == "g2_wade")
                views.Add(View("instream", p.BedC + 1.55 - p.Ground, p.AlongYaw, -10d, _net.WrapDelta(p.CentreA - p.StandA), p.CentreZ - p.StandZ));
            if (p.Prefix is "g2_head" or "g2_inlet")
                views.Add(Air(p, "top600", 600d, -89d, p.AlongYaw, null));
            list.Add(new Dictionary<string, object>
            {
                ["name"] = p.Name, ["caption"] = $"{p.Name} ({(p.Lake >= 0 && p.Prefix == "t2r16_lake" ? "lake" : p.Class.ToLowerInvariant())}, {R(p.Width, 0)} m)",
                ["around_km"] = Math.Round(_net.WrapAround(p.StandA) / 1000d, 3), ["across_km"] = Math.Round(p.StandZ / 1000d, 3), ["ground_m"] = p.Ground, ["views"] = views,
            });
        }
        File.WriteAllText(path, JsonSerializer.Serialize(list, JsonOptions));
    }

    private void WriteBookmarks(List<Place> places, double[]? headPoint, Dictionary<string, object>? route, string routeText, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Rivers review G2 bookmarks (review-rivers-s{_seed}: seed {_seed}, generation 1, review feature water.rivers)");
        sb.AppendLine();
        sb.AppendLine("Teleport with the grave menu, Travel to coordinates. ARC and ACROSS are the standing point (7 m beyond the water's edge, on the bank); yaw is the look direction of the bank view, degrees, 0 = +around, 90 = +across.");
        sb.AppendLine();
        sb.AppendLine("| place | ARC km | ACROSS km | ground m | yaw | class | width m | centre depth m | danger m/s | Q | landing | to torrent km |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (Place p in places)
            sb.AppendLine($"| {p.Name} | {F(_net.WrapAround(p.StandA) / 1000d, "F3")} | {F(p.StandZ / 1000d, "F3")} | {F(p.Ground)} | {F(p.Yaw, "F0")} | {p.Class.ToLowerInvariant()} | {F(p.Width)} | {(double.IsNaN(p.DepthC) ? "-" : F(p.DepthC, "F2"))} | {(p.Prefix == "t2r16_lake" ? "-" : F(p.Danger, "F2"))} | {(p.Prefix == "t2r16_lake" ? "-" : F(p.Q, "F2"))} | {(p.Prefix == "t2r16_lake" ? "-" : F(p.Landing, "F2"))} | {F(p.Dist / 1000d, "F2")} |");
        sb.AppendLine();
        foreach (Place p in places.Where(p => p.Detail.Length > 0)) sb.AppendLine($"- {p.Name}: {p.Detail}");
        Place? wade = places.FirstOrDefault(p => p.Prefix == "g2_wade");
        if (wade != null) sb.AppendLine($"- g2_wade depths (water surface minus bed, m): centre {F(wade.DepthC, "F2")}, quarter points {F(wade.DepthQ1, "F2")} and {F(wade.DepthQ3, "F2")}; swim enters at 1.15 m, exits under 0.9 m");
        if (headPoint != null) sb.AppendLine($"- g2_head point (s = 0): ARC {F(headPoint[0] / 1000d, "F3")} km, ACROSS {F(headPoint[1] / 1000d, "F3")} km");
        sb.AppendLine();
        sb.AppendLine("## Benchmark route (T2R-14)");
        sb.AppendLine();
        sb.AppendLine(routeText);
        File.WriteAllText(path, sb.ToString());
    }
}
