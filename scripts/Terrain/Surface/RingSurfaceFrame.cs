using System;
using Godot;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Precise local tangent frame for one bounded curved ring-surface window.</summary>
public readonly struct RingSurfaceFrame
{
    public RingSurfaceFrame(long anchorAroundMeters, int anchorAcrossMeters, float anchorHeight, long circumferenceMeters,
        double radiusMeters, float spacingMeters = RingSurfaceHeightField.DefaultSpacingMeters, int acrossDirection = 1)
    {
        if (circumferenceMeters <= 0 || radiusMeters <= 0 || !double.IsFinite(radiusMeters) ||
            !float.IsFinite(anchorHeight) || !float.IsFinite(spacingMeters) || spacingMeters <= 0 || spacingMeters != MathF.Round(spacingMeters))
            throw new ArgumentOutOfRangeException(nameof(radiusMeters));
        int latticeSpacing = checked((int)spacingMeters);
        if (circumferenceMeters % latticeSpacing != 0 || anchorAroundMeters % latticeSpacing != 0 || anchorAcrossMeters % latticeSpacing != 0)
            throw new ArgumentException("Frame anchors and circumference must align to the surface lattice.");
        if (acrossDirection is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(acrossDirection));
        AnchorAroundMeters = Wrap(anchorAroundMeters, circumferenceMeters);
        AnchorAcrossMeters = anchorAcrossMeters;
        AnchorHeight = anchorHeight;
        CircumferenceMeters = circumferenceMeters;
        RadiusMeters = radiusMeters;
        SpacingMeters = spacingMeters;
        AcrossDirection = acrossDirection;
    }

    public long AnchorAroundMeters { get; }
    public int AnchorAcrossMeters { get; }
    public float AnchorHeight { get; }
    public long CircumferenceMeters { get; }
    public double RadiusMeters { get; }
    public float SpacingMeters { get; }
    public int AcrossDirection { get; }
    public long SignedAroundDeltaMeters(long aroundMeters)
    {
        long delta = Wrap(aroundMeters, CircumferenceMeters) - AnchorAroundMeters;
        long half = CircumferenceMeters / 2;
        if (delta > half) delta -= CircumferenceMeters;
        if (delta < -half) delta += CircumferenceMeters;
        return delta;
    }

    public Vector3 MapSample(long aroundMeters, int acrossMeters, float absoluteHeight)
        => MapPosition(aroundMeters, acrossMeters, absoluteHeight);

    public Vector3 MapPosition(double aroundMeters, double acrossMeters, double absoluteHeight)
    {
        if (!double.IsFinite(aroundMeters) || !double.IsFinite(acrossMeters) || !double.IsFinite(absoluteHeight)) throw new ArgumentOutOfRangeException(nameof(aroundMeters));
        double dAround = SignedAroundDeltaMeters(aroundMeters);
        double theta = dAround / RadiusMeters;
        double radiusAtHeight = RadiusMeters - absoluteHeight;
        return new Vector3(
            (float)(radiusAtHeight * Math.Sin(theta)),
            (float)((absoluteHeight - AnchorHeight) + 2.0 * radiusAtHeight * Math.Pow(Math.Sin(theta * 0.5), 2.0)),
            (float)((acrossMeters - AnchorAcrossMeters) * AcrossDirection));
    }

    public RingSurfaceCoordinates Inverse(Vector3 localPosition)
    {
        double radialY = RadiusMeters - ((double)localPosition.Y + AnchorHeight);
        double theta = Math.Atan2(localPosition.X, radialY);
        double aroundOffsetMeters = theta * RadiusMeters;
        double aroundMeters = Wrap(AnchorAroundMeters + aroundOffsetMeters, CircumferenceMeters);
        double acrossMeters = (double)AnchorAcrossMeters + localPosition.Z * AcrossDirection;
        double height = RadiusMeters - Math.Sqrt((double)localPosition.X * localPosition.X + radialY * radialY);
        return new RingSurfaceCoordinates(aroundMeters, acrossMeters, height, aroundOffsetMeters);
    }

    public double SignedAroundDeltaMeters(double aroundMeters)
    {
        double delta = Wrap(aroundMeters, CircumferenceMeters) - AnchorAroundMeters;
        double half = CircumferenceMeters * .5;
        if (delta > half) delta -= CircumferenceMeters;
        if (delta < -half) delta += CircumferenceMeters;
        return delta;
    }

    public static long Wrap(long around, long circumferenceSamples)
    {
        long value = around % circumferenceSamples;
        return value < 0 ? value + circumferenceSamples : value;
    }
    public static double Wrap(double around, long circumferenceMeters)
    {
        double value = around % circumferenceMeters;
        return value < 0 ? value + circumferenceMeters : value;
    }
}

public readonly record struct RingSurfaceCoordinates(double AroundMeters, double AcrossMeters, double Height, double AroundOffsetMeters);
