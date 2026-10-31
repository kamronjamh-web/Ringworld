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
    bool IsPreview,
    int SystemKind = 3,
    bool Scythe = false /* megastorm lightning scythe: this is the attachment strike (docs/megastorm-design.md 5.5.4) */)
{
    public const double LifetimeSeconds = 0.72;

    public static float PulseAtAge(double age)
    {
        if (age < 0.0 || age > LifetimeSeconds) return 0.0f;
        if (LightningVisualPolicy.ReducedFlashes) return ReducedPulseAtAge(age);
        // A primary flash followed by two deterministic, renderer-independent restrikes.
        float primary = Gaussian(age, 0.035, 0.018) + Gaussian(age, 0.095, 0.035) * 0.38f;
        float restrikeA = Gaussian(age, 0.265, 0.016) * 0.62f;
        float restrikeB = Gaussian(age, 0.505, 0.022) * 0.35f;
        return Math.Clamp(primary + restrikeA + restrikeB, 0.0f, 1.0f);
    }

    /// <summary>
    /// Reduced-flashes envelope: one slow swell plus a soft tail instead of the sharp primary and restrikes, at a fraction of the
    /// peak. Its steepest slope is about 2.7 per second (0.045 per frame at 60 fps), so no frame changes the pulse by more than 0.05.
    /// </summary>
    public static float ReducedPulseAtAge(double age)
    {
        if (age < 0.0 || age > LifetimeSeconds) return 0.0f;
        float swell = Gaussian(age, 0.25, 0.08) + Gaussian(age, 0.5, 0.09) * 0.4f;
        return Math.Clamp(swell, 0.0f, 1.0f) * LightningVisualPolicy.ReducedPeakScale;
    }

    private static float Gaussian(double age, double center, double width)
    {
        double x = (age - center) / width;
        return (float)Math.Exp(-0.5 * x * x);
    }
}
