using System;

namespace RingworldTechDemo.Underground;

/// <summary>
/// Pure math and smoothing for coupling the cave fog to the coarse tier's filled reach. Deep caves only: the coupling
/// weight is zero unless the coarse underground latch is on, then fades in with depth and is time-smoothed.
/// </summary>
public sealed class UndergroundFogCoupling
{
    /// <summary>Transmittance at the visible distance (ln 50 over the distance gives density).</summary>
    public const float EndTransmittance = .02f;
    public const float FadeBandMeters = 32f;
    public const float CouplingRiseSeconds = 1f, CouplingFallSeconds = .25f;
    public const float ReachGrowSeconds = 1.5f, ReachShrinkSeconds = .4f;

    private bool _hasReach;
    private float _logReach;

    /// <summary>Smoothed 0..1 weight the atmosphere multiplies its cave fog by.</summary>
    public float CouplingWeight { get; private set; }
    public float ReachMeters { get; private set; }
    public float VisibleDistanceMeters { get; private set; }
    public float Density { get; private set; }

    public static float DensityForVisibleDistance(float meters) =>
        float.IsFinite(meters) && meters > 0f ? MathF.Log(1f / EndTransmittance) / MathF.Max(meters, 1f) : 0f;

    public static float VisibleDistance(float reachMeters, float fraction)
    {
        float f = float.IsFinite(fraction) ? Math.Clamp(fraction, .5f, 1f) : .9f;
        return float.IsFinite(reachMeters) ? MathF.Max(reachMeters, 0f) * f : 0f;
    }

    /// <summary>Reach the coarse ring is known to fill: sticky against transient ready drops at the same radius, minimum on shrink.</summary>
    public static float NextCoarseFilledReach(float previous, bool active, int desired, int desiredReady, float guaranteed)
    {
        if (!active || desired <= 0 || !float.IsFinite(guaranteed) || guaranteed <= 0f) return 0f;
        if (desiredReady >= desired) return guaranteed;
        return MathF.Min(float.IsFinite(previous) ? MathF.Max(previous, 0f) : 0f, guaranteed);
    }

    /// <summary>Spatial fade: zero at the release depth, one 32 m deeper; zero unless latched.</summary>
    public static float TargetCoupling(bool latched, float depth, float releaseDepth)
    {
        if (!latched || !float.IsFinite(depth) || !float.IsFinite(releaseDepth)) return 0f;
        float t = Math.Clamp((depth - releaseDepth) / FadeBandMeters, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public void Step(double delta, bool enabled, bool latched, float depth, float releaseDepth, float filledReach, float unfilledReach, float fraction)
    {
        float dt = double.IsFinite(delta) ? (float)Math.Clamp(delta, 0.0, 1.0) : 0f;
        float unfilled = float.IsFinite(unfilledReach) ? MathF.Max(unfilledReach, 1f) : 1024f;
        float filled = float.IsFinite(filledReach) ? filledReach : 0f;
        float targetReach = filled > 0f ? MathF.Max(unfilled, filled) : unfilled;
        float targetLog = MathF.Log(targetReach);
        if (!enabled)
        {
            CouplingWeight = 0f;
            _hasReach = false;
            ReachMeters = targetReach;
            VisibleDistanceMeters = VisibleDistance(targetReach, fraction);
            Density = 0f;
            return;
        }
        float target = TargetCoupling(latched, depth, releaseDepth);
        CouplingWeight = Smooth(CouplingWeight, target, dt, target > CouplingWeight ? CouplingRiseSeconds : CouplingFallSeconds);
        if (target <= 0f && CouplingWeight < .0005f) CouplingWeight = 0f;
        if (CouplingWeight <= 0f || !_hasReach) { _logReach = targetLog; _hasReach = true; }
        else _logReach = Smooth(_logReach, targetLog, dt, targetLog > _logReach ? ReachGrowSeconds : ReachShrinkSeconds);
        ReachMeters = MathF.Exp(_logReach);
        VisibleDistanceMeters = VisibleDistance(ReachMeters, fraction);
        Density = DensityForVisibleDistance(VisibleDistanceMeters);
    }

    private static float Smooth(float current, float target, float dt, float tau) =>
        target + (current - target) * MathF.Exp(-dt / tau);
}
