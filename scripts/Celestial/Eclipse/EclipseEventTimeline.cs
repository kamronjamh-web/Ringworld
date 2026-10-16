using System;
using System.Collections.Generic;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Celestial.Eclipse;

/// <summary>Event window [S, E] in absolute celestial seconds (spec A.3, margin already applied).</summary>
public readonly record struct EclipseEventWindow(int Index, double Start, double End)
{
    public double Duration => End - Start;
    public bool IsValid => double.IsFinite(Start) && double.IsFinite(End) && End > Start;
    public static EclipseEventWindow From(in EclipseWindow w) => w.Valid ? new(w.Index, w.RingStart, w.RingEnd) : new(w.Index, double.NaN, double.NaN);
}

/// <summary>Start of a phase in event time (tau = celestial time - S).</summary>
public readonly record struct EclipsePhaseBoundary(EclipseEventPhase Phase, double StartTau);

/// <summary>Everything the event renders from, as a pure function of (celestial time, weather time). See <see cref="EclipseEventTimeline.Evaluate"/>.</summary>
public readonly record struct EclipseEventState(
    EclipseEventPhase Phase, double Tau, double PhaseElapsed, double PhaseRemaining,
    double Level, double HubDirect, double HubDay, double HubWeight, double BodyRadius,
    double Flicker, double Breath, double Heartbeat, double ShockFlash,
    double ArrivedWeight, int FiredCount, int LitRegularBeams, double RegularBeamIntensity,
    bool MainBeamLit, double MainBeamTip, double BeamCutProgress,
    bool FrontsActive, double ForwardHeadMeters, double BackwardHeadMeters, double MeetingFlare, double PlateauLevel,
    double GridPulseWeight, double GridTime)
{
    public bool Active => Phase != EclipseEventPhase.Idle;
}

/// <summary>
/// Pure event timeline (docs/eclipse-false-sun-spec.md B). Every output is a deterministic function of the construction inputs and
/// <c>Evaluate(celestialTime, weatherTime)</c>: nothing accumulates, so loading, scrubbing and any frame rate reproduce the same state.
/// All event timing (ignition fronts, hub arrivals, collapse, cues) is a function of celestial time only, so it compresses with the
/// celestial TimeScale (user decision 6). With <see cref="EclipseRefuelClock.Celestial"/> (default) the Sustain refuel flashes also run on
/// celestial time via <c>EmitterGridPulseClock.ArrivalTime</c>, so <c>weatherTime</c> is ignored.
/// </summary>
public sealed class EclipseEventTimeline
{
    /// <summary>Partial-phase length appended after the local third contact for the Return phase (spec A.1: 44.6 s).</summary>
    public const double EgressSeconds = 45d;

    private readonly EclipseEventSettings _s;
    private readonly EmitterGridPulseClock _clock;
    private readonly IEclipseSiteOutput _outputs;
    private readonly double _c;
    private readonly double[] _fireTau, _arrivalTau;
    private readonly int[] _order;            // entry indices sorted by fire time
    private readonly double[] _sortedFire, _sortedArrival, _prefixOutput;
    private readonly int[] _prefixAlive;
    private readonly bool[] _alive;
    private readonly double[][] _colQ;        // alive-site pulse coordinates per column, ascending
    private ulong _outputRevision = ulong.MaxValue;
    private double _fullLevel = 1d;
    private readonly EclipsePhaseBoundary[] _bounds;

    public EclipseEventSettings Settings => _s;
    public EclipseEventWindow Window { get; }
    public EclipseSiteTable Sites { get; }
    public EmitterGridPulseClock GridClock => _clock;
    public long Seed { get; }
    public bool Active { get; }

    /// <summary>Factor (&lt;= 1) applied to every authored duration when the window is too short for the configured stages.</summary>
    public double TimeCompression { get; } = 1d;
    /// <summary>Front speed along the loop in m/s (grid speed in MatchGrid mode).</summary>
    public double IgnitionSpeedMetersPerSecond { get; }
    /// <summary>Seconds for a front to reach the antipode.</summary>
    public double IgnitionHalfRingSeconds { get; }

    public double TauEnd { get; }
    public double DarknessBeat { get; }
    public double MainClimb { get; }
    public double KindleDuration { get; }
    public double IgnitionDelay { get; }
    public double RegularClimb { get; }
    public double CrescendoDuration { get; }
    public double HandoffFade { get; }
    public double CollapseLead { get; }
    public double InstabilityDuration { get; }
    public double OverloadDuration { get; }
    public double ImplosionDuration { get; }
    public double ShockwaveDuration { get; }
    public double BeamRetract { get; }

    public double TauMainFire { get; }
    public double TauKindle { get; }
    public double TauKindled { get; }
    public double TauIgnition { get; }
    public double TauFrontsMet { get; }
    public double TauCrescendo { get; }
    public double TauHandoff { get; }
    public double TauSustain { get; }
    public double TauInstability { get; }
    public double TauOverload { get; }
    public double TauImplosion { get; }
    public double TauShockwave { get; }
    public double TauShockArrival { get; }
    public double TauAftermath { get; }

    public IReadOnlyList<EclipsePhaseBoundary> Boundaries => _bounds;
    public int SiteCount => Sites.Count;
    /// <summary>Hub level once every live beam has landed: L0 + (1 - L0) * R(sum(output) / N_total) (spec B.3, B.5).</summary>
    public double FullLevel { get { SyncOutputs(); return _fullLevel; } }

    public EclipseEventTimeline(EclipseEventSettings settings, EclipseEventWindow window, EclipseSiteTable sites,
        EmitterGridPulseClock gridClock, long seed, IEclipseSiteOutput? outputs = null)
    {
        _s = (settings ?? throw new ArgumentNullException(nameof(settings))).Sanitized();
        Window = window;
        Sites = sites ?? throw new ArgumentNullException(nameof(sites));
        _clock = gridClock ?? throw new ArgumentNullException(nameof(gridClock));
        Seed = seed;
        _outputs = outputs ?? FullEclipseSiteOutput.Instance;
        _c = sites.CircumferenceMeters;
        int n = sites.Count;
        _fireTau = new double[n]; _arrivalTau = new double[n]; _order = new int[n];
        _sortedFire = new double[n]; _sortedArrival = new double[n];
        _prefixOutput = new double[n + 1]; _prefixAlive = new int[n + 1]; _alive = new bool[n];
        _colQ = new double[sites.Columns][];
        _bounds = Array.Empty<EclipsePhaseBoundary>();

        Active = _s.EventEnabled && window.IsValid && n > 0;
        TauEnd = Active ? window.Duration : 0d;

        double vBase = _s.IgnitionSpeedMode == EclipseIgnitionSpeedMode.MatchGrid
            ? _clock.SpeedMetersPerSecond
            : (_c * 0.5d) / _s.IgnitionSeconds;
        double halfReach = 0d;
        for (int i = 0; i < n; i++) halfReach = Math.Max(halfReach, Math.Min(sites.P[i], _c - sites.P[i]));
        double travelBase = halfReach / vBase;
        double required = _s.DarknessBeatSeconds + _s.MainClimbSeconds + _s.KindleSeconds + _s.IgnitionDelaySeconds + travelBase
            + _s.RegularClimbSeconds + _s.CrescendoSeconds + _s.HandoffFadeSeconds + _s.CollapseLeadSeconds;
        double f = Active && required > TauEnd ? TauEnd / required : 1d;
        TimeCompression = f;

        DarknessBeat = _s.DarknessBeatSeconds * f; MainClimb = _s.MainClimbSeconds * f; KindleDuration = _s.KindleSeconds * f;
        IgnitionDelay = _s.IgnitionDelaySeconds * f; RegularClimb = _s.RegularClimbSeconds * f;
        CrescendoDuration = _s.CrescendoSeconds * f; HandoffFade = _s.HandoffFadeSeconds * f; CollapseLead = _s.CollapseLeadSeconds * f;
        InstabilityDuration = _s.InstabilitySeconds * f; OverloadDuration = _s.OverloadSeconds * f;
        ImplosionDuration = _s.ImplosionSeconds * f; ShockwaveDuration = _s.ShockwaveSeconds * f; BeamRetract = _s.BeamRetractSeconds * f;
        IgnitionSpeedMetersPerSecond = vBase / f;
        IgnitionHalfRingSeconds = (_c * 0.5d) / IgnitionSpeedMetersPerSecond;

        TauMainFire = DarknessBeat;
        TauKindle = TauMainFire + MainClimb;
        TauKindled = TauKindle + KindleDuration;
        TauIgnition = TauKindled + IgnitionDelay;
        TauFrontsMet = TauIgnition + IgnitionHalfRingSeconds;
        for (int i = 0; i < n; i++)
        {
            double reach = Math.Min(sites.P[i], _c - sites.P[i]);
            _fireTau[i] = TauIgnition + reach / IgnitionSpeedMetersPerSecond;
            _arrivalTau[i] = _fireTau[i] + RegularClimb;
            _order[i] = i;
        }
        Array.Sort(_order, (a, b) => _fireTau[a] != _fireTau[b] ? _fireTau[a].CompareTo(_fireTau[b]) : a.CompareTo(b));
        double lastArrival = TauIgnition + RegularClimb;
        for (int k = 0; k < n; k++)
        {
            _sortedFire[k] = _fireTau[_order[k]]; _sortedArrival[k] = _arrivalTau[_order[k]];
            lastArrival = Math.Max(lastArrival, _sortedArrival[k]);
        }
        TauCrescendo = lastArrival;
        TauHandoff = TauCrescendo + CrescendoDuration;
        TauSustain = TauHandoff + HandoffFade;
        TauInstability = TauEnd - CollapseLead;
        TauOverload = TauInstability + InstabilityDuration;
        TauImplosion = TauOverload + OverloadDuration;
        TauShockwave = TauImplosion + ImplosionDuration;
        TauShockArrival = TauShockwave + ShockwaveDuration * _s.ShockwaveArrivalFraction;
        TauAftermath = TauShockwave + ShockwaveDuration;

        _bounds = !Active ? Array.Empty<EclipsePhaseBoundary>() : new[]
        {
            new EclipsePhaseBoundary(EclipseEventPhase.PreRoll, -_s.PreRollSeconds),
            new EclipsePhaseBoundary(EclipseEventPhase.Darkness, 0d),
            new EclipsePhaseBoundary(EclipseEventPhase.MainFire, TauMainFire),
            new EclipsePhaseBoundary(EclipseEventPhase.Kindle, TauKindle),
            new EclipsePhaseBoundary(EclipseEventPhase.Ignition, TauIgnition),
            new EclipsePhaseBoundary(EclipseEventPhase.Crescendo, TauCrescendo),
            new EclipsePhaseBoundary(EclipseEventPhase.Handoff, TauHandoff),
            new EclipsePhaseBoundary(EclipseEventPhase.Sustain, TauSustain),
            new EclipsePhaseBoundary(EclipseEventPhase.Instability, TauInstability),
            new EclipsePhaseBoundary(EclipseEventPhase.Overload, TauOverload),
            new EclipsePhaseBoundary(EclipseEventPhase.Implosion, TauImplosion),
            new EclipsePhaseBoundary(EclipseEventPhase.Shockwave, TauShockwave),
            new EclipsePhaseBoundary(EclipseEventPhase.Aftermath, TauAftermath),
            new EclipsePhaseBoundary(EclipseEventPhase.Return, TauEnd),
            new EclipsePhaseBoundary(EclipseEventPhase.Idle, TauEnd + _s.ReturnTailSeconds),
        };
        SyncOutputs();
    }

    // ---------------------------------------------------------------- outputs seam

    private void SyncOutputs()
    {
        if (_outputRevision == _outputs.Revision) return;
        _outputRevision = _outputs.Revision;
        int n = Sites.Count;
        var perColumn = new List<double>[Sites.Columns];
        for (int c = 0; c < perColumn.Length; c++) perColumn[c] = new List<double>();
        for (int e = 0; e < n; e++)
        {
            float o = _outputs.Output(Sites.SiteIndex[e]);
            _alive[e] = float.IsFinite(o) && o > 0f;
            if (_alive[e]) perColumn[Sites.Column[e]].Add(_clock.ToQ(Sites.P[e]));
        }
        for (int k = 0; k < n; k++)
        {
            int e = _order[k];
            float o = _alive[e] ? Math.Clamp(_outputs.Output(Sites.SiteIndex[e]), 0f, 1f) : 0f;
            _prefixOutput[k + 1] = _prefixOutput[k] + o;
            _prefixAlive[k + 1] = _prefixAlive[k] + (_alive[e] ? 1 : 0);
        }
        for (int c = 0; c < perColumn.Length; c++) { perColumn[c].Sort(); _colQ[c] = perColumn[c].ToArray(); }
        double l0 = _s.KindleLevel;
        _fullLevel = n == 0 ? l0 : l0 + (1d - l0) * Ramp(_prefixOutput[n] / n);
    }

    private double Ramp(double x)
    {
        x = Math.Clamp(x, 0d, 1d);
        return _s.RampMode == EclipseRampMode.Perceptual ? Math.Pow(x, _s.RampGamma) : x;
    }

    // ---------------------------------------------------------------- schedules

    /// <summary>Event time at which the entry's beam fires: tau_ign + min(p, C - p) / v_ign (spec B.4).</summary>
    public double SiteFireTau(int entry) => _fireTau[entry];
    /// <summary>Event time at which the entry's beam tip reaches the hub (fire + climb).</summary>
    public double SiteArrivalTau(int entry) => _arrivalTau[entry];
    public bool SiteAlive(int entry) { SyncOutputs(); return _alive[entry]; }
    /// <summary>Entry indices ordered by fire time (ties by index).</summary>
    public ReadOnlySpan<int> FireOrder => _order;
    public ReadOnlySpan<double> SortedFireTau => _sortedFire;

    /// <summary>Live beams fired at or before <paramref name="tau"/>.</summary>
    public int CountFired(double tau) { SyncOutputs(); return _prefixAlive[UpperBound(_sortedFire, tau)]; }

    /// <summary>Weighted count N(tau) of beams that have reached the hub, each smoothed over RampStepSmoothSeconds (spec B.3).</summary>
    public double ArrivedWeight(double tau)
    {
        SyncOutputs();
        double sm = _s.RampStepSmoothSeconds;
        if (sm <= 0d) return _prefixOutput[UpperBound(_sortedArrival, tau)];
        int done = UpperBound(_sortedArrival, tau - sm);
        int started = UpperBound(_sortedArrival, tau);
        double sum = _prefixOutput[done];
        for (int k = done; k < started; k++)
        {
            double o = _prefixOutput[k + 1] - _prefixOutput[k];
            sum += o * Smooth01((tau - _sortedArrival[k]) / sm);
        }
        return sum;
    }

    /// <summary>Refuel flash time (in the grid clock's time) of an entry for grid launch <paramref name="launch"/>: exactly <c>ArrivalTime(ToQ(p), column, launch)</c>.</summary>
    public double RefuelArrivalTime(int entry, long launch) => _clock.ArrivalTime(_clock.ToQ(Sites.P[entry]), Sites.Column[entry], launch);

    /// <summary>Smallest grid launch whose arrival at the entry is at or after <paramref name="gridTime"/>.</summary>
    public long RefuelLaunchAtOrAfter(int entry, double gridTime)
    {
        double q = _clock.ToQ(Sites.P[entry]); int col = Sites.Column[entry];
        long n = (long)Math.Floor((gridTime - q / _clock.SpeedMetersPerSecond - _clock.PhaseSeconds - _clock.StaggerSeconds(col)) / _clock.PeriodSeconds);
        while (_clock.ArrivalTime(q, col, n) < gridTime) n++;
        while (_clock.ArrivalTime(q, col, n - 1) >= gridTime) n--;
        return n;
    }

    public double GridTime(double celestialTime, double weatherTime)
        => _s.RefuelClock == EclipseRefuelClock.Celestial ? celestialTime : weatherTime;

    /// <summary>Refuel flash envelope (0..1) of an entry at <paramref name="gridTime"/>; non-zero only for flashes that arrive during Sustain.</summary>
    public double RefuelFlash(int entry, double tau, double gridTime)
    {
        SyncOutputs();
        if (!Active || !_alive[entry]) return 0d;
        double q = _clock.ToQ(Sites.P[entry]); int col = Sites.Column[entry];
        long n = RefuelLaunchAtOrAfter(entry, gridTime);
        if (_clock.ArrivalTime(q, col, n) > gridTime) n--;
        double age = gridTime - _clock.ArrivalTime(q, col, n);
        double passTau = tau - age;
        if (passTau < TauSustain || passTau >= TauInstability) return 0d;
        double climb = _s.FlashClimbSeconds, hold = _s.FlashHoldSeconds, fade = _s.FlashFadeSeconds;
        if (age < climb) return Smooth01(age / climb);
        if (age < climb + hold) return 1d;
        return 1d - Smooth01((age - climb - hold) / fade);
    }

    // ---------------------------------------------------------------- phases

    public EclipseEventPhase PhaseAt(double tau, double localThirdContactCelestial = double.NaN)
        => PhaseAt(tau, out _, out _, localThirdContactCelestial);

    public EclipseEventPhase PhaseAt(double tau, out double phaseStartTau, out double phaseEndTau, double localThirdContactCelestial = double.NaN)
    {
        phaseStartTau = double.NegativeInfinity; phaseEndTau = double.PositiveInfinity;
        if (!Active || double.IsNaN(tau)) return EclipseEventPhase.Idle;
        double returnEnd = ReturnEndTau(localThirdContactCelestial);
        if (tau < _bounds[0].StartTau) { phaseEndTau = _bounds[0].StartTau; return EclipseEventPhase.Idle; }
        if (tau >= returnEnd) { phaseStartTau = returnEnd; return EclipseEventPhase.Idle; }
        for (int i = _bounds.Length - 2; i >= 0; i--)
        {
            if (tau < _bounds[i].StartTau) continue;
            phaseStartTau = _bounds[i].StartTau;
            phaseEndTau = i == _bounds.Length - 2 ? returnEnd : _bounds[i + 1].StartTau;
            return _bounds[i].Phase;
        }
        return EclipseEventPhase.Idle;
    }

    /// <summary>End of the Return phase: the local third contact plus the partial egress, never earlier than E (default E + ReturnTailSeconds).</summary>
    public double ReturnEndTau(double localThirdContactCelestial = double.NaN)
    {
        if (!double.IsFinite(localThirdContactCelestial)) return TauEnd + _s.ReturnTailSeconds;
        return Math.Max(TauEnd, localThirdContactCelestial - Window.Start) + EgressSeconds;
    }

    // ---------------------------------------------------------------- evaluation

    /// <summary>
    /// State at celestial time <paramref name="celestialTime"/>. <paramref name="weatherTime"/> is only read when
    /// <see cref="EclipseEventSettings.RefuelClock"/> is Weather. <paramref name="localThirdContactCelestial"/> (optional) sets the Return length.
    /// </summary>
    public EclipseEventState Evaluate(double celestialTime, double weatherTime, double localThirdContactCelestial = double.NaN)
    {
        double tau = celestialTime - Window.Start;
        double grid = GridTime(celestialTime, weatherTime);
        EclipseEventPhase phase = PhaseAt(tau, out double ps, out double pe, localThirdContactCelestial);
        if (phase == EclipseEventPhase.Idle) return Idle(tau, grid);
        SyncOutputs();

        double l0 = _s.KindleLevel;
        double level = 0d;
        double arrived = 0d;
        if (tau >= TauKindle) level = l0 * Smooth01((tau - TauKindle) / KindleDuration);
        if (tau >= TauIgnition)
        {
            arrived = ArrivedWeight(tau);
            level = l0 + (1d - l0) * Ramp(arrived / Sites.Count);
        }
        double breath = 0d, heart = 0d, flicker = 1d, shock = 0d;
        if (tau >= TauCrescendo && tau < TauOverload)
        {
            breath = _s.BreathAmplitude * Math.Sin(Math.Tau * (tau - TauCrescendo) / _s.BreathPeriodSeconds);
            heart = HeartbeatAt(tau, grid);
            level += breath + heart;
        }
        double bodyBoost = 1d;
        if (tau >= TauInstability && tau < TauOverload)
        {
            double x = tau - TauInstability, u = x / InstabilityDuration;
            double phi = _s.FlickerHzStart * x + (_s.FlickerHzEnd - _s.FlickerHzStart) * x * x / (2d * InstabilityDuration);
            flicker = 1d - _s.FlickerMax * u * u * Noise(phi);
            level *= flicker;
        }
        else if (tau >= TauOverload && tau < TauImplosion)
        {
            double u = (tau - TauOverload) / OverloadDuration;
            double swell = Math.Sin(Math.PI * 0.5d * u);
            level = _fullLevel * (1d + (_s.OverloadGain - 1d) * swell);
            bodyBoost = 1d + 0.6d * swell;
        }
        else if (tau >= TauImplosion && tau < TauShockwave)
        {
            double u = (tau - TauImplosion) / ImplosionDuration, r = 1d - u;
            level = _fullLevel * _s.OverloadGain * r * r * r;
            bodyBoost = (1d + 0.6d) * (1d - u * u);
        }
        else if (tau >= TauShockwave) level = 0d;
        if (tau >= TauShockwave && tau < TauAftermath)
        {
            double dt = tau - TauShockArrival;
            if (dt >= 0d && dt < _s.ShockFlashSeconds) shock = Math.Sin(Math.PI * dt / _s.ShockFlashSeconds);
        }
        level = Math.Max(0d, level);

        double body = 0d;
        if (tau >= TauKindle && tau < TauShockwave)
            body = Smooth01((tau - TauKindle) / KindleDuration) * (0.5d + 0.5d * Math.Sqrt(Math.Min(level, 1d))) * bodyBoost;

        double hubDirect = _s.RealSunEnergy * _s.HubDirectFraction * level + _s.ShockFlashLevel * shock;
        double hubDay = Math.Min(1d, _s.HubDayAtFull * level + 0.5d * _s.ShockFlashLevel * shock);
        double weight = tau >= 0d && tau < TauEnd ? 1d : 0d;

        bool mainLit = tau >= TauMainFire && tau < TauImplosion + BeamRetract;
        double tip = tau < TauMainFire ? 0d : Math.Pow(Math.Clamp((tau - TauMainFire) / MainClimb, 0d, 1d), _s.MainClimbExponent);
        double cut = tau < TauImplosion ? 0d : Math.Clamp((tau - TauImplosion) / BeamRetract, 0d, 1d);

        int fired = CountFired(tau);
        int litRegular = tau >= TauImplosion || tau >= TauSustain ? 0 : fired;
        double regIntensity = tau < TauHandoff ? 1d : 1d - Smooth01((tau - TauHandoff) / HandoffFade);
        if (tau < TauIgnition || tau >= TauSustain) regIntensity = tau < TauIgnition ? 1d : 0d;

        double travel = tau - TauIgnition;
        bool fronts = tau >= TauIgnition && travel <= IgnitionHalfRingSeconds + _s.MeetingFlareSeconds;
        double head = travel <= 0d ? 0d : Math.Min(IgnitionSpeedMetersPerSecond * travel, _c * 0.5d);
        double meet = 0d;
        if (travel >= IgnitionHalfRingSeconds && _s.MeetingFlareSeconds > 0d)
            meet = 1d - Smooth01((travel - IgnitionHalfRingSeconds) / _s.MeetingFlareSeconds);
        double plateau = tau < TauIgnition || tau >= TauSustain ? 0d
            : tau < TauHandoff ? _s.IgnitionPlateauLevel
            : _s.IgnitionPlateauLevel * (1d - Smooth01((tau - TauHandoff) / HandoffFade));
        double gridWeight = _s.SuppressGridPulseDuringIgnition && tau >= TauMainFire && tau < TauSustain ? 0d : 1d;

        return new EclipseEventState(phase, tau, tau - ps, pe - tau, level, hubDirect, hubDay, weight, body,
            flicker, breath, heart, shock, arrived, fired, litRegular, regIntensity,
            mainLit, tip, cut, fronts, head, tau >= TauIgnition ? _c - head : 0d, meet, plateau, gridWeight, grid);
    }

    private EclipseEventState Idle(double tau, double grid)
        => new(EclipseEventPhase.Idle, tau, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 1d, 0d, 0d, 0d, 0d, 0, 0, 1d,
            false, 0d, 0d, false, 0d, 0d, 0d, 0d, 1d, grid);

    /// <summary>Hub heartbeat (spec B.3): kick per grid row landing plus the main relaunch flare, counting only landings during Sustain.</summary>
    private double HeartbeatAt(double tau, double gridTime)
    {
        double sum = 0d;
        int columns = Sites.Columns, counted = 0;
        double v = _clock.SpeedMetersPerSecond;
        Span<EmitterPulseFront> fronts = stackalloc EmitterPulseFront[16];
        for (int col = 0; col < columns; col++)
        {
            double[] qs = _colQ[col];
            if (qs.Length == 0) continue;
            counted++;
            int n = _clock.GetFronts(gridTime, col, fronts);
            for (int j = 0; j < n; j++)
            {
                double head = fronts[j].HeadQ;
                if (head < qs[0]) continue;
                int k = UpperBound(qs, head) - 1;
                double age = (head - qs[k]) / v;
                if (tau - age >= TauSustain) sum += Math.Exp(-Math.Max(0d, age - _s.FlashClimbSeconds) / _s.HeartbeatTauSeconds);
                break;
            }
        }
        double heart = counted == 0 ? 0d : _s.HeartbeatKick * sum / counted;
        double sinceLaunch = _clock.SecondsSinceLaunch(gridTime);
        if (tau - sinceLaunch >= TauSustain) heart += _s.RelaunchKick * _clock.FlareEnvelope(gridTime);
        return heart;
    }

    /// <summary>Deterministic value noise in [0, 1] hashed from (seed, eclipse index, lattice cell), spec B.3 Instability.</summary>
    private double Noise(double x)
    {
        double fl = Math.Floor(x), t = x - fl;
        int i = (int)fl;
        double a = Lattice(i), b = Lattice(i + 1);
        return a + (b - a) * (t * t * (3d - 2d * t));
    }

    private double Lattice(int i)
        => EmitterSiteHash.Unit(EmitterSiteHash.Derive(Seed + 7919L * Window.Index, "eclipse-flicker", i));

    // ---------------------------------------------------------------- helpers

    internal static double Smooth01(double x)
    {
        x = Math.Clamp(x, 0d, 1d);
        return x * x * (3d - 2d * x);
    }

    /// <summary>Number of elements &lt;= value in an ascending array.</summary>
    internal static int UpperBound(double[] a, double value)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (a[mid] <= value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
