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
/// Checks: the channel area summed over a 20 x 20 km window in 500 m cells (the piece-in-rectangle area) equals an independent 2 m walk along every stem within 10 %;
/// coverage is 0 for a 512 m cell more than 2 km from a channel and for a 4 km cell whose edge is more than 2 km away; a cell coverage equals the plain area fraction at 512 m and 4 km (T2R-12b removed the 0.2 minimum); the
/// per-span weight returns the weight over the length inside and 0 where nothing crosses; the vertex and tile encodings are 1 - c and 255 - round(254 c); and a generation-1 sampler has coverage 0, vertex
/// alpha 1 and tile alpha 255 everywhere. Negative controls: a reference 15 % off fails the sum check, a distance limit of 1 km fails the zero check, a thin river (plain coverage under 0.2) fails a floored coverage, and the
/// rivers sampler is shown to fail the generation-1 loop. T2R-12b adds the proxy splat (conservation within 5 % at 256, 512, 2048 and 8192 m, bit-equal shared columns, zero beyond reach, the owner rule at 256 m, a wide river above 0.8), TrySplatRiverLine and WithoutFarRiverOwner. Prints RIVER_FAR_WATER_FRACTION=PASS|FAIL. Headless: tools/run_tests.ps1 -Tests river_far_water_fraction_test.
/// </summary>
public partial class RiverFarWaterFractionTest : RefCounted
{
    private const int Seed = 1337;
    private const double StripStart = 9_804_000d;
    private const int StripKm = 100;
    private const double WindowMeters = 20_000d;

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
            ZeroAndPlain();
            Weights();
            Encodings();
            ProxySplat();
            FarOwner();
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
    private readonly List<(double Around, double Across, double Width, double DAround, double DAcross, double HalfWidth, double DStep)> _walk = new();

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
                curve.Eval(s, out double x, out double y, out double tx, out double ty);
                net.ChannelAt(stem, s, out RiverChannel channel);
                double a = curve.OriginAround + x, c = curve.OriginAcross + y, d = net.Meander(stem, s, in channel);
                double da = a - d * ty, dc = c + d * tx;
                double step = _walk.Count > 0 && n > 0 ? Math.Sqrt((da - _walk[^1].DAround) * (da - _walk[^1].DAround) + (dc - _walk[^1].DAcross) * (dc - _walk[^1].DAcross)) : 2d;
                _walk.Add((a, c, 2d * channel.HalfWidth, da, dc, channel.HalfWidth, step));
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
        foreach ((double a, double c, double w, _, _, _, _) in _walk)
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
        Detail($"window at around {aroundMin - StripStart:F0} m, across {bestAcross:F0} m: graph channel area {best:F0} m2, summed coverage area {sum:F0} m2 ({100 * (sum - best) / best:+0.00;-0.00} %); plain fraction {floored:F0} m2");
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

    private void ZeroAndPlain()
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
                double plain512 = Math.Min(1d, _graph.LineArea(around - 256, around + 256, across - 256, across + 256, null, out _, out _) / (512d * 512d));
                if (Math.Abs(c512 - plain512) > 1e-12) shortMin++;
                if (plain512 > 0d && plain512 < 0.2) rawBelow++;
            }
            if (d < 1500d)
            {
                near4k++; minNear4k = Math.Min(minNear4k, c4k);
                double raw = _graph.LineArea(around - 2000, around + 2000, across - 2000, across + 2000, null, out _, out _) / 16_000_000d;
                if (Math.Abs(c4k - Math.Min(1d, raw)) > 1e-12) shortMin++;
                if (raw > 0d && raw < 0.2) rawBelow++;
            }
        }
        double usPerCall = clock.Elapsed.TotalMilliseconds * 1000d / calls;
        Detail($"zero check: {far512} cells of 512 m beyond 2 km ({leaks512} nonzero), {far4k} cells of 4 km beyond 4.8 km ({leaks4k} nonzero); plain-fraction check: {near512} cells of 512 m (min {minNear512:F3}), {near4k} of 4 km (min {minNear4k:F3}); {usPerCall:F1} us per call including the brute-force distances");
        Check(far512 > 200 && far4k > 50 && near512 > 100 && near4k > 200, $"too few samples in a class: {far512}, {far4k}, {near512}, {near4k}");
        Check(leaks512 == 0, $"{leaks512} cells of 512 m more than 2 km from a channel have coverage");
        Check(leaks4k == 0, $"{leaks4k} cells of 4 km with every channel beyond 2 km have coverage");
        Check(shortMin == 0, $"{shortMin} cells near a channel differ from the plain area fraction (a floor is applied)");
        // Negative controls: a distance limit of 1 km leaks (the check would fail), and a thin river gives a plain coverage under 0.2, so a floor would fail the plain-fraction check.
        Check(farTight > 0, "NEGATIVE CONTROL FAILED: no 4 km cell has coverage beyond 1 km, so the zero check cannot fail at a 1 km limit");
        Check(rawBelow > 0, "NEGATIVE CONTROL FAILED: no cell has a plain coverage under 0.2, so the plain-fraction check cannot fail on a floor");
        Detail($"controls: {farTight} 4 km cells beyond a 1 km limit with coverage, {rawBelow} crossed cells whose plain coverage is under 0.2");
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
        Check(cov > 0d && Math.Abs(mean - 0.5) < 1e-9, $"a constant weight of 0.5 gave a mean of {mean} (coverage {cov})");
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
        Check(cov > 0d, $"coverage on the channel is {cov}");
        Check(Math.Abs(cov - Math.Min(1d, _graph.LineArea(a - 256, a + 256, c - 256, c + 256, null, out _, out _) / (512d * 512d))) < 1e-12, $"coverage {cov} at 512 m is not the plain area fraction");
        Check(Math.Abs(color.A - (1f - cov)) < 1e-6 && color.R == 0.3f && color.G == 0.4f && color.B == 0.2f, $"vertex encoding: alpha {color.A} for coverage {cov}");
        Check(tile == 255 - (int)Math.Round(254d * cov) && tile < 255, $"tile encoding: {tile} for coverage {cov}");
        // Every cell size carries the plain area fraction (T2R-12b): no minimum, so a river is as wide as it is.
        double coarse = _sampler.RiverLineCoverage(a, c, 20_000, 20_000);
        double coarseArea = _graph.LineArea(a - 10_000, a + 10_000, c - 10_000, c + 10_000, null, out _, out _) / 4e8;
        Check(coarse > 0d && Math.Abs(coarse - coarseArea) < 1e-12 && coarse < 0.2, $"a 20 km cell has coverage {coarse} against the plain fraction {coarseArea}");
        double none = _sampler.RiverLineCoverage(StripStart + 99_000d, 300_000, 512, 512);
        Check(none == 0d && _sampler.RiverLineTileAlpha(StripStart + 99_000d, 300_000, 512, 512) == 255
            && _sampler.WithRiverLineAlpha(new Color(0.3f, 0.4f, 0.2f, 1f), StripStart + 99_000d, 300_000, 512, 512).A == 1f, "a cell with no channel changed an alpha");
    }


    // ------------------------------------------------------------------ T2R-12b: proxy splat

    private (double Around, double Across) BestWindowCentre()
    {
        double aroundMin = StripStart, bestAcross = 0, best = 0;
        for (double a0 = StripStart; a0 + WindowMeters <= StripStart + StripKm * 1000d; a0 += 5_000d)
        for (double across = 0; across + WindowMeters <= EmitterSiteTestWorlds.Width; across += 5_000d)
        {
            double area = ReferenceArea(a0, across);
            if (area > best) { best = area; bestAcross = across; aroundMin = a0; }
        }
        return (aroundMin + 0.5 * WindowMeters, bestAcross + 0.5 * WindowMeters);
    }

    /// <summary>Valley-only channel area (walk of 2 m steps, displaced by the meander when the level is) inside the lattice rectangle [origin, origin + (n - 1) spacing).</summary>
    private double ReferenceSplatArea(double aroundOrigin, double acrossOrigin, int spacing, int n, double footprint)
    {
        bool displaced = spacing <= 1024;
        double extent = (n - 1) * (double)spacing, area = 0d;
        foreach ((double a, double c, double w, double da, double dc, double hw, double step) in _walk)
        {
            if (!RiverCarveOperator.ValleyOnly(footprint, hw)) continue;
            double pa = displaced ? da : a, pc = displaced ? dc : c;
            if (pa >= aroundOrigin && pa < aroundOrigin + extent && pc >= acrossOrigin && pc < acrossOrigin + extent) area += (displaced ? step : 2d) * w;
        }
        return area;
    }

    private void ProxySplat()
    {
        (double centreAround, double centreAcross) = BestWindowCentre();
        const int n = 64;
        var values = new float[n * n];
        double maxHalf = 0d;
        foreach (var w in _walk) maxHalf = Math.Max(maxHalf, w.HalfWidth);
        foreach (int spacing in new[] { 256, 512, 2048, 8192 })
        {
            double footprint = spacing == 256 ? RiverFootprint.Far : RiverFootprint.ToMeters(RiverFootprint.ForProxySpacing(spacing));
            double aroundOrigin = Math.Round((centreAround - 0.5 * n * spacing) / spacing) * spacing, acrossOrigin = Math.Round((centreAcross - 0.5 * n * spacing) / spacing) * spacing;
            bool any = _graph.SplatLineArea(aroundOrigin, acrossOrigin, spacing, n, n, footprint, values);
            double sum = 0d, max = 0d;
            int clamped = 0;
            foreach (float v in values) { sum += (double)v * spacing * spacing; max = Math.Max(max, v); if (v >= 1f) clamped++; }
            double reference = ReferenceSplatArea(aroundOrigin, acrossOrigin, spacing, n, footprint);
            Detail($"splat {spacing} m: {(any ? "covered" : "empty")}, lattice area {sum:F0} m2 against the valley-only channel area {reference:F0} m2 ({(reference > 0 ? 100 * (sum - reference) / reference : 0):+0.00;-0.00} %), max {max:F3}, {clamped} at 1");
            Check(reference > 0d && Within(sum, reference, 0.05), $"splat at {spacing} m holds {sum:F0} m2 against the channel area {reference:F0} m2 (5 %)");
            Check(clamped == 0, $"splat at {spacing} m clamped {clamped} vertices");
            // Zero beyond reach: a vertex farther than one spacing diagonal, the half width, the 350 m meander cap and the walk step from every base-curve point is 0.
            double limit = Math.Sqrt(2d) * spacing + maxHalf + 350d + 20d;
            if (spacing <= 2048)
            {
                int beyond = 0, leaks = 0;
                for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    if (DistanceToChannel(aroundOrigin + x * spacing, acrossOrigin + z * spacing) <= limit) continue;
                    beyond++;
                    if (values[z * n + x] != 0f) leaks++;
                }
                Check(beyond > 100 && leaks == 0, $"{leaks} of {beyond} vertices beyond the splat reach at {spacing} m have coverage");
            }
        }
        // Owner rule: at 256 m a segment 16 m or wider is a river with an owner and adds nothing; the same lattice with no owners (Far) holds more.
        {
            const int spacing = 256;
            double aroundOrigin = Math.Round((centreAround - 0.5 * n * spacing) / spacing) * spacing, acrossOrigin = Math.Round((centreAcross - 0.5 * n * spacing) / spacing) * spacing;
            var all = new float[n * n];
            _graph.SplatLineArea(aroundOrigin, acrossOrigin, spacing, n, n, 256d, values);
            _graph.SplatLineArea(aroundOrigin, acrossOrigin, spacing, n, n, RiverFootprint.Far, all);
            double narrow = 0d, every = 0d;
            for (int i = 0; i < values.Length; i++) { narrow += values[i]; every += all[i]; }
            double wideRef = ReferenceSplatArea(aroundOrigin, acrossOrigin, spacing, n, RiverFootprint.Far) - ReferenceSplatArea(aroundOrigin, acrossOrigin, spacing, n, 256d);
            Detail($"owner rule at 256 m: {narrow * spacing * spacing:F0} m2 with owners of 16 m and wider, {every * spacing * spacing:F0} m2 without; the wide segments hold {wideRef:F0} m2");
            Check(wideRef > 1000d && every > narrow * 1.02, $"the 256 m owner rule did not remove wide segments (wide reference {wideRef:F0} m2, {narrow:F1} against {every:F1})");
            // At 512 m and coarser every piece adds: ValleyOnly is true for any width at the far footprint (the conservation sums above use it).
            Check(RiverCarveOperator.ValleyOnly(RiverFootprint.Far, maxHalf) && RiverCarveOperator.ValleyOnly(RiverFootprint.Far, 0.5), "a river has an owner at the far footprint");
            Check(!RiverCarveOperator.ValleyOnly(256d, 8.5) && RiverCarveOperator.ValleyOnly(256d, 7.5), "the 256 m owner threshold is not 16 m");
        }
        // Seam: two lattices that share a column (and a row) give bit-equal values on it.
        foreach (int spacing in new[] { 256, 512, 4096 })
        {
            const int m = 17;
            double footprint = spacing == 256 ? RiverFootprint.Far : RiverFootprint.ToMeters(RiverFootprint.ForProxySpacing(spacing));
            // The shared column and row pass through the window centre.
            double a0 = Math.Round(centreAround / spacing) * spacing - (m - 1) * spacing, z0 = Math.Round(centreAcross / spacing) * spacing - (m - 1) * spacing;
            var left = new float[m * m];
            var right = new float[m * m];
            var below = new float[m * m];
            _graph.SplatLineArea(a0, z0, spacing, m, m, footprint, left);
            _graph.SplatLineArea(a0 + (m - 1) * spacing, z0, spacing, m, m, footprint, right);
            _graph.SplatLineArea(a0, z0 + (m - 1) * spacing, spacing, m, m, footprint, below);
            int nonzero = 0, differ = 0;
            for (int z = 0; z < m; z++)
            {
                float l = left[z * m + (m - 1)], r = right[z * m];
                if (l != 0f) nonzero++;
                if (BitConverter.SingleToInt32Bits(l) != BitConverter.SingleToInt32Bits(r)) differ++;
            }
            for (int x = 0; x < m; x++)
            {
                float l = left[(m - 1) * m + x], r = below[x];
                if (l != 0f) nonzero++;
                if (BitConverter.SingleToInt32Bits(l) != BitConverter.SingleToInt32Bits(r)) differ++;
            }
            Detail($"seam at {spacing} m: {nonzero} nonzero shared vertices, {differ} differ");
            Check(nonzero > 0, $"the seam check at {spacing} m has no river on the shared edge");
            Check(differ == 0, $"{differ} shared vertices differ between two lattices at {spacing} m");
        }
        // Wide river: the widest reach, a lattice of half its width, no owners.
        {
            var widest = _walk[0];
            foreach (var w in _walk) if (w.HalfWidth > widest.HalfWidth) widest = w;
            double spacing = widest.HalfWidth;
            const int k = 21;
            double aroundOrigin = Math.Round(widest.DAround / spacing - k / 2) * spacing, acrossOrigin = Math.Round(widest.DAcross / spacing - k / 2) * spacing;
            var wide = new float[k * k];
            _graph.SplatLineArea(aroundOrigin, acrossOrigin, spacing, k, k, RiverFootprint.Far, wide);
            int col = (int)Math.Round((widest.DAround - aroundOrigin) / spacing), row = (int)Math.Round((widest.DAcross - acrossOrigin) / spacing);
            float centre = wide[row * k + col];
            Detail($"wide river: width {widest.Width:F0} m, spacing {spacing:F1} m, centreline vertex {centre:F3}");
            Check(centre > 0.8f, $"the centreline vertex of the widest reach ({widest.Width:F0} m wide) has coverage {centre:F3}");
        }
        // Other spacings and positions outside every river influence leave the buffer alone.
        {
            var untouched = new float[16];
            Array.Fill(untouched, 7f);
            Check(!_sampler.TrySplatRiverLine(0, 0, 128, 4, 4, untouched) && untouched[0] == 7f, "TrySplatRiverLine ran for a spacing of 128 m");
            Check(!_sampler.TrySplatRiverLine((long)(StripStart + 500_000d) / 512 * 512, 0, 512, 4, 4, untouched) && untouched[0] == 7f, "TrySplatRiverLine ran outside every river influence");
            Check(RiverFootprint.UsesProxyLine(256) && !RiverFootprint.UsesProxyLine(128), "UsesProxyLine is not spacing >= 256");
        }
    }

    private void FarOwner()
    {
        int rivers = 0, others = 0;
        foreach ((double a, double c) in _channel)
        {
            if (rivers >= 20) break;
            TerrainSample s = _sampler.GetTerrainSample((long)Math.Round(a), Math.Clamp((int)Math.Round(c), 0, (int)EmitterSiteTestWorlds.Width));
            TerrainSample f = _sampler.WithoutFarRiverOwner(s);
            if (s.WaterKind == SurfaceWaterKind.River)
            {
                rivers++;
                Check(f.WaterKind == SurfaceWaterKind.None && f.WaterSurfaceHeight == f.SurfaceHeight && f.SurfaceHeight == s.SurfaceHeight && !f.HasExactWaterLevel && f.ExactWaterLevel == 0f, "a River sample kept its owner");
            }
            else { others++; Check(f.Equals(s), "a non-River sample changed"); }
        }
        var rng = new Random(5);
        for (int i = 0; i < 2000; i++)
        {
            TerrainSample s = _sampler.GetTerrainSample((long)(StripStart + rng.NextDouble() * 90_000d), rng.Next(0, (int)EmitterSiteTestWorlds.Width));
            if (s.WaterKind == SurfaceWaterKind.River) continue;
            others++;
            Check(_sampler.WithoutFarRiverOwner(s).Equals(s), "a Lake, Ocean or dry sample changed");
        }
        Detail($"WithoutFarRiverOwner: {rivers} River samples dropped their owner, {others} others unchanged");
        Check(rivers > 0, "no River sample found on the channel walk, so the owner drop was not exercised");
    }

    private void GenerationOne()
    {
        using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1));
        using var gen1 = new RingTerrainSampler(settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
        Check(!gen1.HasRiverLine, "a generation-1 sampler reports a river line");
        var gen1Values = new float[16];
        Array.Fill(gen1Values, 7f);
        foreach (int spacing in new[] { 256, 512, 8192 })
            Check(!gen1.TrySplatRiverLine((long)StripStart / spacing * spacing, 0, spacing, 4, 4, gen1Values) && gen1Values[0] == 7f, $"a generation-1 sampler splatted a river line at {spacing} m");
        int gen1Changed = 0;
        var gen1Rng = new Random(9);
        for (int i = 0; i < 2000; i++)
        {
            TerrainSample s = gen1.GetTerrainSample((long)(StripStart + gen1Rng.NextDouble() * 90_000d), gen1Rng.Next(0, (int)EmitterSiteTestWorlds.Width));
            if (!gen1.WithoutFarRiverOwner(s).Equals(s)) gen1Changed++;
        }
        Check(gen1Changed == 0, $"WithoutFarRiverOwner changed {gen1Changed} generation-1 samples");
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
