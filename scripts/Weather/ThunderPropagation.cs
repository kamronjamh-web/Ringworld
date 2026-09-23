using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Weather;

/// <summary>Physical limits used by the pure thunder scheduler.  Vertical coordinates are metres inward from the ring datum.</summary>
public readonly record struct ThunderAcousticContext(
    double CircumferenceMeters,
    double WidthMeters,
    double AtmosphereHeightMeters,
    double MaxDistanceMeters = 25_000.0,
    double SoundSpeedMetersPerSecond = 343.0,
    double MinimumAltitudeMeters = -2_000.0,
    double DatumInwardOffsetMeters = 0.0);

/// <summary>A single audible arrival. LateSeconds lets an audio player seek rather than replay a missed front.</summary>
public readonly record struct ThunderArrival(
    RingLightningEvent Strike,
    RingCanonicalPosition SourcePosition,
    double DistanceMeters,
    double TravelSeconds,
    double LateSeconds);

/// <summary>
/// Bounded, renderer-independent propagation of thunder through the habitable ring's atmosphere.
/// This is intentionally only geometric propagation; terrain and shelter occlusion belong to a later phase.
/// </summary>
public sealed class ThunderPropagation
{
    public const int MaxPendingEvents = 128;
    public const int MaxHistoryEntries = 512;
    public const double SourceClearanceMeters = 2.0;
    public const double ExpiryGraceSeconds = 2.0;
    private readonly List<Pending> _pending = new();
    private readonly HashSet<long> _pendingIds = new();
    private readonly Dictionary<long, double> _history = new();
    private readonly List<long> _expiredIds = new();
    private double _lastAudioTime = double.NegativeInfinity;

    public int PendingCount => _pending.Count;
    public int HistoryCount => _history.Count;

    /// <summary>
    /// Registers a visual strike. emissionAudioTime is on the real active-playback audio clock, not the weather clock.
    /// The initial listener is used only for inaudible-distance gating and bounded-queue priority.
    /// </summary>
    public bool Observe(in RingLightningEvent strike, double emissionAudioTime,
        in RingCanonicalPosition initialListener, in ThunderAcousticContext context)
    {
        // Active visual events are observed every frame. Reject duplicates before
        // geometry so that observation remains O(1) after the first frame.
        if (_history.ContainsKey(strike.EventId) || ContainsPending(strike.EventId)) return false;
        if (!IsFinite(emissionAudioTime) || strike.EventId <= 0 || !TryGetSource(strike, context, out RingCanonicalPosition source)
            || !TryDistance(source, initialListener, context, out double distance))
            return false;

        double lifetime = MaximumLifetime(context);
        if (!IsFinite(lifetime)) return false;
        double expiry = emissionAudioTime + lifetime;
        if (!IsFinite(expiry)) return false;
        var candidate = new Pending(strike, source, emissionAudioTime, expiry, distance);
        if (_pending.Count >= MaxPendingEvents)
        {
            int farthest = FindFarthestPending();
            if (farthest < 0 || _pending[farthest].InitialDistanceMeters <= distance) return false;
            _pendingIds.Remove(_pending[farthest].Strike.EventId);
            _pending[farthest] = candidate;
        }
        else
        {
            _pending.Add(candidate);
        }
        _pendingIds.Add(strike.EventId);
        AddHistory(strike.EventId, expiry);
        return true;
    }

    public bool TryEnqueue(in RingLightningEvent strike, double emissionAudioTime,
        in RingCanonicalPosition initialListener, in ThunderAcousticContext context) =>
        Observe(strike, emissionAudioTime, initialListener, context);

    /// <summary>Advances the expanding sound front and appends each event at most once.</summary>
    public void Advance(double audioNow, in RingCanonicalPosition listener, in ThunderAcousticContext context,
        bool listenerCanHear, List<ThunderArrival> output)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (!IsFinite(audioNow) || !IsValidContext(context)) return;
        if (audioNow < _lastAudioTime)
        {
            // The caller restarted its active audio clock. Old timing cannot be
            // interpreted safely against the new epoch.
            Reset();
        }
        _lastAudioTime = audioNow;
        Cleanup(audioNow);
        if (!listenerCanHear || !IsValidPosition(listener, context)) return;

        for (int index = _pending.Count - 1; index >= 0; index--)
        {
            Pending pending = _pending[index];
            if (!TryDistance(pending.SourcePosition, listener, context, out double distance)) continue;
            double travel = distance / context.SoundSpeedMetersPerSecond;
            double elapsed = Math.Max(0.0, audioNow - pending.EmissionAudioTime);
            if (elapsed < travel) continue;
            output.Add(new ThunderArrival(pending.Strike, pending.SourcePosition, distance, travel, elapsed - travel));
            _pendingIds.Remove(pending.Strike.EventId);
            _pending.RemoveAt(index);
        }
    }

    public void Reset()
    {
        _pending.Clear();
        _pendingIds.Clear();
        _history.Clear();
        _expiredIds.Clear();
        _lastAudioTime = double.NegativeInfinity;
    }

    public static bool TryDistance(in RingCanonicalPosition source, in RingCanonicalPosition listener,
        in ThunderAcousticContext context, out double distanceMeters)
    {
        distanceMeters = 0.0;
        if (!IsValidContext(context) || !IsValidPosition(source, context) || !IsValidPosition(listener, context)) return false;
        double radius = context.CircumferenceMeters / Math.Tau;
        double datumRadius = radius - context.DatumInwardOffsetMeters;
        double sourceRadius = datumRadius - source.VerticalMeters;
        double listenerRadius = datumRadius - listener.VerticalMeters;
        double angle = ShortestAngle(source.AroundMeters, listener.AroundMeters, context.CircumferenceMeters);
        double across = source.AcrossMeters - listener.AcrossMeters;
        // Relative-angle chord math avoids subtracting two planet-scale floats.
        double radialDelta = sourceRadius - listenerRadius;
        double sine = Math.Sin(angle * 0.5);
        double squared = radialDelta * radialDelta + 4.0 * sourceRadius * listenerRadius * sine * sine + across * across;
        if (!IsFinite(squared)) return false;
        distanceMeters = Math.Sqrt(squared);
        if (SegmentCrossesVacuumRelative(sourceRadius, listenerRadius, angle, datumRadius - context.AtmosphereHeightMeters)) return false;
        return IsFinite(distanceMeters) && distanceMeters <= context.MaxDistanceMeters;
    }

    private static bool TryGetSource(in RingLightningEvent strike, in ThunderAcousticContext context,
        out RingCanonicalPosition source)
    {
        // Point-source approximation: thunder originates at the ground endpoint,
        // raised slightly clear of terrain. This is not full-channel emission;
        // cloud height is deliberately not the acoustic source in this phase.
        source = strike.GroundPosition with { VerticalMeters = strike.GroundPosition.VerticalMeters + SourceClearanceMeters };
        return IsFinite(strike.StartTimeSeconds) && IsFinite(strike.Energy) && strike.Energy > 0.0
            && IsFinite(strike.FlashRadiusMeters) && strike.FlashRadiusMeters >= 0.0
            && IsFinite(strike.PlaneAngleRadians) && IsFinite(strike.CloudPosition.AroundMeters)
            && IsFinite(strike.CloudPosition.VerticalMeters) && IsFinite(strike.CloudPosition.AcrossMeters)
            && IsValidPosition(source, context);
    }

    private static bool IsValidContext(in ThunderAcousticContext context) =>
        IsFinite(context.CircumferenceMeters) && context.CircumferenceMeters > 0.0
        && IsFinite(context.WidthMeters) && context.WidthMeters > 0.0
        && IsFinite(context.AtmosphereHeightMeters) && context.AtmosphereHeightMeters > 0.0
        && IsFinite(context.MaxDistanceMeters) && context.MaxDistanceMeters > 0.0
        && IsFinite(context.SoundSpeedMetersPerSecond) && context.SoundSpeedMetersPerSecond > 0.0
        && IsFinite(context.MinimumAltitudeMeters) && context.MinimumAltitudeMeters <= 0.0
        && context.MinimumAltitudeMeters < context.AtmosphereHeightMeters
        && IsFinite(context.DatumInwardOffsetMeters)
        && context.AtmosphereHeightMeters < context.CircumferenceMeters / Math.Tau - context.DatumInwardOffsetMeters;

    private static bool IsValidPosition(in RingCanonicalPosition position, in ThunderAcousticContext context) =>
        IsFinite(position.AroundMeters) && IsFinite(position.VerticalMeters) && IsFinite(position.AcrossMeters)
        && position.VerticalMeters >= context.MinimumAltitudeMeters && position.VerticalMeters <= context.AtmosphereHeightMeters
        && position.AcrossMeters >= 0.0 && position.AcrossMeters <= context.WidthMeters;

    private static double ShortestAngle(double firstAround, double secondAround, double circumference)
    {
        double delta = (firstAround - secondAround) % circumference;
        if (delta > circumference * 0.5) delta -= circumference;
        if (delta < -circumference * 0.5) delta += circumference;
        return delta / (circumference / Math.Tau);
    }

    private static bool SegmentCrossesVacuumRelative(double sourceRadius, double listenerRadius, double angle, double vacuumRadius)
    {
        double ax = sourceRadius;
        double ay = 0.0;
        double bx = listenerRadius * Math.Cos(angle);
        double by = listenerRadius * Math.Sin(angle);
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared <= 0.0 ? 0.0 : Math.Clamp(-(ax * dx + ay * dy) / lengthSquared, 0.0, 1.0);
        double x = ax + dx * t;
        double y = ay + dy * t;
        return x * x + y * y < vacuumRadius * vacuumRadius;
    }

    private static double MaximumLifetime(in ThunderAcousticContext context) =>
        IsValidContext(context) ? context.MaxDistanceMeters / context.SoundSpeedMetersPerSecond + ExpiryGraceSeconds : double.NaN;

    private int FindFarthestPending()
    {
        int result = -1;
        double farthest = double.NegativeInfinity;
        for (int index = 0; index < _pending.Count; index++)
        {
            if (_pending[index].InitialDistanceMeters > farthest)
            {
                farthest = _pending[index].InitialDistanceMeters;
                result = index;
            }
        }
        return result;
    }

    private void Cleanup(double now)
    {
        if (!IsFinite(now)) return;
        for (int index = _pending.Count - 1; index >= 0; index--)
        {
            if (now < _pending[index].ExpiryAudioTime) continue;
            _pendingIds.Remove(_pending[index].Strike.EventId);
            _pending.RemoveAt(index);
        }
        if (_history.Count == 0) return;
        _expiredIds.Clear();
        foreach ((long id, double expiry) in _history)
            if (now >= expiry) _expiredIds.Add(id);
        foreach (long id in _expiredIds) _history.Remove(id);
    }

    private void AddHistory(long eventId, double expiry)
    {
        if (_history.Count >= MaxHistoryEntries)
        {
            long oldestId = -1;
            double oldestExpiry = double.PositiveInfinity;
            foreach ((long id, double value) in _history)
                if (!ContainsPending(id) && value < oldestExpiry) { oldestExpiry = value; oldestId = id; }
            if (oldestId >= 0) _history.Remove(oldestId);
        }
        _history[eventId] = expiry;
    }

    private bool ContainsPending(long eventId) => _pendingIds.Contains(eventId);

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private readonly record struct Pending(RingLightningEvent Strike, RingCanonicalPosition SourcePosition,
        double EmissionAudioTime, double ExpiryAudioTime, double InitialDistanceMeters);
}
