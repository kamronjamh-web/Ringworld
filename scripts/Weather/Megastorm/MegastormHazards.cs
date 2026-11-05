using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo;

/// <summary>Blast shelter class of a position (docs/megastorm-design.md 5.5.1): open ground, the lee of terrain or a wall, or inside an enclosed structure.</summary>
public enum MegastormShelter { Open, Lee, Enclosed }

/// <summary>Observer-side inputs the pure query cannot know: the shelter class and the wind-aligned rain exposure (0..1, 5.5.2 cover probe).</summary>
public readonly record struct MegastormObserver(MegastormShelter Shelter, float RainExposure)
{
    public static MegastormObserver Open => new(MegastormShelter.Open, 1.0f);
}

/// <summary>The boom passage at one position (5.5.1): open-air peak and impulse, and the weather-clock time the peak hits (the audio envelope's peak).</summary>
public readonly record struct MegastormBoom(float PeakKPa, float ImpulseKPaS, double TimeSeconds, double CrossingTimeSeconds)
{
    public bool Exists => PeakKPa > 0.0f;
}

/// <summary>Hazard values of one point query (docs/megastorm-design.md 5.5). All 0 when the megastorm is not active and outside the hazard zones.</summary>
public readonly record struct MegastormHazards(float OverpressureKPa, float BoomPeakKPa, float BoomImpulseKPaS, double BoomTimeSeconds,
    float RainImpactPowerWm2, Vector3 RainImpactDirection /* ring frame (around, up, across), unit, the direction the drops travel */, float RainDropSpeedMs,
    float RainExposure /* observer only; 1 for a point query without observer context */,
    float PressureDeficitKPa, float PressureRateKPaPerS, float WakeFog, float LocalStrikeRatePerSecond, float ScytheSweepRatePerSecond,
    float NearestScytheDistanceMeters /* ground-plane distance to the nearest live scythe (WO12); +Infinity when none; NaN only in None */, float FieldStrikeRatePerSecond, float FieldGapMeters /* NaN without a field */)
{
    public static MegastormHazards None => new(0.0f, 0.0f, 0.0f, 0.0, 0.0f, Vector3.Zero, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, float.NaN, 0.0f, float.NaN);
}

/// <summary>Observer boom pass (5.5.1 event): edge-triggered when the observer crosses xi = 0.</summary>
public readonly record struct MegastormBoomEvent(long EpisodeId, RingCanonicalPosition Position, float PeakKPa, float ImpulseKPaS, double TimeSeconds);

/// <summary>A player-built structure a future building system registers (5.5 structure hook). Nothing is damaged or removed by the megastorm.</summary>
public readonly record struct MegastormStructure(long Id, RingCanonicalPosition Base, float HeightAboveGroundMeters,
    float RatedOverpressureKPa /* "viable" when >= the local peak */, float MaintenanceFactor /* 0 neglected .. 1 well kept */, bool Enclosed);

/// <summary>Registered by a future building / survival system; null today. The megastorm only publishes values and events.</summary>
public interface IMegastormStructureSource
{
    int CollectNear(RingCanonicalPosition centre, double radiusMeters, Span<MegastormStructure> output);
}

/// <summary>Optional second interface of a megastorm weather source: the exact in-wall and wake wind (5.5.2 / 5.5.3), used by the wind path in place of the approximate arrival-state xi.</summary>
public interface IMegastormHazardSource
{
    /// <summary>Wind override (around, across) at a point; <paramref name="weight"/> 0 leaves the ordinary wind untouched.</summary>
    bool TryGetWindOverride(double around, double across, double t, out Vector2 meanAroundAcross, out float weight);
}

/// <summary>
/// Pure hazard maths (docs/megastorm-design.md 5.5, WO10): no nodes, no state. The boom overpressure is the SAME envelope as the audio
/// (<see cref="MegastormSound.BoomEnvelope"/>), so the overpressure peak and the first boom pulse coincide by construction.
/// </summary>
public static class MegastormHazardMath
{
    // ---- Constants (draft values decided 2026-10-29) ----
    public const float BoomOpenPeakKPa = 60.0f, BoomLeeFactor = 0.6f, BoomEnclosedFactor = 0.25f, BoomLethalKPa = 50.0f, BoomVariation = 0.15f;
    public const float RatedGlassKPa = 7.0f, RatedConcreteKPa = 25.0f, RatedReinforcedKPa = 80.0f, BoomPositivePhaseSeconds = 0.6f;
    public const double BoomZoneAheadMeters = 100000.0, BoomZoneBehindMeters = 50000.0;

    public const float GustOutflowMs = 41.0f, WallWindPeakMs = 140.0f, WakeBackwardPeakMs = 60.0f, WakeForwardPeakMs = 50.0f, WakeTroughKPa = 8.0f;
    public const double WindRampEndXi = 2000.0, WindHoldEndXi = 15000.0, WallWindEndXi = 25000.0, WakeBackwardPeakXi = 30000.0, WakeTroughXi = 35000.0,
        WakeForwardPeakXi = 45000.0, WakeForwardHoldEndXi = 50000.0, WakeEndXi = 60000.0;
    public const float WallWindEndMs = 40.0f;

    public const float DropFallSpeedMs = 9.0f, RainRateWallMmPerHour = 200.0f, RainRateTransitionMmPerHour = 60.0f, LiquidWaterAtWallKgM3 = 0.008f;
    public const double RainZoneStartXi = -500.0;

    public const float ScytheRatePerSecond = 20.0f, ScytheLengthMeters = 3500.0f, ScytheWidthMeters = 125.0f;
    public const double ScytheZoneStartXi = -15000.0, ScytheZoneEndXi = 20000.0;

    private static double Smooth(double x) => MegastormModel.Smooth(x);

    // ---- Boom (5.5.1) ----
    /// <summary>Attenuation of the open-air peak: open 1, lee of terrain or a wall 0.6, enclosed 0.25.</summary>
    public static float ShelterFactor(MegastormShelter shelter) => shelter switch
    {
        MegastormShelter.Lee => BoomLeeFactor,
        MegastormShelter.Enclosed => BoomEnclosedFactor,
        _ => 1.0f
    };

    /// <summary>Seconds after the edge crossing at which the boom envelope peaks (found on the shared <see cref="MegastormSound.BoomEnvelope"/> shape).</summary>
    public static readonly double BoomPeakSeconds;
    /// <summary>The envelope's maximum at full boom gain (M &gt;= 1); the overpressure normalises by it so the open-air peak is exactly 60 kPa.</summary>
    public static readonly float BoomShapeMax;

    static MegastormHazardMath()
    {
        const double speed = MegastormSound.SpeedOfSoundMetersPerSecond;   // M = 1.0: boom gain 1
        double bestT = 0.0; float best = 0.0f;
        for (int i = 0; i <= 4000; i++)
        {
            double t = i * 0.001;
            float e = MegastormSound.BoomEnvelope(speed, speed * t);
            if (e > best) { best = e; bestT = t; }
        }
        BoomPeakSeconds = bestT;
        BoomShapeMax = best;
        ScytheProfileIntegral = IntegrateScytheProfile();
    }

    /// <summary>Instantaneous open-air overpressure for a cell factor: 60 kPa x cell x envelope / envelope peak. 0 ahead of the edge and after the boom window, 0 below M 0.94.</summary>
    public static float OverpressureKPa(double speedMetersPerSecond, double xi, float cellFactor)
        => BoomOpenPeakKPa * cellFactor * MegastormSound.BoomEnvelope(speedMetersPerSecond, xi) / BoomShapeMax;

    /// <summary>+-15% along the wall: 0.85 + 0.3 x the tower noise (strongest under the overshooting cells).</summary>
    public static float CellFactor(MegastormModel model, double across, double t)
        => (float)(1.0 - BoomVariation + 2.0 * BoomVariation * MegastormProfile.AcrossTerms(model, across, t).Tower);

    /// <summary>Weather-clock time at which the edge crosses this point (xi = 0), by Newton steps on the moving frame; the nearest crossing.</summary>
    public static double CrossingTime(MegastormModel model, double around, double across, double t)
    {
        double tc = t;
        for (int i = 0; i < 4; i++)
        {
            double v = Math.Max(1.0, model.SpeedAt(tc));
            tc -= model.Xi(around, across, tc) / v;
        }
        return tc;
    }

    /// <summary>The boom passage at a point (open air): peak, impulse (triangular positive phase, 0.5 x peak x 0.6 s) and the peak time. None when the speed is subsonic or the storm is not active at the crossing.</summary>
    public static MegastormBoom BoomAt(MegastormModel model, double around, double across, double t)
    {
        if (!model.Triggered) return default;
        double tc = CrossingTime(model, around, across, t);
        if (!model.Evaluate(tc).Active) return default;
        float gain = MegastormSound.BoomGain(model.SpeedAt(tc));
        float peak = BoomOpenPeakKPa * CellFactor(model, across, tc) * gain;
        if (peak <= 0.0f) return default;
        return new MegastormBoom(peak, 0.5f * peak * BoomPositivePhaseSeconds, tc + BoomPeakSeconds, tc);
    }

    // ---- Bullet rain wind (5.5.2) and the vacuum wake wind (5.5.3) ----
    /// <summary>
    /// Surface (10 m) wind along the storm's direction of travel at xi metres behind the edge (negative = backward): the 41 m/s gust-front outflow at the edge,
    /// ramping to 140 m/s by xi 2 km, held to 15 km, easing to 40 m/s by 25 km; then the wake: reversing through zero to -60 m/s at 30 km, back to calm at the
    /// trough (35 km), then inflow toward the departing wall (50 m/s from 45 to 50 km) fading to 0 by 60 km. Ahead of the edge it is the gust outflow.
    /// </summary>
    public static float WindAlong(double xi)
    {
        if (xi <= 0.0) return GustOutflowMs;
        if (xi < WindRampEndXi) return Lerp(GustOutflowMs, WallWindPeakMs, Smooth(xi / WindRampEndXi));
        if (xi <= WindHoldEndXi) return WallWindPeakMs;
        if (xi < WallWindEndXi) return Lerp(WallWindPeakMs, WallWindEndMs, Smooth((xi - WindHoldEndXi) / (WallWindEndXi - WindHoldEndXi)));
        if (xi < WakeBackwardPeakXi) return Lerp(WallWindEndMs, -WakeBackwardPeakMs, Smooth((xi - WallWindEndXi) / (WakeBackwardPeakXi - WallWindEndXi)));
        if (xi < WakeTroughXi) return Lerp(-WakeBackwardPeakMs, 0.0f, Smooth((xi - WakeBackwardPeakXi) / (WakeTroughXi - WakeBackwardPeakXi)));
        if (xi < WakeForwardPeakXi) return Lerp(0.0f, WakeForwardPeakMs, Smooth((xi - WakeTroughXi) / (WakeForwardPeakXi - WakeTroughXi)));
        if (xi <= WakeForwardHoldEndXi) return WakeForwardPeakMs;
        if (xi < WakeEndXi) return Lerp(WakeForwardPeakMs, 0.0f, Smooth((xi - WakeForwardHoldEndXi) / (WakeEndXi - WakeForwardHoldEndXi)));
        return 0.0f;
    }

    private static float Lerp(float a, float b, double t) => (float)(a + (b - a) * t);

    /// <summary>Weight with which the profile replaces the ordinary arrival wind: 0 ahead of the edge, 1 through the wall and wake, fading out over xi 55-65 km.</summary>
    public static float WindWeight(double xi) => (float)(Smooth((xi + 300.0) / 600.0) * (1.0 - Smooth((xi - 55000.0) / 10000.0)));

    /// <summary>Wind override at a point: target (around, across) = direction x along-speed x formation level, with the replacement weight. Weight 0 when inactive or outside xi -300 m .. 65 km.</summary>
    public static bool WindOverride(MegastormModel model, double around, double across, double t, out Vector2 target, out float weight)
    {
        target = Vector2.Zero; weight = 0.0f;
        MegastormFrame f = model.Evaluate(t);
        if (!f.Active) return false;
        double xi = model.Xi(around, across, t);
        weight = WindWeight(xi);
        if (weight <= 0.0f) return false;
        target = new Vector2(model.State.Direction * WindAlong(xi) * f.Level, 0.0f);
        return true;
    }

    // ---- Rain impact (5.5.2) ----
    /// <summary>Rain rate (mm/h) at xi: 200 through the wall, easing to 60 through the transition, 60 in the trailing rain, fading out over the last 20 km of it.</summary>
    public static float RainRateMmPerHour(double xi, double trailingEndXi)
    {
        double edge = Smooth((xi - RainZoneStartXi) / 1000.0);
        double rate = RainRateWallMmPerHour + (RainRateTransitionMmPerHour - RainRateWallMmPerHour) * Smooth((xi - MegastormModel.WallEndXi) / (MegastormModel.TransitionEndXi - MegastormModel.WallEndXi));
        double tail = 1.0 - Smooth((xi - (trailingEndXi - 20000.0)) / 20000.0);
        return (float)(edge * rate * tail);
    }

    /// <summary>Impact power on a windward surface: 0.5 x liquid water content x v^3 (about 11 kW/m2 at 140 m/s and 200 mm/h). LWC scales linearly with the rain rate from 8 g/m3 at 200 mm/h.</summary>
    public static float RainImpactPowerWm2(float rainRateMmPerHour, float windSpeedMs)
    {
        float lwc = LiquidWaterAtWallKgM3 * rainRateMmPerHour / RainRateWallMmPerHour;
        return 0.5f * lwc * windSpeedMs * windSpeedMs * Math.Abs(windSpeedMs);
    }

    /// <summary>Unit direction the drops travel, ring frame (around, up, across): the horizontal wind plus the fall speed.</summary>
    public static Vector3 RainImpactDirection(float alongWindMs, int direction)
    {
        var v = new Vector3(direction * alongWindMs, -DropFallSpeedMs, 0.0f);
        return v.LengthSquared() > 1e-6f ? v.Normalized() : new Vector3(0.0f, -1.0f, 0.0f);
    }

    /// <summary>Drop speed: the wind and the fall speed combined.</summary>
    public static float RainDropSpeedMs(float alongWindMs) => MathF.Sqrt(alongWindMs * alongWindMs + DropFallSpeedMs * DropFallSpeedMs);

    // ---- Vacuum wake (5.5.3) ----
    /// <summary>Pressure deficit (kPa, positive = below ambient): falls over xi 25-35 km to 8 kPa at the trough, recovers by 60 km.</summary>
    public static float PressureDeficitKPa(double xi)
    {
        if (xi <= WallWindEndXi || xi >= WakeEndXi) return 0.0f;
        if (xi <= WakeTroughXi) return (float)(WakeTroughKPa * Smooth((xi - WallWindEndXi) / (WakeTroughXi - WallWindEndXi)));
        return (float)(WakeTroughKPa * (1.0 - Smooth((xi - WakeTroughXi) / (WakeEndXi - WakeTroughXi))));
    }

    /// <summary>Rate of change of the pressure (kPa/s, negative = falling) for a still observer: the storm moves at <paramref name="speedMetersPerSecond"/> through xi.</summary>
    public static float PressureRateKPaPerS(double xi, double speedMetersPerSecond)
    {
        const double h = 50.0;
        double slope = (PressureDeficitKPa(xi + h) - PressureDeficitKPa(xi - h)) / (2.0 * h);   // kPa of deficit per metre of xi
        return (float)(-slope * speedMetersPerSecond);
    }

    /// <summary>Condensation fog at the trough: smoothstep(2, 7, deficit).</summary>
    public static float WakeFog(float deficitKPa) => (float)Smooth((deficitKPa - 2.0) / 5.0);

    /// <summary>The Danger wake term: 0.6 x smoothstep(2, 6, deficit).</summary>
    public static float WakeDanger(float deficitKPa) => (float)(0.6 * Smooth((deficitKPa - 2.0) / 4.0));

    // ---- Scythes (5.5.4: rate and cut probability here; the stream is MegastormScythes, WO12) ----
    private static readonly double ScytheProfileIntegral;

    internal static double ScytheProfile(double xi) => Smooth((xi - ScytheZoneStartXi) / -ScytheZoneStartXi) * (1.0 - Smooth((xi - 10000.0) / 10000.0));

    private static double IntegrateScytheProfile()
    {
        const int n = 3500;
        double sum = 0.0, step = (ScytheZoneEndXi - ScytheZoneStartXi) / n;
        for (int i = 0; i < n; i++) sum += ScytheProfile(ScytheZoneStartXi + (i + 0.5) * step) * step;
        return sum;
    }

    /// <summary>
    /// Expected scythe paths crossing a point per second: 20 per second along the 318 km wall, each sweeping 3.5 km x 125 m, spread over xi -15 .. 20 km with the
    /// density peaking over 0-10 km. Integrated over a passage at v this is R x A / (W x v), about 0.08 at 350 m/s.
    /// </summary>
    public static float ScytheSweepRatePerSecond(double xi, double widthMeters)
    {
        double p = ScytheProfile(xi);
        if (p <= 0.0) return 0.0f;
        return (float)(ScytheRatePerSecond * ScytheLengthMeters * ScytheWidthMeters / widthMeters * p / ScytheProfileIntegral);
    }

    /// <summary>Share of scythes whose lower edge lies below the effective height: smoothstep(0.5, 4, h_eff). Prone (0.4 m) and trenches 0, a 10 m tree 1.</summary>
    public static float ScytheCutShare(float effectiveHeightMeters) => (float)Smooth((effectiveHeightMeters - 0.5) / 3.5);

    /// <summary>sweep rate x dt x F(h_eff) x exposure. h_eff = height above ground minus the depth of the ground below the smoothed skim surface (a trench is negative).</summary>
    public static float ScytheCutProbability(float sweepRatePerSecond, float heightAboveGroundMeters, float groundBelowSkimMeters, float exposure, double dt)
        => (float)Math.Clamp(sweepRatePerSecond * dt * ScytheCutShare(heightAboveGroundMeters - groundBelowSkimMeters) * Math.Clamp(exposure, 0.0f, 1.0f), 0.0, 1.0);

    // ---- Everything for one query ----
    /// <summary>All hazards at (around, across) for a frame that is Active. <paramref name="xi"/> is the point's xi.</summary>
    public static MegastormHazards Evaluate(MegastormModel model, double around, double across, double t, double xi, in MegastormFrame frame, in MegastormObserver observer)
    {
        double speed = frame.SpeedMetersPerSecond;
        MegastormSettings s = model.Settings;

        float overpressure = 0.0f, peak = 0.0f, impulse = 0.0f; double boomTime = 0.0;
        if (xi >= -BoomZoneAheadMeters && xi <= BoomZoneBehindMeters)
        {
            MegastormBoom boom = BoomAt(model, around, across, t);
            if (boom.Exists)
            {
                peak = boom.PeakKPa; impulse = boom.ImpulseKPaS; boomTime = boom.TimeSeconds;
                overpressure = OverpressureKPa(speed, xi, CellFactor(model, across, boom.CrossingTimeSeconds));
            }
        }

        float power = 0.0f, dropSpeed = 0.0f; Vector3 dir = Vector3.Zero;
        float weight = WindWeight(xi);
        if (weight > 0.0f)
        {
            float along = WindAlong(xi) * frame.Level;
            float rain = RainRateMmPerHour(xi, model.TrailingEndXi) * frame.Level;
            if (rain > 0.0f)
            {
                power = RainImpactPowerWm2(rain, along);
                dropSpeed = RainDropSpeedMs(along);
                dir = RainImpactDirection(along, model.State.Direction);
            }
        }

        float deficit = PressureDeficitKPa(xi) * frame.Level;
        float rate = PressureRateKPaPerS(xi, speed) * frame.Level;
        float strikeScale = Lightning.LightningVisualPolicy.NaturalStrikeChanceScale;   // the stream's chance scale: Lightning frequency x the reduced-flashes share
        float sweep = ScytheSweepRatePerSecond(xi, s.WidthMeters) * frame.Level * strikeScale;
        float nearest = xi >= ScytheZoneStartXi - 6000.0 && xi <= ScytheZoneEndXi + 6000.0
            ? MegastormScythes.NearestDistanceMeters(model, around, across, t, strikeScale) : float.PositiveInfinity;
        return new MegastormHazards(overpressure, peak, impulse, boomTime, power, dir, dropSpeed, Math.Clamp(observer.RainExposure, 0.0f, 1.0f),
            deficit, rate, WakeFog(deficit), 0.0f, sweep, nearest,
            (float)(MegastormFieldStrikes.FieldStrikeRatePerSecond(across, s.WidthMeters) * strikeScale), (float)MegastormFieldStrikes.GapMeters(across));
    }
}

/// <summary>Edges raised by <see cref="MegastormHazardEventTracker.Update"/>.</summary>
[Flags]
public enum MegastormHazardEdges { None = 0, BoomPassed = 1, WakeEntered = 2, WakeExited = 4 }

/// <summary>
/// Edge-triggered hazard events for one point (the observer, or one registered structure) from its xi history: the boom when xi crosses 0 upward, the wake
/// when xi enters / leaves 25-60 km. A load or a jump over 20 km has no history and raises nothing; a rewind below the edge re-arms the boom.
/// </summary>
public sealed class MegastormHazardEventTracker
{
    public const double JumpMeters = 20000.0;
    private bool _have, _inWake;
    private double _lastXi;

    public MegastormHazardEdges Update(double xi)
    {
        bool wake = xi >= MegastormHazardMath.WallWindEndXi && xi < MegastormHazardMath.WakeEndXi;
        var edges = MegastormHazardEdges.None;
        if (_have && Math.Abs(xi - _lastXi) <= JumpMeters)
        {
            if (_lastXi < 0.0 && xi >= 0.0) edges |= MegastormHazardEdges.BoomPassed;
            if (wake && !_inWake) edges |= MegastormHazardEdges.WakeEntered;
            if (!wake && _inWake) edges |= MegastormHazardEdges.WakeExited;
        }
        _have = true; _lastXi = xi; _inWake = wake;
        return edges;
    }

    public void Reset() { _have = false; _inWake = false; }
}
