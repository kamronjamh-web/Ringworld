using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Underground;

/// <summary>Picks the active terrain backend so the controller never references a backend type.</summary>
public static class UndergroundProbeSourceFactory
{
    public static IUndergroundProbeSource? Create(Node owner, NodePath smoothTerrainPath, NodePath gridManagerPath, NodePath terrain3DPath)
    {
        var smooth = owner.GetNodeOrNull<SmoothTerrainGrid>(smoothTerrainPath);
        var grid = owner.GetNodeOrNull<VoxelChunkGridManager>(gridManagerPath);
        if (smooth != null && grid != null) return new SmoothUndergroundProbeSource(smooth, grid);
        var terrain3D = owner.GetNodeOrNull<RingTerrain3DGameplay>(terrain3DPath);
        return terrain3D != null ? new Terrain3DUndergroundProbeSource(terrain3D) : null;
    }
}
