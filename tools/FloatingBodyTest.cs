using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T3R-6 (docs/work-orders/terrain-vegetation/T3.md, Spec F1 to F3): the kinematic floating integrator against a synthetic channel (a pool, a chute, a second pool, banks that
/// slope up at 0.5 m per metre). Checks: rest at surface minus draft and a release from 0.5 m, 95 % of a 2 m/s flow within three drag time constants, the chute, stranding with sleep
/// and wake (a sleeping body costs no flow sample), the may_strand-off push back, determinism and frame-rate independence, canonical state under an anchor change, and the cost of 32 bodies.
/// Negative control: a reversed lift spring must fail the rest check. Prints FLOATING_BODY=PASS|FAIL. Headless: tools/run_tests.ps1 -Tests floating_body_test.
/// </summary>
public partial class FloatingBodyTest : RefCounted
{
    private const double Draft = 0.8;
    private const double CostPrintMs = 0.06, CostFailMs = 0.2;

    private readonly List<string> _failures = new();
    private void Fail(string message) { if (_failures.Count < 30) _failures.Add(message); }
    private void Check(bool ok, string message) { if (!ok) Fail(message); }
    private static void Detail(string message) => Console.WriteLine("FLOATING_BODY_DETAIL " + message);

    /// <summary>
    /// A straight channel along around, centred on across 0. Water surface 100 m before around 200, a smooth 3 m drop over the 40 m chute, 97 m after. The bed is 2 m below the surface
    /// out to 6 m from the centre, then rises 0.5 m per metre; water ends where the ground meets the surface (10 m from the centre). Velocity is along around only.
    /// </summary>
    private sealed class Channel : IWaterFlowSource
    {
        public double PoolSpeed, ChuteSpeed, ChuteStart = 200.0;
        public const double ChuteLength = 40.0, ChuteDrop = 3.0;
        public long Samples;

        public double SurfaceAt(double around)
        {
            double t = Math.Clamp((around - ChuteStart) / ChuteLength, 0.0, 1.0);
            return 100.0 - ChuteDrop * t * t * (3.0 - 2.0 * t);
        }

        public double GroundAt(double around, double across) => SurfaceAt(around) - 2.0 + 0.5 * Math.Max(0.0, Math.Abs(across) - 6.0);

        public WaterFlowSample Sample(double around, double across, double height)
        {
            Samples++;
            double surface = SurfaceAt(around);
            if (GroundAt(around, across) >= surface)
                return new WaterFlowSample(InlandWaterKind.None, double.NaN, double.NaN, 0, 0, 0, -1, RiverClass.Calm, 0, 0, 0);
            double t = (around - ChuteStart) / ChuteLength;
            double v = t >= 0.0 && t <= 1.0 ? ChuteSpeed : PoolSpeed;
            return new WaterFlowSample(InlandWaterKind.River, surface, GroundAt(around, across), v, 0.0, v, 0, RiverClass.Calm, 0, 0, 0);
        }
    }

    public int Run()
    {
        try
        {
            Rest();
            RestNegativeControl();
            Flow();
            Chute();
            Strand();
            MayStrandOff();
            Determinism();
            Anchor();
            Cost();
        }
        catch (Exception e) { Console.Error.WriteLine("FLOATING_BODY=FAIL exception " + e); return 1; }
        if (_failures.Count != 0) { Console.Error.WriteLine("FLOATING_BODY=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("FLOATING_BODY=PASS");
        return 0;
    }

    private static RingFloatingBody NewBody(Channel ch, bool mayStrand = true)
        => new() { Flow = ch, Ground = ch.GroundAt, Draft = Draft, MayStrand = mayStrand };

    // ------------------------------------------------------------------ rest and release

    private static (double SettleError, double Overshoot, double RestError) RestRun(double liftSign)
    {
        var p = BuoyancyParams.Create(Draft);
        p.LiftSign = liftSign;
        var input = new BuoyancyInput { Surface = 100.0, Ground = 90.0 };
        double target = 100.0 - Draft;
        var s = new BuoyancyState { Height = target + 0.5 };
        double dt = 1.0 / 60.0, undershoot = 0.0;
        double settle = double.NaN;
        for (int i = 1; i <= 600; i++)
        {
            RingBuoyancy.Step(ref s, p, input, dt);
            undershoot = Math.Max(undershoot, -(s.Height - target));
            if (i == 180) settle = Math.Abs(s.Height - target);
            if (double.IsNaN(s.Height) || Math.Abs(s.Height - target) > 1e6) return (double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        }
        return (settle, undershoot / 0.5, Math.Abs(s.Height - target));
    }

    private void Rest()
    {
        var ch = new Channel();
        RingFloatingBody body = NewBody(ch);
        body.Place(50.0, 0.0, 100.0 - Draft);
        for (int i = 0; i < 300; i++) body.Step(1.0 / 60.0);
        double restErr = Math.Abs(body.State.Height - (100.0 - Draft));
        Check(restErr <= 0.02, $"body at rest: height off the draft by {restErr:F4} m");

        var (settle, overshoot, final) = RestRun(1.0);
        Detail($"rest: body error {restErr * 100:F2} cm; release 0.5 m: error at 3 s {settle * 100:F2} cm, overshoot {overshoot * 100:F2} %, final {final * 100:F2} cm");
        Check(settle <= 0.02, $"release: {settle:F4} m from the draft at 3 s");
        Check(overshoot <= 0.10, $"release: overshoot {overshoot * 100:F1} %");
        Check(final <= 0.02, $"release: final error {final:F4} m");
        body.Free();
    }

    private void RestNegativeControl()
    {
        var (settle, overshoot, final) = RestRun(-1.0);
        bool restFails = !(settle <= 0.02 && overshoot <= 0.10 && final <= 0.02);
        Detail($"negative control (reversed lift): error at 3 s {settle:F3} m, overshoot {overshoot * 100:F1} %, final {final:F3} m -> rest check {(restFails ? "fails as required" : "PASSES, control is broken")}");
        Check(restFails, "negative control: a reversed lift sign passed the rest check");
    }

    // ------------------------------------------------------------------ flow

    private void Flow()
    {
        var ch = new Channel { PoolSpeed = 2.0, ChuteStart = 1e9 };
        RingFloatingBody body = NewBody(ch);
        body.Place(0.0, 0.0, 100.0 - Draft);
        double dt = 1.0 / 60.0, tau = 1.0 / RingBuoyancy.DefaultDrag, reached = double.NaN, worst = 0.0;
        for (int i = 1; i <= 1200; i++)
        {
            body.Step(dt);
            double t = i * dt, v = body.State.VAround;
            if (double.IsNaN(reached) && v >= 0.95 * 2.0) reached = t;
            if (!double.IsNaN(reached) && t >= 3.0 * tau) worst = Math.Max(worst, Math.Abs(v - 2.0) / 2.0);
        }
        Detail($"flow 2 m/s: 95 % at {reached:F2} s (limit {3 * tau + dt:F2} s), worst deviation afterwards {worst * 100:F2} %, travelled {body.State.Around:F1} m");
        Check(reached <= 3.0 * tau + dt, $"flow: 95 % reached at {reached:F2} s");
        Check(worst <= 0.05, $"flow: held within {worst * 100:F1} %");
        body.Free();
    }

    private void Chute()
    {
        var ch = new Channel { PoolSpeed = 1.5, ChuteSpeed = 5.0 };
        RingFloatingBody body = NewBody(ch);
        body.Place(100.0, 0.0, 100.0 - Draft);
        double dt = 1.0 / 60.0, worst = 0.0;
        for (int i = 0; i < 60 * 150 && body.State.Around < 320.0; i++)
        {
            body.Step(dt);
            double target = ch.SurfaceAt(body.State.Around) - Draft;
            worst = Math.Max(worst, Math.Abs(body.State.Height - target));
        }
        Detail($"chute: reached around {body.State.Around:F1} m, worst distance from surface minus draft {worst:F3} m, pitch now {body.State.Pitch:F3}");
        Check(body.State.Around >= 320.0, "chute: the body did not come down the chute");
        Check(worst <= 0.3, $"chute: {worst:F3} m from the surface");
        body.Free();
    }

    // ------------------------------------------------------------------ banks

    private static double DepthAt(Channel ch, in BuoyancyState s) => ch.SurfaceAt(s.Around) - ch.GroundAt(s.Around, s.Across);

    private void Strand()
    {
        var ch = new Channel();
        RingFloatingBody body = NewBody(ch);
        body.Place(100.0, 7.0, 100.0 - Draft);
        body.State.VAcross = 2.0;
        double dt = 1.0 / 60.0, stopped = double.NaN, slept = double.NaN;
        for (int i = 1; i <= 60 * 12; i++)
        {
            body.Step(dt);
            double t = i * dt;
            double horizontal = Math.Sqrt(body.State.VAround * body.State.VAround + body.State.VAcross * body.State.VAcross);
            if (double.IsNaN(stopped) && horizontal < RingFloatingBody.SleepSpeed) stopped = t;
            if (double.IsNaN(slept) && body.Sleeping) { slept = t; break; }
        }
        double depth = DepthAt(ch, body.State);
        Detail($"strand: stopped at {stopped:F2} s, slept at {slept:F2} s, across {body.State.Across:F2} m, depth {depth:F2} m (draft {Draft})");
        Check(stopped <= 5.0, $"strand: stopped at {stopped:F2} s");
        Check(depth > 0.0 && depth < Draft, $"strand: depth {depth:F2} m is not under the draft");
        Check(!double.IsNaN(slept) && slept - stopped <= 3.0 + 0.2, $"strand: asleep {slept - stopped:F2} s after stopping");

        long samples = ch.Samples;
        for (int i = 0; i < 200; i++) body.Step(dt);
        Check(ch.Samples == samples, $"a sleeping body took {ch.Samples - samples} flow samples");
        long before = ch.Samples;
        bool stillWoke = body.WakeIfWaterMoves();
        Check(ch.Samples == before + 1 && !stillWoke && body.Sleeping, "wake check: expected one sample and no wake in still water");
        ch.PoolSpeed = 1.0;
        Check(body.WakeIfWaterMoves() && !body.Sleeping, "wake check: water at 1 m/s did not wake the body");
        body.Place(100.0, 7.0, 100.0 - Draft);
        body.State.VAcross = 2.0;
        for (int i = 0; i < 60 * 12; i++) body.Step(dt);
        Check(body.Sleeping, "strand: the second run did not sleep");
        ch.PoolSpeed = 0.0;
        samples = ch.Samples;
        body.Wake();
        body.Step(dt);
        Check(!body.Sleeping && ch.Samples == samples + 1, "Wake() did not restart the body with a sample");
        body.Free();
    }

    private void MayStrandOff()
    {
        var ch = new Channel();
        RingFloatingBody body = NewBody(ch, mayStrand: false);
        body.Place(100.0, 7.0, 100.0 - Draft);
        body.State.VAcross = 2.0;
        double dt = 1.0 / 60.0, maxAcross = 7.0;
        for (int i = 0; i < 60 * 30; i++)
        {
            body.Step(dt);
            maxAcross = Math.Max(maxAcross, body.State.Across);
        }
        double depth = DepthAt(ch, body.State);
        Detail($"may_strand off: reached across {maxAcross:F2} m, ended at {body.State.Across:F2} m, depth {depth:F2} m (draft {Draft})");
        Check(maxAcross > 8.4, "may_strand off: the body never reached shallow water, so nothing was tested");
        Check(depth >= Draft, $"may_strand off: ended at depth {depth:F2} m, under the draft");
        body.Free();
    }

    // ------------------------------------------------------------------ determinism and frame rate

    private static BuoyancyState Scenario(double hz, int seconds)
    {
        var ch = new Channel { PoolSpeed = 1.5, ChuteSpeed = 5.0 };
        RingFloatingBody body = NewBody(ch);
        body.Place(170.0, 0.5, 100.0 - Draft + 0.3);
        double dt = 1.0 / hz;
        int n = (int)Math.Round(seconds * hz);
        for (int i = 0; i < n; i++) body.Step(dt);
        BuoyancyState s = body.State;
        body.Free();
        return s;
    }

    private void Determinism()
    {
        BuoyancyState a = Scenario(60.0, 60), b = Scenario(60.0, 60);
        Check(a.Around == b.Around && a.Across == b.Across && a.Height == b.Height && a.VAround == b.VAround && a.Yaw == b.Yaw && a.Pitch == b.Pitch, "two identical runs differ");
        double worstPos = 0.0, worstHeight = 0.0;
        BuoyancyState r = Scenario(120.0, 60);
        foreach (double hz in new[] { 30.0, 60.0 })
        {
            BuoyancyState s = Scenario(hz, 60);
            double dPos = Math.Abs(s.Around - r.Around) / (r.Around - 170.0);
            double dHeight = Math.Abs(s.Height - r.Height);
            worstPos = Math.Max(worstPos, dPos);
            worstHeight = Math.Max(worstHeight, dHeight);
            Detail($"frame rate {hz:F0} Hz vs 120 Hz after 60 s: travel differs {dPos * 100:F3} %, height {dHeight * 100:F2} cm, around {s.Around:F2} vs {r.Around:F2}");
        }
        Check(worstPos <= 0.02, $"frame rate: travel differs by {worstPos * 100:F2} %");
        Check(worstHeight <= 0.02, $"frame rate: height differs by {worstHeight:F3} m");
    }

    // ------------------------------------------------------------------ floating origin

    private void Anchor()
    {
        double circumference = CertifiedRingScale.EffectiveCircumferenceMeters, radius = CertifiedRingScale.EffectiveRadiusMeters;
        const double around = 981_748.0, across = 108_327.0, height = 5.0;
        RingFloatingBody body = new();
        body.Place(around, across, height, 0.7);
        body.State.Pitch = 0.1;
        BuoyancyState before = body.State;
        double worst = 0.0;
        Vector3 firstForward = default;
        int index = 0;
        foreach (double anchorAround in new[] { around - 200.0, around + 16_000.0 })
        {
            var s = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, radius, 0.0, 0.0, circumference, -1000.0, 1000.0,
                new RingCanonicalPosition(anchorAround, 5.0, across));
            Transform3D t = body.ComputeWorldTransform(s);
            RingCanonicalPosition back = ContainmentFieldPlacement.WorldToCanonical(s, t.Origin);
            double err = Math.Max(Math.Abs(back.AroundMeters - around), Math.Max(Math.Abs(back.VerticalMeters - height), Math.Abs(back.AcrossMeters - across)));
            worst = Math.Max(worst, err);
            Check(Math.Abs(t.Basis.X.Length() - 1f) < 1e-4 && Math.Abs(t.Basis.X.Dot(t.Basis.Y)) < 1e-4, "the body basis is not orthonormal");
            ContainmentFieldPlacement.AxesAt(s, around, out Vector3 aroundDir, out _, out Vector3 acrossDir);
            if (index++ == 0) firstForward = t.Basis.X;
            Check(t.Basis.X.Dot(aroundDir) > 0f && t.Basis.X.Dot(acrossDir) > 0f, "the body heading is not between the around and across axes");
        }
        Detail($"anchor change: canonical point back within {worst * 1000:F2} mm for both anchors; state unchanged {(body.State.Equals(before) ? "yes" : "no")}; first forward {firstForward}");
        Check(worst <= 0.01, $"anchor change: canonical point back off by {worst:F4} m");
        Check(body.State.Equals(before), "anchor change: the canonical state moved");
        body.Free();
    }

    // ------------------------------------------------------------------ cost

    private void Cost()
    {
        var ch = new Channel { PoolSpeed = 2.0, ChuteStart = 1e12 };
        var bodies = new RingFloatingBody[32];
        for (int i = 0; i < bodies.Length; i++)
        {
            bodies[i] = NewBody(ch);
            bodies[i].Place(10.0 * i, (i % 5 - 2) * 1.0, 100.0 - Draft);
        }
        double dt = 1.0 / 60.0;
        for (int w = 0; w < 200; w++) foreach (RingFloatingBody b in bodies) b.Step(dt);
        const int frames = 3000;
        var clock = Stopwatch.StartNew();
        for (int f = 0; f < frames; f++) foreach (RingFloatingBody b in bodies) b.Step(dt);
        double ms = clock.Elapsed.TotalMilliseconds / frames;
        bool awake = true;
        foreach (RingFloatingBody b in bodies) awake &= !b.Sleeping;
        Detail($"cost: 32 bodies step in {ms:F4} ms per frame (target {CostPrintMs} ms, fails above {CostFailMs} ms; dev build, synthetic source); all awake {awake}");
        Check(awake, "cost: bodies fell asleep, so the timing is not of awake bodies");
        Check(ms <= CostFailMs, $"cost: 32 bodies took {ms:F4} ms");
        foreach (RingFloatingBody b in bodies) b.Free();
    }
}
