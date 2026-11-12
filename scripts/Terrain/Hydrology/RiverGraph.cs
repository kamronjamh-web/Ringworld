using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// The immutable, resident river graph (docs/work-orders/terrain-vegetation/T2.md, S5): the <see cref="RiverNetwork"/> (nodes, stems, reaches, knots and what is derived
/// from them), the lakes with their flooded cells, inlets and outlets, and the spatial index. Physics and rendering query it anywhere, so it stays in memory; queries
/// allocate nothing. <see cref="IdentityHash"/> is the FNV-1a hash of the stored payload (<see cref="RiverGraphFormat"/>); it does not depend on the source that baked it.
/// </summary>
public sealed class RiverGraph
{
    public RiverNetwork Network { get; }
    public RiverLake[] Lakes { get; }
    public RiverInlet[] Inlets { get; }
    /// <summary>Flooded cells as runs, sorted by row then column.</summary>
    public LakeRun[] Runs { get; }
    /// <summary>Index of the first run of each row, with a final entry holding the run count (derived).</summary>
    public int[] RowStart { get; }
    public int RingColumns { get; }
    public int Rows { get; }
    public double AroundStepMeters { get; }
    public double AcrossStepMeters { get; }
    public RiverSpatialIndex Index { get; }
    /// <summary>Stem of each piece (piece id = global node index; derived).</summary>
    public int[] PieceStem { get; }
    public ulong InputIdentity { get; }
    /// <summary>0 CPU, 1 GPU. Recorded in the file header, not part of the identity.</summary>
    public byte Source { get; }
    public ulong IdentityHash { get; }

    internal RiverGraph(RiverNetwork network, RiverLake[] lakes, RiverInlet[] inlets, LakeRun[] runs, int ringColumns, int rows, ulong inputIdentity, byte source, ulong? knownHash)
    {
        if (ringColumns > ushort.MaxValue || rows > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(ringColumns), "grid does not fit the run record");
        Network = network; Lakes = lakes; Inlets = inlets; Runs = runs; RingColumns = ringColumns; Rows = rows;
        AroundStepMeters = (double)network.CircumferenceM / ringColumns; AcrossStepMeters = network.WidthM / rows;
        InputIdentity = inputIdentity; Source = source;
        RowStart = new int[rows + 1];
        foreach (LakeRun run in runs) RowStart[run.Row + 1]++;
        for (int r = 0; r < rows; r++) RowStart[r + 1] += RowStart[r];
        PieceStem = new int[network.NodeAroundDm.Length];
        for (int i = 0; i < network.Stems.Length; i++) Array.Fill(PieceStem, i, network.Stems[i].NodeStart, network.Stems[i].NodeCount);
        Index = RiverSpatialIndex.Build(network, ringColumns, rows, runs);
        IdentityHash = knownHash ?? RiverGraphFormat.PayloadHash(this);
    }

    // ------------------------------------------------------------------ queries

    /// <summary>Piece ids near the point (a superset of every piece within 650 m); piece p belongs to stem <see cref="PieceStem"/>[p] and is node p - Stems[stem].NodeStart of it.</summary>
    public ReadOnlySpan<int> ReachesNear(double around, double across) => Index.PiecesNear(around, across);

    /// <summary>False means no river piece, lake or lake ring can influence the point (the operator's early exit).</summary>
    public bool IsNear(double around, double across) => Index.IsNear(around, across);

    /// <summary>Lake id of the flooded cell at the ring column and row, or -1.</summary>
    public int LakeAt(int col, int row)
    {
        if ((uint)row >= (uint)Rows) return -1;
        int lo = RowStart[row], hi = RowStart[row + 1] - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            LakeRun run = Runs[mid];
            if (col < run.Col) hi = mid - 1;
            else if (col >= run.Col + run.Length) lo = mid + 1;
            else return run.LakeId;
        }
        return -1;
    }

    public int LakeAt(double around, double across) => LakeAt(Index.ColumnOf(around), Index.RowOf(across));

    /// <summary>The point where the outlet stem leaves the lake, in ring metres; false when the lake has no outlet.</summary>
    public bool TryGetOutletPoint(int lake, out double around, out double across)
    {
        RiverLake l = Lakes[lake];
        around = across = 0;
        if (l.OutletStem < 0) return false;
        Network.Centre(l.OutletStem, l.OutletS, out double a, out across, out _, out _);
        around = Network.WrapAround(a);
        return true;
    }

    /// <summary>Unit direction (around, across) of an inlet: the stem tangent at its end.</summary>
    public (double Around, double Across) InletDirection(int inlet)
    {
        RiverInlet i = Inlets[inlet];
        Network.Curves[i.Stem].Eval(i.S, out _, out _, out double tx, out double ty);
        return (tx, ty);
    }

    // ------------------------------------------------------------------ build

    /// <summary>
    /// Wraps a built network into the graph: lakes from the conditioned field (level floor(raw), cells, outlet stem and point, inlets), the runs and the index.
    /// <paramref name="inputIdentity"/> is the pre-graph identity (S12, T2R-9; 0 until then) and <paramref name="source"/> 0 for the CPU and 1 for the GPU source.
    /// </summary>
    public static RiverGraph Build(RiverNetwork net, DrainageField f, DrainageGrid grid, ulong inputIdentity = 0, byte source = 0)
    {
        DrainageGridSpec spec = grid.Spec;
        int lakeCount = f.Lakes.Length, width = f.Width, rows = f.Height;
        ReadOnlySpan<DrainageLake> conditioned = f.Lakes.Span;
        var lakes = new RiverLake[lakeCount];
        for (int i = 0; i < lakeCount; i++)
        {
            lakes[i] = new RiverLake
            {
                Level = (float)Math.Floor(conditioned[i].Level), RawLevel = conditioned[i].Level, OutletStem = -1,
                MinCol = int.MaxValue, MinRow = int.MaxValue, MaxCol = -1, MaxRow = -1, MinHeight = float.PositiveInfinity, DeepestCol = -1, DeepestRow = -1,
            };
        }
        var runs = new List<LakeRun>();
        ReadOnlySpan<int> lakeId = f.LakeId;
        ReadOnlySpan<float> lowest = f.Lowest;
        for (int row = 0; row < rows; row++)
        {
            int runLake = -1, runStart = 0, runLength = 0, previousRing = -2;
            for (int col = 0; col < width; col++)
            {
                int id = lakeId[row * width + col];
                int ringCol = spec.RingColumn(col);
                if (id >= 0)
                {
                    ref RiverLake lake = ref lakes[id];
                    lake.CellCount++;
                    lake.MinCol = Math.Min(lake.MinCol, ringCol); lake.MaxCol = Math.Max(lake.MaxCol, ringCol);
                    lake.MinRow = Math.Min(lake.MinRow, row); lake.MaxRow = Math.Max(lake.MaxRow, row);
                    if (lowest[row * width + col] < lake.MinHeight) { lake.MinHeight = lowest[row * width + col]; lake.DeepestCol = ringCol; lake.DeepestRow = row; }
                }
                if (id >= 0 && id == runLake && ringCol == previousRing + 1) runLength++;
                else
                {
                    if (runLake >= 0) runs.Add(new LakeRun { Row = (ushort)row, Col = (ushort)runStart, Length = (ushort)runLength, LakeId = runLake });
                    runLake = id; runStart = ringCol; runLength = id >= 0 ? 1 : 0;
                }
                previousRing = ringCol;
            }
            if (runLake >= 0) runs.Add(new LakeRun { Row = (ushort)row, Col = (ushort)runStart, Length = (ushort)runLength, LakeId = runLake });
        }
        runs.Sort(static (a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Col.CompareTo(b.Col));

        // Outlets and inlets.
        var inletLists = new List<RiverInlet>[lakeCount];
        var lakeIndicator = new RiverLevel.LakeIndicator(f, grid);
        for (int i = 0; i < net.Stems.Length; i++)
        {
            RiverStem st = net.Stems[i];
            int upstream = net.Reaches[st.ReachStart].UpstreamLake;
            if (upstream >= 0 && st.Terminal != RiverTerminal.Dead && lakes[upstream].OutletStem < 0)
            {
                double length = net.Curves[i].Length, s = 0;
                for (; s < length; s += 10)
                {
                    net.Centre(i, s, out double around, out double across, out _, out _);
                    if (lakeIndicator.P(net.WrapAround(around), across) <= 0) break;
                }
                lakes[upstream].OutletStem = i;
                lakes[upstream].OutletS = (float)Math.Min(s, length);
            }
            if (st.Terminal == RiverTerminal.Lake)
                (inletLists[st.TerminalId] ??= new List<RiverInlet>()).Add(new RiverInlet
                {
                    Stem = i, S = (float)net.Curves[i].Length, AroundDm = net.NodeAroundDm[st.NodeStart + st.NodeCount - 1], AcrossDm = net.NodeAcrossDm[st.NodeStart + st.NodeCount - 1],
                });
        }
        var inlets = new List<RiverInlet>();
        for (int i = 0; i < lakeCount; i++)
        {
            lakes[i].InletStart = inlets.Count;
            if (inletLists[i] != null) { inlets.AddRange(inletLists[i]); lakes[i].InletCount = inletLists[i].Count; }
            if (lakes[i].CellCount == 0) { lakes[i].MinHeight = lakes[i].Level; lakes[i].MinCol = lakes[i].MinRow = lakes[i].MaxCol = lakes[i].MaxRow = lakes[i].DeepestCol = lakes[i].DeepestRow = 0; }
        }
        return new RiverGraph(net, lakes, inlets.ToArray(), runs.ToArray(), spec.RingColumns, spec.Rows, inputIdentity, source, null);
    }

    // ------------------------------------------------------------------ sizes

    /// <summary>Bytes of the stored payload by part (the file is this plus the header).</summary>
    public IReadOnlyList<(string Part, long Bytes)> StoredBytesByPart() => RiverGraphFormat.PartBytes(this);

    /// <summary>Estimated resident bytes by part: the stored arrays as held in memory, the curves, sections and tail arrays derived from them, and the index.</summary>
    public IReadOnlyList<(string Part, long Bytes)> ResidentBytesByPart()
    {
        RiverNetwork n = Network;
        int nodes = n.NodeAroundDm.Length, stems = n.Stems.Length, reaches = n.Reaches.Length;
        return new (string, long)[]
        {
            ("nodes", 8L * nodes),
            ("stems", (long)Unsafe.SizeOf<RiverStem>() * stems),
            ("reaches", (long)Unsafe.SizeOf<RiverReach>() * reaches),
            ("knots (int16 cm deltas, anchors every 16)", n.Knots.ResidentBytes),
            ("lakes", (long)Unsafe.SizeOf<RiverLake>() * Lakes.Length),
            ("inlets", (long)Unsafe.SizeOf<RiverInlet>() * Inlets.Length),
            ("runs and row starts", (long)Unsafe.SizeOf<LakeRun>() * Runs.Length + 4L * RowStart.Length),
            ("curves (float X, Y, S0, Len per piece)", 16L * nodes),
            ("sections", (long)Unsafe.SizeOf<RiverSection>() * reaches),
            ("per-stem tails, metrics end, meander noise", 8L * stems * 8),
            ("per-stem curve objects and array headers (estimate 150 B)", 150L * stems),
            ("piece to stem", 4L * nodes),
            ("bitsets (flooded, ring, near)", Index.BitsetBytes),
            ("bucket offsets", Index.BucketOffsetBytes),
            ("bucket entries", Index.BucketEntryBytes),
        };
    }

    public long ResidentBytes => ResidentBytesByPart().Sum(p => p.Bytes);
}
