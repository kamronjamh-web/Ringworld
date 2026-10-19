using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingScaledFarViewport
{
    private void PublishCloudMaskAtmosphere(ShaderMaterial material, Vector3 around, Vector3 up, Vector3 across, Vector3 toSun)
    {
        var profile = new Godot.Collections.Array<Vector4>(); profile.Resize(RingAtmosphereModel.CloudProfileSlots);
        if (RingAtmosphereModel.Enabled && _grid is not null && _sourceCamera is not null)
        {
            // Mask rays already use a physical-metre tangent frame anchored at
            // the volume. Rotate the actual renderer-eye frame into that basis;
            // never use feet height or scaled far-camera distances here.
            var frame = RingAtmosphereRayFrame.FromMainCamera(_grid, _sourceCamera,
                _sourceSun?.GlobalBasis.Z ?? Vector3.Up);
            if (frame.Valid)
            {
                Vector3 outward = WorldDirectionToCanonical(frame.OutwardWorld, around, up, across);
                Vector3 axis = WorldDirectionToCanonical(frame.AxisWorld, around, up, across);
                // Solar visibility 1 / hub 0: the mask shader reads the live eclipse globals itself (Legacy: ringworld_effective_solar_visibility,
                // physical: ringworld_effective_solar_day), so only the lighting colours and layer constants matter here.
                RingAtmosphereModel.PackCloudProfile(profile, _radiusMeters, outward, axis, frame.CameraHeightMeters,
                    frame.SidewallNegativeMeters, frame.SidewallPositiveMeters, toSun, 1.0f, 0.0f);
            }
        }
        material.SetShaderParameter("ring_cloud_atmosphere_profile", profile);
    }
}
