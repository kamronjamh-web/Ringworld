using RingworldTechDemo.Voxel.Phase5;

namespace RingworldTechDemo.Voxel.Phase7;

public readonly record struct VoxelCommandJournalEntry(
    int Sequence,
    VoxelBlockEditCommand Command,
    VoxelBlockEditResult Result
);
