using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// Binary form of a <see cref="RiverGraph"/> (S5, docs/t2-river-reset.md 4.7). Header: magic <c>RRGF</c>, <see cref="HydrologyVersions.GraphFormat"/> (int32),
/// <see cref="HydrologyVersions.DrainageAlgorithm"/> (int32), input identity (uint64), source (byte: 0 CPU, 1 GPU; not part of the identity), payload length (int64) and the
/// content hash (uint64, FNV-1a 64 over the payload bytes: the graph identity hash). Payload, little-endian: the grid and recipe values the hydraulics need, then nodes,
/// stems, reaches, spans, knots, lakes, inlets, runs, closed basins and dry runs, each prefixed by its count. Stem starts, row starts, curves, sections, tail levels and the index are derived at load by
/// the same code the bake ran, so a loaded graph answers exactly like the baked one.
/// </summary>
public static class RiverGraphFormat
{
    public const int HeaderBytes = 4 + 4 + 4 + 8 + 1 + 8 + 8;
    private static readonly byte[] Magic = { (byte)'R', (byte)'R', (byte)'G', (byte)'F' };

    public static ulong Fnv1a(ReadOnlySpan<byte> bytes)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in bytes) h = (h ^ b) * 1099511628211UL;
        return h;
    }

    public static byte[] Payload(RiverGraph graph) => WritePayload(graph, null);

    public static ulong PayloadHash(RiverGraph graph) => Fnv1a(Payload(graph));

    /// <summary>Payload bytes by part, each including its count prefix.</summary>
    public static IReadOnlyList<(string Part, long Bytes)> PartBytes(RiverGraph graph)
    {
        var parts = new List<(string, long)>();
        WritePayload(graph, parts);
        return parts;
    }

    /// <summary>
    /// FNV-1a hash of each stored part of a graph file, read from the bytes (formats 3 and 4; format 3 has no spans part). T3R-3 uses it to show that the path, level and lake parts of a bake
    /// are byte-identical to a bake made before spans existed.
    /// </summary>
    public static IReadOnlyList<(string Part, ulong Hash)> PartHashes(byte[] file)
    {
        int format = BitConverter.ToInt32(file, 4);
        var result = new List<(string, ulong)>();
        int at = 0;
        void Take(string name, long bytes) { result.Add((name, Fnv1a(new ReadOnlySpan<byte>(file, HeaderBytes + at, (int)bytes)))); at += (int)bytes; }
        int Int(int offset) => BitConverter.ToInt32(file, HeaderBytes + offset);
        Take("header values", 64);
        Take("nodes", 4L + 8L * Int(at));
        int stemCount = Int(at), stemsAt = at + 4;
        Take("stems", 4L + 21L * stemCount);
        Take("reaches", 4L + 32L * Int(at));
        if (format >= 4) Take("spans", 4L + 24L * Int(at));
        long knotBytes = 4;
        for (int i = 0; i < stemCount; i++) { int knots = Int(stemsAt + 21 * i + 4); if (knots > 0) knotBytes += 8 + 4L * (knots - 1); }
        Take("knots", knotBytes);
        Take("lakes", 4L + 64L * Int(at));
        Take("inlets", 4L + 16L * Int(at));
        Take("lake cell runs", 4L + 12L * Int(at));
        Take("closed basins", 4L + 16L * Int(at));
        Take("dry floor runs", 4L + 12L * Int(at));
        return result;
    }

    public static byte[] Write(RiverGraph graph)
    {
        byte[] payload = Payload(graph);
        using var ms = new MemoryStream(HeaderBytes + payload.Length);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
        {
            w.Write(Magic);
            w.Write(HydrologyVersions.GraphFormat);
            w.Write(HydrologyVersions.DrainageAlgorithm);
            w.Write(graph.InputIdentity);
            w.Write(graph.Source);
            w.Write((long)payload.Length);
            w.Write(Fnv1a(payload));
            w.Write(payload);
        }
        return ms.ToArray();
    }

    /// <summary>Reads and validates a file: magic, both versions, the identity when given, the length, the content hash and the structure. A failure names the reason.</summary>
    public static bool TryRead(byte[] bytes, ulong? expectedInputIdentity, out RiverGraph? graph, out string error)
    {
        graph = null;
        try
        {
            if (bytes.Length < HeaderBytes) { error = "file shorter than the header"; return false; }
            for (int i = 0; i < 4; i++) if (bytes[i] != Magic[i]) { error = "bad magic"; return false; }
            int format = BitConverter.ToInt32(bytes, 4), algorithm = BitConverter.ToInt32(bytes, 8);
            ulong identity = BitConverter.ToUInt64(bytes, 12);
            byte source = bytes[20];
            long length = BitConverter.ToInt64(bytes, 21);
            ulong hash = BitConverter.ToUInt64(bytes, 29);
            if (format != HydrologyVersions.GraphFormat) { error = $"graph format {format}, expected {HydrologyVersions.GraphFormat}"; return false; }
            if (algorithm != HydrologyVersions.DrainageAlgorithm) { error = $"drainage algorithm {algorithm}, expected {HydrologyVersions.DrainageAlgorithm}"; return false; }
            if (expectedInputIdentity.HasValue && identity != expectedInputIdentity.Value) { error = "input identity differs"; return false; }
            if (length != bytes.Length - HeaderBytes) { error = $"payload length {length}, file holds {bytes.Length - HeaderBytes}"; return false; }
            var payload = new ReadOnlySpan<byte>(bytes, HeaderBytes, (int)length);
            if (Fnv1a(payload) != hash) { error = "content hash mismatch"; return false; }
            graph = ReadPayload(payload, identity, source, hash);
            error = "";
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            graph = null;
            error = "invalid payload: " + e.Message;
            return false;
        }
    }

    // ------------------------------------------------------------------ writing

    private static void Ints(BinaryWriter w, int[] values) => w.Write(MemoryMarshal.AsBytes(values.AsSpan()));

    private static byte[] WritePayload(RiverGraph g, List<(string, long)>? parts)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("graph file is little-endian");
        RiverNetwork n = g.Network;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        long mark = 0;
        void Part(string name) { if (parts != null) parts.Add((name, ms.Position - mark)); mark = ms.Position; }

        w.Write(n.CircumferenceM); w.Write(n.WidthM); w.Write(n.Seed); w.Write(n.MeanderStrength); w.Write(n.ValleyWidth); w.Write(n.ChannelDepth); w.Write(n.Ferocity);
        w.Write(g.RingColumns); w.Write(g.Rows);
        Part("header values");
        w.Write(n.NodeAroundDm.Length); Ints(w, n.NodeAroundDm); Ints(w, n.NodeAcrossDm);
        Part("nodes");
        w.Write(n.Stems.Length);
        foreach (RiverStem s in n.Stems) { w.Write(s.NodeCount); w.Write(s.KnotCount); w.Write(s.ReachCount); w.Write((byte)s.Terminal); w.Write(s.TerminalId); w.Write(s.JunctionS); }
        Part("stems");
        w.Write(n.Reaches.Length);
        foreach (RiverReach r in n.Reaches) { w.Write(r.Stem); w.Write(r.SStart); w.Write(r.SEnd); w.Write(r.AreaKm2); w.Write(r.Q); w.Write(r.S); w.Write(r.MeanderPhase); w.Write(r.UpstreamLake); }
        Part("reaches");
        RiverSpanTable spanTable = n.SpanTable ?? throw new InvalidOperationException("the network has no spans");
        w.Write(spanTable.Count);
        foreach (RiverSpan sp in spanTable.Spans) { w.Write(sp.Reach); w.Write(sp.SStart); w.Write(sp.SEnd); w.Write(sp.Q); w.Write(sp.Sh); w.Write(sp.Landing); }
        Part("spans");
        w.Write(n.Knots.Count);
        for (int i = 0; i < n.Stems.Length; i++)
        {
            int count = n.Knots.KnotCount(i);
            if (count == 0) continue;
            w.Write(n.Knots.FirstS(i)); w.Write(n.Knots.FirstW(i));
            for (int j = 1; j < count; j++) { w.Write(n.Knots.DeltaS(i, j)); w.Write(n.Knots.DeltaW(i, j)); }
        }
        Part("knots");
        w.Write(g.Lakes.Length);
        foreach (RiverLake l in g.Lakes)
        {
            w.Write(l.Level); w.Write(l.RawLevel); w.Write(l.CellCount); w.Write(l.OutletStem); w.Write(l.OutletS); w.Write(l.InletStart); w.Write(l.InletCount);
            w.Write(l.MinCol); w.Write(l.MinRow); w.Write(l.MaxCol); w.Write(l.MaxRow); w.Write(l.MinHeight); w.Write(l.DeepestCol); w.Write(l.DeepestRow); w.Write(l.Basin); w.Write(l.Inflow);
        }
        Part("lakes");
        w.Write(g.Inlets.Length);
        foreach (RiverInlet i in g.Inlets) { w.Write(i.Stem); w.Write(i.S); w.Write(i.AroundDm); w.Write(i.AcrossDm); }
        Part("inlets");
        w.Write(g.Runs.Length);
        foreach (LakeRun r in g.Runs) { w.Write(r.Row); w.Write(r.Col); w.Write(r.Length); w.Write((ushort)0); w.Write(r.LakeId); }
        Part("lake cell runs");
        w.Write(g.Basins.Length);
        foreach (RiverBasin b in g.Basins) { w.Write(b.SpillLevel); w.Write(b.FloorLevel); w.Write(b.DryCellCount); w.Write(b.LakeCount); }
        Part("closed basins");
        w.Write(g.DryRuns.Length);
        foreach (LakeRun r in g.DryRuns) { w.Write(r.Row); w.Write(r.Col); w.Write(r.Length); w.Write((ushort)0); w.Write(r.LakeId); }
        Part("dry floor runs");
        w.Flush();
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ reading

    private static int Count(BinaryReader r, int elementBytes)
    {
        int count = r.ReadInt32();
        if (count < 0 || (long)count * elementBytes > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("count beyond the payload");
        return count;
    }

    private static int[] Ints(BinaryReader r, int count)
    {
        var values = new int[count];
        if (r.Read(MemoryMarshal.AsBytes(values.AsSpan())) != count * 4) throw new EndOfStreamException();
        return values;
    }

    private static RiverGraph ReadPayload(ReadOnlySpan<byte> payload, ulong identity, byte source, ulong hash)
    {
        using var ms = new MemoryStream(payload.ToArray(), false);
        using var r = new BinaryReader(ms);
        long circumference = r.ReadInt64();
        double width = r.ReadDouble();
        long seed = r.ReadInt64();
        double meander = r.ReadDouble(), valley = r.ReadDouble(), depth = r.ReadDouble(), ferocity = r.ReadDouble();
        int ringColumns = r.ReadInt32(), rows = r.ReadInt32();
        if (circumference <= 0 || ringColumns < 1 || rows < 1 || ringColumns > ushort.MaxValue || rows > ushort.MaxValue) throw new InvalidDataException("bad grid");

        int nodeCount = Count(r, 8);
        int[] around = Ints(r, nodeCount), across = Ints(r, nodeCount);
        var stems = new RiverStem[Count(r, 21)];
        int nodeCursor = 0, knotCursor = 0, reachCursor = 0;
        for (int i = 0; i < stems.Length; i++)
        {
            var s = new RiverStem { NodeStart = nodeCursor, KnotStart = knotCursor, ReachStart = reachCursor, HeadCell = -1, LastCell = -1 };
            s.NodeCount = r.ReadInt32(); s.KnotCount = r.ReadInt32(); s.ReachCount = r.ReadInt32();
            s.Terminal = (RiverTerminal)r.ReadByte(); s.TerminalId = r.ReadInt32(); s.JunctionS = r.ReadSingle();
            if (s.NodeCount < 1 || s.KnotCount < 0 || s.ReachCount < 1 || (byte)s.Terminal > (byte)RiverTerminal.Dead) throw new InvalidDataException("bad stem");
            nodeCursor += s.NodeCount; knotCursor += s.KnotCount; reachCursor += s.ReachCount;
            stems[i] = s;
        }
        if (nodeCursor != nodeCount) throw new InvalidDataException("stem node counts differ from the node count");
        var reaches = new RiverReach[Count(r, 32)];
        if (reachCursor != reaches.Length) throw new InvalidDataException("stem reach counts differ from the reach count");
        for (int i = 0; i < reaches.Length; i++)
        {
            reaches[i] = new RiverReach { Stem = r.ReadInt32(), SStart = r.ReadSingle(), SEnd = r.ReadSingle(), AreaKm2 = r.ReadSingle(), Q = r.ReadSingle(), S = r.ReadSingle(), MeanderPhase = r.ReadSingle(), UpstreamLake = r.ReadInt32() };
            if ((uint)reaches[i].Stem >= (uint)stems.Length) throw new InvalidDataException("bad reach stem");
        }
        var spans = new RiverSpan[Count(r, 24)];
        for (int i = 0; i < spans.Length; i++) spans[i] = new RiverSpan { Reach = r.ReadInt32(), SStart = r.ReadSingle(), SEnd = r.ReadSingle(), Q = r.ReadSingle(), Sh = r.ReadSingle(), Landing = r.ReadSingle() };
        int knotTotal = Count(r, 4);
        if (knotCursor != knotTotal) throw new InvalidDataException("stem knot counts differ from the knot count");
        var knotCounts = new int[stems.Length];
        var firstS = new int[stems.Length];
        var firstW = new int[stems.Length];
        var dS = new short[knotTotal];
        var dW = new short[knotTotal];
        for (int i = 0, at = 0; i < stems.Length; i++)
        {
            knotCounts[i] = stems[i].KnotCount;
            for (int j = 0; j < knotCounts[i]; j++, at++)
            {
                if (j == 0) { firstS[i] = r.ReadInt32(); firstW[i] = r.ReadInt32(); }
                else { dS[at] = r.ReadInt16(); dW[at] = r.ReadInt16(); }
            }
        }
        RiverKnotTable knots = RiverKnotTable.FromDeltas(knotCounts, firstS, firstW, dS, dW);
        var lakes = new RiverLake[Count(r, 64)];
        for (int i = 0; i < lakes.Length; i++)
        {
            lakes[i] = new RiverLake
            {
                Level = r.ReadSingle(), RawLevel = r.ReadSingle(), CellCount = r.ReadInt32(), OutletStem = r.ReadInt32(), OutletS = r.ReadSingle(), InletStart = r.ReadInt32(), InletCount = r.ReadInt32(),
                MinCol = r.ReadInt32(), MinRow = r.ReadInt32(), MaxCol = r.ReadInt32(), MaxRow = r.ReadInt32(), MinHeight = r.ReadSingle(), DeepestCol = r.ReadInt32(), DeepestRow = r.ReadInt32(), Basin = r.ReadInt32(), Inflow = r.ReadSingle(),
            };
            if (lakes[i].OutletStem >= stems.Length) throw new InvalidDataException("bad lake outlet");
        }
        var inlets = new RiverInlet[Count(r, 16)];
        for (int i = 0; i < inlets.Length; i++) inlets[i] = new RiverInlet { Stem = r.ReadInt32(), S = r.ReadSingle(), AroundDm = r.ReadInt32(), AcrossDm = r.ReadInt32() };
        foreach (RiverLake l in lakes) if (l.InletStart < 0 || l.InletCount < 0 || (long)l.InletStart + l.InletCount > inlets.Length) throw new InvalidDataException("bad lake inlets");
        foreach (RiverInlet i in inlets) if ((uint)i.Stem >= (uint)stems.Length) throw new InvalidDataException("bad inlet stem");
        var runs = new LakeRun[Count(r, 12)];
        for (int i = 0; i < runs.Length; i++)
        {
            runs[i] = new LakeRun { Row = r.ReadUInt16(), Col = r.ReadUInt16(), Length = r.ReadUInt16() };
            r.ReadUInt16();
            runs[i].LakeId = r.ReadInt32();
            if (runs[i].Row >= rows || runs[i].Col + runs[i].Length > ringColumns || (uint)runs[i].LakeId >= (uint)lakes.Length || (i > 0 && (runs[i - 1].Row > runs[i].Row || (runs[i - 1].Row == runs[i].Row && runs[i - 1].Col + runs[i - 1].Length > runs[i].Col)))) throw new InvalidDataException("bad lake run");
        }
        var basins = new RiverBasin[Count(r, 16)];
        for (int i = 0; i < basins.Length; i++)
        {
            basins[i] = new RiverBasin { SpillLevel = r.ReadSingle(), FloorLevel = r.ReadSingle(), DryCellCount = r.ReadInt32(), LakeCount = r.ReadInt32() };
            if (basins[i].DryCellCount < 0 || basins[i].LakeCount < 0) throw new InvalidDataException("bad basin");
        }
        foreach (RiverLake l in lakes) if (l.Basin < -1 || l.Basin >= basins.Length) throw new InvalidDataException("bad lake basin");
        var dryRuns = new LakeRun[Count(r, 12)];
        for (int i = 0; i < dryRuns.Length; i++)
        {
            dryRuns[i] = new LakeRun { Row = r.ReadUInt16(), Col = r.ReadUInt16(), Length = r.ReadUInt16() };
            r.ReadUInt16();
            dryRuns[i].LakeId = r.ReadInt32();
            if (dryRuns[i].Row >= rows || dryRuns[i].Length < 1 || dryRuns[i].Col + dryRuns[i].Length > ringColumns || (uint)dryRuns[i].LakeId >= (uint)basins.Length) throw new InvalidDataException("bad dry run");
            if (i > 0 && (dryRuns[i - 1].Row > dryRuns[i].Row || (dryRuns[i - 1].Row == dryRuns[i].Row && dryRuns[i - 1].Col + dryRuns[i - 1].Length > dryRuns[i].Col))) throw new InvalidDataException("dry runs out of order or overlapping");
        }
        // A dry run must not overlap a lake run in the same row (two sorted lists, one sweep).
        for (int i = 0, j = 0; i < runs.Length && j < dryRuns.Length;)
        {
            LakeRun a = runs[i], b = dryRuns[j];
            if (a.Row < b.Row) i++;
            else if (b.Row < a.Row) j++;
            else if (a.Col + a.Length <= b.Col) i++;
            else if (b.Col + b.Length <= a.Col) j++;
            else throw new InvalidDataException("a dry run overlaps a lake run");
        }
        if (ms.Position != ms.Length) throw new InvalidDataException("trailing bytes");
        foreach (RiverStem s in stems) if (s.Terminal is RiverTerminal.Lake or RiverTerminal.Confluence && (uint)s.TerminalId >= (uint)(s.Terminal == RiverTerminal.Lake ? lakes.Length : stems.Length)) throw new InvalidDataException("bad stem terminal");

        var net = new RiverNetwork(circumference, width, seed, meander, valley, depth, ferocity, around, across, stems, reaches) { Knots = knots };
        var levels = new double[lakes.Length];
        for (int i = 0; i < lakes.Length; i++) levels[i] = lakes[i].Level;
        RiverLevel.ComputeTails(net, levels);
        net.InstallSpans(RiverSpanTable.Create(net, spans, null, false));
        return new RiverGraph(net, lakes, inlets, runs, basins, dryRuns, ringColumns, rows, identity, source, hash);
    }
}
