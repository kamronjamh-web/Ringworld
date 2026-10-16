using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Phase E0 of docs/eclipse-false-sun-spec.md (B, G.5): the pure event timeline. Phase boundaries, ignition and refuel schedules against
/// the real seed-1337 registry/topology/pulse clock, the brightness model, the dead-emitter seam, determinism (direct versus stepped,
/// bit-exact restore), and user decision 6: the schedule at TimeScale k is the 1x schedule with time divided by k.
/// </summary>
public partial class EclipseEventTimelineTest : RefCounted
{
    const string Format = "eclipse_event_timeline_golden_v1";
    const double Circumference = EmitterSiteTestWorlds.Circumference;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static void Expect(List<string> failures, bool ok, string message) { if (!ok) failures.Add(message); }

    sealed class MutableOutput : IEclipseSiteOutput
    {
        public readonly float[] Values;
        public ulong Revision { get; private set; } = 1UL;
        public MutableOutput(int n) { Values = Enumerable.Repeat(1f, n).ToArray(); }
        public float Output(int site) => Values[site];
        public void Set(int site, float v) { Values[site] = v; Revision++; }
    }

    public int Run(bool capture)
    {
        var failures = new List<string>();
        try
        {
            using RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen17(1337);
            using INaturalHeightSource source = EmitterSiteHeightSources.Create(sampler);
            EmitterSiteRegistry registry = EmitterSiteRegistry.GetOrBuild(EmitterSiteRecipe.Default(1337), source);
            EmitterGridTopology topology = EmitterGridTopology.Build(registry);
            EclipseSiteTable table = EclipseSiteTable.FromTopology(topology);
            var clock = new EmitterGridPulseClock(1337, Circumference);
            EclipseWindow solved = new EclipseWindowSolver().Solve(0);
            var window = EclipseEventWindow.From(solved);
            var timeline = new EclipseEventTimeline(new EclipseEventSettings(), window, table, clock, 1337);

            CheckTable(table, registry, topology, failures);
            CheckBoundaries(timeline, failures);
            CheckIgnition(timeline, table, clock, failures);
            CheckBrightness(timeline, failures);
            CheckRefuel(timeline, table, clock, failures);
            CheckCollapse(timeline, failures);
            CheckDeadSeam(table, clock, window, failures);
            CheckSettingsAndCompression(table, clock, window, failures);
            CheckDeterminism(timeline, table, clock, window, failures);
            CheckTimeScale(timeline, failures);
            CheckWeatherClock(table, clock, window, failures);
            CheckGolden(timeline, capture, failures);
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("ECLIPSE_EVENT_TIMELINE=FAIL " + string.Join(" | ", failures.Take(30))); return 1; }
        Console.WriteLine("ECLIPSE_EVENT_TIMELINE=PASS");
        return 0;
    }

    static void CheckTable(EclipseSiteTable t, EmitterSiteRegistry registry, EmitterGridTopology topology, List<string> failures)
    {
        Expect(failures, t.Count == 594 && t.Columns == 6 && t.MainSiteIndex == registry.MainIndex, $"table shape {t.Count}/{t.Columns}");
        var seen = new HashSet<int>();
        for (int i = 0; i < t.Count; i++)
        {
            seen.Add(t.SiteIndex[i]);
            Expect(failures, t.SiteIndex[i] != t.MainSiteIndex && t.P[i] > 0d && t.P[i] < Circumference && t.Column[i] == t.SiteIndex[i] % 6, $"table entry {i}");
            Expect(failures, t.P[i] == topology[topology.LinkOut(t.SiteIndex[i], t.Column[i])].P0, $"table P entry {i}");
        }
        Expect(failures, seen.Count == 594, "table sites not unique");
    }

    static void CheckBoundaries(EclipseEventTimeline tl, List<string> failures)
    {
        EclipseEventSettings s = tl.Settings;
        double e = tl.TauEnd;
        Expect(failures, tl.Active && tl.TimeCompression == 1d, "timeline inactive or compressed with the default window");
        Expect(failures, Math.Abs(e - 4090.14) < 0.05, $"event window length {e}");
        Expect(failures, tl.TauMainFire == 20d && tl.TauKindle == 24d && tl.TauKindled == 26.5 && tl.TauIgnition == 29.5, $"early boundaries {tl.TauMainFire}/{tl.TauKindle}/{tl.TauIgnition}");
        Expect(failures, Math.Abs(tl.IgnitionHalfRingSeconds - 150d) < 1e-6 && Math.Abs(tl.IgnitionSpeedMetersPerSecond - Circumference / 300d) < 1e-6, "MatchGrid ignition speed");
        Expect(failures, Math.Abs(tl.TauCrescendo - (29.5 + 150d + 1.5)) < 1.0, $"crescendo {tl.TauCrescendo}");
        Expect(failures, Math.Abs(tl.TauHandoff - tl.TauCrescendo - 8d) < 1e-9 && Math.Abs(tl.TauSustain - tl.TauHandoff - 2.5) < 1e-9, "crescendo/handoff durations");
        Expect(failures, Math.Abs(tl.TauInstability - (e - 75d)) < 1e-9 && Math.Abs(tl.TauOverload - (e - 30d)) < 1e-9 && Math.Abs(tl.TauImplosion - (e - 26d)) < 1e-9
            && Math.Abs(tl.TauShockwave - (e - 23.5)) < 1e-9 && Math.Abs(tl.TauAftermath - (e - 15.5)) < 1e-9, "collapse boundaries");
        Expect(failures, Math.Abs((tl.TauInstability - tl.TauSustain) - 3823d) < 3d, $"sustain length {tl.TauInstability - tl.TauSustain}");
        Expect(failures, tl.TauAftermath <= tl.TauEnd && tl.TauShockwave + tl.ShockwaveDuration <= tl.TauEnd, "collapse must end before E");
        double prev = double.NegativeInfinity;
        foreach (EclipsePhaseBoundary b in tl.Boundaries) { Expect(failures, b.StartTau >= prev, $"boundary order {b.Phase}"); prev = b.StartTau; }
        Expect(failures, tl.Boundaries.Count == 15 && tl.Boundaries[0].Phase == EclipseEventPhase.PreRoll && tl.Boundaries[0].StartTau == -300d
            && tl.Boundaries[^1].Phase == EclipseEventPhase.Idle && tl.Boundaries[^2].Phase == EclipseEventPhase.Return, "boundary list");
        // PhaseAt agrees with the boundaries on both sides.
        foreach (EclipsePhaseBoundary b in tl.Boundaries)
        {
            Expect(failures, tl.PhaseAt(b.StartTau) == b.Phase, $"PhaseAt({b.Phase}) at start");
            Expect(failures, tl.PhaseAt(b.StartTau - 1e-6) != b.Phase, $"PhaseAt before {b.Phase}");
        }
        Expect(failures, tl.PhaseAt(-301d) == EclipseEventPhase.Idle && tl.PhaseAt(-1d) == EclipseEventPhase.PreRoll && tl.PhaseAt(0d) == EclipseEventPhase.Darkness, "early phases");
        double thirdLocal = tl.Window.Start + e + 200d;
        Expect(failures, tl.PhaseAt(e + 100d, thirdLocal) == EclipseEventPhase.Return && tl.PhaseAt(e + 244d, thirdLocal) == EclipseEventPhase.Return
            && tl.PhaseAt(e + 246d, thirdLocal) == EclipseEventPhase.Idle, "Return ends 45 s after the local third contact");
        Expect(failures, tl.PhaseAt(e + 449d) == EclipseEventPhase.Return && tl.PhaseAt(e + 451d) == EclipseEventPhase.Idle, "default Return tail");
        Console.WriteLine($"ECLIPSE_EVENT window_s={e:F2} main_fire={tl.TauMainFire} kindled={tl.TauKindled} ignition={tl.TauIgnition} crescendo={tl.TauCrescendo:F2} handoff={tl.TauHandoff:F2} sustain={tl.TauSustain:F2} " +
            $"sustain_len_s={tl.TauInstability - tl.TauSustain:F1} instability={tl.TauInstability:F2} aftermath={tl.TauAftermath:F2} fronts_met={tl.TauFrontsMet:F2}");
        _ = s;
    }

    static void CheckIgnition(EclipseEventTimeline tl, EclipseSiteTable t, EmitterGridPulseClock clock, List<string> failures)
    {
        double c = Circumference, v = clock.SpeedMetersPerSecond, worst = 0d;
        int mismatched = 0;
        for (int i = 0; i < t.Count; i++)
        {
            double expected = 29.5 + Math.Min(t.P[i], c - t.P[i]) / v;
            worst = Math.Max(worst, Math.Abs(tl.SiteFireTau(i) - expected));
            if (Math.Abs(tl.SiteArrivalTau(i) - tl.SiteFireTau(i) - 1.5) > 1e-9) mismatched++;
        }
        Expect(failures, worst < 1e-3 && mismatched == 0, $"fire times differ from tau_ign + min(p, C-p)/v by {worst} s ({mismatched} climb mismatches)");

        // Fired count: monotone, equal to a brute-force count, 12 per 3 s on average, all 594 by the antipode.
        int prevCount = 0, badBrute = 0, nonMonotone = 0;
        for (double tau = 29d; tau <= 185d; tau += 0.37)
        {
            int count = tl.CountFired(tau);
            int brute = 0;
            for (int i = 0; i < t.Count; i++) if (tl.SiteFireTau(i) <= tau) brute++;
            if (count != brute) badBrute++;
            if (count < prevCount) nonMonotone++;
            prevCount = count;
        }
        Expect(failures, badBrute == 0 && nonMonotone == 0, $"CountFired brute mismatches {badBrute}, non-monotone {nonMonotone}");
        Expect(failures, tl.CountFired(29.49) == 0 && tl.CountFired(180d) == 594, $"CountFired bounds {tl.CountFired(29.49)}/{tl.CountFired(180d)}");
        double perThreeSeconds = (tl.CountFired(29.5 + 120d) - tl.CountFired(29.5 + 60d)) / 20d;
        Expect(failures, Math.Abs(perThreeSeconds - 12d) < 0.7, $"fires per 3 s {perThreeSeconds}");
        // Fire order is sorted, covers every entry once.
        ReadOnlySpan<int> order = tl.FireOrder; ReadOnlySpan<double> sorted = tl.SortedFireTau;
        bool sortedOk = true; var seen = new bool[t.Count];
        for (int k = 0; k < order.Length; k++) { if (k > 0 && sorted[k] < sorted[k - 1]) sortedOk = false; seen[order[k]] = true; if (sorted[k] != tl.SiteFireTau(order[k])) sortedOk = false; }
        Expect(failures, sortedOk && seen.All(b => b), "fire order");

        // Fronts: forward at v from the main spire, backward from C, meeting at C/2 at tau_ign + 150 s.
        EclipseEventState a = tl.Evaluate(tl.Window.Start + 29.5 + 60d, 0d);
        Expect(failures, a.FrontsActive && Math.Abs(a.ForwardHeadMeters - v * 60d) < 1e-6 && Math.Abs(a.BackwardHeadMeters - (c - v * 60d)) < 1e-6, "front positions");
        EclipseEventState m = tl.Evaluate(tl.Window.Start + 29.5 + 150.5, 0d);
        Expect(failures, Math.Abs(m.ForwardHeadMeters - c / 2d) < 1e-6 && Math.Abs(m.BackwardHeadMeters - c / 2d) < 1e-6 && m.MeetingFlare > 0.5, "fronts meet at the antipode with a flare");
        EclipseEventState gone = tl.Evaluate(tl.Window.Start + 29.5 + 152d, 0d);
        Expect(failures, !gone.FrontsActive && gone.MeetingFlare == 0d, "fronts end after the meeting flare");
        Expect(failures, tl.Evaluate(tl.Window.Start + 29.4, 0d).FrontsActive == false, "fronts before launch");
        // Main beam: tip accelerates (f = u^2), lit until the cut.
        EclipseEventState half = tl.Evaluate(tl.Window.Start + 22d, 0d);
        Expect(failures, half.MainBeamLit && Math.Abs(half.MainBeamTip - 0.25) < 1e-9 && tl.Evaluate(tl.Window.Start + 24.5, 0d).MainBeamTip == 1d && !tl.Evaluate(tl.Window.Start + 19.9, 0d).MainBeamLit, "main beam climb");
        // Handoff: regular beams stay lit until Handoff, then fade together; plateau and grid-pulse suppression follow.
        EclipseEventState pre = tl.Evaluate(tl.Window.Start + tl.TauHandoff - 0.1, 0d), mid = tl.Evaluate(tl.Window.Start + tl.TauHandoff + 1.25, 0d), post = tl.Evaluate(tl.Window.Start + tl.TauSustain + 0.1, 0d);
        Expect(failures, pre.LitRegularBeams == 594 && pre.RegularBeamIntensity == 1d && mid.RegularBeamIntensity > 0.4 && mid.RegularBeamIntensity < 0.6 && post.RegularBeamIntensity == 0d && post.LitRegularBeams == 0 && post.MainBeamLit, "handoff");
        Expect(failures, pre.GridPulseWeight == 0d && post.GridPulseWeight == 1d && tl.Evaluate(tl.Window.Start + 10d, 0d).GridPulseWeight == 1d && tl.Evaluate(tl.Window.Start + 21d, 0d).GridPulseWeight == 0d, "grid pulse suppression");
        Console.WriteLine($"ECLIPSE_EVENT ignition fire_err_s={worst:E2} fires_per_3s={perThreeSeconds:F2} fronts_met_tau={tl.TauFrontsMet:F2}");
    }

    static void CheckBrightness(EclipseEventTimeline tl, List<string> failures)
    {
        double S = tl.Window.Start;
        Expect(failures, tl.Evaluate(S + 19d, 0d).Level == 0d && tl.Evaluate(S + 23.9, 0d).Level == 0d, "level before kindle");
        Expect(failures, tl.Evaluate(S + 24.5, 0d).Level > 0d && tl.Evaluate(S + 24.5, 0d).Level < 0.1, "level during kindle ramp");
        Expect(failures, tl.Evaluate(S + 26.5, 0d).Level == 0.10d && tl.Evaluate(S + 29.4, 0d).Level == 0.10d && tl.Evaluate(S + 29.5, 0d).Level == 0.10d, "level after the kindle is 0.10");
        EclipseEventState k = tl.Evaluate(S + 26.5, 0d);
        Expect(failures, Math.Abs(k.HubDirect - 4d * 0.8 * 0.1) < 1e-12 && Math.Abs(k.HubDay - 0.08) < 1e-12 && k.HubWeight == 1d, $"kindle lighting {k.HubDirect}/{k.HubDay}");
        // Monotone through ignition, per-beam linear steps of +1.8 % per 3 s.
        double last = 0d; int dips = 0;
        for (double tau = 29.5; tau <= tl.TauCrescendo; tau += 0.05)
        {
            double l = tl.Evaluate(S + tau, 0d).Level;
            if (l < last - 1e-12) dips++;
            last = l;
        }
        Expect(failures, dips == 0, $"level not monotone through ignition ({dips} dips)");
        double step = (tl.Evaluate(S + 29.5 + 1.5 + 90d, 0d).Level - tl.Evaluate(S + 29.5 + 1.5 + 60d, 0d).Level) / 10d;
        Expect(failures, Math.Abs(step - 0.9 * 12d / 594d) < 0.003, $"ramp step per row {step} (expected {0.9 * 12d / 594d})");
        Expect(failures, tl.Evaluate(S + tl.TauCrescendo + 1d, 0d).Level > 0.99 && Math.Abs(tl.FullLevel - 1d) < 1e-12, $"full level {tl.FullLevel}");
        double after = tl.Evaluate(S + tl.TauCrescendo + tl.Settings.RampStepSmoothSeconds + 0.001, 0d).ArrivedWeight;
        Expect(failures, Math.Abs(after - 594d) < 1e-9, $"all beams arrived {after}");
        // Sustain: breath plus heartbeat around 1.0, heartbeat kicks follow the grid rows.
        double min = double.MaxValue, max = double.MinValue, maxHeart = 0d, minHeart = double.MaxValue;
        for (double tau = tl.TauSustain + 5d; tau < tl.TauInstability - 5d; tau += 0.1)
        {
            EclipseEventState s = tl.Evaluate(S + tau, 0d);
            min = Math.Min(min, s.Level); max = Math.Max(max, s.Level);
            maxHeart = Math.Max(maxHeart, s.Heartbeat); minHeart = Math.Min(minHeart, s.Heartbeat);
        }
        Expect(failures, min > 0.95 && max < 1.12 && maxHeart > 0.015 && maxHeart < 0.075 && minHeart >= 0d, $"sustain level {min}..{max}, heartbeat {minHeart}..{maxHeart}");
        EclipseEventState hand = tl.Evaluate(S + tl.TauCrescendo + 3d, 0d);
        Expect(failures, hand.Heartbeat == 0d, "no heartbeat before Sustain");
        // Instability flicker is deterministic, bounded and never raises the level.
        double minFlicker = 1d; bool detSame = true;
        for (double tau = tl.TauInstability; tau < tl.TauOverload; tau += 0.05)
        {
            EclipseEventState a = tl.Evaluate(S + tau, 0d), b = tl.Evaluate(S + tau, 12345d);
            if (a != b) detSame = false;
            minFlicker = Math.Min(minFlicker, a.Flicker);
            Expect(failures, a.Flicker <= 1d && a.Flicker >= 1d - tl.Settings.FlickerMax - 1e-12, $"flicker range {a.Flicker}");
        }
        Expect(failures, detSame && minFlicker < 0.8, $"flicker deterministic={detSame} min={minFlicker}");
        Console.WriteLine($"ECLIPSE_EVENT brightness step_per_row={step:F5} sustain_level={min:F4}..{max:F4} heartbeat_max={maxHeart:F4} flicker_min={minFlicker:F3}");
    }

    static void CheckRefuel(EclipseEventTimeline tl, EclipseSiteTable t, EmitterGridPulseClock clock, List<string> failures)
    {
        double S = tl.Window.Start;
        // Schedule times are exactly the grid clock's ArrivalTime.
        int exact = 0;
        foreach (int entry in new[] { 0, 7, 99, 200, 333, 593 })
            foreach (long n in new long[] { 0, 5, 1000, 123456 })
            {
                double expected = clock.ArrivalTime(clock.ToQ(t.P[entry]), t.Column[entry], n);
                if (tl.RefuelArrivalTime(entry, n) == expected) exact++;
            }
        Expect(failures, exact == 24, $"RefuelArrivalTime exact matches {exact}/24");
        for (int entry = 0; entry < t.Count; entry += 41)
        {
            double g = S + 2000d + entry;
            long n = tl.RefuelLaunchAtOrAfter(entry, g);
            double q = clock.ToQ(t.P[entry]);
            Expect(failures, tl.RefuelArrivalTime(entry, n) >= g && tl.RefuelArrivalTime(entry, n - 1) < g, $"launch search entry {entry}");
            // The flash peaks right after its arrival and is silent a few seconds before and after.
            double arrival = tl.RefuelArrivalTime(entry, n);
            double tauArrival = arrival - S;
            if (tauArrival > tl.TauSustain && tauArrival < tl.TauInstability)
            {
                double mid = tl.RefuelFlash(entry, tauArrival + 0.8, arrival + 0.8);
                Expect(failures, mid == 1d, $"entry {entry}: flash {mid} at 0.8 s");
                Expect(failures, tl.RefuelFlash(entry, tauArrival - 0.5, arrival - 0.5) == 0d && tl.RefuelFlash(entry, tauArrival + 4d, arrival + 4d) == 0d, $"entry {entry}: flash outside its window");
            }
            _ = q;
        }
        // A flash that would arrive before Sustain or during Instability is not counted.
        int before = 0, during = 0;
        for (int entry = 0; entry < t.Count; entry++)
        {
            long n = tl.RefuelLaunchAtOrAfter(entry, S + tl.TauSustain - 60d);
            for (int guard = 0; guard < 3; guard++, n++)
            {
                double arrival = tl.RefuelArrivalTime(entry, n);
                double tauA = arrival - S;
                if (tauA < tl.TauSustain && tauA > tl.TauSustain - 3d && tl.RefuelFlash(entry, tauA + 0.8, arrival + 0.8) != 0d) before++;
            }
            long m = tl.RefuelLaunchAtOrAfter(entry, S + tl.TauInstability - 5d);
            double arrivalI = tl.RefuelArrivalTime(entry, m);
            if (arrivalI - S >= tl.TauInstability && arrivalI - S < tl.TauInstability + 3d && tl.RefuelFlash(entry, arrivalI - S + 0.8, arrivalI + 0.8) != 0d) during++;
        }
        Expect(failures, before == 0 && during == 0, $"flashes outside Sustain counted: {before}/{during}");
        // Cadence: one row of six every ~3 s, so about 4.6 sites flash at any instant (each flash lasts 2.3 s).
        double sum = 0d; int samples = 0;
        for (double tau = tl.TauSustain + 100d; tau < tl.TauSustain + 700d; tau += 1.37)
        {
            int lit = 0;
            for (int i = 0; i < t.Count; i++) if (tl.RefuelFlash(i, tau, S + tau) > 0d) lit++;
            sum += lit; samples++;
        }
        double mean = sum / samples;
        Expect(failures, mean > 3.5 && mean < 5.6, $"mean simultaneous refuel flashes {mean}");
        // Each site flashes once per grid period (300 s).
        double period = 0d;
        { int e = 17; long n = tl.RefuelLaunchAtOrAfter(e, S + 2500d); period = tl.RefuelArrivalTime(e, n + 1) - tl.RefuelArrivalTime(e, n); }
        Expect(failures, Math.Abs(period - 300d) < 1e-9, $"flash period {period}");
        Console.WriteLine($"ECLIPSE_EVENT refuel mean_simultaneous={mean:F2} period_s={period:F1}");
    }

    static void CheckCollapse(EclipseEventTimeline tl, List<string> failures)
    {
        double S = tl.Window.Start;
        EclipseEventState over = tl.Evaluate(S + tl.TauImplosion - 1e-6, 0d);
        Expect(failures, Math.Abs(over.Level - 2.5) < 1e-6 && Math.Abs(over.HubDirect - 8d) < 1e-4 && over.HubDay == 1d, $"overload peak {over.Level}/{over.HubDirect}");
        EclipseEventState start = tl.Evaluate(S + tl.TauOverload, 0d);
        Expect(failures, Math.Abs(start.Level - 1d) < 1e-9, $"overload starts at the full level {start.Level}");
        EclipseEventState imp = tl.Evaluate(S + tl.TauImplosion + 1.25, 0d);
        Expect(failures, imp.Phase == EclipseEventPhase.Implosion && Math.Abs(imp.Level - 2.5 * 0.125) < 1e-9 && imp.BodyRadius > 0d, $"implosion level {imp.Level}");
        Expect(failures, tl.Evaluate(S + tl.TauShockwave, 0d).Level == 0d && tl.Evaluate(S + tl.TauShockwave, 0d).BodyRadius == 0d, "level and body vanish at the shockwave");
        Expect(failures, tl.Evaluate(S + tl.TauImplosion + 0.5, 0d).BeamCutProgress == 0.5 && tl.Evaluate(S + tl.TauImplosion + 1.1, 0d).BeamCutProgress == 1d && !tl.Evaluate(S + tl.TauImplosion + 1.1, 0d).MainBeamLit, "beam retract");
        EclipseEventState flash = tl.Evaluate(S + tl.TauShockArrival + 0.2, 0d), none = tl.Evaluate(S + tl.TauShockArrival + 0.5, 0d);
        Expect(failures, Math.Abs(flash.ShockFlash - 1d) < 1e-9 && Math.Abs(flash.HubDirect - 0.6) < 1e-9 && Math.Abs(flash.HubDay - 0.3) < 1e-9 && none.ShockFlash == 0d && none.HubDirect == 0d, $"shock flash {flash.HubDirect}/{flash.HubDay}");
        EclipseEventState aft = tl.Evaluate(S + tl.TauAftermath + 5d, 0d);
        Expect(failures, aft.Phase == EclipseEventPhase.Aftermath && aft.Level == 0d && aft.HubWeight == 1d && tl.Evaluate(S + tl.TauEnd + 1d, 0d).HubWeight == 0d && tl.Evaluate(S - 1d, 0d).HubWeight == 0d, "hub weight spans Darkness..Aftermath");
        EclipseEventState idle = tl.Evaluate(S - 1000d, 0d);
        Expect(failures, idle.Phase == EclipseEventPhase.Idle && idle.Level == 0d && idle.GridPulseWeight == 1d && !idle.Active, "idle state");
        // The level is zero outside the lit stages: darkness, shockwave, aftermath, return.
        Expect(failures, tl.Evaluate(S + 5d, 0d).Level == 0d && tl.Evaluate(S + tl.TauAftermath + 1d, 0d).Level == 0d && tl.Evaluate(S + tl.TauEnd + 10d, 0d).Level == 0d, "dark stages are dark");
    }

    static void CheckDeadSeam(EclipseSiteTable table, EmitterGridPulseClock clock, EclipseEventWindow window, List<string> failures)
    {
        var outputs = new MutableOutput(table.MainSiteIndex + 1);
        var tl = new EclipseEventTimeline(new EclipseEventSettings(), window, table, clock, 1337, outputs);
        Expect(failures, Math.Abs(tl.FullLevel - 1d) < 1e-12, "all-on full level");
        for (int i = 0; i < 10; i++) outputs.Set(table.SiteIndex[i * 55], 0f);
        double expected = 1d - 0.9 * 10d / 594d;
        Expect(failures, Math.Abs(tl.FullLevel - expected) < 1e-12, $"dead-site full level {tl.FullLevel} vs {expected}");
        double S = window.Start;
        Expect(failures, tl.CountFired(tl.TauCrescendo) == 584 && Math.Abs(tl.ArrivedWeight(tl.TauCrescendo + 1d) - 584d) < 1e-9, "dead sites never fire");
        EclipseEventState end = tl.Evaluate(S + tl.TauCrescendo + 1d, 0d);
        Expect(failures, Math.Abs(end.Level - expected) < 0.02, $"level at crescendo {end.Level} vs {expected}");
        // Dim, not slow: the ignition timing is unchanged (a dead site's slot is just empty).
        var full = new EclipseEventTimeline(new EclipseEventSettings(), window, table, clock, 1337);
        Expect(failures, tl.TauCrescendo == full.TauCrescendo && tl.SiteFireTau(5) == full.SiteFireTau(5), "dead sites must not change the ignition timing");
        Expect(failures, !tl.SiteAlive(0) && tl.SiteAlive(1), "SiteAlive");
        for (int i = 0; i < table.Count; i++)
        {
            if (tl.SiteAlive(i)) continue;
            Expect(failures, tl.RefuelFlash(i, tl.TauSustain + 10d, 0d) == 0d, "dead site flashed");
        }
        outputs.Set(table.SiteIndex[0], 1f);
        Expect(failures, tl.SiteAlive(0) && tl.CountFired(tl.TauCrescendo) == 585, "revision bump is re-read");
        Console.WriteLine($"ECLIPSE_EVENT dead_seam full_level={expected:F6}");
    }

    static void CheckSettingsAndCompression(EclipseSiteTable table, EmitterGridPulseClock clock, EclipseEventWindow window, List<string> failures)
    {
        // Sanitize: ranges and the collapse-fit rule.
        var wild = new EclipseEventSettings { PreRollSeconds = 99999d, KindleLevel = double.NaN, InstabilitySeconds = 300d, ShockwaveSeconds = 30d, CollapseLeadSeconds = 60d }.Sanitized();
        Expect(failures, wild.PreRollSeconds == 900d && wild.KindleLevel == 0.10d, "sanitize ranges");
        Expect(failures, wild.InstabilitySeconds + wild.OverloadSeconds + wild.ImplosionSeconds + wild.ShockwaveSeconds <= wild.CollapseLeadSeconds + 1e-9, "collapse stages fit in the lead");
        var d = new EclipseEventSettings();
        Expect(failures, d.Sanitized() == d, "defaults are already inside every range");
        // Manual ignition speed: 30 s to the antipode.
        var manual = new EclipseEventTimeline(new EclipseEventSettings { IgnitionSpeedMode = EclipseIgnitionSpeedMode.Manual, IgnitionSeconds = 30d }, window, table, clock, 1337);
        Expect(failures, Math.Abs(manual.IgnitionHalfRingSeconds - 30d) < 1e-9 && Math.Abs(manual.TauFrontsMet - 59.5) < 1e-9 && Math.Abs(manual.TauCrescendo - 61d) < 1.0, $"manual ignition {manual.TauFrontsMet}/{manual.TauCrescendo}");
        // Perceptual ramp is below PerBeam in the middle and reaches the same full level.
        var gamma = new EclipseEventTimeline(new EclipseEventSettings { RampMode = EclipseRampMode.Perceptual, RampGamma = 2d }, window, table, clock, 1337);
        var linear = new EclipseEventTimeline(new EclipseEventSettings(), window, table, clock, 1337);
        double tm = window.Start + 29.5 + 80d;
        Expect(failures, gamma.Evaluate(tm, 0d).Level < linear.Evaluate(tm, 0d).Level && Math.Abs(gamma.FullLevel - 1d) < 1e-12, "perceptual ramp");
        // A window too short for the stages compresses everything proportionally and still ends the collapse before E.
        var shortWindow = new EclipseEventWindow(0, window.Start, window.Start + 120d);
        var compressed = new EclipseEventTimeline(new EclipseEventSettings(), shortWindow, table, clock, 1337);
        Expect(failures, compressed.TimeCompression < 1d && compressed.TimeCompression > 0.1 && compressed.TauAftermath <= compressed.TauEnd + 1e-9
            && compressed.TauSustain <= compressed.TauInstability + 1e-9 && compressed.TauCrescendo > compressed.TauIgnition, $"compression {compressed.TimeCompression}");
        Expect(failures, Math.Abs(compressed.TauSustain + compressed.CollapseLead - compressed.TauEnd) < 0.01, $"compressed stages fill the window {compressed.TauSustain + compressed.CollapseLead}/{compressed.TauEnd}");
        // Disabled or invalid windows stay idle.
        var off = new EclipseEventTimeline(new EclipseEventSettings { EventEnabled = false }, window, table, clock, 1337);
        var bad = new EclipseEventTimeline(new EclipseEventSettings(), new EclipseEventWindow(0, double.NaN, double.NaN), table, clock, 1337);
        Expect(failures, !off.Active && off.Evaluate(window.Start + 100d, 0d).Phase == EclipseEventPhase.Idle && !bad.Active && bad.Evaluate(100d, 0d).Level == 0d, "disabled/invalid timelines are idle");
        Expect(failures, off.PhaseAt(100d) == EclipseEventPhase.Idle && off.Boundaries.Count == 0, "disabled boundaries");
    }

    static void CheckDeterminism(EclipseEventTimeline tl, EclipseSiteTable table, EmitterGridPulseClock clock, EclipseEventWindow window, List<string> failures)
    {
        // Direct evaluation equals 1/60 s stepping over 1.5 h: no accumulated state, so no drift.
        const double Dt = 1d / 60d;
        double start = window.Start - 310d, t = start, worst = 0d;
        int frames = 90 * 60 * 60, phaseMismatch = 0;
        var bounds = tl.Boundaries.Select(b => b.StartTau).Append(tl.TauShockArrival).ToArray();
        for (int i = 0; i < frames; i++, t += Dt)
        {
            double direct = start + i * Dt;
            EclipseEventState a = tl.Evaluate(t, 0d), b = tl.Evaluate(direct, 0d);
            double tau = direct - window.Start;
            bool nearBoundary = bounds.Any(x => Math.Abs(tau - x) < 1e-6);
            if (!nearBoundary)
            {
                if (a.Phase != b.Phase) phaseMismatch++;
                worst = Math.Max(worst, Math.Max(Math.Abs(a.Level - b.Level), Math.Abs(a.HubDirect - b.HubDirect)));
            }
        }
        Expect(failures, worst < 1e-5 && phaseMismatch == 0, $"stepping drift {worst} phase mismatches {phaseMismatch}");
        // Restoring any (t_cel, t_w) reproduces every output bit-exactly, also on a freshly built timeline.
        var twin = new EclipseEventTimeline(new EclipseEventSettings(), window, table, clock, 1337);
        var rng = new Random(77);
        int differ = 0;
        for (int i = 0; i < 4000; i++)
        {
            double tc = window.Start + (rng.NextDouble() * 5000d - 400d), tw = rng.NextDouble() * 1e6;
            if (tl.Evaluate(tc, tw) != tl.Evaluate(tc, tw) || tl.Evaluate(tc, tw) != twin.Evaluate(tc, tw)) differ++;
            if (tl.Evaluate(tc, tw) != tl.Evaluate(tc, tw * 0.5 + 3d)) differ++; // celestial refuel clock ignores the weather clock
        }
        Expect(failures, differ == 0, $"{differ} non-reproducible or weather-dependent evaluations");
        var sw = Stopwatch.StartNew();
        double sink = 0d;
        for (int i = 0; i < 20000; i++) sink += tl.Evaluate(window.Start + 300d + i * 0.17, 0d).Level;
        double us = sw.Elapsed.TotalMilliseconds * 1000d / 20000d;
        Console.WriteLine($"ECLIPSE_EVENT determinism frames={frames} stepping_drift={worst:E2} evaluate_us={us:F2} sink={sink > 0d}");
    }

    static void CheckTimeScale(EclipseEventTimeline tl, List<string> failures)
    {
        // Item 6: the event follows the celestial TimeScale. Simulate the clock advancing delta * k and record the real time at which each
        // milestone first appears; the schedule at k must be the 1x schedule divided by k (within one frame).
        const double Dt = 1d / 60d;
        double startTau = -310d, endTau = tl.TauSustain + 20d;
        double[] scales = { 1d, 2d, 10d, 100d };
        var stamps = new Dictionary<double, Dictionary<string, double>>();
        foreach (double k in scales)
        {
            var found = new Dictionary<string, double>();
            EclipseEventPhase lastPhase = EclipseEventPhase.Idle;
            int lastFired = 0;
            bool half = false, ninety = false;
            long frame = 0;
            for (double tau = startTau; tau <= endTau; frame++, tau = startTau + k * frame * Dt)
            {
                double real = frame * Dt;
                EclipseEventState s = tl.Evaluate(tl.Window.Start + tau, 0d);
                if (s.Phase != lastPhase) { found["phase_" + s.Phase] = real; lastPhase = s.Phase; }
                if (!half && s.FiredCount >= 297) { found["fired_297"] = real; half = true; }
                if (!ninety && s.FiredCount >= 594) { found["fired_594"] = real; ninety = true; }
                if (s.Level >= 0.5 && !found.ContainsKey("level_0.5")) found["level_0.5"] = real;
                if (s.FrontsActive && !found.ContainsKey("fronts_start")) found["fronts_start"] = real;
                if (s.ForwardHeadMeters >= Circumference / 2d - 1e-6 && !found.ContainsKey("fronts_met")) found["fronts_met"] = real;
                lastFired = s.FiredCount;
            }
            _ = lastFired;
            stamps[k] = found;
        }
        int compared = 0;
        foreach (double k in scales.Skip(1))
            foreach (var kv in stamps[1d])
            {
                if (!stamps[k].TryGetValue(kv.Key, out double real)) { failures.Add($"k={k}: milestone {kv.Key} missing"); continue; }
                compared++;
                // Quantisation: the milestone lands on a frame, the frame spans k * Dt of celestial time.
                double tolerance = 2d * Dt + 0.0001;
                Expect(failures, Math.Abs(real - kv.Value / k) <= tolerance, $"k={k}: {kv.Key} at {real:F4} s real, expected {kv.Value / k:F4} (1x {kv.Value:F3})");
            }
        // Spot the headline number: at 10x the ignition (launch to full) takes about 15 s of real time.
        double fullReal10 = stamps[10d]["phase_Crescendo"] - stamps[10d]["phase_Ignition"];
        Expect(failures, Math.Abs(fullReal10 - 15.15) < 0.2, $"ignition at 10x takes {fullReal10} s");
        Console.WriteLine($"ECLIPSE_EVENT timescale compared={compared} ignition_real_s_at_10x={fullReal10:F2} ignition_real_s_at_100x={stamps[100d]["phase_Crescendo"] - stamps[100d]["phase_Ignition"]:F2}");
    }

    static void CheckWeatherClock(EclipseSiteTable table, EmitterGridPulseClock clock, EclipseEventWindow window, List<string> failures)
    {
        // RefuelClock.Weather keeps the spec A.4 behaviour: the grid maths run on the weather clock, the event on the celestial clock.
        var tl = new EclipseEventTimeline(new EclipseEventSettings { RefuelClock = EclipseRefuelClock.Weather }, window, table, clock, 1337);
        double tc = window.Start + tl.TauSustain + 200d;
        EclipseEventState a = tl.Evaluate(tc, 5000d), b = tl.Evaluate(tc, 5001.5);
        Expect(failures, a.GridTime == 5000d && b.GridTime == 5001.5, "weather mode grid time");
        Expect(failures, a.Level != 0d && a.Phase == EclipseEventPhase.Sustain, "weather mode phase from the celestial clock");
        // Heartbeat tracks the weather clock: find a weather time where a row lands inside Sustain, and one far from a landing.
        double maxA = 0d, maxB = 0d;
        for (double w = 5000d; w < 5006d; w += 0.01) { maxA = Math.Max(maxA, tl.Evaluate(tc, w).Heartbeat); maxB = Math.Max(maxB, tl.Evaluate(tc + 0.5, w).Heartbeat); }
        Expect(failures, maxA > 0.015 && Math.Abs(maxA - maxB) < 0.01, $"weather heartbeat {maxA}/{maxB}");
        // The event stays a pure function of celestial time: only the refuel terms read the weather clock.
        var cel = new EclipseEventTimeline(new EclipseEventSettings(), window, table, clock, 1337);
        EclipseEventState x = tl.Evaluate(window.Start + 25d, 1d), y = cel.Evaluate(window.Start + 25d, 2d);
        Expect(failures, x.Level == y.Level && x.Phase == y.Phase && x.MainBeamTip == y.MainBeamTip, "pre-Sustain state independent of the refuel clock");
    }

    static void CheckGolden(EclipseEventTimeline tl, bool capture, List<string> failures)
    {
        var actual = new Golden { Format = Format };
        actual.Boundaries = tl.Boundaries.Select(b => new GoldenBoundary { Phase = b.Phase.ToString(), StartTau = b.StartTau }).ToList();
        foreach (int entry in new[] { 0, 1, 137, 300, 593 }) actual.Sites.Add(new GoldenSite { Entry = entry, Fire = tl.SiteFireTau(entry), Arrival = tl.SiteArrivalTau(entry) });
        foreach (double tau in new[] { 21.0, 25.0, 40.0, 100.0, 150.0, 185.0, 500.0, 2000.0, 4000.0, 4050.0, 4066.0, 4075.0, 4080.0 })
        {
            EclipseEventState s = tl.Evaluate(tl.Window.Start + tau, 0d);
            actual.Samples.Add(new GoldenSample { Tau = tau, Phase = s.Phase.ToString(), Level = s.Level, HubDirect = s.HubDirect, HubDay = s.HubDay, Fired = s.FiredCount });
        }
        string fixture = ProjectSettings.GlobalizePath("res://tools/fixtures/eclipse_event_timeline_golden.json");
        if (capture)
        {
            if (File.Exists(fixture)) { failures.Add("Refusing to overwrite existing golden fixture: " + fixture); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);
            File.WriteAllText(fixture, JsonSerializer.Serialize(actual, JsonOptions) + System.Environment.NewLine);
            Console.WriteLine("ECLIPSE_EVENT_GOLDEN captured " + fixture);
            return;
        }
        if (!File.Exists(fixture)) { failures.Add("golden fixture missing"); return; }
        Golden? e = JsonSerializer.Deserialize<Golden>(File.ReadAllText(fixture), JsonOptions);
        if (e == null || e.Format != Format) { failures.Add("golden unreadable"); return; }
        Expect(failures, e.Boundaries.Count == actual.Boundaries.Count, "golden boundary count");
        for (int i = 0; i < Math.Min(e.Boundaries.Count, actual.Boundaries.Count); i++)
            Expect(failures, e.Boundaries[i].Phase == actual.Boundaries[i].Phase && Math.Abs(e.Boundaries[i].StartTau - actual.Boundaries[i].StartTau) < 1e-6, $"golden boundary {e.Boundaries[i].Phase}: {e.Boundaries[i].StartTau} vs {actual.Boundaries[i].StartTau}");
        for (int i = 0; i < Math.Min(e.Sites.Count, actual.Sites.Count); i++)
            Expect(failures, e.Sites[i].Entry == actual.Sites[i].Entry && Math.Abs(e.Sites[i].Fire - actual.Sites[i].Fire) < 1e-6 && Math.Abs(e.Sites[i].Arrival - actual.Sites[i].Arrival) < 1e-6, $"golden site {e.Sites[i].Entry}");
        for (int i = 0; i < Math.Min(e.Samples.Count, actual.Samples.Count); i++)
        {
            GoldenSample g = e.Samples[i], a = actual.Samples[i];
            Expect(failures, g.Phase == a.Phase && g.Fired == a.Fired && Math.Abs(g.Level - a.Level) < 1e-9 && Math.Abs(g.HubDirect - a.HubDirect) < 1e-9 && Math.Abs(g.HubDay - a.HubDay) < 1e-9, $"golden sample tau={g.Tau}");
        }
    }

    public sealed class Golden
    {
        public string Format { get; set; } = "";
        public List<GoldenBoundary> Boundaries { get; set; } = new();
        public List<GoldenSite> Sites { get; set; } = new();
        public List<GoldenSample> Samples { get; set; } = new();
    }
    public sealed class GoldenBoundary { public string Phase { get; set; } = ""; public double StartTau { get; set; } }
    public sealed class GoldenSite { public int Entry { get; set; } public double Fire { get; set; } public double Arrival { get; set; } }
    public sealed class GoldenSample
    {
        public double Tau { get; set; }
        public string Phase { get; set; } = "";
        public double Level { get; set; }
        public double HubDirect { get; set; }
        public double HubDay { get; set; }
        public int Fired { get; set; }
    }
}
