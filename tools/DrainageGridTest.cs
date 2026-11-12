using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T2R-3 (docs/work-orders/terrain-vegetation/T2.md): the valley-floor grid, CPU source. On a 400 km strip at 9,804,000 m and a 400 km strip across the seam:
/// the cell value is at most each of its 16 direct samples (and equals the lowest of them); positions wrap; a cell with a negative sample is sea;
/// two runs and 1 against N workers give the same hash. Negative control: a grid whose value is the mean must fail the "at most each sample" check.
/// Prints DRAINAGE_GRID=PASS|FAIL. Headless: run through tools/run_tests.ps1 -Tests drainage_grid_test.
/// </summary>
public partial class DrainageGridTest : RefCounted
{
    private const int Seed = 1337;
    private const int StripKilometres = 400;
    private const int CheckedCells = 1500;
    private readonly List<string> _failures = new();

    /// <summary>The seed-1337 generation-1 world recipe shared with the GPU test.</summary>
    internal static TerrainGenerationParameters Recipe() => EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1);

    internal static RingTerrainSampler CreateSampler(TerrainGenerationParameters recipe)
    {
        using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, recipe);
        return new RingTerrainSampler(settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
    }

    internal static (string Label, DrainageGridSpec Spec)[] Strips(TerrainGenerationParameters recipe) => new[]
    {
        ("strip_9804km", DrainageGridSpec.Strip(recipe, 9_804_000d, StripKilometres)),
        ("strip_seam", DrainageGridSpec.Strip(recipe, recipe.CircumferenceM - 200_000d, StripKilometres)),
    };

    private void Fail(string message) { if (_failures.Count < 24) _failures.Add(message); }

    public int Run()
    {
        try
        {
            TerrainGenerationParameters recipe = Recipe();
            using RingTerrainSampler root = CreateSampler(recipe);
            DrainageClimateInputs climate = DrainageClimateInputs.FromSampler(root);
            int workers = Math.Clamp(System.Environment.ProcessorCount, 2, 8);
            CheckClimateReader(recipe, root, workers);
            foreach ((string label, DrainageGridSpec spec) in Strips(recipe))
            {
                CheckPositions(label, spec);
                var many = new CpuDrainageHeightSource(recipe, climate, workers);
                var one = new CpuDrainageHeightSource(recipe, climate, 1);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                DrainageGrid grid = many.SampleGrid(spec, null, CancellationToken.None);
                double seconds = clock.Elapsed.TotalSeconds;
                DrainageGrid again = many.SampleGrid(spec, null, CancellationToken.None);
                DrainageGrid single = one.SampleGrid(spec, null, CancellationToken.None);
                ulong hash = grid.Hash();
                if (again.Hash() != hash) Fail($"{label}: two runs gave different hashes");
                if (single.Hash() != hash) Fail($"{label}: 1 worker and {workers} workers gave different hashes");
                int sea = 0;
                for (int i = 0; i < grid.Count; i++) if (grid.IsSea(i)) sea++;
                CheckCells(label, recipe, grid, mean: false);
                int meanFailures = CheckCells(label + "_negative_control", recipe, grid, mean: true, report: false);
                if (meanFailures == 0) Fail($"{label}: negative control did not fail (a mean-valued grid passed the at-most-each-sample check)");
                Console.WriteLine($"DRAINAGE_GRID_DETAIL {label}: columns={spec.Columns} rows={spec.Rows} cells={grid.Count} sea={sea} workers={workers} seconds={seconds:F2} hash=0x{hash:X16} negative_control_failures={meanFailures}");
            }
        }
        catch (Exception e) { Console.Error.WriteLine("DRAINAGE_GRID=FAIL exception " + e); return 1; }
        if (_failures.Count != 0) { Console.Error.WriteLine("DRAINAGE_GRID=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("DRAINAGE_GRID=PASS");
        return 0;
    }

    /// <summary>
    /// T2R-9 (S12): the climate-only reader built without a sampler (legacy mode) is bit-equal to a runtime sampler's Moisture on 1,000 points. Negative control: a reader built from another seed is not.
    /// </summary>
    private void CheckClimateReader(TerrainGenerationParameters recipe, RingTerrainSampler root, int workers)
    {
        var points = new (long Around, int Across)[1000];
        var rng = new Random(20261112);
        for (int i = 0; i < points.Length; i++) points[i] = ((long)(rng.NextDouble() * EmitterSiteTestWorlds.Circumference), rng.Next((int)EmitterSiteTestWorlds.Width));
        int Mismatches(int seed)
        {
            using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, recipe);
            settings.Seed = seed;
            using IDrainageMoistureReader reader = HydrologyBakeInputs.Capture(settings, recipe, workers).Climate.CreateReader();
            int bad = 0;
            foreach ((long around, int across) in points)
                if (BitConverter.SingleToInt32Bits(reader.Moisture(around, across)) != BitConverter.SingleToInt32Bits(root.GetTerrainSample(around, across).Moisture)) bad++;
            return bad;
        }
        int mismatches = Mismatches(Seed), control = Mismatches(Seed + 1);
        if (mismatches != 0) Fail($"climate reader: {mismatches} of 1000 points differ from a runtime sampler's Moisture");
        if (control == 0) Fail("climate reader: negative control did not fail (a reader of another seed matched)");
        Console.WriteLine($"DRAINAGE_GRID_DETAIL climate reader: mismatches={mismatches} negative_control_mismatches={control}");
    }

    private void CheckPositions(string label, DrainageGridSpec spec)
    {
        long c = spec.CircumferenceMeters;
        for (int col = 0; col < spec.Columns; col++)
        for (int k = 0; k < DrainageGridSpec.Stride; k++)
        {
            long a = spec.AroundSamples[col * DrainageGridSpec.Stride + k];
            if (a < 0 || a >= c) { Fail($"{label}: column {col} sample {k} at {a} is outside [0, {c})"); return; }
        }
        for (int col = 1; col < spec.Columns; col++)
        {
            long previous = spec.AroundSamples[(col - 1) * DrainageGridSpec.Stride], next = spec.AroundSamples[col * DrainageGridSpec.Stride];
            long delta = ((next - previous) % c + c) % c;
            if (Math.Abs(delta - spec.AroundStepMeters) > 1.01) { Fail($"{label}: columns {col - 1} and {col} are {delta} m apart, expected {spec.AroundStepMeters:F3}"); return; }
        }
        bool wraps = false;
        for (int col = 1; col < spec.Columns; col++)
            if (spec.AroundSamples[col * DrainageGridSpec.Stride] < spec.AroundSamples[(col - 1) * DrainageGridSpec.Stride]) wraps = true;
        if (label.Contains("seam") && !wraps) Fail($"{label}: the seam strip does not wrap");
        if (!label.Contains("seam") && wraps) Fail($"{label}: the plain strip wraps");
    }

    /// <summary>Recomputes the 16 samples of a deterministic subset of cells with a fresh model and compares the grid value with them.</summary>
    private int CheckCells(string label, TerrainGenerationParameters recipe, DrainageGrid grid, bool mean, bool report = true)
    {
        using var model = new TerrainGenerationHeightModel(recipe, false);
        DrainageGridSpec spec = grid.Spec;
        int failures = 0;
        var rng = new Random(20261111);
        for (int n = 0; n < CheckedCells; n++)
        {
            int cell = rng.Next(grid.Count);
            int row = cell / spec.Columns, col = cell % spec.Columns;
            float min = float.PositiveInfinity, sum = 0f;
            int minIndex = 0;
            bool anyNegative = false;
            var samples = new float[DrainageGridSpec.SamplesPerCell];
            for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++)
            {
                (long around, int across) = spec.Sample(col, row, i, j);
                float h = (float)model.CalculateHeight(around, across);
                samples[j * 4 + i] = h;
                sum += h;
                anyNegative |= h < 0f;
                if (h < min) { min = h; minIndex = j * 4 + i; }
            }
            float value = mean ? sum / DrainageGridSpec.SamplesPerCell : grid.Lowest[cell];
            bool atMost = true;
            foreach (float s in samples) if (value > s) { atMost = false; break; }
            if (!atMost) { failures++; if (report) Fail($"{label}: cell {cell} (col {col}, row {row}) value {value} is above a direct sample"); }
            if (mean) continue;
            if (grid.Lowest[cell] != min || grid.LowestIndex[cell] != minIndex)
            { failures++; Fail($"{label}: cell {cell} lowest {grid.Lowest[cell]} index {grid.LowestIndex[cell]}, recomputed {min} index {minIndex}"); }
            if (grid.IsSea(cell) != anyNegative) { failures++; Fail($"{label}: cell {cell} sea flag {grid.IsSea(cell)} but a negative sample is {anyNegative}"); }
        }
        return failures;
    }
}
