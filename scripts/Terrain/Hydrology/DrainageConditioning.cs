using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>A least-cost breach: the cells from the pit to the cell before certified lower ground, all cut to the pit's routing height.</summary>
public sealed record DrainageBreach(int PitCell, int[] Path, double MaxDepthMeters, double CostMeters);

/// <summary>
/// A lake of the conditioned grid (docs/t2-river-reset.md 4.4). <see cref="Level"/> is the lowest unperturbed value among the cells that border the
/// flooded set and that water can flow to (barrier cells and diagonals blocked by a barrier excluded, T2R-5c; a cut cell at its cut height). Its water cells are the flooded cells whose unperturbed lowest sample is below it.
/// </summary>
public sealed record DrainageLake(int Id, int PitCell, float Level, float MinHeight, int FloodedCells, int WaterCells,
    float RawLevel = float.NaN, int Basin = -1, bool Closed = false, double Inflow = 0d, double Evaporation = 0d)
{
    public float DepthMeters => Level - MinHeight;
}

/// <summary>Output of <see cref="DrainageConditioning.Condition"/>. All arrays are filled once and not modified afterwards.</summary>
public sealed class DrainageConditioned
{
    /// <summary>Routing height after the breaches.</summary>
    public required float[] CutRoute { get; init; }
    /// <summary>Unperturbed valley-floor height, with every cut cell lowered to its pit's unperturbed height.</summary>
    public required float[] Effective { get; init; }
    public required float[] Filled { get; init; }
    /// <summary>F of S6: the unperturbed filled surface (no routing noise): lake cells at the raw lake level (never below their own height), every other cell at its unperturbed height with cuts applied.</summary>
    public required float[] UnperturbedFilled { get; init; }
    public required int[] FloodParent { get; init; }
    /// <summary>Cells in flood order (parents and lower cells first); outlets that touch no land are not listed.</summary>
    public required int[] FloodOrder { get; init; }
    public required bool[] Visited { get; init; }
    /// <summary>Lake index per cell (flooded set of a lake) or -1.</summary>
    public required int[] LakeId { get; init; }
    public required DrainageLake[] Lakes { get; init; }
    public required DrainageBreach[] Cuts { get; init; }
    public int Basins { get; init; }
    public int Candidates { get; init; }
    public int Feasible { get; init; }
    public int RoutingFillBasins { get; init; }
    public int RoutingFillCells { get; init; }
    public long SearchStates { get; init; }
    public double FloodSeconds { get; init; }
    public double LabelSeconds { get; init; }
    public double SearchSeconds { get; init; }
    public double LakeSeconds { get; init; }
}

/// <summary>
/// D3 (docs/t2-river-reset.md 4.2): priority-flood on the routing height, least-cost breaches of at most <see cref="MaxBreachDepthMeters"/> and
/// <see cref="MaxBreachSteps"/> cells towards certified lower ground ranked by cut volume, the cheapest <see cref="BreachShare"/> applied, a second
/// flood, then lakes (depressions at least <see cref="MinLakeDepthMeters"/> deep; shallower ones are routing fill).
/// </summary>
public static class DrainageConditioning
{
    public const double MaxBreachDepthMeters = 40d;
    public const int MaxBreachSteps = 32;
    public const double MinLakeDepthMeters = 2d;
    /// <summary>Share of the feasible breaches that are cut, cheapest first (S8b: breaching no longer reads <c>lake_abundance</c>).</summary>
    public const double BreachShare = 0.5d;
    private const float DepressionTolerance = 0.01f;

    private readonly record struct Basin(int Id, int Pit, int Cells, float Spill);
    private readonly record struct Candidate(Basin Basin, int[] Path, double Cost);

    public static DrainageConditioned Condition(DrainageDomain d, double breachShare, int workers, CancellationToken cancellation = default)
    {
        int count = d.Count;
        var filled = new float[count];
        var parent = new int[count];
        var order = new int[count];
        var visited = new bool[count];
        var lakeLabel = new int[count];
        var clock = Stopwatch.StartNew();
        double floodSeconds = 0, labelSeconds = 0;

        int reached = Flood(d, d.Route, filled, parent, order, visited);
        floodSeconds += clock.Elapsed.TotalSeconds;
        clock.Restart();
        var basins = new List<Basin>();
        Label(d, d.Route, filled, visited, lakeLabel, basins, null);
        labelSeconds += clock.Elapsed.TotalSeconds;
        cancellation.ThrowIfCancellationRequested();

        // Breach search over every basin at least 2 m deep whose sill is within the depth limit. Independent per basin, so parallel and deterministic.
        clock.Restart();
        var eligible = new List<Basin>();
        foreach (Basin basin in basins)
        {
            double depth = basin.Spill - d.Route[basin.Pit];
            if (depth >= MinLakeDepthMeters && depth <= MaxBreachDepthMeters) eligible.Add(basin);
        }
        var found = new Candidate?[eligible.Count];
        long states = 0;
        var local = new ThreadLocal<BreachSearch>(() => new BreachSearch(), true);
        Parallel.For(0, eligible.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers), CancellationToken = cancellation }, i =>
        {
            BreachSearch search = local.Value!;
            if (search.TryFind(d, filled, visited, lakeLabel, eligible[i], out Candidate c)) found[i] = c;
        });
        foreach (BreachSearch s in local.Values) states += s.States;
        local.Dispose();
        var feasible = new List<Candidate>();
        foreach (Candidate? c in found) if (c.HasValue) feasible.Add(c.Value);
        feasible.Sort(static (a, b) =>
        {
            int byCost = a.Cost.CompareTo(b.Cost);
            return byCost != 0 ? byCost : a.Basin.Id.CompareTo(b.Basin.Id);
        });
        double searchSeconds = clock.Elapsed.TotalSeconds;

        int apply = (int)Math.Round(feasible.Count * Math.Clamp(breachShare, 0d, 1d));
        float[] cutRoute = d.Route, effective = d.Lowest;
        var cuts = new List<DrainageBreach>();
        if (apply > 0)
        {
            cutRoute = (float[])d.Route.Clone();
            effective = (float[])d.Lowest.Clone();
            for (int k = 0; k < apply; k++)
            {
                Candidate c = feasible[k];
                float datum = d.Route[c.Basin.Pit], effectiveDatum = d.Lowest[c.Basin.Pit];
                double maxDepth = 0;
                for (int i = 0; i < c.Path.Length - 1; i++)
                {
                    int cell = c.Path[i];
                    maxDepth = Math.Max(maxDepth, d.Route[cell] - datum);
                    cutRoute[cell] = Math.Min(cutRoute[cell], datum);
                    effective[cell] = Math.Min(effective[cell], effectiveDatum);
                }
                cuts.Add(new DrainageBreach(c.Basin.Pit, c.Path, maxDepth, c.Cost));
            }
            clock.Restart();
            reached = Flood(d, cutRoute, filled, parent, order, visited);
            floodSeconds += clock.Elapsed.TotalSeconds;
        }
        cancellation.ThrowIfCancellationRequested();

        // Final labelling, with lakes.
        clock.Restart();
        var lakes = new List<DrainageLake>();
        var lakeId = new int[count];
        Array.Fill(lakeId, -1);
        int fillBasins = 0, fillCells = 0;
        var finalBasins = new List<Basin>();
        Label(d, cutRoute, filled, visited, lakeLabel, finalBasins, (component, basin) =>
        {
            float border = float.PositiveInfinity, minMember = float.PositiveInfinity;
            Span<int> link = stackalloc int[8];
            foreach (int cell in component)
            {
                minMember = Math.Min(minMember, effective[cell]);
                // Only neighbours water can flow to (T2R-5c): barrier cells and diagonals blocked by a barrier are not spill candidates, as in routing.
                d.GetNeighbors(cell, link);
                for (int dir = 0; dir < 8; dir++)
                {
                    int n = link[dir];
                    if (n < 0 || lakeLabel[n] == basin.Id) continue;
                    border = Math.Min(border, effective[n]);
                }
            }
            int water = 0;
            foreach (int cell in component) if (effective[cell] < border) water++;
            if (border - minMember >= MinLakeDepthMeters && water > 0)
            {
                int index = lakes.Count;
                lakes.Add(new DrainageLake(index, basin.Pit, border, minMember, component.Count, water, border, index));
                foreach (int cell in component) lakeId[cell] = index;
            }
            else { fillBasins++; fillCells += component.Count; }
        });
        double lakeSeconds = clock.Elapsed.TotalSeconds;
        _ = reached;

        if (reached != order.Length) Array.Resize(ref order, reached);
        var unperturbed = (float[])effective.Clone();
        for (int i = 0; i < count; i++)
            if (lakeId[i] >= 0) unperturbed[i] = Math.Max(lakes[lakeId[i]].Level, effective[i]);
        return new DrainageConditioned
        {
            CutRoute = cutRoute, Effective = effective, Filled = filled, UnperturbedFilled = unperturbed, FloodParent = parent, FloodOrder = order, Visited = visited,
            LakeId = lakeId, Lakes = lakes.ToArray(), Cuts = cuts.ToArray(),
            Basins = finalBasins.Count, Candidates = eligible.Count, Feasible = feasible.Count, RoutingFillBasins = fillBasins, RoutingFillCells = fillCells,
            SearchStates = states, FloodSeconds = floodSeconds, LabelSeconds = labelSeconds, SearchSeconds = searchSeconds, LakeSeconds = lakeSeconds,
        };
    }

    /// <summary>Priority-flood from the outlets (Barnes' two-queue form). Outlets that touch no land are marked but not queued.</summary>
    internal static int Flood(DrainageDomain d, float[] h, float[] filled, int[] parent, int[] order, bool[] visited)
    {
        Array.Clear(visited);
        Array.Clear(filled);
        Array.Fill(parent, -1);
        var heap = new FloodHeap(1 << 16);
        var pit = new IntFifo(1 << 16);
        Span<int> nb = stackalloc int[8];
        for (int cell = 0; cell < d.Count; cell++)
        {
            if (!d.IsOutlet(cell)) continue;
            visited[cell] = true;
            filled[cell] = h[cell];
        }
        for (int cell = 0; cell < d.Count; cell++)
        {
            if (!d.IsOutlet(cell)) continue;
            d.GetNeighbors(cell, nb);
            bool active = false;
            for (int k = 0; k < 8; k++) if (nb[k] >= 0 && !visited[nb[k]]) { active = true; break; }
            if (active) heap.Push(h[cell], cell);
        }
        int n = 0;
        while (true)
        {
            int cell;
            if (pit.Count > 0) cell = pit.Pop();
            else if (heap.Count > 0) cell = heap.Pop();
            else break;
            order[n++] = cell;
            d.GetNeighbors(cell, nb);
            float level = filled[cell];
            for (int k = 0; k < 8; k++)
            {
                int next = nb[k];
                if (next < 0 || visited[next]) continue;
                visited[next] = true;
                parent[next] = cell;
                if (h[next] <= level) { filled[next] = level; pit.Push(next); }
                else { filled[next] = h[next]; heap.Push(h[next], next); }
            }
        }
        return n;
    }

    private static bool Depressed(DrainageDomain d, float[] h, float[] filled, bool[] visited, int cell)
        => visited[cell] && d.IsLand(cell) && !d.IsOutlet(cell) && filled[cell] - h[cell] > DepressionTolerance;

    /// <summary>Labels connected depressions of one filled level (8-connected). The first cell of a component is its id.</summary>
    private static void Label(DrainageDomain d, float[] h, float[] filled, bool[] visited, int[] label, List<Basin> basins, Action<List<int>, Basin>? each)
    {
        Array.Fill(label, -1);
        var component = new List<int>();
        Span<int> nb = stackalloc int[8];
        for (int start = 0; start < d.Count; start++)
        {
            if (label[start] >= 0 || !Depressed(d, h, filled, visited, start)) continue;
            component.Clear();
            component.Add(start);
            label[start] = start;
            int pit = start;
            float level = filled[start];
            for (int i = 0; i < component.Count; i++)
            {
                int cell = component[i];
                if (h[cell] < h[pit] || (h[cell] == h[pit] && cell < pit)) pit = cell;
                d.GetNeighbors(cell, nb);
                for (int k = 0; k < 8; k++)
                {
                    int next = nb[k];
                    if (next < 0 || label[next] >= 0 || filled[next] != level || !Depressed(d, h, filled, visited, next)) continue;
                    label[next] = start;
                    component.Add(next);
                }
            }
            var basin = new Basin(start, pit, component.Count, level);
            basins.Add(basin);
            each?.Invoke(component, basin);
        }
    }

    private sealed class BreachSearch
    {
        private readonly record struct Lab(int Cell, int Steps, double Cost, int Parent, int Next, bool Active);
        private readonly Dictionary<int, int> _head = new();
        private readonly List<Lab> _labels = new();
        private readonly PriorityQueue<int, (double Cost, int Steps, int Cell)> _queue = new();
        public long States { get; private set; }

        public bool TryFind(DrainageDomain d, float[] filled, bool[] visited, int[] lakeLabel, Basin basin, out Candidate candidate)
        {
            _head.Clear(); _labels.Clear(); _queue.Clear();
            Add(basin.Pit, 0, 0, -1);
            float datum = d.Route[basin.Pit];
            Span<int> nb = stackalloc int[8];
            while (_queue.TryDequeue(out int index, out _))
            {
                Lab label = _labels[index];
                if (!label.Active) continue;
                States++;
                if (lakeLabel[label.Cell] != basin.Id && filled[label.Cell] < datum)
                {
                    var path = new int[label.Steps + 1];
                    int at = index;
                    for (int i = path.Length - 1; i >= 0; i--) { Lab step = _labels[at]; path[i] = step.Cell; at = step.Parent; }
                    candidate = new Candidate(basin, path, label.Cost);
                    return true;
                }
                if (label.Steps >= MaxBreachSteps) continue;
                d.GetNeighbors(label.Cell, nb);
                for (int k = 0; k < 8; k++)
                {
                    int next = nb[k];
                    if (next < 0 || !visited[next]) continue;
                    if (lakeLabel[next] != basin.Id && filled[next] < datum) { Add(next, label.Steps + 1, label.Cost, index); continue; }
                    if (!d.IsLand(next) || d.IsOutlet(next)) continue;
                    double depth = d.Route[next] - datum;
                    if (depth < 0 || depth > MaxBreachDepthMeters) continue;
                    Add(next, label.Steps + 1, label.Cost + depth, index);
                }
            }
            candidate = default;
            return false;
        }

        private void Add(int cell, int steps, double cost, int parent)
        {
            int head = _head.TryGetValue(cell, out int previous) ? previous : -1;
            for (int at = head; at >= 0; at = _labels[at].Next)
            {
                Lab l = _labels[at];
                if (l.Active && l.Steps <= steps && l.Cost <= cost) return;
            }
            for (int at = head; at >= 0; at = _labels[at].Next)
            {
                Lab l = _labels[at];
                if (l.Active && steps <= l.Steps && cost <= l.Cost) _labels[at] = l with { Active = false };
            }
            int index = _labels.Count;
            _head[cell] = index;
            _labels.Add(new Lab(cell, steps, cost, parent, head, true));
            _queue.Enqueue(index, (cost, steps, cell));
        }
    }

    private struct FloodHeap
    {
        private float[] _key;
        private int[] _val;
        public int Count { get; private set; }
        public FloodHeap(int capacity) { _key = new float[capacity]; _val = new int[capacity]; Count = 0; }

        public void Push(float key, int value)
        {
            if (Count == _key.Length) { Array.Resize(ref _key, Count * 2); Array.Resize(ref _val, Count * 2); }
            int i = Count++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_key[p] <= key) break;
                _key[i] = _key[p]; _val[i] = _val[p];
                i = p;
            }
            _key[i] = key; _val[i] = value;
        }

        public int Pop()
        {
            int result = _val[0];
            Count--;
            if (Count > 0)
            {
                float key = _key[Count];
                int value = _val[Count];
                int i = 0;
                while (true)
                {
                    int c = 2 * i + 1;
                    if (c >= Count) break;
                    if (c + 1 < Count && _key[c + 1] < _key[c]) c++;
                    if (_key[c] >= key) break;
                    _key[i] = _key[c]; _val[i] = _val[c];
                    i = c;
                }
                _key[i] = key; _val[i] = value;
            }
            return result;
        }
    }

    private struct IntFifo
    {
        private int[] _items;
        private int _head, _tail;
        public IntFifo(int capacity) { _items = new int[capacity]; _head = _tail = 0; }
        public int Count => _tail - _head;
        public void Push(int value)
        {
            if (_tail == _items.Length)
            {
                if (_head > 0) { Array.Copy(_items, _head, _items, 0, _tail - _head); _tail -= _head; _head = 0; }
                else Array.Resize(ref _items, _items.Length * 2);
            }
            _items[_tail++] = value;
        }
        public int Pop()
        {
            int v = _items[_head++];
            if (_head == _tail) _head = _tail = 0;
            return v;
        }
    }
}
