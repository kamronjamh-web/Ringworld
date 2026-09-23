using Godot;

namespace RingworldTechDemo.Voxel.Core;

/// <summary>
/// 64-bit canonical topology for worlds whose chunk circumference exceeds the
/// active runtime's legacy 32-bit chunk identity range.
/// </summary>
public readonly record struct MegaRingChunkCoordinate(
    long AroundRingChunk,
    long AcrossRingChunk,
    long VerticalChunk);

public static class MegaRingTopology
{
    public static long WrapAround(long coordinate, long length)
    {
        if (length <= 0)
            return coordinate;
        long wrapped = coordinate % length;
        return wrapped < 0 ? wrapped + length : wrapped;
    }

    public static long FloorDiv(long value, long divisor)
    {
        long quotient = value / divisor;
        long remainder = value % divisor;
        return remainder != 0 && ((remainder < 0) != (divisor < 0))
            ? quotient - 1
            : quotient;
    }

    public static MegaRingChunkCoordinate ToChunkCoordinate(
        long aroundVoxel,
        long acrossVoxel,
        long verticalVoxel,
        long aroundRingVoxelLength)
    {
        return new MegaRingChunkCoordinate(
            WrapAround(aroundVoxel, aroundRingVoxelLength) / VoxelConstants.ChunkSizeX,
            FloorDiv(acrossVoxel, VoxelConstants.ChunkSizeZ),
            FloorDiv(verticalVoxel, VoxelConstants.ChunkSizeY));
    }

    public static bool TryToLocalVoxel(
        MegaRingChunkCoordinate chunk,
        long aroundVoxel,
        long acrossVoxel,
        long verticalVoxel,
        long aroundRingVoxelLength,
        out Vector3I localVoxel)
    {
        long wrappedAround = WrapAround(aroundVoxel, aroundRingVoxelLength);
        long localAround = wrappedAround - chunk.AroundRingChunk * VoxelConstants.ChunkSizeX;
        long localAcross = acrossVoxel - chunk.AcrossRingChunk * VoxelConstants.ChunkSizeZ;
        long localVertical = verticalVoxel - chunk.VerticalChunk * VoxelConstants.ChunkSizeY;
        if (localAround is < 0 or >= VoxelConstants.ChunkSizeX
            || localAcross is < 0 or >= VoxelConstants.ChunkSizeZ
            || localVertical is < 0 or >= VoxelConstants.ChunkSizeY)
        {
            localVoxel = default;
            return false;
        }

        localVoxel = new Vector3I((int)localAround, (int)localVertical, (int)localAcross);
        return true;
    }

    public static bool RunSelfTest()
    {
        if (!MegaRingScale.Validate(out _))
            return false;

        long circumference = MegaRingScale.AroundVoxelLength;
        long lastVoxel = circumference - 1;
        MegaRingChunkCoordinate lastChunk = ToChunkCoordinate(
            lastVoxel,
            MegaRingScale.RingWidthMeters - 1,
            MegaRingScale.StructuralDepthMeters - 1,
            circumference);
        if (lastChunk.AroundRingChunk != MegaRingScale.AroundChunkCount - 1
            || lastChunk.AcrossRingChunk <= int.MaxValue / VoxelConstants.ChunkSizeZ
            || lastChunk.VerticalChunk <= int.MaxValue / VoxelConstants.ChunkSizeY)
        {
            return false;
        }

        if (!TryToLocalVoxel(
                lastChunk,
                lastVoxel,
                MegaRingScale.RingWidthMeters - 1,
                MegaRingScale.StructuralDepthMeters - 1,
                circumference,
                out Vector3I localVoxel)
            || localVoxel != new Vector3I(
                VoxelConstants.ChunkSizeX - 1,
                VoxelConstants.ChunkSizeY - 1,
                VoxelConstants.ChunkSizeZ - 1))
        {
            return false;
        }

        return WrapAround(-1, circumference) == circumference - 1
            && WrapAround(circumference, circumference) == 0
            && ToChunkCoordinate(circumference + 17, 0, 0, circumference).AroundRingChunk == 0;
    }
}