using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Card-mode inputs of the GPU vegetation accept branch (words 62..77 of the compute parameters). The GPU culls
/// conservatively at distance - DistanceMarginMeters; the card vertex shader keeps the exact per-vertex say.</summary>
public readonly record struct RingGrassCardComputeParameters(
    float ReachMeters, float NearDensity, float BaseDensity, float FloorDensity, float FalloffExponent,
    float FalloffStartMeters, float NearBoostStartMeters, float NearBoostEndMeters,
    float LowLayerFullMeters, float LowLayerEndMeters, float MinimumGrowIn,
    float DistanceMarginMeters, float GuardWidthFactor, float GuardHeightFactor)
{
    public const float MaximumMarginMeters = 8f;
    // Card horizontal half-extent per unit width scale = 0.46 * 1.6 * 2.5 <= BoundsHorizontalExtent (2.0), applied to the
    // Frobenius radius sqrt(|X|^2+|Z|^2) >= |X|. Height: compensation 2.5 x (1 + wind lateral .58 + tilt .08) + 1 (interaction).
    public const float DefaultGuardWidthFactor = RingGrassCardPlacement.BoundsHorizontalExtent;
    public const float DefaultGuardHeightFactor = RingGrassCardPlacement.BoundsHeightScale * 1.66f + 1f;

    internal static RingGrassCardComputeParameters From(in RingGrassCardCurveParameters c, float marginMeters)
    {
        RingGrassCardCurveParameters d = RingGrassCardCurveParameters.Default;
        return new RingGrassCardComputeParameters(
            Math.Max(0f, Finite(c.ReachMeters, d.ReachMeters)), Math.Max(0f, Finite(c.NearDensity, d.NearDensity)),
            Math.Max(0f, Finite(c.BaseDensity, d.BaseDensity)), Math.Max(0f, Finite(c.FloorDensity, d.FloorDensity)),
            Finite(c.FalloffExponent, d.FalloffExponent), Math.Max(.001f, Finite(c.FalloffStartMeters, d.FalloffStartMeters)),
            Finite(c.NearBoostStartMeters, d.NearBoostStartMeters), Finite(c.NearBoostEndMeters, d.NearBoostEndMeters),
            Finite(c.LowLayerFullMeters, d.LowLayerFullMeters), Finite(c.LowLayerEndMeters, d.LowLayerEndMeters),
            RingGrassCardCurve.MinimumGrowIn, Mathf.Clamp(Finite(marginMeters, 0f), 0f, MaximumMarginMeters),
            DefaultGuardWidthFactor, DefaultGuardHeightFactor);
    }

    private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;

    private RingGrassCardCurveParameters Curve => new(NearDensity: NearDensity, BaseDensity: BaseDensity, FloorDensity: FloorDensity,
        FalloffExponent: FalloffExponent, FalloffStartMeters: FalloffStartMeters, NearBoostStartMeters: NearBoostStartMeters,
        NearBoostEndMeters: NearBoostEndMeters, ReachMeters: ReachMeters, LowLayerFullMeters: LowLayerFullMeters,
        LowLayerEndMeters: LowLayerEndMeters);

    private static void Decode(float customX, out float rank, out bool low)
    {
        float slot = MathF.Floor(customX);
        rank = (customX - slot) / 0.999f;
        low = MathF.Floor(slot * .25f) > .5f;
    }

    /// <summary>C# reference of the GLSL card branch without the frustum test (tests only). Same operation order as the GLSL.</summary>
    public static bool ReferenceAccepts(float customX, float cameraDistance, in RingGrassCardComputeParameters p)
    {
        Decode(customX, out float rank, out bool low);
        float d = MathF.Max(0f, cameraDistance - p.DistanceMarginMeters);
        if (!(d < p.ReachMeters)) return false;
        RingGrassCardCurveParameters curve = p.Curve;
        float keep = low ? RingGrassCardCurve.LowKeep(curve, d) : RingGrassCardCurve.Keep(curve, d);
        if (!(rank < keep)) return false;
        return RingGrassCardCurve.GrowIn(keep, rank) >= p.MinimumGrowIn;
    }

    /// <summary>Tests: true when float noise (GPU pow/division vs MathF) could flip the decision.</summary>
    public static bool ReferenceAmbiguous(float customX, float cameraDistance, in RingGrassCardComputeParameters p)
    {
        Decode(customX, out float rank, out bool low);
        float d = MathF.Max(0f, cameraDistance - p.DistanceMarginMeters);
        if (MathF.Abs(d - p.ReachMeters) <= 1e-3f) return true;
        RingGrassCardCurveParameters curve = p.Curve;
        float keep = low ? RingGrassCardCurve.LowKeep(curve, d) : RingGrassCardCurve.Keep(curve, d);
        if (MathF.Abs(rank - keep) <= 1e-5f) return true;
        return MathF.Abs(RingGrassCardCurve.GrowIn(keep, rank) - p.MinimumGrowIn) <= 1e-4f;
    }
}
