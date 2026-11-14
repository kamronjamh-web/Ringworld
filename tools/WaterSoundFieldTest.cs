using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using RingworldTechDemo.Water;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T3R-10a (docs/work-orders/terrain-vegetation/T3.md, Spec W1 to W3, W5, W6): the pure loudness arithmetic of water sound. Checks: zero power is silence for every kind; the level rises with
/// Q, Sr, whiteness, wave height and drop, falls with distance and never rises with the half size; height is distance (open water of 0.5 m waves is silent 100 m above it, a lake shore of 0.08 m
/// 150 m above it, the wide torrent heard at 500 m and silent at 2,000 m to the side and above alike); the table of W3 (level at the water's edge within 1 dB, reach within 15 %); gain, cutoff,
/// equal-power blends and the masker. Negative controls: today's rule (H40) must fail "silent 100 m above open water", and the law with the vertical part forced to 0 must fail the height checks.
/// Prints WATER_SOUND_FIELD=PASS|FAIL. Headless: tools/run_tests.ps1 -Tests water_sound_field_test.
/// </summary>
public partial class WaterSoundFieldTest : RefCounted
{
    private readonly List<string> _failures = new();
    private void Fail(string message) { if (_failures.Count < 40) _failures.Add(message); }
    private void Check(bool ok, string message) { if (!ok) Fail(message); }
    private static void Detail(string message) => Console.WriteLine("WATER_SOUND_FIELD_DETAIL " + message);
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    // The channel estimates behind W3's table (the Spec gives Q, gradient and width; depth and whiteness are the plain estimates that reproduce its rows).
    private readonly record struct RiverRow(string Name, double Q, double Sr, double Width, double Depth, double Whiteness, double TableLevel, double TableReach);
    private readonly record struct DropRow(string Name, double Q, double Height, double HalfWidth, double TableLevel, double TableReach);
    private readonly record struct ShoreRow(string Name, WaterSoundKind Kind, double Hs, double TableLevel, double TableReach);

    private static readonly RiverRow[] RiverRows =
    {
        new("Trickle, 0.02 m^3/s on 3 %", 0.02, 0.03, 0.54, 0.10, 0.30, 24, 10),
        new("Brook, 0.5 m^3/s on 2 %, 2 m wide", 0.5, 0.02, 2, 0.35, 0.30, 27, 60),
        new("Stream, 3 m^3/s on 1 %, 6 m wide", 3, 0.01, 6, 0.55, 0.30, 25, 95),
        new("Lowland river, 20 m^3/s on 0.1 %, 17 m wide", 20, 0.001, 17, 1.25, 0.05, 10, 25),
        new("Mountain river, 20 m^3/s on 1.5 %, 13 m wide", 20, 0.015, 13, 1.00, 0.35, 32, 330),
        new("Steep cascade, 2 m^3/s on 8 %, 4 m wide, mostly white", 2, 0.08, 4, 0.38, 0.80, 43, 400),
        new("Wide torrent, 200 m^3/s on 3 %, 36 m wide, white", 200, 0.03, 36, 1.75, 1.00, 46, 1000),
        new("Great calm river, 2,000 m^3/s on 0.02 %, 230 m wide", 2000, 0.0002, 230, 5.75, 0.00, 3, 35),
    };

    private static readonly DropRow[] DropRows =
    {
        new("A 1 m chute on the brook", 0.5, 1, 1.4, 49, 230),
        new("A 1 m chute on a 20 m^3/s river", 20, 1, 10.2, 48, 610),
        new("A 10 m fall of 20 m^3/s", 20, 10, 10.2, 58, 930),
        new("A 50 m fall of 500 m^3/s", 500, 50, 28.6, 70, 1700),
    };

    private static readonly ShoreRow[] ShoreRows =
    {
        new("Lake shore, waves of 0.08 m", WaterSoundKind.LakeShore, 0.08, 20, 60),
        new("Sea shore, waves of 0.5 m", WaterSoundKind.SeaShore, 0.5, 32, 430),
        new("Sea shore, waves of 1.5 m", WaterSoundKind.SeaShore, 1.5, 38, 820),
    };

    private static double RiverP(RiverRow r) => WaterSoundField.RiverPower(r.Q, r.Sr, r.Q / (r.Width * r.Depth), r.Depth, r.Whiteness);
    private static double TorrentLevel(double distance)
    {
        RiverRow r = RiverRows[6];
        return WaterSoundField.Level(WaterSoundKind.River, RiverP(r), r.Width / 2.0, distance);
    }

    public int Run()
    {
        try
        {
            Zero();
            Monotone();
            HeightIsDistance();
            Table();
            Gain();
            Cutoff();
            Blends();
            Masker();
            NegativeControls();
        }
        catch (Exception e) { Console.Error.WriteLine("WATER_SOUND_FIELD=FAIL exception " + e); return 1; }
        if (_failures.Count != 0) { Console.Error.WriteLine("WATER_SOUND_FIELD=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("WATER_SOUND_FIELD=PASS");
        return 0;
    }

    private static double Reach(WaterSoundKind kind, double power, double halfSize, double masker = 0.0)
    {
        double lo = 0.0, hi = 1e6;
        if (WaterSoundField.Level(kind, power, halfSize, 0.0) <= masker) return 0.0;
        for (int i = 0; i < 100; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (WaterSoundField.Level(kind, power, halfSize, mid) > masker) lo = mid; else hi = mid;
        }
        return lo;
    }

    // ------------------------------------------------------------------ zero

    private void Zero()
    {
        foreach (WaterSoundKind kind in Enum.GetValues<WaterSoundKind>())
            foreach (double x in new[] { 0.0, 1.0, 100.0 })
            {
                double l = WaterSoundField.Level(kind, 0.0, 5.0, x);
                Check(double.IsNegativeInfinity(l) && WaterSoundField.Audibility(l, 0.0) == 0.0, $"zero power not silent for {kind} at {x} m (level {l})");
                var mix = WaterSoundField.Mix(new WaterSoundSource(kind, 0.0, 5.0, 0, 0, 0, x, 0.5, 20, 5, 0, 0), 0.0);
                Check(mix.Gain == 0.0 && !double.IsNaN(mix.Gain), $"zero power mix has gain {mix.Gain} for {kind}");
            }
        Check(WaterSoundField.ShorePower(0.0) == 0.0 && WaterSoundField.OpenWaterPower(0.0) == 0.0, "wave height 0 is not silent");
        Check(WaterSoundField.RiverPower(0.0, 0.01, 1, 1, 1) == 0.0, "discharge 0 is not silent");
        Check(WaterSoundField.RiverPower(10.0, 0.0, 1, 1, 1) == 0.0, "gradient 0 is not silent");
        Check(WaterSoundField.DropPower(10.0, 0.0) == 0.0 && WaterSoundField.DropPower(0.0, 10.0) == 0.0, "drop 0 or discharge 0 is not silent");
        // The start of a head: RS4's tapered Q is Q * x^(5/3) with x = 0 at the stem's start (the finder tapers; the law must make it silence).
        double qHead = 20.0 * Math.Pow(0.0, 5.0 / 3.0);
        Check(WaterSoundField.RiverPower(qHead, 0.01, 1, 1, 1) == 0.0, "the start of a head is not silent");
    }

    // ------------------------------------------------------------------ monotone

    private void Monotone()
    {
        const double a = 5.0;
        // River power rises with Q, Sr and whiteness at a fixed channel (Fr unchanged), and so does the level.
        double prev = double.NegativeInfinity;
        foreach (double q in new[] { 0.01, 0.1, 1, 10, 100, 1000 })
        {
            double l = WaterSoundField.Level(WaterSoundKind.River, WaterSoundField.RiverPower(q, 0.01, 1.0, 0.5, 0.5), a, 20.0);
            Check(l > prev, $"level does not rise with Q at {q}"); prev = l;
        }
        prev = double.NegativeInfinity;
        foreach (double sr in new[] { 0.0001, 0.001, 0.01, 0.1 })
        {
            double l = WaterSoundField.Level(WaterSoundKind.River, WaterSoundField.RiverPower(5, sr, 1.0, 0.5, 0.5), a, 20.0);
            Check(l > prev, $"level does not rise with Sr at {sr}"); prev = l;
        }
        prev = double.NegativeInfinity;
        foreach (double w in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            double l = WaterSoundField.Level(WaterSoundKind.River, WaterSoundField.RiverPower(5, 0.01, 1.0, 0.5, w), a, 20.0);
            Check(l > prev, $"level does not rise with whiteness at {w}"); prev = l;
        }
        prev = double.NegativeInfinity;
        foreach (double hs in new[] { 0.02, 0.05, 0.1, 0.25, 0.5, 1, 2, 4 })
        {
            foreach (double x in new[] { 0.0, 50.0, 500.0 })
            {
                double l = WaterSoundField.Level(WaterSoundKind.SeaShore, WaterSoundField.ShorePower(hs), WaterSoundField.ShoreHalfSize(hs), x);
                double lPrev = WaterSoundField.Level(WaterSoundKind.SeaShore, WaterSoundField.ShorePower(hs * 0.9), WaterSoundField.ShoreHalfSize(hs * 0.9), x);
                Check(l > lPrev, $"shore level does not rise with Hs at {hs}, x {x}");
            }
            double lo = WaterSoundField.Level(WaterSoundKind.OpenSea, WaterSoundField.OpenWaterPower(hs), 3.0, 10.0);
            Check(lo > prev, $"open water level does not rise with Hs at {hs}"); prev = lo;
        }
        prev = double.NegativeInfinity;
        foreach (double h in new[] { 0.5, 1, 2, 5, 10, 50 })
        {
            double l = WaterSoundField.Level(WaterSoundKind.Drop, WaterSoundField.DropPower(20, h), 8, 30);
            Check(l > prev, $"drop level does not rise with height at {h}"); prev = l;
        }
        foreach (WaterSoundKind kind in Enum.GetValues<WaterSoundKind>())
        {
            double p = 3.0;
            double lastX = double.PositiveInfinity;
            foreach (double x in new[] { 0.0, 1, 3, 10, 30, 100, 300, 1000, 3000 })
            {
                double l = WaterSoundField.Level(kind, p, 4.0, x);
                Check(l < lastX, $"level does not fall with distance for {kind} at {x}"); lastX = l;
            }
            double lastA = double.PositiveInfinity;
            foreach (double half in new[] { 0.1, 0.5, 2, 8, 30, 120 })
            {
                double l = WaterSoundField.Level(kind, p, half, 25.0);
                Check(l <= lastA, $"level rises with half size for {kind} at {half}"); lastA = l;
            }
        }
    }

    // ------------------------------------------------------------------ height is distance

    /// <summary>The height checks with the distance function given: the law proper, or the law with its vertical part forced to 0 (the negative control). Returns the failures.</summary>
    private static List<string> HeightFailures(Func<double, double, double> dist)
    {
        var fail = new List<string>();
        var kinds = new (WaterSoundKind Kind, double Power, double Half)[]
        {
            (WaterSoundKind.River, RiverP(RiverRows[4]), 6.5), (WaterSoundKind.Drop, WaterSoundField.DropPower(20, 10), 10.2),
            (WaterSoundKind.SeaShore, WaterSoundField.ShorePower(0.5), WaterSoundField.ShoreHalfSize(0.5)),
            (WaterSoundKind.LakeShore, WaterSoundField.ShorePower(0.08), WaterSoundField.ShoreHalfSize(0.08)),
            (WaterSoundKind.OpenSea, WaterSoundField.OpenWaterPower(0.5), 3.0), (WaterSoundKind.OpenLake, WaterSoundField.OpenWaterPower(0.12), 3.0),
        };
        foreach (var (kind, power, half) in kinds)
            foreach (double v in new[] { 1.0, 10.0, 100.0, 1000.0 })
            {
                double above = WaterSoundField.Level(kind, power, half, dist(0.0, v));
                double side = WaterSoundField.Level(kind, power, half, dist(v, 0.0));
                if (!(Math.Abs(above - side) < 0.01)) fail.Add($"{kind}: {v} m above gives {above:0.00} dB, {v} m to the side {side:0.00} dB");
            }
        double open = WaterSoundField.Level(WaterSoundKind.OpenSea, WaterSoundField.OpenWaterPower(0.5), 3.0, dist(0.0, 100.0));
        if (!(open < 0.0)) fail.Add($"open water of 0.5 m waves is heard 100 m above it ({open:0.0} dB)");
        double lake = WaterSoundField.Level(WaterSoundKind.LakeShore, WaterSoundField.ShorePower(0.08), WaterSoundField.ShoreHalfSize(0.08), dist(0.0, 150.0));
        if (!(lake < 0.0)) fail.Add($"a lake shore of 0.08 m waves is heard 150 m above it ({lake:0.0} dB)");
        RiverRow t = RiverRows[6];
        double p = RiverP(t);
        foreach (bool above in new[] { false, true })
        {
            double near = WaterSoundField.Level(WaterSoundKind.River, p, t.Width / 2.0, above ? dist(0.0, 500.0) : dist(500.0, 0.0));
            double far = WaterSoundField.Level(WaterSoundKind.River, p, t.Width / 2.0, above ? dist(0.0, 2000.0) : dist(2000.0, 0.0));
            if (!(near > 0.0)) fail.Add($"the wide torrent is not heard at 500 m {(above ? "above" : "to the side")} ({near:0.0} dB)");
            if (!(far < 0.0)) fail.Add($"the wide torrent is heard at 2,000 m {(above ? "above" : "to the side")} ({far:0.0} dB)");
        }
        return fail;
    }

    private void HeightIsDistance()
    {
        List<string> fail = HeightFailures(WaterSoundField.Distance);
        foreach (string f in fail) Fail("height: " + f);
        Check(Math.Abs(WaterSoundField.Distance(30, 40) - 50.0) < 1e-9, "Distance(30, 40) is not 50");
        Detail($"height checks: {(fail.Count == 0 ? "all pass" : fail.Count + " failures")}");
    }

    // ------------------------------------------------------------------ the table of W3

    private void Table()
    {
        Detail("table of W3 (source | edge dB (Spec) | reach m (Spec)):");
        void Row(string name, WaterSoundKind kind, double power, double half, double tableLevel, double tableReach, string extra = "")
        {
            double edge = WaterSoundField.Level(kind, power, half, 0.0);
            double reach = Reach(kind, power, half);
            Detail($"  {name,-58} {F(edge),6} ({F(tableLevel)})  {F(reach),6} ({F(tableReach)}){extra}");
            Check(Math.Abs(edge - tableLevel) <= 1.0, $"{name}: edge level {edge:0.00} dB, Spec {tableLevel}");
            Check(Math.Abs(reach - tableReach) <= 0.15 * tableReach, $"{name}: reach {reach:0.0} m, Spec {tableReach}");
        }
        foreach (RiverRow r in RiverRows)
            Row(r.Name, WaterSoundKind.River, RiverP(r), r.Width / 2.0, r.TableLevel, r.TableReach,
                $"   [U {F(r.Q / (r.Width * r.Depth))} m/s, d {F(r.Depth)} m, Fr^2 {F(Math.Pow(r.Q / (r.Width * r.Depth), 2) / (9.81 * r.Depth))}, white {F(r.Whiteness)}]");
        foreach (DropRow d in DropRows)
            Row(d.Name, WaterSoundKind.Drop, WaterSoundField.DropPower(d.Q, d.Height), d.HalfWidth, d.TableLevel, d.TableReach, $"   [a {F(d.HalfWidth)} m]");
        foreach (ShoreRow s in ShoreRows)
            Row(s.Name, s.Kind, WaterSoundField.ShorePower(s.Hs), WaterSoundField.ShoreHalfSize(s.Hs), s.TableLevel, s.TableReach);
        // Open water: reach is the height above the water.
        foreach ((double hs, double lvl, double reach) in new[] { (0.5, 16.0, 30.0), (2.0, 28.0, 150.0) })
            Row($"Open water, waves of {F(hs)} m (reach: height above it)", WaterSoundKind.OpenSea, WaterSoundField.OpenWaterPower(hs), 3.0, lvl, reach);
    }

    // ------------------------------------------------------------------ gain

    private void Gain()
    {
        // Linear gain (audibility applied) of the torrent and of a lake shore along distance, and of a source 1 cm away from its water.
        (WaterSoundKind Kind, double Power, double Half)[] sources =
        {
            (WaterSoundKind.River, RiverP(RiverRows[6]), 18.0), (WaterSoundKind.River, RiverP(RiverRows[0]), 0.27),
            (WaterSoundKind.Drop, WaterSoundField.DropPower(500, 50), 28.6), (WaterSoundKind.LakeShore, WaterSoundField.ShorePower(0.08), WaterSoundField.ShoreHalfSize(0.08)),
            (WaterSoundKind.OpenSea, WaterSoundField.OpenWaterPower(0.5), 3.0),
        };
        foreach (var (kind, power, half) in sources)
        {
            double maxDbStep = 0.0;
            for (double x = 0.0; x < 3000.0; x = Math.Max(x * 1.01, x + 0.01))
            {
                double l0 = WaterSoundField.Level(kind, power, half, x), l1 = WaterSoundField.Level(kind, power, half, x * 1.01);
                Check(WaterSoundField.GainDb(l0) <= 0.0 && WaterSoundField.GainDb(l1) <= 0.0, $"gain above 0 dB for {kind}");
                // Where audibility is 1 the gain in dB is min(0, L - 45): a step is the level's.
                if (l0 > 6.0 && l1 > 6.0) maxDbStep = Math.Max(maxDbStep, Math.Abs(WaterSoundField.GainDb(l0) - WaterSoundField.GainDb(l1)));
            }
            Check(maxDbStep < 0.5, $"{kind}: gain steps by {maxDbStep:0.00} dB over 1 % of distance");
            double cm = Math.Abs(WaterSoundField.GainDb(WaterSoundField.Level(kind, power, half, 0.0)) - WaterSoundField.GainDb(WaterSoundField.Level(kind, power, half, 0.01)));
            Check(cm < 0.5, $"{kind}: gain steps by {cm:0.00} dB over 1 cm at the source");
            // Silence: no step above 1 dB as L approaches M (0 and 10 dB maskers); linear gain goes smoothly to 0.
            foreach (double masker in new[] { 0.0, 10.0 })
            {
                double previous = double.NaN;
                for (double l = masker + 8.0; l >= masker - 0.5; l -= 0.01)
                {
                    double lin = Linear(l, masker);
                    if (l <= masker) Check(lin == 0.0, $"{kind}: gain {lin} at or under the masker");
                    else if (!double.IsNaN(previous) && previous > 1e-4 && lin > 1e-4)
                        Check(Math.Abs(20.0 * Math.Log10(previous / lin)) < 1.0, $"{kind}: gain steps by more than 1 dB near silence at L {l:0.00}");
                    previous = lin;
                }
            }
        }
        Check(Linear(100.0, 0.0) <= 1.0 && Math.Abs(WaterSoundField.GainDb(100.0)) < 1e-12, "a loud level is raised above 0 dB");
        Check(Math.Abs(WaterSoundField.GainDb(30.0) + 15.0) < 1e-9, "one dB of level is not one dB of gain");
    }

    private static double Linear(double level, double masker)
        => WaterSoundField.Audibility(level, masker) * Math.Pow(10.0, WaterSoundField.GainDb(level) / 20.0);

    // ------------------------------------------------------------------ cutoff

    private void Cutoff()
    {
        foreach (double x in new[] { 0.0, 1.0, 9.99, 10.0 }) Check(WaterSoundField.CutoffHz(x) >= 18000.0 - 1e-9, $"cutoff not open at {x} m");
        Check(WaterSoundField.CutoffHz(5.0) >= 20000.0 - 1e-9, "cutoff under 10 m is not fully open");
        Check(Math.Abs(WaterSoundField.CutoffHz(100.0) / 5700.0 - 1.0) < 0.05, $"cutoff at 100 m is {WaterSoundField.CutoffHz(100.0):0} Hz, Spec 5.7 kHz");
        Check(Math.Abs(WaterSoundField.CutoffHz(1000.0) / 1800.0 - 1.0) < 0.05, $"cutoff at 1 km is {WaterSoundField.CutoffHz(1000.0):0} Hz, Spec 1.8 kHz");
        for (double x = 0.0; x < 1e6; x = Math.Max(1.0, x * 1.5)) Check(WaterSoundField.CutoffHz(x) >= 800.0, $"cutoff under 800 Hz at {x} m");
        Detail($"cutoff: 10 m {F(WaterSoundField.CutoffHz(10))}, 100 m {F(WaterSoundField.CutoffHz(100))}, 1 km {F(WaterSoundField.CutoffHz(1000))} Hz");
    }

    // ------------------------------------------------------------------ blends

    private void Blends()
    {
        void Unit(WaterLoopWeights w, string what)
            => Check(Math.Abs(w.A * w.A + w.B * w.B + w.C * w.C + w.D * w.D - 1.0) < 0.01, $"{what}: squared weights sum to {w.A * w.A + w.B * w.B + w.C * w.C + w.D * w.D:0.0000}");
        for (double hs = 0.0; hs <= 2.0; hs += 0.02)
            for (double x = 0.0; x <= 400.0; x += 5.0) Unit(WaterSoundField.ShoreWeights(hs, x), $"shore Hs {hs:0.00} x {x}");
        for (double q = 0.001; q <= 5000.0; q *= 1.05) Unit(WaterSoundField.RiverWeights(q), $"river Q {q:0.###}");
        for (double q = 0.01; q <= 2000.0; q *= 1.3)
            for (double h = 0.0; h <= 8.0; h += 0.1) Unit(WaterSoundField.DropWeights(q, h), $"drop Q {q:0.###} h {h:0.0}");
        WaterLoopWeights s;
        s = WaterSoundField.ShoreWeights(0.05, 10); Check(s.A > 0.999, "shore: calm near water is not all lap");
        s = WaterSoundField.ShoreWeights(0.6, 10); Check(s.B > 0.999, "shore: big waves near are not all breakers");
        s = WaterSoundField.ShoreWeights(0.6, 400); Check(s.C > 0.999, "shore: big waves far are not all far surf");
        s = WaterSoundField.RiverWeights(0.5); Check(s.A > 0.999, "river: 0.5 m^3/s is not all babble");
        s = WaterSoundField.RiverWeights(20.0); Check(s.B > 0.999, "river: 20 m^3/s is not all rush");
        s = WaterSoundField.RiverWeights(500.0); Check(s.C > 0.999, "river: 500 m^3/s is not all roar");
        s = WaterSoundField.DropWeights(20.0, 1.0); Check(s.B > 0.999, "drop: a 1 m chute on 20 m^3/s is not all rush");
        s = WaterSoundField.DropWeights(20.0, 6.0); Check(s.D > 0.999, "drop: a 6 m fall is not all waterfall");
    }

    // ------------------------------------------------------------------ masker

    private void Masker()
    {
        Check(WaterSoundField.Masker(0, 0, 0) == 0.0 && WaterSoundField.Masker(0, 0, 14) == 0.0 && WaterSoundField.Masker(0, 3, 14) == 0.0, "masker is not 0 on a still dry day at walking and sprinting speed");
        RiverRow brook = RiverRows[1], torrent = RiverRows[6];
        double mRain = WaterSoundField.Masker(1.0, 0, 0);
        double Lvl(RiverRow r, double x) => WaterSoundField.Level(WaterSoundKind.River, RiverP(r), r.Width / 2.0, x);
        Check(WaterSoundField.Audibility(Lvl(brook, 30), mRain) == 0.0, "in rain the brook is heard at 30 m");
        Check(WaterSoundField.Audibility(Lvl(torrent, 30), mRain) > 0.0, "in rain the torrent is silent at 30 m");
        Check(WaterSoundField.Audibility(Lvl(torrent, 300), mRain) == 0.0, "in rain the torrent is heard at 300 m");
        double mFast = WaterSoundField.Masker(0, 0, 240.0);
        Check(WaterSoundField.Audibility(Lvl(torrent, 30), mFast) == 0.0, "at 240 m/s the torrent is heard at 30 m");
        Check(mFast > 40.0 && mFast < 46.0, $"masker at 240 m/s is {mFast:0.0} dB, Spec about 43");
        Detail($"masker: rain 1 {F(mRain)} dB, 240 m/s {F(mFast)} dB; torrent at 30 m {F(Lvl(torrent, 30))} dB, at 300 m {F(Lvl(torrent, 300))} dB");
    }

    // ------------------------------------------------------------------ negative controls

    /// <summary>Today's rule (H40): 1 where the listener's column is wet, else 1 - d / 640 for the column distance d.</summary>
    private static double OldRuleGain(bool columnWet, double columnDistance) => columnWet ? 1.0 : Math.Max(0.0, 1.0 - columnDistance / 640.0);

    private void NegativeControls()
    {
        // 100 m above open water the column is wet: today's rule plays at full gain, where "silent 100 m above open water" demands 0.
        double old = OldRuleGain(true, 0.0);
        Check(old != 0.0, "negative control: today's rule was expected to fail 'silent 100 m above open water' and did not");
        Detail($"control 1: today's rule at 100 m above open water gives gain {F(old)} (must not be silent: it fails the check, as expected)");
        // The law with the vertical part forced to 0 must fail the height checks.
        List<string> broken = HeightFailures((h, v) => WaterSoundField.Distance(h, 0.0));
        Check(broken.Count > 0, "negative control: the law with the vertical part forced to 0 passed the height checks");
        Detail($"control 2: vertical forced to 0 fails {broken.Count} height checks (e.g. {(broken.Count > 0 ? broken[0] : "none")})");
    }
}
