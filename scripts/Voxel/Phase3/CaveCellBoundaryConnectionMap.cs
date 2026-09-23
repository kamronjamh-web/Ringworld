using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase3;

public enum CaveCellBoundaryAxis
{
    Around,
    Vertical,
    Across
}

public enum CaveCellBoundaryDirection
{
    Negative = -1,
    Positive = 1
}

/// <summary>The canonical owner is always the cell on the negative side of its positive face.</summary>
public readonly record struct CaveCellBoundaryFace(
    CaveTopologyCellAddress Owner,
    CaveTopologyCellAddress Neighbor,
    CaveCellBoundaryAxis Axis,
    ulong Key);

public readonly record struct CaveCellBoundaryConnection(
    CaveCellBoundaryFace Face,
    int TreeUses,
    int LoopUses)
{
    public bool Open => true;
    public int TotalUses => checked(TreeUses + LoopUses);
}

public sealed class CaveCellBoundaryRoute
{
    private readonly CaveCellBoundaryFace[] _faces;

    internal CaveCellBoundaryRoute(
        ulong identity,
        ulong edgeKey,
        CaveChamberEdgeKind edgeKind,
        CaveTopologyCellAddress start,
        CaveTopologyCellAddress end,
        CaveCellBoundaryFace[] faces)
    {
        Identity = identity;
        EdgeKey = edgeKey;
        EdgeKind = edgeKind;
        Start = start;
        End = end;
        _faces = (CaveCellBoundaryFace[])faces.Clone();
    }

    public ulong Identity { get; }
    public ulong EdgeKey { get; }
    public CaveChamberEdgeKind EdgeKind { get; }
    public CaveTopologyCellAddress Start { get; }
    public CaveTopologyCellAddress End { get; }
    public int Count => _faces.Length;
    public CaveCellBoundaryFace this[int index] => _faces[index];
    public ReadOnlySpan<CaveCellBoundaryFace> AsSpan() => _faces;
}

public sealed class CaveCellBoundaryConnectionMap
{
    private readonly CaveCellBoundaryConnection[] _connections;
    private readonly CaveCellBoundaryRoute[] _routes;
    private readonly Dictionary<(CaveTopologyCellAddress Owner, CaveCellBoundaryAxis Axis), int> _lookup;

    internal CaveCellBoundaryConnectionMap(
        ulong identity,
        CaveChamberGraph graph,
        CaveCellBoundaryConnection[] connections,
        CaveCellBoundaryRoute[] routes)
    {
        Identity = identity;
        Graph = graph;
        _connections = (CaveCellBoundaryConnection[])connections.Clone();
        _routes = (CaveCellBoundaryRoute[])routes.Clone();
        _lookup = new Dictionary<(CaveTopologyCellAddress, CaveCellBoundaryAxis), int>(_connections.Length);
        for (int i = 0; i < _connections.Length; i++)
        {
            CaveCellBoundaryFace face = _connections[i].Face;
            if (!_lookup.TryAdd((face.Owner, face.Axis), i))
                throw new InvalidOperationException("Duplicate canonical cave cell face in connection map.");
        }
    }

    public ulong Identity { get; }
    public CaveChamberGraph Graph { get; }
    public int Count => _connections.Length;
    public int RouteCount => _routes.Length;
    public CaveCellBoundaryConnection this[int index] => _connections[index];
    public CaveCellBoundaryRoute Route(int index) => _routes[index];
    public ReadOnlySpan<CaveCellBoundaryConnection> AsSpan() => _connections;

    public bool TryGet(
        CaveCellBoundaryFace face,
        out CaveCellBoundaryConnection connection)
    {
        if (_lookup.TryGetValue((face.Owner, face.Axis), out int index)
            && _connections[index].Face == face)
        {
            connection = _connections[index];
            return true;
        }
        connection = default;
        return false;
    }

    public bool IsOpen(CaveCellBoundaryFace face) => TryGet(face, out _);
}

/// <summary>Canonical symmetric cell faces routed only from sparse chamber graph edges.</summary>
public sealed class CaveCellBoundaryConnectionMapGenerator
{
    public const int Version = 1;
    public const int FaceCanonicalizationVersion = 1;
    public const int BalancedRouteVersion = 1;
    public const int MaximumRouteSteps = 3 * (CaveTopologyPartition.CellsPerProvinceAxis - 1);

    private const ulong IdentitySalt = 0x424F554E44563101UL;
    private const ulong FaceKeySalt = 0x424F554E44464143UL;
    private const ulong AxisTieSalt = 0x424F554E44544945UL;
    private const ulong RouteIdentitySalt = 0x424F554E44525445UL;
    private const ulong MapIdentitySalt = 0x424F554E444D4150UL;

    public CaveChamberGraphGenerator Graphs { get; }
    public ulong Identity { get; }

    public CaveCellBoundaryConnectionMapGenerator(CaveChamberGraphGenerator graphs)
    {
        Graphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
        ulong identity = MixIdentity(graphs.Identity, IdentitySalt);
        identity = MixIdentity(identity, Version);
        identity = MixIdentity(identity, FaceCanonicalizationVersion);
        identity = MixIdentity(identity, BalancedRouteVersion);
        identity = MixIdentity(identity, MaximumRouteSteps);
        Identity = Avalanche(identity);
    }

    public CaveCellBoundaryConnectionMap Resolve(CaveProvincePlan plan)
    {
        CaveChamberGraph graph = Graphs.Resolve(plan);
        var routes = new CaveCellBoundaryRoute[graph.Count];
        var merged = new Dictionary<
            (CaveTopologyCellAddress Owner, CaveCellBoundaryAxis Axis),
            ConnectionBuilder>();

        for (int i = 0; i < graph.Count; i++)
        {
            CaveChamberGraphEdge edge = graph[i];
            CaveCellBoundaryRoute route = BuildRoute(graph, edge);
            routes[i] = route;
            foreach (CaveCellBoundaryFace face in route.AsSpan())
            {
                var lookupKey = (face.Owner, face.Axis);
                merged.TryGetValue(lookupKey, out ConnectionBuilder builder);
                if (builder.TotalUses > 0 && builder.Face != face)
                    throw new InvalidOperationException("Canonical cave face identity mismatch during route merge.");
                if (edge.Kind == CaveChamberEdgeKind.Tree) builder.TreeUses++;
                else builder.LoopUses++;
                builder.Face = face;
                merged[lookupKey] = builder;
            }
        }

        var connections = new List<CaveCellBoundaryConnection>(merged.Count);
        foreach (ConnectionBuilder builder in merged.Values)
            connections.Add(new CaveCellBoundaryConnection(
                builder.Face,
                builder.TreeUses,
                builder.LoopUses));
        connections.Sort(CompareConnections);

        ulong mapIdentity = MixIdentity(Identity, MapIdentitySalt);
        mapIdentity = MixIdentity(mapIdentity, graph.Identity);
        mapIdentity = MixIdentity(mapIdentity, (ulong)routes.Length);
        foreach (CaveCellBoundaryRoute route in routes)
        {
            mapIdentity = MixIdentity(mapIdentity, route.Identity);
            mapIdentity = MixIdentity(mapIdentity, route.EdgeKey);
            mapIdentity = MixIdentity(mapIdentity, (ulong)route.EdgeKind);
            mapIdentity = MixIdentity(mapIdentity, (ulong)route.Count);
            foreach (CaveCellBoundaryFace face in route.AsSpan())
                mapIdentity = MixFace(mapIdentity, face);
        }
        mapIdentity = MixIdentity(mapIdentity, (ulong)connections.Count);
        foreach (CaveCellBoundaryConnection connection in connections)
        {
            mapIdentity = MixFace(mapIdentity, connection.Face);
            mapIdentity = MixIdentity(mapIdentity, (ulong)connection.TreeUses);
            mapIdentity = MixIdentity(mapIdentity, (ulong)connection.LoopUses);
        }
        return new CaveCellBoundaryConnectionMap(
            Avalanche(mapIdentity),
            graph,
            connections.ToArray(),
            routes);
    }

    public CaveCellBoundaryFace ResolveFace(
        CaveTopologyCellAddress address,
        CaveCellBoundaryAxis axis,
        CaveCellBoundaryDirection direction)
    {
        if (!Enum.IsDefined(axis))
            throw new ArgumentOutOfRangeException(nameof(axis));
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));

        CaveTopologyPartition partition = Graphs.Anchors.Distribution.Partition;
        CaveTopologyCellAddress canonical = partition.ResolveNormalizedCell(
            address.Around,
            address.Vertical,
            address.Across).Address;
        CaveTopologyCellAddress owner = direction == CaveCellBoundaryDirection.Positive
            ? canonical
            : Neighbor(partition, canonical, axis, -1);
        CaveTopologyCellAddress neighbor = Neighbor(partition, owner, axis, 1);
        CaveTopologyCell ownerCell = partition.ResolveNormalizedCell(
            owner.Around,
            owner.Vertical,
            owner.Across);
        CaveTopologyCell neighborCell = partition.ResolveNormalizedCell(
            neighbor.Around,
            neighbor.Vertical,
            neighbor.Across);
        ulong key = MixIdentity(Identity, FaceKeySalt);
        key = MixIdentity(key, (ulong)axis);
        key = MixIdentity(key, ownerCell.Key);
        key = MixIdentity(key, neighborCell.Key);
        return new CaveCellBoundaryFace(owner, neighbor, axis, Avalanche(key));
    }

    private CaveCellBoundaryRoute BuildRoute(
        CaveChamberGraph graph,
        CaveChamberGraphEdge edge)
    {
        CaveTopologyPartition partition = Graphs.Anchors.Distribution.Partition;
        CaveTopologyCellAddress start = graph.Anchors[edge.LowRank].OwningCellAddress;
        CaveTopologyCellAddress end = graph.Anchors[edge.HighRank].OwningCellAddress;
        var events = new List<StepEvent>(MaximumRouteSteps);
        AddAxisEvents(events, CaveCellBoundaryAxis.Around, start.Around, end.Around, edge.Key);
        AddAxisEvents(events, CaveCellBoundaryAxis.Vertical, start.Vertical, end.Vertical, edge.Key);
        AddAxisEvents(events, CaveCellBoundaryAxis.Across, start.Across, end.Across, edge.Key);
        events.Sort(CompareStepEvents);
        if (events.Count > MaximumRouteSteps)
            throw new InvalidOperationException("Chamber cell route exceeded its bounded province contract.");

        var faces = new CaveCellBoundaryFace[events.Count];
        CaveTopologyCellAddress current = start;
        for (int i = 0; i < events.Count; i++)
        {
            StepEvent step = events[i];
            CaveCellBoundaryDirection direction = step.Direction > 0
                ? CaveCellBoundaryDirection.Positive
                : CaveCellBoundaryDirection.Negative;
            CaveCellBoundaryFace face = ResolveFace(current, step.Axis, direction);
            CaveTopologyCellAddress next = direction == CaveCellBoundaryDirection.Positive
                ? face.Neighbor
                : face.Owner;
            CaveTopologyCell ownerCell = partition.ResolveNormalizedCell(
                face.Owner.Around,
                face.Owner.Vertical,
                face.Owner.Across);
            CaveTopologyCell neighborCell = partition.ResolveNormalizedCell(
                face.Neighbor.Around,
                face.Neighbor.Vertical,
                face.Neighbor.Across);
            if (!Graphs.Anchors.Distribution.PermitsDesignedCaveTopology(ownerCell)
                || !Graphs.Anchors.Distribution.PermitsDesignedCaveTopology(neighborCell))
                throw new InvalidOperationException("Chamber cell route escaped its eligible province envelope.");
            faces[i] = face;
            current = next;
        }
        if (current != end)
            throw new InvalidOperationException("Balanced chamber cell route did not reach its destination.");

        ulong routeIdentity = MixIdentity(Identity, RouteIdentitySalt);
        routeIdentity = MixIdentity(routeIdentity, edge.Key);
        routeIdentity = MixIdentity(routeIdentity, (ulong)edge.Kind);
        routeIdentity = MixCellAddress(routeIdentity, start);
        routeIdentity = MixCellAddress(routeIdentity, end);
        foreach (CaveCellBoundaryFace face in faces)
            routeIdentity = MixFace(routeIdentity, face);
        return new CaveCellBoundaryRoute(
            Avalanche(routeIdentity),
            edge.Key,
            edge.Kind,
            start,
            end,
            faces);
    }

    private void AddAxisEvents(
        List<StepEvent> events,
        CaveCellBoundaryAxis axis,
        long start,
        long end,
        ulong edgeKey)
    {
        Int128 signedDelta = (Int128)end - start;
        int direction = signedDelta < 0 ? -1 : 1;
        if (signedDelta < 0) signedDelta = -signedDelta;
        int count = checked((int)signedDelta);
        if (count == 0) return;
        ulong tie = MixIdentity(Identity, AxisTieSalt);
        tie = MixIdentity(tie, edgeKey);
        tie = MixIdentity(tie, (ulong)axis);
        tie = Avalanche(tie);
        for (int step = 0; step < count; step++)
            events.Add(new StepEvent(
                axis,
                direction,
                step * 2 + 1,
                count * 2,
                tie));
    }

    private static int CompareStepEvents(StepEvent left, StepEvent right)
    {
        long leftProduct = (long)left.Numerator * right.Denominator;
        long rightProduct = (long)right.Numerator * left.Denominator;
        int comparison = leftProduct.CompareTo(rightProduct);
        if (comparison != 0) return comparison;
        comparison = left.AxisTie.CompareTo(right.AxisTie);
        if (comparison != 0) return comparison;
        return left.Axis.CompareTo(right.Axis);
    }

    private static CaveTopologyCellAddress Neighbor(
        CaveTopologyPartition partition,
        CaveTopologyCellAddress address,
        CaveCellBoundaryAxis axis,
        int direction)
    {
        return axis switch
        {
            CaveCellBoundaryAxis.Around => partition.ResolveNormalizedCell(
                checked(address.Around + direction),
                address.Vertical,
                address.Across).Address,
            CaveCellBoundaryAxis.Vertical => partition.ResolveNormalizedCell(
                address.Around,
                checked(address.Vertical + direction),
                address.Across).Address,
            CaveCellBoundaryAxis.Across => partition.ResolveNormalizedCell(
                address.Around,
                address.Vertical,
                checked(address.Across + direction)).Address,
            _ => throw new ArgumentOutOfRangeException(nameof(axis))
        };
    }

    private static int CompareConnections(
        CaveCellBoundaryConnection left,
        CaveCellBoundaryConnection right)
    {
        int comparison = left.Face.Owner.Around.CompareTo(right.Face.Owner.Around);
        if (comparison != 0) return comparison;
        comparison = left.Face.Owner.Vertical.CompareTo(right.Face.Owner.Vertical);
        if (comparison != 0) return comparison;
        comparison = left.Face.Owner.Across.CompareTo(right.Face.Owner.Across);
        if (comparison != 0) return comparison;
        comparison = left.Face.Axis.CompareTo(right.Face.Axis);
        return comparison != 0 ? comparison : left.Face.Key.CompareTo(right.Face.Key);
    }

    private static ulong MixFace(ulong hash, CaveCellBoundaryFace face)
    {
        hash = MixCellAddress(hash, face.Owner);
        hash = MixCellAddress(hash, face.Neighbor);
        hash = MixIdentity(hash, (ulong)face.Axis);
        return MixIdentity(hash, face.Key);
    }

    private static ulong MixCellAddress(ulong hash, CaveTopologyCellAddress address)
    {
        hash = MixIdentity(hash, unchecked((ulong)address.Around));
        hash = MixIdentity(hash, unchecked((ulong)address.Vertical));
        return MixIdentity(hash, unchecked((ulong)address.Across));
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

    private readonly record struct StepEvent(
        CaveCellBoundaryAxis Axis,
        int Direction,
        int Numerator,
        int Denominator,
        ulong AxisTie);

    private struct ConnectionBuilder
    {
        public CaveCellBoundaryFace Face;
        public int TreeUses;
        public int LoopUses;
        public readonly int TotalUses => checked(TreeUses + LoopUses);
    }
}
