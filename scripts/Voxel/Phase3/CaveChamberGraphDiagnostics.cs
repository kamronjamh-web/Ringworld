using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase3;

public static class CaveChamberGraphDiagnostics
{
    public static bool RunSelfTest(out string summary)
    {
        try
        {
            const long fullProvinceCount = 24;
            const int partialCellCount = 24;
            long aroundLength = CaveTopologyPartition.ProvinceAroundMeters * fullProvinceCount
                + CaveTopologyPartition.CellAroundMeters * partialCellCount;
            CaveGenerationContract contract = CaveGenerationContract.Create(
                7331);
            var partition = new CaveTopologyPartition(contract, aroundLength);
            var distribution = new CaveProvinceDistribution(partition);
            var generator = CreateGenerator(contract, aroundLength);
            var repeatedGenerator = CreateGenerator(contract, aroundLength);
            var changedSeedGenerator = CreateGenerator(
                CaveGenerationContract.Create(7332),
                aroundLength);
            var changedRingGenerator = CreateGenerator(contract, aroundLength + 1);
            if (generator.Identity != repeatedGenerator.Identity
                || generator.Identity == changedSeedGenerator.Identity
                || generator.Identity == changedRingGenerator.Identity)
                throw new InvalidOperationException("graph generator identity isolation failed");

            CaveProvincePlan? active = null;
            CaveProvincePlan? inactive = null;
            var orderedPlans = new List<CaveProvincePlan>();
            for (long vertical = -8; vertical < 0; vertical++)
            for (long around = 0; around < fullProvinceCount; around++)
            for (long across = -4; across <= 4; across++)
            {
                CaveProvincePlan plan = distribution.ResolvePlan(
                    new CaveTopologyProvinceAddress(around, vertical, across));
                if (plan.Active)
                {
                    active ??= plan;
                    if (orderedPlans.Count < 12) orderedPlans.Add(plan);
                }
                else inactive ??= plan;
            }
            if (active == null || inactive == null || orderedPlans.Count < 12)
                throw new InvalidOperationException("active/inactive negative-depth graph planning failed");

            CaveChamberGraph empty = generator.Resolve(inactive.Value);
            if (empty.Count != 0
                || empty.Anchors.Count != 0
                || empty.TreeEdgeCount != 0
                || empty.LoopEdgeCount != 0
                || empty.HasBranch
                || empty.Identity == 0
                || empty.ProvincePlan != inactive.Value)
                throw new InvalidOperationException("inactive province produced a non-empty graph");

            CaveChamberGraph graph = generator.Resolve(active.Value);
            ValidateGraph(graph);
            AssertSameGraph(graph, repeatedGenerator.Resolve(active.Value), "repeated generator");

            CaveProvincePlan? activePartial = null;
            long partialProvince = partition.AroundProvinceCount - 1;
            for (long vertical = -32; vertical < 32 && activePartial == null; vertical++)
            for (long across = -4; across <= 4 && activePartial == null; across++)
            {
                CaveProvincePlan plan = distribution.ResolvePlan(
                    new CaveTopologyProvinceAddress(partialProvince, vertical, across));
                if (plan.Active) activePartial = plan;
            }
            if (activePartial == null)
                throw new InvalidOperationException("capable partial seam province never produced a graph");
            CaveChamberGraph partialGraph = generator.Resolve(activePartial.Value);
            ValidateGraph(partialGraph);
            CaveProvincePlan normalizedPartial = distribution.ResolvePlan(
                new CaveTopologyProvinceAddress(-1, activePartial.Value.Province.Address.Vertical, activePartial.Value.Province.Address.Across));
            if (normalizedPartial != activePartial.Value)
                throw new InvalidOperationException("ring-normalized province changed its graph plan");
            AssertSameGraph(partialGraph, generator.Resolve(normalizedPartial), "ring-normalized plan");

            var byAddress = new Dictionary<CaveTopologyProvinceAddress, CaveChamberGraph>();
            foreach (CaveProvincePlan plan in orderedPlans)
                byAddress.Add(plan.Province.Address, generator.Resolve(plan));
            orderedPlans.Reverse();
            foreach (CaveProvincePlan plan in orderedPlans)
                AssertSameGraph(byAddress[plan.Province.Address], generator.Resolve(plan), "reverse request order");

            var forged = new CaveProvincePlan(active.Value.Province, active.Value.Key + 1, active.Value.Active, active.Value.Bounds);
            Throws(() => generator.Resolve(forged));
            CaveProvincePlan foreign = changedSeedGenerator.Anchors.Distribution.ResolvePlan(active.Value.Province.Address);
            Throws(() => generator.Resolve(foreign));

            summary = $"identity={graph.Identity:X16} nodes={graph.Anchors.Count} edges={graph.Count} loops={graph.LoopEdgeCount} partial={partialGraph.Anchors.Count}/{partialGraph.Count}";
            return true;
        }
        catch (Exception exception)
        {
            summary = exception.Message;
            return false;
        }
    }

    private static CaveChamberGraphGenerator CreateGenerator(
        CaveGenerationContract contract,
        long aroundLength)
    {
        return new CaveChamberGraphGenerator(
            new CaveChamberAnchorGenerator(
                new CaveProvinceDistribution(
                    new CaveTopologyPartition(contract, aroundLength))));
    }

    private static void ValidateGraph(CaveChamberGraph graph)
    {
        int nodeCount = graph.Anchors.Count;
        int expectedLoops = Math.Clamp(
            nodeCount / CaveChamberGraphGenerator.LoopAnchorDivisor,
            CaveChamberGraphGenerator.MinimumLoops,
            CaveChamberGraphGenerator.MaximumLoops);
        if (nodeCount < CaveChamberAnchorGenerator.MinimumAnchors
            || nodeCount > CaveChamberAnchorGenerator.MaximumAnchors
            || graph.TreeEdgeCount != nodeCount - 1
            || graph.LoopEdgeCount != expectedLoops
            || graph.Count != nodeCount - 1 + expectedLoops
            || graph.AsSpan().Length != graph.Count
            || graph.Identity == 0)
            throw new InvalidOperationException("graph size, cycle-rank, or identity contract failed");

        var degrees = new int[nodeCount];
        var adjacency = new List<int>[nodeCount];
        for (int i = 0; i < nodeCount; i++) adjacency[i] = new List<int>();
        var endpoints = new HashSet<(int Low, int High)>();
        var keys = new HashSet<ulong>();
        var treeUnion = new DiagnosticUnionFind(nodeCount);
        int trees = 0;
        int loops = 0;
        ulong treeLength = 0;
        CaveChamberGraphEdge? previous = null;
        for (int i = 0; i < graph.Count; i++)
        {
            CaveChamberGraphEdge edge = graph[i];
            if (edge.LowRank < 0
                || edge.HighRank >= nodeCount
                || edge.LowRank >= edge.HighRank
                || !endpoints.Add((edge.LowRank, edge.HighRank))
                || !keys.Add(edge.Key))
                throw new InvalidOperationException("graph edge endpoint or key uniqueness failed");
            ulong expectedLength = DistanceSquared(
                graph.Anchors[edge.LowRank],
                graph.Anchors[edge.HighRank]);
            if (edge.SquaredLength != expectedLength)
                throw new InvalidOperationException("graph edge length failed");
            if (previous.HasValue && CompareEdges(previous.Value, edge) > 0)
                throw new InvalidOperationException("graph edge order is not deterministic");
            previous = edge;

            adjacency[edge.LowRank].Add(edge.HighRank);
            adjacency[edge.HighRank].Add(edge.LowRank);
            degrees[edge.LowRank]++;
            degrees[edge.HighRank]++;
            if (edge.Kind == CaveChamberEdgeKind.Tree)
            {
                trees++;
                if (!treeUnion.Join(edge.LowRank, edge.HighRank))
                    throw new InvalidOperationException("spanning-tree subset contains a cycle");
                treeLength = checked(treeLength + edge.SquaredLength);
            }
            else if (edge.Kind == CaveChamberEdgeKind.Loop) loops++;
            else throw new InvalidOperationException("unknown chamber graph edge kind");
        }
        if (trees != graph.TreeEdgeCount
            || loops != graph.LoopEdgeCount
            || treeUnion.ComponentCount != 1
            || treeLength != MinimumSpanningTreeLength(graph.Anchors))
            throw new InvalidOperationException("tree subset is not the connected minimum spanning tree");

        var visited = new bool[nodeCount];
        var queue = new Queue<int>();
        visited[0] = true;
        queue.Enqueue(0);
        int visitedCount = 0;
        while (queue.Count > 0)
        {
            int node = queue.Dequeue();
            visitedCount++;
            foreach (int neighbor in adjacency[node])
            {
                if (visited[neighbor]) continue;
                visited[neighbor] = true;
                queue.Enqueue(neighbor);
            }
        }
        bool actualBranch = false;
        foreach (int degree in degrees)
            if (degree >= CaveChamberGraphGenerator.BranchDegree) actualBranch = true;
        int cyclomatic = graph.Count - nodeCount + 1;
        if (visitedCount != nodeCount
            || cyclomatic != graph.LoopEdgeCount
            || !actualBranch
            || graph.HasBranch != actualBranch)
            throw new InvalidOperationException("graph connectivity, branch, or loop rank failed");
    }

    private static ulong MinimumSpanningTreeLength(CaveChamberAnchorSet anchors)
    {
        int count = anchors.Count;
        var included = new bool[count];
        var best = new ulong[count];
        Array.Fill(best, ulong.MaxValue);
        best[0] = 0;
        ulong total = 0;
        for (int step = 0; step < count; step++)
        {
            int selected = -1;
            for (int i = 0; i < count; i++)
                if (!included[i] && (selected < 0 || best[i] < best[selected])) selected = i;
            if (selected < 0 || best[selected] == ulong.MaxValue)
                throw new InvalidOperationException("reference MST could not reach every anchor");
            included[selected] = true;
            total = checked(total + best[selected]);
            for (int candidate = 0; candidate < count; candidate++)
            {
                if (included[candidate]) continue;
                ulong distance = DistanceSquared(anchors[selected], anchors[candidate]);
                if (distance < best[candidate]) best[candidate] = distance;
            }
        }
        return total;
    }

    private static ulong DistanceSquared(CaveChamberAnchor left, CaveChamberAnchor right)
    {
        ulong around = AbsDifference(left.Around, right.Around);
        ulong vertical = AbsDifference(left.Vertical, right.Vertical);
        ulong across = AbsDifference(left.Across, right.Across);
        return checked(around * around + vertical * vertical + across * across);
    }

    private static ulong AbsDifference(long left, long right)
    {
        Int128 difference = (Int128)left - right;
        if (difference < 0) difference = -difference;
        return checked((ulong)difference);
    }

    private static int CompareEdges(CaveChamberGraphEdge left, CaveChamberGraphEdge right)
    {
        int comparison = left.Kind.CompareTo(right.Kind);
        if (comparison != 0) return comparison;
        comparison = left.SquaredLength.CompareTo(right.SquaredLength);
        if (comparison != 0) return comparison;
        comparison = left.Key.CompareTo(right.Key);
        if (comparison != 0) return comparison;
        comparison = left.LowRank.CompareTo(right.LowRank);
        return comparison != 0 ? comparison : left.HighRank.CompareTo(right.HighRank);
    }

    private static void AssertSameGraph(
        CaveChamberGraph expected,
        CaveChamberGraph actual,
        string context)
    {
        if (expected.Identity != actual.Identity
            || expected.ProvincePlan != actual.ProvincePlan
            || expected.Anchors.Identity != actual.Anchors.Identity
            || expected.Count != actual.Count
            || expected.TreeEdgeCount != actual.TreeEdgeCount
            || expected.LoopEdgeCount != actual.LoopEdgeCount
            || expected.HasBranch != actual.HasBranch)
            throw new InvalidOperationException($"{context} changed chamber graph identity");
        for (int i = 0; i < expected.Count; i++)
            if (expected[i] != actual[i])
                throw new InvalidOperationException($"{context} changed chamber graph edge {i}");
    }

    private static void Throws(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("foreign chamber graph plan was accepted");
    }

    private sealed class DiagnosticUnionFind
    {
        private readonly int[] _parents;
        public int ComponentCount { get; private set; }

        public DiagnosticUnionFind(int count)
        {
            ComponentCount = count;
            _parents = new int[count];
            for (int i = 0; i < count; i++) _parents[i] = i;
        }

        private int Find(int value)
        {
            while (_parents[value] != value)
            {
                _parents[value] = _parents[_parents[value]];
                value = _parents[value];
            }
            return value;
        }

        public bool Join(int left, int right)
        {
            left = Find(left);
            right = Find(right);
            if (left == right) return false;
            _parents[right] = left;
            ComponentCount--;
            return true;
        }
    }
}
