using Godot;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>
/// Shared deterministic terrain colour classification for non-voxel LODs.
/// Every input comes from RingTerrainSampler, so changing the world seed changes
/// the same continents, climate bands, and mountain belts at every distance.
/// </summary>
public static class RingTerrainVisuals
{
    // The close and regional proxy tiers shade land from these maps. The
    // complete-ring mesh cannot afford those triplanar samples per fragment,
    // so it uses their mip-average colours as vertex colours instead.
    private static readonly object TexturePaletteLock = new();
    // Must match the grass grade in voxel_ring_terrain and ring_terrain_proxy.
    // Those shaders mix the original sample with a 0.72/1.30/0.62 multiply at 58%.
    private static readonly Vector3 HealthyGrassMultiplier = new(0.8376f, 1.1740f, 0.7796f);
    private static bool _texturePaletteLoaded;
    // Linear-light averages measured from the current maps. They are reliable
    // fallbacks if an imported texture cannot expose its CPU image at runtime.
    private static Color _grassTextureColor = new(0.246128f, 0.221681f, 0.054416f, 1.0f);
    private static Color _desertTextureColor = new(0.741727f, 0.433516f, 0.128448f, 1.0f);
    private static Color _forestTextureColor = new(0.060681f, 0.037592f, 0.020822f, 1.0f);
    private static Color _tundraTextureColor = new(0.096062f, 0.092159f, 0.067381f, 1.0f);
    private static Color _alpineTextureColor = new(0.218961f, 0.222097f, 0.216046f, 1.0f);

    /// <summary>Caches the close-tier biome maps' representative linear colours.</summary>
    public static void PreloadTexturePalette()
    {
        lock (TexturePaletteLock)
        {
            if (_texturePaletteLoaded) return;
            _grassTextureColor = Multiply(
                LoadTextureAverage("res://materials/third_party/voxelgame/grass_albedo.png", _grassTextureColor),
                HealthyGrassMultiplier);
            _desertTextureColor = LoadTextureAverage("res://materials/biomes/desert_sand_albedo.png", _desertTextureColor);
            _forestTextureColor = LoadTextureAverage("res://materials/biomes/forest_floor_albedo.png", _forestTextureColor);
            _tundraTextureColor = LoadTextureAverage("res://materials/biomes/tundra_ground_albedo.png", _tundraTextureColor);
            _alpineTextureColor = LoadTextureAverage("res://materials/biomes/alpine_rock_albedo.png", _alpineTextureColor);
            _texturePaletteLoaded = true;
        }
    }

    /// <summary>
    /// Colour for whole-ring/global meshes. It mirrors the close-tier biome
    /// texture blend instead of the older illustrative biome palette, avoiding
    /// a material-colour step at the LOD boundary without far shader samples.
    /// </summary>
    public static Color ResolveMacroTextureColor(
        in TerrainSample sample,
        int displayedHeight,
        RingTerrainSampler sampler,
        Color lowFallback,
        Color highFallback)
    {
        if (sample.HasSurfaceWater && displayedHeight >= sample.WaterSurfaceHeight)
            return ResolveSeededColor(sample, displayedHeight, sampler, lowFallback, highFallback);

        RingTerrainSampler.BiomeTextureWeights weights = sampler.GetBiomeTextureWeights(sample);
        float grassWeight = Mathf.Max(
            0.0f,
            1.0f - weights.Desert - weights.Forest - weights.Tundra - weights.Alpine);
        float total = Mathf.Max(
            0.0001f,
            grassWeight + weights.Desert + weights.Forest + weights.Tundra + weights.Alpine);
        Vector3 blended = (
            ToVector3(_grassTextureColor) * grassWeight
            + ToVector3(_desertTextureColor) * weights.Desert
            + ToVector3(_forestTextureColor) * weights.Forest
            + ToVector3(_tundraTextureColor) * weights.Tundra
            + ToVector3(_alpineTextureColor) * weights.Alpine) / total;
        return new Color(blended.X, blended.Y, blended.Z, 1.0f);
    }

    private static Vector3 ToVector3(Color color) => new(color.R, color.G, color.B);

    private static Color Multiply(Color color, Vector3 multiplier) => new(
        color.R * multiplier.X,
        color.G * multiplier.Y,
        color.B * multiplier.Z,
        color.A);

    private static Color LoadTextureAverage(string path, Color fallback)
    {
        Texture2D? texture = GD.Load<Texture2D>(path);
        Image? image = texture?.GetImage();
        if (image == null || image.IsEmpty())
            return fallback;

        try
        {
            // Imported textures may retain a compressed CPU image. GetPixel() is
            // unsupported for that format and can terminate the native C# bridge.
            // GetImage() returns an Image copy, so decompressing it cannot modify
            // the shared texture resource.
            if (image.IsCompressed() && image.Decompress() != Error.Ok)
                return fallback;

            int width = image.GetWidth();
            int height = image.GetHeight();
            if (width <= 0 || height <= 0)
                return fallback;

            // A stratified 128x128 read is equivalent to a high mip while avoiding
            // a multi-million-pixel startup scan for each terrain material.
            const int samplesPerAxis = 128;
            Vector3 total = Vector3.Zero;
            int count = 0;
            for (int y = 0; y < samplesPerAxis; y++)
            for (int x = 0; x < samplesPerAxis; x++)
            {
                int pixelX = Mathf.Min(width - 1, (int)((x + 0.5f) * width / samplesPerAxis));
                int pixelY = Mathf.Min(height - 1, (int)((y + 0.5f) * height / samplesPerAxis));
                Color srgb = image.GetPixel(pixelX, pixelY);
                total += new Vector3(SrgbToLinear(srgb.R), SrgbToLinear(srgb.G), SrgbToLinear(srgb.B));
                count++;
            }
            if (count == 0) return fallback;
            total /= count;
            return new Color(total.X, total.Y, total.Z, 1.0f);
        }
        finally
        {
            image.Dispose();
        }
    }

    private static float SrgbToLinear(float value) => value <= 0.04045f
        ? value / 12.92f
        : Mathf.Pow((value + 0.055f) / 1.055f, 2.4f);

    public static Color ResolveSeededColor(
        in TerrainSample sample,
        int displayedHeight,
        RingTerrainSampler sampler,
        Color lowFallback,
        Color highFallback)
    {
        bool displayingWater = sample.HasSurfaceWater
            && displayedHeight >= sample.WaterSurfaceHeight;
        if (displayingWater)
        {
            Color water = sample.WaterKind switch
            {
                SurfaceWaterKind.Ocean => sample.WaterDepth > 500
                    ? new Color(0.025f, 0.13f, 0.24f, 1.0f)
                    : new Color(0.035f, 0.25f, 0.34f, 1.0f),
                SurfaceWaterKind.Lake => new Color(0.045f, 0.29f, 0.36f, 1.0f),
                SurfaceWaterKind.River => new Color(0.075f, 0.36f, 0.43f, 1.0f),
                _ => new Color(0.04f, 0.22f, 0.30f, 1.0f)
            };
            float climateVariation = (sample.Moisture - 0.5f) * 0.06f;
            return new Color(
                Mathf.Clamp(water.R + climateVariation * 0.35f, 0.0f, 1.0f),
                Mathf.Clamp(water.G + climateVariation, 0.0f, 1.0f),
                Mathf.Clamp(water.B + climateVariation, 0.0f, 1.0f),
                1.0f);
        }

        Color region = sampler.GetBiomeColor(sample);

        float elevation = Mathf.Clamp(Mathf.InverseLerp(
            sampler.BedrockY,
            sampler.MaximumPossibleSurfaceHeight,
            displayedHeight), 0.0f, 1.0f);
        Color elevationColor = lowFallback.Lerp(highFallback, elevation);
        // Broad regional identity must survive the global LOD. Local detail is
        // carried by geometry; stronger coherent biome color makes hundreds of
        // kilometres read as continents rather than noisy material variation.
        Color seeded = elevationColor.Lerp(region, 0.82f);

        // Climate variation is deliberately restrained: it makes the chosen
        // seed legible without turning the macro terrain into false-colour noise.
        float warmDry = Mathf.Clamp(sample.Temperature * (1.0f - sample.Moisture), 0.0f, 1.0f);
        float coolWet = Mathf.Clamp((1.0f - sample.Temperature) * sample.Moisture, 0.0f, 1.0f);
        seeded = seeded.Lerp(new Color(0.44f, 0.35f, 0.17f, 1.0f), warmDry * 0.25f);
        seeded = seeded.Lerp(new Color(0.075f, 0.26f, 0.14f, 1.0f), coolWet * 0.27f);

        if (!sample.HasSurfaceWater)
        {
            float exposedRock = Mathf.Clamp(sample.RidgeStrength * 0.55f + elevation * 0.45f, 0.0f, 1.0f);
            seeded = seeded.Lerp(new Color(0.43f, 0.42f, 0.39f, 1.0f), exposedRock * 0.31f);
            float snow = Mathf.SmoothStep(0.52f, 0.88f, elevation)
                * Mathf.Clamp(1.15f - sample.Temperature, 0.0f, 1.0f)
                * Mathf.Lerp(0.42f, 1.0f, sample.RidgeStrength);
            seeded = seeded.Lerp(new Color(0.74f, 0.77f, 0.78f, 1.0f), snow * 0.62f);
        }

        return seeded;
    }
}
