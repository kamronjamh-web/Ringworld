using System;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Vegetation;

/// <summary>
/// T6 design B5 (vegetation.far_canopy, world feature bit 8): the forest texture weight becomes the canopy cover, so the ground under and beyond the trees matches where trees are.
///   cover   = SmoothStep(0.05, 0.55, ForestRegionSampler.Sample(around, across, sample, slope 0).Density) * ForestStructureSampler.TreelineDensity(T)
///   forest' = max(0, 1 - (desert + tundra + alpine)) * cover; desert, tundra and alpine unchanged, grass is the remainder.
/// The GLSL twin is rc_forest_canopy_fold in materials/terrain_gpu/ring_regional_climate.glslinc (same expressions in the same order). The sampler owns one fold per world with the bit
/// (<see cref="RingTerrainSampler.CanopyFold"/>); every other world has none and keeps its weights exactly.
/// </summary>
public sealed class RingForestCanopyFold
{
    public const string FeatureId = "vegetation.far_canopy";
    public const int Bit = 8;

    private readonly ForestRegionSampler _forest;

    public RingForestCanopyFold(int seed, long circumferenceMeters, int widthMeters)
        => _forest = new ForestRegionSampler(seed, circumferenceMeters, widthMeters);

    /// <summary>The canopy cover in [0, 1] at a canonical coordinate; <paramref name="sample"/> supplies biome, temperature, moisture and water (slope is 0: the weight overloads have none).</summary>
    public float Cover(long aroundMeters, int acrossMeters, in TerrainSample sample)
    {
        float density = _forest.Sample(aroundMeters, acrossMeters, sample, 0f).Density;
        return SmoothStep(0.05f, 0.55f, density) * ForestStructureSampler.TreelineDensity(sample.Temperature);
    }

    /// <summary>The natural weights with the forest channel replaced by veg * cover.</summary>
    public RingTerrainSampler.BiomeTextureWeights Apply(in RingTerrainSampler.BiomeTextureWeights weights, long aroundMeters, int acrossMeters, in TerrainSample sample)
    {
        float veg = MathF.Max(0f, 1f - (weights.Desert + weights.Tundra + weights.Alpine));
        float forest = veg * Cover(aroundMeters, acrossMeters, sample);
        return new RingTerrainSampler.BiomeTextureWeights(weights.Desert, forest, weights.Tundra, weights.Alpine);
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
