using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T3R-10b (docs/work-orders/terrain-vegetation/T3.md, Spec W4): finding the water for the water sound. A seed-1337 recipe with water.rivers, a 400 km strip graph and its runtime sampler (as
/// inland_water_sampler_test builds them). Prints V11 (the cost of one sampler call on land near a river, in a lake and at sea) and V12 (<c>RiverCarveOperator.ChannelsNear</c> against a brute
/// force over the displaced centrelines at 300, 600, 1,200 and 2,000 m) first, then checks the shores, the rivers, the drops, open water and the cost of the finder.
/// Prints WATER_SOUND_SOURCES=PASS|FAIL. Headless: tools/run_tests.ps1 -Tests water_sound_sources_test.
/// </summary>
public partial class WaterSoundSourcesTest : RefCounted
{
    private const int Seed = 1337;
    private const double StripStart = 9_804_000d;
    private const int StripKm = 400;

    private readonly List<string> _failures = new();
    private void Fail(string message) { if (_failures.Count < 30) _failures.Add(message); }
    private void Check(bool ok, string message) { if (!ok) Fail(message); }
    private static void Detail(string text) => Console.WriteLine("WATER_SOUND_SOURCES_DETAIL " + text);

    private TerrainGenerationParameters _recipe = null!;
    private VoxelTerrainSettings _settings = null!;
    private RingTerrainSampler _sampler = null!;
    private TerrainGenerationHeightModel _model = null!;
    private RiverGraph _graph = null!;
    private RiverNetwork _net = null!;
    private RiverCarveOperator _op = null!;
    private List<(long A, int Z)> _seaPoints = new(), _lakePoints = new();

    public int Run()
    {
        try
        {
            Build();
            V11();
            V12();
            Shores();
            Rivers();
            NarrowStreams();
            LakesAndHeads();
            Drops();
            OpenWater();
            Synthetic();
            Places();
            Cost();
        }
        catch (Exception e) { Console.Error.WriteLine("WATER_SOUND_SOURCES=FAIL exception " + e); return 1; }
        finally
        {
            _model?.Dispose();
            _sampler?.Dispose();
            RiverGraphStore.Clear();
            _settings?.Dispose();
        }
        if (_failures.Count != 0) { Console.Error.WriteLine("WATER_SOUND_SOURCES=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("WATER_SOUND_SOURCES=PASS");
        return 0;
    }

    // ------------------------------------------------------------------ fixture

    private void Build()
    {
        _recipe = EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1, new[] { "water.rivers" });
        _settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, _recipe);
        int workers = Math.Clamp(System.Environment.ProcessorCount / 2, 2, 6);
        var clock = Stopwatch.StartNew();
        RiverTestGraphs.Install(_recipe, _settings, StripStart, StripKm, workers);
        _sampler = new RingTerrainSampler(_settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
        _graph = _sampler.RuntimeRiverGraph!;
        _net = _graph.Network;
        _model = new TerrainGenerationHeightModel(_recipe, true, _graph);
        _op = _model.RiverOperator!;
        Detail($"strip graph and sampler: {_net.Stems.Length} stems, {_graph.Lakes.Length} lakes, built in {clock.Elapsed.TotalSeconds:F1} s");
    }

    private bool Inside(double around, double across, double margin = 20_000d)
    {
        double min = StripStart + margin, max = StripStart + StripKm * 1000d - margin;
        return around > min && around < max && across > 3000 && across < _net.WidthM - 3000;
    }

    private static double Unit(int a, int b, int salt) => RiverHash.Unit(Seed, a, b, salt);

    // ------------------------------------------------------------------ V11: the cost of one sampler call

    private double TimeSamples(IReadOnlyList<(long A, int Z)> points, out double medianUs, out double p95Us)
    {
        for (int i = 0; i < Math.Min(64, points.Count); i++) _sampler.GetTerrainSample(points[i].A, points[i].Z);
        var costs = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            _sampler.GetTerrainSample(points[i].A, points[i].Z);
            costs[i] = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        }
        double mean = costs.Average();
        Array.Sort(costs);
        medianUs = costs[costs.Length / 2]; p95Us = costs[(int)(costs.Length * 0.95)];
        return mean;
    }

    private void V11()
    {
        var land = new List<(long A, int Z)>();
        var lake = new List<(long A, int Z)>();
        var sea = new List<(long A, int Z)>();
        // Land near a river: 1,500 points 20 to 600 m from a centreline, dry.
        for (int i = 0; i < 20000 && land.Count < 1500; i++)
        {
            int stem = (int)(Unit(i, 1, 11) * _net.Stems.Length) % _net.Stems.Length;
            double s = Unit(i, 2, 12) * _net.Curves[stem].Length;
            _net.Centre(stem, s, out double a, out double z, out _, out _);
            double ang = Unit(i, 3, 13) * Math.Tau, d = 20 + Unit(i, 4, 14) * 580;
            double pa = a + Math.Cos(ang) * d, pz = z + Math.Sin(ang) * d;
            if (!Inside(pa, pz)) continue;
            land.Add(((long)Math.Round(pa), (int)Math.Round(pz)));
        }
        // Lake: sampled columns inside a lake; sea: found by walking out from the mouths of stems that end in the sea.
        for (int l = 0; l < _graph.Lakes.Length && lake.Count < 1500; l++)
        {
            RiverLake lk = _graph.Lakes[l];
            for (int row = lk.MinRow; row <= lk.MaxRow && lake.Count < 1500; row++)
                for (int col = lk.MinCol; col <= lk.MaxCol && lake.Count < 1500; col++)
                {
                    if (_graph.LakeAt(col, row) != l) continue;
                    double a0 = (col + 0.5) * 1000d, z0 = (row + 0.5) * 1000d;
                    if (!Inside(a0, z0)) continue;
                    for (int k = 0; k < 6 && lake.Count < 1500; k++)
                    {
                        long a = (long)Math.Round(a0 + (Unit(l * 977 + col, row, 20 + k) - 0.5) * 900), z = (long)Math.Round(z0 + (Unit(l * 977 + col, row, 30 + k) - 0.5) * 900);
                        if (_sampler.GetTerrainSample(a, (int)z).WaterKind == SurfaceWaterKind.Lake) lake.Add((a, (int)z));
                    }
                }
        }
        for (int stem = 0; stem < _net.Stems.Length && sea.Count < 1500; stem++)
        {
            if (_net.Stems[stem].Terminal != RiverTerminal.Sea) continue;
            _net.Centre(stem, _net.Curves[stem].Length, out double a, out double z, out _, out _);
            for (int ray = 0; ray < 16 && sea.Count < 1500; ray++)
            {
                double ang = ray * Math.Tau / 16;
                for (double r = 40; r < 4000; r *= 1.4)
                {
                    long pa = (long)Math.Round(a + Math.Cos(ang) * r), pz = (long)Math.Round(z + Math.Sin(ang) * r);
                    if (pz < 0 || pz > _net.WidthM || !Inside(pa, pz)) break;
                    if (_sampler.GetTerrainSample(pa, (int)pz).WaterKind != SurfaceWaterKind.Ocean) continue;
                    for (int k = 0; k < 20 && sea.Count < 1500; k++) sea.Add((pa + k * 7, (int)pz + k * 5));
                    break;
                }
            }
        }
        _seaPoints = sea; _lakePoints = lake;
        double mLand = TimeSamples(land, out double medLand, out double p95Land);
        Detail($"V11 land near a river ({land.Count} points): mean {mLand:F1} us, median {medLand:F1} us, p95 {p95Land:F1} us");
        if (lake.Count > 0) { double m = TimeSamples(lake, out double med, out double p95); Detail($"V11 lake ({lake.Count} points): mean {m:F1} us, median {med:F1} us, p95 {p95:F1} us"); }
        else Detail("V11 lake: no lake column found on the strip");
        if (sea.Count > 0) { double m = TimeSamples(sea, out double med, out double p95); Detail($"V11 sea ({sea.Count} points): mean {m:F1} us, median {med:F1} us, p95 {p95:F1} us"); }
        else Detail("V11 sea: no sea column found on the strip");
        Check(land.Count >= 1000, $"V11: only {land.Count} land points");
    }

    // ------------------------------------------------------------------ V12: ChannelsNear against brute force

    private sealed class StemBox { public double ALo, AHi, ZLo, ZHi; }
    private StemBox[]? _boxes;

    private void BuildBoxes()
    {
        if (_boxes != null) return;
        _boxes = new StemBox[_net.Stems.Length];
        for (int stem = 0; stem < _boxes.Length; stem++)
        {
            double length = _net.Curves[stem].Length;
            var b = new StemBox { ALo = double.MaxValue, AHi = double.MinValue, ZLo = double.MaxValue, ZHi = double.MinValue };
            for (double s = 0; ; s += 25)
            {
                double sc = Math.Min(s, length);
                _net.Centre(stem, sc, out double a, out double z, out _, out _);
                b.ALo = Math.Min(b.ALo, a); b.AHi = Math.Max(b.AHi, a); b.ZLo = Math.Min(b.ZLo, z); b.ZHi = Math.Max(b.ZHi, z);
                if (sc >= length) break;
            }
            _boxes[stem] = b;
        }
    }

    /// <summary>The distance from the point to the displaced centreline of a stem: 4 m samples, then a refinement around every sample that is a local minimum.</summary>
    private double BruteDistance(int stem, double qa, double qz, double limit, out double bestS)
    {
        double length = _net.Curves[stem].Length, best = double.MaxValue;
        bestS = 0;
        double Dist(double s)
        {
            _net.Centre(stem, s, out double a, out double z, out _, out _);
            double da = _net.WrapDelta(a - qa), dz = z - qz;
            return Math.Sqrt(da * da + dz * dz);
        }
        const double Step = 4d;
        int n = (int)Math.Ceiling(length / Step);
        var d = new double[n + 1];
        for (int i = 0; i <= n; i++) d[i] = Dist(Math.Min(length, i * Step));
        for (int i = 0; i <= n; i++)
        {
            if (!((i == 0 || d[i] <= d[i - 1]) && (i == n || d[i] <= d[i + 1])) || d[i] >= limit + 2 * Step) continue;
            double s = Math.Min(length, i * Step), lo = Math.Max(0, s - Step), hi = Math.Min(length, s + Step);
            for (int it = 0; it < 40; it++)
            {
                double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                if (Dist(m1) < Dist(m2)) hi = m2; else lo = m1;
            }
            double sm = 0.5 * (lo + hi), dm = Dist(sm);
            if (dm > d[i]) { dm = d[i]; sm = s; }
            if (dm < best) { best = dm; bestS = sm; }
        }
        return best;
    }

    private (double A, double Z) NearRiverPoint(int i, double maxDistance)
    {
        for (int k = 0; k < 1000; k++)
        {
            int stem = (int)(Unit(i, k, 41) * _net.Stems.Length) % _net.Stems.Length;
            if (_net.Curves[stem].Length < 400) continue;
            double s = Unit(i, k, 42) * _net.Curves[stem].Length;
            _net.Centre(stem, s, out double a, out double z, out _, out _);
            double ang = Unit(i, k, 43) * Math.Tau, d = Math.Sqrt(Unit(i, k, 44)) * maxDistance;
            double pa = a + Math.Cos(ang) * d, pz = z + Math.Sin(ang) * d;
            if (Inside(pa, pz, 25_000d)) return (pa, pz);
        }
        throw new InvalidOperationException("no point near a river");
    }

    private void V12()
    {
        foreach (double radius in new[] { 300d, 600d, 1200d, 2000d }) ChannelsCheck(radius, 60, "V12");
    }

    private void ChannelsCheck(double radius, int points, string label)
    {
        BuildBoxes();
        var buffer = new RiverChannelNear[8192];
        {
            int missing = 0, extra = 0, compared = 0, bad = 0, totalStems = 0;
            double worstAbs = 0, worstPct = 0, sumAbs = 0;
            long ticks = 0;
            for (int p = 0; p < points; p++)
            {
                (double qa, double qz) = NearRiverPoint(p * 7 + (int)radius, 2500d);
                long t0 = Stopwatch.GetTimestamp();
                int n = _op.ChannelsNear(qa, qz, radius, buffer);
                ticks += Stopwatch.GetTimestamp() - t0;
                var found = new Dictionary<int, double>();
                for (int i = 0; i < n; i++)
                {
                    double d = Math.Abs(buffer[i].T);
                    found[buffer[i].Stem] = found.TryGetValue(buffer[i].Stem, out double old) ? Math.Min(old, d) : d;
                }
                for (int stem = 0; stem < _net.Stems.Length; stem++)
                {
                    StemBox b = _boxes![stem];
                    double da = _net.WrapDelta(qa - 0.5 * (b.ALo + b.AHi)), dz = qz - 0.5 * (b.ZLo + b.ZHi);
                    double ha = 0.5 * (b.AHi - b.ALo) + radius + 60, hz = 0.5 * (b.ZHi - b.ZLo) + radius + 60;
                    if (Math.Abs(da) > ha || Math.Abs(dz) > hz) { if (found.ContainsKey(stem)) { extra++; Fail($"{label} {radius} m point {p}: stem {stem} found far outside its box"); } continue; }
                    double brute = BruteDistance(stem, qa, qz, radius, out _);
                    bool within = brute <= radius;
                    bool has = found.TryGetValue(stem, out double got);
                    double tolerance = Math.Max(1d, 0.03 * radius);
                    if (within && !has) { missing++; if (brute < radius - tolerance) { bad++; Fail($"{label} {radius} m point {p}: stem {stem} at {brute:F1} m is missing"); } }
                    else if (!within && has) { extra++; if (brute > radius + tolerance) { bad++; Fail($"{label} {radius} m point {p}: stem {stem} found at {got:F1} m, brute {brute:F1} m"); } }
                    if (within && has)
                    {
                        compared++;
                        double err = Math.Abs(got - brute), allowed = Math.Max(1d, 0.03 * brute);
                        worstAbs = Math.Max(worstAbs, err); worstPct = Math.Max(worstPct, err / Math.Max(brute, 1d)); sumAbs += err;
                        if (err > allowed) { bad++; if (bad < 12) Detail($"{label} {radius} m point {p}: stem {stem} T {got:F1} m, brute {brute:F1} m, error {err:F1} m"); }
                    }
                    if (within) totalStems++;
                }
            }
            Detail($"{label} radius {radius:F0} m: {points} points, {totalStems} stems within by brute force, {compared} compared, {missing} missing, {extra} extra, mean error {(compared > 0 ? sumAbs / compared : 0):F2} m, worst {worstAbs:F1} m ({worstPct:P1}), outside the larger of 1 m and 3 %: {bad}, query {ticks * 1e6 / Stopwatch.Frequency / points:F1} us each");
        }
    }
}
