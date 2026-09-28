using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Test-only access to the real raw-sector builder.  It deliberately records
/// the current array contract before any buffer ownership refactor.
/// </summary>
public partial class RingTerrainProxySector
{
    public readonly record struct AllocationFixtureSnapshot(
        long AllocatedBytes,
        long EstimatedBytes,
        int VertexCount,
        int NormalCount,
        int ColorCount,
        int UvCount,
        int MorphCount,
        int ParentChordCount,
        int IndexCount,
        int OceanIndexCount,
        int ShorelineByteCount,
        ulong GeometryHash,
        ulong ColorHash,
        ulong UvHash,
        ulong MorphHash,
        ulong ParentChordHash,
        ulong IndexHash,
        ulong OceanIndexHash,
        ulong ShorelineHash);

    public sealed class AllocationFixtureSession
    {
        private readonly RingLodSurfaceEditSnapshot _edits;
        private readonly RingTerrainSampler _sampler;
        private readonly TerrainBuildSampleCache _cache;
        private readonly RingLodGeometrySnapshot _geometry;
        private readonly int _ringWidth;

        internal AllocationFixtureSession(
            RingLodSurfaceEditSnapshot edits,
            RingTerrainSampler sampler,
            RingLodGeometrySnapshot geometry,
            int ringWidth)
        {
            _edits = edits;
            _sampler = sampler;
            _cache = new TerrainBuildSampleCache(edits, sampler, ringWidth);
            _geometry = geometry;
            _ringWidth = ringWidth;
        }

        public AllocationFixtureSnapshot Build(
            RingTerrainSectorCoordinate coordinate,
            int cells,
            bool buildSkirts)
        {
            const int spacing = 2;
            int size = cells * spacing;
            long before = GC.GetAllocatedBytesForCurrentThread();
            SectorData data = BuildRawSector(
                requestVersion: 1,
                coordinate: coordinate,
                editSnapshot: _edits,
                sampler: _sampler,
                sampleCache: _cache,
                geometry: _geometry,
                ownerInverse: Transform3D.Identity,
                size: size,
                spacing: spacing,
                cells: cells,
                underlay: 0.0f,
                parentUnderlay: 0.0f,
                parentRestoresWaterUnderlay: false,
                ringWidth: _ringWidth,
                buildWalls: false,
                buildSkirts: buildSkirts,
                skirtDepth: 32.0f,
                blocky: false,
                alignSurfaceToTerrainSdf: false,
                heightQuantum: 1,
                low: Colors.Black,
                high: Colors.White,
                coverageMask: null);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            return Snapshot(data, allocated);
        }
    }

    public static AllocationFixtureSession CreateAllocationFixtureSession(
        RingLodSurfaceEditSnapshot edits,
        RingTerrainSampler sampler,
        RingLodGeometrySnapshot geometry,
        int ringWidth)
        => new(edits, sampler, geometry, ringWidth);

    private static AllocationFixtureSnapshot Snapshot(SectorData data, long allocatedBytes)
    {
        byte[] shoreline = data.ShorelineTile?.InteriorBytes ?? Array.Empty<byte>();
        return new AllocationFixtureSnapshot(
            allocatedBytes,
            data.EstimatedBytes,
            data.Vertices.Length,
            data.Normals.Length,
            data.Colors.Length,
            data.Uvs.Length,
            data.MorphData.Length,
            data.ParentChordOffsets.Length,
            data.Indices.Length,
            data.OceanIndices.Length,
            shoreline.Length,
            Hash(data.Vertices, data.Normals),
            Hash(data.Colors),
            Hash(data.Uvs),
            Hash(data.MorphData),
            Hash(data.ParentChordOffsets),
            Hash(data.Indices),
            Hash(data.OceanIndices),
            Hash(shoreline));
    }

    private const ulong HashOffset = 14695981039346656037UL;
    private const ulong HashPrime = 1099511628211UL;

    private static ulong Hash(params Vector3[][] arrays)
    {
        ulong hash = HashOffset;
        foreach (Vector3[] array in arrays)
        {
            Add(ref hash, array.Length);
            foreach (Vector3 value in array)
            {
                Add(ref hash, BitConverter.SingleToInt32Bits(value.X));
                Add(ref hash, BitConverter.SingleToInt32Bits(value.Y));
                Add(ref hash, BitConverter.SingleToInt32Bits(value.Z));
            }
        }
        return hash;
    }

    private static ulong Hash(Color[] values)
    {
        ulong hash = HashOffset;
        Add(ref hash, values.Length);
        foreach (Color value in values)
        {
            Add(ref hash, BitConverter.SingleToInt32Bits(value.R));
            Add(ref hash, BitConverter.SingleToInt32Bits(value.G));
            Add(ref hash, BitConverter.SingleToInt32Bits(value.B));
            Add(ref hash, BitConverter.SingleToInt32Bits(value.A));
        }
        return hash;
    }

    private static ulong Hash(Vector2[] values)
    {
        ulong hash = HashOffset;
        Add(ref hash, values.Length);
        foreach (Vector2 value in values)
        {
            Add(ref hash, BitConverter.SingleToInt32Bits(value.X));
            Add(ref hash, BitConverter.SingleToInt32Bits(value.Y));
        }
        return hash;
    }

    private static ulong Hash(int[] values)
    {
        ulong hash = HashOffset;
        Add(ref hash, values.Length);
        foreach (int value in values) Add(ref hash, value);
        return hash;
    }

    private static ulong Hash(byte[] values)
    {
        ulong hash = HashOffset;
        Add(ref hash, values.Length);
        foreach (byte value in values) hash = (hash ^ value) * HashPrime;
        return hash;
    }

    private static void Add(ref ulong hash, int value)
    {
        unchecked
        {
            uint bits = (uint)value;
            hash = (hash ^ (byte)bits) * HashPrime;
            hash = (hash ^ (byte)(bits >> 8)) * HashPrime;
            hash = (hash ^ (byte)(bits >> 16)) * HashPrime;
            hash = (hash ^ (byte)(bits >> 24)) * HashPrime;
        }
    }
}
