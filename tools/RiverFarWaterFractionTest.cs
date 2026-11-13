using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T2R-12 (docs/work-orders/terrain-vegetation/T2.md, Spec S11): the far river line coverage of <see cref="RiverGraph.LineCoverage(double, double, double, double)"/> on a 100 km strip graph.
/// Checks: the channel area summed over a 20 x 20 km window in 500 m cells (the raw piece-in-rectangle area, before the minimum) equals an independent 2 m walk along every stem within 10 %;
/// coverage is 0 for a 512 m cell more than 2 km from a channel and for a 4 km cell whose edge is more than 2 km away; a cell any channel crosses has at least 0.2 at 512 m and at 4 km; the
/// per-span weight returns the weight over the length inside and 0 where nothing crosses; the vertex and tile encodings are 1 - c and 255 - round(254 c); and a generation-1 sampler has coverage 0, vertex
/// alpha 1 and tile alpha 255 everywhere. Negative controls: a reference 15 % off fails the sum check, a distance limit of 1 km fails the zero check, the raw area (no minimum) fails the minimum check, and the
/// rivers sampler is shown to fail the generation-1 loop. Prints RIVER_FAR_WATER_FRACTION=PASS|FAIL. Headless: tools/run_tests.ps1 -Tests river_far_water_fraction_test.
/// </summary>
public partial class RiverFarWaterFractionTest : RefCounted
{
    private const int Seed = 1337;
    private const double StripStart = 9_804_000d;
    private const int StripKm = 100;
    private const double WindowMeters = 20_000d;
    /// <summary>The S11 minimum, written out here so a change of the constant is caught.</summary>
    private const double MinimumCoverage = 0.2;

    private readonly List<string> _failures = new();
    private void Fail(string message) { if (_failures.Count < 30) _failures.Add(message); }
    private void Check(bool ok, string message) { if (!ok) Fail(message); }
    private static void Detail(string text) => Console.WriteLine("RIVER_FAR_WATER_FRACTION_DETAIL " + text);

    private RingTerrainSampler _sampler = null!;
    private RiverGraph _graph = null!;
    private double[] _lineAround = Array.Empty<double>(), _lineAcross = Array.Empty<double>();
    private (double Around, double Across)[] _channel = Array.Empty<(double, double)>();

    public int Run()
    {
        TerrainGenerationParameters recipe = EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1, new[] { "water.rivers" });
        using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, recipe);
        try
        {
            var clock = Stopwatch.StartNew();
            RiverTestGraphs.Install(recipe, settings, StripStart, StripKm, Math.Clamp(System.Environment.ProcessorCount / 2, 2, 6));
            Detail($"strip graph installed in {clock.Elapsed.TotalSeconds:F1} s");
            _sampler = new RingTerrainSampler(settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
            _graph = _sampler.RuntimeRiverGraph!;
            Check(_sampler.HasRiverLine, "a rivers sampler reports no river line");
            WalkChannel();
            WindowSum();
            ZeroAndMinimum();
            Weights();
            Encodings();
            GenerationOne();
        }
        catch (Exception e) { Console.Error.WriteLine("RIVER_FAR_WATER_FRACTION=FAIL exception " + e); return 1; }
        finally
        {
            _sampler?.Dispose();
            RiverGraphStore.Clear();
        }
        if (_failures.Count != 0) { Console.Error.WriteLine("RIVER_FAR_WATER_FRACTION=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("RIVER_FAR_WATER_FRACTION=PASS");
        return 0;
    }

    /// <summary>Every stem walked in 2 m steps of arc length on the base curve; the points for the distance checks every 10th step, and the full widths for the reference area.</summary>
    private readonly List<(double Around, double Across, double Width)> _walk = new();

    private void WalkChannel()
    {
        RiverNetwork net = _graph.Network;
        var around = new List<double>();
        var across = new List<double>();
        for (int stem = 0; stem < net.Stems.Length; stem++)
        {
            RiverStemCurve curve = net.Curves[stem];
            int n = 0;
            for (double s = 0; s <= curve.Length; s += 2d, n++)
            {
                curve.Eval(s, out double x, out double y, out _, out _);
                net.ChannelAt(stem, s, out RiverChannel channel);
                double a = curve.OriginAround + x, c = curve.OriginAcross + y;
                _walk.Add((a, c, 2d * channel.HalfWidth));
                if (n % 10 == 0) { around.Add(a); across.Add(c); }
            }
        }
        _lineAround = around.ToArray(); _lineAcross = across.ToArray();
        _channel = new (double, double)[_lineAround.Length];
        for (int i = 0; i < _channel.Length; i++) _channel[i] = (_lineAround[i], _lineAcross[i]);
        Check(_walk.Count > 1000, $"the strip graph has only {_walk.Count} channel samples");
        Detail($"strip: {net.Stems.Length} stems, {_walk.Count * 2d / 1000d:F0} km of channel");
    }

    private double ReferenceArea(double aroundMin, double acrossMin)
    {
        double area = 0;
        foreach ((double a, double c, double w) in _walk)
        {
            double da = _graph.Network.WrapDelta(a - aroundMin);
            if (da >= 0 && da < WindowMeters && c >= acrossMin && c < acrossMin + WindowMeters) area += 2d * w;
        }
        return area;
    }

    private static bool Within(double value, double reference, double tolerance) => reference > 0 && Math.Abs(value - reference) <= tolerance * reference;

    private void WindowSum()
    {
        double aroundMin = StripStart, bestAcross = 0, best = 0;
        for (double a0 = StripStart; a0 + WindowMeters <= StripStart + StripKm * 1000d; a0 += 5_000d)
        for (double across = 0; across + WindowMeters <= EmitterSiteTestWorlds.Width; across += 5_000d)
        {
            double area = ReferenceArea(a0, across);
            if (area > best) { best = area; bestAcross = across; aroundMin = a0; }
        }
        Check(best > 0, "no channel in any 20 x 20 km window of the strip");
        const int cells = 40;
        double cell = WindowMeters / cells, sum = 0, floored = 0;
        for (int i = 0; i < cells; i++)
        for (int j = 0; j < cells; j++)
        {
            double a0 = aroundMin + i * cell, c0 = bestAcross + j * cell;
            sum += _graph.LineArea(a0, a0 + cell, c0, c0 + cell, null, out _, out _);
            floored += _graph.LineCoverage(a0, a0 + cell, c0, c0 + cell) * cell * cell;
        }
        Detail($"window at around {aroundMin - StripStart:F0} m, across {bestAcross:F0} m: graph channel area {best:F0} m2, summed coverage area {sum:F0} m2 ({100 * (sum - best) / best:+0.00;-0.00} %); with the minimum {floored:F0} m2");
        Check(Within(sum, best, 0.10), $"summed coverage {sum:F0} m2 is not within 10 % of the channel area {best:F0} m2");
        // Negative control: a reference 15 % off must fail the same predicate.
        Check(!Within(sum, best * 1.15, 0.10) && !Within(sum, best * 0.85, 0.10), "NEGATIVE CONTROL FAILED: the sum check accepted a reference 15 % off");
    }

    private double DistanceToChannel(double around, double across)
    {
        double best = double.MaxValue;
        for (int i = 0; i < _lineAround.Length; i++)
        {
            double da = _graph.Network.WrapDelta(_lineAround[i] - around), dc = _lineAcross[i] - across;
            double d = da * da + dc * dc;
            if (d < best) best = d;
        }
        return Math.Sqrt(best);
    }

    private void ZeroAndMinimum()
    {
        var rng = new Random(1234);
        int far512 = 0, far4k = 0, near512 = 0, near4k = 0, leaks512 = 0, leaks4k = 0, shortMin = 0, rawBelow = 0, farTight = 0;
        double minNear512 = 1, minNear4k = 1;
        var clock = Stopwatch.StartNew();
        long calls = 0;
        for (int n = 0; n < 4000; n++)
        {
            double around, across;
            if (n % 2 == 0)
            {
                // Beside a channel: a channel point plus up to 2.5 km of offset.
                (double a, double c) = _channel[rng.Next(_channel.Length)];
                around = a + (rng.NextDouble() * 2 - 1) * 2500d; across = c + (rng.NextDouble() * 2 - 1) * 2500d;
            }
            else
            {
                around = StripStart + 5_000d + rng.NextDouble() * 90_000d; across = rng.NextDouble() * EmitterSiteTestWorlds.Width;
            }
            double d = DistanceToChannel(around, across);
            double c512 = _graph.LineCoverage(around - 256, around + 256, across - 256, across + 256);
            double c4k = _graph.LineCoverage(around - 2000, around + 2000, across - 2000, across + 2000);
            calls += 2;
            Check(c512 >= 0 && c512 <= 1 && c4k >= 0 && c4k <= 1, $"coverage out of [0, 1]: {c512}, {c4k}");
            if (d > 2000d) { far512++; if (c512 != 0d) leaks512++; }
            if (d > 2000d + 2000d * Math.Sqrt(2d)) { far4k++; if (c4k != 0d) leaks4k++; }
            if (d > 1000d && c4k != 0d) farTight++;
            if (d < 200d)
            {
                near512++; minNear512 = Math.Min(minNear512, c512);
                if (c512 < MinimumCoverage - 1e-9) shortMin++;
            }
            if (d < 1500d)
            {
                near4k++; minNear4k = Math.Min(minNear4k, c4k);
                if (c4k < MinimumCoverage - 1e-9) shortMin++;
                double raw = _graph.LineArea(around - 2000, around + 2000, across - 2000, across + 2000, null, out _, out _) / 16_000_000d;
                if (raw < MinimumCoverage) rawBelow++;
            }
        }
        double usPerCall = clock.Elapsed.TotalMilliseconds * 1000d / calls;
        Detail($"zero check: {far512} cells of 512 m beyond 2 km ({leaks512} nonzero), {far4k} cells of 4 km beyond 4.8 km ({leaks4k} nonzero); minimum check: {near512} cells of 512 m (min {minNear512:F3}), {near4k} of 4 km (min {minNear4k:F3}); {usPerCall:F1} us per call including the brute-force distances");
        Check(far512 > 200 && far4k > 50 && near512 > 100 && near4k > 200, $"too few samples in a class: {far512}, {far4k}, {near512}, {near4k}");
        Check(leaks512 == 0, $"{leaks512} cells of 512 m more than 2 km from a channel have coverage");
        Check(leaks4k == 0, $"{leaks4k} cells of 4 km with every channel beyond 2 km have coverage");
        Check(shortMin == 0, $"{shortMin} crossed cells have less than the 0.2 minimum");
        // Negative controls: a distance limit of 1 km leaks (the check would fail), and the raw area without the minimum is below 0.2 for narrow rivers in a 4 km cell.
        Check(farTight > 0, "NEGATIVE CONTROL FAILED: no 4 km cell has coverage beyond 1 km, so the zero check cannot fail at a 1 km limit");
        Check(rawBelow > 0, "NEGATIVE CONTROL FAILED: the raw area never falls below the minimum, so the minimum check cannot fail without the floor");
        Detail($"controls: {farTight} 4 km cells beyond a 1 km limit with coverage, {rawBelow} crossed 4 km cells whose raw coverage is under 0.2");
        // Timing of a coverage call on a cell with channel against one without.
        (double ca, double cc) = _channel[_channel.Length / 2];
        var timer = Stopwatch.StartNew();
        double sink = 0;
        for (int i = 0; i < 200_000; i++) sink += _graph.LineCoverage(ca - 256 + (i & 7), ca + 256 + (i & 7), cc - 256, cc + 256);
        double onUs = timer.Elapsed.TotalMilliseconds * 1000d / 200_000;
        timer.Restart();
        double faraway = StripStart + 99_000d;
        for (int i = 0; i < 200_000; i++) sink += _graph.LineCoverage(faraway, faraway + 512, 300_000, 300_512);
        double offUs = timer.Elapsed.TotalMilliseconds * 1000d / 200_000;
        Detail($"LineCoverage 512 m cell: {onUs:F2} us on a channel, {offUs:F2} us in empty country (sink {sink:F1})");
    }

    private void Weights()
    {
        (double a, double c) = _channel[_channel.Length / 3];
        double cov = _graph.LineCoverage(a - 256, a + 256, c - 256, c + 256, span => 0.5, out double mean);
        Check(cov >= MinimumCoverage && Math.Abs(mean - 0.5) < 1e-9, $"a constant weight of 0.5 gave a mean of {mean} (coverage {cov})");
        double none = _graph.LineCoverage(StripStart + 99_000d, StripStart + 99_512d, 300_000, 300_512, span => 0.5, out double noneMean);
        Check(none == 0d && noneMean == 0d, $"a cell with no channel gave coverage {none} and mean {noneMean}");
        double wa = _graph.LineCoverage(a - 256, a + 256, c - 256, c + 256, span => span >= 0 ? 1d : 0d, out double spanMean);
        Check(wa > 0 && spanMean >= 0.99, $"spans are not installed or the weight is not passed: mean {spanMean}");
    }

    private void Encodings()
    {
        (double a, double c) = _channel[_channel.Length / 2];
        double cov = _sampler.RiverLineCoverage(a, c, 512, 512);
        Color color = _sampler.WithRiverLineAlpha(new Color(0.3f, 0.4f, 0.2f, 1f), a, c, 512, 512);
        byte tile = _sampler.RiverLineTileAlpha(a, c, 512, 512);
        Check(cov >= MinimumCoverage, $"coverage on the channel is {cov}");
        Check(Math.Abs(color.A - (1f - cov)) < 1e-6 && color.R == 0.3f && color.G == 0.4f && color.B == 0.2f, $"vertex encoding: alpha {color.A} for coverage {cov}");
        Check(tile == 255 - (int)Math.Round(254d * cov) && tile < 255, $"tile encoding: {tile} for coverage {cov}");
        // Cells above 4 km carry the plain area fraction, without the minimum (a vertex of the coarse global tiers must not tint a whole cell for one river).
        double coarse = _sampler.RiverLineCoverage(a, c, 20_000, 20_000);
        double coarseArea = _graph.LineArea(a - 10_000, a + 10_000, c - 10_000, c + 10_000, null, out _, out _) / 4e8;
        Check(coarse > 0d && Math.Abs(coarse - coarseArea) < 1e-12 && coarse < MinimumCoverage, $"a 20 km cell has coverage {coarse} against the plain fraction {coarseArea}");
        double none = _sampler.RiverLineCoverage(StripStart + 99_000d, 300_000, 512, 512);
        Check(none == 0d && _sampler.RiverLineTileAlpha(StripStart + 99_000d, 300_000, 512, 512) == 255
            && _sampler.WithRiverLineAlpha(new Color(0.3f, 0.4f, 0.2f, 1f), StripStart + 99_000d, 300_000, 512, 512).A == 1f, "a cell with no channel changed an alpha");
    }

    private void GenerationOne()
    {
        using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1));
        using var gen1 = new RingTerrainSampler(settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
        Check(!gen1.HasRiverLine, "a generation-1 sampler reports a river line");
        int nonzero = 0, loopNonzeroOnRivers = 0, n = 0;
        var rng = new Random(77);
        foreach (double size in new[] { 512d, 4000d, 20_000d })
        for (int i = 0; i < 4000; i++)
        {
            (double ca, double cc) = _channel[rng.Next(_channel.Length)];
            double around = i % 2 == 0 ? ca : rng.NextDouble() * EmitterSiteTestWorlds.Circumference;
            double across = i % 2 == 0 ? cc : rng.NextDouble() * EmitterSiteTestWorlds.Width;
            if (gen1.RiverLineCoverage(around, across, size, size) != 0d
                || gen1.RiverLineTileAlpha(around, across, size, size) != 255
                || gen1.WithRiverLineAlpha(new Color(0.2f, 0.3f, 0.4f, 1f), around, across, size, size).A != 1f) nonzero++;
            if (_sampler.RiverLineCoverage(around, across, size, size) != 0d) loopNonzeroOnRivers++;
            n++;
        }
        Check(nonzero == 0, $"{nonzero} of {n} generation-1 cells carry coverage");
        // Negative control: the same loop on the rivers sampler finds coverage, so the generation-1 loop can fail.
        Check(loopNonzeroOnRivers > 0, "NEGATIVE CONTROL FAILED: the rivers sampler gives no coverage in the generation-1 loop");
        Detail($"generation 1: {n} cells, {nonzero} with coverage; the loop finds {loopNonzeroOnRivers} on the rivers sampler");
    }
}
