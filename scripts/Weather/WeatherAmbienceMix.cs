using System;

namespace RingworldTechDemo.Weather;

/// <summary>Pure, bounded target and smoothing math shared by weather ambience runtime/tests.</summary>
public static class WeatherAmbienceMix
{
    public static float Smooth(float current, float target, double delta, float seconds = 1.5f)
    {
        if (!float.IsFinite(current) || !float.IsFinite(target)) return 0.0f;
        current = Math.Clamp(current, 0.0f, 1.0f);
        target = Math.Clamp(target, 0.0f, 1.0f);
        if (!double.IsFinite(delta) || delta <= 0.0) return current;
        seconds = float.IsFinite(seconds) ? Math.Max(0.05f, seconds) : 1.5f;
        float step = (float)Math.Min(1.0, delta / seconds);
        float difference = target - current;
        if (Math.Abs(difference) <= step) return target;
        return Math.Clamp(current + MathF.CopySign(step, difference), 0.0f, 1.0f);
    }

    public static void Targets(float precipitation, bool snowing, float windMetersPerSecond, float distantStorm,
        out float rainLight, out float rainHeavy, out float wind, out float distant)
    {
        precipitation = Math.Clamp(float.IsFinite(precipitation) ? precipitation : 0, 0, 1);
        windMetersPerSecond = Math.Max(0, float.IsFinite(windMetersPerSecond) ? windMetersPerSecond : 0);
        distantStorm = Math.Clamp(float.IsFinite(distantStorm) ? distantStorm : 0, 0, 1);
        float heavyBlend = SmoothStep(.35f, .85f, precipitation);
        rainLight = snowing ? 0 : precipitation * (1 - heavyBlend);
        rainHeavy = snowing ? 0 : precipitation * heavyBlend;
        wind = Math.Clamp(windMetersPerSecond / 16f, 0, 1);
        distant = distantStorm * (1 - rainHeavy);
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
