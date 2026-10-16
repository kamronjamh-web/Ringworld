using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Spike B1 density benchmark: ms per 1 m regular chunk (37^3 lattice) and per medium / coarse 37^3 lattice, gen-16 vs gen-17 (uncached, cached cold, cached streaming),
/// on flat land, mountains, coast and an emitter pad. Single thread, JIT warmed, no regular-chunk cache hits (CaptureRegularChunk is invoked directly).
/// </summary>
public partial class SmoothRuntimeSpikeBenchmark : RefCounted
{
    const long Circumference = EmitterSiteTestWorlds.Circumference;
    const int Width = EmitterSiteTestWorlds.Width;
    const int Chunks = 12;
    const int Passes = 4;

    static RingTerrainSmoothBaseline NewBaseline(RingTerrainSampler s, bool cache)
        => new(s, structuralBottomVertical: -9992, terrainBottomVertical: -8192, protectedRoofThickness: 512, lowerHullTopVertical: -9192, structuralWallTopVertical: 10400, useRuntimeLatticeCache: cache);

    sealed record Scenario(string Name, long Around, int Across);

    public int Run()
    {
        using RingTerrainSampler g16 = EmitterSiteTestWorlds.CreateGen16(1337);
        using RingTerrainSampler g17 = EmitterSiteTestWorlds.CreateGen17Sites(1337);
        Console.WriteLine($"SMOOTH_BENCH logical_cpus={System.Environment.ProcessorCount} headless={DisplayServer.GetName() == "headless"} gen16_hash={g16.TerrainDefinitionHash:X16} gen17_hash={g17.TerrainDefinitionHash:X16}");
        var main = g17.EmitterSites!.MainSite;
        var rows = new List<string>();
        foreach (string category in new[] { "flat_land", "mountains", "coast", "emitter_pad" })
        {
            Scenario s16 = category == "emitter_pad" ? new Scenario(category, main.Around, main.Across) : Find(g16, category);
            Scenario s17 = category == "emitter_pad" ? new Scenario(category, main.Around, main.Across) : Find(g17, category);
            rows.Add(Chunk(category, g16, s16, g17, s17));
        }
        Console.WriteLine("SMOOTH_BENCH_TABLE chunk: ms per 37^3 regular chunk capture, median over " + Passes + " passes of the mean over " + Chunks + " consecutive chunks");
        foreach (string row in rows) Console.WriteLine(row);
        Scenario land17 = Find(g17, "flat_land"), land16 = Find(g16, "flat_land");
        foreach (int spacing in new[] { 2, 4, 8, 16, 32 }) Console.WriteLine(Lattice(spacing, g16, land16, g17, land17));
        Console.WriteLine("SMOOTH_RUNTIME_SPIKE_BENCH=DONE");
        return 0;
    }

    static float H(RingTerrainSampler s, long a, int x) => s.GetPresentationSurfaceHeight(RingCoordMath.WrapAround(a, Circumference), Math.Clamp(x, 0, Width));

    static Scenario Find(RingTerrainSampler s, string category)
    {
        var rng = new Random(2024 + category.Length);
        for (int i = 0; i < 400000; i++)
        {
            long a = ((long)(rng.NextDouble() * (Circumference / 2 - 100)) * 2L) + 20;
            int x = (int)(rng.NextDouble() * (Width / 2 - 200)) * 2 + 400;
            float h = H(s, a, x);
            float slope = Math.Max(Math.Abs(H(s, a + 4, x) - h), Math.Abs(H(s, a, x + 4) - h));
            bool ok = category switch
            {
                "flat_land" => h > 20 && h < 80 && slope < 0.4f && Math.Abs(H(s, a + 400, x) - h) < 40,
                "mountains" => h > 1200 && slope > 6f && slope < 40f,
                "coast" => false,
                _ => false,
            };
            if (category == "coast" && h > 0.5f && h < 30f)
                for (int d = 8; d <= 4000; d += 8)
                    if (H(s, a + d, x) <= 0.5f) return new Scenario(category, a + d / 2 * 2, x);
            if (ok) return new Scenario(category, a, x);
        }
        throw new InvalidOperationException("no " + category + " found");
    }

    static string Chunk(string name, RingTerrainSampler g16, Scenario s16, RingTerrainSampler g17, Scenario s17)
    {
        double t16 = Time(g16, s16, false, false, out int m16, out _);
        double t17 = Time(g17, s17, false, false, out int m17, out _);
        double t17c = Time(g17, s17, true, true, out int mc, out long evCold);
        double t17s = Time(g17, s17, true, false, out _, out long evStream);
        return $"SMOOTH_BENCH chunk {name,-12} gen16={t16,7:F2} | gen17 uncached={t17,7:F2} (mixed {m17}/{Chunks}) | gen17 cached cold={t17c,7:F2} (mixed {mc}, {evCold / (double)Chunks:F0} evals/chunk) | gen17 cached streaming={t17s,7:F2} ({evStream / (double)Chunks:F0} evals/chunk) | gen16 mixed {m16}/{Chunks} | ratios vs gen16: uncached {t17 / t16:F2}x cold {t17c / t16:F2}x streaming {t17s / t16:F2}x | gen16 at ({s16.Around},{s16.Across}) gen17 at ({s17.Around},{s17.Across})";
    }

    static double Time(RingTerrainSampler sampler, Scenario sc, bool cache, bool resetEach, out int mixed, out long evals)
    {
        RingTerrainSmoothBaseline baseline = NewBaseline(sampler.CreateIndependentCopy(), cache);
        var field = new SmoothTerrainVolumetricDensitySnapshot(baseline, new SmoothTerrainEditSnapshot(new List<SmoothTerrainEditCommand>(), 0, baseline.AroundLength));
        var coords = new List<SmoothTerrainChunkCoordinate>();
        for (int i = 0; i < Chunks; i++)
        {
            long a = RingCoordMath.WrapAround(sc.Around / 32 * 32 + i * 32 + 16, Circumference);
            int vertical = (int)Math.Floor(H(sampler, a, sc.Across / 32 * 32 + 16) / 32d);
            coords.Add(new SmoothTerrainChunkCoordinate(a / 32, vertical, sc.Across / 32));
        }
        SmoothTerrainVolumetricDensityCacheKey Key(SmoothTerrainChunkCoordinate c) => new(field.Identity, c.Origin.AroundVoxel - 2, c.Origin.VerticalVoxel - 2, c.Origin.AcrossVoxel - 2, 36, 1);
        mixed = 0;
        foreach (var c in coords.Take(2)) field.CaptureRegularChunk(Key(c), c); // JIT warm-up
        var means = new List<double>();
        evals = 0;
        for (int pass = 0; pass < Passes; pass++)
        {
            baseline.Sampler.ResetLatticeCache();
            double total = 0; mixed = 0; long passEvals = 0;
            foreach (var c in coords)
            {
                if (resetEach) baseline.Sampler.ResetLatticeCache();
                long start = Stopwatch.GetTimestamp();
                var capture = field.CaptureRegularChunk(Key(c), c);
                total += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (capture.Range.Classification == SmoothTerrainDensityClassification.Mixed) mixed++;
                if (resetEach) passEvals += baseline.Sampler.LatticeVertexEvaluations;
            }
            if (!resetEach) passEvals = baseline.Sampler.LatticeVertexEvaluations;
            evals = passEvals;
            means.Add(total / coords.Count);
        }
        means.Sort();
        baseline.DisposeIndependentWorkerCopy();
        return means[means.Count / 2];
    }

    static string Lattice(int spacing, RingTerrainSampler g16, Scenario s16, RingTerrainSampler g17, Scenario s17)
    {
        double a = TimeLattice(g16, s16, spacing, false), b = TimeLattice(g17, s17, spacing, false), c = TimeLattice(g17, s17, spacing, true);
        return $"SMOOTH_BENCH lattice spacing={spacing,2} m flat_land 37^3: gen16={a,7:F2} ms gen17 uncached={b,7:F2} ms gen17 cached(cold)={c,7:F2} ms cached/gen16={c / a:F2}x";
    }

    static double TimeLattice(RingTerrainSampler sampler, Scenario sc, int spacing, bool cache)
    {
        RingTerrainSmoothBaseline baseline = NewBaseline(sampler.CreateIndependentCopy(), cache);
        var field = new SmoothTerrainVolumetricDensitySnapshot(baseline, new SmoothTerrainEditSnapshot(new List<SmoothTerrainEditCommand>(), 0, baseline.AroundLength));
        var densities = new float[37 * 37 * 37]; var materials = new byte[densities.Length];
        var samples = new List<double>();
        for (int i = 0; i < 12; i++)
        {
            long ao = (sc.Around / spacing + i * 36) * spacing;
            int vo = (int)(Math.Floor(H(sampler, ao + 18 * spacing, sc.Across) / spacing) * spacing - 18 * spacing);
            var k = new SmoothTerrainVolumetricDensityCacheKey(field.Identity, ao, vo, sc.Across / spacing * spacing, 36, spacing);
            baseline.Sampler.ResetLatticeCache();
            long start = Stopwatch.GetTimestamp();
            field.CaptureNativeLattice(k, densities, materials);
            if (i >= 2) samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        baseline.DisposeIndependentWorkerCopy();
        samples.Sort();
        return samples[samples.Count / 2];
    }
}
