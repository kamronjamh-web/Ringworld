using System;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.ContainmentField;

// Pure C# canonical-metre profile of the atmospheric containment field (docs/containment-field-design.md 1.1-1.5, plus the 2026-11-05 vertical skirt).
// Nothing here touches the scene tree.

/// <summary>Where the field leaves the wall top, measured across the wall depth. With DepthMeters = 0 all three are identical.</summary>
public enum RimFieldRoot { InnerEdge, CentreLine, OuterEdge }

/// <summary>
/// The single description of the rim wall cross-section that every consumer (field, POI anchors, later the wall meshes) reads.
/// Decision 2/3 (2026-11-04): 128 m deep, field rooted on the centre line (the projector's shield plane).
/// </summary>
public readonly record struct RimWallDescription(double DepthMeters, RimFieldRoot Root)
{
    public static readonly RimWallDescription Current = new(128.0, RimFieldRoot.CentreLine);

    /// <summary>Across of the wall top's inner edge for side 0 (across 0) or 1 (across W).</summary>
    public double InnerEdge(int side, double w) => side == 0 ? 0.0 : w;
    public double OuterEdge(int side, double w) => side == 0 ? -DepthMeters : w + DepthMeters;
    public double CentreLine(int side, double w) => side == 0 ? -DepthMeters * 0.5 : w + DepthMeters * 0.5;

    /// <summary>Field root across for a side according to <see cref="Root"/>.</summary>
    public double RootAcross(int side, double w) => Root switch
    {
        RimFieldRoot.InnerEdge => InnerEdge(side, w),
        RimFieldRoot.CentreLine => CentreLine(side, w),
        _ => OuterEdge(side, w),
    };
}

/// <summary>
/// Profile in canonical metres (vertical 0 = sea level, across 0..W between the inner wall faces). The curve runs from the across-0 root: a vertical
/// skirt from the wall top up to the projector crown (<see cref="SkirtHeightMeters"/> above it), the upper semi-ellipse from the crown to the apex at
/// the centre, then the mirrored skirt down to the across-W root. Built from the live snapshot, never hard-coded.
/// </summary>
public readonly record struct ContainmentFieldProfile(
    double WidthMeters,            // habitat width W (inner face to inner face)
    double WallTopMeters,
    double ApexMeters,
    double RootAcross0,
    double RootAcrossW,
    double SkirtHeightMeters = 1_600.0)
{
    public const double DefaultApexMeters = 40_000.0;
    public const double DefaultSkirtHeightMeters = 1_600.0;

    /// <summary>Ellipse semi-axis along across.</summary>
    public double HalfWidth => (RootAcrossW - RootAcross0) * 0.5;
    public double Centre => (RootAcross0 + RootAcrossW) * 0.5;
    /// <summary>Vertical of the skirt top (projector crown) and ellipse base.</summary>
    public double CrownMeters => WallTopMeters + SkirtHeightMeters;
    /// <summary>Ellipse semi-axis along vertical: apex above the crown.</summary>
    public double Rise => ApexMeters - CrownMeters;
    public bool IsValid => WidthMeters > 1000 && RootAcrossW - RootAcross0 >= WidthMeters && SkirtHeightMeters >= 0 && Rise > 100
        && double.IsFinite(ApexMeters) && double.IsFinite(WallTopMeters);

    public static ContainmentFieldProfile FromSnapshot(in RingLodGeometrySnapshot s, double widthMeters, in RimWallDescription wall,
        double apexMeters = DefaultApexMeters, double skirtHeightMeters = DefaultSkirtHeightMeters)
        => new(widthMeters, s.StructuralWallTopGlobalY, apexMeters, wall.RootAcross(0, widthMeters), wall.RootAcross(1, widthMeters), skirtHeightMeters);
}

/// <summary>A point on the field curve. Phi is 0 / pi on the left / right skirt, the ellipse parameter elsewhere (across = c - a cos phi).</summary>
public readonly record struct ContainmentFieldPoint(
    double AroundMeters, double Phi, double AcrossMeters, double VerticalMeters,
    double NormalVertical, double NormalAcross,   // unit, points INTO the contained volume (down/inward)
    double ArchLengthMeters);                     // from the across-0 wall top along the curve, 0..ArchLength
