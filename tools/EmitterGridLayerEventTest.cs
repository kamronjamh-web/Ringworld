using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Phase E4d of docs/eclipse-false-sun-spec.md (E.3): the grid layer's eclipse hooks. The normal pulse is bit-identical with no event (CPU goldens of
/// <c>PulseAtQ</c>/<c>CountLitLinks</c> and the empty-event overloads); ignition fronts (z/w lanes) are bidirectional with a plateau, mirror on the
/// retrograde grid and are appended after the normal fronts (at most 24, up to 24 head sprites); the director pushes the grid-time override only from
/// MainFire to E, suppresses the normal pulse from MainFire to the end of Handoff and returns every value to its default outside the event; a normal
/// frame writes no extra uniforms.
/// </summary>
public partial class EmitterGridLayerTest
{
    sealed class StubClock : IEclipseClock
    {
        public double Seconds { get; set; }
        public float Scale { get; set; } = 1f;
        public bool IsPaused { get; set; }
        public double SimulationSeconds => Seconds;
        public float TimeScale => Scale;
        public bool Paused { get => IsPaused; set => IsPaused = value; }
        public void SeekSimulationSeconds(double seconds) => Seconds = seconds;
    }

    static void CheckEventFronts(EmitterGridBeamLayer layer, EmitterGridTopology topology, Node3D fixture, List<string> failures)
    {
        layer.Configure(new GameplayEmitterGridSettings());
        EmitterGridPulseClock clock = layer.Clock!;
        double c = clock.CircumferenceMeters, v = clock.SpeedMetersPerSecond;

        CheckLegacyIdentity(clock, topology, failures);
        CheckEventMath(topology, failures);

        // ---- layer: fronts appended after the normal ones, up to 24 heads, no change without events
        layer.SetGridClockOverride(null);
        layer.NormalPulseWeight = 1f;
        layer.SetEventFronts(ReadOnlySpan<Vector4>.Empty);
        double t0 = 7_777.5;
        layer.UpdateFronts(t0);
        int normalFronts = layer.LiveFrontCount, normalHeads = layer.HeadSpriteCount;
        Expect(failures, normalFronts == 6 && normalHeads == 6 && layer.EventFrontCount == 0 && layer.EventWeight == 0f, $"normal fronts {normalFronts}/{normalHeads}");
        Expect(failures, EmitterGridBeamLayer.MaxHeadSprites == 24 && EmitterGridBeamLayer.MaxShaderFronts == 24 && layer.HeadInstances!.Multimesh.InstanceCount == 24, "24 head sprites");

        var lanes = new List<Vector4>();
        for (int col = 0; col < 6; col++)
        {
            lanes.Add(ToLane(clock.MakeEventFront(col, 3_000_000d, 3_000_000d, true, 0.35d, false)));
            lanes.Add(ToLane(clock.MakeEventFront(col, c - 3_000_000d, 3_000_000d, false, 0.35d, false)));
        }
        layer.SetEventFronts(lanes.ToArray(), 1f);
        layer.UpdateFronts(t0);
        Expect(failures, layer.EventFrontCount == 12 && layer.LiveFrontCount == normalFronts + 12 && layer.HeadSpriteCount == normalHeads + 12, $"appended fronts {layer.LiveFrontCount}/{layer.HeadSpriteCount}");
        Expect(failures, layer.HeadSpriteCount > 12, "more than the old 12 head sprites are used");
        layer.NormalPulseWeight = 0f;
        layer.UpdateFronts(t0);
        Expect(failures, layer.LiveFrontCount == 12 && layer.HeadSpriteCount == 12, $"suppressed normal pulse leaves {layer.LiveFrontCount}/{layer.HeadSpriteCount}");
        layer.NormalPulseWeight = 1f;

        // Headless fronts (absorbed or the meeting flare) get no head sprite; the cap protects the array.
        var headless = new List<Vector4>();
        for (int col = 0; col < 6; col++)
        {
            headless.Add(ToLane(clock.MakeEventFront(col, c * 0.5d, c * 0.5d, true, 0.35d, true)));
            headless.Add(ToLane(clock.MakeEventFront(col, c * 0.5d, c * 0.5d, false, 0.35d, true)));
            headless.Add(ToLane(clock.MakeEventFront(col, c * 0.5d + 300_000d, 600_000d, true, 0.9d, true)));
        }
        layer.SetEventFronts(headless.ToArray(), 1f);
        layer.UpdateFronts(t0);
        Expect(failures, layer.LiveFrontCount == 24 && layer.HeadSpriteCount == normalHeads, $"headless fronts {layer.LiveFrontCount}/{layer.HeadSpriteCount} (18 + 6 normal, normal heads only)");
        layer.SetEventFronts(new Vector4[40], 1f);
        layer.UpdateFronts(t0);
        Expect(failures, layer.EventFrontCount == EmitterGridBeamLayer.MaxShaderFronts && layer.LiveFrontCount <= EmitterGridBeamLayer.MaxShaderFronts, "event front overflow");
        layer.SetEventFronts(ReadOnlySpan<Vector4>.Empty);
        layer.UpdateFronts(t0);
        Expect(failures, layer.LiveFrontCount == normalFronts && layer.HeadSpriteCount == normalHeads && layer.EventWeight == 0f, "clearing the event restores the normal fronts");

        CheckGridClockSlew(clock.PeriodSeconds, failures);
        CheckKnotFlareSuppression(clock, topology, failures);
        CheckLayerUpdate(layer, fixture, failures);
        CheckDirectorPush(layer, topology, failures);
        _ = v;
    }

    static double WrapDiff(double x, double p) => x - Math.Round(x / p) * p;

    /// <summary>E4 review fix 1: the override/weather clock hand-over is slewed (continuous at both edges, 0.5-1.5x, never reversing) and snaps on a time jump.</summary>
    static void CheckGridClockSlew(double period, List<string> failures)
    {
        const double dt = 1d / 60d;
        foreach ((double offset, string name) in new[] { (0d, "equal"), (97.25d, "ahead"), (-141.5d, "behind"), (149.9d, "near half"), (4_000d * period + 0.5d * period + 13d, "far apart") })
        {
            // override -> none
            var slew = new EmitterGridClockSlew();
            double weather = 1_000d, ov = weather + offset;
            double last = 0d;
            for (int i = 0; i < 120; i++, weather += dt, ov += dt) last = slew.Resolve(ov, weather, period);
            Expect(failures, last == ov - dt, $"{name}: override frames return the override");
            double prev = last, minRate = double.MaxValue, maxRate = double.MinValue, first = double.NaN;
            int frames = 0;
            while (frames < 100_000)
            {
                double t = slew.Resolve(null, weather, period);
                if (frames == 0) first = t;
                double rate = WrapDiff(t - prev, period) / dt;
                minRate = Math.Min(minRate, rate); maxRate = Math.Max(maxRate, rate);
                prev = t;
                weather += dt; frames++;
                if (!slew.Slewing) break;
            }
            // continuity: the first slewed value continues the override by one frame (mod the period)
            Expect(failures, Math.Abs(WrapDiff(first - last, period) - dt) < 0.5d * dt + 1e-6, $"{name}: override -> none first step {WrapDiff(first - last, period):0.0000} s");
            Expect(failures, minRate > 0.5d - 1e-3 && maxRate < 1.5d + 1e-3, $"{name}: override -> none speed {minRate:0.000}..{maxRate:0.000}");
            Expect(failures, frames < 100_000, $"{name}: slew length {frames} frames");
            Expect(failures, slew.Resolve(null, weather, period) == weather && !slew.Slewing, $"{name}: slew ends on the weather clock");

            // none -> override
            slew = new EmitterGridClockSlew();
            weather = 2_000d; ov = 40_000d + offset;
            for (int i = 0; i < 120; i++, weather += dt) last = slew.Resolve(null, weather, period);
            prev = last; minRate = double.MaxValue; maxRate = double.MinValue; first = double.NaN; frames = 0;
            while (frames < 100_000)
            {
                double t = slew.Resolve(ov, weather, period);
                if (frames == 0) first = t;
                double rate = WrapDiff(t - prev, period) / dt;
                minRate = Math.Min(minRate, rate); maxRate = Math.Max(maxRate, rate);
                prev = t;
                weather += dt; ov += dt; frames++;
                if (!slew.Slewing) break;
            }
            Expect(failures, Math.Abs(WrapDiff(first - last, period) - dt) < 0.5d * dt + 1e-6, $"{name}: none -> override first step {WrapDiff(first - last, period):0.0000} s");
            Expect(failures, minRate > 0.5d - 1e-3 && maxRate < 1.5d + 1e-3, $"{name}: none -> override speed {minRate:0.000}..{maxRate:0.000}");
            Expect(failures, slew.Resolve(ov, weather, period) == ov && !slew.Slewing, $"{name}: slew ends on the override clock");
        }

        // A scrub, load or time jump snaps instead of slewing.
        var snap = new EmitterGridClockSlew();
        double w = 500d, o = 9_000d;
        for (int i = 0; i < 10; i++, w += dt, o += dt) snap.Resolve(o, w, period);
        w += 1_000d; // weather jump on the frame the override ends
        Expect(failures, snap.Resolve(null, w, period) == w && !snap.Slewing, "override -> none with a weather jump snaps");
        for (int i = 0; i < 10; i++, w += dt) snap.Resolve(null, w, period);
        o = 20_000d;
        w += 800d; // weather jump on the frame the override starts
        Expect(failures, snap.Resolve(o, w, period) == o && !snap.Slewing, "none -> override with a weather jump snaps");
        for (int i = 0; i < 10; i++, w += dt, o += dt) snap.Resolve(o, w, period);
        o += 3_000d; // override jump, then the override ends
        snap.Resolve(o, w, period);
        w += dt;
        Expect(failures, snap.Resolve(null, w, period) == w && !snap.Slewing, "override -> none after an override jump snaps");
        // A jump during a slew cancels it.
        var mid = new EmitterGridClockSlew();
        w = 100d; o = w + 120d;
        for (int i = 0; i < 10; i++, w += dt, o += dt) mid.Resolve(o, w, period);
        for (int i = 0; i < 30; i++, w += dt) mid.Resolve(null, w, period);
        Expect(failures, mid.Slewing, "slew under way before the jump");
        w += 500d;
        Expect(failures, mid.Resolve(null, w, period) == w && !mid.Slewing, "a time jump during the slew snaps");
    }

    /// <summary>E4 review fix 2: the knot relaunch flare belongs to the normal pulse; <c>knotFlare: false</c> drops it, the default is bit-identical.</summary>
    static void CheckKnotFlareSuppression(EmitterGridPulseClock layerClock, EmitterGridTopology topology, List<string> failures)
    {
        // A long flare window (100 s) so the check runs where no normal front's 40 s trail still covers the knot links.
        var clock = new EmitterGridPulseClock(1337, layerClock.CircumferenceMeters, new EmitterGridPulseSettings { RelaunchFlareSeconds = 100d });
        double tLaunch = clock.LaunchTime(40);
        double t = tLaunch + 90d; // inside the flare, the fronts have left the ring base
        Expect(failures, clock.FlareEnvelope(t) > 0d, "test time is inside the relaunch flare");
        int withFlare = clock.CountLitLinks(topology, t, ReadOnlySpan<EmitterEventFront>.Empty);
        int without = clock.CountLitLinks(topology, t, ReadOnlySpan<EmitterEventFront>.Empty, knotFlare: false);
        Expect(failures, withFlare == clock.CountLitLinks(topology, t) && without < withFlare, $"knot flare lit links {withFlare} vs suppressed {without}");
        int knots = 0, knotsLitNoFlare = 0, knotsLitFlare = 0, knotIndex = -1;
        for (int i = 0; i < topology.Links.Length; i++)
        {
            ref readonly EmitterLink l = ref topology.Links[i];
            if (l.Kind == EmitterLinkKind.Chain) continue;
            if (knotIndex < 0) knotIndex = i;
            knots++;
            if (clock.IsLinkLit(l, t, ReadOnlySpan<EmitterEventFront>.Empty)) knotsLitFlare++;
            if (clock.IsLinkLit(l, t, ReadOnlySpan<EmitterEventFront>.Empty, knotFlare: false)) knotsLitNoFlare++;
        }
        Expect(failures, knots > 0 && knotsLitFlare == knots && knotsLitNoFlare < knots, $"knot links lit {knotsLitFlare}/{knots} with the flare, {knotsLitNoFlare} without");
        // Between launches nothing differs.
        double quiet = tLaunch + clock.Settings.RelaunchFlareSeconds + 20d;
        Expect(failures, clock.CountLitLinks(topology, quiet, ReadOnlySpan<EmitterEventFront>.Empty) == clock.CountLitLinks(topology, quiet, ReadOnlySpan<EmitterEventFront>.Empty, knotFlare: false), "no flare, no difference");
        // PulseAtLink: the knot flare floor disappears.
        ref readonly EmitterLink knot = ref topology.Links[knotIndex];
        double floor = clock.PulseAtLink(t, knot, 0.5d, ReadOnlySpan<EmitterEventFront>.Empty), bare = clock.PulseAtLink(t, knot, 0.5d, ReadOnlySpan<EmitterEventFront>.Empty, knotFlare: false);
        Expect(failures, floor >= clock.FlareEnvelope(t) && bare <= floor, $"PulseAtLink flare floor {floor:0.000} vs {bare:0.000}");
    }

    static Vector4 ToLane(in EmitterEventFront f) => new((float)f.HeadQ, f.Column, (float)f.SignedTravelled, (float)f.Plateau);

    /// <summary>Legacy maths bit-identical: the empty-event overloads equal the legacy methods exactly, and goldens of the legacy code are unchanged.</summary>
    static void CheckLegacyIdentity(EmitterGridPulseClock clock, EmitterGridTopology topology, List<string> failures)
    {
        var rng = new Random(99);
        double c = clock.CircumferenceMeters;
        int differing = 0, lit = 0;
        for (int i = 0; i < 4000; i++)
        {
            double t = 5_000d + rng.NextDouble() * 20_000d;
            int col = rng.Next(6);
            double q = rng.NextDouble() * c;
            double a = clock.PulseAtQ(t, col, q), b = clock.PulseAtQ(t, col, q, ReadOnlySpan<EmitterEventFront>.Empty);
            if (BitConverter.DoubleToInt64Bits(a) != BitConverter.DoubleToInt64Bits(b)) differing++;
            if (a > 0d) lit++;
        }
        Expect(failures, differing == 0 && lit > 100, $"empty-event PulseAtQ differs from the legacy path in {differing} samples ({lit} lit)");
        int countDiff = 0;
        for (int i = 0; i < 60; i++)
        {
            double t = 5_000d + i * 331.7;
            if (clock.CountLitLinks(topology, t) != clock.CountLitLinks(topology, t, ReadOnlySpan<EmitterEventFront>.Empty)) countDiff++;
        }
        Expect(failures, countDiff == 0, $"empty-event CountLitLinks differs at {countDiff} times");
        // Goldens of the legacy code (captured at the E4c tree): the bit patterns and the lit-link counts.
        Span<EmitterPulseFront> fronts = stackalloc EmitterPulseFront[8];
        var sb = new System.Text.StringBuilder();
        foreach ((double t, int col) in new[] { (12_345.678, 0), (12_345.678, 3), (777.25, 5), (20_000.5, 2), (4_321.0, 1) })
        {
            // Probe points around the first live head (inside the leading edge, the head streak, the trail and the wake).
            double head = clock.GetFronts(t, col, fronts) > 0 ? fronts[0].HeadQ : 0d;
            foreach (double offset in new[] { -2_000d, 0d, 1_000d, 60_000d, 400_000d, 2_500_000d })
                sb.Append(BitConverter.DoubleToInt64Bits(clock.PulseAtQ(t, col, Math.Clamp(head - offset, 0d, c))).ToString("X16")).Append(' ');
        }
        foreach (double t in new[] { 12_345.678, 777.25, 20_000.5, 4_321.0 }) sb.Append(clock.CountLitLinks(topology, t)).Append(' ');
        string actual = sb.ToString().TrimEnd();
        Console.WriteLine("EMITTER_GRID_LAYER legacy_golden=" + actual);
        Expect(failures, actual == LegacyGolden, $"legacy golden changed: {actual}");
    }

    const string LegacyGolden = "3FD097B425ED097B 3FF0000000000000 3FF0000000000000 3FEB2080B925A33B 3FD02A1EFF767C39 3F731C1F58B98FAF 3FD097B425ED097B 3FF0000000000000 3FF0000000000000 3FEB2080B925A33B 3FD02A1EFF767C39 3F731C1F58B98FAF 3FD097B425ED097B 3FF0000000000000 3FF0000000000000 3FEB2080B925A33B 3FD02A1EFF767C39 3F731C1F58B98FAF 3FD097B425ED097B 3FF0000000000000 3FF0000000000000 3FEB2080B925A33B 3FD02A1EFF767C39 3F731C1F58B98FAF 3FD097B425ED097B 3FF0000000000000 3FF0000000000000 3FEB2080B925A33B 3FD02A1EFF767C39 3F731C1F58B98FAF 84 84 84 84";

    /// <summary>CPU math of the ignition fronts: bidirectional with a plateau, headless spans, retrograde mirror.</summary>
    static void CheckEventMath(EmitterGridTopology topology, List<string> failures)
    {
        double c = topology.CircumferenceMeters;
        var pro = new EmitterGridPulseClock(1337, c);
        var retro = new EmitterGridPulseClock(1337, c, new EmitterGridPulseSettings { Direction = EmitterPulseDirection.Retrograde });
        double head = 5_000_000d, plateau = 0.35d;
        EmitterEventFront fwd = pro.MakeEventFront(2, head, head, true, plateau, false);
        EmitterEventFront bwd = pro.MakeEventFront(2, c - head, head, false, plateau, false);
        Expect(failures, fwd.SignedTravelled > 0d && bwd.SignedTravelled < 0d && fwd.HeadQ == head && bwd.HeadQ == c - head, "prograde front signs/heads");
        // Bidirectional: the forward front lights [0, head] and the backward one [C - head, C]; ahead of a head there is only the 3 km edge.
        Expect(failures, Math.Abs(pro.EventFrontAtQ(fwd, head) - 1d) < 1e-12 && Math.Abs(pro.EventFrontAtQ(bwd, c - head) - 1d) < 1e-12, "head profile is 1 at the head");
        Expect(failures, Math.Abs(pro.EventFrontAtQ(fwd, 1_000_000d) - Math.Max(pro.Profile(head - 1_000_000d), plateau)) < 1e-12 && pro.EventFrontAtQ(fwd, 1_000_000d) >= plateau, "forward plateau behind the head");
        Expect(failures, pro.EventFrontAtQ(bwd, c - 1_000_000d) >= plateau && pro.EventFrontAtQ(bwd, c - head - 5_000d) == 0d, "backward plateau behind its head, nothing ahead");
        Expect(failures, pro.EventFrontAtQ(fwd, head + 5_000d) == 0d && pro.EventFrontAtQ(fwd, head + 1_500d) > 0d, "leading edge is 3 km");
        Expect(failures, pro.EventFrontAtQ(fwd, c - 1_000_000d) == 0d, "forward front does not light the backward half");
        // Headless: no head profile, plateau only.
        EmitterEventFront parked = pro.MakeEventFront(2, c * 0.5d, c * 0.5d, true, plateau, true);
        Expect(failures, parked.Headless && Math.Abs(pro.EventFrontAtQ(parked, c * 0.5d) - plateau) < 1e-12 && pro.EventFrontAtQ(parked, c * 0.5d + 10d) == 0d, "headless front is the plateau only");
        // Pulse at a chain coordinate sums the normal fronts and the events of the same column only.
        double t = 12_345.678, q = 2_000_000d;
        double normal = pro.PulseAtQ(t, 2, q);
        double with = pro.PulseAtQ(t, 2, q, new[] { fwd });
        Expect(failures, with >= normal && Math.Abs(with - Math.Min(1d, normal + pro.EventFrontAtQ(fwd, q))) < 1e-12, "event sum");
        Expect(failures, pro.PulseAtQ(t, 3, q, new[] { fwd }) == pro.PulseAtQ(t, 3, q), "another column is not lit by the event");
        // Retrograde mirror: the same p-space front reads the same at the same link coordinate p.
        EmitterEventFront rf = retro.MakeEventFront(2, head, head, true, plateau, false);
        EmitterEventFront rb = retro.MakeEventFront(2, c - head, head, false, plateau, false);
        Expect(failures, rf.SignedTravelled < 0d && rb.SignedTravelled > 0d && Math.Abs(rf.HeadQ - (c - head)) < 1e-6, "retrograde front signs/heads");
        var rng = new Random(5);
        double worst = 0d;
        for (int i = 0; i < 2000; i++)
        {
            double p = rng.NextDouble() * c;
            worst = Math.Max(worst, Math.Abs(pro.EventFrontAtQ(fwd, pro.ToQ(p)) - retro.EventFrontAtQ(rf, retro.ToQ(p))));
            worst = Math.Max(worst, Math.Abs(pro.EventFrontAtQ(bwd, pro.ToQ(p)) - retro.EventFrontAtQ(rb, retro.ToQ(p))));
        }
        Expect(failures, worst < 1e-12, $"retrograde mirror differs by {worst}");
        // Collapse rule: links covered by the plateau are lit, the rest are not (no normal fronts on a column far from any head is not guaranteed, so compare with/without).
        int lit0 = pro.CountLitLinks(topology, t), lit1 = pro.CountLitLinks(topology, t, new[] { fwd, bwd });
        Expect(failures, lit1 > lit0, $"event fronts do not light more links ({lit0} -> {lit1})");
        // Every link inside the forward span is lit; a link ahead of the head (beyond the edge) and outside every other window is not lit by the event alone.
        int first = topology.ChainFirstLink(2), insideLit = 0, inside = 0;
        for (int k = 0; k < topology.LinksPerChain; k++)
        {
            EmitterLink l = topology[first + k];
            if (l.P1 <= head) { inside++; if (pro.IsLinkLit(l, t, new[] { fwd })) insideLit++; }
        }
        Expect(failures, inside > 10 && insideLit == inside, $"links inside the span lit {insideLit}/{inside}");
    }

    /// <summary>A real layer update with a camera: the grid-time override reaches the pulse time, and a normal frame writes no extra uniforms.</summary>
    static void CheckLayerUpdate(EmitterGridBeamLayer layer, Node3D fixture, List<string> failures)
    {
        var camera = new Camera3D { Name = "EventTestCamera", Current = true };
        fixture.AddChild(camera);
        try
        {
            layer.FreezePulse = false;
            layer.PulseScrubSeconds = 0d;
            layer.SetGridClockOverride(null);
            layer.NormalPulseWeight = 1f;
            layer.SetEventFronts(ReadOnlySpan<Vector4>.Empty);
            layer._Process(0.016);
            int writes = layer.LookWriteCount;
            bool synchronised = layer.ClockSynchronised;
            double weatherTime = layer.PulseTimeSeconds;
            for (int i = 0; i < 20; i++) layer._Process(0.016);
            Expect(failures, layer.LookWriteCount == writes, $"normal frames rewrote the look uniforms ({layer.LookWriteCount - writes})");
            Expect(failures, !layer.GridClockOverridden && Math.Abs((float)layer.BeamMaterial!.GetShaderParameter("eg_force_night")) < 1e-9f, "force night off outside the event");
            Expect(failures, ((Vector4)layer.BeamMaterial!.GetShaderParameter("eg_visibility")).X == layer.Settings.NightPeakIntensity, "night peak untouched outside the event");

            double beforeOverride = layer.PulseTimeSeconds, period = layer.Clock!.PeriodSeconds;
            layer.SetGridClockOverride(12_345.5);
            layer._Process(0.016);
            double handOver = layer.PulseTimeSeconds - beforeOverride;
            handOver -= Math.Round(handOver / period) * period;
            Expect(failures, layer.GridClockOverridden && Math.Abs(handOver) < 0.5d, $"the hand-over to the override is phase-continuous (step {handOver:0.000} s mod the period)");
            double overrideNow = 12_345.5;
            for (int i = 0; i < 80; i++) { overrideNow += 4d; layer.SetGridClockOverride(overrideNow); layer._Process(0.016); }
            Expect(failures, layer.PulseTimeSeconds == overrideNow, $"override time {layer.PulseTimeSeconds} after the slew (expected {overrideNow})");
            layer.PulseScrubSeconds = 2d;
            layer._Process(0.016);
            Expect(failures, layer.PulseTimeSeconds == overrideNow + 2d, "scrub is added on top of the override");
            layer.PulseScrubSeconds = 0d;
            layer.SetGridClockOverride(double.NaN);
            Expect(failures, !layer.GridClockOverridden, "a non-finite override is ignored");
            layer.SetGridClockOverride(null);
            layer._Process(0.016);
            Expect(failures, !layer.GridClockOverridden && (synchronised || layer.PulseTimeSeconds != 12_345.5), "clearing the override returns to the weather clock");
            _ = weatherTime;

            // E4 review fix 2: the knot relaunch flare uniform follows the normal pulse weight (bit-identical at 1, zero at 0).
            layer.FirePulseNow();
            layer._Process(0.016);
            float flareOn = (float)layer.BeamMaterial!.GetShaderParameter("eg_flare");
            layer.NormalPulseWeight = 0f;
            layer._Process(0.016);
            float flareOff = (float)layer.BeamMaterial.GetShaderParameter("eg_flare");
            layer.NormalPulseWeight = 1f;
            layer._Process(0.016);
            float flareBack = (float)layer.BeamMaterial.GetShaderParameter("eg_flare");
            layer.PulseScrubSeconds = 0d;
            Expect(failures, flareOn > 0.5f && flareOff == 0f && flareBack > 0.5f && flareBack == (float)layer.Clock!.FlareEnvelope(layer.PulseTimeSeconds), $"layer flare uniform {flareOn:0.000} / suppressed {flareOff:0.000} / back {flareBack:0.000}");

            // The event look: peak raised by EventPeak and the background forced to night, written once per change.
            layer.EventPeak = 2f;
            layer.SetEventFronts(new[] { new Vector4(1_000_000f, 0f, 500_000f, 0.35f) }, 1f);
            layer._Process(0.016);
            int eventWrites = layer.LookWriteCount;
            Expect(failures, eventWrites == writes + 1, $"event look writes {eventWrites - writes}");
            Vector4 visibility = (Vector4)layer.BeamMaterial!.GetShaderParameter("eg_visibility");
            Expect(failures, Math.Abs(visibility.X - 2f * layer.Settings.NightPeakIntensity) < 1e-5f && Math.Abs((float)layer.BeamMaterial.GetShaderParameter("eg_force_night") - 1f) < 1e-6f
                && Math.Abs((float)layer.HeadMaterial!.GetShaderParameter("eg_force_night") - 1f) < 1e-6f, $"event look {visibility}");
            for (int i = 0; i < 10; i++) layer._Process(0.016);
            Expect(failures, layer.LookWriteCount == eventWrites, "steady event frames rewrote the look uniforms");
            Vector4[] sent = (Vector4[])layer.BeamMaterial.GetShaderParameter("eg_fronts");
            Expect(failures, layer.LiveFrontCount == 6 + 1 && sent.Length >= 7 && sent[6] == new Vector4(1_000_000f, 0f, 500_000f, 0.35f), "event front reached the shader array after the normal ones");
            layer.SetEventFronts(ReadOnlySpan<Vector4>.Empty);
            layer.EventPeak = EmitterGridBeamLayer.DefaultEventPeak;
            layer._Process(0.016);
            Expect(failures, Math.Abs((float)layer.BeamMaterial.GetShaderParameter("eg_force_night")) < 1e-9f
                && ((Vector4)layer.BeamMaterial.GetShaderParameter("eg_visibility")).X == layer.Settings.NightPeakIntensity, "the look returns to the default after the event");
        }
        finally
        {
            layer.SetEventFronts(ReadOnlySpan<Vector4>.Empty);
            layer.SetGridClockOverride(null);
            fixture.RemoveChild(camera);
            camera.Free();
        }
    }

    /// <summary>The director's push through a real layer and a real engine over the whole event, at TimeScale 1 and 10.</summary>
    static void CheckDirectorPush(EmitterGridBeamLayer layer, EmitterGridTopology topology, List<string> failures)
    {
        foreach (float scale in new[] { 1f, 10f })
        {
            var engine = new EclipseEventEngine { SynchronousSolve = true };
            engine.SetSettings(new EclipseEventSettings());
            engine.SetSolver(new EclipseWindowSolver());
            engine.Bind(EclipseSiteTable.FromTopology(topology), layer.Clock!, 1337);
            var clock = new StubClock { Scale = scale };
            engine.Update(clock, 0d, EclipseObserverPose.None);
            double s = engine.Window.Start;
            EclipseEventTimeline t = engine.Timeline!;
            double c = layer.Clock!.CircumferenceMeters, v = layer.Clock.SpeedMetersPerSecond;
            double Push(double tau, double weather = 31.0)
            {
                clock.Seconds = s + tau;
                engine.Update(clock, weather, EclipseObserverPose.None);
                EclipseEventDirector.ApplyGridState(engine, layer);
                layer.UpdateFronts(layer.GridTimeOverrideSeconds ?? weather);
                return clock.Seconds;
            }

            Push(t.TauMainFire - 5d);
            Expect(failures, !layer.GridClockOverridden && layer.NormalPulseWeight == 1f && layer.EventFrontCount == 0, $"x{scale}: before MainFire the grid is untouched");
            double now = Push(t.TauMainFire + 2d);
            Expect(failures, layer.GridClockOverridden && layer.GridTimeOverrideSeconds == now && layer.NormalPulseWeight == 0f && layer.EventFrontCount == 0 && layer.LiveFrontCount == 0, $"x{scale}: MainFire override/suppression");
            Push(t.TauIgnition - 0.5d);
            Expect(failures, layer.NormalPulseWeight == 0f && layer.EventFrontCount == 0, $"x{scale}: kindle has no fronts yet");
            now = Push(t.TauIgnition + 40d);
            Expect(failures, layer.EventFrontCount == 12 && layer.LiveFrontCount == 12 && layer.HeadSpriteCount == 12 && layer.EventWeight == 1f, $"x{scale}: ignition fronts {layer.EventFrontCount}/{layer.LiveFrontCount}/{layer.HeadSpriteCount}");
            Expect(failures, layer.GridTimeOverrideSeconds == now, $"x{scale}: override follows the celestial clock");
            // Heads sit at v * travel, forward and backward, plateau 0.35.
            ReadOnlySpan<Vector4> lanes = layer.ShaderFronts;
            int fwd = 0, bwd = 0;
            for (int i = 0; i < 12; i++)
            {
                Vector4 f = lanes[i];
                if (f.Z > 0f && Math.Abs(f.X - 40d * v) < 2d && Math.Abs(f.Z - 40d * v) < 2d && Math.Abs(f.W - 0.35f) < 1e-6f) fwd++;
                if (f.Z < 0f && Math.Abs(f.X - (c - 40d * v)) < 2d && Math.Abs(-f.Z - 40d * v) < 2d && Math.Abs(f.W - 0.35f) < 1e-6f) bwd++;
            }
            Expect(failures, fwd == 6 && bwd == 6, $"x{scale}: bidirectional lanes forward {fwd} backward {bwd}");
            // Meeting: heads absorbed (headless), the flare spans appear, no sprites.
            Push(t.TauFrontsMet + 0.5d);
            Expect(failures, layer.EventFrontCount == 18 && layer.HeadSpriteCount == 0, $"x{scale}: meeting {layer.EventFrontCount} fronts, {layer.HeadSpriteCount} sprites");
            Push(t.TauFrontsMet + 5d);
            Expect(failures, layer.EventFrontCount == 12 && layer.HeadSpriteCount == 0 && layer.NormalPulseWeight == 0f, $"x{scale}: after the flare {layer.EventFrontCount} plateau spans");
            Push(t.TauHandoff + t.HandoffFade * 0.5d);
            float w = layer.EventWeight;
            Expect(failures, w > 0f && w < 1f && layer.NormalPulseWeight == 0f, $"x{scale}: hand-off fades the event look ({w})");
            Push(t.TauSustain + 1d);
            Expect(failures, layer.NormalPulseWeight == 1f && layer.EventFrontCount == 0 && layer.GridClockOverridden && layer.EventWeight == 0f, $"x{scale}: Sustain restores the normal pulse; the grid still runs on celestial time");
            Push(t.TauEnd + 3d, weather: 42.0);
            Expect(failures, !layer.GridClockOverridden && layer.NormalPulseWeight == 1f && layer.LiveFrontCount == 6, $"x{scale}: Return is back on the weather clock");
            Push(t.TauEnd + t.Settings.ReturnTailSeconds + 5d);
            Expect(failures, !layer.GridClockOverridden && layer.NormalPulseWeight == 1f && layer.EventFrontCount == 0, $"x{scale}: idle");
        }
    }
}
