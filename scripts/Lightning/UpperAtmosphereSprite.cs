using System;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Lightning;

public enum UpperAtmosphereKind { None = 0, RedSprite = 1, BlueJet = 2, GiganticJet = 3 }

/// <summary>One transient upper-atmosphere discharge derived from a <see cref="RingLightningEvent"/>.</summary>
public readonly record struct UpperAtmosphereSprite(
    long EventId, UpperAtmosphereKind Kind, double StartTimeSeconds, double DurationSeconds,
    RingCanonicalPosition BasePosition, float HeightMeters, float WidthMeters, float Energy);

/// <summary>Pure, deterministic derivation of red sprites and blue jets from lightning events (storm revamp S4 WO-E).</summary>
public static class UpperAtmosphereLightning
{
    public const float ChanceMajor = 0.08f, ChanceExceptional = 0.2f, ChanceSuperstorm = 0.45f;
    public const float SpriteShare = 0.70f, BlueJetShare = 0.25f; // remainder is a gigantic jet
    /// <summary>Red sprites are centred this far above the cloud top (metres); jets rise from the cloud top.</summary>
    public const float SpriteAltitudeMeters = 55_000.0f;
    /// <summary>Delay after the parent flash begins (the sprite follows the return stroke).</summary>
    public const double StartDelaySeconds = 0.04;

    /// <summary>SystemKind values: 3 major front, 4 exceptional front, 5 and above superstorm (class added by S4 WO-B).</summary>
    public static float BaseChance(int systemKind) => systemKind >= 5 ? ChanceSuperstorm : systemKind == 4 ? ChanceExceptional : ChanceMajor;

    /// <summary>Chance after the user setting and reduced flashes.</summary>
    public static float EffectiveChance(int systemKind, float chanceScale) =>
        Math.Clamp(BaseChance(systemKind) * Math.Max(0.0f, chanceScale), 0.0f, 1.0f);

    public static uint Hash(long eventId, int salt)
    {
        unchecked
        {
            uint hash = (uint)eventId * 0x9e3779b9u ^ (uint)(eventId >> 32) * 0x85ebca6bu ^ (uint)salt * 0xc2b2ae35u;
            hash ^= hash >> 16;
            hash *= 0x7feb352du;
            hash ^= hash >> 15;
            hash *= 0x846ca68bu;
            return hash ^ (hash >> 16);
        }
    }

    public static float Unit(long eventId, int salt) => (Hash(eventId, salt) & 0x00ffffff) / 16777215.0f;

    /// <summary>
    /// Derives the discharge for <paramref name="e"/>, or false when the roll does not select one. Uses
    /// <see cref="LightningVisualPolicy.UpperAtmosphereChanceScale"/> unless <paramref name="chanceScale"/> is given.
    /// </summary>
    public static bool TryDerive(in RingLightningEvent e, float cloudTopMeters, out UpperAtmosphereSprite sprite, float? chanceScale = null)
    {
        sprite = default;
        if (e.EventId == 0) return false;
        float chance = EffectiveChance(e.SystemKind, chanceScale ?? LightningVisualPolicy.UpperAtmosphereChanceScale);
        if (chance <= 0.0f || Unit(e.EventId, 101) >= chance) return false;
        float pick = Unit(e.EventId, 102);
        UpperAtmosphereKind kind = pick < SpriteShare ? UpperAtmosphereKind.RedSprite
            : pick < SpriteShare + BlueJetShare ? UpperAtmosphereKind.BlueJet : UpperAtmosphereKind.GiganticJet;
        float u = Unit(e.EventId, 103);
        double duration;
        float height, width, baseVertical;
        switch (kind)
        {
            case UpperAtmosphereKind.RedSprite:
                duration = 0.12 + u * 0.18; height = 45_000.0f; width = 30_000.0f; baseVertical = cloudTopMeters + SpriteAltitudeMeters; break;
            case UpperAtmosphereKind.BlueJet:
                duration = 0.18 + u * 0.22; height = 40_000.0f; width = 14_000.0f; baseVertical = cloudTopMeters; break;
            default:
                duration = 0.25 + u * 0.25; height = 75_000.0f; width = 16_000.0f; baseVertical = cloudTopMeters; break;
        }
        sprite = new UpperAtmosphereSprite(e.EventId, kind, e.StartTimeSeconds + StartDelaySeconds, duration,
            e.CloudPosition with { VerticalMeters = baseVertical }, height, width,
            Math.Clamp(e.Energy, 0.0f, 1.0f) * LightningVisualPolicy.PeakScale);
        return true;
    }

    /// <summary>Brightness 0..1: fast rise over 15 percent of the duration, then a squared decay. Zero outside the window.</summary>
    public static float Level(in UpperAtmosphereSprite s, double timeSeconds)
    {
        double t = (timeSeconds - s.StartTimeSeconds) / Math.Max(1e-3, s.DurationSeconds);
        if (t < 0.0 || t > 1.0) return 0.0f;
        float rise = (float)Math.Clamp(t / (LightningVisualPolicy.ReducedFlashes ? 0.4 : 0.15), 0.0, 1.0);
        float fall = (float)Math.Pow(1.0 - t, 2.0);
        return rise * rise * (3.0f - 2.0f * rise) * fall;
    }

    // Air model constants mirror the ring_atmosphere.gdshaderinc defaults (boundary layer 0-2.5 km, upper layer to 20 km).
    public const float BoundaryTopMeters = 2_500.0f, UpperTopMeters = 20_000.0f;
    public const float BoundaryExtinctionMeters = 60_000.0f, UpperExtinctionMeters = 300_000.0f;
    public const float BoundaryMaxOpacity = 0.22f, UpperMaxOpacity = 0.08f, TotalMaxOpacity = 0.80f;
    public const float DistanceClearMeters = 30_000.0f, DistanceFullMeters = 220_000.0f, DistanceMaxOpacity = 0.68f;

    /// <summary>
    /// Fraction of light that survives the air between an observer and a discharge, using the same opacity-capped boundary and
    /// upper layer extinction that the far cloud flashes receive through the ring haze (legacy model). The straight chord is
    /// sampled through the ring cross-section (altitude = radius - |point|), so a chord to the opposite arch leaves the air
    /// almost at once while one along the near arch stays in it; the distance shroud counts that air path only.
    /// </summary>
    public static float PathTransmittance(in RingCanonicalPosition observer, in RingCanonicalPosition target, double ringRadiusMeters, double circumferenceMeters)
    {
        if (ringRadiusMeters <= 1000.0 || circumferenceMeters <= 1.0) return 1.0f;
        double angleA = observer.AroundMeters / ringRadiusMeters;
        double around = observer.AroundMeters - target.AroundMeters;
        around -= Math.Round(around / circumferenceMeters) * circumferenceMeters;
        double angleB = angleA - around / ringRadiusMeters;
        double radiusA = ringRadiusMeters - observer.VerticalMeters, radiusB = ringRadiusMeters - target.VerticalMeters;
        double ax = radiusA * Math.Cos(angleA), ay = radiusA * Math.Sin(angleA);
        double bx = radiusB * Math.Cos(angleB), by = radiusB * Math.Sin(angleB);
        double dz = target.AcrossMeters - observer.AcrossMeters;
        double length = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + dz * dz);
        if (length <= 1.0) return 1.0f;
        const int Samples = 96;
        double boundaryPath = 0.0, upperPath = 0.0, step = length / Samples;
        for (int i = 0; i < Samples; i++)
        {
            double t = (i + 0.5) / Samples;
            double altitude = ringRadiusMeters - Math.Sqrt(Math.Pow(ax + (bx - ax) * t, 2.0) + Math.Pow(ay + (by - ay) * t, 2.0));
            if (altitude < 0.0) continue;
            if (altitude <= BoundaryTopMeters) boundaryPath += step;
            else if (altitude <= UpperTopMeters) upperPath += step;
        }
        float boundary = Math.Min(BoundaryMaxOpacity, 1.0f - MathF.Exp(-(float)boundaryPath / BoundaryExtinctionMeters));
        float upper = Math.Min(UpperMaxOpacity, 1.0f - MathF.Exp(-(float)upperPath / UpperExtinctionMeters));
        float shroud = DistanceMaxOpacity * SmoothStep(DistanceClearMeters, DistanceFullMeters, (float)(boundaryPath + upperPath));
        float opacity = Math.Min(TotalMaxOpacity, 1.0f - (1.0f - boundary) * (1.0f - upper) * (1.0f - shroud));
        return 1.0f - opacity;
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / Math.Max(0.001f, edge1 - edge0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }
}
