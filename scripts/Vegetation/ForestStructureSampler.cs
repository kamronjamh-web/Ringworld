using System;
using System.Collections.Concurrent;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Vegetation;

/// <summary>
/// T6 design A2-A4: the pure fields behind canopy structure in worlds with vegetation.forest_structure. Stand age (about 600 m patches with a 190 m texture), its contrast and effective age,
/// the stem and spacing factors, the treeline density term, the size-class tables and the habitat species weights. Its value noise is a copy of ForestRegionSampler's (same hash, same seam rule)
/// with its own salts, so ForestRegionSampler and its AlgorithmVersion are untouched. Stateless apart from the seed and world size: safe to share between workers.
/// Bump <see cref="AlgorithmVersion"/> when any formula, salt or table here changes (it is part of the structured tree cache identity).
/// </summary>
public sealed class ForestStructureSampler
{
    public const int AlgorithmVersion = 1;

    private const uint AgeBroadSalt = 0x5A6E1u, AgeFineSalt = 0x5A6E2u;
    private const int AgeBroadCell = 640, AgeFineCell = 192;

    private readonly uint _seed;
    private readonly long _circumferenceMeters;
    private readonly float _contrast;

    private static readonly ConcurrentDictionary<(int Seed, long Circumference, int Width, int ContrastBits), ForestStructureSampler> Cache = new();

    public ForestStructureSampler(int seed, long circumferenceMeters, int widthMeters, float contrast = 1f)
    {
        _seed = unchecked((uint)seed);
        _circumferenceMeters = circumferenceMeters > 0 ? circumferenceMeters : 1;
        _ = widthMeters; // across is not wrapped, so the width never enters the noise; it only keys the cache like ForestRegionSampler's identity
        _contrast = float.IsFinite(contrast) ? Math.Clamp(contrast, 0f, 1.5f) : 1f;
    }

    public float Contrast => _contrast;

    /// <summary>One immutable instance per (seed, circumference, width, contrast). Thread safe.</summary>
    public static ForestStructureSampler For(int seed, long circumferenceMeters, int widthMeters, float contrast)
        => Cache.GetOrAdd((seed, circumferenceMeters, widthMeters, BitConverter.SingleToInt32Bits(contrast)),
            key => new ForestStructureSampler(key.Seed, key.Circumference, key.Width, contrast));

    /// <summary>The world's sampler: seed, ring size and the forest_stand_age_contrast recipe field (1 when the feature's field is not active).</summary>
    public static ForestStructureSampler For(RingTerrainSampler sampler)
        => For(sampler.Seed, sampler.AroundRingVoxelLength, sampler.RingAcrossWidthMeters, ContrastOf(sampler));

    internal const string FeatureId = "vegetation.forest_structure";

    public static float ContrastOf(RingTerrainSampler sampler)
    {
        var recipe = sampler.RuntimeSurfaceRecipe;
        return recipe != null && recipe.HasWorldFeature(FeatureId) ? (float)recipe.GetExtensionField("forest_stand_age_contrast") : 1f;
    }

    // ------------------------------------------------------------------ stand age

    /// <summary>Stand age before contrast, in [0, 1]: 0.7 x 640 m noise + 0.3 x 192 m noise, SmoothStep(0.22, 0.78).</summary>
    public float StandAge(long aroundMeters, int acrossMeters)
    {
        long around = WrapAround(aroundMeters);
        float raw = 0.7f * ValueNoise(around, acrossMeters, AgeBroadCell, AgeBroadSalt) + 0.3f * ValueNoise(around, acrossMeters, AgeFineCell, AgeFineSalt);
        return SmoothStep(0.22f, 0.78f, raw);
    }

    /// <summary>a_c = clamp(0.5 + (age - 0.5) c) with c the recipe contrast.</summary>
    public float ApplyContrast(float age) => Saturate(0.5f + (age - 0.5f) * _contrast);

    public float ContrastedStandAge(long aroundMeters, int acrossMeters) => ApplyContrast(StandAge(aroundMeters, acrossMeters));

    /// <summary>The root's effective age (young edges and regrowth around clearings, dwarfing at the treeline).</summary>
    public float EffectiveAge(long aroundMeters, int acrossMeters, in ForestRegionSample regional, float temperature)
        => EffectiveAge(ContrastedStandAge(aroundMeters, acrossMeters), regional.Density, regional.Clearing, temperature);

    public static float EffectiveAge(float contrastedAge, float regionalDensity, float regionalClearing, float temperature)
        => contrastedAge * (0.5f + 0.5f * SmoothStep(0.20f, 0.30f, regionalDensity))
            * (1f - 0.5f * SmoothStep(0.05f, 0.40f, regionalClearing))
            * SmoothStep(0.24f, 0.45f, temperature);

    /// <summary>k(a) = 1.35 - 0.7 a: young thickets 1.35x, old self-thinned stands 0.65x.</summary>
    public static float StemFactor(float a) => 1.35f - 0.7f * a;

    /// <summary>tau_d(T) = 0.15 + 0.85 SmoothStep(0.24, 0.40, T).</summary>
    public static float TreelineDensity(float temperature) => 0.15f + 0.85f * SmoothStep(0.24f, 0.40f, temperature);

    /// <summary>Spacing radius multiplier 0.75 + 0.55 a (1.9-6.8 m young, 3.25-11.7 m old).</summary>
    public static float SpacingFactor(float a) => 0.75f + 0.55f * a;

    /// <summary>The largest spacing radius the gated pass can produce: 9 m x 1.3.</summary>
    public const float MaximumSpacingMeters = 11.7f;

    // ------------------------------------------------------------------ size classes

    // Rows are the anchors at a = 0, 0.5 and 1 (design A2 table); classes in ascending height.
    private static readonly TreeSizeClass[] MapleClasses = { TreeSizeClass.Small, TreeSizeClass.Medium, TreeSizeClass.Large };
    private static readonly float[,] MapleAnchors = { { .85f, .15f, 0f }, { .45f, .40f, .15f }, { .15f, .40f, .45f } };
    private static readonly TreeSizeClass[] PineClasses = { TreeSizeClass.Small, TreeSizeClass.Medium, TreeSizeClass.Big, TreeSizeClass.Large };
    private static readonly float[,] PineAnchors = { { .75f, .25f, 0f, 0f }, { .35f, .35f, .20f, .10f }, { .10f, .25f, .30f, .35f } };
    private static readonly TreeSizeClass[] FirClasses = { TreeSizeClass.Medium, TreeSizeClass.Large };
    private static readonly float[,] FirAnchors = { { 1f, 0f }, { .6f, .4f }, { .3f, .7f } };

    /// <summary>The class drawn for a uniform <paramref name="random"/> from the species' table at effective age <paramref name="a"/> (linear between the anchors).</summary>
    public static TreeSizeClass SelectSizeClass(TreeSpecies species, float a, float random)
    {
        TreeSizeClass[] classes; float[,] anchors;
        switch (species)
        {
            case TreeSpecies.Maple: classes = MapleClasses; anchors = MapleAnchors; break;
            case TreeSpecies.Pine: classes = PineClasses; anchors = PineAnchors; break;
            default: classes = FirClasses; anchors = FirAnchors; break;
        }
        a = Saturate(a);
        int lo = a < 0.5f ? 0 : 1;
        float t = (a - lo * 0.5f) / 0.5f;
        float cumulative = 0f;
        for (int i = 0; i < classes.Length - 1; i++)
        {
            cumulative += anchors[lo, i] + (anchors[lo + 1, i] - anchors[lo, i]) * t;
            if (random < cumulative) return classes[i];
        }
        return classes[^1];
    }

    // ------------------------------------------------------------------ species by habitat

    /// <summary>
    /// A3: habitat weights from moisture M, temperature T, wetness W, 64 m relief R64, rock exposure X and slope S (degrees), 75 % habitat and 25 % the regional pockets.
    /// The three weights sum to 1 (the regional weights do).
    /// </summary>
    public static void SpeciesWeights(float moisture, float temperature, float wetness, float relief64, float rockExposure, float slopeDegrees,
        in ForestRegionSample regional, out float maple, out float fir, out float pine)
    {
        float mapleH = SmoothStep(0.30f, 0.65f, moisture) * (0.4f + 0.6f * SmoothStep(0.30f, 0.70f, wetness))
            * (1f - 0.5f * SmoothStep(0.15f, 0.50f, relief64)) * (1f - 0.6f * SmoothStep(12f, 28f, slopeDegrees)) * SmoothStep(0.30f, 0.45f, temperature);
        float firH = (1f - SmoothStep(0.32f, 0.55f, temperature)) * (0.5f + 0.5f * SmoothStep(0.40f, 0.70f, moisture));
        float pineH = 0.8f * (1f - SmoothStep(0.25f, 0.60f, moisture)) + 0.6f * SmoothStep(0.10f, 0.50f, relief64) * SmoothStep(10f, 25f, slopeDegrees) + 0.5f * rockExposure;
        float h = mapleH + firH + pineH;
        if (!(h >= 1e-4f)) { mapleH = 1f; firH = 0f; pineH = 0f; h = 1f; }
        maple = 0.75f * mapleH / h + 0.25f * regional.MapleWeight;
        fir = 0.75f * firH / h + 0.25f * regional.FirWeight;
        pine = 0.75f * pineH / h + 0.25f * regional.PineWeight;
    }

    // ------------------------------------------------------------------ noise (a copy of ForestRegionSampler's)

    private long WrapAround(long around)
    {
        long wrapped = around % _circumferenceMeters;
        return wrapped < 0 ? wrapped + _circumferenceMeters : wrapped;
    }

    private float ValueNoise(long around, int across, int cellSize, uint salt)
    {
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
