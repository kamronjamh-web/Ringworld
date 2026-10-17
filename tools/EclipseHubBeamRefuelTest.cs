using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Phase E4d of docs/eclipse-false-sun-spec.md (E.4): the Sustain refuel flashes of the hub beams. The CPU twin of the shader
/// (<see cref="EclipseHubBeamMath.RefuelFlash"/>, fed with the grid's normal fronts) equals <see cref="EclipseEventTimeline.RefuelFlash"/>; a flash
/// starts within one frame of <c>ArrivalTime</c> at TimeScale 1 and 10 (frame stepping with the celestial clock, the grid on celestial time); with
/// no flash the beam level is bit-identical to the E4b level; and the layer publishes the fronts only while a flash can be visible.
/// </summary>
public partial class EclipseHubBeamLayerTest
{
    sealed class StepClock : IEclipseClock
    {
        public double Seconds { get; set; }
        public float Scale { get; set; } = 1f;
        public bool IsPaused { get; set; }
        public double SimulationSeconds => Seconds;
        public float TimeScale => Scale;
        public bool Paused { get => IsPaused; set => IsPaused = value; }
        public void SeekSimulationSeconds(double seconds) => Seconds = seconds;
    }

    static Vector4[] NormalLanes(EmitterGridPulseClock clock, double gridTime, int columns = 6)
    {
        var lanes = new List<Vector4>();
        Span<EmitterPulseFront> buffer = stackalloc EmitterPulseFront[32];
        for (int col = 0; col < columns; col++)
        {
            int n = clock.GetFronts(gridTime, col, buffer);
            for (int i = 0; i < n; i++) lanes.Add(new Vector4((float)buffer[i].HeadQ, col, 0f, 0f));
        }
        return lanes.ToArray();
    }

    /// <summary>The fronts the layer publishes for the orbs: no legacy age cutoff, up to travel + hold + fade + 2 s behind the loop end.</summary>
    static Vector4[] OrbLanes(EmitterGridPulseClock clock, double gridTime, EclipseEventSettings s, int columns = 6)
    {
        var lanes = new List<Vector4>();
        Span<EmitterPulseFront> buffer = stackalloc EmitterPulseFront[32];
        double behind = clock.SpeedMetersPerSecond * (s.OrbTravelSeconds + s.FlashHoldSeconds + s.FlashFadeSeconds + 2d);
        for (int col = 0; col < columns; col++)
        {
            int n = clock.GetFrontsWithin(gridTime, col, behind, buffer);
            for (int i = 0; i < n; i++) lanes.Add(new Vector4((float)buffer[i].HeadQ, col, 0f, 0f));
        }
        return lanes.ToArray();
    }

    static void CheckRefuel(EclipseHubBeamLayer layer, EclipseEventTimeline timeline, EmitterGridPulseClock clock, List<string> failures)
    {
        var beams = new EclipseBeamSettings();
        double s = timeline.Window.Start, c = clock.CircumferenceMeters;
        float direction = clock.Settings.Direction == EmitterPulseDirection.Prograde ? 1f : -1f;
        var rng = new Random(2024);

        // ---- uniforms: speed, flash shape, Sustain and Instability marks
        EclipseHubBeamUniforms probe = EclipseHubBeamMath.FromTimeline(timeline, beams, timeline.TauSustain + 10d);
        Expect(failures, Math.Abs(probe.Flash.X - 0.5f) < 1e-6f && Math.Abs(probe.Flash.Y - 0.6f) < 1e-6f && Math.Abs(probe.Flash.Z - 1.2f) < 1e-6f
            && Math.Abs(probe.Flash.W - (float)clock.SpeedMetersPerSecond) < 1e-3f && Math.Abs(probe.Refuel.X - (float)timeline.TauSustain) < 1e-3f
            && Math.Abs(probe.Refuel.Y - (float)timeline.TauInstability) < 1e-3f, $"flash uniforms {probe.Flash} {probe.Refuel}");

        // ---- twin versus the timeline over many (site, time) pairs, most of them inside a flash
        int compared = 0, active = 0;
        double worst = 0d;
        for (int i = 0; i < 400; i++)
        {
            int entry = rng.Next(timeline.SiteCount);
            double passGrid = s + timeline.TauSustain + 60d + rng.NextDouble() * 1500d;
            long n = timeline.RefuelLaunchAtOrAfter(entry, passGrid);
            double arrival = timeline.RefuelArrivalTime(entry, n);
            double age = i % 5 == 0 ? rng.NextDouble() * 6d - 1d : rng.NextDouble() * 2.6d;
            double gridTime = arrival + age, tau = gridTime - s;
            EclipseHubBeamUniforms u = EclipseHubBeamMath.FromTimeline(timeline, beams, tau);
            float fire = (float)timeline.SiteFireTau(entry);
            var (env, tip) = EclipseHubBeamMath.RefuelFlash(u, NormalLanes(clock, gridTime), direction, (float)c, fire, 0f, (float)timeline.Sites.P[entry], timeline.Sites.Column[entry]);
            double want = timeline.RefuelFlash(entry, tau, gridTime);
            worst = Math.Max(worst, Math.Abs(env - want));
            compared++;
            if (want > 0d) active++;
            if ((env > 0f) != (want > 0d) && Math.Abs(age) > 1e-3d) failures.Add($"flash onset differs for site {entry} age {age:F4}: twin {env} timeline {want}");
            if (env > 0f && Math.Abs(tip - Math.Clamp(age / 0.5d, 0d, 1d)) > 1e-3d) failures.Add($"flash tip {tip} for age {age}");
        }
        Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER refuel twin_vs_timeline compared={compared} active={active} worst_abs_diff={worst:E2}");
        Expect(failures, active > 150 && worst < 2e-3d, $"refuel twin differs from the timeline by {worst} ({active} active of {compared})");

        // Outside Sustain and the main spire, dead sites and a clock with no fronts never flash.
        {
            int entry = 5;
            double arrival = timeline.RefuelArrivalTime(entry, timeline.RefuelLaunchAtOrAfter(entry, s + timeline.TauIgnition + 60d));
            double tau = arrival + 0.3d - s;
            EclipseHubBeamUniforms u = EclipseHubBeamMath.FromTimeline(timeline, beams, tau);
            var lanes = NormalLanes(clock, arrival + 0.3d);
            float p = (float)timeline.Sites.P[entry]; int col = timeline.Sites.Column[entry];
            Expect(failures, EclipseHubBeamMath.RefuelFlash(u, lanes, direction, (float)c, (float)timeline.SiteFireTau(entry), 0f, p, col).Envelope == 0f, "a pass during Ignition must not flash");
            double sustainArrival = timeline.RefuelArrivalTime(entry, timeline.RefuelLaunchAtOrAfter(entry, s + timeline.TauSustain + 120d));
            u = EclipseHubBeamMath.FromTimeline(timeline, beams, sustainArrival + 0.3d - s);
            lanes = NormalLanes(clock, sustainArrival + 0.3d);
            Expect(failures, EclipseHubBeamMath.RefuelFlash(u, lanes, direction, (float)c, (float)timeline.SiteFireTau(entry), 0f, p, col).Envelope > 0f, "a Sustain pass flashes");
            Expect(failures, EclipseHubBeamMath.RefuelFlash(u, lanes, direction, (float)c, (float)timeline.SiteFireTau(entry), 1f, p, col).Envelope == 0f, "the main spire does not flash");
            Expect(failures, EclipseHubBeamMath.RefuelFlash(u, lanes, direction, (float)c, EclipseHubBeamMath.NeverFireTau, 0f, p, col).Envelope == 0f, "a dead site does not flash");
            Expect(failures, EclipseHubBeamMath.RefuelFlash(u, ReadOnlySpan<Vector4>.Empty, direction, (float)c, (float)timeline.SiteFireTau(entry), 0f, p, col).Envelope == 0f, "no fronts, no flash");
            // Instability gating: the last pass before Instability flashes, the first pass after it does not.
            double instabilityGrid = s + timeline.TauInstability;
            long before = timeline.RefuelLaunchAtOrAfter(entry, instabilityGrid - 3d) - 1;
            while (timeline.RefuelArrivalTime(entry, before) > instabilityGrid - 3d) before--;
            foreach ((long launch, bool expectFlash) in new[] { (before, true), (before + 1, false) })
            {
                double at = timeline.RefuelArrivalTime(entry, launch) + 0.3d;
                EclipseHubBeamUniforms ui = EclipseHubBeamMath.FromTimeline(timeline, beams, at - s);
                float envI = EclipseHubBeamMath.RefuelFlash(ui, NormalLanes(clock, at), direction, (float)c, (float)timeline.SiteFireTau(entry), 0f, p, col).Envelope;
                Expect(failures, (envI > 0f) == expectFlash && (timeline.RefuelFlash(entry, at - s, at) > 0d) == expectFlash, $"Instability gating (launch {launch}, expect {expectFlash}): twin {envI}");
            }
        }

        // ---- the hub-off identity: with no flash the level is bit-identical to the E4b level
        int identical = 0, total = 0;
        EclipseHubBeamUniforms uu = EclipseHubBeamMath.FromTimeline(timeline, beams, timeline.TauIgnition + 40d);
        for (int i = 0; i < 4000; i++)
        {
            float fire = (float)(timeline.TauIgnition + rng.NextDouble() * 120d), f = (float)rng.NextDouble(), day = (float)rng.NextDouble(), atm = (float)rng.NextDouble(), lw = (float)rng.NextDouble();
            float kind = i % 17 == 0 ? 1f : 0f;
            float a = EclipseHubBeamMath.Level(uu, fire, kind, f, day, atm, lw), b = EclipseHubBeamMath.LevelRefuel(uu, fire, kind, f, day, atm, lw, (0f, 1f));
            total++;
            if (BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b)) identical++;
        }
        Expect(failures, identical == total, $"no-flash level differs from the E4b level in {total - identical} samples");

        // ---- flash timing: first lit frame within one frame of ArrivalTime, TimeScale 1 and 10
        foreach (float scale in new[] { 1f, 10f })
        {
            var engine = new EclipseEventEngine { SynchronousSolve = true };
            engine.SetSettings(new EclipseEventSettings());
            engine.SetSolver(new EclipseWindowSolver());
            engine.Bind(timeline.Sites, clock, 1337);
            var stub = new StepClock { Scale = scale };
            engine.Update(stub, 0d, EclipseObserverPose.None);
            EclipseEventTimeline t = engine.Timeline!;
            double ws = t.Window.Start, frame = scale / 60d;
            int checkedEntries = 0, wrongFrame = 0, notWithinFrame = 0;
            for (int k = 0; k < 8; k++)
            {
                int entry = (k * 71 + 13) % t.SiteCount;
                long n = t.RefuelLaunchAtOrAfter(entry, ws + t.TauSustain + 300d + k * 217d);
                double arrival = t.RefuelArrivalTime(entry, n);
                stub.Seconds = arrival - 0.5d;
                engine.Update(stub, 0d, EclipseObserverPose.None);
                long twinFrame = -1, timelineFrame = -1;
                for (int f = 0; f < 400 && (twinFrame < 0 || timelineFrame < 0); f++)
                {
                    stub.Seconds += frame;
                    engine.Update(stub, 0d, EclipseObserverPose.None);
                    double grid = engine.GridTimeSeconds, tau = engine.State.Tau;
                    EclipseHubBeamUniforms u = EclipseHubBeamMath.FromTimeline(t, beams, tau);
                    var (env, _) = EclipseHubBeamMath.RefuelFlash(u, NormalLanes(clock, grid), direction, (float)c, (float)t.SiteFireTau(entry), 0f, (float)t.Sites.P[entry], t.Sites.Column[entry]);
                    if (twinFrame < 0 && env > 0f) { twinFrame = f; if (stub.Seconds < arrival - 1e-6d || stub.Seconds - arrival > frame + 1e-6d) notWithinFrame++; }
                    if (timelineFrame < 0 && t.RefuelFlash(entry, tau, grid) > 0d) timelineFrame = f;
                }
                checkedEntries++;
                if (twinFrame < 0 || twinFrame != timelineFrame) wrongFrame++;
            }
            Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER refuel timing x{scale:0} entries={checkedEntries} frame={frame * 1000d:F1}ms wrong_frame={wrongFrame} outside_one_frame={notWithinFrame}");
            Expect(failures, checkedEntries == 8 && wrongFrame == 0, $"x{scale}: the twin and the timeline start a flash on different frames ({wrongFrame})");
            Expect(failures, notWithinFrame == 0, $"x{scale}: a flash did not start within one frame of ArrivalTime ({notWithinFrame})");
        }

        // ---- layer plumbing: the fronts are published only while a flash can be visible
        layer.BindTimeline(timeline);
        layer.RefuelFrontClock = clock;
        ShaderMaterial near = layer.BeamMaterial!;
        Expect(failures, ((Vector4)near.GetShaderParameter("ev_flash")).IsEqualApprox(layer.Uniforms.Flash) && ((Vector2)near.GetShaderParameter("ev_refuel")).IsEqualApprox(layer.Uniforms.Refuel), "ev_flash/ev_refuel reach the near material");
        double gridNow = s + timeline.TauSustain + 100d;
        layer.ApplyEventTime(timeline.TauIgnition + 10d, true, 0.016, s + timeline.TauIgnition + 10d);
        Expect(failures, layer.RefuelFronts.Length == 0 && (int)near.GetShaderParameter("eg_front_count") == 0, "no refuel fronts during Ignition");
        layer.ApplyEventTime(timeline.TauSustain + 100d, true, 0.016, gridNow);
        Vector4[] expected = OrbLanes(clock, gridNow, timeline.Settings);
        Expect(failures, layer.RefuelFronts.Length >= 6 && layer.RefuelFronts.Length == expected.Length && (int)near.GetShaderParameter("eg_front_count") == expected.Length
            && (float)near.GetShaderParameter("eg_direction") == direction && Math.Abs((float)near.GetShaderParameter("eg_circumference") - (float)c) < 1f, $"refuel fronts in Sustain {layer.RefuelFronts.Length} (expected {expected.Length})");
        bool same = layer.RefuelFronts.Length == expected.Length;
        for (int i = 0; same && i < expected.Length; i++) same = layer.RefuelFronts[i] == expected[i];
        Expect(failures, same, "the published fronts are the pulse clock's normal fronts at the grid time");
        long customWrites = layer.CustomDataWriteCount, transformWrites = layer.TransformWriteCount;
        for (int i = 0; i < 30; i++) layer.ApplyEventTime(timeline.TauSustain + 100d + i / 60d, true, 1d / 60d, gridNow + i / 60d);
        Expect(failures, layer.CustomDataWriteCount == customWrites && layer.TransformWriteCount == transformWrites, "refuel frames write per-beam data");
        layer.ApplyEventTime(timeline.TauInstability + 60d, true, 0.016, s + timeline.TauInstability + 60d);
        Expect(failures, layer.RefuelFronts.Length == 0 && (int)near.GetShaderParameter("eg_front_count") == 0, "refuel fronts are cleared after the last flash");
        layer.RefuelFrontClock = null;
        layer.ApplyEventTime(timeline.TauSustain + 100d, true, 0.016, gridNow);
        Expect(failures, layer.RefuelFronts.Length == 0, "without a grid clock there are no refuel fronts");

        // ---- shaders compile with the flash maths
        foreach (string path in new[] { EclipseHubBeamLayer.BeamShaderPath, EclipseHubBeamLayer.CoreShaderPath, EclipseHubFarLayer.BeamShaderPath })
        {
            Shader? shader = GD.Load<Shader>(path);
            var uniforms = shader?.GetShaderUniformList();
            if (shader == null || uniforms == null || uniforms.Count == 0) { failures.Add("hub shader did not compile " + path); continue; }
            foreach (string required in new[] { "ev_flash", "ev_refuel", "ev_orb", "orb_look", "eg_fronts", "eg_front_count", "eg_direction", "eg_circumference" })
                if (!uniforms.Any(u => (string)u.AsGodotDictionary()["name"] == required)) failures.Add($"hub shader lacks {required}: {path}");
        }
    }

    // ---------------------------------------------------------------- Sustain refuel orbs (tweaks T3)

    static void CheckOrbs(EclipseHubBeamLayer layer, EclipseEventTimeline timeline, EmitterGridPulseClock clock, EmitterGridTopology topology, List<string> failures)
    {
        var beams = new EclipseBeamSettings();
        double s = timeline.Window.Start, c = clock.CircumferenceMeters, v = clock.SpeedMetersPerSecond;
        float direction = clock.Settings.Direction == EmitterPulseDirection.Prograde ? 1f : -1f;
        EclipseEventSettings settings = timeline.Settings;
        double travel = settings.OrbTravelSeconds;
        var rng = new Random(77);

        // ---- uniform: (travel, ease, radius, min px) reaches the layer and the shaders
        EclipseHubBeamUniforms probe = EclipseHubBeamMath.FromTimeline(timeline, beams, timeline.TauSustain + 10d);
        Expect(failures, probe.Orb == new Vector4(30f, 3f, 350f, 2.5f), $"ev_orb {probe.Orb}");
        layer.BindTimeline(timeline);
        layer.ApplyBeamSettings(layer.Beams);
        Expect(failures, ((Vector4)layer.BeamMaterial!.GetShaderParameter("ev_orb")) == probe.Orb && ((Vector2)layer.BeamMaterial.GetShaderParameter("orb_look")).IsEqualApprox(new Vector2(1500f, 16f)), "ev_orb / orb_look on the near material");

        // ---- the orb twin against the timeline over (site, time) pairs from the launch to past the fade; position and envelope by definition
        int compared = 0, alive = 0;
        double worstEnv = 0d, worstPos = 0d;
        for (int i = 0; i < 500; i++)
        {
            int entry = rng.Next(timeline.SiteCount);
            long n = timeline.RefuelLaunchAtOrAfter(entry, s + timeline.TauSustain + 60d + rng.NextDouble() * 1500d);
            double pass = timeline.RefuelArrivalTime(entry, n);
            double age = i % 6 == 0 ? rng.NextDouble() * (travel + 6d) : rng.NextDouble() * (travel + settings.FlashHoldSeconds + settings.FlashFadeSeconds + 1d) - 0.3d;
            double gridTime = pass + age, tau = gridTime - s;
            EclipseHubBeamUniforms u = EclipseHubBeamMath.FromTimeline(timeline, beams, tau);
            var (env, f) = EclipseHubBeamMath.Orb(u, OrbLanes(clock, gridTime, settings), direction, (float)c, (float)timeline.SiteFireTau(entry), 0f, (float)timeline.Sites.P[entry], timeline.Sites.Column[entry]);
            var (wantEnv, wantF) = timeline.RefuelOrb(entry, tau, gridTime, beams.OrbEaseExponent);
            worstEnv = Math.Max(worstEnv, Math.Abs(env - wantEnv));
            if (wantEnv > 0d) worstPos = Math.Max(worstPos, Math.Abs(f - wantF)); // past the fade the position is moot
            compared++;
            if (wantEnv > 0d) alive++;
            if ((env > 0f) != (wantEnv > 0d) && Math.Abs(age) > 1e-3d && Math.Abs(age - (travel + settings.FlashHoldSeconds + settings.FlashFadeSeconds)) > 1e-3d) failures.Add($"orb onset differs for site {entry} age {age:F4}: twin {env} timeline {wantEnv}");
            if (wantEnv > 0d && Math.Abs(wantF - Math.Pow(Math.Clamp(age / travel, 0d, 1d), 3d)) > 1e-9d) failures.Add($"orb position {wantF} at age {age}");
        }
        Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER orb twin_vs_timeline compared={compared} alive={alive} worst_env={worstEnv:E2} worst_fraction={worstPos:E2}");
        Expect(failures, alive > 300 && worstEnv < 2e-3d && worstPos < 2e-4d, $"orb twin differs from the timeline (env {worstEnv}, fraction {worstPos}, alive {alive})");

        // ---- lift-off and arrival: about 6 km up at 3.2 s, exactly at the hub (fraction 1, full envelope) at the travel time
        {
            int entry = 9;
            long n = timeline.RefuelLaunchAtOrAfter(entry, s + timeline.TauSustain + 300d);
            double pass = timeline.RefuelArrivalTime(entry, n);
            Func<double, (float Env, float F)> at = age =>
            {
                double g = pass + age;
                EclipseHubBeamUniforms u = EclipseHubBeamMath.FromTimeline(timeline, beams, g - s);
                return EclipseHubBeamMath.Orb(u, OrbLanes(clock, g, settings), direction, (float)c, (float)timeline.SiteFireTau(entry), 0f, (float)timeline.Sites.P[entry], timeline.Sites.Column[entry]);
            };
            var (e0, f0) = at(0d); var (eUp, fUp) = at(3.2d); var (eArr, fArr) = at(travel); var (eHold, fHold) = at(travel + settings.FlashHoldSeconds * 0.5d);
            var (eEnd, _) = at(travel + settings.FlashHoldSeconds + settings.FlashFadeSeconds + 0.1d); var (eBefore, _) = at(-0.5d);
            double beamLengthKm = 4998d;
            Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER orb profile env0={e0:F3} liftoff_3.2s_km={fUp * beamLengthKm:F2} arrival_fraction={fArr:F6} hold={eHold:F3} end={eEnd:F3}");
            Expect(failures, e0 == 0f && f0 == 0f && eBefore == 0f, "an orb exists before its launch");
            Expect(failures, fUp * beamLengthKm is > 5d and < 8d && eUp == 1f, $"lift-off: {fUp * beamLengthKm:F2} km at 3.2 s (envelope {eUp})");
            Expect(failures, fArr > 0.99999f && eArr == 1f && fHold == 1f && eHold == 1f, $"arrival: fraction {fArr}, envelope {eArr}");
            Expect(failures, eEnd == 0f, "the orb outlives its fade");
        }

        // ---- no pass outside Sustain launches an orb; the main spire and dead sites have none; no fronts, no orb
        {
            int entry = 5;
            double ignitionPass = timeline.RefuelArrivalTime(entry, timeline.RefuelLaunchAtOrAfter(entry, s + timeline.TauIgnition + 60d));
            EclipseHubBeamUniforms u = EclipseHubBeamMath.FromTimeline(timeline, beams, ignitionPass + 5d - s);
            float p = (float)timeline.Sites.P[entry]; int col = timeline.Sites.Column[entry];
            var lanes = OrbLanes(clock, ignitionPass + 5d, settings);
            Expect(failures, EclipseHubBeamMath.Orb(u, lanes, direction, (float)c, (float)timeline.SiteFireTau(entry), 0f, p, col).Envelope == 0f, "an orb launched during Ignition");
            double sustainPass = timeline.RefuelArrivalTime(entry, timeline.RefuelLaunchAtOrAfter(entry, s + timeline.TauSustain + 120d));
            u = EclipseHubBeamMath.FromTimeline(timeline, beams, sustainPass + 5d - s);
            lanes = OrbLanes(clock, sustainPass + 5d, settings);
            Expect(failures, EclipseHubBeamMath.Orb(u, lanes, direction, (float)c, (float)timeline.SiteFireTau(entry), 0f, p, col).Envelope > 0f, "no orb after a Sustain pass");
            Expect(failures, EclipseHubBeamMath.Orb(u, lanes, direction, (float)c, (float)timeline.SiteFireTau(entry), 1f, p, col).Envelope == 0f, "the main spire launches an orb");
            Expect(failures, EclipseHubBeamMath.Orb(u, lanes, direction, (float)c, EclipseHubBeamMath.NeverFireTau, 0f, p, col).Envelope == 0f, "a dead site launches an orb");
            Expect(failures, EclipseHubBeamMath.Orb(u, ReadOnlySpan<Vector4>.Empty, direction, (float)c, (float)timeline.SiteFireTau(entry), 0f, p, col).Envelope == 0f, "no fronts, no orb");
            // After the cut nothing is left.
            EclipseHubBeamUniforms cut = EclipseHubBeamMath.FromTimeline(timeline, beams, timeline.TauImplosion + 0.5d);
            Expect(failures, EclipseHubBeamMath.Orb(cut, lanes, direction, (float)c, (float)timeline.SiteFireTau(entry), 0f, p, col).Envelope == 0f, "an orb survives the cut");
        }

        // ---- a long travel keeps the last rows' orbs: GetFronts (legacy cutoff) drops the front, GetFrontsWithin keeps it
        {
            var longSettings = new EclipseEventSettings { OrbTravelSeconds = 55d };
            var longTimeline = new EclipseEventTimeline(longSettings, timeline.Window, timeline.Sites, clock, 1337);
            // The site nearest the loop end of column 0: its orb needs the front 55 s after the pass, about 5,760 km behind the loop end... beyond the 4,189 km legacy cutoff.
            int last = 0; double bestQ = -1d;
            for (int i = 0; i < longTimeline.SiteCount; i++) if (longTimeline.Sites.Column[i] == 0 && clock.ToQ(longTimeline.Sites.P[i]) > bestQ) { bestQ = clock.ToQ(longTimeline.Sites.P[i]); last = i; }
            long n = longTimeline.RefuelLaunchAtOrAfter(last, s + longTimeline.TauSustain + 400d);
            double pass = longTimeline.RefuelArrivalTime(last, n), g = pass + 50d, tau = g - s;
            Span<EmitterPulseFront> legacy = stackalloc EmitterPulseFront[32];
            int nLegacy = clock.GetFronts(g, 0, legacy);
            bool legacyHas = false;
            for (int i = 0; i < nLegacy; i++) if (legacy[i].Launch == n) legacyHas = true;
            var within = OrbLanes(clock, g, longSettings, 1);
            EclipseHubBeamUniforms u = EclipseHubBeamMath.FromTimeline(longTimeline, beams, tau);
            float envLegacy = EclipseHubBeamMath.Orb(u, NormalLanes(clock, g, 1), direction, (float)c, (float)longTimeline.SiteFireTau(last), 0f, (float)longTimeline.Sites.P[last], 0).Envelope;
            float envWithin = EclipseHubBeamMath.Orb(u, within, direction, (float)c, (float)longTimeline.SiteFireTau(last), 0f, (float)longTimeline.Sites.P[last], 0).Envelope;
            var (tlEnv, tlF) = longTimeline.RefuelOrb(last, tau, g, 3d);
            Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER orb long_travel T=55 last_row_q_km={bestQ / 1000d:F0} legacy_front_kept={legacyHas} env_legacy={envLegacy:F3} env_within={envWithin:F3} timeline={tlEnv:F3}");
            Expect(failures, tlEnv > 0.99d && envWithin > 0.99f, $"T=55 loses the last row's orb (twin {envWithin}, timeline {tlEnv})");
            Expect(failures, !legacyHas && envLegacy == 0f, "the legacy cutoff was expected to drop this front (the test no longer shows the difference)");
            _ = tlF;
        }

        // ---- the hub heartbeat lands on the arrival: nothing before the first orb arrives, a kick right after it, and the same one frame late at TimeScale 1 and 10
        {
            var hbSettings = new EclipseEventSettings { RelaunchKick = 0d };
            var hb = new EclipseEventTimeline(hbSettings, timeline.Window, timeline.Sites, clock, 1337);
            double firstArrivalTau = double.MaxValue;
            for (int i = 0; i < hb.SiteCount; i++)
            {
                long n = hb.RefuelLaunchAtOrAfter(i, s + hb.TauSustain);
                firstArrivalTau = Math.Min(firstArrivalTau, hb.RefuelArrivalTime(i, n) - s + hbSettings.OrbTravelSeconds);
            }
            Func<double, double> heart = tau => hb.Evaluate(s + tau, s + tau).Heartbeat;
            double before = 0d; for (double t = hb.TauSustain; t < firstArrivalTau - 0.01d; t += 0.37d) before = Math.Max(before, heart(t));
            double after = heart(firstArrivalTau + 0.05d);
            Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER orb heartbeat first_arrival_tau={firstArrivalTau:F3} (Sustain {hb.TauSustain:F2}, travel {hbSettings.OrbTravelSeconds:F0}) max_before={before:E2} after={after:E2}");
            Expect(failures, firstArrivalTau >= hb.TauSustain + hbSettings.OrbTravelSeconds - 1e-6d, "the first arrival is before the travel time");
            Expect(failures, before == 0d && after > 0d, $"heartbeat before the first arrival {before}, 0.05 s after it {after}");
            foreach (float scale in new[] { 1f, 10f })
            {
                var engine = new EclipseEventEngine { SynchronousSolve = true };
                engine.SetSettings(hbSettings);
                engine.SetSolver(new EclipseWindowSolver());
                engine.Bind(timeline.Sites, clock, 1337);
                var stub = new StepClock { Scale = scale };
                engine.Update(stub, 0d, EclipseObserverPose.None);
                EclipseEventTimeline t = engine.Timeline!;
                double ws = t.Window.Start, frame = scale / 60d;
                double arrival = double.MaxValue;
                for (int i = 0; i < t.SiteCount; i++) arrival = Math.Min(arrival, t.RefuelArrivalTime(i, t.RefuelLaunchAtOrAfter(i, ws + t.TauSustain)) + hbSettings.OrbTravelSeconds);
                stub.Seconds = arrival - 0.5d;
                engine.Update(stub, 0d, EclipseObserverPose.None);
                double firstKick = double.NaN; double early = 0d;
                for (int f = 0; f < 400 && double.IsNaN(firstKick); f++)
                {
                    stub.Seconds += frame;
                    engine.Update(stub, 0d, EclipseObserverPose.None);
                    if (stub.Seconds < arrival - 1e-6d) early = Math.Max(early, engine.State.Heartbeat);
                    else if (engine.State.Heartbeat > 0d) firstKick = stub.Seconds;
                }
                Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER orb heartbeat timing x{scale:0} frame={frame * 1000d:F1}ms kick_after_arrival_ms={(firstKick - arrival) * 1000d:F1} early={early:E2}");
                Expect(failures, early == 0d && !double.IsNaN(firstKick) && firstKick - arrival <= frame + 1e-6d, $"x{scale}: heartbeat kick {(firstKick - arrival) * 1000d:F1} ms after the arrival (frame {frame * 1000d:F1} ms), early {early}");
            }
        }

        // ---- the layer: orbs are counted, the beam body is not relit, steady frames write nothing
        {
            layer.RefuelFrontClock = clock;
            double gridNow = s + timeline.TauSustain + 400d;
            layer.ApplyEventTime(timeline.TauSustain + 400d, true, 0.016, gridNow);
            int orbs = layer.OrbsInFlight();
            int lit = layer.LitBeamCount();
            Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER orb layer orbs_in_flight={orbs} lit_beams={lit} status='{layer.DescribeStatus()}'");
            Expect(failures, orbs is > 40 and < 120, $"orbs in flight {orbs} (about 64 expected: six rows per 3 s over about 32 s)");
            Expect(failures, lit <= 1, $"a refuel orb relit {lit - 1} regular beams");
            Expect(failures, layer.DescribeStatus().Contains($"orbs in flight {orbs}"), "status text lacks the orb count");
            long custom = layer.CustomDataWriteCount, transforms = layer.TransformWriteCount, colors = layer.ColorWriteCount;
            for (int i = 0; i < 60; i++) layer.ApplyEventTime(timeline.TauSustain + 400d + i / 60d, true, 1d / 60d, gridNow + i / 60d);
            Expect(failures, layer.CustomDataWriteCount == custom && layer.TransformWriteCount == transforms && layer.ColorWriteCount == colors, "orb frames wrote per-beam data");
            // The beam body of a regular beam is dark in Sustain whatever its orb does: the CPU level at the crown is 0 once the hand-off is over.
            int regular = 1;
            while (layer.GetOrb(regular).Envelope > 0f && regular < 50) regular++;
            Expect(failures, layer.CrownLevel(regular) <= 1f && EclipseHubBeamMath.Handoff(layer.Uniforms, 0f) == 0f, "regular beam still lit after the hand-off");
            layer.RefuelFrontClock = null;
        }
    }
}
