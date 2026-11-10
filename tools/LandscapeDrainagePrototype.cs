using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Generation;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Offline prototype of the plan section 3.1 drainage bake (docs/landscape-audit-t0-spec.md WO T0-3). Samples a strip of the pinned generation 1 world on a regular grid,
/// routes flow with priority-flood plus flow directions, accumulates catchment and runoff, derives per-reach hydraulics and danger classes, and writes tables and river maps
/// to .godot/audit_baseline/common/t0/drainage/&lt;stamp&gt;/. Run via tools/landscape_drainage_prototype.gd. Arguments (after `--`) are listed in the spec; --time-limit=&lt;s&gt; (default 180) is the run-time acceptance limit.
/// Routing height: natural height (Clamp(Macro + Detail)) for land; outlet cells (shaped height below 0, including the Weather Lung basin) use min(natural, shaped) so the basin drains.
/// Confinement is not modelled: every channel cell takes the open-channel relation on its own gradient with no valley-width squeeze.
/// </summary>
public partial class LandscapeDrainagePrototype : RefCounted
{
    const byte TLand = 0, TOcean = 1, TLung = 2, TBarrier = 3, TEdge = 4;
    const double StepDiag = 1.4142135623730951;
    static readonly string[] ClassNames = { "calm", "swift", "fierce", "torrent" };
    static readonly string[] EndNames = { "enclosed/none", "ocean", "weather lung", "barrier", "strip edge" };

    sealed class Options
    {
        public double StripStart = double.NaN;
        public int StripKm = 2000;
        public int[] Cells = { 1000, 500 };
        public double[] Breach = { 5, 15, 40 };
        public double[] Thresholds = { 25, 50, 100 };
        public double Runoff = 0.02, N = 0.035, DepthCoef = 0.27, DepthExp = 0.4, GradientKm = 5, TimeLimit = 180, ShadeExaggeration = 6;
    }

    sealed class Grid
    {
        public int Cell, NA, NJ;
        public double Start;
        public double[] H = Array.Empty<double>();      // routing height
        public double[] Nat = Array.Empty<double>();    // natural height (hillshade)
        public float[] Shaped = Array.Empty<float>();
        public float[] Moist = Array.Empty<float>();
        public byte[] Type = Array.Empty<byte>();
        public int Count => NA * NJ;
    }

    sealed class Flood
    {
        public double[] Filled = Array.Empty<double>();
        public int[] Down = Array.Empty<int>();
        public int[] Order = Array.Empty<int>();
        public int OrderCount;
        public bool[] Visited = Array.Empty<bool>();
        public double[] Acc = Array.Empty<double>();    // catchment km^2
        public double[] Q = Array.Empty<double>();      // m^3/s at the default runoff coefficient
        public byte[] Term = Array.Empty<byte>();
        public ulong DownHash;
    }

    sealed class Depression { public int Cells; public double MaxDepth, Spill; public double AreaKm2; }

    sealed class TimeSplit { public double Sampling, FloodSec, Accum, Hydraulics, Stats; public double BakeTotal => Sampling + FloodSec + Accum + Hydraulics; }

    sealed class Comparer : IComparer<(double H, int I)>
    {
        public int Compare((double H, int I) a, (double H, int I) b) { int c = a.H.CompareTo(b.H); return c != 0 ? c : a.I.CompareTo(b.I); }
    }

    static string Arg(string prefix)
    {
        foreach (string arg in OS.GetCmdlineUserArgs()) if (arg.StartsWith(prefix, StringComparison.Ordinal)) return arg[prefix.Length..];
        return "";
    }

    static double[] ParseList(string text, double[] fallback)
        => string.IsNullOrWhiteSpace(text) ? fallback : text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    static double ParseD(string text, double fallback) => string.IsNullOrWhiteSpace(text) ? fallback : double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    static Options ParseOptions()
    {
        var o = new Options();
        o.StripStart = ParseD(Arg("--strip-start="), double.NaN);
        o.StripKm = (int)ParseD(Arg("--strip-km="), o.StripKm);
        o.Cells = ParseList(Arg("--cells="), new double[] { 1000, 500 }).Select(v => (int)v).ToArray();
        o.Breach = ParseList(Arg("--breach="), o.Breach);
        o.Thresholds = ParseList(Arg("--thresholds="), o.Thresholds);
        o.Runoff = ParseD(Arg("--runoff="), o.Runoff);
        o.N = ParseD(Arg("--n="), o.N);
        o.DepthCoef = ParseD(Arg("--depth-coef="), o.DepthCoef);
        o.DepthExp = ParseD(Arg("--depth-exp="), o.DepthExp);
        o.GradientKm = ParseD(Arg("--gradient-km="), o.GradientKm);
        o.TimeLimit = ParseD(Arg("--time-limit="), o.TimeLimit);
        return o;
    }

    public bool Run()
    {
        var failures = new List<string>();
        var total = Stopwatch.StartNew();
        try { RunInternal(failures, total); }
        catch (Exception exception) { failures.Add(exception.ToString()); }
        foreach (string failure in failures) GD.PushError("LANDSCAPE_DRAINAGE=FAIL " + failure);
        return failures.Count == 0;
    }

    void RunInternal(List<string> failures, Stopwatch total)
    {
        Options o = ParseOptions();
        int threads = LandscapeWorld.DefaultWorkerCount;
        LandscapeWorld.Worker[] workers = LandscapeWorld.CreateWorkers(threads);
        try
        {
            var clock = Stopwatch.StartNew();
            double start = double.IsNaN(o.StripStart) ? AutoStrip(o, workers, out string scanNote) : o.StripStart;
            string stripNote = double.IsNaN(o.StripStart) ? "auto (most land of 16 windows, pre-scan at 4 km)" : "given";
            double scanSeconds = clock.Elapsed.TotalSeconds;
            GD.Print($"LANDSCAPE_DRAINAGE_STRIP start={LandscapeWorld.Num(start)} km={o.StripKm} ({stripNote}) prescan={scanSeconds:F1}s threads={threads}");

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outDir = ProjectSettings.GlobalizePath("res://.godot/audit_baseline/common/t0/drainage/" + stamp);
            Directory.CreateDirectory(outDir);

            double savedPose = 27_984_760.943;
            bool inside = savedPose >= start && savedPose < start + o.StripKm * 1000.0;
            var summaries = new List<string>();

            foreach (int cell in o.Cells)
            {
                RunCell(o, workers, threads, start, cell, stripNote, scanSeconds, inside, outDir, failures, summaries, cell == o.Cells[0]);
            }

            double elapsed = total.Elapsed.TotalSeconds;
            GD.Print($"LANDSCAPE_DRAINAGE_TOTAL seconds={elapsed:F1} limit={o.TimeLimit:F0} out={outDir}");
            if (elapsed > o.TimeLimit) failures.Add($"run took {elapsed:F1} s, over the {o.TimeLimit:F0} s limit");
            foreach (string s in summaries) GD.Print(s);
        }
        finally { foreach (LandscapeWorld.Worker w in workers) w.Dispose(); }
    }

    // ---------------------------------------------------------------- strip choice

    static double AutoStrip(Options o, LandscapeWorld.Worker[] workers, out string note)
    {
        const int cell = 4000;
        int rows = (int)(LandscapeWorld.Circumference / cell);
        int cols = LandscapeWorld.Width / cell;
        var land = new int[rows];
        int next = -1;
        Parallel.For(0, workers.Length, new ParallelOptions { MaxDegreeOfParallelism = workers.Length }, wi =>
        {
            LandscapeWorld.Worker w = workers[wi];
            while (true)
            {
                int r = Interlocked.Increment(ref next);
                if (r >= rows) break;
                int n = 0;
                for (int j = 0; j < cols; j++)
                    if (w.Diag((r + 0.5) * cell, (j + 0.5) * cell).Height >= 0d) n++;
                land[r] = n;
            }
        });
        int win = o.StripKm * 1000 / cell;
        var prefix = new long[rows + 1];
        for (int r = 0; r < rows; r++) prefix[r + 1] = prefix[r] + land[r];
        long best = -1; int bestRow = 0;
        var report = new StringBuilder();
        for (int k = 0; k < 16; k++)
        {
            int row = (int)Math.Round(k * (rows - win) / 15.0);   // evenly spaced and never wrapping past the ring seam
            long sum = prefix[row + win] - prefix[row];
            report.Append($"{k}:{row * (long)cell}:{sum} ");
            if (sum > best) { best = sum; bestRow = row; }
        }
        note = report.ToString();
        GD.Print("LANDSCAPE_DRAINAGE_WINDOWS (index:start_m:land_cells_at_4km) " + note);
        return bestRow * (double)cell;
    }

    // ---------------------------------------------------------------- per cell size

    void RunCell(Options o, LandscapeWorld.Worker[] workers, int threads, double start, int cell, string stripNote, double scanSeconds, bool inside, string outDir,
        List<string> failures, List<string> summaries, bool first)
    {
        var time = new TimeSplit();
        var sw = Stopwatch.StartNew();
        Grid g = SampleGrid(o, workers, start, cell);
        time.Sampling = sw.Elapsed.TotalSeconds;

        Flood f = BakeFlow(g, o, time);
        string label = cell + "m";
        // determinism: bake the 1 km grid again in-process (flood and accumulation from the same samples, then a full resample for the first cell size)
        bool deterministic = true;
        ulong secondHash = 0;
        if (cell == 1000 || first)
        {
            var scratch = new TimeSplit();
            Grid g2 = SampleGrid(o, workers, start, cell);
            Flood f2 = BakeFlow(g2, o, scratch);
            secondHash = f2.DownHash;
            deterministic = f2.DownHash == f.DownHash;
        }
        if (!deterministic) failures.Add($"{label}: two bakes gave different flow-direction hashes");

        // invariants
        Invariants(g, f, o, label, failures);

        // lakes and depressions
        var depressions = new List<Depression>();
        int[] comp = FindDepressions(g, f, depressions);
        double[] breach = o.Breach.OrderBy(v => v).ToArray();
        double midBreach = breach[breach.Length / 2];
        var lake = new bool[g.Count];
        for (int c = 0; c < g.Count; c++) if (comp[c] >= 0 && depressions[comp[c]].MaxDepth > midBreach) lake[c] = true;

        sw.Restart();
        Hydro hy = Hydraulics(g, f, o, lake, o.Thresholds.Min(), o.N, o.DepthCoef, o.Runoff, time);
        time.Hydraulics = sw.Elapsed.TotalSeconds;

        sw.Restart();
        Dictionary<string, object> json = Statistics(g, f, o, depressions, lake, midBreach, hy, time, threads, start, stripNote, scanSeconds, inside, deterministic, secondHash, out string md, out double[] classShareSf50, out double[] classShareSo50, out double endEdge, out double enclosedShare);
        time.Stats = sw.Elapsed.TotalSeconds;
        // refresh timing section now that statistics time is known
        json["timing"] = TimingJson(time, o.StripKm, threads);
        md = md.Replace("{{TIMING}}", TimingMd(time, o.StripKm, threads));

        File.WriteAllText(Path.Combine(outDir, $"drainage_{label}.json"), JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(outDir, $"drainage_{label}.md"), md);
        WriteMaps(g, f, o, lake, hy, outDir, label);

        // escalation conditions (spec): reported, not asserted
        string flags = "";
        if (enclosedShare > 0.20) flags += " ESCALATE:enclosed>20%";
        if (endEdge > 0.15) flags += " ESCALATE:edge-ends>15%";
        if (classShareSf50.Max() > 0.9 && classShareSo50.Max() > 0.9) flags += " ESCALATE:single-class>90%-both";
        summaries.Add($"LANDSCAPE_DRAINAGE_CELL {label} bake={time.BakeTotal:F1}s sampling={time.Sampling:F1}s flood={time.FloodSec:F1}s accum={time.Accum:F1}s hydro={time.Hydraulics:F1}s stats={time.Stats:F1}s"
            + $" enclosed={enclosedShare * 100:F1}% edge_end={endEdge * 100:F1}% Sf_len={Fmt(classShareSf50)} So_len={Fmt(classShareSo50)}{flags}");
    }

    static string Fmt(double[] shares) => string.Join("/", shares.Select(v => (v * 100).ToString("F1")));

    // ---------------------------------------------------------------- sampling

    static Grid SampleGrid(Options o, LandscapeWorld.Worker[] workers, double start, int cell)
    {
        var g = new Grid { Cell = cell, Start = start, NA = (int)(o.StripKm * 1000L / cell), NJ = LandscapeWorld.Width / cell };
        int n = g.Count;
        g.H = new double[n]; g.Nat = new double[n]; g.Shaped = new float[n]; g.Moist = new float[n]; g.Type = new byte[n];
        int next = -1;
        Parallel.For(0, workers.Length, new ParallelOptions { MaxDegreeOfParallelism = workers.Length }, wi =>
        {
            LandscapeWorld.Worker w = workers[wi];
            while (true)
            {
                int j = Interlocked.Increment(ref next);
                if (j >= g.NJ) break;
                double across = (j + 0.5) * cell;
                for (int i = 0; i < g.NA; i++)
                {
                    double around = start + (i + 0.5) * cell;
                    TerrainGenerationHeightModel.Diagnostics d = w.Diag(around, across);
                    int idx = j * g.NA + i;
                    double nat = LandscapeWorld.NaturalHeight(d);
                    g.Nat[idx] = nat;
                    g.Shaped[idx] = (float)d.Height;
                    byte type;
                    if (d.Height < 0d) type = d.WeatherLungWeight > 0d ? TLung : TOcean;
                    else if (d.SiteWeight > 0d || d.WeatherLungWeight > 0d) type = TBarrier;
                    else if (i == 0 || i == g.NA - 1) type = TEdge;
                    else type = TLand;
                    g.Type[idx] = type;
                    g.H[idx] = (type == TOcean || type == TLung) ? Math.Min(nat, d.Height) : nat;
                    if (type == TLand || type == TEdge)
                        g.Moist[idx] = w.Sampler.GetTerrainSample((long)Math.Floor(around), (int)Math.Floor(across)).Moisture;
                }
            }
        });
        return g;
    }

    // ---------------------------------------------------------------- flood, accumulation

    static bool IsOutlet(byte t) => t == TOcean || t == TLung || t == TEdge;
    static bool IsLand(byte t) => t == TLand || t == TEdge;

    static readonly int[] Dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
    static readonly int[] Dy = { -1, -1, -1, 0, 0, 1, 1, 1 };

    static Flood BakeFlow(Grid g, Options o, TimeSplit time)
    {
        var sw = Stopwatch.StartNew();
        int n = g.Count, na = g.NA, nj = g.NJ;
        var f = new Flood { Filled = new double[n], Down = new int[n], Order = new int[n], Visited = new bool[n], Acc = new double[n], Q = new double[n], Term = new byte[n] };
        Array.Fill(f.Down, -1);
        var queue = new PriorityQueue<int, (double H, int I)>(new Comparer());
        for (int c = 0; c < n; c++)
        {
            if (!IsOutlet(g.Type[c])) continue;
            f.Visited[c] = true; f.Filled[c] = g.H[c];
            queue.Enqueue(c, (g.H[c], c));
        }
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            f.Order[f.OrderCount++] = c;
            int cx = c % na, cy = c / na;
            for (int k = 0; k < 8; k++)
            {
                int x = cx + Dx[k], y = cy + Dy[k];
                if (x < 0 || y < 0 || x >= na || y >= nj) continue;
                int m = y * na + x;
                if (f.Visited[m] || g.Type[m] == TBarrier) continue;
                f.Visited[m] = true;
                f.Filled[m] = Math.Max(g.H[m], f.Filled[c]);
                f.Down[m] = c;
                queue.Enqueue(m, (f.Filled[m], m));
            }
        }
        time.FloodSec = sw.Elapsed.TotalSeconds;

        sw.Restart();
        double cellKm2 = (g.Cell / 1000.0) * (g.Cell / 1000.0);
        for (int c = 0; c < n; c++)
        {
            if (!f.Visited[c] || !IsLand(g.Type[c])) continue;
            f.Acc[c] = cellKm2;
            f.Q[c] = o.Runoff * Math.Max(0.1, g.Moist[c] / 0.5) * cellKm2;
        }
        for (int p = f.OrderCount - 1; p >= 0; p--)
        {
            int c = f.Order[p];
            int d = f.Down[c];
            if (d >= 0) { f.Acc[d] += f.Acc[c]; f.Q[d] += f.Q[c]; }
        }
        // terminal outlet type per cell, in pop order (down is always popped before its source)
        for (int p = 0; p < f.OrderCount; p++)
        {
            int c = f.Order[p];
            byte t = g.Type[c];
            f.Term[c] = t == TOcean ? (byte)1 : t == TLung ? (byte)2 : t == TEdge ? (byte)4 : (f.Down[c] >= 0 ? f.Term[f.Down[c]] : (byte)0);
        }
        time.Accum = sw.Elapsed.TotalSeconds;

        ulong hash = 1469598103934665603UL;
        for (int c = 0; c < n; c++) { hash ^= (uint)f.Down[c]; hash *= 1099511628211UL; }
        f.DownHash = hash;
        return f;
    }

    static void Invariants(Grid g, Flood f, Options o, string label, List<string> failures)
    {
        int n = g.Count;
        var popIndex = new int[n];
        Array.Fill(popIndex, -1);
        for (int p = 0; p < f.OrderCount; p++) popIndex[f.Order[p]] = p;
        int bad = 0;
        for (int c = 0; c < n && bad < 5; c++)
        {
            if (!f.Visited[c]) continue;
            if (f.Filled[c] < g.H[c] - 1e-9) { failures.Add($"{label}: filled < h at cell {c}"); bad++; }
            int d = f.Down[c];
            if (d >= 0)
            {
                if (f.Filled[d] > f.Filled[c] + 1e-9) { failures.Add($"{label}: filled increases downstream at cell {c}"); bad++; }
                if (popIndex[d] >= popIndex[c]) { failures.Add($"{label}: down pointer of cell {c} is not popped earlier (cycle risk)"); bad++; }
            }
            else if (!IsOutlet(g.Type[c])) { failures.Add($"{label}: visited non-outlet cell {c} has no down pointer"); bad++; }
        }
        // chain termination: every visited cell reaches an outlet within `cells` steps (checked through the terminal type)
        for (int c = 0; c < n && bad < 5; c++)
            if (f.Visited[c] && f.Term[c] == 0) { failures.Add($"{label}: chain from cell {c} does not end at an outlet"); bad++; }
        double cellKm2 = (g.Cell / 1000.0) * (g.Cell / 1000.0);
        double reachable = 0, sumOutlets = 0;
        for (int c = 0; c < n; c++)
        {
            if (f.Visited[c] && IsLand(g.Type[c])) reachable += cellKm2;
            if (IsOutlet(g.Type[c])) sumOutlets += f.Acc[c];
        }
        if (Math.Abs(reachable - sumOutlets) > 1e-6 * Math.Max(1.0, reachable)) failures.Add($"{label}: accumulated area at outlets {sumOutlets} differs from reachable land {reachable}");
    }

    // ---------------------------------------------------------------- depressions

    static int[] FindDepressions(Grid g, Flood f, List<Depression> list)
    {
        int n = g.Count, na = g.NA, nj = g.NJ;
        var comp = new int[n];
        Array.Fill(comp, -1);
        double cellKm2 = (g.Cell / 1000.0) * (g.Cell / 1000.0);
        var stack = new Stack<int>();
        for (int s = 0; s < n; s++)
        {
            if (comp[s] >= 0 || !Depressed(g, f, s)) continue;
            int id = list.Count;
            var dep = new Depression();
            comp[s] = id; stack.Push(s);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                dep.Cells++;
                dep.MaxDepth = Math.Max(dep.MaxDepth, f.Filled[c] - g.H[c]);
                dep.Spill = Math.Max(dep.Spill, f.Filled[c]);
                int cx = c % na, cy = c / na;
                for (int k = 0; k < 8; k++)
                {
                    int x = cx + Dx[k], y = cy + Dy[k];
                    if (x < 0 || y < 0 || x >= na || y >= nj) continue;
                    int m = y * na + x;
                    if (comp[m] >= 0 || !Depressed(g, f, m)) continue;
                    comp[m] = id; stack.Push(m);
                }
            }
            dep.AreaKm2 = dep.Cells * cellKm2;
            list.Add(dep);
        }
        return comp;
    }

    static bool Depressed(Grid g, Flood f, int c) => f.Visited[c] && g.Type[c] == TLand && f.Filled[c] > g.H[c] + 0.01;

    // ---------------------------------------------------------------- hydraulics

    sealed class Hydro
    {
        public bool[] Channel = Array.Empty<bool>();    // acc >= smallest threshold, land, not a lake cell
        public float[] Sf = Array.Empty<float>(), So = Array.Empty<float>();
        public byte[] ClassSf = Array.Empty<byte>(), ClassSo = Array.Empty<byte>();
        public float[] D = Array.Empty<float>(), Uc = Array.Empty<float>(), W = Array.Empty<float>();
    }

    static double StepLen(Grid g, Flood f, int c)
    {
        int d = f.Down[c];
        if (d < 0) return g.Cell;
        bool diag = (d % g.NA != c % g.NA) && (d / g.NA != c / g.NA);
        return diag ? g.Cell * StepDiag : g.Cell;
    }

    static (double Depth, double Mean, double Centre, double Width) Hydraulics1(double q, double slope, double n, double a, double b)
    {
        double d = a * Math.Pow(q, b);
        double u = Math.Pow(d, 2.0 / 3.0) * Math.Sqrt(slope) / n;
        return (d, u, Math.Min(10.0, 1.5 * u), q / (u * d));
    }

    static int ClassOf(double centre) => centre < 1.5 ? 0 : centre < 4.0 ? 1 : centre < 7.0 ? 2 : 3;

    static Hydro Hydraulics(Grid g, Flood f, Options o, bool[] lake, double minThreshold, double n, double a, double runoff, TimeSplit time)
    {
        int cnt = g.Count;
        var h = new Hydro { Channel = new bool[cnt], Sf = new float[cnt], So = new float[cnt], ClassSf = new byte[cnt], ClassSo = new byte[cnt], D = new float[cnt], Uc = new float[cnt], W = new float[cnt] };
        double reach = o.GradientKm * 1000.0;
        double qScale = runoff / o.Runoff;
        for (int c = 0; c < cnt; c++)
        {
            if (!f.Visited[c] || !IsLand(g.Type[c]) || lake[c] || f.Acc[c] < minThreshold) continue;
            h.Channel[c] = true;
            int cur = c; double len = 0;
            while (len < reach && f.Down[cur] >= 0)
            {
                len += StepLen(g, f, cur);
                cur = f.Down[cur];
            }
            double sf = len > 0 ? Math.Max((f.Filled[c] - f.Filled[cur]) / len, 1e-5) : 1e-5;
            double so = len > 0 ? Math.Max((g.H[c] - g.H[cur]) / len, 1e-5) : 1e-5;
            h.Sf[c] = (float)sf; h.So[c] = (float)so;
            double q = f.Q[c] * qScale;
            var hf = Hydraulics1(q, sf, n, a, o.DepthExp);
            var ho = Hydraulics1(q, so, n, a, o.DepthExp);
            h.D[c] = (float)hf.Depth; h.Uc[c] = (float)hf.Centre; h.W[c] = (float)hf.Width;
            h.ClassSf[c] = (byte)ClassOf(hf.Centre);
            h.ClassSo[c] = (byte)ClassOf(ho.Centre);
        }
        return h;
    }

    // ---------------------------------------------------------------- statistics

    static double Pct(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        int idx = Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[idx];
    }

    static string R(double v) => double.IsFinite(v) ? v.ToString("G4", System.Globalization.CultureInfo.InvariantCulture) : "n/a";
    static double J(double v) => double.IsFinite(v) ? Math.Round(v, 6) : 0;

    static double[] ClassSharesByLength(Grid g, Flood f, Hydro hy, double threshold, bool useFilled, double n, double a, double b, double qScale)
    {
        var len = new double[4];
        double sum = 0;
        for (int c = 0; c < g.Count; c++)
        {
            if (!hy.Channel[c] || f.Acc[c] < threshold) continue;
            double s = StepLen(g, f, c);
            double slope = useFilled ? hy.Sf[c] : hy.So[c];
            int cls = ClassOf(Hydraulics1(f.Q[c] * qScale, slope, n, a, b).Centre);
            len[cls] += s; sum += s;
        }
        if (sum > 0) for (int k = 0; k < 4; k++) len[k] /= sum;
        return len;
    }

    Dictionary<string, object> Statistics(Grid g, Flood f, Options o, List<Depression> deps, bool[] lake, double midBreach, Hydro hy, TimeSplit time, int threads, double start, string stripNote,
        double scanSeconds, bool inside, bool deterministic, ulong secondHash, out string md, out double[] classShareSf50, out double[] classShareSo50, out double endEdgeShare, out double enclosedShare)
    {
        int n = g.Count;
        double cellKm = g.Cell / 1000.0, cellKm2 = cellKm * cellKm;
        var json = new Dictionary<string, object>();
        var sb = new StringBuilder();
        sb.AppendLine($"# Drainage prototype, {g.Cell} m cells");
        sb.AppendLine();
        sb.AppendLine($"Strip start {LandscapeWorld.Num(start)} m ({stripNote}), length {o.StripKm} km, grid {g.NA} x {g.NJ} cells ({n:N0}). Saved pose around 27,984,761 m is {(inside ? "inside" : "outside")} the strip.");
        sb.AppendLine($"Hash of the flow-direction array: 0x{f.DownHash:X16}; second in-process bake: {(secondHash == 0 ? "not run" : "0x" + secondHash.ToString("X16"))}; deterministic: {deterministic}.");
        sb.AppendLine("Confinement is not modelled: every reach takes the open-channel relation on its own gradient with no valley-width squeeze, so gorge and water-gap speeds are underestimated.");
        sb.AppendLine("Routing height is the natural height; outlet cells use min(natural, shaped). Lakes at the middle breach limit are excluded from the class statistics.");
        sb.AppendLine();

        long landCells = 0, ocean = 0, barrier = 0, edge = 0, enclosed = 0;
        for (int c = 0; c < n; c++)
        {
            switch (g.Type[c])
            {
                case TOcean: case TLung: ocean++; break;
                case TBarrier: barrier++; break;
                case TEdge: edge++; landCells++; break;
                default: landCells++; if (!f.Visited[c]) enclosed++; break;
            }
        }
        double landKm2 = landCells * cellKm2;
        enclosedShare = landCells > 0 ? (double)enclosed / landCells : 0;
        double barrierShare = (double)barrier / n;
        sb.AppendLine("## Strip composition");
        sb.AppendLine();
        sb.AppendLine("| Quantity | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Land (non-barrier, shaped height >= 0), km^2 | {landKm2:N0} ({(double)landCells / n * 100:F1} % of strip) |");
        sb.AppendLine($"| Ocean and Weather Lung outlet cells | {ocean:N0} ({(double)ocean / n * 100:F1} % of strip) |");
        sb.AppendLine($"| Barrier cells | {barrier:N0} ({barrierShare * 100:F2} % of strip) |");
        sb.AppendLine($"| Edge-outlet land cells | {edge:N0} |");
        sb.AppendLine($"| Enclosed land (never reached by the flood) | {enclosed:N0} ({enclosedShare * 100:F2} % of land) |");
        sb.AppendLine();
        json["strip"] = new Dictionary<string, object> { ["start_m"] = start, ["km"] = o.StripKm, ["note"] = stripNote, ["cells_around"] = g.NA, ["cells_across"] = g.NJ, ["saved_pose_inside"] = inside,
            ["land_km2"] = J(landKm2), ["land_share_of_strip"] = J((double)landCells / n), ["ocean_cells"] = ocean, ["barrier_cells"] = barrier, ["barrier_share"] = J(barrierShare), ["edge_cells"] = edge, ["enclosed_cells"] = enclosed, ["enclosed_share_of_land"] = J(enclosedShare), ["down_hash"] = "0x" + f.DownHash.ToString("X16"), ["deterministic"] = deterministic };

        // depressions
        var depths = deps.Select(d => d.MaxDepth).OrderBy(v => v).ToList();
        var areas = deps.Select(d => d.AreaKm2).OrderBy(v => v).ToList();
        sb.AppendLine("## Depressions (filled > h + 0.01 m, 8-connected components)");
        sb.AppendLine();
        sb.AppendLine("| Count | depth p50 m | depth p90 m | depth max m | area p50 km^2 | area p90 km^2 |");
        sb.AppendLine("|---|---|---|---|---|---|");
        sb.AppendLine($"| {deps.Count:N0} | {R(Pct(depths, .5))} | {R(Pct(depths, .9))} | {R(depths.Count > 0 ? depths[^1] : 0)} | {R(Pct(areas, .5))} | {R(Pct(areas, .9))} |");
        sb.AppendLine();
        json["depressions"] = new Dictionary<string, object> { ["count"] = deps.Count, ["depth_p50"] = J(Pct(depths, .5)), ["depth_p90"] = J(Pct(depths, .9)), ["depth_max"] = J(depths.Count > 0 ? depths[^1] : 0), ["area_p50_km2"] = J(Pct(areas, .5)), ["area_p90_km2"] = J(Pct(areas, .9)) };

        // lakes per breach limit
        sb.AppendLine("## Lakes by breach limit (a depression deeper than the limit is a lake)");
        sb.AppendLine();
        sb.AppendLine("| Breach limit m | Lakes | Lake area share of land | area p50 km^2 | area p90 km^2 | area max km^2 | < 10 km^2 | 10-100 | 100-1,000 | > 1,000 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        var lakeJson = new List<object>();
        foreach (double b in o.Breach.OrderBy(v => v))
        {
            var ls = deps.Where(d => d.MaxDepth > b).Select(d => d.AreaKm2).OrderBy(v => v).ToList();
            double share = landKm2 > 0 ? ls.Sum() / landKm2 : 0;
            int b1 = ls.Count(v => v < 10), b2 = ls.Count(v => v >= 10 && v < 100), b3 = ls.Count(v => v >= 100 && v < 1000), b4 = ls.Count(v => v >= 1000);
            sb.AppendLine($"| {b:G} | {ls.Count:N0} | {share * 100:F2} % | {R(Pct(ls, .5))} | {R(Pct(ls, .9))} | {R(ls.Count > 0 ? ls[^1] : 0)} | {b1} | {b2} | {b3} | {b4} |");
            lakeJson.Add(new Dictionary<string, object> { ["breach_m"] = b, ["lakes"] = ls.Count, ["land_share"] = J(share), ["area_p50"] = J(Pct(ls, .5)), ["area_p90"] = J(Pct(ls, .9)), ["area_max"] = J(ls.Count > 0 ? ls[^1] : 0), ["bins_lt10_10_100_100_1000_gt1000"] = new[] { b1, b2, b3, b4 } });
        }
        sb.AppendLine();
        json["lakes"] = lakeJson;

        // drainage density and graph size
        sb.AppendLine($"## Channels by catchment threshold (lakes at breach {midBreach:G} m excluded)");
        sb.AppendLine();
        sb.AppendLine("| Threshold km^2 | Channel nodes | Channel length km | Density km/km^2 | Nodes if whole ring | Bytes per node for 16 MB | Calm+swift by length (S_f) | Fierce | Torrent | Calm+swift (S_o) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        var thrJson = new List<object>();
        double ringScale = LandscapeWorld.Circumference / (o.StripKm * 1000.0);
        foreach (double thr in o.Thresholds.OrderBy(v => v))
        {
            long nodes = 0; double length = 0;
            for (int c = 0; c < n; c++)
            {
                if (!hy.Channel[c] || f.Acc[c] < thr) continue;
                nodes++; length += StepLen(g, f, c) / 1000.0;
            }
            double[] sf = ClassSharesByLength(g, f, hy, thr, true, o.N, o.DepthCoef, o.DepthExp, 1.0);
            double[] so = ClassSharesByLength(g, f, hy, thr, false, o.N, o.DepthCoef, o.DepthExp, 1.0);
            double ringNodes = nodes * ringScale;
            double bytes = ringNodes > 0 ? 16.0 * 1024 * 1024 / ringNodes : 0;
            sb.AppendLine($"| {thr:G} | {nodes:N0} | {length:N0} | {(landKm2 > 0 ? length / landKm2 : 0):F4} | {ringNodes:N0} | {bytes:F1} | {(sf[0] + sf[1]) * 100:F1} % | {sf[2] * 100:F1} % | {sf[3] * 100:F1} % | {(so[0] + so[1]) * 100:F1} % |");
            thrJson.Add(new Dictionary<string, object> { ["threshold_km2"] = thr, ["nodes"] = nodes, ["length_km"] = J(length), ["density_km_per_km2"] = J(landKm2 > 0 ? length / landKm2 : 0), ["ring_nodes"] = J(ringNodes), ["bytes_per_node_16mb"] = J(bytes), ["class_len_Sf"] = sf.Select(J).ToArray(), ["class_len_So"] = so.Select(J).ToArray() });
        }
        sb.AppendLine();
        json["thresholds"] = thrJson;

        // hydraulics at 50 km^2 (or the closest threshold to 50)
        double main = o.Thresholds.OrderBy(v => Math.Abs(v - 50)).First();
        var ch = new List<int>();
        for (int c = 0; c < n; c++) if (hy.Channel[c] && f.Acc[c] >= main) ch.Add(c);
        List<double> Sorted(Func<int, double> sel) => ch.Select(sel).OrderBy(v => v).ToList();
        var qs = Sorted(c => f.Q[c]); var sfs = Sorted(c => hy.Sf[c]); var sos = Sorted(c => hy.So[c]); var ds = Sorted(c => hy.D[c]); var us = Sorted(c => hy.Uc[c]); var ws = Sorted(c => hy.W[c]);
        sb.AppendLine($"## Hydraulics of channel cells at {main:G} km^2 ({ch.Count:N0} cells)");
        sb.AppendLine();
        sb.AppendLine("| Quantity | p10 | p50 | p90 | max |");
        sb.AppendLine("|---|---|---|---|---|");
        var hydJson = new Dictionary<string, object>();
        void Row(string name, List<double> l)
        {
            sb.AppendLine($"| {name} | {R(Pct(l, .1))} | {R(Pct(l, .5))} | {R(Pct(l, .9))} | {R(l.Count > 0 ? l[^1] : 0)} |");
            hydJson[name] = new[] { J(Pct(l, .1)), J(Pct(l, .5)), J(Pct(l, .9)), J(l.Count > 0 ? l[^1] : 0) };
        }
        Row("Q m^3/s", qs); Row("S_f (filled gradient)", sfs); Row("S_o (original gradient)", sos); Row("depth d m", ds); Row("centre speed m/s (S_f)", us); Row("width w m", ws);
        sb.AppendLine();
        json["hydraulics"] = hydJson;

        // class shares
        var cnt = new long[2, 4]; var len = new double[2, 4]; double lenSum = 0;
        foreach (int c in ch)
        {
            double s = StepLen(g, f, c); lenSum += s;
            cnt[0, hy.ClassSf[c]]++; cnt[1, hy.ClassSo[c]]++;
            len[0, hy.ClassSf[c]] += s; len[1, hy.ClassSo[c]] += s;
        }
        classShareSf50 = new double[4]; classShareSo50 = new double[4];
        for (int k = 0; k < 4; k++) { classShareSf50[k] = lenSum > 0 ? len[0, k] / lenSum : 0; classShareSo50[k] = lenSum > 0 ? len[1, k] / lenSum : 0; }
        sb.AppendLine($"## Danger classes at {main:G} km^2 (centre pool speed: calm < 1.5, swift 1.5-4, fierce 4-7, torrent > 7 m/s)");
        sb.AppendLine();
        sb.AppendLine("| Gradient | Basis | calm | swift | fierce | torrent |");
        sb.AppendLine("|---|---|---|---|---|---|");
        sb.AppendLine($"| S_f | by length | {string.Join(" | ", classShareSf50.Select(v => (v * 100).ToString("F1") + " %"))} |");
        sb.AppendLine($"| S_f | by cell count | {string.Join(" | ", Enumerable.Range(0, 4).Select(k => (ch.Count > 0 ? (double)cnt[0, k] / ch.Count * 100 : 0).ToString("F1") + " %"))} |");
        sb.AppendLine($"| S_o | by length | {string.Join(" | ", classShareSo50.Select(v => (v * 100).ToString("F1") + " %"))} |");
        sb.AppendLine($"| S_o | by cell count | {string.Join(" | ", Enumerable.Range(0, 4).Select(k => (ch.Count > 0 ? (double)cnt[1, k] / ch.Count * 100 : 0).ToString("F1") + " %"))} |");
        sb.AppendLine();
        sb.AppendLine($"Plan targets by length: at least 70 % calm or swift (S_f: {(classShareSf50[0] + classShareSf50[1]) * 100:F1} %, S_o: {(classShareSo50[0] + classShareSo50[1]) * 100:F1} %), 5-10 % fierce (S_f {classShareSf50[2] * 100:F1} %), at most 3 % torrent (S_f {classShareSf50[3] * 100:F1} %).");
        sb.AppendLine();
        json["danger"] = new Dictionary<string, object> { ["threshold_km2"] = main, ["channel_cells"] = ch.Count, ["len_Sf"] = classShareSf50.Select(J).ToArray(), ["len_So"] = classShareSo50.Select(J).ToArray(),
            ["count_Sf"] = Enumerable.Range(0, 4).Select(k => J(ch.Count > 0 ? (double)cnt[0, k] / ch.Count : 0)).ToArray(), ["count_So"] = Enumerable.Range(0, 4).Select(k => J(ch.Count > 0 ? (double)cnt[1, k] / ch.Count : 0)).ToArray(), ["classes"] = ClassNames };

        // exits
        var dist = new double[n];
        var danger = new bool[n];
        foreach (int c in ch) danger[c] = hy.ClassSf[c] >= 2;
        long dangerCells = 0, within5 = 0, noExit = 0;
        for (int p = 0; p < f.OrderCount; p++)
        {
            int c = f.Order[p];
            if (!danger[c]) continue;
            int d = f.Down[c];
            double step = StepLen(g, f, c);
            dist[c] = d < 0 ? double.PositiveInfinity : danger[d] ? step + dist[d] : step;
            dangerCells++;
            if (double.IsInfinity(dist[c])) noExit++;
            else if (dist[c] <= 5000.0) within5++;
        }
        double exitShare = dangerCells > 0 ? (double)within5 / dangerCells : 1.0;
        sb.AppendLine("## Downstream exit of fierce and torrent cells (S_f)");
        sb.AppendLine();
        sb.AppendLine($"{dangerCells:N0} fierce or torrent cells; {within5:N0} ({exitShare * 100:F1} %) reach a calm or swift cell, a lake or the ocean within 5 km downstream; {noExit:N0} end at a strip edge while still dangerous.");
        sb.AppendLine();
        json["exit"] = new Dictionary<string, object> { ["dangerous_cells"] = dangerCells, ["within_5km"] = within5, ["share_within_5km"] = J(exitShare), ["dangerous_at_strip_edge"] = noExit };

        // where flow ends
        var ends = new long[5];
        foreach (int c in ch) ends[f.Term[c]]++;
        endEdgeShare = ch.Count > 0 ? (double)ends[4] / ch.Count : 0;
        sb.AppendLine("## Where flow ends (channel cells)");
        sb.AppendLine();
        sb.AppendLine("| Ocean | Weather Lung | Strip edge |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| {(ch.Count > 0 ? (double)ends[1] / ch.Count * 100 : 0):F1} % | {(ch.Count > 0 ? (double)ends[2] / ch.Count * 100 : 0):F1} % | {endEdgeShare * 100:F1} % |");
        sb.AppendLine();
        json["flow_end"] = new Dictionary<string, object> { ["ocean"] = J(ch.Count > 0 ? (double)ends[1] / ch.Count : 0), ["weather_lung"] = J(ch.Count > 0 ? (double)ends[2] / ch.Count : 0), ["strip_edge"] = J(endEdgeShare) };

        // sensitivity (post-processing only, S_f, main threshold)
        if (g.Cell == 1000)
        {
            sb.AppendLine($"## Sensitivity (S_f, {main:G} km^2, by length; one parameter varied at a time)");
            sb.AppendLine();
            sb.AppendLine("| Variation | calm | swift | fierce | torrent | calm+swift |");
            sb.AppendLine("|---|---|---|---|---|---|");
            var sens = new List<(string Name, double N, double A, double Scale)>
            {
                ("default", o.N, o.DepthCoef, 1.0),
                ("n = 0.030", 0.030, o.DepthCoef, 1.0), ("n = 0.045", 0.045, o.DepthCoef, 1.0),
                ("depth coef = 0.20", o.N, 0.20, 1.0), ("depth coef = 0.35", o.N, 0.35, 1.0),
                ("runoff = 0.01", o.N, o.DepthCoef, 0.01 / o.Runoff), ("runoff = 0.04", o.N, o.DepthCoef, 0.04 / o.Runoff),
            };
            var sensJson = new List<object>();
            foreach (var s in sens)
            {
                double[] sh = ClassSharesByLength(g, f, hy, main, true, s.N, s.A, o.DepthExp, s.Scale);
                sb.AppendLine($"| {s.Name} | {string.Join(" | ", sh.Select(v => (v * 100).ToString("F1") + " %"))} | {(sh[0] + sh[1]) * 100:F1} % |");
                sensJson.Add(new Dictionary<string, object> { ["name"] = s.Name, ["len_Sf"] = sh.Select(J).ToArray() });
            }
            sb.AppendLine();
            json["sensitivity"] = sensJson;
        }

        sb.AppendLine("## Timing");
        sb.AppendLine();
        sb.AppendLine("{{TIMING}}");
        sb.AppendLine();
        sb.AppendLine("## River map legend");
        sb.AppendLine();
        sb.AppendLine($"river_map_{g.Cell}m.png: one pixel per cell, around to the right, across downward. Land hillshade grey (vertical exaggeration {o.ShadeExaggeration:G}); ocean blue darkened with depth; lakes (breach {midBreach:G} m) light blue; barriers magenta; edge-outlet cells yellow;");
        sb.AppendLine("channels at 50 km^2 by S_f class: calm blue (0.20, 0.45, 0.95), swift green (0.15, 0.75, 0.35), fierce orange (0.98, 0.60, 0.10), torrent red (0.90, 0.10, 0.10); width 1 px below 100 m^3/s, 2 px to 1,000 m^3/s, 3 px above.");
        sb.AppendLine($"river_map_{g.Cell}m_crop.png: the 300 km window with the most fierce plus torrent length, scaled 3x with nearest-neighbour sampling.");
        md = sb.ToString();
        json["timing"] = TimingJson(time, o.StripKm, threads);
        return json;
    }

    static Dictionary<string, object> TimingJson(TimeSplit t, int stripKm, int threads) => new()
    {
        ["sampling_s"] = J(t.Sampling), ["flood_s"] = J(t.FloodSec), ["accumulation_s"] = J(t.Accum), ["hydraulics_s"] = J(t.Hydraulics), ["statistics_s"] = J(t.Stats),
        ["threads"] = threads, ["cores"] = System.Environment.ProcessorCount,
        ["ring_extrapolation_s_at_this_cell_size"] = J(t.BakeTotal * LandscapeWorld.Circumference / (stripKm * 1000.0)),
    };

    static string TimingMd(TimeSplit t, int stripKm, int threads)
        => $"| Sampling | Flood | Accumulation | Hydraulics | Statistics | Threads / cores |\n|---|---|---|---|---|---|\n| {t.Sampling:F2} s | {t.FloodSec:F2} s | {t.Accum:F2} s | {t.Hydraulics:F2} s | {t.Stats:F2} s | {threads} / {System.Environment.ProcessorCount} |\n\n"
         + $"Whole-ring bake at this cell size (strip bake time {t.BakeTotal:F1} s x {LandscapeWorld.Circumference / (stripKm * 1000.0):F2}): about {t.BakeTotal * LandscapeWorld.Circumference / (stripKm * 1000.0):F0} s.";

    // ---------------------------------------------------------------- maps

    static void WriteMaps(Grid g, Flood f, Options o, bool[] lake, Hydro hy, string outDir, string label)
    {
        int w = g.NA, h = g.NJ;
        var rgb = new byte[w * h * 3];
        double thr = o.Thresholds.OrderBy(v => Math.Abs(v - 50)).First();
        (double X, double Y, double Z) light = (-1 / Math.Sqrt(3), 1 / Math.Sqrt(3), -1 / Math.Sqrt(3));
        void Put(int idx, double r, double gr, double b)
        {
            rgb[idx * 3] = (byte)Math.Clamp((int)(r * 255 + 0.5), 0, 255);
            rgb[idx * 3 + 1] = (byte)Math.Clamp((int)(gr * 255 + 0.5), 0, 255);
            rgb[idx * 3 + 2] = (byte)Math.Clamp((int)(b * 255 + 0.5), 0, 255);
        }
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int idx = j * w + i;
                byte t = g.Type[idx];
                if (t == TOcean || t == TLung)
                {
                    double depth = Math.Clamp(-g.Shaped[idx] / 600.0, 0, 1);
                    double k = 1 - 0.6 * depth;
                    Put(idx, 0.10 * k, 0.20 * k, 0.45 * k);
                    continue;
                }
                if (t == TBarrier) { Put(idx, 0.80, 0.20, 0.80); continue; }
                if (t == TEdge) { Put(idx, 0.95, 0.90, 0.15); continue; }
                if (lake[idx]) { Put(idx, 0.30, 0.65, 0.85); continue; }
                double hl = g.Nat[j * w + Math.Max(i - 1, 0)], hr = g.Nat[j * w + Math.Min(i + 1, w - 1)];
                double hu = g.Nat[Math.Max(j - 1, 0) * w + i], hd = g.Nat[Math.Min(j + 1, h - 1) * w + i];
                double dx = o.ShadeExaggeration * (hr - hl) / (2.0 * g.Cell), dz = o.ShadeExaggeration * (hd - hu) / (2.0 * g.Cell);
                double inv = 1.0 / Math.Sqrt(dx * dx + 1 + dz * dz);
                double lit = Math.Max(0, (-dx * inv) * light.X + inv * light.Y + (-dz * inv) * light.Z);
                double v = 0.35 + 0.55 * lit;
                Put(idx, v, v, v);
            }
        // channels at the 50 km^2 threshold, widest last so the big rivers stay on top
        (double R, double G, double B)[] palette = { (0.20, 0.45, 0.95), (0.15, 0.75, 0.35), (0.98, 0.60, 0.10), (0.90, 0.10, 0.10) };
        for (int pass = 1; pass <= 3; pass++)
            for (int c = 0; c < g.Count; c++)
            {
                if (!hy.Channel[c] || f.Acc[c] < thr) continue;
                double q = f.Q[c];
                int width = q < 100 ? 1 : q < 1000 ? 2 : 3;
                if (width != pass) continue;
                int cx = c % w, cy = c / w;
                int lo = -(width - 1) / 2, hi = width / 2;
                var col = palette[hy.ClassSf[c]];
                for (int yy = cy + lo; yy <= cy + hi; yy++)
                    for (int xx = cx + lo; xx <= cx + hi; xx++)
                    {
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int m = yy * w + xx;
                        if (g.Type[m] == TOcean || g.Type[m] == TLung || g.Type[m] == TBarrier) continue;
                        Put(m, col.R, col.G, col.B);
                    }
            }
        SavePng(rgb, w, h, Path.Combine(outDir, $"river_map_{label}.png"));

        // crop: the 300 km window (full across extent) with the most fierce plus torrent length
        int win = 300 * 1000 / g.Cell;
        var colScore = new double[w];
        for (int c = 0; c < g.Count; c++)
            if (hy.Channel[c] && f.Acc[c] >= thr && hy.ClassSf[c] >= 2) colScore[c % w] += StepLen(g, f, c);
        double run = 0; for (int i = 0; i < Math.Min(win, w); i++) run += colScore[i];
        double bestScore = run; int bestStart = 0;
        for (int i = 1; i + win <= w; i++)
        {
            run += colScore[i + win - 1] - colScore[i - 1];
            if (run > bestScore) { bestScore = run; bestStart = i; }
        }
        int cw = Math.Min(win, w), scale = 3;
        var crop = new byte[cw * scale * h * scale * 3];
        for (int y = 0; y < h * scale; y++)
            for (int x = 0; x < cw * scale; x++)
            {
                int src = ((y / scale) * w + bestStart + x / scale) * 3;
                int dst = (y * cw * scale + x) * 3;
                crop[dst] = rgb[src]; crop[dst + 1] = rgb[src + 1]; crop[dst + 2] = rgb[src + 2];
            }
        SavePng(crop, cw * scale, h * scale, Path.Combine(outDir, $"river_map_{label}_crop.png"));
        GD.Print($"LANDSCAPE_DRAINAGE_CROP {label} window_start_around_m={LandscapeWorld.Num(g.Start + bestStart * (double)g.Cell)} dangerous_km={bestScore / 1000.0:F0}");
    }

    static void SavePng(byte[] rgb, int w, int h, string path)
    {
        using Image image = Image.CreateFromData(w, h, false, Image.Format.Rgb8, rgb);
        Error err = image.SavePng(path);
        if (err != Error.Ok) throw new IOException("SavePng failed: " + err + " " + path);
    }
}
