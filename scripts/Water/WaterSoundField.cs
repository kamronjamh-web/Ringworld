using System;

namespace RingworldTechDemo.Water;

/// <summary>Kinds of water sound source (docs/work-orders/terrain-vegetation/T3.md, Spec W4).</summary>
public enum WaterSoundKind { River, Drop, SeaShore, LakeShore, OpenSea, OpenLake }

/// <summary>
/// One water sound source as the finder hands it over (Spec W4). Around, Vertical and Across are the canonical position of the nearest point of the source's water surface;
/// Distance is W1's straight distance from the listener to it. Shield is W8's smoothstep value from 0 (clear) to 1 (full ridge in the way).
/// </summary>
public readonly record struct WaterSoundSource(WaterSoundKind Kind, double Power, double HalfSize,
    double Around, double Vertical, double Across,
    double Distance,
    double WaveHeight, double Discharge, double Drop, double Shield, int Stem);

/// <summary>Weights of the loops a source plays, in the order of Spec W5: shore lap, breakers, far surf; river babble, rush, roar (plus waterfall for a drop); open and under water A = 1. Equal power: squares sum to 1.</summary>
public readonly record struct WaterLoopWeights(double A, double B, double C, double D);

/// <summary>What the node needs per source per frame (Spec W2, W5, W8).</summary>
public readonly record struct WaterSoundMix(double Level, double Audibility, double GainDb, double Gain, double CutoffHz, WaterLoopWeights Weights);

/// <summary>Every constant of Spec W2, W3, W5, W6 and W8 in one place. Marked "(tune at review)" are the ones the lead and the user may move by ear.</summary>
public sealed record WaterSoundConstants
{
    /// <summary>W3: rho * g in N/m^3.</summary>
    public double RhoG { get; init; } = 9810.0;
    /// <summary>W3: gravity in m/s^2 (Froude number, wave celerity).</summary>
    public double Gravity { get; init; } = 9.81;
    /// <summary>W2: P0, the line power that is just heard at a + x = 1 m, in W/m <b>(tune at review)</b>.</summary>
    public double LinePowerRef { get; init; } = 0.011;
    /// <summary>W2: P1, the point power that is just heard at a + x = 1 m, in W <b>(tune at review)</b>.</summary>
    public double PointPowerRef { get; init; } = 0.03;
    /// <summary>W2: A, what air and ground take, in dB per metre <b>(tune at review)</b>.</summary>
    public double AirLossDbPerMeter { get; init; } = 0.02;
    /// <summary>W2: spreading of a line of sources, dB per decade of a + x.</summary>
    public double LineSpreadDb { get; init; } = 15.0;
    /// <summary>W2: spreading of a point, dB per decade of a + x.</summary>
    public double PointSpreadDb { get; init; } = 20.0;
    /// <summary>W3: the share of a river's power that unbroken riffles still make audible. It multiplies a power; it is not a minimum <b>(tune at review)</b>.</summary>
    public double RiffleShare { get; init; } = 0.15;
    /// <summary>W3: shore half size is base + per-metre * Hs.</summary>
    public double ShoreHalfSizeBase { get; init; } = 2.0;
    /// <summary>W3: see <see cref="ShoreHalfSizeBase"/>.</summary>
    public double ShoreHalfSizePerMeter { get; init; } = 20.0;
    /// <summary>W3: the 1/8 of the shore's wave power per metre.</summary>
    public double ShoreEnergyFactor { get; init; } = 0.125;
    /// <summary>W3: the 0.8 under the root of the shore's wave celerity.</summary>
    public double ShoreCelerityDivisor { get; init; } = 0.8;
    /// <summary>W3: the 0.001 of open water's power <b>(tune at review)</b>.</summary>
    public double OpenWaterFactor { get; init; } = 0.001;
    /// <summary>W3: open water's half size in metres.</summary>
    public double OpenWaterHalfSize { get; init; } = 3.0;
    /// <summary>W2: the level at which a source plays at full scale, dB. Anything louder is not raised.</summary>
    public double GainFullScaleDb { get; init; } = 45.0;
    /// <summary>W2: width in dB of the smoothstep from silence to heard.</summary>
    public double AudibilityWidthDb { get; init; } = 6.0;
    /// <summary>W2: low-pass at 10 m, Hz.</summary>
    public double CutoffBaseHz { get; init; } = 18000.0;
    /// <summary>W2: distance below which the low-pass is open, and the reference of the root.</summary>
    public double CutoffReferenceMeters { get; init; } = 10.0;
    /// <summary>W2: lowest cutoff.</summary>
    public double CutoffMinHz { get; init; } = 800.0;
    /// <summary>W2: open cutoff.</summary>
    public double CutoffMaxHz { get; init; } = 20000.0;
    /// <summary>W6: rain masker at intensity 1, dB <b>(tune at review)</b>.</summary>
    public double MaskRainDb { get; init; } = 35.0;
    /// <summary>W6: wind masker, dB per decade of wind over the reference <b>(tune at review)</b>.</summary>
    public double MaskWindDb { get; init; } = 40.0;
    /// <summary>W6: wind speed in m/s below which wind masks nothing <b>(tune at review)</b>.</summary>
    public double MaskWindRefSpeed { get; init; } = 4.0;
    /// <summary>W6: the listener's own air rush, dB per decade of speed over the reference <b>(tune at review)</b>.</summary>
    public double MaskSpeedDb { get; init; } = 40.0;
    /// <summary>W6: listener speed in m/s below which the rush masks nothing (sprint is 14) <b>(tune at review)</b>.</summary>
    public double MaskSpeedRefSpeed { get; init; } = 20.0;
    /// <summary>W5: wave height where the lap starts to give way to breakers, m.</summary>
    public double ShoreLapHs { get; init; } = 0.15;
    /// <summary>W5: wave height where breakers have it all, m.</summary>
    public double ShoreBreakHs { get; init; } = 0.5;
    /// <summary>W5: distance where breakers start to give way to far surf, m.</summary>
    public double SurfNearMeters { get; init; } = 60.0;
    /// <summary>W5: distance where far surf has it all, m.</summary>
    public double SurfFarMeters { get; init; } = 250.0;
    /// <summary>W5: discharge with all babble, m^3/s <b>(tune at review)</b>.</summary>
    public double RiverBabbleQ { get; init; } = 1.0;
    /// <summary>W5: discharge with all rush, m^3/s <b>(tune at review)</b>.</summary>
    public double RiverRushQ { get; init; } = 20.0;
    /// <summary>W5: discharge with all roar, m^3/s <b>(tune at review)</b>.</summary>
    public double RiverRoarQ { get; init; } = 200.0;
    /// <summary>W5: drop height where the waterfall loop starts, m.</summary>
    public double FallStartDrop { get; init; } = 1.5;
    /// <summary>W5: drop height where the waterfall loop has it all, m.</summary>
    public double FallFullDrop { get; init; } = 4.0;
    /// <summary>W8: dB taken off the level by a full shield <b>(tune at review)</b>.</summary>
    public double ShieldDb { get; init; } = 20.0;
    /// <summary>W8: the cutoff's multiplier at a full shield <b>(tune at review)</b>.</summary>
    public double ShieldCutoffScale { get; init; } = 0.35;

    public static WaterSoundConstants Default { get; } = new();
}

/// <summary>
/// The arithmetic of water sound and nothing else (Spec W1 to W3, W5, W6, W8): no node, no asset, no world. Nothing here raises a level to make it heard;
/// a power of 0 is silence (level negative infinity). Checked by water_sound_field_test.
/// </summary>
public static class WaterSoundField
{
    private static WaterSoundConstants K => WaterSoundConstants.Default;

    /// <summary>W1: straight distance in metres to the nearest point of the water surface. Height is distance.</summary>
    public static double Distance(double horizontal, double vertical) => Math.Sqrt(horizontal * horizontal + vertical * vertical);

    // ------------------------------------------------------------------ W3 powers

    /// <summary>W3 river: P = rho g Q Sr * min(1, Fr^2) * (0.15 + 0.85 whiteness), watts per metre of channel. speed U and depth d give Fr = U / sqrt(g d).</summary>
    public static double RiverPower(double q, double sr, double speed, double depth, double whiteness)
    {
        if (q <= 0.0 || sr <= 0.0 || speed <= 0.0 || depth <= 0.0) return 0.0;
        double fr2 = speed * speed / (K.Gravity * depth);
        double w = Math.Clamp(whiteness, 0.0, 1.0);
        return K.RhoG * q * sr * Math.Min(1.0, fr2) * (K.RiffleShare + (1.0 - K.RiffleShare) * w);
    }

    /// <summary>W3 chute or fall: W = rho g Q h, watts.</summary>
    public static double DropPower(double q, double drop) => q <= 0.0 || drop <= 0.0 ? 0.0 : K.RhoG * q * drop;

    /// <summary>W3 shore: P = (1/8) rho g Hs^2 sqrt(g Hs / 0.8), watts per metre of shore.</summary>
    public static double ShorePower(double hs)
        => hs <= 0.0 ? 0.0 : K.ShoreEnergyFactor * K.RhoG * hs * hs * Math.Sqrt(K.Gravity * hs / K.ShoreCelerityDivisor);

    /// <summary>W3 open water: P = 0.001 rho g Hs^2, watts per metre.</summary>
    public static double OpenWaterPower(double hs) => hs <= 0.0 ? 0.0 : K.OpenWaterFactor * K.RhoG * hs * hs;

    /// <summary>W3 shore half size: a = 2 + 20 Hs, metres.</summary>
    public static double ShoreHalfSize(double hs) => K.ShoreHalfSizeBase + K.ShoreHalfSizePerMeter * Math.Max(0.0, hs);

    // ------------------------------------------------------------------ W2 law

    /// <summary>W2: level in dB above a quiet day's background. A line (every kind but Drop): 10 log10(P/P0) - 15 log10(a + x) - A x; a point (Drop): 10 log10(W/P1) - 20 log10(a + x) - A x. Negative infinity for a power of 0.</summary>
    public static double Level(WaterSoundKind kind, double power, double halfSize, double distance)
    {
        if (!(power > 0.0)) return double.NegativeInfinity;
        double x = Math.Max(0.0, distance);
        double reach = Math.Max(1e-6, halfSize) + x;
        bool point = kind == WaterSoundKind.Drop;
        double reference = point ? K.PointPowerRef : K.LinePowerRef;
        double spread = point ? K.PointSpreadDb : K.LineSpreadDb;
        return 10.0 * Math.Log10(power / reference) - spread * Math.Log10(reach) - K.AirLossDbPerMeter * x;
    }

    /// <summary>W2: u = smoothstep(M, M + 6, L): 0 while L is at or under the masker.</summary>
    public static double Audibility(double level, double masker) => SmoothStep(masker, masker + K.AudibilityWidthDb, level);

    /// <summary>W2: the players' gain in dB before the volume rows and audibility: min(0, L - 45). One dB of level is one dB of gain.</summary>
    public static double GainDb(double level) => Math.Min(0.0, level - K.GainFullScaleDb);

    /// <summary>W2: low-pass cutoff in Hz: open (20 kHz) up to 10 m, then 18 kHz * sqrt(10 / x) between 800 Hz and 20 kHz.</summary>
    public static double CutoffHz(double distance)
        => distance <= K.CutoffReferenceMeters
            ? K.CutoffMaxHz
            : Math.Clamp(K.CutoffBaseHz * Math.Sqrt(K.CutoffReferenceMeters / distance), K.CutoffMinHz, K.CutoffMaxHz);

    // ------------------------------------------------------------------ W6 masker

    /// <summary>W6: M = max(35 sqrt(rain), 40 log10(max(1, wind / 4)), 40 log10(max(1, speed / 20))), dB. rain 0 to 1, wind and listener speed in m/s.</summary>
    public static double Masker(double rain, double wind, double listenerSpeed)
    {
        double mRain = K.MaskRainDb * Math.Sqrt(Math.Clamp(rain, 0.0, 1.0));
        double mWind = K.MaskWindDb * Math.Log10(Math.Max(1.0, wind / K.MaskWindRefSpeed));
        double mSpeed = K.MaskSpeedDb * Math.Log10(Math.Max(1.0, listenerSpeed / K.MaskSpeedRefSpeed));
        return Math.Max(mRain, Math.Max(mWind, mSpeed));
    }

    // ------------------------------------------------------------------ W5 loop weights (equal power)

    /// <summary>W5 shore: lap to breakers by Hs (0.15 m to 0.5 m), breakers to far surf by distance (60 m to 250 m). A = lap, B = breakers, C = far surf.</summary>
    public static WaterLoopWeights ShoreWeights(double hs, double distance)
    {
        double t1 = SmoothStep(K.ShoreLapHs, K.ShoreBreakHs, hs) * (Math.PI / 2.0);
        double t2 = SmoothStep(K.SurfNearMeters, K.SurfFarMeters, distance) * (Math.PI / 2.0);
        double s1 = Math.Sin(t1);
        return new WaterLoopWeights(Math.Cos(t1), s1 * Math.Cos(t2), s1 * Math.Sin(t2), 0.0);
    }

    /// <summary>W5 river: by Q on a log scale, babble at 1 m^3/s and under, rush at 20, roar at 200 and over. A = babble, B = rush, C = roar.</summary>
    public static WaterLoopWeights RiverWeights(double q)
    {
        double lq = Math.Log10(Math.Max(q, 1e-12));
        double l0 = Math.Log10(K.RiverBabbleQ), l1 = Math.Log10(K.RiverRushQ), l2 = Math.Log10(K.RiverRoarQ);
        if (lq <= l1)
        {
            double t = Math.Clamp((lq - l0) / (l1 - l0), 0.0, 1.0) * (Math.PI / 2.0);
            return new WaterLoopWeights(Math.Cos(t), Math.Sin(t), 0.0, 0.0);
        }
        double u = Math.Clamp((lq - l1) / (l2 - l1), 0.0, 1.0) * (Math.PI / 2.0);
        return new WaterLoopWeights(0.0, Math.Cos(u), Math.Sin(u), 0.0);
    }

    /// <summary>W5 drop: the river loops by Q (A, B, C), crossfaded to the waterfall loop (D) by the drop's height, 1.5 m to 4 m.</summary>
    public static WaterLoopWeights DropWeights(double q, double drop)
    {
        double t = SmoothStep(K.FallStartDrop, K.FallFullDrop, drop) * (Math.PI / 2.0);
        double river = Math.Cos(t);
        WaterLoopWeights r = RiverWeights(q);
        return new WaterLoopWeights(r.A * river, r.B * river, r.C * river, Math.Sin(t));
    }

    // ------------------------------------------------------------------ the node's one call

    /// <summary>
    /// The one call the node makes per source: level (W2, less W8's shield), audibility against the masker, gain (dB and linear, audibility applied), cutoff (W2, less the shield) and the loop weights (W5).
    /// </summary>
    public static WaterSoundMix Mix(in WaterSoundSource source, double masker)
    {
        double shield = Math.Clamp(source.Shield, 0.0, 1.0);
        double level = Level(source.Kind, source.Power, source.HalfSize, source.Distance) - K.ShieldDb * shield;
        double u = Audibility(level, masker);
        double gainDb = GainDb(level);
        double gain = u > 0.0 ? u * Math.Pow(10.0, gainDb / 20.0) : 0.0;
        double cutoff = CutoffHz(source.Distance) * (1.0 + (K.ShieldCutoffScale - 1.0) * shield);
        WaterLoopWeights weights = source.Kind switch
        {
            WaterSoundKind.SeaShore or WaterSoundKind.LakeShore => ShoreWeights(source.WaveHeight, source.Distance),
            WaterSoundKind.River => RiverWeights(source.Discharge),
            WaterSoundKind.Drop => DropWeights(source.Discharge, source.Drop),
            _ => new WaterLoopWeights(1.0, 0.0, 0.0, 0.0),
        };
        return new WaterSoundMix(level, u, gainDb, gain, cutoff, weights);
    }

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        if (double.IsNegativeInfinity(x)) return 0.0;
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }
}
