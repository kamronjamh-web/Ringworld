using Godot;
using System.Runtime.CompilerServices;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingTerrainProxySector
{
    // Weak keys keep replacement materials from extending the cache lifetime.
    private readonly ConditionalWeakTable<ShaderMaterial, ProxyAtmosphereMaterialFrame> _proxyAtmosphereFrames = new();
    private long _proxyAtmosphereParameterWrites;

    private sealed class ProxyAtmosphereMaterialFrame
    {
        public ProxyAtmosphereMaterialFrame() { }
        public bool Initialized;
        public RingAtmosphereRayFrame Frame;
        public bool Valid;
    }

    private void ApplyProxyAtmosphereMaterialFrame(ShaderMaterial? material, RingAtmosphereRayFrame frame)
    {
        if (material == null) return;
        ProxyAtmosphereMaterialFrame cached = _proxyAtmosphereFrames.GetOrCreateValue(material);
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
            _proxyAtmosphereParameterWrites++;
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
