using System;
using Godot;

namespace RingworldTechDemo.Weather;

/// <summary>
/// Pure layout of the stateless rain streak lattice (docs/storm-revamp-s1-design.md section 2). Two square lattices (near 12 m, mid 40 m) of hashed drop columns are anchored
/// to the world in canonical around/across metres; the preset budget fixes the cell counts, so a changing intensity or wind never re-hashes a drop. A drop's height is a
/// world-anchored periodic function of the shared simulation clock, computed here in double precision (a float clock would snap after hours of play).
/// ring_rain_streaks.glsl mirrors <see cref="FallPhase"/> and <see cref="Density"/>.
/// </summary>
public static class RainStreakLattice
{
    public const float NearRadiusMeters = 12.0f, MidRadiusMeters = 40.0f, SplashMidRadiusMeters = 20.0f;
    /// <summary>Share of the budget given to the near lattice.</summary>
    public const float NearShare = 0.4f;
    /// <summary>Vertical period of a drop column per lattice: drops live in a band this tall around the observer height (short near, taller mid), cut at the occlusion-map surface and the cloud base.</summary>
    public const float NearPeriodMeters = 20.0f, MidPeriodMeters = 36.0f;
    public const float FallSpeedMetersPerSecond = 18.0f;
    public const float MaximumSlope = 1.5f;
    public const float StreakLengthMeters = 2.0f, StreakWidthMeters = 0.016f, MinimumWidthPixels = 1.6f;
    public const float GridSpacingMeters = 60.0f;
    public const int GridSide = 5;
    /// <summary>Hail pellets (WO-C2) are a drop class of the same lattice with their own vertical periods and fall speed (the shader mirrors these constants).</summary>
    public const float HailNearPeriodMeters = 36.0f, HailMidPeriodMeters = 60.0f, HailFallSpeedMetersPerSecond = 30.0f;
    /// <summary>Share of lattice drops that are pellets at hail intensity 1, hail slant relative to the rain slope, pellet diameter range (m) and puff life (s).</summary>
    public const float HailFractionAtFull = 0.6f, HailSlantScale = 0.35f, HailMinDiameterMeters = 0.006f, HailMaxDiameterMeters = 0.025f, HailPuffLifeSeconds = 0.28f;
    public const float SplashLifeSeconds = 0.16f, SplashStartRadiusMeters = 0.01f, SplashEndRadiusMeters = 0.07f;

    public readonly record struct Layers(int NearCellsPerSide, int MidCellsPerSide, float NearCellMeters, float MidCellMeters)
    {
        public int NearCells => NearCellsPerSide * NearCellsPerSide;
        public int MidCells => MidCellsPerSide * MidCellsPerSide;
    }

    public static Layers Resolve(int budget)
    {
        int b = Math.Clamp(budget, 256, 32000);
        int near = Math.Max(4, (int)Math.Floor(Math.Sqrt(b * NearShare)));
        int mid = Math.Max(4, (int)Math.Floor(Math.Sqrt(b * (1.0f - NearShare))));
        return new Layers(near, mid, 2.0f * NearRadiusMeters / near, 2.0f * MidRadiusMeters / mid);
    }

    /// <summary>Base cell index of the lattice for an observer coordinate (cells are anchored at multiples of the cell size).</summary>
    public static int BaseCell(double canonicalMeters, float cellMeters) => (int)Math.Floor(canonicalMeters / cellMeters);

    /// <summary>(time * fall speed + observer height) modulo the period, in double precision. A drop column's absolute height is hash*P - time*fall (mod P), independent of the observer.</summary>
    public static float FallPhase(double timeSeconds, double observerVerticalMeters, float period)
    {
        double t = double.IsFinite(timeSeconds) ? timeSeconds : 0.0;
        double v = double.IsFinite(observerVerticalMeters) ? observerVerticalMeters : 0.0;
        double phase = (t * FallSpeedMetersPerSecond + v) % period;
        if (phase < 0.0) phase += period;
        return (float)phase;
    }

    /// <summary>Hail fall phase: (time * hail fall speed + observer height) modulo the hail period, in double precision.</summary>
    public static float HailPhase(double timeSeconds, double observerVerticalMeters, float period)
    {
        double t = double.IsFinite(timeSeconds) ? timeSeconds : 0.0;
        double v = double.IsFinite(observerVerticalMeters) ? observerVerticalMeters : 0.0;
        double phase = (t * HailFallSpeedMetersPerSecond + v) % period;
        if (phase < 0.0) phase += period;
        return (float)phase;
    }

    /// <summary>Height of a drop relative to the observer, as the shader computes it (hash in 0..1): (hash * period - phase) folded into [-period/2, period/2).</summary>
    public static float RelativeHeight(float hash01, float phase, float period)
    {
        float x = (hash01 * period - phase) / period + 0.5f;
        return (x - MathF.Floor(x)) * period - 0.5f * period;
    }

    /// <summary>Share of lattice drops drawn at a local intensity; the stage-weighted table of <see cref="RainPrecipitationProfile"/>.</summary>
    public static float Density(float intensity) => RainPrecipitationProfile.StreakDensity(intensity);

    /// <summary>Streak slope (horizontal metres per metre of fall) from the wind at a point, capped.</summary>
    public static Vector2 Slope(Vector2 windMetersPerSecond)
    {
        if (!windMetersPerSecond.IsFinite()) return Vector2.Zero;
        Vector2 slope = windMetersPerSecond / FallSpeedMetersPerSecond;
        return slope.Length() > MaximumSlope ? slope.Normalized() * MaximumSlope : slope;
    }
}
