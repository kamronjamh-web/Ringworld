using Godot;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Underground;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.Tools;

/// <summary>Gameplay test helper: evaluates the Terrain3D underground probe source (density -1 solid, +1 air, NaN unavailable) and the velocity clamp.</summary>
public partial class EmitterBoreGameplayProbe : RefCounted
{
    public float Density(Node gameplay, double around, double vertical, double across)
    {
        if (gameplay is not RingTerrain3DGameplay terrain) return float.NaN;
        var source = new Terrain3DUndergroundProbeSource(terrain);
        return source.TryGetDensity(around, vertical, across, out float density) ? density : float.NaN;
    }

    /// <summary>The coverage velocity clamp as the explorer's physics applies it at the explorer's current position.</summary>
    public Vector3 ConstrainedVelocity(Node gameplay, Node explorer, Vector3 velocity, double delta)
    {
        if (gameplay is not RingTerrain3DGameplay terrain || explorer is not RingVoxelExplorerController controller) return new Vector3(float.NaN, float.NaN, float.NaN);
        return terrain.ConstrainSurfaceVelocity(controller.CanonicalPosition, velocity, delta);
    }
}
