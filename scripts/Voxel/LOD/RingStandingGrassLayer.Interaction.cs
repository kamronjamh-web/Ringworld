using Godot;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    [ExportGroup("Interaction")]
    [Export] public bool EnableGrassInteraction { get; set; } = true;
    [Export(PropertyHint.Range, "0.2,5,0.05")] public float GrassInteractionRadiusMeters { get; set; } = 1.1f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float GrassInteractionStrength { get; set; } = 1;
    [Export(PropertyHint.Range, "0.1,20,0.1")] public float GrassInteractionRecoverySeconds { get; set; } = 3;
    [Export(PropertyHint.Range, "0.1,8,0.1")] public float GrassInteractionSampleSpacingMeters { get; set; } = .75f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float GrassWindStrengthMultiplier { get; set; } = 1;
    [Export(PropertyHint.Range, "0,2,0.01")] public float GrassWindGustMultiplier { get; set; } = 1;
    [Export(PropertyHint.Range, "1,2,0.01")] public float GrassMaximumWidthScale { get; set; } = 1.35f;

    private readonly RingGrassInteractionField _interaction = new();
    private static readonly Vector4[] EmptyInteractionPoints = new Vector4[RingGrassInteractionField.Capacity];
    private RingLodGeometrySnapshot _interactionGeometry;
    private bool _hasInteractionGeometry;
    public int ActiveGrassInteractionSampleCount { get; private set; }

    private void UpdateGrassInteraction(float delta)
    {
        if (_grid == null || _explorer == null || !EnableGrassInteraction || StreamingSuspended
            || _explorer.CurrentAltitudeAboveTerrainMeters > 2 || _explorer.ActualTravelSpeedMetersPerSecond > SuspendStreamingAboveSpeedMetersPerSecond)
        { _interaction.Clear(); PublishGrassInteraction(); return; }
        _interactionGeometry = _grid.CaptureLodGeometrySnapshot(); _hasInteractionGeometry = true;
        _interaction.Age(Mathf.Max(0, delta));
        _interaction.Push(_explorer.CanonicalPosition, Mathf.Max(.1f, GrassInteractionSampleSpacingMeters), _grid.TerrainSampler?.AroundRingVoxelLength ?? 0);
        PublishGrassInteraction();
    }
    private void PublishGrassInteraction()
    {
        int count = 0;
        Vector4[] points = _hasInteractionGeometry && EnableGrassInteraction
            ? _interaction.Project(_interactionGeometry, GrassInteractionRecoverySeconds, out count) : EmptyInteractionPoints;
        ActiveGrassInteractionSampleCount = count;
        ConfigureInteractionMaterial(_material, points); ConfigureInteractionMaterial(_intermediateMaterial, points);
    }
    private void ConfigureInteractionMaterial(ShaderMaterial? material, Vector4[] points)
    {
        if (material == null) return;
        material.SetShaderParameter("grass_interaction_enabled", EnableGrassInteraction && ActiveGrassInteractionSampleCount > 0);
        material.SetShaderParameter("grass_interaction_points", points);
        material.SetShaderParameter("grass_interaction_count", ActiveGrassInteractionSampleCount);
        material.SetShaderParameter("grass_interaction_radius", GrassInteractionRadiusMeters);
        material.SetShaderParameter("grass_interaction_strength", GrassInteractionStrength);
        material.SetShaderParameter("grass_wind_strength_multiplier", GrassWindStrengthMultiplier);
        material.SetShaderParameter("grass_wind_gust_multiplier", GrassWindGustMultiplier);
        material.SetShaderParameter("grass_lod_max_width_scale", GrassMaximumWidthScale);
    }
}
