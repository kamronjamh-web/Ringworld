using System;

namespace RingworldTechDemo.Celestial.Eclipse;

/// <summary>
/// Hub sun body and sky look settings (docs/eclipse-false-sun-spec.md E.1, phase E4c). Visual only, like <see cref="EclipseBeamSettings"/>:
/// <see cref="EclipseEventEngine.SetSettings"/> treats a change here as not touching the timeline. Radii are real kilometres at the hub
/// (the body is a main-viewport proxy that keeps the angular size). Call <see cref="Sanitized"/> before use.
/// </summary>
public sealed record EclipseHubBodySettings
{
    /// <summary>Master switch for the hub disc and its halo (the hub light, the sky and the beams are unaffected).</summary>
    public bool BodyEnabled { get; init; } = true;
    /// <summary>HDR emission of the disc core at hub level 1 (scaled by the level); 40 saturates to white under AgX at every level from the kindle up.</summary>
    public double CoreEmission { get; init; } = 40d;
    /// <summary>Core radius at full level in real km (spec E.1: 30 km, 0.34 degrees at the floor). Scaled by <see cref="EclipseHubBodyMath.RadiusFactor"/>.</summary>
    public double RadiusKm { get; init; } = 30d;
    /// <summary>The core is never drawn smaller than this many pixels in radius, so the kindle still reads as a point.</summary>
    public double MinRadiusPixels { get; init; } = 2d;
    /// <summary>Peak emission of the teal halo at hub level 1, centred on the disc.</summary>
    public double HaloAmplitude { get; init; } = 0.6d;
    /// <summary>Exponential e-fold radius of the halo in real km.</summary>
    public double HaloRadiusKm { get; init; } = 400d;
    /// <summary>Strength of the sky shader's hub in-scatter phase (blue, forward-lit air around the zenith); 0 leaves the sky exactly as before.</summary>
    public double SkyInScatter { get; init; } = 1d;

    public EclipseHubBodySettings Sanitized() => this with
    {
        CoreEmission = C(CoreEmission, 1d, 200d, 40d),
        RadiusKm = C(RadiusKm, 2d, 200d, 30d),
        MinRadiusPixels = C(MinRadiusPixels, 0.5d, 8d, 2d),
        HaloAmplitude = C(HaloAmplitude, 0d, 4d, 0.6d),
        HaloRadiusKm = C(HaloRadiusKm, 20d, 3000d, 400d),
        SkyInScatter = C(SkyInScatter, 0d, 4d, 1d)
    };

    private static double C(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;
}

/// <summary>Pure hub body profile (CPU twin of ring_eclipse_hub_body.gdshader's radius and emission terms).</summary>
public static class EclipseHubBodyMath
{
    /// <summary>The core radius grows with the level, from about 0.66 of full at the kindle to 1 at L = 1 (spec E.1: 0.5 + 0.5 sqrt(min(L, 1))).</summary>
    public static double RadiusFactor(double level) => 0.5d + 0.5d * Math.Sqrt(Math.Clamp(level, 0d, 1d));

    /// <summary>Core radius in real km at a hub level (before the pixel minimum).</summary>
    public static double CoreRadiusKm(EclipseHubBodySettings s, double level) => s.RadiusKm * RadiusFactor(level);

    /// <summary>Core HDR emission at a hub level.</summary>
    public static double CoreEmission(EclipseHubBodySettings s, double level) => s.CoreEmission * Math.Max(level, 0d);

    /// <summary>Halo peak emission at a hub level (the overload swell rides on this, spec B.3).</summary>
    public static double HaloEmission(EclipseHubBodySettings s, double level) => s.HaloAmplitude * Math.Max(level, 0d);
}
