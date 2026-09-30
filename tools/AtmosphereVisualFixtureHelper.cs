using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>Fixture-only placement that bypasses terrain spawn-floor settling.</summary>
public partial class AtmosphereVisualFixtureHelper : RefCounted
{
    public bool Place(Node explorer, double aroundMeters, double verticalMeters, double acrossMeters)
    {
        if (explorer is not RingVoxelExplorerController controller
            || !double.IsFinite(aroundMeters)
            || !double.IsFinite(verticalMeters)
            || !double.IsFinite(acrossMeters))
            return false;
        // Prime the controller's camera basis for this ring location. The
        // benchmark override intentionally preserves the prior basis, which
        // can otherwise be upside down after a large around-ring teleport.
        controller.SetCanonicalSpawnMeters(aroundMeters, verticalMeters, acrossMeters);
        controller.SetBenchmarkCanonicalPosition(new RingCanonicalPosition(
            aroundMeters, verticalMeters, acrossMeters));
        controller.SetPresentationView(0.0f, -10.0f);
        return true;
    }

    public double ResolveCameraCanonicalHeight(Node gridNode, Camera3D camera)
    {
        if (gridNode is not VoxelChunkGridManager grid
            || !GodotObject.IsInstanceValid(camera))
            return double.NaN;
        return grid.WorldToCanonicalRingPosition64(camera.GlobalPosition).VerticalMeters;
    }

    /// <summary>Fixture-only deterministic noclip motion for renderer diagnostics.</summary>
    public bool Move(Node explorer, double aroundMeters, double verticalMeters, double acrossMeters, float simulatedSpeedMetersPerSecond)
    {
        if (explorer is not RingVoxelExplorerController controller
            || !double.IsFinite(aroundMeters)
            || !double.IsFinite(verticalMeters)
            || !double.IsFinite(acrossMeters))
            return false;
        controller.SetBenchmarkCanonicalPosition(
            new RingCanonicalPosition(aroundMeters, verticalMeters, acrossMeters),
            simulatedSpeedMetersPerSecond,
            simulatedNoclip: true);
        return true;
    }
}
