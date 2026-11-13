using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>Evaporation parameters of the water balance (S8b): E0 (runoff units per cell area and unit of dryness) and lambda from <c>lake_abundance</c>.</summary>
public readonly record struct DrainageBalanceParameters(double E0, double Lambda)
{
    /// <summary><c>lambda = 4^(2 la - 1)</c>: 0 gives 0.25 (evaporation x 4), 0.5 gives 1, 1 gives 4 (evaporation / 4). <paramref name="evaporationScale"/> multiplies E0 (report sweep only).</summary>
    public static DrainageBalanceParameters From(double lakeAbundance, double evaporationScale = 1d)
        => new(DrainageLakeBalance.E0 * evaporationScale, Math.Pow(4d, 2d * Math.Clamp(lakeAbundance, 0d, 1d) - 1d));
}

/// <summary>What the balance made of one conditioned basin (a depression at least 2 m deep). Basins are indexed like <see cref="DrainageConditioned.Lakes"/>.</summary>
public struct DrainageBasinInfo
{
    /// <summary>True: inflow is below the evaporation of the full surface; the basin holds balance lakes below its spill and a dry floor. False: it spills (today's lake).</summary>
    public bool Closed;
    /// <summary>T2R-23 (RS2): the basin spills into a site (its exit is an Intake cell), so it holds no lake and passes its whole inflow on. Never set with the site option off.</summary>
    public bool SiteDrained;
    /// <summary>floor of the raw spill level.</summary>
    public float SpillLevel;
    /// <summary>The lowest Level among the basin's lakes, or its lowest cell height when it holds none (a dry pan).</summary>
    public float FloorLevel;
    public int Cells, DryCells, Lakes, DryPans, Roots, PitsAboveSpill, MultiPatchLakes;
    /// <summary>T2R-22: full pools of this closed basin that pass a surplus over their saddle (overflows).</summary>
    public int Overflows;
    /// <summary>Inflow (donors plus rain, runoff units), raw evaporation (the water that stays in the basin) and what leaves over the sill.</summary>
    public double Inflow, Evaporation, Outflow;
}

/// <summary>
/// T2R-22 (RS1): one overflow of a closed basin, a top-level pool that is full, pinned at its parent's saddle and has passed a surplus (<see cref="Handover"/>, runoff units) on. Its cells link to
/// <see cref="SaddleCell"/>, the saddle cell links to <see cref="OutCell"/> (the neighbour on the other side that the arithmetic used) and the descent goes on from there. The saddle cell's own descent before the
/// links is classed: <see cref="Sill"/> (it pointed into the full pool: the cell is the lake's sill and nothing is redirected), <see cref="SamePool"/> (it pointed elsewhere but into the top-level pool that the out cell is in:
/// the path moves by a cell, no water between pools) or <see cref="OtherPool"/> (it pointed into another pool than the arithmetic filled with the hand-over; <see cref="RedirectRunoff"/> is what the old link carried).
/// <see cref="SharedSaddle"/>: another overflow of the basin has the same saddle cell.
/// </summary>
public readonly record struct DrainageOverflow(int Basin, int SaddleCell, int OutCell, int PoolCells, double Handover, bool HasLake, bool Sill, bool SamePool, bool OtherPool, double RedirectRunoff, bool SharedSaddle, bool BelowThreshold);

/// <summary>Merge tree of one basin's cells below its spill level Z (S8b). Depends only on the heights, so it is built in parallel per basin.</summary>
internal sealed class BasinTree
{
    /// <summary>Basin cells in key order (height, then cell index).</summary>
    public int[] Cells = Array.Empty<int>();
    /// <summary>Number of cells with h below Z: the cells of the forest. They are a prefix of <see cref="Cells"/>.</summary>
    public int Forest;
    public int Nodes;
    public int[] ChildA = Array.Empty<int>(), ChildB = Array.Empty<int>(), Parent = Array.Empty<int>();
    /// <summary>For a merge node: the lowest-key neighbour of the saddle cell inside child A and child B (a cell index).</summary>
    public int[] NbA = Array.Empty<int>(), NbB = Array.Empty<int>();
    public float[] Saddle = Array.Empty<float>();
    /// <summary>T2R-22: for a merge node, the cell whose insertion created it (the saddle cell, an own cell of the node); -1 for a leaf.</summary>
    public int[] SaddleCell = Array.Empty<int>();
    /// <summary>Node each forest cell joined (own cell), and the own cells of each node as local indices in key order (CSR).</summary>
    public int[] OwnNode = Array.Empty<int>(), OwnStart = Array.Empty<int>(), OwnCells = Array.Empty<int>();
}

/// <summary>
/// S8b (docs/work-orders/terrain-vegetation/T2.md): water-balance lakes. Every conditioned basin is resolved once, upstream first: a basin whose inflow exceeds the evaporation
/// of its full surface spills (today's lake, surplus passed downstream); otherwise it is closed and holds balance lakes at the level where evaporation equals inflow, the
/// rest being dry floor. One bottom-up pass over the merge tree of each basin; no time stepping and no iteration.
/// </summary>
public static class DrainageLakeBalance
{
    /// <summary>Evaporation per unit of cell area at lambda 1 and zero moisture, in runoff units (S8b, tune at review).</summary>
    public const double E0 = 20d;
    private const double FullTolerance = 1e-12;
    /// <summary>T2R-22 test hook: false resolves closed basins as before the overflow links (levels, wet cells and the mass balance are the same either way). Default true.</summary>
    public static bool Overflows = true;
    /// <summary>T2R-22: contributing area, in km^2, at which the cell after a saddle counts as channel for the report ("overflows below the channel threshold"); the builder's default threshold.</summary>
    internal const double OverflowChannelThresholdKm2 = 100d;

    internal sealed class Run
    {
        public required DrainageDomain Domain;
        public required DrainageConditioned Conditioned;
        public required DrainageBalanceParameters Parameters;
        public required BasinTree[] Trees;
        public required int[] Local, BasinDown;
        public required double[] Accumulated;
        public required int[] Area;
        public required int[] OldDown, Rank;
        public required float[] Unperturbed;
        public required int[] LakeTemp;
        public required DrainageBasinInfo[] Info;
        public readonly List<DrainageLake> Lakes = new();
        public readonly List<int> LakeMinCell = new();
        public long HeldArea;
        public double Evaporation, Outflow;
        /// <summary>T2R-22: the overflows in resolve order, the closed basins whose links were left as they were (a cycle, never expected) and the pool cells the walk from the saddle did not reach (never expected).</summary>
        public readonly List<DrainageOverflow> OverflowList = new();
        public int OverflowCycles, OverflowUnreached;
        public int Spilling, MultiRootSpills, DryPans, MultiPatchLakes, PitsAboveSpill;
        public double PrepareSeconds, ResolveSeconds;
    }

    private static ulong Key(float h, int cell)
    {
        int bits = BitConverter.SingleToInt32Bits(h);
        uint u = bits >= 0 ? (uint)bits | 0x80000000u : ~(uint)bits;
        return ((ulong)u << 32) | (uint)cell;
    }

    /// <summary>Groups the basin cells, sorts them by key and builds the merge trees (parallel per basin) and the descent <c>BasinDown</c>.</summary>
    internal static Run Prepare(DrainageDomain d, DrainageConditioned c, DrainageBalanceParameters parameters, int workers, double[] accumulated, int[] area, int[] oldDown, int[] rank)
    {
        var clock = Stopwatch.StartNew();
        int count = d.Count, basins = c.Lakes.Length;
        int[] basinOf = c.LakeId;
        var start = new int[basins + 1];
        for (int cell = 0; cell < count; cell++) if (basinOf[cell] >= 0) start[basinOf[cell] + 1]++;
        for (int b = 0; b < basins; b++) start[b + 1] += start[b];
        var next = (int[])start.Clone();
        var all = new int[start[basins]];
        for (int cell = 0; cell < count; cell++) if (basinOf[cell] >= 0) all[next[basinOf[cell]]++] = cell;
        var local = new int[count];
        var basinDown = new int[count];
        Array.Fill(basinDown, -1);
        var trees = new BasinTree[basins];
        float[] h = c.Effective;
        Parallel.For(0, basins, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers) }, k =>
        {
            int len = start[k + 1] - start[k];
            var keys = new ulong[len];
            for (int i = 0; i < len; i++) { int cell = all[start[k] + i]; keys[i] = Key(h[cell], cell); }
            Array.Sort(keys);
            var cells = new int[len];
            for (int i = 0; i < len; i++) cells[i] = (int)(keys[i] & 0xFFFFFFFFUL);
            trees[k] = BuildTree(d, h, basinOf, k, cells, c.Lakes[k].Level, local, basinDown);
        });
        var run = new Run
        {
            Domain = d, Conditioned = c, Parameters = parameters, Trees = trees, Local = local, BasinDown = basinDown, Accumulated = accumulated, Area = area, OldDown = oldDown, Rank = rank,
            Unperturbed = (float[])c.UnperturbedFilled.Clone(), LakeTemp = new int[count], Info = new DrainageBasinInfo[basins],
        };
        Array.Fill(run.LakeTemp, -1);
        run.PrepareSeconds = clock.Elapsed.TotalSeconds;
        return run;
    }

    private static BasinTree BuildTree(DrainageDomain d, float[] h, int[] basinOf, int basin, int[] cells, float z, int[] local, int[] basinDown)
    {
        int n = cells.Length;
        for (int i = 0; i < n; i++) local[cells[i]] = i;
        int forest = 0;
        while (forest < n && h[cells[forest]] < z) forest++;
        Span<int> nb = stackalloc int[8];

        // Descent: the steepest strictly smaller key among the basin neighbours (ties: lower index); none means a pit.
        for (int i = 0; i < n; i++)
        {
            int cell = cells[i], best = -1;
            double bestSlope = 0;
            d.GetNeighbors(cell, nb);
            for (int dir = 0; dir < 8; dir++)
            {
                int m = nb[dir];
                if (m < 0 || basinOf[m] != basin) continue;
                if (!(h[m] < h[cell] || (h[m] == h[cell] && m < cell))) continue;
                double slope = (h[cell] - h[m]) / d.StepMeters(dir);
                if (best < 0 || slope > bestSlope || (slope == bestSlope && m < best)) { best = m; bestSlope = slope; }
            }
            basinDown[cell] = best;
        }

        // Merge tree of the cells below Z, in key order, with union-find.
        var uf = new int[forest];
        var setNode = new int[forest];
        var ownNode = new int[forest];
        var childA = new List<int>(); var childB = new List<int>(); var parent = new List<int>();
        var nbA = new List<int>(); var nbB = new List<int>(); var saddle = new List<float>(); var saddleCell = new List<int>();
        int NewNode(int a, int b, float zz, int na, int nbb, int at = -1)
        {
            childA.Add(a); childB.Add(b); parent.Add(-1); nbA.Add(na); nbB.Add(nbb); saddle.Add(zz); saddleCell.Add(at);
            return childA.Count - 1;
        }
        int Find(int x)
        {
            while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; }
            return x;
        }
        var tmp = new List<(int Node, int Root, int MinNb)>(8);
        for (int i = 0; i < forest; i++)
        {
            int cell = cells[i];
            d.GetNeighbors(cell, nb);
            tmp.Clear();
            for (int dir = 0; dir < 8; dir++)
            {
                int m = nb[dir];
                if (m < 0 || basinOf[m] != basin) continue;
                int lm = local[m];
                if (lm >= i) continue;
                int root = Find(lm);
                int at = -1;
                for (int t = 0; t < tmp.Count; t++) if (tmp[t].Root == root) { at = t; break; }
                if (at >= 0) { if (lm < tmp[at].MinNb) tmp[at] = (tmp[at].Node, root, lm); }
                else tmp.Add((setNode[root], root, lm));
            }
            if (tmp.Count == 0)
            {
                int leaf = NewNode(-1, -1, h[cell], -1, -1);
                uf[i] = i; setNode[i] = leaf; ownNode[i] = leaf;
                continue;
            }
            // Insertion sort by node id (ascending), then merge pairwise at this cell's height.
            for (int a = 1; a < tmp.Count; a++)
            {
                var item = tmp[a];
                int b = a - 1;
                while (b >= 0 && tmp[b].Node > item.Node) { tmp[b + 1] = tmp[b]; b--; }
                tmp[b + 1] = item;
            }
            int current = tmp[0].Node, firstRoot = tmp[0].Root, groupMin = tmp[0].MinNb;
            for (int t = 1; t < tmp.Count; t++)
            {
                int merged = NewNode(current, tmp[t].Node, h[cell], cells[groupMin], cells[tmp[t].MinNb], cell);
                parent[current] = merged; parent[tmp[t].Node] = merged;
                groupMin = Math.Min(groupMin, tmp[t].MinNb);
                uf[tmp[t].Root] = firstRoot;
                current = merged;
            }
            uf[i] = firstRoot;
            setNode[firstRoot] = current;
            ownNode[i] = current;
        }
        int nodes = childA.Count;
        var ownStart = new int[nodes + 1];
        for (int i = 0; i < forest; i++) ownStart[ownNode[i] + 1]++;
        for (int i = 0; i < nodes; i++) ownStart[i + 1] += ownStart[i];
        var fill = new int[nodes];
        var ownCells = new int[forest];
        for (int i = 0; i < forest; i++) { int node = ownNode[i]; ownCells[ownStart[node] + fill[node]++] = i; }
        return new BasinTree
        {
            Cells = cells, Forest = forest, Nodes = nodes, ChildA = childA.ToArray(), ChildB = childB.ToArray(), Parent = parent.ToArray(), NbA = nbA.ToArray(), NbB = nbB.ToArray(),
            Saddle = saddle.ToArray(), SaddleCell = saddleCell.ToArray(), OwnNode = ownNode, OwnStart = ownStart, OwnCells = ownCells,
        };
    }

    /// <summary>
    /// Resolves basin <paramref name="k"/> once all its donors have been added to <see cref="Run.Accumulated"/> (at the basin's first cell in the flood order). A spilling basin
    /// adds its outflow and its whole area to <paramref name="exit"/> (the flood parent of its first cell, outside the basin). A closed basin rewrites down, area and R inside.
    /// </summary>
    internal static void Resolve(Run run, int k, int exit, int[] down)
    {
        long begin = Stopwatch.GetTimestamp();
        DrainageDomain d = run.Domain;
        DrainageConditioned c = run.Conditioned;
        BasinTree t = run.Trees[k];
        DrainageLake basin = c.Lakes[k];
        float[] h = c.Effective;
        int[] cells = t.Cells, local = run.Local, basinDown = run.BasinDown;
        int n = cells.Length, forest = t.Forest, nodes = t.Nodes;
        float z = basin.Level;
        double factor = d.CellAreaKm2 * (run.Parameters.E0 / run.Parameters.Lambda);
        double E(int cell) => factor * Math.Clamp(1d - d.Moisture[cell], 0.05, 1d);

        // Inflow along the descent (donors and rain; local copies so a spilling basin keeps today's values inside).
        var acc = new double[n];
        var ar = new int[n];
        double total = 0;
        long totalArea = 0;
        for (int i = 0; i < n; i++) { acc[i] = run.Accumulated[cells[i]]; ar[i] = run.Area[cells[i]]; total += acc[i]; totalArea += ar[i]; }
        var accDown = (double[])acc.Clone();
        var arDown = (int[])ar.Clone();
        var pitOf = new int[n];
        for (int i = 0; i < n; i++)
        {
            int nd = basinDown[cells[i]];
            pitOf[i] = nd < 0 ? i : pitOf[local[nd]];
        }
        for (int i = n - 1; i >= 0; i--)
        {
            int nd = basinDown[cells[i]];
            if (nd < 0) continue;
            int j = local[nd];
            accDown[j] += accDown[i];
            arDown[j] += arDown[i];
        }
        var inflow = new double[nodes];
        int pitsAbove = 0;
        double extra = 0;
        for (int i = 0; i < n; i++)
        {
            if (basinDown[cells[i]] >= 0) continue;
            if (i < forest) inflow[t.OwnNode[i]] += accDown[i]; else { pitsAbove++; extra += accDown[i]; }
        }
        if (extra > 0) inflow[t.OwnNode[0]] += extra;

        // Capacity of each subtree (sum of e over all its cells), the lowest height, and the evaporation of the forest cells.
        var eCell = new double[forest];
        for (int i = 0; i < forest; i++) eCell[i] = E(cells[i]);
        var cap = new double[nodes];
        var minH = new float[nodes];
        for (int node = 0; node < nodes; node++)
        {
            double sum = 0;
            float low = float.PositiveInfinity;
            for (int p = t.OwnStart[node]; p < t.OwnStart[node + 1]; p++) { sum += eCell[t.OwnCells[p]]; low = Math.Min(low, h[cells[t.OwnCells[p]]]); }
            if (t.ChildA[node] >= 0) { sum += cap[t.ChildA[node]] + cap[t.ChildB[node]]; low = Math.Min(low, Math.Min(minH[t.ChildA[node]], minH[t.ChildB[node]])); }
            cap[node] = sum; minH[node] = low;
        }

        // Fill and spill, one pass in creation order.
        var pool = new bool[nodes];
        var pinned = new bool[nodes];
        var handover = new double[nodes]; // T2R-22: the surplus a pool has passed over its parent's saddle (grows at each pin, cleared with the pins); read only by the overflow block
        bool Full(int x) => pool[x] && inflow[x] >= cap[x] * (1d - FullTolerance);
        int LeafOf(int cell) => t.OwnNode[pitOf[local[cell]]];
        int PoolOf(int leaf, int current)
        {
            int node = leaf;
            while (true)
            {
                int q = t.Parent[node];
                if (q < 0 || q >= current || !pool[q]) return node;
                node = q;
            }
        }
        void Add(int p, double amount, int current)
        {
            for (int guard = 0; guard < 8 * nodes + 16; guard++)
            {
                inflow[p] += amount;
                int q = t.Parent[p];
                if (q < 0 || q >= current || !Full(p)) return;
                int sibling = t.ChildA[q] == p ? t.ChildB[q] : t.ChildA[q];
                if (Full(sibling))
                {
                    pool[q] = true; inflow[q] = inflow[p] + inflow[sibling]; pinned[p] = pinned[sibling] = false; handover[p] = handover[sibling] = 0d;
                    p = q; amount = 0;
                    continue;
                }
                double surplus = Math.Max(0d, inflow[p] - cap[p]);
                inflow[p] = cap[p]; pinned[p] = true; handover[p] += surplus;
                if (surplus <= 0) return;
                int nbCell = t.ChildA[q] == p ? t.NbB[q] : t.NbA[q];
                p = PoolOf(LeafOf(nbCell), current);
                amount = surplus;
            }
        }
        for (int node = 0; node < nodes; node++)
        {
            int a = t.ChildA[node], b = t.ChildB[node];
            if (a < 0) { pool[node] = true; continue; }
            bool fullA = Full(a), fullB = Full(b);
            if (fullA && fullB) { pool[node] = true; inflow[node] = inflow[a] + inflow[b]; continue; }
            if (!fullA && !fullB) continue;
            int x = fullA ? a : b, y = fullA ? b : a;
            double surplus = Math.Max(0d, inflow[x] - cap[x]);
            inflow[x] = cap[x]; pinned[x] = true; handover[x] += surplus;
            if (surplus > 0) Add(PoolOf(LeafOf(fullA ? t.NbB[node] : t.NbA[node]), node), surplus, node);
            if (Full(y)) { pool[node] = true; inflow[node] = inflow[a] + inflow[b]; pinned[a] = pinned[b] = false; handover[a] = handover[b] = 0d; }
        }
        int roots = 0;
        bool spills = false;
        for (int node = 0; node < nodes; node++)
        {
            if (t.Parent[node] >= 0) continue;
            roots++;
            if (pool[node] && inflow[node] > cap[node] * (1d + FullTolerance)) spills = true;
        }

        var info = new DrainageBasinInfo { Cells = n, Roots = roots, PitsAboveSpill = pitsAbove, SpillLevel = (float)Math.Floor(z), Inflow = total };
        run.PitsAboveSpill += pitsAbove;
        if (spills)
        {
            // Today's lake: every cell of the basin, the raw spill level, an outlet. The surplus over the evaporation at floor(Z) goes to the exit.
            float floorZ = (float)Math.Floor(z);
            double evapFull = 0;
            for (int i = 0; i < forest && h[cells[i]] < floorZ; i++) evapFull += eCell[i];
            // T2R-23 (RS2): a basin that spills into a site holds no lake (no record, no wet cells, no evaporation) and passes its whole inflow to the exit. Only Intake cells exist with the site option, so nothing changes without it.
            bool siteDrained = d.Kind[exit] == DrainageCellKind.Intake;
            double outflow = siteDrained ? total : Math.Max(0d, total - evapFull);
            info.Closed = false; info.SiteDrained = siteDrained; info.Outflow = outflow; info.Evaporation = total - outflow; info.Lakes = siteDrained ? 0 : 1; info.FloorLevel = basin.MinHeight;
            if (!siteDrained)
            {
                int temp = run.Lakes.Count, minCell = int.MaxValue;
                run.Lakes.Add(basin with { Id = temp, Level = z, RawLevel = z, Basin = k, Closed = false, Inflow = total, Evaporation = total - outflow });
                foreach (int cell in cells) { run.LakeTemp[cell] = temp; if (cell < minCell) minCell = cell; }
                run.LakeMinCell.Add(minCell);
            }
            // Inside values as today: along the old links, in reverse flood order, within the basin.
            var keys = new int[n];
            var order = (int[])cells.Clone();
            for (int i = 0; i < n; i++) keys[i] = -run.Rank[cells[i]];
            Array.Sort(keys, order);
            foreach (int cell in order)
            {
                int nx = run.OldDown[cell];
                if (nx < 0 || c.LakeId[nx] != k) continue;
                run.Accumulated[nx] += run.Accumulated[cell];
                run.Area[nx] += run.Area[cell];
            }
            run.Accumulated[exit] += outflow;
            run.Area[exit] += (int)totalArea;
            run.Spilling++;
            if (roots > 1) run.MultiRootSpills++;
            run.Evaporation += info.Evaporation; run.Outflow += outflow;
            run.Info[k] = info;
            run.ResolveSeconds += (double)(Stopwatch.GetTimestamp() - begin) / Stopwatch.Frequency;
            return;
        }

        // Closed: the descent replaces the links, the inflow along it replaces area and R.
        for (int i = 0; i < n; i++)
        {
            int cell = cells[i];
            down[cell] = basinDown[cell];
            run.Accumulated[cell] = accDown[i];
            run.Area[cell] = arDown[i];
            run.Unperturbed[cell] = h[cell];
        }
        double evaporationRaw = 0;
        int wetTotal = 0, lakes = 0, pans = 0, multiPatch = 0;
        float floorLevel = float.PositiveInfinity;
        var stack = new Stack<int>();
        var hasLake = new bool[nodes]; // T2R-22: which top-level pools hold a lake (read only by the overflow block)
        for (int node = 0; node < nodes; node++)
        {
            if (!pool[node] || (t.Parent[node] >= 0 && pool[t.Parent[node]])) continue;
            double water = inflow[node];
            evaporationRaw += water;
            double top = t.Parent[node] < 0 ? z : t.Saddle[t.Parent[node]];
            double low = minH[node];
            double raw;
            if (pinned[node] || water >= cap[node] * (1d - FullTolerance)) raw = top;
            else
            {
                double cumulative = t.ChildA[node] >= 0 ? cap[t.ChildA[node]] + cap[t.ChildB[node]] : 0d;
                int first = t.OwnStart[node], last = t.OwnStart[node + 1];
                raw = top;
                for (int p = first; p < last; p++)
                {
                    int li = t.OwnCells[p];
                    double before = cumulative;
                    cumulative += eCell[li];
                    if (cumulative < water) continue;
                    double hj = h[cells[li]];
                    double next = p + 1 < last ? h[cells[t.OwnCells[p + 1]]] : top;
                    raw = hj + (water - before) / eCell[li] * (next - hj);
                    break;
                }
            }
            bool leaf = t.ChildA[node] < 0;
            double lo = Math.Floor(low) + 1d, hi = Math.Floor(top);
            if ((leaf && water < 0.5 * eCell[t.OwnCells[t.OwnStart[node]]]) || hi < lo) { pans++; continue; }
            float level = (float)Math.Clamp(Math.Floor(raw + 0.5), lo, hi);
            int temp = run.Lakes.Count, wet = 0, deepest = int.MaxValue, minCell = int.MaxValue;
            double evap = 0;
            stack.Clear();
            stack.Push(node);
            while (stack.Count > 0)
            {
                int current = stack.Pop();
                for (int p = t.OwnStart[current]; p < t.OwnStart[current + 1]; p++)
                {
                    int li = t.OwnCells[p], cell = cells[li];
                    if (!(h[cell] < level)) continue;
                    run.LakeTemp[cell] = temp; run.Unperturbed[cell] = level;
                    wet++; evap += eCell[li];
                    if (li < deepest) deepest = li;
                    if (cell < minCell) minCell = cell;
                }
                if (t.ChildA[current] >= 0) { stack.Push(t.ChildA[current]); stack.Push(t.ChildB[current]); }
            }
            if (wet == 0) { pans++; continue; }
            run.Lakes.Add(new DrainageLake(temp, cells[deepest], level, h[cells[deepest]], wet, wet, (float)raw, k, true, water, evap));
            run.LakeMinCell.Add(minCell);
            hasLake[node] = true;
            wetTotal += wet; lakes++;
            floorLevel = Math.Min(floorLevel, level);
            if (t.ChildA[node] >= 0 && level <= t.Saddle[node]) multiPatch++;
        }
        if (Overflows) ApplyOverflows(run, k, t, pool, pinned, handover, hasLake, acc, ar, accDown, pitOf, ref info, down);
        info.Closed = true; info.Lakes = lakes; info.DryPans = pans; info.DryCells = n - wetTotal; info.MultiPatchLakes = multiPatch;
        info.FloorLevel = lakes > 0 ? floorLevel : h[cells[0]];
        info.Evaporation = evaporationRaw; info.Outflow = 0;
        run.HeldArea += totalArea;
        run.Evaporation += evaporationRaw;
        run.DryPans += pans; run.MultiPatchLakes += multiPatch;
        run.Info[k] = info;
        run.ResolveSeconds += (double)(Stopwatch.GetTimestamp() - begin) / Stopwatch.Frequency;
    }

    // ------------------------------------------------------------------ T2R-22: overflows (RS1)

    /// <summary>
    /// T2R-22 (RS1): gives every overflow of a closed basin its path and re-accumulates the basin along the final links. The fill arithmetic, the levels, the wet cells and the verdicts are not touched; this runs after them.
    /// An overflow is a top-level pool that is full, pinned at its parent's saddle and has passed a surplus on. Its cells link to the saddle cell (breadth-first from it over the pool's subtree, 8-neighbours in index
    /// order, first in first out), the saddle cell links to the neighbour on the other side that the arithmetic used, and from there the descent is unchanged. The links of a pool without a lake are the same: the
    /// river runs through it. Area and R are then accumulated once in topological order. R is accumulated plainly except at the step from a pool into its saddle, where the pool passes its hand-over in total
    /// (its evaporation stays in the lake). A saddle cell whose old link pointed into a full pool that has it as saddle is that pool's sill: everything that flows through it was already counted into the pool, so it passes the hand-over and nothing else.
    /// An overflow whose out cell lies in the pool of another overflow with the same saddle cell does not add its hand-over: the other one's already contains it.
    /// </summary>
    private static void ApplyOverflows(Run run, int k, BasinTree t, bool[] pool, bool[] pinned, double[] handover, bool[] hasLake, double[] acc, int[] ar, double[] accDown, int[] pitOf, ref DrainageBasinInfo info, int[] down)
    {
        int nodes = t.Nodes;
        List<int>? found = null;
        for (int node = 0; node < nodes; node++)
            if (pool[node] && pinned[node] && handover[node] > 0d && t.Parent[node] >= 0 && !pool[t.Parent[node]]) (found ??= new List<int>()).Add(node);
        if (found == null) return;
        found.Sort((a, b) => t.Parent[b] != t.Parent[a] ? t.Parent[b].CompareTo(t.Parent[a]) : a.CompareTo(b)); // the higher saddle first (RS1 "Order")
        DrainageDomain d = run.Domain;
        int[] cells = t.Cells, local = run.Local, basinDown = run.BasinDown, basinOf = run.Conditioned.LakeId;
        int n = cells.Length;
        var next = new int[n];
        for (int i = 0; i < n; i++) { int nd = basinDown[cells[i]]; next[i] = nd < 0 ? -1 : local[nd]; }
        var owner = new int[n];      // 1 + index in found of the overflow whose pool holds the cell
        var reached = new int[n];    // 1 + index in found of the overflow whose walk reached the cell
        var saddleHand = new double[n];
        var dropOwn = new bool[n];   // the saddle cell's old link pointed into a pool that has it as saddle: the cell passes the hand-over only
        var saddleUses = new int[n];
        var saddleOf = new int[found.Count];
        var poolSize = new int[found.Count];
        var queue = new int[n];
        var stack = new Stack<int>();
        var records = new List<DrainageOverflow>(found.Count);
        int unreached = 0;
        Span<int> nb = stackalloc int[8];
        // Every overflow's pool and saddle first, so that overflows sharing a saddle cell see each other.
        for (int f = 0; f < found.Count; f++)
        {
            int a = found[f], mark = f + 1;
            saddleOf[f] = local[t.SaddleCell[t.Parent[a]]];
            saddleUses[saddleOf[f]]++;
            stack.Clear(); stack.Push(a);
            while (stack.Count > 0)
            {
                int node = stack.Pop();
                for (int p = t.OwnStart[node]; p < t.OwnStart[node + 1]; p++) { owner[t.OwnCells[p]] = mark; poolSize[f]++; }
                if (t.ChildA[node] >= 0) { stack.Push(t.ChildA[node]); stack.Push(t.ChildB[node]); }
            }
        }
        // The top-level pool of a cell: the pool of the pit its descent reaches, climbed while the parent is a pool (-1 above the forest).
        int TopPool(int cell)
        {
            int pit = pitOf[local[cell]];
            if (pit >= t.Forest) return -1;
            int node = t.OwnNode[pit];
            while (t.Parent[node] >= 0 && pool[t.Parent[node]]) node = t.Parent[node];
            return node;
        }
        for (int f = 0; f < found.Count; f++)
        {
            int a = found[f], q = t.Parent[a], mark = f + 1;
            int saddleCell = t.SaddleCell[q], outCell = t.ChildA[q] == a ? t.NbB[q] : t.NbA[q];
            int ls = saddleOf[f], lx = local[outCell];
            int head = 0, tail = 0, count = 0;
            queue[tail++] = ls;
            while (head < tail)
            {
                int u = queue[head++];
                d.GetNeighbors(cells[u], nb);
                for (int dir = 0; dir < 8; dir++)
                {
                    int m = nb[dir];
                    if (m < 0 || basinOf[m] != k) continue;
                    int lm = local[m];
                    if (owner[lm] != mark || reached[lm] == mark) continue;
                    reached[lm] = mark; next[lm] = u; queue[tail++] = lm; count++;
                }
            }
            unreached += poolSize[f] - count;
            int oldCell = basinDown[saddleCell];
            int old = oldCell < 0 ? -1 : local[oldCell];
            bool sill = old >= 0 && owner[old] != 0 && saddleOf[owner[old] - 1] == ls; // into a full pool that has this cell as its saddle
            bool samePool = !sill && oldCell != outCell && oldCell >= 0 && TopPool(oldCell) == TopPool(outCell);
            bool otherPool = !sill && oldCell != outCell && !samePool;
            // An overflow that hands over into the pool of another overflow with the same saddle cell is part of that one's hand-over.
            bool intoSibling = owner[lx] != 0 && owner[lx] != mark && saddleOf[owner[lx] - 1] == ls;
            next[ls] = lx;
            if (!intoSibling) saddleHand[ls] += handover[a];
            if (sill) dropOwn[ls] = true;
            records.Add(new DrainageOverflow(k, saddleCell, outCell, poolSize[f], handover[a], hasLake[a], sill, samePool, otherPool, otherPool ? accDown[ls] : 0d, saddleUses[ls] > 1, false));
        }

        // One accumulation along the final links, in topological order.
        var indegree = new int[n];
        for (int i = 0; i < n; i++) if (next[i] >= 0) indegree[next[i]]++;
        var r = new double[n];
        var area = new int[n];
        for (int i = 0; i < n; i++) { r[i] = dropOwn[i] ? saddleHand[i] : acc[i] + saddleHand[i]; area[i] = ar[i]; }
        int head2 = 0, tail2 = 0;
        for (int i = 0; i < n; i++) if (indegree[i] == 0) queue[tail2++] = i;
        while (head2 < tail2)
        {
            int u = queue[head2++], v = next[u];
            if (v < 0) continue;
            if (!dropOwn[v] && !(owner[u] != 0 && v == saddleOf[owner[u] - 1])) r[v] += r[u];
            area[v] += area[u];
            if (--indegree[v] == 0) queue[tail2++] = v;
        }
        if (tail2 != n) { run.OverflowCycles++; return; }
        for (int i = 0; i < n; i++)
        {
            down[cells[i]] = next[i] < 0 ? -1 : cells[next[i]];
            run.Accumulated[cells[i]] = r[i];
            run.Area[cells[i]] = area[i];
        }
        int minCells = (int)Math.Ceiling(OverflowChannelThresholdKm2 / d.CellAreaKm2);
        foreach (DrainageOverflow o in records) run.OverflowList.Add(o with { BelowThreshold = area[local[o.OutCell]] < minCells });
        run.OverflowUnreached += unreached;
        info.Overflows = records.Count;
    }

    /// <summary>Final lakes: ids by lowest cell as in S5, the per-cell lake id and the lake array.</summary>
    internal static (int[] LakeId, DrainageLake[] Lakes) Finish(Run run)
    {
        int count = run.Lakes.Count;
        var order = new int[count];
        for (int i = 0; i < count; i++) order[i] = i;
        int[] minCell = run.LakeMinCell.ToArray();
        Array.Sort(minCell, order);
        var remap = new int[count];
        var lakes = new DrainageLake[count];
        for (int id = 0; id < count; id++) { remap[order[id]] = id; lakes[id] = run.Lakes[order[id]] with { Id = id }; }
        int[] lakeId = run.LakeTemp;
        for (int cell = 0; cell < lakeId.Length; cell++) if (lakeId[cell] >= 0) lakeId[cell] = remap[lakeId[cell]];
        return (lakeId, lakes);
    }
}
