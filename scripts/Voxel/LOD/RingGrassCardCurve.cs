using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Distance-curve constants of the grass-card system (variant D). Values mirror the Grass cards settings.</summary>
internal readonly record struct RingGrassCardCurveParameters(
    float NearDensity = 14f, float BaseDensity = 2.5f, float FloorDensity = .15f, float FalloffExponent = 1.3f,
    float FalloffStartMeters = 20f, float NearBoostStartMeters = 8f, float NearBoostEndMeters = 45f,
    float CompensationMax = 2.5f, float NearWidthScale = .7f, float ReachMeters = 400f, float FadeBandPercent = 15f,
    float LowLayerFullMeters = 8f, float LowLayerEndMeters = 22f,
    float HeroThinMinimum = .4f, float HeroThinStartMeters = 3f, float HeroThinEndMeters = 7.5f)
{
    /// <summary>Use this, not default(...): a record struct does not apply its parameter defaults to default.</summary>
    public static readonly RingGrassCardCurveParameters Default = new(NearDensity: 14f);
}

/// <summary>
/// C# reference of the runtime card density / keep / grow-in / scale curve. This is the single source of truth: the
/// compute accept branch and the card vertex shader must reproduce these values (parity-tested). Ranks are the
/// per-instance Bayer-stratified values the tile builder stores in CUSTOM.x (fractional part) and TierKeys.
/// </summary>
internal static class RingGrassCardCurve
{
    public const float MinimumGrowIn = .08f;

    public static float Smooth(float a, float b, float x)
    {
        float t = Mathf.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Tier fraction of ladder step t: 2^(-t/2).</summary>
    public static float LadderFraction(int tier) => MathF.Pow(2f, -Math.Max(0, tier) * .5f);

    /// <summary>Number of ladder steps until the fraction drops to the floor density.</summary>
    public static int LadderTierCount(in RingGrassCardCurveParameters p)
    {
        float floor = p.FloorDensity / Math.Max(.01f, p.NearDensity);
        int t = 0;
        while (LadderFraction(t) > floor && t < 32) t++;
        return t;
    }

    /// <summary>The far density law: (d0/d)^k clamped between the floor and the base density.</summary>
    public static float FarDensity(in RingGrassCardCurveParameters p, float distance)
        => Mathf.Clamp(p.BaseDensity * MathF.Pow(p.FalloffStartMeters / Math.Max(distance, p.FalloffStartMeters), p.FalloffExponent),
            p.FloorDensity, p.BaseDensity);

    /// <summary>Instances per square metre at a distance, including the near boost toward NearDensity.</summary>
    public static float Density(in RingGrassCardCurveParameters p, float distance)
    {
        float far = FarDensity(p, distance);
        if (p.NearDensity <= p.BaseDensity) return far;
        return far + (p.NearDensity - far) * (1f - Smooth(p.NearBoostStartMeters, p.NearBoostEndMeters, distance));
    }

    /// <summary>Fraction of the lattice kept at a distance; an instance survives when its rank is below this.</summary>
    public static float Keep(in RingGrassCardCurveParameters p, float distance) => Math.Min(1f, Density(p, distance) / Math.Max(.01f, p.NearDensity));

    public static float GrowIn(float keep, float rank) => Smooth(0f, 1f, (keep - rank) / Math.Max(.02f, .2f * keep));

    public static bool Accepts(in RingGrassCardCurveParameters p, float rank, float distance)
        => rank < Keep(p, distance) && GrowIn(Keep(p, distance), rank) >= MinimumGrowIn;

    /// <summary>1/sqrt(density ratio), capped: thinned survivors grow so screen coverage stays roughly constant.</summary>
    public static float Compensation(in RingGrassCardCurveParameters p, float distance)
        => Math.Min(1f / MathF.Sqrt(Math.Min(1f, Density(p, distance) / Math.Max(.01f, p.BaseDensity))), p.CompensationMax);

    public static float NearWidth(in RingGrassCardCurveParameters p, float distance)
        => Mathf.Lerp(p.NearWidthScale, 1f, Smooth(p.NearBoostStartMeters, p.NearBoostEndMeters, distance));

    /// <summary>Final collapse over the last FadeBandPercent of the reach.</summary>
    public static float Fade(in RingGrassCardCurveParameters p, float distance)
        => 1f - Smooth(p.ReachMeters * (1f - p.FadeBandPercent * .01f), p.ReachMeters, distance);

    /// <summary>Hero-layer thinning of the cards: 1 when disabled by the caller, else smooth(0,.15, keepNear - rank).</summary>
    public static float HeroThin(in RingGrassCardCurveParameters p, float rank, float distance)
    {
        float keep = Mathf.Lerp(p.HeroThinMinimum, 1f, Smooth(p.HeroThinStartMeters, p.HeroThinEndMeters, distance));
        return keep >= .999f ? 1f : Smooth(0f, .15f, keep - rank * .999f);
    }

    /// <summary>Instance scale multiplier (0 = culled): the product the card shader applies to the baked transform.</summary>
    public static float Scale(in RingGrassCardCurveParameters p, float rank, float distance)
    {
        float keep = Keep(p, distance);
        if (rank >= keep) return 0f;
        float grow = GrowIn(keep, rank);
        if (grow < MinimumGrowIn) return 0f;
        return Compensation(p, distance) * grow * NearWidth(p, distance) * Fade(p, distance) * HeroThin(p, rank, distance);
    }

    /// <summary>Low upright layer: fully kept inside LowLayerFullMeters, gone by LowLayerEndMeters.</summary>
    public static float LowKeep(in RingGrassCardCurveParameters p, float distance)
        => 1f - Smooth(p.LowLayerFullMeters, p.LowLayerEndMeters, distance);

    public static float LowScale(in RingGrassCardCurveParameters p, float rank, float distance)
    {
        float keep = LowKeep(p, distance);
        if (rank >= keep) return 0f;
        float grow = GrowIn(keep, rank);
        return grow < MinimumGrowIn ? 0f : grow;
    }

    /// <summary>A tier whose nearest point is at or inside lowEnd + lookahead still needs the low layer.</summary>
    public static bool TierNeedsLowLayer(in RingGrassCardCurveParameters p, float nearestDistance, float lookahead)
        => nearestDistance <= p.LowLayerEndMeters + Math.Max(0f, lookahead);
}
