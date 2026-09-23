using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo;

/// <summary>Headless regression coverage for the pure regional forest decision layer.</summary>
public partial class ForestRegionRegressionTest : RefCounted
{
    public bool Run()
    {
        try
        {
            const long circumference = 471_000_000_000L;
            const int width = 120_000;
            ForestRegionSampler first = new(9173, circumference, width);
            ForestRegionSampler same = new(9173, circumference, width);
            ForestRegionSampler changed = new(9174, circumference, width);
            TerrainSample temperate = Terrain(TerrainBiomeId.Forest, .62f, .68f);

            ForestRegionSample referenceSample = first.Sample(19_345_678_901L, 42_000, temperate, 7f);
            Require(referenceSample.Equals(same.Sample(19_345_678_901L, 42_000, temperate, 7f)), "same seed is not deterministic");
            Require(referenceSample.Equals(first.Sample(19_345_678_901L, 42_000, temperate, 7f)), "sampling order changed a result");
            bool seedChanged = false;
            for (long x = 0; x < 2_000_000; x += 31_337)
                seedChanged |= !first.Sample(x, 42_000, temperate).Equals(changed.Sample(x, 42_000, temperate));
            Require(seedChanged, "seed does not affect forest regions");

            ForestRegionSample seamLeft = first.Sample(-1, 42_000, temperate);
            ForestRegionSample seamWrapped = first.Sample(circumference - 1, 42_000, temperate);
            Require(seamLeft.Equals(seamWrapped), "negative coordinate does not wrap at seam");
            Require(first.Sample(0, 42_000, temperate).Equals(first.Sample(circumference, 42_000, temperate)), "ring seam is not exactly periodic");
            CheckContinuousSeam(first, circumference, temperate);
            CheckContinuousSeam(new ForestRegionSampler(91, 31_415_936L, width), 31_415_936L, temperate);
            CheckContinuousSeam(new ForestRegionSampler(92, 471_000_000_000L, width), 471_000_000_000L, temperate);
            Require(first.Sample(long.MaxValue - 31, 42_000, temperate).Density is >= 0f and <= 1f, "huge coordinate invalid");

            Require(None(first.Sample(0, -1, temperate)), "negative across coordinate accepted");
            Require(None(first.Sample(0, width, temperate)), "across upper bound accepted");
            Require(None(first.Sample(0, 42_000, temperate, 32f)), "steep slope accepted");
            Require(None(first.Sample(0, 42_000, Terrain(TerrainBiomeId.Desert, .8f, .02f))), "desert accepted");
            Require(None(first.Sample(0, 42_000, Water(TerrainBiomeId.Forest))), "surface water accepted");

            int open = 0, dense = 0, clearing = 0, present = 0, firDominant = 0, pineDominant = 0;
            double maple = 0, fir = 0, pine = 0;
            for (long around = 0; around < 40_000_000; around += 10_003)
            {
                ForestRegionSample sample = first.Sample(around, 42_000, temperate, 5f);
                CheckRange(sample);
                switch (sample.Habitat)
                {
                    case ForestHabitat.OpenWoodland: open++; break;
                    case ForestHabitat.DenseForest: dense++; break;
                    case ForestHabitat.Clearing: clearing++; break;
                }
                if (sample.Density > 0f)
                {
                    present++;
                    Require(MathF.Abs(sample.MapleWeight + sample.FirWeight + sample.PineWeight - 1f) < .0002f,
                        "species weights do not normalize");
                    maple += sample.MapleWeight; fir += sample.FirWeight; pine += sample.PineWeight;
                    if (sample.FirWeight > sample.MapleWeight && sample.FirWeight > sample.PineWeight) firDominant++;
                    if (sample.PineWeight > sample.MapleWeight && sample.PineWeight > sample.FirWeight) pineDominant++;
                }
            }
            GD.Print($"FOREST_REGION_COVERAGE open={open} dense={dense} clearing={clearing} present={present} fir_dominant={firDominant} pine_dominant={pineDominant}");
            Require(open > 20 && dense > 20 && clearing > 20,
                $"regional scan lacks open woodland, dense forest, or clearings (open={open}, dense={dense}, clearing={clearing})");
            Require(present > 100, "regional scan has too little tree coverage");
            Require(maple > fir && maple > pine, "maple is not dominant in temperate mixed forest");
            Require(fir > 1 && pine > 1, "fir or pine lacks regional pockets");
            Require(firDominant > 5 && pineDominant > 5, "conifers never form locally dominant pockets");

            // Warm JIT/caches before checking that the hot sampling path allocates nothing.
            for (int i = 0; i < 256; i++) _ = first.Sample(i * 7919L, 42_000, temperate);
            Stopwatch stopwatch = Stopwatch.StartNew();
            long before = GC.GetAllocatedBytesForCurrentThread();
            double checksum = 0d;
            for (int i = 0; i < 10_000; i++) checksum += first.Sample(i * 7919L, 42_000, temperate).Density;
            stopwatch.Stop();
            Require(GC.GetAllocatedBytesForCurrentThread() == before, "sampling allocates after warmup");
            Require(double.IsFinite(checksum) && checksum > 0d, "benchmark checksum is invalid");
            GD.Print($"FOREST_REGION_SAMPLER measured_10000_ms={stopwatch.Elapsed.TotalMilliseconds:F3} checksum={checksum:F5}");

            ForestRegionSample[] parallel = new ForestRegionSample[256];
            Parallel.For(0, parallel.Length, i => parallel[i] = first.Sample(i * 10_003L, 42_000, temperate));
            for (int i = 0; i < parallel.Length; i++)
                Require(parallel[i].Equals(first.Sample(i * 10_003L, 42_000, temperate)), "parallel sampling changed a result");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("FOREST_REGION_REGRESSION=FAIL " + exception.Message);
            return false;
        }
    }

    private static TerrainSample Terrain(TerrainBiomeId biome, float temperature, float moisture) =>
        new(120, TerrainRegion.Lowlands, .3f, .2f, .1f, temperature, moisture, biome, SurfaceWaterKind.None, 120);
    private static TerrainSample Water(TerrainBiomeId biome) =>
        new(120, TerrainRegion.Lowlands, .3f, .2f, .1f, .6f, .7f, biome, SurfaceWaterKind.Lake, 122);
    private static bool None(ForestRegionSample sample) => sample.Habitat == ForestHabitat.None && sample.Density == 0f;
    private static void CheckContinuousSeam(ForestRegionSampler sampler, long circumference, TerrainSample terrain)
    {
        ForestRegionSample left = sampler.Sample(circumference - 1, 42_000, terrain);
        ForestRegionSample edge = sampler.Sample(0, 42_000, terrain);
        ForestRegionSample right = sampler.Sample(1, 42_000, terrain);
        Require(MathF.Abs(left.Density - edge.Density) < .01f && MathF.Abs(right.Density - edge.Density) < .01f,
            "density has a discontinuity at the ring seam");
        Require(MathF.Abs(left.MapleWeight - edge.MapleWeight) < .01f && MathF.Abs(right.FirWeight - edge.FirWeight) < .01f,
            "species has a discontinuity at the ring seam");
    }
    private static void CheckRange(ForestRegionSample sample)
    {
        Require(sample.Density is >= 0f and <= 1f && sample.Suitability is >= 0f and <= 1f && sample.Clearing is >= 0f and <= 1f,
            "sample field outside unit range");
        Require(sample.MapleWeight is >= 0f and <= 1f && sample.FirWeight is >= 0f and <= 1f && sample.PineWeight is >= 0f and <= 1f,
            "species field outside unit range");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
