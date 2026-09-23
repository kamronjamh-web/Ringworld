using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Phase5;

public readonly record struct VoxelBlockEditCommand(
    long CommandId,
    int AuthorityId,
    RingVoxelCoordinate TargetVoxel,
    BlockType BlockType,
    bool RequireTargetAir = false
);
