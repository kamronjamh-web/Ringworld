using System;
using Godot;

namespace RingworldTechDemo.Weather;

/// <summary>Pure, finite rain-stage interpolation; GPU consumers sanitize authored references separately.</summary>
public readonly record struct RainPrecipitationProfile(float ScatteredWeight, float SteadyWeight, float HeavyWeight)
{
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
}
