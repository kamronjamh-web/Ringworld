using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase3;

public static class CaveChamberAnchorDiagnostics
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
            var generator = new CaveChamberAnchorGenerator(distribution);
            var repeatedGenerator = new CaveChamberAnchorGenerator(
                new CaveProvinceDistribution(new CaveTopologyPartition(contract, aroundLength)));
            var changedSeedGenerator = new CaveChamberAnchorGenerator(
                new CaveProvinceDistribution(new CaveTopologyPartition(
                    CaveGenerationContract.Create(7332),
                    aroundLength)));
            var changedRingGenerator = new CaveChamberAnchorGenerator(
                new CaveProvinceDistribution(new CaveTopologyPartition(contract, aroundLength + 1)));
            if (generator.Identity != repeatedGenerator.Identity
                || generator.Identity == changedSeedGenerator.Identity
                || generator.Identity == changedRingGenerator.Identity)
                throw new InvalidOperationException("anchor generator identity isolation failed");

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
                throw new InvalidOperationException("active/inactive negative-depth anchor planning failed");

            CaveChamberAnchorSet empty = generator.Resolve(inactive.Value);
            if (empty.Count != 0
                || empty.Identity == 0
                || empty.ProvinceAddress != inactive.Value.Province.Address)
                throw new InvalidOperationException("inactive province produced anchors or lost set identity");

            CaveChamberAnchorSet anchors = generator.Resolve(active.Value);
            ValidateSet(generator, active.Value, anchors);
            CaveChamberAnchorSet repeated = repeatedGenerator.Resolve(active.Value);
            AssertSameSet(anchors, repeated, "repeated generator");

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
                throw new InvalidOperationException("capable partial seam province never produced an anchor plan");
            CaveChamberAnchorSet partialAnchors = generator.Resolve(activePartial.Value);
            ValidateSet(generator, activePartial.Value, partialAnchors);
            for (int i = 0; i < partialAnchors.Count; i++)
                if (partialAnchors[i].Around < activePartial.Value.Province.AroundOrigin
                    || partialAnchors[i].Around >= partition.AroundLengthMeters)
                    throw new InvalidOperationException("partial seam anchor escaped the canonical ring");
            CaveProvincePlan normalizedPartial = distribution.ResolvePlan(
                new CaveTopologyProvinceAddress(-1, activePartial.Value.Province.Address.Vertical, activePartial.Value.Province.Address.Across));
            if (normalizedPartial != activePartial.Value)
                throw new InvalidOperationException("ring-normalized province changed its anchor plan");

            var byAddress = new Dictionary<CaveTopologyProvinceAddress, CaveChamberAnchorSet>();
            foreach (CaveProvincePlan plan in orderedPlans)
                byAddress.Add(plan.Province.Address, generator.Resolve(plan));
            orderedPlans.Reverse();
            foreach (CaveProvincePlan plan in orderedPlans)
                AssertSameSet(byAddress[plan.Province.Address], generator.Resolve(plan), "reverse request order");

            var forged = new CaveProvincePlan(active.Value.Province, active.Value.Key + 1, active.Value.Active, active.Value.Bounds);
            Throws(() => generator.Resolve(forged));
            CaveProvincePlan foreign = changedSeedGenerator.Distribution.ResolvePlan(active.Value.Province.Address);
            Throws(() => generator.Resolve(foreign));

            summary = $"identity={generator.Identity:X16} anchors={anchors.Count} partial={partialAnchors.Count}";
            return true;
        }
        catch (Exception exception)
        {
            summary = exception.Message;
            return false;
        }
    }

    private static void ValidateSet(
        CaveChamberAnchorGenerator generator,
        CaveProvincePlan plan,
        CaveChamberAnchorSet anchors)
    {
        CaveProvinceLocalCellBounds bounds = plan.Bounds!.Value;
        int expected = Math.Clamp(
            (bounds.CellCount + CaveChamberAnchorGenerator.TargetCellsPerAnchor - 1)
                / CaveChamberAnchorGenerator.TargetCellsPerAnchor,
            CaveChamberAnchorGenerator.MinimumAnchors,
            CaveChamberAnchorGenerator.MaximumAnchors);
        if (anchors.Count != expected
            || anchors.Identity == 0
            || anchors.ProvinceAddress != plan.Province.Address
            || anchors.AsSpan().Length != anchors.Count)
            throw new InvalidOperationException("anchor set count or identity failed");

        var slots = new HashSet<int>();
        var keys = new HashSet<ulong>();
        var priorities = new HashSet<ulong>();
        var positions = new HashSet<(long Around, long Vertical, long Across)>();
        for (int i = 0; i < anchors.Count; i++)
        {
            CaveChamberAnchor anchor = anchors[i];
            CaveTopologyCell cell = generator.Distribution.Partition.Resolve(
                anchor.Around,
                anchor.Vertical,
                anchor.Across);
            if (anchor.Rank != i
                || anchor.SlotIndex < 0
                || anchor.SlotIndex >= CaveChamberAnchorGenerator.CandidateCount
                || anchor.ProvinceAddress != plan.Province.Address
                || anchor.OwningCellAddress != cell.Address
                || !generator.Distribution.PermitsDesignedCaveTopology(cell)
                || !slots.Add(anchor.SlotIndex)
                || !keys.Add(anchor.Key)
                || !priorities.Add(anchor.Priority)
                || !positions.Add((anchor.Around, anchor.Vertical, anchor.Across)))
                throw new InvalidOperationException("anchor ownership or uniqueness failed");
            if (i > 0
                && (anchors[i - 1].Priority > anchor.Priority
                    || (anchors[i - 1].Priority == anchor.Priority
                        && anchors[i - 1].SlotIndex > anchor.SlotIndex)))
                throw new InvalidOperationException("anchor priority order failed");
            ValidateCentralStratum(plan, anchor);
        }
    }

    private static void ValidateCentralStratum(CaveProvincePlan plan, CaveChamberAnchor anchor)
    {
        CaveProvinceLocalCellBounds bounds = plan.Bounds!.Value;
        int aroundStratum = anchor.SlotIndex % CaveChamberAnchorGenerator.StrataPerAxis;
        int verticalStratum = anchor.SlotIndex / CaveChamberAnchorGenerator.StrataPerAxis
            % CaveChamberAnchorGenerator.StrataPerAxis;
        int acrossStratum = anchor.SlotIndex
            / (CaveChamberAnchorGenerator.StrataPerAxis * CaveChamberAnchorGenerator.StrataPerAxis);
        ValidateAxis(anchor.Around, plan.Province.AroundOrigin,
            bounds.AroundStart * CaveTopologyPartition.CellAroundMeters,
            bounds.AroundSpan * CaveTopologyPartition.CellAroundMeters, aroundStratum);
        ValidateAxis(anchor.Vertical, plan.Province.VerticalOrigin,
            bounds.VerticalStart * CaveTopologyPartition.CellVerticalMeters,
            bounds.VerticalSpan * CaveTopologyPartition.CellVerticalMeters, verticalStratum);
        ValidateAxis(anchor.Across, plan.Province.AcrossOrigin,
            bounds.AcrossStart * CaveTopologyPartition.CellAcrossMeters,
            bounds.AcrossSpan * CaveTopologyPartition.CellAcrossMeters, acrossStratum);
    }

    private static void ValidateAxis(long position, long origin, long offset, long length, int stratum)
    {
        long start = checked(origin + offset
            + length * stratum / CaveChamberAnchorGenerator.StrataPerAxis);
        long end = checked(origin + offset
            + length * (stratum + 1) / CaveChamberAnchorGenerator.StrataPerAxis);
        long margin = (end - start) / CaveChamberAnchorGenerator.JitterInsetDivisor;
        if (position < start + margin || position >= end - margin)
            throw new InvalidOperationException("anchor escaped the central half of its stratum");
    }

    private static void AssertSameSet(
        CaveChamberAnchorSet expected,
        CaveChamberAnchorSet actual,
        string context)
    {
        if (expected.Identity != actual.Identity
            || expected.ProvinceAddress != actual.ProvinceAddress
            || expected.Count != actual.Count)
            throw new InvalidOperationException($"{context} changed anchor set identity");
        for (int i = 0; i < expected.Count; i++)
            if (expected[i] != actual[i])
                throw new InvalidOperationException($"{context} changed anchor {i}");
    }

    private static void Throws(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("foreign chamber anchor plan was accepted");
    }
}
