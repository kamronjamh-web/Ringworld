using Godot;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Underground;

namespace RingworldTechDemo.Tools;

/// <summary>M1-8 gameplay test helper: evaluates the Terrain3D underground probe source (density -1 solid, +1 air, NaN unavailable).</summary>
public partial class EmitterBoreGameplayProbe : RefCounted
{
    public float Density(Node gameplay, double around, double vertical, double across)
    {
        if (gameplay is not RingTerrain3DGameplay terrain) return float.NaN;
        var source = new Terrain3DUndergroundProbeSource(terrain);
        return source.TryGetDensity(around, vertical, across, out float density) ? density : float.NaN;
    }
}
