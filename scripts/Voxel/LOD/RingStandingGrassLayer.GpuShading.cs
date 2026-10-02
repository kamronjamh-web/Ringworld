using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    [ExportGroup("Experimental Grass GPU Shading")]
    /// <summary>Skips custom direct-light GGX only on intermediate/far passes; preserves diffuse, backlight, wind and interaction.</summary>
    [Export] public bool UseSimplifiedIntermediateGrassShading { get; set; }

    private readonly Dictionary<ulong, bool> _grassGpuShadingMaterialStates = new();

    // Called for both materials outside the LOD fingerprint early return so a
    // live setting change reaches existing renderers without rebuilding tiles.
    private void ConfigureGrassGpuShadingMaterial(ShaderMaterial? material)
    {
        if (material == null) return;
        ulong materialId = material.GetInstanceId();
        if (_grassGpuShadingMaterialStates.TryGetValue(materialId, out bool previous)
            && previous == UseSimplifiedIntermediateGrassShading) return;
        material.SetShaderParameter("grass_simplified_intermediate_shading", UseSimplifiedIntermediateGrassShading);
        _grassGpuShadingMaterialStates[materialId] = UseSimplifiedIntermediateGrassShading;
    }
}
