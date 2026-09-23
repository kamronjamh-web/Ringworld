using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>Godot-facing CPU-only launcher for proxy seam regression checks.</summary>
[GlobalClass]
public partial class ProxySeamGeometryContractTest : RefCounted
{
    public bool RunContract() => RingTerrainProxySector.RunProxySeamGeometryContract();
}
