using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>The spans of a network (Spec E1, T3R-1 form: at most 2 km inside each reach, reach Q), stored per stem in order of arc length.</summary>
public sealed class RiverDangerSpans
{
    public int Count;
    public int[] Stem = Array.Empty<int>(), Reach = Array.Empty<int>();
    public double[] S0 = Array.Empty<double>(), S1 = Array.Empty<double>(), Q = Array.Empty<double>(), Sh = Array.Empty<double>(), Sr = Array.Empty<double>(), ReachS = Array.Empty<double>();
    /// <summary>First span and span count of each stem.</summary>
    public int[] StemStart = Array.Empty<int>(), StemCount = Array.Empty<int>();
    /// <summary>Stems in topological order: every tributary before the stem it joins.</summary>
    public int[] Order = Array.Empty<int>();
    /// <summary>For each stem, the stems that join it as (arc parameter on it, tributary), sorted by arc parameter.</summary>
    public List<(double J, int Trib)>[] Joins = Array.Empty<List<(double, int)>>();

    public double Length(int i) => S1[i] - S0[i];

    /// <summary>The span of <paramref name="stem"/> that contains arc parameter <paramref name="s"/>, or -1 when the stem has none.</summary>
    public int SpanAt(int stem, double s)
    {
        int n = StemCount[stem];
        if (n == 0) return -1;
        int lo = StemStart[stem], hi = lo + n - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (S0[mid] <= s) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}

/// <summary>Per-span values of one evaluation. D arrays are the dangerous run length in metres entering and leaving the span (E4).</summary>
public sealed class RiverDangerResult
{
    public double[] D = Array.Empty<double>(), W = Array.Empty<double>(), U = Array.Empty<double>(), Vd = Array.Empty<double>(), Landing = Array.Empty<double>(), DIn = Array.Empty<double>(), DOut = Array.Empty<double>();
    public RiverClass[] Class = Array.Empty<RiverClass>();
    public int Landings;
    public double LandingMeters;
    public double[] Share = new double[4];
}

/// <summary>
/// T3R-1 (docs/work-orders/terrain-vegetation/T3.md): re-buckets danger on an existing graph. Cuts stems into spans (E1), applies the E2 and E3 law
/// (<see cref="RiverDanger"/>), walks the exit rule and landings (E4, without the site exclusions) and writes the text and JSON report and the grid of reset doc 4.5.
/// </summary>
public static class RiverDangerReport
{
    public const double RunLimitMeters = 5000d;
    public const double LandingSpeed = 3d;
    public const double WindowHalfMeters = 2500d;

    /// <summary>Cuts every reach into <c>ceil(length / spanMeters)</c> equal parts and computes Sh (stored profile over a 5 km window) and Sr (over the span).</summary>
    public static RiverDangerSpans Cut(RiverNetwork net, double spanMeters = 2000d)
    {
        int stems = net.Stems.Length;
        var stem = new List<int>(); var reach = new List<int>();
        var s0 = new List<double>(); var s1 = new List<double>(); var q = new List<double>(); var sh = new List<double>(); var sr = new List<double>(); var rs = new List<double>();
        var stemStart = new int[stems]; var stemCount = new int[stems];
        for (int i = 0; i < stems; i++)
        {
            RiverStem st = net.Stems[i];
            stemStart[i] = stem.Count;
            if (st.ReachCount == 0) continue;
            double send = net.Reaches[st.ReachStart + st.ReachCount - 1].SEnd;
            bool levels = net.Knots.KnotCount(i) > 0;
            for (int r = st.ReachStart; r < st.ReachStart + st.ReachCount; r++)
            {
                RiverReach rr = net.Reaches[r];
                double len = rr.SEnd - rr.SStart;
                if (len <= 0) continue;
                int n = Math.Max(1, (int)Math.Ceiling(len / spanMeters));
                for (int k = 0; k < n; k++)
                {
                    double a = rr.SStart + len * k / n, b = rr.SStart + len * (k + 1) / n, m = 0.5 * (a + b);
                    double gradient = rr.S, own = 0d;
                    if (levels)
                    {
                        double x0 = Math.Min(WindowHalfMeters, m), x1 = Math.Min(WindowHalfMeters, send - m);
                        if (x0 + x1 > 1d)
                        {
                            double v = (RiverLevel.Stored(net, i, m - x0) - RiverLevel.Stored(net, i, m + x1)) / (x0 + x1);
                            if (!double.IsNaN(v)) gradient = v;
                        }
                        double drop = RiverLevel.Stored(net, i, a) - RiverLevel.Stored(net, i, b);
                        if (!double.IsNaN(drop)) own = Math.Max(0d, drop / (b - a));
                    }
                    stem.Add(i); reach.Add(r); s0.Add(a); s1.Add(b); q.Add(rr.Q); sh.Add(Math.Max(gradient, 1e-4)); sr.Add(own); rs.Add(Math.Max(rr.S, 1e-4));
                    stemCount[i]++;
                }
            }
        }
        var joins = new List<(double J, int Trib)>[stems];
        var indeg = new int[stems];
        for (int i = 0; i < stems; i++)
        {
            RiverStem st = net.Stems[i];
            if (st.Terminal != RiverTerminal.Confluence || st.TerminalId < 0 || st.TerminalId >= stems) continue;
            (joins[st.TerminalId] ??= new List<(double, int)>()).Add((st.JunctionS, i));
            indeg[st.TerminalId]++;
        }
        for (int i = 0; i < stems; i++) joins[i]?.Sort((a, b) => a.J.CompareTo(b.J));
        var order = new List<int>(stems);
        var queue = new Queue<int>();
        for (int i = 0; i < stems; i++) if (indeg[i] == 0) queue.Enqueue(i);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            order.Add(i);
            RiverStem st = net.Stems[i];
            if (st.Terminal == RiverTerminal.Confluence && st.TerminalId >= 0 && st.TerminalId < stems && --indeg[st.TerminalId] == 0) queue.Enqueue(st.TerminalId);
        }
        for (int i = 0; i < stems; i++) if (indeg[i] > 0) order.Add(i); // a cycle never happens; keeps the walk total
        return new RiverDangerSpans
        {
            Count = stem.Count, Stem = stem.ToArray(), Reach = reach.ToArray(), S0 = s0.ToArray(), S1 = s1.ToArray(), Q = q.ToArray(), Sh = sh.ToArray(), Sr = sr.ToArray(), ReachS = rs.ToArray(),
            StemStart = stemStart, StemCount = stemCount, Order = order.ToArray(), Joins = joins,
        };
    }

    /// <summary>Evaluates the law on every span in topological order. With <paramref name="landings"/> the exit rule of E4 softens the span that would take a dangerous run past 5 km.</summary>
    public static RiverDangerResult Evaluate(RiverNetwork net, RiverDangerSpans sp, RiverDangerConstants c, bool landings)
    {
        int n = sp.Count;
        var res = new RiverDangerResult
        {
            D = new double[n], W = new double[n], U = new double[n], Vd = new double[n], Landing = new double[n], DIn = new double[n], DOut = new double[n], Class = new RiverClass[n],
        };
        double fer = net.Ferocity, depth = net.ChannelDepth;
        var stemOut = new double[sp.StemStart.Length];
        var shares = new double[4];
        double total = 0d;
        foreach (int stem in sp.Order)
        {
            int first = sp.StemStart[stem], count = sp.StemCount[stem];
            List<(double J, int Trib)>? joins = sp.Joins[stem];
            int jp = 0;
            double prevOut = 0d;
            for (int i = first; i < first + count; i++)
            {
                double dIn = prevOut;
                bool last = i == first + count - 1;
                if (joins != null)
                {
                    while (jp < joins.Count && (joins[jp].J < sp.S1[i] || last)) { dIn = Math.Max(dIn, stemOut[joins[jp].Trib]); jp++; }
                }
                double len = sp.Length(i), grad = c.UseReachGradient ? sp.ReachS[i] : sp.Sh[i], l = 1d;
                RiverDangerChannel ch = RiverDanger.Channel(c, sp.Q[i], grad, depth, fer);
                double vd = RiverDanger.DangerSpeed(c, ch.Speed, ch.Depth, sp.Sr[i], fer);
                RiverClass cls = RiverDanger.Classify(vd);
                bool dangerous = cls >= RiverClass.Fierce;
                if (dangerous && landings && dIn > 0d && dIn + len > RunLimitMeters)
                {
                    double lo = 1d, hi = 64d;
                    if (Vd(c, sp, i, grad, depth, fer, hi) > LandingSpeed) lo = hi;
                    else for (int k = 0; k < 40; k++) { double mid = 0.5 * (lo + hi); if (Vd(c, sp, i, grad, depth, fer, mid) <= LandingSpeed) hi = mid; else lo = mid; }
                    l = hi;
                    ch = RiverDanger.Channel(c, sp.Q[i], grad, depth, fer, l);
                    vd = RiverDanger.DangerSpeed(c, ch.Speed, ch.Depth, sp.Sr[i], fer, l);
                    cls = RiverDanger.Classify(vd);
                    dangerous = false;
                    res.Landings++; res.LandingMeters += len;
                }
                double dOut = dangerous ? dIn + len : 0d;
                res.D[i] = ch.Depth; res.W[i] = ch.Width; res.U[i] = ch.Speed; res.Vd[i] = vd; res.Landing[i] = l; res.DIn[i] = dIn; res.DOut[i] = dOut; res.Class[i] = cls;
                shares[(int)cls] += len; total += len;
                prevOut = dOut;
            }
            stemOut[stem] = prevOut;
        }
        for (int k = 0; k < 4; k++) res.Share[k] = total > 0 ? shares[k] / total : 0d;
        return res;
    }

    private static double Vd(RiverDangerConstants c, RiverDangerSpans sp, int i, double grad, double depth, double fer, double l)
    {
        RiverDangerChannel ch = RiverDanger.Channel(c, sp.Q[i], grad, depth, fer, l);
        return RiverDanger.DangerSpeed(c, ch.Speed, ch.Depth, sp.Sr[i], fer, l);
    }

    private static double Percentile(double[] values, double[] weights, int[] sorted, double total, double q)
    {
        double run = 0d, target = q * total;
        foreach (int i in sorted) { run += weights[i]; if (run >= target) return values[i]; }
        return sorted.Length == 0 ? 0d : values[sorted[^1]];
    }

    private static string Pcts(double[] values, double[] weights, double scale = 1d)
    {
        int[] idx = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();
        double total = weights.Sum();
        return string.Join("/", new[] { 0.1, 0.5, 0.9, 0.99 }.Select(q => (Percentile(values, weights, idx, total, q) * scale).ToString("G4", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string Mix(double[] share) => FormattableString.Invariant($"calm {share[0] * 100:F1} %, swift {share[1] * 100:F1} %, fierce {share[2] * 100:F1} %, torrent {share[3] * 100:F1} %");

    /// <summary>The acceptance window of E3 (both seeds, after landings): calm at least 30 %, calm plus swift at least 85 %, fierce 6 to 9 %, torrent 1 to 2.5 %.</summary>
    public static bool InWindow(double[] s) => s[0] >= 0.30 && s[0] + s[1] >= 0.85 && s[2] >= 0.06 && s[2] <= 0.09 && s[3] >= 0.01 && s[3] <= 0.025;

    /// <summary>The grid of reset doc 4.5: mountain roughness Nm x depth coefficient Kd x eta E0, shares after landings.</summary>
    public static string Grid(RiverNetwork net, RiverDangerSpans sp, out List<(double Nm, double Kd, double E0, double[] Share, double LandingShare)> rows)
    {
        rows = new List<(double, double, double, double[], double)>();
        var sb = new StringBuilder();
        sb.AppendLine("danger grid (after landings; * = inside the acceptance window: calm >= 30 %, calm+swift >= 85 %, fierce 6-9 %, torrent 1-2.5 %)");
        double length = Enumerable.Range(0, sp.Count).Sum(i => sp.Length(i));
        foreach (double nm in new[] { 0.16, 0.18, 0.20, 0.22 })
        foreach (double kd in new[] { 0.30, 0.34, 0.40 })
        foreach (double e0 in new[] { 0.5, 0.65, 0.8 })
        {
            RiverDangerResult r = Evaluate(net, sp, RiverDangerConstants.Default with { Nm = nm, Kd = kd, E0 = e0 }, true);
            double ls = r.LandingMeters / length;
            rows.Add((nm, kd, e0, r.Share, ls));
            sb.AppendLine(FormattableString.Invariant($"Nm {nm:F2} Kd {kd:F2} E0 {e0:F2}: {Mix(r.Share)}; landings {ls * 100:F2} % of length {(InWindow(r.Share) ? "*" : "")}"));
        }
        return sb.ToString();
    }

    /// <summary>The text report of T3R-1 item 3 for the given constants. <paramref name="json"/> receives the same numbers.</summary>
    public static string Report(RiverNetwork net, RiverDangerSpans sp, RiverDangerConstants c, out string json)
    {
        var sb = new StringBuilder();
        RiverDangerResult before = Evaluate(net, sp, c, false), after = Evaluate(net, sp, c, true);
        int n = sp.Count;
        var len = new double[n];
        for (int i = 0; i < n; i++) len[i] = sp.Length(i);
        double total = len.Sum();
        sb.AppendLine(FormattableString.Invariant($"seed {net.Seed}: {n} spans, {net.Stems.Length} stems, {net.Reaches.Length} reaches, {total / 1000d:F0} km of river; ferocity {net.Ferocity:G}, channel depth {net.ChannelDepth:G}"));
        sb.AppendLine("constants: " + c);
        // Interim comparison: whole reaches, reach S, the T2R-6b law; and the stored sections as a check on the tool.
        RiverDangerSpans reaches = Cut(net, double.PositiveInfinity);
        RiverDangerResult interim = Evaluate(net, reaches, RiverDangerConstants.Interim, false);
        var stored = new double[4]; double storedTotal = 0;
        for (int r = 0; r < net.Reaches.Length; r++) { double l = net.Reaches[r].SEnd - net.Reaches[r].SStart; if (l <= 0) continue; stored[(int)net.Sections[r].Class] += l; storedTotal += l; }
        for (int k = 0; k < 4; k++) stored[k] /= Math.Max(1d, storedTotal);
        sb.AppendLine("interim law re-computed (reaches, reach S): " + Mix(interim.Share));
        sb.AppendLine("interim classes stored in the graph (RiverNetwork.Sections): " + Mix(stored));
        sb.AppendLine("class share by length before landings: " + Mix(before.Share));
        sb.AppendLine("class share by length after landings:  " + Mix(after.Share));
        sb.AppendLine("percentiles over length (p10/p50/p90/p99), before landings:");
        sb.AppendLine("  U (m/s) " + Pcts(before.U, len) + "; w (m) " + Pcts(before.W, len) + "; d (m) " + Pcts(before.D, len) + "; Vd (m/s) " + Pcts(before.Vd, len));
        sb.AppendLine("  Sh " + Pcts(sp.Sh, len) + "; Sr " + Pcts(sp.Sr, len));
        sb.AppendLine(FormattableString.Invariant($"largest w {before.W.Max():F0} m (after landings {after.W.Max():F0} m); widest span count above 300 m: {Enumerable.Range(0, n).Count(i => after.W[i] > 300)}"));

        string[] bandNames = { "under 0.5 %", "0.5-2 %", "2-5 %", "5-10 %", "over 10 %" };
        double[] bandEdges = { 0.005, 0.02, 0.05, 0.10 };
        sb.AppendLine("class share by own gradient Sr band (before landings):");
        for (int b = 0; b < 5; b++)
        {
            var s = new double[4]; double t = 0;
            for (int i = 0; i < n; i++) { int band = 0; while (band < 4 && sp.Sr[i] >= bandEdges[band]) band++; if (band != b) continue; s[(int)before.Class[i]] += len[i]; t += len[i]; }
            sb.AppendLine(FormattableString.Invariant($"  {bandNames[b],-12} {t / total * 100,5:F1} % of length: ") + (t > 0 ? Mix(s.Select(x => x / t).ToArray()) : "-"));
        }
        string[] qNames = { "Q under 20", "Q 20-100", "Q 100-500", "Q 500-2000", "Q over 2000" };
        double[] qEdges = { 20, 100, 500, 2000 };
        sb.AppendLine("class share by Q band (before landings):");
        for (int b = 0; b < 5; b++)
        {
            var s = new double[4]; double t = 0;
            for (int i = 0; i < n; i++) { int band = 0; while (band < 4 && sp.Q[i] >= qEdges[band]) band++; if (band != b) continue; s[(int)before.Class[i]] += len[i]; t += len[i]; }
            sb.AppendLine(FormattableString.Invariant($"  {qNames[b],-12} {t / total * 100,5:F1} % of length: ") + (t > 0 ? Mix(s.Select(x => x / t).ToArray()) : "-"));
        }

        // Dangerous runs and the exit walk, before landings.
        double dangerous = 0; int ends = 0;
        var succ = new int[n];
        for (int i = 0; i < n; i++) succ[i] = -1;
        foreach (int stem in sp.Order)
        {
            int f = sp.StemStart[stem], cnt = sp.StemCount[stem];
            for (int i = f; i < f + cnt - 1; i++) succ[i] = i + 1;
            RiverStem st = net.Stems[stem];
            if (cnt > 0 && st.Terminal == RiverTerminal.Confluence && st.TerminalId >= 0) succ[f + cnt - 1] = sp.SpanAt(st.TerminalId, st.JunctionS);
        }
        bool Danger(int i) => before.Class[i] >= RiverClass.Fierce;
        var remaining = new double[n];
        for (int k = sp.Order.Length - 1; k >= 0; k--)
        {
            int stem = sp.Order[k];
            for (int i = sp.StemStart[stem] + sp.StemCount[stem] - 1; i >= sp.StemStart[stem]; i--)
            {
                int j = succ[i];
                remaining[i] = j >= 0 && Danger(j) ? len[j] + remaining[j] : 0d;
            }
        }
        var runLengths = new List<double>();
        double over2 = 0, over5 = 0, over10 = 0;
        for (int i = 0; i < n; i++)
        {
            if (!Danger(i)) continue;
            dangerous += len[i];
            if (remaining[i] > 2000) over2 += len[i];
            if (remaining[i] > 5000) over5 += len[i];
            if (remaining[i] > 10000) over10 += len[i];
            if (succ[i] < 0 || !Danger(succ[i])) { ends++; runLengths.Add(before.DOut[i]); }
        }
        runLengths.Sort();
        double RunP(double q) => runLengths.Count == 0 ? 0 : runLengths[Math.Min(runLengths.Count - 1, (int)(q * runLengths.Count))] / 1000d;
        sb.AppendLine(FormattableString.Invariant($"dangerous (fierce or torrent) {dangerous / 1000d:F0} km = {dangerous / total * 100:F1} % of length; dangerous runs (maximal, before landings) {ends}, length p50/p90/max {RunP(0.5):F1}/{RunP(0.9):F1}/{(runLengths.Count == 0 ? 0 : runLengths[^1] / 1000d):F1} km"));
        sb.AppendLine(FormattableString.Invariant($"share of dangerous length with more than 2/5/10 km of dangerous water downstream: {over2 / Math.Max(1d, dangerous) * 100:F1} % / {over5 / Math.Max(1d, dangerous) * 100:F1} % / {over10 / Math.Max(1d, dangerous) * 100:F1} % (so {(1d - over5 / Math.Max(1d, dangerous)) * 100:F1} % already exits within 5 km)"));
        int natural = runLengths.Count(r => r <= RunLimitMeters);
        sb.AppendLine(FormattableString.Invariant($"runs ending within 5 km without a landing (by the walk, before landings) {natural} of {ends}; runs that needed a landing {ends - natural}"));
        sb.AppendLine(FormattableString.Invariant($"landings {after.Landings} spans, {after.LandingMeters / 1000d:F0} km = {after.LandingMeters / total * 100:F2} % of river length ({after.LandingMeters / Math.Max(1d, dangerous) * 100:F1} % of dangerous length); largest landing factor {after.Landing.Max():F1}; landing spans still dangerous at factor 64: {Enumerable.Range(0, n).Count(i => after.Landing[i] >= 63.9 && after.Class[i] >= RiverClass.Fierce)}"));
        sb.AppendLine("not applied here: the E4 exclusions near emitter sites, the Wall Engine stadium, the Weather Lung and the fresh spawn (need the site list; T3R-3).");

        json = FormattableString.Invariant($"{{\"seed\":{net.Seed},\"spans\":{n},\"length_km\":{total / 1000d:F1},\"interim\":{Arr(interim.Share)},\"before\":{Arr(before.Share)},\"after\":{Arr(after.Share)},\"landings\":{after.Landings},\"landing_km\":{after.LandingMeters / 1000d:F1},\"landing_share\":{after.LandingMeters / total:F5},\"dangerous_runs\":{ends},\"dangerous_km\":{dangerous / 1000d:F1},\"dangerous_over5km_share\":{over5 / Math.Max(1d, dangerous):F4}}}");
        return sb.ToString();
    }

    private static string Arr(double[] s) => FormattableString.Invariant($"[{s[0]:F4},{s[1]:F4},{s[2]:F4},{s[3]:F4}]");

    /// <summary>Picks a mountain river for the staircase close-up: a fierce span of median discharge and a gradient of about 7 %, with 300 m of stem on each side. Returns the span or -1.</summary>
    public static int PickStaircase(RiverNetwork net, RiverDangerSpans sp, RiverDangerResult r)
    {
        int best = -1; double bestScore = double.MaxValue;
        for (int i = 0; i < sp.Count; i++)
        {
            if (r.Class[i] != RiverClass.Fierce || r.Landing[i] > 1d || sp.Length(i) < 1000d || sp.Q[i] < 40d || sp.Q[i] > 250d || sp.Sr[i] < 0.05 || sp.Sr[i] > 0.10) continue;
            int stem = sp.Stem[i];
            double mid = 0.5 * (sp.S0[i] + sp.S1[i]), send = net.Reaches[net.Stems[stem].ReachStart + net.Stems[stem].ReachCount - 1].SEnd;
            if (mid < 1600d || mid > send - 1600d) continue;
            double score = Math.Abs(Math.Log(sp.Q[i] / 100d)) + 20d * Math.Abs(sp.Sr[i] - 0.07);
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    /// <summary>Writes the staircase data: a 3 km plan polyline (metres from the span middle) and a 300 m profile with the stored and the displayed level. A picture script draws them.</summary>
    public static string StaircaseCsv(RiverNetwork net, RiverDangerSpans sp, RiverDangerResult r, int span)
    {
        int stem = sp.Stem[span];
        double mid = 0.5 * (sp.S0[span] + sp.S1[span]);
        var sb = new StringBuilder();
        sb.AppendLine(FormattableString.Invariant($"# stem {stem} span {span} s_mid {mid:F0} Q {sp.Q[span]:F0} Sh {sp.Sh[span]:F4} Sr {sp.Sr[span]:F4} d {r.D[span]:F2} w {r.W[span]:F1} U {r.U[span]:F2} Vd {r.Vd[span]:F2}"));
        sb.AppendLine("plan,s,x_m,y_m,class,vd");
        net.Centre(stem, mid, out double a0, out double c0, out _, out _);
        for (double s = mid - 1500; s <= mid + 1500 + 1e-6; s += 10)
        {
            net.Centre(stem, s, out double a, out double c, out _, out _);
            int k = sp.SpanAt(stem, s);
            sb.AppendLine(FormattableString.Invariant($"plan,{s - mid:F0},{net.WrapDelta(a - a0):F2},{c - c0:F2},{(int)r.Class[k]},{r.Vd[k]:F2}"));
        }
        sb.AppendLine("profile,s,stored,displayed");
        for (double s = mid - 150; s <= mid + 150 + 1e-6; s += 0.5)
            sb.AppendLine(FormattableString.Invariant($"profile,{s - mid:F1},{RiverLevel.Stored(net, stem, s):F3},{RiverLevel.AtStem(net, stem, s):F3}"));
        return sb.ToString();
    }
}
