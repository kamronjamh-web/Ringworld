using System;
using System.Buffers;
using Godot;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// T4-4a (docs/work-orders/terrain-vegetation/T4.md, design D5): the 64 m convexity C64 per proxy vertex, in worlds with ground.surface only.
/// Nodes sit on the global 32 m lattice, so adjacent sectors agree; a vertex takes the bilinear value of the four nodes of its 32 m cell.
/// Levels of 128 m and coarser store nothing. Generation 1 never reaches this code (BuildsGroundRelief is false), so its data and cost are unchanged.
/// </summary>
public partial class RingTerrainProxySector
{
    /// <summary>The finest-to-coarsest spacing that stores C64 (D5): 64 m and finer.</summary>
    internal const int GroundReliefMaxSpacingMeters = 64;

    /// <summary>The slot payload range: snorm16 of C64 / this many metres, 1 mm steps (D5).</summary>
    internal const float GroundReliefSlotRangeMeters = 32.0f;

    /// <summary>Reach of the stencil beyond a sector: the 64 m tap of a lattice node, which the GPU router keeps inside the ring (GpuSectorRoutable).</summary>
    internal const int GroundReliefReachMeters = 64;
    public const int GroundReliefReachMetersPublic = GroundReliefReachMeters;

    /// <summary>True when this sampler's world has ground.surface and the level stores C64 (D5).</summary>
    internal static bool BuildsGroundRelief(RingTerrainSampler sampler, int spacing)
        => spacing <= GroundReliefMaxSpacingMeters && (sampler.WorldFeatureMask & (1 << RingGroundSurfaceModel.SurfaceBit)) != 0;

    internal static ushort EncodeReliefSnorm16(float c64)
        => (ushort)(short)MathF.Round(Math.Clamp(c64 / GroundReliefSlotRangeMeters, -1.0f, 1.0f) * 32767.0f);

    /// <summary>Test hook: the metres a slot's snorm16 payload stands for.</summary>
    public static float DecodeReliefSnorm16Public(uint bits) => DecodeReliefSnorm16(bits);

    internal static float DecodeReliefSnorm16(uint bits)
        => MathF.Max((short)(bits & 0xFFFFu) / 32767.0f, -1.0f) * GroundReliefSlotRangeMeters;

    /// <summary>
    /// C64 per vertex of the terrain lattice (row-major, cells + 1 per row), followed by zeros up to <paramref name="totalVertexCount"/> (water, clipped water and skirt vertices store 0).
    /// Node heights come from the same height source the level uses for its vertices and are never cached on disk: the lattice nodes inside the sector are the vertices' own samples,
    /// the rest are at most two nodes (64 m) outside, in a cross around each node, fetched height-only.
    /// </summary>
    private static float[]? BuildSectorRelief(TerrainBuildSampleCache cache, RingTerrainSectorCoordinate coordinate, int size, int spacing, int cells,
        bool alignSurfaceToTerrainSdf, int totalVertexCount)
    {
        const int lattice = (int)RingGroundSurfaceModel.ReliefLatticeM;
        if (size % lattice != 0 || spacing > lattice * 2 || lattice % Math.Min(spacing, lattice) != 0) return null;
        int row = cells + 1;
        var relief = new float[Math.Max(totalVertexCount, row * row)];
        long around0 = coordinate.AroundSector * size;
        int across0 = coordinate.AcrossSector * size;
        int nodes = size / lattice;
        int width = nodes + 5; // node indices -2 .. nodes + 2
        float[] nodeH = ArrayPool<float>.Shared.Rent(width * width);
        float[] nodeC = ArrayPool<float>.Shared.Rent(width * width);
        try
        {
            Array.Fill(nodeH, float.NaN, 0, width * width);
            Array.Fill(nodeC, float.NaN, 0, width * width);

            float H(int i, int j)
            {
                ref float slot = ref nodeH[(j + 2) * width + (i + 2)];
                if (float.IsNaN(slot))
                    slot = GetTerrainRenderHeight(cache.GetHeightOnly(around0 + (long)lattice * i, across0 + lattice * j), alignSurfaceToTerrainSdf);
                return slot;
            }

            float C(int i, int j)
            {
                ref float slot = ref nodeC[(j + 2) * width + (i + 2)];
                if (float.IsNaN(slot))
                    slot = (float)((double)H(i, j) - 0.25 * ((double)H(i + 2, j) + H(i - 2, j) + H(i, j + 2) + H(i, j - 2)));
                return slot;
            }

            for (int z = 0; z <= cells; z++)
            {
                int offsetZ = z * spacing;
                int nodeZ = offsetZ / lattice;
                float fz = (offsetZ - nodeZ * lattice) / (float)lattice;
                for (int x = 0; x <= cells; x++)
                {
                    int offsetX = x * spacing;
                    int nodeX = offsetX / lattice;
                    float fx = (offsetX - nodeX * lattice) / (float)lattice;
                    float v00 = C(nodeX, nodeZ);
                    float v10 = fx > 0.0f ? C(nodeX + 1, nodeZ) : 0.0f;
                    float v01 = fz > 0.0f ? C(nodeX, nodeZ + 1) : 0.0f;
                    float v11 = fx > 0.0f && fz > 0.0f ? C(nodeX + 1, nodeZ + 1) : 0.0f;
                    relief[z * row + x] = (v00 * (1.0f - fx) + v10 * fx) * (1.0f - fz) + (v01 * (1.0f - fx) + v11 * fx) * fz;
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(nodeH);
            ArrayPool<float>.Shared.Return(nodeC);
        }
        return relief;
    }

    /// <summary>
    /// The CUSTOM0 array of a mesh install and its format. Generation 1 sectors (no relief) keep today's RgbFloat chord on runtime-chord sectors and no CUSTOM0 otherwise;
    /// sectors with relief carry RgbaFloat (xyz = chord or 0, w = C64).
    /// </summary>
    private static float[]? BuildCustom0(Vector3[] chordOffsets, bool usesRuntimeParentChord, float[]? relief, out Mesh.ArrayFormat format)
    {
        format = 0;
        if (relief != null && relief.Length == chordOffsets.Length)
        {
            var custom4 = new float[chordOffsets.Length * 4];
            for (int i = 0; i < chordOffsets.Length; i++)
            {
                if (usesRuntimeParentChord) { custom4[i * 4] = chordOffsets[i].X; custom4[i * 4 + 1] = chordOffsets[i].Y; custom4[i * 4 + 2] = chordOffsets[i].Z; }
                custom4[i * 4 + 3] = relief[i];
            }
            format = (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
            return custom4;
        }
        if (!usesRuntimeParentChord) return null;
        var custom = new float[chordOffsets.Length * 3];
        for (int i = 0; i < chordOffsets.Length; i++)
        { custom[i * 3] = chordOffsets[i].X; custom[i * 3 + 1] = chordOffsets[i].Y; custom[i * 3 + 2] = chordOffsets[i].Z; }
        format = (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
        return custom;
    }
}
