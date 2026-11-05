using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.ContainmentField;

/// <summary>Collects the render weather systems near a point (RingVolumetricCloudLayer.CollectRenderWeatherSystems without the megastorm records).</summary>
public delegate int StormSystemCollector(RingCanonicalPosition center, double radiusMeters, double time, List<RingVolumetricCloudLayer.RenderWeatherSystem> output);

/// <summary>
/// Storm tops pressing the field (docs/containment-field-design.md 4.2 "Storm contact"). A render system whose top reaches within 500 m of the field underside
/// anywhere in its footprint publishes a refraction-only ripple (kind StormContact, no flash) at the contact: the across nearest the rim where its top meets H,
/// clipped to the footprint. The cadence is deterministic, one ripple per system and side per slot of 1.1 s at a hashed offset in [0, 0.4 s), so consecutive ripples
/// are 0.7-1.5 s apart; SourceId = hash(SystemId, slot, side), so a paused clock gives identical frames. E scales with how far the top presses past the field.
/// <b>Kind-6 (megastorm) records are skipped</b>: the wall is published by <see cref="MegastormDisturbanceAdapter"/> and must never appear twice (megastorm doc 6.3).
/// The system list is refreshed every <see cref="RefreshSeconds"/> of weather time (the contact geometry changes slowly), not every frame.
/// </summary>
public sealed class StormContactAdapter
{
    public const double SlotSeconds = 1.1, JitterSeconds = 0.4, RefreshSeconds = 1.5, MaxCatchUpSeconds = 2.0, CollectRadiusMeters = 250_000.0;
    public const double PressMeters = 500.0, PenetrationScaleMeters = 2000.0;
    public const float MinEnergy = 0.1f, DurationSeconds = 3.0f, MaxOccluderRadiusMeters = 12_000.0f;

    private readonly IContainmentDisturbanceSink _sink;
    private readonly StormSystemCollector _collect;
    private readonly Func<Vector2> _wind;
    private readonly double _circumference;
    private readonly List<RingVolumetricCloudLayer.RenderWeatherSystem> _systems = new();
    private readonly List<Candidate> _candidates = new();
    private double _lastTime = double.NaN, _refreshedAt = double.NaN;

    public ContainmentFieldProfile Profile { get; set; }
    public int Published { get; private set; }
    public int Rejected { get; private set; }
    public int CandidateCount => _candidates.Count;

    private readonly record struct Candidate(int SystemId, int Side, double Around, double Across, double Underside, float Top, float Energy, float Radius, double Footprint);

    public StormContactAdapter(IContainmentDisturbanceSink sink, in ContainmentFieldProfile profile, StormSystemCollector collect, Func<Vector2> windMetersPerSecond,
        double circumferenceMeters)
    {
        _sink = sink; Profile = profile; _collect = collect; _wind = windMetersPerSecond; _circumference = circumferenceMeters;
    }

    /// <summary>Publishes the contact ripples whose slot time lies in (last time, <paramref name="now"/>]. A paused clock publishes nothing; a backwards jump re-arms.</summary>
    public int Update(double now, in RingCanonicalPosition observer)
    {
        if (!double.IsFinite(now) || !Profile.IsValid) return 0;
        if (double.IsNaN(_lastTime) || now < _lastTime - 1e-9) { _lastTime = now; _refreshedAt = double.NaN; _candidates.Clear(); return 0; }
        if (now == _lastTime) return 0;
        if (double.IsNaN(_refreshedAt) || now - _refreshedAt >= RefreshSeconds || now < _refreshedAt) { Refresh(now, observer); _refreshedAt = now; }
        double from = Math.Max(_lastTime, now - MaxCatchUpSeconds);
        int accepted = 0;
        foreach (Candidate c in _candidates)
        {
            long k0 = (long)Math.Floor((from - JitterSeconds) / SlotSeconds), k1 = (long)Math.Floor(now / SlotSeconds);
            for (long k = k0; k <= k1; k++)
            {
                double at = k * SlotSeconds + JitterSeconds * Hash01(c.SystemId, k, c.Side, 11);
                if (!(at > from && at <= now)) continue;
                double jitter = (Hash01(c.SystemId, k, c.Side, 12) - 0.5) * 0.5 * c.Footprint;
                var position = new RingCanonicalPosition(MegastormModel.WrapPositive(c.Around + jitter, _circumference), c.Underside, c.Across);
                var d = new ContainmentDisturbance(MegastormFieldCoupling.Mix64(c.SystemId, k, c.Side, 3), ContainmentDisturbanceKind.StormContact, position, at, c.Energy, c.Radius,
                    DurationSeconds, 0.0f, null, position, (float)Math.Min(c.Footprint, MaxOccluderRadiusMeters), c.Top);
                if (_sink.Publish(d)) { accepted++; Published++; } else Rejected++;
            }
        }
        _lastTime = now;
        return accepted;
    }

    private void Refresh(double now, in RingCanonicalPosition observer)
    {
        _candidates.Clear();
        _systems.Clear();
        _collect(observer, CollectRadiusMeters, now, _systems);
        Vector2 wind = _wind();
        foreach (RingVolumetricCloudLayer.RenderWeatherSystem s in _systems)
        {
            if (s.Kind == RingVolumetricCloudLayer.WeatherSystemKind.Megastorm || RingVolumetricCloudLayer.IsMegastormRecordId(s.SystemId)) continue;
            float top = s.StormCeilingMeters;
            if (!(top > s.StormFloorMeters + 1.0f)) continue;   // not a storm tower
            double around = s.IsStatic ? s.CenterAroundMeters : MegastormModel.WrapPositive(s.CenterAroundMeters + wind.X * now, _circumference);
            double across = s.IsStatic ? s.CenterAcrossMeters : s.CenterAcrossMeters + wind.Y * now;
            double radius = s.MajorRadiusMeters;
            for (int side = 0; side < 2; side++)
                if (TryContact(side, top, across, radius, out double contactAcross, out double underside, out float energy))
                    _candidates.Add(new Candidate(s.SystemId, side, around, contactAcross, underside, top, energy, 4000.0f + 6000.0f * energy, radius));
        }
    }

    /// <summary>
    /// The contact of a storm top with one half of the field: the across where H = top + 500 m (or the centre when the whole half is within reach), clipped to the
    /// footprint [across - radius, across + radius]. False when the field is out of reach (higher than the top + 500 m everywhere in the footprint, or the footprint misses it).
    /// </summary>
    private bool TryContact(int side, double top, double centreAcross, double radius, out double contactAcross, out double underside, out float energy)
    {
        contactAcross = underside = 0.0; energy = 0.0f;
        ContainmentFieldProfile p = Profile;
        double reach = top + PressMeters, root = side == 0 ? p.RootAcross0 : p.RootAcrossW, mid = p.Centre;
        if (!(ContainmentFieldGeometry.HeightAt(p, root) <= reach)) return false;
        // The across where H meets the reach on this half (H rises monotonically from the root to the centre): bisection.
        double inside = root, outside = mid;
        double extreme = ContainmentFieldGeometry.HeightAt(p, mid) <= reach ? mid : double.NaN;
        if (double.IsNaN(extreme))
        {
            for (int i = 0; i < 40; i++)
            {
                double m = 0.5 * (inside + outside);
                if (ContainmentFieldGeometry.HeightAt(p, m) <= reach) inside = m; else outside = m;
            }
            extreme = inside;
        }
        double lo = Math.Min(root, extreme), hi = Math.Max(root, extreme);   // the part of the half within reach
        double fLo = Math.Max(lo, centreAcross - radius), fHi = Math.Min(hi, centreAcross + radius);
        if (!(fLo <= fHi)) return false;
        contactAcross = side == 0 ? fHi : fLo;
        double deepest = side == 0 ? Math.Max(fLo, root + 1.0) : Math.Min(fHi, root - 1.0);   // the footprint point nearest the rim is where the top presses hardest (a metre inside the skirt)
        double press = reach - ContainmentFieldGeometry.HeightAt(p, deepest);
        underside = ContainmentFieldGeometry.HeightAt(p, contactAcross);
        energy = (float)Math.Clamp(press / PenetrationScaleMeters, MinEnergy, 1.0);
        return !double.IsNaN(underside);
    }

    private static double Hash01(long a, long b, long c, int salt) => (double)((ulong)MegastormFieldCoupling.Mix64(a, b, c, salt) >> 11) / (double)(1UL << 53);
}
