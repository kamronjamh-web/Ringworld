using Godot;

namespace RingworldTechDemo;

[GlobalClass]
public partial class CelestialConfiguration : Resource
{
    public const double DefaultRingDaySeconds = 4500d;

    /// <summary>
    /// Active gameplay day length. The default is 4,500 seconds (75 minutes).
    /// </summary>
    [Export] public double RingDaySeconds { get; set; } = DefaultRingDaySeconds;
    [Export] public float RingTiltDegrees { get; set; } = 15f;
    [Export] public float NominalRingRadiusKm { get; set; } = 5000f;
    [Export] public float StarAngularRadiusDegrees { get; set; } = .265f;
    // Partial-phase tint control only (docs/eclipse-false-sun-spec.md D.3): it caps the gameplay darkness that tints fog and sky by
    // darkness x day factor. Totality is already exactly dark (direct sun and solar visibility use raw coverage), and the pitch-black
    // eclipse (EclipseDarknessModel, phase E2) is driven by raw coverage, never by this cap.
    [Export(PropertyHint.Range, "0,1,0.01")] public float GameplayEclipseDarknessCap { get; set; } = .95f;
    [Export(PropertyHint.Range, "-1,1,0.001")] public float DaylightStartDot { get; set; } = -.2f;
    [Export(PropertyHint.Range, "-1,1,0.001")] public float DaylightFullDot { get; set; } = .35f;
    [Export(PropertyHint.Range, "-30,-1,0.1")] public float AstronomicalTwilightElevationDegrees { get; set; } = -18f;
    [Export(PropertyHint.Range, "-3,1,0.01")] public float HorizonEventElevationDegrees { get; set; } = -.833f;
    [Export(PropertyHint.Range, ".1,5,0.1")] public float SunriseBandDegrees { get; set; } = 1.5f;
    [Export] public int VisualSeed { get; set; }
    [Export] public GasGiantConfiguration GasGiant { get; set; } = new();
}
