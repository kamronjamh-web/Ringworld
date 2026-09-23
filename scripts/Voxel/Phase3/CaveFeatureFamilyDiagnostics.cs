using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>Deterministic contract and SDF probes for all ten cave families.</summary>
public static class CaveFeatureFamilyDiagnostics
{
    private const int DistributionSampleCount = 50_000;

    public static bool RunSelfTest(out string summary)
    {
        try
        {
            long ring = CaveTopologyPartition.ProvinceAroundMeters * 24
                + CaveTopologyPartition.CellAroundMeters * 24;
            CaveFeatureFamilyGenerator first = Create(7331, ring);
            CaveFeatureFamilyGenerator repeated = Create(7331, ring);
            CaveFeatureFamilyGenerator changed = Create(7332, ring);
            if (first.Identity != repeated.Identity || first.Identity == changed.Identity)
                throw new InvalidOperationException("family identity isolation failed");

            var representatives = new Dictionary<CaveFeatureFamily, CaveFeatureShape>();
            var variants = new Dictionary<CaveFeatureVariant, CaveFeatureShape>();
            var counts = new int[Enum.GetValues<CaveFeatureFamily>().Length];
            CaveFeatureShape? layeredGrand = null;
            CaveFeatureShape? unlayeredGrand = null;
            for (ulong key = 1; key <= DistributionSampleCount; key++)
            {
                CaveRegionFeature anchor = SyntheticAnchor(key);
                CaveFeatureShape shape = first.ResolveAnchorForDiagnostics(anchor);
                CaveFeatureShape same = repeated.ResolveAnchorForDiagnostics(anchor);
                if (shape != same)
                    throw new InvalidOperationException("same-contract family descriptor changed");
                ValidateDescriptor(shape, anchor);
                counts[(int)shape.Family]++;
                representatives.TryAdd(shape.Family, shape);
                if (shape.Variant != CaveFeatureVariant.None)
                    variants.TryAdd(shape.Variant, shape);
                if (shape.Family == CaveFeatureFamily.GrandChamber)
                {
                    if (shape.Variant == CaveFeatureVariant.GrandLayered)
                        layeredGrand ??= shape;
                    else
                        unlayeredGrand ??= shape;
                }
            }

            if (representatives.Count != Enum.GetValues<CaveFeatureFamily>().Length)
                throw new InvalidOperationException("all ten cave families were not sampled");
            foreach (CaveFeatureVariant purpose in new[]
            {
                CaveFeatureVariant.MaintenanceTrunk,
                CaveFeatureVariant.Reservoir,
                CaveFeatureVariant.Habitat,
                CaveFeatureVariant.TransitGallery
            })
            {
                if (!variants.ContainsKey(purpose))
                    throw new InvalidOperationException($"engineered purpose {purpose} was not sampled");
            }
            if (layeredGrand is null || unlayeredGrand is null)
                throw new InvalidOperationException("grand chamber layering was not optional");

            ValidateRarity(counts);
            ValidateGeometry(representatives, variants, layeredGrand.Value, unlayeredGrand.Value);
            ValidateOrderIndependence(first, repeated);
            ValidateChangedContract(first, changed);
            ValidateIntegratedSnapshots(first, repeated, changed, ring);

            summary = $"families={representatives.Count} variants=4 "
                + $"grand_layers=optional mega={counts[(int)CaveFeatureFamily.Megacavern]} "
                + $"super={counts[(int)CaveFeatureFamily.SupermassiveCavern]} "
                + $"sample={DistributionSampleCount} halo={CaveFeatureFamilyGenerator.MaximumInfluenceMeters}";
            return true;
        }
        catch (Exception error)
        {
            summary = error.Message;
            return false;
        }
    }

    private static void ValidateDescriptor(CaveFeatureShape shape, CaveRegionFeature anchor)
    {
        if (shape.Key == 0
            || shape.AnchorKey != anchor.Key
            || shape.OwnerProvince != anchor.ProvinceAddress
            || shape.Around != anchor.AroundA
            || shape.Vertical != anchor.VerticalA
            || shape.Across != anchor.AcrossA
            || !double.IsFinite(shape.RadiusAround)
            || !double.IsFinite(shape.RadiusVertical)
            || !double.IsFinite(shape.RadiusAcross)
            || !double.IsFinite(shape.Tilt)
            || !double.IsFinite(shape.Detail)
            || shape.RadiusAround <= 0.0
            || shape.RadiusVertical <= 0.0
            || shape.RadiusAcross <= 0.0
            || shape.Detail <= 0.0)
        {
            throw new InvalidOperationException("family descriptor ownership or finite bounds failed");
        }

        switch (shape.Family)
        {
            case CaveFeatureFamily.GrandChamber:
                InRange(shape.RadiusAround, 50.0, 200.0, "grand around radius");
                InRange(shape.RadiusAcross, 50.0, 200.0, "grand across radius");
                InRange(shape.RadiusVertical, 35.0, 175.0, "grand vertical radius");
                if (shape.Variant is not (CaveFeatureVariant.None or CaveFeatureVariant.GrandLayered))
                    throw new InvalidOperationException("grand chamber variant failed");
                break;
            case CaveFeatureFamily.Megacavern:
                InRange(shape.RadiusAround, 250.0, 750.0, "megacavern around radius");
                InRange(shape.RadiusAcross, 250.0, 750.0, "megacavern across radius");
                InRange(shape.RadiusVertical, 50.0, 300.0, "megacavern vertical radius");
                RequireNoVariantOrTilt(shape);
                break;
            case CaveFeatureFamily.SupermassiveCavern:
                InRange(shape.RadiusAround, 1500.0, 3000.0, "supermassive around radius");
                InRange(shape.RadiusAcross, 1500.0, 3000.0, "supermassive across radius");
                InRange(shape.RadiusVertical, 600.0, 1200.0, "supermassive vertical radius");
                RequireNoVariantOrTilt(shape);
                break;
            case CaveFeatureFamily.FaultCavern:
                InRange(
                    Math.Abs(shape.Tilt),
                    CaveFeatureFamilyGenerator.FaultMinimumTiltRadians,
                    CaveFeatureFamilyGenerator.FaultMaximumTiltRadians,
                    "fault tilt");
                InRange(
                    shape.RadiusAcross,
                    CaveFeatureFamilyGenerator.FaultMinimumThinRadius,
                    CaveFeatureFamilyGenerator.FaultMaximumThinRadius,
                    "fault thin radius");
                if (Math.Max(shape.RadiusAround, shape.RadiusVertical) / shape.RadiusAcross < 1.5
                    || shape.Variant != CaveFeatureVariant.None)
                {
                    throw new InvalidOperationException("fault fracture anisotropy failed");
                }
                break;
            case CaveFeatureFamily.PillarHall:
                InRange(
                    shape.Detail,
                    CaveFeatureFamilyGenerator.PillarMinimumSpacing,
                    CaveFeatureFamilyGenerator.PillarMaximumSpacing,
                    "pillar spacing");
                RequireNoVariantOrTilt(shape);
                break;
            case CaveFeatureFamily.ChasmSystem:
                InRange(
                    shape.Detail,
                    CaveFeatureFamilyGenerator.ChasmMinimumShelfSpacing,
                    CaveFeatureFamilyGenerator.ChasmMaximumShelfSpacing,
                    "chasm shelf spacing");
                if (shape.RadiusVertical <= Math.Max(shape.RadiusAround, shape.RadiusAcross))
                    throw new InvalidOperationException("chasm was not vertically dominant");
                RequireNoVariantOrTilt(shape);
                break;
            case CaveFeatureFamily.UndergroundBasin:
                if (Math.Min(shape.RadiusAround, shape.RadiusAcross) <= shape.RadiusVertical)
                    throw new InvalidOperationException("basin was not horizontally broad");
                RequireNoVariantOrTilt(shape);
                break;
            case CaveFeatureFamily.CathedralCavern:
                if (shape.RadiusVertical < Math.Max(shape.RadiusAround, shape.RadiusAcross) * 1.20)
                    throw new InvalidOperationException("cathedral dome was not dominant");
                RequireNoVariantOrTilt(shape);
                break;
            case CaveFeatureFamily.CollapsedRegion:
                RequireNoVariantOrTilt(shape);
                break;
            case CaveFeatureFamily.EngineeredVoid:
                if (shape.Variant is not (CaveFeatureVariant.MaintenanceTrunk
                    or CaveFeatureVariant.Reservoir
                    or CaveFeatureVariant.Habitat
                    or CaveFeatureVariant.TransitGallery))
                {
                    throw new InvalidOperationException("engineered void had no explicit purpose");
                }
                InRange(
                    shape.Detail,
                    CaveFeatureFamilyGenerator.EngineeredMinimumSpacing,
                    CaveFeatureFamilyGenerator.EngineeredMaximumSpacing,
                    "engineered spacing");
                if (shape.Tilt != 0.0)
                    throw new InvalidOperationException("engineered void left its coordinate grid");
                ValidateEngineeredDimensions(shape);
                break;
            default:
                throw new InvalidOperationException("unknown cave family descriptor");
        }
    }

    private static void ValidateEngineeredDimensions(CaveFeatureShape shape)
    {
        bool valid = shape.Variant switch
        {
            CaveFeatureVariant.MaintenanceTrunk =>
                shape.RadiusAcross > shape.RadiusAround
                && shape.RadiusAcross > shape.RadiusVertical,
            CaveFeatureVariant.Reservoir =>
                Math.Min(shape.RadiusAround, shape.RadiusAcross) > shape.RadiusVertical,
            CaveFeatureVariant.Habitat =>
                Math.Min(shape.RadiusAround, shape.RadiusAcross) > shape.RadiusVertical,
            CaveFeatureVariant.TransitGallery =>
                shape.RadiusAround > shape.RadiusAcross
                && shape.RadiusAround > shape.RadiusVertical,
            _ => false
        };
        if (!valid)
            throw new InvalidOperationException($"engineered dimensions failed for {shape.Variant}");
    }

    private static void ValidateRarity(int[] counts)
    {
        int supermassive = counts[(int)CaveFeatureFamily.SupermassiveCavern];
        int megacavern = counts[(int)CaveFeatureFamily.Megacavern];
        int grand = counts[(int)CaveFeatureFamily.GrandChamber];
        if (supermassive is < 40 or > 180)
            throw new InvalidOperationException("supermassive rarity escaped its deterministic sanity band");
        if (megacavern is < 350 or > 750)
            throw new InvalidOperationException("megacavern rarity escaped its deterministic sanity band");
        if (!(supermassive < megacavern && megacavern < grand))
            throw new InvalidOperationException("rare family ordering failed");
    }

    private static void ValidateGeometry(
        Dictionary<CaveFeatureFamily, CaveFeatureShape> representatives,
        Dictionary<CaveFeatureVariant, CaveFeatureShape> variants,
        CaveFeatureShape layeredGrand,
        CaveFeatureShape unlayeredGrand)
    {
        if (Density(unlayeredGrand, 0.0, 0.0, 0.0) <= 0.0)
            throw new InvalidOperationException("unlayered grand chamber center was not open");
        double grandProbe = Math.Min(layeredGrand.RadiusAround, layeredGrand.RadiusAcross) * 0.35;
        if (Density(layeredGrand, 0.0, 0.0, 0.0) <= 0.0
            || Density(layeredGrand, grandProbe, 0.0, 0.0) >= 0.0
            || Density(
                layeredGrand,
                0.0,
                layeredGrand.RadiusVertical
                    * CaveFeatureFamilyGenerator.GrandLayerCenterFraction,
                0.0) <= 0.0)
        {
            throw new InvalidOperationException("grand chamber layered floor or connecting shaft failed");
        }

        foreach (CaveFeatureFamily family in new[]
        {
            CaveFeatureFamily.Megacavern,
            CaveFeatureFamily.SupermassiveCavern,
            CaveFeatureFamily.FaultCavern
        })
        {
            CaveFeatureShape shape = representatives[family];
            if (Density(shape, 0.0, 0.0, 0.0) <= 0.0
                || Density(shape, 0.0, 0.0, shape.RadiusAcross * 1.01) >= 0.0)
            {
                throw new InvalidOperationException($"{family} open/solid boundary failed");
            }
        }

        CaveFeatureShape pillar = representatives[CaveFeatureFamily.PillarHall];
        if (Density(pillar, 0.0, 0.0, 0.0) >= 0.0
            || Density(pillar, pillar.Detail * 0.5, 0.0, pillar.Detail * 0.5) <= 0.0)
        {
            throw new InvalidOperationException("pillar hall retained-column spacing failed");
        }

        CaveFeatureShape chasm = representatives[CaveFeatureFamily.ChasmSystem];
        if (Density(chasm, 0.0, 0.0, 0.0) <= 0.0
            || Density(chasm, 0.0, 0.0, chasm.RadiusAcross * 0.75) >= 0.0
            || Density(chasm, 0.0, chasm.Detail * 0.5, 0.0) >= 0.0
            || Density(chasm, 0.0, chasm.Detail * 0.25, 0.0) <= 0.0)
        {
            throw new InvalidOperationException("chasm shelves, bridge, or open shaft failed");
        }

        CaveFeatureShape basin = representatives[CaveFeatureFamily.UndergroundBasin];
        double basinFloor = -basin.RadiusVertical * CaveFeatureFamilyGenerator.BasinFloorFraction;
        double basinCenter = Density(basin, 0.0, basinFloor + 1.0, 0.0);
        double basinSide = Density(basin, 0.0, basinFloor + 1.0, basin.RadiusAcross * 0.35);
        if (basinCenter <= 0.0
            || Math.Abs(basinCenter - basinSide) > 1e-9
            || Density(basin, 0.0, basinFloor - 1.0, 0.0) >= 0.0)
        {
            throw new InvalidOperationException("underground basin flat floor failed");
        }

        CaveFeatureShape cathedral = representatives[CaveFeatureFamily.CathedralCavern];
        if (Density(cathedral, 0.0, cathedral.RadiusVertical * 0.50, 0.0) <= 0.0
            || Density(
                cathedral,
                0.0,
                cathedral.RadiusVertical * 0.50,
                cathedral.RadiusAcross * 0.90) >= 0.0
            || Density(
                cathedral,
                0.0,
                -cathedral.RadiusVertical
                    * (CaveFeatureFamilyGenerator.CathedralFloorFraction + 0.02),
                0.0) >= 0.0)
        {
            throw new InvalidOperationException("cathedral central dome or floor failed");
        }

        CaveFeatureShape collapsed = representatives[CaveFeatureFamily.CollapsedRegion];
        double slopeSign = (collapsed.Key & 1UL) == 0 ? 1.0 : -1.0;
        if (Density(collapsed, 0.0, collapsed.RadiusVertical * 0.2, 0.0) <= 0.0
            || Density(
                collapsed,
                0.0,
                -collapsed.RadiusVertical * 0.2,
                slopeSign * collapsed.RadiusAcross * 0.8) >= 0.0)
        {
            throw new InvalidOperationException("collapsed-region open volume or rubble slope failed");
        }

        ValidateEngineeredGeometry(variants[CaveFeatureVariant.MaintenanceTrunk]);
        ValidateEngineeredGeometry(variants[CaveFeatureVariant.Reservoir]);
        ValidateEngineeredGeometry(variants[CaveFeatureVariant.Habitat]);
        ValidateEngineeredGeometry(variants[CaveFeatureVariant.TransitGallery]);
    }

    private static void ValidateEngineeredGeometry(CaveFeatureShape shape)
    {
        if (Density(shape, 0.0, 0.0, 0.0) <= 0.0)
            throw new InvalidOperationException($"{shape.Variant} central clearance failed");
        switch (shape.Variant)
        {
            case CaveFeatureVariant.MaintenanceTrunk:
                if (Density(shape, 0.0, shape.RadiusVertical * 0.8, 0.0) >= 0.0)
                    throw new InvalidOperationException("maintenance-trunk wall rib failed");
                break;
            case CaveFeatureVariant.Reservoir:
            {
                double floor = -shape.RadiusVertical
                    * CaveFeatureFamilyGenerator.ReservoirFloorFraction;
                if (Density(shape, 0.0, floor - 1.0, 0.0) >= 0.0
                    || Density(shape, 0.0, floor + 1.0, 0.0) <= 0.0)
                {
                    throw new InvalidOperationException("reservoir floor failed");
                }
                break;
            }
            case CaveFeatureVariant.Habitat:
                if (Density(shape, shape.Detail * 0.5, 0.0, shape.Detail * 0.5) >= 0.0)
                    throw new InvalidOperationException("habitat support grid failed");
                break;
            case CaveFeatureVariant.TransitGallery:
                if (Density(shape, 0.0, shape.RadiusVertical * 0.8, 0.0) >= 0.0)
                    throw new InvalidOperationException("transit-gallery arch rib failed");
                break;
        }
    }

    private static void ValidateOrderIndependence(
        CaveFeatureFamilyGenerator first,
        CaveFeatureFamilyGenerator repeated)
    {
        const int count = 512;
        var expected = new Dictionary<ulong, CaveFeatureShape>(count);
        var keys = new ulong[count];
        for (int index = 0; index < count; index++)
        {
            ulong key = (ulong)(index + 10_001);
            keys[index] = key;
            CaveRegionFeature anchor = SyntheticAnchor(key);
            expected.Add(key, first.ResolveAnchorForDiagnostics(anchor));
        }

        Array.Reverse(keys);
        ValidateOrder(keys, expected, repeated, "reverse");
        for (int index = 0; index < keys.Length; index++)
        {
            int swap = (index * 73 + 19) % keys.Length;
            (keys[index], keys[swap]) = (keys[swap], keys[index]);
        }
        ValidateOrder(keys, expected, first, "shuffled");
    }

    private static void ValidateOrder(
        ulong[] keys,
        Dictionary<ulong, CaveFeatureShape> expected,
        CaveFeatureFamilyGenerator generator,
        string context)
    {
        foreach (ulong key in keys)
        {
            CaveFeatureShape actual = generator.ResolveAnchorForDiagnostics(SyntheticAnchor(key));
            if (actual != expected[key])
                throw new InvalidOperationException($"{context} load order changed a family descriptor");
        }
    }

    private static void ValidateChangedContract(
        CaveFeatureFamilyGenerator first,
        CaveFeatureFamilyGenerator changed)
    {
        CaveRegionFeature anchor = SyntheticAnchor(91_337);
        CaveFeatureShape expected = first.ResolveAnchorForDiagnostics(anchor);
        CaveFeatureShape actual = changed.ResolveAnchorForDiagnostics(anchor);
        if (expected == actual || expected.Key == actual.Key)
            throw new InvalidOperationException("changed cave contract reused a family descriptor");

        CaveRegionFeature corridor = anchor with { Kind = CaveRegionFeatureKind.CorridorSegment };
        Throws<ArgumentException>(() => first.ResolveAnchorForDiagnostics(corridor));
    }

    private static void ValidateIntegratedSnapshots(
        CaveFeatureFamilyGenerator first,
        CaveFeatureFamilyGenerator repeated,
        CaveFeatureFamilyGenerator changed,
        long ring)
    {
        CaveProvinceDistribution distribution = first.Features.Maps.Graphs.Anchors.Distribution;
        CaveProvincePlan negativePlan = FindActivePlan(distribution, partialOnly: false);
        CaveProvincePlan partialPlan = FindActivePlan(distribution, partialOnly: true);
        CaveChamberAnchor negativeAnchor = first.Features.Maps.Resolve(negativePlan).Graph.Anchors[0];
        CaveChamberAnchor partialAnchor = first.Features.Maps.Resolve(partialPlan).Graph.Anchors[0];

        CaveRegionFeature realFeature = first.Features.CreateAnchorFeatureForDiagnostics(negativeAnchor);
        CaveFeatureShape realShape = first.ResolveAnchorForDiagnostics(realFeature);
        ValidateDescriptor(realShape, realFeature);
        if (realShape.OwnerProvince != negativePlan.Province.Address || realShape.Vertical >= 0)
            throw new InvalidOperationException("negative-depth family ownership failed");

        var requests = new[]
        {
            RequestAround(negativeAnchor),
            RequestAround(partialAnchor),
            new CaveRegionFeatureRequest(
                ring - 16,
                negativeAnchor.Vertical - 16,
                negativeAnchor.Across - 16,
                32,
                32,
                32,
                CaveFeatureFamilyGenerator.MaximumInfluenceMeters)
        };
        var expected = new Dictionary<CaveRegionFeatureRequest, ulong>();
        foreach (CaveRegionFeatureRequest request in requests)
        {
            CaveRegionFeatureSnapshot source = first.Features.Capture(request);
            CaveFeatureFamilySnapshot snapshot = first.Resolve(source);
            CaveFeatureFamilySnapshot same = repeated.Resolve(repeated.Features.Capture(request));
            AssertSameSnapshot(snapshot, same);
            if (source.CandidateProvinceCount > CaveRegionFeatureSnapshotGenerator.MaximumCandidateProvinces
                || source.Count > CaveRegionFeatureSnapshotGenerator.MaximumFeatures
                || snapshot.Count > source.Count)
            {
                throw new InvalidOperationException("family snapshot exceeded its bounded source capture");
            }
            expected.Add(request, snapshot.Identity);
        }

        Array.Reverse(requests);
        foreach (CaveRegionFeatureRequest request in requests)
        {
            ulong identity = first.Resolve(first.Features.Capture(request)).Identity;
            if (identity != expected[request])
                throw new InvalidOperationException("reverse region order changed a family snapshot");
        }
        CaveFeatureFamilySnapshot changedSnapshot = changed.Resolve(
            changed.Features.Capture(requests[0]));
        if (changedSnapshot.Identity == expected[requests[0]])
            throw new InvalidOperationException("changed shared terrain seed reused a family snapshot identity");
    }

    private static CaveRegionFeatureRequest RequestAround(CaveChamberAnchor anchor) =>
        new(
            anchor.Around - 16,
            anchor.Vertical - 16,
            anchor.Across - 16,
            32,
            32,
            32,
            CaveFeatureFamilyGenerator.MaximumInfluenceMeters);

    private static void AssertSameSnapshot(
        CaveFeatureFamilySnapshot expected,
        CaveFeatureFamilySnapshot actual)
    {
        if (expected.Identity != actual.Identity || expected.Count != actual.Count)
            throw new InvalidOperationException("same-contract family snapshot changed");
        ReadOnlySpan<CaveFeatureShape> expectedShapes = expected.AsSpan();
        ReadOnlySpan<CaveFeatureShape> actualShapes = actual.AsSpan();
        for (int index = 0; index < expectedShapes.Length; index++)
        {
            if (expectedShapes[index] != actualShapes[index])
                throw new InvalidOperationException("same-contract family snapshot contents changed");
        }
    }

    private static CaveProvincePlan FindActivePlan(
        CaveProvinceDistribution distribution,
        bool partialOnly)
    {
        long start = partialOnly ? distribution.Partition.AroundProvinceCount - 1 : 0;
        long end = partialOnly
            ? distribution.Partition.AroundProvinceCount
            : distribution.Partition.AroundProvinceCount - 1;
        for (long vertical = -64; vertical < 0; vertical++)
        for (long around = start; around < end; around++)
        for (long across = -16; across <= 16; across++)
        {
            CaveProvincePlan plan = distribution.ResolvePlan(
                new CaveTopologyProvinceAddress(around, vertical, across));
            if (plan.Active)
                return plan;
        }
        throw new InvalidOperationException("could not find an active family-test province");
    }

    private static CaveRegionFeature SyntheticAnchor(ulong key)
    {
        long signed = checked((long)key);
        var owner = new CaveTopologyProvinceAddress(
            signed % 23,
            -1 - signed % 17,
            signed % 19 - 9);
        long around = signed * 37;
        long vertical = -1024 - signed * 3;
        long across = signed * 11 - 5000;
        return new CaveRegionFeature(
            CaveRegionFeatureKind.ChamberAnchor,
            key,
            key ^ 0xA5A5A5A5A5A5A5A5UL,
            owner,
            0,
            0,
            around,
            vertical,
            across,
            around,
            vertical,
            across);
    }

    private static double Density(
        CaveFeatureShape shape,
        double around,
        double vertical,
        double across) =>
        CaveTopologyDensityComposer.EvaluateFamilyDensityAtOffset(
            shape,
            around,
            vertical,
            across);

    private static void RequireNoVariantOrTilt(CaveFeatureShape shape)
    {
        if (shape.Variant != CaveFeatureVariant.None || shape.Tilt != 0.0)
            throw new InvalidOperationException($"{shape.Family} descriptor carried an invalid variant or tilt");
    }

    private static void InRange(double value, double minimum, double maximum, string context)
    {
        if (value < minimum || value > maximum)
            throw new InvalidOperationException($"{context} escaped [{minimum}, {maximum}]");
    }

    private static CaveFeatureFamilyGenerator Create(int seed, long ring) =>
        new(new CaveRegionFeatureSnapshotGenerator(
            new CaveCellBoundaryConnectionMapGenerator(
                new CaveChamberGraphGenerator(
                    new CaveChamberAnchorGenerator(
                        new CaveProvinceDistribution(
                            new CaveTopologyPartition(
                                CaveGenerationContract.Create(seed),
                                ring)))))));

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
