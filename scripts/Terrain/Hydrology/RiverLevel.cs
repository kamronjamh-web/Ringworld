using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>Stations of one stem kept for tests and the report: arc parameter, lowest of the three ground samples, centreline ground, profile before fill, fill flag.</summary>
public sealed class RiverStemStations
{
    public float[] S = Array.Empty<float>();
    public float[] Ground = Array.Empty<float>();
    public float[] CentreGround = Array.Empty<float>();
    /// <summary>Running minimum of the lowest ground minus the incision, before any outlet hold or receiving-level fill.</summary>
    public float[] Running = Array.Empty<float>();
    /// <summary>The profile the knots were fitted to (after hold and fill).</summary>
    public float[] Profile = Array.Empty<float>();
    public bool[] Filled = Array.Empty<bool>();
}

/// <summary>
/// T2R-5 (docs/work-orders/terrain-vegetation/T2.md, S7 "Level"): ground samples every 50 m (25 m above 3 % gradient) on the centreline and both bank lines,
/// the running-minimum profile from the sources downstream, knots within 0.75 m under it, and the displayed whole-metre pools and chutes.
/// </summary>
public static class RiverLevel
{
    public const double StationMeters = 50d;
    public const double SteepStationMeters = 25d;
    public const double SteepSlope = 0.03;
    public const double KnotBand = 0.75;
    private const double BandLower = 0.74, BandUpper = 0.012; // T2R-6: 7 mm more upper margin so whole-centimetre knots stay under the profile.
    public const double ChuteMeters = 10d;
    public const double OutletTailMeters = 50d;

    // ------------------------------------------------------------------ displayed level

    /// <summary>The stored profile W at s: linear between the stem's knots.</summary>
    public static double Stored(RiverNetwork net, int stem, double s)
    {
        RiverKnotTable k = net.Knots;
        int count = k.KnotCount(stem);
        if (count == 0) return double.NaN;
        k.Segment(stem, s, out double s0, out double w0, out double s1, out double w1);
        return Interpolate(s, s0, w0, s1, w1);
    }

    private static double Interpolate(double s, double s0, double w0, double s1, double w1)
    {
        if (s <= s0 || s1 == s0) return w0; // before the first knot, on a knot, or at the last one
        double t = (s - s0) / Math.Max(1e-9, s1 - s0);
        return w0 + (w1 - w0) * t;
    }

    /// <summary>Largest s at which the stored profile is still at or above the integer <paramref name="level"/> (where it goes strictly below it), or NaN if it never goes below.</summary>
    private static double LastAtOrAbove(RiverNetwork net, int stem, double level)
    {
        RiverKnotTable k = net.Knots;
        int last = k.KnotCount(stem) - 1;
        if (k.W(stem, last) >= level) return double.NaN;
        if (k.W(stem, 0) < level) return k.S(stem, 0);
        int lo = k.LastIndexWAtLeast(stem, level), hi = lo + 1;
        double w0 = k.W(stem, lo), s0 = k.S(stem, lo), drop = w0 - k.W(stem, hi);
        return drop <= 1e-12 ? s0 : s0 + (w0 - level) / drop * (k.S(stem, hi) - s0);
    }

    /// <summary>Displayed level of reach <paramref name="reach"/> at stem arc parameter s: whole-metre pools, chutes of at most 10 m, never above the stored profile (S7).</summary>
    public static double At(RiverNetwork net, int reach, double s) => AtStem(net, net.Reaches[reach].Stem, s);

    public static double AtStem(RiverNetwork net, int stem, double s) => AtStem(net, stem, s, out _);

    /// <summary>
    /// The displayed level, and how far the stored profile W stands above the whole-metre pools and chutes there (0 to 1 m, continuous). T2R-7 review: the
    /// operator lifts the valley floor by it away from the bank, so the floodplain follows the smooth profile instead of stepping with every pool.
    /// </summary>
    public static double AtStem(RiverNetwork net, int stem, double s, out double storedAbove)
    {
        double level = BeforeTail(net, stem, s, out double stored);
        storedAbove = double.IsNaN(stored) ? 0d : Math.Clamp(stored - level, 0d, 1d);
        double recv = net.ReceivingLevel[stem];
        if (!double.IsNaN(recv))
        {
            // Tail (S7, T2R-5b): the residual drop to the receiving level is taken off the profile, tapered at one metre per chute length upstream
            // of s_end - T. A profile steeper than a chute is kept (it shows as a cascade) instead of being capped by a 1:10 ramp from the mouth.
            double distance = Math.Max(0, net.Curves[stem].Length - net.TailMeters[stem] - s);
            level = Math.Max(recv, level - Math.Max(0, net.TailDrop[stem] - distance / ChuteMeters));
        }
        return level;
    }

    /// <summary>Whole-metre pools and chutes of the stored profile, without the tail rule.</summary>
    private static double BeforeTail(RiverNetwork net, int stem, double s) => BeforeTail(net, stem, s, out _);

    /// <summary>
    /// T2R-7c (S7): K = floor(W); the chute into K runs from the crossing before K + 1 to the crossing of K and is at most 10 m long, so where crossings
    /// are closer than 10 m the level is a continuous ramp about 1 m under W instead of dropping with every crossing that enters a 10 m look-ahead.
    /// </summary>
    internal static double BeforeTail(RiverNetwork net, int stem, double s, out double stored)
    {
        RiverKnotTable knots = net.Knots;
        double w, ahead;
        if (knots.KnotCount(stem) == 0) { w = double.NaN; ahead = double.NaN; }
        else
        {
            // One segment lookup serves s and s + 10 m when both lie in it (the same arithmetic as Stored).
            knots.Segment(stem, s, out double s0, out double w0, out double s1, out double w1);
            w = Interpolate(s, s0, w0, s1, w1);
            double sAhead = s + ChuteMeters;
            ahead = s1 == s0 || (sAhead >= s0 && sAhead < s1) ? Interpolate(sAhead, s0, w0, s1, w1) : Stored(net, stem, sAhead);
        }
        stored = w;
        double level = Math.Floor(w);
        if (!(level > ahead)) return level; // the next crossing is 10 m or more ahead
        double xK = LastAtOrAbove(net, stem, level);
        if (double.IsNaN(xK)) return level;
        double xPrev = LastAtOrAbove(net, stem, level + 1);
        double len = Math.Min(ChuteMeters, double.IsNaN(xPrev) ? ChuteMeters : xK - xPrev);
        if (len <= 1e-9) return level - 1; // coincident crossings (a vertical drop): the chute has no length
        return Math.Min(level, (level - 1) + (xK - s) / len);
    }

    /// <summary>The level before T2R-7c (10 m chutes that overlap on steep profiles); kept for the identity and negative-control checks in drainage_graph_test.</summary>
    internal static double BeforeTailLegacy(RiverNetwork net, int stem, double s, out double stored)
    {
        RiverKnotTable knots = net.Knots;
        double w, ahead;
        if (knots.KnotCount(stem) == 0) { w = double.NaN; ahead = double.NaN; }
        else
        {
            // One segment lookup serves s and s + 10 m when both lie in it (the same arithmetic as Stored).
            knots.Segment(stem, s, out double s0, out double w0, out double s1, out double w1);
            w = Interpolate(s, s0, w0, s1, w1);
            double sAhead = s + ChuteMeters;
            ahead = s1 == s0 || (sAhead >= s0 && sAhead < s1) ? Interpolate(sAhead, s0, w0, s1, w1) : Stored(net, stem, sAhead);
        }
        stored = w;
        double level = Math.Floor(w);
        for (int k = (int)Math.Floor(w), n = 0; k > ahead && n < 64; k--, n++)
        {
            double x = LastAtOrAbove(net, stem, k);
            if (double.IsNaN(x) || x < s || x > s + ChuteMeters) continue;
            level = Math.Min(level, (k - 1) + (x - s) / ChuteMeters);
        }
        return level;
    }

    // ------------------------------------------------------------------ lake indicator

    internal sealed class LakeIndicator
    {
        private readonly DrainageField _f;
        private readonly DrainageGridSpec _spec;
        private readonly int[] _lakeId;

        public LakeIndicator(DrainageField f, DrainageGrid grid) { _f = f; _spec = grid.Spec; _lakeId = f.LakeId.ToArray(); }

        private double J(int col, int row, int lake)
        {
            row = Math.Clamp(row, 0, _f.Height - 1);
            int strip = ((col % _spec.RingColumns) + _spec.RingColumns) % _spec.RingColumns - _spec.ColumnStart;
            if (strip < 0) strip += _spec.RingColumns;
            if (strip >= _f.Width) return -1;
            int cell = row * _f.Width + strip;
            if (lake < 0 ? _lakeId[cell] >= 0 : _lakeId[cell] == lake) return 1;
            for (int dir = 0; dir < 8; dir++)
            {
                int m = _f.Domain.GeometricNeighbor(cell, dir);
                if (m >= 0 && (lake < 0 ? _lakeId[m] >= 0 : _lakeId[m] == lake)) return 0;
            }
            return -1;
        }

        /// <summary>P of S8; with <paramref name="lake"/> at or above 0 the lake's own indicator (T2R-8: an outlet is held until it leaves its own lake's membership, not the membership of a lake it flows into).</summary>
        public double P(double around, double across, int lake = -1)
        {
            double gx = around / _spec.AroundStepMeters - 0.5, gy = across / _spec.AcrossStepMeters - 0.5;
            int x0 = (int)Math.Floor(gx), y0 = (int)Math.Floor(gy);
            double tx = gx - x0, ty = gy - y0;
            return (J(x0, y0, lake) * (1 - tx) + J(x0 + 1, y0, lake) * tx) * (1 - ty) + (J(x0, y0 + 1, lake) * (1 - tx) + J(x0 + 1, y0 + 1, lake) * tx) * ty;
        }
    }

    /// <summary>T2R-22 8f: the fall that the cap of a lake-end join made in its receiver, p50 / p90 / max and the ten largest with position.</summary>
    private static string LakeEndText(RiverNetwork net, List<(double Drop, int Receiver, int LakeStem, double S)> drops)
    {
        if (drops.Count == 0) return "none";
        double[] d = drops.Select(x => x.Drop).OrderBy(x => x).ToArray();
        var sb = new System.Text.StringBuilder(FormattableString.Invariant($"fall made in the receiver p50/p90/max {d[d.Length / 2]:F1}/{d[Math.Min(d.Length - 1, d.Length * 9 / 10)]:F1}/{d[^1]:F1} m; over 25 m: {d.Count(x => x > 25d)}; the ten largest:"));
        foreach (var x in drops.OrderByDescending(x => x.Drop).Take(10))
        {
            net.Centre(x.Receiver, x.S, out double around, out double across, out _, out _);
            sb.Append(FormattableString.Invariant($" [{x.Drop:F1} m, receiver stem {x.Receiver}, lake stem {x.LakeStem}, around {net.WrapAround(around) / 1000d:F3} km across {across / 1000d:F3} km]"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// T2R-8b: the arc length up to which a stem leaving (or running through) lake <paramref name="lake"/> holds its level. A stem that ends inside its lake (its last cell is a flooded cell of it) is
    /// held to its end; an outlet is held until its centreline, stepped every 10 m, leaves the lake's membership (the outlet point of the lake record). The bake, the graph records and the operator agree on it.
    /// </summary>
    internal static double HeldEnd(RiverNetwork net, LakeIndicator indicator, int stem, int lake, bool endsInLake)
    {
        double length = net.Curves[stem].Length, s = 0;
        if (endsInLake) return length;
        double inLakeEnd = stem < net.InLakeEnd.Length ? net.InLakeEnd[stem] : 0d; // the drowned channel is held along its whole in-lake part: the wander can carry it over a pinch of the membership indicator
        for (; s < length; s += 10)
        {
            net.Centre(stem, s, out double around, out double across, out _, out _);
            if (s >= inLakeEnd && indicator.P(net.WrapAround(around), across, lake) <= 0) break;
        }
        return Math.Min(s, length);
    }

    // ------------------------------------------------------------------ build

    private static void MakePoints(RiverNetwork net, int stem, ReadOnlySpan<float> s, Span<DrainagePoint> dst)
    {
        long c = net.CircumferenceM;
        int maxAcross = (int)net.WidthM - 1;
        for (int i = 0; i < s.Length; i++)
        {
            net.CentreFrame(stem, s[i], out double around, out double across, out double nx, out double ny);
            net.PathGeometry(stem, s[i], out double a, out double b);
            double t = a + b;
            for (int k = 0; k < 3; k++)
            {
                double off = k == 0 ? 0 : k == 1 ? t : -t;
                long ar = (long)Math.Round(around + nx * off) % c;
                if (ar < 0) ar += c;
                dst[i * 3 + k] = new DrainagePoint(ar, Math.Clamp((int)Math.Round(across + ny * off), 0, maxAcross));
            }
        }
    }

    private static float[] BaseStations(double length)
    {
        int n = (int)Math.Floor(length / StationMeters);
        var list = new List<float>(n + 2);
        for (int i = 0; i <= n; i++) list.Add((float)(i * StationMeters));
        if (length - n * StationMeters > 0.5) list.Add((float)length);
        else list[^1] = (float)length;
        return list.ToArray();
    }

    /// <summary>Samples the ground at the stations of every stem in chunks of about two million points; fills G (lowest of three) and Gc (centre).</summary>
    private static void Sample(RiverNetwork net, IDrainageHeightSource source, int workers, float[][] s, float[][] g, float[][] gc, IReadOnlyList<int> stems)
    {
        const int chunk = 2_000_000;
        int i = 0;
        while (i < stems.Count)
        {
            int first = i;
            long points = 0;
            while (i < stems.Count && (i == first || points + s[stems[i]].Length * 3L <= chunk)) { points += s[stems[i]].Length * 3L; i++; }
            var offsets = new long[i - first + 1];
            for (int k = first; k < i; k++) offsets[k - first + 1] = offsets[k - first] + s[stems[k]].Length * 3L;
            var positions = new DrainagePoint[points];
            var heights = new float[points];
            Parallel.For(first, i, new ParallelOptions { MaxDegreeOfParallelism = workers },
                k => MakePoints(net, stems[k], s[stems[k]], positions.AsSpan((int)offsets[k - first], s[stems[k]].Length * 3)));
            source.SamplePoints(positions, heights);
            Parallel.For(first, i, new ParallelOptions { MaxDegreeOfParallelism = workers }, k =>
            {
                int stem = stems[k];
                int o = (int)offsets[k - first], n = s[stem].Length;
                g[stem] = new float[n];
                gc[stem] = new float[n];
                for (int j = 0; j < n; j++)
                {
                    float c = heights[o + j * 3], l = heights[o + j * 3 + 1], r = heights[o + j * 3 + 2];
                    gc[stem][j] = c;
                    g[stem][j] = Math.Min(c, Math.Min(l, r));
                }
            });
        }
    }

    public static void Build(RiverNetwork net, DrainageField f, DrainageGrid grid, RiverBuildOptions options, CancellationToken cancellation, ref RiverBuildReport report)
    {
        IDrainageHeightSource source = options.Source ?? throw new ArgumentException("levels need a height source");
        int stemCount = net.Stems.Length, workers = Math.Max(1, options.Workers);
        var clock = Stopwatch.StartNew();
        var all = Enumerable.Range(0, stemCount).ToList();
        var s = new float[stemCount][];
        var g = new float[stemCount][];
        var gc = new float[stemCount][];
        for (int i = 0; i < stemCount; i++) s[i] = BaseStations(net.Curves[i].Length);
        Sample(net, source, workers, s, g, gc, all);

        // 25 m refinement where the centreline ground is steeper than 3 %.
        var mids = new float[stemCount][];
        var midIndex = new List<int>();
        for (int i = 0; i < stemCount; i++)
        {
            var list = new List<float>();
            for (int j = 0; j + 1 < s[i].Length; j++)
                if (Math.Abs(gc[i][j + 1] - gc[i][j]) / (s[i][j + 1] - s[i][j]) > SteepSlope && s[i][j + 1] - s[i][j] > SteepStationMeters + 0.5)
                    list.Add(0.5f * (s[i][j] + s[i][j + 1]));
            mids[i] = list.ToArray();
            if (list.Count > 0) midIndex.Add(i);
        }
        var ms = new float[stemCount][];
        var mg = new float[stemCount][];
        var mgc = new float[stemCount][];
        foreach (int i in midIndex) ms[i] = mids[i];
        Sample(net, source, workers, ms, mg, mgc, midIndex);
        foreach (int i in midIndex)
        {
            int n = s[i].Length + mids[i].Length;
            var ns = new float[n];
            var ng = new float[n];
            var ngc = new float[n];
            int a = 0, b = 0, o = 0;
            while (a < s[i].Length || b < mids[i].Length)
            {
                bool takeMid = b < mids[i].Length && (a >= s[i].Length || mids[i][b] < s[i][a]);
                if (takeMid) { ns[o] = mids[i][b]; ng[o] = mg[i][b]; ngc[o] = mgc[i][b]; b++; }
                else { ns[o] = s[i][a]; ng[o] = g[i][a]; ngc[o] = gc[i][a]; a++; }
                o++;
            }
            s[i] = ns; g[i] = ng; gc[i] = ngc;
        }
        double sampleSeconds = clock.Elapsed.TotalSeconds;
        cancellation.ThrowIfCancellationRequested();

        // Upstream-first order (reverse of receivers first).
        var children = new List<int>[stemCount];
        var order = new List<int>(stemCount);
        for (int i = 0; i < stemCount; i++)
            if (net.Stems[i].Terminal != RiverTerminal.Confluence) order.Add(i);
            else (children[net.Stems[i].TerminalId] ??= new List<int>()).Add(i);
        for (int h = 0; h < order.Count; h++) { List<int>? kids = children[order[h]]; if (kids != null) order.AddRange(kids); }
        order.Reverse();

        var lakes = new LakeIndicator(f, grid);
        var lakeLevels = f.Lakes.ToArray().Select(l => Math.Floor(l.Level)).ToArray();
        // T2R-8b item 5: where each stem's hold ends, from the stems and the indicator alone (the junctions of incoming stems are classified against it before the upstream-first pass).
        int[] fieldLake = f.LakeId.ToArray();
        int lakeEndJoins = 0; // T2R-22 8f
        var lakeEndDrops = new List<(double Drop, int Receiver, int LakeStem, double S)>();
        var heldEnd = new double[stemCount];
        Array.Fill(heldEnd, -1d);
        for (int i = 0; i < stemCount; i++)
        {
            RiverStem st = net.Stems[i];
            int upLake = net.Reaches[st.ReachStart].UpstreamLake;
            if (upLake < 0 || st.Terminal == RiverTerminal.Dead) continue;
            heldEnd[i] = HeldEnd(net, lakes, i, upLake, RiverGraph.EndsInLake(net, st, upLake, f)); // T2R-22: a stem that ends in its lake's water, a strait included, is held to its end
        }
        var profile = new float[stemCount][];
        var running = new float[stemCount][];
        var filled = new bool[stemCount][];
        var endValue = new double[stemCount];
        var heldStations = new int[stemCount];
        var incoming = new List<(double S, int Stem)>[stemCount];
        foreach (int i in order)
        {
            RiverStem st = net.Stems[i];
            int n = s[i].Length;
            var w = new float[n];
            var rm = new float[n];
            var fill = new bool[n];
            double m = double.PositiveInfinity;
            int upLake = net.Reaches[st.ReachStart].UpstreamLake;
            bool holding = upLake >= 0 && st.Terminal != RiverTerminal.Dead;
            double hold = holding ? lakeLevels[upLake] : 0;
            if (holding) m = hold;
            List<(double S, int Stem)>? inc = incoming[i];
            inc?.Sort((x, y) => x.S.CompareTo(y.S));
            int ptr = 0;
            bool extraHeld = false;
            double plain = double.PositiveInfinity;
            // T2R-22 8f: a stem that is lake water to its end joins at its lake's level. Its cap acts from the last station at or before the junction and stands BandLower + BandUpper over the lake level, so the receiver displays the lake level
            // there and the lake stem's tail drop is 0. While the receiver holds, the T2R-8b rule stays as it is.
            bool LakeEnd(int k)
            {
                int ul = net.Reaches[net.Stems[k].ReachStart].UpstreamLake;
                return ul >= 0 && net.Stems[k].Terminal != RiverTerminal.Dead && heldEnd[k] >= net.Curves[k].Length - 1e-6 && endValue[k] <= lakeLevels[ul] + BandUpper + 1e-3;
            }
            double LakeCap(int k) => lakeLevels[net.Reaches[net.Stems[k].ReachStart].UpstreamLake] + BandLower + BandUpper;
            double[]? early = null;
            List<(int Stem, double S)>? earlyJoins = null;
            if (inc != null)
                foreach ((double js, int k) in inc)
                {
                    if (!LakeEnd(k)) continue;
                    lakeEndJoins++;
                    int jb = 0;
                    while (jb + 1 < n && s[i][jb + 1] <= js) jb++;
                    early ??= Enumerable.Repeat(double.PositiveInfinity, n).ToArray();
                    early[jb] = Math.Min(early[jb], LakeCap(k));
                    (earlyJoins ??= new()).Add((k, js));
                }
            for (int j = 0; j < n; j++)
            {
                if (early != null && !double.IsPositiveInfinity(early[j]) && !(holding && (s[i][j] < heldEnd[i] + ChuteMeters || heldEnd[i] >= net.Curves[i].Length - 1e-6)))
                {
                    if (!double.IsPositiveInfinity(m) && m > early[j] + 1e-9 && earlyJoins != null)
                    {
                        // The cap lowers the receiver here by m - cap + (BandLower + BandUpper): the fall to the lake level.
                        double drop = m - (early[j] - BandLower - BandUpper);
                        lock (lakeEndDrops) lakeEndDrops.Add((drop, i, earlyJoins[0].Stem, earlyJoins[0].S));
                    }
                    m = Math.Min(m, early[j]); plain = Math.Min(plain, early[j]);
                }
                while (inc != null && ptr < inc.Count && inc[ptr].S <= s[i][j])
                {
                    // T2R-8b: while the stem holds, an incoming stem's end value is raised to the hold, or the outlet would drop by the depth of the inlet's last station as soon as the hold ends.
                    double v = holding ? Math.Max(endValue[inc[ptr].Stem], hold) : LakeEnd(inc[ptr].Stem) ? LakeCap(inc[ptr].Stem) : endValue[inc[ptr].Stem];
                    m = Math.Min(m, v); plain = Math.Min(plain, v); ptr++;
                }
                if (holding)
                {
                    // T2R-8b: held at the stations up to the stem's held end (HeldEnd: the same 10 m stepping as the lake records, so the level, the records and the operator agree). T2R-8: the knots sit BandUpper under the profile, so a held level of whole metres would display one metre low; stand BandUpper above it and the knot lands on the lake level.
                    bool inHeld = s[i][j] < heldEnd[i] + ChuteMeters || heldEnd[i] >= net.Curves[i].Length - 1e-6;
                    // The stations up to a chute length past the held end, and the first one after, are held too: the knots after the last held station slope away from the lake level, which would display a metre low under the 10 m chute before the held end.
                    if (!inHeld && j > 0 && heldStations[i] == j && !extraHeld) { inHeld = true; extraHeld = true; }
                    if (inHeld) { w[j] = (float)(hold + BandUpper); rm[j] = float.NaN; fill[j] = true; heldStations[i] = j + 1; continue; }
                    holding = false;
                }
                double ground = g[i][j] - RiverHydraulics.LevelIncision;
                m = Math.Min(m, ground);
                plain = Math.Min(plain, ground);
                rm[j] = (float)plain;
                w[j] = (float)m;
            }
            double recvLevel = st.Terminal switch { RiverTerminal.Sea => 0d, RiverTerminal.Lake => lakeLevels[st.TerminalId], _ => double.NaN };
            if (st.Terminal == RiverTerminal.Confluence)
            {
                // A confluence in the receiver's held part joins the lake's water: its level is the lake level there, like a Terminal Lake stem.
                int recv = st.TerminalId, recvLake = net.Reaches[net.Stems[recv].ReachStart].UpstreamLake;
                if (recvLake >= 0 && heldEnd[recv] >= 0 && st.JunctionS <= heldEnd[recv] + 1e-6) recvLevel = lakeLevels[recvLake];
            }
            if (!double.IsNaN(recvLevel))
                for (int j = 0; j < n; j++) if (w[j] < recvLevel) { w[j] = (float)recvLevel; fill[j] = true; }
            profile[i] = w; running[i] = rm; filled[i] = fill;
            endValue[i] = w[n - 1];
            if (st.Terminal == RiverTerminal.Confluence) (incoming[st.TerminalId] ??= new()).Add((st.JunctionS, i));
        }

        // Knots.
        var knotLists = new List<RiverKnot>[stemCount];
        Parallel.For(0, stemCount, new ParallelOptions { MaxDegreeOfParallelism = workers }, i => knotLists[i] = FitKnots(s[i], profile[i], heldStations[i]));
        var knotCm = new List<(int S, int W)>[stemCount];
        int knotTotal = 0;
        for (int i = 0; i < stemCount; i++)
        {
            knotCm[i] = RiverKnotTable.Quantise(knotLists[i]);
            net.Stems[i].KnotStart = knotTotal;
            net.Stems[i].KnotCount = knotCm[i].Count;
            knotTotal += knotCm[i].Count;
        }
        net.Knots = RiverKnotTable.FromCm(knotCm);

        ComputeTails(net, lakeLevels);
        double levelSeconds = clock.Elapsed.TotalSeconds - sampleSeconds;

        // Cut depth along paths: centreline ground minus the bed (displayed level minus depth), weighted by station spacing; histogram of 0.1 m bins.
        const int bins = 4000;
        var hist = new double[bins + 1];
        double total = 0, over40 = 0, over15 = 0, maxCut = 0, fillStations = 0, lakeTotal = 0, lakeOver40 = 0, lakeMax = 0;
        int stations = 0;
        object gate = new();
        Parallel.For(0, stemCount, new ParallelOptions { MaxDegreeOfParallelism = workers }, i =>
        {
            var local = new double[bins + 1];
            double lt = 0, lo = 0, l15 = 0, lm = double.NegativeInfinity, lf = 0, kt = 0, ko = 0, km = 0;
            int ln = s[i].Length;
            double inLake = i < net.InLakeEnd.Length ? net.InLakeEnd[i] : 0d;
            for (int j = 0; j < ln; j++)
            {
                double span = 0.5 * ((j + 1 < ln ? s[i][j + 1] : s[i][j]) - (j > 0 ? s[i][j - 1] : s[i][j]));
                if (span <= 0) continue;
                int reach = net.ReachAt(i, s[i][j]);
                double bed = AtStem(net, i, s[i][j]) - net.Sections[reach].Depth;
                double cut = gc[i][j] - bed;
                // T2R-8b item 8: the in-lake parts of stems are counted apart.
                if (inLake > 0d && s[i][j] <= inLake) { if (filled[i][j]) lf++; kt += span; if (cut > 40) ko += span; km = Math.Max(km, cut); continue; }
                local[(int)Math.Clamp(Math.Round(cut * 10), 0, bins)] += span;
                lt += span; if (cut > 40) lo += span; if (cut > 15) l15 += span; lm = Math.Max(lm, cut);
                if (filled[i][j]) lf++;
            }
            lock (gate)
            {
                for (int k = 0; k <= bins; k++) hist[k] += local[k];
                total += lt; over40 += lo; over15 += l15; maxCut = Math.Max(maxCut, lm); fillStations += lf; stations += ln;
                lakeTotal += kt; lakeOver40 += ko; lakeMax = Math.Max(lakeMax, km);
            }
        });
        double Pct(double q)
        {
            double run = 0;
            for (int k = 0; k <= bins; k++) { run += hist[k]; if (run >= q * total) return k / 10d; }
            return bins / 10d;
        }
        net.Stations = options.KeepStations
            ? Enumerable.Range(0, stemCount).Select(i => new RiverStemStations { S = s[i], Ground = g[i], CentreGround = gc[i], Running = running[i], Profile = profile[i], Filled = filled[i] }).ToArray()
            : null;
        report = report with
        {
            Knots = net.Knots.Count, Stations = stations, CutP50 = Pct(0.5), CutP90 = Pct(0.9), CutMax = maxCut,
            CutOver40Share = total > 0 ? over40 / total : 0, CutOver15Share = total > 0 ? over15 / total : 0, FillShareOfLevel = stations > 0 ? fillStations / stations : 0,
            InLakeCutOver40Share = lakeTotal > 0 ? lakeOver40 / lakeTotal : 0, InLakeCutMax = lakeMax,
            LakeEndJoins = lakeEndJoins, LakeEndLowered = lakeEndDrops.Count, LakeEndText = LakeEndText(net, lakeEndDrops),
            SampleSeconds = sampleSeconds, LevelSeconds = levelSeconds,
        };
    }

    /// <summary>
    /// Receiving levels and tail parameters per stem (S7 tail rule), receivers first: derived from the stems, knots and whole-metre lake levels only, so the
    /// bake and the graph loader (T2R-6) run the same code and agree exactly.
    /// </summary>
    public static void ComputeTails(RiverNetwork net, double[] lakeLevels)
    {
        int stemCount = net.Stems.Length;
        var children = new List<int>[stemCount];
        var receiversFirst = new List<int>(stemCount);
        for (int i = 0; i < stemCount; i++)
            if (net.Stems[i].Terminal != RiverTerminal.Confluence) receiversFirst.Add(i);
            else (children[net.Stems[i].TerminalId] ??= new List<int>()).Add(i);
        for (int h = 0; h < receiversFirst.Count; h++) { List<int>? kids = children[receiversFirst[h]]; if (kids != null) receiversFirst.AddRange(kids); }
        foreach (int i in receiversFirst)
        {
            RiverStem st = net.Stems[i];
            switch (st.Terminal)
            {
                case RiverTerminal.Sea: net.ReceivingLevel[i] = 0; net.TailMeters[i] = OutletTailMeters; break;
                case RiverTerminal.Lake: net.ReceivingLevel[i] = lakeLevels[st.TerminalId]; net.TailMeters[i] = OutletTailMeters; break;
                case RiverTerminal.Confluence when net.Reaches[st.ReachStart].UpstreamLake >= 0 && net.Reaches[net.Stems[st.TerminalId].ReachStart].UpstreamLake == net.Reaches[st.ReachStart].UpstreamLake:
                    // T2R-22 8a: a stem of a lake that joins another stem of the same lake (an in-lake merge, or a strait that leaves the lake across a dry cell and returns) ends in that lake's water, whatever the receiving stem does at that point.
                    net.ReceivingLevel[i] = lakeLevels[net.Reaches[st.ReachStart].UpstreamLake]; net.TailMeters[i] = OutletTailMeters; break;
                case RiverTerminal.Confluence:
                {
                    net.ReceivingLevel[i] = AtStem(net, st.TerminalId, st.JunctionS);
                    int reach = net.ReachAt(st.TerminalId, st.JunctionS);
                    net.PathGeometry(st.TerminalId, st.JunctionS, out double a, out double b);
                    RiverSection sec = net.Sections[reach];
                    net.TailMeters[i] = a + b + RiverHydraulics.FloodplainWidth(sec.Width, sec.Gradient, net.ValleyWidth) + 50d;
                    break;
                }
                default: net.ReceivingLevel[i] = double.NaN; break;
            }
            if (!double.IsNaN(net.ReceivingLevel[i]))
                net.TailDrop[i] = Math.Max(0, BeforeTail(net, i, Math.Max(0, net.Curves[i].Length - net.TailMeters[i])) - net.ReceivingLevel[i]);
        }
    }

    /// <summary>Greedy fit: extend a segment from its start knot while one slope keeps every station inside [W - 0.74, W - 0.005]; slopes never rise above zero.</summary>
    internal static List<RiverKnot> FitKnots(float[] s, float[] w, int flatPrefix = 0)
    {
        var knots = new List<RiverKnot>();
        int n = s.Length;
        double s0 = s[0], v0 = w[0] - BandUpper;
        knots.Add(new RiverKnot { S = (float)s0, W = (float)v0 });
        double lo = double.NegativeInfinity, hi = 0;
        int first = 1;
        // T2R-8: the stations held at a lake level stay one flat segment (a mid-band slope would sag the displayed level a whole metre under the lake at once).
        if (flatPrefix >= 2 && s[flatPrefix - 1] > s0 + 1e-9)
        {
            s0 = s[flatPrefix - 1];
            knots.Add(new RiverKnot { S = (float)s0, W = (float)v0 });
            first = flatPrefix;
        }
        for (int j = first; j < n; j++)
        {
            double ds = s[j] - s0;
            if (ds <= 1e-9) continue;
            double l = (w[j] - BandLower - v0) / ds, h = Math.Min((w[j] - BandUpper - v0) / ds, 0);
            double nlo = Math.Max(lo, l), nhi = Math.Min(hi, h);
            if (nlo > nhi + 1e-12 && s[j - 1] > s0 + 1e-9 && !double.IsNegativeInfinity(lo))
            {
                double slope = 0.5 * (lo + hi);
                double s1 = s[j - 1], v1 = v0 + slope * (s1 - s0);
                knots.Add(new RiverKnot { S = (float)s1, W = (float)v1 });
                s0 = s1; v0 = v1; lo = double.NegativeInfinity; hi = 0;
                j--;
                continue;
            }
            lo = nlo; hi = Math.Max(nhi, nlo);
        }
        if (!double.IsNegativeInfinity(lo) && s[n - 1] > s0 + 1e-9)
            knots.Add(new RiverKnot { S = s[n - 1], W = (float)(v0 + 0.5 * (lo + hi) * (s[n - 1] - s0)) });
        else if (s[n - 1] > s0 + 1e-9) knots.Add(new RiverKnot { S = s[n - 1], W = (float)v0 });
        return knots;
    }
}
