using System;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Vegetation;

public enum ForestHabitat
{
    None,
    Edge,
    OpenWoodland,
    DenseForest,
    Clearing
}

/// <summary>
/// A compact, deterministic description of forest cover at a canonical ring
/// coordinate.  It deliberately consumes the already-sampled terrain climate
/// rather than duplicating terrain generation.
/// </summary>
public readonly record struct ForestRegionSample(
    ForestHabitat Habitat,
    float Density,
    float Suitability,
    float Clearing,
    float MapleWeight,
    float FirWeight,
    float PineWeight);

/// <summary>
/// Stateless allocation-free forest-region field.  All noise is integer-cell
/// value noise, so canonical coordinates remain stable on mega-ring scales.
/// </summary>
public sealed class ForestRegionSampler
{
    public const int AlgorithmVersion = 1;

    private readonly uint _seed;
    private readonly long _circumferenceMeters;
    private readonly int _widthMeters;

    public ForestRegionSampler(int seed, long circumferenceMeters, int widthMeters)
    {
        _seed = unchecked((uint)seed);
        _circumferenceMeters = circumferenceMeters > 0 ? circumferenceMeters : 1;
        _widthMeters = Math.Max(1, widthMeters);
    }

    public ForestRegionSample Sample(
        long aroundMeters,
        int acrossMeters,
        in TerrainSample terrain,
        float slopeDegrees = 0f)
    {
        // Across is deliberately not wrapped: it is a physical ring boundary.
        if (acrossMeters < 0 || acrossMeters >= _widthMeters
            || terrain.HasSurfaceWater
            || !IsVegetatedBiome(terrain.Biome)
            || !float.IsFinite(slopeDegrees)
            || slopeDegrees >= 32f)
        {
            return default;
        }

        long around = WrapAround(aroundMeters);
        float temperature = Saturate(terrain.Temperature);
        float moisture = Saturate(terrain.Moisture);
        float climateCore = SmoothStep(.30f, .78f, temperature)
            * (1f - SmoothStep(.88f, 1f, temperature))
            * SmoothStep(.38f, .82f, moisture);
        // Temperate terrain has a viable base cover; the climate core still
        // controls how strongly it can develop into dense forest.
        float climate = .34f + climateCore * .66f;
        float biome = terrain.Biome == TerrainBiomeId.Forest ? 1f : .58f;
        float slope = 1f - SmoothStep(14f, 32f, Math.Max(0f, slopeDegrees));
        float suitability = Saturate(climate * biome * slope);
        if (suitability <= 0f)
        {
            return default;
        }

        // Broad cover produces coherent regions; the smaller fields introduce
        // woodland variation and clearings without a per-instance random walk.
        float cover = ValueNoise(around, acrossMeters, 12288, 0x51A3u);
        float variation = ValueNoise(around, acrossMeters, 2048, 0xD925u);
        float clearingField = ValueNoise(around, acrossMeters, 768, 0x2F71u);
        float clearing = SmoothStep(.70f, .91f, clearingField)
            * SmoothStep(.22f, .55f, suitability);
        float density = Saturate((cover * .72f + variation * .28f) * suitability - clearing * .98f);

        ForestHabitat habitat = density switch
        {
            < .14f => clearing > .18f ? ForestHabitat.Clearing : ForestHabitat.None,
            < .27f => ForestHabitat.Edge,
            < .53f => ForestHabitat.OpenWoodland,
            _ => ForestHabitat.DenseForest
        };

        // Maple is the default temperate tree.  Fir and pine are deliberately
        // localized by independent coarse fields, with cooler/drier climate
        // nudging their pockets into plausible parts of the forest range.
        float firPocket = ValueNoise(around, acrossMeters, 6144, 0xAC43u);
        float pinePocket = ValueNoise(around, acrossMeters, 4608, 0x71E9u);
        float fir = SmoothStep(.68f, .91f, firPocket) * (1f - temperature * .45f) * 2.45f;
        float pine = SmoothStep(.70f, .93f, pinePocket) * (.45f + (1f - moisture) * .55f) * 2.45f;
        // A high maple baseline preserves its regional dominance, while a
        // strong localized conifer field can still form a recognizable pocket.
        float maple = .85f;
        float total = maple + fir + pine;

        return new ForestRegionSample(habitat, density, suitability, clearing,
            maple / total, fir / total, pine / total);
    }

    private static bool IsVegetatedBiome(TerrainBiomeId biome)
        => biome is TerrainBiomeId.Forest or TerrainBiomeId.Grassland;

    private long WrapAround(long around)
    {
        long wrapped = around % _circumferenceMeters;
        return wrapped < 0 ? wrapped + _circumferenceMeters : wrapped;
    }

    private float ValueNoise(long around, int across, int cellSize, uint salt)
    {
        // Keep all ordinary cells at the requested meter scale and let the
        // final cell absorb any circumference remainder.  Its right lattice
        // point is cell zero, so even circumferences that are not a multiple
        // of cellSize meet continuously at the seam.
        long period = Math.Max(1L, _circumferenceMeters / cellSize);
        long x0 = around / cellSize;
        if (x0 >= period) x0 = period - 1;
        int z0 = (int)FloorDiv(across, cellSize);
        long x1 = x0 + 1;
        if (x1 >= period) x1 = 0;
        long cellStart = x0 * (long)cellSize;
        long cellWidth = x0 == period - 1 ? _circumferenceMeters - cellStart : cellSize;
        float tx = (float)(around - cellStart) / cellWidth;
        float tz = (float)(across - (long)z0 * cellSize) / cellSize;
        tx = Fade(tx);
        tz = Fade(tz);
        float a = Lerp(Hash01(x0, z0, salt), Hash01(x1, z0, salt), tx);
        float b = Lerp(Hash01(x0, z0 + 1, salt), Hash01(x1, z0 + 1, salt), tx);
        return Lerp(a, b, tz);
    }

    private float Hash01(long x, int z, uint salt)
    {
        unchecked
        {
            ulong h = (ulong)_seed ^ salt;
            h ^= (ulong)x * 0x9E3779B185EBCA87UL;
            h ^= (uint)z * 0xC2B2AE3D27D4EB4FUL;
            h ^= h >> 30;
            h *= 0xBF58476D1CE4E5B9UL;
            h ^= h >> 27;
            h *= 0x94D049BB133111EBUL;
            h ^= h >> 31;
            return (uint)(h >> 40) * (1f / 16777215f);
        }
    }

    private static long FloorDiv(long value, int divisor)
    {
        long q = value / divisor;
        return value < 0 && value % divisor != 0 ? q - 1 : q;
    }

    private static float Saturate(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Saturate((value - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }
    private static float Fade(float value) => value * value * value * (value * (value * 6f - 15f) + 10f);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
