using System;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>Every constant of Spec E2 and E3 (docs/work-orders/terrain-vegetation/T3.md). <see cref="Default"/> is the reset's starting set; <see cref="Interim"/> reproduces the T2R-6b law.</summary>
public sealed record RiverDangerConstants
{
    /// <summary>E2: depth coefficient Kd (d = max(1.2, channel_depth Kd Q^0.4)).</summary>
    public double Kd { get; init; } = 0.34;
    public double MinDepth { get; init; } = 1.2;
    public double MinWidth { get; init; } = 8d;
    /// <summary>E2: roughness at the lowest gradient Np, its limit Nm (the mountain roughness), the reference gradient Sa and the exponent Gn.</summary>
    public double Np { get; init; } = 0.05;
    public double Nm { get; init; } = 0.20;
    public double Sa { get; init; } = 0.0003;
    public double Gn { get; init; } = 0.4;
    /// <summary>E2: cap on the centre speed in pools (U0 is at most Vpool / 1.5 times ferocity).</summary>
    public double Vpool { get; init; } = 9d;
    public double Wmax { get; init; } = 400d;
    /// <summary>E3: chute length per metre of drop Lc, chute head per unit of span gradient Ld, speed share E0, reference depth Dref, chute speed cap Vchute.</summary>
    public double Lc { get; init; } = RiverLevel.ChuteMeters;
    public double Ld { get; init; } = 30d;
    public double E0 { get; init; } = 0.65;
    public double Dref { get; init; } = 2.2;
    public double Vchute { get; init; } = 12d;
    /// <summary>Report only: use the reach's grid gradient instead of the window gradient Sh (the interim law).</summary>
    public bool UseReachGradient { get; init; }

    public static RiverDangerConstants Default { get; } = new();

    /// <summary>The T2R-6b law: n 0.035 at every gradient, depth coefficient 0.27, centre cap 15 m/s (U0 at most 10), no width limit, no chute term, reach gradient.</summary>
    public static RiverDangerConstants Interim { get; } = new() { Kd = 0.27, Np = 0.035, Nm = 0.035, Vpool = 15d, Wmax = double.PositiveInfinity, Lc = 0d, UseReachGradient = true };
}

/// <summary>d, w and U of a span (E2) and the pool centre speed.</summary>
public readonly record struct RiverDangerChannel(double Depth, double Width, double Speed);

/// <summary>
/// T3R-1: the danger law of Spec E2 and E3 as pure statics, with no Godot types. T3R-3 wires the same formulas into the bake.
/// </summary>
public static class RiverDanger
{
    public const double Gravity = 9.81;
    public const double CalmLimit = 1.5, SwiftLimit = 4d, FierceLimit = 7d;

    /// <summary>E2: Manning roughness n = L clamp(Np (Sh / Sa)^Gn, Np, Nm) / ferocity, where L is the landing factor.</summary>
    public static double Roughness(RiverDangerConstants c, double sh, double ferocity, double landing = 1d)
        => landing * Math.Clamp(c.Np * Math.Pow(sh / c.Sa, c.Gn), Math.Min(c.Np, c.Nm), Math.Max(c.Np, c.Nm)) / ferocity;

    /// <summary>E2: depth, width and mean speed from Q and the window gradient <paramref name="sh"/>. <c>Q = U w d</c> holds exactly.</summary>
    public static RiverDangerChannel Channel(RiverDangerConstants c, double q, double sh, double channelDepth, double ferocity, double landing = 1d)
    {
        sh = Math.Max(sh, 1e-4);
        double d = Math.Max(c.MinDepth, channelDepth * c.Kd * Math.Pow(q, 0.4));
        double n = Roughness(c, sh, ferocity, landing);
        double u0 = Math.Min(Math.Pow(d, 2d / 3d) * Math.Sqrt(sh) / n, c.Vpool / 1.5 * ferocity);
        double raw = q / (u0 * d);
        double w = Math.Clamp(raw, c.MinWidth, c.Wmax);
        if (raw > c.Wmax) d = q / (u0 * c.Wmax);
        return new RiverDangerChannel(d, w, q / (w * d));
    }

    /// <summary>E3: the danger speed, the length-weighted mean of the centre speed over pools (1.5 U) and chutes. <paramref name="sr"/> is the span's own gradient.</summary>
    public static double DangerSpeed(RiverDangerConstants c, double u, double d, double sr, double ferocity, double landing = 1d)
    {
        double vp = 1.5 * u;
        double chute = Math.Min(1d, c.Lc * sr);
        double head = Math.Max(1d, c.Ld * sr);
        double eta = c.E0 / landing * Math.Clamp(d / c.Dref, 0.5, 1.6);
        double vc = Math.Max(vp, Math.Min(Math.Sqrt(vp * vp + 2d * Gravity * eta * head), c.Vchute * ferocity));
        return (1d - chute) * vp + chute * vc;
    }

    /// <summary>E3: calm under 1.5 m/s, swift under 4, fierce under 7, torrent above.</summary>
    public static RiverClass Classify(double dangerSpeed) => dangerSpeed < CalmLimit ? RiverClass.Calm : dangerSpeed < SwiftLimit ? RiverClass.Swift : dangerSpeed < FierceLimit ? RiverClass.Fierce : RiverClass.Torrent;

    /// <summary>E3: whiteness of a span, smoothstep(2.5, 7, Vd).</summary>
    public static double Whiteness(double dangerSpeed) => RiverHydraulics.Smoothstep(2.5, 7d, dangerSpeed);
}
