using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase3;

public static class CaveCellBoundaryConnectionDiagnostics
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
                throw new InvalidOperationException("cell-boundary generator identity isolation failed");

            ValidateFaceSymmetry(generator, partition);
            Throws<ArgumentOutOfRangeException>(() => generator.ResolveFace(
                new CaveTopologyCellAddress(0, 0, 0),
                (CaveCellBoundaryAxis)99,
                CaveCellBoundaryDirection.Positive));
            Throws<ArgumentOutOfRangeException>(() => generator.ResolveFace(
                new CaveTopologyCellAddress(0, 0, 0),
                CaveCellBoundaryAxis.Around,
                (CaveCellBoundaryDirection)0));

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
                    if (orderedPlans.Count < 10) orderedPlans.Add(plan);
                }
                else inactive ??= plan;
            }
            if (active == null || inactive == null || orderedPlans.Count < 10)
                throw new InvalidOperationException("active/inactive negative-depth boundary planning failed");

            CaveCellBoundaryConnectionMap empty = generator.Resolve(inactive.Value);
            CaveTopologyCellAddress inactiveCell = partition.ResolveNormalizedCell(
                inactive.Value.Province.Address.Around * CaveTopologyPartition.CellsPerProvinceAxis,
                inactive.Value.Province.Address.Vertical * CaveTopologyPartition.CellsPerProvinceAxis,
                inactive.Value.Province.Address.Across * CaveTopologyPartition.CellsPerProvinceAxis).Address;
            CaveCellBoundaryFace closed = generator.ResolveFace(
                inactiveCell,
                CaveCellBoundaryAxis.Vertical,
                CaveCellBoundaryDirection.Positive);
            CaveCellBoundaryFace closedReverse = generator.ResolveFace(
                closed.Neighbor,
                CaveCellBoundaryAxis.Vertical,
                CaveCellBoundaryDirection.Negative);
            if (empty.Count != 0
                || empty.RouteCount != 0
                || empty.Identity == 0
                || empty.Graph.Count != 0
                || closed != closedReverse
                || empty.IsOpen(closed)
                || empty.TryGet(closed, out _))
                throw new InvalidOperationException("inactive or closed symmetric boundary contract failed");

            CaveCellBoundaryConnectionMap map = generator.Resolve(active.Value);
            ValidateMap(generator, active.Value, map);
            AssertSameMap(map, repeatedGenerator.Resolve(active.Value), "repeated generator");

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
                throw new InvalidOperationException("capable partial seam province never produced boundary routes");
            CaveCellBoundaryConnectionMap partialMap = generator.Resolve(activePartial.Value);
            ValidateMap(generator, activePartial.Value, partialMap);
            CaveProvincePlan normalizedPartial = distribution.ResolvePlan(
                new CaveTopologyProvinceAddress(-1, activePartial.Value.Province.Address.Vertical, activePartial.Value.Province.Address.Across));
            if (normalizedPartial != activePartial.Value)
                throw new InvalidOperationException("ring-normalized province changed its boundary plan");
            AssertSameMap(partialMap, generator.Resolve(normalizedPartial), "ring-normalized plan");

            var byAddress = new Dictionary<CaveTopologyProvinceAddress, CaveCellBoundaryConnectionMap>();
            foreach (CaveProvincePlan plan in orderedPlans)
                byAddress.Add(plan.Province.Address, generator.Resolve(plan));
            orderedPlans.Reverse();
            foreach (CaveProvincePlan plan in orderedPlans)
                AssertSameMap(byAddress[plan.Province.Address], generator.Resolve(plan), "reverse request order");

            var forged = new CaveProvincePlan(active.Value.Province, active.Value.Key + 1, active.Value.Active, active.Value.Bounds);
            Throws<ArgumentException>(() => generator.Resolve(forged));
            CaveProvincePlan foreign = changedSeedGenerator.Graphs.Anchors.Distribution.ResolvePlan(active.Value.Province.Address);
            Throws<ArgumentException>(() => generator.Resolve(foreign));

            summary = $"identity={map.Identity:X16} routes={map.RouteCount} open={map.Count} partial={partialMap.RouteCount}/{partialMap.Count}";
            return true;
        }
        catch (Exception exception)
        {
            summary = exception.Message;
            return false;
        }
    }

    private static CaveCellBoundaryConnectionMapGenerator CreateGenerator(
        CaveGenerationContract contract,
        long aroundLength)
    {
        return new CaveCellBoundaryConnectionMapGenerator(
            new CaveChamberGraphGenerator(
                new CaveChamberAnchorGenerator(
                    new CaveProvinceDistribution(
                        new CaveTopologyPartition(contract, aroundLength)))));
    }

    private static void ValidateFaceSymmetry(
        CaveCellBoundaryConnectionMapGenerator generator,
        CaveTopologyPartition partition)
    {
        var samples = new[]
        {
            new CaveTopologyCellAddress(0, -3, -2),
            new CaveTopologyCellAddress(partition.AroundCellCount - 1, -1, -1),
            new CaveTopologyCellAddress(-1, -9, -7)
        };
        var keys = new HashSet<ulong>();
        foreach (CaveTopologyCellAddress sample in samples)
        foreach (CaveCellBoundaryAxis axis in Enum.GetValues<CaveCellBoundaryAxis>())
        foreach (CaveCellBoundaryDirection direction in Enum.GetValues<CaveCellBoundaryDirection>())
        {
            CaveCellBoundaryFace face = generator.ResolveFace(sample, axis, direction);
            CaveTopologyCellAddress opposite = direction == CaveCellBoundaryDirection.Positive
                ? face.Neighbor
                : face.Owner;
            CaveCellBoundaryDirection reverse = direction == CaveCellBoundaryDirection.Positive
                ? CaveCellBoundaryDirection.Negative
                : CaveCellBoundaryDirection.Positive;
            CaveCellBoundaryFace fromOtherSide = generator.ResolveFace(opposite, axis, reverse);
            if (face != fromOtherSide || face.Key == 0)
                throw new InvalidOperationException("neighboring cells disagreed on canonical face identity");
            keys.Add(face.Key);
        }
        CaveCellBoundaryFace seamPositive = generator.ResolveFace(
            new CaveTopologyCellAddress(partition.AroundCellCount - 1, -1, -1),
            CaveCellBoundaryAxis.Around,
            CaveCellBoundaryDirection.Positive);
        CaveCellBoundaryFace seamNegative = generator.ResolveFace(
            new CaveTopologyCellAddress(0, -1, -1),
            CaveCellBoundaryAxis.Around,
            CaveCellBoundaryDirection.Negative);
        if (seamPositive != seamNegative
            || seamPositive.Owner.Around != partition.AroundCellCount - 1
            || seamPositive.Neighbor.Around != 0
            || keys.Count < 12)
            throw new InvalidOperationException("around seam face canonicalization failed");
    }

    private static void ValidateMap(
        CaveCellBoundaryConnectionMapGenerator generator,
        CaveProvincePlan plan,
        CaveCellBoundaryConnectionMap map)
    {
        if (map.Identity == 0
            || map.Graph.ProvincePlan != plan
            || map.RouteCount != map.Graph.Count
            || map.Count <= 0
            || map.Count > map.RouteCount * CaveCellBoundaryConnectionMapGenerator.MaximumRouteSteps
            || map.AsSpan().Length != map.Count)
            throw new InvalidOperationException("boundary map size or identity contract failed");

        var expected = new Dictionary<
            (CaveTopologyCellAddress Owner, CaveCellBoundaryAxis Axis),
            (CaveCellBoundaryFace Face, int TreeUses, int LoopUses)>();
        int totalRouteFaces = 0;
        for (int routeIndex = 0; routeIndex < map.RouteCount; routeIndex++)
        {
            CaveCellBoundaryRoute route = map.Route(routeIndex);
            CaveChamberGraphEdge edge = map.Graph[routeIndex];
            CaveTopologyCellAddress expectedStart = map.Graph.Anchors[edge.LowRank].OwningCellAddress;
            CaveTopologyCellAddress expectedEnd = map.Graph.Anchors[edge.HighRank].OwningCellAddress;
            int manhattan = checked(
                AbsDifference(expectedStart.Around, expectedEnd.Around)
                + AbsDifference(expectedStart.Vertical, expectedEnd.Vertical)
                + AbsDifference(expectedStart.Across, expectedEnd.Across));
            if (route.Identity == 0
                || route.EdgeKey != edge.Key
                || route.EdgeKind != edge.Kind
                || route.Start != expectedStart
                || route.End != expectedEnd
                || route.Count != manhattan
                || route.Count > CaveCellBoundaryConnectionMapGenerator.MaximumRouteSteps
                || route.AsSpan().Length != route.Count)
                throw new InvalidOperationException("boundary route identity, endpoint, or length failed");

            totalRouteFaces = checked(totalRouteFaces + route.Count);
            CaveTopologyCellAddress current = route.Start;
            int[] axisTotals =
            {
                AbsDifference(route.Start.Around, route.End.Around),
                AbsDifference(route.Start.Vertical, route.End.Vertical),
                AbsDifference(route.Start.Across, route.End.Across)
            };
            var axisSeen = new int[3];
            int previousNumerator = -1;
            int previousDenominator = 1;
            var routeFaces = new HashSet<(CaveTopologyCellAddress, CaveCellBoundaryAxis)>();
            for (int faceIndex = 0; faceIndex < route.Count; faceIndex++)
            {
                CaveCellBoundaryFace face = route[faceIndex];
                CaveCellBoundaryDirection direction;
                CaveTopologyCellAddress next;
                if (face.Owner == current)
                {
                    direction = CaveCellBoundaryDirection.Positive;
                    next = face.Neighbor;
                }
                else if (face.Neighbor == current)
                {
                    direction = CaveCellBoundaryDirection.Negative;
                    next = face.Owner;
                }
                else throw new InvalidOperationException("route face is not adjacent to the current cell");

                CaveCellBoundaryFace independentlyResolved = generator.ResolveFace(
                    current,
                    face.Axis,
                    direction);
                CaveCellBoundaryDirection reverse = direction == CaveCellBoundaryDirection.Positive
                    ? CaveCellBoundaryDirection.Negative
                    : CaveCellBoundaryDirection.Positive;
                if (face != independentlyResolved
                    || face != generator.ResolveFace(next, face.Axis, reverse)
                    || !routeFaces.Add((face.Owner, face.Axis))
                    || !map.IsOpen(face)
                    || !map.TryGet(face, out _))
                    throw new InvalidOperationException("route face symmetry, uniqueness, or open lookup failed");

                CaveTopologyCell owner = generator.Graphs.Anchors.Distribution.Partition.ResolveNormalizedCell(
                    face.Owner.Around, face.Owner.Vertical, face.Owner.Across);
                CaveTopologyCell neighbor = generator.Graphs.Anchors.Distribution.Partition.ResolveNormalizedCell(
                    face.Neighbor.Around, face.Neighbor.Vertical, face.Neighbor.Across);
                if (owner.Province.Address != plan.Province.Address
                    || neighbor.Province.Address != plan.Province.Address
                    || !generator.Graphs.Anchors.Distribution.PermitsDesignedCaveTopology(owner)
                    || !generator.Graphs.Anchors.Distribution.PermitsDesignedCaveTopology(neighbor))
                    throw new InvalidOperationException("route face escaped its eligible province envelope");

                int axis = (int)face.Axis;
                int numerator = axisSeen[axis] * 2 + 1;
                int denominator = axisTotals[axis] * 2;
                if (previousNumerator >= 0
                    && (long)numerator * previousDenominator
                        < (long)previousNumerator * denominator)
                    throw new InvalidOperationException("route steps are not balanced by normalized progress");
                previousNumerator = numerator;
                previousDenominator = denominator;
                axisSeen[axis]++;

                var key = (face.Owner, face.Axis);
                expected.TryGetValue(key, out var counts);
                if (counts.TreeUses + counts.LoopUses > 0 && counts.Face != face)
                    throw new InvalidOperationException("expected route merge face mismatch");
                if (edge.Kind == CaveChamberEdgeKind.Tree) counts.TreeUses++;
                else counts.LoopUses++;
                counts.Face = face;
                expected[key] = counts;
                current = next;
            }
            if (current != route.End)
                throw new InvalidOperationException("boundary route did not reach its target cell");
        }
        if (map.Count != expected.Count || totalRouteFaces < map.Count)
            throw new InvalidOperationException("boundary route merge count failed");

        var connectionKeys = new HashSet<ulong>();
        CaveCellBoundaryConnection? previous = null;
        int mergedUses = 0;
        for (int i = 0; i < map.Count; i++)
        {
            CaveCellBoundaryConnection connection = map[i];
            var key = (connection.Face.Owner, connection.Face.Axis);
            if (!expected.TryGetValue(key, out var counts)
                || counts.Face != connection.Face
                || counts.TreeUses != connection.TreeUses
                || counts.LoopUses != connection.LoopUses
                || connection.TotalUses <= 0
                || !connection.Open
                || !connectionKeys.Add(connection.Face.Key)
                || !map.TryGet(connection.Face, out CaveCellBoundaryConnection found)
                || found != connection)
                throw new InvalidOperationException("merged boundary provenance or lookup failed");
            if (previous.HasValue && CompareConnections(previous.Value, connection) > 0)
                throw new InvalidOperationException("boundary connections are not deterministically ordered");
            previous = connection;
            mergedUses = checked(mergedUses + connection.TotalUses);
        }
        if (mergedUses != totalRouteFaces)
            throw new InvalidOperationException("merged boundary provenance totals changed");

        CaveCellBoundaryFace? closed = FindClosedEligibleFace(generator, plan, map);
        if (closed == null)
            throw new InvalidOperationException("no closed eligible face was available for regression coverage");
        CaveCellBoundaryFace closedReverse = generator.ResolveFace(
            closed.Value.Neighbor,
            closed.Value.Axis,
            CaveCellBoundaryDirection.Negative);
        if (closed.Value != closedReverse || map.IsOpen(closed.Value))
            throw new InvalidOperationException("closed eligible face decision was not symmetric");
        CaveCellBoundaryFace forged = closed.Value with { Key = closed.Value.Key + 1 };
        if (map.TryGet(forged, out _))
            throw new InvalidOperationException("forged face key was accepted by the immutable map");
    }

    private static CaveCellBoundaryFace? FindClosedEligibleFace(
        CaveCellBoundaryConnectionMapGenerator generator,
        CaveProvincePlan plan,
        CaveCellBoundaryConnectionMap map)
    {
        CaveProvinceLocalCellBounds bounds = plan.Bounds!.Value;
        CaveTopologyPartition partition = generator.Graphs.Anchors.Distribution.Partition;
        for (int localAround = bounds.AroundStart; localAround < bounds.AroundEndExclusive; localAround++)
        for (int localVertical = bounds.VerticalStart; localVertical < bounds.VerticalEndExclusive; localVertical++)
        for (int localAcross = bounds.AcrossStart; localAcross < bounds.AcrossEndExclusive; localAcross++)
        {
            var address = new CaveTopologyCellAddress(
                plan.Province.Address.Around * CaveTopologyPartition.CellsPerProvinceAxis + localAround,
                plan.Province.Address.Vertical * CaveTopologyPartition.CellsPerProvinceAxis + localVertical,
                plan.Province.Address.Across * CaveTopologyPartition.CellsPerProvinceAxis + localAcross);
            foreach (CaveCellBoundaryAxis axis in Enum.GetValues<CaveCellBoundaryAxis>())
            {
                CaveCellBoundaryFace face = generator.ResolveFace(
                    address,
                    axis,
                    CaveCellBoundaryDirection.Positive);
                CaveTopologyCell neighbor = partition.ResolveNormalizedCell(
                    face.Neighbor.Around,
                    face.Neighbor.Vertical,
                    face.Neighbor.Across);
                if (generator.Graphs.Anchors.Distribution.PermitsDesignedCaveTopology(neighbor)
                    && !map.IsOpen(face))
                    return face;
            }
        }
        return null;
    }

    private static int AbsDifference(long left, long right)
    {
        Int128 difference = (Int128)left - right;
        if (difference < 0) difference = -difference;
        return checked((int)difference);
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

    private static void AssertSameMap(
        CaveCellBoundaryConnectionMap expected,
        CaveCellBoundaryConnectionMap actual,
        string context)
    {
        if (expected.Identity != actual.Identity
            || expected.Graph.Identity != actual.Graph.Identity
            || expected.Count != actual.Count
            || expected.RouteCount != actual.RouteCount)
            throw new InvalidOperationException($"{context} changed boundary map identity");
        for (int i = 0; i < expected.Count; i++)
            if (expected[i] != actual[i])
                throw new InvalidOperationException($"{context} changed boundary connection {i}");
        for (int i = 0; i < expected.RouteCount; i++)
        {
            CaveCellBoundaryRoute expectedRoute = expected.Route(i);
            CaveCellBoundaryRoute actualRoute = actual.Route(i);
            if (expectedRoute.Identity != actualRoute.Identity
                || expectedRoute.EdgeKey != actualRoute.EdgeKey
                || expectedRoute.EdgeKind != actualRoute.EdgeKind
                || expectedRoute.Start != actualRoute.Start
                || expectedRoute.End != actualRoute.End
                || expectedRoute.Count != actualRoute.Count)
                throw new InvalidOperationException($"{context} changed boundary route {i}");
            for (int face = 0; face < expectedRoute.Count; face++)
                if (expectedRoute[face] != actualRoute[face])
                    throw new InvalidOperationException($"{context} changed route {i} face {face}");
        }
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("invalid boundary input was accepted");
    }
}
