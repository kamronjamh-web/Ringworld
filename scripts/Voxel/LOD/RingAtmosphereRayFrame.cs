using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Camera-relative physical-space frame for the local finite-cylinder air path.</summary>
public readonly record struct RingAtmosphereRayFrame(
    bool Valid, Vector3 CameraWorld, float CameraHeightMeters, Vector3 OutwardWorld,
    Vector3 AxisWorld, float SidewallNegativeMeters, float SidewallPositiveMeters,
    float CoordinateScale, Vector3 ToSun)
{
    private static readonly StringName ValidName = "ring_atmosphere_ray_frame_valid";
    private static readonly StringName CameraWorldName = "ring_atmosphere_camera_world_position";
    private static readonly StringName CameraHeightName = "ring_atmosphere_camera_height_meters";
    private static readonly StringName OutwardName = "ring_atmosphere_outward_world";
    private static readonly StringName AxisName = "ring_atmosphere_axis_world";
    private static readonly StringName SidewallNegativeName = "ring_atmosphere_sidewall_negative_meters";
    private static readonly StringName SidewallPositiveName = "ring_atmosphere_sidewall_positive_meters";
    private static readonly StringName CoordinateScaleName = "ring_atmosphere_coordinate_scale";
    private static readonly StringName ToSunName = "ring_atmosphere_to_sun";
    public static RingAtmosphereRayFrame Invalid => new(false, Vector3.Zero, 0, Vector3.Up, Vector3.Forward, 0, 0, 1, Vector3.Up);

    public static RingAtmosphereRayFrame FromMainCamera(
        VoxelChunkGridManager grid, Camera3D camera, Vector3 toSun)
    {
        RingCanonicalPosition canonical = grid.WorldToCanonicalRingPosition64(camera.GlobalPosition);
        RingLodGeometrySnapshot geometry = grid.CaptureLodGeometrySnapshot();
        if (geometry.RingRadiusMeters <= 0.0) return Invalid;
        double angle = canonical.AroundMeters / geometry.RingRadiusMeters;
        Vector3 outward = (geometry.GridGlobalTransform.Basis * new Vector3(
            (float)Math.Cos(angle), 0.0f, (float)Math.Sin(angle))).Normalized();
        Vector3 axis = (geometry.GridGlobalTransform.Basis * Vector3.Up).Normalized();
        double width = grid.RingAcrossWidthMeters;
        return new RingAtmosphereRayFrame(true, camera.GlobalPosition,
            (float)canonical.VerticalMeters, outward, axis,
            (float)canonical.AcrossMeters, (float)(width - canonical.AcrossMeters), 1.0f, toSun);
    }

    public void Apply(ShaderMaterial? material)
    {
        if (material is not null) Apply(material.SetShaderParameter);
    }

    /// <summary>Publishes to shader and plugin material APIs alike.</summary>
    public void Apply(Action<StringName, Variant> setParameter)
    {
        bool valid = Valid
            && CameraWorld.IsFinite()
            && float.IsFinite(CameraHeightMeters)
            && OutwardWorld.IsFinite() && OutwardWorld.LengthSquared() > 0.000001f
            && AxisWorld.IsFinite() && AxisWorld.LengthSquared() > 0.000001f
            && float.IsFinite(SidewallNegativeMeters) && float.IsFinite(SidewallPositiveMeters)
            && float.IsFinite(CoordinateScale) && CoordinateScale > 0.0f
            && ToSun.IsFinite() && ToSun.LengthSquared() > 0.000001f;
        setParameter(ValidName, valid);
        if (!valid) return;
        setParameter(CameraWorldName, CameraWorld);
        setParameter(CameraHeightName, CameraHeightMeters);
        setParameter(OutwardName, OutwardWorld);
        setParameter(AxisName, AxisWorld);
        setParameter(SidewallNegativeName, SidewallNegativeMeters);
        setParameter(SidewallPositiveName, SidewallPositiveMeters);
        setParameter(CoordinateScaleName, CoordinateScale);
        setParameter(ToSunName, ToSun);
    }
}
