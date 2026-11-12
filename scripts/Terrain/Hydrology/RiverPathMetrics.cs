using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace RingworldTechDemo.Terrain.Hydrology;

public readonly record struct PathPoint(double X, double Y)
{
    public static PathPoint operator +(PathPoint a, PathPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static PathPoint operator -(PathPoint a, PathPoint b) => new(a.X - b.X, a.Y - b.Y);
    public static PathPoint operator *(PathPoint a, double t) => new(a.X * t, a.Y * t);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>One straight piece of a path network; <see cref="Next"/> is the downstream segment or -1.</summary>
public sealed record PathSegment(int Id, int Next, PathPoint A, PathPoint B)
{
    public double Length { get; } = (B - A).Length;
    public PathPoint At(double distance) => Length <= 0 ? A : A + (B - A) * (distance / Length);
}

/// <summary>Coastal mask (one bit per grid cell, lsb first, row major) on the grid the paths are measured in.</summary>
public sealed record PathGrid(int Width, int Height, double Dx, double Dy, byte[] Mask)
{
    public bool Coastal(int cell) => (Mask[cell >> 3] & (1 << (cell & 7))) != 0;
}

public sealed record PathScope(double LengthKm, double FlaggedKm, double Share);

/// <summary>The D4 numbers (docs/t2-river-reset.md 4.5): straight-run share overall and coastal, and the two bend checks.</summary>
public sealed record PathMetrics(PathScope Overall, PathScope Coastal, PathScope SharpOverall, PathScope SharpCoastal, PathScope AlternatingOverall, PathScope AlternatingCoastal)
{
    public string ToText() => string.Concat(FormattableString.Invariant(
        $"D4 straight runs: overall {Overall.Share:P4} ({Overall.FlaggedKm:F1} of {Overall.LengthKm:F1} km), coastal {Coastal.Share:P4} ({Coastal.FlaggedKm:F1} of {Coastal.LengthKm:F1} km); "),
        FormattableString.Invariant($"sharp bends overall {SharpOverall.Share:P4} ({SharpOverall.FlaggedKm:F1} km) coastal {SharpCoastal.Share:P4} ({SharpCoastal.FlaggedKm:F1} km); "),
        FormattableString.Invariant($"alternating overall {AlternatingOverall.Share:P4} ({AlternatingOverall.FlaggedKm:F1} km) coastal {AlternatingCoastal.Share:P4} ({AlternatingCoastal.FlaggedKm:F1} km)"));
}

/// <summary>A polyline that ends on a vertex of another polyline (a tributary) or nowhere (an outlet).</summary>
public sealed record PathPolyline(PathPoint[] Points, int Receiver, int ReceiverVertex);

/// <summary>
/// T2R-5 (docs/work-orders/terrain-vegetation/T2.md, docs/t2-river-reset.md 4.5): the approved straight-run criterion on polylines. A window of 6 km
/// (phase every 250 m from the outlet, plus one at each source) is flagged when its chord is at least 99 % of its arc and it stays within 150 m of the chord;
/// the flagged share is the union of flagged windows over the total length, overall and in the coastal mask. Sharp (90 degrees over 1 km chords) and
/// alternating bends are measured the same way. It is a port of the frozen baseline's evaluator (tools fixture d4_baseline) onto arbitrary polylines.
/// </summary>
public static class RiverPathMetrics
{
    private const double Tolerance = 1e-6;
    private const double WindowLength = 6000, PhaseStep = 250, MinimumChord = 5940, MaximumDeviation = 150;
    private const double BendStep = 1000, SharpAngle = 90, ParallelAngle = 5, AlternationAngle = 22.5;
    public const double SampleMeters = 25d;

    private readonly record struct Interval(double Start, double End);
    private readonly record struct Span(int Edge, double Start, double End);
    private sealed record Trace(List<Span> Spans, List<PathPoint> Points);
    private readonly record struct Coverage(double Overall, double Coastal)
    {
        public static Coverage operator +(Coverage a, Coverage b) => new(a.Overall + b.Overall, a.Coastal + b.Coastal);
    }

    // ------------------------------------------------------------------ building networks

    public static PathSegment[] FromPolylines(IReadOnlyList<PathPolyline> lines)
    {
        var baseId = new int[lines.Count + 1];
        for (int i = 0; i < lines.Count; i++) baseId[i + 1] = baseId[i] + lines[i].Points.Length - 1;
        var segments = new PathSegment[baseId[^1]];
        for (int i = 0; i < lines.Count; i++)
        {
            PathPolyline l = lines[i];
            int m = l.Points.Length - 1;
            for (int k = 0; k < m; k++)
            {
                int next = k + 1 < m ? baseId[i] + k + 1 : -1;
                if (k + 1 == m && l.Receiver >= 0)
                {
                    PathPolyline r = lines[l.Receiver];
                    int rm = r.Points.Length - 1;
                    next = l.ReceiverVertex < rm ? baseId[l.Receiver] + l.ReceiverVertex : -1;
                }
                segments[baseId[i] + k] = new PathSegment(baseId[i] + k, next, l.Points[k], l.Points[k + 1]);
            }
        }
        return segments;
    }

    /// <summary>T2R-8b: the metrics leave the in-lake part of a stem out (a drowned channel is not a river path); the polyline starts where the stem leaves the lake, and a stem inside a lake is a point.</summary>
    private static double InLakeStart(RiverNetwork net, int stem) => stem < net.InLakeEnd.Length ? Math.Min(net.InLakeEnd[stem], net.MetricsEnd[stem]) : 0d;

    /// <summary>A confluence stem is joined to its receiver in the metrics unless it ends in the receiver's in-lake part (it then ends in water, like a Lake stem).</summary>
    private static bool Attached(RiverNetwork net, int stem) => net.Stems[stem].Terminal == RiverTerminal.Confluence && net.Stems[stem].JunctionS >= InLakeStart(net, net.Stems[stem].TerminalId) - 1e-6;

    /// <summary>
    /// The production centrelines as polylines sampled every 25 m of stem arc parameter (plus a vertex at each tributary junction), in the strip's local
    /// metres: around from <paramref name="aroundOrigin"/> (ring metres), across as is. Stems that end in water stop at the half segment into it, like the
    /// baseline's wet-contact clipping; a tributary's last vertex is the receiving vertex, so the network is connected exactly.
    /// </summary>
    public static PathSegment[] FromNetwork(RiverNetwork net, double aroundOrigin)
    {
        int n = net.Stems.Length;
        var joins = new List<(double S, int Child)>[n];
        for (int i = 0; i < n; i++)
            if (Attached(net, i))
            {
                int r = net.Stems[i].TerminalId;
                (joins[r] ??= new()).Add((Math.Min(net.Stems[i].JunctionS, net.MetricsEnd[r]), i));
            }
        var s = new double[n][];
        var childVertex = new int[n];
        for (int i = 0; i < n; i++)
        {
            double end = net.MetricsEnd[i];
            var list = new List<double>();
            for (double v = InLakeStart(net, i); v < end - 1e-3; v += SampleMeters) list.Add(v);
            list.Add(end);
            if (joins[i] != null)
            {
                foreach ((double js, int child) in joins[i].OrderBy(j => j.S))
                {
                    int idx = list.BinarySearch(js);
                    if (idx < 0)
                    {
                        idx = ~idx;
                        if (idx < list.Count && list[idx] - js < 0.01) { }
                        else if (idx > 0 && js - list[idx - 1] < 0.01) idx--;
                        else list.Insert(idx, js);
                    }
                    childVertex[child] = idx;
                }
                // inserts shift later indices: resolve again against the final list
                foreach ((double js, int child) in joins[i])
                {
                    int best = 0;
                    double bd = double.MaxValue;
                    int lo = list.BinarySearch(js);
                    if (lo < 0) lo = Math.Max(0, ~lo - 1);
                    for (int k = Math.Max(0, lo - 1); k <= Math.Min(list.Count - 1, lo + 1); k++) if (Math.Abs(list[k] - js) < bd) { bd = Math.Abs(list[k] - js); best = k; }
                    childVertex[child] = best;
                }
            }
            s[i] = list.ToArray();
        }
        var lines = new PathPolyline[n];
        var points = new PathPoint[n][];
        // receivers first
        var order = new List<int>(n);
        var kids = new List<int>[n];
        for (int i = 0; i < n; i++)
            if (!Attached(net, i)) order.Add(i);
            else (kids[net.Stems[i].TerminalId] ??= new()).Add(i);
        for (int h = 0; h < order.Count; h++) if (kids[order[h]] != null) order.AddRange(kids[order[h]]);
        double circumference = net.CircumferenceM;
        foreach (int i in order)
        {
            var p = new PathPoint[s[i].Length];
            for (int k = 0; k < p.Length; k++)
            {
                net.Centre(i, s[i][k], out double around, out double across, out _, out _);
                double x = (around - aroundOrigin) % circumference;
                if (x < 0) x += circumference;
                p[k] = new PathPoint(x, across);
            }
            RiverStem st = net.Stems[i];
            int recv = -1, recvVertex = 0;
            if (Attached(net, i))
            {
                recv = st.TerminalId; recvVertex = childVertex[i];
                p[^1] = points[recv][recvVertex];
            }
            points[i] = p;
            lines[i] = new PathPolyline(p, recv, recvVertex);
        }
        return FromPolylines(lines.Where(l => l.Points.Length >= 2).Count() == n ? lines : lines.Select(l => l.Points.Length >= 2 ? l : new PathPolyline(new[] { l.Points[0], l.Points[0] + new PathPoint(1e-3, 0) }, l.Receiver, l.ReceiverVertex)).ToArray());
    }

    // ------------------------------------------------------------------ frozen fixture

    public static PathGrid ReadFixtureGrid(string graphGzPath)
    {
        using var doc = ReadJson(graphGzPath);
        JsonElement g = doc.RootElement.GetProperty("grid"), m = doc.RootElement.GetProperty("coastal_mask");
        return new PathGrid(g.GetProperty("width").GetInt32(), g.GetProperty("height").GetInt32(), g.GetProperty("around_step_m").GetDouble(),
            g.GetProperty("across_step_m").GetDouble(), Convert.FromBase64String(m.GetProperty("base64").GetString()!));
    }

    private static JsonDocument ReadJson(string gzPath)
    {
        using var file = File.OpenRead(gzPath);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return JsonDocument.Parse(ms.ToArray());
    }

    /// <summary>The frozen baseline's physical geometry: the quotient arm at 50 km^2, cell centres joined, wet contacts clipped at half a step.</summary>
    public static PathSegment[] ReadFixtureSegments(string graphGzPath, out PathGrid grid)
    {
        using var doc = ReadJson(graphGzPath);
        JsonElement root = doc.RootElement, g = root.GetProperty("grid");
        PathGrid pg = new(g.GetProperty("width").GetInt32(), g.GetProperty("height").GetInt32(), g.GetProperty("around_step_m").GetDouble(),
            g.GetProperty("across_step_m").GetDouble(), Convert.FromBase64String(root.GetProperty("coastal_mask").GetProperty("base64").GetString()!));
        grid = pg;
        var origins = new List<int>();
        var receivers = new List<int>();
        var dry = new List<bool>();
        foreach (JsonElement e in root.GetProperty("edges").EnumerateArray())
        {
            if (!e.GetProperty("quotient_eligible").GetBoolean()) continue;
            origins.Add(e.GetProperty("origin").GetInt32());
            receivers.Add(e.GetProperty("receiver").GetInt32());
            dry.Add(e.GetProperty("receiver_class").GetString() == "Dry");
        }
        var index = new Dictionary<int, int>(origins.Count);
        for (int i = 0; i < origins.Count; i++) index[origins[i]] = i;
        PathPoint Centre(int cell) => new((cell % pg.Width + .5) * pg.Dx, (cell / pg.Width + .5) * pg.Dy);
        var segments = new PathSegment[origins.Count];
        for (int i = 0; i < segments.Length; i++)
        {
            PathPoint a = Centre(origins[i]), b = Centre(receivers[i]);
            int next = -1;
            if (dry[i]) next = index[receivers[i]]; else b = a + (b - a) * .5;
            segments[i] = new PathSegment(origins[i], next, a, b);
        }
        return segments;
    }

    // ------------------------------------------------------------------ evaluator (port of the frozen baseline's)

    private static (int[] Order, double[] Remaining) OutletFirst(PathSegment[] segments)
    {
        var order = new List<int>(segments.Length);
        var chain = new List<int>();
        var state = new byte[segments.Length];
        var remaining = new double[segments.Length];
        for (int start = 0; start < segments.Length; start++)
        {
            int index = start;
            while (index >= 0 && state[index] == 0)
            {
                state[index] = 1;
                chain.Add(index);
                index = segments[index].Next;
            }
            if (index >= 0 && state[index] != 2) throw new InvalidDataException("The path network contains a directed cycle.");
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                int cell = chain[i], next = segments[cell].Next;
                remaining[cell] = segments[cell].Length + (next >= 0 ? remaining[next] : 0);
                state[cell] = 2;
                order.Add(cell);
            }
            chain.Clear();
        }
        return (order.ToArray(), remaining);
    }

    private static IEnumerable<double> PhaseStarts(double before, double after, double step, int minimumIndex)
    {
        int first = Math.Max(minimumIndex, (int)Math.Floor((after + Tolerance) / step) + 1);
        int last = (int)Math.Floor((before + Tolerance) / step);
        for (int k = first; k <= last; k++) yield return k * step;
    }

    private static Trace? Walk(PathSegment[] segments, int index, double start, double length)
    {
        var spans = new List<Span>();
        var points = new List<PathPoint>();
        while (length > Tolerance && index >= 0)
        {
            PathSegment segment = segments[index];
            double take = Math.Min(length, segment.Length - start);
            if (points.Count == 0) points.Add(segment.At(start));
            if (take > 0)
            {
                spans.Add(new Span(index, start, start + take));
                points.Add(segment.At(start + take));
                length -= take;
            }
            index = segment.Next;
            start = 0;
        }
        return length <= Tolerance && points.Count >= 2 ? new Trace(spans, points) : null;
    }

    private static PathPoint TracePoint(Trace trace, double distance)
    {
        for (int i = 0; i < trace.Spans.Count; i++)
        {
            Span span = trace.Spans[i];
            double length = span.End - span.Start;
            if (distance <= length + Tolerance)
                return trace.Points[i] + (trace.Points[i + 1] - trace.Points[i]) * Math.Clamp(distance / length, 0, 1);
            distance -= length;
        }
        return trace.Points[^1];
    }

    private static double Dot(PathPoint a, PathPoint b) => a.X * b.X + a.Y * b.Y;
    private static double Turn(PathPoint a, PathPoint b) => Math.Atan2(a.X * b.Y - a.Y * b.X, Dot(a, b)) * 180 / Math.PI;

    /// <summary>True when the window passes the straight-run test: chord at least 5,940 m and every point within 150 m of the chord.</summary>
    public static bool WindowIsStraight(IReadOnlyList<PathPoint> points)
    {
        PathPoint a = points[0], chord = points[^1] - a;
        double norm = Dot(chord, chord), max = 0;
        foreach (PathPoint point in points)
        {
            PathPoint offset = point - a;
            double t = norm == 0 ? 0 : Math.Clamp(Dot(offset, chord) / norm, 0, 1);
            max = Math.Max(max, (offset - chord * t).Length);
        }
        return Math.Sqrt(norm) >= MinimumChord - Tolerance && max <= MaximumDeviation + Tolerance;
    }

    private static bool IsAlternating(PathPoint[] chords)
    {
        double a = Turn(chords[0], chords[1]), b = Turn(chords[1], chords[2]), c = Turn(chords[2], chords[3]);
        const double angularRoundoff = 1e-10;
        return Math.Abs(Turn(chords[0], chords[2])) <= ParallelAngle + angularRoundoff &&
            Math.Abs(Turn(chords[1], chords[3])) <= ParallelAngle + angularRoundoff && Math.Abs(a) >= AlternationAngle - angularRoundoff &&
            Math.Sign(a) == -Math.Sign(b) && Math.Sign(b) == -Math.Sign(c);
    }

    private static void Mark(List<Interval>?[] sets, IEnumerable<Span> spans, double firstLength = double.PositiveInfinity)
    {
        foreach (Span span in spans)
        {
            double take = Math.Min(firstLength, span.End - span.Start);
            if (take <= 0) break;
            (sets[span.Edge] ??= new List<Interval>()).Add(new Interval(span.Start, span.Start + take));
            firstLength -= take;
        }
    }

    private static Interval[] Merge(List<Interval>? intervals)
    {
        if (intervals == null) return Array.Empty<Interval>();
        intervals.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        var result = new List<Interval>();
        Interval pending = intervals[0];
        for (int i = 1; i < intervals.Count; i++)
        {
            Interval item = intervals[i];
            if (item.Start <= pending.End + Tolerance) pending = new Interval(pending.Start, Math.Max(pending.End, item.End));
            else { result.Add(pending); pending = item; }
        }
        result.Add(pending);
        return result.ToArray();
    }

    private static double CoastalLength(PathGrid grid, PathSegment segment, Interval interval)
    {
        PathPoint a = segment.At(interval.Start), b = segment.At(interval.End), delta = b - a;
        double length = interval.End - interval.Start;
        var stations = new List<double> { 0, 1 };
        void Crossings(double x, double y, double step)
        {
            double high = Math.Max(x, y);
            for (double face = (Math.Floor(Math.Min(x, y) / step) + 1) * step; face < high; face += step) stations.Add((face - x) / (y - x));
        }
        Crossings(a.X, b.X, grid.Dx);
        Crossings(a.Y, b.Y, grid.Dy);
        stations.Sort();
        double previous = 0, coastal = 0;
        int CellAxis(double point, double step, int count)
        {
            double face = Math.Round(point / step) * step;
            if (Math.Abs(point - face) <= Tolerance) point = face;
            return Math.Clamp((int)Math.Floor(point / step), 0, count - 1);
        }
        foreach (double current in stations)
        {
            if ((current - previous) * length <= Tolerance) continue;
            PathPoint midpoint = a + delta * ((previous + current) / 2);
            int row = CellAxis(midpoint.Y, grid.Dy, grid.Height), column = CellAxis(midpoint.X, grid.Dx, grid.Width);
            if (grid.Coastal(row * grid.Width + column)) coastal += (current - previous) * length;
            previous = current;
        }
        return coastal;
    }

    private static Coverage Cover(PathGrid grid, PathSegment edge, Interval[] intervals)
    {
        Coverage coverage = default;
        foreach (Interval interval in intervals) coverage += new Coverage(interval.End - interval.Start, CoastalLength(grid, edge, interval));
        return coverage;
    }

    private static PathScope Scope(double length, double flagged)
        => new(length / 1000, flagged / 1000, length > Tolerance ? flagged / length : 0);

    public static PathMetrics Evaluate(PathSegment[] segments, PathGrid grid)
    {
        var (_, remaining) = OutletFirst(segments);
        var incoming = new int[segments.Length];
        foreach (PathSegment s in segments) if (s.Next >= 0) incoming[s.Next]++;
        var windows = new List<Interval>?[segments.Length];
        var sharpEligible = new List<Interval>?[segments.Length];
        var sharpFlagged = new List<Interval>?[segments.Length];
        var alternateEligible = new List<Interval>?[segments.Length];
        var alternateFlagged = new List<Interval>?[segments.Length];

        for (int index = 0; index < segments.Length; index++)
        {
            PathSegment edge = segments[index];
            double before = remaining[index], after = edge.Next >= 0 ? remaining[edge.Next] : 0;
            foreach (double phase in PhaseStarts(before, after, PhaseStep, 24))
                CheckWindow(Math.Clamp(before - phase, 0, edge.Length));
            if (incoming[index] == 0 && before >= WindowLength - Tolerance && Math.Abs(before - Math.Round(before / PhaseStep) * PhaseStep) > Tolerance)
                CheckWindow(0);
            foreach (double phase in PhaseStarts(before, after, BendStep, 2))
            {
                bool four = phase >= 4 * BendStep;
                Trace? trace = Walk(segments, index, Math.Clamp(before - phase, 0, edge.Length), (four ? 4 : 2) * BendStep);
                if (trace == null) continue;
                var chords = new PathPoint[four ? 4 : 2];
                PathPoint prior = trace.Points[0];
                for (int j = 0; j < chords.Length; j++)
                {
                    PathPoint next = TracePoint(trace, (j + 1) * BendStep);
                    chords[j] = next - prior;
                    prior = next;
                }
                if (chords[0].Length > Tolerance && chords[1].Length > Tolerance)
                {
                    Mark(sharpEligible, trace.Spans, BendStep);
                    if (Math.Abs(Turn(chords[0], chords[1])) >= SharpAngle - 1e-10) Mark(sharpFlagged, trace.Spans, BendStep);
                }
                if (!four || chords.Any(c => c.Length <= Tolerance)) continue;
                Mark(alternateEligible, trace.Spans, BendStep);
                if (IsAlternating(chords)) Mark(alternateFlagged, trace.Spans, BendStep);
            }

            void CheckWindow(double offset)
            {
                Trace? trace = Walk(segments, index, offset, WindowLength);
                if (trace != null && WindowIsStraight(trace.Points)) Mark(windows, trace.Spans);
            }
        }

        Coverage total = default, flagged = default;
        for (int index = 0; index < segments.Length; index++)
        {
            total += Cover(grid, segments[index], new[] { new Interval(0, segments[index].Length) });
            flagged += Cover(grid, segments[index], Merge(windows[index]));
        }
        (PathScope, PathScope) Bend(List<Interval>?[] eligible, List<Interval>?[] marked)
        {
            Coverage e = default, f = default;
            for (int index = 0; index < segments.Length; index++)
            {
                e += Cover(grid, segments[index], Merge(eligible[index]));
                f += Cover(grid, segments[index], Merge(marked[index]));
            }
            return (Scope(e.Overall, f.Overall), Scope(e.Coastal, f.Coastal));
        }
        (PathScope sharpO, PathScope sharpC) = Bend(sharpEligible, sharpFlagged);
        (PathScope altO, PathScope altC) = Bend(alternateEligible, alternateFlagged);
        return new PathMetrics(Scope(total.Overall, flagged.Overall), Scope(total.Coastal, flagged.Coastal), sharpO, sharpC, altO, altC);
    }
}
