using System;
using System.Collections.Generic;
using System.IO;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// The stored record of a span (T3.md Spec E1, 24 bytes): a stretch of one reach of at most two cells. <see cref="Q"/> is the discharge at the span's last cell, <see cref="Sh"/> the
/// hydraulic gradient over the 5 km window of the stored profile, <see cref="Landing"/> 1 for none or the factor of E4 above 1. Spans are sorted by reach, then by <see cref="SStart"/>,
/// and tile their reach.
/// </summary>
public struct RiverSpan
{
    public int Reach;
    public float SStart, SEnd, Q, Sh, Landing;
}

/// <summary>
/// T3R-3 (T3.md Spec E1 to E4): the spans of a network and everything derived from them at load: the own gradient Sr, the calibrated section (E2), the danger speed, class and
/// whiteness (E3), and the exit valley (E4). The bake and the loader build it through <see cref="Create"/> from the same stored records, so a loaded graph answers exactly like a baked one.
/// Channel and valley values are read through <see cref="RiverNetwork.ChannelAt"/>. Immutable after construction.
/// </summary>
public sealed class RiverSpanTable
{
    /// <summary>E4: the longest run of dangerous water a stem may carry before a span becomes a landing, and the danger speed a landing is softened to.</summary>
    public const double RunLimitMeters = 5000d, LandingSpeed = 3d;
    /// <summary>E4: the valley slope limit of a landing and of the first calm or swift span after a dangerous one.</summary>
    public const double ExitValleySlope = 0.6;

    public RiverSpan[] Spans { get; }
    public int Count => Spans.Length;
    /// <summary>First span and span count of each reach.</summary>
    public int[] ReachFirst { get; }
    public int[] ReachCount { get; }
    /// <summary>Own gradient over the span (Sr), from the stored profile.</summary>
    public float[] Sr { get; }
    public float[] HalfWidth { get; }
    public float[] BankWidth { get; }
    public float[] Depth { get; }
    public float[] EdgeDepth { get; }
    public float[] Floodplain { get; }
    public float[] ValleySlope { get; }
    /// <summary>Mean speed U of the channel (Q = U w d).</summary>
    public float[] Speed { get; }
    /// <summary>The speed share eta of E3 and the danger speed Vd.</summary>
    public float[] Eta { get; }
    public float[] DangerSpeed { get; }
    /// <summary>Whiteness smoothstep(2.5, 7, Vd) of E3.</summary>
    public float[] Whiteness { get; }
    public RiverClass[] Class { get; }
    /// <summary>Spans whose valley slope is limited (E4): a landing, or the first span that is not dangerous after a dangerous one.</summary>
    public bool[] ExitValley { get; }
    public int LandingCount { get; }
    public double LandingMeters { get; }
    public double LengthMeters { get; }

    private RiverSpanTable(RiverSpan[] spans, int reaches)
    {
        Spans = spans;
        int n = spans.Length;
        ReachFirst = new int[reaches]; ReachCount = new int[reaches];
        Sr = new float[n]; HalfWidth = new float[n]; BankWidth = new float[n]; Depth = new float[n]; EdgeDepth = new float[n]; Floodplain = new float[n]; ValleySlope = new float[n];
        Speed = new float[n]; Eta = new float[n]; DangerSpeed = new float[n]; Whiteness = new float[n]; Class = new RiverClass[n]; ExitValley = new bool[n];
    }

    /// <summary>Resident bytes of the stored records and the derived arrays.</summary>
    public long ResidentBytes => 24L * Count + 4L * 11 * Count + Count + Count + 8L * ReachFirst.Length;

    /// <summary>The span of <paramref name="reach"/> that contains arc parameter <paramref name="s"/> (the last one at or beyond its end).</summary>
    public int SpanAt(int reach, double s)
    {
        int lo = ReachFirst[reach], hi = lo + ReachCount[reach] - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (Spans[mid].SStart <= s) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>
    /// Derives the table from stored records. With <paramref name="resolveLandings"/> (the bake) the exit rule and the exclusions of E4 set <see cref="RiverSpan.Landing"/> on the
    /// records in <paramref name="spans"/>; without it (the loader) the stored factors are used as they are. <paramref name="excluded"/> flags spans inside an exclusion distance.
    /// Deterministic: stems are visited in the topological order of the stem list, tributaries before the stem they join.
    /// </summary>
    internal static RiverSpanTable Create(RiverNetwork net, RiverSpan[] spans, bool[]? excluded, bool resolveLandings, RiverDangerConstants? constants = null)
    {
        RiverDangerConstants c = constants ?? RiverDangerConstants.Default;
        int n = spans.Length, reaches = net.Reaches.Length, stems = net.Stems.Length;
        var t = new RiverSpanTable(spans, reaches);
        for (int i = 0; i < n; i++)
        {
            int r = spans[i].Reach;
            if ((uint)r >= (uint)reaches || (i > 0 && (spans[i - 1].Reach > r || (spans[i - 1].Reach == r && !(spans[i].SStart >= spans[i - 1].SEnd - 1e-3f)))))
                throw new InvalidDataException("spans out of order");
            if (t.ReachCount[r]++ == 0) t.ReachFirst[r] = i;
            if (!(spans[i].SEnd > spans[i].SStart) || !(spans[i].Q >= 0f) || !(spans[i].Sh > 0f) || !(spans[i].Landing >= 1f) || spans[i].Landing > 64.01f) throw new InvalidDataException("bad span");
        }
        for (int r = 0; r < reaches; r++) if (t.ReachCount[r] == 0) throw new InvalidDataException("a reach has no span");
        bool hasKnots = net.Knots.Count > 0;
        
        for (int i = 0; i < n; i++)
        {
            RiverSpan sp = spans[i];
            int stem = net.Reaches[sp.Reach].Stem;
            double drop = hasKnots && net.Knots.KnotCount(stem) > 0 ? RiverLevel.Stored(net, stem, sp.SStart) - RiverLevel.Stored(net, stem, sp.SEnd) : 0d;
            t.Sr[i] = double.IsNaN(drop) ? 0f : (float)Math.Max(0d, drop / (sp.SEnd - sp.SStart));
        }

        // Joins and the topological order of the stems (tributaries first).
        var joins = new List<(double J, int Trib)>?[stems];
        var indegree = new int[stems];
        for (int i = 0; i < stems; i++)
        {
            RiverStem st = net.Stems[i];
            if (st.Terminal != RiverTerminal.Confluence || st.TerminalId < 0 || st.TerminalId >= stems) continue;
            (joins[st.TerminalId] ??= new List<(double, int)>()).Add((st.JunctionS, i));
            indegree[st.TerminalId]++;
        }
        for (int i = 0; i < stems; i++) joins[i]?.Sort((a, b) => a.J != b.J ? a.J.CompareTo(b.J) : a.Trib.CompareTo(b.Trib));
        var order = new List<int>(stems);
        var queue = new Queue<int>();
        for (int i = 0; i < stems; i++) if (indegree[i] == 0) queue.Enqueue(i);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            order.Add(i);
            RiverStem st = net.Stems[i];
            if (st.Terminal == RiverTerminal.Confluence && st.TerminalId >= 0 && st.TerminalId < stems && --indegree[st.TerminalId] == 0) queue.Enqueue(st.TerminalId);
        }
        if (order.Count != stems) throw new InvalidDataException("stems form a cycle");

        double fer = net.Ferocity, chDepth = net.ChannelDepth, valleyWidth = net.ValleyWidth;
        var stemOut = new double[stems];
        int landings = 0;
        double landingMeters = 0d, length = 0d;
        foreach (int stem in order)
        {
            RiverStem st = net.Stems[stem];
            int first = t.ReachFirst[st.ReachStart], last = t.ReachFirst[st.ReachStart + st.ReachCount - 1] + t.ReachCount[st.ReachStart + st.ReachCount - 1] - 1;
            List<(double J, int Trib)>? joined = joins[stem];
            int jp = 0;
            double previousOut = 0d;
            for (int i = first; i <= last; i++)
            {
                ref RiverSpan sp = ref spans[i];
                double dIn = previousOut, len = sp.SEnd - sp.SStart, sh = Math.Max(sp.Sh, 1e-4f), sr = t.Sr[i], q = sp.Q;
                if (joined != null)
                    while (jp < joined.Count && (joined[jp].J < sp.SEnd || i == last)) dIn = Math.Max(dIn, stemOut[joined[jp++].Trib]);
                double l = sp.Landing;
                RiverDangerChannel ch = RiverDanger.Channel(c, q, sh, chDepth, fer, l);
                double vd = RiverDanger.DangerSpeed(c, ch.Speed, ch.Depth, sr, fer, l);
                RiverClass cls = RiverDanger.Classify(vd);
                bool dangerous = cls >= RiverClass.Fierce && sp.Landing <= 1f;
                if (resolveLandings && dangerous && ((excluded != null && excluded[i]) || (dIn > 0d && dIn + len > RunLimitMeters)))
                {
                    sp.Landing = (float)RiverDanger.LandingFactor(c, q, sh, chDepth, fer, sr, LandingSpeed);
                    l = sp.Landing;
                    ch = RiverDanger.Channel(c, q, sh, chDepth, fer, l);
                    vd = RiverDanger.DangerSpeed(c, ch.Speed, ch.Depth, sr, fer, l);
                    cls = RiverDanger.Classify(vd);
                    dangerous = false;
                }
                bool landing = sp.Landing > 1f;
                bool exit = landing || (dIn > 0d && !dangerous);
                double slope = RiverHydraulics.ValleySlope(sh, valleyWidth);
                if (exit) slope = Math.Min(slope, ExitValleySlope);
                t.HalfWidth[i] = (float)RiverHydraulics.HalfWidth(ch.Width);
                t.BankWidth[i] = (float)RiverHydraulics.BankWidth(ch.Width);
                t.Depth[i] = (float)ch.Depth;
                t.EdgeDepth[i] = (float)RiverHydraulics.EdgeDepth(ch.Depth);
                t.Floodplain[i] = (float)RiverHydraulics.FloodplainWidth(ch.Width, sh, valleyWidth);
                t.ValleySlope[i] = (float)slope;
                t.Speed[i] = (float)ch.Speed;
                t.Eta[i] = (float)RiverDanger.Eta(c, ch.Depth, l);
                t.DangerSpeed[i] = (float)vd;
                t.Whiteness[i] = (float)RiverDanger.Whiteness(vd);
                t.Class[i] = cls;
                t.ExitValley[i] = exit;
                if (landing) { landings++; landingMeters += len; }
                length += len;
                previousOut = dangerous ? dIn + len : 0d;
            }
            stemOut[stem] = previousOut;
        }
        return t.WithTotals(landings, landingMeters, length);
    }

    /// <summary>The span that follows each span downstream: the next one on its stem, or for the last span of a stem that joins another the span of the receiving stem at the junction; -1 at a lake, the sea or a dry pan.</summary>
    public int[] Successors(RiverNetwork net)
    {
        var next = new int[Count];
        Array.Fill(next, -1);
        for (int stem = 0; stem < net.Stems.Length; stem++)
        {
            RiverStem st = net.Stems[stem];
            int lastReach = st.ReachStart + st.ReachCount - 1, last = ReachFirst[lastReach] + ReachCount[lastReach] - 1;
            for (int i = ReachFirst[st.ReachStart]; i < last; i++) next[i] = i + 1;
            if (st.Terminal == RiverTerminal.Confluence && st.TerminalId >= 0 && st.TerminalId < net.Stems.Length)
                next[last] = SpanAt(net.ReachAt(st.TerminalId, st.JunctionS), st.JunctionS);
        }
        return next;
    }

    /// <summary>
    /// For each span, the length of dangerous water that follows its downstream end without a break (E4): the next span if it is dangerous, its length, and so on. Pure over the arrays
    /// so the exit check can be run on fabricated stems.
    /// </summary>
    public static double[] DangerousRunAfter(int[] successor, bool[] dangerous, double[] length)
    {
        int n = successor.Length;
        var run = new double[n];
        var done = new bool[n];
        var stack = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            if (done[i]) continue;
            int cur = i;
            stack.Clear();
            while (cur >= 0 && !done[cur]) { stack.Push(cur); cur = successor[cur]; }
            while (stack.Count > 0)
            {
                int k = stack.Pop(), s = successor[k];
                run[k] = s >= 0 && dangerous[s] ? length[s] + run[s] : 0d;
                done[k] = true;
            }
        }
        return run;
    }

    /// <summary>Fierce or torrent with no landing (E4).</summary>
    public bool IsDangerous(int span) => Class[span] >= RiverClass.Fierce && Spans[span].Landing <= 1f;

    private RiverSpanTable WithTotals(int landings, double landingMeters, double length) => new(this, landings, landingMeters, length);

    private RiverSpanTable(RiverSpanTable from, int landings, double landingMeters, double length)
    {
        Spans = from.Spans; ReachFirst = from.ReachFirst; ReachCount = from.ReachCount; Sr = from.Sr; HalfWidth = from.HalfWidth; BankWidth = from.BankWidth; Depth = from.Depth;
        EdgeDepth = from.EdgeDepth; Floodplain = from.Floodplain; ValleySlope = from.ValleySlope; Speed = from.Speed; Eta = from.Eta; DangerSpeed = from.DangerSpeed;
        Whiteness = from.Whiteness; Class = from.Class; ExitValley = from.ExitValley;
        LandingCount = landings; LandingMeters = landingMeters; LengthMeters = length;
    }
}
