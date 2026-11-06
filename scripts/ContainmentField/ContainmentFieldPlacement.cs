using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.ContainmentField;

/// <summary>A direction in canonical axes (around, vertical across), in double.</summary>
public readonly record struct CanonicalVector(double Around, double Vertical, double Across);

/// <summary>World placement helpers (patches, POIs, gizmos) for field points; docs/containment-field-design.md 1.4 and 1.6.</summary>
public static class ContainmentFieldPlacement
{
    /// <summary>Canonical tangent frame at a surface point: T_around, T_arch (increasing arch length) and N (into the contained volume).</summary>
    public static (CanonicalVector around, CanonicalVector arch, CanonicalVector normal) CanonicalFrame(in ContainmentFieldPoint pt)
        => (new CanonicalVector(1.0, 0.0, 0.0),
            new CanonicalVector(0.0, pt.NormalAcross, -pt.NormalVertical),
            new CanonicalVector(0.0, pt.NormalVertical, pt.NormalAcross));

    /// <summary>World directions of the canonical around and vertical (up) axes and the across axis at a canonical around.</summary>
    public static void AxesAt(in RingLodGeometrySnapshot s, double around, out Vector3 aroundDir, out Vector3 upDir, out Vector3 acrossDir)
    {
        double wrapped = around % s.CircumferenceMeters;
        if (wrapped < 0.0) wrapped += s.CircumferenceMeters;
        double angle = wrapped / s.RingRadiusMeters;
        float sin = (float)Math.Sin(angle), cos = (float)Math.Cos(angle);
        Basis basis = s.GridGlobalTransform.Basis;
        aroundDir = basis * new Vector3(-sin, 0f, cos);
        upDir = basis * new Vector3(-cos, 0f, -sin);   // vertical increases radially inward
        acrossDir = basis * new Vector3(0f, 1f, 0f);
    }

    /// <summary>
    /// World transform of a surface point (plus an optional offset along N), relative to the current floating origin. Basis: X = around tangent,
    /// Y = -N (out of the contained volume), Z = X x Y (the arch tangent reversed; the spec's Z = arch tangent would make the basis left-handed).
    /// </summary>
    public static Transform3D WorldTransform(in RingLodGeometrySnapshot s, in ContainmentFieldPoint pt, double normalOffsetMeters = 0.0)
    {
        AxesAt(s, pt.AroundMeters, out Vector3 aroundDir, out Vector3 upDir, out Vector3 acrossDir);
        Vector3 n = upDir * (float)pt.NormalVertical + acrossDir * (float)pt.NormalAcross;
        Vector3 y = -n;
        Vector3 z = aroundDir.Cross(y);
        Vector3 origin = s.CanonicalToWorld(pt.AroundMeters, pt.VerticalMeters + pt.NormalVertical * normalOffsetMeters,
            pt.AcrossMeters + pt.NormalAcross * normalOffsetMeters);
        return new Transform3D(new Basis(aroundDir, y, z), origin);
    }

    /// <summary>
    /// Anchor snapshot for meshes built around a field point: like CenteredAt, but anchored at the point's own vertical (CenteredAt pins vertical 0,
    /// which would put 10-40 km into every vertex). Mesh vertices are CanonicalToWorld of this snapshot; the instance origin is the point's world position.
    /// </summary>
    public static RingLodGeometrySnapshot AnchoredAt(in RingLodGeometrySnapshot s, in ContainmentFieldPoint pt)
        => new(new Transform3D(s.GridGlobalTransform.Basis, Vector3.Zero), Vector3.Zero, s.RingCenterLocalOffset, s.RingRadiusMeters,
            s.RingAcrossBaseOffsetMeters, s.RingVerticalBaseOffsetMeters, s.CircumferenceMeters, s.StructuralBottomGlobalY, s.StructuralWallTopGlobalY,
            new RingCanonicalPosition(pt.AroundMeters, pt.VerticalMeters, pt.AcrossMeters));

    /// <summary>Rim root line point: where the field leaves the wall top. side 0 = across-0 wall, side 1 = across-W wall.</summary>
    public static ContainmentFieldPoint RimRoot(in ContainmentFieldProfile p, int side, double around)
        => ContainmentFieldGeometry.SurfaceAtArchLength(p, around, side == 0 ? 0.0 : ContainmentFieldGeometry.ArchLength(p));

    /// <summary>Inverse of <see cref="RingLodGeometrySnapshot.CanonicalToWorld(double, double, double)"/> (around wrapped to [0, C)).</summary>
    public static RingCanonicalPosition WorldToCanonical(in RingLodGeometrySnapshot s, Vector3 world)
    {
        Vector3 origin = s.GridGlobalTransform.Origin - s.FloatingOriginWorldOffset;
        Vector3 rel = s.GridGlobalTransform.Basis.Inverse() * (world - origin);
        double ax = rel.X, ay = rel.Y, az = rel.Z;
        if (s.FloatingOriginCanonicalAnchor is { } anchor)
        {
            (double x, double y, double z) = s.CanonicalToAbsoluteLocal(anchor.AroundMeters, anchor.VerticalMeters, anchor.AcrossMeters);
            ax += x; ay += y; az += z;
        }
        double lx = ax - s.RingCenterLocalOffset.X, lz = az - s.RingCenterLocalOffset.Z;
        double radial = Math.Sqrt(lx * lx + lz * lz);
        double around = Math.Atan2(lz, lx) * s.RingRadiusMeters;
        around %= s.CircumferenceMeters;
        if (around < 0.0) around += s.CircumferenceMeters;
        return new RingCanonicalPosition(around, s.RingRadiusMeters - radial - s.RingVerticalBaseOffsetMeters,
            ay - s.RingCenterLocalOffset.Y - s.RingAcrossBaseOffsetMeters);
    }
}
