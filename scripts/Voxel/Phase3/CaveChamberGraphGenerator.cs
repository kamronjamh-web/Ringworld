using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase3;

public enum CaveChamberEdgeKind
{
    Tree,
    Loop
}

public readonly record struct CaveChamberGraphEdge(
    ulong Key,
    int LowRank,
    int HighRank,
    ulong SquaredLength,
    CaveChamberEdgeKind Kind);

public sealed class CaveChamberGraph
{
    private readonly CaveChamberGraphEdge[] _edges;

    internal CaveChamberGraph(
        CaveProvincePlan plan,
        CaveChamberAnchorSet anchors,
        ulong identity,
        CaveChamberGraphEdge[] edges,
        int treeEdgeCount,
        int loopEdgeCount,
        bool hasBranch)
    {
        ProvincePlan = plan;
        Anchors = anchors;
        Identity = identity;
        _edges = (CaveChamberGraphEdge[])edges.Clone();
        TreeEdgeCount = treeEdgeCount;
        LoopEdgeCount = loopEdgeCount;
        HasBranch = hasBranch;
    }

    public CaveProvincePlan ProvincePlan { get; }
    public CaveChamberAnchorSet Anchors { get; }
    public ulong Identity { get; }
    public int Count => _edges.Length;
    public CaveChamberGraphEdge this[int index] => _edges[index];
    public int TreeEdgeCount { get; }
    public int LoopEdgeCount { get; }
    public bool HasBranch { get; }
    public ReadOnlySpan<CaveChamberGraphEdge> AsSpan() => _edges;
}

/// <summary>Stateless bounded MST-plus-loops chamber connectivity.</summary>
public sealed class CaveChamberGraphGenerator
{
    public const int Version = 1;
    public const int MinimumLoops = 1;
    public const int MaximumLoops = 3;
    public const int LoopAnchorDivisor = 8;
    public const int BranchDegree = 3;

    private const ulong IdentitySalt = 0x4752415048563101UL;
    private const ulong EdgeKeySalt = 0x4752415048454447UL;
    private const ulong LoopPrioritySalt = 0x47524150484C4F50UL;
    private const ulong GraphIdentitySalt = 0x4752415048494431UL;

    public CaveChamberAnchorGenerator Anchors { get; }
    public ulong Identity { get; }

    public CaveChamberGraphGenerator(CaveChamberAnchorGenerator anchors)
    {
        Anchors = anchors ?? throw new ArgumentNullException(nameof(anchors));
        ulong identity = MixIdentity(anchors.Identity, IdentitySalt);
        identity = MixIdentity(identity, Version);
        identity = MixIdentity(identity, MinimumLoops);
        identity = MixIdentity(identity, MaximumLoops);
        identity = MixIdentity(identity, LoopAnchorDivisor);
        identity = MixIdentity(identity, BranchDegree);
        Identity = Avalanche(identity);
    }

    public CaveChamberGraph Resolve(CaveProvincePlan plan)
    {
        CaveChamberAnchorSet nodes = Anchors.Resolve(plan);
        int nodeCount = nodes.Count;
        if (nodeCount == 0)
            return CreateGraph(plan, nodes, Array.Empty<CaveChamberGraphEdge>(), 0, 0, false);

        var candidates = new List<Candidate>(nodeCount * (nodeCount - 1) / 2);
        for (int low = 0; low < nodeCount; low++)
        for (int high = low + 1; high < nodeCount; high++)
        {
            ulong edgeKey = Derive(EdgeKeySalt, nodes[low].Key, nodes[high].Key);
            candidates.Add(new Candidate(
                low,
                high,
                DistanceSquared(nodes[low], nodes[high]),
                edgeKey,
                Derive(LoopPrioritySalt, edgeKey, plan.Key)));
        }
        candidates.Sort(CompareTreeCandidates);

        var used = new bool[nodeCount, nodeCount];
        var unionFind = new UnionFind(nodeCount);
        var edges = new List<CaveChamberGraphEdge>(nodeCount + MaximumLoops - 1);
        var degree = new int[nodeCount];
        int treeEdges = 0;
        foreach (Candidate candidate in candidates)
        {
            if (!unionFind.Join(candidate.Low, candidate.High)) continue;
            AddEdge(candidate, CaveChamberEdgeKind.Tree, used, edges, degree);
            treeEdges++;
            if (treeEdges == nodeCount - 1) break;
        }
        if (treeEdges != nodeCount - 1)
            throw new InvalidOperationException("Chamber anchor spanning tree is disconnected.");

        int desiredLoops = Math.Clamp(
            nodeCount / LoopAnchorDivisor,
            MinimumLoops,
            MaximumLoops);
        var loopCandidates = new List<Candidate>(candidates.Count - treeEdges);
        foreach (Candidate candidate in candidates)
            if (!used[candidate.Low, candidate.High]) loopCandidates.Add(candidate);
        loopCandidates.Sort(CompareLoopCandidates);

        int loopEdges = 0;
        if (!HasBranch(degree))
        {
            foreach (Candidate candidate in loopCandidates)
            {
                if (degree[candidate.Low] < BranchDegree - 1
                    && degree[candidate.High] < BranchDegree - 1)
                    continue;
                AddEdge(candidate, CaveChamberEdgeKind.Loop, used, edges, degree);
                loopEdges++;
                break;
            }
        }
        foreach (Candidate candidate in loopCandidates)
        {
            if (loopEdges >= desiredLoops) break;
            if (used[candidate.Low, candidate.High]) continue;
            AddEdge(candidate, CaveChamberEdgeKind.Loop, used, edges, degree);
            loopEdges++;
        }
        bool hasBranch = HasBranch(degree);
        if (loopEdges != desiredLoops || !hasBranch)
            throw new InvalidOperationException("Sparse chamber graph did not satisfy its loop/branch contract.");

        edges.Sort(CompareInstalledEdges);
        return CreateGraph(plan, nodes, edges.ToArray(), treeEdges, loopEdges, hasBranch);
    }

    private CaveChamberGraph CreateGraph(
        CaveProvincePlan plan,
        CaveChamberAnchorSet anchors,
        CaveChamberGraphEdge[] edges,
        int treeEdges,
        int loopEdges,
        bool hasBranch)
    {
        ulong identity = MixIdentity(Identity, GraphIdentitySalt);
        identity = MixIdentity(identity, plan.Key);
        identity = MixIdentity(identity, anchors.Identity);
        identity = MixIdentity(identity, (ulong)anchors.Count);
        identity = MixIdentity(identity, (ulong)treeEdges);
        identity = MixIdentity(identity, (ulong)loopEdges);
        foreach (CaveChamberGraphEdge edge in edges)
        {
            identity = MixIdentity(identity, edge.Key);
            identity = MixIdentity(identity, (ulong)edge.LowRank);
            identity = MixIdentity(identity, (ulong)edge.HighRank);
            identity = MixIdentity(identity, edge.SquaredLength);
            identity = MixIdentity(identity, (ulong)edge.Kind);
        }
        return new CaveChamberGraph(
            plan,
            anchors,
            Avalanche(identity),
            edges,
            treeEdges,
            loopEdges,
            hasBranch);
    }

    private static void AddEdge(
        Candidate candidate,
        CaveChamberEdgeKind kind,
        bool[,] used,
        List<CaveChamberGraphEdge> edges,
        int[] degree)
    {
        if (used[candidate.Low, candidate.High])
            throw new InvalidOperationException("Duplicate chamber graph edge selected.");
        used[candidate.Low, candidate.High] = true;
        edges.Add(new CaveChamberGraphEdge(
            candidate.Key,
            candidate.Low,
            candidate.High,
            candidate.Distance,
            kind));
        degree[candidate.Low]++;
        degree[candidate.High]++;
    }

    private static int CompareTreeCandidates(Candidate left, Candidate right)
    {
        int comparison = left.Distance.CompareTo(right.Distance);
        if (comparison != 0) return comparison;
        comparison = left.Key.CompareTo(right.Key);
        if (comparison != 0) return comparison;
        comparison = left.Low.CompareTo(right.Low);
        return comparison != 0 ? comparison : left.High.CompareTo(right.High);
    }

    private static int CompareLoopCandidates(Candidate left, Candidate right)
    {
        int comparison = left.Distance.CompareTo(right.Distance);
        if (comparison != 0) return comparison;
        comparison = left.LoopPriority.CompareTo(right.LoopPriority);
        if (comparison != 0) return comparison;
        comparison = left.Key.CompareTo(right.Key);
        if (comparison != 0) return comparison;
        comparison = left.Low.CompareTo(right.Low);
        return comparison != 0 ? comparison : left.High.CompareTo(right.High);
    }

    private static int CompareInstalledEdges(CaveChamberGraphEdge left, CaveChamberGraphEdge right)
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

    private static bool HasBranch(int[] degree)
    {
        foreach (int value in degree)
            if (value >= BranchDegree) return true;
        return false;
    }

    private ulong Derive(ulong salt, ulong first, ulong second)
    {
        ulong hash = MixIdentity(Identity, salt);
        hash = MixIdentity(hash, first);
        hash = MixIdentity(hash, second);
        return Avalanche(hash);
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

    private static ulong Avalanche(ulong value) { value ^= value >> 30; value *= 0xbf58476d1ce4e5b9UL; value ^= value >> 27; value *= 0x94d049bb133111ebUL; return value ^ (value >> 31); }
    private static ulong MixIdentity(ulong hash, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            hash ^= (byte)(value >> (i * 8));
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private readonly record struct Candidate(
        int Low,
        int High,
        ulong Distance,
        ulong Key,
        ulong LoopPriority);

    private sealed class UnionFind
    {
        private readonly int[] _parents;

        public UnionFind(int count)
        {
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
            return true;
        }
    }
}
