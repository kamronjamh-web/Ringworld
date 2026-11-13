using System;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>Speed class of a span or reach: calm, swift, fierce, torrent (T3.md Spec E3, bands on the danger speed; the path section uses the interim bands on 1.5 U).</summary>
public enum RiverClass : byte { Calm, Swift, Fierce, Torrent }

/// <summary>Discharge, gradient, depth, width and speed of one reach. <c>Q = Speed * Width * Depth</c> holds exactly.</summary>
public readonly record struct RiverSection(double Q, double Gradient, double Depth, double Width, double Speed, RiverClass Class);

/// <summary>
/// S6 (docs/work-orders/terrain-vegetation/T2.md): plan 3.2 relations with the T0 constants, a runoff scale of 20x and no floors (T2R-20 lifted D6), as pure statics of the
/// constants and the recipe fields. The bake stores Q and S per reach; everything else is derived here. All constants are starting values (T3-3 recalibrates).
/// </summary>
public static class RiverHydraulics
{
    /// <summary>q_s: discharge per unit of accumulated runoff, m^3/s per km^2 (20 x the T0 0.02).</summary>
    public const double DischargePerKm2 = 0.40;
    public const double DepthCoefficient = 0.27;
    public const double DepthExponent = 0.4;
    public const double ManningBase = 0.035;
    public const double SpeedCap = 10.0;
    public const double MinGradient = 1e-4;
    public const double Freeboard = 0.4;
    public const double LevelIncision = 0.6;
    public const double MaxFloodplainReach = 220.0;
    public const double WanderAmplitudeMeters = 250.0;
    public const double MeanderAmplitudeCapMeters = 100.0;
    public const double BlendMeters = 200.0;
    /// <summary>T2R-21 (RS4): a source stem grows from a point to its mapped section over this length. Provisional until the user has seen the head pictures.</summary>
    public const double HeadTaperMeters = 1500.0;

    public static double Smoothstep(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / (e1 - e0), 0d, 1d);
        return t * t * (3d - 2d * t);
    }

    public static double Discharge(double accumulatedRunoff) => DischargePerKm2 * accumulatedRunoff;

    /// <summary>The path section of a reach (T3R-3; the interim S6 law, formerly <c>Section</c>): d, w, U and the class from Q and the reach gradient S. It feeds the wander, the meander and the confluence tails; the channel is the span's (<see cref="RiverDanger"/>). <paramref name="channelDepth"/> and <paramref name="ferocity"/> are the recipe fields.</summary>
    public static RiverSection PathSection(double q, double gradient, double channelDepth, double ferocity)
    {
        double s = Math.Max(gradient, MinGradient);
        if (q <= 0d) return new RiverSection(0d, s, 0d, 0d, 0d, RiverClass.Calm);
        double d = channelDepth * DepthCoefficient * Math.Pow(q, DepthExponent);
        double n = ManningBase / ferocity;
        double manning = Math.Pow(d, 2d / 3d) * Math.Sqrt(s) / n;
        double u0 = Math.Min(manning, SpeedCap * ferocity);
        double w = q / (u0 * d);
        double u = q / (w * d);
        double centre = 1.5 * u;
        RiverClass cls = centre < 1.5 ? RiverClass.Calm : centre < 4 ? RiverClass.Swift : centre < 7 ? RiverClass.Fierce : RiverClass.Torrent;
        return new RiverSection(q, s, d, w, u, cls);
    }

    /// <summary>The Manning speed U_M and the cap U_0 of <see cref="Section"/>, for the relation checks.</summary>
    public static (double Manning, double Cap) SpeedLimits(double depth, double gradient, double ferocity)
        => (Math.Pow(depth, 2d / 3d) * Math.Sqrt(Math.Max(gradient, MinGradient)) / (ManningBase / ferocity), SpeedCap * ferocity);

    public static double HalfWidth(double w) => 0.5 * w;
    public static double BankWidth(double w) => Math.Clamp(0.25 * w, 2d, 12d);
    public static double EdgeDepth(double d) => Math.Min(0.3, 0.25 * d);

    /// <summary>Floodplain width beyond the bank, F = vw * clamp(2 w sqrt(0.002 / S), 10, 120), at most 220 - a - b.</summary>
    public static double FloodplainWidth(double w, double gradient, double valleyWidth)
    {
        double f = valleyWidth * Math.Clamp(2d * w * Math.Sqrt(0.002 / Math.Max(gradient, MinGradient)), 10d, 120d);
        return Math.Max(0d, Math.Min(f, MaxFloodplainReach - HalfWidth(w) - BankWidth(w)));
    }

    public static double ValleySlope(double gradient, double valleyWidth) => Math.Clamp(0.3 + 10d * gradient, 0.3, 2.0) / valleyWidth;

    /// <summary>Tuned in T2R-5c: small streams get a quarter of the amplitude (full from w = 32 m), so the 8 m streams show no regular wiggle.</summary>
    public static double MeanderSizeScale(double w) => Math.Clamp((w - 8d) / 24d, 0.25, 1d);

    public static double MeanderAmplitude(double w, double meanderStrength, double gradient)
        => Math.Min(2d * w * meanderStrength * (1d - Smoothstep(0.005, 0.03, gradient)), MeanderAmplitudeCapMeters) * MeanderSizeScale(w);

    /// <summary>Tuned in T2R-5c: floor 300 m (was 60 m).</summary>
    public static double MeanderWavelength(double w) => Math.Max(12d * w, 300d);

    public static double WanderAmplitude(double meanderStrength, double gradient, double scale = 1d)
        => Math.Min(WanderAmplitudeMeters, WanderAmplitudeMeters * scale * meanderStrength * (1d - 0.7 * Smoothstep(0.001, 0.02, gradient)));
}
