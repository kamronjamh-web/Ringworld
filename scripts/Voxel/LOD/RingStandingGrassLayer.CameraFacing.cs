using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    [ExportGroup("Experimental Distant Camera Facing")]
    /// <summary>Rotates only intermediate/far grass sources toward the main camera.</summary>
    [Export] public bool EnableGrassCameraFacing { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")] public float GrassCameraFacingStrength { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,1500,1")] public float GrassCameraFacingStartMeters { get; set; } = 12f;
    [Export(PropertyHint.Range, "1,2000,1")] public float GrassCameraFacingEndMeters { get; set; } = 48f;

    // Near and intermediate materials are separate resources. Tracking one
    // shared fingerprint would configure only whichever material ran first.
    private readonly Dictionary<ulong, (bool Enabled, float Strength, float Start, float End)> _grassCameraFacingMaterialStates = new();

    /// <summary>
    /// Writes the optional distant-card comparison settings. This deliberately
    /// remains separate from the LOD fingerprint, so editor/live tuning updates
    /// apply even when distance-LOD values did not change.
    /// </summary>
    private void ConfigureGrassCameraFacingMaterial(ShaderMaterial? material)
    {
        if (material == null) return;
        float start = Mathf.Clamp(GrassCameraFacingStartMeters, 0f, 1500f);
        float end = Mathf.Clamp(GrassCameraFacingEndMeters, 1f, 2000f);
        if (end <= start) end = Mathf.Min(2000f, start + .01f);
        var state = (EnableGrassCameraFacing, Mathf.Clamp(GrassCameraFacingStrength, 0f, 1f), start, end);
        ulong materialId = material.GetInstanceId();
        if (_grassCameraFacingMaterialStates.TryGetValue(materialId, out var previous) && state == previous) return;
        _grassCameraFacingMaterialStates[materialId] = state;
        material.SetShaderParameter("grass_camera_facing_enabled", EnableGrassCameraFacing);
        material.SetShaderParameter("grass_camera_facing_strength", Mathf.Clamp(GrassCameraFacingStrength, 0f, 1f));
        material.SetShaderParameter("grass_camera_facing_start", start);
        material.SetShaderParameter("grass_camera_facing_end", end);
    }
}
