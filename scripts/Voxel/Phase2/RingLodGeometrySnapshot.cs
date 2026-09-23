using System;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Phase2;

/// <summary>Thread-safe value snapshot of canonical ring-to-world geometry.</summary>
public readonly record struct RingLodGeometrySnapshot(
    Transform3D GridGlobalTransform,
    Vector3 FloatingOriginWorldOffset,
    Vector3 RingCenterLocalOffset,
    double RingRadiusMeters,
    double RingAcrossBaseOffsetMeters,
    double RingVerticalBaseOffsetMeters,
    double CircumferenceMeters,
    double StructuralBottomGlobalY,
    double StructuralWallTopGlobalY,
    RingCanonicalPosition? FloatingOriginCanonicalAnchor)
{
    public Vector3 CanonicalToWorld(Vector3 canonical)
    {
        return CanonicalToWorld(canonical.X, canonical.Y, canonical.Z);
    }

    public Vector3 CanonicalToWorld(double aroundMeters, double verticalMeters, double acrossMeters)
    {
        Vector3 relativeLocal = CanonicalToRelativeLocal(
            aroundMeters,
            verticalMeters,
            acrossMeters);
        Basis basis = GridGlobalTransform.Basis;
        Vector3 origin = GridGlobalTransform.Origin - FloatingOriginWorldOffset;
        return basis * relativeLocal + origin;
    }

    public Vector3 CanonicalToRelativeLocal(double aroundMeters, double verticalMeters, double acrossMeters)
    {
        (double localX, double localY, double localZ) = CanonicalToAbsoluteLocal(
            aroundMeters,
            verticalMeters,
            acrossMeters);
        if (FloatingOriginCanonicalAnchor is { } anchor)
        {
            (double anchorX, double anchorY, double anchorZ) = CanonicalToAbsoluteLocal(
                anchor.AroundMeters,
                anchor.VerticalMeters,
                anchor.AcrossMeters);
            localX -= anchorX;
            localY -= anchorY;
            localZ -= anchorZ;
        }

        return new Vector3((float)localX, (float)localY, (float)localZ);
    }

    public (double X, double Y, double Z) CanonicalToAbsoluteLocal(
        double aroundMeters,
        double verticalMeters,
        double acrossMeters)
    {
        double around = aroundMeters % CircumferenceMeters;
        if (around < 0.0) around += CircumferenceMeters;
        double angle = around / RingRadiusMeters;
        double radialDistance = RingRadiusMeters - (RingVerticalBaseOffsetMeters + verticalMeters);
        double localX = RingCenterLocalOffset.X + Math.Cos(angle) * radialDistance;
        double localY = RingCenterLocalOffset.Y + RingAcrossBaseOffsetMeters + acrossMeters;
        double localZ = RingCenterLocalOffset.Z + Math.Sin(angle) * radialDistance;
        return (localX, localY, localZ);
    }

    /// <summary>
    /// Produces a snapshot whose floating origin is a sector center.  Mesh
    /// builders can therefore subtract the large ring coordinates in double
    /// precision before they are stored in a Godot Vector3.
    /// </summary>
    public RingLodGeometrySnapshot CenteredAt(double aroundMeters, double acrossMeters)
    {
        return new RingLodGeometrySnapshot(
            new Transform3D(GridGlobalTransform.Basis, Vector3.Zero),
            Vector3.Zero,
            RingCenterLocalOffset,
            RingRadiusMeters,
            RingAcrossBaseOffsetMeters,
            RingVerticalBaseOffsetMeters,
            CircumferenceMeters,
            StructuralBottomGlobalY,
            StructuralWallTopGlobalY,
            new RingCanonicalPosition(aroundMeters, 0.0, acrossMeters));
    }

    public static bool RunFloatingOriginSelfTest()
    {
        if (!MegaRingScale.Validate(out _) || !CertifiedRingScale.Validate(out _))
        {
            return false;
        }

        return HasPrecisionSafeAnchor(
                MegaRingScale.AroundVoxelLength / 3.0,
                120.0,
                MegaRingScale.RingWidthMeters * 0.5,
                MegaRingScale.EffectiveRadiusMeters,
                MegaRingScale.EffectiveCircumferenceMeters)
            && HasPrecisionSafeAnchor(
                CertifiedRingScale.AroundVoxelLength / 3.0,
                120.0,
                CertifiedRingScale.RingWidthMeters * 0.5,
                CertifiedRingScale.EffectiveRadiusMeters,
                CertifiedRingScale.EffectiveCircumferenceMeters)
            && RunSectorLocalSharedEdgeSelfTest();
    }

    /// <summary>
    /// Regression for sector-local proxy meshes: adjacent sectors must retain
    /// a common edge below one millimetre even after the canonical anchor moves.
    /// This models worker data built before the move and installed afterwards.
    /// </summary>
    public static bool RunSectorLocalSharedEdgeSelfTest()
    {
        double circumference = CertifiedRingScale.EffectiveCircumferenceMeters;
        double radius = CertifiedRingScale.EffectiveRadiusMeters;
        const int sectorSize = 128;
        double edgeAround = circumference - sectorSize;
        const double across = 108_327.0;
        var beforeMove = new RingLodGeometrySnapshot(
            Transform3D.Identity, Vector3.Zero, Vector3.Zero, radius, 0.0, 0.0,
            circumference, -1000.0, 1000.0,
            new RingCanonicalPosition(981_748.0, 5.0, across));
        var afterMove = beforeMove with
        {
            FloatingOriginCanonicalAnchor = new RingCanonicalPosition(
                edgeAround + sectorSize * 4.0, 5.0, across + 64.0)
        };

        // Neighbouring sectors near the circumference boundary are generated
        // from the pre-teleport snapshot and placed after the anchor wraps.
        RingLodGeometrySnapshot leftLocal = beforeMove.CenteredAt(edgeAround - sectorSize * 0.5, across);
        RingLodGeometrySnapshot rightLocal = beforeMove.CenteredAt(edgeAround + sectorSize * 0.5, across);
        Vector3 leftVertex = leftLocal.CanonicalToWorld(edgeAround, 0.0, across);
        Vector3 rightVertex = rightLocal.CanonicalToWorld(edgeAround, 0.0, across);
        Vector3 leftOrigin = afterMove.CanonicalToWorld(edgeAround - sectorSize * 0.5, 0.0, across);
        Vector3 rightOrigin = afterMove.CanonicalToWorld(edgeAround + sectorSize * 0.5, 0.0, across);
        return (leftOrigin + leftVertex).DistanceTo(rightOrigin + rightVertex) < 0.001f;
    }

    private static bool HasPrecisionSafeAnchor(
        double aroundMeters,
        double verticalMeters,
        double acrossMeters,
        double ringRadiusMeters,
        double circumferenceMeters)
    {
        var anchor = new RingCanonicalPosition(aroundMeters, verticalMeters, acrossMeters);
        var geometry = new RingLodGeometrySnapshot(
            Transform3D.Identity,
            Vector3.Zero,
            Vector3.Zero,
            ringRadiusMeters,
            0.0,
            0.0,
            circumferenceMeters,
            -1000.0,
            1000.0,
            anchor);
        Vector3 anchorWorld = geometry.CanonicalToWorld(anchor.AroundMeters, anchor.VerticalMeters, anchor.AcrossMeters);
        Vector3 nearbyWorld = geometry.CanonicalToWorld(
            anchor.AroundMeters + 128.0,
            anchor.VerticalMeters + 3.0,
            anchor.AcrossMeters + 11.0);
        double expectedDistance = Math.Sqrt(128.0 * 128.0 + 3.0 * 3.0 + 11.0 * 11.0);

        return anchorWorld.LengthSquared() <= 0.000001f
            && Math.Abs(nearbyWorld.Length() - expectedDistance) <= 0.01;
    }
}
