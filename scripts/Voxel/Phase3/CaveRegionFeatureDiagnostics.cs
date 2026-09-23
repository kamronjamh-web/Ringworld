using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase3;

public static class CaveRegionFeatureDiagnostics
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
            CaveRegionFeatureSnapshotGenerator generator = CreateGenerator(contract, aroundLength);
            CaveRegionFeatureSnapshotGenerator repeatedGenerator = CreateGenerator(contract, aroundLength);
            CaveRegionFeatureSnapshotGenerator changedSeedGenerator = CreateGenerator(
                CaveGenerationContract.Create(7332),
                aroundLength);
            CaveRegionFeatureSnapshotGenerator changedRingGenerator = CreateGenerator(
                contract,
                aroundLength + 1);
            if (generator.Identity != repeatedGenerator.Identity
                || generator.Identity == changedSeedGenerator.Identity
                || generator.Identity == changedRingGenerator.Identity)
            {
                throw new InvalidOperationException("region feature generator identity isolation failed");
            }

            ValidateInvalidRequests(generator);
            CaveProvinceDistribution distribution = generator.Maps.Graphs.Anchors.Distribution;
            CaveTopologyPartition partition = distribution.Partition;
            CaveProvincePlan active = FindPlan(distribution, active: true, partialAroundOnly: false);
            CaveProvincePlan inactive = FindPlan(distribution, active: false, partialAroundOnly: false);
            CaveProvincePlan activePartial = FindPlan(distribution, active: true, partialAroundOnly: true);
            if (active.Province.Address.Vertical >= 0
                || inactive.Province.Address.Vertical >= 0
                || activePartial.Province.Address.Around != partition.AroundProvinceCount - 1)
            {
                throw new InvalidOperationException("negative-depth or partial-province search failed");
            }

            CaveRegionFeatureSnapshot empty = generator.Capture(new CaveRegionFeatureRequest(
                inactive.Province.AroundOrigin + 1,
                inactive.Province.VerticalOrigin + 1,
                inactive.Province.AcrossOrigin + 1,
                1,
                1,
                1,
                0));
            CaveRegionFeatureSnapshot shiftedEmpty = generator.Capture(empty.Request with
            {
                Vertical = empty.Request.Vertical + 1
            });
            if (empty.Identity == 0
                || empty.Count != 0
                || empty.CandidateProvinceCount != 1
                || empty.ActiveProvinceCount != 0
                || shiftedEmpty.Identity == empty.Identity)
            {
                throw new InvalidOperationException("inactive identified-empty capture contract failed");
            }

            CaveCellBoundaryConnectionMap activeMap = generator.Maps.Resolve(active);
            if (activeMap.Graph.Anchors.Count < 2 || activeMap.Count == 0)
                throw new InvalidOperationException("active province did not supply region feature sources");
            CaveChamberAnchor anchor = activeMap.Graph.Anchors[0];
            var anchorRequest = new CaveRegionFeatureRequest(
                anchor.Around,
                anchor.Vertical,
                anchor.Across,
                1,
                1,
                1,
                0);
            CaveRegionFeatureSnapshot anchorSnapshot = generator.Capture(anchorRequest);
            CaveRegionFeature anchorFeature = RequireFeature(
                anchorSnapshot,
                CaveRegionFeatureKind.ChamberAnchor,
                anchor.Key);
            if (anchorFeature.AroundA != anchor.Around
                || anchorFeature.VerticalA != anchor.Vertical
                || anchorFeature.AcrossA != anchor.Across
                || anchorFeature.AroundB != anchor.Around
                || anchorFeature.VerticalB != anchor.Vertical
                || anchorFeature.AcrossB != anchor.Across
                || anchorFeature.TreeUses != 0
                || anchorFeature.LoopUses != 0
                || anchorFeature.ProvinceAddress != anchor.ProvinceAddress)
            {
                throw new InvalidOperationException("captured chamber anchor descriptor changed its source");
            }

            CaveChamberAnchor otherAnchor = activeMap.Graph.Anchors[1];
            if (ContainsFeature(
                    anchorSnapshot,
                    CaveRegionFeatureKind.ChamberAnchor,
                    otherAnchor.Key)
                && (otherAnchor.Around != anchor.Around
                    || otherAnchor.Vertical != anchor.Vertical
                    || otherAnchor.Across != anchor.Across))
            {
                throw new InvalidOperationException("zero-halo point capture retained a remote chamber anchor");
            }

            var displacedRequest = anchorRequest with { Around = anchor.Around + 2 };
            CaveRegionFeatureSnapshot zeroHalo = generator.Capture(displacedRequest);
            CaveRegionFeatureSnapshot halo = generator.Capture(displacedRequest with { Halo = 2 });
            if (ContainsFeature(zeroHalo, CaveRegionFeatureKind.ChamberAnchor, anchor.Key)
                || !ContainsFeature(halo, CaveRegionFeatureKind.ChamberAnchor, anchor.Key)
                || zeroHalo.Identity == halo.Identity)
            {
                throw new InvalidOperationException("explicit cave feature halo filtering failed");
            }

            CaveCellBoundaryConnection connection = activeMap[0];
            (long AroundA, long VerticalA, long AcrossA, long AroundB, long VerticalB, long AcrossB) =
                CorridorCenters(partition, connection);
            CaveRegionFeatureSnapshot corridorSnapshot = generator.Capture(new CaveRegionFeatureRequest(
                AroundA,
                VerticalA,
                AcrossA,
                1,
                1,
                1,
                0));
            CaveRegionFeature corridorFeature = RequireFeature(
                corridorSnapshot,
                CaveRegionFeatureKind.CorridorSegment,
                connection.Face.Key);
            if (corridorFeature.AroundA != AroundA
                || corridorFeature.VerticalA != VerticalA
                || corridorFeature.AcrossA != AcrossA
                || corridorFeature.AroundB != AroundB
                || corridorFeature.VerticalB != VerticalB
                || corridorFeature.AcrossB != AcrossB
                || corridorFeature.TreeUses != connection.TreeUses
                || corridorFeature.LoopUses != connection.LoopUses
                || corridorFeature.ProvinceAddress != active.Province.Address)
            {
                throw new InvalidOperationException("captured corridor descriptor changed face provenance or centers");
            }

            CaveRegionFeatureSnapshot broad = generator.Capture(new CaveRegionFeatureRequest(
                anchor.Around,
                anchor.Vertical,
                anchor.Across,
                64,
                64,
                64,
                2048));
            ValidateSnapshot(generator, broad);
            AssertSameSnapshot(
                broad,
                repeatedGenerator.Capture(broad.Request),
                "repeated generator");
            if (broad.Identity == changedSeedGenerator.Capture(broad.Request).Identity
                || broad.Identity == changedRingGenerator.Capture(broad.Request).Identity)
            {
                throw new InvalidOperationException("seed or ring identity did not reach region snapshot identity");
            }

            ValidateSeamEnumerationAndFiltering(generator, aroundLength);
            CaveCellBoundaryConnectionMap partialMap = generator.Maps.Resolve(activePartial);
            CaveChamberAnchor partialAnchor = partialMap.Graph.Anchors[0];
            CaveRegionFeatureSnapshot partialSnapshot = generator.Capture(new CaveRegionFeatureRequest(
                partialAnchor.Around,
                partialAnchor.Vertical,
                partialAnchor.Across,
                1,
                1,
                1,
                0));
            CaveRegionFeature partialFeature = RequireFeature(
                partialSnapshot,
                CaveRegionFeatureKind.ChamberAnchor,
                partialAnchor.Key);
            if (partialFeature.ProvinceAddress != activePartial.Province.Address)
                throw new InvalidOperationException("partial seam province feature ownership changed");
            ValidateSnapshot(generator, partialSnapshot);

            ValidateFullRingEnumeration(contract);
            ValidateRequestOrderIndependence(generator, new[]
            {
                empty.Request,
                anchorSnapshot.Request,
                corridorSnapshot.Request,
                partialSnapshot.Request,
                broad.Request
            });

            summary = $"identity={broad.Identity:X16} features={broad.Count} candidates={broad.CandidateProvinceCount} partial={partialSnapshot.Count}";
            return true;
        }
        catch (Exception exception)
        {
            summary = exception.Message;
            return false;
        }
    }

    private static void ValidateInvalidRequests(CaveRegionFeatureSnapshotGenerator generator)
    {
        Throws<ArgumentOutOfRangeException>(() => generator.Capture(
            new CaveRegionFeatureRequest(0, 0, 0, 0, 1, 1, 0)));
        Throws<ArgumentOutOfRangeException>(() => generator.Capture(
            new CaveRegionFeatureRequest(
                0,
                0,
                0,
                CaveRegionFeatureSnapshotGenerator.MaximumRegionExtentMeters + 1,
                1,
                1,
                0)));
        Throws<ArgumentOutOfRangeException>(() => generator.Capture(
            new CaveRegionFeatureRequest(0, 0, 0, 1, 1, 1, -1)));
        Throws<ArgumentOutOfRangeException>(() => generator.Capture(
            new CaveRegionFeatureRequest(
                0,
                0,
                0,
                1,
                1,
                1,
                CaveRegionFeatureSnapshotGenerator.MaximumInfluenceHaloMeters + 1)));
        Throws<ArgumentOutOfRangeException>(() => generator.Capture(
            new CaveRegionFeatureRequest(0, long.MaxValue, 0, 1, 1, 1, 1)));
        Throws<ArgumentOutOfRangeException>(() => generator.Capture(
            new CaveRegionFeatureRequest(0, 0, long.MinValue, 1, 1, 1, 1)));
    }

    private static void ValidateSeamEnumerationAndFiltering(
        CaveRegionFeatureSnapshotGenerator generator,
        long aroundLength)
    {
        CaveTopologyPartition partition = generator.Maps.Graphs.Anchors.Distribution.Partition;
        var seamRequest = new CaveRegionFeatureRequest(
            aroundLength - 1024,
            -4096,
            0,
            2048,
            1,
            1,
            0);
        CaveTopologyProvinceAddress[] addresses = generator.ResolveCandidateProvinceAddresses(seamRequest);
        long partialProvince = partition.AroundProvinceCount - 1;
        if (!ContainsAround(addresses, 0)
            || !ContainsAround(addresses, partialProvince)
            || addresses.Length != 2)
        {
            throw new InvalidOperationException("seam request did not enumerate province zero and the partial final province once");
        }
        AssertOrderedAddresses(addresses);

        CaveRegionFeatureSnapshot canonical = generator.Capture(seamRequest);
        CaveRegionFeatureSnapshot normalized = generator.Capture(seamRequest with
        {
            Around = seamRequest.Around - aroundLength
        });
        AssertSameSnapshot(canonical, normalized, "ring-normalized request");

        CaveProvincePlan? nearZeroPlan = null;
        CaveChamberAnchor nearZeroAnchor = default;
        CaveProvinceDistribution distribution = generator.Maps.Graphs.Anchors.Distribution;
        for (long vertical = -48; vertical < 0 && nearZeroPlan == null; vertical++)
        for (long across = -16; across <= 16 && nearZeroPlan == null; across++)
        {
            CaveProvincePlan plan = distribution.ResolvePlan(
                new CaveTopologyProvinceAddress(0, vertical, across));
            if (!plan.Active) continue;
            CaveChamberAnchorSet anchors = generator.Maps.Graphs.Anchors.Resolve(plan);
            foreach (CaveChamberAnchor anchor in anchors.AsSpan())
            {
                if (anchor.Around >= CaveRegionFeatureSnapshotGenerator.MaximumInfluenceHaloMeters)
                    continue;
                nearZeroPlan = plan;
                nearZeroAnchor = anchor;
                break;
            }
        }
        if (nearZeroPlan == null)
            throw new InvalidOperationException("could not find a province-zero feature for seam filtering");

        var wrappedFeatureRequest = new CaveRegionFeatureRequest(
            aroundLength - 1,
            nearZeroAnchor.Vertical,
            nearZeroAnchor.Across,
            1,
            1,
            1,
            CaveRegionFeatureSnapshotGenerator.MaximumInfluenceHaloMeters);
        CaveRegionFeatureSnapshot wrapped = generator.Capture(wrappedFeatureRequest);
        if (!ContainsFeature(
                wrapped,
                CaveRegionFeatureKind.ChamberAnchor,
                nearZeroAnchor.Key))
        {
            throw new InvalidOperationException("seam-crossing spatial filter discarded a province-zero feature");
        }
        ValidateSnapshot(generator, wrapped);
    }

    private static void ValidateFullRingEnumeration(CaveGenerationContract contract)
    {
        long shortRingLength = CaveTopologyPartition.ProvinceAroundMeters
            + CaveTopologyPartition.CellAroundMeters * 8;
        CaveRegionFeatureSnapshotGenerator generator = CreateGenerator(contract, shortRingLength);
        var request = new CaveRegionFeatureRequest(
            shortRingLength - 1,
            -4096,
            0,
            CaveRegionFeatureSnapshotGenerator.MaximumRegionExtentMeters,
            1,
            1,
            CaveRegionFeatureSnapshotGenerator.MaximumInfluenceHaloMeters);
        CaveTopologyProvinceAddress[] addresses = generator.ResolveCandidateProvinceAddresses(request);
        if (!ContainsAround(addresses, 0)
            || !ContainsAround(addresses, 1)
            || CountDistinctAround(addresses) != 2)
        {
            throw new InvalidOperationException("full-ring halo did not enumerate every around province exactly once");
        }
        AssertOrderedAddresses(addresses);
    }

    private static void ValidateRequestOrderIndependence(
        CaveRegionFeatureSnapshotGenerator generator,
        CaveRegionFeatureRequest[] requests)
    {
        var expected = new Dictionary<CaveRegionFeatureRequest, CaveRegionFeatureSnapshot>();
        foreach (CaveRegionFeatureRequest request in requests)
            expected.Add(request, generator.Capture(request));
        Array.Reverse(requests);
        foreach (CaveRegionFeatureRequest request in requests)
            AssertSameSnapshot(expected[request], generator.Capture(request), "reverse request order");
    }

    private static void ValidateSnapshot(
        CaveRegionFeatureSnapshotGenerator generator,
        CaveRegionFeatureSnapshot snapshot)
    {
        if (snapshot.Count > CaveRegionFeatureSnapshotGenerator.MaximumFeatures
            || snapshot.CandidateProvinceCount > CaveRegionFeatureSnapshotGenerator.MaximumCandidateProvinces
            || snapshot.ActiveProvinceCount > snapshot.CandidateProvinceCount)
        {
            throw new InvalidOperationException("region snapshot exceeded a declared bound");
        }

        var sources = new HashSet<(CaveRegionFeatureKind Kind, ulong SourceKey)>();
        var maps = new Dictionary<CaveTopologyProvinceAddress, CaveCellBoundaryConnectionMap>();
        ulong previousKey = 0;
        for (int index = 0; index < snapshot.Count; index++)
        {
            CaveRegionFeature feature = snapshot[index];
            if (index > 0 && feature.Key <= previousKey)
                throw new InvalidOperationException("region features are not strictly key ordered");
            previousKey = feature.Key;
            if (!sources.Add((feature.Kind, feature.SourceKey)))
                throw new InvalidOperationException("region snapshot contains a duplicate topology source");

            if (!maps.TryGetValue(feature.ProvinceAddress, out CaveCellBoundaryConnectionMap? map))
            {
                CaveProvincePlan plan = generator.Maps.Graphs.Anchors.Distribution.ResolvePlan(
                    feature.ProvinceAddress);
                if (!plan.Active)
                    throw new InvalidOperationException("region feature names an inactive province");
                map = generator.Maps.Resolve(plan);
                maps.Add(feature.ProvinceAddress, map);
            }

            if (feature.Kind == CaveRegionFeatureKind.ChamberAnchor)
                ValidateAnchorFeature(map, feature);
            else if (feature.Kind == CaveRegionFeatureKind.CorridorSegment)
                ValidateCorridorFeature(generator, map, feature);
            else
                throw new InvalidOperationException("region snapshot contains an unknown feature kind");
        }

        CaveTopologyProvinceAddress[] candidates = generator.ResolveCandidateProvinceAddresses(
            snapshot.Request);
        if (candidates.Length != snapshot.CandidateProvinceCount)
            throw new InvalidOperationException("snapshot candidate count changed after capture");
        AssertOrderedAddresses(candidates);
    }

    private static void ValidateAnchorFeature(
        CaveCellBoundaryConnectionMap map,
        CaveRegionFeature feature)
    {
        foreach (CaveChamberAnchor anchor in map.Graph.Anchors.AsSpan())
        {
            if (anchor.Key != feature.SourceKey) continue;
            if (feature.TreeUses != 0
                || feature.LoopUses != 0
                || feature.AroundA != anchor.Around
                || feature.VerticalA != anchor.Vertical
                || feature.AcrossA != anchor.Across
                || feature.AroundB != anchor.Around
                || feature.VerticalB != anchor.Vertical
                || feature.AcrossB != anchor.Across)
            {
                throw new InvalidOperationException("region anchor feature failed source validation");
            }
            return;
        }
        throw new InvalidOperationException("region anchor source key was absent from its province graph");
    }

    private static void ValidateCorridorFeature(
        CaveRegionFeatureSnapshotGenerator generator,
        CaveCellBoundaryConnectionMap map,
        CaveRegionFeature feature)
    {
        foreach (CaveCellBoundaryConnection connection in map.AsSpan())
        {
            if (connection.Face.Key != feature.SourceKey) continue;
            var centers = CorridorCenters(
                generator.Maps.Graphs.Anchors.Distribution.Partition,
                connection);
            if (feature.TreeUses != connection.TreeUses
                || feature.LoopUses != connection.LoopUses
                || feature.TreeUses + feature.LoopUses <= 0
                || feature.AroundA != centers.AroundA
                || feature.VerticalA != centers.VerticalA
                || feature.AcrossA != centers.AcrossA
                || feature.AroundB != centers.AroundB
                || feature.VerticalB != centers.VerticalB
                || feature.AcrossB != centers.AcrossB)
            {
                throw new InvalidOperationException("region corridor feature failed source validation");
            }
            return;
        }
        throw new InvalidOperationException("region corridor source key was absent from its province map");
    }

    private static (long AroundA, long VerticalA, long AcrossA, long AroundB, long VerticalB, long AcrossB)
        CorridorCenters(CaveTopologyPartition partition, CaveCellBoundaryConnection connection)
    {
        CaveTopologyCell owner = partition.ResolveNormalizedCell(
            connection.Face.Owner.Around,
            connection.Face.Owner.Vertical,
            connection.Face.Owner.Across);
        CaveTopologyCell neighbor = partition.ResolveNormalizedCell(
            connection.Face.Neighbor.Around,
            connection.Face.Neighbor.Vertical,
            connection.Face.Neighbor.Across);
        return (
            owner.AroundOrigin + owner.AroundExtent / 2,
            owner.VerticalOrigin + owner.VerticalExtent / 2,
            owner.AcrossOrigin + owner.AcrossExtent / 2,
            neighbor.AroundOrigin + neighbor.AroundExtent / 2,
            neighbor.VerticalOrigin + neighbor.VerticalExtent / 2,
            neighbor.AcrossOrigin + neighbor.AcrossExtent / 2);
    }

    private static CaveProvincePlan FindPlan(
        CaveProvinceDistribution distribution,
        bool active,
        bool partialAroundOnly)
    {
        long aroundStart = partialAroundOnly ? distribution.Partition.AroundProvinceCount - 1 : 0;
        long aroundEnd = partialAroundOnly
            ? distribution.Partition.AroundProvinceCount
            : distribution.Partition.AroundProvinceCount - 1;
        for (long vertical = -64; vertical < 0; vertical++)
        for (long around = aroundStart; around < aroundEnd; around++)
        for (long across = -16; across <= 16; across++)
        {
            CaveProvincePlan plan = distribution.ResolvePlan(
                new CaveTopologyProvinceAddress(around, vertical, across));
            if (plan.Active == active)
                return plan;
        }
        throw new InvalidOperationException(
            $"could not find requested {(active ? "active" : "inactive")} cave province");
    }

    private static CaveRegionFeature RequireFeature(
        CaveRegionFeatureSnapshot snapshot,
        CaveRegionFeatureKind kind,
        ulong sourceKey)
    {
        foreach (CaveRegionFeature feature in snapshot.AsSpan())
        {
            if (feature.Kind == kind && feature.SourceKey == sourceKey)
                return feature;
        }
        throw new InvalidOperationException($"region snapshot omitted {kind} source {sourceKey:X16}");
    }

    private static bool ContainsFeature(
        CaveRegionFeatureSnapshot snapshot,
        CaveRegionFeatureKind kind,
        ulong sourceKey)
    {
        foreach (CaveRegionFeature feature in snapshot.AsSpan())
        {
            if (feature.Kind == kind && feature.SourceKey == sourceKey)
                return true;
        }
        return false;
    }

    private static bool ContainsAround(CaveTopologyProvinceAddress[] addresses, long around)
    {
        foreach (CaveTopologyProvinceAddress address in addresses)
        {
            if (address.Around == around)
                return true;
        }
        return false;
    }

    private static int CountDistinctAround(CaveTopologyProvinceAddress[] addresses)
    {
        var around = new HashSet<long>();
        foreach (CaveTopologyProvinceAddress address in addresses)
            around.Add(address.Around);
        return around.Count;
    }

    private static void AssertOrderedAddresses(CaveTopologyProvinceAddress[] addresses)
    {
        var unique = new HashSet<CaveTopologyProvinceAddress>();
        for (int index = 0; index < addresses.Length; index++)
        {
            if (!unique.Add(addresses[index]))
                throw new InvalidOperationException("candidate province enumeration contains a duplicate");
            if (index == 0) continue;
            CaveTopologyProvinceAddress previous = addresses[index - 1];
            CaveTopologyProvinceAddress current = addresses[index];
            bool ordered = previous.Around < current.Around
                || (previous.Around == current.Around && previous.Vertical < current.Vertical)
                || (previous.Around == current.Around
                    && previous.Vertical == current.Vertical
                    && previous.Across < current.Across);
            if (!ordered)
                throw new InvalidOperationException("candidate province enumeration is not deterministic");
        }
    }

    private static void AssertSameSnapshot(
        CaveRegionFeatureSnapshot expected,
        CaveRegionFeatureSnapshot actual,
        string context)
    {
        if (expected.Identity != actual.Identity
            || expected.Request != actual.Request
            || expected.CandidateProvinceCount != actual.CandidateProvinceCount
            || expected.ActiveProvinceCount != actual.ActiveProvinceCount
            || expected.Count != actual.Count)
        {
            throw new InvalidOperationException($"{context} changed region snapshot metadata");
        }
        for (int index = 0; index < expected.Count; index++)
        {
            if (expected[index] != actual[index])
                throw new InvalidOperationException($"{context} changed region feature {index}");
        }
    }

    private static CaveRegionFeatureSnapshotGenerator CreateGenerator(
        CaveGenerationContract contract,
        long aroundLength)
    {
        return new CaveRegionFeatureSnapshotGenerator(
            new CaveCellBoundaryConnectionMapGenerator(
                new CaveChamberGraphGenerator(
                    new CaveChamberAnchorGenerator(
                        new CaveProvinceDistribution(
                            new CaveTopologyPartition(contract, aroundLength))))));
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"expected {typeof(TException).Name}");
    }
}
