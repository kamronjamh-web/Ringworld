using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T2R-3 (docs/work-orders/terrain-vegetation/T2.md): GPU drainage height source against the CPU source. U2 first: a local RenderingDevice is created,
/// used and freed on a worker thread. Then, on a 400 km strip at 9,804,000 m and a 400 km strip across the seam: lowest height differs by at most 0.05 m
/// on 99.9 % of cells and 0.5 m anywhere; the index of the lowest sample agrees on at least 99 %; moisture within 0.01; two GPU runs are bit-equal.
/// Point mode is compared on 20,000 random points. It then times a whole-ring GPU grid and prints the seconds.
/// Run (windowed Vulkan, not --headless): tools/run_tests.ps1 -Tests drainage_height_source_gpu_test. Prints DRAINAGE_HEIGHT_SOURCE_GPU=PASS|FAIL.
/// </summary>
public partial class DrainageHeightSourceGpuTest : RefCounted
{
    private const double HeightFraction = 0.999, HeightTypical = 0.05, HeightHard = 0.5, IndexFraction = 0.99, MoistureTolerance = 0.01;
    private readonly List<string> _failures = new();

    private void Fail(string message) { if (_failures.Count < 24) _failures.Add(message); }

    public int Run()
    {
        RenderingDevice? rd = null;
        try
        {
            TerrainGenerationParameters recipe = DrainageGridTest.Recipe();
            using RingTerrainSampler root = DrainageGridTest.CreateSampler(recipe);
            DrainageClimateInputs climate = DrainageClimateInputs.FromSampler(root);
            var strips = DrainageGridTest.Strips(recipe);

            // U2: a local device on a worker thread.
            string u2 = "fails";
            ulong workerHash = 0;
            DrainageGridSpec small = DrainageGridSpec.Strip(recipe, 9_804_000d, 24);
            Exception? workerError = null;
            var thread = new Thread(() =>
            {
                RenderingDevice? local = null;
                try
                {
                    local = RenderingServer.CreateLocalRenderingDevice() ?? throw new InvalidOperationException("no local RenderingDevice on the worker thread");
                    using var source = new GpuDrainageHeightSource(local, recipe, climate);
                    workerHash = source.SampleGrid(small, null, CancellationToken.None).Hash();
                }
                catch (Exception e) { workerError = e; }
                finally { local?.Free(); }
            });
            thread.Start();
            thread.Join();
            if (workerError == null) u2 = "works";
            Console.WriteLine($"DRAINAGE_HEIGHT_SOURCE_GPU_U2 local RenderingDevice on a worker thread: {u2}" + (workerError != null ? " " + workerError.Message : ""));
            if (workerError != null) { Fail("U2: " + workerError.Message); return Finish(); }

            rd = RenderingServer.CreateLocalRenderingDevice();
            if (rd == null) { Fail("no local RenderingDevice"); return Finish(); }
            Console.WriteLine($"DRAINAGE_HEIGHT_SOURCE_GPU_DETAIL device: {rd.GetDeviceName()} ({rd.GetDeviceVendorName()})");
            using var gpu = new GpuDrainageHeightSource(rd, recipe, climate);
            int workers = Math.Clamp(System.Environment.ProcessorCount, 2, 8);
            var cpu = new CpuDrainageHeightSource(recipe, climate, workers);
            if (gpu.SampleGrid(small, null, CancellationToken.None).Hash() != workerHash) Fail("the worker-thread device and the main device gave different small-strip hashes");

            foreach ((string label, DrainageGridSpec spec) in strips)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                DrainageGrid g1 = gpu.SampleGrid(spec, null, CancellationToken.None);
                double gpuSeconds = clock.Elapsed.TotalSeconds;
                DrainageGrid g2 = gpu.SampleGrid(spec, null, CancellationToken.None);
                if (g1.Hash() != g2.Hash()) Fail($"{label}: two GPU runs are not bit-equal");
                clock.Restart();
                DrainageGrid c = cpu.SampleGrid(spec, null, CancellationToken.None);
                double cpuSeconds = clock.Elapsed.TotalSeconds;
                Compare(label, g1, c, gpuSeconds, cpuSeconds);
            }
            ComparePoints(recipe, gpu, cpu);
            TimeWholeRing(recipe, gpu);
        }
        catch (Exception e) { Console.Error.WriteLine("DRAINAGE_HEIGHT_SOURCE_GPU=FAIL exception " + e); return 1; }
        finally { rd?.Free(); }
        return Finish();
    }

    private int Finish()
    {
        if (_failures.Count != 0) { Console.Error.WriteLine("DRAINAGE_HEIGHT_SOURCE_GPU=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("DRAINAGE_HEIGHT_SOURCE_GPU=PASS");
        return 0;
    }

    private void Compare(string label, DrainageGrid gpu, DrainageGrid cpu, double gpuSeconds, double cpuSeconds)
    {
        int n = gpu.Count, within = 0, indexSame = 0, seaDiffers = 0;
        double maxDiff = 0, sumDiff = 0, maxMoisture = 0;
        var diffs = new double[n];
        int worst = 0;
        for (int i = 0; i < n; i++)
        {
            double d = Math.Abs(gpu.Lowest[i] - cpu.Lowest[i]);
            diffs[i] = d;
            if (d <= HeightTypical) within++;
            if (d > maxDiff) { maxDiff = d; worst = i; }
            sumDiff += d;
            if (gpu.LowestIndex[i] == cpu.LowestIndex[i]) indexSame++;
            if (gpu.IsSea(i) != cpu.IsSea(i)) seaDiffers++;
            maxMoisture = Math.Max(maxMoisture, Math.Abs(gpu.Moisture[i] - cpu.Moisture[i]));
        }
        Array.Sort(diffs);
        double p999 = diffs[(int)Math.Min(n - 1, Math.Floor(n * 0.999))];
        double fractionWithin = (double)within / n, fractionIndex = (double)indexSame / n;
        Console.WriteLine($"DRAINAGE_HEIGHT_SOURCE_GPU_DETAIL {label}: cells={n} lowest_diff mean={sumDiff / n:E2} p99.9={p999:F4} max={maxDiff:F4} (cell {worst}, gpu {gpu.Lowest[worst]:F3} cpu {cpu.Lowest[worst]:F3}) within_0.05={fractionWithin:P3} "
            + $"index_agree={fractionIndex:P3} sea_differs={seaDiffers} moisture_max_diff={maxMoisture:E2} gpu_s={gpuSeconds:F3} cpu_s={cpuSeconds:F2}");
        if (fractionWithin < HeightFraction) Fail($"{label}: only {fractionWithin:P3} of cells within {HeightTypical} m");
        if (maxDiff > HeightHard) Fail($"{label}: lowest height differs by {maxDiff:F3} m at cell {worst}");
        if (fractionIndex < IndexFraction) Fail($"{label}: lowest-sample index agrees on only {fractionIndex:P3}");
        if (maxMoisture > MoistureTolerance) Fail($"{label}: moisture differs by {maxMoisture:F4}");
    }

    private void ComparePoints(TerrainGenerationParameters recipe, GpuDrainageHeightSource gpu, CpuDrainageHeightSource cpu)
    {
        const int count = 20_000;
        var rng = new Random(20261111);
        var points = new DrainagePoint[count];
        for (int i = 0; i < count; i++) points[i] = new DrainagePoint(rng.NextInt64(0, (long)recipe.CircumferenceM), rng.Next(0, (int)recipe.WidthM - 1));
        var a = new float[count];
        var b = new float[count];
        gpu.SamplePoints(points, a);
        cpu.SamplePoints(points, b);
        int over = 0;
        double max = 0;
        for (int i = 0; i < count; i++)
        {
            double d = Math.Abs(a[i] - b[i]);
            max = Math.Max(max, d);
            if (d > HeightTypical) over++;
        }
        Console.WriteLine($"DRAINAGE_HEIGHT_SOURCE_GPU_DETAIL points: n={count} max_diff={max:F4} over_0.05={over}");
        if ((double)over / count > 1 - HeightFraction) Fail($"points: {over} of {count} differ by more than {HeightTypical} m");
        if (max > HeightHard) Fail($"points: max difference {max:F3} m");
    }

    private void TimeWholeRing(TerrainGenerationParameters recipe, GpuDrainageHeightSource gpu)
    {
        DrainageGridSpec ring = DrainageGridSpec.Ring(recipe);
        for (int run = 0; run < 2; run++)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            DrainageGrid grid = gpu.SampleGrid(ring, null, CancellationToken.None);
            double seconds = clock.Elapsed.TotalSeconds;
            int sea = 0;
            for (int i = 0; i < grid.Count; i++) if (grid.IsSea(i)) sea++;
            Console.WriteLine($"DRAINAGE_HEIGHT_SOURCE_GPU_TIMING whole ring run {run + 1}: columns={ring.Columns} rows={ring.Rows} cells={grid.Count} seconds={seconds:F3} (device {gpu.LastDeviceSeconds:F3}) sea_cells={sea} hash=0x{grid.Hash():X16}");
        }
    }
}
