using System;
using System.Collections.Generic;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Timing of one emitter field shot: the false-sun refuel orb (EclipseEventSettings travel, climb, hold and fade; EclipseBeamSettings ease) and the mean interval
/// between shots of one emitter. Below <see cref="EmitterFieldShotSchedule.SlotSeconds"/> the shots are off.
/// </summary>
public readonly record struct EmitterFieldShotTiming(double IntervalSeconds, double OrbTravelSeconds, double OrbEaseExponent, double ClimbSeconds, double HoldSeconds, double FadeSeconds)
{
    public static EmitterFieldShotTiming FromEclipse(EclipseEventSettings ev, double intervalSeconds)
        => new(intervalSeconds, ev.OrbTravelSeconds, ev.Beams.OrbEaseExponent, ev.FlashClimbSeconds, ev.FlashHoldSeconds, ev.FlashFadeSeconds);

    public bool Off => !(IntervalSeconds >= EmitterFieldShotSchedule.SlotSeconds);
}

/// <summary>One scheduled shot: which emitter fires it (beam index and registry site), the slot it belongs to, and when it leaves and reaches the field.</summary>
public readonly record struct EmitterFieldShot(long Id, int Beam, int Site, long Slot, double LaunchTimeSeconds, double ArrivalTimeSeconds, float Energy);

public enum EmitterFieldShotPhase { None, Rising, Dispersing }

/// <summary>
/// An orb at one weather time: <see cref="OrbEnvelope"/> is the orb's brightness (the false-sun shape while rising, fading out as it disperses), <see cref="Flare"/> the
/// lightning-like pulse at the field and <see cref="Spread"/> 0..1 the dispersal progress. The position is canonical.
/// </summary>
public readonly record struct EmitterFieldShotState(EmitterFieldShotPhase Phase, double DistanceAlongMeters, float OrbEnvelope, float Flare, float Spread,
    double AroundMeters, double VerticalMeters, double AcrossMeters);

/// <summary>
/// The deterministic schedule of the ground emitters' field shots (every emitter fires an orb straight toward the ring hub; the orb stops where it meets the
/// containment field). Pure: no scene access, everything a function of the world identity, the beam origin, the field profile and the timing, so the weather clock
/// scrubbed anywhere gives the same shots. Per beam and slot of 10 s: the shot exists when a hash is below p = SlotSeconds / interval (a shorter interval keeps every
/// shot of a longer one), at a hashed time inside the slot. Doubles throughout.
/// </summary>
public sealed class EmitterFieldShotSchedule
{
    public const double SlotSeconds = 10.0;
    public const float RippleDurationSeconds = LightningDisturbanceAdapter.LightningDurationSeconds;
    /// <summary>The flare's rise with reduced flashes on (the slowest it ever is): the flare of a shot holds for at least this long, so the shot's life covers it.</summary>
    public const double FlareReducedRiseSeconds = 0.4;
    private const int SaltExists = 0x5E01, SaltLaunch = 0x5E02, SaltEnergy = 0x5E03, SaltId = 0x5E04;

    private readonly long _seed;
    private readonly int[] _beams;                     // included beam indices in order
    private readonly int[] _site;                      // by beam index
    private readonly double[] _a0, _v0, _x0, _r0, _dA, _hubLength, _contactDistance, _contactVertical, _contactAcross, _arrivalAge;
    private readonly bool[] _valid;
    private EmitterFieldShotTiming _timing;
    private double _maxLife;

    public EmitterBeamOrigin Origin { get; }
    public ContainmentFieldProfile Profile { get; }
    public int BeamCount => _beams.Length;
    public int ExcludedBeams { get; }
    public int TotalBeams => _valid.Length;

    public EmitterFieldShotTiming Timing
    {
        get => _timing;
        set
        {
            if (value == _timing) return;
            _timing = value;
            Recompute();
        }
    }

    /// <summary>Longest life of a shot (latest arrival age plus the dispersal hold, at least the flare's reduced rise, and the fade).</summary>
    public double MaxLifeSeconds => _maxLife;

    private EmitterFieldShotSchedule(long seed, EmitterBeamOrigin origin, in ContainmentFieldProfile profile, int total, int excluded, List<int> beams)
    {
        _seed = seed; Origin = origin; Profile = profile; ExcludedBeams = excluded;
        _beams = beams.ToArray();
        _valid = new bool[total];
        _site = new int[total];
        _a0 = new double[total]; _v0 = new double[total]; _x0 = new double[total]; _r0 = new double[total]; _dA = new double[total]; _hubLength = new double[total];
        _contactDistance = new double[total]; _contactVertical = new double[total]; _contactAcross = new double[total]; _arrivalAge = new double[total];
    }

    /// <summary>Builds the schedule, or null when the profile is invalid. Beams whose path never meets the field are excluded (<see cref="ExcludedBeams"/>).</summary>
    public static EmitterFieldShotSchedule? Build(EmitterGridTopology topology, EmitterBeamOrigin origin, in ContainmentFieldProfile profile, double ringRadiusMeters,
        double verticalBaseOffsetMeters, EmitterFieldShotTiming timing)
    {
        if (!profile.IsValid || !(ringRadiusMeters > 0.0)) return null;
        int total = topology.HubBeamCount;
        var included = new List<int>(total);
        double hubAcross = topology.HubAcrossMeters;
        var contact = new (bool Ok, double S, double V, double X)[total];
        var ctx = new double[total, 4];
        for (int b = 0; b < total; b++)
        {
            (double around, double vertical, double across) = topology.HubBeamOrigin(b, origin);
            double r0 = ringRadiusMeters - (verticalBaseOffsetMeters + vertical), dA = hubAcross - across, length = Math.Sqrt(r0 * r0 + dA * dA);
            ctx[b, 0] = around; ctx[b, 1] = vertical; ctx[b, 2] = across; ctx[b, 3] = r0;
            contact[b] = TryContact(profile, vertical, across, r0, dA, length, out double s, out double cv, out double cx) ? (true, s, cv, cx) : (false, 0.0, 0.0, 0.0);
            if (contact[b].Ok) included.Add(b);
        }
        var result = new EmitterFieldShotSchedule(unchecked((long)topology.Registry.Identity), origin, profile, total, total - included.Count, included);
        for (int b = 0; b < total; b++)
        {
            result._site[b] = topology.HubBeamSite(b);
            result._a0[b] = ctx[b, 0]; result._v0[b] = ctx[b, 1]; result._x0[b] = ctx[b, 2]; result._r0[b] = ctx[b, 3];
            result._dA[b] = hubAcross - ctx[b, 2];
            result._hubLength[b] = Math.Sqrt(result._r0[b] * result._r0[b] + result._dA[b] * result._dA[b]);
            result._valid[b] = contact[b].Ok;
            result._contactDistance[b] = contact[b].S; result._contactVertical[b] = contact[b].V; result._contactAcross[b] = contact[b].X;
        }
        result._timing = timing;
        result.Recompute();
        return result;
    }

    /// <summary>Bisection to under 1 mm on g(s) = vertical(s) - H(across(s)) between the origin (under the field) and a point above its apex.</summary>
    private static bool TryContact(in ContainmentFieldProfile p, double v0, double x0, double r0, double dA, double length, out double s, out double vertical, out double across)
    {
        s = vertical = across = 0.0;
        ContainmentFieldProfile profile = p;
        if (!(r0 > 0.0) || !(length > 0.0)) return false;
        double dv = r0 / length, dx = dA / length;
        double G(double t)
        {
            double h = ContainmentFieldGeometry.HeightAt(profile, x0 + t * dx);
            return double.IsNaN(h) ? double.NaN : v0 + t * dv - h;
        }
        double hi = (p.ApexMeters + 1000.0 - v0) / dv;
        if (!(hi > 0.0)) return false;
        double glo = G(0.0), ghi = G(hi);
        if (!(glo < 0.0) || !(ghi > 0.0)) return false;
        double lo = 0.0;
        for (int i = 0; i < 200 && hi - lo > 1e-4; i++)
        {
            double mid = 0.5 * (lo + hi), g = G(mid);
            if (double.IsNaN(g)) return false;
            if (g < 0.0) lo = mid; else hi = mid;
        }
        s = 0.5 * (lo + hi);
        vertical = v0 + s * dv;
        across = x0 + s * dx;
        return true;
    }

    private void Recompute()
    {
        double travel = Math.Max(_timing.OrbTravelSeconds, 1e-3), ease = Math.Max(_timing.OrbEaseExponent, 1e-3);
        double maxAge = 0.0;
        foreach (int b in _beams)
        {
            double fraction = Math.Clamp(_contactDistance[b] / _hubLength[b], 0.0, 1.0);
            _arrivalAge[b] = travel * Math.Pow(fraction, 1.0 / ease);
            maxAge = Math.Max(maxAge, _arrivalAge[b]);
        }
        _maxLife = maxAge + Math.Max(_timing.HoldSeconds, FlareReducedRiseSeconds) + Math.Max(_timing.FadeSeconds, 0.0);
    }

    // ---- per beam ----

    public bool IsIncluded(int beam) => (uint)beam < (uint)_valid.Length && _valid[beam];
    public (double Around, double Vertical, double Across) ContactOf(int beam) => (_a0[beam], _contactVertical[beam], _contactAcross[beam]);
    public (double Around, double Vertical, double Across) OriginOf(int beam) => (_a0[beam], _v0[beam], _x0[beam]);
    public double ContactDistanceOf(int beam) => _contactDistance[beam];
    public double HubDistanceOf(int beam) => _hubLength[beam];
    public double ArrivalAgeOf(int beam) => _arrivalAge[beam];
    public int SiteOf(int beam) => _site[beam];
    /// <summary>Included beam indices, in order.</summary>
    public ReadOnlySpan<int> Beams => _beams;

    private double LifeOf(int beam) => _arrivalAge[beam] + Math.Max(_timing.HoldSeconds, FlareReducedRiseSeconds) + Math.Max(_timing.FadeSeconds, 0.0);

    // ---- the schedule ----

    private static double Hash01(long a, long b, long c, int salt) => (double)((ulong)MegastormFieldCoupling.Mix64(a, b, c, salt) >> 11) / (double)(1UL << 53);

    private double Probability => Math.Min(1.0, SlotSeconds / _timing.IntervalSeconds);

    private bool TryShot(int beam, long slot, double probability, out EmitterFieldShot shot)
    {
        shot = default;
        int site = _site[beam];
        if (!(Hash01(_seed, site, slot, SaltExists) < probability)) return false;
        double launch = slot * SlotSeconds + SlotSeconds * Hash01(_seed, site, slot, SaltLaunch);
        float energy = (float)(0.45 + 0.30 * Hash01(_seed, site, slot, SaltEnergy));
        long id = MegastormFieldCoupling.Mix64(_seed, site, slot, SaltId) & 0x7FFF_FFFF_FFFF_FFFFL;
        if (id == 0) id = 1;
        shot = new EmitterFieldShot(id, beam, site, slot, launch, launch + _arrivalAge[beam], energy);
        return true;
    }

    /// <summary>Every shot with launch &lt;= now &lt; launch + arrival age + hold + fade, all beams, ordered by (beam, slot).</summary>
    public int CollectActive(double now, List<EmitterFieldShot> output)
    {
        output.Clear();
        if (_timing.Off || !double.IsFinite(now)) return 0;
        double p = Probability;
        foreach (int b in _beams)
        {
            long k0 = (long)Math.Floor((now - LifeOf(b)) / SlotSeconds) - 1, k1 = (long)Math.Floor(now / SlotSeconds);
            for (long k = k0; k <= k1; k++)
            {
                if (!TryShot(b, k, p, out EmitterFieldShot shot)) continue;
                if (shot.LaunchTimeSeconds <= now && now < shot.LaunchTimeSeconds + LifeOf(b)) output.Add(shot);
            }
        }
        return output.Count;
    }

    /// <summary>Shots whose arrival at the field lies in (<paramref name="fromExclusive"/>, <paramref name="toInclusive"/>], ordered by arrival then id.</summary>
    public int CollectArrivals(double fromExclusive, double toInclusive, List<EmitterFieldShot> output)
    {
        output.Clear();
        if (_timing.Off || !double.IsFinite(fromExclusive) || !double.IsFinite(toInclusive) || !(toInclusive > fromExclusive)) return 0;
        double p = Probability;
        foreach (int b in _beams)
        {
            double age = _arrivalAge[b];
            long k0 = (long)Math.Floor((fromExclusive - age) / SlotSeconds) - 1, k1 = (long)Math.Floor((toInclusive - age) / SlotSeconds);
            for (long k = k0; k <= k1; k++)
            {
                if (!TryShot(b, k, p, out EmitterFieldShot shot)) continue;
                if (shot.ArrivalTimeSeconds > fromExclusive && shot.ArrivalTimeSeconds <= toInclusive) output.Add(shot);
            }
        }
        output.Sort((x, y) => { int c = x.ArrivalTimeSeconds.CompareTo(y.ArrivalTimeSeconds); return c != 0 ? c : x.Id.CompareTo(y.Id); });
        return output.Count;
    }

    /// <summary>Shots whose launch lies in (<paramref name="fromExclusive"/>, <paramref name="toInclusive"/>], ordered by launch then id (the launch sounds).</summary>
    public int CollectLaunches(double fromExclusive, double toInclusive, List<EmitterFieldShot> output)
    {
        output.Clear();
        if (_timing.Off || !double.IsFinite(fromExclusive) || !double.IsFinite(toInclusive) || !(toInclusive > fromExclusive)) return 0;
        double p = Probability;
        long k0 = (long)Math.Floor(fromExclusive / SlotSeconds), k1 = (long)Math.Floor(toInclusive / SlotSeconds);
        foreach (int b in _beams)
        {
            for (long k = k0; k <= k1; k++)
            {
                if (!TryShot(b, k, p, out EmitterFieldShot shot)) continue;
                if (shot.LaunchTimeSeconds > fromExclusive && shot.LaunchTimeSeconds <= toInclusive) output.Add(shot);
            }
        }
        output.Sort((x, y) => { int c = x.LaunchTimeSeconds.CompareTo(y.LaunchTimeSeconds); return c != 0 ? c : x.Id.CompareTo(y.Id); });
        return output.Count;
    }

    // ---- kinematics ----

    private static double Smooth01(double x)
    {
        x = Math.Clamp(x, 0.0, 1.0);
        return x * x * (3.0 - 2.0 * x);
    }

    public EmitterFieldShotState Evaluate(in EmitterFieldShot shot, double now)
    {
        int b = shot.Beam;
        double age = now - shot.LaunchTimeSeconds, arrivalAge = _arrivalAge[b], hold = Math.Max(_timing.HoldSeconds, 0.0), fade = Math.Max(_timing.FadeSeconds, 0.0);
        if (!(age >= 0.0) || age > arrivalAge + Math.Max(hold, FlareReducedRiseSeconds) + fade) return default;
        double travel = Math.Max(_timing.OrbTravelSeconds, 1e-3), ease = Math.Max(_timing.OrbEaseExponent, 1e-3), climb = Math.Max(_timing.ClimbSeconds, 1e-4);
        double dv = _r0[b] / _hubLength[b], dx = _dA[b] / _hubLength[b];
        if (age < arrivalAge)
        {
            double s = Math.Min(_hubLength[b] * Math.Pow(age / travel, ease), _contactDistance[b]);
            float envelope = age < climb ? (float)Smooth01(age / climb) : 1.0f;
            return new EmitterFieldShotState(EmitterFieldShotPhase.Rising, s, envelope, 0.0f, 0.0f, _a0[b], _v0[b] + s * dv, _x0[b] + s * dx);
        }
        double x = age - arrivalAge;
        return new EmitterFieldShotState(EmitterFieldShotPhase.Dispersing, _contactDistance[b], (float)(1.0 - Smooth01(x / Math.Max(hold, 0.05))), FlareLevel(x), (float)Smooth01(x / Math.Max(hold + fade, 1e-6)),
            _a0[b], _contactVertical[b], _contactAcross[b]);
    }

    /// <summary>The flare at the field, <paramref name="x"/> seconds after the orb arrived: a fast rise (0.12 s, 0.4 s with reduced flashes), a hold, a fade; scaled by the reduced-flash peak.</summary>
    public static float FlareLevel(double x, double hold, double fade)
    {
        double rise = LightningVisualPolicy.ReducedFlashes ? FlareReducedRiseSeconds : 0.12, top = Math.Max(hold, rise);
        if (!(x >= 0.0) || x > top + fade) return 0.0f;
        double level = x < rise ? Smooth01(x / rise) : x < top ? 1.0 : 1.0 - Smooth01((x - top) / Math.Max(fade, 1e-6));
        return (float)(level * LightningVisualPolicy.PeakScale);
    }

    public float FlareLevel(double x) => FlareLevel(x, Math.Max(_timing.HoldSeconds, 0.0), Math.Max(_timing.FadeSeconds, 0.0));
}
