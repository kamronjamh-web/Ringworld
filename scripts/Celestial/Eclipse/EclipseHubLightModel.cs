using System;

namespace RingworldTechDemo.Celestial.Eclipse;

/// <summary>
/// Hub lighting for one frame (docs/eclipse-false-sun-spec.md C.1, C.5). <see cref="Weight"/> is the share of the primary DirectionalLight's
/// direction that follows the local zenith; <see cref="DirectEnergy"/> is the StarLight energy the hub asks for (the real sun's own energy is
/// max'd against it); <see cref="Day"/> is the per-point day the hub contributes everywhere. Everything is exactly 0 outside the event.
/// </summary>
public readonly record struct EclipseHubLightOutput(
    double Weight, double Level, double DirectEnergy, double Day,
    double ColorR, double ColorG, double ColorB, double SkyTintStrength, double ZenithHaloIntensity, bool Overridden)
{
    public static EclipseHubLightOutput Neutral => new(0d, 0d, 0d, 0d, 1d, 1d, 1d, 0d, 0d, false);
    public bool Active => Weight > 0d || Day > 0d || DirectEnergy > 0d;
}

/// <summary>Pure hub lighting model: timeline state (or a debug level override) to the lighting outputs.</summary>
public static class EclipseHubLightModel
{
    /// <summary>Highest debug override level (the overload flare peak is 2.5).</summary>
    public const double MaxOverrideLevel = 2.5d;

    /// <summary>
    /// <paramref name="levelOverride"/> below 0 means "no override": the timeline state drives the hub. At or above 0 it forces the hub weight
    /// to 1 and the level to the override (visual debug only, meant for lighting captures outside an eclipse).
    /// </summary>
    public static EclipseHubLightOutput Evaluate(EclipseEventSettings settings, in EclipseEventState state, double levelOverride)
    {
        double weight, level, direct, day;
        bool overridden = double.IsFinite(levelOverride) && levelOverride >= 0d;
        if (overridden)
        {
            level = Math.Min(levelOverride, MaxOverrideLevel);
            weight = 1d;
            direct = settings.RealSunEnergy * settings.HubDirectFraction * level;
            day = Math.Min(1d, settings.HubDayAtFull * level);
        }
        else
        {
            weight = state.HubWeight;
            level = state.Level;
            direct = state.HubDirect;
            day = state.HubDay;
        }
        weight = Finite(weight, 0d, 1d);
        level = Finite(level, 0d, MaxOverrideLevel);
        direct = Finite(direct, 0d, 64d);
        day = Finite(day, 0d, 1d);
        if (weight <= 0d && day <= 0d && direct <= 0d) return EclipseHubLightOutput.Neutral;
        return new EclipseHubLightOutput(weight, level, direct, day,
            settings.HubLightRed, settings.HubLightGreen, settings.HubLightBlue,
            settings.HubSkyTintStrength, settings.HubZenithHaloIntensity, overridden);
    }

    /// <summary>Per-point day with the hub term (CPU twin of ringworld_effective_solar_day): max(geometric x visibility, hub day).</summary>
    public static double EffectiveDay(double geometricDay, double solarVisibility, double hubDay)
        => Math.Max(Math.Clamp(geometricDay, 0d, 1d) * Math.Clamp(solarVisibility, 0d, 1d), Math.Clamp(hubDay, 0d, 1d));

    private static double Finite(double v, double lo, double hi) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : 0d;
}

/// <summary>
/// Last hub lighting weight published by the eclipse director, readable without touching the RenderingServer. Used by the macro-shadow
/// producer to hold its rebuilds while the light follows the camera zenith (spec C.2, F17).
/// </summary>
public static class EclipseHubLighting
{
    /// <summary>Frames the weight must stay exactly 0 before the macro shadow hold is released (the light basis lags the director by a frame).</summary>
    public const int QuietFramesBeforeRelease = 3;

    private static volatile float _weight;
    private static int _quietFrames = int.MaxValue / 2;

    public static float Weight { get => _weight; set => _weight = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f; }

    /// <summary>
    /// True while the primary light is (or was a moment ago) aimed away from the real sun. The macro-shadow mask is built from the light
    /// direction at the moment a rebuild starts, so the rebuild after the event must wait until the light is exactly back on the sun.
    /// </summary>
    public static bool HoldMacroShadows => _weight > 0f || _quietFrames < QuietFramesBeforeRelease;

    /// <summary>Called once per frame by the director after it publishes the weight.</summary>
    public static void Tick() => _quietFrames = _weight > 0f ? 0 : Math.Min(_quietFrames + 1, int.MaxValue / 2);

    public static void Reset() { _weight = 0f; _quietFrames = int.MaxValue / 2; }
}
