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

    private RiverCarveTables? _carveTables;
    private readonly object _carveTablesLock = new();
    /// <summary>The river operator's per-stem tables, built once on first use (thread-safe) and shared by every operator on this graph.</summary>
    internal RiverCarveTables CarveTables => _carveTables ?? BuildCarveTables();

    private RiverCarveTables BuildCarveTables()
    {
        lock (_carveTablesLock) return _carveTables ??= new RiverCarveTables(this);
    }

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

    // ------------------------------------------------------------------ far coverage (T2R-12, S11)

    /// <summary>Segments a piece is sampled in (S11): nine base-curve points per piece.</summary>
    public const int CoverageSegmentsPerPiece = 8;
    /// <summary>Most segments a piece is walked in by <see cref="SplatLineArea"/> (S13.2).</summary>
    public const int MaxSplatSegmentsPerPiece = 64;

    [ThreadStatic] private static List<int>? _coveragePieces;

    /// <summary>
    /// S11 far coverage: <c>c = min(1, sum of (length of each base-curve segment inside the rectangle) * (full water width there) / area)</c>: the true area fraction, with no minimum (T2R-12b). Around is unwrapped metres. The base curve is sampled at <see cref="CoverageSegmentsPerPiece"/> segments per piece (the meander is not
    /// included, so the length is that of the base curve). Allocation-free after the first call on a thread.
    /// </summary>
    public double LineCoverage(double aroundMin, double aroundMax, double acrossMin, double acrossMax)
        => LineCoverage(aroundMin, aroundMax, acrossMin, acrossMax, null, out _);

    /// <summary>
    /// As <see cref="LineCoverage(double, double, double, double)"/> with a per-span weight (T3R-9's whitewater): <paramref name="weightedMean"/> is the width-weighted mean of
    /// <paramref name="spanWeight"/> over the length inside the rectangle, <c>sum(len * w * weight) / sum(len * w)</c> (0 when nothing crosses, and without a weight). The weight
    /// receives the span index of <see cref="RiverNetwork.ChannelAt"/> (-1 before spans are installed).
    /// </summary>
    public double LineCoverage(double aroundMin, double aroundMax, double acrossMin, double acrossMax, Func<int, double>? spanWeight, out double weightedMean)
    {
        double channelArea = LineArea(aroundMin, aroundMax, acrossMin, acrossMax, spanWeight, out weightedMean, out bool crossed);
        if (!crossed) return 0d;
        return Math.Min(1d, channelArea / ((aroundMax - aroundMin) * (acrossMax - acrossMin)));
    }

    /// <summary>
    /// The piece-in-rectangle loop of <see cref="LineCoverage(double, double, double, double)"/> without the cap (T3.md reset, interface 7): the channel area inside the
    /// rectangle in square metres (the sum over base-curve segments of the length inside times the full water width), whether any reach crosses it, and the weighted mean of
    /// <paramref name="spanWeight"/> as described there.
    /// </summary>
    public double LineArea(double aroundMin, double aroundMax, double acrossMin, double acrossMax, Func<int, double>? spanWeight, out double weightedMean, out bool crossed)
    {
        weightedMean = 0d;
        crossed = false;
        double area = (aroundMax - aroundMin) * (acrossMax - acrossMin);
        if (!(area > 0d)) return 0d;
        List<int> pieces = _coveragePieces ??= new List<int>(64);
        pieces.Clear();
        Index.CollectPieces(aroundMin, aroundMax, acrossMin, acrossMax, pieces);
        if (pieces.Count == 0) return 0d;
        double cx = 0.5 * (aroundMin + aroundMax), cy = 0.5 * (acrossMin + acrossMax), hw = 0.5 * (aroundMax - aroundMin), hh = 0.5 * (acrossMax - acrossMin);
        Span<double> px = stackalloc double[CoverageSegmentsPerPiece + 1], py = stackalloc double[CoverageSegmentsPerPiece + 1];
        double sum = 0d, weighted = 0d;
        for (int n = 0; n < pieces.Count; n++)
        {
            int piece = pieces[n];
            int stem = PieceStem[piece], k = piece - Network.Stems[stem].NodeStart;
            RiverStemCurve curve = Network.Curves[stem];
            curve.Controls(k, out double ax, out double ay, out double mx, out double my, out double bx, out double by);
            double off = Network.WrapDelta(curve.OriginAround + ax - cx) - ax;
            double yOff = curve.OriginAcross - cy;
            if (off + Math.Max(ax, Math.Max(mx, bx)) < -hw || off + Math.Min(ax, Math.Min(mx, bx)) > hw
                || yOff + Math.Max(ay, Math.Max(my, by)) < -hh || yOff + Math.Min(ay, Math.Min(my, by)) > hh) continue;
            for (int i = 0; i <= CoverageSegmentsPerPiece; i++)
            {
                curve.EvalPiece(k, (double)i / CoverageSegmentsPerPiece, out double x, out double y, out _, out _);
                px[i] = off + x; py[i] = yOff + y;
            }
            for (int i = 0; i < CoverageSegmentsPerPiece; i++)
            {
                double inside = ClipSegment(px[i], py[i], px[i + 1], py[i + 1], hw, hh);
                if (inside <= 0d) continue;
                crossed = true;
                double s = curve.S0[k] + curve.Len[k] * (i + 0.5) / CoverageSegmentsPerPiece;
                Network.ChannelAt(stem, s, out RiverChannel channel);
                double w = 2d * channel.HalfWidth * inside;
                sum += w;
                if (spanWeight != null) weighted += w * spanWeight(channel.Span);
            }
        }
        if (spanWeight != null && sum > 0d) weightedMean = weighted / sum;
        return sum;
    }

    /// <summary>
    /// T2R-12b (S11b): deposits the valley-only channel area of the pieces near a lattice onto its vertices. Vertex (x, z) of <paramref name="coverage"/> (row-major, <paramref name="columns"/> by
    /// <paramref name="rows"/>) is at <c>origin + index * spacing</c> (around is unwrapped metres). A segment that has no river owner at <paramref name="footprintMeters"/>
    /// (<see cref="RiverCarveOperator.ValleyOnly"/>) is walked in 8 to 64 segments (S13.2: a chord of at most two spacings) and cut into steps of at most half a spacing along and across its width, and each step's water area goes to its four surrounding vertices
    /// with bilinear weights, so the lattice holds the channel area; values clamp to 1 and there is no minimum. A vertex's sum does not depend on the lattice that contains it.
    /// Returns true when any value is above 0.
    /// </summary>
    public bool SplatLineArea(double aroundOrigin, double acrossOrigin, double spacing, int columns, int rows, double footprintMeters, Span<float> coverage)
    {
        coverage.Slice(0, columns * rows).Clear();
        if (!(spacing > 0d) || columns < 1 || rows < 1) return false;
        List<int> pieces = _coveragePieces ??= new List<int>(64);
        pieces.Clear();
        double grow = spacing + RiverSpatialIndex.ReachRadiusMeters;
        double aroundMax = aroundOrigin + (columns - 1) * spacing, acrossMax = acrossOrigin + (rows - 1) * spacing;
        Index.CollectPieces(aroundOrigin - grow, aroundMax + grow, acrossOrigin - grow, acrossMax + grow, pieces);
        if (pieces.Count == 0) return false;
        bool displaced = spacing <= 1024d;
        double circumference = Network.CircumferenceM, centreAround = 0.5 * (aroundOrigin + aroundMax);
        double originColumn = aroundOrigin / spacing, originRow = acrossOrigin / spacing;
        // Two spacings: a step lies up to half the water width (under 0.75 spacings on a level that has owners at all, under one spacing elsewhere) beside its chord and still deposits on a vertex one spacing out.
        double aroundLo = aroundOrigin - 2d * spacing, aroundHi = aroundMax + 2d * spacing, acrossLo = acrossOrigin - 2d * spacing, acrossHi = acrossMax + 2d * spacing;
        Span<double> px = stackalloc double[MaxSplatSegmentsPerPiece + 1], py = stackalloc double[MaxSplatSegmentsPerPiece + 1];
        bool any = false;
        for (int n = 0; n < pieces.Count; n++)
        {
            int piece = pieces[n];
            int stem = PieceStem[piece], k = piece - Network.Stems[stem].NodeStart;
            RiverStemCurve curve = Network.Curves[stem];
            // The image of the piece nearest the lattice: one fixed shift per piece, so two lattices that share a column see the same positions.
            curve.Controls(k, out _, out _, out double mx, out _, out _, out _);
            int segments = displaced ? Math.Clamp((int)Math.Ceiling(curve.Len[k] / Math.Max(2d * spacing, 16d)), CoverageSegmentsPerPiece, MaxSplatSegmentsPerPiece) : 1;
            double baseAround = curve.OriginAround + Math.Round((centreAround - (curve.OriginAround + mx)) / circumference) * circumference;
            for (int i = 0; i <= segments; i++)
            {
                double u = (double)i / segments;
                curve.EvalPiece(k, u, out double x, out double y, out double tx, out double ty);
                double pa = baseAround + x, pc = curve.OriginAcross + y;
                if (displaced)
                {
                    // The operator's displaced centreline: the base point moved along (-ty, tx) by the meander.
                    double s = curve.S0[k] + curve.Len[k] * u;
                    Network.ChannelAt(stem, s, out RiverChannel endChannel);
                    double d = Network.Meander(stem, s, in endChannel);
                    pa -= d * ty; pc += d * tx;
                }
                px[i] = pa; py[i] = pc;
            }
            for (int i = 0; i < segments; i++)
            {
                double x0 = px[i], y0 = py[i], x1 = px[i + 1], y1 = py[i + 1];
                if (Math.Max(x0, x1) < aroundLo || Math.Min(x0, x1) > aroundHi || Math.Max(y0, y1) < acrossLo || Math.Min(y0, y1) > acrossHi) continue;
                Network.ChannelAt(stem, curve.S0[k] + curve.Len[k] * (i + 0.5) / segments, out RiverChannel channel);
                if (!RiverCarveOperator.ValleyOnly(footprintMeters, channel.HalfWidth)) continue;
                double dx = x1 - x0, dy = y1 - y0, chord = Math.Sqrt(dx * dx + dy * dy);
                double length = displaced ? chord : curve.Len[k], w = 2d * channel.HalfWidth;
                if (!(chord > 0d) || !(w > 0d)) continue;
                int along = Math.Max(1, (int)Math.Ceiling(length / (0.5 * spacing))), across = Math.Max(1, (int)Math.Ceiling(w / (0.5 * spacing)));
                double nx = -dy / chord, ny = dx / chord, area = (length / along) * (w / across) / (spacing * spacing);
                for (int a = 0; a < along; a++)
                {
                    double t = (a + 0.5) / along, cx = x0 + dx * t, cy = y0 + dy * t;
                    for (int c = 0; c < across; c++)
                    {
                        double lateral = w * ((c + 0.5) / across - 0.5);
                        // Lattice coordinates: an integer origin index (the sector lattices) is subtracted exactly, so a vertex shared by two lattices gets the same weights.
                        double fx = (cx + nx * lateral) / spacing - originColumn, fy = (cy + ny * lateral) / spacing - originRow;
                        double ix = Math.Floor(fx), iy = Math.Floor(fy), wx = fx - ix, wy = fy - iy;
                        int col = (int)ix, row = (int)iy;
                        any |= Deposit(coverage, columns, rows, col, row, (1d - wx) * (1d - wy) * area);
                        any |= Deposit(coverage, columns, rows, col + 1, row, wx * (1d - wy) * area);
                        any |= Deposit(coverage, columns, rows, col, row + 1, (1d - wx) * wy * area);
                        any |= Deposit(coverage, columns, rows, col + 1, row + 1, wx * wy * area);
                    }
                }
            }
        }
        if (!any) return false;
        for (int i = 0; i < columns * rows; i++)
            if (coverage[i] > 1f) coverage[i] = 1f;
        return true;
    }

    private static bool Deposit(Span<float> coverage, int columns, int rows, int col, int row, double value)
    {
        if ((uint)col >= (uint)columns || (uint)row >= (uint)rows || !(value > 0d)) return false;
        coverage[row * columns + col] += (float)value;
        return true;
    }

    /// <summary>Length of the segment inside the box [-hw, hw] x [-hh, hh] (Liang-Barsky); 0 when it misses.</summary>
    private static double ClipSegment(double x0, double y0, double x1, double y1, double hw, double hh)
    {
        double dx = x1 - x0, dy = y1 - y0, t0 = 0d, t1 = 1d;
        if (!ClipEdge(-dx, x0 + hw, ref t0, ref t1) || !ClipEdge(dx, hw - x0, ref t0, ref t1)
            || !ClipEdge(-dy, y0 + hh, ref t0, ref t1) || !ClipEdge(dy, hh - y0, ref t0, ref t1)) return 0d;
        return Math.Sqrt(dx * dx + dy * dy) * (t1 - t0);
    }

    private static bool ClipEdge(double p, double q, ref double t0, ref double t1)
    {
        if (p == 0d) return q >= 0d;
        double r = q / p;
        if (p < 0d) { if (r > t1) return false; if (r > t0) t0 = r; }
        else { if (r < t0) return false; if (r < t1) t1 = r; }
        return true;
    }

    /// <summary>
    /// The base-curve polyline of one piece for drawing (the minimap): <see cref="CoverageSegmentsPerPiece"/> + 1 points in unwrapped ring metres and the full water width at the
    /// middle of each of the eight segments.
    /// </summary>
    public void SamplePiece(int piece, Span<double> around, Span<double> across, Span<double> width)
    {
        int stem = PieceStem[piece], k = piece - Network.Stems[stem].NodeStart;
        RiverStemCurve curve = Network.Curves[stem];
        for (int i = 0; i <= CoverageSegmentsPerPiece; i++)
        {
            curve.EvalPiece(k, (double)i / CoverageSegmentsPerPiece, out double x, out double y, out _, out _);
            around[i] = curve.OriginAround + x; across[i] = curve.OriginAcross + y;
        }
        for (int i = 0; i < CoverageSegmentsPerPiece; i++)
        {
            Network.ChannelAt(stem, curve.S0[k] + curve.Len[k] * (i + 0.5) / CoverageSegmentsPerPiece, out RiverChannel channel);
            width[i] = 2d * channel.HalfWidth;
        }
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
