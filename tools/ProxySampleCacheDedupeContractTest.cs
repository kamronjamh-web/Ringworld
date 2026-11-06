using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO-C1/C2 contract: TerrainBuildSampleCache.Get (one model + one climate evaluation per lattice point) returns every
/// CachedTerrainPoint field bit-identical to the original three-call path, across the ring seam (negative and over-length
/// around), across 0 / width-2 / width, odd and even coordinates, edited columns, float edits, and presentation on and off.
/// Run: Godot --headless --path . --script res://tools/proxy_sample_cache_dedupe_contract_test.gd
/// Prints PROXY_SAMPLE_CACHE_DEDUPE=PASS.
/// </summary>
public partial class ProxySampleCacheDedupeContractTest : RefCounted
{
    public int Run()
    {
        var failures = new List<string>();
        try
        {
            TerrainGenerationParameters recipe = TerrainGenerationParameters.Default();
            using var runtimeSettings = new VoxelTerrainSettings
            {
                SurfaceGenerationRecipeJson = recipe.CanonicalJson,
                SurfaceGenerationContractId = RingTerrainSampler.RuntimeSurfaceGenerationContractId,
                BedrockY = -8000, SeaLevelY = 0, EnableUnified3dDensityTerrain = false,
                EnableProceduralCaves = false, EnableNaturalOverhangs = false, EnableDesignedCaveDensityComposition = false
            };
            using var runtime = new RingTerrainSampler(runtimeSettings, true, CertifiedRingScale.AroundVoxelLength,
                CertifiedRingScale.EffectiveRadiusMeters, CertifiedRingScale.RingWidthMeters, 8400);
            using var legacySettings = new VoxelTerrainSettings();
            using var legacy = new RingTerrainSampler(legacySettings, true, CertifiedRingScale.AroundVoxelLength,
                CertifiedRingScale.EffectiveRadiusMeters, CertifiedRingScale.RingWidthMeters, 8000);
            // A sampler with the lattice cache on takes a different (memoised) height path; it must match too.
            using var latticeCached = new RingTerrainSampler(runtimeSettings, true, CertifiedRingScale.AroundVoxelLength,
                CertifiedRingScale.EffectiveRadiusMeters, CertifiedRingScale.RingWidthMeters, 8400);
            latticeCached.EnablePresentationLatticeCache();

            long length = runtime.AroundRingVoxelLength;
            int width = runtime.RingAcrossWidthMeters;
            var points = BuildPoints(length, width);
            // Edited neighbourhood: the edited vertex, its odd neighbours (interpolation touches it) and the seam.
            const long ea = 1_000_000;
            const int ex = 160_000;
            for (long a = ea - 5; a <= ea + 5; a++)
                for (int x = ex - 5; x <= ex + 5; x++) points.Add((a, x));
            for (long a = length - 3; a <= length + 3; a++)
                for (int x = 0; x <= 3; x++) { points.Add((a, x)); points.Add((a, width - x)); }

            var floatEdits = new Dictionary<RingTerrainColumnCoordinate, float>
            {
                [new(ea, ex)] = runtime.GetPresentationSurfaceHeight(ea, ex) + 47.25f,
                [new(ea + 2, ex + 2)] = -12.5f,
                [new(0, 0)] = 33.0f,
                [new(length - 2, width - 2)] = 9.5f,
                [new(0, width - 2)] = -3.25f,
            };
            var intEdits = new Dictionary<RingTerrainColumnCoordinate, int>
            {
                [new(ea + 4, ex)] = 77,
                [new(length - 2, 2)] = -9,
            };
            var noInt = new Dictionary<RingTerrainColumnCoordinate, int>();
            var noFloat = new Dictionary<RingTerrainColumnCoordinate, float>();

            void Case(string label, RingLodSurfaceEditSnapshot edits, RingTerrainSampler sampler)
            {
                string difference = RingTerrainProxySector.CompareSampleCacheToLegacy(edits, sampler, width, points, out int compared);
                if (difference.Length != 0) failures.Add($"{label}: {difference}");
                else GD.Print($"PROXY_SAMPLE_CACHE_DEDUPE case={label} points={compared} identical");
                difference = RingTerrainProxySector.CompareHeightOnlyToFull(edits, sampler, width, points, out compared);
                if (difference.Length != 0) failures.Add($"{label} (WO-C2): {difference}");
                else GD.Print($"PROXY_SAMPLE_CACHE_DEDUPE case={label} points={compared} height-only consistent");
            }

            Case("runtime-float-empty", new(0, length, noInt, noFloat, true), runtime);
            Case("runtime-float-edited", new(1, length, intEdits, floatEdits, true), runtime);
            Case("runtime-float-int-only", new(2, length, intEdits, noFloat, true), runtime);
            Case("runtime-nofloat-empty", new(3, length, noInt, noFloat, false), runtime);
            Case("runtime-nofloat-edited", new(4, length, intEdits, floatEdits, false), runtime);
            Case("runtime-lattice-cache", new(5, length, noInt, noFloat, true), latticeCached);
            Case("runtime-lattice-cache-edited", new(6, length, intEdits, floatEdits, true), latticeCached);
            Case("legacy-presentation-off", new(7, length, noInt, noFloat, false), legacy);
            Case("legacy-presentation-off-int", new(8, length, intEdits, noFloat, false), legacy);
            Case("legacy-float-surface", new(9, length, noInt, floatEdits, true), legacy);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.ToString());
            return 1;
        }
        if (failures.Count != 0)
        {
            Console.Error.WriteLine("PROXY_SAMPLE_CACHE_DEDUPE=FAIL " + string.Join(" | ", failures));
            return 1;
        }
        Console.WriteLine("PROXY_SAMPLE_CACHE_DEDUPE=PASS");
        return 0;
    }

    private static List<(long Around, int Across)> BuildPoints(long length, int width)
    {
        var points = new List<(long, int)>();
        long[] arounds =
        {
            -8, -7, -3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 1001, 1002, 123_457, 123_458, 9_999_999, 10_000_000,
            length / 2, length / 2 + 1, length - 6, length - 3, length - 2, length - 1, length, length + 1, length + 2,
            length + 3, length + 100, 2 * length - 2, 2 * length + 1,
        };
        int[] acrosses =
        {
            -9, -1, 0, 1, 2, 3, 4, 5, 1000, 1001, 160_001, 160_002, width / 2, width / 2 + 1, width - 6, width - 5, width - 4,
            width - 3, width - 2, width - 1, width, width + 1, width + 7,
        };
        foreach (long a in arounds)
            foreach (int x in acrosses) points.Add((a, x));
        var random = new Random(20261105);
        for (int i = 0; i < 400; i++)
            points.Add((random.NextInt64(-length, 2 * length), random.Next(-4, width + 5)));
        return points;
    }
}
