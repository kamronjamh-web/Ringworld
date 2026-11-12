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
    /// <summary>Closed basins (S8b), ids by lowest basin cell.</summary>
    public RiverBasin[] Basins { get; }
    /// <summary>Dry floor of the closed basins as runs (<see cref="LakeRun.LakeId"/> is the basin id), sorted by row then column.</summary>
    public LakeRun[] DryRuns { get; }
    /// <summary>Index of the first dry run of each row, with a final entry holding the run count (derived).</summary>
    public int[] DryRowStart { get; }
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

    internal RiverGraph(RiverNetwork network, RiverLake[] lakes, RiverInlet[] inlets, LakeRun[] runs, RiverBasin[] basins, LakeRun[] dryRuns, int ringColumns, int rows, ulong inputIdentity, byte source, ulong? knownHash)
    {
        if (ringColumns > ushort.MaxValue || rows > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(ringColumns), "grid does not fit the run record");
        Network = network; Lakes = lakes; Inlets = inlets; Runs = runs; Basins = basins; DryRuns = dryRuns; RingColumns = ringColumns; Rows = rows;
        AroundStepMeters = (double)network.CircumferenceM / ringColumns; AcrossStepMeters = network.WidthM / rows;
        InputIdentity = inputIdentity; Source = source;
        RowStart = new int[rows + 1];
        foreach (LakeRun run in runs) RowStart[run.Row + 1]++;
        for (int r = 0; r < rows; r++) RowStart[r + 1] += RowStart[r];
        DryRowStart = new int[rows + 1];
        foreach (LakeRun run in dryRuns) DryRowStart[run.Row + 1]++;
        for (int r = 0; r < rows; r++) DryRowStart[r + 1] += DryRowStart[r];
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

    /// <summary>Id of the closed basin whose dry floor holds the ring column and row (S8b), or -1.</summary>
    public int DryBasinAt(int col, int row)
    {
        if ((uint)row >= (uint)Rows) return -1;
        int lo = DryRowStart[row], hi = DryRowStart[row + 1] - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            LakeRun run = DryRuns[mid];
            if (col < run.Col) hi = mid - 1;
            else if (col >= run.Col + run.Length) lo = mid + 1;
            else return run.LakeId;
        }
        return -1;
    }

    public int DryBasinAt(double around, double across) => DryBasinAt(Index.ColumnOf(around), Index.RowOf(across));

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
        ReadOnlySpan<DrainageBasinInfo> basinInfo = f.Basins.Span;
        var closedOrdinal = new int[basinInfo.Length];
        int closedCount = 0;
        for (int b = 0; b < basinInfo.Length; b++) closedOrdinal[b] = basinInfo[b].Closed ? closedCount++ : -1;
        var basins = new RiverBasin[closedCount];
        for (int b = 0; b < basinInfo.Length; b++)
            if (basinInfo[b].Closed) basins[closedOrdinal[b]] = new RiverBasin { SpillLevel = basinInfo[b].SpillLevel, FloorLevel = basinInfo[b].FloorLevel, DryCellCount = basinInfo[b].DryCells, LakeCount = basinInfo[b].Lakes };
        var lakes = new RiverLake[lakeCount];
        for (int i = 0; i < lakeCount; i++)
        {
            lakes[i] = new RiverLake
            {
                Level = (float)Math.Floor(conditioned[i].Level), RawLevel = conditioned[i].RawLevel, OutletStem = -1,
                MinCol = int.MaxValue, MinRow = int.MaxValue, MaxCol = -1, MaxRow = -1, MinHeight = float.PositiveInfinity, DeepestCol = -1, DeepestRow = -1,
                Basin = conditioned[i].Closed ? closedOrdinal[conditioned[i].Basin] : -1, Inflow = (float)conditioned[i].Inflow,
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

        // Dry floor of the closed basins as runs of the basin id (S8b).
        var dryRuns = new List<LakeRun>();
        for (int row = 0; row < rows; row++)
        {
            int runBasin = -1, runStart = 0, runLength = 0, previousRing = -2;
            for (int col = 0; col < width; col++)
            {
                int cell = row * width + col, id = f.IsDryFloor(cell) ? closedOrdinal[f.BasinId[cell]] : -1;
                int ringCol = spec.RingColumn(col);
                if (id >= 0 && id == runBasin && ringCol == previousRing + 1) runLength++;
                else
                {
                    if (runBasin >= 0) dryRuns.Add(new LakeRun { Row = (ushort)row, Col = (ushort)runStart, Length = (ushort)runLength, LakeId = runBasin });
                    runBasin = id; runStart = ringCol; runLength = id >= 0 ? 1 : 0;
                }
                previousRing = ringCol;
            }
            if (runBasin >= 0) dryRuns.Add(new LakeRun { Row = (ushort)row, Col = (ushort)runStart, Length = (ushort)runLength, LakeId = runBasin });
        }
        dryRuns.Sort(static (a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Col.CompareTo(b.Col));

        // Outlets and inlets.
        var inletLists = new List<RiverInlet>[lakeCount];
        var lakeIndicator = new RiverLevel.LakeIndicator(f, grid);
        // T2R-8b item 6: the outlet of lake l is the first stem that starts in or at it, is not Dead and whose last cell is not a flooded cell of l (a stem that ends inside the lake is an in-lake channel, not its outlet).
        for (int i = 0; i < net.Stems.Length; i++)
        {
            RiverStem st = net.Stems[i];
            int upstream = net.Reaches[st.ReachStart].UpstreamLake;
            if (upstream >= 0 && st.Terminal != RiverTerminal.Dead && lakes[upstream].OutletStem < 0 && !(st.LastCell >= 0 && lakeId[st.LastCell] == upstream))
            {
                lakes[upstream].OutletStem = i;
                lakes[upstream].OutletS = (float)RiverLevel.HeldEnd(net, lakeIndicator, i, upstream, false);
            }
        }
        // Inlets: stems that end Terminal Lake in l and do not start in it, or end as a confluence in the held part of a stem that starts in l (the outlet is held up to the lake's outlet point, any other in-lake stem to its end).
        for (int i = 0; i < net.Stems.Length; i++)
        {
            RiverStem st = net.Stems[i];
            int upstream = net.Reaches[st.ReachStart].UpstreamLake, into = -1;
            if (st.Terminal == RiverTerminal.Lake) { if (upstream != st.TerminalId) into = st.TerminalId; }
            else if (st.Terminal == RiverTerminal.Confluence)
            {
                int recv = st.TerminalId, recvLake = net.Reaches[net.Stems[recv].ReachStart].UpstreamLake;
                if (recvLake >= 0 && recvLake != upstream && net.Stems[recv].Terminal != RiverTerminal.Dead
                    && st.JunctionS <= (lakes[recvLake].OutletStem == recv ? lakes[recvLake].OutletS : net.Curves[recv].Length) + 1e-6) into = recvLake;
            }
            if (into >= 0)
                (inletLists[into] ??= new List<RiverInlet>()).Add(new RiverInlet
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
        return new RiverGraph(net, lakes, inlets.ToArray(), runs.ToArray(), basins, dryRuns.ToArray(), spec.RingColumns, spec.Rows, inputIdentity, source, null);
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
            ("closed basins", (long)Unsafe.SizeOf<RiverBasin>() * Basins.Length),
            ("dry runs and row starts", (long)Unsafe.SizeOf<LakeRun>() * DryRuns.Length + 4L * DryRowStart.Length),
            ("curves (float X, Y, S0, Len per piece)", 16L * nodes),
            ("sections", (long)Unsafe.SizeOf<RiverSection>() * reaches),
            ("spans (24 B stored, 11 float arrays and 2 byte arrays derived)", n.SpanTable?.ResidentBytes ?? 0L),
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
