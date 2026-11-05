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
    bool Scythe = false /* megastorm lightning scythe: this is the attachment strike (docs/megastorm-design.md 5.5.4) */,
    bool FieldStrike = false /* upward discharge that ends on the containment field: GroundPosition = contact point, CloudPosition = channel base (docs/megastorm-design.md 5.5.5) */,
    float DurationSeconds = 0.0f /* per-event life; 0 = LifetimeSeconds. Field strikes live 0.8-1.9 s */)
{
    public const double LifetimeSeconds = 0.72;
    /// <summary>Field-strike lifetime bounds: leader rise 0.05-0.45 s, attached 0.4-1.0 s, fade 0.4 s.</summary>
    public const double FieldStrikeMinSeconds = 0.85, FieldStrikeMaxSeconds = 1.85, FieldStrikeFadeSeconds = 0.4;

    /// <summary>How long the event stays active (its own duration, else the 0.72 s default). Active events are pruned by this.</summary>
    public double LifeSeconds => DurationSeconds > 0.0f ? DurationSeconds : LifetimeSeconds;

    /// <summary>A field-strike event that is a near-rim contact arc (50-500 m, 0.2-0.5 s) rather than a full channel.</summary>
    public bool IsFieldArc => FieldStrike && DurationSeconds > 0.0f && DurationSeconds <= 0.51f;

    /// <summary>Re-strikes of a field strike (1-3), a pure function of the shape seed.</summary>
    public static int FieldStrikeRepulses(int shapeSeed) => 1 + (int)((uint)shapeSeed * 2654435761u >> 28) % 3;

    /// <summary>
    /// The event's brightness 0..1 at <paramref name="now"/>: <see cref="PulseAtAge"/> for ordinary events, the field-strike envelope (rise, re-pulses, fade) for
    /// flagged ones. The single source for every consumer that supports per-event durations.
    /// </summary>
    public float PulseAt(double now)
    {
        double age = now - StartTimeSeconds;
        return FieldStrike ? FieldStrikePulseAtAge(age, LifeSeconds, FieldStrikeRepulses(ShapeSeed)) : PulseAtAge(age);
    }

    /// <summary>
    /// Field-strike envelope over <paramref name="duration"/> seconds: a dim climbing leader glow for the rise (0.05-0.45 s from the duration), then
    /// <paramref name="repulses"/> return strokes (1.0, 0.65, 0.5) spread over the attached phase with a continuing-current tail, then a smooth fade over
    /// the last 0.4 s. Reduced flashes: one slow swell at the reduced peak (steepest slope about 3 per second, 0.05 per frame at 60 fps).
    /// </summary>
    public static float FieldStrikePulseAtAge(double age, double duration, int repulses)
    {
        if (age < 0.0 || age > duration || !(duration > 0.0)) return 0.0f;
        double fade = Math.Min(FieldStrikeFadeSeconds, 0.47 * duration);
        double rise = FieldStrikeRiseSeconds(duration);
        double attach = duration - rise - fade;
        if (LightningVisualPolicy.ReducedFlashes)
        {
            double centre = rise + 0.45 * attach, sigma = Math.Max(0.1, 0.3 * attach);
            double envelope = Math.Exp(-0.5 * ((age - centre) / sigma) * ((age - centre) / sigma));
            double edge = Math.Min(Smooth01(age / Math.Max(rise, 0.3)), 1.0 - Smooth01((age - (duration - fade)) / fade));   // eases in over at least 0.3 s: no frame step above 0.05
            return (float)Math.Clamp(envelope * Math.Max(edge, 0.0), 0.0, 1.0) * LightningVisualPolicy.ReducedPeakScale;
        }
        double value = age < rise ? 0.12 + 0.18 * (age / Math.Max(rise, 1.0e-3)) : 0.0;
        int n = Math.Clamp(repulses, 1, 3);
        double span = Math.Max(attach - 0.1, 0.05);
        for (int k = 0; k < n; k++)
        {
            double centre = rise + 0.04 + (n == 1 ? 0.0 : span * k / (n - 1)), weight = k == 0 ? 1.0 : k == 1 ? 0.65 : 0.5;
            double dt = age - centre;
            double stroke = Math.Exp(-0.5 * (dt / 0.022) * (dt / 0.022)) * weight;
            if (dt >= 0.0) stroke = Math.Max(stroke, weight * 0.4 * Math.Exp(-dt / 0.12));   // continuing current
            value = Math.Max(value, stroke);
        }
        if (age >= rise) value = Math.Max(value, 0.22 * Smooth01((age - rise) / 0.05));   // the attached channel keeps a dim steady glow between strokes
        if (age > duration - fade) value *= 1.0 - Smooth01((age - (duration - fade)) / fade);
        return (float)Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>Leader rise (0.05-0.45 s, from the duration): the channel grows from the dome top to the field at about 50 km/s over this time.</summary>
    public static double FieldStrikeRiseSeconds(double duration)
    {
        double t01 = Math.Clamp((duration - FieldStrikeMinSeconds) / (FieldStrikeMaxSeconds - FieldStrikeMinSeconds), 0.0, 1.0);
        return Math.Min(0.05 + 0.4 * t01, 0.3 * duration);
    }

    /// <summary>Index 0..2 of the return stroke most recently fired (0 before the first), so branch flicker can re-roll per stroke.</summary>
    public int FieldStrokeIndex(double now)
    {
        if (!FieldStrike) return 0;
        double duration = LifeSeconds, age = now - StartTimeSeconds, rise = FieldStrikeRiseSeconds(duration);
        double attach = duration - rise - Math.Min(FieldStrikeFadeSeconds, 0.47 * duration), span = Math.Max(attach - 0.1, 0.05);
        int n = Math.Clamp(FieldStrikeRepulses(ShapeSeed), 1, 3), index = 0;
        for (int k = 0; k < n; k++) if (age >= rise + 0.04 + (n == 1 ? 0.0 : span * k / (n - 1))) index = k;
        return index;
    }

    /// <summary>Leader progress 0..1 (1 once attached) at <paramref name="now"/>, for the growing channel.</summary>
    public float FieldLeaderProgress(double now) => FieldStrike ? (float)Math.Clamp((now - StartTimeSeconds) / Math.Max(FieldStrikeRiseSeconds(LifeSeconds), 1.0e-3), 0.0, 1.0) : 1.0f;

    private static double Smooth01(double x) { x = Math.Clamp(x, 0.0, 1.0); return x * x * (3.0 - 2.0 * x); }

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
