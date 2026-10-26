using System;

namespace RingworldTechDemo.Weather;

/// <summary>
/// Lens wetness L 0..1 (docs/storm-revamp-s2-wet-world-design.md 3, lens droplets). Pure: gain while rain falls on an exposed camera (much more when looking up),
/// an emergence burst when the camera leaves cover into rain, exponential decay (tau 5 s), zero underwater or in snow.
/// </summary>
public sealed class LensWetnessModel
{
    public const float DecayTauSeconds = 5.0f;
    public const float EmergeRain = 0.2f;
    public const float ExposedThreshold = 0.5f;

    private bool _wasExposed = true;
    public float Wetness { get; private set; }

    public static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    /// <summary>Gain per second for rain at the camera, its exposure (1 = open sky) and how far the camera looks up (dot of view and up, -1..1).</summary>
    public static float GainPerSecond(float rainAtCamera, float exposure, float lookUp)
        => Math.Clamp(rainAtCamera, 0.0f, 1.0f) * Math.Clamp(exposure, 0.0f, 1.0f) * (0.04f + 0.5f * SmoothStep(0.1f, 0.7f, lookUp));

    public void Reset(float wetness = 0.0f) { Wetness = Math.Clamp(wetness, 0.0f, 1.0f); _wasExposed = true; }

    public float Step(float deltaSeconds, float rainAtCamera, float exposure, float lookUp, bool underwaterOrSnow)
    {
        deltaSeconds = float.IsFinite(deltaSeconds) ? Math.Clamp(deltaSeconds, 0.0f, 1.0f) : 0.0f;
        rainAtCamera = float.IsFinite(rainAtCamera) ? Math.Clamp(rainAtCamera, 0.0f, 1.0f) : 0.0f;
        if (underwaterOrSnow) { Wetness = 0.0f; _wasExposed = exposure >= ExposedThreshold; return 0.0f; }
        bool exposed = exposure >= ExposedThreshold;
        float l = Wetness * MathF.Exp(-deltaSeconds / DecayTauSeconds);
        l += GainPerSecond(rainAtCamera, exposure, lookUp) * deltaSeconds;
        if (exposed && !_wasExposed && rainAtCamera > EmergeRain) l = Math.Max(l, 0.25f + 0.4f * rainAtCamera);
        _wasExposed = exposed;
        Wetness = Math.Clamp(l, 0.0f, 1.0f);
        return Wetness;
    }
}
