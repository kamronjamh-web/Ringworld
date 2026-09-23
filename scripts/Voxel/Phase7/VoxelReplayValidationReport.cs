namespace RingworldTechDemo.Voxel.Phase7;

public readonly record struct VoxelReplayValidationReport(
    bool Success,
    int JournalCommandCount,
    int ReplayedCommandCount,
    int ResultMismatchCount,
    bool FinalStateMatches,
    string ExpectedStateHash,
    string ReplayedStateHash,
    string Message
);
