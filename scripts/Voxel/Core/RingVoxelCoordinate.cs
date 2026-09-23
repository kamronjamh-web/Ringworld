namespace RingworldTechDemo.Voxel.Core;

public readonly record struct RingVoxelCoordinate(
    long AroundRing,
    int AcrossRing,
    int Vertical
);
