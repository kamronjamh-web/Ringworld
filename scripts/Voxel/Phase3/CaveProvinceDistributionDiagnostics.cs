using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase3;

public static class CaveProvinceDistributionDiagnostics
{
    public static bool RunSelfTest(out string summary)
    {
        try
        {
            var contract = CaveGenerationContract.Create(7331);
            var partition = new CaveTopologyPartition(contract, 16_384L * 40 + 1);
            var distribution = new CaveProvinceDistribution(partition);
            var same = new CaveProvinceDistribution(new CaveTopologyPartition(contract, 16_384L * 40 + 1));
            var changedSeed = new CaveProvinceDistribution(new CaveTopologyPartition(CaveGenerationContract.Create(7332), 16_384L * 40 + 1));
            var changedRing = new CaveProvinceDistribution(new CaveTopologyPartition(contract, 16_384L * 41 + 1));
            if (distribution.Identity != same.Identity
                || distribution.Identity == changedSeed.Identity
                || distribution.Identity == changedRing.Identity)
                throw new InvalidOperationException("distribution identity isolation failed");
            int active = 0, total = 0;
            long eligible = 0;
            CaveProvincePlan? firstActive = null;
            CaveProvincePlan? firstInactive = null;
            var shuffled = new List<CaveTopologyProvinceAddress>();
            for (long v = -8; v < 8; v++) for (long a = 0; a < 40; a++) for (long x = -1; x <= 1; x++)
            {
                CaveProvincePlan plan = distribution.ResolvePlan(new CaveTopologyProvinceAddress(a, v, x));
                total++;
                if (plan.Active) { active++; firstActive ??= plan; CaveProvinceLocalCellBounds b = plan.Bounds!.Value; eligible += b.CellCount; ValidateBounds(b, CaveTopologyPartition.CellsPerProvinceAxis); }
                else firstInactive ??= plan;
                shuffled.Add(plan.Province.Address);
            }
            if (active == 0
                || firstInactive == null
                || active < total / 16
                || active > total / 4
                || eligible * 10 >= (long)total * 32 * 32 * 32)
                throw new InvalidOperationException("activation or eligible fraction outside v1 bounds");
            CaveProvincePlan partial = distribution.ResolvePlan(16_384L * 40, 0, 0);
            if (partial.Active
                || partial.Key == 0
                || partition.ResolveNormalizedProvince(-1, 0, 0).Address.Around != partition.AroundProvinceCount - 1
                || partition.ResolveNormalizedProvince(partition.AroundProvinceCount, 0, 0).Address.Around != 0)
                throw new InvalidOperationException("undersized seam/normalized province failed");

            const int capablePartialCells = 20;
            var capablePartition = new CaveTopologyPartition(contract, 16_384L * 40 + CaveTopologyPartition.CellAroundMeters * capablePartialCells);
            var capableDistribution = new CaveProvinceDistribution(capablePartition);
            CaveProvincePlan? activePartial = null;
            for (long vertical = -32; vertical < 32 && activePartial == null; vertical++)
            for (long across = -4; across <= 4 && activePartial == null; across++)
            {
                CaveProvincePlan candidate = capableDistribution.ResolvePlan(
                    new CaveTopologyProvinceAddress(40, vertical, across));
                if (candidate.Active) activePartial = candidate;
            }
            if (activePartial == null)
                throw new InvalidOperationException("capable partial province never activated");
            ValidateBounds(activePartial.Value.Bounds!.Value, capablePartialCells);

            CaveProvincePlan selected = firstActive!.Value;
            CaveProvinceLocalCellBounds bounds = selected.Bounds!.Value;
            CaveTopologyCell inside = partition.Resolve(selected.Province.AroundOrigin + bounds.AroundStart * CaveTopologyPartition.CellAroundMeters, selected.Province.VerticalOrigin + bounds.VerticalStart * CaveTopologyPartition.CellVerticalMeters, selected.Province.AcrossOrigin + bounds.AcrossStart * CaveTopologyPartition.CellAcrossMeters);
            CaveTopologyCell insideEnd = partition.Resolve(selected.Province.AroundOrigin + (bounds.AroundEndExclusive - 1) * CaveTopologyPartition.CellAroundMeters, selected.Province.VerticalOrigin + (bounds.VerticalEndExclusive - 1) * CaveTopologyPartition.CellVerticalMeters, selected.Province.AcrossOrigin + (bounds.AcrossEndExclusive - 1) * CaveTopologyPartition.CellAcrossMeters);
            CaveTopologyCell outside = partition.Resolve(selected.Province.AroundOrigin + (bounds.AroundStart - 1) * CaveTopologyPartition.CellAroundMeters, selected.Province.VerticalOrigin + bounds.VerticalStart * CaveTopologyPartition.CellVerticalMeters, selected.Province.AcrossOrigin + bounds.AcrossStart * CaveTopologyPartition.CellAcrossMeters);
            CaveTopologyCell outsideVertical = partition.Resolve(selected.Province.AroundOrigin + bounds.AroundStart * CaveTopologyPartition.CellAroundMeters, selected.Province.VerticalOrigin + (bounds.VerticalEndExclusive) * CaveTopologyPartition.CellVerticalMeters, selected.Province.AcrossOrigin + bounds.AcrossStart * CaveTopologyPartition.CellAcrossMeters);
            if (!distribution.PermitsDesignedCaveTopology(inside)
                || !distribution.PermitsDesignedCaveTopology(insideEnd)
                || !distribution.IsSolidByDefault(outside)
                || !distribution.IsSolidByDefault(outsideVertical))
                throw new InvalidOperationException("envelope edge eligibility failed");
            foreach (int local in new[] { 0, 15, 31 })
            {
                CaveTopologyCell inactiveCell = partition.Resolve(
                    firstInactive.Value.Province.AroundOrigin + local * CaveTopologyPartition.CellAroundMeters,
                    firstInactive.Value.Province.VerticalOrigin + local * CaveTopologyPartition.CellVerticalMeters,
                    firstInactive.Value.Province.AcrossOrigin + local * CaveTopologyPartition.CellAcrossMeters);
                if (!distribution.IsSolidByDefault(inactiveCell))
                    throw new InvalidOperationException("inactive province was eligible");
            }
            Throws<ArgumentNullException>(() => new CaveProvinceDistribution(null!));
            Throws<ArgumentException>(() => distribution.ResolvePlan(changedSeed.Partition.ResolveProvince(0, 0, 0)));
            Throws<ArgumentException>(() => distribution.PermitsDesignedCaveTopology(changedSeed.Partition.Resolve(0, 0, 0)));
            shuffled.Reverse(); foreach (var address in shuffled) if (distribution.ResolvePlan(address) != same.ResolvePlan(address)) throw new InvalidOperationException("load order changed plan");
            summary = $"identity={distribution.Identity:X16} active={active}/{total} eligible={eligible}"; return true;
        }
        catch (Exception e) { summary = e.Message; return false; }
    }
    private static void ValidateBounds(CaveProvinceLocalCellBounds b, int aroundCapacity)
    {
        if (b.AroundStart < CaveProvinceDistribution.InsetCells
            || b.VerticalStart < CaveProvinceDistribution.InsetCells
            || b.AcrossStart < CaveProvinceDistribution.InsetCells
            || b.AroundEndExclusive > aroundCapacity - CaveProvinceDistribution.InsetCells
            || b.VerticalEndExclusive > CaveTopologyPartition.CellsPerProvinceAxis - CaveProvinceDistribution.InsetCells
            || b.AcrossEndExclusive > CaveTopologyPartition.CellsPerProvinceAxis - CaveProvinceDistribution.InsetCells
            || b.AroundSpan < CaveProvinceDistribution.MinimumAroundSpan
            || b.AroundSpan > CaveProvinceDistribution.MaximumAroundSpan
            || b.VerticalSpan < CaveProvinceDistribution.MinimumVerticalSpan
            || b.VerticalSpan > CaveProvinceDistribution.MaximumVerticalSpan
            || b.AcrossSpan < CaveProvinceDistribution.MinimumAcrossSpan
            || b.AcrossSpan > CaveProvinceDistribution.MaximumAcrossSpan)
            throw new InvalidOperationException("active envelope bounds invalid");
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("invalid cave province distribution input was accepted");
    }
}
