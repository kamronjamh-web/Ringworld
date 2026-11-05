using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Containment field WO2 (docs/containment-field-design.md 4 and 8 WO2): the disturbance bus (dedupe, the pool of 16, eviction order, the 6/s limit, merge rule, the
/// reservations, field-strike caps / restrike merge / far list, BodyContact cooldown), the envelope, the lightning / sprite / storm-contact / megastorm adapters against
/// the real field geometry, the shared megastorm fixture (tools/fixtures/containment_megastorm_fixture.json, read only), the real provider and its ceiling, and a scripted
/// busy-megastorm minute. <see cref="Run"/> is pure; <see cref="Tick"/> is a staged check of the wiring in the integration scene (host node, real layer).
/// Prints CONTAINMENT_DISTURBANCE=PASS.
/// </summary>
public partial class ContainmentDisturbanceTest : RefCounted
{
    private const double W = 318_000.0, Speed = 350.0;
    private static readonly double Circumference = CertifiedRingScale.EffectiveCircumferenceMeters;
    private static readonly RingCanonicalPosition Observer = new(1_000_000.0, 0.0, 159_000.0);
    private const string FixturePath = "res://tools/fixtures/containment_megastorm_fixture.json";

    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok) _failures.Add(message); }
    private static void Info(string m) => Console.WriteLine("CONTAINMENT_DISTURBANCE_INFO " + m);

    private static ContainmentFieldProfile Prof(double wallTop)
    {
        RimWallDescription wall = RimWallDescription.Current;
        return new ContainmentFieldProfile(W, wallTop, ContainmentFieldProfile.DefaultApexMeters, wall.RootAcross(0, W), wall.RootAcross(1, W), ContainmentFieldProfile.DefaultSkirtHeightMeters);
    }

    private sealed class Rec : IContainmentDisturbanceSink
    {
        public readonly List<ContainmentDisturbance> Items = new();
        private readonly HashSet<long> _ids = new();
        private readonly bool _dedupe;
        public Rec(bool dedupe = true) { _dedupe = dedupe; }
        public bool Publish(in ContainmentDisturbance d) { if (_dedupe && !_ids.Add(d.SourceId)) return false; Items.Add(d); return true; }
    }

    private static ContainmentDisturbance D(long id, ContainmentDisturbanceKind kind, double around, double across, double start, float e = 0.5f, float r = 1000.0f, float t = 5.0f,
        float coupling = 0.0f, Func<double, float>? flash = null, bool natural = false)
        => new(id, kind, new RingCanonicalPosition(around, 36_000.0, across), start, e, r, t, coupling, flash, null, 0.0f, 0.0f, natural);

    private static ContainmentDisturbanceBus NewBus(double? now = null)
    {
        var bus = new ContainmentDisturbanceBus(Prof(10_000.0), Circumference) { Observer = Observer };
        if (now != null) bus.Advance(now.Value);
        return bus;
    }

    private static List<ContainmentRipple> Live(ContainmentDisturbanceBus bus) => bus.Slots.Where(r => r != null).Select(r => r!).ToList();

    private static string Signature(ContainmentDisturbanceBus bus)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < bus.Slots.Count; i++)
        {
            ContainmentRipple? r = bus.Slots[i];
            sb.Append(r == null ? "-" : string.Create(CultureInfo.InvariantCulture, $"{r.SourceId}:{(int)r.Kind}:{r.StartTimeSeconds:R}:{r.Energy:R}:{r.Surface.AroundMeters:R}:{r.Surface.AcrossMeters:R}:{r.CrawlSeed}")).Append('|');
        }
        foreach (ContainmentFarStrike f in bus.FarStrikes) sb.Append(string.Create(CultureInfo.InvariantCulture, $"F{f.SourceId}:{f.StartTimeSeconds:R}|"));
        return sb.ToString();
    }

    private static double WrapSigned(double v, double c) { double r = v % c; if (r > 0.5 * c) r -= c; else if (r < -0.5 * c) r += c; return r; }

    private static RingLightningEvent Ev(long id, int kind, double start, double around, double across, float energy, bool flagged = false, float duration = 0.0f, int seed = 1234,
        double cloudVertical = 8000.0, double groundVertical = 0.0, int systemId = 5)
        => new(id, systemId, start, new RingCanonicalPosition(around, groundVertical, across), new RingCanonicalPosition(around, cloudVertical, across), seed, 0.0f, energy, 6000.0f,
            false, kind, false, flagged, duration);

    public int Run()
    {
        Func<double, double>? savedProvider = MegastormFieldCoupling.FieldUndersideProvider;
        Func<double, double> savedCeiling = MegastormFieldCoupling.CeilingFromUnderside;
        try
        {
            LightningVisualPolicy.Reset();
            Envelope();
            Dedupe();
            PoolRateMerge();
            EvictionOrder();
            Reservations();
            BodyContact();
            Range();
            Coupling();
            DeterminismAndPause();
            ReducedFlashes();
            Sprites();
            StormContact();
            Provider();
            FieldStrikes();
            Megastorm();
            BusyMinute();
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); Console.Error.WriteLine("CONTAINMENT_DISTURBANCE=FAIL exception " + e.Message + " | earlier: " + string.Join(" | ", _failures.Take(10))); return 1; }
        finally
        {
            LightningVisualPolicy.Reset();
            MegastormFieldCoupling.FieldUndersideProvider = savedProvider;
            MegastormFieldCoupling.CeilingFromUnderside = savedCeiling;
            ContainmentFieldProvider.Uninstall();
            MegastormFieldCoupling.FieldUndersideProvider = savedProvider;
        }
        if (_failures.Count != 0) { Console.Error.WriteLine("CONTAINMENT_DISTURBANCE=FAIL " + string.Join(" | ", _failures.Take(25))); return 1; }
        Console.WriteLine("CONTAINMENT_DISTURBANCE=PASS");
        return 0;
    }

    // ---- envelope ----
    private void Envelope()
    {
        foreach (float t in new[] { 0.8f, 1.5f, 3.0f, 5.0f })
        {
            foreach (double tau in new[] { (double)t, t + 1e-9, t + 0.5, 10.0 * t })
                Check(ContainmentDisturbanceBus.Envelope(0.7f, tau, t) == 0.0f, $"A(tau) must be exactly 0 at tau {tau} >= T {t}");
            Check(ContainmentDisturbanceBus.Envelope(0.7f, 0.0, t) == 0.0f && ContainmentDisturbanceBus.Envelope(0.7f, -1.0, t) == 0.0f, "A must be 0 at and before the start");
            float peak = 0.0f;
            for (double tau = 0.0; tau < t; tau += t / 500.0) peak = Math.Max(peak, ContainmentDisturbanceBus.Envelope(0.7f, tau, t));
            Check(peak > 0.3f && peak <= 0.7f, $"envelope peak {peak} out of range for T {t}");
            Check(ContainmentDisturbanceBus.Envelope(0.7f, t - 1e-6, t) < 1e-9f, "A must reach 0 smoothly at T");
            Check(ContainmentDisturbanceBus.FrontRadius(10_000.0f, t, t) is > 9400.0f and < 9600.0f && ContainmentDisturbanceBus.FrontRadius(10_000.0f, 0.0, t) == 0.0f, "front radius 1 - exp(-3) at T");
        }
        // Expired patches leave the pool and the patch reads 0.
        ContainmentDisturbanceBus bus = NewBus(0.0);
        Check(bus.Publish(D(1, ContainmentDisturbanceKind.Impact, 1_000_000, 80_000, 0.0, 0.8f, 1000, 2.0f)), "envelope: publish");
        ContainmentRipple r = Live(bus)[0];
        Check(r.Amplitude(1.0) > 0.0f && r.Amplitude(2.0) == 0.0f && r.Amplitude(3.0) == 0.0f, "ripple amplitude over its life");
        bus.Advance(1.999); Check(bus.LiveCount == 1, "still live just before T");
        bus.Advance(2.0); Check(bus.LiveCount == 0, "freed at tau >= T");
        Check(bus.TryPublish(D(2, ContainmentDisturbanceKind.Impact, 1_000_000, 80_000, 0.0, 0.8f, 1000, 2.0f)) == ContainmentPublishResult.Expired, "an already expired disturbance is rejected");
    }

    // ---- dedupe ----
    private void Dedupe()
    {
        ContainmentDisturbanceBus bus = NewBus(10.0);
        Check(bus.TryPublish(D(1, ContainmentDisturbanceKind.Impact, 1_000_000, 80_000, 10.0, 0.5f, 1000, 2.0f)) == ContainmentPublishResult.Accepted, "dedupe: first");
        Check(bus.TryPublish(D(1, ContainmentDisturbanceKind.Impact, 1_020_000, 90_000, 10.5, 0.9f, 1000, 2.0f)) == ContainmentPublishResult.Duplicate, "dedupe: same SourceId rejected");
        Check(!bus.Publish(D(1, ContainmentDisturbanceKind.Lightning, 1_000_000, 80_000, 10.0)), "dedupe applies across kinds");
        Check(bus.LiveCount == 1 && bus.Duplicates == 2, "dedupe: one patch");
        bus.Advance(14.5);   // ripple ended at 12, grace 2 s
        Check(bus.TryPublish(D(1, ContainmentDisturbanceKind.Impact, 1_000_000, 80_000, 14.5, 0.5f, 1000, 2.0f)) == ContainmentPublishResult.Accepted, "an id is forgotten after its ripple and the grace");
        bus.Advance(12.0);   // scrub back: what started later is removed and may publish again
        Check(bus.LiveCount == 0, "a scrub back removes ripples that had not happened");
        Check(bus.TryPublish(D(1, ContainmentDisturbanceKind.Impact, 1_000_000, 80_000, 14.5, 0.5f, 1000, 2.0f)) == ContainmentPublishResult.Accepted, "re-publish after a scrub");
        Check(bus.TryPublish(D(3, ContainmentDisturbanceKind.Impact, double.NaN, 80_000, 12.0)) == ContainmentPublishResult.Invalid
            && bus.TryPublish(D(4, ContainmentDisturbanceKind.Impact, 1_000_000, 80_000, 12.0, 0.0f)) == ContainmentPublishResult.Invalid, "invalid input rejected");
    }

    // ---- pool cap, 6/s, merge ----
    private void PoolRateMerge()
    {
        // 20 distinct ripples inside one second: exactly 6 spawn.
        ContainmentDisturbanceBus bus = NewBus(1.0);
        int accepted = 0, limited = 0;
        for (int i = 0; i < 20; i++)
        {
            ContainmentPublishResult r = bus.TryPublish(D(100 + i, ContainmentDisturbanceKind.Impact, 1_000_000 + (i - 10) * 15_000, 80_000, i * 0.05, 0.5f, 1000, 3.0f));
            if (r == ContainmentPublishResult.Accepted) accepted++; else if (r == ContainmentPublishResult.RateLimited) limited++;
        }
        Check(accepted == 6 && limited == 14, $"6/s limit: {accepted} accepted, {limited} limited");
        // A long stream at 20/s: the live count never passes 16, no sliding second holds more than 6 spawns.
        bus = NewBus();
        var starts = new List<double>();
        int maxLive = 0;
        for (int i = 0; i < 1200; i++)
        {
            double t = i * 0.05;
            bus.Advance(t);
            if (bus.Publish(D(1000 + i, ContainmentDisturbanceKind.Impact, 1_000_000 + ((i % 21) - 10) * 15_000, 40_000 + (i % 5) * 40_000, t, 0.5f, 1000, 10.0f))) starts.Add(t);
            maxLive = Math.Max(maxLive, bus.LiveCount);
        }
        Check(maxLive <= ContainmentDisturbanceBus.PoolSize && bus.PeakLive <= 16, $"pool cap: peak {bus.PeakLive}");
        Check(maxLive >= 10, $"the stream never filled the shared slots ({maxLive})");
        Check(starts.All(a => starts.Count(b => b >= a - 1e-9 && b < a + 1.0 - 1e-9) <= 6), "a sliding second held more than 6 spawns");
        Check(starts.Count >= 1200 / 20 * 5, $"the 6/s limit starved the stream ({starts.Count} spawns in 60 s)");
        Info($"stream 60 s at 20/s: {starts.Count} spawns, peak live {bus.PeakLive}, evictions {bus.Evicted}");

        // Merge: same kind, within 0.25 s, closer than Rmax.
        bus = NewBus(0.0);
        const double A0 = 1_000_000.0;
        Check(bus.TryPublish(D(1, ContainmentDisturbanceKind.Lightning, A0, 80_000, 0.0, 0.4f, 5000, 3.0f)) == ContainmentPublishResult.Accepted, "merge: first");
        Check(bus.TryPublish(D(2, ContainmentDisturbanceKind.Lightning, A0 + 2000, 80_000, 0.2, 0.9f, 5000, 3.0f)) == ContainmentPublishResult.Merged, "merge: same kind, 0.2 s, 2 km");
        Check(bus.LiveCount == 1 && Math.Abs(Live(bus)[0].Energy - 0.9f) < 1e-6, "merge: energy is the max, no new slot");
        Check(bus.TryPublish(D(3, ContainmentDisturbanceKind.Lightning, A0 + 6000, 80_000, 0.2, 0.5f, 5000, 3.0f)) == ContainmentPublishResult.Accepted, "merge: beyond Rmax is a new ripple");
        Check(bus.TryPublish(D(4, ContainmentDisturbanceKind.Impact, A0, 80_000, 0.2, 0.5f, 5000, 3.0f)) == ContainmentPublishResult.Accepted, "merge: a different kind is a new ripple");
        Check(bus.TryPublish(D(5, ContainmentDisturbanceKind.Lightning, A0 - 3000, 80_000, 0.3, 0.5f, 5000, 3.0f)) == ContainmentPublishResult.Accepted, "merge: 0.3 s apart is a new ripple");
        Check(bus.TryPublish(D(5, ContainmentDisturbanceKind.Lightning, A0 - 3000, 80_000, 0.3, 0.5f, 5000, 3.0f)) == ContainmentPublishResult.Duplicate, "an accepted id is remembered");
        Check(bus.TryPublish(D(2, ContainmentDisturbanceKind.Lightning, A0, 80_000, 0.0)) == ContainmentPublishResult.Duplicate, "a merged id is a duplicate afterwards");
        Check(bus.LiveCount == 4 && bus.Merged == 1, $"merge: {bus.LiveCount} live, {bus.Merged} merged");
    }

    // ---- eviction order ----
    private void EvictionOrder()
    {
        ContainmentDisturbanceBus bus = NewBus(2.0);
        // 4 strikes and 2 body contacts sit at their reservation (published early, long lived); 10 plain ripples fill the rest.
        for (int i = 0; i < 4; i++)
            Check(bus.Publish(D(10 + i, ContainmentDisturbanceKind.FieldStrike, 1_000_000 + (i - 2) * 90_000 + 40_000, 60_000 + i * 50_000, -5.9 + i * 0.5, 0.6f, 3000, 8.0f)), "eviction: strike fill");
        for (int i = 0; i < 2; i++)
            Check(bus.Publish(D(20 + i, ContainmentDisturbanceKind.BodyContact, 1_000_000 + (i - 1) * 120_000, 100_000 + i * 20_000, -3.5 + i * 0.01, 0.6f, 3000, 8.0f)), "eviction: body fill");
        for (int i = 0; i < 10; i++)
            Check(bus.Publish(D(30 + i, ContainmentDisturbanceKind.Impact, 1_000_000 + (i - 5) * 30_000 + (i % 2) * 4000, 70_000 + (i % 3) * 20_000, -1.8 + 0.18 * i, 0.3f + 0.05f * i,
                1000, 6.0f + (i % 3))), $"eviction: fill {i}");
        Check(bus.LiveCount == 16, $"eviction: pool full ({bus.LiveCount})");
        List<ContainmentRipple> before = Live(bus);
        double Score(ContainmentRipple r)
        {
            double tau = Math.Max(0.0, 2.0 - r.StartTimeSeconds), life = Math.Clamp(1.0 - tau / r.DurationSeconds, 0.0, 1.0);
            double da = WrapSigned(r.Surface.AroundMeters - Observer.AroundMeters, Circumference), dz = r.Surface.AcrossMeters - Observer.AcrossMeters;
            return r.Energy * life / (1.0 + Math.Sqrt(da * da + dz * dz) / 100_000.0);
        }
        ContainmentRipple expected = before.Where(r => r.Kind == ContainmentDisturbanceKind.Impact).OrderBy(Score).First();
        Check(bus.TryPublish(D(99, ContainmentDisturbanceKind.Impact, 1_000_000, 90_000, 2.0, 0.9f, 1000, 5.0f)) == ContainmentPublishResult.Accepted, "eviction: newcomer accepted");
        List<ContainmentRipple> after = Live(bus);
        long gone = before.Select(r => r.SourceId).Except(after.Select(r => r.SourceId)).Single();
        Check(gone == expected.SourceId, $"evicted {gone}, lowest E x (1 - tau/T) / (1 + d/100 km) is {expected.SourceId}");
        Check(after.Count == 16 && bus.Evicted == 1, "eviction: still 16");
        Check(after.Count(r => r.Kind == ContainmentDisturbanceKind.FieldStrike) == 4 && after.Count(r => r.Kind == ContainmentDisturbanceKind.BodyContact) == 2, "eviction never takes reserved patches");
        // A second newcomer takes the next-lowest one.
        ContainmentRipple expected2 = after.Where(r => r.Kind == ContainmentDisturbanceKind.Impact && r.SourceId != 99).OrderBy(Score).First();
        Check(bus.TryPublish(D(98, ContainmentDisturbanceKind.Impact, 1_040_000, 90_000, 2.0, 0.9f, 1000, 5.0f)) == ContainmentPublishResult.Accepted, "eviction: second newcomer");
        long gone2 = after.Select(r => r.SourceId).Except(Live(bus).Select(r => r.SourceId)).Single();
        Check(gone2 == expected2.SourceId, $"second eviction took {gone2}, expected {expected2.SourceId}");
    }

    // ---- reservations ----
    private void Reservations()
    {
        // A saturating load of contact, bursts, lightning and the rest: 4 strike slots (and 2 body slots) stay free; contact <= 6 live, bursts <= 3.
        ContainmentDisturbanceBus bus = NewBus(0.0);
        ContainmentDisturbanceKind[] cycle = { ContainmentDisturbanceKind.MegastormContact, ContainmentDisturbanceKind.UpdraftBurst, ContainmentDisturbanceKind.Impact,
            ContainmentDisturbanceKind.Lightning, ContainmentDisturbanceKind.StormContact, ContainmentDisturbanceKind.MegastormContact, ContainmentDisturbanceKind.UpdraftBurst };
        int maxContact = 0, maxBurst = 0, maxShared = 0;
        for (int i = 0; i < 200; i++)
        {
            double t = i * 0.2;
            bus.Advance(t);
            bus.Publish(D(5000 + i, cycle[i % cycle.Length], 1_000_000 + ((i * 7) % 29 - 14) * 12_000, 60_000 + (i % 7) * 20_000, t, 0.5f + 0.001f * (i % 50), 800, 12.0f));
            maxContact = Math.Max(maxContact, bus.LiveOf(ContainmentDisturbanceKind.MegastormContact));
            maxBurst = Math.Max(maxBurst, bus.LiveOf(ContainmentDisturbanceKind.UpdraftBurst));
            maxShared = Math.Max(maxShared, bus.LiveCount - bus.LiveOf(ContainmentDisturbanceKind.FieldStrike) - bus.LiveOf(ContainmentDisturbanceKind.BodyContact));
        }
        Check(maxContact >= 2 && maxContact <= 6, $"contact live max {maxContact} (cap 6)");
        Check(maxBurst >= 1 && maxBurst <= 3, $"burst live max {maxBurst} (cap 3)");
        foreach ((ContainmentDisturbanceKind kind, int cap) in new[] { (ContainmentDisturbanceKind.MegastormContact, 6), (ContainmentDisturbanceKind.UpdraftBurst, 3) })
        {
            var solo = NewBus(0.0);
            int peak = 0;
            for (int i = 0; i < 60; i++) { solo.Advance(i * 0.3); solo.Publish(D(9000 + i, kind, 1_000_000 + ((i * 7) % 29 - 14) * 12_000, 60_000 + (i % 7) * 20_000, i * 0.3, 0.5f, 800, 12.0f)); peak = Math.Max(peak, solo.LiveCount); }
            Check(peak == cap && solo.LiveCount == cap, $"{kind} alone: live peak {peak}, expected the cap {cap}");
        }
        Check(maxShared <= 16 - ContainmentDisturbanceBus.StrikeReserve - ContainmentDisturbanceBus.BodyReserve, $"non-reserved classes held {maxShared} slots (max 10)");
        Check(bus.LiveCount <= 10, $"the saturated pool holds {bus.LiveCount}");
        // Four strikes then fit without evicting anything.
        double t0 = 39.8;
        bus.Advance(t0);
        int live0 = bus.LiveCount;
        for (int j = 0; j < 4; j++)
        {
            ContainmentPublishResult r = bus.TryPublish(D(7000 + j, ContainmentDisturbanceKind.FieldStrike, 1_000_000 + (j - 2) * 80_000 + 30_000, 50_000 + j * 60_000, t0 + j * 0.6, 0.8f, 6000, 3.0f));
            Check(r == ContainmentPublishResult.Accepted, $"strike {j} under a saturating load: {r}");
        }
        Check(bus.LiveOf(ContainmentDisturbanceKind.FieldStrike) == 4, "4 strikes live");
        Check(bus.LiveCount == live0 + 4, "the strikes took free slots, nothing was evicted for them");
        // The same with the shared slots also saturated by long-lived ripples: still 4 for strikes.
        bus = NewBus(0.0);
        for (int i = 0; i < 40; i++)
        {
            bus.Advance(i * 0.2);
            bus.Publish(D(8000 + i, ContainmentDisturbanceKind.Impact, 1_000_000 + ((i * 5) % 23 - 11) * 14_000, 60_000 + (i % 6) * 25_000, i * 0.2, 0.5f, 800, 20.0f));
        }
        Check(bus.LiveCount == 10, $"shared slots saturate at 10 ({bus.LiveCount})");
        bus.Advance(10.0);
        int strikeOk = 0;
        for (int j = 0; j < 4; j++)
            if (bus.Publish(D(8500 + j, ContainmentDisturbanceKind.FieldStrike, 1_000_000 + (j - 2) * 70_000 + 20_000, 50_000 + j * 55_000, 10.0 + j * 0.6, 0.8f, 6000, 3.0f))) strikeOk++;
        Check(strikeOk == 4 && bus.LiveCount == 14, $"4 strikes with the shared slots full: {strikeOk}, live {bus.LiveCount}");
        // The 2 body slots are free too.
        Check(bus.Publish(D(1, ContainmentDisturbanceKind.BodyContact, 1_000_000, 100_000, 10.0, 0.5f, 5, 1.5f)) && bus.Publish(D(2, ContainmentDisturbanceKind.BodyContact, 1_010_000, 110_000, 10.0, 0.5f, 5, 1.5f))
            && bus.LiveCount == 16, "the 2 BodyContact slots are reserved");
    }

    // ---- BodyContact: reservation, cooldown ----
    private void BodyContact()
    {
        ContainmentDisturbanceBus bus = NewBus(0.0);
        ContainmentPublishResult R(long id, double t, double around = 1_000_000) => bus.TryPublish(D(id, ContainmentDisturbanceKind.BodyContact, around, 100_000, t, 0.6f, 6, 1.5f));
        Check(R(1, 0.0) == ContainmentPublishResult.Accepted, "body: first contact");
        Check(R(1, 0.4) == ContainmentPublishResult.Cooldown && R(1, 0.99) == ContainmentPublishResult.Cooldown, "body: a player standing in the sheet cannot spam it (cooldown)");
        Check(R(1, 1.0) == ContainmentPublishResult.Accepted, "body: the cooldown ends after 1 s");
        Check(R(2, 1.0, 1_050_000) == ContainmentPublishResult.Accepted, "body: another source is independent");
        // Only 2 live: a third source takes the lowest-scored body patch, never another class's.
        Check(R(3, 1.2, 1_100_000) == ContainmentPublishResult.Accepted && bus.LiveOf(ContainmentDisturbanceKind.BodyContact) == 2, "body: at most 2 live");
        Check(bus.Publish(D(9, ContainmentDisturbanceKind.Impact, 1_000_000, 80_000, 1.2)), "body: others still publish");
    }

    // ---- range, far list ----
    private void Range()
    {
        ContainmentDisturbanceBus bus = NewBus(0.0);
        Check(bus.TryPublish(D(1, ContainmentDisturbanceKind.Lightning, Observer.AroundMeters + 450_000, 159_000, 0.0)) == ContainmentPublishResult.OutOfRange, "> 400 km rejected");
        Check(bus.TryPublish(D(2, ContainmentDisturbanceKind.Lightning, Observer.AroundMeters + 399_000, 159_000, 0.0)) == ContainmentPublishResult.Accepted, "399 km accepted");
        Check(bus.TryPublish(D(3, ContainmentDisturbanceKind.Lightning, Observer.AroundMeters - 401_000, 159_000, 0.0)) == ContainmentPublishResult.OutOfRange, "401 km behind rejected (wrapped)");
        // The wrap: an around 1/2 ring away is out, the same point seen across the seam is in.
        var near = new ContainmentDisturbanceBus(Prof(10_000.0), Circumference) { Observer = new RingCanonicalPosition(Circumference - 10_000.0, 0.0, 159_000.0) };
        Check(near.TryPublish(D(4, ContainmentDisturbanceKind.Lightning, 20_000.0, 159_000, 0.0)) == ContainmentPublishResult.Accepted, "range wraps around the ring");
        // Strikes beyond 350 km go to the far list, never the pool, 32 entries, oldest dropped, ending with their pulse.
        bus = NewBus(5.0);
        for (int i = 0; i < 40; i++)
            Check(bus.TryPublish(D(100 + i, ContainmentDisturbanceKind.FieldStrike, Observer.AroundMeters + 360_000 + i * 5000, 80_000, 5.0 + i * 0.01, 0.8f, 6000, 1.5f)) == ContainmentPublishResult.FarList, $"far strike {i}");
        Check(bus.LiveCount == 0 && bus.FarStrikes.Count == 32 && bus.FarDropped == 8, $"far list: pool {bus.LiveCount}, list {bus.FarStrikes.Count}, dropped {bus.FarDropped}");
        Check(bus.FarStrikes[0].SourceId == 108 && bus.FarStrikes[31].SourceId == 139, "far list keeps the newest 32");
        Check(bus.TryPublish(D(100, ContainmentDisturbanceKind.FieldStrike, Observer.AroundMeters + 380_000, 80_000, 5.0, 0.8f, 6000, 1.5f)) == ContainmentPublishResult.Duplicate, "far strikes dedupe too");
        bus.Advance(7.0);
        Check(bus.FarStrikes.Count == 0, "far strikes end with their pulse (<= 2 s)");
        Check(bus.TryPublish(D(200, ContainmentDisturbanceKind.FieldStrike, Observer.AroundMeters + 349_000, 159_000, 7.0, 0.8f, 6000, 3.0f)) == ContainmentPublishResult.Accepted
            && bus.LiveCount == 1, "a strike at 349 km uses the pool");
        Check(bus.TryPublish(D(201, ContainmentDisturbanceKind.FieldStrike, Observer.AroundMeters + 351_000, 159_000, 7.0, 0.8f, 6000, 3.0f)) == ContainmentPublishResult.FarList, "a strike at 351 km goes far");
        // No observer: nothing is out of range.
        var free = new ContainmentDisturbanceBus(Prof(10_000.0), Circumference);
        Check(free.Publish(D(1, ContainmentDisturbanceKind.Lightning, 20_000_000, 159_000, 0.0)), "no observer: accepted");
    }

    // ---- lightning adapter: coupling threshold ----
    private void Coupling()
    {
        ContainmentFieldProfile p = Prof(10_000.0);
        int stormCalls = 0;
        var sink = new Rec();
        var adapter = new LightningDisturbanceAdapter(sink, p, e => { stormCalls++; return new LightningStormInfo(18_000.0f, 20_000.0f); });
        double centre = p.Centre;
        RingLightningEvent atCentre = Ev(1, 3, 10.0, 1_000_000, centre, 1.0f), atRim = Ev(2, 3, 10.0, 1_000_000, 1000.0, 1.0f);
        Check(!adapter.TryMake(atCentre, out _), "a centre-habitat 18 km top gives no ripple");
        Check(stormCalls == 0, "the centre event needs no storm lookup");
        Check(adapter.TryMake(atRim, out ContainmentDisturbance d), "a 1 km-from-rim 18 km top does");
        Check(d.Kind == ContainmentDisturbanceKind.Lightning && d.SourceId == 2 && d.Energy >= LightningDisturbanceAdapter.MinEnergy && d.Energy <= 1.0f, "lightning payload");
        Check(Math.Abs(d.RadiusMeters - (3000.0f + 5000.0f * d.Energy)) < 1e-3 && d.DurationSeconds == 2.5f && d.FlashCoupling == 1.0f && d.OccluderTopMeters == 18_000.0f && d.OccluderCenter == atRim.CloudPosition,
            "lightning: Rmax 3-8 km, T 2.5 s, source flash, occluder = the parent storm");
        Check(d.Position.VerticalMeters == 18_000.0 && d.Position.AcrossMeters == 1000.0, "the source point is the storm top");
        // The threshold across: gap = 2 km x ln 10 for E = 1.
        double h = 18_000.0 + LightningDisturbanceAdapter.GapScaleMeters * Math.Log(10.0), lo = p.RootAcross0, hi = centre;
        for (int i = 0; i < 60; i++) { double m = 0.5 * (lo + hi); if (ContainmentFieldGeometry.HeightAt(p, m) < h) lo = m; else hi = m; }
        Check(adapter.TryMake(Ev(3, 3, 10.0, 1_000_000, lo - 10.0, 1.0f), out _) && !adapter.TryMake(Ev(4, 3, 10.0, 1_000_000, lo + 10.0, 1.0f), out _), $"the E = 0.1 edge is at across {lo:0} (H {h:0})");
        Check(!adapter.TryMake(Ev(5, 3, 10.0, 1_000_000, lo - 10.0, 0.5f), out _), "a weaker bolt at the same place does not couple");
        Check(!adapter.TryMake(Ev(6, 3, 10.0, 1_000_000, -5000.0, 1.0f), out _), "outside the roots: no field, no ripple");
        // Scythes publish nothing.
        RingLightningEvent scythe = atRim with { EventId = 7, Scythe = true };
        Check(!adapter.TryMake(scythe, out _), "scythe attachments publish nothing");
        // A storm whose top is low never reaches.
        var low = new LightningDisturbanceAdapter(sink, p, e => new LightningStormInfo(9000.0f, 1000.0f));
        Check(!low.TryMake(atRim, out _), "a 9 km top 5 km under the field gives no ripple");
        // Update: once per id, forgetting ids that leave the list.
        var list = new List<RingLightningEvent> { atRim, atCentre };
        var rec = new Rec(false);
        var a2 = new LightningDisturbanceAdapter(rec, p, e => new LightningStormInfo(18_000.0f, 20_000.0f));
        for (int i = 0; i < 10; i++) a2.Update(list);
        Check(rec.Items.Count == 1 && a2.Published == 1, "polling the same event every frame publishes once");
        list.Clear(); a2.Update(list); list.Add(atRim); a2.Update(list);
        Check(a2.Published == 2, "an id that left the list publishes again when it returns (rewind)");
    }

    // ---- determinism and paused clock ----
    private sealed class Pipeline
    {
        public readonly ContainmentDisturbanceBus Bus = NewBus();
        public readonly LightningDisturbanceAdapter Lightning;
        public readonly UpperAtmosphereDisturbanceAdapter Upper;
        public readonly List<RingLightningEvent> Source, Active = new();
        public Pipeline(List<RingLightningEvent> source, ContainmentFieldProfile profile)
        {
            Source = source;
            Bus.Profile = profile;
            Lightning = new LightningDisturbanceAdapter(Bus, profile, e => new LightningStormInfo(17_500.0f, 20_000.0f));
            Upper = new UpperAtmosphereDisturbanceAdapter(Bus, profile, () => 17_000.0f, () => 1.0f);
        }
        public void Step(double now)
        {
            Active.Clear();
            foreach (RingLightningEvent e in Source) if (e.StartTimeSeconds <= now && now - e.StartTimeSeconds <= e.LifeSeconds) Active.Add(e);
            Bus.Advance(now);
            Lightning.Update(Active);
            Upper.Update(Active);
        }
    }

    private static List<RingLightningEvent> Stream(int count, double spacing, double firstAt = 0.0)
    {
        var list = new List<RingLightningEvent>();
        for (int i = 0; i < count; i++)
        {
            double u = UpperAtmosphereLightning.Unit(i + 1, 900), v = UpperAtmosphereLightning.Unit(i + 1, 901), w = UpperAtmosphereLightning.Unit(i + 1, 902);
            bool strike = i % 9 == 4;
            double across = (i % 3 == 0 ? 500.0 + 6000.0 * u : i % 3 == 1 ? W - 500.0 - 6000.0 * u : W * v);
            list.Add(Ev(1000 + i, 3 + i % 4, firstAt + i * spacing, Observer.AroundMeters + (w - 0.5) * 300_000.0, across, 0.5f + 0.5f * (float)u, strike, strike ? 0.85f + 1.0f * (float)v : 0.0f,
                unchecked((int)(1234567u * (uint)(i + 1)))));
        }
        return list;
    }

    private void DeterminismAndPause()
    {
        ContainmentFieldProfile p = Prof(10_000.0);
        List<RingLightningEvent> events = Stream(120, 0.4);
        string Run(int frames, double dt, out int maxLive, int pausedFrom = int.MaxValue)
        {
            var pl = new Pipeline(events, p);
            double now = 0.0;
            var history = new System.Text.StringBuilder();
            maxLive = 0;
            for (int f = 0; f < frames; f++)
            {
                if (f < pausedFrom) now = f * dt;
                pl.Step(now);
                maxLive = Math.Max(maxLive, pl.Bus.LiveCount);
                if (f % 5 == 0) history.Append(Signature(pl.Bus)).Append((char)10);
            }
            return Signature(pl.Bus) + "#" + pl.Bus.Accepted + "," + pl.Bus.Merged + "," + pl.Bus.Restruck + "," + pl.Bus.Evicted + "," + pl.Bus.RateLimited + "#" + history;
        }
        string a = Run(1500, 1.0 / 30.0, out int liveA), b = Run(1500, 1.0 / 30.0, out _);
        Check(a == b, "the same event sequence gives the same pool, frame by frame");
        Check(liveA >= 4, $"determinism test is vacuous (max live {liveA})");
        // Paused clock: after the clock stops, 300 more frames change nothing.
        string at = Run(900, 1.0 / 30.0, out int liveAt), paused = Run(1200, 1.0 / 30.0, out _, 900);
        at = at[..at.IndexOf('#')]; paused = paused[..paused.IndexOf('#')];
        Check(liveAt >= 4 && !at.StartsWith("-|-|-|-|-|-|-|-|-|-|-|-|-|-|-|-|#"), "pause test is vacuous");
        Check(at == paused, "a paused clock gives the same state");
        // Scrub: back to an earlier time removes what had not happened yet, and the same ids publish again going forward.
        var pl2 = new Pipeline(events, p);
        for (int f = 0; f < 900; f++) pl2.Step(f / 30.0);
        int published = pl2.Bus.Accepted;
        for (int f = 899; f >= 300; f -= 10)
        {
            pl2.Step(f / 30.0);
            Check(Live(pl2.Bus).All(r => r.StartTimeSeconds <= f / 30.0 + 1e-9), "a scrub back left a ripple that had not started");
        }
        for (int f = 300; f < 900; f++) pl2.Step(f / 30.0);
        Check(pl2.Bus.Accepted > published, "ids publish again after a scrub");
        Info($"stream: {events.Count} events, pool signature length {a.Length}");
    }

    // ---- reduced flashes ----
    private void ReducedFlashes()
    {
        ContainmentFieldProfile p = Prof(10_000.0);
        foreach (bool reduced in new[] { true, false })
        {
            LightningVisualPolicy.Apply(1.0f, 1.0f, reduced);
            var bus = NewBus(0.0);
            bus.Profile = p;
            var adapter = new LightningDisturbanceAdapter(bus, p, e => new LightningStormInfo(17_500.0f, 20_000.0f));
            RingLightningEvent strike = Ev(1, 6, 0.0, 1_000_000, 159_000, 0.9f, true, 1.5f, 4242), ordinary = Ev(2, 3, 0.0, 1_100_000, 1500.0, 1.0f, false, 0.0f, 11);
            adapter.Update(new[] { strike, ordinary });
            Check(bus.LiveCount == 2, $"reduced={reduced}: both publish");
            double peak = 0.0, maxStep = 0.0, prev = 0.0;
            foreach (ContainmentRipple r in Live(bus))
            {
                RingLightningEvent src = r.SourceId == 1 ? strike : ordinary;
                double rpeak = 0.0, rprev = 0.0, rstep = 0.0;
                for (double t = 0.0; t <= 2.0; t += 1.0 / 60.0)
                {
                    float f = r.FlashAt(t);
                    Check(Math.Abs(f - src.PulseAt(t)) < 1e-6f, $"reduced={reduced}: flash passthrough at {t:0.000}");
                    rpeak = Math.Max(rpeak, f); rstep = Math.Max(rstep, Math.Abs(f - rprev)); rprev = f;
                }
                peak = Math.Max(peak, rpeak); maxStep = Math.Max(maxStep, rstep);
                if (reduced) Check(rpeak <= LightningVisualPolicy.ReducedPeakScale + 1e-5 && rstep <= 0.052, $"reduced flashes: peak {rpeak:0.000} step {rstep:0.00000} (source {src.EventId})");
                else Check(rpeak > 0.6, $"full flashes: peak {rpeak:0.000}");
                _ = prev;
            }
            Info($"reduced={reduced}: peak {peak:0.000}, max per-frame step {maxStep:0.000}");
        }
        LightningVisualPolicy.Reset();
    }

    // ---- sprites and jets ----
    private void Sprites()
    {
        ContainmentFieldProfile p = Prof(10_000.0);
        // Hand-built columns.
        double acrossLow = 1000.0, hLow = ContainmentFieldGeometry.HeightAt(p, acrossLow), acrossMid = 100_000.0, hMid = ContainmentFieldGeometry.HeightAt(p, acrossMid);
        UpperAtmosphereSprite Jet(long id, UpperAtmosphereKind kind, double baseVertical, float height, double across, float energy = 0.8f)
            => new(id, kind, 5.0, 0.3, new RingCanonicalPosition(2_000_000, baseVertical, across), height, 14_000.0f, energy);
        Check(UpperAtmosphereDisturbanceAdapter.TryMake(p, Jet(1, UpperAtmosphereKind.BlueJet, hMid - 5000.0, 40_000.0f, acrossMid), out ContainmentDisturbance d)
            && d.Position == new RingCanonicalPosition(2_000_000, hMid, acrossMid) && d.Kind == ContainmentDisturbanceKind.UpperAtmosphere && d.SourceId == (1L ^ UpperAtmosphereDisturbanceAdapter.SourceSalt),
            "a jet whose column spans H publishes at (around, H, across)");
        Check(!UpperAtmosphereDisturbanceAdapter.TryMake(p, Jet(2, UpperAtmosphereKind.BlueJet, hLow + 1000.0, 40_000.0f, acrossLow), out _), "a column starting above H does not cross");
        Check(!UpperAtmosphereDisturbanceAdapter.TryMake(p, Jet(3, UpperAtmosphereKind.BlueJet, hMid - 50_000.0, 40_000.0f, acrossMid), out _), "a column ending below H does not cross");
        Check(!UpperAtmosphereDisturbanceAdapter.TryMake(p, Jet(4, UpperAtmosphereKind.RedSprite, 6000.0 + UpperAtmosphereLightning.SpriteAltitudeMeters, 45_000.0f, acrossMid), out _)
            && !UpperAtmosphereDisturbanceAdapter.TryMake(p, Jet(5, UpperAtmosphereKind.RedSprite, 17_000.0 + UpperAtmosphereLightning.SpriteAltitudeMeters, 45_000.0f, 159_000.0), out _), "red sprites (base about 61 km, above the apex) never publish");
        Check(!UpperAtmosphereDisturbanceAdapter.TryMake(p, Jet(6, UpperAtmosphereKind.BlueJet, hMid - 1000.0, 40_000.0f, -5000.0), out _), "no field outside the roots");
        Check(UpperAtmosphereDisturbanceAdapter.TryMake(p, Jet(7, UpperAtmosphereKind.GiganticJet, 6000.0, 75_000.0f, 159_000.0), out ContainmentDisturbance gj)
            && Math.Abs(gj.Position.VerticalMeters - 40_000.0) < 1e-6, "a gigantic jet crosses the apex");

        // A 600 s event stream: the sprite lists are identical with the adapter on or off, publications are exactly the crossing jets.
        var events = new List<RingLightningEvent>();
        for (int i = 0; i < 1200; i++)
        {
            double u = UpperAtmosphereLightning.Unit(i + 5, 31), v = UpperAtmosphereLightning.Unit(i + 5, 32), w = UpperAtmosphereLightning.Unit(i + 5, 33);
            double across = i % 4 == 0 ? 500.0 + 8000.0 * u : W * v;   // a quarter of the stream is near the rims, where H is under the 17 km jets
            events.Add(Ev(50_000 + i * 7, 3 + i % 4, i * 0.5, 100_000.0 + w * 3_000_000.0, across, 0.3f + 0.7f * (float)u, false, 0.0f, i * 31, cloudVertical: 17_000.0));
        }
        List<UpperAtmosphereSprite> Derive()
        {
            var list = new List<UpperAtmosphereSprite>();
            foreach (RingLightningEvent e in events) if (UpperAtmosphereLightning.TryDerive(e, 17_000.0f, out UpperAtmosphereSprite s, 1.0f)) list.Add(s);
            return list;
        }
        List<UpperAtmosphereSprite> off = Derive();
        var rec = new Rec();
        var adapter = new UpperAtmosphereDisturbanceAdapter(rec, p, () => 17_000.0f, () => 1.0f);
        adapter.Update(events);
        List<UpperAtmosphereSprite> on = Derive();
        Check(off.SequenceEqual(on) && off.Count > 100, $"sprites identical with the adapter on or off ({off.Count} sprites)");
        var expectedIds = new HashSet<long>();
        int red = 0, jets = 0, jetsSkipped = 0;
        foreach (UpperAtmosphereSprite s in off)
        {
            bool crosses = UpperAtmosphereDisturbanceAdapter.TryMake(p, s, out _);
            if (s.Kind == UpperAtmosphereKind.RedSprite) { red++; Check(!crosses, "a red sprite crossed"); }
            else { jets++; if (crosses) expectedIds.Add(UpperAtmosphereDisturbanceAdapter.SourceIdFor(s.EventId)); else jetsSkipped++; }
        }
        Check(red > 20 && jets > 20 && jetsSkipped > 0 && expectedIds.Count > 0, $"stream coverage: {red} red, {jets} jets, {jetsSkipped} jets under the field");
        Check(rec.Items.Select(x => x.SourceId).ToHashSet().SetEquals(expectedIds) && rec.Items.All(x => x.Kind == ContainmentDisturbanceKind.UpperAtmosphere), "published ids = crossing jets only");
        foreach (ContainmentDisturbance x in rec.Items)
            Check(Math.Abs(x.Position.VerticalMeters - ContainmentFieldGeometry.HeightAt(p, x.Position.AcrossMeters)) < 1e-9, "published on the field");
        Info($"sprites: {off.Count} derived, {red} red (none published), {jets} jets, {rec.Items.Count} crossing");

        // The same EventId through the natural layer and through megastorm UpperDischarge: one ripple.
        MegastormModel model = MakeFixtureModel(out FixtureData f);
        ContainmentFieldProvider.Install(Prof(10_000.0));
        MegastormLightning.MegastormFlash? chosen = null;
        UpperAtmosphereSprite chosenSprite = default;
        var flashes = new List<MegastormLightning.MegastormFlash>();
        MegastormLightning.FlashesInWindow(model, f.WeatherTime, f.WeatherTime + 600.0, flashes);
        foreach (MegastormLightning.MegastormFlash fl in flashes)
            if (MegastormFieldCoupling.TryDeriveSprite(model, fl, out UpperAtmosphereSprite sp, 1.0f) && UpperAtmosphereDisturbanceAdapter.TryMake(p, sp, out _)) { chosen = fl; chosenSprite = sp; break; }
        Check(chosen != null, "no crossing megastorm discharge in 600 s (test would be vacuous)");
        if (chosen is MegastormLightning.MegastormFlash flash)
        {
            RingLightningEvent fe = MegastormFieldCoupling.FlashEvent(model, flash)!.Value;
            long id = UpperAtmosphereDisturbanceAdapter.SourceIdFor(flash.Id);
            foreach (bool naturalFirst in new[] { true, false })
            {
                var bus = NewBus(flash.StartTimeSeconds + 0.1);
                bus.Profile = p; bus.Observer = new RingCanonicalPosition(chosenSprite.BasePosition.AroundMeters, 0.0, f.CameraAcross);
                var natural = new UpperAtmosphereDisturbanceAdapter(bus, p, () => model.Evaluate(flash.StartTimeSeconds).TopMeters, () => 1.0f);
                var mega = new MegastormDisturbanceAdapter(() => model, bus, p) { SpriteChanceScale = 1.0f };
                void Natural() => natural.Update(new[] { fe });
                void Mega() { mega.Update(flash.StartTimeSeconds - 0.5, bus.Observer!.Value); mega.Update(flash.StartTimeSeconds + 0.1, bus.Observer!.Value); }
                if (naturalFirst) { Natural(); Mega(); } else { Mega(); Natural(); }
                int ripples = Live(bus).Count(r => r.SourceId == id);
                Check(ripples == 1, $"the same EventId via the natural layer and megastorm UpperDischarge gives {ripples} ripples (natural first: {naturalFirst})");
                Check(bus.Duplicates >= 1, $"the second path was deduped (natural first: {naturalFirst})");
            }
        }
    }

    // ---- storm contact ----
    private static RingVolumetricCloudLayer.RenderWeatherSystem System(int id, RingVolumetricCloudLayer.WeatherSystemKind kind, double around, double across, float major, float top, float floor = 500.0f)
        => new(id, kind, around, across, major, major * 0.6f, 1.0f, 0.0f, 1.0f, 0.9f, floor, top, CloudFormationProfile.Cumulonimbus, RingVolumetricCloudLayer.RenderWeatherFlagStatic, 0.0f, 1.0f);

    private void StormContact()
    {
        ContainmentFieldProfile p = Prof(10_000.0);
        var systems = new List<RingVolumetricCloudLayer.RenderWeatherSystem>();
        int Collect(RingCanonicalPosition c, double r, double t, List<RingVolumetricCloudLayer.RenderWeatherSystem> o) { o.AddRange(systems); return systems.Count; }
        List<ContainmentDisturbance> Run(double from, double to, double dt)
        {
            var rec = new Rec();
            var adapter = new StormContactAdapter(rec, p, Collect, () => Vector2.Zero, Circumference);
            for (double t = from; t <= to + 1e-9; t += dt) adapter.Update(t, Observer);
            return rec.Items;
        }
        // A tall storm pressing the left rim; a storm whose top is far under the field; a megastorm record; the far side.
        systems.Add(System(11, RingVolumetricCloudLayer.WeatherSystemKind.MajorFront, 1_000_000, 12_000, 40_000.0f, 17_500.0f));
        systems.Add(System(12, RingVolumetricCloudLayer.WeatherSystemKind.MajorFront, 1_100_000, 150_000, 40_000.0f, 17_500.0f));   // centre: field 40 km up
        systems.Add(System(13, RingVolumetricCloudLayer.WeatherSystemKind.MajorFront, 1_200_000, 12_000, 40_000.0f, 8_000.0f));      // top under the field
        systems.Add(System(-30001, RingVolumetricCloudLayer.WeatherSystemKind.Megastorm, 1_000_000, 8_000, 40_000.0f, 17_500.0f));    // the megastorm wall
        systems.Add(System(14, RingVolumetricCloudLayer.WeatherSystemKind.Megastorm, 1_300_000, 8_000, 40_000.0f, 17_500.0f));       // any kind-6 record
        systems.Add(System(15, RingVolumetricCloudLayer.WeatherSystemKind.MajorFront, 1_050_000, W - 9000.0, 40_000.0f, 18_000.0f)); // the right rim
        List<ContainmentDisturbance> a = Run(100.0, 160.0, 1.0 / 30.0), b = Run(100.0, 160.0, 1.0 / 30.0);
        Check(a.Count > 30, $"storm contact published only {a.Count} ripples in 60 s");
        Check(a.Select(x => x.SourceId).SequenceEqual(b.Select(x => x.SourceId)), "storm contact ids are deterministic");
        Check(a.All(x => x.Kind == ContainmentDisturbanceKind.StormContact && x.FlashCoupling == 0.0f && x.FlashLevel == null && x.DurationSeconds == 3.0f), "storm contact is refraction only (no flash)");
        // Kind-6 skip, centre and low storms never publish.
        Check(a.All(x => Math.Abs(x.OccluderTopMeters - 17_500.0f) < 1e-3 || Math.Abs(x.OccluderTopMeters - 18_000.0f) < 1e-3), "only the two rim storms publish");
        Check(a.All(x => x.Position.AcrossMeters < 40_000.0 || x.Position.AcrossMeters > W - 40_000.0), "contacts are at the rims");
        Check(a.All(x => Math.Abs(x.Position.AroundMeters - 1_000_000) < 30_000 || Math.Abs(x.Position.AroundMeters - 1_050_000) < 30_000), "no ripple for the megastorm records, the low storm or the centre storm");
        Check(a.Any(x => x.Position.AcrossMeters < 40_000.0) && a.Any(x => x.Position.AcrossMeters > W - 40_000.0), "both rims");
        foreach (ContainmentDisturbance x in a)
        {
            double h = ContainmentFieldGeometry.HeightAt(p, x.Position.AcrossMeters);
            Check(Math.Abs(x.Position.VerticalMeters - h) < 1e-6 && h <= x.OccluderTopMeters + StormContactAdapter.PressMeters + 1.0, "the contact is on the field, within 500 m of the top");
            Check(x.Energy >= StormContactAdapter.MinEnergy && x.Energy <= 1.0f && x.RadiusMeters >= 4000.0f && x.RadiusMeters <= 10_000.0f, "contact payload");
        }
        // Cadence 0.7-1.5 s per system and side.
        foreach (var group in a.GroupBy(x => (x.OccluderTopMeters, x.Position.AcrossMeters > 0.5 * W)))
        {
            List<double> times = group.Select(x => x.StartTimeSeconds).OrderBy(x => x).ToList();
            for (int i = 1; i < times.Count; i++)
                Check(times[i] - times[i - 1] >= 0.7 - 1e-9 && times[i] - times[i - 1] <= 1.5 + 1e-9, $"contact cadence {times[i] - times[i - 1]:0.000} s outside 0.7-1.5 s");
        }
        // A paused clock publishes nothing new, and a rewind re-arms without publishing.
        var rec2 = new Rec();
        var adapter2 = new StormContactAdapter(rec2, p, Collect, () => Vector2.Zero, Circumference);
        for (double t = 100.0; t <= 110.0; t += 0.1) adapter2.Update(t, Observer);
        int count = rec2.Items.Count;
        for (int i = 0; i < 100; i++) adapter2.Update(110.0, Observer);
        Check(rec2.Items.Count == count, "a paused clock publishes no new storm contact");
        Check(adapter2.Update(50.0, Observer) == 0, "a backwards jump publishes nothing");
        // E scales with penetration.
        systems.Clear();
        systems.Add(System(21, RingVolumetricCloudLayer.WeatherSystemKind.MajorFront, 1_000_000, 12_000, 40_000.0f, 12_000.0f));
        float eLow = Run(100.0, 130.0, 0.1).Max(x => x.Energy);
        systems.Clear();
        systems.Add(System(21, RingVolumetricCloudLayer.WeatherSystemKind.MajorFront, 1_000_000, 12_000, 40_000.0f, 17_900.0f));
        float eHigh = Run(100.0, 130.0, 0.1).Max(x => x.Energy);
        Check(eHigh > eLow && eHigh <= 1.0f && eLow >= StormContactAdapter.MinEnergy, $"energy scales with penetration ({eLow:0.00} < {eHigh:0.00})");
        Info($"storm contact: {a.Count} ripples in 60 s from 2 pressing storms");
    }

    // ---- provider ----
    private void Provider()
    {
        foreach (double wallTop in new[] { 10_000.0, 10_400.0 })
        {
            ContainmentFieldProfile p = Prof(wallTop);
            ContainmentFieldProvider.Install(p);
            Check(MegastormFieldCoupling.FieldUndersideProvider != null && ContainmentFieldProvider.IsInstalled, "provider installed");
            Check(MegastormFieldCoupling.CeilingFromUnderside(15_000.0) == ContainmentFieldGeometry.CloudCeilingFromUnderside(15_000.0)
                && MegastormFieldCoupling.CeilingFromUnderside(25_000.0) == 18_000.0 && MegastormFieldCoupling.CeilingFromUnderside(20_000.0) == 18_000.0, "the real CeilingFromUnderside replaces the stub");
            int mismatches = 0;
            for (int i = 0; i < 64; i++)
            {
                double across = (i + 0.5) / 64.0 * W;
                if (MegastormFieldCoupling.CeilingAt(across) != ContainmentFieldGeometry.CloudCeilingAt(p, across)) mismatches++;
                if (ContainmentFieldProvider.CloudCeilingAt(across) != ContainmentFieldGeometry.CloudCeilingAt(p, across)) mismatches++;
                if (!(MegastormFieldCoupling.FieldUndersideProvider!(across) == ContainmentFieldGeometry.HeightAt(p, across))) mismatches++;
            }
            for (int i = 0; i <= 64; i++)   // the roots and a margin outside them
            {
                double across = p.RootAcross0 - 100.0 + (i / 64.0) * (p.RootAcrossW - p.RootAcross0 + 200.0);
                if (MegastormFieldCoupling.CeilingAt(across) != ContainmentFieldGeometry.CloudCeilingAt(p, across)) mismatches++;
            }
            Check(mismatches == 0, $"wall top {wallTop}: provider ceiling differs from CloudCeilingAt at {mismatches} samples");
            double edge = MegastormFieldCoupling.BandEdgeMeters(W);
            Check(edge > 3800.0 && edge < 4800.0, $"wall top {wallTop}: band edge {edge:0.0} m (WO1: about 4.0-4.45 km)");
            Info($"wall top {wallTop}: band edge {edge:0.0} m, 64 samples equal");
        }
        ContainmentFieldProvider.Uninstall();
        Check(MegastormFieldCoupling.FieldUndersideProvider == null && !ContainmentFieldProvider.IsInstalled && MegastormFieldCoupling.CeilingAt(5000.0) == 18_000.0, "uninstall removes the provider");
        Func<double, double> other = a => 12_345.0;
        ContainmentFieldProvider.Install(Prof(10_000.0));
        MegastormFieldCoupling.FieldUndersideProvider = other;
        ContainmentFieldProvider.Uninstall();
        Check(ReferenceEquals(MegastormFieldCoupling.FieldUndersideProvider, other), "uninstall leaves a provider that another owner replaced");
        MegastormFieldCoupling.FieldUndersideProvider = null;
    }

    // ---- field strikes ----
    private void FieldStrikes()
    {
        ContainmentFieldProfile p = Prof(10_000.0);
        // A flagged event is a FieldStrike (never Lightning) at the contact point, with no gap attenuation.
        var rec = new Rec();
        var adapter = new LightningDisturbanceAdapter(rec, p, e => new LightningStormInfo(5000.0f, 1000.0f));
        RingLightningEvent strike = Ev(900, 6, 20.0, 1_000_000, 159_000.0, 0.7f, true, 1.9f, 77, 17_000.0, 39_950.0);
        RingLightningEvent natural = Ev(901, 3, 20.0, 1_000_000, 159_000.0, 0.7f, true, 1.0f, 78, 3000.0, 39_950.0);
        RingLightningEvent plain = Ev(902, 4, 20.0, 1_000_000, 159_000.0, 0.7f, false, 0.0f, 79, 17_000.0, 0.0);
        adapter.Update(new[] { strike, natural, plain });
        Check(rec.Items.Count == 2 && rec.Items.All(x => x.Kind == ContainmentDisturbanceKind.FieldStrike), "a flagged event publishes FieldStrike, never Lightning; the plain centre event nothing");
        ContainmentDisturbance ds = rec.Items[0];
        Check(ds.Energy == 0.7f && ds.Position == strike.GroundPosition && ds.SourceId == 900 && !ds.NaturalSource, "no gap attenuation, contact point, SourceId = EventId, megastorm = not natural");
        Check(rec.Items[1].NaturalSource && rec.Items[1].Energy == 0.7f, "a natural flagged strike carries the natural tag");
        Check(Math.Abs(ds.RadiusMeters - (6000.0f + 9000.0f * 0.7f)) < 1e-3 && ds.DurationSeconds == 3.0f && ds.FlashCoupling == 1.0f, "strike ripple: Rmax 6-15 km, T 3 s, own pulse");
        // The flash follows the event's own envelope: a 1.9 s megastorm strike stays lit through its re-pulses; a 0.72 s event is unchanged.
        var bus = NewBus(20.0);
        bus.Profile = p;
        var live = new LightningDisturbanceAdapter(bus, p, e => new LightningStormInfo(17_500.0f, 1000.0f));
        RingLightningEvent ordinary = Ev(903, 3, 20.0, 1_050_000, 1500.0, 1.0f, false, 0.0f, 80);
        live.Update(new[] { strike, ordinary });
        ContainmentRipple rs = Live(bus).Single(r => r.SourceId == 900), ro = Live(bus).Single(r => r.SourceId == 903);
        float minLit = 1.0f;
        for (double t = 20.0 + RingLightningEvent.FieldStrikeRiseSeconds(1.9) + 0.06; t <= 21.45; t += 0.01)
        {
            float f = rs.FlashAt(t);
            minLit = Math.Min(minLit, f);
            Check(Math.Abs(f - strike.PulseAt(t)) < 1e-6f, "strike flash = its own envelope");
        }
        Check(minLit > 0.2f && rs.FlashAt(21.5) > 0.0f && rs.FlashAt(21.95) == 0.0f, $"a 1.9 s strike stays lit through its re-pulses (min {minLit:0.00})");
        Check(ro.FlashAt(20.3) == ordinary.PulseAt(20.3) && ro.FlashAt(20.73) == 0.0f && Math.Abs(ordinary.LifeSeconds - 0.72) < 1e-9, "a 0.72 s default is unchanged");
        // The same EventId on consecutive frames: one patch.
        for (int i = 0; i < 30; i++) live.Update(new[] { strike, ordinary });
        Check(Live(bus).Count(r => r.SourceId == 900) == 1 && bus.LiveCount == 2, "the same EventId seen on consecutive frames gives one patch");

        // Caps: 2/s for FieldStrike, natural at most 1/s.
        bus = NewBus(0.0);
        int ok = 0;
        for (int i = 0; i < 5; i++)
            if (bus.Publish(D(300 + i, ContainmentDisturbanceKind.FieldStrike, 1_000_000 + (i - 2) * 60_000, 80_000, i * 0.2, 0.8f, 6000, 3.0f))) ok++;
        Check(ok == 2 && bus.RateLimited == 3, $"FieldStrike 2/s: {ok} accepted");
        bus = NewBus(0.0);
        ok = 0;
        for (int i = 0; i < 4; i++)
            if (bus.Publish(D(400 + i, ContainmentDisturbanceKind.FieldStrike, 1_000_000 + (i - 2) * 60_000, 80_000, i * 0.2, 0.8f, 6000, 3.0f, natural: true))) ok++;
        Check(ok == 1, $"natural FieldStrike 1/s: {ok} accepted");
        Check(bus.Publish(D(410, ContainmentDisturbanceKind.FieldStrike, 1_300_000, 80_000, 0.5, 0.8f, 6000, 3.0f)), "a megastorm strike still takes the second slot of the second");

        // Restrike merge: inside the crawl radius of a live strike the patch is re-triggered, no new slot.
        bus = NewBus(0.0);
        Func<double, float> pulse1 = t => t >= 0.0 && t < 0.6 ? 0.8f : 0.0f, pulse2 = t => t >= 0.6 && t < 1.2 ? 0.9f : 0.0f;
        Check(bus.TryPublish(D(500, ContainmentDisturbanceKind.FieldStrike, 1_000_000, 80_000, 0.0, 0.7f, 6000, 3.0f, 1.0f, pulse1)) == ContainmentPublishResult.Accepted, "restrike: first");
        ContainmentRipple patch = Live(bus)[0];
        int seed0 = patch.CrawlSeed;
        float rc = patch.CrawlRadiusMeters;
        bus.Advance(0.5);
        Check(bus.TryPublish(D(501, ContainmentDisturbanceKind.FieldStrike, 1_003_000, 80_000, 0.6, 0.9f, 6000, 3.0f, 1.0f, pulse2)) == ContainmentPublishResult.Restruck, "restrike: 3 km away re-triggers the live patch");
        Check(bus.LiveCount == 1 && patch.Energy == 0.9f && patch.CrawlSeed != seed0 && patch.Restrikes == 1 && patch.StartTimeSeconds == 0.6, "restrike: no new slot, energy max, new crawl seed, envelope restarted");
        Check(patch.FlashAt(0.3) == 0.8f && patch.FlashAt(0.9) == 0.9f, "restrike: the flash is the max of the two pulses");
        Check(patch.CrawlRadiusMeters > rc, "restrike: the crawl radius follows the higher energy");
        bus.Advance(0.7);
        Check(bus.TryPublish(D(502, ContainmentDisturbanceKind.FieldStrike, 1_000_000 + 30_000, 80_000, 1.5, 0.9f, 6000, 3.0f)) == ContainmentPublishResult.Accepted && bus.LiveCount == 2, "a strike outside the crawl radius is a new patch");

        // Never evict a lit patch for a dimmer one.
        bus = NewBus(2.0);
        Func<double, float> lit = t => 1.0f;
        for (int i = 0; i < 4; i++) bus.Publish(D(600 + i, ContainmentDisturbanceKind.FieldStrike, 1_000_000 + (i - 2) * 90_000 + 40_000, 60_000 + i * 50_000, -5.9 + i * 0.5, 0.6f, 3000, 8.0f, 1.0f, lit));
        for (int i = 0; i < 2; i++) bus.Publish(D(610 + i, ContainmentDisturbanceKind.BodyContact, 1_000_000 + (i - 1) * 120_000, 100_000 + i * 20_000, -3.5 + i * 0.01, 0.6f, 3000, 8.0f, 1.0f, lit));
        for (int i = 0; i < 10; i++) bus.Publish(D(620 + i, ContainmentDisturbanceKind.Impact, 1_000_000 + (i - 5) * 30_000 + (i % 2) * 4000, 70_000 + (i % 3) * 20_000, -1.8 + 0.18 * i, 0.3f + 0.05f * i, 1000, 6.0f + (i % 3), 1.0f, lit));
        Check(bus.LiveCount == 16, "lit pool full");
        string full = Signature(bus);
        Check(bus.TryPublish(D(700, ContainmentDisturbanceKind.Impact, 1_000_000, 90_000, 2.0, 0.95f, 1000, 5.0f)) == ContainmentPublishResult.NoBudget && Signature(bus) == full, "a lit pool is not evicted for a plain newcomer");
        Check(bus.TryPublish(D(701, ContainmentDisturbanceKind.FieldStrike, 1_100_000, 90_000, 2.0, 0.1f, 1000, 3.0f, 1.0f, lit)) == ContainmentPublishResult.NoBudget && Signature(bus) == full, "a dimmer strike does not evict a lit patch");
        List<ContainmentRipple> before = Live(bus);
        ContainmentRipple victim = before.Where(r => r.Kind != ContainmentDisturbanceKind.BodyContact && r.Energy < 0.95f)
            .OrderBy(r => r.Energy * Math.Clamp(1.0 - Math.Max(0.0, 2.0 - r.StartTimeSeconds) / r.DurationSeconds, 0.0, 1.0)
                / (1.0 + Math.Sqrt(Math.Pow(WrapSigned(r.Surface.AroundMeters - Observer.AroundMeters, Circumference), 2) + Math.Pow(r.Surface.AcrossMeters - Observer.AcrossMeters, 2)) / 100_000.0)).First();
        Check(bus.TryPublish(D(702, ContainmentDisturbanceKind.FieldStrike, 1_100_000, 90_000, 2.0, 0.95f, 1000, 3.0f, 1.0f, lit)) == ContainmentPublishResult.Accepted, "a brighter strike takes a lit patch");
        long gone = before.Select(r => r.SourceId).Except(Live(bus).Select(r => r.SourceId)).Single();
        Check(gone == victim.SourceId && gone != 610 && gone != 611, $"the strike evicted {gone}, expected {victim.SourceId} (never a reserved body patch)");
    }

    // ---- megastorm: fixture, bursts, contact, cadence ----
    private sealed record FixtureData(double Circumference, double Width, double WeatherTime, double Window, double CameraXi, double CameraAcross, string TriggerId, double Leading, int Direction,
        double TriggerTime, float Speed, float Formation, int Seed, List<(string Name, double WallTop, double Apex, double MaxRim, double Band, List<string> ContactIds)> Profiles, List<string> SpriteIds);

    private static FixtureData ReadFixture()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(FixturePath)));
        JsonElement r = doc.RootElement, m = r.GetProperty("model");
        var profiles = new List<(string, double, double, double, double, List<string>)>();
        foreach (JsonElement pe in r.GetProperty("profiles").EnumerateArray())
            profiles.Add((pe.GetProperty("name").GetString()!, pe.GetProperty("wallTopMeters").GetDouble(), pe.GetProperty("apexMeters").GetDouble(), pe.GetProperty("maxContactRimMeters").GetDouble(),
                pe.GetProperty("expected").GetProperty("bandEdgeMeters").GetDouble(), pe.GetProperty("expected").GetProperty("contactSourceIds").EnumerateArray().Select(x => x.GetString()!).ToList()));
        return new FixtureData(r.GetProperty("circumferenceMeters").GetDouble(), r.GetProperty("widthMeters").GetDouble(), r.GetProperty("weatherTimeSeconds").GetDouble(), r.GetProperty("windowSeconds").GetDouble(),
            r.GetProperty("cameraXiMeters").GetDouble(), r.GetProperty("cameraAcrossMeters").GetDouble(), m.GetProperty("triggerId").GetString()!, m.GetProperty("leadingAroundMeters").GetDouble(),
            m.GetProperty("direction").GetInt32(), m.GetProperty("triggerTimeSeconds").GetDouble(), (float)m.GetProperty("speedMetersPerSecond").GetDouble(), (float)m.GetProperty("formationSeconds").GetDouble(),
            m.GetProperty("seed").GetInt32(), profiles, r.GetProperty("spriteEventIds").EnumerateArray().Select(x => x.GetString()!).ToList());
    }

    private static MegastormModel MakeModel(FixtureData f, float? speed = null)
    {
        var model = new MegastormModel(new MegastormSettings { CircumferenceMeters = f.Circumference, WidthMeters = f.Width });
        model.Trigger(new MegastormTrigger(f.TriggerId, f.Leading, f.Direction, f.TriggerTime, speed ?? f.Speed, f.Formation, f.Seed));
        return model;
    }

    private static MegastormModel MakeFixtureModel(out FixtureData f) { f = ReadFixture(); return MakeModel(f); }

    private static RingCanonicalPosition CameraAt(MegastormModel model, FixtureData f, double t)
        => new(MegastormModel.WrapPositive(model.AroundAt(t) - model.State.Direction * f.CameraXi, f.Circumference), 0.0, f.CameraAcross);

    private static Func<double, double> StubProvider(double wallTop, double apex, double width)
    {
        double a = 0.5 * width, b = apex - wallTop;
        return across => { if (across < 0.0 || across > width) return double.NaN; double x = (across - a) / a; return wallTop + b * Math.Sqrt(Math.Max(0.0, 1.0 - x * x)); };
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static List<ContainmentDisturbance> RunAdapter(MegastormModel model, FixtureData f, ContainmentFieldProfile p, double t0, double seconds, double dt, bool follow, IContainmentDisturbanceSink? sink = null,
        RingCanonicalPosition? fixedCamera = null)
    {
        var rec = new Rec();
        RingCanonicalPosition camera = fixedCamera ?? CameraAt(model, f, t0);
        int steps = (int)Math.Round(seconds / dt);
        var all = new List<ContainmentDisturbance>();
        var adapter = new MegastormDisturbanceAdapter(() => model, new Tee(sink ?? rec, all), p) { SpriteChanceScale = 1.0f };
        for (int i = 0; i <= steps; i++)
        {
            double t = t0 + i * dt;
            if (follow) camera = CameraAt(model, f, t);
            adapter.Update(t, camera);
        }
        return all;
    }

    /// <summary>Forwards to a sink and records what it accepted.</summary>
    private sealed class Tee : IContainmentDisturbanceSink
    {
        private readonly IContainmentDisturbanceSink _inner; private readonly List<ContainmentDisturbance> _accepted;
        public Tee(IContainmentDisturbanceSink inner, List<ContainmentDisturbance> accepted) { _inner = inner; _accepted = accepted; }
        public bool Publish(in ContainmentDisturbance d) { bool ok = _inner.Publish(d); if (ok) _accepted.Add(d); return ok; }
    }

    private void Megastorm()
    {
        FixtureData f = ReadFixture();
        MegastormModel model = MakeModel(f);
        double t0 = f.WeatherTime, c = f.Circumference, w = f.Width;

        // ---- 1. the shared fixture: the adapter publishes the same contact ids as MegastormFieldEventsTest, per profile ----
        foreach (var fp in f.Profiles)
        {
            MegastormFieldCoupling.FieldUndersideProvider = StubProvider(fp.WallTop, fp.Apex, w);
            ContainmentFieldProfile profile = Prof(fp.WallTop);
            List<ContainmentDisturbance> a = RunAdapter(model, f, profile, t0, f.Window, 0.25, false), b = RunAdapter(model, f, profile, t0, f.Window, 0.25, false);
            var contact = a.Where(d => d.Kind == ContainmentDisturbanceKind.MegastormContact).ToList();
            Check(contact.Select(d => Id(d.SourceId)).SequenceEqual(fp.ContactIds) && contact.Count == fp.ContactIds.Count && contact.Count > 0,
                $"{fp.Name}: adapter contact ids differ from the shared fixture ({contact.Count} vs {fp.ContactIds.Count})");
            Check(a.Select(d => d.SourceId).SequenceEqual(b.Select(d => d.SourceId)), $"{fp.Name}: adapter is not deterministic");
            // Through the real bus the same ids give the same ripples.
            var bus = NewBus(t0);
            bus.Profile = profile; bus.Observer = CameraAt(model, f, t0);
            var passed = new List<ContainmentDisturbance>();
            var adapter = new MegastormDisturbanceAdapter(() => model, new Tee(bus, passed), profile);
            for (int i = 0; i <= (int)(f.Window / 0.25); i++) { bus.Advance(t0 + i * 0.25); adapter.Update(t0 + i * 0.25, bus.Observer!.Value); }
            // The adapter makes exactly the fixture's ids; the bus then applies its own budget (contact at most 6 live, lit patches are never evicted), so what it accepts is an ordered subset.
            List<string> got = passed.Where(d => d.Kind == ContainmentDisturbanceKind.MegastormContact).Select(d => Id(d.SourceId)).ToList();
            int cursor = 0;
            foreach (string id in fp.ContactIds) if (cursor < got.Count && got[cursor] == id) cursor++;
            Check(cursor == got.Count && got.Count >= 20 && bus.OutOfRangeRejected == 0 && bus.Merged == 0, $"{fp.Name}: bus accepted {passed.Count(d => d.Kind == ContainmentDisturbanceKind.MegastormContact)} of {fp.ContactIds.Count} fixture contacts (merged {bus.Merged}, rate limited {bus.RateLimited}, no budget {bus.NoBudget}, out of range {bus.OutOfRangeRejected}, evicted {bus.Evicted})");
            Info($"{fp.Name}: bus accepted {got.Count} of {fp.ContactIds.Count} fixture contacts (contact live cap 6: no budget {bus.NoBudget}, evicted {bus.Evicted})");
            Check(Live(bus).All(r => r.Kind == ContainmentDisturbanceKind.MegastormContact || r.Kind == ContainmentDisturbanceKind.UpdraftBurst || r.Kind == ContainmentDisturbanceKind.Lightning
                || r.Kind == ContainmentDisturbanceKind.UpperAtmosphere), "megastorm adapter publishes contact, bursts, lightning and discharges only");
            Info($"{fp.Name}: {contact.Count} contact ids match the shared fixture");
        }
        // The fixture's sprite ids: the adapter derives through the same path.
        {
            var sprites = new List<UpperAtmosphereSprite>();
            MegastormFieldCoupling.SpritesInWindow(model, t0, t0 + 600.0, sprites, 1.0f);
            Check(sprites.Where(s => s.StartTimeSeconds < t0 + 60.0).Take(8).Select(s => Id(s.EventId)).SequenceEqual(f.SpriteIds), "fixture sprite ids");
            MegastormFieldCoupling.FieldUndersideProvider = StubProvider(10_000.0, 40_000.0, w);
            ContainmentFieldProfile profile = Prof(10_000.0);
            List<ContainmentDisturbance> got = RunAdapter(model, f, profile, t0, 120.0, 0.25, true);
            var upper = got.Where(d => d.Kind == ContainmentDisturbanceKind.UpperAtmosphere).ToList();
            var ids = new HashSet<long>(sprites.Select(s => s.EventId));
            Check(upper.Count > 0 && upper.All(d => ids.Contains(d.SourceId ^ UpperAtmosphereDisturbanceAdapter.SourceSalt)), "published discharges are the megastorm's sprites, shared ids");
            Check(got.Where(d => d.Kind == ContainmentDisturbanceKind.Lightning).All(d => !MegastormFieldStrikes.IsFieldStrikeId(d.SourceId)), "no field-strike id leaks through the megastorm adapter");
            Info($"megastorm adapter 120 s: {got.Count(d => d.Kind == ContainmentDisturbanceKind.MegastormContact)} contact, {got.Count(d => d.Kind == ContainmentDisturbanceKind.UpdraftBurst)} burst pulses, "
                + $"{upper.Count} discharges, {got.Count(d => d.Kind == ContainmentDisturbanceKind.Lightning)} lightning");
        }

        // ---- real provider from here ----
        ContainmentFieldProfile real = Prof(10_000.0);
        ContainmentFieldProvider.Install(real);
        double band = MegastormFieldCoupling.BandEdgeMeters(w);

        // ---- 2. a 2 h sweep: cap 4/s, band, bursts re-pulse and drift, centre bursts silent, contact only while active ----
        var sweepRec = new Rec();
        List<ContainmentDisturbance> sweep = RunAdapter(model, f, real, t0, 7200.0, 0.25, true, sweepRec);
        var perSecond = new Dictionary<long, int>();
        foreach (ContainmentDisturbance d in sweep.Where(x => x.Kind is ContainmentDisturbanceKind.MegastormContact or ContainmentDisturbanceKind.UpdraftBurst))
        { long s = (long)Math.Floor(d.StartTimeSeconds); perSecond[s] = perSecond.GetValueOrDefault(s) + 1; }
        int maxPerSecond = perSecond.Values.DefaultIfEmpty(0).Max();
        var contactSweep = sweep.Where(d => d.Kind == ContainmentDisturbanceKind.MegastormContact).ToList();
        var burstSweep = sweep.Where(d => d.Kind == ContainmentDisturbanceKind.UpdraftBurst).ToList();
        Check(maxPerSecond <= 4, $"megastorm ripples in one second: {maxPerSecond} (cap 4)");
        Check(contactSweep.Count > 1000 && burstSweep.Count > 20, $"sweep coverage: {contactSweep.Count} contact, {burstSweep.Count} burst pulses");
        foreach (ContainmentDisturbance d in contactSweep)
        {
            double rim = Math.Min(d.Position.AcrossMeters, w - d.Position.AcrossMeters);
            double xi = model.Xi(d.Position.AroundMeters, d.Position.AcrossMeters, d.StartTimeSeconds);
            if (rim > band + 15.0 || xi < -1.0 || xi > MegastormModel.WallEndXi + 1.0 || Math.Abs(d.Position.VerticalMeters - MegastormFieldCoupling.CeilingAt(d.Position.AcrossMeters)) > 1e-6)
            { Check(false, $"contact at rim {rim:0.0} m (band {band:0.0}), xi {xi:0.0}, vertical {d.Position.VerticalMeters:0.0}"); break; }
        }
        Check(contactSweep.Any(d => d.Position.AcrossMeters < 0.5 * w) && contactSweep.Any(d => d.Position.AcrossMeters > 0.5 * w), "contact on both walls");
        // Bursts: re-pulse every 1.5 s while alive, drifting at the record's DriftMetersPerSecond.
        var bundle = new List<MegastormRecord>();
        var last = new Dictionary<double, ContainmentDisturbance>();
        int pairs = 0;
        foreach (ContainmentDisturbance d in burstSweep)
        {
            if (last.TryGetValue(d.Position.AcrossMeters, out ContainmentDisturbance prev) && Math.Abs(d.StartTimeSeconds - prev.StartTimeSeconds - 1.5) < 1e-9)
            {
                model.BuildBundle(d.StartTimeSeconds, d.Position.AcrossMeters, bundle);
                double drift = bundle.First(r => r.Role == MegastormRecordRole.Wall).DriftMetersPerSecond;
                double advance = MegastormModel.WrapSigned(d.Position.AroundMeters - prev.Position.AroundMeters, c);
                if (Math.Abs(advance - drift * 1.5) > 1e-3) { Check(false, $"burst pulse advanced {advance:0.000} m, drift x 1.5 s = {drift * 1.5:0.000}"); break; }
                pairs++;
            }
            last[d.Position.AcrossMeters] = d;
            Check(d.Energy >= MegastormFieldPublisher.MinBurstEnergy && d.DurationSeconds == 5.0f, "burst payload");
        }
        Check(pairs >= 20, $"only {pairs} consecutive burst pulse pairs");
        // A centre-habitat burst (gap about 22 km) gives no ripple.
        var live = new List<MegastormFieldCoupling.UpdraftBurst>();
        var centreBursts = new HashSet<double>();
        for (double t = t0; t < t0 + 7200.0; t += 30.0)
        {
            MegastormFieldCoupling.BurstsAt(model, t, live);
            foreach (var bu in live) if (Math.Abs(bu.AcrossMeters - 0.5 * w) < 40_000.0) centreBursts.Add(bu.AcrossMeters);
        }
        Check(centreBursts.Count >= 3, $"only {centreBursts.Count} centre bursts in the sweep (test would be vacuous)");
        Check(burstSweep.All(d => !centreBursts.Contains(d.Position.AcrossMeters)), "a centre-habitat burst published a ripple");
        double maxBurstAcross = burstSweep.Max(d => Math.Min(d.Position.AcrossMeters, w - d.Position.AcrossMeters));
        Check(maxBurstAcross < 13_000.0, $"bursts couple only near the rims (farthest {maxBurstAcross:0} m)");
        Info($"2 h sweep: {contactSweep.Count} contact, {burstSweep.Count} burst pulses ({pairs} consecutive pairs), max {maxPerSecond}/s, band {band:0.0} m, {centreBursts.Count} centre bursts silent");

        // Contact publishes only while Active.
        var late = new MegastormModel(new MegastormSettings { CircumferenceMeters = c, WidthMeters = w });
        late.Trigger(new MegastormTrigger("late", f.Leading, 1, 6000.0, 350f, 1800f, 7));
        var before = new Rec();
        var lateAdapter = new MegastormDisturbanceAdapter(() => late, before, real);
        RingCanonicalPosition cam = CameraAt(late, f, 6000.0);
        for (double t = 100.0; t < 130.0; t += 0.25) lateAdapter.Update(t, cam);
        Check(before.Items.Count == 0, "contact published before the storm is active");
        var untriggered = new MegastormModel(new MegastormSettings { CircumferenceMeters = c, WidthMeters = w });
        var none = new Rec();
        var nadapter = new MegastormDisturbanceAdapter(() => untriggered, none, real);
        for (double t = t0; t < t0 + 30.0; t += 0.25) nadapter.Update(t, cam);
        Check(none.Items.Count == 0, "published for an untriggered storm");
        // Paused clock and rewind.
        var paused = new Rec();
        var pa = new MegastormDisturbanceAdapter(() => model, paused, real);
        pa.Update(t0, CameraAt(model, f, t0)); pa.Update(t0 + 0.25, CameraAt(model, f, t0));
        int n0 = paused.Items.Count;
        for (int i = 0; i < 50; i++) pa.Update(t0 + 0.25, CameraAt(model, f, t0));
        Check(paused.Items.Count == n0, "a paused clock published again");
        Check(pa.Update(t0 - 100.0, CameraAt(model, f, t0)) == 0, "a backwards jump published");
        // Without a provider: nothing. Wall beyond 400 km: nothing reaches the bus.
        MegastormFieldCoupling.FieldUndersideProvider = null;
        Check(RunAdapter(model, f, real, t0, 30.0, 0.25, true).Count == 0, "published with a null provider");
        ContainmentFieldProvider.Install(real);
        var far = new RingCanonicalPosition(MegastormModel.WrapPositive(model.AroundAt(t0) + 450_000.0, c), 0.0, 0.5 * w);
        var farBus = NewBus(t0);
        farBus.Observer = far;
        RunAdapter(model, f, real, t0, 30.0, 0.25, false, farBus, far);
        Check(farBus.Accepted == 0, "the bus accepted ripples with the wall 450 km away");

        // ---- 3. cadence: identical at 25 and 350 m/s ----
        foreach (bool follow in new[] { true, false })
        {
            var slots = new Dictionary<float, HashSet<long>>();
            foreach (float speed in new[] { 25.0f, 350.0f })
            {
                MegastormModel m = MakeModel(f, speed);
                var set = new HashSet<long>();
                foreach (double start in new[] { t0, t0 + 3000.0 })
                {
                    RingCanonicalPosition? fixedCam = follow ? null : new RingCanonicalPosition(MegastormModel.WrapPositive(m.AroundAt(start) - 12_000.0, c), 0.0, f.CameraAcross);
                    foreach (ContainmentDisturbance d in RunAdapter(m, f, real, start, 40.0, 0.25, follow, null, fixedCam).Where(x => x.Kind == ContainmentDisturbanceKind.MegastormContact))
                        set.Add((long)Math.Round(d.StartTimeSeconds / MegastormFieldPublisher.SlotSeconds));
                }
                slots[speed] = set;
            }
            Check(slots[25.0f].Count > 40 && slots[25.0f].SetEquals(slots[350.0f]), $"contact slot cadence differs between 25 and 350 m/s (follow={follow}): {slots[25.0f].Count} vs {slots[350.0f].Count} slots");
            Info($"cadence follow={follow}: {slots[25.0f].Count} contact slots, identical at 25 and 350 m/s = {slots[25.0f].SetEquals(slots[350.0f])}");
        }
        ContainmentFieldProvider.Uninstall();
    }

    // ---- a scripted busy-megastorm minute ----
    private void BusyMinute()
    {
        FixtureData f = ReadFixture();
        MegastormModel model = MakeModel(f);
        ContainmentFieldProfile real = Prof(10_000.0);
        ContainmentFieldProvider.Install(real);
        double t0 = f.WeatherTime, dt = 1.0 / 30.0;
        var strikes = new List<MegastormFieldStrike>();
        MegastormFieldStrikes.FieldStrikesInWindow(model, t0 - 3.0, t0 + 61.0, 2.0f, strikes);   // frequency 2: a busy wall
        var strikeEvents = strikes.Select(s => MegastormFieldStrikes.ToEvent(s)).ToList();
        // Natural near-rim lightning and discharges, plus a storm pressing the rim.
        var natural = new List<RingLightningEvent>();
        for (int i = 0; i < 40; i++)
        {
            double u = UpperAtmosphereLightning.Unit(i + 1, 77), v = UpperAtmosphereLightning.Unit(i + 1, 78);
            natural.Add(Ev(70_000 + i, 4, t0 + i * 1.5, CameraAt(model, f, t0).AroundMeters + (u - 0.5) * 200_000.0, v < 0.5 ? 1500.0 + 4000.0 * u : W - 1500.0 - 4000.0 * u, 0.7f + 0.3f * (float)v, false, 0.0f, i * 13,
                cloudVertical: 17_000.0));
        }
        var bus = NewBus(t0);
        bus.Profile = real;
        var systems = new List<RingVolumetricCloudLayer.RenderWeatherSystem>
        {
            System(31, RingVolumetricCloudLayer.WeatherSystemKind.MajorFront, CameraAt(model, f, t0).AroundMeters, 12_000, 40_000.0f, 17_500.0f),
            System(32, RingVolumetricCloudLayer.WeatherSystemKind.MajorFront, CameraAt(model, f, t0).AroundMeters + 60_000, W - 12_000, 40_000.0f, 17_800.0f),
        };
        var lightning = new LightningDisturbanceAdapter(bus, real, e => new LightningStormInfo(17_500.0f, 20_000.0f));
        var upper = new UpperAtmosphereDisturbanceAdapter(bus, real, () => 17_000.0f, () => 1.0f);
        var contact = new StormContactAdapter(bus, real, (c, r, t, o) => { o.AddRange(systems); return systems.Count; }, () => Vector2.Zero, f.Circumference);
        var megastorm = new MegastormDisturbanceAdapter(() => model, bus, real) { SpriteChanceScale = 1.0f };
        var active = new List<RingLightningEvent>();
        int maxLive = 0, maxContact = 0, maxBurst = 0, maxShared = 0, frames = (int)(60.0 / dt);
        var seenStrikes = new HashSet<long>();
        long totalTicks = 0;
        for (int i = 0; i <= frames; i++)
        {
            double t = t0 + i * dt;
            RingCanonicalPosition cam = CameraAt(model, f, t);
            bus.Observer = cam;
            long tick0 = Stopwatch.GetTimestamp();
            bus.Advance(t);
            active.Clear();
            foreach (RingLightningEvent e in strikeEvents) if (e.StartTimeSeconds <= t && t - e.StartTimeSeconds <= e.LifeSeconds) { active.Add(e); seenStrikes.Add(e.EventId); }
            foreach (RingLightningEvent e in natural) if (e.StartTimeSeconds <= t && t - e.StartTimeSeconds <= e.LifeSeconds) active.Add(e);
            lightning.Update(active);
            upper.Update(active);
            contact.Update(t, cam);
            megastorm.Update(t, cam);
            totalTicks += Stopwatch.GetTimestamp() - tick0;
            maxLive = Math.Max(maxLive, bus.LiveCount);
            maxContact = Math.Max(maxContact, bus.LiveOf(ContainmentDisturbanceKind.MegastormContact));
            maxBurst = Math.Max(maxBurst, bus.LiveOf(ContainmentDisturbanceKind.UpdraftBurst));
            maxShared = Math.Max(maxShared, bus.LiveCount - bus.LiveOf(ContainmentDisturbanceKind.FieldStrike) - bus.LiveOf(ContainmentDisturbanceKind.BodyContact));
        }
        Check(maxLive <= 16 && bus.PeakLive <= 16, $"busy minute: peak live {bus.PeakLive}");
        Check(maxContact <= 6 && maxBurst <= 3 && maxShared <= 10, $"busy minute: contact {maxContact}, bursts {maxBurst}, shared {maxShared}");
        Check(seenStrikes.Count >= 15, $"busy minute: only {seenStrikes.Count} strikes (test would be vacuous)");
        int strikesRendered = lightning.StrikesPublished;
        Check(strikesRendered == seenStrikes.Count && bus.FarListed == 0, $"busy minute: {strikesRendered} of {seenStrikes.Count} strikes were rendered (far list {bus.FarListed}, rate limited {bus.RateLimited}, no budget {bus.NoBudget})");
        Check(bus.Accepted + bus.Merged + bus.Restruck > 100, "busy minute: the pool was busy");
        Info($"busy minute CPU: {totalTicks * 1000.0 / Stopwatch.Frequency / (frames + 1):0.0000} ms per frame for the bus and all four adapters (headless, includes the megastorm window queries)");
        Info($"busy minute (strike frequency 2): peak live {bus.PeakLive}, strikes {seenStrikes.Count} all rendered (restruck {bus.Restruck}), accepted {bus.Accepted}, merged {bus.Merged}, evicted {bus.Evicted}, "
            + $"rate limited {bus.RateLimited}, max contact {maxContact}, bursts {maxBurst}");
        ContainmentFieldProvider.Uninstall();
    }

    // ---- staged wiring check in the integration scene ----
    private int _stage, _wait = 4, _ticks;
    private FixtureData? _fixture;
    private double _clock;
    private readonly HashSet<long> _arcIds = new();
    private readonly HashSet<long> _strikeIds = new();

    /// <summary>
    /// Called once per frame from the wrapper with the integration scene root: the host node exists, the real provider and ceiling are installed, and with a megastorm
    /// triggered the bus is fed from the real layer (contact ripples, field strikes) while contact arcs publish nothing. Returns -1 to continue, 0 pass, 1 fail.
    /// </summary>
    public int Tick(Node scene)
    {
        if (_wait > 0) { _wait--; return -1; }
        var layer = scene.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds");
        var megastorm = scene.GetNode<RingMegastorm>("RingMegastorm");
        try
        {
            RingContainmentDisturbanceHost? host = RingContainmentDisturbanceHost.Instance;
            if (_stage == 0)
            {
                _stage = 1;
                Check(host != null && host.GetParent() == scene, "wiring: the host node is not in the scene");
                Check(host?.Bus != null && ContainmentFieldProvider.IsInstalled && MegastormFieldCoupling.FieldUndersideProvider != null, "wiring: the real field provider is not installed");
                if (host?.Profile is not ContainmentFieldProfile p) { Check(false, "wiring: no profile"); return Finish(); }
                Check(p.WidthMeters == W && p.WallTopMeters == 10_400.0, $"wiring: profile width {p.WidthMeters} wall top {p.WallTopMeters} (the v5 test world: 318,000 / 10,400)");
                int bad = 0;
                for (int i = 0; i < 64; i++)
                {
                    double across = (i + 0.5) / 64.0 * p.WidthMeters;
                    if (MegastormFieldCoupling.CeilingAt(across) != ContainmentFieldGeometry.CloudCeilingAt(p, across)) bad++;
                }
                Check(bad == 0, $"wiring: the megastorm ceiling differs from CloudCeilingAt at {bad} of 64 samples");
                Check(host.LightningAdapter != null && host.UpperAdapter != null && host.ContactAdapter != null && host.MegastormAdapter != null, "wiring: adapters");
                _fixture = ReadFixture();
                layer.CloudSimulationPaused = true;
                Check(megastorm.Trigger(new MegastormTrigger(_fixture.TriggerId, _fixture.Leading, _fixture.Direction, _fixture.TriggerTime, _fixture.Speed, _fixture.Formation, _fixture.Seed)), "wiring: trigger");
                _clock = _fixture.WeatherTime;
                layer.CloudSimulationTimeSeconds = _clock;
                host.ObserverProvider = () => CameraAt(megastorm.Model, _fixture, layer.CloudSimulationTimeSeconds);
                return _failures.Count == 0 ? -1 : Finish();
            }
            if (host?.Bus == null) { Check(false, "wiring: host vanished"); return Finish(); }
            // Advance the weather clock 0.25 s per frame for 30 s and watch the bus fill from the real layer.
            if (_ticks++ < 120)
            {
                _clock += 0.25;
                layer.CloudSimulationTimeSeconds = _clock;
                foreach (RingLightningEvent arc in layer.ActiveContactArcs) _arcIds.Add(arc.EventId);
                foreach (RingLightningEvent e in layer.ActiveLightningEvents) if (e.FieldStrike) _strikeIds.Add(e.EventId);
                return -1;
            }
            ContainmentDisturbanceBus bus = host.Bus;
            Check(bus.Accepted + bus.Merged > 0, "wiring: the megastorm published nothing to the live bus");
            Check(host.MegastormAdapter!.Publisher != null && host.MegastormAdapter.Publisher.PublishedContact + host.MegastormAdapter.Publisher.PublishedBurst > 0, "wiring: no megastorm contact reached the bus");
            Check(_strikeIds.Count > 0 && host.LightningAdapter!.StrikesPublished > 0, $"wiring: field strikes seen {_strikeIds.Count}, published {host.LightningAdapter!.StrikesPublished}");
            Check(host.LightningAdapter.StrikesPublished <= _strikeIds.Count, "wiring: more strikes published than the layer made");
            Check(_arcIds.All(id => Live(bus).All(r => r.SourceId != id)), "wiring: a contact arc published a disturbance");
            Check(bus.LiveCount <= 16 && bus.PeakLive <= 16, "wiring: pool over 16");
            Console.WriteLine($"CONTAINMENT_DISTURBANCE_INFO wiring: accepted {bus.Accepted}, merged {bus.Merged}, restruck {bus.Restruck}, far {bus.FarListed}, strikes seen {_strikeIds.Count} "
                + $"published {host.LightningAdapter.StrikesPublished}, arcs {_arcIds.Count}, peak live {bus.PeakLive}");
            // The storm-contact adapter refreshes its system list every 1.5 s of weather time: what one refresh costs.
            var systems = new List<RingVolumetricCloudLayer.RenderWeatherSystem>();
            RingCanonicalPosition cam = CameraAt(megastorm.Model, _fixture!, _clock);
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < 20; i++) { systems.Clear(); layer.CollectRenderWeatherSystems(cam, StormContactAdapter.CollectRadiusMeters, _clock, systems, 0.0f, 500.0f, includeMegastorm: false); }
            Console.WriteLine($"CONTAINMENT_DISTURBANCE_INFO storm contact refresh: {(Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency / 20:0.000} ms per {StormContactAdapter.CollectRadiusMeters / 1000:0} km collect every {StormContactAdapter.RefreshSeconds} s");
            host.ObserverProvider = null;
            return Finish();
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); Check(false, "wiring exception " + e.Message); return Finish(); }
    }

    private int Finish()
    {
        if (_failures.Count != 0) { Console.Error.WriteLine("CONTAINMENT_DISTURBANCE_WIRING=FAIL " + string.Join(" | ", _failures.Take(20))); return 1; }
        Console.WriteLine("CONTAINMENT_DISTURBANCE_WIRING=PASS");
        return 0;
    }
}
