using System;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Fine terrain samples with the asymmetric Transvoxel padding: one sample
/// before and two samples after the owning 32m voxel chunk on every axis.
/// </summary>
public sealed class RingTerrainFineSdfHalo
{
    public const int ChunkCellCount = 32;
    public const int NegativePadding = 1;
    public const int PositivePadding = 2;
    public const int SampleCount = ChunkCellCount + NegativePadding + PositivePadding;

    public RingTerrainSampleCoordinate GlobalOrigin { get; }
    public float[] Densities { get; }
    public BlockType[] Materials { get; }

    public RingTerrainFineSdfHalo(
        RingTerrainSampleCoordinate globalOrigin,
        float[] densities,
        BlockType[] materials)
    {
        GlobalOrigin = globalOrigin;
        Densities = densities;
        Materials = materials;
        int expected = SampleCount * SampleCount * SampleCount;
        if (densities.Length != expected || materials.Length != expected)
            throw new ArgumentException($"Fine SDF halos require {expected} samples.");
    }

    public int GetSampleIndex(int x, int y, int z)
    {
        if ((uint)x >= SampleCount || (uint)y >= SampleCount || (uint)z >= SampleCount)
            throw new ArgumentOutOfRangeException();
        return x + SampleCount * (y + SampleCount * z);
    }

    public float GetDensity(int x, int y, int z) => Densities[GetSampleIndex(x, y, z)];
    public BlockType GetMaterial(int x, int y, int z) => Materials[GetSampleIndex(x, y, z)];

    public static RingTerrainFineSdfHalo Build(
        RingTerrainSdfSnapshot snapshot,
        RingTerrainSampleCoordinate fineChunkOrigin)
    {
        var haloOrigin = new RingTerrainSampleCoordinate(
            fineChunkOrigin.AroundVoxel - NegativePadding,
            fineChunkOrigin.VerticalVoxel - NegativePadding,
            fineChunkOrigin.AcrossVoxel - NegativePadding);
        int sampleTotal = SampleCount * SampleCount * SampleCount;
        var densities = new float[sampleTotal];
        var materials = new BlockType[sampleTotal];
        int index = 0;
        for (int z = 0; z < SampleCount; z++)
        for (int y = 0; y < SampleCount; y++)
        for (int x = 0; x < SampleCount; x++)
        {
            long around = haloOrigin.AroundVoxel + x;
            int vertical = haloOrigin.VerticalVoxel + y;
            int across = haloOrigin.AcrossVoxel + z;
            densities[index] = snapshot.SampleDensity(around, vertical, across);
            materials[index] = snapshot.SampleNaturalMaterial(around, vertical, across);
            index++;
        }
        return new RingTerrainFineSdfHalo(haloOrigin, densities, materials);
    }
}