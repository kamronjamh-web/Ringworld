using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>Recognizable cave-space families derived from topology anchors.</summary>
public enum CaveFeatureFamily
{
    GrandChamber,
    Megacavern,
    SupermassiveCavern,
    FaultCavern,
    PillarHall,
    ChasmSystem,
    UndergroundBasin,
    CathedralCavern,
    CollapsedRegion,
    EngineeredVoid
}

/// <summary>
/// Family-specific deterministic variants. None is used when the family needs
/// no secondary descriptor; every engineered purpose is explicit save data.
/// </summary>
public enum CaveFeatureVariant
{
    None,
    GrandLayered,
    MaintenanceTrunk,
    Reservoir,
    Habitat,
    TransitGallery
}

/// <summary>Versioned, immutable family shape derived from one topology anchor.</summary>
public readonly record struct CaveFeatureShape(
    CaveFeatureFamily Family,
    CaveFeatureVariant Variant,
    ulong Key,
    ulong AnchorKey,
    CaveTopologyProvinceAddress OwnerProvince,
    long Around,
    long Vertical,
    long Across,
    double RadiusAround,
    double RadiusVertical,
    double RadiusAcross,
    double Tilt,
    double Detail,
    CaveLandmarkTemplate Landmark);

public sealed class CaveFeatureFamilySnapshot
{
    private readonly CaveFeatureShape[] _shapes;

    internal CaveFeatureFamilySnapshot(ulong identity, CaveFeatureShape[] shapes)
    {
        Identity = identity;
        _shapes = (CaveFeatureShape[])shapes.Clone();
    }

    public ulong Identity { get; }
    public int Count => _shapes.Length;
    public ReadOnlySpan<CaveFeatureShape> AsSpan() => _shapes;
}

/// <summary>
/// Stateless deterministic family assignment. It consumes only immutable
/// <see cref="CaveRegionFeature"/> anchor descriptors, never chunk state.
/// </summary>
public sealed class CaveFeatureFamilyGenerator
{
    public const int Version = 3;
    public const int AssignmentVersion = 1;
    public const int ShapeOperationVersion = 1;

    // The largest radius is 3 km. Extra room covers the conservative outside
    // wall band of the most anisotropic family SDF.
    public const long MaximumInfluenceMeters = 3200;
    public const long MaximumWallInfluencePaddingMeters = 200;

    public const int SupermassivePerThousand = 2;
    public const int MegacavernPerThousand = 10;
    public const int FaultUpperRoll = 92;
    public const int PillarUpperRoll = 172;
    public const int ChasmUpperRoll = 252;
    public const int BasinUpperRoll = 342;
    public const int CathedralUpperRoll = 432;
    public const int CollapsedUpperRoll = 522;
    public const int EngineeredUpperRoll = 612;
    public const int SelectionRollCount = 1000;

    public const double GrandMinimumRadius = 50.0;
    public const double GrandMaximumRadius = 200.0;
    public const double GrandMinimumVerticalRadius = 35.0;
    public const double GrandMaximumVerticalRadius = 175.0;
    public const double GrandLayerCenterFraction = 0.32;
    public const double GrandLayerRadiusFraction = 0.62;
    public const double GrandFloorHalfThicknessFraction = 0.045;
    public const double GrandShaftRadiusFraction = 0.13;

    public const double MegacavernMinimumRadius = 250.0;
    public const double MegacavernMaximumRadius = 750.0;
    public const double MegacavernMinimumVerticalRadius = 50.0;
    public const double MegacavernMaximumVerticalRadius = 300.0;

    public const double SupermassiveMinimumRadius = 1500.0;
    public const double SupermassiveMaximumRadius = 3000.0;
    public const double SupermassiveMinimumVerticalRadius = 600.0;
    public const double SupermassiveMaximumVerticalRadius = 1200.0;

    public const double FaultMinimumTiltRadians = 0.16;
    public const double FaultMaximumTiltRadians = 0.42;
    public const double FaultMinimumThinRadius = 40.0;
    public const double FaultMaximumThinRadius = 120.0;

    public const double PillarMinimumSpacing = 24.0;
    public const double PillarMaximumSpacing = 64.0;
    public const double PillarHalfThicknessFraction = 0.16;

    public const double ChasmMinimumShelfSpacing = 48.0;
    public const double ChasmMaximumShelfSpacing = 96.0;
    public const double ChasmShelfHalfThicknessFraction = 0.06;
    public const double ChasmShelfInnerRadiusFraction = 0.58;
    public const double ChasmBridgeHalfWidthFraction = 0.12;
    public const double ChasmBridgePhase = 0.5;

    public const double BasinFloorFraction = 0.55;
    public const double CathedralFloorFraction = 0.72;
    public const double CollapsedFloorFraction = 0.28;
    public const double CollapsedSlope = 0.35;

    public const double EngineeredMinimumSpacing = 16.0;
    public const double EngineeredMaximumSpacing = 48.0;
    public const double EngineeredRibHalfThicknessFraction = 0.07;
    public const double EngineeredSupportHalfThicknessFraction = 0.12;
    public const double EngineeredClearanceFraction = 0.62;
    public const double ReservoirFloorFraction = 0.65;

    private const ulong Salt = 0x46414D494C593032UL;

    public CaveRegionFeatureSnapshotGenerator Features { get; }
    public CaveFeaturePlacementPolicy Placement { get; }
    public CaveLandmarkTemplateCatalog Landmarks { get; }
    public ulong Identity { get; }

    public CaveFeatureFamilyGenerator(
        CaveRegionFeatureSnapshotGenerator features,
        CaveFeaturePlacementPolicy? placement = null)
    {
        Features = features ?? throw new ArgumentNullException(nameof(features));
        Placement = placement ?? CaveFeaturePlacementPolicy.UnboundedForDiagnostics;
        Landmarks = new CaveLandmarkTemplateCatalog();
        ulong identity = Mix(features.Identity, Version);
        identity = Mix(identity, Placement.Identity);
        identity = Mix(identity, Landmarks.Identity);
        identity = Mix(identity, AssignmentVersion);
        identity = Mix(identity, ShapeOperationVersion);
        identity = Mix(identity, MaximumInfluenceMeters);
        identity = Mix(identity, MaximumWallInfluencePaddingMeters);
        identity = Mix(identity, CaveFeaturePlacementPolicy.SupermassiveMacroMeters);
        identity = Mix(identity, SupermassivePerThousand);
        identity = Mix(identity, MegacavernPerThousand);
        identity = Mix(identity, FaultUpperRoll);
        identity = Mix(identity, PillarUpperRoll);
        identity = Mix(identity, ChasmUpperRoll);
        identity = Mix(identity, BasinUpperRoll);
        identity = Mix(identity, CathedralUpperRoll);
        identity = Mix(identity, CollapsedUpperRoll);
        identity = Mix(identity, EngineeredUpperRoll);
        identity = Mix(identity, SelectionRollCount);

        identity = MixDouble(identity, GrandMinimumRadius);
        identity = MixDouble(identity, GrandMaximumRadius);
        identity = MixDouble(identity, GrandMinimumVerticalRadius);
        identity = MixDouble(identity, GrandMaximumVerticalRadius);
        identity = MixDouble(identity, GrandLayerCenterFraction);
        identity = MixDouble(identity, GrandLayerRadiusFraction);
        identity = MixDouble(identity, GrandFloorHalfThicknessFraction);
        identity = MixDouble(identity, GrandShaftRadiusFraction);
        identity = MixDouble(identity, MegacavernMinimumRadius);
        identity = MixDouble(identity, MegacavernMaximumRadius);
        identity = MixDouble(identity, MegacavernMinimumVerticalRadius);
        identity = MixDouble(identity, MegacavernMaximumVerticalRadius);
        identity = MixDouble(identity, SupermassiveMinimumRadius);
        identity = MixDouble(identity, SupermassiveMaximumRadius);
        identity = MixDouble(identity, SupermassiveMinimumVerticalRadius);
        identity = MixDouble(identity, SupermassiveMaximumVerticalRadius);
        identity = MixDouble(identity, FaultMinimumTiltRadians);
        identity = MixDouble(identity, FaultMaximumTiltRadians);
        identity = MixDouble(identity, FaultMinimumThinRadius);
        identity = MixDouble(identity, FaultMaximumThinRadius);
        identity = MixDouble(identity, PillarMinimumSpacing);
        identity = MixDouble(identity, PillarMaximumSpacing);
        identity = MixDouble(identity, PillarHalfThicknessFraction);
        identity = MixDouble(identity, ChasmMinimumShelfSpacing);
        identity = MixDouble(identity, ChasmMaximumShelfSpacing);
        identity = MixDouble(identity, ChasmShelfHalfThicknessFraction);
        identity = MixDouble(identity, ChasmShelfInnerRadiusFraction);
        identity = MixDouble(identity, ChasmBridgeHalfWidthFraction);
        identity = MixDouble(identity, ChasmBridgePhase);
        identity = MixDouble(identity, BasinFloorFraction);
        identity = MixDouble(identity, CathedralFloorFraction);
        identity = MixDouble(identity, CollapsedFloorFraction);
        identity = MixDouble(identity, CollapsedSlope);
        identity = MixDouble(identity, EngineeredMinimumSpacing);
        identity = MixDouble(identity, EngineeredMaximumSpacing);
        identity = MixDouble(identity, EngineeredRibHalfThicknessFraction);
        identity = MixDouble(identity, EngineeredSupportHalfThicknessFraction);
        identity = MixDouble(identity, EngineeredClearanceFraction);
        identity = MixDouble(identity, ReservoirFloorFraction);

        // Remaining declared axis ranges are part of the family contract too.
        foreach (double value in new[]
        {
            180.0, 400.0, 260.0, 680.0,
            110.0, 270.0, 70.0, 160.0,
            90.0, 250.0, 300.0, 1000.0,
            180.0, 480.0, 55.0, 160.0,
            130.0, 340.0, 1.20, 1.65,
            70.0, 220.0, 45.0, 165.0,
            70.0, 130.0, 30.0, 60.0, 180.0, 500.0,
            120.0, 280.0, 45.0, 110.0,
            100.0, 240.0, 40.0, 100.0,
            250.0, 600.0, 35.0, 80.0, 70.0, 140.0
        })
        {
            identity = MixDouble(identity, value);
        }
        Identity = Avalanche(identity);
    }

    public CaveFeatureFamilySnapshot Resolve(CaveRegionFeatureSnapshot snapshot)
    {
        if (snapshot is null)
            throw new ArgumentNullException(nameof(snapshot));

        var shapes = new List<CaveFeatureShape>();
        foreach (CaveRegionFeature anchor in snapshot.AsSpan())
        {
            if (anchor.Kind != CaveRegionFeatureKind.ChamberAnchor)
                continue;
            CaveFeatureShape shape = ResolveAnchor(anchor);
            if (Placement.Permits(shape)
                && IsDepthEligible(shape)
                && Overlaps(snapshot.Request, shape))
                shapes.Add(shape);
        }
        shapes.Sort((left, right) => left.Key.CompareTo(right.Key));

        ulong hash = Mix(Identity, snapshot.Identity);
        hash = Mix(hash, shapes.Count);
        foreach (CaveFeatureShape shape in shapes)
        {
            hash = Mix(hash, shape.Key);
            hash = Mix(hash, shape.AnchorKey);
            hash = Mix(hash, (int)shape.Family);
            hash = Mix(hash, (int)shape.Variant);
            hash = Mix(hash, shape.OwnerProvince.Around);
            hash = Mix(hash, shape.OwnerProvince.Vertical);
            hash = Mix(hash, shape.OwnerProvince.Across);
            hash = Mix(hash, shape.Around);
            hash = Mix(hash, shape.Vertical);
            hash = Mix(hash, shape.Across);
            hash = MixDouble(hash, shape.RadiusAround);
            hash = MixDouble(hash, shape.RadiusVertical);
            hash = MixDouble(hash, shape.RadiusAcross);
            hash = MixDouble(hash, shape.Tilt);
            hash = MixDouble(hash, shape.Detail);
            foreach (char character in shape.Landmark.StableId)
                hash = Mix(hash, character);
            hash = Mix(hash, (int)shape.Landmark.Operation);
            hash = MixDouble(hash, shape.Landmark.BoundAround);
            hash = MixDouble(hash, shape.Landmark.BoundVertical);
            hash = MixDouble(hash, shape.Landmark.BoundAcross);
        }
        return new CaveFeatureFamilySnapshot(Avalanche(hash), shapes.ToArray());
    }

    /// <summary>Resolves one anchor without a region capture for bounded locators and diagnostics.</summary>
    public CaveFeatureShape ResolveAnchorForLocator(CaveRegionFeature anchor) =>
        ResolveAnchor(anchor);

    internal CaveFeatureShape ResolveAnchorForDiagnostics(CaveRegionFeature anchor) =>
        ResolveAnchorForLocator(anchor);

    public bool IsDepthEligibleForLocator(CaveFeatureShape shape) =>
        IsDepthEligible(shape);

    internal bool IsDepthEligibleForDiagnostics(CaveFeatureShape shape) =>
        IsDepthEligibleForLocator(shape);

    private CaveFeatureShape ResolveAnchor(CaveRegionFeature anchor)
    {
        if (anchor.Kind != CaveRegionFeatureKind.ChamberAnchor)
            throw new ArgumentException("A cave family can only be assigned to a chamber anchor.", nameof(anchor));

        ulong random = Avalanche(Mix(Mix(Identity, Salt), anchor.Key));
        int roll = (int)(random % SelectionRollCount);
        CaveFeatureFamily family = roll switch
        {
            < SupermassivePerThousand => CaveFeatureFamily.SupermassiveCavern,
            < SupermassivePerThousand + MegacavernPerThousand => CaveFeatureFamily.Megacavern,
            < FaultUpperRoll => CaveFeatureFamily.FaultCavern,
            < PillarUpperRoll => CaveFeatureFamily.PillarHall,
            < ChasmUpperRoll => CaveFeatureFamily.ChasmSystem,
            < BasinUpperRoll => CaveFeatureFamily.UndergroundBasin,
            < CathedralUpperRoll => CaveFeatureFamily.CathedralCavern,
            < CollapsedUpperRoll => CaveFeatureFamily.CollapsedRegion,
            < EngineeredUpperRoll => CaveFeatureFamily.EngineeredVoid,
            _ => CaveFeatureFamily.GrandChamber
        };
        if (family == CaveFeatureFamily.SupermassiveCavern
            && !Placement.OwnsSupermassiveMacro(anchor, Identity))
        {
            family = CaveFeatureFamily.Megacavern;
        }

        double u = Unit(Avalanche(random ^ 0xA0761D6478BD642FUL));
        double v = Unit(Avalanche(random ^ 0xE7037ED1A0B428DBUL));
        double w = Unit(Avalanche(random ^ 0x8EBC6AF09C88C6E3UL));
        double z = Unit(Avalanche(random ^ 0x589965CC75374CC3UL));
        CaveFeatureVariant variant = ResolveVariant(family, random, z);
        (double radiusAround, double radiusVertical, double radiusAcross) = ResolveRadii(
            family,
            variant,
            u,
            v,
            w);
        double tilt = family == CaveFeatureFamily.FaultCavern
            ? ((random & (1UL << 32)) == 0 ? -1.0 : 1.0)
                * Lerp(FaultMinimumTiltRadians, FaultMaximumTiltRadians, z)
            : 0.0;
        double detail = family switch
        {
            CaveFeatureFamily.PillarHall => Lerp(PillarMinimumSpacing, PillarMaximumSpacing, z),
            CaveFeatureFamily.ChasmSystem => Lerp(ChasmMinimumShelfSpacing, ChasmMaximumShelfSpacing, z),
            CaveFeatureFamily.EngineeredVoid => Lerp(EngineeredMinimumSpacing, EngineeredMaximumSpacing, z),
            _ => Lerp(EngineeredMinimumSpacing, EngineeredMaximumSpacing, z)
        };
        ulong key = Avalanche(Mix(Mix(Identity, anchor.Key), (int)family));
        CaveLandmarkTemplate landmark = Landmarks.Resolve(family, key);
        return new CaveFeatureShape(
            family,
            variant,
            key,
            anchor.Key,
            anchor.ProvinceAddress,
            anchor.AroundA,
            anchor.VerticalA,
            anchor.AcrossA,
            radiusAround,
            radiusVertical,
            radiusAcross,
            tilt,
            detail,
            landmark);
    }

    private bool IsDepthEligible(CaveFeatureShape shape)
    {
        double weight = Placement.FamilyWeight(shape.Family, shape.Vertical);
        if (weight >= 1.0) return true;
        if (weight <= 0.0) return false;
        double rank = Unit(Avalanche(shape.AnchorKey ^ Identity ^ 0xD6E8FEB86659FD93UL));
        return rank <= weight;
    }

    private static CaveFeatureVariant ResolveVariant(
        CaveFeatureFamily family,
        ulong random,
        double variantUnit)
    {
        if (family == CaveFeatureFamily.GrandChamber)
            return (random & (1UL << 17)) == 0
                ? CaveFeatureVariant.None
                : CaveFeatureVariant.GrandLayered;
        if (family != CaveFeatureFamily.EngineeredVoid)
            return CaveFeatureVariant.None;
        int selection = Math.Min(3, (int)(variantUnit * 4.0));
        return selection switch
        {
            0 => CaveFeatureVariant.MaintenanceTrunk,
            1 => CaveFeatureVariant.Reservoir,
            2 => CaveFeatureVariant.Habitat,
            _ => CaveFeatureVariant.TransitGallery
        };
    }

    private static (double Around, double Vertical, double Across) ResolveRadii(
        CaveFeatureFamily family,
        CaveFeatureVariant variant,
        double u,
        double v,
        double w)
    {
        return family switch
        {
            CaveFeatureFamily.GrandChamber => (
                Lerp(GrandMinimumRadius, GrandMaximumRadius, u),
                Lerp(GrandMinimumVerticalRadius, GrandMaximumVerticalRadius, w),
                Lerp(GrandMinimumRadius, GrandMaximumRadius, v)),
            CaveFeatureFamily.Megacavern => (
                Lerp(MegacavernMinimumRadius, MegacavernMaximumRadius, u),
                Lerp(MegacavernMinimumVerticalRadius, MegacavernMaximumVerticalRadius, w),
                Lerp(MegacavernMinimumRadius, MegacavernMaximumRadius, v)),
            CaveFeatureFamily.SupermassiveCavern => (
                Lerp(SupermassiveMinimumRadius, SupermassiveMaximumRadius, u),
                Lerp(SupermassiveMinimumVerticalRadius, SupermassiveMaximumVerticalRadius, w),
                Lerp(SupermassiveMinimumRadius, SupermassiveMaximumRadius, v)),
            CaveFeatureFamily.FaultCavern => (
                Lerp(180.0, 400.0, u),
                Lerp(260.0, 680.0, w),
                Lerp(FaultMinimumThinRadius, FaultMaximumThinRadius, v)),
            CaveFeatureFamily.PillarHall => (
                Lerp(110.0, 270.0, u),
                Lerp(70.0, 160.0, w),
                Lerp(110.0, 270.0, v)),
            CaveFeatureFamily.ChasmSystem => (
                Lerp(90.0, 250.0, u),
                Lerp(300.0, 1000.0, w),
                Lerp(90.0, 250.0, v)),
            CaveFeatureFamily.UndergroundBasin => (
                Lerp(180.0, 480.0, u),
                Lerp(55.0, 160.0, w),
                Lerp(180.0, 480.0, v)),
            CaveFeatureFamily.CathedralCavern => ResolveCathedralRadii(u, v, w),
            CaveFeatureFamily.CollapsedRegion => (
                Lerp(70.0, 220.0, u),
                Lerp(45.0, 165.0, w),
                Lerp(70.0, 220.0, v)),
            CaveFeatureFamily.EngineeredVoid => ResolveEngineeredRadii(variant, u, v, w),
            _ => throw new InvalidOperationException("Unknown cave feature family.")
        };
    }

    private static (double Around, double Vertical, double Across) ResolveCathedralRadii(
        double u,
        double v,
        double w)
    {
        double around = Lerp(130.0, 340.0, u);
        double across = Lerp(130.0, 340.0, v);
        double vertical = Math.Max(around, across) * Lerp(1.20, 1.65, w);
        return (around, vertical, across);
    }

    private static (double Around, double Vertical, double Across) ResolveEngineeredRadii(
        CaveFeatureVariant variant,
        double u,
        double v,
        double w)
    {
        return variant switch
        {
            CaveFeatureVariant.MaintenanceTrunk => (
                Lerp(70.0, 130.0, u),
                Lerp(30.0, 60.0, w),
                Lerp(180.0, 500.0, v)),
            CaveFeatureVariant.Reservoir => (
                Lerp(120.0, 280.0, u),
                Lerp(45.0, 110.0, w),
                Lerp(120.0, 280.0, v)),
            CaveFeatureVariant.Habitat => (
                Lerp(100.0, 240.0, u),
                Lerp(40.0, 100.0, w),
                Lerp(100.0, 240.0, v)),
            CaveFeatureVariant.TransitGallery => (
                Lerp(250.0, 600.0, u),
                Lerp(35.0, 80.0, w),
                Lerp(70.0, 140.0, v)),
            _ => throw new InvalidOperationException("Engineered void requires an explicit purpose variant.")
        };
    }

    private bool Overlaps(CaveRegionFeatureRequest request, CaveFeatureShape shape)
    {
        double cosine = Math.Abs(Math.Cos(shape.Tilt));
        double sine = Math.Abs(Math.Sin(shape.Tilt));
        long radiusAround = checked((long)Math.Ceiling(
            shape.RadiusAround * cosine
                + shape.RadiusVertical * sine
                + MaximumWallInfluencePaddingMeters));
        long radiusVertical = checked((long)Math.Ceiling(
            shape.RadiusAround * sine
                + shape.RadiusVertical * cosine
                + MaximumWallInfluencePaddingMeters));
        long radiusAcross = checked((long)Math.Ceiling(
            shape.RadiusAcross + MaximumWallInfluencePaddingMeters));
        if ((Int128)shape.Vertical + radiusVertical < request.Vertical
            || (Int128)shape.Vertical - radiusVertical
                >= (Int128)request.Vertical + request.VerticalExtent
            || (Int128)shape.Across + radiusAcross < request.Across
            || (Int128)shape.Across - radiusAcross
                >= (Int128)request.Across + request.AcrossExtent)
        {
            return false;
        }

        long ring = Features.Maps.Graphs.Anchors.Distribution.Partition.AroundLengthMeters;
        Int128 low = request.Around;
        Int128 high = (Int128)request.Around + request.AroundExtent;
        if (high - low >= ring)
            return true;
        for (int shift = -1; shift <= 1; shift++)
        {
            Int128 center = (Int128)shape.Around + shift * (Int128)ring;
            if (center + radiusAround >= low && center - radiusAround < high)
                return true;
        }
        return false;
    }

    private static double Unit(ulong value) =>
        (value & 0x1FFFFFFFFFFFFFUL) / 9007199254740991.0;
    private static double Lerp(double minimum, double maximum, double unit) =>
        minimum + (maximum - minimum) * unit;
    private static ulong MixDouble(ulong hash, double value) =>
        Mix(hash, BitConverter.DoubleToInt64Bits(value));
    private static ulong Mix(ulong hash, int value) => Mix(hash, unchecked((ulong)(long)value));
    private static ulong Mix(ulong hash, long value) => Mix(hash, unchecked((ulong)value));

    private static ulong Mix(ulong hash, ulong value)
    {
        for (int index = 0; index < 8; index++)
        {
            hash ^= (byte)(value >> (index * 8));
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static ulong Avalanche(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58476d1ce4e5b9UL;
        value ^= value >> 27;
        value *= 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }
}
