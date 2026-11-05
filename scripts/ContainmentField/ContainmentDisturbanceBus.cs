using System;
using System.Collections.Generic;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.ContainmentField;

/// <summary>Outcome of one publish (<see cref="ContainmentDisturbanceBus.TryPublish"/>). The first four are accepted, the rest rejected.</summary>
public enum ContainmentPublishResult { Accepted, Merged, Restruck, FarList, Duplicate, Cooldown, OutOfRange, Expired, Invalid, RateLimited, NoBudget }

/// <summary>One live ripple patch of the pool (docs/containment-field-design.md 3.5). Read-only to consumers; the bus owns and mutates it.</summary>
public sealed class ContainmentRipple
{
    public int Slot { get; internal set; }
    public long SourceId { get; internal set; }
    public ContainmentDisturbanceKind Kind { get; internal set; }
    /// <summary>The source point projected onto the field (exact nearest point, keeps the source's around).</summary>
    public ContainmentFieldPoint Surface { get; internal set; }
    public RingCanonicalPosition Source { get; internal set; }
    public double StartTimeSeconds { get; internal set; }
    public float Energy { get; internal set; }
    public float RadiusMeters { get; internal set; }
    public float DurationSeconds { get; internal set; }
    public float FlashCoupling { get; internal set; }
    public Func<double, float>? FlashLevel { get; internal set; }
    public RingCanonicalPosition? OccluderCenter { get; internal set; }
    public float OccluderRadiusMeters { get; internal set; }
    public float OccluderTopMeters { get; internal set; }
    public bool Natural { get; internal set; }
    /// <summary>FieldStrike only: the crawl radius Rc = 2 km + 4 km x E, the crawl seed (new on every restrike) and the root the crawl leans to (0 / 1).</summary>
    public float CrawlRadiusMeters { get; internal set; }
    public int CrawlSeed { get; internal set; }
    public int NearerRoot { get; internal set; }
    public int Restrikes { get; internal set; }

    /// <summary>Seconds since the start (negative before it).</summary>
    public double Age(double now) => now - StartTimeSeconds;
    /// <summary>Envelope A(tau) at <paramref name="now"/>; exactly 0 at tau >= T.</summary>
    public float Amplitude(double now) => ContainmentDisturbanceBus.Envelope(Energy, now - StartTimeSeconds, DurationSeconds);
    /// <summary>Front radius rho(tau) in metres.</summary>
    public float FrontRadius(double now) => ContainmentDisturbanceBus.FrontRadius(RadiusMeters, now - StartTimeSeconds, DurationSeconds);
    /// <summary>The source flash (0..1) that lights the ripple: the source's own pulse (reduced-flash aware) times the coupling; 0 for refraction-only kinds.</summary>
    public float FlashAt(double now)
    {
        if (FlashLevel == null || FlashCoupling <= 0.0f) return 0.0f;
        float f = FlashLevel(now);
        return float.IsFinite(f) ? Math.Clamp(f, 0.0f, 1.0f) * Math.Clamp(FlashCoupling, 0.0f, 1.0f) : 0.0f;
    }
    /// <summary>Radius of the patch mesh: 1.15 x Rmax (1.15 x max(Rmax, Rc) for a field strike).</summary>
    public float PatchRadiusMeters => 1.15f * (Kind == ContainmentDisturbanceKind.FieldStrike ? Math.Max(RadiusMeters, CrawlRadiusMeters) : RadiusMeters);
}

/// <summary>A field strike beyond 350 km: it never uses a pool slot; the far strike layer draws it for the life of its pulse.</summary>
public sealed class ContainmentFarStrike
{
    public long SourceId { get; internal set; }
    public ContainmentFieldPoint Surface { get; internal set; }
    public double StartTimeSeconds { get; internal set; }
    public float Energy { get; internal set; }
    public float CrawlRadiusMeters { get; internal set; }
    public float LifeSeconds { get; internal set; }
    public Func<double, float>? FlashLevel { get; internal set; }
    public float FlashAt(double now)
    {
        if (FlashLevel == null) return 0.0f;
        float f = FlashLevel(now);
        return float.IsFinite(f) ? Math.Clamp(f, 0.0f, 1.0f) : 0.0f;
    }
}

/// <summary>
/// The deterministic disturbance bus (docs/containment-field-design.md 4.1, 3.5 and the pool budget of 3.10.4): pure C#, main thread, driven by the weather clock.
/// It projects each disturbance onto the field, then applies, in order: validity, dedupe by SourceId (BodyContact: a per-source cooldown instead), the 400 km range
/// (field strikes beyond 350 km go to the 32-entry far list instead), the merge rules (same kind within 0.25 s and closer than Rmax; a field strike inside a live
/// strike's crawl radius re-triggers that patch), the spawn caps (6/s total, FieldStrike 2/s with natural at most 1/s, megastorm contact plus bursts 4/s) and the pool
/// of 16 patches with its reservations (4 for FieldStrike, 2 for BodyContact, 1 for EmitterShot, so 9 shared slots; contact at most 6 live, bursts at most 3, EmitterShot at most 2) and the eviction order. An EmitterShot has its own spawn window (1/s) and is neither checked against nor added to the global 6/s one, so it never consumes budget the other sources see. Everything depends only
/// on the sequence of Publish and <see cref="Advance"/> calls, so a paused clock reproduces the same state and a scrub back removes what had not happened yet.
/// </summary>
public sealed class ContainmentDisturbanceBus : IContainmentDisturbanceSink
{
    public const int PoolSize = 16, FarListSize = 32;
    public const double RangeMeters = 400_000.0, FarStrikeMeters = 350_000.0;
    public const int GlobalSpawnsPerSecond = 6, StrikeSpawnsPerSecond = 2, NaturalStrikeSpawnsPerSecond = 1, MegastormSpawnsPerSecond = 4;
    public const int StrikeReserve = 4, BodyReserve = 2, ContactLiveCap = 6, BurstLiveCap = 3;
    public const int EmitterReserve = 1, EmitterLiveCap = 2, EmitterSpawnsPerSecond = 1;
    public const double MergeWindowSeconds = 0.25, BodyContactCooldownSeconds = 1.0, FarStrikeMaxLifeSeconds = 2.0;
    private const double SeenGraceSeconds = 2.0;

    private enum PoolClass { Other, Strike, Contact, Burst, Body, Emitter }
    private const int PoolClassCount = 6;

    private readonly ContainmentRipple?[] _slots = new ContainmentRipple?[PoolSize];
    private readonly List<ContainmentFarStrike> _far = new(FarListSize);
    private readonly Dictionary<long, (double Start, double End)> _seen = new();
    private readonly Dictionary<long, double> _bodyLast = new();
    private readonly RateWindow _global = new(), _strike = new(), _natural = new(), _megastorm = new(), _emitter = new();
    private readonly List<long> _scratchIds = new();
    private double _circumference;

    public ContainmentFieldProfile Profile { get; set; }
    /// <summary>The camera (range and eviction distance). Null = no observer: nothing is out of range and every patch counts as distance 0.</summary>
    public RingCanonicalPosition? Observer { get; set; }
    /// <summary>The weather time of the last <see cref="Advance"/> (NaN before the first).</summary>
    public double Now { get; private set; } = double.NaN;

    public int LiveCount { get; private set; }
    public IReadOnlyList<ContainmentRipple?> Slots => _slots;
    public IReadOnlyList<ContainmentFarStrike> FarStrikes => _far;

    // ---- counters (tests and the debug overlay) ----
    public int Accepted { get; private set; }
    public int Merged { get; private set; }
    public int Restruck { get; private set; }
    public int Duplicates { get; private set; }
    public int CooledDown { get; private set; }
    public int OutOfRangeRejected { get; private set; }
    public int RateLimited { get; private set; }
    public int NoBudget { get; private set; }
    public int Evicted { get; private set; }
    public int FarListed { get; private set; }
    public int FarDropped { get; private set; }
    public int PeakLive { get; private set; }

    public ContainmentDisturbanceBus(in ContainmentFieldProfile profile, double circumferenceMeters)
    {
        Profile = profile;
        _circumference = circumferenceMeters;
    }

    public double CircumferenceMeters { get => _circumference; set => _circumference = value; }

    // ---- the ripple envelope (3.5) ----

    /// <summary>A(tau) = E x smoothstep(0, 0.08 s, tau) x (1 - tau / T)^2; exactly 0 for tau &lt;= 0 and for tau &gt;= T.</summary>
    public static float Envelope(float energy, double tau, float durationSeconds)
    {
        if (!(tau > 0.0) || !(tau < durationSeconds) || !(durationSeconds > 0.0f)) return 0.0f;
        double s = Math.Clamp(tau / 0.08, 0.0, 1.0), rise = s * s * (3.0 - 2.0 * s), rest = 1.0 - tau / durationSeconds;
        return (float)(energy * rise * rest * rest);
    }

    /// <summary>rho(tau) = Rmax x (1 - exp(-3 tau / T)), the decelerating front radius (0 before the start).</summary>
    public static float FrontRadius(float radiusMeters, double tau, float durationSeconds)
        => tau <= 0.0 || !(durationSeconds > 0.0f) ? 0.0f : (float)(radiusMeters * (1.0 - Math.Exp(-3.0 * tau / durationSeconds)));

    // ---- clock ----

    /// <summary>
    /// Moves the bus to weather time <paramref name="now"/>: patches whose A(tau) has reached 0 (tau &gt;= T) are freed, old dedupe entries are forgotten and far
    /// strikes end with their pulse. A time earlier than the previous call is a scrub: whatever started after it is removed so the same ids can publish again.
    /// </summary>
    public void Advance(double now)
    {
        if (!double.IsFinite(now)) return;
        bool back = !double.IsNaN(Now) && now < Now - 1e-9;
        Now = now;
        for (int i = 0; i < PoolSize; i++)
        {
            ContainmentRipple? r = _slots[i];
            if (r == null) continue;
            if (now - r.StartTimeSeconds >= r.DurationSeconds || (back && r.StartTimeSeconds > now)) { _slots[i] = null; LiveCount--; }
        }
        for (int i = _far.Count - 1; i >= 0; i--)
            if (now - _far[i].StartTimeSeconds > _far[i].LifeSeconds || (back && _far[i].StartTimeSeconds > now)) _far.RemoveAt(i);
        _scratchIds.Clear();
        foreach (KeyValuePair<long, (double Start, double End)> kv in _seen)
            if (now > kv.Value.End + SeenGraceSeconds || (back && kv.Value.Start > now)) _scratchIds.Add(kv.Key);
        foreach (long id in _scratchIds) _seen.Remove(id);
        _scratchIds.Clear();
        foreach (KeyValuePair<long, double> kv in _bodyLast)
            if (now > kv.Value + 10.0 * BodyContactCooldownSeconds || (back && kv.Value > now)) _scratchIds.Add(kv.Key);
        foreach (long id in _scratchIds) _bodyLast.Remove(id);
        _global.Prune(now, back); _strike.Prune(now, back); _natural.Prune(now, back); _megastorm.Prune(now, back); _emitter.Prune(now, back);
    }

    /// <summary>Frees every patch and forgets everything (a world change).</summary>
    public void Clear()
    {
        Array.Clear(_slots);
        LiveCount = 0;
        _far.Clear(); _seen.Clear(); _bodyLast.Clear();
        _global.Clear(); _strike.Clear(); _natural.Clear(); _megastorm.Clear(); _emitter.Clear();
    }

    // ---- publish ----

    public bool Publish(in ContainmentDisturbance d)
    {
        ContainmentPublishResult r = TryPublish(d);
        return r <= ContainmentPublishResult.FarList;
    }

    public ContainmentPublishResult TryPublish(in ContainmentDisturbance d)
    {
        if (!(d.Energy > 0.0f) || !float.IsFinite(d.Energy) || !(d.RadiusMeters > 0.0f) || !float.IsFinite(d.RadiusMeters) || !(d.DurationSeconds > 0.0f)
            || !float.IsFinite(d.DurationSeconds) || !double.IsFinite(d.StartTimeSeconds) || !double.IsFinite(d.Position.AroundMeters)
            || !double.IsFinite(d.Position.AcrossMeters) || !double.IsFinite(d.Position.VerticalMeters) || !Profile.IsValid)
            return ContainmentPublishResult.Invalid;
        double clock = double.IsNaN(Now) ? d.StartTimeSeconds : Now;
        if (d.StartTimeSeconds + d.DurationSeconds <= clock) return ContainmentPublishResult.Expired;
        bool body = d.Kind == ContainmentDisturbanceKind.BodyContact, strike = d.Kind == ContainmentDisturbanceKind.FieldStrike;
        if (body)
        {
            if (_bodyLast.TryGetValue(d.SourceId, out double last) && Math.Abs(d.StartTimeSeconds - last) < BodyContactCooldownSeconds) { CooledDown++; return ContainmentPublishResult.Cooldown; }
        }
        else if (_seen.ContainsKey(d.SourceId)) { Duplicates++; return ContainmentPublishResult.Duplicate; }

        ContainmentFieldPoint surface = ContainmentFieldGeometry.Nearest(Profile, d.Position);
        double distance = DistanceFromObserver(surface);
        float energy = Math.Clamp(d.Energy, 0.0f, 1.0f);

        if (strike && distance > FarStrikeMeters) { AddFar(d, surface, energy); return ContainmentPublishResult.FarList; }
        if (distance > RangeMeters) { OutOfRangeRejected++; return ContainmentPublishResult.OutOfRange; }

        // Merge rules. A new FieldStrike inside the crawl radius of a live strike patch re-triggers it (no new slot); otherwise same kind within 0.25 s and closer than Rmax.
        if (strike)
        {
            for (int i = 0; i < PoolSize; i++)
            {
                ContainmentRipple? r = _slots[i];
                if (r == null || r.Kind != ContainmentDisturbanceKind.FieldStrike || clock - r.StartTimeSeconds >= r.DurationSeconds) continue;
                if (Distance(r.Surface, surface) > r.CrawlRadiusMeters) continue;
                Restrike(r, d, surface, energy);
                MarkSeen(d);
                Restruck++;
                return ContainmentPublishResult.Restruck;
            }
        }
        else if (!body)
        {
            for (int i = 0; i < PoolSize; i++)
            {
                ContainmentRipple? r = _slots[i];
                if (r == null || r.Kind != d.Kind || Math.Abs(d.StartTimeSeconds - r.StartTimeSeconds) >= MergeWindowSeconds || clock - r.StartTimeSeconds >= r.DurationSeconds) continue;
                if (Distance(r.Surface, surface) >= r.RadiusMeters) continue;
                r.Energy = Math.Max(r.Energy, energy);
                r.FlashLevel = MaxFlash(r.FlashLevel, d.FlashLevel);
                r.FlashCoupling = Math.Max(r.FlashCoupling, Math.Clamp(d.FlashCoupling, 0.0f, 1.0f));
                MarkSeen(d);
                Merged++;
                return ContainmentPublishResult.Merged;
            }
        }

        // Spawn caps by start time.
        bool megastorm = d.Kind == ContainmentDisturbanceKind.MegastormContact || d.Kind == ContainmentDisturbanceKind.UpdraftBurst;
        bool emitter = d.Kind == ContainmentDisturbanceKind.EmitterShot;
        double t = d.StartTimeSeconds;
        if ((emitter && !_emitter.Allows(t, EmitterSpawnsPerSecond)) || (!strike && !emitter && !_global.Allows(t, GlobalSpawnsPerSecond)) || (strike && !_strike.Allows(t, StrikeSpawnsPerSecond)) || (strike && d.NaturalSource && !_natural.Allows(t, NaturalStrikeSpawnsPerSecond))
            || (megastorm && !_megastorm.Allows(t, MegastormSpawnsPerSecond)))
        { RateLimited++; return ContainmentPublishResult.RateLimited; }

        PoolClass cls = Classify(d.Kind);
        int slot = Allocate(cls, energy, strike, clock);
        if (slot < 0) { NoBudget++; return ContainmentPublishResult.NoBudget; }

        var ripple = new ContainmentRipple
        {
            Slot = slot, SourceId = d.SourceId, Kind = d.Kind, Surface = surface, Source = d.Position, StartTimeSeconds = d.StartTimeSeconds, Energy = energy,
            RadiusMeters = d.RadiusMeters, DurationSeconds = d.DurationSeconds, FlashCoupling = Math.Clamp(d.FlashCoupling, 0.0f, 1.0f), FlashLevel = d.FlashLevel,
            OccluderCenter = d.OccluderCenter, OccluderRadiusMeters = d.OccluderRadiusMeters, OccluderTopMeters = d.OccluderTopMeters, Natural = d.NaturalSource,
        };
        if (strike) StrikeInit(ripple, d.SourceId, energy, surface);
        _slots[slot] = ripple;
        LiveCount++;
        if (LiveCount > PeakLive) PeakLive = LiveCount;
        if (emitter) _emitter.Add(t); else _global.Add(t);
        if (strike) { _strike.Add(t); if (d.NaturalSource) _natural.Add(t); }
        if (megastorm) _megastorm.Add(t);
        if (body) _bodyLast[d.SourceId] = d.StartTimeSeconds; else MarkSeen(d);
        Accepted++;
        return ContainmentPublishResult.Accepted;
    }

    // ---- pool ----

    private static PoolClass Classify(ContainmentDisturbanceKind kind) => kind switch
    {
        ContainmentDisturbanceKind.FieldStrike => PoolClass.Strike,
        ContainmentDisturbanceKind.MegastormContact => PoolClass.Contact,
        ContainmentDisturbanceKind.UpdraftBurst => PoolClass.Burst,
        ContainmentDisturbanceKind.BodyContact => PoolClass.Body,
        ContainmentDisturbanceKind.EmitterShot => PoolClass.Emitter,
        _ => PoolClass.Other,
    };

    private static int Reserve(PoolClass c) => c == PoolClass.Strike ? StrikeReserve : c == PoolClass.Body ? BodyReserve : c == PoolClass.Emitter ? EmitterReserve : 0;
    private static int Cap(PoolClass c) => c == PoolClass.Contact ? ContactLiveCap : c == PoolClass.Burst ? BurstLiveCap : c == PoolClass.Body ? BodyReserve : c == PoolClass.Emitter ? EmitterLiveCap : int.MaxValue;

    public int LiveOf(ContainmentDisturbanceKind kind)
    {
        int n = 0;
        foreach (ContainmentRipple? r in _slots) if (r != null && r.Kind == kind) n++;
        return n;
    }

    private int LiveOf(PoolClass c)
    {
        int n = 0;
        foreach (ContainmentRipple? r in _slots) if (r != null && Classify(r.Kind) == c) n++;
        return n;
    }

    /// <summary>
    /// A slot for a newcomer of class <paramref name="cls"/>: a free one when the class is under its live cap and taking it leaves the other classes' unmet reservations
    /// intact, else the lowest-scored evictable patch. Never a lit patch (flash above 0) unless the newcomer is a FieldStrike with higher E; never a patch whose class is at
    /// or under its reservation (unless it is the newcomer's own class); with the class at its live cap only its own patches.
    /// </summary>
    private int Allocate(PoolClass cls, float energy, bool strike, double clock)
    {
        int[] live = new int[PoolClassCount];
        foreach (ContainmentRipple? r in _slots) if (r != null) live[(int)Classify(r.Kind)]++;
        int free = PoolSize - LiveCount, unmetOthers = 0;
        for (int c = 0; c < PoolClassCount; c++) if ((PoolClass)c != cls) unmetOthers += Math.Max(0, Reserve((PoolClass)c) - live[c]);
        bool atCap = live[(int)cls] >= Cap(cls);
        if (!atCap && free - 1 >= unmetOthers)
        {
            for (int i = 0; i < PoolSize; i++) if (_slots[i] == null) return i;
        }
        int victim = -1;
        double best = double.MaxValue;
        if (!atCap && cls != PoolClass.Emitter && live[(int)PoolClass.Emitter] > EmitterReserve)   // a newcomer at its own live cap only ever takes its own patch
        {
            // An emitter patch above its reservation goes first, lit or not: the shots are decoration and never starve the real sources.
            for (int i = 0; i < PoolSize; i++)
            {
                ContainmentRipple? r = _slots[i];
                if (r == null || Classify(r.Kind) != PoolClass.Emitter) continue;
                if (victim < 0 || Older(r, _slots[victim]!)) victim = i;
            }
            _slots[victim] = null;
            LiveCount--;
            Evicted++;
            return victim;
        }
        for (int i = 0; i < PoolSize; i++)
        {
            ContainmentRipple? r = _slots[i];
            if (r == null) continue;
            PoolClass vc = Classify(r.Kind);
            if (atCap ? vc != cls : (vc != cls && live[(int)vc] <= Reserve(vc))) continue;
            bool lit = r.FlashAt(clock) > 0.0f;
            if (lit && !(strike && energy > r.Energy)) continue;
            double score = EvictionScore(r, clock);
            if (score < best || (score == best && victim >= 0 && Older(r, _slots[victim]!)))
            {
                best = score; victim = i;
            }
        }
        if (victim < 0) return -1;
        _slots[victim] = null;
        LiveCount--;
        Evicted++;
        return victim;
    }

    private static bool Older(ContainmentRipple a, ContainmentRipple b)
        => a.StartTimeSeconds < b.StartTimeSeconds || (a.StartTimeSeconds == b.StartTimeSeconds && a.SourceId < b.SourceId);

    /// <summary>E x (1 - tau / T) / (1 + camera distance / 100 km): ripple tails go first.</summary>
    private double EvictionScore(ContainmentRipple r, double clock)
    {
        double tau = Math.Max(0.0, clock - r.StartTimeSeconds), life = Math.Clamp(1.0 - tau / r.DurationSeconds, 0.0, 1.0);
        return r.Energy * life / (1.0 + DistanceFromObserver(r.Surface) / 100_000.0);
    }

    // ---- field strikes ----

    private void StrikeInit(ContainmentRipple r, long sourceId, float energy, in ContainmentFieldPoint surface)
    {
        r.CrawlRadiusMeters = 2000.0f + 4000.0f * energy;
        r.CrawlSeed = SeedFor(sourceId, 0);
        r.NearerRoot = surface.AcrossMeters < Profile.Centre ? 0 : 1;
        r.Restrikes = 0;
    }

    private void Restrike(ContainmentRipple r, in ContainmentDisturbance d, in ContainmentFieldPoint surface, float energy)
    {
        r.Restrikes++;
        r.Energy = Math.Max(r.Energy, energy);
        r.CrawlRadiusMeters = 2000.0f + 4000.0f * r.Energy;
        r.CrawlSeed = SeedFor(d.SourceId, r.Restrikes);
        r.RadiusMeters = Math.Max(r.RadiusMeters, d.RadiusMeters);
        r.StartTimeSeconds = Math.Max(r.StartTimeSeconds, d.StartTimeSeconds);   // the envelope restarts: a re-triggered ripple
        r.DurationSeconds = Math.Max(r.DurationSeconds, d.DurationSeconds);
        r.FlashLevel = MaxFlash(r.FlashLevel, d.FlashLevel);
        r.FlashCoupling = Math.Max(r.FlashCoupling, Math.Clamp(d.FlashCoupling, 0.0f, 1.0f));
    }

    private static int SeedFor(long id, int salt) => unchecked((int)(MegastormFieldCoupling.Mix64(id, salt, 0, 0x5F1E) & 0x7fffffff));

    private static Func<double, float>? MaxFlash(Func<double, float>? a, Func<double, float>? b)
        => a == null ? b : b == null ? a : t => Math.Max(a(t), b(t));

    private void AddFar(in ContainmentDisturbance d, in ContainmentFieldPoint surface, float energy)
    {
        while (_far.Count >= FarListSize) { _far.RemoveAt(0); FarDropped++; }   // overflow drops the oldest
        _far.Add(new ContainmentFarStrike
        {
            SourceId = d.SourceId, Surface = surface, StartTimeSeconds = d.StartTimeSeconds, Energy = energy, CrawlRadiusMeters = 2000.0f + 4000.0f * energy,
            LifeSeconds = Math.Clamp(d.DurationSeconds, 0.1f, (float)FarStrikeMaxLifeSeconds), FlashLevel = d.FlashLevel,
        });
        MarkSeen(d);
        FarListed++;
    }

    // ---- helpers ----

    private void MarkSeen(in ContainmentDisturbance d) => _seen[d.SourceId] = (d.StartTimeSeconds, d.StartTimeSeconds + d.DurationSeconds);

    private double DistanceFromObserver(in ContainmentFieldPoint p)
    {
        if (Observer is not RingCanonicalPosition o) return 0.0;
        double da = _circumference > 0.0 ? WrapSigned(p.AroundMeters - o.AroundMeters, _circumference) : p.AroundMeters - o.AroundMeters, dz = p.AcrossMeters - o.AcrossMeters;
        return Math.Sqrt(da * da + dz * dz);
    }

    private double Distance(in ContainmentFieldPoint a, in ContainmentFieldPoint b)
    {
        double da = _circumference > 0.0 ? WrapSigned(a.AroundMeters - b.AroundMeters, _circumference) : a.AroundMeters - b.AroundMeters;
        double dz = a.AcrossMeters - b.AcrossMeters, dv = a.VerticalMeters - b.VerticalMeters;
        return Math.Sqrt(da * da + dz * dz + dv * dv);
    }

    private static double WrapSigned(double v, double c) { double r = v % c; if (r > 0.5 * c) r -= c; else if (r < -0.5 * c) r += c; return r; }

    /// <summary>Start times of accepted spawns; a new one is allowed while the windows [t-1, t] and [t, t+1] each hold fewer than the cap.</summary>
    private sealed class RateWindow
    {
        private readonly List<double> _times = new();
        public bool Allows(double t, int cap)
        {
            int before = 0, after = 0;
            foreach (double e in _times) { if (e > t - 1.0 && e <= t) before++; if (e >= t && e < t + 1.0) after++; }
            return before < cap && after < cap;
        }
        public void Add(double t) => _times.Add(t);
        public void Clear() => _times.Clear();
        public void Prune(double now, bool back)
        {
            for (int i = _times.Count - 1; i >= 0; i--) if (_times[i] < now - 4.0 || (back && _times[i] > now)) _times.RemoveAt(i);
        }
    }
}
