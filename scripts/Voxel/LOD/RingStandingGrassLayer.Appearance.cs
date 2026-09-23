using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    private static readonly System.Collections.Generic.Dictionary<string, Vector3> TerrainAlbedoAverages = new();
    private readonly System.Collections.Generic.Dictionary<ulong, ulong> _appearanceMaterialFingerprints = new();
    /// <summary>Optional gameplay appearance profile. A null profile preserves legacy placement.</summary>
    [Export] public RingGrassAppearanceProfile? GrassAppearanceProfile { get; set; }
    private RingGrassAppearanceProfile EnsureAppearance() => GrassAppearanceProfile ??= new RingGrassAppearanceProfile();
    [Export] public bool EnableGrassAppearance { get => GrassAppearanceProfile?.Enabled ?? false; set => EnsureAppearance().Enabled = value; }
    [Export] public float GrasslandBiomeDensity { get => GrassAppearanceProfile?.GrasslandDensityMultiplier ?? 1; set => EnsureAppearance().GrasslandDensityMultiplier = value; }
    [Export] public float ForestBiomeDensity { get => GrassAppearanceProfile?.ForestDensityMultiplier ?? .65f; set => EnsureAppearance().ForestDensityMultiplier = value; }
    [Export] public float SparseBiomeDensity { get => GrassAppearanceProfile?.SparseDensityMultiplier ?? .22f; set => EnsureAppearance().SparseDensityMultiplier = value; }
    [Export] public float GrasslandBiomeHeight { get => GrassAppearanceProfile?.GrasslandHeightMultiplier ?? 1; set => EnsureAppearance().GrasslandHeightMultiplier = value; }
    [Export] public float ForestBiomeHeight { get => GrassAppearanceProfile?.ForestHeightMultiplier ?? 1.12f; set => EnsureAppearance().ForestHeightMultiplier = value; }
    [Export] public float SparseBiomeHeight { get => GrassAppearanceProfile?.SparseHeightMultiplier ?? .70f; set => EnsureAppearance().SparseHeightMultiplier = value; }
    [Export] public float GrassTerrainHandoffStrength { get => GrassAppearanceProfile?.TerrainHandoffStrength ?? .55f; set => EnsureAppearance().TerrainHandoffStrength = value; }
    [Export] public Color GrasslandBiomeTint { get => GrassAppearanceProfile?.GrasslandTint ?? new Color(.22f, .36f, .12f, 1); set => EnsureAppearance().GrasslandTint = value; }
    [Export] public Color ForestBiomeTint { get => GrassAppearanceProfile?.ForestTint ?? new Color(.14f, .28f, .10f, 1); set => EnsureAppearance().ForestTint = value; }
    [Export] public Color SparseBiomeTint { get => GrassAppearanceProfile?.SparseTint ?? new Color(.42f, .38f, .18f, 1); set => EnsureAppearance().SparseTint = value; }

    private void ConfigureGrassAppearance(ShaderMaterial? material)
    {
        if (material == null) return;
        RingGrassAppearanceProfile? profile = GrassAppearanceProfile;
        ulong fingerprint = profile == null ? 0UL : unchecked((ulong)profile.Enabled.GetHashCode()
            ^ ((ulong)profile.GrasslandTint.GetHashCode() << 1) ^ ((ulong)profile.ForestTint.GetHashCode() << 2)
            ^ ((ulong)profile.SparseTint.GetHashCode() << 3) ^ ((ulong)profile.TerrainHandoffStrength.GetHashCode() << 4));
        ulong materialId = material.GetInstanceId();
        if (_appearanceMaterialFingerprints.TryGetValue(materialId, out ulong previous) && previous == fingerprint) return;
        _appearanceMaterialFingerprints[materialId] = fingerprint;
        material.SetShaderParameter("grass_appearance_enabled", profile?.Enabled ?? false);
        if (profile == null) return;
        material.SetShaderParameter("grass_palette_grassland", profile.GrasslandTint);
        material.SetShaderParameter("grass_palette_forest", profile.ForestTint);
        material.SetShaderParameter("grass_palette_sparse", profile.SparseTint);
        material.SetShaderParameter("grass_terrain_handoff_strength", profile.TerrainHandoffStrength);
        material.SetShaderParameter("grass_terrain_average_grass", AverageTerrainAlbedo("res://materials/third_party/voxelgame/grass_albedo.png", new Vector3(.22f, .36f, .12f)) * Vector3.One.Lerp(new Vector3(.72f, 1.30f, .62f), .58f));
        material.SetShaderParameter("grass_terrain_average_dirt", AverageTerrainAlbedo("res://materials/third_party/voxelgame/dirt_albedo.png", new Vector3(.32f, .24f, .14f)));
        material.SetShaderParameter("grass_terrain_average_desert", AverageTerrainAlbedo("res://materials/biomes/desert_sand_albedo.png", new Vector3(.46f, .38f, .22f)));
        material.SetShaderParameter("grass_terrain_average_forest", AverageTerrainAlbedo("res://materials/biomes/forest_floor_albedo.png", new Vector3(.18f, .25f, .12f)));
        material.SetShaderParameter("grass_terrain_average_tundra", AverageTerrainAlbedo("res://materials/biomes/tundra_ground_albedo.png", new Vector3(.40f, .42f, .35f)));
        material.SetShaderParameter("grass_terrain_average_alpine", AverageTerrainAlbedo("res://materials/biomes/alpine_rock_albedo.png", new Vector3(.38f, .36f, .32f)));
    }

    private static Vector3 AverageTerrainAlbedo(string path, Vector3 fallback)
    {
        if (TerrainAlbedoAverages.TryGetValue(path, out Vector3 cached)) return cached;
        Image? image = GD.Load<Texture2D>(path)?.GetImage();
        if (image == null || image.IsEmpty()) return TerrainAlbedoAverages[path] = fallback;
        if (image.IsCompressed() && image.Decompress() != Error.Ok) { image.Dispose(); return TerrainAlbedoAverages[path] = fallback; }
        int stepX = Mathf.Max(1, image.GetWidth() / 32), stepY = Mathf.Max(1, image.GetHeight() / 32), count = 0;
        Vector3 sum = Vector3.Zero;
        for (int y = 0; y < image.GetHeight(); y += stepY)
        for (int x = 0; x < image.GetWidth(); x += stepX)
        { Color linear = image.GetPixel(x, y).SrgbToLinear(); sum += new Vector3(linear.R, linear.G, linear.B); count++; }
        Vector3 result = count == 0 ? fallback : sum / count;
        image.Dispose();
        return TerrainAlbedoAverages[path] = result;
    }
}
