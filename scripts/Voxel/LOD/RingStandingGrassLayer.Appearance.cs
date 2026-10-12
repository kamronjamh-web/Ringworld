using Godot;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    private readonly record struct AppearanceMaterialState(bool Enabled, bool BladeShading, Color Grassland, Color Forest, Color Sparse,
        float Strength, float StartFraction, float EndFraction, float LightingMatch, float DrawDistance,
        Vector3 TerrainGrass, Vector3 TerrainDesert, Vector3 TerrainForest, Vector3 TerrainTundra, Vector3 TerrainAlpine);
    private readonly System.Collections.Generic.Dictionary<ulong, AppearanceMaterialState> _appearanceMaterialStates = new();
    /// <summary>Optional gameplay appearance profile. A null profile preserves legacy placement.</summary>
    [Export] public RingGrassAppearanceProfile? GrassAppearanceProfile { get; set; }
    private RingGrassAppearanceProfile EnsureAppearance() => GrassAppearanceProfile ??= new RingGrassAppearanceProfile();
    [Export] public bool EnableGrassBladeShading { get => GrassAppearanceProfile?.EnableBladeShading ?? false; set => EnsureAppearance().EnableBladeShading = value; }
    [Export] public bool EnableGrassAppearance { get => GrassAppearanceProfile?.Enabled ?? false; set => EnsureAppearance().Enabled = value; }
    [Export] public float GrasslandBiomeDensity { get => GrassAppearanceProfile?.GrasslandDensityMultiplier ?? 1; set => EnsureAppearance().GrasslandDensityMultiplier = value; }
    [Export] public float ForestBiomeDensity { get => GrassAppearanceProfile?.ForestDensityMultiplier ?? .65f; set => EnsureAppearance().ForestDensityMultiplier = value; }
    [Export] public float SparseBiomeDensity { get => GrassAppearanceProfile?.SparseDensityMultiplier ?? .22f; set => EnsureAppearance().SparseDensityMultiplier = value; }
    [Export] public float GrasslandBiomeHeight { get => GrassAppearanceProfile?.GrasslandHeightMultiplier ?? 1; set => EnsureAppearance().GrasslandHeightMultiplier = value; }
    [Export] public float ForestBiomeHeight { get => GrassAppearanceProfile?.ForestHeightMultiplier ?? 1.12f; set => EnsureAppearance().ForestHeightMultiplier = value; }
    [Export] public float SparseBiomeHeight { get => GrassAppearanceProfile?.SparseHeightMultiplier ?? .70f; set => EnsureAppearance().SparseHeightMultiplier = value; }
    [Export] public float GrassTerrainHandoffStrength { get => GrassAppearanceProfile?.TerrainHandoffStrength ?? 1f; set => EnsureAppearance().TerrainHandoffStrength = value; }
    [Export] public float GrassTerrainBlendStartPercent { get => GrassAppearanceProfile?.TerrainBlendStartPercent ?? 70f; set => EnsureAppearance().TerrainBlendStartPercent = value; }
    [Export] public float GrassTerrainBlendEndPercent { get => GrassAppearanceProfile?.TerrainBlendEndPercent ?? 90f; set => EnsureAppearance().TerrainBlendEndPercent = value; }
    [Export] public float GrassTerrainLightingMatch { get => GrassAppearanceProfile?.TerrainLightingMatch ?? 1f; set => EnsureAppearance().TerrainLightingMatch = value; }
    [Export] public Color GrasslandBiomeTint { get => GrassAppearanceProfile?.GrasslandTint ?? new Color(.22f, .36f, .12f, 1); set => EnsureAppearance().GrasslandTint = value; }
    [Export] public Color ForestBiomeTint { get => GrassAppearanceProfile?.ForestTint ?? new Color(.14f, .28f, .10f, 1); set => EnsureAppearance().ForestTint = value; }
    [Export] public Color SparseBiomeTint { get => GrassAppearanceProfile?.SparseTint ?? new Color(.42f, .38f, .18f, 1); set => EnsureAppearance().SparseTint = value; }

    private void ConfigureGrassAppearance(ShaderMaterial? material)
    {
        if (material == null) return;
        RingGrassAppearanceProfile? profile = GrassAppearanceProfile;
        float start = FiniteClamp(GrassTerrainBlendStartPercent, 70f, 0f, 99.9f);
        float end = FiniteClamp(GrassTerrainBlendEndPercent, 90f, start + .1f, 100f);
        float near = float.IsFinite(LodNearDistanceMeters) ? LodNearDistanceMeters : 0f;
        float far = float.IsFinite(LodFarDistanceMeters) ? LodFarDistanceMeters : near + 1f;
        float drawDistance = EnableDistanceLod ? Mathf.Max(near + 1f, far) : VisibilityRadiusMeters;
        if (!float.IsFinite(drawDistance)) drawDistance = 1f;
        drawDistance = Mathf.Max(1f, drawDistance);
        var palette = RingTerrainVisuals.CaptureTexturePalette();
        var state = new AppearanceMaterialState(profile?.Enabled ?? false, EnableGrassBladeShading, GrasslandBiomeTint, ForestBiomeTint, SparseBiomeTint,
            FiniteClamp(GrassTerrainHandoffStrength, 1f, 0f, 1f), start * .01f, end * .01f,
            FiniteClamp(GrassTerrainLightingMatch, 1f, 0f, 1f), drawDistance,
            palette.Grass, palette.Desert, palette.Forest, palette.Tundra, palette.Alpine);
        ulong materialId = material.GetInstanceId();
        if (_appearanceMaterialStates.TryGetValue(materialId, out AppearanceMaterialState previous) && previous == state) return;
        material.SetShaderParameter("grass_appearance_enabled", state.Enabled);
        material.SetShaderParameter("grass_blade_shading_enabled", state.BladeShading);
        material.SetShaderParameter("grass_palette_grassland", state.Grassland);
        material.SetShaderParameter("grass_palette_forest", state.Forest);
        material.SetShaderParameter("grass_palette_sparse", state.Sparse);
        material.SetShaderParameter("grass_terrain_handoff_strength", state.Strength);
        material.SetShaderParameter("grass_terrain_blend_start_fraction", state.StartFraction);
        material.SetShaderParameter("grass_terrain_blend_end_fraction", state.EndFraction);
        material.SetShaderParameter("grass_terrain_lighting_match", state.LightingMatch);
        material.SetShaderParameter("grass_terrain_draw_distance", state.DrawDistance);
        material.SetShaderParameter("grass_terrain_average_grass", state.TerrainGrass);
        material.SetShaderParameter("grass_terrain_average_desert", state.TerrainDesert);
        material.SetShaderParameter("grass_terrain_average_forest", state.TerrainForest);
        material.SetShaderParameter("grass_terrain_average_tundra", state.TerrainTundra);
        material.SetShaderParameter("grass_terrain_average_alpine", state.TerrainAlpine);
        _appearanceMaterialStates[materialId] = state;
    }

    private static float FiniteClamp(float value, float fallback, float minimum, float maximum)
        => Mathf.Clamp(float.IsFinite(value) ? value : fallback, minimum, maximum);
}
