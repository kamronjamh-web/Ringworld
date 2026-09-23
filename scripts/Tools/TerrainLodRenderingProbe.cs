using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>Bounded dry-grass fixture selection for the terrain LOD render probe.</summary>
public partial class TerrainLodRenderingProbe : RefCounted
{

    public bool PlaceCamera(Node game, Godot.Collections.Dictionary fixture)
    {
        if (!fixture.TryGetValue("valid", out Variant valid) || !valid.AsBool()) return false;
        RingVoxelExplorerController? explorer = game.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        if (explorer is null) return false;
        explorer.SetBenchmarkCanonicalPosition(new RingCanonicalPosition(
            fixture["around"].AsDouble(), fixture["height"].AsDouble() + 120d,
            fixture["across"].AsDouble()), 0f, true);
        if (!explorer.ResetCanonicalOrientationForCapture()) return false;
        explorer.SetPresentationView(0f, -30f);
        return true;
    }

    public Godot.Collections.Dictionary FindDryGentleGrassland(Node game)
    {
        var result = new Godot.Collections.Dictionary { ["valid"] = false };
        VoxelChunkGridManager? grid = game.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager");
        RingTerrainSampler? sampler = grid?.TerrainSampler;
        if (grid is null || sampler is null) return result;

        const int aroundSamples = 20, acrossSamples = 20;
        double circumference = grid.GetCanonicalRingCircumferenceMeters64();
        int width = Mathf.RoundToInt(grid.RingAcrossWidthMeters);
        float bestScore = float.NegativeInfinity;
        for (int x = 0; x < aroundSamples; x++)
        for (int z = 1; z < acrossSamples; z++)
        {
            long around = (long)((x + .5d) * circumference / aroundSamples);
            int across = Mathf.RoundToInt(z * width / (float)acrossSamples);
            TerrainSample sample = sampler.GetTerrainSample(around, across);
            if (sample.HasSurfaceWater) continue;
            RingTerrainSampler.BiomeTextureWeights weights = sampler.GetBiomeTextureWeights(sample);
            float grass = 1f - weights.Desert - weights.Forest - weights.Tundra - weights.Alpine;
            // A central difference is sufficient for fixture selection, not a
            // terrain-normal oracle (the GPU mesh is independently verified).
            const int step = 32;
            float dx = sampler.GetTerrainSample(around + step, across).SurfaceHeight
                - sampler.GetTerrainSample(around - step, across).SurfaceHeight;
            float dz = sampler.GetTerrainSample(around, across + step).SurfaceHeight
                - sampler.GetTerrainSample(around, across - step).SurfaceHeight;
            float slope = Mathf.Sqrt(dx * dx + dz * dz) / (2f * step);
            float score = grass * 4f - slope;
            if (grass < .72f || slope > .24f || score <= bestScore) continue;
            bestScore = score;
            result["valid"] = true;
            result["around"] = around;
            result["across"] = across;
            result["height"] = sample.SurfaceHeight;
            result["grass_weight"] = grass;
            result["slope"] = slope;
        }
        return result;
    }
}
