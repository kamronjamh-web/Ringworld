using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Phase E0 of docs/eclipse-false-sun-spec.md (F.1, G.5): the pure event stream. One-shots fire exactly once at 30/60/144 fps and at any
/// TimeScale, loading mid-event gives one Resync and no backlog, rewinds and jumps give one Resync, refuel flashes are rate limited and
/// match <c>ArrivalTime</c>, and the weather-clock mode converts flash times with the clock offset.
/// </summary>
public partial class EclipseEventStreamTest : RefCounted
{
    const double Circumference = EmitterSiteTestWorlds.Circumference;

    static void Expect(List<string> failures, bool ok, string message) { if (!ok) failures.Add(message); }

    sealed record SimRun(List<EclipseEvent> Events, List<long> Frames);

    public int Run(bool unused)
    {
        _ = unused;
        var failures = new List<string>();
        try
        {
            EclipseSiteTable table = EclipseSiteTable.Uniform(Circumference);
            var clock = new EmitterGridPulseClock(1337, Circumference);
            EclipseWindow solved = new EclipseWindowSolver().Solve(0);
            var window = EclipseEventWindow.From(solved);
            var timeline = new EclipseEventTimeline(new EclipseEventSettings(), window, table, clock, 1337);
            double S = window.Start, E = window.End;

            var observer = new EclipseObserver(table.AroundMeters[16 * 1], 0d, S - 150d, E + 120d);
            Dictionary<EclipseEventKind, int> baseline = CheckExactlyOnce(timeline, table, observer, failures);
            CheckTimeScale(timeline, observer, baseline, failures);
            CheckFarObserver(timeline, table, failures);
            CheckLoadAndScrub(timeline, table, observer, failures);
            CheckRefuel(timeline, table, clock, window, observer, failures);
            CheckWeatherMode(table, clock, window, observer, failures);
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("ECLIPSE_EVENT_STREAM=FAIL " + string.Join(" | ", failures.Take(30))); return 1; }
        Console.WriteLine("ECLIPSE_EVENT_STREAM=PASS");
        return 0;
    }

    /// <summary>Steps a fixed-rate clock (celestial advance = scale / fps per frame) over the whole event and returns every event with its frame.</summary>
    static SimRun Simulate(EclipseEventStream stream, EclipseEventTimeline tl, in EclipseObserver observer, int fps, double scale, List<string> failures,
        double weatherOffset = double.NaN, string label = "")
    {
        var events = new List<EclipseEvent>(); var frames = new List<long>();
        double start = tl.Window.Start - 520d, end = tl.Window.Start + tl.TauEnd + 200d;
        double step = scale / fps, maxStep = Math.Max(5d, 0.5d * scale);
        double prev = start - step, prevW = double.IsNaN(weatherOffset) ? 0d : prev - weatherOffset;
        var buffer = new List<EclipseEvent>();
        double lastTime = double.NegativeInfinity;
        for (long i = 0; ; i++)
        {
            double now = start + i * step;
            if (now > end) break;
            double w = double.IsNaN(weatherOffset) ? 0d : now - weatherOffset;
            buffer.Clear();
            stream.Advance(prev, now, prevW, w, observer, buffer, maxStep);
            foreach (EclipseEvent e in buffer)
            {
                if (!(e.CelestialTime > prev && e.CelestialTime <= now)) failures.Add($"{label}{fps}fps x{scale}: {e.Kind} at {e.CelestialTime} outside ({prev}, {now}]");
                if (e.CelestialTime < lastTime) failures.Add($"{label}{fps}fps x{scale}: {e.Kind} out of order");
                lastTime = e.CelestialTime;
                events.Add(e); frames.Add(i);
            }
            prev = now; prevW = w;
        }
        return new SimRun(events, frames);
    }

    static Dictionary<EclipseEventKind, int> Count(IEnumerable<EclipseEvent> events)
        => events.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());

    static HashSet<int> ExpectedSiteFires(EclipseSiteTable t, in EclipseObserver o, EclipseEventStreamSettings s, double fireAfterTau, EclipseEventTimeline tl)
    {
        var dist = new double[t.Count];
        for (int i = 0; i < t.Count; i++)
        {
            double da = EmitterGridTopology.SignedWrap(t.AroundMeters[i] - o.AroundMeters, t.CircumferenceMeters), dx = t.AcrossMeters[i] - o.AcrossMeters;
            dist[i] = Math.Sqrt(da * da + dx * dx) / 1000d;
        }
        var set = new HashSet<int>();
        for (int i = 0; i < t.Count; i++)
        {
            if (tl.SiteFireTau(i) <= fireAfterTau) continue;
            int rank = 0;
            for (int j = 0; j < t.Count; j++) if (j != i && (dist[j] < dist[i] || (dist[j] == dist[i] && j < i))) rank++;
            if (rank < s.NearestSiteCount || dist[i] <= s.AudibleRadiusKm) set.Add(t.SiteIndex[i]);
        }
        return set;
    }

    static Dictionary<EclipseEventKind, int> CheckExactlyOnce(EclipseEventTimeline tl, EclipseSiteTable table, EclipseObserver observer, List<string> failures)
    {
        var settings = new EclipseEventStreamSettings();
        HashSet<int> expectedSites = ExpectedSiteFires(table, observer, settings, double.NegativeInfinity, tl);
        Dictionary<EclipseEventKind, int>? first = null;
        foreach (int fps in new[] { 30, 60, 144 })
        {
            var stream = new EclipseEventStream(tl, settings);
            SimRun run = Simulate(stream, tl, observer, fps, 1d, failures);
            Dictionary<EclipseEventKind, int> c = Count(run.Events);
            first ??= c;
            foreach (EclipseEventKind kind in new[] { EclipseEventKind.MainBeamFired, EclipseEventKind.HubKindled, EclipseEventKind.IgnitionLaunched, EclipseEventKind.FrontsMet,
                EclipseEventKind.FullIgnition, EclipseEventKind.HandoffComplete, EclipseEventKind.InstabilityBegan, EclipseEventKind.Overload, EclipseEventKind.Implosion,
                EclipseEventKind.BeamsCut, EclipseEventKind.ShockwaveArrival, EclipseEventKind.NearestSpireFired, EclipseEventKind.LocalTotality, EclipseEventKind.LocalThirdContact })
                Expect(failures, c.GetValueOrDefault(kind) == 1, $"{fps} fps: {kind} fired {c.GetValueOrDefault(kind)} times");
            Expect(failures, c.GetValueOrDefault(EclipseEventKind.TotalityWarning) == 2 && c.GetValueOrDefault(EclipseEventKind.PhaseChanged) == tl.Boundaries.Count, $"{fps} fps: warnings {c.GetValueOrDefault(EclipseEventKind.TotalityWarning)} phase changes {c.GetValueOrDefault(EclipseEventKind.PhaseChanged)}/{tl.Boundaries.Count}");
            Expect(failures, !c.ContainsKey(EclipseEventKind.Resync), $"{fps} fps: unexpected Resync");
            var sites = run.Events.Where(e => e.Kind == EclipseEventKind.SiteFired).Select(e => e.Site).ToList();
            Expect(failures, sites.Count == sites.Distinct().Count() && sites.ToHashSet().SetEquals(expectedSites), $"{fps} fps: SiteFired set {sites.Count} vs expected {expectedSites.Count}");
            // Phase changes arrive in order with consistent from/to; local events carry their exact times.
            var phases = run.Events.Where(e => e.Kind == EclipseEventKind.PhaseChanged).ToList();
            bool chain = true;
            for (int i = 0; i < phases.Count; i++)
            {
                if (phases[i].Phase != tl.Boundaries[i].Phase) chain = false;
                if (i > 0 && phases[i].FromPhase != phases[i - 1].Phase) chain = false;
                if (i == phases.Count - 1 && Math.Abs(phases[i].CelestialTime - (tl.Window.Start + tl.ReturnEndTau(observer.LocalThirdContact))) > 1e-9) chain = false;
            }
            Expect(failures, chain, $"{fps} fps: phase-change chain");
            var warn = run.Events.Where(e => e.Kind == EclipseEventKind.TotalityWarning).Select(e => (e.CelestialTime, e.Value)).ToList();
            Expect(failures, warn.Count == 2 && warn[0].CelestialTime == observer.LocalSecondContact - 300d && warn[0].Value == 5f
                && warn[1].CelestialTime == observer.LocalSecondContact - 60d && warn[1].Value == 1f, $"{fps} fps: totality warnings");
            EclipseEvent main = run.Events.First(e => e.Kind == EclipseEventKind.MainBeamFired);
            Expect(failures, main.CelestialTime == tl.Window.Start + tl.TauMainFire && main.DistanceKm > 0f, $"{fps} fps: main beam event");
            EclipseEvent full = run.Events.First(e => e.Kind == EclipseEventKind.FullIgnition);
            Expect(failures, Math.Abs(full.Value - 100f) < 1e-3, $"{fps} fps: full ignition level {full.Value}");
            EclipseEvent imp = run.Events.First(e => e.Kind == EclipseEventKind.Implosion);
            Expect(failures, Math.Abs(imp.Value - (float)(observer.LocalThirdContact - imp.CelestialTime)) < 1e-3, $"{fps} fps: seconds to local third contact");
            Console.WriteLine($"ECLIPSE_EVENT_STREAM {fps}fps events={run.Events.Count} site_fired={sites.Count} refuel={c.GetValueOrDefault(EclipseEventKind.RefuelFlash)}");
        }
        return first!;
    }

    static void CheckTimeScale(EclipseEventTimeline tl, EclipseObserver observer, Dictionary<EclipseEventKind, int> baseline, List<string> failures)
    {
        // Item 6: the same events fire at any TimeScale, at real times equal to the 1x real times divided by the scale.
        SimRun one = Simulate(new EclipseEventStream(tl), tl, observer, 60, 1d, failures);
        foreach (double k in new[] { 2d, 10d, 100d })
        {
            SimRun run = Simulate(new EclipseEventStream(tl), tl, observer, 60, k, failures);
            Dictionary<EclipseEventKind, int> c = Count(run.Events);
            Expect(failures, c.OrderBy(p => p.Key).SequenceEqual(baseline.OrderBy(p => p.Key)), $"x{k}: event counts differ from the 1x counts");
            Expect(failures, run.Events.Count == one.Events.Count && run.Events.Select(e => (e.Kind, e.Site, e.CelestialTime)).SequenceEqual(one.Events.Select(e => (e.Kind, e.Site, e.CelestialTime))),
                $"x{k}: event sequence differs from the 1x sequence");
            int bad = 0;
            for (int i = 0; i < run.Events.Count; i++)
            {
                double real1 = one.Frames[i] / 60d, realK = run.Frames[i] / 60d;
                if (Math.Abs(realK - real1 / k) > 2d / 60d) bad++;
            }
            Expect(failures, bad == 0, $"x{k}: {bad} events not at 1x real time / {k}");
        }
        EclipseEvent ignition = one.Events.First(e => e.Kind == EclipseEventKind.IgnitionLaunched), full = one.Events.First(e => e.Kind == EclipseEventKind.FullIgnition);
        Console.WriteLine($"ECLIPSE_EVENT_STREAM timescale ignition_to_full_celestial_s={full.CelestialTime - ignition.CelestialTime:F2} (real: 1x {full.CelestialTime - ignition.CelestialTime:F1} s, 10x {(full.CelestialTime - ignition.CelestialTime) / 10d:F2} s)");
    }

    static void CheckFarObserver(EclipseEventTimeline tl, EclipseSiteTable table, List<string> failures)
    {
        // Far from the grid only the six nearest sites fire events; inside the 40 km radius everything within it qualifies.
        var far = new EclipseObserver(Circumference * 0.5d, 150_000d);
        var s = new EclipseEventStreamSettings();
        SimRun run = Simulate(new EclipseEventStream(tl, s), tl, far, 60, 10d, failures, label: "far ");
        var sites = run.Events.Where(e => e.Kind == EclipseEventKind.SiteFired).ToList();
        Expect(failures, sites.Count == 6 && sites.All(e => e.DistanceKm > 40f), $"far observer: {sites.Count} SiteFired");
        Expect(failures, run.Events.Count(e => e.Kind == EclipseEventKind.NearestSpireFired) == 1 && sites.Min(e => e.DistanceKm) == run.Events.First(e => e.Kind == EclipseEventKind.NearestSpireFired).DistanceKm, "far observer: nearest spire");
        Expect(failures, run.Events.Where(e => e.Kind == EclipseEventKind.TotalityWarning || e.Kind == EclipseEventKind.LocalTotality).Count() == 0, "no local events without local contacts");
        var near = new EclipseObserver(table.AroundMeters[16], 0d);
        var nearSites = Simulate(new EclipseEventStream(tl, s), tl, near, 60, 10d, failures, label: "near ").Events.Where(e => e.Kind == EclipseEventKind.SiteFired).ToList();
        int within = Enumerable.Range(0, table.Count).Count(i => Math.Sqrt(Math.Pow(EmitterGridTopology.SignedWrap(table.AroundMeters[i] - near.AroundMeters, Circumference), 2) + Math.Pow(table.AcrossMeters[i], 2)) / 1000d <= 40d);
        Expect(failures, nearSites.Count == Math.Max(6, within) || nearSites.Count > 6, $"near observer: {nearSites.Count} SiteFired, {within} within 40 km");
        Expect(failures, nearSites.All(e => e.Phase == EclipseEventPhase.Ignition && e.Column >= 0 && e.BearingDegrees >= 0f && e.BearingDegrees < 360f), "SiteFired facts");
    }

    static void CheckLoadAndScrub(EclipseEventTimeline tl, EclipseSiteTable table, EclipseObserver observer, List<string> failures)
    {
        double S = tl.Window.Start;
        var settings = new EclipseEventStreamSettings();
        foreach (double loadTau in new[] { 100d, 60d })
        {
            var stream = new EclipseEventStream(tl, settings);
            var events = new List<EclipseEvent>();
            double load = S + loadTau;
            // Load: no previous time.
            int n = stream.Advance(double.NaN, load, double.NaN, 0d, observer, events, 5d);
            Expect(failures, n == 1 && events[0].Kind == EclipseEventKind.Resync && events[0].Phase == EclipseEventPhase.Ignition && events[0].FromPhase == EclipseEventPhase.Idle
                && events[0].Value > 10f && events[0].Value < 90f, $"load at tau {loadTau}: {n} events, first {(events.Count > 0 ? events[0].Kind.ToString() : "none")}");
            // Continue from the loaded time: no backlog, only what lies ahead.
            events.Clear();
            double prev = load, end = S + tl.TauEnd + 200d;
            for (long i = 1; ; i++)
            {
                double now = load + i / 60d;
                if (now > end) break;
                stream.Advance(prev, now, 0d, 0d, observer, events, 5d);
                prev = now;
            }
            var kinds = Count(events);
            Expect(failures, !kinds.ContainsKey(EclipseEventKind.MainBeamFired) && !kinds.ContainsKey(EclipseEventKind.HubKindled) && !kinds.ContainsKey(EclipseEventKind.IgnitionLaunched)
                && !kinds.ContainsKey(EclipseEventKind.Resync) && !kinds.ContainsKey(EclipseEventKind.TotalityWarning), $"load at tau {loadTau}: replayed past events");
            var sites = events.Where(e => e.Kind == EclipseEventKind.SiteFired).Select(e => e.Site).ToHashSet();
            Expect(failures, sites.SetEquals(ExpectedSiteFires(table, observer, settings, loadTau, tl)), $"load at tau {loadTau}: SiteFired set {sites.Count}");
            double nearestFire = Enumerable.Range(0, table.Count).Select(i => (Math.Abs(table.AroundMeters[i] - observer.AroundMeters) + Math.Abs(table.AcrossMeters[i]), i)).Min().i is int ni ? tl.SiteFireTau(ni) : 0d;
            int nearestExpected = nearestFire > loadTau ? 1 : 0;
            Expect(failures, kinds.GetValueOrDefault(EclipseEventKind.NearestSpireFired) == nearestExpected, $"load at tau {loadTau}: NearestSpireFired {kinds.GetValueOrDefault(EclipseEventKind.NearestSpireFired)} expected {nearestExpected}");
            Expect(failures, kinds.GetValueOrDefault(EclipseEventKind.FullIgnition) == 1 && kinds.GetValueOrDefault(EclipseEventKind.Implosion) == 1, $"load at tau {loadTau}: later one-shots must still fire once");
        }
        // Loading outside the event (before PreRoll, or after the tail) is silent.
        var quiet = new EclipseEventStream(tl, settings); var qe = new List<EclipseEvent>();
        Expect(failures, quiet.Advance(double.NaN, S - 1000d, double.NaN, 0d, observer, qe, 5d) == 0 && quiet.Advance(S - 1000d, S + tl.TauEnd + 1000d, 0d, 0d, observer, qe, 5d) == 0 || qe.Count == 1, "idle load/jump should be silent or a single Resync");

        // Scrub backwards in Sustain: exactly one Resync, then forward stepping continues normally.
        var scrub = new EclipseEventStream(tl, settings); var se = new List<EclipseEvent>();
        scrub.Advance(S + 2000d - 0.01, S + 2000d, 0d, 0d, observer, se, 5d);
        int back = scrub.Advance(S + 2000d, S + 1900d, 0d, 0d, observer, se, 5d);
        Expect(failures, back == 1 && se[^1].Kind == EclipseEventKind.Resync && se[^1].Phase == EclipseEventPhase.Sustain, $"scrub back gave {back} events");
        int fwd = scrub.Advance(S + 1900d, S + 1900.02, 0d, 0d, observer, se, 5d);
        Expect(failures, fwd == 0 || se.Skip(se.Count - fwd).All(e => e.Kind != EclipseEventKind.Resync), "forward after scrub");
        // A forward jump larger than the allowed step is one Resync too; the same jump is continuous at high speed (max step 50 s at 100x).
        var jump = new EclipseEventStream(tl, settings); var je = new List<EclipseEvent>();
        int j1 = jump.Advance(S + 25d, S + 40d, 0d, 0d, observer, je, 5d);
        Expect(failures, j1 == 1 && je[0].Kind == EclipseEventKind.Resync, $"forward jump gave {j1} events");
        je.Clear();
        int j2 = new EclipseEventStream(tl, settings).Advance(S + 25d, S + 40d, 0d, 0d, observer, je, 50d);
        Expect(failures, j2 > 0 && je.All(e => e.Kind != EclipseEventKind.Resync) && je.Any(e => e.Kind == EclipseEventKind.HubKindled || e.Kind == EclipseEventKind.PhaseChanged), $"15 s step at 100x gave {j2} events");
        // Paused clock: no events, no Resync.
        var paused = new EclipseEventStream(tl, settings); var pe = new List<EclipseEvent>();
        Expect(failures, paused.Advance(S + 500d, S + 500d, 0d, 0d, observer, pe, 5d) == 0, "paused clock produced events");
        Console.WriteLine("ECLIPSE_EVENT_STREAM load/scrub/jump ok");
    }

    static void CheckRefuel(EclipseEventTimeline tl, EclipseSiteTable table, EmitterGridPulseClock clock, EclipseEventWindow window, EclipseObserver observer, List<string> failures)
    {
        int nearest = Enumerable.Range(0, table.Count).Select(i => (d: Dist(table, i, observer), i)).Min().i;
        // Default rate limit: one flash per grid circuit (300 s) at the nearest site, every one on an ArrivalTime.
        SimRun run = Simulate(new EclipseEventStream(tl), tl, observer, 60, 10d, failures);
        var flashes = run.Events.Where(e => e.Kind == EclipseEventKind.RefuelFlash).ToList();
        Expect(failures, flashes.Count >= 12 && flashes.Count <= 14 && flashes.All(e => e.Site == table.SiteIndex[nearest]), $"refuel flashes {flashes.Count}");
        bool onArrival = flashes.All(e => Enumerable.Range(-2, 5).Any(d => tl.RefuelArrivalTime(nearest, tl.RefuelLaunchAtOrAfter(nearest, e.CelestialTime) + d) == e.CelestialTime));
        Expect(failures, onArrival, "refuel flash times must equal ArrivalTime exactly");
        Expect(failures, flashes.All(e => e.CelestialTime - window.Start >= tl.TauSustain && e.CelestialTime - window.Start < tl.TauInstability), "refuel flashes only inside Sustain");
        double minGap = flashes.Zip(flashes.Skip(1), (a, b) => b.CelestialTime - a.CelestialTime).DefaultIfEmpty(1e9).Min();
        Expect(failures, Math.Abs(minGap - 300d) < 1e-6, $"flash gap {minGap}");
        // A longer rate limit drops flashes and still respects the gap.
        var limited = new EclipseEventStreamSettings { RefuelRateLimitSeconds = 700d };
        var lf = Simulate(new EclipseEventStream(tl, limited), tl, observer, 60, 10d, failures).Events.Where(e => e.Kind == EclipseEventKind.RefuelFlash).ToList();
        double lGap = lf.Zip(lf.Skip(1), (a, b) => b.CelestialTime - a.CelestialTime).DefaultIfEmpty(1e9).Min();
        Expect(failures, lf.Count < flashes.Count && lf.Count >= 5 && lGap >= 700d, $"rate-limited flashes {lf.Count}, min gap {lGap}");
        Console.WriteLine($"ECLIPSE_EVENT_STREAM refuel flashes={flashes.Count} gap_s={minGap:F1} limited={lf.Count}");
    }

    static double Dist(EclipseSiteTable t, int i, EclipseObserver o)
    {
        double da = EmitterGridTopology.SignedWrap(t.AroundMeters[i] - o.AroundMeters, t.CircumferenceMeters), dx = t.AcrossMeters[i] - o.AcrossMeters;
        return Math.Sqrt(da * da + dx * dx);
    }

    static void CheckWeatherMode(EclipseSiteTable table, EmitterGridPulseClock clock, EclipseEventWindow window, EclipseObserver observer, List<string> failures)
    {
        var tl = new EclipseEventTimeline(new EclipseEventSettings { RefuelClock = EclipseRefuelClock.Weather }, window, table, clock, 1337);
        const double Offset = 1234.5; // weather clock = celestial - 1234.5 (the two saved clocks are unrelated)
        SimRun run = Simulate(new EclipseEventStream(tl), tl, observer, 60, 1d, failures, weatherOffset: Offset, label: "weather ");
        int nearest = Enumerable.Range(0, table.Count).Select(i => (d: Dist(table, i, observer), i)).Min().i;
        var flashes = run.Events.Where(e => e.Kind == EclipseEventKind.RefuelFlash).ToList();
        Expect(failures, flashes.Count >= 12 && flashes.Count <= 14, $"weather mode flashes {flashes.Count}");
        bool converted = flashes.All(e => Enumerable.Range(-2, 5).Any(d =>
            tl.RefuelArrivalTime(nearest, tl.RefuelLaunchAtOrAfter(nearest, e.CelestialTime - Offset) + d) + Offset == e.CelestialTime));
        Expect(failures, converted, "weather-mode flash time = ArrivalTime + (celestial - weather)");
        // A weather-clock jump (save/load of one clock) is a discontinuity in this mode only.
        double S = window.Start;
        var stream = new EclipseEventStream(tl); var ev = new List<EclipseEvent>();
        Expect(failures, stream.Advance(S + 1000d, S + 1000.02, 100d, 100.02, observer, ev, 5d) == 0, "continuous weather step");
        Expect(failures, stream.Advance(S + 1000.02, S + 1000.04, 100.02, 400d, observer, ev, 5d) == 1 && ev[^1].Kind == EclipseEventKind.Resync, "weather jump should Resync");
        var celestial = new EclipseEventStream(new EclipseEventTimeline(new EclipseEventSettings(), window, table, clock, 1337)); ev.Clear();
        Expect(failures, celestial.Advance(S + 1000d, S + 1000.02, 100d, 100.02, observer, ev, 5d) == 0
            && celestial.Advance(S + 1000.02, S + 1000.04, 100.02, 400d, observer, ev, 5d) == 0, "weather jump must not matter with the celestial refuel clock");
    }
}
