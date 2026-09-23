using System;
using Godot;

namespace RingworldTechDemo.Weather;

public partial class WeatherAmbienceDiagnosticRunner : RefCounted
{
    public string RunAll()
    {
        try
        {
            CheckTargets();
            CheckFiniteSafety();
            CheckSmoothing();
            return "Weather ambience diagnostics: PASS";
        }
        catch (Exception exception)
        {
            return "Weather ambience diagnostics: FAIL: " + exception.Message;
        }
    }

    /// <summary>Fixture helper: publishes the production local-effects state through its public override API.</summary>
    public void ConfigureLocalWeather(Node localEffects, float rain, Vector2 wind, bool snow)
    {
        if (localEffects is not global::RingworldTechDemo.RingLocalWeatherEffects local)
            throw new ArgumentException("Expected RingLocalWeatherEffects.");
        local.SnowGuaranteedAboveAltitudeMeters = snow ? -10_000 : 1_000_000;
        local.SnowTemperatureThreshold = snow ? 100 : -100;
        var state = new global::RingworldTechDemo.RingVolumetricCloudLayer.LocalWeatherState(
            1, default, 1, 1, Vector2.Zero, Vector2.One, 0, 1, rain, wind, default, 2000, 6000);
        local.SetExternalWeatherOverride(true, state);
        for (int index = 0; index < 16; index++) local._Process(.25);
    }

    private static void CheckTargets()
    {
        for (int index = 0; index <= 20; index++)
        {
            float rain = index / 20f;
            WeatherAmbienceMix.Targets(rain, false, 0, 0, out float light, out float heavy, out _, out _);
            Require(Near(light + heavy, rain), "rain split");
        }
        WeatherAmbienceMix.Targets(.2f, false, 0, 0, out _, out float lowHeavy, out _, out _);
        WeatherAmbienceMix.Targets(1, false, 0, 0, out _, out float fullHeavy, out _, out _);
        Require(lowHeavy == 0 && Near(fullHeavy, 1), "heavy bounds");
        WeatherAmbienceMix.Targets(1, true, 0, 0, out float snowLight, out float snowHeavy, out _, out _);
        Require(snowLight == 0 && snowHeavy == 0, "snow silence");
        WeatherAmbienceMix.Targets(0, false, 0, 1, out _, out _, out float wind0, out float distant0);
        WeatherAmbienceMix.Targets(0, false, 8, 1, out _, out _, out float wind8, out _);
        WeatherAmbienceMix.Targets(0, false, 16, 1, out _, out _, out float wind16, out _);
        WeatherAmbienceMix.Targets(1, false, 0, 1, out _, out _, out _, out float distantHeavy);
        Require(wind0 == 0 && Near(wind8, .5f) && wind16 == 1 && distant0 == 1 && distantHeavy == 0, "wind distant");
    }

    private static void CheckFiniteSafety()
    {
        WeatherAmbienceMix.Targets(float.NaN, false, float.PositiveInfinity, float.NegativeInfinity, out float a, out float b, out float c, out float d);
        Require(a == 0 && b == 0 && c == 0 && d == 0, "finite targets");
        Require(WeatherAmbienceMix.Smooth(float.NaN, 1, 1) == 0, "invalid current");
        Require(WeatherAmbienceMix.Smooth(0, float.PositiveInfinity, 1) == 0, "invalid target");
        Require(WeatherAmbienceMix.Smooth(0, 1, -1, -1) == 0, "invalid delta seconds");
        foreach (double delta in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1.0 })
            Require(Near(WeatherAmbienceMix.Smooth(.3f, 1, delta), .3f), "invalid delta preserves current");
        foreach (float seconds in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1.0f, 0.0f })
        {
            float value = WeatherAmbienceMix.Smooth(.3f, 1, .1, seconds);
            Require(float.IsFinite(value) && value >= .3f && value <= 1, "invalid smoothing duration stays bounded");
        }
        WeatherAmbienceMix.Targets(-1, false, -8, -1, out a, out b, out c, out d);
        Require(a == 0 && b == 0 && c == 0 && d == 0, "negative targets");
    }

    private static void CheckSmoothing()
    {
        float once = WeatherAmbienceMix.Smooth(0, 1, .5, 1);
        float split = WeatherAmbienceMix.Smooth(WeatherAmbienceMix.Smooth(0, 1, .25, 1), 1, .25, 1);
        Require(Near(once, split) && once <= 1, "partition no overshoot");
        Require(WeatherAmbienceMix.Smooth(0, .2f, 100) == .2f, "rising target no overshoot");
        Require(WeatherAmbienceMix.Smooth(1, .2f, 100) == .2f, "falling target no overshoot");
    }

    private static bool Near(float first, float second) => Math.Abs(first - second) < .001f;
    private static void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
}
