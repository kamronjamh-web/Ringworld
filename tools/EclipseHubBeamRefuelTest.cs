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
        Expect(failures, layer.RefuelFronts.Length == 6 && (int)near.GetShaderParameter("eg_front_count") == 6 && (float)near.GetShaderParameter("eg_direction") == direction
            && Math.Abs((float)near.GetShaderParameter("eg_circumference") - (float)c) < 1f, $"refuel fronts in Sustain {layer.RefuelFronts.Length}");
        Vector4[] expected = NormalLanes(clock, gridNow);
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
            foreach (string required in new[] { "ev_flash", "ev_refuel", "eg_fronts", "eg_front_count", "eg_direction", "eg_circumference" })
                if (!uniforms.Any(u => (string)u.AsGodotDictionary()["name"] == required)) failures.Add($"hub shader lacks {required}: {path}");
        }
    }
}
