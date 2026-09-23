using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>
/// Filesystem-free distribution, landmark, exclusion, depth-band and macro-
/// spacing audit. It exercises the same immutable generator and policy used by
/// live terrain; the older family diagnostic separately guards the exact raw
/// rarity and declared radius ranges over 50,000 anchors.
/// </summary>
public static class CaveFeaturePlacementDiagnostics
{
    private const int BandSampleCount = 20_000;

    public static bool RunSelfTest(out string summary)
    {
        try
        {
            var policy = new CaveFeaturePlacementPolicy(50_000);
            CaveFeatureFamilyGenerator generator = CreateGenerator(policy);
            ValidateLandmarks(generator);
            ValidateStructuralExclusions(policy, generator);
            ValidateDepthBands(policy, generator);
            ValidateSmoothTransitions(policy);
            int macroOwners = ValidateMacroSpacing(policy);
            summary = $"families=10 authored_landmarks={generator.Landmarks.Identity:X16} "
                + $"band_samples={BandSampleCount * 7} macro_owners={macroOwners} "
                + $"spacing={CaveFeaturePlacementPolicy.SupermassiveMinimumSpacingMeters}m "
                + $"placement={policy.Identity:X16}";
            return true;
        }
        catch (Exception error)
        {
            summary = error.Message;
            return false;
        }
    }

    private static void ValidateLandmarks(CaveFeatureFamilyGenerator generator)
    {
        var representatives = new Dictionary<CaveFeatureFamily, CaveFeatureShape>();
        for (ulong key = 1; key <= 100_000 && representatives.Count < 10; key++)
        {
            CaveFeatureShape shape = generator.ResolveAnchorForDiagnostics(
                Anchor((long)key * 37, -3200, 25_000, key));
            representatives.TryAdd(shape.Family, shape);
        }
        if (representatives.Count != 10)
            throw new InvalidOperationException("authored landmark audit did not sample every family");

        foreach ((CaveFeatureFamily family, CaveFeatureShape shape) in representatives)
        {
            CaveLandmarkTemplate template = shape.Landmark;
            if (!template.IsDefined
                || template.Family != family
                || generator.Landmarks.Resolve(shape) != template)
                throw new InvalidOperationException($"{family} landmark selection was missing or unstable");

            CaveFeatureShape baseline = shape with { Landmark = default };
            bool changedDensity = false;
            for (int y = -6; y <= 6 && !changedDensity; y++)
            for (int x = -6; x <= 6 && !changedDensity; x++)
            for (int a = -6; a <= 6; a++)
            {
                double around = shape.RadiusAround * a / 12.0;
                double vertical = shape.RadiusVertical * y / 12.0;
                double across = shape.RadiusAcross * x / 12.0;
                double authored = CaveTopologyDensityComposer.EvaluateFamilyDensityAtOffset(
                    shape, around, vertical, across);
                double plain = CaveTopologyDensityComposer.EvaluateFamilyDensityAtOffset(
                    baseline, around, vertical, across);
                if (Math.Abs(authored - plain) > 0.001)
                {
                    changedDensity = true;
                    break;
                }
            }
            if (!changedDensity)
                throw new InvalidOperationException($"{family} landmark did not alter its real family SDF");
        }
    }

    private static void ValidateStructuralExclusions(
        CaveFeaturePlacementPolicy policy,
        CaveFeatureFamilyGenerator generator)
    {
        foreach (CaveFeatureFamily family in Enum.GetValues<CaveFeatureFamily>())
        {
            CaveFeatureShape shape = FindFamily(generator, family);
            CaveFeatureShape safe = shape with
            {
                Around = 25_000,
                Vertical = -4000,
                Across = 25_000
            };
            if (!policy.Permits(safe))
                throw new InvalidOperationException($"{family} was rejected inside the safe cave domain");

            double verticalExtent = Math.Abs(Math.Sin(safe.Tilt)) * safe.RadiusAround
                + Math.Abs(Math.Cos(safe.Tilt)) * safe.RadiusVertical;
            long surfaceBreach = checked((long)Math.Ceiling(
                policy.SurfaceFloorMeters - CaveFeaturePlacementPolicy.SurfaceClearanceMeters
                - verticalExtent + 1.0));
            long roofBreach = checked((long)Math.Floor(
                policy.ProtectedRoofTopMeters + CaveFeaturePlacementPolicy.RoofClearanceMeters
                + verticalExtent - 1.0));
            if (policy.Permits(safe with { Vertical = surfaceBreach })
                || policy.Permits(safe with { Vertical = roofBreach })
                || policy.Permits(safe with
                {
                    Across = checked((long)Math.Ceiling(
                        CaveFeaturePlacementPolicy.SidewallClearanceMeters
                        + safe.RadiusAcross - 1.0))
                })
                || policy.Permits(safe with
                {
                    Across = checked((long)Math.Floor(
                        policy.AcrossWidthMeters
                        - CaveFeaturePlacementPolicy.SidewallClearanceMeters
                        - safe.RadiusAcross + 1.0))
                }))
            {
                throw new InvalidOperationException($"{family} breached surface, roof, or sidewall exclusion");
            }

            long lowerHullBreach = checked((long)Math.Floor(
                policy.LowerHullTopMeters
                + CaveFeaturePlacementPolicy.LowerHullClearanceMeters
                + verticalExtent - 1.0));
            if (policy.Permits(safe with { Vertical = lowerHullBreach }))
                throw new InvalidOperationException($"{family} breached the lower-hull exclusion");
        }

        CaveRegionFeature corridor = Anchor(25_000, -1000, 25_000, 9001) with
        {
            Kind = CaveRegionFeatureKind.CorridorSegment,
            VerticalB = policy.SurfaceFloorMeters,
            AcrossB = 25_000
        };
        if (policy.PermitsConnectivity(corridor, 16.0))
            throw new InvalidOperationException("corridor endpoint B breached the surface exclusion");

        var oceanPolicy = new CaveFeaturePlacementPolicy(50_000, surfaceFloorMeters: -255);
        CaveFeatureShape oceanProbe = FindFamily(generator, CaveFeatureFamily.GrandChamber) with
        {
            Vertical = -300,
            Across = 25_000
        };
        if (oceanPolicy.Permits(oceanProbe))
            throw new InvalidOperationException("deep-ocean surface floor was ignored");
    }

    private static void ValidateDepthBands(
        CaveFeaturePlacementPolicy policy,
        CaveFeatureFamilyGenerator generator)
    {
        AuditBand(policy, generator, 128,
            CaveFeatureFamily.CollapsedRegion, CaveFeatureFamily.GrandChamber);
        AuditBand(policy, generator, 1024,
            CaveFeatureFamily.GrandChamber, CaveFeatureFamily.PillarHall,
            CaveFeatureFamily.CathedralCavern, CaveFeatureFamily.UndergroundBasin);
        AuditBand(policy, generator, 3200,
            CaveFeatureFamily.FaultCavern, CaveFeatureFamily.ChasmSystem,
            CaveFeatureFamily.Megacavern, CaveFeatureFamily.EngineeredVoid);
        AuditBand(policy, generator, 5600,
            CaveFeatureFamily.FaultCavern, CaveFeatureFamily.Megacavern,
            CaveFeatureFamily.SupermassiveCavern);
        AuditBand(policy, generator, 7050,
            CaveFeatureFamily.FaultCavern, CaveFeatureFamily.Megacavern,
            CaveFeatureFamily.SupermassiveCavern);
        AuditBand(policy, generator, 7900);
        AuditBand(policy, generator, 10_000);
    }

    private static void AuditBand(
        CaveFeaturePlacementPolicy policy,
        CaveFeatureFamilyGenerator generator,
        long depth,
        params CaveFeatureFamily[] expectedFamilies)
    {
        var seen = new HashSet<CaveFeatureFamily>();
        var expected = new HashSet<CaveFeatureFamily>(expectedFamilies);
        for (ulong key = 1; key <= BandSampleCount; key++)
        {
            long across = 4096 + (long)(key * 977UL % 41_000UL);
            CaveFeatureShape shape = generator.ResolveAnchorForDiagnostics(
                Anchor((long)key * 131, -depth, across, key));
            bool accepted = policy.Permits(shape)
                && generator.IsDepthEligibleForDiagnostics(shape);
            if (!accepted) continue;
            if (!expected.Contains(shape.Family))
                throw new InvalidOperationException(
                    $"{shape.Family} appeared outside its configured {depth} m depth band");
            ValidateSize(shape);
            seen.Add(shape.Family);
        }
        foreach (CaveFeatureFamily family in expected)
        {
            if (family != CaveFeatureFamily.SupermassiveCavern && !seen.Contains(family))
                throw new InvalidOperationException($"{family} never appeared in its {depth} m band");
        }
        if (expected.Count == 0 && seen.Count != 0)
            throw new InvalidOperationException($"random family appeared in protected {depth} m domain");
    }

    private static void ValidateSmoothTransitions(CaveFeaturePlacementPolicy policy)
    {
        foreach ((long edge, CaveFeatureFamily family) in new[]
        {
            (300L, CaveFeatureFamily.CollapsedRegion),
            (2000L, CaveFeatureFamily.PillarHall),
            (4500L, CaveFeatureFamily.ChasmSystem),
            (7000L, CaveFeatureFamily.SupermassiveCavern)
        })
        {
            double shallower = policy.FamilyWeight(family, -(edge - 1));
            double deeper = policy.FamilyWeight(family, -(edge + 1));
            if (Math.Abs(shallower - deeper) > 0.05)
                throw new InvalidOperationException($"{family} depth transition stepped at {edge} m");
        }
    }

    private static int ValidateMacroSpacing(CaveFeaturePlacementPolicy policy)
    {
        const ulong identity = 0x1234UL;
        var owners = new List<(long Around, long Across)>();
        for (long a = -8; a <= 8; a++)
        for (long x = 0; x <= 8; x++)
        {
            long around = a * CaveFeaturePlacementPolicy.SupermassiveMacroMeters
                + CaveFeaturePlacementPolicy.SupermassiveMacroMeters / 2;
            long across = x * CaveFeaturePlacementPolicy.SupermassiveMacroMeters
                + CaveFeaturePlacementPolicy.SupermassiveMacroMeters / 2;
            CaveRegionFeature anchor = Anchor(around, -5600, across, (ulong)((a + 9) * 31 + x + 1));
            if (policy.OwnsSupermassiveMacro(anchor, identity))
                owners.Add((around, across));

            CaveRegionFeature shifted = anchor with
            {
                AroundA = around + 32,
                AroundB = around + 32,
                AcrossA = across + 32,
                AcrossB = across + 32,
                Key = anchor.Key + 0x100000UL
            };
            if (policy.OwnsSupermassiveMacro(anchor, identity)
                != policy.OwnsSupermassiveMacro(shifted, identity))
                throw new InvalidOperationException("supermassive ownership changed at chunk scale");
        }
        if (owners.Count < 4)
            throw new InvalidOperationException("supermassive macro lattice produced no auditable owners");
        for (int left = 0; left < owners.Count; left++)
        for (int right = left + 1; right < owners.Count; right++)
        {
            long da = Math.Abs(owners[left].Around - owners[right].Around);
            long dx = Math.Abs(owners[left].Across - owners[right].Across);
            double distance = Math.Sqrt((double)da * da + (double)dx * dx);
            if (distance < CaveFeaturePlacementPolicy.SupermassiveMinimumSpacingMeters)
                throw new InvalidOperationException("adjacent supermassive macro opportunities were accepted");
        }
        return owners.Count;
    }

    private static CaveFeatureShape FindFamily(
        CaveFeatureFamilyGenerator generator,
        CaveFeatureFamily family)
    {
        for (ulong key = 1; key <= 100_000; key++)
        {
            CaveFeatureShape shape = generator.ResolveAnchorForDiagnostics(
                Anchor((long)key * 37, -4000, 25_000, key));
            if (shape.Family == family) return shape;
        }
        throw new InvalidOperationException($"could not sample {family}");
    }

    private static void ValidateSize(CaveFeatureShape shape)
    {
        bool valid = shape.Family switch
        {
            CaveFeatureFamily.GrandChamber =>
                In(shape.RadiusAround, 50, 200) && In(shape.RadiusVertical, 35, 175),
            CaveFeatureFamily.Megacavern =>
                In(shape.RadiusAround, 250, 750) && In(shape.RadiusVertical, 50, 300),
            CaveFeatureFamily.SupermassiveCavern =>
                In(shape.RadiusAround, 1500, 3000) && In(shape.RadiusVertical, 600, 1200),
            CaveFeatureFamily.FaultCavern => In(shape.RadiusAcross, 40, 120),
            CaveFeatureFamily.PillarHall => In(shape.RadiusAround, 110, 270),
            CaveFeatureFamily.ChasmSystem => In(shape.RadiusVertical, 300, 1000),
            CaveFeatureFamily.UndergroundBasin => In(shape.RadiusAround, 180, 480),
            CaveFeatureFamily.CathedralCavern => shape.RadiusVertical >= shape.RadiusAround * 1.2,
            CaveFeatureFamily.CollapsedRegion => In(shape.RadiusAround, 70, 220),
            CaveFeatureFamily.EngineeredVoid => shape.Variant is
                CaveFeatureVariant.MaintenanceTrunk or CaveFeatureVariant.Reservoir
                or CaveFeatureVariant.Habitat or CaveFeatureVariant.TransitGallery,
            _ => false
        };
        if (!valid || !In(shape.RadiusAcross, 1, 3000))
            throw new InvalidOperationException($"{shape.Family} escaped its configured size band");
    }

    private static bool In(double value, double minimum, double maximum) =>
        value >= minimum && value <= maximum;

    private static CaveFeatureFamilyGenerator CreateGenerator(CaveFeaturePlacementPolicy policy) =>
        new(new CaveRegionFeatureSnapshotGenerator(
            new CaveCellBoundaryConnectionMapGenerator(
                new CaveChamberGraphGenerator(
                    new CaveChamberAnchorGenerator(
                        new CaveProvinceDistribution(
                            new CaveTopologyPartition(
                                CaveGenerationContract.Create(7331),
                                CaveTopologyPartition.ProvinceAroundMeters * 32)))))),
            policy);

    private static CaveRegionFeature Anchor(
        long around,
        long vertical,
        long across,
        ulong key) =>
        new(
            CaveRegionFeatureKind.ChamberAnchor,
            key,
            key ^ 0xA5A5A5A5A5A5A5A5UL,
            default,
            0,
            0,
            around,
            vertical,
            across,
            around,
            vertical,
            across);
}
