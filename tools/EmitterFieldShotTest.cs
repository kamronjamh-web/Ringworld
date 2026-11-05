using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Underground;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Emitter field shots, data side (the orb each ground emitter fires up to the containment field): the pure schedule and orb kinematics, the EmitterShot disturbance
/// kind and the adapter, the bus reservation and its isolation from the other sources, the shot life covering the flare and the launch collection, then a staged
/// wiring check in the integration scene (node, host adapter, the world segment crossing agreeing with the 2D contact, ripples reaching the live bus, then the orb
/// layers: near and far draw counts, instance positions and custom data, disabled and underground hiding, shader sources; and the launch and crackle audio: the
/// d / 343 delay, nothing underwater). Expectations come from the live registry, topology and field geometry.
/// Run: Godot --headless --path . --script res://tools/emitter_field_shot_test.gd. Prints EMITTER_FIELD_SHOT=PASS and EMITTER_FIELD_SHOT_WRAPPER=PASS.
/// </summary>
public partial class EmitterFieldShotTest : RefCounted
{
    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok && _failures.Count < 200) _failures.Add(message); }
    private static void Info(string message) => Console.WriteLine("EMITTER_FIELD_SHOT_INFO " + message);
    private static string F(double v, string format = "0.###") => v.ToString(format, CultureInfo.InvariantCulture);

    private double _circumference, _ringRadius, _hubAcross;
    private ContainmentFieldProfile _profile;
    private EmitterGridTopology _topology = null!;
    private RingCanonicalPosition _observer;

    private static EmitterFieldShotTiming Timing(double interval) => EmitterFieldShotTiming.FromEclipse(new EclipseEventSettings(), interval);

    private EmitterFieldShotSchedule Build(EmitterBeamOrigin origin, double interval)
        => EmitterFieldShotSchedule.Build(_topology, origin, _profile, _ringRadius, 0.0, Timing(interval)) ?? throw new InvalidOperationException("schedule did not build");

    private sealed class RecordingSink : IContainmentDisturbanceSink
    {
        public ContainmentDisturbanceBus Bus = null!;
        public readonly List<(long Id, double Start, ContainmentDisturbanceKind Kind, ContainmentPublishResult Result)> Log = new();
        public bool Publish(in ContainmentDisturbance d)
        {
            ContainmentPublishResult r = Bus.TryPublish(d);
            Log.Add((d.SourceId, d.StartTimeSeconds, d.Kind, r));
            return r <= ContainmentPublishResult.FarList;
        }
    }

    public int Run()
    {
        try
        {
            LightningVisualPolicy.Reset();
            using RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen17(1337);
            using INaturalHeightSource source = EmitterSiteHeightSources.Create(sampler);
            EmitterSiteRegistry registry = EmitterSiteRegistry.GetOrBuild(EmitterSiteRecipe.Default(1337), source);
            _topology = EmitterGridTopology.Build(registry);
            _circumference = EmitterSiteTestWorlds.Circumference;
            _ringRadius = _circumference / Math.Tau;
            _hubAcross = _topology.HubAcrossMeters;
            double width = registry.Recipe.WidthM;
            RimWallDescription wall = RimWallDescription.Current;
            _profile = new ContainmentFieldProfile(width, 10_400.0, ContainmentFieldProfile.DefaultApexMeters, wall.RootAcross(0, width), wall.RootAcross(1, width),
                ContainmentFieldProfile.DefaultSkirtHeightMeters);
            Check(_profile.IsValid, "the fixture profile is not valid");
            (double oa, _, double ox) = _topology.HubBeamOrigin(1, EmitterBeamOrigin.Base);
            _observer = new RingCanonicalPosition(oa, 0.0, ox);
            Info($"{_topology.HubBeamCount} hub beams, width {width:0}, apex {_profile.ApexMeters:0}, observer at beam 1 (around {oa:0}, across {ox:0})");

            var sw = Stopwatch.StartNew();
            EndPoint();
            Info($"end point {sw.ElapsedMilliseconds} ms"); sw.Restart();
            DeterminismAndRewind();
            Info($"determinism {sw.ElapsedMilliseconds} ms"); sw.Restart();
            Rate();
            Info($"rate {sw.ElapsedMilliseconds} ms"); sw.Restart();
            OneDisturbancePerShot();
            Info($"one disturbance per shot {sw.ElapsedMilliseconds} ms"); sw.Restart();
            CapsAndNoStarvation();
            Info($"caps {sw.ElapsedMilliseconds} ms"); sw.Restart();
            LifeAndLaunches();
            Info($"life and launches {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); Console.Error.WriteLine("EMITTER_FIELD_SHOT=FAIL exception " + e.Message + " | earlier: " + string.Join(" | ", _failures.Take(10))); return 1; }
        finally { LightningVisualPolicy.Reset(); }
        if (_failures.Count != 0) { Console.Error.WriteLine("EMITTER_FIELD_SHOT=FAIL " + string.Join(" | ", _failures.Take(25))); return 1; }
        Console.WriteLine("EMITTER_FIELD_SHOT=PASS");
        return 0;
    }

    // ---- c. the end point is on the field ----
    private void EndPoint()
    {
        foreach (EmitterBeamOrigin origin in new[] { EmitterBeamOrigin.Base, EmitterBeamOrigin.Crown })
        {
            EmitterFieldShotSchedule s = Build(origin, 300.0);
            Check(s.ExcludedBeams == 0 && s.BeamCount == _topology.HubBeamCount, $"{origin}: {s.ExcludedBeams} beams excluded of {_topology.HubBeamCount}");
            double minAge = double.MaxValue, maxAge = 0.0, worstSd = 0.0, worstV = 0.0, minS = double.MaxValue, maxS = 0.0;
            int bad = 0;
            foreach (int b in s.Beams)
            {
                (double a, double v, double x) = s.ContactOf(b);
                double h = ContainmentFieldGeometry.HeightAt(_profile, x), sd = Math.Abs(ContainmentFieldGeometry.SignedDistance(_profile, new RingCanonicalPosition(a, v, x)));
                worstSd = Math.Max(worstSd, sd); worstV = Math.Max(worstV, Math.Abs(v - h));
                if (!(sd < 0.01) || !(x > _profile.RootAcross0 && x < _profile.RootAcrossW) || !(Math.Abs(v - h) < 0.01)) bad++;
                double age = s.ArrivalAgeOf(b);
                minAge = Math.Min(minAge, age); maxAge = Math.Max(maxAge, age);
                minS = Math.Min(minS, s.ContactDistanceOf(b)); maxS = Math.Max(maxS, s.ContactDistanceOf(b));
                Check(age >= 2.0 && age <= 30.0, $"{origin} beam {b}: arrival age {age:0.00} s outside 2..30");
                // The orb at its arrival time and on the way.
                var shot = new EmitterFieldShot(1, b, s.SiteOf(b), 0, 0.0, age, 0.5f);
                EmitterFieldShotState end = s.Evaluate(shot, age);
                if (end.Phase != EmitterFieldShotPhase.Dispersing || Math.Abs(end.DistanceAlongMeters - s.ContactDistanceOf(b)) > 0.01 || Math.Abs(end.VerticalMeters - v) > 0.01
                    || Math.Abs(end.AcrossMeters - x) > 0.01 || Math.Abs(end.AroundMeters - a) > 0.01) bad++;
                double previous = -1.0;
                for (int i = 0; i <= 24; i++)
                {
                    double t = age * Math.Min(i / 24.0, 0.999);
                    EmitterFieldShotState st = s.Evaluate(shot, t);
                    double sd2 = ContainmentFieldGeometry.SignedDistance(_profile, new RingCanonicalPosition(st.AroundMeters, st.VerticalMeters, st.AcrossMeters));
                    if (st.Phase != EmitterFieldShotPhase.Rising || !(sd2 < 0.0) || !(st.DistanceAlongMeters > previous) && i > 0 || st.DistanceAlongMeters > s.ContactDistanceOf(b)) { bad++; break; }
                    previous = st.DistanceAlongMeters;
                }
            }
            Check(bad == 0, $"{origin}: {bad} beams failed the end-point or rising checks");
            Info($"{origin}: contact distance {minS:0}..{maxS:0} m, arrival age {minAge:0.00}..{maxAge:0.00} s at defaults, worst |SignedDistance| {worstSd:0.000000} m, worst |vertical - H| {worstV:0.000000} m");
            // Outside the life, nothing.
            var probe = new EmitterFieldShot(1, 1, s.SiteOf(1), 0, 10.0, 10.0 + s.ArrivalAgeOf(1), 0.5f);
            Check(s.Evaluate(probe, 9.999).Phase == EmitterFieldShotPhase.None && s.Evaluate(probe, 10.0 + s.MaxLifeSeconds + 1.0).Phase == EmitterFieldShotPhase.None, $"{origin}: a shot is not None outside its life");
            Check(s.Evaluate(probe, 10.0).Phase == EmitterFieldShotPhase.Rising && s.Evaluate(probe, 10.0).OrbEnvelope == 0.0f, $"{origin}: the orb starts at envelope 0");
            Check(s.Evaluate(probe, 10.0 + 0.5).OrbEnvelope == 1.0f, $"{origin}: the envelope is 1 after the climb");
            EmitterFieldShotState mid = s.Evaluate(probe, probe.ArrivalTimeSeconds + 0.3);
            Check(mid.Phase == EmitterFieldShotPhase.Dispersing && mid.Flare > 0.9f && mid.Spread > 0.0f && mid.Spread < 1.0f && mid.OrbEnvelope < 1.0f && mid.OrbEnvelope > 0.0f, $"{origin}: dispersal state {mid}");
        }
    }

    // ---- a. determinism and rewind ----
    private void DeterminismAndRewind()
    {
        EmitterFieldShotSchedule a = Build(EmitterBeamOrigin.Base, 300.0), b = Build(EmitterBeamOrigin.Base, 300.0);
        var la = new List<EmitterFieldShot>(); var lb = new List<EmitterFieldShot>();
        a.CollectArrivals(0.0, 7200.0, la); b.CollectArrivals(0.0, 7200.0, lb);
        Check(la.Count > 1000 && la.SequenceEqual(lb), $"two independent schedules differ over 7200 s ({la.Count} vs {lb.Count})");
        for (int i = 1; i < la.Count; i++)
            if (la[i].ArrivalTimeSeconds < la[i - 1].ArrivalTimeSeconds) { Check(false, "arrivals are not ordered by time"); break; }
        // Per-frame collection equals one collect.
        var whole = new List<EmitterFieldShot>(); var frames = new List<EmitterFieldShot>(); var one = new List<EmitterFieldShot>();
        a.Timing = Timing(60.0);
        a.CollectArrivals(0.0, 600.0, whole);
        for (int i = 1; i <= 36_000; i++)
        {
            a.CollectArrivals((i - 1) / 60.0, i / 60.0, one);
            frames.AddRange(one);
        }
        Check(whole.Count > 1000 && whole.SequenceEqual(frames), $"1/60 s frames differ from one collect ({frames.Count} vs {whole.Count})");
        // Evaluate is a pure function of time.
        int bad = 0;
        for (int i = 0; i < whole.Count; i += Math.Max(1, whole.Count / 40))
        {
            EmitterFieldShot shot = whole[i];
            double t = shot.LaunchTimeSeconds + 0.37 * (shot.ArrivalTimeSeconds - shot.LaunchTimeSeconds), later = shot.ArrivalTimeSeconds + 0.5;
            EmitterFieldShotState s1 = a.Evaluate(shot, t), s2 = a.Evaluate(shot, later), s3 = a.Evaluate(shot, t);
            if (s1 != s3 || s1.Phase != EmitterFieldShotPhase.Rising || s2.Phase != EmitterFieldShotPhase.Dispersing) bad++;
        }
        Check(bad == 0, $"{bad} Evaluate calls were not repeatable or in the wrong phase");
        // Setting the same timing again keeps the schedule; a different timing changes only the ages.
        double age0 = a.ArrivalAgeOf(1);
        a.Timing = a.Timing with { OrbTravelSeconds = 15.0 };
        Check(Math.Abs(a.ArrivalAgeOf(1) - age0 * 0.5) < 1e-9, "arrival age does not scale with the orb travel time");
        a.Timing = Timing(60.0);

        // Adapter and a real bus, driven at 1/60 s, then scrubbed back and replayed.
        var observer = _observer;
        var sink = new RecordingSink();
        var bus = new ContainmentDisturbanceBus(_profile, _circumference) { Observer = observer };
        sink.Bus = bus;
        var adapter = new EmitterShotDisturbanceAdapter(sink, () => a, _circumference);
        for (int i = 0; i <= 36_000; i++) { double t = i / 60.0; bus.Advance(t); adapter.Update(t, observer); }
        List<long> first = sink.Log.Where(e => e.Result == ContainmentPublishResult.Accepted && e.Start > 300.0 && e.Start <= 600.0).Select(e => e.Id).ToList();
        int firstAll = sink.Log.Count(e => e.Result == ContainmentPublishResult.Accepted);
        sink.Log.Clear();
        bus.Advance(300.0); adapter.Update(300.0, observer);
        for (int i = 18_001; i <= 36_000; i++) { double t = i / 60.0; bus.Advance(t); adapter.Update(t, observer); }
        List<long> replay = sink.Log.Where(e => e.Result == ContainmentPublishResult.Accepted && e.Start > 300.0 && e.Start <= 600.0).Select(e => e.Id).ToList();
        Check(first.Count >= 5 && first.SequenceEqual(replay), $"the replay after a scrub accepted a different set ({first.Count} first pass vs {replay.Count})");
        Info($"rewind: {firstAll} accepted in 0..600 s at 60 s interval near the observer, {first.Count} in (300, 600], replay equal = {first.SequenceEqual(replay)}");
        // A paused clock publishes nothing.
        int before = adapter.Published;
        for (int i = 0; i < 50; i++) adapter.Update(600.0, observer);
        Check(adapter.Published == before, "a paused clock published");
        // Non-finite time and a missing schedule.
        var none = new EmitterShotDisturbanceAdapter(sink, () => null, _circumference);
        Check(none.Update(10.0, observer) == 0 && adapter.Update(double.NaN, observer) == 0, "a missing schedule or NaN time published");
    }

    // ---- b. rate ----
    private void Rate()
    {
        EmitterFieldShotSchedule s = Build(EmitterBeamOrigin.Base, 300.0);
        var list = new List<EmitterFieldShot>();
        const double Hours = 50.0, Span = Hours * 3600.0;
        var sets = new Dictionary<double, HashSet<long>>();
        foreach (double interval in new[] { 60.0, 300.0, 900.0 })
        {
            s.Timing = Timing(interval);
            s.CollectArrivals(0.0, Span, list);
            double expected = s.BeamCount * Span / interval, ratio = list.Count / expected;
            double perEmitterPerHour = list.Count / Hours / s.BeamCount;
            Check(Math.Abs(ratio - 1.0) < 0.05, $"interval {interval}: {list.Count} shots, expected {expected:0} (ratio {ratio:0.0000})");
            Info($"interval {interval:0} s: {list.Count} shots in {Hours} h ring-wide = {perEmitterPerHour:0.00} per emitter per hour (expected {3600.0 / interval:0.00}), ratio {ratio:0.0000}");
            if (interval <= 300.0) { s.CollectArrivals(0.0, 7200.0, list); sets[interval] = list.Select(x => x.Id).ToHashSet(); }
        }
        Check(sets[300.0].Count > 1000 && sets[300.0].IsSubsetOf(sets[60.0]), "the 300 s shots are not a subset of the 60 s shots");
        // Off values.
        var sink = new RecordingSink();
        var bus = new ContainmentDisturbanceBus(_profile, _circumference) { Observer = _observer };
        sink.Bus = bus;
        var adapter = new EmitterShotDisturbanceAdapter(sink, () => s, _circumference);
        foreach (double interval in new[] { 0.0, 5.0, 9.99, double.NaN })
        {
            s.Timing = Timing(interval);
            Check(s.Timing.Off && s.CollectArrivals(0.0, 7200.0, list) == 0 && s.CollectActive(100.0, list) == 0, $"interval {interval} gave shots");
            for (int i = 0; i <= 3600; i++) { double t = i / 6.0; bus.Advance(t); adapter.Update(t, _observer); }
            Check(sink.Log.Count == 0, $"interval {interval}: the adapter published {sink.Log.Count}");
            bus.Clear(); bus.Advance(0.0); adapter.Update(0.0, _observer);   // the clock jumped back: re-arm
        }
        s.Timing = Timing(EmitterFieldShotSchedule.SlotSeconds);
        Check(!s.Timing.Off && s.CollectArrivals(0.0, 600.0, list) > s.BeamCount * 50, "interval 10 s (the smallest) did not give a shot per slot");
        // Emitters near the observer.
        s.Timing = Timing(300.0);
        int near = 0;
        foreach (int b in s.Beams)
        {
            (double a, _, double x) = s.ContactOf(b);
            double da = WrapSigned(a - _observer.AroundMeters, _circumference), dz = x - _observer.AcrossMeters;
            if (Math.Sqrt(da * da + dz * dz) <= ContainmentDisturbanceBus.RangeMeters) near++;
        }
        Info($"emitters within {ContainmentDisturbanceBus.RangeMeters / 1000:0} km of an observer at a regular emitter: {near}; at 300 s that is {near * 60.0 / 300.0:0.0} shots per minute nearby (ring-wide {s.BeamCount * 60.0 / 300.0:0.0} per minute)");
        Check(near >= 5, $"only {near} emitters within range of the observer");
    }

    // ---- d. one disturbance per shot ----
    private void OneDisturbancePerShot()
    {
        LightningVisualPolicy.Reset();
        EmitterFieldShotSchedule s = Build(EmitterBeamOrigin.Base, 10.0);
        var sink = new RecordingSink();
        var bus = new ContainmentDisturbanceBus(_profile, _circumference) { Observer = _observer };
        sink.Bus = bus;
        var adapter = new EmitterShotDisturbanceAdapter(sink, () => s, _circumference);
        var published = new HashSet<long>(); var seen = new HashSet<long>();
        int duplicatePublish = 0, duplicateSlot = 0, badRipple = 0;
        ContainmentRipple? sample = null;
        for (int i = 0; i <= 216_000; i++)
        {
            double t = i / 60.0;
            bus.Advance(t);
            int logged = sink.Log.Count;
            adapter.Update(t, _observer);
            for (int j = logged; j < sink.Log.Count; j++) if (!published.Add(sink.Log[j].Id)) duplicatePublish++;
            for (int k = 0; k < bus.Slots.Count; k++)
            {
                ContainmentRipple? r = bus.Slots[k];
                if (r == null) continue;
                for (int m = k + 1; m < bus.Slots.Count; m++) if (bus.Slots[m]?.SourceId == r.SourceId) duplicateSlot++;
                if (r.Kind != ContainmentDisturbanceKind.EmitterShot || !seen.Add(r.SourceId)) continue;
                sample ??= r;
                if (!(r.Energy >= 0.45f - 1e-6f && r.Energy <= 0.75f + 1e-6f) || Math.Abs(r.RadiusMeters - (3000.0f + 5000.0f * r.Energy)) > 1e-3f
                    || r.DurationSeconds != EmitterFieldShotSchedule.RippleDurationSeconds || r.FlashCoupling != 1.0f || !(r.FlashAt(r.StartTimeSeconds + 0.3) > 0.5f)) badRipple++;
            }
        }
        Check(published.Count > 1000, $"only {published.Count} publishes in an hour at 10 s");
        Check(duplicatePublish == 0, $"{duplicatePublish} adapter publishes reused an id");
        Check(duplicateSlot == 0, $"{duplicateSlot} frames held two ripples with the same SourceId");
        Check(seen.Count > 500 && badRipple == 0, $"{badRipple} of {seen.Count} accepted ripples had the wrong energy, radius, duration or flash");
        Info($"one hour at 10 s: {published.Count} publishes, {seen.Count} ripples accepted, rejected by the bus {sink.Log.Count(e => e.Result != ContainmentPublishResult.Accepted)} (rate limited {bus.RateLimited}, out of range {adapter.OutOfRange})");
        // Reduced flashes: the peak and the steps.
        Check(sample != null, "no sample ripple");
        if (sample == null) return;
        try
        {
            LightningVisualPolicy.Apply(1.0f, 1.0f, true);
            float peak = 0.0f, previous = 0.0f, maxStep = 0.0f;
            for (int i = 0; i <= 300; i++)
            {
                float level = sample.FlashAt(sample.StartTimeSeconds - 0.1 + i / 60.0);
                peak = Math.Max(peak, level); maxStep = Math.Max(maxStep, Math.Abs(level - previous)); previous = level;
            }
            Check(peak > 0.3f && peak <= 0.4f + 1e-6f, $"reduced flashes: peak {peak}");
            Check(maxStep <= 0.05f, $"reduced flashes: largest frame step {maxStep}");
            Info($"reduced flashes: flare peak {peak:0.000}, largest 60 fps step {maxStep:0.0000}");
        }
        finally { LightningVisualPolicy.Reset(); }
    }

    // ---- f. the life covers the flare; the launch collection ----
    private void LifeAndLaunches()
    {
        foreach (bool reduced in new[] { false, true })
        {
            LightningVisualPolicy.Apply(1.0f, 1.0f, reduced);
            try
            {
                EmitterFieldShotSchedule s = Build(EmitterBeamOrigin.Base, 300.0);
                // A short hold (shorter than the reduced flare rise) is the case the life fix is for.
                s.Timing = Timing(300.0) with { HoldSeconds = 0.1 };
                double fade = s.Timing.FadeSeconds;
                Check(s.MaxLifeSeconds >= s.ArrivalAgeOf(s.Beams[0]) + EmitterFieldShotSchedule.FlareReducedRiseSeconds + fade - 1e-9, $"reduced={reduced}: MaxLifeSeconds {s.MaxLifeSeconds:0.000} does not cover the flare");
                int bad = 0, flareSteps = 0;
                foreach (int b in s.Beams.ToArray().Take(5))
                {
                    var shot = new EmitterFieldShot(1, b, s.SiteOf(b), 0, 10.0, 10.0 + s.ArrivalAgeOf(b), 0.5f);
                    for (double x = 0.0; x <= EmitterFieldShotSchedule.FlareReducedRiseSeconds + fade + 0.5; x += 0.005)
                    {
                        EmitterFieldShotState st = s.Evaluate(shot, shot.ArrivalTimeSeconds + x);
                        if (s.FlareLevel(x) > 0.0f) { flareSteps++; if (st.Phase == EmitterFieldShotPhase.None || st.Flare != s.FlareLevel(x)) bad++; }
                    }
                }
                Check(flareSteps > 100 && bad == 0, $"reduced={reduced}: {bad} of {flareSteps} flare samples fell outside the shot's life");
                // CollectActive keeps a shot while its flare runs.
                var arrivals = new List<EmitterFieldShot>(); var active = new List<EmitterFieldShot>();
                s.CollectArrivals(100.0, 400.0, arrivals);
                int missing = 0;
                foreach (EmitterFieldShot shot in arrivals.Take(60))
                {
                    double t = shot.ArrivalTimeSeconds + Math.Max(0.1, EmitterFieldShotSchedule.FlareReducedRiseSeconds) + fade - 0.01;
                    s.CollectActive(t, active);
                    if (!active.Contains(shot)) missing++;
                }
                Check(arrivals.Count > 60 && missing == 0, $"reduced={reduced}: {missing} shots left the active list before their flare ended");
            }
            finally { LightningVisualPolicy.Reset(); }
        }
        // CollectLaunches: deterministic, ordered, windowed, consistent with the arrivals.
        EmitterFieldShotSchedule a = Build(EmitterBeamOrigin.Base, 60.0), c = Build(EmitterBeamOrigin.Base, 60.0);
        var la = new List<EmitterFieldShot>(); var lb = new List<EmitterFieldShot>(); var arr = new List<EmitterFieldShot>(); var step = new List<EmitterFieldShot>(); var frames = new List<EmitterFieldShot>();
        a.CollectLaunches(0.0, 1200.0, la); c.CollectLaunches(0.0, 1200.0, lb);
        Check(la.Count > 300 && la.SequenceEqual(lb), $"two schedules gave different launches ({la.Count} vs {lb.Count})");
        bool ordered = true, windowed = true;
        for (int i = 0; i < la.Count; i++)
        {
            if (!(la[i].LaunchTimeSeconds > 0.0 && la[i].LaunchTimeSeconds <= 1200.0)) windowed = false;
            if (i > 0 && (la[i].LaunchTimeSeconds < la[i - 1].LaunchTimeSeconds || la[i].LaunchTimeSeconds == la[i - 1].LaunchTimeSeconds && la[i].Id < la[i - 1].Id)) ordered = false;
        }
        Check(ordered && windowed, "launches are not ordered by (launch, id) or leave the window");
        for (int i = 1; i <= 1200 * 4; i++) { a.CollectLaunches((i - 1) / 4.0, i / 4.0, step); frames.AddRange(step); }
        Check(frames.OrderBy(x => x.LaunchTimeSeconds).ThenBy(x => x.Id).SequenceEqual(la), $"0.25 s windows differ from one collect ({frames.Count} vs {la.Count})");
        a.CollectArrivals(0.0, 1200.0 + 60.0, arr);
        var byId = arr.ToDictionary(x => x.Id);
        Check(la.All(x => byId.TryGetValue(x.Id, out EmitterFieldShot y) && y == x), "a launch has no matching arrival record");
        Check(a.CollectLaunches(10.0, 10.0, step) == 0 && a.CollectLaunches(double.NaN, 10.0, step) == 0, "an empty or NaN window gave launches");
        a.Timing = Timing(0.0);
        Check(a.CollectLaunches(0.0, 1200.0, step) == 0, "an off interval gave launches");
        Info($"launches: {la.Count} in 1200 s at 60 s, ordered {ordered}, equal to the 0.25 s windows {frames.Count == la.Count}");
    }

    // ---- e. caps and no starvation ----
    private void CapsAndNoStarvation()
    {
        var alone = RunBusyStream(withEmitters: false, out _);
        var loaded = RunBusyStream(withEmitters: true, out (int MaxEmitter, int MaxLive, int Starts, int Accepted, int Rejected) stats);
        Check(alone.Count > 100, $"the scripted stream was too small ({alone.Count})");
        bool same = alone.Count == loaded.Count && alone.All(kv => loaded.TryGetValue(kv.Key, out ContainmentPublishResult r) && r == kv.Value);
        Check(same, $"the other sources' results changed with the emitter load ({alone.Count} vs {loaded.Count} ids, {alone.Count(kv => !loaded.TryGetValue(kv.Key, out ContainmentPublishResult r) || r != kv.Value)} differ)");
        Check(stats.MaxEmitter <= ContainmentDisturbanceBus.EmitterLiveCap && stats.MaxEmitter >= 1, $"emitter live max {stats.MaxEmitter} (cap {ContainmentDisturbanceBus.EmitterLiveCap})");
        Check(stats.MaxLive <= 16, $"total live {stats.MaxLive}");
        Check(stats.Starts >= 20, $"only {stats.Starts} emitter shots accepted in the busy minute");
        Info($"busy minute with the emitter load: {stats.Accepted} EmitterShot accepted, {stats.Rejected} rejected, max emitter live {stats.MaxEmitter}, max live {stats.MaxLive}; {alone.Count} other publishes identical = {same}");
    }

    private Dictionary<long, ContainmentPublishResult> RunBusyStream(bool withEmitters, out (int MaxEmitter, int MaxLive, int Starts, int Accepted, int Rejected) stats)
    {
        var bus = new ContainmentDisturbanceBus(_profile, _circumference) { Observer = _observer };
        var sink = new RecordingSink { Bus = bus };
        EmitterFieldShotSchedule? schedule = withEmitters ? Build(EmitterBeamOrigin.Base, 10.0) : null;
        var adapter = new EmitterShotDisturbanceAdapter(sink, () => schedule, _circumference);
        var results = new Dictionary<long, ContainmentPublishResult>();
        double centre = _profile.Centre;
        ContainmentDisturbance Make(long id, ContainmentDisturbanceKind kind, int i, double t, float energy, float radius, float duration, float coupling, Func<double, float>? flash)
            => new(id, kind, new RingCanonicalPosition(_observer.AroundMeters + ((i * 37) % 41 - 20) * 2500.0, 0.0, centre + ((i * 13) % 11 - 5) * 20_000.0), t, energy, radius, duration, coupling, flash);
        int maxEmitter = 0, maxLive = 0;
        for (int i = 0; i <= 1800; i++)
        {
            double t = i / 30.0;
            bus.Advance(t);
            // A busy wall: strikes about 2/s (lit), megastorm contact and bursts, ordinary lightning and impacts.
            var stream = new List<ContainmentDisturbance>();
            if (i % 15 == 0) stream.Add(Make(1_000_000 + i, ContainmentDisturbanceKind.FieldStrike, i, t, 0.5f + 0.003f * (i % 100), 9000.0f, 3.0f, 1.0f, _ => 1.0f));
            if (i % 7 == 0) stream.Add(Make(2_000_000 + i, ContainmentDisturbanceKind.MegastormContact, i, t, 0.6f, 4000.0f, 8.0f, 0.0f, null));
            if (i % 29 == 0) stream.Add(Make(3_000_000 + i, ContainmentDisturbanceKind.UpdraftBurst, i, t, 0.6f, 4000.0f, 6.0f, 0.0f, null));
            if (i % 11 == 0) stream.Add(Make(4_000_000 + i, ContainmentDisturbanceKind.Impact, i, t, 0.4f + 0.004f * (i % 50), 3000.0f, 10.0f, 0.0f, null));
            if (i % 17 == 0) stream.Add(Make(5_000_000 + i, ContainmentDisturbanceKind.Lightning, i, t, 0.7f, 5000.0f, 2.5f, 1.0f, _ => 1.0f));
            foreach (ContainmentDisturbance d in stream)
            {
                ContainmentPublishResult r = bus.TryPublish(d);
                results[d.SourceId] = r;
            }
            adapter.Update(t, _observer);
            maxEmitter = Math.Max(maxEmitter, bus.LiveOf(ContainmentDisturbanceKind.EmitterShot));
            maxLive = Math.Max(maxLive, bus.LiveCount);
        }
        var starts = sink.Log.Where(e => e.Result == ContainmentPublishResult.Accepted).Select(e => e.Start).OrderBy(x => x).ToList();
        Check(starts.All(a => starts.Count(b => b >= a && b < a + 1.0) <= 1), "more than one EmitterShot started inside one second");
        stats = (maxEmitter, maxLive, starts.Count, starts.Count, sink.Log.Count - starts.Count);
        return results;
    }

    private static double WrapSigned(double v, double c) { double r = v % c; if (r > 0.5 * c) r -= c; else if (r < -0.5 * c) r += c; return r; }

    // ---- staged wiring check in the integration scene ----
    private int _stage, _frames, _ticks;
    private double _clock;
    private readonly HashSet<long> _ids = new();
    private int _maxActive, _maxLiveEmitter;
    private int _renderFrames, _nearFramesChecked, _farWait, _stepFrames, _audioTicks, _underwaterTicks;
    private bool _dispersingChecked, _farChecked, _farSkipped, _shadersChecked, _hubShadersOk;
    private readonly List<long> _updateUs = new();
    private int _maxRenderActive, _maxDrawnNear, _maxDrawnFar;
    private int _launchStart, _crackleStart, _expectedLaunchBeam;
    private double _expectedLaunchDistance;
    private Camera3D? _testCamera, _previousCamera;
    private readonly List<long> _updateUsDefault = new();
    private int _defaultActive;

    /// <summary>Called once per frame with the integration scene root. Returns -1 to continue, 0 pass, 1 fail.</summary>
    public int Tick(Node scene)
    {
        try
        {
            var shots = scene.GetNodeOrNull<EmitterFieldShotLayer>("EmitterFieldShots");
            var grid = scene.GetNodeOrNull<EmitterGridBeamLayer>("EmitterGrid");
            var gridManager = scene.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager");
            var weather = scene.GetNodeOrNull<RingVolumetricCloudLayer>("RingVolumetricClouds");
            RingContainmentDisturbanceHost? host = RingContainmentDisturbanceHost.Instance;
            if (_stage == 0)
            {
                Check(shots != null && shots.GetParent() == scene, "wiring: the EmitterFieldShots node is not in the scene");
                Check(grid != null && gridManager != null && weather != null && host != null, "wiring: grid, grid manager, weather or host missing");
                if (_failures.Count != 0) return Finish();
                // Wait for the grid to bind and the schedule to build (the terrain sampler and the registry come up lazily).
                if (++_frames > 1500) { Check(false, $"wiring: no schedule after {_frames} frames (bound {grid!.IsBound}, profile {host!.Profile != null})"); return Finish(); }
                if (!grid!.IsBound || shots!.Schedule == null || host!.EmitterShotAdapter == null) return -1;
                _stage = 1;
                EmitterFieldShotSchedule schedule = shots.Schedule;
                EmitterGridTopology topology = grid.Topology!;
                Check(schedule.ExcludedBeams == 0 && schedule.BeamCount == topology.HubBeamCount - schedule.ExcludedBeams, $"wiring: {schedule.BeamCount} beams, {schedule.ExcludedBeams} excluded of {topology.HubBeamCount}");
                Check(schedule.Profile == host.Profile, "wiring: the schedule's profile is not the host's");
                WorldCrossCheck(schedule, topology, gridManager!, host.Profile!.Value);
                Info($"wiring: {shots.DescribeStatus()} (built {shots.Builds}x after {_frames} frames)");
                // Fire often, put the observer at a regular emitter, step the weather clock about 60 s.
                shots.TimingOverride = Timing(10.0);
                (double a, _, double x) = topology.HubBeamOrigin(1, EmitterBeamOrigin.Base);
                var observer = new RingCanonicalPosition(a, 0.0, x);
                host.ObserverProvider = () => observer;
                weather!.CloudSimulationPaused = true;
                _clock = weather.CloudSimulationTimeSeconds;
                return _failures.Count == 0 ? -1 : Finish();
            }
            if (host?.Bus == null || weather == null || shots == null) { Check(false, "wiring: host, bus or weather vanished"); return Finish(); }
            if (_stage >= 2) return TickRender(scene, shots, weather, gridManager!, host);
            if (_ticks++ < 120)
            {
                _clock += 0.5;
                weather.CloudSimulationTimeSeconds = _clock;
                _maxActive = Math.Max(_maxActive, shots.ActiveShots.Count);
                _maxLiveEmitter = Math.Max(_maxLiveEmitter, host.Bus.LiveOf(ContainmentDisturbanceKind.EmitterShot));
                foreach (ContainmentRipple? r in host.Bus.Slots) if (r != null && r.Kind == ContainmentDisturbanceKind.EmitterShot) _ids.Add(r.SourceId);
                return -1;
            }
            EmitterShotDisturbanceAdapter adapter = host.EmitterShotAdapter!;
            Check(_ids.Count > 0 && adapter.Published > 0, $"wiring: no EmitterShot ripple reached the live bus (ripples {_ids.Count}, published {adapter.Published}, rejected {adapter.Rejected}, out of range {adapter.OutOfRange})");
            Check(_maxActive > 0, "wiring: the node listed no active shot at interval 10 s");
            Check(_maxLiveEmitter <= ContainmentDisturbanceBus.EmitterLiveCap, $"wiring: {_maxLiveEmitter} EmitterShot ripples live at once");
            Check(host.Bus.LiveCount <= 16, "wiring: pool over 16");
            Info($"wiring: {_ids.Count} EmitterShot ripples seen on the live bus in 60 s of weather time, published {adapter.Published}, rejected {adapter.Rejected}, out of range {adapter.OutOfRange}, max active shots {_maxActive}");
            host.ObserverProvider = null;
            if (_failures.Count != 0) return Finish();
            _stage = 2;
            return -1;
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); Check(false, "wiring exception " + e.Message); return Finish(); }
    }

    // ---- the orb layers and the audio (stages 2 to 6) ----

    private int TickRender(Node scene, EmitterFieldShotLayer shots, RingVolumetricCloudLayer weather, VoxelChunkGridManager gridManager, RingContainmentDisturbanceHost host)
    {
        var audio = scene.GetNodeOrNull<RingEmitterFieldShotAudio>("EmitterFieldShotAudio");
        var far = scene.GetNodeOrNull<RingScaledFarViewport>("ScaledFarViewport");
        _clock += 0.5;
        weather.CloudSimulationTimeSeconds = _clock;
        switch (_stage)
        {
            case 2:
            {
                if (!_shadersChecked) { _shadersChecked = true; CheckShaders(); }
                if (_renderFrames++ < 3) return -1;
                if (shots.Schedule == null || shots.NearInstances == null) { if (_renderFrames > 600) { Check(false, "render: no near layer after 600 frames"); return Finish(); } return -1; }
                if (_testCamera == null)
                {
                    // The scene's own camera is wherever the explorer spawned and (headless) has a short far plane: put a camera with a long one at an emitter's field contact,
                    // so the near range holds dozens of shots in every phase.
                    _previousCamera = scene.GetViewport().GetCamera3D();
                    (double ca, double cv, double cx) = shots.Schedule.ContactOf(shots.Schedule.Beams[1]);
                    var ctx = new EmitterSiteFrameContext(gridManager.CaptureLodGeometrySnapshot());
                    _testCamera = new Camera3D { Name = "FieldShotTestCamera", Far = 3_000_000f, Near = 0.1f };
                    scene.AddChild(_testCamera);
                    _testCamera.GlobalPosition = ctx.WorldPosition(ca, cv - 500.0, cx).ToVector3();
                    _testCamera.MakeCurrent();
                    _renderFrames = 0;
                    return -1;
                }
                CheckRenderFrame(shots, gridManager, far, scene);
                bool farDone = _farChecked || _farSkipped;
                if (_nearFramesChecked >= 12 && _dispersingChecked && farDone) { _stage = 7; _stepFrames = 0; shots.TimingOverride = Timing(300.0); return -1; }
                if (_renderFrames > 700)
                {
                    Check(_nearFramesChecked >= 12, $"render: only {_nearFramesChecked} frames with shots in the near range");
                    Check(_dispersingChecked, "render: no dispersing near shot found in 700 frames");
                    Check(farDone, "render: the far layer never drew");
                    return Finish();
                }
                return -1;
            }
            case 7:
            {
                // The default interval (300 s): the cost of the layer with the shots a player normally has in flight.
                _stepFrames++;
                if (_stepFrames > 4) { _updateUsDefault.Add(shots.LastUpdateMicroseconds); _defaultActive = Math.Max(_defaultActive, shots.ActiveShots.Count); }
                if (_stepFrames < 34) return -1;
                shots.TimingOverride = Timing(10.0);
                _stage = 3; _stepFrames = 0; shots.Enabled = false;
                return -1;
            }
            case 3:
            {
                // Disabled: nothing drawn; then underground-hidden: nothing drawn.
                if (++_stepFrames < 3) return -1;
                if (_stepFrames == 3)
                {
                    Check(shots.DrawnNear == 0 && shots.DrawnFar == 0 && shots.NearInstances?.Multimesh.VisibleInstanceCount == 0, $"disabled: drew {shots.DrawnNear} near, {shots.DrawnFar} far");
                    shots.Enabled = true;
                    return -1;
                }
                // Back on: wait for the schedule and the first drawn frame, then hide it underground. The scene's underground controller drives the same flag every frame, so
                // the check is made in the call that sets it.
                if (shots.Schedule == null || shots.DrawnNear == 0) { if (_stepFrames > 600) { Check(false, "re-enabled: nothing drawn after 600 frames"); return Finish(); } return -1; }
                shots.SetUndergroundHidden(true);
                Check(shots.DrawnNear == 0 && shots.DrawnFar == 0 && shots.UndergroundHidden && shots.NearInstances?.Multimesh.VisibleInstanceCount == 0 && shots.NearInstances?.Visible == false,
                    $"underground hidden: drew {shots.DrawnNear} near, {shots.DrawnFar} far");
                shots.SetUndergroundHidden(false);
                _stage = 4; _stepFrames = 0;
                return -1;
            }
            case 4:
            {
                if (++_stepFrames < 4) return -1;
                if (shots.Schedule == null) return _stepFrames > 600 ? FailAudio("the schedule did not come back after re-enabling") : -1;
                if (audio == null) { Check(false, "audio: the EmitterFieldShotAudio node is not in the scene"); return Finish(); }
                Check(audio.SampleCountLoaded == 6, $"audio: {audio.SampleCountLoaded} of 6 samples loaded");
                // The listener next to a launching emitter (34.3 m across: a 0.1 s delay), then the weather clock through one 10 s slot (one launch per emitter).
                EmitterFieldShotSchedule schedule = shots.Schedule;
                _expectedLaunchBeam = schedule.Beams[1];
                (double a, double v, double x) = schedule.OriginOf(_expectedLaunchBeam);
                var listener = new RingCanonicalPosition(a, v, x + 34.3);
                audio.ListenerOverride = listener;
                audio.UnderwaterOverride = false;
                ThunderAcousticContext ctx = audio.ContextFor(RingEmitterFieldShotAudio.LaunchRangeMeters, schedule);
                Check(ThunderPropagation.TryDistance(new RingCanonicalPosition(a, v, x), listener, ctx, out _expectedLaunchDistance), "audio: no air path between the emitter and the listener");
                _launchStart = audio.PlayedLaunch; _crackleStart = audio.PlayedCrackle;
                _stage = 5; _audioTicks = 0;
                return -1;
            }
            case 5:
            {
                if (audio == null) return Finish();
                _audioTicks++;
                if (_audioTicks <= 24) return -1;                     // 12 s of weather time in 0.5 s steps (the clock is stepped above)
                bool played = audio.PlayedLaunch > _launchStart;
                if (!played && _audioTicks < 400) { _clock -= 0.5; weather.CloudSimulationTimeSeconds = _clock; return -1; }   // hold the clock, let real time pass
                int launches = audio.PlayedLaunch - _launchStart;
                Check(launches >= 1, $"audio: no launch sound after 12 s of weather time next to emitter beam {_expectedLaunchBeam} (pending {audio.PendingCount})");
                Check(Math.Abs(audio.LastLaunchDelaySeconds - audio.LastLaunchDistanceMeters / RingEmitterFieldShotAudio.SoundSpeedMetersPerSecond) < 1e-3,
                    $"audio: launch delay {audio.LastLaunchDelaySeconds:0.0000} s is not distance {audio.LastLaunchDistanceMeters:0.00} m / 343");
                if (launches == 1) Check(Math.Abs(audio.LastLaunchDistanceMeters - _expectedLaunchDistance) < 1.0, $"audio: launch distance {audio.LastLaunchDistanceMeters:0.00} m, expected {_expectedLaunchDistance:0.00} m");
                Check(audio.LastLaunchDistanceMeters <= RingEmitterFieldShotAudio.LaunchRangeMeters && audio.PendingCount <= RingEmitterFieldShotAudio.MaxPending, "audio: distance or pending count out of bounds");
                Info($"audio: {launches} launch and {audio.PlayedCrackle - _crackleStart} crackle sounds in 12 s, launch {audio.LastLaunchDistanceMeters:0.0} m (expected {_expectedLaunchDistance:0.0}) delay {audio.LastLaunchDelaySeconds * 1000.0:0.0} ms, pending {audio.PendingCount}, voices {audio.ActiveVoiceCount}");
                // Underwater: nothing plays through another 12 s.
                audio.UnderwaterOverride = true;
                _launchStart = audio.PlayedLaunch; _crackleStart = audio.PlayedCrackle;
                _underwaterTicks = 0; _stage = 6;
                return -1;
            }
            case 6:
            {
                if (audio == null) return Finish();
                if (++_underwaterTicks < 26) return -1;
                Check(audio.PlayedLaunch == _launchStart && audio.PlayedCrackle == _crackleStart && audio.ActiveVoiceCount == 0, $"audio: underwater played {audio.PlayedLaunch - _launchStart} launch and {audio.PlayedCrackle - _crackleStart} crackle sounds");
                audio.UnderwaterOverride = null;
                audio.ListenerOverride = null;
                shots.TimingOverride = null;
                return Finish();
            }
        }
        return Finish();
    }

    private int FailAudio(string message) { Check(false, "audio: " + message); return Finish(); }

    private void CheckShaders()
    {
        // Headless has no shader compiler: the sources must at least load and be non-empty (the real compile is the windowed GPU run).
        foreach (string path in new[]
        {
            "res://materials/ring_emitter_field_shot.gdshader", "res://materials/ring_emitter_field_shot_core.gdshader", "res://materials/ring_emitter_field_shot_far.gdshader",
            "res://materials/ring_eclipse_hub_beam.gdshader", "res://materials/ring_eclipse_hub_beam_core.gdshader", "res://materials/ring_eclipse_hub_beam_far.gdshader"
        })
        {
            Shader? shader = GD.Load<Shader>(path);
            Check(shader != null && !string.IsNullOrWhiteSpace(shader.Code), $"shader {path} did not load or is empty");
        }
        Check(UndergroundParticipantFactory.CreateDefaults().Any(p => p.Name == "EmitterFieldShots"), "the EmitterFieldShots underground participant is not registered");
    }

    /// <summary>One frame of render checks against the layer's own last frame (the layer runs after this tick, so its list and clock are the previous frame's).</summary>
    private void CheckRenderFrame(EmitterFieldShotLayer shots, VoxelChunkGridManager gridManager, RingScaledFarViewport? far, Node scene)
    {
        EmitterFieldShotSchedule schedule = shots.Schedule!;
        RingLodGeometrySnapshot snapshot = gridManager.CaptureLodGeometrySnapshot();
        var frame = new EmitterSiteFrameContext(snapshot);
        Camera3D? camera = scene.GetViewport().GetCamera3D();
        float max = shots.EffectiveMaxDistanceMeters;
        var drawn = new List<(int Index, EmitterFieldShot Shot, EmitterFieldShotState State, Vector3 World)>();
        int nonNone = 0;
        foreach (EmitterFieldShot shot in shots.ActiveShots)
        {
            EmitterFieldShotState state = schedule.Evaluate(shot, shots.NowSeconds);
            if (state.Phase == EmitterFieldShotPhase.None) continue;
            nonNone++;
            Vector3 world = frame.WorldPosition(state.AroundMeters, state.VerticalMeters, state.AcrossMeters).ToVector3();
            if (camera == null || (world - camera.GlobalPosition).LengthSquared() < max * max) drawn.Add((drawn.Count, shot, state, world));
        }
        _maxRenderActive = Math.Max(_maxRenderActive, nonNone);
        if (shots.ActiveShots.Count > 0) _updateUs.Add(shots.LastUpdateMicroseconds);
        Check(nonNone == shots.ActiveShots.Count, $"render: {shots.ActiveShots.Count - nonNone} listed shots evaluate to None");
        // The near layer: only the shots inside its range; a boundary shot may flip between the layer's frame and this one, so allow one.
        int diff = Math.Abs(shots.DrawnNear - drawn.Count);
        Check(diff <= 1, $"render: drew {shots.DrawnNear} near, {drawn.Count} of {nonNone} active are inside {max / 1000.0:0} km");
        if (nonNone > 0 && drawn.Count > 0) _nearFramesChecked++;
        _maxDrawnNear = Math.Max(_maxDrawnNear, shots.DrawnNear);
        if (shots.NearInstances?.Multimesh.VisibleInstanceCount != shots.DrawnNear) Check(false, $"render: VisibleInstanceCount {shots.NearInstances?.Multimesh.VisibleInstanceCount} != DrawnNear {shots.DrawnNear}");
        if (diff == 0)
        {
            foreach (var d in drawn)
            {
                (Vector3 origin, Color custom) = shots.GetNearInstance(d.Index);
                if (d.State.Phase == EmitterFieldShotPhase.Dispersing)
                {
                    // The instance is at the world contact (within 1 m) and carries exactly the evaluated state.
                    Vector3 contact = frame.WorldPosition(schedule.ContactOf(d.Shot.Beam).Around, schedule.ContactOf(d.Shot.Beam).Vertical, schedule.ContactOf(d.Shot.Beam).Across).ToVector3();
                    Check((origin - contact).Length() < 1.0f, $"render: the dispersing orb is {(origin - contact).Length():0.000} m from the world contact");
                    Check(Math.Abs(custom.R - d.State.OrbEnvelope) < 1e-6 && Math.Abs(custom.G - d.State.Flare) < 1e-6 && Math.Abs(custom.B - d.State.Spread) < 1e-6 && Math.Abs(custom.A - d.Shot.Energy) < 1e-6,
                        $"render: custom data {custom} != ({d.State.OrbEnvelope}, {d.State.Flare}, {d.State.Spread}, {d.Shot.Energy})");
                    if (d.State.Flare > 0.0f && d.State.Spread > 0.0f) _dispersingChecked = true;
                }
                else
                {
                    Check((origin - d.World).Length() < 1.0f && Math.Abs(custom.R - d.State.OrbEnvelope) < 1e-6 && custom.G == 0.0f && custom.B == 0.0f, "render: a rising orb's instance does not match its state");
                }
            }
        }
        // The far layer: every active shot, ring-wide, once it has bound (retried every 0.5 s).
        if (_farChecked || _farSkipped) return;
        if (far == null || !far.HasScaledWorld)
        {
            if (_renderFrames > 30) { _farSkipped = true; Info("render: the far viewport has no scaled world headless; far layer checks skipped"); }
            return;
        }
        if (shots.FarLayer == null || !shots.FarLayerActive)
        {
            if (++_farWait > 600) { Check(false, "render: the far layer did not bind with a scaled world available"); _farSkipped = true; }
            return;
        }
        Check(shots.DrawnFar == nonNone, $"render: drew {shots.DrawnFar} far of {nonNone} active");
        _maxDrawnFar = Math.Max(_maxDrawnFar, shots.DrawnFar);
        if (shots.DrawnFar > 0)
        {
            (Vector3 farOrigin, Color farCustom) = shots.GetFarInstance(0);
            EmitterFieldShot first = shots.ActiveShots[0];
            EmitterFieldShotState st = schedule.Evaluate(first, shots.NowSeconds);
            Vector3 expected = far.ScaledPoint(st.AroundMeters, st.VerticalMeters, st.AcrossMeters);
            Check((farOrigin - expected).Length() < 1e-3f && Math.Abs(farCustom.R - st.OrbEnvelope) < 1e-6, "render: the first far instance does not match its state");
            _farChecked = true;
        }
    }

    /// <summary>
    /// The world segment from a beam origin toward the hub meets the field where the 2D contact says. The floating origin is re-anchored at each beam's origin first
    /// (float world coordinates are only good to about a decimetre near the anchor, the segment test accepts a crossing to 5 cm) and restored afterwards.
    /// </summary>
    private void WorldCrossCheck(EmitterFieldShotSchedule schedule, EmitterGridTopology topology, VoxelChunkGridManager gridManager, in ContainmentFieldProfile profile)
    {
        RingCanonicalPosition? original = gridManager.CaptureLodGeometrySnapshot().FloatingOriginCanonicalAnchor;
        double worst = 0.0;
        int checkedBeams = 0;
        try
        {
            ReadOnlySpan<int> beams = schedule.Beams;
            for (int n = 0; n < 5; n++)
            {
                int b = beams[1 + n * (beams.Length - 2) / 4];
                (double a0, double v0, double x0) = schedule.OriginOf(b);
                gridManager.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(a0, v0, x0));
                RingLodGeometrySnapshot snap = gridManager.CaptureLodGeometrySnapshot();
                var ctx = new EmitterSiteFrameContext(snap);
                Vector3D origin = ctx.WorldPosition(a0, v0, x0), hub = ctx.HubWorldPosition(topology.HubAcrossMeters);
                Vector3D direction = (hub - origin).Normalized();
                double sc = schedule.ContactDistanceOf(b);
                Vector3 from = origin.ToVector3(), to = (origin + direction * (sc + 2000.0)).ToVector3();
                if (!ContainmentFieldGeometry.TryIntersectWorldSegment(profile, snap, from, to, out ContainmentFieldPoint hit, out _))
                {
                    RingCanonicalPosition ca0 = ContainmentFieldPlacement.WorldToCanonical(snap, from), cb0 = ContainmentFieldPlacement.WorldToCanonical(snap, to);
                    Check(false, $"wiring: beam {b}: the world segment does not meet the field (A {ca0} sd {ContainmentFieldGeometry.SignedDistance(profile, ca0):0.0}, B {cb0} sd {ContainmentFieldGeometry.SignedDistance(profile, cb0):0.0}, contact {schedule.ContactOf(b)})");
                    continue;
                }
                (double ca, double cv, double cx) = schedule.ContactOf(b);
                double da = WrapSigned(hit.AroundMeters - ca, snap.CircumferenceMeters), dv = hit.VerticalMeters - cv, dx = hit.AcrossMeters - cx, distance = Math.Sqrt(da * da + dv * dv + dx * dx);
                worst = Math.Max(worst, distance); checkedBeams++;
                Check(distance < 1.0, $"wiring: beam {b}: the world crossing is {distance:0.000} m from the 2D contact");
            }
        }
        finally
        {
            if (original is RingCanonicalPosition anchor) gridManager.SetFloatingOriginCanonicalAnchor(anchor); else gridManager.ClearFloatingOriginCanonicalAnchor();
        }
        Info($"wiring: world segment vs 2D contact, worst of {checkedBeams} beams {worst:0.0000} m");
    }

    private int Finish()
    {
        if (_updateUs.Count > 0)
        {
            var sorted = _updateUs.OrderBy(x => x).ToList();
            Info($"render: max {_maxRenderActive} active shots, max drawn near {_maxDrawnNear} far {_maxDrawnFar}; LastUpdateMicroseconds over {sorted.Count} frames: median {sorted[sorted.Count / 2]} us, p95 {sorted[(int)(sorted.Count * 0.95)]} us, max {sorted[^1]} us");
        }
        if (_updateUsDefault.Count > 0)
        {
            var sorted = _updateUsDefault.OrderBy(x => x).ToList();
            Info($"render: at the default 300 s interval (up to {_defaultActive} active shots) LastUpdateMicroseconds over {sorted.Count} frames: median {sorted[sorted.Count / 2]} us, max {sorted[^1]} us");
        }
        if (_testCamera != null && GodotObject.IsInstanceValid(_testCamera))
        {
            _testCamera.QueueFree();
            if (_previousCamera != null && GodotObject.IsInstanceValid(_previousCamera)) _previousCamera.MakeCurrent();
            _testCamera = null;
        }
        if (_failures.Count != 0) { Console.Error.WriteLine("EMITTER_FIELD_SHOT_WIRING=FAIL " + string.Join(" | ", _failures.Take(20))); return 1; }
        Console.WriteLine("EMITTER_FIELD_SHOT_WIRING=PASS");
        return 0;
    }
}
