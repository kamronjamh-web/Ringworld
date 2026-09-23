using System;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// A canonical coarse terrain volume. At the initial 2m level it covers the
/// same 64m cube as eight adjacent 1m voxel chunks and carries a 33^3 sample
/// lattice, ready for a Transvoxel transition-cell consumer.
/// </summary>
public sealed class RingTerrainCoarseSdfChunk
{
    public const int CellCount = 32;
    public const int SampleCount = CellCount + 1;

    public RingTerrainSampleCoordinate GlobalOrigin { get; }
    public int SpacingMeters { get; }
    public float[] Densities { get; }
    public BlockType[] Materials { get; }

    public RingTerrainCoarseSdfChunk(
        RingTerrainSampleCoordinate globalOrigin,
        int spacingMeters,
        float[] densities,
        BlockType[] materials)
    {
        GlobalOrigin = globalOrigin;
        SpacingMeters = Math.Max(1, spacingMeters);
        Densities = densities;
        Materials = materials;
        int expected = SampleCount * SampleCount * SampleCount;
        if (densities.Length != expected || materials.Length != expected)
            throw new ArgumentException($"Coarse SDF chunks require {expected} samples.");
    }

    public int GetSampleIndex(int x, int y, int z)
    {
        if ((uint)x >= SampleCount || (uint)y >= SampleCount || (uint)z >= SampleCount)
            throw new ArgumentOutOfRangeException();
        return x + SampleCount * (y + SampleCount * z);
    }

    public float GetDensity(int x, int y, int z) => Densities[GetSampleIndex(x, y, z)];
    public BlockType GetMaterial(int x, int y, int z) => Materials[GetSampleIndex(x, y, z)];

    public static RingTerrainCoarseSdfChunk Build(
        RingTerrainSdfSnapshot snapshot,
        RingTerrainSampleCoordinate globalOrigin,
        int spacingMeters)
    {
        int spacing = Math.Max(1, spacingMeters);
        int sampleTotal = SampleCount * SampleCount * SampleCount;
        var densities = new float[sampleTotal];
        var materials = new BlockType[sampleTotal];
        int index = 0;
        for (int z = 0; z < SampleCount; z++)
        for (int y = 0; y < SampleCount; y++)
        for (int x = 0; x < SampleCount; x++)
        {
            long around = globalOrigin.AroundVoxel + x * spacing;
            int vertical = globalOrigin.VerticalVoxel + y * spacing;
            int across = globalOrigin.AcrossVoxel + z * spacing;
            densities[index] = snapshot.SampleDensity(around, vertical, across);
            materials[index] = snapshot.SampleNaturalMaterial(around, vertical, across);
            index++;
        }
        return new RingTerrainCoarseSdfChunk(globalOrigin, spacing, densities, materials);
    }
}