using System;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Phase1;

public sealed class VoxelChunkData
{
    private readonly BlockType[] _blocks = new BlockType[VoxelConstants.ChunkVolume];

    // Worker-only read view used by the mesher to avoid a method call and
    // repeated coordinate validation for every face sample. The span cannot
    // escape or mutate the chunk's backing storage.
    internal ReadOnlySpan<BlockType> BlockSpan => _blocks;

    public VoxelChunkData Clone()
    {
        var clone = new VoxelChunkData();
        Array.Copy(_blocks, clone._blocks, _blocks.Length);
        return clone;
    }

    public bool IsInBounds(int x, int y, int z)
    {
        return x >= 0 && x < VoxelConstants.ChunkSizeX
            && y >= 0 && y < VoxelConstants.ChunkSizeY
            && z >= 0 && z < VoxelConstants.ChunkSizeZ;
    }

    public BlockType GetBlock(int x, int y, int z)
    {
        return _blocks[ToLinearIndex(x, y, z)];
    }

    public BlockType GetBlockSafe(int x, int y, int z)
    {
        if (!IsInBounds(x, y, z))
        {
            return BlockType.Air;
        }

        return GetBlock(x, y, z);
    }

    public void SetBlock(int x, int y, int z, BlockType blockType)
    {
        _blocks[ToLinearIndex(x, y, z)] = blockType;
    }

    public void Fill(BlockType blockType)
    {
        Array.Fill(_blocks, blockType);
    }

    public void FillFromSampler(Func<int, int, int, BlockType> sampler)
    {
        for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
        {
            for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
            {
                for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
                {
                    SetBlock(x, y, z, sampler(x, y, z));
                }
            }
        }
    }

    public void FillFlatTestTerrain(int solidHeight)
    {
        Fill(BlockType.Air);

        int height = Math.Clamp(solidHeight, 1, VoxelConstants.ChunkSizeY);

        for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
        {
            for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    SetBlock(x, y, z, GetFlatTestTerrainBlock(y, height));
                }
            }
        }
    }

    public void CopyBoundaryX(bool positive, BlockType[] destination)
    {
        if (destination.Length != VoxelConstants.ChunkSizeY * VoxelConstants.ChunkSizeZ)
            throw new ArgumentException("Invalid X boundary buffer length.", nameof(destination));
        int x = positive ? VoxelConstants.ChunkSizeX - 1 : 0;
        for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
        for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
            destination[y + VoxelConstants.ChunkSizeY * z] =
                _blocks[ToLinearIndex(x, y, z)];
    }

    public void CopyBoundaryY(bool positive, BlockType[] destination)
    {
        if (destination.Length != VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeZ)
            throw new ArgumentException("Invalid Y boundary buffer length.", nameof(destination));
        int y = positive ? VoxelConstants.ChunkSizeY - 1 : 0;
        for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
        for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
            destination[x + VoxelConstants.ChunkSizeX * z] =
                _blocks[ToLinearIndex(x, y, z)];
    }

    public void CopyBoundaryZ(bool positive, BlockType[] destination)
    {
        if (destination.Length != VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeY)
            throw new ArgumentException("Invalid Z boundary buffer length.", nameof(destination));
        int z = positive ? VoxelConstants.ChunkSizeZ - 1 : 0;
        for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
        for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
            destination[x + VoxelConstants.ChunkSizeX * y] =
                _blocks[ToLinearIndex(x, y, z)];
    }

    public static BlockType GetFlatTestTerrainBlock(int y, int solidHeight)
    {
        int height = Math.Clamp(solidHeight, 1, VoxelConstants.ChunkSizeY);

        if (y < 0 || y >= height)
        {
            return BlockType.Air;
        }

        if (y == 0)
        {
            return BlockType.Bedrock;
        }

        return y < height - 2 ? BlockType.Rock : BlockType.Soil;
    }

    public static int ToLinearIndex(int x, int y, int z)
    {
        return x + VoxelConstants.ChunkSizeX * (y + VoxelConstants.ChunkSizeY * z);
    }

    public static bool TryFromLinearIndex(int index, out Vector3I localVoxel)
    {
        if (index < 0 || index >= VoxelConstants.ChunkVolume)
        {
            localVoxel = default;
            return false;
        }

        int xyArea = VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeY;
        int z = index / xyArea;
        int remainder = index - z * xyArea;
        int y = remainder / VoxelConstants.ChunkSizeX;
        int x = remainder - y * VoxelConstants.ChunkSizeX;

        localVoxel = new Vector3I(x, y, z);
        return true;
    }
}
