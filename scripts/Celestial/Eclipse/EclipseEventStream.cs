using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Celestial.Eclipse;

public enum EclipseEventKind : byte
{
    PhaseChanged = 0, TotalityWarning, LocalTotality, LocalThirdContact,
    MainBeamFired, HubKindled, IgnitionLaunched, SiteFired, NearestSpireFired,
    FrontsMet, FullIgnition, HandoffComplete, RefuelFlash,
    InstabilityBegan, Overload, Implosion, ShockwaveArrival, BeamsCut, Resync
}

/// <summary>
/// One discrete event (spec F.1). <see cref="Value"/> depends on the kind: minutes (TotalityWarning), hub level in percent
/// (HubKindled, FullIgnition, HandoffComplete, Resync), seconds to the next phase (PhaseChanged), or seconds to the player's local
/// third contact (InstabilityBegan .. BeamsCut, ShockwaveArrival; NaN when the observer supplied none). <see cref="Site"/> is the
/// registry site index (-1 when not site related); distance/bearing are measured from the observer (bearing 0 = +around, 90 = +across).
/// </summary>
public readonly record struct EclipseEvent(
    EclipseEventKind Kind, double CelestialTime, EclipseEventPhase Phase, EclipseEventPhase FromPhase,
    int Site, int Column, float DistanceKm, float BearingDegrees, float Value);

/// <summary>Player position in ring coordinates plus optional local contacts (absolute celestial seconds, NaN = unknown).</summary>
public readonly record struct EclipseObserver(double AroundMeters, double AcrossMeters,
    double LocalSecondContact = double.NaN, double LocalThirdContact = double.NaN);

public sealed record EclipseEventStreamSettings
{
    public double AudibleRadiusKm { get; init; } = 40d;
    public int NearestSiteCount { get; init; } = 6;
    public double RefuelRateLimitSeconds { get; init; } = 60d;
    public double FirstWarningLeadSeconds { get; init; } = 300d;
    public double SecondWarningLeadSeconds { get; init; } = 60d;
    /// <summary>Weather-clock jump (or rewind) treated as a discontinuity when the refuel clock is Weather.</summary>
    public double WeatherJumpSeconds { get; init; } = 5d;
}

/// <summary>
/// Discrete event stream over an <see cref="EclipseEventTimeline"/> (spec F.1). <see cref="Advance"/> returns the events whose time lies in
/// (prev, now]; the intervals of consecutive frames partition time, so every one-shot fires exactly once at any frame rate. A rewind, a
/// first call (<c>prev</c> not finite: load), or a step larger than <c>maxContinuousStepSeconds</c> is a discontinuity and produces a single
/// <see cref="EclipseEventKind.Resync"/> instead of replaying anything in between.
/// <para>The director must pass <c>maxContinuousStepSeconds = max(5, 0.5 * TimeScale)</c>: with item 6 a 100x frame legitimately advances
/// 1.7 s per 60 fps frame, so a fixed 5 s would misfire on slow frames at high speed.</para>
/// </summary>
public sealed class EclipseEventStream
{
    private readonly EclipseEventTimeline _t;
    private readonly EclipseEventStreamSettings _s;
    private readonly (double Tau, EclipseEventKind Kind, EclipseEventPhase Phase, float Value, bool Main, bool SecondsToThird)[] _scheduled;
    private bool _nearestDone;
    private double _lastRefuel = double.NegativeInfinity;
    private EclipseObserver _cachedObserver;
    private bool _haveNearest;
    private int _nearestEntry = -1;

    public EclipseEventStream(EclipseEventTimeline timeline, EclipseEventStreamSettings? settings = null)
    {
        _t = timeline ?? throw new ArgumentNullException(nameof(timeline));
        _s = settings ?? new EclipseEventStreamSettings();
        var list = new List<(double, EclipseEventKind, EclipseEventPhase, float, bool, bool)>
        {
            (_t.TauMainFire, EclipseEventKind.MainBeamFired, EclipseEventPhase.MainFire, 0f, true, false),
            (_t.TauKindled, EclipseEventKind.HubKindled, EclipseEventPhase.Kindle, (float)(_t.Settings.KindleLevel * 100d), true, false),
            (_t.TauIgnition, EclipseEventKind.IgnitionLaunched, EclipseEventPhase.Ignition, 0f, true, false),
            (_t.TauFrontsMet, EclipseEventKind.FrontsMet, EclipseEventPhase.Ignition, 0f, false, false),
            (_t.TauCrescendo, EclipseEventKind.FullIgnition, EclipseEventPhase.Crescendo, 0f, false, false),
            (_t.TauSustain, EclipseEventKind.HandoffComplete, EclipseEventPhase.Sustain, 0f, false, false),
            (_t.TauInstability, EclipseEventKind.InstabilityBegan, EclipseEventPhase.Instability, 0f, false, true),
            (_t.TauOverload, EclipseEventKind.Overload, EclipseEventPhase.Overload, 0f, false, true),
            (_t.TauImplosion, EclipseEventKind.Implosion, EclipseEventPhase.Implosion, 0f, false, true),
            (_t.TauImplosion, EclipseEventKind.BeamsCut, EclipseEventPhase.Implosion, 0f, false, true),
            (_t.TauShockArrival, EclipseEventKind.ShockwaveArrival, EclipseEventPhase.Shockwave, 0f, false, true),
        };
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        _scheduled = list.ToArray();
    }

    public void Reset()
    {
        _nearestDone = false; _lastRefuel = double.NegativeInfinity; _haveNearest = false;
    }

    /// <summary>Appends the events in (prevCelestial, celestial] to <paramref name="output"/> in time order and returns how many were added.</summary>
    public int Advance(double prevCelestial, double celestial, double prevWeather, double weather, in EclipseObserver observer,
        List<EclipseEvent> output, double maxContinuousStepSeconds = 5d)
    {
        if (!_t.Active || !double.IsFinite(celestial)) return 0;
        int start = output.Count;
        double s0 = _t.Window.Start;
        bool weatherMode = _t.Settings.RefuelClock == EclipseRefuelClock.Weather;
        bool disc = !double.IsFinite(prevCelestial) || celestial < prevCelestial
            || celestial - prevCelestial > Math.Max(0d, maxContinuousStepSeconds)
            || (weatherMode && (!double.IsFinite(prevWeather) || !double.IsFinite(weather) || weather < prevWeather
                || weather - prevWeather > _s.WeatherJumpSeconds));
        if (disc)
        {
            EclipseEventPhase now = _t.PhaseAt(celestial - s0, observer.LocalThirdContact);
            EclipseEventPhase before = double.IsFinite(prevCelestial) ? _t.PhaseAt(prevCelestial - s0, observer.LocalThirdContact) : EclipseEventPhase.Idle;
            RestoreFlags(celestial - s0, observer);
            if (now == EclipseEventPhase.Idle && before == EclipseEventPhase.Idle) return 0;
            EclipseEventState st = _t.Evaluate(celestial, weather, observer.LocalThirdContact);
            output.Add(new EclipseEvent(EclipseEventKind.Resync, celestial, now, before, -1, -1, 0f, 0f, (float)(st.Level * 100d)));
            return 1;
        }
        if (celestial == prevCelestial) return 0;

        double tp = prevCelestial - s0, tc = celestial - s0;

        // Player-local contacts and warnings.
        if (double.IsFinite(observer.LocalSecondContact))
        {
            EmitLocal(output, observer.LocalSecondContact - _s.FirstWarningLeadSeconds, EclipseEventKind.TotalityWarning, 5f, prevCelestial, celestial);
            EmitLocal(output, observer.LocalSecondContact - _s.SecondWarningLeadSeconds, EclipseEventKind.TotalityWarning, 1f, prevCelestial, celestial);
            EmitLocal(output, observer.LocalSecondContact, EclipseEventKind.LocalTotality, 0f, prevCelestial, celestial);
        }
        if (double.IsFinite(observer.LocalThirdContact))
            EmitLocal(output, observer.LocalThirdContact, EclipseEventKind.LocalThirdContact, 0f, prevCelestial, celestial);

        // Phase boundaries.
        IReadOnlyList<EclipsePhaseBoundary> bounds = _t.Boundaries;
        for (int i = 0; i < bounds.Count; i++)
        {
            bool last = i == bounds.Count - 1;
            double tau = last ? _t.ReturnEndTau(observer.LocalThirdContact) : bounds[i].StartTau;
            if (!(tp < tau && tau <= tc)) continue;
            double next = i + 1 < bounds.Count ? (i + 1 == bounds.Count - 1 ? _t.ReturnEndTau(observer.LocalThirdContact) : bounds[i + 1].StartTau) : double.NaN;
            output.Add(new EclipseEvent(EclipseEventKind.PhaseChanged, s0 + tau, bounds[i].Phase,
                i == 0 ? EclipseEventPhase.Idle : bounds[i - 1].Phase, -1, -1, 0f, 0f, double.IsNaN(next) ? float.NaN : (float)(next - tau)));
        }

        // Scheduled one-shots.
        foreach (var e in _scheduled)
        {
            if (!(tp < e.Tau && e.Tau <= tc)) continue;
            float dist = 0f, bearing = 0f;
            if (e.Main) _t.Sites.MainDistanceBearing(observer.AroundMeters, observer.AcrossMeters, out dist, out bearing);
            float value = e.Value;
            if (e.Kind == EclipseEventKind.FullIgnition || e.Kind == EclipseEventKind.HandoffComplete) value = (float)(_t.FullLevel * 100d);
            if (e.SecondsToThird) value = double.IsFinite(observer.LocalThirdContact) ? (float)(observer.LocalThirdContact - (s0 + e.Tau)) : float.NaN;
            output.Add(new EclipseEvent(e.Kind, s0 + e.Tau, e.Phase, e.Phase, -1, -1, dist, bearing, value));
        }

        // Site fires.
        ReadOnlySpan<double> fire = _t.SortedFireTau;
        ReadOnlySpan<int> order = _t.FireOrder;
        int lo = UpperBound(fire, tp), hi = UpperBound(fire, tc);
        for (int k = lo; k < hi; k++)
        {
            int entry = order[k];
            if (!_t.SiteAlive(entry)) continue;
            _t.Sites.DistanceBearing(entry, observer.AroundMeters, observer.AcrossMeters, out float dist, out float bearing);
            int rank = 0;
            for (int j = 0; j < _t.Sites.Count && rank < _s.NearestSiteCount; j++)
            {
                if (j == entry || !_t.SiteAlive(j)) continue;
                _t.Sites.DistanceBearing(j, observer.AroundMeters, observer.AcrossMeters, out float d2, out _);
                if (d2 < dist || (d2 == dist && j < entry)) rank++;
            }
            bool qualifies = rank < _s.NearestSiteCount || dist <= _s.AudibleRadiusKm;
            if (!qualifies) continue;
            int site = _t.Sites.SiteIndex[entry], col = _t.Sites.Column[entry];
            double t = s0 + _t.SiteFireTau(entry);
            output.Add(new EclipseEvent(EclipseEventKind.SiteFired, t, EclipseEventPhase.Ignition, EclipseEventPhase.Ignition, site, col, dist, bearing, 0f));
            if (rank == 0 && !_nearestDone)
            {
                _nearestDone = true;
                output.Add(new EclipseEvent(EclipseEventKind.NearestSpireFired, t, EclipseEventPhase.Ignition, EclipseEventPhase.Ignition, site, col, dist, bearing, 0f));
            }
        }

        // Refuel flash of the nearest site (rate limited).
        if (tc > _t.TauSustain && tp < _t.TauInstability) EmitRefuel(output, prevCelestial, celestial, prevWeather, weather, observer);

        SortSegment(output, start);
        return output.Count - start;
    }

    private void EmitLocal(List<EclipseEvent> output, double time, EclipseEventKind kind, float value, double prev, double now)
    {
        if (!(prev < time && time <= now)) return;
        EclipseEventPhase phase = _t.PhaseAt(time - _t.Window.Start);
        output.Add(new EclipseEvent(kind, time, phase, phase, -1, -1, 0f, 0f, value));
    }

    private void EmitRefuel(List<EclipseEvent> output, double prevCel, double cel, double prevW, double w, in EclipseObserver observer)
    {
        int entry = Nearest(observer);
        if (entry < 0) return;
        bool weatherMode = _t.Settings.RefuelClock == EclipseRefuelClock.Weather;
        double gp = weatherMode ? prevW : prevCel, gc = weatherMode ? w : cel, offset = cel - gc;
        long n = _t.RefuelLaunchAtOrAfter(entry, gc);
        if (_t.RefuelArrivalTime(entry, n) > gc) n--;
        for (int guard = 0; guard < 4; guard++, n--)
        {
            double a = _t.RefuelArrivalTime(entry, n);
            if (a <= gp) break;
            double celT = a + offset, tau = celT - _t.Window.Start;
            if (tau < _t.TauSustain || tau >= _t.TauInstability) continue;
            if (celT - _lastRefuel < _s.RefuelRateLimitSeconds) continue;
            _lastRefuel = celT;
            _t.Sites.DistanceBearing(entry, observer.AroundMeters, observer.AcrossMeters, out float dist, out float bearing);
            output.Add(new EclipseEvent(EclipseEventKind.RefuelFlash, celT, EclipseEventPhase.Sustain, EclipseEventPhase.Sustain,
                _t.Sites.SiteIndex[entry], _t.Sites.Column[entry], dist, bearing, 0f));
        }
    }

    private int Nearest(in EclipseObserver observer)
    {
        if (_haveNearest && _cachedObserver.AroundMeters == observer.AroundMeters && _cachedObserver.AcrossMeters == observer.AcrossMeters)
            return _nearestEntry;
        float best = float.MaxValue; int bestEntry = -1;
        for (int j = 0; j < _t.Sites.Count; j++)
        {
            if (!_t.SiteAlive(j)) continue;
            _t.Sites.DistanceBearing(j, observer.AroundMeters, observer.AcrossMeters, out float d, out _);
            if (d < best) { best = d; bestEntry = j; }
        }
        _cachedObserver = observer; _haveNearest = true; _nearestEntry = bestEntry;
        return bestEntry;
    }

    /// <summary>After a discontinuity: one-shot flags reflect what already happened at the new time, so nothing is replayed later.</summary>
    private void RestoreFlags(double tau, in EclipseObserver observer)
    {
        _lastRefuel = double.NegativeInfinity;
        int nearest = Nearest(observer);
        _nearestDone = nearest < 0 || _t.SiteFireTau(nearest) <= tau;
    }

    private static void SortSegment(List<EclipseEvent> list, int start)
    {
        for (int i = start + 1; i < list.Count; i++)
        {
            EclipseEvent v = list[i];
            int j = i - 1;
            while (j >= start && list[j].CelestialTime > v.CelestialTime) { list[j + 1] = list[j]; j--; }
            list[j + 1] = v;
        }
    }

    private static int UpperBound(ReadOnlySpan<double> a, double value)
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
