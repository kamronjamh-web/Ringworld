using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Canonical extents for one persistent grass tile.</summary>
internal readonly record struct RingGrassTileBounds(
    double AroundMin, double AroundMax, double AcrossMin, double AcrossMax)
{
    public double AroundCenter => (AroundMin + AroundMax) * .5;
    public double AcrossCenter => (AcrossMin + AcrossMax) * .5;
}

/// <summary>
/// Converts canonical grass tile and blade positions to local render frames.
/// The tile coordinate convention is X=across, Y=inward/up, Z=around.
/// </summary>
internal static class RingGrassCoordinates
{
    public static RingGrassTileBounds Bounds(long tileAround, long tileAcross, double tileSize, long circumference, int width)
    {
        double safeSize = Math.Max(1.0, tileSize);
        long aroundCount = AroundTileCount(circumference, safeSize);
        long wrappedAround = WrapTileIndex(tileAround, aroundCount);
        double aroundMin = Math.Min(circumference, wrappedAround * safeSize);
        double aroundMax = Math.Min(circumference, aroundMin + safeSize);
        double acrossMin = Math.Clamp(tileAcross * safeSize, 0.0, (double)width);
        double acrossMax = Math.Clamp(acrossMin + safeSize, 0.0, (double)width);
        return new RingGrassTileBounds(aroundMin, aroundMax, acrossMin, acrossMax);
    }

    public static long AroundTileCount(long circumference, double tileSize) =>
        Math.Max(1L, (long)Math.Ceiling(Math.Max(1L, circumference) / Math.Max(1.0, tileSize)));

    public static long WrapTileIndex(long index, long count)
    {
        if (count <= 1) return 0;
        long wrapped = index % count;
        return wrapped < 0 ? wrapped + count : wrapped;
    }

    public static double Canonicalize(double around, long circumference)
    {
        if (circumference <= 0) return 0.0;
        double canonical = around % circumference;
        return canonical < 0.0 ? canonical + circumference : canonical;
    }

    public static double TileDistanceSquared(in RingGrassTileBounds bounds, in RingCanonicalPosition focus, long circumference)
    {
        double aroundDelta = Math.Abs(ShortestWrapped(focus.AroundMeters - bounds.AroundCenter, circumference));
        double acrossDelta = Math.Abs(focus.AcrossMeters - bounds.AcrossCenter);
        double aroundOutside = Math.Max(0.0, aroundDelta - (bounds.AroundMax - bounds.AroundMin) * .5);
        double acrossOutside = Math.Max(0.0, acrossDelta - (bounds.AcrossMax - bounds.AcrossMin) * .5);
        return aroundOutside * aroundOutside + acrossOutside * acrossOutside;
    }

    public static Transform3D CreateTileWorldTransform(in RingGrassTileBounds bounds, double anchorHeight, in RingLodGeometrySnapshot geometry)
    {
        double anchorAround = bounds.AroundCenter;
        double angle = anchorAround / geometry.RingRadiusMeters;
        Basis gridBasis = geometry.GridGlobalTransform.Basis;
        Vector3 across = gridBasis * Vector3.Up;
        Vector3 up = gridBasis * new Vector3((float)-Math.Cos(angle), 0.0f, (float)-Math.Sin(angle));
        Vector3 around = gridBasis * new Vector3((float)-Math.Sin(angle), 0.0f, (float)Math.Cos(angle));
        Vector3 origin = geometry.CanonicalToWorld(anchorAround, anchorHeight, bounds.AcrossCenter);
        return new Transform3D(new Basis(across, up, around), origin);
    }

    public static Transform3D CreateBladeLocalTransform(
        in RingGrassTileBounds bounds, double anchorHeight, double around, double surfaceHeight, double across,
        float width, float bladeHeight, float yaw, in RingLodGeometrySnapshot geometry,
        Vector3? canonicalNormal = null)
    {
        double anchorAround = bounds.AroundCenter;
        double delta = ShortestWrapped(around - anchorAround, geometry.CircumferenceMeters);
        double deltaAngle = delta / geometry.RingRadiusMeters;
        double radius = geometry.RingRadiusMeters - (geometry.RingVerticalBaseOffsetMeters + surfaceHeight);
        Vector3 origin = new(
            (float)(across - bounds.AcrossCenter),
            (float)(surfaceHeight - anchorHeight + radius * 2.0 * Math.Sin(deltaAngle * .5) * Math.Sin(deltaAngle * .5)),
            (float)(radius * Math.Sin(deltaAngle)));
        Basis ringRelative = new(
            Vector3.Right,
            new Vector3(0.0f, (float)Math.Cos(deltaAngle), (float)-Math.Sin(deltaAngle)),
            new Vector3(0.0f, (float)Math.Sin(deltaAngle), (float)Math.Cos(deltaAngle)));
        Basis surfaceAlignment = Basis.Identity;
        if (canonicalNormal is Vector3 normal && normal.LengthSquared() > .0001f)
        {
            // Canonical axes are around/up/across while this mesh's local axes
            // are across/up/around.  Keep the shared wind shader's local-up
            // convention by rotating the blade's Y axis onto the surface.
            double arcScale = (geometry.RingRadiusMeters - geometry.RingVerticalBaseOffsetMeters - surfaceHeight)
                / geometry.RingRadiusMeters;
            Vector3 localNormal = new(normal.Z, normal.Y, normal.X / (float)Math.Max(.0001, arcScale));
            Vector3 axis = Vector3.Up.Cross(localNormal);
            if (axis.LengthSquared() > .0001f)
                surfaceAlignment = Basis.Identity.Rotated(axis.Normalized(), Vector3.Up.AngleTo(localNormal.Normalized()));
        }
        Basis yawAndScale = Basis.FromEuler(new Vector3(0.0f, yaw, 0.0f))
            .Scaled(new Vector3(width, bladeHeight, width));
        return new Transform3D(ringRelative * surfaceAlignment * yawAndScale, origin);
    }

    private static double ShortestWrapped(double delta, double circumference)
    {
        if (circumference <= 0.0) return delta;
        double wrapped = delta % circumference;
        if (wrapped > circumference * .5) wrapped -= circumference;
        if (wrapped < -circumference * .5) wrapped += circumference;
        return wrapped;
    }
}
