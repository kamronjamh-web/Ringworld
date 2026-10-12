using System;
using Godot;
using RingworldTechDemo.Voxel.Integration;

/// <summary>Capture-boundary value snapshots only; no gameplay mutation or GPU readback.</summary>
public partial class GrassCostGameplayCapture : RefCounted
{
    public Godot.Collections.Dictionary CaptureView(Node world)
    {
        var explorer = world.GetNode<RingVoxelExplorerController>("Explorer");
        var position = explorer.CanonicalPosition;
        Camera3D camera = world.GetViewport().GetCamera3D()
            ?? throw new InvalidOperationException("Grass cost capture requires an active viewport camera.");
        Transform3D transform = camera.GlobalTransform;
        var values = new Godot.Collections.Array<double>();
        foreach (Vector3 axis in new[] { transform.Basis.X, transform.Basis.Y, transform.Basis.Z, transform.Origin })
        { values.Add(axis.X); values.Add(axis.Y); values.Add(axis.Z); }
        return new Godot.Collections.Dictionary
        {
            ["around"] = position.AroundMeters, ["across"] = position.AcrossMeters,
            ["height"] = position.VerticalMeters, ["yaw"] = explorer.SavedYaw, ["pitch"] = explorer.SavedPitch,
            ["cachedAltitude"] = explorer.CurrentAltitudeAboveTerrainMeters,
            ["computedAltitude"] = explorer.GetCanonicalAtmosphereHeightMeters(),
            ["cameraId"] = camera.GetInstanceId().ToString(), ["cameraPath"] = camera.GetPath().ToString(),
            ["cameraTransform"] = values, ["projection"] = (int)camera.Projection,
            ["cameraHOffset"] = camera.HOffset, ["cameraVOffset"] = camera.VOffset,
            ["fov"] = camera.Fov, ["near"] = camera.Near, ["far"] = camera.Far,
            ["size"] = camera.Size, ["keepAspect"] = (int)camera.KeepAspect,
            ["frustumOffset"] = camera.FrustumOffset.ToString(), ["cullMask"] = camera.CullMask,
            ["viewportSize"] = camera.GetViewport().GetVisibleRect().Size.ToString(),
            ["thirdPersonState"] = "No public third-person state property; active camera identity/transform checked."
        };
    }
}
