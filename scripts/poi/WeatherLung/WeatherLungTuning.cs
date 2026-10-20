using Godot;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Every live-tunable Weather Lung value (the grave-key menu section and its saved defaults, S6). A plain value bundle so the Poi layer does not
/// depend on the UI layer: <c>GameplayWeatherLungDefaults.Apply</c> builds one and hands it to <see cref="WeatherLungStreamer.Configure"/>.
/// </summary>
public readonly record struct WeatherLungTuning(
    bool Enabled, bool AnimationEnabled, float CycleSeconds, float VaneAmplitudeDeg, float GillAmplitudeDeg, float TurbineMaxRpm, float RippleLag,
    float NozzleEmissionScale, float HeatGridEmissionScale, WeatherLungSunMode SunMode, float SunManual,
    float FullRateMeters, float ReducedRateMeters, float ReducedRateHz,
    float DetailRangeMeters, float DressingRangeMeters, float DressingFadeMeters, float FarProxyMaxKm,
    float DetailLoadRadiusMeters, float UnloadMarginMeters, float CollisionRadiusMeters, float DetailAttachBudgetMs)
{
    /// <summary>The Detail load radius must cover the Detail visible range (the FarProxy begin) plus this much.</summary>
    public const float LoadRadiusOverRangeMeters = 4000f;

    public static WeatherLungTuning Defaults => new(
        true, true, 180f, 35f, 35f, 1.5f, 0.012f, 0.25f, 1f, WeatherLungSunMode.Auto, 1f,
        8000f, 28000f, 10f, 28000f, 2000f, 250f, 700f, 36000f, 8000f, 3000f, 4f);

    /// <summary>Detail load radius raised to at least DetailRange + 4 km (what the streamer actually uses).</summary>
    public float EffectiveLoadRadiusMeters => Mathf.Max(DetailLoadRadiusMeters, DetailRangeMeters + LoadRadiusOverRangeMeters);
}
