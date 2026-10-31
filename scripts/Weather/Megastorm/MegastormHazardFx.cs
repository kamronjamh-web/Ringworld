using System;

namespace RingworldTechDemo;

/// <summary>
/// Pure maths of the hazard visuals and audio (docs/megastorm-design.md 5.5, WO11): the blast band profile the tree, grass and water readers use (GLSL twin in
/// materials/ring_megastorm_blast.gdshaderinc), the debris burst, the shock distortion amplitude, the wind-aligned bullet-rain constants, the rain-hammer and
/// wake-hush mappings and the ear-pop trigger. No nodes, no state except <see cref="EarPopTracker"/>.
/// </summary>
public static class MegastormHazardFx
{
    // ---- Blast band (5.5.1) ----
    public const float BlastRiseMeters = 60.0f, BlastHeldShare = 0.55f;
    public const double BlastHeldFromMeters = 2500.0, BlastRelaxFromMeters = 300.0, BlastReleaseStartMeters = 15000.0, BlastReleaseEndMeters = 25000.0;
    /// <summary>Full bend angle of a tree's arc (75 degrees).</summary>
    public const float BlastBendRadians = 1.309f;
    /// <summary>The blast globals are live from this far ahead of the front to this far behind it (xi, metres).</summary>
    public const double BlastZoneAheadMeters = 2000.0, BlastZoneBehindMeters = 26000.0;
    /// <summary>The shock distortion is attached from this far ahead (the band is seen sweeping in) to this far behind.</summary>
    public const double ShockAheadMeters = 6000.0, ShockBehindMeters = 1500.0;

    private static double Smooth(double e0, double e1, double x) { double t = Math.Clamp((x - e0) / (e1 - e0), 0.0, 1.0); return t * t * (3.0 - 2.0 * t); }

    /// <summary>Bend share 0..1 of a point <paramref name="s"/> metres behind the front: thrown over within 60 m, relaxing to a held lean of 0.55 by 2.5 km, released over 15-25 km. 0 ahead of the front.</summary>
    public static float BlastProfile(double s)
    {
        if (s <= 0.0) return 0.0f;
        double level = (1.0 + (BlastHeldShare - 1.0) * Smooth(BlastRelaxFromMeters, BlastHeldFromMeters, s)) * (1.0 - Smooth(BlastReleaseStartMeters, BlastReleaseEndMeters, s));
        return (float)(Smooth(0.0, BlastRiseMeters, s) * level);
    }

    /// <summary>Blast strength 0..1 from the open-air peak overpressure of the passage (60 kPa = 1; 0 below Mach 0.94, where there is no boom).</summary>
    public static float BlastStrength(float peakKPa) => float.IsFinite(peakKPa) ? Math.Clamp(peakKPa / MegastormHazardMath.BoomOpenPeakKPa, 0.0f, 1.0f) : 0.0f;

    /// <summary>
    /// Front offset along the direction of travel from the observer (the value the shaders subtract from the along-travel coordinate). The visual front passes the
    /// observer at the instant of the audio, camera-shake and overpressure peak (<see cref="MegastormHazardMath.BoomPeakSeconds"/> after the edge), centred on the
    /// 60 m throw, so the tree flattening, the debris and the water slam land in the same frame window as the peak.
    /// </summary>
    public static double FrontOffset(double xi, double speedMetersPerSecond) => xi - MegastormHazardMath.BoomPeakSeconds * speedMetersPerSecond + 0.5 * BlastRiseMeters;

    public static bool BlastZone(double xi) => xi >= -BlastZoneAheadMeters && xi <= BlastZoneBehindMeters;
    public static bool ShockZone(double xi) => xi >= -ShockAheadMeters && xi <= ShockBehindMeters;

    // ---- Debris (5.5.1 burst, 5.5.3 backwards drag) ----
    public const float BurstHoldSeconds = 1.5f, BurstEndSeconds = 4.5f;

    /// <summary>Burst level 0..1 of the dust and debris sheet <paramref name="secondsSincePeak"/> after the peak: instant rise over the 0.15 s before the peak, held 1.5 s, gone by 4.5 s (a 4 s sheet).</summary>
    public static float BurstLevel(double secondsSincePeak, float strength)
    {
        if (secondsSincePeak < -0.15 || strength <= 0.0f) return 0.0f;
        double rise = Smooth(-0.15, 0.0, secondsSincePeak);
        return (float)(rise * (1.0 - Smooth(BurstHoldSeconds, BurstEndSeconds, secondsSincePeak))) * Math.Clamp(strength, 0.0f, 1.0f);
    }

    /// <summary>Share of the debris pool alive in the sustained wind of the wall and wake: full from 45 m/s either way (so the reversed wake flow carries debris backwards), times the profile weight.</summary>
    public static float WindDebrisLevel(double xi, float levelFactor)
    {
        float weight = MegastormHazardMath.WindWeight(xi);
        if (weight <= 0.0f) return 0.0f;
        float along = Math.Abs(MegastormHazardMath.WindAlong(xi)) * levelFactor;
        return Math.Clamp(along / 45.0f, 0.0f, 1.0f) * 0.85f * weight;
    }

    // ---- Shock distortion (5.5.1) ----
    /// <summary>"Reduced flashes" damps the band to 30 % of its amplitude.</summary>
    public const float ReducedDistortionScale = 0.3f;
    public const float ShockAmplitudePixelsAt1080 = 9.0f, ShockBandHalfWidthMeters = 40.0f, ShockTailWidthMeters = 500.0f;
    public static float ShockDistortionScale(bool reducedFlashes) => reducedFlashes ? ReducedDistortionScale : 1.0f;

    /// <summary>Peak refraction shift in pixels at the internal resolution: setting x strength x reduced scale x height / 1080 x the nearness of the front (full inside 1.5 km, 0.35 at 6 km).</summary>
    public static float ShockAmplitudePixels(float setting, float strength, bool reducedFlashes, double xi, int heightPixels)
    {
        if (!float.IsFinite(setting) || setting <= 0.0f || strength <= 0.0f) return 0.0f;
        double near = 1.0 - 0.65 * Smooth(1500.0, ShockAheadMeters, Math.Abs(xi));
        return (float)(ShockAmplitudePixelsAt1080 * Math.Clamp(setting, 0.0f, 1.0f) * strength * ShockDistortionScale(reducedFlashes) * near * Math.Max(1, heightPixels) / 1080.0);
    }

    // ---- Bullet rain (5.5.2) ----
    /// <summary>Above this streak slope (horizontal metres per metre of fall) the lattice switches to the wind-aligned mode.</summary>
    public const float WindAlignedSlope = 3.0f;
    /// <summary>The slope cap for the megastorm: 140 m/s over the lattice fall speed, with headroom for the gust field.</summary>
    public const float MaximumSlopeMegastorm = 16.0f;
    public const float StreakSecondsOfTravel = 1.0f / 60.0f, StreakMaxLengthMeters = 2.5f;

    /// <summary>Streak length of a drop: speed x 1/60 s, clamped to 2.5 m.</summary>
    public static float StreakLengthMeters(float dropSpeedMs) => Math.Clamp(dropSpeedMs * StreakSecondsOfTravel, 0.0f, StreakMaxLengthMeters);

    /// <summary>Slope cap to publish: the megastorm cap when a megastorm is live (wind-aligned mode possible), else the ordinary cap.</summary>
    public static float SlopeCap(bool megastormLive, float ordinaryCap) => megastormLive ? MaximumSlopeMegastorm : ordinaryCap;

    /// <summary>Windward spray: 0 below a 1 kW/m2 impact power, full from 6 kW/m2.</summary>
    public static float SprayAmount(float impactPowerWm2, float exposure) => (float)(Smooth(1000.0, 6000.0, impactPowerWm2) * Math.Clamp(exposure, 0.0f, 1.0f));

    // ---- Audio (5.5.2 / 5.5.3) ----
    /// <summary>Rain-hammer bed gain 0..1: impact power from 1.5 to 8 kW/m2, quieter in the open than on a roof (indoors drums at full).</summary>
    public static float RainHammerGain(float impactPowerWm2, float exposure, MegastormShelter shelter, bool underRoof)
    {
        double power = Smooth(1500.0, 8000.0, impactPowerWm2);
        double cover = underRoof ? 1.0 : (shelter == MegastormShelter.Lee ? 0.45 : 0.4 + 0.2 * Math.Clamp(exposure, 0.0f, 1.0f));
        return (float)(power * cover);
    }

    public const float EarPopRateKPaPerS = 0.2f, EarPopCooldownSeconds = 8.0f, OpenCutoffHz = 20500.0f, HushCutoffHz = 3200.0f;

    /// <summary>The dull low-passed hush of the trough: the master cutoff falls from open to 3.2 kHz as the deficit goes from 1 to 6 kPa.</summary>
    public static float WakeHushCutoffHz(float deficitKPa)
    {
        double k = Smooth(1.0, 6.0, deficitKPa);
        return (float)(OpenCutoffHz + (HushCutoffHz - OpenCutoffHz) * k);
    }

    /// <summary>Edge trigger of the ear pop: the pressure rate's magnitude crossing 0.2 kPa/s from below, at most once per cooldown.</summary>
    public sealed class EarPopTracker
    {
        private bool _above;
        private double _since = double.PositiveInfinity;

        public bool Update(float rateKPaPerS, double dtSeconds)
        {
            _since += Math.Max(0.0, dtSeconds);
            bool above = Math.Abs(rateKPaPerS) > EarPopRateKPaPerS;
            bool fire = above && !_above && _since >= EarPopCooldownSeconds;
            _above = above;
            if (fire) _since = 0.0;
            return fire;
        }

        public void Reset() { _above = false; _since = double.PositiveInfinity; }
    }
}
