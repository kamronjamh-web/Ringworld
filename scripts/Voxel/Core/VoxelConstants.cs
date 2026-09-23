namespace RingworldTechDemo.Voxel.Core;

public static class VoxelConstants
{
    public const int VoxelSizeMeters = 1;

    public const int ChunkSizeX = 32;
    public const int ChunkSizeY = 32;
    public const int ChunkSizeZ = 32;

    public const int ChunkVolume = ChunkSizeX * ChunkSizeY * ChunkSizeZ;

    public const int MaxExcavationDepth = 256;
    public const int MinVertical = -MaxExcavationDepth;
    public const int BedrockVertical = -MaxExcavationDepth;
    public const int MaxVertical = 2048;

    public const int DefaultBuildHeightAboveSurface = 128;
}
