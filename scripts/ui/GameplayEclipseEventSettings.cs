using Godot;

namespace RingworldTechDemo.UI;

/// <summary>
/// Persisted eclipse false-sun event choices (docs/eclipse-false-sun-spec.md B.2, C.5, G.1). Enums are stored as ints; every numeric field maps
/// one to one onto <c>RingworldTechDemo.Celestial.Eclipse.EclipseEventSettings</c> (see <see cref="GameplayEclipseEventDefaults.ToCore"/>).
/// There is deliberately no real-time-hold field: the event follows the celestial TimeScale (spec user decision 6).
/// </summary>
[GlobalClass]
public partial class GameplayEclipseEventSettings : Resource
{
    [Export] public bool EventEnabled { get; set; } = true;
    /// <summary>0 = MatchGrid, 1 = Manual.</summary>
    [Export] public int IgnitionSpeedMode { get; set; }
    /// <summary>0 = PerBeam, 1 = Perceptual.</summary>
    [Export] public int RampMode { get; set; }
    [Export] public bool SuppressGridPulseDuringIgnition { get; set; } = true;
    /// <summary>0 = Celestial (default, follows TimeScale), 1 = Weather.</summary>
    [Export] public int RefuelClock { get; set; }
    [Export] public float ParallaxSafetySeconds { get; set; } = 10f;
    [Export] public float PreRollSeconds { get; set; } = 300f;
    [Export] public float DarknessBeatSeconds { get; set; } = 20f;
    [Export] public float MainClimbSeconds { get; set; } = 4f;
    [Export] public float MainClimbExponent { get; set; } = 2f;
    [Export] public float KindleSeconds { get; set; } = 2.5f;
    [Export] public float KindleLevel { get; set; } = 0.1f;
    [Export] public float IgnitionDelaySeconds { get; set; } = 3f;
    [Export] public float IgnitionSeconds { get; set; } = 150f;
    [Export] public float RegularClimbSeconds { get; set; } = 1.5f;
    [Export] public float RampGamma { get; set; } = 1f;
    [Export] public float RampStepSmoothSeconds { get; set; } = 0.5f;
    [Export] public float IgnitionPlateauLevel { get; set; } = 0.35f;
    [Export] public float MeetingFlareSeconds { get; set; } = 1.5f;
    [Export] public float CrescendoSeconds { get; set; } = 8f;
    [Export] public float HandoffFadeSeconds { get; set; } = 2.5f;
    [Export] public float FlashClimbSeconds { get; set; } = 0.5f;
    [Export] public float FlashHoldSeconds { get; set; } = 0.6f;
    [Export] public float FlashFadeSeconds { get; set; } = 1.2f;
    [Export] public float HeartbeatKick { get; set; } = 0.02f;
    [Export] public float HeartbeatTauSeconds { get; set; } = 0.8f;
    [Export] public float RelaunchKick { get; set; } = 0.05f;
    [Export] public float BreathAmplitude { get; set; } = 0.015f;
    [Export] public float BreathPeriodSeconds { get; set; } = 11f;
    [Export] public float CollapseLeadSeconds { get; set; } = 75f;
    [Export] public float InstabilitySeconds { get; set; } = 45f;
    [Export] public float FlickerMax { get; set; } = 0.35f;
    [Export] public float FlickerHzStart { get; set; } = 0.5f;
    [Export] public float FlickerHzEnd { get; set; } = 6f;
    [Export] public float OverloadSeconds { get; set; } = 4f;
    [Export] public float OverloadGain { get; set; } = 2.5f;
    [Export] public float ImplosionSeconds { get; set; } = 2.5f;
    [Export] public float BeamRetractSeconds { get; set; } = 1f;
    [Export] public float ShockwaveSeconds { get; set; } = 8f;
    [Export] public float ShockwaveArrivalFraction { get; set; } = 0.75f;
    [Export] public float ShockFlashLevel { get; set; } = 0.6f;
    [Export] public float ShockFlashSeconds { get; set; } = 0.4f;
    [Export] public float ReturnTailSeconds { get; set; } = 450f;
    [Export] public float RealSunEnergy { get; set; } = 4f;
    [Export] public float HubDirectFraction { get; set; } = 0.8f;
    [Export] public float HubDayAtFull { get; set; } = 0.8f;
    [Export] public float HubLightRed { get; set; } = 0.92f;
    [Export] public float HubLightGreen { get; set; } = 0.97f;
    [Export] public float HubLightBlue { get; set; } = 1f;
    [Export] public float HubSkyTintStrength { get; set; } = 0.25f;
    [Export] public float HubZenithHaloIntensity { get; set; } = 0.35f;
    /// <summary>Pitch-black eclipse darkness (spec D.1): master switch and the totality multipliers. Applies with or without the false-sun event.</summary>
    [Export] public bool EclipseDarknessEnabled { get; set; } = true;
    [Export] public float EclipseAmbientScale { get; set; } = 0.15f;
    [Export] public float EclipseAtmosphereNightScale { get; set; } = 0.35f;
    [Export] public float EclipseSkyNightScale { get; set; } = 0.5f;
    [Export] public float EclipseCloudAmbientScale { get; set; } = 0.4f;
    [Export] public float EclipseStarVisibility { get; set; } = 1f;
}
