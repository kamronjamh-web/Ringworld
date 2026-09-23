using System;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Lightning;

/// <summary>Pure canonical-distance ownership weights shared by local and far lightning renderers.</summary>
public readonly record struct LightningDistanceHandoff(float Local, float Regional, float Shell)
{
    public static LightningDistanceHandoff Evaluate(
        in RingCanonicalPosition observer,
        in RingCanonicalPosition eventPosition,
        double circumferenceMeters,
        float regionalOuterMeters,
        float regionalAcrossHalfMeters,
        int regionalLayerCount)
    {
        double around = ShortestAroundDelta(observer.AroundMeters, eventPosition.AroundMeters, circumferenceMeters);
        double across = observer.AcrossMeters - eventPosition.AcrossMeters;
        double distance = Math.Sqrt(around * around + across * across);
        float farIn = SmoothStep(48_000.0f, 72_000.0f, (float)distance);
        // When the regional clipmap is intentionally disabled, the shell must
        // take all post-local ownership immediately; waiting for its normal
        // outer fade would make a 48 km-to-outer-radius lightning hole.
        if (regionalLayerCount <= 0)
            return new LightningDistanceHandoff(1.0f - farIn, 0.0f, farIn);
        float outer = Math.Max(1.0f, regionalOuterMeters);
        float acrossHalf = Math.Max(1.0f, regionalAcrossHalfMeters);
        float ellipseRadius = MathF.Sqrt(
            (float)(around * around) / (outer * outer)
            + (float)(across * across) / (acrossHalf * acrossHalf));
        float shellIn = SmoothStep(0.72f, 1.0f, ellipseRadius);
        float local = 1.0f - farIn;
        // The regional layers occupy distinct cloud heights but can overlap in
        // projection. Divide their visual lightning contribution so their
        // combined weight remains complementary with the shell.
        float regional = farIn * (1.0f - shellIn) / Math.Max(1, regionalLayerCount);
        return new LightningDistanceHandoff(local, regional, farIn * shellIn);
    }

    public static double CanonicalPlanarDistance(
        in RingCanonicalPosition first,
        in RingCanonicalPosition second,
        double circumferenceMeters)
    {
        double around = ShortestAroundDelta(first.AroundMeters, second.AroundMeters, circumferenceMeters);
        double across = first.AcrossMeters - second.AcrossMeters;
        return Math.Sqrt(around * around + across * across);
    }

    private static double ShortestAroundDelta(double first, double second, double circumferenceMeters)
    {
        double circumference = Math.Max(1.0, circumferenceMeters);
        double around = (first - second) % circumference;
        if (around > circumference * 0.5) around -= circumference;
        if (around < -circumference * 0.5) around += circumference;
        return around;
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / Math.Max(0.001f, edge1 - edge0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }
}
