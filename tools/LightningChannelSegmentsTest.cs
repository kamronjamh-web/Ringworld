using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Pure checks for the megastorm lightning channel overlay (scripts/Lightning/LightningChannelSegments.cs): event selection (kind, age, distance, cap, id ties),
/// deterministic 2^SubdivisionLevels subdivision of a generated bolt, and the pulse / reduced-flash limits. Run: tools/lightning_channel_segments_test.gd (headless).
/// Prints LIGHTNING_CHANNEL_SEGMENTS_TEST=PASS.
/// </summary>
public partial class LightningChannelSegmentsTest : RefCounted
{
    readonly List<string> _failures = new();
    void Check(bool ok, string message) { if (!ok) _failures.Add(message); }

    static RingLightningEvent Event(long id, int kind, double start, double around, float energy = 1.0f, bool scythe = false) =>
        new(id, 1, start, new RingCanonicalPosition(around, 0, 0), new RingCanonicalPosition(around, 3000, 0), 1234 + (int)id, 0.4f, energy, 500.0f, false, kind, scythe);

    public int Run()
    {
        try
        {
            Selection();
            Building();
            ScytheCore();
            Megastorm();
            ChannelPulses();
            Pulses();
        }
        catch (Exception error) { _failures.Add("exception: " + error); }
        finally { LightningVisualPolicy.Reset(); }
        if (_failures.Count == 0) { Console.WriteLine("LIGHTNING_CHANNEL_SEGMENTS_TEST=PASS"); return 0; }
        foreach (string failure in _failures) Console.Error.WriteLine("LIGHTNING_CHANNEL_SEGMENTS_TEST=FAIL " + failure);
        return 1;
    }

    void Selection()
    {
        double now = 100.0;
        var list = new List<RingLightningEvent>
        {
            Event(1, 6, 99.9, 1000),               // kept
            Event(2, 3, 99.9, 500),                // natural: dropped
            Event(3, 6, 90.0, 800),                // expired
            Event(4, 6, 100.5, 800),               // future
            Event(5, 6, 99.9, 70000),              // too far
            Event(6, 6, 99.9, 1000, scythe: true), // tie with 1 (same distance): lower id first, scythe included
            Event(7, 6, 99.9, 200),                // nearest
        };
        for (int i = 0; i < 8; i++) list.Add(Event(100 + i, 6, 99.9, 20000 + i * 100));
        var output = new List<RingLightningEvent>();
        Func<RingCanonicalPosition, double> distance = p => Math.Abs(p.AroundMeters);
        int n = LightningChannelSegments.SelectMegastormEvents(list, now, distance, 60000.0, 8, output);
        Check(n == 8 && output.Count == 8, "select count " + n);
        Check(output[0].EventId == 7, "nearest first");
        Check(output[1].EventId == 1 && output[2].EventId == 6, "tie goes to the lower event id, scythe included");
        foreach (RingLightningEvent e in output) Check(e.SystemKind == 6 && e.EventId != 3 && e.EventId != 4 && e.EventId != 5, "bad event kept " + e.EventId);
        Check(output[7].EventId == 104, "cap keeps the nearest eight, last is 104 got " + output[7].EventId);
        n = LightningChannelSegments.SelectMegastormEvents(list, now, distance, 600.0, 8, output);
        Check(n == 1 && output[0].EventId == 7, "distance filter");
        n = LightningChannelSegments.SelectMegastormEvents(list, now, distance, 60000.0, 2, output);
        Check(n == 2 && output[0].EventId == 7 && output[1].EventId == 1, "maxBolts 2");
        // Natural selection: kind != 6, not field strikes, appended after what is already in the list (priority), nearest first.
        var mixed = new List<RingLightningEvent> { Event(200, 3, 99.9, 900), Event(201, 6, 99.9, 100), Event(202, 3, 99.9, 300), Event(203, 4, 99.9, 70000), Event(204, 3, 90.0, 100) };
        var sel = new List<RingLightningEvent>();
        int m = LightningChannelSegments.SelectMegastormEvents(mixed, now, distance, 60000.0, 8, sel);
        int nat = LightningChannelSegments.SelectNaturalEvents(mixed, now, distance, 60000.0, 8, sel);
        Check(m == 1 && nat == 2 && sel.Count == 3, "natural count " + m + "/" + nat);
        Check(sel[0].EventId == 201 && sel[1].EventId == 202 && sel[2].EventId == 200, "megastorm first, then natural nearest first");
        sel.Clear();
        LightningChannelSegments.SelectNaturalEvents(mixed, now, distance, 60000.0, 1, sel);
        Check(sel.Count == 1 && sel[0].EventId == 202, "natural slot cap keeps the nearest");
        Check(LightningChannelSegments.NaturalBranchBudget(1000) == 3 && LightningChannelSegments.NaturalBranchBudget(20000) == 9, "branch budget scales and clamps");
        var shortBolt = new List<ChannelSegment>(); var longBolt = new List<ChannelSegment>();
        int brShort = 0, brLong = 0;
        for (int seed = 1; seed <= 20; seed++)
        {
            shortBolt.Clear(); longBolt.Clear();
            LightningChannelSegments.BuildMegastormChannel(new Vector3(0, 1500, 0), Vector3.Zero, seed, 0, shortBolt, LightningChannelSegments.NaturalBranchBudget(1500));
            LightningChannelSegments.BuildMegastormChannel(new Vector3(0, 9000, 0), Vector3.Zero, seed, 0, longBolt, LightningChannelSegments.NaturalBranchBudget(9000));
            Check(shortBolt.Count > 0 && shortBolt.Count <= LightningChannelSegments.MaxBoltSegments, "natural channel segment count " + shortBolt.Count);
            foreach (ChannelSegment s in shortBolt) if (s.AlongB > 0.5f) { brShort++; break; }
            foreach (ChannelSegment s in longBolt) if (s.AlongB > 0.5f) { brLong++; break; }
        }
        Check(brShort >= 15, "natural channels carry forks, got " + brShort + "/20");
    }

    void Building()
    {
        var settings = new LightningSettings { Lod = LightningLod.Ultra };
        const float length = 6000.0f;
        LightningBolt bolt = ProceduralLightningGenerator.Generate(Vector2.Zero, new Vector2(0, length), 4242, settings);
        var start = new Vector3(100, 5000, -200);
        Vector3 down = new Vector3(0.1f, -1, 0.05f).Normalized();
        Vector3 side = down.Cross(Vector3.Right).Normalized();
        var a = new List<ChannelSegment>(); var b = new List<ChannelSegment>();
        LightningChannelSegments.BuildBolt(bolt, start, side, down, 3, 4242, a);
        LightningChannelSegments.BuildBolt(bolt, start, side, down, 3, 4242, b);
        Check(a.Count > 0 && a.Count == b.Count, "deterministic count");
        bool same = a.Count == b.Count;
        for (int i = 0; same && i < a.Count; i++) same = a[i] == b[i];
        Check(same, "deterministic segments");
        int expected = Math.Max(0, bolt.MainPath.Points.Count - 1);
        void CountBranch(LightningBranch br) { expected += Math.Max(0, br.Path.Points.Count - 1); foreach (var c in br.Children) CountBranch(c); }
        foreach (var br in bolt.Branches) CountBranch(br);
        int factor = 1 << LightningChannelSegments.SubdivisionLevels;
        Check(a.Count == expected * factor, $"{factor}x subdivision: {a.Count} vs {expected * factor}");
        Check(a[0].A.IsEqualApprox(start), "first segment starts at startWorld");
        int mainSegments = (bolt.MainPath.Points.Count - 1) * factor;
        Vector3 end = start + down * length;
        Check(a[mainSegments - 1].B.DistanceTo(end) < 1.0f, "main path ends at start + down*length, off by " + a[mainSegments - 1].B.DistanceTo(end));
        Check(a[0].Intensity == 1.0f && a[0].Bolt == 3, "main path intensity and bolt index");
        bool branchWeights = true;
        for (int i = mainSegments; i < a.Count; i++) branchWeights &= a[i].Intensity == 0.45f || a[i].Intensity == 0.25f;
        Check(branchWeights, "branch intensities");
        // Displacement must lie mostly in the side/down plane: for a vertical bolt the midpoint offsets have a side component for at least half the segments.
        {
            var straight = new List<ChannelSegment>();
            var line = new LightningBolt { Seed = 1 };
            line.MainPath.Points.Add(new LightningPoint(Vector2.Zero, 1.0f, 0.0f));
            line.MainPath.Points.Add(new LightningPoint(new Vector2(0, 1000), 1.0f, 1.0f));
            LightningChannelSegments.BuildBolt(line, Vector3.Zero, Vector3.Right, Vector3.Down, 0, 17, straight);
            int withSide = 0;
            foreach (ChannelSegment seg in straight) if (Math.Abs(seg.B.X) > 1.0e-3f || Math.Abs(seg.A.X) > 1.0e-3f) withSide++;
            Check(straight.Count == 1 << LightningChannelSegments.SubdivisionLevels && withSide * 2 >= straight.Count, $"displacement has a side component: {withSide}/{straight.Count}");
            Check(straight[0].A == Vector3.Zero && straight[^1].B.IsEqualApprox(new Vector3(0, -1000, 0)), "subdivision keeps the endpoints");
        }
        var other = new List<ChannelSegment>();
        LightningChannelSegments.BuildBolt(bolt, start, side, down, 3, 99, other);
        Check(other.Count == a.Count && !other[1].A.IsEqualApprox(a[1].A), "a different seed displaces differently");
    }

    void ScytheCore()
    {
        // Straight 3-point line of length 1000 along X: the whole line comes out, with monotone along fractions 0..1 and intensity 1 (the tip cut and head boost are in the shader).
        Vector3[] line = { Vector3.Zero, new Vector3(250, 0, 0), new Vector3(1000, 0, 0) };
        var segs = new List<ChannelSegment>();
        LightningChannelSegments.BuildScytheCore(line, 3.0f, 9, segs);
        Check(segs.Count == 2, "core emits every segment, got " + segs.Count);
        bool ok = segs.Count == 2;
        float previous = 0.0f;
        foreach (ChannelSegment s in segs)
        {
            ok &= s.Intensity == 1.0f && s.WidthA == 3.0f && s.WidthB == 3.0f && s.Bolt == 9;
            ok &= Mathf.IsEqualApprox(s.AlongA, previous, 1.0e-5f) && s.AlongB > s.AlongA;
            previous = s.AlongB;
        }
        Check(ok, "core widths, slot, intensity and monotone along");
        Check(segs.Count == 2 && Mathf.IsEqualApprox(segs[0].AlongB, 0.25f, 1.0e-5f) && Mathf.IsEqualApprox(segs[1].AlongB, 1.0f, 1.0e-5f), "along fractions are cumulative length / total");
        var none = new List<ChannelSegment>();
        LightningChannelSegments.BuildScytheCore(new Vector3[] { Vector3.One }, 3.0f, 8, none);
        LightningChannelSegments.BuildScytheCore(ReadOnlySpan<Vector3>.Empty, 3.0f, 8, none);
        Check(none.Count == 0, "fewer than two points adds nothing");
    }

    void Megastorm()
    {
        var top = new Vector3(2000, 12000, -500);
        var ground = new Vector3(0, 0, 0);
        float height = top.DistanceTo(ground);
        int levels = Math.Clamp((int)Math.Ceiling(Math.Log2(height / 30.0f)), 6, 8);
        int mainCount = 1 << levels;
        bool anyBranches = false, shiftOk = true, endOk = true, turn = false, tipThin = true, angleOk = true, deterministic = true, budget = true;
        int branchChecks = 0;
        for (int seed = 1; seed <= 40; seed++)
        {
            var a = new List<ChannelSegment>(); var b = new List<ChannelSegment>();
            int na = LightningChannelSegments.BuildMegastormChannel(top, ground, seed, 5, a);
            int nb = LightningChannelSegments.BuildMegastormChannel(top, ground, seed, 5, b);
            deterministic &= na == nb && a.Count == b.Count;
            for (int i = 0; deterministic && i < a.Count; i++) deterministic &= a[i] == b[i];
            budget &= na == a.Count && na <= LightningChannelSegments.MaxBoltSegments && na >= mainCount;
            if (na < mainCount) continue;
            shiftOk &= a[0].A.DistanceTo(top) <= 0.26f * height;
            endOk &= a[mainCount - 1].B.DistanceTo(ground) < 1.0f;
            // Main path: first segments (AlongB == 0), a turn above 20 degrees somewhere.
            for (int i = 1; i < mainCount; i++)
            {
                Vector3 d0 = (a[i - 1].B - a[i - 1].A).Normalized(), d1 = (a[i].B - a[i].A).Normalized();
                if (Mathf.RadToDeg(d0.AngleTo(d1)) > 20.0f) turn = true;
                if (a[i].AlongB != 0.0f || a[i].Bolt != 5) { endOk = false; }
            }
            // Primary branches: first segments that start on a main vertex (v >= 1).
            for (int i = mainCount; i < a.Count; i++)
            {
                if (a[i].AlongB != 1.0f) { budget = false; continue; }
                for (int v = 1; v < mainCount; v++)
                {
                    if (!a[i].A.IsEqualApprox(a[v - 1].B)) continue;
                    // Only the first segment of a branch has the exact vertex as its start; skip the middle of the run / secondary segments.
                    if (i > mainCount && a[i - 1].B.IsEqualApprox(a[i].A)) break;
                    Vector3 heading = (a[v].B - a[v - 1].A).Normalized();
                    float angle = Mathf.RadToDeg(heading.AngleTo((a[i].B - a[i].A).Normalized()));
                    anyBranches = true; branchChecks++;
                    angleOk &= angle >= 18.0f && angle <= 52.0f;
                    break;
                }
            }
            // Branch tip width below root width: scan runs of consecutive connected segments with AlongB == 1.
            int runStart = mainCount;
            for (int i = mainCount; i < a.Count; i++)
            {
                bool last = i == a.Count - 1 || !a[i + 1].A.IsEqualApprox(a[i].B);
                if (last)
                {
                    tipThin &= a[i].WidthB < a[runStart].WidthA;
                    runStart = i + 1;
                }
            }
        }
        Check(deterministic, "megastorm channel is deterministic");
        Check(budget, "megastorm channel stays within budget and flags");
        Check(shiftOk, "top shift <= 0.26 H");
        Check(endOk, "main path ends at the ground");
        Check(turn, "main path has a turn above 20 degrees");
        Check(anyBranches && branchChecks >= 3, "primary branches exist, checked " + branchChecks);
        Check(angleOk, "primary branch first-segment angle in [18, 52] degrees");
        Check(tipThin, "branch tip width below root width");
    }

    void ChannelPulses()
    {
        LightningVisualPolicy.Reset();
        var e = Event(1, 6, 0.0, 0, 0.8f);
        float v = LightningChannelSegments.ChannelPulse(e, 0.2, out int stroke);
        Check(v > RingLightningEvent.PulseAtAge(0.2) * 0.8f, "afterglow lifts the pulse at age 0.2, got " + v);
        Check(LightningChannelSegments.ChannelPulse(e, 0.73, out _) == 0.0f, "pulse is 0 at age 0.73");
        LightningChannelSegments.ChannelPulse(e, 0.05, out int s0); LightningChannelSegments.ChannelPulse(e, 0.3, out int s1); LightningChannelSegments.ChannelPulse(e, 0.55, out int s2);
        Check(s0 == 0 && s1 == 1 && s2 == 2, $"strokes {s0},{s1},{s2}");
        LightningVisualPolicy.Apply(1.0f, 1.0f, true);
        bool same = true;
        for (double t = 0; t <= 0.72; t += 0.01) same &= LightningChannelSegments.ChannelPulse(e, t, out int rs) == RingLightningEvent.PulseAtAge(t) * 0.8f && rs == 0;
        Check(same, "reduced flashes: ChannelPulse equals PulseAtAge x Energy");
        LightningVisualPolicy.Reset();
    }

    void Pulses()
    {
        LightningVisualPolicy.Reset();
        var e = Event(1, 6, 0.0, 0, 1.0f);
        float peak = 0.0f;
        for (double t = 0; t <= RingLightningEvent.LifetimeSeconds; t += 0.005) peak = Math.Max(peak, LightningChannelSegments.Pulse(e, t));
        Check(peak > 0.9f, "full pulse peaks near 1, got " + peak);
        Check(LightningChannelSegments.Pulse(Event(2, 6, 0.0, 0, 0.5f), 0.035) < LightningChannelSegments.Pulse(e, 0.035) * 0.6f, "energy scales the pulse");
        LightningVisualPolicy.Apply(1.0f, 1.0f, true);
        float reducedPeak = 0.0f;
        for (double t = 0; t <= RingLightningEvent.LifetimeSeconds; t += 0.005) reducedPeak = Math.Max(reducedPeak, LightningChannelSegments.Pulse(e, t));
        Check(reducedPeak <= 0.4f + 1.0e-4f && reducedPeak > 0.2f, "reduced flashes peak <= 0.4, got " + reducedPeak);
        Span<float> levels = stackalloc float[] { 0.4f, 0.4f, 0.4f, 0.4f };
        LightningVisualPolicy.LimitSummed(levels);
        float sum = levels[0] + levels[1] + levels[2] + levels[3];
        Check(sum <= 0.55f + 1.0e-4f, "LimitSummed caps at 0.55, got " + sum);
        LightningVisualPolicy.Reset();
    }
}
