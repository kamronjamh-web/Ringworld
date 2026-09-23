namespace RingworldTechDemo.Voxel.Core;

public readonly record struct RingChunkCoordinate(
    long AroundRingChunk,
    int AcrossRingChunk,
    int VerticalChunk
);
