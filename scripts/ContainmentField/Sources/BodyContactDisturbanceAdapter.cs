using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.ContainmentField;

/// <summary>A fast body that may pass through the containment field (projectiles, debris later). Nothing but the player registers yet.</summary>
public interface IContainmentBody
{
    /// <summary>Stable id; with the crossing count it gives the ripple's SourceId.</summary>
    long BodyId { get; }
    /// <summary>False while the body is dead or pooled: its track is dropped.</summary>
    bool IsActive { get; }
    RingCanonicalPosition CanonicalPosition { get; }
    /// <summary>Characteristic size in metres (scales the ripple radius).</summary>
    float SizeMeters { get; }
}

/// <summary>One crossing of the field by a body's per-frame movement segment.</summary>
public readonly record struct ContainmentBodyCrossing(long BodyId, bool IsPlayer, ContainmentFieldPoint Point, float SpeedMetersPerSecond, float RadiusMeters, float Energy,
    bool ToVacuumSide, bool Published);

/// <summary>
/// Contact ripples (docs/containment-field-design.md decision 4, kind <see cref="ContainmentDisturbanceKind.BodyContact"/>): the player/camera and every registered
/// <see cref="IContainmentBody"/> pass through the field, and where their per-frame movement segment (previous to current canonical position) crosses it
/// (<see cref="ContainmentFieldGeometry.TryIntersectWorldSegment"/>) a ripple is published: Rmax 2-30 m from speed and size, T 1.5 s, refraction and a brief sheen
/// boost, no flash light. SourceId = hash(body id, crossing count), so a replay gives the same ids. A body that crosses again within
/// <see cref="ContainmentDisturbanceBus.BodyContactCooldownSeconds"/> of its last published ripple (weather clock) is a crossing without a ripple or a sound
/// (the bus's own cooldown is keyed by SourceId, which changes every crossing, so the per-body cooldown lives here too). A segment longer than
/// <see cref="MaxSegmentMeters"/> (or a speed over <see cref="MaxSpeedMetersPerSecond"/>: a teleport) is not a crossing. Works in noclip (free-fly) and on foot across the wall top, which crosses the centre-line skirt.
/// </summary>
public sealed class BodyContactDisturbanceAdapter
{
    public const long PlayerBodyId = 0x504C4159;   // "PLAY"
    public const float PlayerSizeMeters = 1.8f, DurationSeconds = 1.5f, MinRadiusMeters = 2.0f, MaxRadiusMeters = 30.0f;
    public const double MaxSegmentMeters = 20_000.0, SurfaceToleranceMeters = 0.01, MaxSpeedMetersPerSecond = 100_000.0;   // the segment query bisects to 5 mm

    private sealed class Track
    {
        public RingCanonicalPosition Previous;
        public bool HasPrevious;
        public int Crossings;
        public bool EndHit;   // the last segment's crossing was at its end point (a sample exactly on the surface)
        public double LastPublished = double.NegativeInfinity;
    }

    private readonly IContainmentDisturbanceSink _sink;
    private readonly Dictionary<long, Track> _tracks = new();
    private readonly List<IContainmentBody> _bodies = new();
    private readonly List<long> _scratch = new();

    public ContainmentFieldProfile Profile { get; set; }
    public double CircumferenceMeters { get; set; }
    public int Crossings { get; private set; }
    public int Published { get; private set; }
    public int Suppressed { get; private set; }
    public int Rejected { get; private set; }
    public ContainmentBodyCrossing? LastCrossing { get; private set; }
    /// <summary>Raised for every crossing that was not suppressed by the cooldown (the audio plays the whump from the player's).</summary>
    public event Action<ContainmentBodyCrossing>? Crossed;

    public BodyContactDisturbanceAdapter(IContainmentDisturbanceSink sink, ContainmentFieldProfile profile, double circumferenceMeters)
    {
        _sink = sink; Profile = profile; CircumferenceMeters = circumferenceMeters;
    }

    public int RegisteredBodies => _bodies.Count;
    public void Register(IContainmentBody body) { if (!_bodies.Contains(body)) _bodies.Add(body); }
    public void Unregister(IContainmentBody body) { _bodies.Remove(body); _tracks.Remove(body.BodyId); }

    /// <summary>Forgets every track (a world change or a teleport); crossing counts restart.</summary>
    public void Reset() { _tracks.Clear(); }

    public static long SourceIdOf(long bodyId, int crossing)
        => unchecked((bodyId * unchecked((long)0x9E3779B97F4A7C15UL)) ^ (crossing * unchecked((long)0xBF58476D1CE4E5B9UL)) ^ 0x0B0D1C07L);

    /// <summary>Rmax in metres from speed (m/s) and size (m): 2 m for a still small body, 30 m at sqrt(speed x size) = 30.</summary>
    public static float RadiusFor(float speedMetersPerSecond, float sizeMeters)
        => MinRadiusMeters + (MaxRadiusMeters - MinRadiusMeters) * Strength(speedMetersPerSecond, sizeMeters);

    public static float EnergyFor(float speedMetersPerSecond, float sizeMeters) => 0.4f + 0.6f * Strength(speedMetersPerSecond, sizeMeters);

    private static float Strength(float speed, float size)
        => Math.Clamp(MathF.Sqrt(Math.Max(speed, 0.0f) * Math.Max(size, 0.0f)) / 30.0f, 0.0f, 1.0f);

    /// <summary>
    /// Advances every track one frame. <paramref name="now"/> is the weather clock (the ripples' and cooldown's clock), <paramref name="deltaSeconds"/> the real frame time
    /// (speed). The player's position is the camera's canonical position. Returns the number of ripples published.
    /// </summary>
    public int Update(double now, double deltaSeconds, in RingLodGeometrySnapshot snapshot, in RingCanonicalPosition player)
    {
        if (!Profile.IsValid || !double.IsFinite(now) || !double.IsFinite(player.AroundMeters)) return 0;
        int published = Step(PlayerBodyId, true, player, PlayerSizeMeters, now, deltaSeconds, snapshot);
        for (int i = 0; i < _bodies.Count; i++)
        {
            IContainmentBody body = _bodies[i];
            if (!body.IsActive) { _tracks.Remove(body.BodyId); continue; }
            published += Step(body.BodyId, false, body.CanonicalPosition, body.SizeMeters, now, deltaSeconds, snapshot);
        }
        // Tracks of bodies that are no longer registered.
        if (_tracks.Count > _bodies.Count + 1)
        {
            _scratch.Clear();
            foreach (long id in _tracks.Keys)
            {
                if (id == PlayerBodyId) continue;
                bool found = false;
                for (int i = 0; i < _bodies.Count && !found; i++) found = _bodies[i].BodyId == id;
                if (!found) _scratch.Add(id);
            }
            foreach (long id in _scratch) _tracks.Remove(id);
        }
        return published;
    }

    private int Step(long id, bool isPlayer, in RingCanonicalPosition position, float size, double now, double deltaSeconds, in RingLodGeometrySnapshot snapshot)
    {
        if (!_tracks.TryGetValue(id, out Track? track)) _tracks[id] = track = new Track();
        if (track.LastPublished > now + 1e-9) track.LastPublished = double.NegativeInfinity;   // the weather clock went back
        if (!track.HasPrevious) { track.Previous = position; track.HasPrevious = true; return 0; }
        RingCanonicalPosition from = track.Previous;
        track.Previous = position;

        double dAround = position.AroundMeters - from.AroundMeters;
        if (CircumferenceMeters > 0.0)
        {
            dAround %= CircumferenceMeters;
            if (dAround > 0.5 * CircumferenceMeters) dAround -= CircumferenceMeters; else if (dAround < -0.5 * CircumferenceMeters) dAround += CircumferenceMeters;
        }
        double dv = position.VerticalMeters - from.VerticalMeters, dx = position.AcrossMeters - from.AcrossMeters;
        double length = Math.Sqrt(dAround * dAround + dv * dv + dx * dx);
        if (length < 1e-4 || length > MaxSegmentMeters || (deltaSeconds > 1e-6 && length / deltaSeconds > MaxSpeedMetersPerSecond)) { track.EndHit = false; return 0; }

        Vector3 a = snapshot.CanonicalToWorld(from.AroundMeters, from.VerticalMeters, from.AcrossMeters);
        Vector3 b = snapshot.CanonicalToWorld(from.AroundMeters + dAround, position.VerticalMeters, position.AcrossMeters);
        bool wasEndHit = track.EndHit;
        track.EndHit = false;
        if (!ContainmentFieldGeometry.TryIntersectWorldSegment(Profile, snapshot, a, b, out ContainmentFieldPoint hit, out double t)) return 0;
        // A sample landing exactly on the surface is a crossing of both the segment that ends on it and the one that starts on it: count it once.
        track.EndHit = (1.0 - t) * length < SurfaceToleranceMeters;
        if (t * length < SurfaceToleranceMeters && wasEndHit) return 0;

        track.Crossings++;
        Crossings++;
        bool toVacuum = ContainmentFieldGeometry.SignedDistance(Profile, position) > 0.0;
        if (now - track.LastPublished < ContainmentDisturbanceBus.BodyContactCooldownSeconds) { Suppressed++; return 0; }
        float speed = deltaSeconds > 1e-6 ? (float)(length / deltaSeconds) : 0.0f;
        float radius = RadiusFor(speed, size), energy = EnergyFor(speed, size);
        var d = new ContainmentDisturbance(SourceIdOf(id, track.Crossings), ContainmentDisturbanceKind.BodyContact,
            new RingCanonicalPosition(hit.AroundMeters, hit.VerticalMeters, hit.AcrossMeters), now, energy, radius, DurationSeconds, 0.0f);
        bool ok = _sink.Publish(d);
        track.LastPublished = now;
        if (ok) Published++; else Rejected++;
        var crossing = new ContainmentBodyCrossing(id, isPlayer, hit, speed, radius, energy, toVacuum, ok);
        LastCrossing = crossing;
        Crossed?.Invoke(crossing);
        return ok ? 1 : 0;
    }
}
