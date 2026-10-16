using System;

namespace RingworldTechDemo.Celestial.Eclipse;

/// <summary>Event phases in order (spec B.1). Idle is also the state outside [S - PreRoll, return end].</summary>
public enum EclipseEventPhase : byte
{
    Idle = 0, PreRoll, Darkness, MainFire, Kindle, Ignition, Crescendo, Handoff, Sustain,
    Instability, Overload, Implosion, Shockwave, Aftermath, Return
}

public enum EclipseIgnitionSpeedMode : byte { MatchGrid = 0, Manual = 1 }
public enum EclipseRampMode : byte { PerBeam = 0, Perceptual = 1 }

/// <summary>
/// Which clock feeds the grid pulse maths for the Sustain refuel flashes. <see cref="Celestial"/> (default, user decision 6) evaluates
/// <c>EmitterGridPulseClock.ArrivalTime</c> in celestial seconds so the cadence compresses with TimeScale; <see cref="Weather"/> uses the
/// saved weather clock (real-time cadence, spec A.4) and converts flash times with the current clock offset.
/// </summary>
public enum EclipseRefuelClock : byte { Celestial = 0, Weather = 1 }

/// <summary>
/// Timeline and lighting-level settings as plain data (spec B.2, C.5). Persistence and the drawer are phase E1. Defaults are the spec
/// defaults; user decision 6 (2026-10-16) removes the real-time hold, so there is no <c>HoldRealTimeDuringEvent</c> field and the event
/// follows the celestial TimeScale. Call <see cref="Sanitized"/> before use: it clamps every field to its range and makes the collapse
/// stages fit inside <see cref="CollapseLeadSeconds"/>.
/// </summary>
public sealed record EclipseEventSettings
{
    public bool EventEnabled { get; init; } = true;
    public double ParallaxSafetySeconds { get; init; } = 10d;
    public double PreRollSeconds { get; init; } = 300d;
    public double DarknessBeatSeconds { get; init; } = 20d;
    public double MainClimbSeconds { get; init; } = 4d;
    public double MainClimbExponent { get; init; } = 2d;
    public double KindleSeconds { get; init; } = 2.5d;
    public double KindleLevel { get; init; } = 0.10d;
    public double IgnitionDelaySeconds { get; init; } = 3d;
    public EclipseIgnitionSpeedMode IgnitionSpeedMode { get; init; } = EclipseIgnitionSpeedMode.MatchGrid;
    /// <summary>Seconds for a front to cover half the ring in <see cref="EclipseIgnitionSpeedMode.Manual"/> mode.</summary>
    public double IgnitionSeconds { get; init; } = 150d;
    public double RegularClimbSeconds { get; init; } = 1.5d;
    public EclipseRampMode RampMode { get; init; } = EclipseRampMode.PerBeam;
    public double RampGamma { get; init; } = 1d;
    public double RampStepSmoothSeconds { get; init; } = 0.5d;
    public double IgnitionPlateauLevel { get; init; } = 0.35d;
    public bool SuppressGridPulseDuringIgnition { get; init; } = true;
    public double MeetingFlareSeconds { get; init; } = 1.5d;
    public double CrescendoSeconds { get; init; } = 8d;
    public double HandoffFadeSeconds { get; init; } = 2.5d;
    public double FlashClimbSeconds { get; init; } = 0.5d;
    public double FlashHoldSeconds { get; init; } = 0.6d;
    public double FlashFadeSeconds { get; init; } = 1.2d;
    public double HeartbeatKick { get; init; } = 0.02d;
    public double HeartbeatTauSeconds { get; init; } = 0.8d;
    public double RelaunchKick { get; init; } = 0.05d;
    public double BreathAmplitude { get; init; } = 0.015d;
    public double BreathPeriodSeconds { get; init; } = 11d;
    public double CollapseLeadSeconds { get; init; } = 75d;
    public double InstabilitySeconds { get; init; } = 45d;
    public double FlickerMax { get; init; } = 0.35d;
    public double FlickerHzStart { get; init; } = 0.5d;
    public double FlickerHzEnd { get; init; } = 6d;
    public double OverloadSeconds { get; init; } = 4d;
    public double OverloadGain { get; init; } = 2.5d;
    public double ImplosionSeconds { get; init; } = 2.5d;
    public double BeamRetractSeconds { get; init; } = 1d;
    public double ShockwaveSeconds { get; init; } = 8d;
    /// <summary>Fraction of the shockwave at which the shell reaches the floor (ground flash), spec E.5.</summary>
    public double ShockwaveArrivalFraction { get; init; } = 0.75d;
    public double ShockFlashLevel { get; init; } = 0.6d;
    public double ShockFlashSeconds { get; init; } = 0.4d;
    /// <summary>Seconds of Return phase after E when no player-local third contact is supplied.</summary>
    public double ReturnTailSeconds { get; init; } = 450d;
    public EclipseRefuelClock RefuelClock { get; init; } = EclipseRefuelClock.Celestial;
    /// <summary>Real-sun StarLight energy at noon (day_side_atmosphere.gd day_sun_energy).</summary>
    public double RealSunEnergy { get; init; } = 4d;
    public double HubDirectFraction { get; init; } = 0.8d;
    public double HubDayAtFull { get; init; } = 0.8d;

    public EclipseEventSettings Sanitized()
    {
        EclipseEventSettings s = this with
        {
            ParallaxSafetySeconds = C(ParallaxSafetySeconds, 0d, 120d, 10d),
            PreRollSeconds = C(PreRollSeconds, 0d, 900d, 300d),
            DarknessBeatSeconds = C(DarknessBeatSeconds, 0d, 600d, 20d),
            MainClimbSeconds = C(MainClimbSeconds, 0.5d, 20d, 4d),
            MainClimbExponent = C(MainClimbExponent, 1d, 4d, 2d),
            KindleSeconds = C(KindleSeconds, 0.1d, 10d, 2.5d),
            KindleLevel = C(KindleLevel, 0.02d, 0.5d, 0.10d),
            IgnitionDelaySeconds = C(IgnitionDelaySeconds, 0d, 30d, 3d),
            IgnitionSeconds = C(IgnitionSeconds, 30d, 600d, 150d),
            RegularClimbSeconds = C(RegularClimbSeconds, 0.2d, 10d, 1.5d),
            RampGamma = C(RampGamma, 0.5d, 3d, 1d),
            RampStepSmoothSeconds = C(RampStepSmoothSeconds, 0d, 3d, 0.5d),
            IgnitionPlateauLevel = C(IgnitionPlateauLevel, 0d, 1d, 0.35d),
            MeetingFlareSeconds = C(MeetingFlareSeconds, 0d, 10d, 1.5d),
            CrescendoSeconds = C(CrescendoSeconds, 0d, 60d, 8d),
            HandoffFadeSeconds = C(HandoffFadeSeconds, 0.2d, 10d, 2.5d),
            FlashClimbSeconds = C(FlashClimbSeconds, 0.1d, 3d, 0.5d),
            FlashHoldSeconds = C(FlashHoldSeconds, 0d, 3d, 0.6d),
            FlashFadeSeconds = C(FlashFadeSeconds, 0.1d, 5d, 1.2d),
            HeartbeatKick = C(HeartbeatKick, 0d, 0.2d, 0.02d),
            HeartbeatTauSeconds = C(HeartbeatTauSeconds, 0.1d, 5d, 0.8d),
            RelaunchKick = C(RelaunchKick, 0d, 0.3d, 0.05d),
            BreathAmplitude = C(BreathAmplitude, 0d, 0.1d, 0.015d),
            BreathPeriodSeconds = C(BreathPeriodSeconds, 2d, 60d, 11d),
            CollapseLeadSeconds = C(CollapseLeadSeconds, 20d, 600d, 75d),
            InstabilitySeconds = C(InstabilitySeconds, 5d, 300d, 45d),
            FlickerMax = C(FlickerMax, 0d, 0.9d, 0.35d),
            FlickerHzStart = C(FlickerHzStart, 0.1d, 5d, 0.5d),
            FlickerHzEnd = C(FlickerHzEnd, 1d, 20d, 6d),
            OverloadSeconds = C(OverloadSeconds, 0.5d, 20d, 4d),
            OverloadGain = C(OverloadGain, 1d, 5d, 2.5d),
            ImplosionSeconds = C(ImplosionSeconds, 0.3d, 10d, 2.5d),
            BeamRetractSeconds = C(BeamRetractSeconds, 0.1d, 5d, 1d),
            ShockwaveSeconds = C(ShockwaveSeconds, 1d, 30d, 8d),
            ShockwaveArrivalFraction = C(ShockwaveArrivalFraction, 0d, 1d, 0.75d),
            ShockFlashLevel = C(ShockFlashLevel, 0d, 2d, 0.6d),
            ShockFlashSeconds = C(ShockFlashSeconds, 0.05d, 3d, 0.4d),
            ReturnTailSeconds = C(ReturnTailSeconds, 0d, 1800d, 450d),
            RealSunEnergy = C(RealSunEnergy, 0d, 16d, 4d),
            HubDirectFraction = C(HubDirectFraction, 0d, 1.5d, 0.8d),
            HubDayAtFull = C(HubDayAtFull, 0d, 1d, 0.8d)
        };
        // The four collapse stages must fit in the lead so the collapse ends before E (Aftermath >= 0 s): scale them proportionally.
        double sum = s.InstabilitySeconds + s.OverloadSeconds + s.ImplosionSeconds + s.ShockwaveSeconds;
        if (sum > s.CollapseLeadSeconds)
        {
            double k = s.CollapseLeadSeconds / sum;
            s = s with
            {
                InstabilitySeconds = s.InstabilitySeconds * k, OverloadSeconds = s.OverloadSeconds * k,
                ImplosionSeconds = s.ImplosionSeconds * k, ShockwaveSeconds = s.ShockwaveSeconds * k
            };
        }
        return s;
    }

    private static double C(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;
}
