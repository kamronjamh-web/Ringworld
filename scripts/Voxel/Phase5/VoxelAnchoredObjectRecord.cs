using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Phase5;

public sealed class VoxelAnchoredObjectRecord
{
    public long AnchorId { get; set; }
    public string ArchetypeId { get; set; } = string.Empty;
    public RingVoxelCoordinate AnchorVoxel { get; set; }
    public string ScenePath { get; set; } = string.Empty;
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public Vector3 LocalOffset { get; set; } = Vector3.Zero;
}
