using System;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>Canonical state of one floating body: position (around, across, height above the ring floor datum), velocity in m/s, yaw and pitch in radians.</summary>
public struct BuoyancyState
{
    public double Around, Across, Height;
    public double VAround, VAcross, VHeight;
    /// <summary>Heading in the around/across plane: atan2(across, around), so 0 faces downstream-around.</summary>
    public double Yaw;
    /// <summary>Positive is nose up.</summary>
    public double Pitch;
}

/// <summary>The per-body constants of the step (T3.md Spec F2).</summary>
public struct BuoyancyParams
{
    /// <summary>How far below the surface the body's origin floats, in metres.</summary>
    public double Draft;
    /// <summary>Horizontal relaxation rate toward the water velocity, per second.</summary>
    public double Drag;
    public bool MayStrand;
    /// <summary>1 for the lift spring; a negative value reverses it. Test hook for the negative control only.</summary>
    public double LiftSign;

    public static BuoyancyParams Create(double draft, double drag = RingBuoyancy.DefaultDrag, bool mayStrand = true)
        => new() { Draft = draft, Drag = drag, MayStrand = mayStrand, LiftSign = 1.0 };
}

/// <summary>What the step needs from the world at the body: water level (NaN when dry), mean water velocity, ground height, the unit direction toward deeper water and the surface slope along the heading.</summary>
public struct BuoyancyInput
{
    public double Surface;
    public double WaterAround, WaterAcross;
    public double Ground;
    public double DeeperAround, DeeperAcross;
    public double SurfaceSlope;
}

/// <summary>
/// The kinematic floating step (T3.md Spec F2): a critically damped lift spring toward surface minus draft, horizontal relaxation toward the water velocity, deceleration or a push
/// in water shallower than the draft, and a heading that follows the flow. Doubles only, no engine types, no allocation.
/// </summary>
public static class RingBuoyancy
{
    public const double SpringPeriodSeconds = 1.2;
    public const double DefaultDrag = 1.0 / 1.5;
    public const double MaxStepSeconds = 1.0 / 30.0;
    public const double GroundClearanceMeters = 0.05;
    public const double StrandDecelMetersPerSecond2 = 3.0;
    public const double StrandPushMetersPerSecond = 0.5;
    public const double OutOfWaterSeconds = 0.5;
    public const double YawRateRadiansPerSecond = 0.5;
    public const double YawFlowSpeed = 0.3;
    private const double PitchRatePerSecond = 4.0;
    private const double OutOfWaterRate = 9.2; // exp(-9.2 * 0.5 s) = 1 %

    /// <summary>Advances the state by dt seconds with the inputs held fixed. Steps longer than MaxStepSeconds are split.</summary>
    public static void Step(ref BuoyancyState s, in BuoyancyParams p, in BuoyancyInput input, double dt)
    {
        if (!(dt > 0.0)) return;
        int n = Math.Max(1, (int)Math.Ceiling(dt / MaxStepSeconds - 1e-9));
        double h = dt / n;
        for (int i = 0; i < n; i++) SubStep(ref s, p, input, h);
    }

    private static void SubStep(ref BuoyancyState s, in BuoyancyParams p, in BuoyancyInput input, double h)
    {
        bool wet = !double.IsNaN(input.Surface) && input.Surface > input.Ground;
        double depth = wet ? input.Surface - input.Ground : 0.0;
        double draft = Math.Max(p.Draft, 1e-3);
        bool shallow = wet && depth < draft;
        double va0 = s.VAround, vc0 = s.VAcross;

        if (wet)
        {
            double ta = input.WaterAround, tc = input.WaterAcross;
            if (shallow && !p.MayStrand)
            {
                ta += StrandPushMetersPerSecond * input.DeeperAround;
                tc += StrandPushMetersPerSecond * input.DeeperAcross;
            }
            double k = 1.0 - Math.Exp(-h * p.Drag);
            s.VAround += (ta - s.VAround) * k;
            s.VAcross += (tc - s.VAcross) * k;
            if (shallow && p.MayStrand)
            {
                double speed = Math.Sqrt(s.VAround * s.VAround + s.VAcross * s.VAcross);
                if (speed > 0.0)
                {
                    double scale = Math.Max(0.0, speed - StrandDecelMetersPerSecond2 * (1.0 - depth / draft) * h) / speed;
                    s.VAround *= scale;
                    s.VAcross *= scale;
                }
            }
        }
        else
        {
            double f = Math.Exp(-h * OutOfWaterRate);
            s.VAround *= f;
            s.VAcross *= f;
            if (s.VAround * s.VAround + s.VAcross * s.VAcross < 1e-4) { s.VAround = 0.0; s.VAcross = 0.0; }
        }
        s.Around += 0.5 * (va0 + s.VAround) * h;
        s.Across += 0.5 * (vc0 + s.VAcross) * h;

        double floor = input.Ground + GroundClearanceMeters;
        double target = wet ? Math.Max(input.Surface - draft, floor) : floor;
        double x = s.Height - target;
        double w = 2.0 * Math.PI / SpringPeriodSeconds;
        if (p.LiftSign >= 0.0)
        {
            // Exact critically damped solution for a fixed target, so the result does not depend on the step size.
            double e = Math.Exp(-w * h);
            double c = s.VHeight + w * x;
            s.Height = target + (x + c * h) * e;
            s.VHeight = (s.VHeight - w * c * h) * e;
        }
        else
        {
            s.VHeight += w * w * x * h;
            s.Height += s.VHeight * h;
        }
        if (s.Height < floor)
        {
            s.Height = floor;
            if (s.VHeight < 0.0) s.VHeight = 0.0;
        }

        double water = Math.Sqrt(input.WaterAround * input.WaterAround + input.WaterAcross * input.WaterAcross);
        if (wet && water > YawFlowSpeed)
        {
            double diff = Math.Atan2(input.WaterAcross, input.WaterAround) - s.Yaw;
            diff = Math.IEEERemainder(diff, 2.0 * Math.PI);
            double turn = YawRateRadiansPerSecond * h;
            s.Yaw += Math.Clamp(diff, -turn, turn);
            s.Yaw = Math.IEEERemainder(s.Yaw, 2.0 * Math.PI);
        }
        double pitchTarget = wet ? Math.Atan(input.SurfaceSlope) : 0.0;
        s.Pitch += (pitchTarget - s.Pitch) * (1.0 - Math.Exp(-h * PitchRatePerSecond));
    }
}
