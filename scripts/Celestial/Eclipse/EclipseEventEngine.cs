using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Celestial.Eclipse;

/// <summary>The celestial clock as the event engine sees it (implemented by an adapter over <c>RingDayNightCycle</c>, or a stub in tests).</summary>
public interface IEclipseClock
{
    /// <summary>Absolute celestial seconds.</summary>
    double SimulationSeconds { get; }
    /// <summary>The user's TimeScale (1-100). The event follows it (user decision 6): there is no real-time hold.</summary>
    float TimeScale { get; }
    bool Paused { get; set; }
    /// <summary>A real clock jump (the same mechanism as <c>CelestialDebugHud.Jump</c>); the saved clock value is what moves.</summary>
    void SeekSimulationSeconds(double seconds);
}

/// <summary>Player pose for the event: canonical ring coordinates (site distance and bearing) and ring-frame geometry (local contacts).</summary>
public readonly record struct EclipseObserverPose(
    double AroundMeters, double AcrossMeters, double LongitudeRadians, double CentredAcrossKm, double RadiusKm, bool Valid)
{
    public static EclipseObserverPose None => new(0d, 0d, 0d, 0d, 0d, false);
}

public enum EclipseSolverState : byte { None = 0, Pending, Cached }

/// <summary>
/// Plain-C# core of the eclipse director (spec H, phase E1): picks the eclipse near the celestial clock, owns the window cache (async solve,
/// spec A.3), the <see cref="EclipseEventTimeline"/> and <see cref="EclipseEventStream"/>, and publishes <see cref="State"/> plus events
/// every <see cref="Update"/>. It never changes lighting or visuals and never writes the clock except through the explicit debug actions.
/// <para><b>Clocks (user decision 6).</b> The stream is advanced with <c>maxContinuousStepSeconds = max(5, 0.5 * TimeScale)</c>. Force, jump and
/// scrub move the celestial clock itself (<see cref="IEclipseClock.SeekSimulationSeconds"/>): the celestial clock is an absolute saved value,
/// so a seek is exactly what a save/load or the existing time-jump buttons do, and the event, being a pure function of that value, stays
/// deterministic. No debug offset is introduced. Each debug action also calls <see cref="MarkDiscontinuity"/> so the next update emits one Resync
/// even for a small step.</para>
/// <para><b>Grid clock hook (for E4).</b> While <see cref="GridClockOverrideActive"/> the on-screen grid pulse should be driven from
/// <see cref="GridTimeSeconds"/> (celestial time) instead of the weather clock, so the visible flashes coincide with the timeline at any
/// TimeScale. E1 only exposes it; nothing reads it yet.</para>
/// </summary>
public sealed class EclipseEventEngine
{
    public const double LocalContactRefreshSeconds = 10d;
    public const double ScrubTailSeconds = 120d;
    private const int LogCapacity = 32;

    private EclipseEventSettings _settings = new EclipseEventSettings().Sanitized();
    private EclipseWindowSolver? _solver;
    private readonly Dictionary<int, EclipseWindow> _windows = new();
    private readonly Dictionary<int, Task<EclipseWindow>> _pending = new();
    private EclipseSiteTable? _sites;
    private EmitterGridPulseClock? _gridClock;
    private IEclipseSiteOutput? _outputs;
    private long _seed;
    private EclipseEventTimeline? _timeline;
    private EclipseEventStream? _stream;
    private int _timelineIndex = -1;
    private EclipseEventWindow _timelineWindow;
    private bool _timelineDirty = true;
    private double _prevCelestial = double.NaN, _prevWeather = double.NaN;
    private double _contactsTime = double.NegativeInfinity;
    private int _contactsIndex = -1;
    private double _localSecond = double.NaN, _localThird = double.NaN;
    private readonly List<EclipseEvent> _frameEvents = new();
    private readonly Queue<EclipseEvent> _log = new();
    private EclipseObserverPose _pose = EclipseObserverPose.None;
    private EclipseEventState _state;
    private double _celestial = double.NaN, _weather = double.NaN;
    private float _timeScale = 1f;
    private bool _paused;

    /// <summary>When true, window solves run on the calling thread (tests, debug actions); otherwise on a worker (about 30 ms per eclipse).</summary>
    public bool SynchronousSolve { get; set; }
    public EclipseEventSettings Settings => _settings;
    public EclipseWindowSolver? Solver => _solver;
    public EclipseEventTimeline? Timeline => _timeline;
    public EclipseEventState State => _state;
    public EclipseEventPhase Phase => _state.Phase;
    public int EventIndex => _timelineIndex;
    public EclipseEventWindow Window => _timelineWindow;
    public bool Bound => _sites != null && _gridClock != null;
    public bool Active => _timeline?.Active == true;
    public double LocalSecondContact => _localSecond;
    public double LocalThirdContact => _localThird;
    public double CelestialSeconds => _celestial;
    public double WeatherSeconds => _weather;
    public float TimeScale => _timeScale;
    public IReadOnlyCollection<EclipseEvent> EventLog => _log;
    public long UpdateCount { get; private set; }
    public long ResyncCount { get; private set; }
    public long EventCount { get; private set; }
    /// <summary>Per-update event hook (E2-E6 consumers). Raised in time order from inside <see cref="Update"/>.</summary>
    public event Action<EclipseEvent>? EventRaised;

    /// <summary>True from MainFire to the end of the event while the refuel clock is Celestial: E4 drives the grid layer from <see cref="GridTimeSeconds"/>.</summary>
    public bool GridClockOverrideActive { get; private set; }
    /// <summary>The time the grid pulse maths should use (celestial seconds while the override is active; the weather clock otherwise).</summary>
    public double GridTimeSeconds => GridClockOverrideActive ? _state.GridTime : _weather;

    public EclipseSolverState SolverState
    {
        get
        {
            if (_solver == null) return EclipseSolverState.None;
            int k = _timelineIndex >= 0 ? _timelineIndex : (double.IsFinite(_celestial) ? _solver.NearestIndex(_celestial) : 0);
            return _windows.ContainsKey(k) ? EclipseSolverState.Cached : EclipseSolverState.Pending;
        }
    }

    // ---------------------------------------------------------------- wiring

    public void SetSettings(EclipseEventSettings settings)
    {
        EclipseEventSettings clean = (settings ?? throw new ArgumentNullException(nameof(settings))).Sanitized();
        if (clean == _settings) return;
        // The darkness model does not feed the timeline, so a darkness-only change must not rebuild it (the drawer sliders send many).
        if (clean with { Darkness = _settings.Darkness } == _settings) { _settings = clean; return; }
        bool marginChanged = clean.ParallaxSafetySeconds != _settings.ParallaxSafetySeconds;
        _settings = clean;
        _timelineDirty = true;
        if (marginChanged) { _windows.Clear(); _pending.Clear(); }
    }

    public void SetSolver(EclipseWindowSolver? solver)
    {
        if (ReferenceEquals(solver, _solver)) return;
        _solver = solver;
        _windows.Clear(); _pending.Clear();
        _timelineDirty = true; _contactsIndex = -1;
    }

    public void Bind(EclipseSiteTable? sites, EmitterGridPulseClock? gridClock, long seed, IEclipseSiteOutput? outputs = null)
    {
        if (ReferenceEquals(sites, _sites) && ReferenceEquals(gridClock, _gridClock) && seed == _seed && ReferenceEquals(outputs, _outputs)) return;
        _sites = sites; _gridClock = gridClock; _seed = seed; _outputs = outputs;
        _timelineDirty = true;
    }

    /// <summary>Makes the next <see cref="Update"/> treat the clock as a discontinuity: the stream emits exactly one Resync and replays nothing.</summary>
    public void MarkDiscontinuity() { _prevCelestial = double.NaN; _prevWeather = double.NaN; }

    public static double MaxContinuousStepSeconds(float timeScale)
        => Math.Max(5d, 0.5d * (float.IsFinite(timeScale) ? timeScale : 1f));

    // ---------------------------------------------------------------- windows

    private EclipseWindow? TryGetWindow(int index, bool wait)
    {
        if (_windows.TryGetValue(index, out EclipseWindow cached)) return cached;
        if (_solver == null) return null;
        double margin = _settings.ParallaxSafetySeconds;
        if (SynchronousSolve || wait)
        {
            _pending.Remove(index);
            EclipseWindow w = _solver.Solve(index, margin);
            _windows[index] = w;
            return w;
        }
        if (!_pending.TryGetValue(index, out Task<EclipseWindow>? task))
        {
            _pending[index] = _solver.SolveAsync(index, margin);
            return null;
        }
        if (!task.IsCompleted) return null;
        _pending.Remove(index);
        if (task.IsFaulted || task.IsCanceled) { _windows[index] = EclipseWindow.Invalid(index, _solver.CentreTime(index), margin); return _windows[index]; }
        _windows[index] = task.Result;
        return task.Result;
    }

    private bool EnsureTimeline(int index, bool wait)
    {
        if (_sites == null || _gridClock == null || _solver == null) return false;
        EclipseWindow? w = TryGetWindow(index, wait);
        if (w == null) return false;
        EclipseEventWindow window = EclipseEventWindow.From(w.Value);
        // record-struct equality uses double.Equals, so two invalid (NaN) windows compare equal.
        if (_timeline != null && _timelineIndex == index && !_timelineDirty && window.Equals(_timelineWindow)) return true;
        _timeline = new EclipseEventTimeline(_settings, window, _sites, _gridClock, _seed, _outputs);
        _stream = new EclipseEventStream(_timeline);
        _timelineIndex = index; _timelineWindow = window; _timelineDirty = false;
        MarkDiscontinuity();
        _contactsIndex = -1;
        return true;
    }

    // ---------------------------------------------------------------- per frame

    /// <summary>Evaluates the event at the clock's current time and publishes <see cref="State"/> and events. Call once per frame after the clock.</summary>
    public void Update(IEclipseClock clock, double weatherSeconds, in EclipseObserverPose pose)
    {
        UpdateCount++;
        _celestial = clock.SimulationSeconds; _weather = weatherSeconds; _timeScale = clock.TimeScale; _paused = clock.Paused;
        _pose = pose;
        _frameEvents.Clear();
        if (_solver == null || _sites == null || _gridClock == null || !double.IsFinite(_celestial)) { SetIdle(); return; }

        int index = _solver.NearestIndex(_celestial);
        if (!EnsureTimeline(index, wait: false) || _timeline == null || _stream == null) { SetIdle(); return; }
        if (!_timeline.Active) { SetIdle(); _prevCelestial = _celestial; return; }

        RefreshLocalContacts(index);
        double around = pose.Valid ? pose.AroundMeters : _sites.MainAroundMeters, across = pose.Valid ? pose.AcrossMeters : _sites.MainAcrossMeters;
        var observer = new EclipseObserver(around, across, _localSecond, _localThird);
        _stream.Advance(_prevCelestial, _celestial, _prevWeather, _weather, observer, _frameEvents, MaxContinuousStepSeconds(_timeScale));
        _prevCelestial = _celestial; _prevWeather = _weather;
        _state = _timeline.Evaluate(_celestial, _weather, _localThird);
        GridClockOverrideActive = _settings.RefuelClock == EclipseRefuelClock.Celestial && _timeline.Active
            && _state.Tau >= _timeline.TauMainFire && _state.Tau < _timeline.TauEnd;
        for (int i = 0; i < _frameEvents.Count; i++) Publish(_frameEvents[i]);
    }

    private void SetIdle()
    {
        _state = default;
        GridClockOverrideActive = false;
    }

    private void Publish(in EclipseEvent e)
    {
        EventCount++;
        if (e.Kind == EclipseEventKind.Resync) ResyncCount++;
        _log.Enqueue(e);
        while (_log.Count > LogCapacity) _log.Dequeue();
        EventRaised?.Invoke(e);
    }

    private void RefreshLocalContacts(int index)
    {
        if (_solver == null || !_pose.Valid) { _localSecond = _localThird = double.NaN; return; }
        if (_contactsIndex == index && Math.Abs(_celestial - _contactsTime) < LocalContactRefreshSeconds) return;
        EclipseLocalContacts c = _solver.LocalContacts(index, _pose.LongitudeRadians, _pose.CentredAcrossKm, _pose.RadiusKm);
        _localSecond = c.Valid ? c.SecondContact : double.NaN;
        _localThird = c.Valid ? c.ThirdContact : double.NaN;
        _contactsIndex = index; _contactsTime = _celestial;
    }

    // ---------------------------------------------------------------- debug actions (spec G.3; not saved)

    /// <summary>The eclipse a debug jump should target: the current one until its Return tail has passed, then the next.</summary>
    private int TargetIndex(double now)
    {
        if (_solver == null) return -1;
        int k = _solver.NearestIndex(now);
        EclipseWindow? w = TryGetWindow(k, wait: true);
        if (w is { Valid: true } win && now > win.RingEnd + _settings.ReturnTailSeconds) k++;
        return k;
    }

    /// <summary>The target event's tau range, solving synchronously when needed (a debug action may hitch about 30 ms).</summary>
    private bool PrepareTarget(IEclipseClock clock, out double start, out EclipseEventTimeline timeline)
    {
        start = double.NaN; timeline = null!;
        if (_solver == null || !Bound) return false;
        int k = TargetIndex(clock.SimulationSeconds);
        if (k < 0 || !EnsureTimeline(k, wait: true) || _timeline == null || !_timeline.Active) return false;
        timeline = _timeline; start = _timeline.Window.Start;
        return true;
    }

    /// <summary>Force eclipse now: moves the celestial clock to S - PreRoll of the current-or-next eclipse (a real clock jump).</summary>
    public bool ForceEclipseNow(IEclipseClock clock)
    {
        if (!PrepareTarget(clock, out double s, out EclipseEventTimeline t)) return false;
        SeekTau(clock, s, -t.Settings.PreRollSeconds);
        return true;
    }

    /// <summary>Jumps to the start of <paramref name="phase"/> in the current-or-next eclipse (a real clock jump that emits one Resync).</summary>
    public bool JumpToPhase(IEclipseClock clock, EclipseEventPhase phase)
    {
        if (!PrepareTarget(clock, out double s, out EclipseEventTimeline t)) return false;
        double tau;
        if (phase == EclipseEventPhase.Idle) tau = t.TauEnd + t.Settings.ReturnTailSeconds;
        else
        {
            tau = double.NaN;
            foreach (EclipsePhaseBoundary b in t.Boundaries) if (b.Phase == phase) { tau = b.StartTau; break; }
            if (double.IsNaN(tau)) return false;
        }
        SeekTau(clock, s, tau);
        return true;
    }

    /// <summary>Timeline scrub: jumps to event time <paramref name="tau"/> clamped to [-PreRoll, E - S + 120 s].</summary>
    public bool ScrubToTau(IEclipseClock clock, double tau)
    {
        if (!PrepareTarget(clock, out double s, out EclipseEventTimeline t) || !double.IsFinite(tau)) return false;
        SeekTau(clock, s, Math.Clamp(tau, -t.Settings.PreRollSeconds, t.TauEnd + ScrubTailSeconds));
        return true;
    }

    /// <summary>Scrub range [min, max] in event time for the current-or-next eclipse (false when no event is available).</summary>
    public bool TryGetScrubRange(out double minTau, out double maxTau)
    {
        minTau = maxTau = 0d;
        if (_timeline is not { Active: true }) return false;
        minTau = -_timeline.Settings.PreRollSeconds; maxTau = _timeline.TauEnd + ScrubTailSeconds;
        return true;
    }

    /// <summary>Freeze event: pauses or resumes the celestial clock (the weather clock and grid pulse keep running).</summary>
    public static void SetFrozen(IEclipseClock clock, bool frozen) => clock.Paused = frozen;

    /// <summary>Seeks to event time <paramref name="tau"/>; nudged up by ulps so that (S + tau) - S never rounds below a phase boundary.</summary>
    private void SeekTau(IEclipseClock clock, double windowStart, double tau)
    {
        double t = Math.Max(0d, windowStart + tau);
        if (windowStart + tau >= 0d) while (t - windowStart < tau) t = Math.BitIncrement(t);
        clock.SeekSimulationSeconds(t);
        MarkDiscontinuity();
    }

    // ---------------------------------------------------------------- status

    /// <summary>One block of live text for the grave drawer and the F9 HUD (spec G.2 status line).</summary>
    public string DescribeStatus()
    {
        var sb = new StringBuilder();
        if (!Bound) return "Eclipse event: unavailable (no emitter relay grid on this world; darkness only).";
        if (_solver == null) return "Eclipse event: waiting for the celestial clock.";
        if (_timeline == null) return $"Eclipse event: solver {SolverState} (celestial {Fmt(_celestial)}).";
        if (!_timeline.Active)
            return _settings.EventEnabled ? $"Eclipse event k={_timelineIndex}: no total eclipse window (event inactive). Solver {SolverState}."
                : $"Eclipse event k={_timelineIndex}: disabled in settings.";
        double s = _timeline.Window.Start, e = _timeline.Window.End;
        EclipseEventState st = _state;
        sb.Append($"Eclipse k={_timelineIndex}  S {Fmt(s)}  E {Fmt(e)}  (S {Countdown(s - _celestial)}, E {Countdown(e - _celestial)})\n");
        sb.Append($"local 2nd {Countdown(_localSecond - _celestial)}  3rd {Countdown(_localThird - _celestial)}\n");
        sb.Append($"phase {st.Phase}  +{st.PhaseElapsed:0.0}s  (next in {(double.IsInfinity(st.PhaseRemaining) ? "--" : st.PhaseRemaining.ToString("0.0") + "s")})  tau {st.Tau:0.0}\n");
        sb.Append($"hub L {st.Level:0.000}  energy {st.HubDirect:0.00}  day {st.HubDay:0.00}  weight {st.HubWeight:0.0}\n");
        sb.Append($"beams fired {st.FiredCount}/{_timeline.SiteCount}  lit {st.LitRegularBeams}  main {(st.MainBeamLit ? "on" : "off")}  fronts {(st.FrontsActive ? "live" : "off")}\n");
        sb.Append($"next refuel at nearest site {NextRefuelEta()}\n");
        sb.Append($"clocks: celestial {Fmt(_celestial)}  weather {Fmt(_weather)}  grid {(GridClockOverrideActive ? "celestial (override)" : "weather")}\n");
        sb.Append($"TimeScale {_timeScale:0}x{(_paused ? " PAUSED" : "")} (event follows it; no hold)  solver {SolverState}");
        if (_timeline.TimeCompression < 1d) sb.Append($"\nWARNING: window too short, stages compressed x{_timeline.TimeCompression:0.000}");
        return sb.ToString();
    }

    private string NextRefuelEta()
    {
        if (_timeline == null || _sites == null || !_pose.Valid || _state.Phase != EclipseEventPhase.Sustain) return "--";
        int best = -1; float bestKm = float.MaxValue;
        for (int i = 0; i < _sites.Count; i++)
        {
            _sites.DistanceBearing(i, _pose.AroundMeters, _pose.AcrossMeters, out float km, out _);
            if (km < bestKm) { bestKm = km; best = i; }
        }
        if (best < 0) return "--";
        double grid = _state.GridTime;
        long n = _timeline.RefuelLaunchAtOrAfter(best, grid);
        double eta = _timeline.RefuelArrivalTime(best, n) - grid;
        return $"{eta:0.0}s ({bestKm:0} km)";
    }

    /// <summary>Text strip along the loop (main spire at the left) showing the fraction of sites that have fired in each cell.</summary>
    public string BuildRingStrip(int cells = 48)
    {
        if (_timeline == null || _sites == null || !_timeline.Active || cells <= 0) return "";
        var fired = new int[cells]; var total = new int[cells];
        double c = _sites.CircumferenceMeters, tau = _state.Tau;
        for (int i = 0; i < _sites.Count; i++)
        {
            int cell = Math.Clamp((int)(_sites.P[i] / c * cells), 0, cells - 1);
            total[cell]++;
            if (_timeline.SiteFireTau(i) <= tau && _timeline.SiteAlive(i)) fired[cell]++;
        }
        const string ramp = " .:-=+*#%@";
        var sb = new StringBuilder(cells + 2).Append('[');
        for (int i = 0; i < cells; i++)
            sb.Append(total[i] == 0 ? ' ' : ramp[Math.Clamp(fired[i] * (ramp.Length - 1) / total[i], 0, ramp.Length - 1)]);
        return sb.Append(']').ToString();
    }

    private static string Fmt(double v) => double.IsFinite(v) ? v.ToString("0.0") : "--";

    private static string Countdown(double seconds)
    {
        if (!double.IsFinite(seconds)) return "--";
        string sign = seconds < 0d ? "-" : "+";
        TimeSpan span = TimeSpan.FromSeconds(Math.Abs(seconds));
        return span.TotalHours >= 1d ? $"{sign}{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{sign}{span.Minutes:00}:{span.Seconds:00}";
    }
}
