using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Biomes;

/// <summary>
/// Preview-only sampler. Authored noise supplies relief, while the existing
/// terrain sampler supplies a seeded contribution to moisture.
/// It never feeds world streaming, saves, or TerrainBiomeId selection.
/// </summary>
public sealed class BiomeAuthoringSampler : IDisposable
{
    const long PreviewCircumference = 10_000_000;
    const int PreviewWidth = 1_000_000;
    readonly BiomeAuthoringSnapshot _preset;
    readonly RingTerrainSampler _terrain;
    readonly VoxelTerrainSettings _settings;

    public BiomeAuthoringSampler(BiomeAuthoringPreset preset) : this(preset?.CreateSnapshot() ?? throw new ArgumentNullException(nameof(preset))) { }
    public BiomeAuthoringSampler(BiomeAuthoringSnapshot preset)
    {
        _preset = preset;
        _settings = new VoxelTerrainSettings { Seed = preset.Seed, EnableLakes = false, EnableProceduralCaves = false, EnableNaturalOverhangs = false };
        _terrain = new RingTerrainSampler(_settings, true, PreviewCircumference, PreviewCircumference / Math.Tau, PreviewWidth, 8192);
    }

    public BiomeAuthoringSurfaceSample Sample(float x, float z)
    {
        float h = SampleSurfaceHeight(x, z);
        TerrainSample terrain = SourceTerrain(x, z);
        float dx = SampleSurfaceHeight(x + 1, z) - SampleSurfaceHeight(x - 1, z);
        float dz = SampleSurfaceHeight(x, z + 1) - SampleSurfaceHeight(x, z - 1);
        Vector3 normal = new(-dx * .5f, 1, -dz * .5f); normal = normal.Normalized();
        float slope = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(normal.Y, -1, 1)));
        float moisture = Mathf.Clamp(_preset.Humidity * .78f + terrain.Moisture * .22f, 0, 1);
        float temperatureSuitability = SmoothStep(.16f, .36f, _preset.Temperature) * (1 - SmoothStep(.78f, .96f, _preset.Temperature));
        float baseSuitability = _preset.BaseBiome switch { TerrainBiomeId.Forest => 1f, TerrainBiomeId.Grassland => .82f, TerrainBiomeId.Tundra => .46f, TerrainBiomeId.Desert => .24f, TerrainBiomeId.Alpine => .32f, _ => 0f };
        float treeDensity = Mathf.Clamp(_preset.TreeDensity * (0.4f + moisture * .6f) * temperatureSuitability * baseSuitability, 0, 1);
        bool water = h <= 0;
        return new BiomeAuthoringSurfaceSample(h, normal, slope, moisture, treeDensity, GroundColor(), _preset.GroundMaterial, water);
    }

    /// <summary>Fast, continuous authored height for incremental preview meshes; it does not query source terrain.</summary>
    public float SampleSurfaceHeight(float x, float z)
    {
        float broad = ValueNoise(x, z, _preset.BroadFeatureScaleMeters, 21) * 2 - 1;
        float detail = ValueNoise(x, z, _preset.DetailFeatureScaleMeters, 37) * 2 - 1;
        return _preset.MeanElevationMeters + broad * _preset.ElevationVariationMeters + detail * _preset.DetailAmplitudeMeters;
    }

    public IReadOnlyList<BiomeAuthoringTree> GenerateTrees(Rect2 bounds, int maximum)
    {
        // Preview callers request small local strips and advance between frames.
        // Bound returned instances as well as the caller's sampled area.
        maximum = Mathf.Clamp(maximum, 0, 10_000);
        var result = new List<BiomeAuthoringTree>(Math.Min(maximum, 4096));
        if (maximum == 0 || _preset.TreeDensity <= 0) return result;
        // Cell centers are 1.25 times the requested spacing apart and jitter
        // by no more than 10% of a cell. Adjacent candidates remain >= spacing,
        // including when independently generated preview patches meet.
        float spacing = _preset.MinimumTreeSpacingMeters * 1.25f;
        int minX = Mathf.FloorToInt(bounds.Position.X / spacing) - 1, maxX = Mathf.CeilToInt(bounds.End.X / spacing) + 1;
        int minZ = Mathf.FloorToInt(bounds.Position.Y / spacing) - 1, maxZ = Mathf.CeilToInt(bounds.End.Y / spacing) + 1;
        for (int cz = minZ; cz <= maxZ && result.Count < maximum; cz++)
        for (int cx = minX; cx <= maxX && result.Count < maximum; cx++)
        {
            float x = (cx + .4f + Hash01(cx, cz, 1) * .2f) * spacing;
            float z = (cz + .4f + Hash01(cx, cz, 2) * .2f) * spacing;
            if (!bounds.HasPoint(new Vector2(x, z))) continue;
            BiomeAuthoringSurfaceSample surface = Sample(x, z);
            if (surface.HasSurfaceWater || surface.SlopeDegrees > _preset.MaximumTreeSlopeDegrees) continue;
            float cluster = ValueNoise(x, z, _preset.BroadFeatureScaleMeters * .55f, 7);
            float clearing = ValueNoise(x, z, _preset.BroadFeatureScaleMeters * .32f, 11);
            float chance = surface.TreeDensity * (.55f + _preset.TreeClustering * cluster) * (1 - _preset.ClearingStrength * clearing);
            if (Hash01(cx, cz, 3) >= Mathf.Clamp(chance, 0, 1)) continue;
            TreeSpecies species = SelectSpecies(Hash01(cx, cz, 4));
            float height = Mathf.Lerp(_preset.MinimumTreeHeightMeters, _preset.MaximumTreeHeightMeters, Hash01(cx, cz, 5));
            result.Add(new BiomeAuthoringTree(new Vector3(x, surface.SurfaceHeight, z), species, height, Hash01(cx, cz, 6) * Mathf.Tau));
        }
        return result;
    }

    public void Dispose() { _terrain.Dispose(); _settings.Dispose(); }

    TerrainSample SourceTerrain(float x, float z)
    {
        return _terrain.GetTerrainSample(Mathf.RoundToInt(x), Mathf.Clamp(Mathf.RoundToInt(z + PreviewWidth * .5f), 0, PreviewWidth - 1));
    }

    TreeSpecies SelectSpecies(float value)
    {
        float total = _preset.MapleWeight + _preset.FirWeight + _preset.PineWeight;
        if (total <= 0) return TreeSpecies.Maple;
        float maple = _preset.MapleWeight / total, fir = (_preset.MapleWeight + _preset.FirWeight) / total;
        return value < maple ? TreeSpecies.Maple : value < fir ? TreeSpecies.Fir : TreeSpecies.Pine;
    }
    Color GroundColor()
    {
        Color baseColor = _preset.GroundMaterial switch
        {
            BiomeGroundMaterial.ForestFloor => new(.18f, .25f, .12f), BiomeGroundMaterial.DesertSand => new(.51f, .42f, .22f),
            BiomeGroundMaterial.TundraGround => new(.60f, .64f, .57f), BiomeGroundMaterial.AlpineRock => new(.38f, .39f, .40f),
            _ => new(.25f, .38f, .16f)
        };
        return new Color(baseColor.R * _preset.GroundTint.R, baseColor.G * _preset.GroundTint.G, baseColor.B * _preset.GroundTint.B, 1);
    }
    float ValueNoise(float x, float z, float scale, uint salt)
    {
        scale = Mathf.Max(1, scale); float gx = x / scale, gz = z / scale;
        int ix = Mathf.FloorToInt(gx), iz = Mathf.FloorToInt(gz); float tx = Smooth(gx - ix), tz = Smooth(gz - iz);
        return Mathf.Lerp(Mathf.Lerp(Hash01(ix, iz, salt), Hash01(ix + 1, iz, salt), tx), Mathf.Lerp(Hash01(ix, iz + 1, salt), Hash01(ix + 1, iz + 1, salt), tx), tz);
    }
    float Hash01(int x, int z, uint salt)
    {
        unchecked { uint h = (uint)_preset.Seed ^ salt; h ^= (uint)x * 0x9E3779B9u; h ^= (uint)z * 0x85EBCA6Bu; h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; return (h & 0x00FFFFFF) / 16777215f; }
    }
    static float Smooth(float x) => x * x * (3 - 2 * x);
    static float SmoothStep(float a, float b, float x) { float t = Mathf.Clamp((x - a) / (b - a), 0, 1); return Smooth(t); }
}

public readonly record struct BiomeAuthoringSurfaceSample(float SurfaceHeight, Vector3 Normal, float SlopeDegrees,
    float Moisture, float TreeDensity, Color GroundColor, BiomeGroundMaterial GroundMaterial, bool HasSurfaceWater);
public readonly record struct BiomeAuthoringTree(Vector3 Position, TreeSpecies Species, float Height, float Yaw);
