using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

// Test-only access to the production CPU height sampler; no world/save edits.
public partial class TerrainLayerOverlapProbe : RefCounted
{
    public Vector3 WorldPoint(Node source, double around, double vertical, double across) =>
        ((VoxelChunkGridManager)source).CanonicalRingPositionToWorld(new RingCanonicalPosition(around, vertical, across));

    public void Anchor(Node source, double around, double vertical, double across) =>
        ((VoxelChunkGridManager)source).SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(around, vertical, across));

    public int SurfaceHeight(Node source, long around, int across)
    {
        var grid = (VoxelChunkGridManager)source;
        if (grid.TerrainSampler != null) return grid.TerrainSampler.GetSurfaceHeight(around, across);
        using var sampler = grid.CreateIndependentTerrainSampler();
        return sampler!.GetSurfaceHeight(around, across);
    }
}
