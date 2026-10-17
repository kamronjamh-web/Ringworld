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
    /// <summary>Tweaks T3: seconds a Sustain refuel orb takes from the spire base to the hub (the hub heartbeat lands on its arrival).</summary>
    [Export] public float OrbTravelSeconds { get; set; } = 30f;
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
    [Export] public float HubSkyTintStrength { get; set; } = 0.10f;
    [Export] public float HubZenithHaloIntensity { get; set; } = 0.15f;
    /// <summary>Pitch-black eclipse darkness (spec D.1): master switch and the totality multipliers. Applies with or without the false-sun event.</summary>
    [Export] public bool EclipseDarknessEnabled { get; set; } = true;
    [Export] public float EclipseAmbientScale { get; set; } = 0.15f;
    [Export] public float EclipseAtmosphereNightScale { get; set; } = 0.35f;
    [Export] public float EclipseSkyNightScale { get; set; } = 0.5f;
    [Export] public float EclipseCloudAmbientScale { get; set; } = 0.4f;
    [Export] public float EclipseStarVisibility { get; set; } = 1f;
    /// <summary>Spire to hub beams (spec E.2, phase E4b): layers, widths in real metres, emissions and the depth core. Visual only.</summary>
    [Export] public bool BeamsEnabled { get; set; } = true;
    [Export] public bool BeamFarLayerEnabled { get; set; } = true;
    [Export] public bool BeamDepthWriteCore { get; set; } = true;
    [Export] public float BeamCoreWidthMeters { get; set; } = 150f;
    [Export] public float BeamHaloWidthMeters { get; set; } = 1200f;
    [Export] public float BeamMainCoreWidthMeters { get; set; } = 300f;
    [Export] public float BeamMainHaloWidthMeters { get; set; } = 2400f;
    [Export] public float BeamMinPixelWidth { get; set; } = 1.5f;
    [Export] public float BeamFarCompensation { get; set; } = 0.45f;
    [Export] public float BeamBaseEmission { get; set; } = 12f;
    [Export] public float BeamMainEmissionScale { get; set; } = 1.5f;
    [Export] public float BeamHaloAmplitude { get; set; } = 0.12f;
    [Export] public float BeamBaseFadeMeters { get; set; } = 400f;
    /// <summary>Beam origin (tweaks T2): 0 = Crown, 1 = Base (the bore iris, 200 m under the pad; default), 2 = Reactor (exhaust top, a preview).</summary>
    [Export] public int BeamOrigin { get; set; } = 1;
    /// <summary>Close-range softening of the beam light: it fades in over this many metres of view distance (0 = off).</summary>
    [Export] public float BeamCloseSoftenMeters { get; set; } = 1000f;
    [Export] public float BeamDayVisibility { get; set; } = 0.6f;
    [Export] public float BeamCoreDepthThreshold { get; set; } = 0.35f;
    [Export] public float BeamCoreDepthMinPixels { get; set; } = 4f;
    [Export] public float BeamSoftTipFraction { get; set; } = 0.005f;
    /// <summary>Grid ignition fronts (spec E.3, phase E4d): multiplier of the grid night peak while the fronts are live.</summary>
    [Export] public float BeamGridEventPeak { get; set; } = 1.5f;
    /// <summary>In-cloud diffusion of the near beams (spec E.6, phase E4e), the stand-in crown glow and the bore light boost (spec E.7).</summary>
    [Export] public bool BeamCloudDiffusionEnabled { get; set; } = true;
    [Export] public float BeamCloudWidening { get; set; } = 3f;
    [Export] public float BeamCloudHaloBoost { get; set; } = 1f;
    [Export] public float BeamCloudCoreCut { get; set; } = 1.5f;
    [Export] public bool BeamCrownGlowEnabled { get; set; } = true;
    [Export] public float BeamCrownGlowEmission { get; set; } = 3f;
    [Export] public float BeamBoreLightBoost { get; set; } = 4f;
    /// <summary>Sustain refuel orb look (tweaks T3): position ease exponent, core radius and halo e-fold (m), minimum core radius (px), HDR emission.</summary>
    [Export] public float BeamOrbEaseExponent { get; set; } = 3f;
    [Export] public float BeamOrbRadiusMeters { get; set; } = 350f;
    [Export] public float BeamOrbHaloMeters { get; set; } = 1500f;
    [Export] public float BeamOrbMinPixelRadius { get; set; } = 2.5f;
    [Export] public float BeamOrbEmission { get; set; } = 16f;
    /// <summary>Beam cloud light (tweaks T4/T5): beams, orbs and optionally grid links light the Sunshine clouds as segment lights. Max segments is stored as a float (1-8, rounded).</summary>
    [Export] public bool BeamCloudLightEnabled { get; set; } = true;
    [Export] public float BeamCloudLightIntensity { get; set; } = 5f;
    [Export] public float BeamCloudLightRadiusMeters { get; set; } = 4000f;
    [Export] public float BeamCloudLightMaxSegments { get; set; } = 4f;
    [Export] public bool GridCloudLightEnabled { get; set; } = true;
    /// <summary>Hub sun body and sky in-scatter (spec E.1, phase E4c): the main-viewport disc and halo, radii in real km at the hub. Visual only.</summary>
    [Export] public bool HubBodyEnabled { get; set; } = true;
    [Export] public float HubBodyCoreEmission { get; set; } = 40f;
    [Export] public float HubBodyRadiusKm { get; set; } = 30f;
    [Export] public float HubBodyMinRadiusPixels { get; set; } = 2f;
    [Export] public float HubBodyHaloAmplitude { get; set; } = 0.6f;
    [Export] public float HubBodyHaloRadiusKm { get; set; } = 400f;
    [Export] public float HubSkyInScatter { get; set; } = 1f;
}
