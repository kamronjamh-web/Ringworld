using System;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Lightning;

/// <summary>Immutable, world-space lightning pulse shared by every weather renderer.</summary>
public readonly record struct RingLightningEvent(
    long EventId,
    int SystemId,
    double StartTimeSeconds,
    RingCanonicalPosition GroundPosition,
    RingCanonicalPosition CloudPosition,
    int ShapeSeed,
    float PlaneAngleRadians,
    float Energy,
    float FlashRadiusMeters,
    bool IsPreview)
{
    public const double LifetimeSeconds = 0.72;

    public static float PulseAtAge(double age)
    {
        if (age < 0.0 || age > LifetimeSeconds) return 0.0f;
        // A primary flash followed by two deterministic, renderer-independent restrikes.
        float primary = Gaussian(age, 0.035, 0.018) + Gaussian(age, 0.095, 0.035) * 0.38f;
        float restrikeA = Gaussian(age, 0.265, 0.016) * 0.62f;
        float restrikeB = Gaussian(age, 0.505, 0.022) * 0.35f;
        return Math.Clamp(primary + restrikeA + restrikeB, 0.0f, 1.0f);
    }

    private static float Gaussian(double age, double center, double width)
    {
        double x = (age - center) / width;
        return (float)Math.Exp(-0.5 * x * x);
    }
}
