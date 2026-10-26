using System;
using Godot;

namespace RingworldTechDemo.Weather;

/// <summary>Pure, finite rain-stage interpolation; GPU consumers sanitize authored references separately. Also the driver of the rain streak tiers (density, length, brightness, mid layer).</summary>
public readonly record struct RainPrecipitationProfile(float ScatteredWeight, float SteadyWeight, float HeavyWeight)
{
    // Streak tier table (scattered / steady / heavy), mixed by the stage weights. Density is the share of lattice drops that are drawn.
    public const float ScatteredDensity = 0.30f, SteadyDensity = 0.70f, HeavyDensity = 1.0f;
    public const float ScatteredLength = 0.8f, SteadyLength = 1.0f, HeavyLength = 1.3f;
    public const float ScatteredBrightness = 0.85f, SteadyBrightness = 1.1f, HeavyBrightness = 1.5f;
    public const float ScatteredMidLayer = 0.15f, SteadyMidLayer = 0.7f, HeavyMidLayer = 1.0f;

    public static RainPrecipitationProfile Resolve(float intensity)
    {
        float i = float.IsFinite(intensity) ? Mathf.Clamp(intensity, 0.0f, 1.0f) : 0.0f;
        float steadyRamp = Mathf.SmoothStep(.22f, .62f, i);
        float heavy = Mathf.SmoothStep(.60f, .92f, i);
        return new RainPrecipitationProfile((1.0f - steadyRamp) * (1.0f - heavy), steadyRamp * (1.0f - heavy), heavy);
    }
    public string Stage(float intensity, bool snow)
    {
        if (snow) return "Snow";
        if (!float.IsFinite(intensity) || intensity <= .01f) return "Clear";
        return HeavyWeight >= SteadyWeight && HeavyWeight >= ScatteredWeight ? "Heavy" : SteadyWeight >= ScatteredWeight ? "Steady" : "Scattered";
    }

    /// <summary>Share of lattice drops drawn at a local intensity (0 at dry, ramping in over the first 6 percent). The shader mirrors this exactly (ring_rain_streaks.glsl, rain_density).</summary>
    public static float StreakDensity(float intensity)
    {
        float i = float.IsFinite(intensity) ? Mathf.Clamp(intensity, 0.0f, 1.0f) : 0.0f;
        RainPrecipitationProfile p = Resolve(i);
        return Mathf.SmoothStep(0.0f, 0.06f, i) * (p.ScatteredWeight * ScatteredDensity + p.SteadyWeight * SteadyDensity + p.HeavyWeight * HeavyDensity);
    }
    public float LengthScale => ScatteredWeight * ScatteredLength + SteadyWeight * SteadyLength + HeavyWeight * HeavyLength;
    public float BrightnessScale => ScatteredWeight * ScatteredBrightness + SteadyWeight * SteadyBrightness + HeavyWeight * HeavyBrightness;
    /// <summary>Share of the mid (40 m) layer that is drawn; light rain is mostly the near layer, heavy rain adds the full mid layer.</summary>
    public float MidLayerWeight => ScatteredWeight * ScatteredMidLayer + SteadyWeight * SteadyMidLayer + HeavyWeight * HeavyMidLayer;

    // ---- storm arrival hooks (docs/storm-revamp-s3-storm-arrival-design.md WO-C1). All weights 0 reproduce the plain tiers exactly. The shader mirrors these (ring_rain_streaks.glslinc). ----
    /// <summary>Share of lattice drops that stay drawn as fat drops at FatDrop weight 1, even where the local intensity is below the start threshold.</summary>
    public const float FatDropFloorDensity = 0.10f;
    /// <summary>Fat drop length and width multipliers; their splashes are this much bigger.</summary>
    public const float FatDropLengthScale = 1.6f, FatDropWidthScale = 1.8f, FatDropSplashScale = 2.2f;
    /// <summary>Core intensity bias at Core x Severity 1, the Steady cap of the trailing rain and its length factor.</summary>
    public const float CoreIntensityBias = 0.15f, TrailingTierCap = 0.6f, TrailingLengthScale = 0.85f;
    /// <summary>Core x Severity at which the grey haze veil is fully open to the heavy-core rule (S1's "severe system / heavy core").</summary>
    public const float HeavyCoreSeverity = 0.7f;

    /// <summary>Local intensity after the arrival tier hooks: core bias (+0.15 x Core x Severity where it rains) then the trailing cap at Steady.</summary>
    public static float ArrivalTierIntensity(float intensity, float core, float severity, float trailing)
    {
        float i = float.IsFinite(intensity) ? Mathf.Clamp(intensity, 0.0f, 1.0f) : 0.0f;
        float bias = CoreIntensityBias * Mathf.Min(Mathf.Max(core * severity, 0.0f), 1.0f) * Mathf.SmoothStep(0.02f, 0.1f, i);
        return Mathf.Min(Mathf.Min(i + bias, 1.0f), Mathf.Lerp(1.0f, TrailingTierCap, Mathf.Clamp(trailing, 0.0f, 1.0f)));
    }
    public static float ArrivalLengthScale(float trailing) => Mathf.Lerp(1.0f, TrailingLengthScale, Mathf.Clamp(trailing, 0.0f, 1.0f));
    public static float FatDropFraction(float fatDrop) => FatDropFloorDensity * Mathf.Clamp(fatDrop, 0.0f, 1.0f);
    /// <summary>Weight 0..1 of the heavy-core veil rule: 1 at Core x Severity >= 0.7 (smooth from 0.6, so the veil never pops).</summary>
    public static float HeavyCoreWeight(float core, float severity) => Mathf.SmoothStep(HeavyCoreSeverity - 0.1f, HeavyCoreSeverity, core * severity);
}
