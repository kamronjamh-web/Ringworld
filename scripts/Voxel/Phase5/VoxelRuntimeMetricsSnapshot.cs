namespace RingworldTechDemo.Voxel.Phase5;

public readonly record struct VoxelRuntimeMetricsSnapshot(
    int LoadedChunkCount,
    int QueuedRebuildCount,
    int QueuedNavInvalidationCount,
    int OverrideChunkCount,
    int OverrideVoxelCount,
    int AnchoredObjectCount,
    long EstimatedChunkDataBytes,
    long EstimatedOverrideBytes,
    long EstimatedAnchoredObjectBytes,
    long EstimatedTotalBytes,
    bool ExceedsSoftMemoryBudget,
    int SoftMemoryBudgetMb
);
