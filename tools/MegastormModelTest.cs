using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO1 (docs/megastorm-design.md section 8): the pure megastorm model. Re-anchor continuity, ring wrap, inactive before the trigger,
/// TimeToArrival = xi/v at 5/350/500 m/s, slab edge error across the width, suppression/zones/bands, and bit-identical results across
/// runs and a JSON round trip.
/// Run: Godot --headless --path . --script res://tools/megastorm_model_test.gd. Prints MEGASTORM_MODEL=PASS.
/// </summary>
public partial class MegastormModelTest : RefCounted
{
    private const double C = 31415936.0, W = 318000.0;

    public int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            ReAnchor(Check);
            Wrap(Check);
            Inactive(Check);
            Arrival(Check);
            Slab(Check);
            Zones(Check);
            Determinism(Check);
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); Console.Error.WriteLine("MEGASTORM_MODEL=FAIL exception " + e.Message); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("MEGASTORM_MODEL=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("MEGASTORM_MODEL=PASS");
        return 0;
    }

    private static MegastormModel Make(double around, int dir, double start, float speed, int seed = 1234)
    {
        var m = new MegastormModel();
        m.Trigger(new MegastormTrigger("t", around, dir, start, speed, 1800f, seed));
        return m;
    }

    private static void ReAnchor(Action<bool, string> check)
    {
        var m = Make(1.0e6, 1, 100.0, 350f);
        double now = 5000.0;
        double before = m.AroundAt(now);
        check(m.SetSpeed(now, 100f), "SetSpeed refused");
        double after = m.AroundAt(now);
        check(Math.Abs(after - before) < 1e-3, $"re-anchor jump {Math.Abs(after - before)} m");
        check(Math.Abs(m.AroundAt(now + 100.0) - (before + 100.0 * 100.0)) < 1e-3, "post-anchor speed not applied");
        check(Math.Abs(m.AroundAt(now - 1.0) - (before - 350.0)) < 1e-3, "history before the anchor changed");
        for (int i = 0; i < 40; i++) m.SetSpeed(now + 10.0 * (i + 1), 50f + i);
        check(m.State.Anchors.Count == MegastormModel.MaxAnchors, "anchors not capped at 16");
        check(!m.SetSpeed(now, 10f), "SetSpeed into the past accepted");
        var clamp = Make(0, 1, 0, 9999f);
        check(clamp.SpeedAt(1.0) == 500f, "speed not clamped to 500");
    }

    private static void Wrap(Action<bool, string> check)
    {
        var up = Make(C - 10000.0, 1, 0.0, 350f);
        double s = up.AroundAt(100.0);
        check(Math.Abs(s - 25000.0) < 1e-6, $"wrap + direction s={s}");
        var down = Make(10000.0, -1, 0.0, 350f);
        s = down.AroundAt(100.0);
        check(Math.Abs(s - (C - 25000.0)) < 1e-6, $"wrap - direction s={s}");
        // Points either side of 0/C see a continuous xi: the line is at 25 km; 30 km ahead is around 55 km, 30 km behind is C - 5 km.
        double t = 100.0;
        check(Math.Abs(up.X(55000.0, t) + 30000.0) < 1e-6, "x ahead across wrap");
        check(Math.Abs(up.X(C - 5000.0, t) - 30000.0) < 1e-6, "x behind across wrap");
        check(Math.Abs(down.X(C - 55000.0, t) + 30000.0) < 1e-6, "x ahead across wrap (dir -1)");
        check(MegastormModel.WrapSigned(C - 1.0, C) is > -2.0 and < 0.0, "WrapSigned near C");
    }

    private static void Inactive(Action<bool, string> check)
    {
        var m = Make(1.0e6, 1, 1000.0, 350f);
        var list = new List<MegastormRecord>();
        check(!m.Evaluate(999.0).Active && m.Evaluate(1000.0).Active, "active edge at trigger time");
        check(m.BuildBundle(999.0, 100000.0, list) == 0 && list.Count == 0, "bundle before trigger");
        var q = m.Query(1.0e6, 100000.0, 999.0);
        check(!q.Active && q.Phase == MegastormPhase.Inactive && q.Danger == 0.0f && q.Suppression == 0.0f, "query before trigger");
        check(m.SuppressionAt(1.0e6, 100000.0, 999.0) == 0.0f, "suppression before trigger");
        check(MegastormProfile.Column(m, 0.0, 100000.0, 999.0).Equals(default(MegastormColumn)), "column before trigger");
        var untriggered = new MegastormModel();
        check(!untriggered.Evaluate(5.0).Active && untriggered.BuildBundle(5.0, 1.0, list) == 0, "untriggered model active");
        // Stop: fades to inactive.
        check(m.Stop(5000.0, 100f), "Stop refused");
        check(m.Evaluate(5050.0).Active && m.Evaluate(5050.0).Level < 1.0f, "stop fade");
        check(!m.Evaluate(5100.0).Active && m.BuildBundle(5100.0, 1.0, list) == 0, "inactive after stop fade");
        check(!m.Trigger(new MegastormTrigger("other", 0, 1, 6000.0)), "second trigger accepted");
        // Formation ramp.
        var f = Make(0, 1, 0.0, 350f);
        check(f.Evaluate(0.0).Level == 0.0f && Math.Abs(f.Evaluate(900.0).Level - 0.5f) < 1e-6 && f.Evaluate(1800.0).Level == 1.0f, "formation smoothstep");
        check(f.Evaluate(1800.0).TopMeters == MegastormModel.FullTopMeters && f.Evaluate(0.0).TopMeters == MegastormModel.FormingTopMeters, "formation top");
        float breath = f.Evaluate(40000.0).Strength;
        check(breath >= 0.8f && breath <= 1.0f, "strength out of 0.8-1.0");
    }

    private static void Arrival(Action<bool, string> check)
    {
        foreach (float v in new[] { 5f, 350f, 500f })
        {
            var m = Make(2.0e6, 1, 0.0, v);
            double t = 3000.0;
            foreach (double across in new[] { 0.0, 80000.0, 159000.0, 318000.0 })
            foreach (double ahead in new[] { 300000.0, 1000000.0 })
            {
                double around = MegastormModel.WrapPositive(m.AroundAt(t) + ahead, C);
                var q = m.Query(around, across, t);
                // TTA is the true edge crossing (meander included): advancing by it puts xi within 0.1 s of travel (v +- the meander's ~1 m/s) of zero.
                double xiAt = m.Xi(around, across, t + q.TimeToArrivalSeconds);
                check(Math.Abs(xiAt) <= 0.1 * (v - 1.0), $"xi after TTA v={v} across={across}: {xiAt}");
                check(Math.Abs(q.DistanceToEdgeMeters + q.XiMeters) < 1e-9 && q.Phase != MegastormPhase.Inactive, "distance to edge");
            }
        }
        // Phase walk at the default speed (formed).
        var d = Make(0.0, 1, 0.0, 350f);
        double tf = 4000.0;
        MegastormPhase PhaseAtXi(double xi)
        {
            double a = MegastormModel.WrapPositive(d.AroundAt(tf) - (xi + d.LeadOffset(W / 2, tf)), C);
            return d.Query(a, W / 2, tf).Phase;
        }
        check(PhaseAtXi(-400000.0) == MegastormPhase.Clear, "phase clear");
        check(PhaseAtXi(-150000.0) == MegastormPhase.Precursor, "phase precursor");
        check(PhaseAtXi(-40000.0) == MegastormPhase.Approach, "phase approach");
        check(PhaseAtXi(-5000.0) == MegastormPhase.GustFront, "phase gust");
        check(PhaseAtXi(10000.0) == MegastormPhase.Wall, "phase wall");
        check(PhaseAtXi(40000.0) == MegastormPhase.Transition, "phase transition");
        check(PhaseAtXi(200000.0) == MegastormPhase.Trailing, "phase trailing");
        check(PhaseAtXi(600000.0) == MegastormPhase.Aftermath, "phase aftermath");
        check(PhaseAtXi(5.0e6) == MegastormPhase.Clear, "phase clear behind");
        check(Make(0, 1, 0.0, 350f).Query(100000.0, W / 2, 10.0).Phase == MegastormPhase.Forming, "phase forming");
        check(MegastormBands.BandWidthMeters(350, 60) == 15750.0 && MegastormBands.ApproachReachMeters(350, 60) == 52500.0, "bands at 350");
        check(MegastormBands.BandWidthMeters(25, 60) == 2000.0 && MegastormBands.ApproachReachMeters(25, 60) == 10000.0, "bands at 25");
        check(MegastormBands.BandWidthMeters(500, 600) == 20000.0 && MegastormBands.ApproachReachMeters(500, 600) == 60000.0, "bands cap");
        // TriggerToReach.
        var r = new MegastormModel();
        check(!r.TriggerToReach(5.0e6, 100000.0, 2000.0, 1, "r", 0.0), "TriggerToReach accepted a too-short lead");
        check(r.TriggerToReach(5.0e6, 100000.0, 20000.0, 1, "r", 0.0), "TriggerToReach refused a long lead");
        double xiReach = r.Xi(5.0e6, 100000.0, 20000.0);
        check(Math.Abs(xiReach) < 1e-6, $"TriggerToReach xi at arrival {xiReach}");
    }

    private static void Slab(Action<bool, string> check)
    {
        var m = Make(3.0e6, 1, 0.0, 350f);
        var list = new List<MegastormRecord>();
        double t = 5000.0, queryAcross = W / 2;
        check(m.BuildBundle(t, queryAcross, list) == 4, "bundle count");
        check(list.Select(r => r.Id).SequenceEqual(new[] { -30001, -30002, -30003, -30004 }), "bundle ids");
        check(list[0].Kind == 6 && list[0].Profile == 12 && list[1].Kind == 6 && list[1].Profile == 12, "W/A kind and profile");
        check(Math.Abs(list[2].SemiAlongMeters - 242857.142857) < 1e-3 && Math.Abs(list[2].CenterXiMeters - 220000.0) < 1e-6, "trailing extents");
        check(list.All(r => r.DriftMetersPerSecond == 350f && r.SemiAcrossMeters == 1.0e7), "drift / semi-across");
        for (int i = 0; i < 4; i++)
        {
            var r = list[i];
            // Centre xi at the query across is exact.
            double a = MegastormModel.WrapPositive(r.CenterAroundMeters, C);
            check(Math.Abs(m.Xi(a, queryAcross, t) - r.CenterXiMeters) < 1e-6, $"record {i} centre xi");
            // Slab edge error across the width: where q = 1 versus the straight slab edge (half depth), bisecting q along around.
            double worst = 0.0;
            for (double across = 0.0; across <= W; across += W / 64.0)
            {
                double lo = 0.0, hi = r.SemiAlongMeters * 1.5;
                for (int k = 0; k < 60; k++)
                {
                    double mid = 0.5 * (lo + hi);
                    if (m.SlabNorm(r, r.CenterAroundMeters + mid, across) < 1.0) lo = mid; else hi = mid;
                }
                worst = Math.Max(worst, Math.Abs(lo - r.SemiAlongMeters));
            }
            // W and A (the profile-12 slabs) are the design's slabs; T and P are 3.5-9x deeper so their error scales with half depth.
            double limit = i < 2 ? 10.0 : 40.0;
            check(worst < limit, $"slab error record {i}: {worst} m");
        }
        // Suppression profile.
        check(m.Suppression(-200000.0) == 0.0f && m.Suppression(-150000.0) == 0.0f, "suppression far ahead");
        check(m.Suppression(-70000.0) == 1.0f && m.Suppression(0.0) == 1.0f && m.Suppression(390000.0) == 1.0f, "suppression full");
        check(m.Suppression(990000.0) == 0.0f && m.Suppression(2.0e6) == 0.0f, "suppression decayed");
        float sa = m.Suppression(-110000.0), sb = m.Suppression(-90000.0);
        check(sa > 0.0f && sa < sb && sb < 1.0f, "suppression ramp monotonic");
        check(m.Suppression(700000.0) > 0.0f && m.Suppression(700000.0) < 1.0f, "suppression decay");
    }

    private static void Zones(Action<bool, string> check)
    {
        var m = Make(0.0, 1, 0.0, 350f);
        double t = 4000.0;
        float Danger(double xi) => m.Danger(xi, xi < 0 ? -xi / 350.0 : 1e9, 350.0);
        check(Danger(10000.0) > 0.99f && Danger(60000.0) == 0.25f && Danger(-1000000.0) == 0.0f, "danger wall/trailing/far");
        check(Math.Abs(Danger(47000.0) - 0.5f) < 1e-6, $"danger transition {Danger(47000.0)}");
        check(Danger(-100.0) >= 0.75f * 0.99f, "danger gust front");
        check(m.Danger(-1e5, 100.0, 350.0, 0.0f) < m.Danger(-1e5, 100.0, 350.0, 1.0f), "danger shelter factor");
        // Column: wall, shelf, anvil, ceiling, buttress.
        var wall = MegastormProfile.Column(m, 10000.0 + m.LeadOffset(W / 2, t), W / 2, t);
        check(wall.BodyBase == 500.0f && wall.BodyTop >= 15000.0f && wall.BodyTop <= 18000.0f, $"wall column {wall.BodyBase}/{wall.BodyTop}");
        var shelf = MegastormProfile.Column(m, 0.0 + m.LeadOffset(W / 2, t), W / 2, t);
        check(shelf.BodyBase == 300.0f, "shelf base");
        var ahead = MegastormProfile.Column(m, -30000.0 + m.LeadOffset(W / 2, t), W / 2, t);
        check(ahead.BodyTop == 0.0f && ahead.CanopyCoverage > 0.0f && ahead.CanopyUnderside > 9000.0f && ahead.CanopyUnderside <= 12000.0f, "anvil ahead of the wall");
        var capped = MegastormProfile.Column(m, 10000.0 + m.LeadOffset(W / 2, t), W / 2, t, _ => 9700.0f);
        check(capped.BodyTop <= 9700.0f && capped.CanopyTop <= 9700.0f, "ceiling function");
        var rim = MegastormProfile.Column(m, 10000.0 + m.LeadOffset(100.0, t), 100.0, t);
        check(rim.BodyTop >= 16900.0f, "rim buttress");
        var far = MegastormProfile.Column(m, 200000.0, W / 2, t);
        check(far.BodyTop == 0.0f && far.CanopyTop == 0.0f, "no column far behind");
    }

    private static List<long> Fingerprint(MegastormModel m)
    {
        var bits = new List<long>();
        var list = new List<MegastormRecord>();
        void D(double d) => bits.Add(BitConverter.DoubleToInt64Bits(d));
        void F(float f) => bits.Add(BitConverter.SingleToInt32Bits(f));
        for (double t = 0.0; t < 200000.0; t += 7919.0)
        {
            MegastormFrame fr = m.Evaluate(t);
            D(fr.AroundMeters); F(fr.Strength); F(fr.Level); F(fr.TopMeters);
            for (int a = 0; a < 5; a++)
            {
                double across = a * W / 4.0, around = MegastormModel.WrapPositive(fr.AroundMeters + (a - 2) * 90000.0, C);
                var q = m.Query(around, across, t);
                D(q.XiMeters); D(q.TimeToArrivalSeconds); D(q.TimeToClearSeconds); F(q.Danger); F(q.Suppression); bits.Add((long)q.Phase);
                MegastormColumn c = MegastormProfile.Column(m, 5000.0 * a, across, t);
                F(c.BodyBase); F(c.BodyTop); F(c.CanopyUnderside); F(c.CanopyTop); F(c.CanopyCoverage); F(c.Mammatus); F(c.TowerCoverage); F(c.RainShaft);
                m.BuildBundle(t, across, list);
                foreach (var r in list) { D(r.CenterAroundMeters); D(r.CenterAcrossMeters); D(r.SemiAlongMeters); F(r.Density); F(r.Precipitation); }
            }
        }
        return bits;
    }

    private static MegastormModel Scripted()
    {
        var m = new MegastormModel();
        m.Trigger(new MegastormTrigger("det", 31000000.0, -1, 500.0, 350f, 1800f, 77));
        m.SetSpeed(20000.0, 120f);
        m.SetSpeed(60000.0, 480f);
        m.Stop(150000.0, 3600f);
        return m;
    }

    private static void Determinism(Action<bool, string> check)
    {
        var a = Fingerprint(Scripted());
        var b = Fingerprint(Scripted());
        check(a.SequenceEqual(b), "runs differ");
        MegastormModel original = Scripted();
        string json = original.ToJson();
        MegastormModel round = MegastormModel.FromJson(json);
        check(Fingerprint(round).SequenceEqual(a), "JSON round trip differs");
        check(round.ToJson() == json, "JSON not stable");
        // An untriggered state (NaN stop time) also round trips.
        check(MegastormModel.FromJson(new MegastormModel().ToJson()).Triggered == false, "untriggered round trip");
        check(a.Count > 1000 && a.Distinct().Count() > 100, "fingerprint too thin");
    }
}
