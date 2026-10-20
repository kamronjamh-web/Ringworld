using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Persisted Weather Lung choices (grave-key menu). Freeze, phase scrub and the teleports are session-only and not saved.</summary>
[GlobalClass]
public partial class GameplayWeatherLungSettings : Resource
{
    [Export] public bool Enabled { get; set; } = true;
    [Export] public bool AnimationEnabled { get; set; } = true;
    [Export] public float CycleSeconds { get; set; } = 180f;
    [Export] public float VaneAmplitudeDeg { get; set; } = 35f;
    [Export] public float GillAmplitudeDeg { get; set; } = 35f;
    [Export] public float TurbineMaxRpm { get; set; } = 1.5f;
    [Export] public float RippleLag { get; set; } = 0.012f;
    [Export] public float NozzleEmissionScale { get; set; } = 0.25f;
    [Export] public float HeatGridEmissionScale { get; set; } = 1f;
    /// <summary>0 Auto, 1 Day, 2 Night, 3 Manual (WeatherLungSunMode).</summary>
    [Export] public int SunMode { get; set; }
    [Export] public float SunManual { get; set; } = 1f;
    [Export] public float FullRateMeters { get; set; } = 8000f;
    [Export] public float ReducedRateMeters { get; set; } = 28000f;
    [Export] public float ReducedRateHz { get; set; } = 10f;
    [Export] public float DetailRangeMeters { get; set; } = 28000f;
    [Export] public float DressingRangeMeters { get; set; } = 2000f;
    [Export] public float DressingFadeMeters { get; set; } = 250f;
    [Export] public float FarProxyMaxKm { get; set; } = 700f;
    [Export] public float DetailLoadRadiusMeters { get; set; } = 36000f;
    [Export] public float UnloadMarginMeters { get; set; } = 8000f;
    [Export] public float CollisionRadiusMeters { get; set; } = 3000f;
    /// <summary>Main-thread time per frame spent assembling the Detail scene (it is instantiated one piece at a time).</summary>
    [Export] public float DetailAttachBudgetMs { get; set; } = 4f;
}
