using System;
using Godot;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tests;

/// <summary>Disposable-scene fixture adapter for the existing typed override API.</summary>
public partial class RainPrecipitationTestDriver : RefCounted
{
    public bool CheckProfileMath()
    {
        RainPrecipitationProfile previous = RainPrecipitationProfile.Resolve(0);
        for (int sample = 0; sample <= 10000; sample++)
        {
            float intensity = sample / 10000.0f;
            RainPrecipitationProfile p = RainPrecipitationProfile.Resolve(intensity);
            if (Math.Abs(p.ScatteredWeight + p.SteadyWeight + p.HeavyWeight - 1) > .000001f
                || p.ScatteredWeight < 0 || p.SteadyWeight < 0 || p.HeavyWeight < 0
                || Math.Abs(p.ScatteredWeight - previous.ScatteredWeight) > .001f
                || Math.Abs(p.SteadyWeight - previous.SteadyWeight) > .001f
                || Math.Abs(p.HeavyWeight - previous.HeavyWeight) > .001f)
                throw new InvalidOperationException($"Invalid rain blend at {intensity}: {p}");
            previous = p;
        }
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1.0f })
            if (RainPrecipitationProfile.Resolve(invalid) != RainPrecipitationProfile.Resolve(0))
                throw new InvalidOperationException("Invalid intensity did not resolve to the dry profile.");
        if (RainPrecipitationProfile.Resolve(2) != RainPrecipitationProfile.Resolve(1))
            throw new InvalidOperationException("Rain profile exceeded its upper intensity bound.");
        foreach (var (intensity, name) in new[] { (0.0f, "Clear"), (.15f, "Scattered"), (.5f, "Steady"), (1.0f, "Heavy") })
        {
            RainPrecipitationProfile p = RainPrecipitationProfile.Resolve(intensity);
            if (p.Stage(intensity, false) != name || p.Stage(intensity, true) != "Snow")
                throw new InvalidOperationException("Unexpected rain stage label.");
        }
        GD.Print("RAIN_PRECIPITATION_PROFILE_MATH=PASS samples=10001");
        return true;
    }

    public void SetIntensity(Node effects, float intensity, bool snow, Vector2 wind)
    {
        if (effects is not RingLocalWeatherEffects local)
            throw new ArgumentException("Expected the production local weather effects node.");
        float target = float.IsFinite(intensity) ? Mathf.Clamp(intensity, 0, 1) : 0;
        // Invert the publisher's existing smoothstep, preserving its normal
        // threshold and rise/fall smoothing rather than assigning private state.
        float lower = 0, upper = 1;
        for (int iteration = 0; iteration < 24; iteration++)
        {
            float middle = (lower + upper) * .5f;
            if (middle * middle * (3 - 2 * middle) < target) lower = middle;
            else upper = middle;
        }
        float raw = local.PrecipitationStart + (1 - local.PrecipitationStart) * ((lower + upper) * .5f);
        if (target == 0) raw = 0;
        if (target == 1) raw = 1;
        local.SnowGuaranteedAboveAltitudeMeters = 1_000_000;
        local.SnowTemperatureThreshold = snow ? 100 : -100;
        local.SetExternalWeatherOverride(true, new RingVolumetricCloudLayer.LocalWeatherState(
            -9511, RingVolumetricCloudLayer.WeatherSystemKind.MajorFront, 1,
            10_000, Vector2.Zero, new Vector2(10_000, 10_000), 0,
            1, raw, wind, CloudFormationProfile.Cumulonimbus, 2000, 6000));
    }
}
