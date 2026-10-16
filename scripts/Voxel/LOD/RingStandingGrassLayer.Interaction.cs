using System;
using System.Collections.Generic;
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
    private readonly record struct InteractionControls(float Radius, float Strength, float Wind, float Gust, float Width);
    private sealed class InteractionMaterialCache
    {
        internal InteractionControls? Controls;
        internal bool? Enabled;
        internal int? Count;
        internal Vector4[]? Points;
    }
    private readonly Dictionary<ulong, InteractionMaterialCache> _interactionMaterialCaches = new();
    internal long InteractionParameterWrites { get; private set; }

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
        ConfigureInteractionMaterial(_material, points); ConfigureInteractionMaterial(_intermediateMaterial, points); if (_style.IsCards) ConfigureInteractionMaterial(_cardMaterial, points);
    }
    private void ConfigureInteractionMaterial(ShaderMaterial? material, Vector4[] points)
    {
        if (material == null) return;
        ulong id = material.GetInstanceId();
        if (!_interactionMaterialCaches.TryGetValue(id, out InteractionMaterialCache? cache))
            _interactionMaterialCaches[id] = cache = new InteractionMaterialCache();
        bool enabled = EnableGrassInteraction && ActiveGrassInteractionSampleCount > 0;
        if (cache.Enabled != enabled)
        {
            material.SetShaderParameter("grass_interaction_enabled", enabled);
            cache.Enabled = enabled; InteractionParameterWrites++;
        }
        if (cache.Count != ActiveGrassInteractionSampleCount)
        {
            material.SetShaderParameter("grass_interaction_count", ActiveGrassInteractionSampleCount);
            cache.Count = ActiveGrassInteractionSampleCount; InteractionParameterWrites++;
        }
        // Project reuses its array; retain a separate content snapshot rather than its reference.
        bool pointsChanged = cache.Points == null || cache.Points.Length != points.Length;
        if (!pointsChanged)
            for (int i = 0; i < points.Length; i++)
                if (cache.Points![i] != points[i]) { pointsChanged = true; break; }
        if (pointsChanged)
        {
            material.SetShaderParameter("grass_interaction_points", points);
            if (cache.Points == null || cache.Points.Length != points.Length) cache.Points = new Vector4[points.Length];
            Array.Copy(points, cache.Points, points.Length);
            InteractionParameterWrites++;
        }
        var controls = new InteractionControls(GrassInteractionRadiusMeters, GrassInteractionStrength,
            GrassWindStrengthMultiplier, GrassWindGustMultiplier, GrassMaximumWidthScale);
        if (cache.Controls != controls)
        {
            material.SetShaderParameter("grass_interaction_radius", controls.Radius);
            material.SetShaderParameter("grass_interaction_strength", controls.Strength);
            material.SetShaderParameter("grass_wind_strength_multiplier", controls.Wind);
            material.SetShaderParameter("grass_wind_gust_multiplier", controls.Gust);
            material.SetShaderParameter("grass_lod_max_width_scale", controls.Width);
            cache.Controls = controls; InteractionParameterWrites += 5;
        }
    }
}
