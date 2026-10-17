using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingScaledFarViewport
{
    private void PublishCloudMaskAtmosphere(ShaderMaterial material, Vector3 around, Vector3 up, Vector3 across, Vector3 toSun)
    {
        var profile = new Godot.Collections.Array<Vector4>(); profile.Resize(10);
        if (RingAtmosphereModel.Enabled && _grid is not null && _sourceCamera is not null)
        {
            // Mask rays already use a physical-metre tangent frame anchored at
            // the volume. Rotate the actual renderer-eye frame into that basis;
            // never use feet height or scaled far-camera distances here.
            var frame = RingAtmosphereRayFrame.FromMainCamera(_grid, _sourceCamera,
                _sourceSun?.GlobalBasis.Z ?? Vector3.Up);
            if (frame.Valid)
            {
                profile[0] = new(1, (float)_radiusMeters, RingAtmosphereModel.FloorDepthMeters, RingAtmosphereModel.BoundaryHeightMeters);
                profile[1] = new(RingAtmosphereModel.UpperHeightMeters, RingAtmosphereModel.BoundaryExtinctionMeters, RingAtmosphereModel.UpperExtinctionMeters, RingAtmosphereModel.TotalMaxOpacity);
                profile[2] = new(RingAtmosphereModel.BoundaryMaxOpacity, RingAtmosphereModel.UpperMaxOpacity, RingAtmosphereModel.DistanceHazeMaxOpacity, RingAtmosphereModel.ScatteringEnergy);
                profile[3] = new(RingAtmosphereModel.ClearDistanceMeters, RingAtmosphereModel.FullDistanceMeters, RingAtmosphereModel.HighAltitudeClearDistanceMeters, RingAtmosphereModel.HighAltitudeFullDistanceMeters);
                profile[4] = new(RingAtmosphereModel.DistanceAltitudeStartMeters, RingAtmosphereModel.DistanceAltitudeEndMeters, RingAtmosphereModel.Aerosol, RingAtmosphereModel.ForwardScattering);
                Color day = RingAtmosphereModel.DayColor, night = RingAtmosphereModel.NightColor;
                profile[5] = new(day.R, day.G, day.B, RingAtmosphereModel.SunWarmth);
                profile[6] = new(night.R * RingAtmosphereModel.EclipseNightScale, night.G * RingAtmosphereModel.EclipseNightScale, night.B * RingAtmosphereModel.EclipseNightScale, 1);
                Vector3 outward = WorldDirectionToCanonical(frame.OutwardWorld, around, up, across);
                Vector3 axis = WorldDirectionToCanonical(frame.AxisWorld, around, up, across);
                profile[7] = new(outward.X, outward.Y, outward.Z, frame.CameraHeightMeters);
                profile[8] = new(axis.X, axis.Y, axis.Z, frame.SidewallNegativeMeters);
                profile[9] = new(toSun.X, toSun.Y, toSun.Z, frame.SidewallPositiveMeters);
            }
        }
        material.SetShaderParameter("ring_cloud_atmosphere_profile", profile);
    }
}
