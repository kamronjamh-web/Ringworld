using System;
using Godot;

namespace RingworldTechDemo;

public enum CelestialEclipseType
{
    None,
    Partial,
    Total
}

/// <summary>
/// Human-readable phase for diagnostics and coarse gameplay decisions. Smooth
/// factors remain authoritative for lighting and should be preferred to switches.
/// </summary>
public enum CelestialLightingPhase
{
    Night,
    AstronomicalTwilight,
    Dawn,
    Sunrise,
    Morning,
    Midday,
    Afternoon,
    Sunset,
    Dusk
}

/// <summary>Immutable, allocation-free result of one absolute-time celestial evaluation.</summary>
public readonly struct CelestialFrame
{
    public readonly double AbsoluteSeconds;
    public readonly double RingRotationAngleRadians;
    public readonly double OrbitalAngleRadians;
    public readonly double SeasonAngleRadians;

    public readonly Vector3 ToSun;
    public readonly Vector3 ToGiant;
    public readonly float GiantDistanceKm;
    public readonly float GiantAngularRadiusDegrees;
    public readonly float GiantAngularDiameterDegrees;
    public readonly float StarGiantAngularSeparationDegrees;

    public readonly float RawEclipseCoverage;
    public readonly float GameplayEclipseDarkness;
    public readonly CelestialEclipseType EclipseType;

    public readonly float SunElevationDegrees;
    public readonly float GiantElevationDegrees;
    public readonly float DaylightFactor;
    public readonly float TwilightFactor;
    public readonly float NightFactor;
    public readonly float GiantLightingPhase;
    public readonly CelestialLightingPhase LightingPhase;
    public readonly bool SunAboveHorizon;
    public readonly bool GiantAboveHorizon;

    public readonly double SecondsUntilSunrise;
    public readonly double SecondsUntilSunset;
    public readonly double SecondsUntilGiantRise;
    public readonly double SecondsUntilGiantSet;
    public readonly double NextSunriseSeconds;
    public readonly double NextSunsetSeconds;
    public readonly double NextGiantRiseSeconds;
    public readonly double NextGiantSetSeconds;

    public double RingRotationAngleDegrees => Mathf.RadToDeg(RingRotationAngleRadians);
    public double OrbitalAngleDegrees => Mathf.RadToDeg(OrbitalAngleRadians);
    public double SeasonAngleDegrees => Mathf.RadToDeg(SeasonAngleRadians);

    public CelestialFrame(
        double absoluteSeconds,
        double ringRotationAngleRadians,
        double orbitalAngleRadians,
        double seasonAngleRadians,
        Vector3 toSun,
        Vector3 toGiant,
        float giantDistanceKm,
        float giantAngularRadiusDegrees,
        float starGiantAngularSeparationDegrees,
        float rawEclipseCoverage,
        float gameplayEclipseDarkness,
        CelestialEclipseType eclipseType,
        float sunElevationDegrees,
        float giantElevationDegrees,
        float daylightFactor,
        float twilightFactor,
        float nightFactor,
        float giantLightingPhase,
        CelestialLightingPhase lightingPhase,
        bool sunAboveHorizon,
        bool giantAboveHorizon,
        double secondsUntilSunrise,
        double secondsUntilSunset,
        double secondsUntilGiantRise,
        double secondsUntilGiantSet)
    {
        AbsoluteSeconds = absoluteSeconds;
        RingRotationAngleRadians = ringRotationAngleRadians;
        OrbitalAngleRadians = orbitalAngleRadians;
        SeasonAngleRadians = seasonAngleRadians;
        ToSun = toSun;
        ToGiant = toGiant;
        GiantDistanceKm = giantDistanceKm;
        GiantAngularRadiusDegrees = giantAngularRadiusDegrees;
        GiantAngularDiameterDegrees = giantAngularRadiusDegrees * 2f;
        StarGiantAngularSeparationDegrees = starGiantAngularSeparationDegrees;
        RawEclipseCoverage = rawEclipseCoverage;
        GameplayEclipseDarkness = gameplayEclipseDarkness;
        EclipseType = eclipseType;
        SunElevationDegrees = sunElevationDegrees;
        GiantElevationDegrees = giantElevationDegrees;
        DaylightFactor = daylightFactor;
        TwilightFactor = twilightFactor;
        NightFactor = nightFactor;
        GiantLightingPhase = giantLightingPhase;
        LightingPhase = lightingPhase;
        SunAboveHorizon = sunAboveHorizon;
        GiantAboveHorizon = giantAboveHorizon;
        SecondsUntilSunrise = SanitizeEventDuration(secondsUntilSunrise);
        SecondsUntilSunset = SanitizeEventDuration(secondsUntilSunset);
        SecondsUntilGiantRise = SanitizeEventDuration(secondsUntilGiantRise);
        SecondsUntilGiantSet = SanitizeEventDuration(secondsUntilGiantSet);
        NextSunriseSeconds = AbsoluteEventTime(absoluteSeconds, SecondsUntilSunrise);
        NextSunsetSeconds = AbsoluteEventTime(absoluteSeconds, SecondsUntilSunset);
        NextGiantRiseSeconds = AbsoluteEventTime(absoluteSeconds, SecondsUntilGiantRise);
        NextGiantSetSeconds = AbsoluteEventTime(absoluteSeconds, SecondsUntilGiantSet);
    }

    /// <summary>
    /// Returns the same instantaneous frame with refreshed giant horizon-event
    /// predictions. This lets the runtime evaluate the expensive root search at
    /// a low cadence without making the continuously updated sky state mutable.
    /// </summary>
    public CelestialFrame WithGiantEventDurations(
        double secondsUntilGiantRise,
        double secondsUntilGiantSet) => new(
            AbsoluteSeconds,
            RingRotationAngleRadians,
            OrbitalAngleRadians,
            SeasonAngleRadians,
            ToSun,
            ToGiant,
            GiantDistanceKm,
            GiantAngularRadiusDegrees,
            StarGiantAngularSeparationDegrees,
            RawEclipseCoverage,
            GameplayEclipseDarkness,
            EclipseType,
            SunElevationDegrees,
            GiantElevationDegrees,
            DaylightFactor,
            TwilightFactor,
            NightFactor,
            GiantLightingPhase,
            LightingPhase,
            SunAboveHorizon,
            GiantAboveHorizon,
            SecondsUntilSunrise,
            SecondsUntilSunset,
            secondsUntilGiantRise,
            secondsUntilGiantSet);

    private static double AbsoluteEventTime(double now, double duration) =>
        double.IsFinite(duration) ? now + Math.Max(0d, duration) : double.PositiveInfinity;

    private static double SanitizeEventDuration(double duration) =>
        double.IsFinite(duration) ? Math.Max(0d, duration) : double.PositiveInfinity;
}
