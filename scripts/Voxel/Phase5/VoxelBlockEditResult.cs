using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Phase5;

public readonly record struct VoxelBlockEditResult(
    bool Applied,
    string Reason,
    RingChunkCoordinate ChunkCoordinate,
    Vector3I LocalVoxel,
    BlockType PreviousBlockType,
    BlockType NewBlockType,
    uint ChunkRevision,
    uint GlobalRevision
);
