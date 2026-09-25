using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Double-precision canonical ring-space operations shared by the coarse terrain
/// shadow mask and its focused geometry fixtures. Height is measured inward from
/// the ring's interior radius; across is the cylinder axis.
/// </summary>
public static class RingTerrainShadowGeometry
{
    public readonly record struct RingPoint(double X, double Y, double Z);
    public readonly record struct RingRaySample(double AroundMeters, double AcrossMeters, double HeightMeters);

    public static RingPoint ReceiverPosition(double radiusMeters, double aroundMeters, double acrossMeters, double heightMeters)
    {
        double angle = aroundMeters / radiusMeters;
        double radialDistance = radiusMeters - heightMeters;
        return new RingPoint(
            Math.Cos(angle) * radialDistance,
            acrossMeters,
            Math.Sin(angle) * radialDistance);
    }

    public static RingRaySample SampleStraightRay(double radiusMeters, RingPoint receiver, Vector3 rayDirection, double distanceMeters)
    {
        double x = receiver.X + rayDirection.X * distanceMeters;
        double y = receiver.Y + rayDirection.Y * distanceMeters;
        double z = receiver.Z + rayDirection.Z * distanceMeters;
        double radialDistance = Math.Sqrt(x * x + z * z);
        double angle = Math.Atan2(z, x);
        if (angle < 0.0) angle += Math.Tau;
        return new RingRaySample(
            angle * radiusMeters,
            y,
            radiusMeters - radialDistance);
    }

    /// <summary>Tests both finite structural walls at across 0 and ringWidth.</summary>
    public static bool RayHitsStructuralWall(
        double radiusMeters,
        RingPoint receiver,
        Vector3 rayDirection,
        double ringWidthMeters,
        double wallBottomMeters,
        double wallTopMeters,
        double maximumDistanceMeters,
        double selfIntersectionBiasMeters = 1.0)
    {
        if (Math.Abs(rayDirection.Y) <= 1e-8
            || maximumDistanceMeters <= 0.0
            || wallTopMeters < wallBottomMeters)
            return false;

        return HitsWallAtAcross(0.0) || HitsWallAtAcross(ringWidthMeters);

        bool HitsWallAtAcross(double wallAcross)
        {
            double distance = (wallAcross - receiver.Y) / rayDirection.Y;
            if (distance <= selfIntersectionBiasMeters || distance > maximumDistanceMeters)
                return false;

            RingRaySample hit = SampleStraightRay(radiusMeters, receiver, rayDirection, distance);
            return hit.HeightMeters >= wallBottomMeters && hit.HeightMeters <= wallTopMeters;
        }
    }
}
