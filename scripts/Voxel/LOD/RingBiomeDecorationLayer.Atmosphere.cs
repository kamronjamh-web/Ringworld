using Godot;
using System.Runtime.CompilerServices;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingBiomeDecorationLayer
{
    // Weak keys keep replacement materials from extending the cache lifetime.
    private readonly ConditionalWeakTable<ShaderMaterial, TreeAtmosphereMaterialFrame> _treeAtmosphereFrames = new();
    private long _treeAtmosphereParameterWrites;

    private sealed class TreeAtmosphereMaterialFrame
    {
        public TreeAtmosphereMaterialFrame() { }
        public bool Initialized;
        public RingAtmosphereRayFrame Frame;
        public bool Valid;
    }

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
            ApplyTreeAtmosphereRayFrame(material, frame);
    }

    private void ApplyTreeAtmosphereRayFrame(ShaderMaterial material, RingAtmosphereRayFrame frame)
    {
        TreeAtmosphereMaterialFrame cached = _treeAtmosphereFrames.GetOrCreateValue(material);
        // Record equality uses exact field equality, preserving small rebases.
        if (cached.Initialized && cached.Frame == frame) return;
        bool valid = frame.Valid
            && frame.CameraWorld.IsFinite() && float.IsFinite(frame.CameraHeightMeters)
            && frame.OutwardWorld.IsFinite() && frame.OutwardWorld.LengthSquared() > .000001f
            && frame.AxisWorld.IsFinite() && frame.AxisWorld.LengthSquared() > .000001f
            && float.IsFinite(frame.SidewallNegativeMeters) && float.IsFinite(frame.SidewallPositiveMeters)
            && float.IsFinite(frame.CoordinateScale) && frame.CoordinateScale > 0
            && frame.ToSun.IsFinite() && frame.ToSun.LengthSquared() > .000001f;
        bool full = !cached.Initialized || !cached.Valid;
        RingAtmosphereRayFrame previous = cached.Frame;
        void Set(StringName name, Variant value)
        {
            material.SetShaderParameter(name, value);
            _treeAtmosphereParameterWrites++;
        }
        if (!cached.Initialized || cached.Valid != valid) Set("ring_atmosphere_ray_frame_valid", valid);
        if (valid)
        {
            if (full || !previous.CameraWorld.Equals(frame.CameraWorld)) Set("ring_atmosphere_camera_world_position", frame.CameraWorld);
            if (full || !previous.CameraHeightMeters.Equals(frame.CameraHeightMeters)) Set("ring_atmosphere_camera_height_meters", frame.CameraHeightMeters);
            if (full || !previous.OutwardWorld.Equals(frame.OutwardWorld)) Set("ring_atmosphere_outward_world", frame.OutwardWorld);
            if (full || !previous.AxisWorld.Equals(frame.AxisWorld)) Set("ring_atmosphere_axis_world", frame.AxisWorld);
            if (full || !previous.SidewallNegativeMeters.Equals(frame.SidewallNegativeMeters)) Set("ring_atmosphere_sidewall_negative_meters", frame.SidewallNegativeMeters);
            if (full || !previous.SidewallPositiveMeters.Equals(frame.SidewallPositiveMeters)) Set("ring_atmosphere_sidewall_positive_meters", frame.SidewallPositiveMeters);
            if (full || !previous.CoordinateScale.Equals(frame.CoordinateScale)) Set("ring_atmosphere_coordinate_scale", frame.CoordinateScale);
            if (full || !previous.ToSun.Equals(frame.ToSun)) Set("ring_atmosphere_to_sun", frame.ToSun);
        }
        cached.Frame = frame; cached.Valid = valid; cached.Initialized = true;
    }
}
