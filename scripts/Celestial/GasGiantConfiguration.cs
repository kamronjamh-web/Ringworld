using Godot;

namespace RingworldTechDemo;

[GlobalClass]
public partial class GasGiantConfiguration : Resource
{
    // Presentation reference only: with a centered disc, a 16:9 viewport, 80 degrees
    // vertical FOV and KEEP_HEIGHT camera behaviour, width = tan(alpha) /
    // (aspect * tan(verticalFov / 2)).  The physical radius/distance below give
    // alpha = 30.82423340340 degrees and therefore the requested 40% screen width.
    // This is documentation for the normal view, not a runtime camera dependency.
    public const float NormalViewVerticalFovDegrees = 80f;
    public const float NormalViewAspectRatio = 16f / 9f;
    public const float NormalViewCenteredWidthFraction = .4f;
    public const float DefaultApparentAngularRadiusDegrees = 30.82423340340f;
    public const double DefaultCenteredTotalDurationSeconds = 4500d;
    public const float PartialFixtureGrazingMissDegrees = 30.724235534668f;

    // Circular two-body preset: T = 2*pi*sqrt(a^3/(G*M)), with a =
    // 136,610.390625 km, G = 6.67430e-11 SI, and M = 1.898e27 kg.
    // The authored orbit period remains configurable; this simply supplies the
    // physically-derived default.
    public const float DefaultRadiusKm = 70000f;
    public const float DefaultDistanceKm = 136610.390625f;
    public const double DefaultOrbitSeconds = 28187.36649295002d;
    // Chosen from the physical disc radii to give a centered 4,500-second
    // total eclipse per orbit: miss = acos(cos(alpha - beta) / cos(pi * day / T)).
    // Observer parallax naturally shifts surface timing.
    public const float DefaultGrazingMissDegrees = 10.87182510946549f;
    public const double DefaultMassKg = 1.898e27d;
    public const float DefaultInitialPhaseDegrees = 180f;

    [Export] public bool Enabled { get; set; } = true;
    [Export] public float RadiusKm { get; set; } = DefaultRadiusKm;
    [Export] public float DistanceKm { get; set; } = DefaultDistanceKm;
    [Export] public double OrbitSeconds { get; set; } = DefaultOrbitSeconds;
    /// <summary>
    /// Signed closest angular approach to the star. It doubles as the current
    /// game-friendly orbital-inclination control. The serialized compatibility
    /// name remains, but the default now creates the authored total eclipse.
    /// </summary>
    [Export] public float GrazingMissDegrees { get; set; } = DefaultGrazingMissDegrees;
    [Export] public float InitialPhaseDegrees { get; set; } = DefaultInitialPhaseDegrees;
    [Export] public float SeasonMissAmplitudeDegrees { get; set; } = 0f;
    [Export] public double SeasonSeconds { get; set; } = 0d;
    [Export] public float SeasonPhaseDegrees { get; set; }
    [Export] public double MassKg { get; set; } = DefaultMassKg;
    [Export] public int VisualSeedOffset { get; set; }
    [Export] public float AxialTiltDegrees { get; set; } = 3.13f;
    [Export] public double RotationPeriodSeconds { get; set; } = 35730d;

    // Clear astronomical name for C# consumers while retaining the serialized
    // compatibility name used by the first grazing-eclipse implementation.
    public float OrbitalInclinationDegrees
    {
        get => GrazingMissDegrees;
        set => GrazingMissDegrees = value;
    }
}
