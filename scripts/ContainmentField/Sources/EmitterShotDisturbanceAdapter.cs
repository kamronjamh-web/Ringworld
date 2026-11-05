using System;
using System.Collections.Generic;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.ContainmentField;

/// <summary>
/// An emitter's orb reaching the field (docs/containment-field-design.md 4.2, emitter shots): one lightning-like ripple (kind EmitterShot) per shot at the contact
/// point, started at the orb's arrival, SourceId = the shot id. The schedule is pure, so the adapter only asks it for the arrivals in (last time, now]: a paused
/// clock publishes nothing, a backwards jump re-arms with the last <see cref="EmitterFieldShotSchedule.RippleDurationSeconds"/> so ripples still alive at the
/// new time come back (the bus ignores ids it still holds). Only contacts within the bus range of the observer are published.
/// </summary>
public sealed class EmitterShotDisturbanceAdapter
{
    public const double MaxCatchUpSeconds = 2.0;

    private readonly IContainmentDisturbanceSink _sink;
    private readonly List<EmitterFieldShot> _arrivals = new();
    private double _lastTime = double.NaN;

    public Func<EmitterFieldShotSchedule?> ScheduleProvider { get; set; }
    public double CircumferenceMeters { get; set; }
    public int Published { get; private set; }
    public int Rejected { get; private set; }
    public int OutOfRange { get; private set; }
    public long LastPublished { get; private set; }

    public EmitterShotDisturbanceAdapter(IContainmentDisturbanceSink sink, Func<EmitterFieldShotSchedule?> schedule, double circumferenceMeters)
    {
        _sink = sink; ScheduleProvider = schedule; CircumferenceMeters = circumferenceMeters;
    }

    /// <summary>Publishes the ripples of the arrivals in the window; returns how many the sink accepted.</summary>
    public int Update(double now, in RingCanonicalPosition observer)
    {
        EmitterFieldShotSchedule? schedule = double.IsFinite(now) ? ScheduleProvider() : null;
        if (schedule == null || schedule.Timing.Off)
        {
            if (double.IsFinite(now)) _lastTime = now;
            return 0;
        }
        double from;
        if (double.IsNaN(_lastTime) || now < _lastTime - 1e-9) from = now - EmitterFieldShotSchedule.RippleDurationSeconds;
        else if (now == _lastTime) return 0;
        else from = Math.Max(_lastTime, now - MaxCatchUpSeconds);
        _lastTime = now;

        EmitterFieldShotTiming timing = schedule.Timing;
        float hold = (float)Math.Max(timing.HoldSeconds, 0.0), fade = (float)Math.Max(timing.FadeSeconds, 0.0);
        int accepted = 0;
        schedule.CollectArrivals(from, now, _arrivals);
        foreach (EmitterFieldShot shot in _arrivals)
        {
            (double around, double vertical, double across) = schedule.ContactOf(shot.Beam);
            double da = CircumferenceMeters > 0.0 ? WrapSigned(around - observer.AroundMeters, CircumferenceMeters) : around - observer.AroundMeters, dz = across - observer.AcrossMeters;
            if (Math.Sqrt(da * da + dz * dz) > ContainmentDisturbanceBus.RangeMeters) { OutOfRange++; continue; }
            double arrival = shot.ArrivalTimeSeconds;
            var d = new ContainmentDisturbance(shot.Id, ContainmentDisturbanceKind.EmitterShot, new RingCanonicalPosition(around, vertical, across), arrival, shot.Energy,
                3000.0f + 5000.0f * shot.Energy, EmitterFieldShotSchedule.RippleDurationSeconds, 1.0f, t => EmitterFieldShotSchedule.FlareLevel(t - arrival, hold, fade));
            if (_sink.Publish(d)) { accepted++; Published++; LastPublished = shot.Id; } else Rejected++;
        }
        return accepted;
    }

    private static double WrapSigned(double v, double c) { double r = v % c; if (r > 0.5 * c) r -= c; else if (r < -0.5 * c) r += c; return r; }
}
