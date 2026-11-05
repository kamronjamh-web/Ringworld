using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Megastorm WO13 (docs/megastorm-design.md 5.5.5 and section 8): field strikes and contact arcs, against a stub field provider (the containment field is not built).
/// Pure checks: the field-strike pulse envelope (re-pulses, reduced flashes, no frame step above 0.05), determinism (two models, window split, save/load, ids), nothing
/// without a provider, the rate law within 10% at five across positions and about 18 per minute in total, the strike-rate scale as proportional counts, channels ending
/// within 100 m below H on the Certified (10.0 km wall top) and runtime-surface (10.4 km) profiles, none under a 0.5 km gap, contact arcs (50-500 m, 0.2-0.5 s,
/// near the rim, no disturbance of their own), the field bus budget over 10 minutes through a stub adapter (SourceId = EventId, no duplicates), a synthetic natural
/// kind 3 event selected and built exactly like a megastorm one. Then a staged run through the real layer: flagged events stay for their own duration while ordinary
/// events prune at 0.72 s, rewind reproduces them, no source or no provider leaves none. Run: tools/megastorm_field_strike_test.gd. Prints MEGASTORM_FIELD_STRIKE=PASS.
/// </summary>
public partial class MegastormFieldStrikeTest : RefCounted
{
    readonly List<string> _failures = new();
    int _stage, _wait;
    MegastormModel? _model;
    MegastormFieldStrike _target;
    RingLightningEvent _first;
    double _t0;

    void Check(bool ok, string message) { if (!ok) _failures.Add(message); }
    static void Info(string message) => Console.WriteLine("MEGASTORM_FIELD_STRIKE_INFO " + message);

    const double Width = 318000.0;

    static MegastormModel NewModel(double lead = 5_000_000.0, int seed = 7, int dir = 1, float formation = 0.0f)
    {
        var model = new MegastormModel();
        model.Trigger(new MegastormTrigger("field_strike_test", lead, dir, 0.0, 350.0f, formation, seed));
        return model;
    }

    /// <summary>The stub field underside: a semi-ellipse from the wall top to the apex (containment-field-design 1.2).</summary>
    static Func<double, double> Provider(double wallTop, double apex = 40000.0)
    {
        double a = 0.5 * Width, b = apex - wallTop;
        return across =>
        {
            if (across < 0.0 || across > Width) return double.NaN;
            double x = (across - a) / a;
            return wallTop + b * Math.Sqrt(Math.Max(0.0, 1.0 - x * x));
        };
    }

    sealed class StubAdapter : IContainmentDisturbanceSink
    {
        public readonly List<ContainmentDisturbance> Items = new();
        readonly HashSet<long> _ids = new();
        public int Duplicates;
        public bool Publish(in ContainmentDisturbance d)
        {
            if (!_ids.Add(d.SourceId)) { Duplicates++; return false; }
            Items.Add(d);
            return true;
        }
        /// <summary>What the field's LightningDisturbanceAdapter does for flagged events: one FieldStrike per EventId, Position = the contact point, FlashLevel = the event's own pulse.</summary>
        public void Pump(IReadOnlyList<RingLightningEvent> active)
        {
            foreach (RingLightningEvent e in active)
            {
                if (!e.FieldStrike) continue;
                RingLightningEvent copy = e;
                Publish(new ContainmentDisturbance(e.EventId, ContainmentDisturbanceKind.FieldStrike, e.GroundPosition, e.StartTimeSeconds, e.Energy, 8000.0f, 3.0f, 1.0f,
                    tt => copy.PulseAt(tt)));
            }
        }
    }

    int Finish(string stage)
    {
        if (_failures.Count == 0) { Info(stage + " ok"); return 0; }
        foreach (string f in _failures) Console.Error.WriteLine("MEGASTORM_FIELD_STRIKE=FAIL " + stage + ": " + f);
        return 1;
    }

    public int RunPure()
    {
        Func<double, double>? savedProvider = MegastormFieldCoupling.FieldUndersideProvider;
        Func<double, double> savedCeiling = MegastormFieldCoupling.CeilingFromUnderside;
        try
        {
            LightningVisualPolicy.Reset();
            Envelope();
            MegastormFieldCoupling.FieldUndersideProvider = Provider(10000.0);
            Determinism();
            Gating();
            RateLaw();
            Geometry();
            Arcs();
            Budget();
            Cost();
            SharedConvention();
            LightningVisualPolicy.Reset();
            return Finish("PURE");
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        finally
        {
            MegastormFieldCoupling.FieldUndersideProvider = savedProvider;
            MegastormFieldCoupling.CeilingFromUnderside = savedCeiling;
            LightningVisualPolicy.Reset();
        }
    }

    static RingLightningEvent Flagged(float duration, int seed = 12345, bool flag = true, int kind = 6) => new(77, 1, 100.0, new RingCanonicalPosition(0, 25000.0, 0),
        new RingCanonicalPosition(0, 17000.0, 0), seed, 0.0f, 1.0f, 3000.0f, false, kind, false, flag, duration);

    void Envelope()
    {
        LightningVisualPolicy.Reset();
        RingLightningEvent e = Flagged(1.9f);
        Check(e.LifeSeconds == 1.9f && Flagged(0.0f).LifeSeconds == RingLightningEvent.LifetimeSeconds, "LifeSeconds default");
        foreach (float duration in new[] { 0.85f, 1.2f, 1.6f, 1.85f })
            foreach (int seed in new[] { 1, 2, 3, 4, 5, 99, 12345 })
            {
                RingLightningEvent ev = Flagged(duration, seed);
                int repulses = RingLightningEvent.FieldStrikeRepulses(seed);
                Check(repulses is >= 1 and <= 3, "repulse count");
                float peak = 0.0f, last = 0.0f, maxStep = 0.0f;
                int peaks = 0; bool rising = false; float prev = 0.0f;
                for (double age = 0.0; age <= duration + 0.1; age += 1.0 / 240.0)
                {
                    float p = ev.PulseAt(ev.StartTimeSeconds + age);
                    if (age > duration) Check(p == 0.0f, "pulse after the duration");
                    peak = Math.Max(peak, p);
                    if (p > prev + 1e-4f) rising = true; else if (p < prev - 1e-4f && rising && prev > 0.42f) { peaks++; rising = false; }
                    prev = p; last = p;
                }
                Check(peak > 0.95f && peak <= 1.0f, $"full peak {peak}");
                Check(peaks >= repulses, $"seed {seed} D={duration}: {peaks} strokes for {repulses} re-pulses");
                Check(last == 0.0f, "pulse not zero at the end");
                // Reduced flashes: one slow swell, peak 0.4, no 60 fps frame step above 0.05.
                LightningVisualPolicy.Apply(1.0f, 1.0f, true);
                float reducedPeak = 0.0f, step = 0.0f, before = 0.0f;
                for (double age = 0.0; age <= duration + 0.05; age += 1.0 / 60.0)
                {
                    float p = ev.PulseAt(ev.StartTimeSeconds + age);
                    reducedPeak = Math.Max(reducedPeak, p); step = Math.Max(step, Math.Abs(p - before)); before = p;
                }
                LightningVisualPolicy.Reset();
                Check(reducedPeak <= LightningVisualPolicy.ReducedPeakScale + 1e-4f && reducedPeak > 0.2f, $"reduced peak {reducedPeak}");
                Check(step <= 0.05f, $"reduced frame step {step:F3} (seed {seed}, D={duration})");
                maxStep = Math.Max(maxStep, step);
            }
        // An ordinary event is unchanged: the envelope is PulseAtAge and it ends at 0.72 s.
        RingLightningEvent plain = Flagged(0.0f, 5, false, 3);
        Check(plain.PulseAt(100.0 + 0.5) == RingLightningEvent.PulseAtAge(0.5) && plain.PulseAt(100.0 + 0.8) == 0.0f, "ordinary events changed");
        Check(RingLightningEvent.FieldStrikeRiseSeconds(0.85) is >= 0.049 and <= 0.26 && RingLightningEvent.FieldStrikeRiseSeconds(1.85) is >= 0.44 and <= 0.46, "rise range");
        Check(Flagged(1.5f).FieldLeaderProgress(100.0) == 0.0f && Flagged(1.5f).FieldLeaderProgress(101.0) == 1.0f, "leader progress");
    }

    void Determinism()
    {
        MegastormModel a = NewModel(), b = NewModel();
        var la = new List<MegastormFieldStrike>(); var lb = new List<MegastormFieldStrike>(); var part = new List<MegastormFieldStrike>(); var tmp = new List<MegastormFieldStrike>();
        MegastormFieldStrikes.FieldStrikesInWindow(a, 100.0, 7300.0, 1.0f, la);
        MegastormFieldStrikes.FieldStrikesInWindow(b, 100.0, 7300.0, 1.0f, lb);
        Check(la.Count > 1000 && la.SequenceEqual(lb), "two models of one seed differ");
        MegastormFieldStrikes.FieldStrikesInWindow(a, 100.0, 3000.0, 1.0f, tmp); part.AddRange(tmp);
        MegastormFieldStrikes.FieldStrikesInWindow(a, 3000.0, 7300.0, 1.0f, tmp); part.AddRange(tmp);
        Check(la.SequenceEqual(part), "window split changes the stream");
        Check(la.Select(s => s.Id).Distinct().Count() == la.Count, "field strike ids are not unique");
        var flashIds = new List<long>();
        MegastormLightning.FlashIdsInWindow(a, 100.0, 7300.0, flashIds);
        Check(!flashIds.Intersect(la.Select(s => s.Id)).Any(), "field strike id collides with a flash id");
        var scythes = new List<MegastormScythe>();
        MegastormScythes.ScythesInWindow(a, 100.0, 400.0, 0.0, 0.0, double.PositiveInfinity, 1.0f, scythes);
        Check(!scythes.Select(s => s.Id).Intersect(la.Select(s => s.Id)).Any(), "field strike id collides with a scythe id");
        MegastormState? state = MegastormDocument.FromState(a.State, false).ToState();
        Check(state != null, "document round trip failed");
        if (state != null)
        {
            MegastormFieldStrikes.FieldStrikesInWindow(new MegastormModel(state), 100.0, 7300.0, 1.0f, tmp);
            Check(la.SequenceEqual(tmp), "save/load changes the stream");
        }
        MegastormLightning.FieldStrikesInWindow(a, 100.0, 7300.0, 1.0f, tmp);
        Check(la.SequenceEqual(tmp), "MegastormLightning.FieldStrikesInWindow differs");
        // Different seed, different stream.
        MegastormFieldStrikes.FieldStrikesInWindow(NewModel(5_000_000.0, 8), 100.0, 7300.0, 1.0f, tmp);
        Check(!la.Select(s => s.StartTimeSeconds).SequenceEqual(tmp.Select(s => s.StartTimeSeconds)), "seed does not change the stream");
        // Events are pure too.
        Check(la.All(s => MegastormFieldStrikes.ToEvent(s) == MegastormFieldStrikes.ToEvent(s)), "ToEvent not pure");
        Info($"{la.Count} strikes in 7200 s ({la.Count / 120.0:F1}/min), deterministic across models, windows and save/load");
    }

    void Gating()
    {
        var list = new List<MegastormFieldStrike>();
        var arcs = new List<MegastormContactArc>();
        MegastormModel m = NewModel();
        MegastormFieldCoupling.FieldUndersideProvider = null;
        Check(MegastormFieldStrikes.FieldStrikesInWindow(m, 100.0, 3000.0, 1.0f, list) == 0, "strikes with a null provider");
        Check(MegastormFieldStrikes.ContactArcsInWindow(m, 100.0, 300.0, 1.0f, arcs) == 0, "arcs with a null provider");
        Check(double.IsNaN(MegastormFieldStrikes.GapMeters(100000.0)) && MegastormFieldStrikes.RateBetween(0, Width) == 0.0, "gap / rate with a null provider");
        MegastormFieldCoupling.FieldUndersideProvider = Provider(10000.0);
        Check(MegastormFieldStrikes.FieldStrikesInWindow(new MegastormModel(), 100.0, 3000.0, 1.0f, list) == 0, "strikes without a storm");
        Check(MegastormFieldStrikes.FieldStrikesInWindow(m, 100.0, 3000.0, 0.0f, list) == 0, "strikes at scale 0");
        // Hazard query fields.
        Check(!double.IsNaN(MegastormFieldStrikes.GapMeters(159000.0)) && Math.Abs(MegastormFieldStrikes.GapMeters(159000.0) - 22000.0) < 1.0, $"centre gap {MegastormFieldStrikes.GapMeters(159000.0)}");
        double qAround = MegastormModel.WrapPositive(m.AroundAt(2000.0) - 5000.0, m.Settings.CircumferenceMeters);
        MegastormHazards Hazard() => MegastormHazardMath.Evaluate(m, qAround, 159000.0, 2000.0, m.Xi(qAround, 159000.0, 2000.0), m.Evaluate(2000.0), MegastormObserver.Open);
        MegastormHazards h = Hazard();
        Check(h.FieldStrikeRatePerSecond > 0.0f && Math.Abs(h.FieldGapMeters - 22000.0f) < 1.0f, $"hazard query rate {h.FieldStrikeRatePerSecond} gap {h.FieldGapMeters}");
        MegastormFieldCoupling.FieldUndersideProvider = null;
        h = Hazard();
        Check(h.FieldStrikeRatePerSecond == 0.0f && float.IsNaN(h.FieldGapMeters), "hazard query without a provider");
        MegastormFieldCoupling.FieldUndersideProvider = Provider(10000.0);
    }

    void RateLaw()
    {
        MegastormModel m = NewModel();
        var list = new List<MegastormFieldStrike>();
        const double T0 = 100.0, Span = 72000.0;
        MegastormFieldStrikes.FieldStrikesInWindow(m, T0, T0 + Span, 1.0f, list);
        double active = 0.0;
        for (long slot = (long)(T0 / 0.5); slot < (long)((T0 + Span) / 0.5); slot++) if (m.Evaluate(slot * 0.5).Active) active += 0.5;
        Check(active > 0.95 * Span, $"storm active only {active:F0} s of {Span}");
        double[] centres = { 12000.0, 20000.0, 40000.0, 80000.0, 159000.0 };
        foreach (double c in centres)
        {
            double lo = c - 7000.0, hi = c + 7000.0;
            double expected = MegastormFieldStrikes.RateBetween(lo, hi) * active;
            int count = list.Count(s => s.AcrossMeters >= lo && s.AcrossMeters < hi);
            double error = (count - expected) / expected;
            Check(Math.Abs(error) < 0.10, $"across {c / 1000:F0} km: {count} strikes vs {expected:F1} expected ({error * 100:F1}%)");
            Info($"rate law across {c / 1000:F0} km +-7: {count} vs {expected:F1} ({error * 100:+0.0;-0.0}%), {count / active * 60.0 / 14.0:F3}/min/km");
        }
        double totalExpected = MegastormFieldStrikes.RateBetween(0.0, Width) * 60.0;
        double perMinute = list.Count / active * 60.0;
        Check(Math.Abs(perMinute - totalExpected) < 0.10 * totalExpected && perMinute is > 16.0 and < 20.0, $"total {perMinute:F2}/min vs {totalExpected:F2} expected (about 18)");
        Info($"total {perMinute:F2}/min (law {totalExpected:F2}) over {active / 3600.0:F1} h");
        // The query integrates +-30 km of wall.
        double q = MegastormFieldStrikes.FieldStrikeRatePerSecond(80000.0, Width);
        Check(Math.Abs(q - MegastormFieldStrikes.RateBetween(50000.0, 110000.0)) < 1e-6, "FieldStrikeRatePerSecond window");
        // Strike-rate scale: proportional counts (Lightning frequency, Reduced flashes).
        var half = new List<MegastormFieldStrike>(); var twice = new List<MegastormFieldStrike>();
        MegastormFieldStrikes.FieldStrikesInWindow(m, T0, T0 + 7200.0, 0.5f, half);
        MegastormFieldStrikes.FieldStrikesInWindow(m, T0, T0 + 7200.0, 2.0f, twice);
        int one = list.Count(s => s.StartTimeSeconds < T0 + 7200.0);
        Check(Math.Abs(half.Count / (double)one - 0.5) < 0.07 && Math.Abs(twice.Count / (double)one - 2.0) < 0.2, $"scaled counts {half.Count} / {one} / {twice.Count}");
        Info($"scale 0.5 / 1 / 2 over 2 h: {half.Count} / {one} / {twice.Count}");
    }

    void Geometry()
    {
        foreach ((double wallTop, string name, double band) in new[] { (10000.0, "certified", 6200.0), (10400.0, "runtime_surface", 5800.0) })
        {
            MegastormFieldCoupling.FieldUndersideProvider = Provider(wallTop);
            Func<double, double> h = Provider(wallTop);
            MegastormModel m = NewModel();
            var list = new List<MegastormFieldStrike>();
            MegastormFieldStrikes.FieldStrikesInWindow(m, 100.0, 7300.0, 1.0f, list);
            Check(list.Count > 1000, name + ": too few strikes");
            double minRim = double.MaxValue, maxBelow = 0.0, minBelow = double.MaxValue, minLength = double.MaxValue;
            foreach (MegastormFieldStrike s in list)
            {
                double field = h(s.AcrossMeters), below = field - s.ContactAltitudeMeters;
                minBelow = Math.Min(minBelow, below); maxBelow = Math.Max(maxBelow, below);
                minRim = Math.Min(minRim, Math.Min(s.AcrossMeters, Width - s.AcrossMeters));
                minLength = Math.Min(minLength, s.ContactAltitudeMeters - s.BaseAltitudeMeters);
                Check(s.GapMeters >= MegastormFieldStrikes.MinGapMeters && Math.Abs(s.GapMeters - MegastormFieldStrikes.GapMeters(s.AcrossMeters)) < 1e-6, name + ": a strike under the 0.5 km gap");
                Check(s.DurationSeconds is >= 0.849f and <= 1.851f && s.Energy is >= 0.599f and <= 1.001f, name + ": duration / energy range");
                Check(s.BaseAltitudeMeters > 5000.0 && s.AcrossMeters > 0.0 && s.AcrossMeters < Width, name + ": base / across");
                RingLightningEvent e = MegastormFieldStrikes.ToEvent(s);
                Check(e.FieldStrike && e.SystemKind == MegastormModel.KindMegastorm && e.EventId == s.Id && e.LifeSeconds == s.DurationSeconds
                    && e.GroundPosition.VerticalMeters == s.ContactAltitudeMeters && e.CloudPosition.VerticalMeters == s.BaseAltitudeMeters, name + ": event convention");
            }
            Check(minBelow >= 0.0 && maxBelow <= 100.0, $"{name}: channels end {minBelow:F0}..{maxBelow:F0} m below H (want 0..100)");
            Check(minRim > band, $"{name}: a strike {minRim:F0} m from a rim (contact arcs only inside {band:F0} m)");
            Info($"{name}: {list.Count} strikes end {minBelow:F0}-{maxBelow:F0} m below H, nearest rim {minRim:F0} m, shortest channel {minLength:F0} m");
        }
        MegastormFieldCoupling.FieldUndersideProvider = Provider(10000.0);
    }

    void Arcs()
    {
        MegastormModel m = NewModel();
        var arcs = new List<MegastormContactArc>();
        const double Span = 3600.0;
        MegastormFieldStrikes.ContactArcsInWindow(m, 100.0, 100.0 + Span, 1.0f, arcs);
        double edge = MegastormFieldCoupling.BandEdgeMeters(Width);
        int left = arcs.Count(a => a.Side == 0), right = arcs.Count(a => a.Side == 1);
        Check(left / Span is > 1.2 and < 2.4 && right / Span is > 1.2 and < 2.4, $"arc rate per side {left / Span:F2} / {right / Span:F2} per second (about 2)");
        Func<double, double> h = Provider(10000.0);
        foreach (MegastormContactArc a in arcs)
        {
            double length = a.ContactAltitudeMeters - a.BaseAltitudeMeters, rim = Math.Min(a.AcrossMeters, Width - a.AcrossMeters);
            Check(length is >= 49.9 and <= 500.1, $"arc length {length}");
            Check(a.DurationSeconds is >= 0.199f and <= 0.501f, $"arc duration {a.DurationSeconds}");
            Check(rim <= edge + 1.0 && MegastormFieldStrikes.IsContactArcId(a.Id) && a.ContactAltitudeMeters <= h(a.AcrossMeters), "arc position / id");
            RingLightningEvent e = MegastormFieldStrikes.ToEvent(a);
            Check(e.IsFieldArc && e.FieldStrike, "arc event flags");
        }
        var again = new List<MegastormContactArc>();
        MegastormFieldStrikes.ContactArcsInWindow(NewModel(), 100.0, 100.0 + Span, 1.0f, again);
        Check(arcs.SequenceEqual(again) && arcs.Select(a => a.Id).Distinct().Count() == arcs.Count, "arc stream not deterministic / ids not unique");
        // An arc lights the contact ripples through FlashLevel (and only through it).
        MegastormContactArc arc = arcs[arcs.Count / 2];
        double during = MegastormFieldStrikes.ContactArcFlash(m, arc.AroundMeters, arc.AcrossMeters, arc.StartTimeSeconds + 0.5 * arc.DurationSeconds);
        double after = MegastormFieldStrikes.ContactArcFlash(m, arc.AroundMeters + 200000.0, arc.AcrossMeters, arc.StartTimeSeconds + 0.5 * arc.DurationSeconds);
        Check(during > 0.05 && after == 0.0, $"arc flash {during} / far point {after}");
        // The publisher puts out contact ripples and bursts only: no FieldStrike kind from arcs.
        var sink = new StubAdapter();
        var publisher = new MegastormFieldPublisher(m, sink);
        var camera = new RingCanonicalPosition(arc.AroundMeters, 0.0, arc.AcrossMeters);
        for (double t = arc.StartTimeSeconds - 4.0; t <= arc.StartTimeSeconds + 4.0; t += 0.1) publisher.Update(t, camera);
        Check(sink.Items.Count > 0 && sink.Items.All(d => d.Kind is ContainmentDisturbanceKind.MegastormContact or ContainmentDisturbanceKind.UpdraftBurst), "arcs published a disturbance kind of their own");
        bool lit = sink.Items.Any(d => d.Kind == ContainmentDisturbanceKind.MegastormContact && d.FlashLevel != null && d.FlashLevel(arc.StartTimeSeconds + 0.5 * arc.DurationSeconds) > 0.0f);
        Info($"{arcs.Count} arcs in {Span} s ({left / Span:F2} + {right / Span:F2}/s), band edge {edge:F0} m, a MegastormContact ripple lit by an arc at the arc time: {lit}");
        LightningVisualPolicy.Apply(1.0f, 1.0f, true);
        var reduced = new List<MegastormContactArc>();
        MegastormFieldStrikes.ContactArcsInWindow(m, 100.0, 100.0 + Span, LightningVisualPolicy.NaturalStrikeChanceScale, reduced);
        LightningVisualPolicy.Reset();
        Check(Math.Abs(reduced.Count / (double)arcs.Count - 0.5) < 0.1, "reduced flashes does not halve the arcs");
    }

    void Budget()
    {
        foreach (float scale in new[] { 1.0f, 2.0f })
        {
            MegastormModel m = NewModel();
            var strikes = new List<MegastormFieldStrike>();
            MegastormFieldStrikes.FieldStrikesInWindow(m, 600.0, 1200.0, scale, strikes);
            var sink = new StubAdapter();
            var events = strikes.Select(s => MegastormFieldStrikes.ToEvent(s)).ToList();
            // Pump every frame at 30 fps like the adapter polls ActiveLightningEvents: each event is live for its own duration.
            for (double t = 600.0; t <= 1203.0; t += 1.0 / 30.0)
                sink.Pump(events.Where(e => t >= e.StartTimeSeconds && t - e.StartTimeSeconds <= e.LifeSeconds).ToList());
            Check(sink.Items.Count == strikes.Count && sink.Items.Select(d => d.SourceId).OrderBy(x => x).SequenceEqual(strikes.Select(s => s.Id).OrderBy(x => x)), "one disturbance per strike with SourceId = EventId");
            Check(sink.Items.All(d => d.Kind == ContainmentDisturbanceKind.FieldStrike), "kind");
            Check(sink.Duplicates > 0 && sink.Items.Select(d => d.SourceId).Distinct().Count() == sink.Items.Count, "duplicate polling was not deduplicated");
            int maxPerSecond = 0, maxLive = 0;
            for (double t = 600.0; t < 1200.0; t += 0.25)
            {
                maxPerSecond = Math.Max(maxPerSecond, strikes.Count(s => s.StartTimeSeconds >= t && s.StartTimeSeconds < t + 1.0));
                maxLive = Math.Max(maxLive, strikes.Count(s => s.StartTimeSeconds <= t && t < s.StartTimeSeconds + s.DurationSeconds));
            }
            Info($"10 min x{scale}: {strikes.Count} strikes, max {maxPerSecond} starts in 1 s (cap 2/s), max {maxLive} live (4 reserved slots)");
            Check(maxPerSecond <= 2 && maxLive <= 4, $"field bus budget exceeded: {maxPerSecond}/s, {maxLive} live");
        }
    }

    void Cost()
    {
        // CPU cost of the per-frame scheduling (the layer calls these two windows every frame while a megastorm is triggered and a field exists).
        MegastormModel m = NewModel();
        var strikes = new List<MegastormFieldStrike>(); var arcs = new List<MegastormContactArc>();
        for (int i = 0; i < 20; i++) { MegastormFieldStrikes.FieldStrikesInWindow(m, 1000.0 + i, 1002.0 + i, 1.0f, strikes); MegastormFieldStrikes.ContactArcsInWindow(m, 1000.0 + i, 1000.5 + i, 1.0f, arcs); }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        const int Frames = 2000;
        for (int i = 0; i < Frames; i++)
        {
            double now = 1000.0 + i / 30.0;
            MegastormFieldStrikes.FieldStrikesInWindow(m, now - RingLightningEvent.FieldStrikeMaxSeconds, Math.BitIncrement(now), 1.0f, strikes);
            MegastormFieldStrikes.ContactArcsInWindow(m, now - 0.5, Math.BitIncrement(now), 1.0f, arcs);
        }
        double micros = watch.Elapsed.TotalMilliseconds * 1000.0 / Frames;
        Check(micros < 150.0, $"scheduling costs {micros:F1} us per frame");
        Info($"scheduling cost {micros:F1} us per frame (strike window 1.85 s + arc window 0.5 s, scale 1)");
        MegastormFieldCoupling.FieldUndersideProvider = null;
        watch.Restart();
        for (int i = 0; i < Frames; i++) { MegastormFieldStrikes.FieldStrikesInWindow(m, 1000.0, 1002.0, 1.0f, strikes); MegastormFieldStrikes.ContactArcsInWindow(m, 1000.0, 1000.5, 1.0f, arcs); }
        Info($"no provider: {watch.Elapsed.TotalMilliseconds * 1000.0 / Frames:F3} us per frame");
        MegastormFieldCoupling.FieldUndersideProvider = Provider(10000.0);
    }

    void SharedConvention()
    {
        // A natural kind 3 event with the flag (1 km channel) is selected and built exactly like a megastorm one.
        RingLightningEvent natural = new(900, 5, 50.0, new RingCanonicalPosition(1000.0, 18000.0, 40000.0), new RingCanonicalPosition(1000.0, 17000.0, 40000.0),
            4242, 0.0f, 0.8f, 3000.0f, false, 3, false, true, 1.1f);
        RingLightningEvent mega = natural with { EventId = 901, SystemKind = 6, SystemId = MegastormModel.WallRecordId };
        var events = new List<RingLightningEvent>(); var weights = new List<float>();
        double Distance(RingCanonicalPosition p) => Math.Abs(p.AroundMeters - 1000.0) + Math.Abs(p.AcrossMeters - 40000.0);
        int n = RingFieldStrikeLayer.SelectMain(new[] { natural, mega, natural with { EventId = 902, FieldStrike = false } }, null, 50.3, Distance, true, 4, events, weights);
        Check(n == 2 && events.All(e => e.FieldStrike) && weights.All(w => w == 1.0f), "SelectMain should take both flagged events (kinds 3 and 6) and ignore the unflagged one");
        var a = new List<ChannelSegment>(); var b = new List<ChannelSegment>();
        Vector3 baseW = new(0, 17000.0f, 0), contact = new(30.0f, 18000.0f, -20.0f);
        int na = LightningChannelSegments.BuildFieldStrikeChannel(baseW, contact, natural.ShapeSeed, false, 0, a);
        int nb = LightningChannelSegments.BuildFieldStrikeChannel(baseW, contact, mega.ShapeSeed, false, 0, b);
        Check(na > 50 && na == nb && a.SequenceEqual(b), "the builder differs by system kind");
        var trunk = a.Where(s => Math.Floor(s.AlongB) == 0.0).ToList();
        Check(trunk.Count > 8 && trunk[0].A.DistanceTo(baseW) < 0.01f && trunk[^1].B.DistanceTo(contact) < 0.01f, "the trunk does not run from the base to the contact point");
        Check(a.Count(s => Math.Floor(s.AlongB) == 2.0) > 6 && a.Any(s => Math.Floor(s.AlongB) == 1.0), "no terminal spray / fan");
        Check(a.All(s => s.AlongA >= 0.0f && s.AlongA <= 1.0f && s.Bolt == 0), "segment fields");
        // A long megastorm channel: the fan stays under 8 km wide and under the field.
        var tall = new List<ChannelSegment>();
        LightningChannelSegments.BuildFieldStrikeChannel(Vector3.Zero, new Vector3(0, 22000.0f, 0), 99, false, 0, tall);
        float maxSide = tall.Max(s => Math.Max(Math.Abs(s.A.X), Math.Abs(s.A.Z))), maxY = tall.Max(s => Math.Max(s.A.Y, s.B.Y));
        Check(tall.Count <= LightningChannelSegments.FieldMaxSegments && maxSide < 8000.0f && maxY <= 22000.5f, $"tall channel: {tall.Count} segments, side {maxSide:F0} m, top {maxY:F0} m");
        if (System.Environment.GetEnvironmentVariable("MEGASTORM_FIELD_SHAPE_DUMP") is { Length: > 0 } dump)
        {
            var rows = new List<string>();
            foreach (int shapeSeed in new[] { 11, 222, 3333 })
            {
                var segs = new List<ChannelSegment>();
                LightningChannelSegments.BuildFieldStrikeChannel(Vector3.Zero, new Vector3(0, 22000.0f, 0), shapeSeed, false, 0, segs);
                rows.Add("[" + string.Join(",", segs.Select(q => FormattableString.Invariant($"[{q.A.X:0.#},{q.A.Y:0.#},{q.A.Z:0.#},{q.B.X:0.#},{q.B.Y:0.#},{q.B.Z:0.#},{q.Intensity:0.##},{Math.Floor(q.AlongB):0}]"))) + "]");
            }
            File.WriteAllText(dump, "[" + string.Join(",", rows) + "]");
        }
        var arc = new List<ChannelSegment>();
        LightningChannelSegments.BuildFieldStrikeChannel(Vector3.Zero, new Vector3(0, 300.0f, 0), 7, true, 0, arc);
        Check(arc.Count > 8 && arc.Count < 200, $"arc segments {arc.Count}");
        // Natural unflagged events and pruning are unchanged: LifeSeconds default.
        Check(natural with { FieldStrike = false, DurationSeconds = 0.0f } is { } plain && plain.LifeSeconds == RingLightningEvent.LifetimeSeconds, "default life");
        Info($"flagged kind 3 / kind 6 events: same selection and the same {na}-segment channel; tall channel {tall.Count} segments, fan {maxSide:F0} m");
    }

    // ---- Staged run through the real layer ----
    static RingLightningEvent? Find(RingVolumetricCloudLayer layer, long id)
    {
        foreach (RingLightningEvent e in layer.ActiveLightningEvents) if (e.EventId == id) return e;
        return null;
    }

    public int Tick(Node layerNode)
    {
        var layer = (RingVolumetricCloudLayer)layerNode;
        Func<double, double>? savedProvider = MegastormFieldCoupling.FieldUndersideProvider;
        try
        {
            if (_wait > 0) { _wait--; return -1; }
            var explorer = layer.GetNode<RingVoxelExplorerController>(layer.ExplorerPath);
            RingCanonicalPosition observer = explorer.CanonicalPosition;
            switch (_stage++)
            {
                case 0:
                {
                    layer.CloudSimulationPaused = true;
                    MegastormFieldCoupling.FieldUndersideProvider = Provider(10000.0);
                    _model = NewModel(observer.AroundMeters + 20000.0, 33);
                    var list = new List<MegastormFieldStrike>();
                    MegastormFieldStrikes.FieldStrikesInWindow(_model, 1000.0, 3000.0, 1.0f, list);
                    list = list.Where(s => s.DurationSeconds >= 1.5f).ToList();
                    Check(list.Count > 0, "no long strike to stage (test is vacuous)");
                    if (list.Count == 0) return Finish("LAYER");
                    _target = list[0];
                    layer.MegastormSource = _model;
                    layer.CloudSimulationTimeSeconds = _target.StartTimeSeconds + 0.1;
                    _wait = 3; return -1;
                }
                case 1:
                {
                    MegastormFieldCoupling.FieldUndersideProvider = Provider(10000.0);
                    RingLightningEvent? e = Find(layer, _target.Id);
                    Check(e.HasValue, "the field strike did not appear");
                    if (e is RingLightningEvent ev)
                    {
                        _first = ev;
                        Check(ev.FieldStrike && ev.SystemKind == MegastormModel.KindMegastorm && ev.StartTimeSeconds == _target.StartTimeSeconds && ev.LifeSeconds == _target.DurationSeconds, "event fields / flag / duration");
                        Check(ev.GroundPosition.VerticalMeters > ev.CloudPosition.VerticalMeters + 100.0, "the contact point is not above the channel base");
                        Check(Provider(10000.0)(ev.GroundPosition.AcrossMeters) - ev.GroundPosition.VerticalMeters is >= 0.0 and <= 100.0, "contact not within 100 m below H");
                    }
                    Check(layer.ActiveLightningEvents.Where(x => x.FieldStrike).All(x => MegastormFieldStrikes.IsFieldStrikeId(x.EventId)), "a flagged event that is not a field strike");
                    Check(layer.ActiveContactArcs.All(x => x.IsFieldArc) && layer.ActiveLightningEvents.All(x => !x.IsFieldArc), "contact arcs leaked into ActiveLightningEvents");
                    // An ordinary (preview) event made now prunes at 0.72 s; the flagged strike lives on.
                    _t0 = layer.CloudSimulationTimeSeconds;
                    Godot.Collections.Dictionary preview = layer.TriggerSharedLightningPreviewAt(observer.AroundMeters, observer.AcrossMeters);
                    Check(preview.Count > 0, "no ordinary preview event could be made");
                    _wait = 2; return -1;
                }
                case 2:
                {
                    int ordinary = layer.ActiveLightningEvents.Count(x => !x.FieldStrike && x.IsPreview);
                    Check(ordinary > 0, "the ordinary event is not live");
                    layer.CloudSimulationTimeSeconds = _t0 + 0.8;   // preview age 0.8 > 0.72; strike age 0.9
                    _wait = 3; return -1;
                }
                case 3:
                {
                    Check(layer.ActiveLightningEvents.All(x => x.FieldStrike || !x.IsPreview), "the ordinary event outlived 0.72 s");
                    Check(Find(layer, _target.Id).HasValue && _target.DurationSeconds > 0.9f, "the flagged event was pruned at 0.72 s instead of its own duration");
                    layer.CloudSimulationTimeSeconds = _target.StartTimeSeconds + _target.DurationSeconds - 0.05;
                    _wait = 3; return -1;
                }
                case 4:
                {
                    Check(Find(layer, _target.Id).HasValue, "the flagged event is gone before its full duration");
                    var sink = new StubAdapter();
                    for (int i = 0; i < 5; i++) sink.Pump(layer.ActiveLightningEvents);
                    int flagged = layer.ActiveLightningEvents.Count(x => x.FieldStrike);
                    Check(sink.Items.Count == flagged && sink.Items.All(d => d.SourceId > 0 && layer.ActiveLightningEvents.Any(x => x.EventId == d.SourceId)), "one FieldStrike per flagged event, SourceId = EventId");
                    layer.CloudSimulationTimeSeconds = _target.StartTimeSeconds + _target.DurationSeconds + 0.1;
                    _wait = 3; return -1;
                }
                case 5:
                {
                    Check(!Find(layer, _target.Id).HasValue, "the flagged event outlived its duration");
                    layer.CloudSimulationTimeSeconds = _target.StartTimeSeconds + 0.1;   // rewind
                    _wait = 3; return -1;
                }
                case 6:
                {
                    MegastormFieldCoupling.FieldUndersideProvider = Provider(10000.0);
                    RingLightningEvent? e = Find(layer, _target.Id);
                    Check(e.HasValue && e.Value == _first, "rewind did not reproduce the identical field strike");
                    MegastormFieldCoupling.FieldUndersideProvider = null;
                    layer.CloudSimulationTimeSeconds = _target.StartTimeSeconds + 4.0;   // past its life
                    _wait = 2; return -1;
                }
                case 7:
                {
                    layer.CloudSimulationTimeSeconds = _target.StartTimeSeconds + 0.1;
                    _wait = 3; return -1;
                }
                case 8:
                {
                    Check(layer.ActiveLightningEvents.All(x => !x.FieldStrike) && layer.ActiveContactArcs.Count == 0, "field strikes with a null provider");
                    MegastormFieldCoupling.FieldUndersideProvider = Provider(10000.0);
                    layer.MegastormSource = null;
                    layer.CloudSimulationTimeSeconds = _target.StartTimeSeconds + 0.2;
                    _wait = 3; return -1;
                }
                case 9:
                {
                    Check(layer.ActiveLightningEvents.All(x => !x.FieldStrike), "field strikes without a megastorm source");
                    // Per-frame scheduling cost through the layer's own method (slot cache included), with a source and a field.
                    MegastormFieldCoupling.FieldUndersideProvider = Provider(10000.0);
                    layer.MegastormSource = _model;
                    var call = (Action<double>)Delegate.CreateDelegate(typeof(Action<double>), layer,
                        typeof(RingVolumetricCloudLayer).GetMethod("TryScheduleMegastormFieldStrikes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!);
                    for (int i = 0; i < 300; i++) call(2000.0 + i / 30.0);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    const int Frames = 6000;
                    for (int i = 0; i < Frames; i++) call(2010.0 + i / 30.0);
                    double micros = watch.Elapsed.TotalMilliseconds * 1000.0 / Frames;
                    Check(micros < 10.0, $"layer scheduling costs {micros:F2} us per frame (budget 10)");
                    Info($"layer scheduling cost {micros:F2} us per frame over {Frames} frames (slot cache; budget 10 us)");
                    layer.MegastormSource = null;
                    _wait = 2; return -1;
                }
                case 10:
                {
                    // World mapping probe: two points that differ only in altitude, 116 km along the ring from the explorer.
                    var grid = layer.GetNode<RingworldTechDemo.Voxel.Phase2.VoxelChunkGridManager>(layer.GridManagerPath);
                    foreach (double d in new[] { 0.0, 10000.0, 60000.0, 116000.0 })
                    {
                        Vector3 p0 = grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(observer.AroundMeters + d, 15000.0, observer.AcrossMeters));
                        Vector3 p1 = grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(observer.AroundMeters + d, 38000.0, observer.AcrossMeters));
                        Info($"world map d={d:0}: p0={p0} p1={p1} diff={(p1 - p0)} |diff|={(p1 - p0).Length():0}");
                    }
                    return Finish("LAYER");
                }
            }
            return Finish("LAYER");
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        finally
        {
            if (_stage >= 11 || _failures.Count > 0) MegastormFieldCoupling.FieldUndersideProvider = savedProvider;
        }
    }
}
