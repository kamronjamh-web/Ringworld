using System;
using Godot;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    private readonly record struct AppearanceMaterialState(bool Enabled, bool BladeShading, Color Grassland, Color Forest, Color Sparse,
        float Strength, float StartFraction, float EndFraction, float LightingMatch, float DrawDistance, bool Coordinated,
        Vector3 TerrainGrass, Vector3 TerrainDesert, Vector3 TerrainForest, Vector3 TerrainTundra, Vector3 TerrainAlpine);
    private readonly System.Collections.Generic.Dictionary<ulong, AppearanceMaterialState> _appearanceMaterialStates = new();
    /// <summary>Optional gameplay appearance profile. A null profile preserves legacy placement.</summary>
    [Export] public RingGrassAppearanceProfile? GrassAppearanceProfile { get; set; }
    private RingGrassAppearanceProfile EnsureAppearance() => GrassAppearanceProfile ??= new RingGrassAppearanceProfile();
    [Export] public bool EnableCoordinatedGrassHandoff { get => GrassAppearanceProfile?.CoordinatedTerrainHandoff ?? true; set => EnsureAppearance().CoordinatedTerrainHandoff = value; }
    private (bool Enabled, float Start, float End)? _coordinatedHandoffGlobals;
    private static WeakReference<RingStandingGrassLayer>? _coordinatedHandoffPublisher;
    private static readonly System.Collections.Generic.List<WeakReference<RingStandingGrassLayer>> _coordinatedHandoffCandidates = new();
    private bool CanPublishCoordinatedGrassHandoff => GodotObject.IsInstanceValid(this) && IsInsideTree()
        && Enabled && EnableGrassAppearance && EnableCoordinatedGrassHandoff && !StreamingSuspended && IsVisibleInTree();
    private static ulong CurrentCoordinatedGrassHandoffPublisherId
        => _coordinatedHandoffPublisher != null && _coordinatedHandoffPublisher.TryGetTarget(out var publisher)
            && GodotObject.IsInstanceValid(publisher) ? publisher.GetInstanceId() : 0;
    internal static ulong CoordinatedGrassHandoffPublisherIdForTests => CurrentCoordinatedGrassHandoffPublisherId;

    // A guaranteed nearest-first window, rather than the currently loaded tiles:
    // startup, worker completion and camera movement must not move this band.
    internal float EffectiveGrassDrawDistance()
    {
        // Cards: the reach. No resident-window clamp, because the resident cap is sized from the reach.
        if (_style.IsCards) return _style.ReachMeters;
        float near = FiniteClamp(LodNearDistanceMeters, 0f, 0f, 100000f);
        float requested = EnableDistanceLod
            ? Mathf.Max(near + 1f, FiniteClamp(LodFarDistanceMeters, near + 1f, 0f, 100000f))
            : FiniteClamp(VisibilityRadiusMeters, 1f, 1f, 100000f);
        if (!EnableCoordinatedGrassHandoff || !EnableGrassAppearance) return requested;
        float tile = FiniteClamp(SpatialTileMeters, 16f, 16f, 10000f);
        int sourceTiles = Math.Max(1, MaximumGrassSourceInstances / MaximumGrassSourcesPerTile);
        int supportedTiles = Math.Min(Math.Max(1, MaximumResidentTiles), sourceTiles);
        // Inverts RingGrassQualityProfile.HasSufficientResidentWindow's bound.
        float guaranteed = tile * (Mathf.Sqrt(supportedTiles / Mathf.Pi) - Mathf.Sqrt(2f));
        return Mathf.Min(requested, Mathf.Max(1f, guaranteed));
    }

    internal Vector2 CoordinatedGrassHandoffBand()
    {
        float cutoff = EffectiveGrassDrawDistance();
        float width = FiniteClamp(LodTransitionWidthMeters, 1f, .001f, 100000f);
        float requestedStart = FiniteClamp(GrassTerrainBlendStartPercent, 70f, 0f, 99.9f);
        float requestedEnd = FiniteClamp(GrassTerrainBlendEndPercent, 90f, requestedStart + .1f, 100f);
        float fadeStart = cutoff - width * .5f;
        float end = Mathf.Min(cutoff * requestedEnd * .01f,
            fadeStart > 0f ? fadeStart : cutoff);
        float minimumBand = Mathf.Min(end, Mathf.Max(.001f, cutoff * .01f));
        float start = Mathf.Min(cutoff * requestedStart * .01f,
            Mathf.Max(0f, end - minimumBand));
        return new Vector2(start, Mathf.Max(start + .001f, end));
    }

    private void RefreshCoordinatedGrassHandoffGlobals()
    {
        if (!CanPublishCoordinatedGrassHandoff)
        {
            ReleaseCoordinatedGrassHandoffGlobals();
            return;
        }
        bool registered = false;
        for (int i = _coordinatedHandoffCandidates.Count - 1; i >= 0; i--)
        {
            if (!_coordinatedHandoffCandidates[i].TryGetTarget(out var candidate) || !GodotObject.IsInstanceValid(candidate))
                _coordinatedHandoffCandidates.RemoveAt(i);
            else if (ReferenceEquals(candidate, this)) registered = true;
        }
        if (!registered) _coordinatedHandoffCandidates.Add(new(this));
        if (_coordinatedHandoffPublisher != null && _coordinatedHandoffPublisher.TryGetTarget(out var owner)
            && owner.CanPublishCoordinatedGrassHandoff && !ReferenceEquals(owner, this)) return;
        bool acquired = CurrentCoordinatedGrassHandoffPublisherId != GetInstanceId();
        _coordinatedHandoffPublisher = new(this);
        Vector2 band = CoordinatedGrassHandoffBand();
        var next = (true, band.X, band.Y);
        if (!acquired && _coordinatedHandoffGlobals == next) return;
        RenderingServer.GlobalShaderParameterSet("ring_grass_coordinated_handoff_enabled", next.Item1 ? 1f : 0f);
        RenderingServer.GlobalShaderParameterSet("ring_grass_coordinated_handoff_start_meters", band.X);
        RenderingServer.GlobalShaderParameterSet("ring_grass_coordinated_handoff_end_meters", band.Y);
        _coordinatedHandoffGlobals = next;
    }

    private void ReleaseCoordinatedGrassHandoffGlobals()
    {
        for (int i = _coordinatedHandoffCandidates.Count - 1; i >= 0; i--)
            if (!_coordinatedHandoffCandidates[i].TryGetTarget(out var candidate)
                || !GodotObject.IsInstanceValid(candidate) || ReferenceEquals(candidate, this))
                _coordinatedHandoffCandidates.RemoveAt(i);
        _coordinatedHandoffGlobals = null;
        if (_coordinatedHandoffPublisher == null || !_coordinatedHandoffPublisher.TryGetTarget(out var owner)
            || !ReferenceEquals(owner, this)) return;
        _coordinatedHandoffPublisher = null;
        foreach (var reference in _coordinatedHandoffCandidates)
            if (reference.TryGetTarget(out var candidate) && candidate.CanPublishCoordinatedGrassHandoff)
            {
                candidate.RefreshCoordinatedGrassHandoffGlobals();
                return;
            }
        RenderingServer.GlobalShaderParameterSet("ring_grass_coordinated_handoff_enabled", 0f);
    }
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
        RefreshCoordinatedGrassHandoffGlobals();
        RingGrassAppearanceProfile? profile = GrassAppearanceProfile;
        float start = FiniteClamp(GrassTerrainBlendStartPercent, 70f, 0f, 99.9f);
        float end = FiniteClamp(GrassTerrainBlendEndPercent, 90f, start + .1f, 100f);
        float drawDistance = EffectiveGrassDrawDistance();
        var palette = RingTerrainVisuals.CaptureTexturePalette();
        var state = new AppearanceMaterialState(profile?.Enabled ?? false, EnableGrassBladeShading, GrasslandBiomeTint, ForestBiomeTint, SparseBiomeTint,
            FiniteClamp(GrassTerrainHandoffStrength, 1f, 0f, 1f), start * .01f, end * .01f,
            FiniteClamp(GrassTerrainLightingMatch, 1f, 0f, 1f), drawDistance, EnableCoordinatedGrassHandoff,
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
        material.SetShaderParameter("grass_coordinated_handoff_enabled", state.Coordinated);
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
