using Godot;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingBiomeDecorationLayer
{
    // Every local tree path (procedural, imported meshes, cloned GPU surfaces,
    // and the shared far impostor) enters through this registration point.
    private void RegisterTreeLightingMaterial(ShaderMaterial material)
    {
        if (_treeMaterials.Contains(material)) return;
        global::RingworldTechDemo.RingCloudShadowProjector.Register(material);
        _treeMaterials.Add(material);
        if (_grid != null)
            RingAtmosphereModel.ConfigureMaterial(material,
                _grid.GetCanonicalRingCircumferenceMeters64() / System.Math.Tau);
        ConfigureTreeLightingMaterial(material, true, true, true);
    }

    private void UpdateTreeAtmosphereRayFrame(Vector3 sunDirection)
    {
        if (_grid == null || _treeMaterials.Count == 0) return;
        Camera3D? camera = GetViewport().GetCamera3D();
        RingAtmosphereRayFrame frame = camera == null
            ? RingAtmosphereRayFrame.Invalid
            : RingAtmosphereRayFrame.FromMainCamera(_grid, camera, sunDirection);
        foreach (ShaderMaterial material in _treeMaterials)
            frame.Apply(material);
    }
}
