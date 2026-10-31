using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Megastorm WO12 (docs/megastorm-design.md 5.5.4 and section 8): the lightning scythe stream. Pure checks: determinism, window split and save/load,
/// rate within 10% of 20/s x the strike-rate scale, ranges (path 2-5 km, width 50-200 m, lifetime 1-3 s, tip speed 1.5-2 km/s, 3-8 strokes 0.2-0.5 s apart,
/// 12-16 points, lower edge 0.5-4 m above the smoothed ground at every point), the tip moving in the storm's direction both ways, the reduced-flashes
/// envelope step, the cut table, a Monte-Carlo of 10,000 passages (analytic rate and real stream geometry), the crackle schedule's per-segment delays and
/// no overlap with the shared field fixture. Then a staged run through the real layer (attachment strike with the Scythe flag, rewind, no source) and the
/// RingMegastormScythes node (visuals appear, crackle gated by the air gate). Run: tools/megastorm_scythe_test.gd (headless). Prints MEGASTORM_SCYTHE=PASS.
/// </summary>
public partial class MegastormScytheTest : RefCounted
{
    const string FixturePath = "res://tools/fixtures/containment_megastorm_fixture.json";
    readonly List<string> _failures = new();
    int _stage, _wait;
    MegastormModel? _model;
    MegastormScythe _target;
    RingLightningEvent _first;
    double _clock;
    RingCanonicalPosition _watcher;

    void Check(bool ok, string message) { if (!ok) _failures.Add(message); }
    static void Info(string message) => Console.WriteLine("MEGASTORM_SCYTHE_INFO " + message);

    static MegastormModel NewModel(double lead, int dir = 1, int seed = 33, float speed = 350.0f)
    {
        var model = new MegastormModel();
        model.Trigger(new MegastormTrigger("scythe_test", lead, dir, 0.0, speed, 0.0f, seed));
        return model;
    }

    static double Ground(double around, double across) => 40.0 * Math.Sin(around / 700.0) + 25.0 * Math.Cos(across / 450.0) + 0.002 * around % 90.0;

    int Finish(string stage)
    {
        if (_failures.Count == 0) { Info(stage + " ok"); return 0; }
        foreach (string f in _failures) Console.Error.WriteLine("MEGASTORM_SCYTHE=FAIL " + stage + ": " + f);
        return 1;
    }

    public int RunPure()
    {
        try
        {
            LightningVisualPolicy.Reset();
            Determinism();
            Rates();
            Ranges();
            TipAndPulse();
            CutTable();
            MonteCarloAnalytic();
            MonteCarloStream();
            Crackle();
            FieldFixture();
            LightningVisualPolicy.Reset();
            return Finish("PURE");
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
    }

    void Determinism()
    {
        MegastormModel a = NewModel(5_000_000.0), b = NewModel(5_000_000.0);
        var la = new List<MegastormScythe>(); var lb = new List<MegastormScythe>(); var part = new List<MegastormScythe>(); var tmp = new List<MegastormScythe>();
        MegastormScythes.ScythesInWindow(a, 10.0, 400.0, 0.0, 0.0, double.PositiveInfinity, 1.0f, la);
        MegastormScythes.ScythesInWindow(b, 10.0, 400.0, 0.0, 0.0, double.PositiveInfinity, 1.0f, lb);
        Check(la.Count > 5000 && la.SequenceEqual(lb), "two models of one seed differ");
        MegastormScythes.ScythesInWindow(a, 10.0, 200.0, 0.0, 0.0, double.PositiveInfinity, 1.0f, tmp); part.AddRange(tmp);
        MegastormScythes.ScythesInWindow(a, 200.0, 400.0, 0.0, 0.0, double.PositiveInfinity, 1.0f, tmp); part.AddRange(tmp);
        Check(la.SequenceEqual(part), "window split changes the stream");
        Check(la.Select(s => s.Id).Distinct().Count() == la.Count, "scythe ids are not unique");
        Check(la.All(s => MegastormScythes.FromId(a, s.Id) == s), "FromId does not rebuild the scythe");
        Check(la.All(s => s.StartTimeSeconds >= 10.0 && s.StartTimeSeconds < 400.0), "start outside the window");
        // The flash field and the blue stream never share an id with a scythe.
        var flashIds = new List<long>();
        MegastormLightning.FlashIdsInWindow(a, 10.0, 400.0, flashIds);
        Check(!flashIds.Intersect(la.Select(s => s.Id)).Any(), "scythe id collides with a flash id");
        // Save / load: the document round trip reproduces the stream.
        MegastormState? state = MegastormDocument.FromState(a.State, false).ToState();
        Check(state != null, "document round trip failed");
        if (state != null)
        {
            var restored = new MegastormModel(state);
            MegastormScythes.ScythesInWindow(restored, 10.0, 400.0, 0.0, 0.0, double.PositiveInfinity, 1.0f, tmp);
            Check(la.SequenceEqual(tmp), "save/load changes the stream");
        }
        // Paths and states are pure.
        var p1 = new ScythePoint[16]; var p2 = new ScythePoint[16];
        foreach (MegastormScythe s in la.Take(200))
        {
            int n1 = MegastormScythes.PathPoints(a, s, Ground, p1), n2 = MegastormScythes.PathPoints(b, s, Ground, p2);
            Check(n1 == n2 && p1.AsSpan(0, n1).SequenceEqual(p2.AsSpan(0, n2)), "path not reproducible");
            Check(MegastormScythes.StateAt(a, s, s.StartTimeSeconds + 0.37, false) == MegastormScythes.StateAt(b, s, s.StartTimeSeconds + 0.37, false), "state not reproducible");
        }
        // Rewind-safety is the same property: nothing carries state between calls. Untriggered and inactive models have no stream.
        MegastormScythes.ScythesInWindow(new MegastormModel(), 10.0, 400.0, 0.0, 0.0, double.PositiveInfinity, 1.0f, tmp);
        Check(tmp.Count == 0, "scythes without a storm");
        Info($"{la.Count} scythes in 390 s, deterministic across models, windows and save/load");
    }

    void Rates()
    {
        MegastormModel m = NewModel(5_000_000.0);
        var list = new List<MegastormScythe>();
        double Rate(float scale)
        {
            MegastormScythes.ScythesInWindow(m, 20.0, 2020.0, 0.0, 0.0, double.PositiveInfinity, scale, list);
            return list.Count / 2000.0;
        }
        double r1 = Rate(1.0f), r05 = Rate(0.5f), r15 = Rate(1.5f);
        Check(Math.Abs(r1 - 20.0) < 2.0, $"rate {r1:F2}/s is not within 10% of 20");
        Check(Math.Abs(r05 - 10.0) < 1.0 && Math.Abs(r15 - 30.0) < 3.0, $"scaled rates {r05:F2} / {r15:F2}");
        MegastormScythes.ScythesInWindow(m, 20.0, 600.0, 0.0, 0.0, double.PositiveInfinity, 0.5f, list);
        var half = list.Select(s => s.Id).ToHashSet();
        MegastormScythes.ScythesInWindow(m, 20.0, 600.0, 0.0, 0.0, double.PositiveInfinity, 1.0f, list);
        Check(half.IsSubsetOf(list.Select(s => s.Id)), "a lower rate scale is not a subset");
        // The policy: Lightning frequency and Reduced flashes feed NaturalStrikeChanceScale.
        LightningVisualPolicy.Apply(1.0f, 1.0f, true);
        Check(Math.Abs(Rate(LightningVisualPolicy.NaturalStrikeChanceScale) - 10.0) < 1.0, "reduced flashes does not halve the stream");
        LightningVisualPolicy.Apply(0.0f, 1.0f, false);
        Check(Rate(LightningVisualPolicy.NaturalStrikeChanceScale) == 0.0, "frequency 0 leaves scythes");
        LightningVisualPolicy.Reset();
        // Zone shape: attachments follow the profile over xi -15 .. 20 km, mostly 0-10 km.
        MegastormScythes.ScythesInWindow(m, 20.0, 1020.0, 0.0, 0.0, double.PositiveInfinity, 1.0f, list);
        Check(list.All(s => s.XiMeters >= -15000.0 && s.XiMeters <= 20000.0), "attachment xi outside -15 .. 20 km");
        Check(list.All(s => Math.Abs(m.Xi(s.AttachAroundMeters, s.AttachAcrossMeters, s.StartTimeSeconds) - s.XiMeters) < 0.5), "attachment is not at its xi");
        double inner = list.Count(s => s.XiMeters >= 0.0 && s.XiMeters <= 10000.0) / (double)list.Count;
        Check(inner > 0.38 && inner < 0.52, $"share of attachments over xi 0-10 km is {inner:F3}");
        // Near window: a 10 km observer window keeps the share of the wall that is inside it.
        MegastormScythes.ScythesInWindow(m, 20.0, 1020.0, list[0].AttachAroundMeters, 159000.0, 25000.0, 1.0f, list);
        Info($"rate {r1:F2}/s (scale 0.5: {r05:F2}, 1.5: {r15:F2}), xi 0-10 km share {inner:F3}, near (25 km) {list.Count / 1000.0:F2}/s");
    }

    void Ranges()
    {
        var list = new List<MegastormScythe>();
        var points = new ScythePoint[16];
        double sumLength = 0.0, sumWidth = 0.0, sumLife = 0.0;
        double minSkim = 99.0, maxSkim = 0.0, minLen = 1e9, maxLen = 0.0;
        int strokeMin = 99, strokeMax = 0;
        foreach (int dir in new[] { 1, -1 })
        {
            MegastormModel m = NewModel(5_000_000.0, dir, 40 + dir);
            MegastormScythes.ScythesInWindow(m, 20.0, 220.0, 0.0, 0.0, double.PositiveInfinity, 1.0f, list);
            Span<float> times = stackalloc float[8], amps = stackalloc float[8];
            foreach (MegastormScythe s in list)
            {
                Check(s.LengthMeters >= 2000.0f && s.LengthMeters <= 5000.0f, $"length {s.LengthMeters}");
                Check(s.WidthMeters >= 50.0f && s.WidthMeters <= 200.0f, $"width {s.WidthMeters}");
                Check(s.LifetimeSeconds >= 1.0f && s.LifetimeSeconds <= 3.0f, $"lifetime {s.LifetimeSeconds}");
                Check(s.TipSpeedMetersPerSecond >= 1500.0f - 0.5f && s.TipSpeedMetersPerSecond <= 2000.0f + 0.5f, $"tip speed {s.TipSpeedMetersPerSecond}");
                Check(s.LengthMeters / s.TipSpeedMetersPerSecond <= s.LifetimeSeconds + 1e-4, "tip does not reach the end inside the lifetime");
                Check(s.ThicknessMeters >= 2.0f && s.ThicknessMeters <= 8.0f, $"thickness {s.ThicknessMeters}");
                Check(s.PointCount >= 12 && s.PointCount <= 16, $"points {s.PointCount}");
                Check(s.Direction == dir, "direction");
                int n = MegastormScythes.PathPoints(m, s, Ground, points);
                Check(n == s.PointCount, "point count");
                for (int i = 0; i < n; i++)
                {
                    double skim = points[i].LowerEdgeMeters - Ground(points[i].AroundMeters, points[i].AcrossMeters);
                    minSkim = Math.Min(minSkim, skim); maxSkim = Math.Max(maxSkim, skim);
                    Check(skim >= 0.5 - 1e-3 && skim <= 4.0 + 1e-3, $"lower edge {skim:F3} m above the smoothed ground");
                    Check(points[i].AcrossMeters >= 0.0 && points[i].AcrossMeters <= m.Settings.WidthMeters, "point outside the ring width");
                }
                // Arc length of the polyline runs from the nominal length to a few percent above it.
                double arc = 0.0, c = m.Settings.CircumferenceMeters;
                for (int i = 1; i < n; i++)
                {
                    double da = MegastormModel.WrapSigned(points[i].AroundMeters - points[i - 1].AroundMeters, c), dz = points[i].AcrossMeters - points[i - 1].AcrossMeters;
                    arc += Math.Sqrt(da * da + dz * dz);
                }
                Check(arc >= 0.99 * s.LengthMeters && arc <= 1.06 * s.LengthMeters + 1.0 && arc >= 2000.0 && arc <= 5000.0, $"polyline length {arc:F0} vs {s.LengthMeters}");
                minLen = Math.Min(minLen, arc); maxLen = Math.Max(maxLen, arc);
                int count = MegastormScythes.StrokeTimes(m, s, times, amps);
                strokeMin = Math.Min(strokeMin, count); strokeMax = Math.Max(strokeMax, count);
                Check(count >= 3 && count <= 8, $"{count} strokes");
                for (int i = 1; i < count; i++) Check(times[i] - times[i - 1] >= 0.2f - 1e-4f && times[i] - times[i - 1] <= 0.5f + 1e-4f, $"stroke gap {times[i] - times[i - 1]}");
                Check(count == 0 || times[count - 1] <= s.LifetimeSeconds, "stroke after the lifetime");
                sumLength += s.LengthMeters; sumWidth += s.WidthMeters; sumLife += s.LifetimeSeconds;
            }
        }
        double nAll = list.Count * 2.0;
        Info($"{(int)nAll} scythes: mean length {sumLength / nAll:F0} m, width {sumWidth / nAll:F0} m, lifetime {sumLife / nAll:F2} s, skim {minSkim:F2}..{maxSkim:F2} m, polyline {minLen:F0}..{maxLen:F0} m, strokes {strokeMin}..{strokeMax}");
    }

    void TipAndPulse()
    {
        var list = new List<MegastormScythe>();
        var points = new ScythePoint[16];
        foreach (int dir in new[] { 1, -1 })
        {
            MegastormModel m = NewModel(5_000_000.0, dir, 50 + dir);
            MegastormScythes.ScythesInWindow(m, 20.0, 70.0, 0.0, 0.0, double.PositiveInfinity, 1.0f, list);
            double c = m.Settings.CircumferenceMeters;
            int forward = 0, total = 0;
            float maxStep = 0.0f, maxPeak = 0.0f, maxFull = 0.0f, maxFullStep = 0.0f, prevFull = 0.0f;
            foreach (MegastormScythe s in list)
            {
                MegastormScythes.PathPoints(m, s, null, points);
                double prevAround = double.NaN, prevTip = -1.0;
                float prevReduced = 0.0f; int firedPrev = 0;
                for (double age = 0.0; age <= s.LifetimeSeconds + 0.05; age += 1.0 / 60.0)
                {
                    ScytheState full = MegastormScythes.StateAt(m, s, s.StartTimeSeconds + age, false);
                    ScytheState red = MegastormScythes.StateAt(m, s, s.StartTimeSeconds + age, true);
                    if (age > s.LifetimeSeconds) { Check(!full.Visible && !red.Visible, "visible after the lifetime"); break; }
                    Check(full.Visible, "not visible inside the lifetime");
                    Check(full.TipDistanceMeters >= prevTip - 1e-3, "tip went backwards");
                    prevTip = full.TipDistanceMeters;
                    Check(full.StrokesFired >= firedPrev, "stroke count went down"); firedPrev = full.StrokesFired;
                    maxStep = Math.Max(maxStep, Math.Abs(red.Pulse - prevReduced)); prevReduced = red.Pulse;
                    maxFullStep = Math.Max(maxFullStep, Math.Abs(full.Pulse - prevFull)); prevFull = full.Pulse;
                    maxPeak = Math.Max(maxPeak, red.Pulse); maxFull = Math.Max(maxFull, full.Pulse);
                    (double around, double _) = MegastormScythes.PositionAt(points, s.PointCount, s, full.TipDistanceMeters, c);
                    if (!double.IsNaN(prevAround) && full.TipDistanceMeters > 1.0f)
                    {
                        double step = MegastormModel.WrapSigned(around - prevAround, c) * dir;
                        total++; if (step >= -1e-6) forward++;
                    }
                    prevAround = around;
                }
                Check(MegastormScythes.StateAt(m, s, s.StartTimeSeconds - 0.01, false).Visible == false, "visible before the start");
                Check(MegastormScythes.StateAt(m, s, s.StartTimeSeconds + 0.04, false).Pulse > 0.5f, "no first return stroke");
            }
            Check(forward == total && total > 1000, $"tip moved in the storm direction {forward}/{total} (dir {dir})");
            Check(maxStep <= 0.05f, $"reduced pulse step {maxStep:F4} per 1/60 s exceeds 0.05");
            Check(maxPeak <= LightningVisualPolicy.ReducedPeakScale + 1e-3f && maxFull > 0.95f, $"reduced peak {maxPeak:F3} / full peak {maxFull:F3}");
            Check(maxFullStep > 4.0f * maxStep, $"full envelope step {maxFullStep:F3} is not clearly sharper than the reduced step {maxStep:F3}");
            Info($"dir {dir}: full step max {maxFullStep:F3}, tip forward {forward}/{total}, reduced step max {maxStep:F4}, reduced peak {maxPeak:F3}, full peak {maxFull:F3}");
        }
    }

    void CutTable()
    {
        float Share(float h) => MegastormHazardMath.ScytheCutShare(h);
        Check(Share(0.4f) == 0.0f && Share(0.5f) == 0.0f, "prone");
        Check(Math.Abs(Share(1.0f) - 0.05f) < 0.015f, $"crouched {Share(1.0f)}");
        Check(Math.Abs(Share(1.8f) - 0.33f) < 0.04f, $"standing {Share(1.8f)}");
        Check(Math.Abs(Share(10.0f) - 1.0f) < 1e-5f, "10 m");
        float rate = 0.2f;
        Check(MegastormHazardMath.ScytheCutProbability(rate, 1.8f, 3.0f, 1.0f, 1.0) == 0.0f, "trench 3 m deep, standing 1.8 m");
        Check(MegastormHazardMath.ScytheCutProbability(rate, 10.0f, 0.0f, 0.0f, 1.0) == 0.0f, "enclosed (exposure 0)");
        Check(Math.Abs(MegastormHazardMath.ScytheCutProbability(rate, 10.0f, 0.0f, 1.0f, 1.0) - rate) < 1e-5f, "10 m at full exposure");
        // The smoothed ground: flat is 0 depth, a 3 m trench in a 100 m smoothing window reads as a few tenths, a deep one as its depth share.
        Func<double, double, double> flat = (a, z) => 12.0;
        Check(Math.Abs(MegastormScythes.GroundBelowSkimMeters(flat, 100.0, 100.0)) < 1e-4f, "flat ground depth");
        Func<double, double, double> slope = (a, z) => 0.1 * a;
        Check(Math.Abs(MegastormScythes.GroundBelowSkimMeters(slope, 100.0, 100.0)) < 1e-3f, "a plane smooths to itself");
        Func<double, double, double> ditch = (a, z) => Math.Abs(a - 100.0) < 10.0 ? 6.0 : 10.0;
        float depth = MegastormScythes.GroundBelowSkimMeters(ditch, 100.0, 100.0);
        Check(depth > 1.0f && depth < 4.0f, $"ditch depth {depth}");
        Info($"shares: prone {Share(0.4f):F3}, 1 m {Share(1.0f):F3}, 1.8 m {Share(1.8f):F3}, 10 m {Share(10.0f):F3}; ditch depth {depth:F2} m");
    }

    void MonteCarloAnalytic()
    {
        MegastormModel m = NewModel(5_000_000.0);
        var rng = new Random(12345);
        double width = m.Settings.WidthMeters, speed = 350.0;
        int standing = 0, tree = 0, crouched = 0, prone = 0;
        const int Passages = 10_000;
        for (int p = 0; p < Passages; p++)
        {
            bool cutStanding = false, cutTree = false, cutCrouch = false, cutProne = false;
            for (double xi = -16000.0; xi <= 21000.0; xi += speed * 1.0)
            {
                float rate = MegastormHazardMath.ScytheSweepRatePerSecond(xi, width);
                if (rate <= 0.0f) continue;
                double u = rng.NextDouble();
                if (u < MegastormHazardMath.ScytheCutProbability(rate, 1.8f, 0.0f, 1.0f, 1.0)) cutStanding = true;
                double u2 = rng.NextDouble();
                if (u2 < MegastormHazardMath.ScytheCutProbability(rate, 10.0f, 0.0f, 1.0f, 1.0)) cutTree = true;
                if (rng.NextDouble() < MegastormHazardMath.ScytheCutProbability(rate, 1.0f, 0.0f, 1.0f, 1.0)) cutCrouch = true;
                if (rng.NextDouble() < MegastormHazardMath.ScytheCutProbability(rate, 0.4f, 0.0f, 1.0f, 1.0)) cutProne = true;
            }
            if (cutStanding) standing++; if (cutTree) tree++; if (cutCrouch) crouched++; if (cutProne) prone++;
        }
        double ps = 100.0 * standing / Passages, pt = 100.0 * tree / Passages, pc = 100.0 * crouched / Passages;
        Check(Math.Abs(ps - 2.6) <= 0.5, $"analytic Monte-Carlo standing {ps:F2}% (want 2.6 +- 0.5)");
        Check(Math.Abs(pt - 8.0) <= 1.0, $"analytic Monte-Carlo 10 m {pt:F2}% (want 8 +- 1)");
        Check(prone == 0, "prone was cut");
        Info($"analytic Monte-Carlo (10,000 passages, dt 1 s): standing {ps:F2}%, crouched {pc:F2}%, 10 m {pt:F2}%, prone {prone}");
    }

    /// <summary>The real stream: a storm of 1,500 s, 10,000 random ground points, each tested against every sheet (nominal footprint) that covers it.</summary>
    void MonteCarloStream()
    {
        const double Seconds = 1500.0, Lead = 1_000_000.0;
        MegastormModel m = NewModel(Lead, 1, 77);
        double c = m.Settings.CircumferenceMeters, w = m.Settings.WidthMeters;
        var list = new List<MegastormScythe>();
        MegastormScythes.ScythesInWindow(m, 5.0, Seconds, 0.0, 0.0, double.PositiveInfinity, 1.0f, list);
        const double Cell = 8000.0;
        var grid = new Dictionary<(long, long), List<int>>();
        var paths = new ScythePoint[list.Count][];
        for (int i = 0; i < list.Count; i++)
        {
            var pts = new ScythePoint[16];
            int n = MegastormScythes.PathPoints(m, list[i], null, pts);
            paths[i] = pts.AsSpan(0, n).ToArray();
            var seen = new HashSet<(long, long)>();
            foreach (ScythePoint pt in paths[i])
                for (int da = -1; da <= 1; da++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        var key = ((long)Math.Floor(pt.AroundMeters / Cell) + da, (long)Math.Floor(pt.AcrossMeters / Cell) + dz);
                        if (seen.Add(key)) { if (!grid.TryGetValue(key, out List<int>? l)) grid[key] = l = new List<int>(); l.Add(i); }
                    }
        }
        var rng = new Random(2024);
        int observers = 40_000, crossed = 0, standing = 0, tree = 0;
        double crossingSum = 0.0, groundZero = 0.0;
        double lo = Lead + 30000.0 + 20000.0, hi = Lead + 350.0 * Seconds - 40000.0;
        for (int o = 0; o < observers; o++)
        {
            double around = MegastormModel.WrapPositive(lo + (hi - lo) * rng.NextDouble(), c), across = 6000.0 + (w - 12000.0) * rng.NextDouble();
            if (!grid.TryGetValue(((long)Math.Floor(around / Cell), (long)Math.Floor(across / Cell)), out List<int>? cand)) { continue; }
            int hitsHere = 0; bool cutStanding = false, cutTree = false;
            foreach (int i in cand)
            {
                MegastormScythe s = list[i];
                ScythePoint[] pts = paths[i];
                for (int k = 0; k < pts.Length - 1; k++)
                {
                    double ax = MegastormModel.WrapSigned(pts[k].AroundMeters - around, c), az = pts[k].AcrossMeters - across;
                    double bx = MegastormModel.WrapSigned(pts[k + 1].AroundMeters - around, c), bz = pts[k + 1].AcrossMeters - across;
                    double dx = bx - ax, dz = bz - az, len2 = dx * dx + dz * dz;
                    double u = len2 > 0.0 ? Math.Clamp(-(ax * dx + az * dz) / len2, 0.0, 1.0) : 0.0;
                    double px = ax + dx * u, pz = az + dz * u;
                    double f = (k + u) / (pts.Length - 1);
                    if (px * px + pz * pz > Math.Pow(ScytheMeshBuilder.HalfWidth(s, (float)f), 2.0)) continue;
                    double lower = pts[k].SkimMeters + (pts[k + 1].SkimMeters - pts[k].SkimMeters) * u;
                    hitsHere++;
                    if (lower < 1.8) cutStanding = true;
                    if (lower < 10.0) cutTree = true;
                    break;
                }
            }
            crossingSum += hitsHere; if (hitsHere > 0) crossed++;
            if (cutStanding) standing++; if (cutTree) tree++;
        }
        double ps = 100.0 * standing / observers, pt2 = 100.0 * tree / observers, crossings = crossingSum / observers;
        Check(Math.Abs(crossings - 0.0786) < 0.012, $"stream crossings per passage {crossings:F4} (analytic 0.0786)");
        Check(Math.Abs(ps - 2.6) <= 0.5, $"stream Monte-Carlo standing {ps:F2}% (want 2.6 +- 0.5)");
        Check(Math.Abs(pt2 - 8.0) <= 1.0, $"stream Monte-Carlo 10 m {pt2:F2}% (want 8 +- 1)");
        Info($"stream Monte-Carlo ({list.Count} scythes, 40,000 points): crossings {crossings:F4}/passage, standing {ps:F2}%, 10 m {pt2:F2}%, any {100.0 * crossed / observers:F2}% (ground-zero {groundZero})");
    }

    void Crackle()
    {
        MegastormModel m = NewModel(5_000_000.0);
        var list = new List<MegastormScythe>(); var sched = new List<ScytheCrackle>(); var points = new ScythePoint[16];
        MegastormScythes.ScythesInWindow(m, 20.0, 60.0, 0.0, 0.0, double.PositiveInfinity, 1.0f, list);
        MegastormScythe s = list[0];
        int n = MegastormScythes.PathPoints(m, s, null, points);
        // A listener 1 km across from the attachment.
        double la = s.AttachAroundMeters, lz = s.AttachAcrossMeters + 1000.0;
        MegastormScythes.CrackleSchedule(m, s, points, n, la, lz, sched);
        var tip = sched.Where(x => x.Stroke == 0).OrderBy(x => x.PointIndex).ToList();
        Check(tip.Count == n, "the tip crackle misses a path point");
        bool exact = true;
        for (int i = 0; i < tip.Count; i++)
        {
            double along = s.LengthMeters * i / (n - 1);
            double expect = s.StartTimeSeconds + along / s.TipSpeedMetersPerSecond + tip[i].DistanceMeters / 343.0;
            if (Math.Abs(tip[i].ArrivalTimeSeconds - expect) > 1e-6) exact = false;
        }
        Check(exact, "tip crackle arrival is not tip time + d / 343");
        double spread = tip.Max(x => x.ArrivalTimeSeconds) - tip.Min(x => x.ArrivalTimeSeconds);
        double tipSpan = s.LengthMeters / s.TipSpeedMetersPerSecond;
        Check(tip.Select(x => Math.Round(x.ArrivalTimeSeconds, 3)).Distinct().Count() == tip.Count, "tip crackles arrive together");
        // Each re-stroke fires at several points, each at its own delay (smear), later than the stroke itself by d / 343.
        var re = sched.Where(x => x.Stroke == 1).ToList();
        Check(re.Count >= 4, "a re-stroke fires too few tears");
        if (re.Count >= 4)
        {
            double stroke1 = s.StartTimeSeconds + StrokeTime(m, s, 1);
            Check(re.All(x => Math.Abs(x.ArrivalTimeSeconds - (stroke1 + x.DistanceMeters / 343.0)) < 1e-6), "re-stroke tear is not stroke time + d / 343");
            double smear = re.Max(x => x.ArrivalTimeSeconds) - re.Min(x => x.ArrivalTimeSeconds);
            Check(smear > 0.5, $"re-stroke tears smeared over only {smear:F2} s");
        }
        Check(sched.Zip(sched.Skip(1), (a, b) => a.ArrivalTimeSeconds <= b.ArrivalTimeSeconds).All(x => x), "schedule not sorted by arrival");
        Info($"crackle: {sched.Count} tears for one scythe at 1 km, tip crackle spread {spread:F2} s (tip travel {tipSpan:F2} s)");
    }

    static float StrokeTime(MegastormModel m, in MegastormScythe s, int k)
    {
        Span<float> times = stackalloc float[8], amps = stackalloc float[8];
        MegastormScythes.StrokeTimes(m, s, times, amps);
        return times[k];
    }

    void FieldFixture()
    {
        string path = ProjectSettings.GlobalizePath(FixturePath);
        Check(File.Exists(path), "shared fixture missing");
        if (!File.Exists(path)) return;
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement jm = doc.RootElement.GetProperty("model");
        var model = new MegastormModel();
        model.Trigger(new MegastormTrigger(jm.GetProperty("triggerId").GetString() ?? "f", jm.GetProperty("leadingAroundMeters").GetDouble(), jm.GetProperty("direction").GetInt32(),
            jm.GetProperty("triggerTimeSeconds").GetDouble(), (float)jm.GetProperty("speedMetersPerSecond").GetDouble(), (float)jm.GetProperty("formationSeconds").GetDouble(), jm.GetProperty("seed").GetInt32()));
        double t = doc.RootElement.GetProperty("weatherTimeSeconds").GetDouble(), window = doc.RootElement.GetProperty("windowSeconds").GetDouble();
        var published = new HashSet<string>();
        int fixtureIds = 0;
        foreach (JsonElement profile in doc.RootElement.GetProperty("profiles").EnumerateArray())
        {
            JsonElement expected = profile.GetProperty("expected");
            foreach (JsonProperty prop in expected.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement e in prop.Value.EnumerateArray()) if (e.ValueKind == JsonValueKind.String) { published.Add(e.GetString()!); fixtureIds++; }
        }
        var list = new List<MegastormScythe>();
        MegastormScythes.ScythesInWindow(model, t, t + 600.0, 0.0, 0.0, double.PositiveInfinity, 1.0f, list);
        var scytheIds = list.Select(s => s.Id.ToString()).ToHashSet();
        Check(fixtureIds > 50 && list.Count > 1000, "fixture check is vacuous");
        Check(!published.Overlaps(scytheIds), "a scythe id appears among the field's published SourceIds");
        // The field coupling reads the model only: it is the existing megastorm_field_events_test (untouched file) that asserts the ids and counts.
        string coupling = File.ReadAllText(ProjectSettings.GlobalizePath("res://scripts/Weather/Megastorm/MegastormFieldCoupling.cs"));
        Check(!coupling.Contains("MegastormScythe", StringComparison.Ordinal) && !coupling.Contains("Scythe", StringComparison.Ordinal), "the field coupling mentions scythes");
        Info($"fixture: {fixtureIds} published ids, {list.Count} scythes in 600 s, no overlap; MegastormFieldCoupling.cs has no scythe reference");
    }

    // ---- Staged run through the real layer and the node ----
    static RingLightningEvent? Find(RingVolumetricCloudLayer layer, long id)
    {
        foreach (RingLightningEvent e in layer.ActiveLightningEvents) if (e.EventId == id) return e;
        return null;
    }

    public int Tick(Node layerNode)
    {
        var layer = (RingVolumetricCloudLayer)layerNode;
        try
        {
            if (_wait > 0) { _wait--; return -1; }
            var explorer = layer.GetNode<RingVoxelExplorerController>(layer.ExplorerPath);
            RingCanonicalPosition observer = explorer.CanonicalPosition;
            Node root = layer.GetParent();
            var node = root.GetNode<RingMegastorm>("RingMegastorm");
            var scythes = root.GetNodeOrNull<RingMegastormScythes>("MegastormScythes");
            switch (_stage++)
            {
                case 0:
                {
                    Check(scythes != null, "the scene has no MegastormScythes node");
                    if (scythes == null) return Finish("LAYER");
                    layer.CloudSimulationPaused = true;
                    _model = NewModel(observer.AroundMeters + 20000.0);
                    var list = new List<MegastormScythe>();
                    MegastormScythes.ScythesInWindow(_model, 0.0, 600.0, observer.AroundMeters, observer.AcrossMeters, RingVolumetricCloudLayer.ScytheAttachmentReachMeters, 1.0f, list);
                    Check(list.Count > 0, "no scythe near the explorer (test is vacuous)");
                    if (list.Count == 0) return Finish("LAYER");
                    _target = list[0];
                    layer.MegastormSource = _model;
                    layer.CloudSimulationTimeSeconds = _target.StartTimeSeconds + 0.1;
                    _wait = 3; return -1;
                }
                case 1:
                {
                    RingLightningEvent? e = Find(layer, _target.Id);
                    Check(e.HasValue, "the attachment strike did not appear");
                    if (e is RingLightningEvent ev)
                    {
                        _first = ev;
                        Check(ev.Scythe && ev.SystemKind == MegastormModel.KindMegastorm && ev.StartTimeSeconds == _target.StartTimeSeconds, "attachment event fields / Scythe flag");
                        Check(Math.Abs(ev.GroundPosition.AroundMeters - _target.AttachAroundMeters) < 1.0 && Math.Abs(ev.GroundPosition.AcrossMeters - _target.AttachAcrossMeters) < 1.0, "attachment position");
                        Check(ev.CloudPosition.VerticalMeters > ev.GroundPosition.VerticalMeters + 200.0, "cloud point not above the ground");
                    }
                    Check(layer.ActiveLightningEvents.Where(x => x.Scythe).All(x => MegastormScythes.IsScytheId(x.EventId)), "a Scythe-flagged event without a scythe id");
                    Check(layer.ActiveLightningEvents.Where(x => !MegastormScythes.IsScytheId(x.EventId)).All(x => !x.Scythe), "a scythe flag on an ordinary strike");
                    layer.CloudSimulationTimeSeconds = _target.StartTimeSeconds + 5.0;
                    _wait = 3; return -1;
                }
                case 2:
                    Check(Find(layer, _target.Id) == null, "attachment strike outlived its lifetime");
                    layer.CloudSimulationTimeSeconds = _target.StartTimeSeconds + 0.1;   // rewind
                    _wait = 3; return -1;
                case 3:
                {
                    RingLightningEvent? e = Find(layer, _target.Id);
                    Check(e.HasValue && e.Value == _first, "rewind did not reproduce the identical attachment event");
                    layer.MegastormSource = null;
                    layer.CloudSimulationTimeSeconds = _target.StartTimeSeconds + 6.0;   // past every attachment bolt's life
                    _wait = 3; return -1;
                }
                case 4:
                {
                    Check(layer.ActiveLightningEvents.All(x => !x.Scythe), "scythe strikes without a source");
                    // The node: drive it on a paused clock with a stub observer and gate.
                    node.RestoreFrom(null);
                    bool triggered = node.Trigger(new MegastormTrigger("scythe_node", observer.AroundMeters + 20000.0, 1, 0.0, 350.0f, 0.0f, 33));
                    Info($"node trigger={triggered} triggered={node.Triggered} observer around={observer.AroundMeters:F0} across={observer.AcrossMeters:F0} width={node.Model.Settings.WidthMeters}");
                    node.ClockProvider = () => _clock;
                    scythes!.SurfaceProvider = Ground;
                    scythes.AirGateProvider = () => 0.0f;
                    scythes.Enabled = true;
                    var wide = new List<MegastormScythe>();
                    MegastormScythes.ScythesInWindow(node.Model, 0.0, 600.0, observer.AroundMeters, observer.AcrossMeters, 25000.0, 1.0f, wide);
                    Check(wide.Count > 0, "no scythe within 25 km of the explorer");
                    if (wide.Count == 0) return Finish("NODE");
                    _target = wide[0];
                    // The listener stands 800 m across from the attachment (the node only reads the observer it is given).
                    _watcher = new RingCanonicalPosition(_target.AttachAroundMeters, observer.VerticalMeters, _target.AttachAcrossMeters + 800.0);
                    scythes.ObserverProvider = () => _watcher;
                    _clock = _target.StartTimeSeconds + 0.3;
                    scythes.Step(_clock, _watcher);
                    _wait = 2; return -1;
                }
                case 5:
                {
                    Check(scythes!.ActiveCount > 0 && scythes.CreatedCount > 0, "the node drew no scythe");
                    Check(scythes.NearestDistanceMeters < 5000.0f, $"nearest scythe {scythes.NearestDistanceMeters}");
                    Info($"node: {scythes.ActiveCount} active, {scythes.DetailedCount} detailed, {scythes.ScorchCount} scorch, {scythes.PendingCrackles} crackles queued, light energy {scythes.MaxLightEnergy:F2}");
                    // Closed gate: crackles that fall due are dropped, none start.
                    int before = scythes.CrackleStarts;
                    for (double t = _clock; t < _clock + 30.0; t += 0.1) scythes.Step(t, _watcher);
                    Check(scythes.CrackleStarts == before, "crackles played with the airborne gate closed");
                    _clock += 30.0;
                    Check(scythes.PendingCrackles == 0 || scythes.CrackleDropped > 0, "closed gate neither played nor dropped");
                    // Rewind: everything is cleared and rebuilt from the stream.
                    _clock = _target.StartTimeSeconds + 0.3;
                    scythes.AirGateProvider = () => 1.0f;
                    scythes.Step(_clock, _watcher);
                    int created = scythes.CreatedCount;
                    Check(scythes.ActiveCount > 0, "rewind did not rebuild the scythe");
                    int starts = scythes.CrackleStarts;
                    for (double t = _clock; t < _clock + 30.0; t += 0.05) scythes.Step(t, _watcher);
                    bool haveAudio = (int)scythes.GetScytheDiagnostics()["crackle_streams"] > 0;
                    if (haveAudio) Check(scythes.CrackleStarts > starts, "no crackle played with the gate open");
                    else Info("crackle assets not imported in this run: playback not exercised");
                    Info($"node: rebuilt {created} total creations, crackles started {scythes.CrackleStarts - starts}, dropped {scythes.CrackleDropped}, last gate {scythes.LastCrackleGateDb:F1} dB");
                    node.ClockProvider = null;
                    node.RemoveMegastorm();
                    scythes.Step(0.0, _watcher);
                    _wait = 2; return -1;
                }
                default:
                {
                    Check(scythes!.ActiveCount == 0 && scythes.ScorchCount == 0 && scythes.PendingCrackles == 0 && scythes.FlashLevel == 0.0f, "the node kept scythes after the storm was removed");
                    int code = Finish("NODE");
                    if (code == 0) Console.WriteLine("MEGASTORM_SCYTHE=PASS");
                    return code;
                }
            }
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
    }
}
