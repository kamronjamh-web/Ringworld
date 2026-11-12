using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>Result of <see cref="DrainageRouting.Route"/>.</summary>
public sealed class DrainageRouted
{
    public required int[] Down { get; init; }
    /// <summary>Contributing area in cells (the cell itself included for land).</summary>
    public required int[] AreaCells { get; init; }
    /// <summary>Per-cell runoff r = A_cell(km^2) * max(0.1, m / 0.5) (S6); 0 for non-land cells.</summary>
    public required double[] Runoff { get; init; }
    /// <summary>Accumulated runoff R: r summed over the cell and everything draining through it (S6).</summary>
    public required double[] RunoffAccumulation { get; init; }
    /// <summary>Kind of the outlet each cell drains to; Barrier means the cell reaches none (enclosed or a barrier).</summary>
    public required DrainageCellKind[] Terminal { get; init; }
    public ulong DownHash { get; init; }
    public int FlatCrossingsRepaired { get; init; }
    public int FlatCrossingsUnresolved { get; init; }
    /// <summary>Crossings of two strictly descending diagonals; these are legal and are only counted.</summary>
    public int DescendingCrossings { get; init; }
    public int EnclosedLandCells { get; init; }
    public bool Conserved { get; init; }
    public double DirectionsSeconds { get; init; }
    public double RepairSeconds { get; init; }
    public double AccumulationSeconds { get; init; }
}

/// <summary>
/// D4 (docs/t2-river-reset.md 4.2): steepest strictly lower neighbour on the filled surface, the flood parent inside filled cells, ties by cell
/// index, crossing repair on flat diagonals, accumulation of area and moisture-weighted runoff (S6), terminal per cell, a hash of the down array.
/// The flood order is a valid topological order (strictly lower filled cells and parents come earlier), so accumulation runs it in reverse.
/// </summary>
public static class DrainageRouting
{
    private static readonly int[] Cardinal = { 1, 3, 4, 6 };

    public static DrainageRouted Route(DrainageDomain d, DrainageConditioned c, int workers)
    {
        int count = d.Count;
        int[] order = c.FloodOrder;
        var down = new int[count];
        Array.Fill(down, -1);
        var clock = Stopwatch.StartNew();
        int ranges = (int)Math.Min(order.Length, Math.Max(1, workers) * 16L);
        Parallel.For(0, Math.Max(1, ranges), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers) }, range =>
        {
            int start = (int)((long)order.Length * range / ranges), end = (int)((long)order.Length * (range + 1) / ranges);
            Span<int> nb = stackalloc int[8];
            for (int i = start; i < end; i++)
            {
                int cell = order[i];
                if (d.IsOutlet(cell)) continue;
                int best = -1;
                double bestSlope = 0;
                d.GetNeighbors(cell, nb);
                for (int k = 0; k < 8; k++)
                {
                    int next = nb[k];
                    if (next < 0 || !c.Visited[next]) continue;
                    float drop = c.Filled[cell] - c.Filled[next];
                    if (drop <= 0f) continue;
                    double slope = drop / d.StepMeters(k);
                    if (slope > bestSlope || (slope == bestSlope && next < best)) { bestSlope = slope; best = next; }
                }
                down[cell] = best >= 0 ? best : c.FloodParent[cell];
            }
        });
        double directionsSeconds = clock.Elapsed.TotalSeconds;

        clock.Restart();
        var rank = new int[count];
        Array.Fill(rank, -1);
        for (int i = 0; i < order.Length; i++) rank[order[i]] = i;
        var terminal = BuildTerminal(d, order, down);
        int repaired = 0, unresolved = 0, descending = 0;
        for (int pass = 0; pass < 4; pass++)
        {
            (int r, int u, int s) = RepairCrossings(d, c.Filled, down, rank, terminal);
            repaired += r; unresolved = u; descending = s;
            if (r == 0) break;
        }
        double repairSeconds = clock.Elapsed.TotalSeconds;

        clock.Restart();
        terminal = BuildTerminal(d, order, down);
        var area = new int[count];
        var runoff = new double[count];
        var accumulated = new double[count];
        double cellArea = d.CellAreaKm2;
        int landReached = 0;
        foreach (int cell in order)
        {
            if (!d.IsLand(cell)) continue;
            area[cell] = 1;
            runoff[cell] = cellArea * Math.Max(0.1, d.Moisture[cell] / 0.5);
            accumulated[cell] = runoff[cell];
            landReached++;
        }
        long outletArea = 0;
        for (int i = order.Length - 1; i >= 0; i--)
        {
            int cell = order[i], next = down[cell];
            if (next >= 0) { area[next] += area[cell]; accumulated[next] += accumulated[cell]; }
            else outletArea += area[cell];
        }
        int enclosed = 0;
        for (int cell = 0; cell < count; cell++) if (d.IsLand(cell) && !c.Visited[cell]) enclosed++;
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < count; i++) hash = (hash ^ (uint)down[i]) * 1099511628211UL;
        return new DrainageRouted
        {
            Down = down, AreaCells = area, Runoff = runoff, RunoffAccumulation = accumulated, Terminal = terminal, DownHash = hash,
            FlatCrossingsRepaired = repaired, FlatCrossingsUnresolved = unresolved, DescendingCrossings = descending,
            EnclosedLandCells = enclosed, Conserved = outletArea == landReached,
            DirectionsSeconds = directionsSeconds, RepairSeconds = repairSeconds, AccumulationSeconds = clock.Elapsed.TotalSeconds,
        };
    }

    private static DrainageCellKind[] BuildTerminal(DrainageDomain d, int[] order, int[] down)
    {
        var terminal = new DrainageCellKind[d.Count];
        Array.Fill(terminal, DrainageCellKind.Barrier);
        foreach (int cell in order)
        {
            if (d.IsOutlet(cell)) terminal[cell] = d.Kind[cell];
            else if (down[cell] >= 0) terminal[cell] = terminal[down[cell]];
        }
        return terminal;
    }

    /// <summary>
    /// Two diagonal links that cross inside one 2 x 2 square. Flat ones (both links stay at one filled level) are repaired by pointing one origin at a
    /// cardinal neighbour of the same level that is earlier in the flood order and reaches the same kind of outlet. Strictly descending crossings are legal.
    /// </summary>
    internal static (int Repaired, int Unresolved, int Descending) RepairCrossingsForTest(DrainageDomain d, float[] filled, int[] down, int[] rank, DrainageCellKind[] terminal)
        => RepairCrossings(d, filled, down, rank, terminal);

    private static (int Repaired, int Unresolved, int Descending) RepairCrossings(DrainageDomain d, float[] filled, int[] down, int[] rank, DrainageCellKind[] terminal)
    {
        int repaired = 0, unresolved = 0, descending = 0, w = d.Width;
        Span<int> nb = stackalloc int[8];
        int lastColumn = d.Wrap ? w : w - 1;
        for (int row = 0; row + 1 < d.Height; row++)
        for (int col = 0; col < lastColumn; col++)
        {
            int a = row * w + col, b = row * w + (col + 1) % w, e = (row + 1) * w + (col + 1) % w, c = (row + 1) * w + col;
            int originOne = down[a] == e ? a : down[e] == a ? e : -1;
            int originTwo = down[b] == c ? b : down[c] == b ? c : -1;
            if (originOne < 0 || originTwo < 0) continue;
            bool flat = filled[originOne] == filled[down[originOne]] && filled[originTwo] == filled[down[originTwo]];
            if (!flat) { descending++; continue; }
            int first = Math.Min(originOne, originTwo), second = Math.Max(originOne, originTwo);
            bool done = false;
            for (int pass = 0; pass < 2 && !done; pass++)
            for (int i = 0; i < 2 && !done; i++)
            {
                int source = i == 0 ? first : second;
                int opposite = down[i == 0 ? second : first];
                d.GetNeighbors(source, nb);
                foreach (int dir in Cardinal)
                {
                    int next = nb[dir];
                    if (next < 0 || (next == opposite) != (pass == 0)) continue;
                    if (filled[next] != filled[source] || rank[next] < 0 || rank[next] >= rank[source] || terminal[next] != terminal[source]) continue;
                    down[source] = next;
                    done = true;
                    break;
                }
            }
            if (done) repaired++; else unresolved++;
        }
        return (repaired, unresolved, descending);
    }
}
