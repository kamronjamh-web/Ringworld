using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Vertex-table infrastructure for proxy sectors (docs/gpu-offload-plan.md section 3, WO-S0). A sector slot is one
/// layer of three per-level array textures that the shared grid meshes fetch by VERTEX_ID in
/// materials/ring_terrain_proxy_vertex_fetch.gdshaderinc. Nothing here runs unless a level asks for a pool:
/// the shaders keep proxy_vertex_table_enabled false and no installed sector reads a slot until WO-S1.
/// </summary>
public partial class RingTerrainProxySector
{
    private static long _vertexTableReservedBytes;

    /// <summary>
    /// Internal install-backend flag. On by default and by design not a menu setting; the debug user arg
    /// <c>--proxy-install=mesh</c> turns it off for A/B work. WO-S0 itself never consults it.
    /// </summary>
    internal static bool VertexTableInstallEnabled { get; set; } = Array.IndexOf(OS.GetCmdlineUserArgs(), "--proxy-install=mesh") < 0;

    /// <summary>Debug A/B for GPU cost: <c>--proxy-slot-collapse=on</c> enables the vertex-stage covered-quad collapse. Off by default: on the 4070 it cost 0.05-0.1 ms stationary and about 0.2 ms in flight, while the fragment atlas discard alone already hides covered quads.</summary>
    internal static bool VertexCollapseEnabled { get; set; } = Array.IndexOf(OS.GetCmdlineUserArgs(), "--proxy-slot-collapse=on") >= 0;

    /// <summary>Bytes of vertex-table texture memory reserved by every live pool. Zero until a level allocates one.</summary>
    public static long VertexTableReservedBytes => Interlocked.Read(ref _vertexTableReservedBytes);

    private VertexTablePool? _vertexTable;

    /// <summary>This level's slot pool, or null while it has not been allocated (the flag-off state).</summary>
    internal VertexTablePool? VertexTable => _vertexTable;

    /// <summary>Slots a level needs: its streaming window, the warm cache and in-flight installs.</summary>
    internal static int VertexTableCapacityFor(int radiusAround, int radiusAcross, int warmSectors)
        => (2 * radiusAround + 1) * (2 * radiusAcross + 1) + Math.Max(0, warmSectors) + 8;

    /// <summary>Allocates (or reallocates on a layout change) this level's slot pool. Lazy: nothing calls it with the flag off.</summary>
    internal VertexTablePool? EnsureVertexTable(bool hasChord, int minimumCapacity = 0, bool gpuWritable = false)
    {
        int cells = Math.Max(1, SectorSizeMeters / Math.Max(1, SampleSpacingMeters));
        int capacity = minimumCapacity > 0 ? minimumCapacity : VertexTableCapacityFor(SectorRadiusAround, SectorRadiusAcross, MaximumWarmSectorCount);
        // Growth only: a pool that is large enough is kept, because reallocating drops every installed slot.
        if (_vertexTable != null && _vertexTable.Cells == cells && _vertexTable.Capacity >= capacity && _vertexTable.HasChord == hasChord
            && (_vertexTable.GpuWritable || !gpuWritable))
            return _vertexTable;
        DisposeVertexTable();
        if (RenderingServer.GetRenderingDevice() == null) return null;
        _vertexTable = new VertexTablePool(cells, capacity, hasChord, gpuWritable);
        GD.Print($"{LodLabel} vertex table pool: capacity={capacity} slots, {_vertexTable.BytesPerSlot / 1024} KiB/slot, reserved={_vertexTable.ReservedBytes / (1024.0 * 1024.0):0.0} MiB (radius {SectorRadiusAround}x{SectorRadiusAcross}, warm {MaximumWarmSectorCount})");
        return _vertexTable;
    }

    private void DisposeVertexTable()
    {
        _vertexTable?.Dispose();
        _vertexTable = null;
    }

    /// <summary>Texture geometry of one slot, W = cells + 1 texels wide. See the include file for the row map.</summary>
    internal readonly record struct VertexTableLayout(int Cells)
    {
        public const int SkirtEdges = 4;
        public int Width => Cells + 1;
        public int LatticeVertices => Width * Width;
        /// <summary>Fixed skirt capacity: 4 edges x Cells quads x 4 vertices.</summary>
        public int SkirtVertices => SkirtEdges * Cells * 4;
        public int SkirtRows => (SkirtVertices + Width - 1) / Width;
        public int Height => 2 * Width + SkirtRows;
        public int Texels => Width * Height;
        public int BytesPerBlock => Texels * 16;
        /// <summary>Texel index of skirt-block vertex k (the layout is linear: terrain, water, then skirts).</summary>
        public int SkirtTexel(int k) => 2 * LatticeVertices + k;
    }

    /// <summary>The three byte blocks of one slot, ready for a render-thread texture update.</summary>
    public sealed class VertexTableSlotBlocks
    {
        public required byte[] PosMorph { get; init; }
        public byte[]? Chord { get; init; }
        public required byte[] Attr { get; init; }
        public long Bytes => PosMorph.LongLength + (Chord?.LongLength ?? 0L) + Attr.LongLength;
    }

    /// <summary>Per-level pool of vertex-table slots: three array textures plus a free list.</summary>
    internal sealed class VertexTablePool : IDisposable
    {
        private readonly Stack<int> _free = new();
        private readonly bool[] _used;
        private readonly Texture2DArrayRD _posTexture = new();
        private readonly Texture2DArrayRD? _chordTexture;
        private readonly Texture2DArrayRD _attrTexture = new();
        private Rid _posRid, _chordRid, _attrRid;
        private volatile bool _ready;
        private volatile bool _disposed;
        private readonly long _reservedBytes;

        private static int _nextGeneration;

        public VertexTablePool(int cells, int capacity, bool hasChord, bool gpuWritable = false)
        {
            GpuWritable = gpuWritable;
            Generation = Interlocked.Increment(ref _nextGeneration);
            Layout = new VertexTableLayout(Math.Max(1, cells));
            Capacity = Math.Max(1, capacity);
            HasChord = hasChord;
            _chordTexture = hasChord ? new Texture2DArrayRD() : null;
            _used = new bool[Capacity];
            for (int slot = Capacity - 1; slot >= 0; slot--) _free.Push(slot);
            _reservedBytes = (long)Layout.BytesPerBlock * Capacity * (hasChord ? 3 : 2);
            Interlocked.Add(ref _vertexTableReservedBytes, _reservedBytes);
            RenderingServer.CallOnRenderThread(Callable.From(CreateOnRenderThread));
        }

        public VertexTableLayout Layout { get; }
        /// <summary>Slot textures carry the storage usage bit so the GPU height backend can write them from compute (docs/gpu-offload-plan.md WO-G2).</summary>
        public bool GpuWritable { get; }
        /// <summary>Unique per pool instance: a job or sector that outlives its pool must not touch another pool's slots.</summary>
        public int Generation { get; }
        public int Cells => Layout.Cells;
        public int Capacity { get; }
        public bool HasChord { get; }
        public int FreeSlots => _free.Count;
        public int UsedSlots => Capacity - _free.Count;
        public long ReservedBytes => _reservedBytes;
        public long BytesPerSlot => (long)Layout.BytesPerBlock * (HasChord ? 3 : 2);
        /// <summary>True once the render thread has created the textures. Uploads queued earlier run after creation.</summary>
        public bool Ready => _ready;
        public Texture2DArrayRD PosTexture => _posTexture;
        public Texture2DArrayRD? ChordTexture => _chordTexture;
        public Texture2DArrayRD AttrTexture => _attrTexture;
        public Rid PosRid => _posRid;
        public Rid ChordRid => _chordRid;
        public Rid AttrRid => _attrRid;

        /// <summary>Main thread only.</summary>
        public bool TryAcquire(out int slot)
        {
            if (_free.Count == 0) { slot = -1; return false; }
            slot = _free.Pop();
            _used[slot] = true;
            return true;
        }

        /// <summary>
        /// Main thread only. A released slot may be re-acquired in the same frame: texture updates are recorded in
        /// queue order, so frames in flight that still read the old content complete first.
        /// </summary>
        public void Release(int slot)
        {
            if (slot < 0 || slot >= Capacity || !_used[slot]) return;
            _used[slot] = false;
            _free.Push(slot);
        }

        /// <summary>Queues the slot's blocks for the render thread. The arrays are not copied; do not mutate them afterwards.</summary>
        public void Upload(int slot, VertexTableSlotBlocks blocks)
        {
            if (slot < 0 || slot >= Capacity || _disposed) return;
            RenderingServer.CallOnRenderThread(Callable.From(() => UploadOnRenderThread(slot, blocks)));
        }

        private void CreateOnRenderThread()
        {
            if (_disposed) return;
            RenderingDevice? rd = RenderingServer.GetRenderingDevice();
            if (rd == null) return;
            _posRid = CreateArray(rd, RenderingDevice.DataFormat.R32G32B32A32Sfloat);
            // RGBA32F carrying raw uint32 bits: Texture2DArrayRD cannot wrap integer formats (no Godot Image format for them).
            _attrRid = CreateArray(rd, RenderingDevice.DataFormat.R32G32B32A32Sfloat);
            if (HasChord) _chordRid = CreateArray(rd, RenderingDevice.DataFormat.R32G32B32A32Sfloat);
            _posTexture.TextureRdRid = _posRid;
            _attrTexture.TextureRdRid = _attrRid;
            if (_chordTexture != null) _chordTexture.TextureRdRid = _chordRid;
            _ready = true;
        }

        private Rid CreateArray(RenderingDevice rd, RenderingDevice.DataFormat dataFormat)
        {
            var format = new RDTextureFormat
            {
                TextureType = RenderingDevice.TextureType.Type2DArray,
                Format = dataFormat,
                Width = (uint)Layout.Width,
                Height = (uint)Layout.Height,
                ArrayLayers = (uint)Capacity,
                Mipmaps = 1,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
                    | RenderingDevice.TextureUsageBits.CanUpdateBit
                    | RenderingDevice.TextureUsageBits.CanCopyFromBit
                    | (GpuWritable ? RenderingDevice.TextureUsageBits.StorageBit : 0),
            };
            return rd.TextureCreate(format, new RDTextureView());
        }

        private void UploadOnRenderThread(int slot, VertexTableSlotBlocks blocks)
        {
            if (_disposed || !_ready) return;
            RenderingDevice? rd = RenderingServer.GetRenderingDevice();
            if (rd == null) return;
            rd.TextureUpdate(_posRid, (uint)slot, blocks.PosMorph);
            rd.TextureUpdate(_attrRid, (uint)slot, blocks.Attr);
            if (HasChord && blocks.Chord != null) rd.TextureUpdate(_chordRid, (uint)slot, blocks.Chord);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Add(ref _vertexTableReservedBytes, -_reservedBytes);
            RenderingServer.CallOnRenderThread(Callable.From(FreeOnRenderThread));
        }

        private void FreeOnRenderThread()
        {
            _ready = false;
            _posTexture.TextureRdRid = default;
            _attrTexture.TextureRdRid = default;
            if (_chordTexture != null) _chordTexture.TextureRdRid = default;
            RenderingDevice? rd = RenderingServer.GetRenderingDevice();
            if (rd != null)
                foreach (Rid rid in new[] { _posRid, _attrRid, _chordRid })
                    if (rid.IsValid) rd.FreeRid(rid);
            _posRid = default; _attrRid = default; _chordRid = default;
        }
    }

    /// <summary>Binds a pool's textures on a terrain, shadow or ocean proxy material and sets the flag uniform.</summary>
    internal static void ApplyVertexTableUniforms(ShaderMaterial material, VertexTablePool? pool, bool enabled)
    {
        bool active = enabled && pool != null;
        material.SetShaderParameter("proxy_vertex_table_enabled", active);
        if (pool == null) return;
        material.SetShaderParameter("proxy_vertex_table_pos", pool.PosTexture);
        material.SetShaderParameter("proxy_vertex_table_attr", pool.AttrTexture);
        material.SetShaderParameter("proxy_vertex_table_has_chord", pool.HasChord);
        if (pool.ChordTexture != null) material.SetShaderParameter("proxy_vertex_table_chord", pool.ChordTexture);
    }

    // ---- Shared grid meshes ---------------------------------------------------------------------------------

    /// <summary>Terrain lattice triangles (all cells) followed by the fixed skirt quads. Vertex ids as in the include file.</summary>
    internal static int[] BuildTerrainGridIndices(int cells, bool positiveDiagonal)
    {
        var layout = new VertexTableLayout(cells);
        int row = layout.Width;
        var indices = new List<int>(cells * cells * 6 + VertexTableLayout.SkirtEdges * cells * 6);
        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
        {
            int i00 = z * row + x, i10 = i00 + 1, i01 = i00 + row, i11 = i01 + 1;
            if (positiveDiagonal)
            {
                indices.Add(i00); indices.Add(i11); indices.Add(i10);
                indices.Add(i00); indices.Add(i01); indices.Add(i11);
            }
            else
            {
                indices.Add(i01); indices.Add(i10); indices.Add(i00);
                indices.Add(i10); indices.Add(i01); indices.Add(i11);
            }
        }
        for (int edge = 0; edge < VertexTableLayout.SkirtEdges; edge++)
        {
            // Same winding as AppendSectorSkirts: the first around edge and the last across edge are reversed.
            bool reverse = edge is 0 or 3;
            for (int quad = 0; quad < cells; quad++)
            {
                int start = layout.LatticeVertices + edge * cells * 4 + quad * 4;
                if (reverse)
                {
                    indices.Add(start); indices.Add(start + 1); indices.Add(start + 3);
                    indices.Add(start + 1); indices.Add(start + 2); indices.Add(start + 3);
                }
                else
                {
                    indices.Add(start + 3); indices.Add(start + 1); indices.Add(start);
                    indices.Add(start + 1); indices.Add(start + 3); indices.Add(start + 2);
                }
            }
        }
        return indices.ToArray();
    }

    /// <summary>Water lattice triangles. The worker always uses the legacy diagonal for water, whatever the terrain uses.</summary>
    internal static int[] BuildWaterGridIndices(int cells)
    {
        int row = cells + 1;
        var indices = new int[cells * cells * 6];
        int n = 0;
        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
        {
            int i00 = z * row + x, i10 = i00 + 1, i01 = i00 + row, i11 = i01 + 1;
            indices[n++] = i01; indices[n++] = i10; indices[n++] = i00;
            indices[n++] = i10; indices[n++] = i01; indices[n++] = i11;
        }
        return indices;
    }

    /// <summary>Index-only mesh: the VERTEX array is a dummy, the vertex shader overwrites it from the slot.</summary>
    internal static ArrayMesh BuildTerrainGridMesh(int cells, bool positiveDiagonal)
    {
        var layout = new VertexTableLayout(cells);
        return BuildIndexOnlyMesh(layout.LatticeVertices + layout.SkirtVertices, BuildTerrainGridIndices(cells, positiveDiagonal));
    }

    internal static ArrayMesh BuildWaterGridMesh(int cells)
        => BuildIndexOnlyMesh(new VertexTableLayout(cells).LatticeVertices, BuildWaterGridIndices(cells));

    private static ArrayMesh BuildIndexOnlyMesh(int vertexCount, int[] indices)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[vertexCount];
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // ---- Worker packer --------------------------------------------------------------------------------------

    /// <summary>The worker parameters that decide which skirt edges AppendSectorSkirts emitted (and how many quads each has).</summary>
    internal readonly record struct SkirtPlan(int SectorSize, int RingWidth, bool StructuralWalls);

    /// <summary>
    /// Maps every skirt vertex of the worker arrays to its texel in the fixed skirt block (or null when the layout
    /// does not fit). The edge order and quad counts replay AppendSectorSkirts: around start and end (one quad per
    /// spacing across the ring-clipped range), then across start and end (Cells quads each, omitted at structural
    /// walls). Omitted edges and clipped quads leave their slots unmapped (zero-area quads in the packed slot).
    /// </summary>
    private static int[]? ComputeSkirtTexelMap(SectorData data, int cells, SkirtPlan plan, out int skirtCount)
    {
        skirtCount = 0;
        var layout = new VertexTableLayout(cells);
        int start = data.SkirtVertexStart;
        int lattice2 = layout.LatticeVertices * 2;
        var map = new int[data.Vertices.Length];
        Array.Fill(map, -1);
        for (int i = 0; i < Math.Min(lattice2, map.Length); i++) map[i] = i;
        if (start < 0 || start >= data.Vertices.Length) return map;
        if (start < lattice2 || (data.Vertices.Length - start) % 4 != 0) return null;
        int spacing = Math.Max(1, plan.SectorSize / cells);
        long acrossStart = Math.Max(0, (long)data.Coordinate.AcrossSector * plan.SectorSize);
        long acrossEnd = Math.Min(plan.RingWidth, (long)data.Coordinate.AcrossSector * plan.SectorSize + plan.SectorSize);
        if (acrossEnd <= acrossStart) return start == data.Vertices.Length ? map : null;
        int aroundQuads = (int)((acrossEnd - acrossStart + spacing - 1) / spacing);
        Span<int> quadsPerEdge = stackalloc int[VertexTableLayout.SkirtEdges]
        {
            aroundQuads, aroundQuads,
            !plan.StructuralWalls || acrossStart > 0 ? cells : 0,
            !plan.StructuralWalls || acrossEnd < plan.RingWidth ? cells : 0,
        };
        int expectedQuads = quadsPerEdge[0] + quadsPerEdge[1] + quadsPerEdge[2] + quadsPerEdge[3];
        if ((data.Vertices.Length - start) / 4 != expectedQuads) return null;
        int s = start;
        for (int edge = 0; edge < VertexTableLayout.SkirtEdges; edge++)
        for (int quad = 0; quad < quadsPerEdge[edge]; quad++, s += 4)
        {
            if (quad >= cells) return null;
            for (int v = 0; v < 4; v++)
                map[s + v] = layout.SkirtTexel(edge * cells * 4 + quad * 4 + v);
        }
        skirtCount = data.Vertices.Length - start;
        return map;
    }

    /// <summary>
    /// Packs worker arrays into slot blocks: pos_morph (xyz position, w radial morph), chord (runtime parent chord
    /// xyz, only on runtime-chord sectors) and attr (oct16 normal, RGBA8 colour, flags, skirt UV as two halves).
    /// Returns null for sectors the table cannot hold (blocky sectors, unexpected skirt layouts).
    /// </summary>
    private static VertexTableSlotBlocks? PackVertexTableSlot(SectorData data, int cells, SkirtPlan plan)
    {
        var layout = new VertexTableLayout(cells);
        int lattice2 = layout.LatticeVertices * 2;
        if (data.Vertices.Length < lattice2 || data.Normals.Length != data.Vertices.Length
            || data.Colors.Length != data.Vertices.Length || data.MorphData.Length != data.Vertices.Length
            || data.Uvs.Length != data.Vertices.Length)
            return null;
        int[]? map = ComputeSkirtTexelMap(data, cells, plan, out _);
        if (map == null) return null;
        bool hasChord = data.UsesRuntimeParentChord && data.ParentChordOffsets.Length == data.Vertices.Length;
        byte[] posBytes = new byte[layout.BytesPerBlock];
        byte[] attrBytes = new byte[layout.BytesPerBlock];
        byte[]? chordBytes = hasChord ? new byte[layout.BytesPerBlock] : null;
        Span<float> pos = MemoryMarshal.Cast<byte, float>(posBytes.AsSpan());
        Span<uint> attr = MemoryMarshal.Cast<byte, uint>(attrBytes.AsSpan());
        Span<float> chord = chordBytes != null ? MemoryMarshal.Cast<byte, float>(chordBytes.AsSpan()) : default;
        for (int i = 0; i < map.Length; i++)
        {
            int texel = map[i];
            if (texel < 0) continue;
            bool skirt = i >= lattice2;
            Vector3 vertex = data.Vertices[i];
            pos[texel * 4] = vertex.X; pos[texel * 4 + 1] = vertex.Y; pos[texel * 4 + 2] = vertex.Z;
            pos[texel * 4 + 3] = data.MorphData[i].X;
            attr[texel * 4] = EncodeOctNormal16(data.Normals[i]);
            attr[texel * 4 + 1] = EncodeColor8(data.Colors[i]);
            uint skirtBits = 0u, skirtWord = 0u;
            if (skirt && !TryEncodeSkirtUv(data.Uvs[i], cells, out skirtBits, out skirtWord)) return null;
            attr[texel * 4 + 2] = (data.MorphData[i].Y >= 0.5f ? 1u : 0u) | (skirt ? 2u : 0u) | skirtBits;
            attr[texel * 4 + 3] = skirtWord;
            if (chordBytes != null)
            {
                Vector3 offset = data.ParentChordOffsets[i];
                chord[texel * 4] = offset.X; chord[texel * 4 + 1] = offset.Y; chord[texel * 4 + 2] = offset.Z;
            }
        }
        return new VertexTableSlotBlocks { PosMorph = posBytes, Chord = chordBytes, Attr = attrBytes };
    }

    internal static uint EncodeOctNormal16(Vector3 normal)
    {
        float l1 = MathF.Abs(normal.X) + MathF.Abs(normal.Y) + MathF.Abs(normal.Z);
        if (!(l1 > 1.0e-12f)) return 0u;
        float x = normal.X / l1, y = normal.Y / l1;
        if (normal.Z < 0.0f)
        {
            float fx = (1.0f - MathF.Abs(y)) * (x >= 0.0f ? 1.0f : -1.0f);
            float fy = (1.0f - MathF.Abs(x)) * (y >= 0.0f ? 1.0f : -1.0f);
            x = fx; y = fy;
        }
        return (uint)(ushort)ToSnorm16(x) | ((uint)(ushort)ToSnorm16(y) << 16);
    }

    internal static Vector3 DecodeOctNormal16(uint packed)
    {
        float x = FromSnorm16((short)(packed & 0xFFFFu)), y = FromSnorm16((short)(packed >> 16));
        float z = 1.0f - MathF.Abs(x) - MathF.Abs(y);
        if (z < 0.0f)
        {
            float fx = (1.0f - MathF.Abs(y)) * (x >= 0.0f ? 1.0f : -1.0f);
            float fy = (1.0f - MathF.Abs(x)) * (y >= 0.0f ? 1.0f : -1.0f);
            x = fx; y = fy;
        }
        var v = new Vector3(x, y, z);
        float length = v.Length();
        return length > 0.0f ? v / length : new Vector3(0.0f, 0.0f, 1.0f);
    }

    private static short ToSnorm16(float value) => (short)MathF.Round(Math.Clamp(value, -1.0f, 1.0f) * 32767.0f);
    private static float FromSnorm16(short value) => MathF.Max(value / 32767.0f, -1.0f);

    /// <summary>RGBA8 exactly as Godot quantises a mesh COLOR array (unorm, truncated).</summary>
    internal static uint EncodeColor8(Color color)
        => ColorByte(color.R) | (ColorByte(color.G) << 8) | (ColorByte(color.B) << 16) | (ColorByte(color.A) << 24);

    private static uint ColorByte(float channel) => (uint)Math.Clamp((int)(channel * 255.0f), 0, 255);

    /// <summary>
    /// Skirt UV, bit exact. One component is always a lattice value k / cells (the other runs along a ring-clipped or
    /// edge-constant range and may be any float): z bit 2 says which one is the lattice component (0 = x), z bits
    /// 16..31 hold k, and w holds the other component's float bits.
    /// </summary>
    internal static bool TryEncodeSkirtUv(Vector2 uv, int cells, out uint zBits, out uint wBits)
    {
        if (TryLatticeIndex(uv.X, cells, out uint kx))
        {
            zBits = kx << 16; wBits = (uint)BitConverter.SingleToInt32Bits(uv.Y);
            return true;
        }
        if (TryLatticeIndex(uv.Y, cells, out uint ky))
        {
            zBits = 4u | (ky << 16); wBits = (uint)BitConverter.SingleToInt32Bits(uv.X);
            return true;
        }
        zBits = 0u; wBits = 0u;
        return false;
    }

    internal static Vector2 DecodeSkirtUv(uint z, uint w, int cells)
    {
        float lattice = (z >> 16) / (float)cells;
        float other = BitConverter.Int32BitsToSingle((int)w);
        return (z & 4u) == 0u ? new Vector2(lattice, other) : new Vector2(other, lattice);
    }

    private static bool TryLatticeIndex(float value, int cells, out uint k)
    {
        float scaled = value * cells;
        k = 0u;
        if (!(scaled >= 0.0f && scaled <= cells)) return false;
        k = (uint)MathF.Round(scaled);
        return k <= (uint)cells && BitConverter.SingleToInt32Bits(k / (float)cells) == BitConverter.SingleToInt32Bits(value);
    }

    // ---- Slot install (WO-S1) -------------------------------------------------------------------------------

    private static readonly StringName ProxySlotName = "proxy_slot";
    private static readonly StringName CoverageAtlasRectName = "coverage_atlas_rect";
    private static readonly StringName OceanReplacementRectName = "ocean_replacement_coverage_rect";
    private static readonly StringName FftDistantOceanMaskRectName = "fft_distant_ocean_mask_rect";
    private const string OceanSurfaceName = "OceanSurface";

    private bool _slotInstallActive;
    private volatile int _slotPackCells; // worker-visible pack target (cells of the live pool); 0 = workers do not pack
    private volatile bool _slotPackHasChord;
    private int _slotPoolHighWater;
    private ArrayMesh? _terrainGridMeshPositive, _terrainGridMeshLegacy, _waterGridMesh;
    private readonly Dictionary<RingTerrainSectorCoordinate, int> _activeSlots = new();
    private readonly Dictionary<MeshInstance3D, MeshInstance3D> _oceanChildren = new();
    private readonly Dictionary<MeshInstance3D, float> _oceanChildReach = new();

    /// <summary>
    /// The fine levels, where installs are frequent: 2, 4, 8, 16 and 32 m. Coarser levels keep mesh install because their
    /// slots would reserve VRAM for sectors that are installed rarely.
    /// </summary>
    internal static bool SpacingUsesVertexTableInstall(int spacingMeters) => spacingMeters is 2 or 4 or 8 or 16 or 32;

    /// <summary>Debug: <c>--proxy-slot-pool=N</c> caps every pool at N slots to exercise the mesh fallback.</summary>
    internal static int SlotPoolCapOverride { get; } = ParsePoolCap();

    private static int ParsePoolCap()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--proxy-slot-pool=", StringComparison.Ordinal) && int.TryParse(arg.AsSpan(18), out int n)) return n;
        return 0;
    }

    // Peak slots in use per level over 20 s, 250 m/s flights (open ocean and land plus coast, 4070, 7x7 windows, 48 warm):
    // 2 m 281, 4 m 192, 8 m 165, 16 m 150, 32 m 127 (max of the land and ocean runs). Pools get that peak plus 30%, capped at the full window count.
    // A pool that runs out falls back to mesh install for the sector (counted as slotFallbackInstalls); it never fails.
    internal static int SlotPoolCapacityFor(int spacingMeters, int fullWindowCapacity)
    {
        int peak = spacingMeters switch { 2 => 281, 4 => 192, 8 => 165, 16 => 150, 32 => 127, _ => fullWindowCapacity };
        int capacity = Math.Min(fullWindowCapacity, (int)Math.Ceiling(peak * 1.3));
        return SlotPoolCapOverride > 0 ? Math.Min(capacity, SlotPoolCapOverride) : capacity;
    }

    /// <summary>Slot pool bytes held by this level (zero unless it installs through slots).</summary>
    internal long VertexTableLevelReservedBytes => _vertexTable?.ReservedBytes ?? 0L;
    internal bool SlotInstallActive => _slotInstallActive;
    internal int SlotsInUse => _vertexTable?.UsedSlots ?? 0;

    /// <summary>Everything a worker pre-packs so the main-thread slot install allocates no mesh for terrain or fully-ocean water.</summary>
    internal sealed class SlotInstallPayload
    {
        public required VertexTableSlotBlocks Blocks { get; init; }
        /// <summary>GPU-built sector (docs/gpu-offload-plan.md WO-G2): K2 already wrote this slot, so install only binds it. -1 for worker-packed sectors.</summary>
        public int GpuSlot { get; init; } = -1;
        public int PoolGeneration { get; init; }
        public required int Cells { get; init; }
        public required bool HasChord { get; init; }
        /// <summary>Fully-ocean sector: the shared WaterGrid draws exactly the worker's wet triangles.</summary>
        public bool WaterGrid { get; init; }
        /// <summary>Coastal sector: water and clip vertices only, re-indexed.</summary>
        public CompactOceanArrays? CompactOcean { get; init; }
        /// <summary>Bounds of every vertex the mesh-mode install would have uploaded. The ocean child uses them as its AABB so transparent sort order matches mesh mode.</summary>
        public Aabb MeshBounds { get; init; }
        public long Bytes => Blocks.Bytes + (CompactOcean?.Bytes ?? 0L);
    }

    internal sealed class CompactOceanArrays
    {
        public required Vector3[] Vertices { get; init; }
        public required Vector3[] Normals { get; init; }
        public required Color[] Colors { get; init; }
        public required Vector2[] Uvs { get; init; }
        public required Vector2[] MorphData { get; init; }
        public required int[] Indices { get; init; }
        public long Bytes => Vertices.LongLength * 12L + Normals.LongLength * 12L + Colors.LongLength * 16L
            + Uvs.LongLength * 8L + MorphData.LongLength * 8L + Indices.LongLength * 4L;
    }

    /// <summary>
    /// Resolves whether this level installs through slots and keeps its pool and material bindings in step. Called when
    /// materials are created and whenever the level's streaming window changes. The compatibility renderer has no
    /// RenderingDevice, so it always stays on mesh install.
    /// </summary>
    internal void RefreshVertexTableInstall()
    {
        bool want = VertexTableInstallEnabled && SpacingUsesVertexTableInstall(SampleSpacingMeters)
            && !UseBlockyHeightfield && _material != null && RenderingServer.GetRenderingDevice() != null;
        if (!want)
        {
            if (_slotInstallActive || _vertexTable != null)
            {
                DropSlotInstalledSectors();
                _slotInstallActive = false;
                _slotPackCells = 0;
                DisposeVertexTable();
                _terrainGridMeshPositive = _terrainGridMeshLegacy = _waterGridMesh = null;
                BindVertexTableMaterials();
            }
            RefreshGpuHeightBackend();
            return;
        }
        bool hasChord = UsesRuntimeParentChord();
        int cells = Math.Max(1, SectorSizeMeters / Math.Max(1, SampleSpacingMeters));
        _slotPoolHighWater = Math.Max(_slotPoolHighWater,
            SlotPoolCapacityFor(SampleSpacingMeters, VertexTableCapacityFor(SectorRadiusAround, SectorRadiusAcross, MaximumWarmSectorCount)));
        bool wantGpuWritable = GpuHeightsEnabled && !_gpuBackendFailed && SpacingUsesGpuHeights(SampleSpacingMeters);
        bool needsNewPool = _vertexTable == null || _vertexTable.Cells != cells || _vertexTable.HasChord != hasChord
            || _vertexTable.Capacity < _slotPoolHighWater || (wantGpuWritable && !_vertexTable.GpuWritable);
        if (needsNewPool)
        {
            // A new pool starts empty: every sector installed through the old one must be rebuilt.
            DropSlotInstalledSectors();
            _terrainGridMeshPositive = _terrainGridMeshLegacy = _waterGridMesh = null;
            if (EnsureVertexTable(hasChord, _slotPoolHighWater, wantGpuWritable) == null)
            {
                _slotInstallActive = false;
                _slotPackCells = 0;
                BindVertexTableMaterials();
                return;
            }
        }
        _slotInstallActive = true;
        _slotPackHasChord = hasChord;
        _slotPackCells = cells;
        if (needsNewPool) BindVertexTableMaterials();
        RefreshGpuHeightBackend();
    }

    private void BindVertexTableMaterials()
    {
        bool active = _slotInstallActive;
        if (_material != null)
        {
            ApplyVertexTableUniforms(_material, _vertexTable, active);
            _material.SetShaderParameter("proxy_vertex_collapse_enabled", active && EnablePartialCoverageMeshCulling && VertexCollapseEnabled);
        }
        if (_oceanMaterial != null) ApplyVertexTableUniforms(_oceanMaterial, _vertexTable, active);
        if (_opaqueShadowMaterial != null) ApplyVertexTableUniforms(_opaqueShadowMaterial, _vertexTable, active);
    }

    private void DropSlotInstalledSectors()
    {
        if (_activeSlots.Count == 0) return;
        foreach (RingTerrainSectorCoordinate coordinate in new List<RingTerrainSectorCoordinate>(_activeSlots.Keys))
            RecycleActiveSector(coordinate);
        _activeSlots.Clear();
    }

    /// <summary>Worker thread: packs the slot blocks and the coastal ocean arrays for one finished sector, or null to install through a mesh.</summary>
    private SlotInstallPayload? BuildSlotPayload(SectorData data)
    {
        int cells = _slotPackCells;
        if (cells <= 0) return null;
        bool hasChord = _slotPackHasChord;
        int size = SectorSizeMeters, spacing = Math.Max(1, SampleSpacingMeters);
        if (data.UsesRuntimeParentChord != hasChord || size / spacing != cells) return null;
        int ringWidth = Math.Max(1, RingWidthMeters);
        VertexTableSlotBlocks? blocks = PackVertexTableSlot(data, cells, new SkirtPlan(size, ringWidth, BuildStructuralEdgeWalls));
        if (blocks == null) return null;
        bool waterGrid = false;
        CompactOceanArrays? compact = null;
        if (data.OceanIndices.Length > 0)
        {
            waterGrid = data.FullyOceanSurface && OceanIndicesAreFullGrid(data, cells, size, spacing, ringWidth);
            if (!waterGrid) compact = BuildCompactOceanArrays(data);
        }
        return new SlotInstallPayload { Blocks = blocks, Cells = cells, HasChord = hasChord, WaterGrid = waterGrid, CompactOcean = compact, MeshBounds = ComputeVertexBounds(data.Vertices) };
    }

    // A fully-ocean sector submits both wet triangles of every in-ring cell; cells outside the ring are skipped by the
    // worker but collapse to zero-area triangles in the shared grid, so the two draw the same pixels.
    private static bool OceanIndicesAreFullGrid(SectorData data, int cells, int size, int spacing, int ringWidth)
    {
        var layout = new VertexTableLayout(cells);
        int validCells = 0;
        for (int z = 0; z < cells; z++)
        {
            long across0 = (long)data.Coordinate.AcrossSector * size + (long)z * spacing;
            if (across0 + spacing > 0 && across0 < ringWidth) validCells += cells;
        }
        if (data.OceanIndices.Length != validCells * 6) return false;
        foreach (int index in data.OceanIndices)
            if (index < layout.LatticeVertices || index >= layout.LatticeVertices * 2) return false;
        return true;
    }

    private static Aabb ComputeVertexBounds(Vector3[] vertices)
    {
        if (vertices.Length == 0) return default;
        Vector3 min = vertices[0], max = vertices[0];
        for (int i = 1; i < vertices.Length; i++)
        {
            Vector3 v = vertices[i];
            min = new Vector3(MathF.Min(min.X, v.X), MathF.Min(min.Y, v.Y), MathF.Min(min.Z, v.Z));
            max = new Vector3(MathF.Max(max.X, v.X), MathF.Max(max.Y, v.Y), MathF.Max(max.Z, v.Z));
        }
        return new Aabb(min, max - min);
    }

    private static CompactOceanArrays BuildCompactOceanArrays(SectorData data)
    {
        var remap = new int[data.Vertices.Length];
        Array.Fill(remap, -1);
        var order = new List<int>(Math.Min(data.Vertices.Length, data.OceanIndices.Length));
        var indices = new int[data.OceanIndices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            int source = data.OceanIndices[i];
            int mapped = remap[source];
            if (mapped < 0) { mapped = order.Count; remap[source] = mapped; order.Add(source); }
            indices[i] = mapped;
        }
        int count = order.Count;
        var vertices = new Vector3[count];
        var normals = new Vector3[count];
        var colors = new Color[count];
        var uvs = new Vector2[count];
        var morph = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            int source = order[i];
            vertices[i] = data.Vertices[source]; normals[i] = data.Normals[source]; colors[i] = data.Colors[source];
            uvs[i] = data.Uvs[source]; morph[i] = data.MorphData[source];
        }
        return new CompactOceanArrays { Vertices = vertices, Normals = normals, Colors = colors, Uvs = uvs, MorphData = morph, Indices = indices };
    }

    private ArrayMesh GetTerrainGridMesh(int cells, bool positiveDiagonal)
    {
        ArrayMesh? mesh = positiveDiagonal ? _terrainGridMeshPositive : _terrainGridMeshLegacy;
        if (mesh != null) return mesh;
        mesh = BuildTerrainGridMesh(cells, positiveDiagonal);
        if (_material != null) mesh.SurfaceSetMaterial(0, _material);
        if (positiveDiagonal) _terrainGridMeshPositive = mesh; else _terrainGridMeshLegacy = mesh;
        return mesh;
    }

    private ArrayMesh GetWaterGridMesh(int cells)
    {
        if (_waterGridMesh != null) return _waterGridMesh;
        ArrayMesh mesh = BuildWaterGridMesh(cells);
        if (_oceanMaterial != null) mesh.SurfaceSetMaterial(0, _oceanMaterial);
        _waterGridMesh = mesh;
        return mesh;
    }

    /// <summary>
    /// Main thread. Installs a sector through a slot: queues the render-thread texture update, points the node at the
    /// shared terrain grid and builds the <c>OceanSurface</c> child. Returns the node mesh, or null when the sector has to
    /// install through a mesh (no payload, no free slot, pool or layout changed since packing).
    /// </summary>
    private ArrayMesh? TryInstallSectorSlot(SectorData data, MeshInstance3D node, out long residentBytes, out int surfaceUploads)
    {
        residentBytes = 0L;
        surfaceUploads = 0;
        SlotInstallPayload? payload = data.SlotPayload;
        VertexTablePool? pool = _vertexTable;
        if (payload == null || pool == null || !_slotInstallActive) return null;
        int slot = -1;
        if (payload.GpuSlot >= 0)
        {
            // GPU-built sector: K2 wrote the slot on the render thread, the slot is already ours (reserved when the job was queued).
            if (payload.PoolGeneration != pool.Generation || payload.Cells != pool.Cells) return null;
            slot = payload.GpuSlot;
        }
        else
        {
            if (payload.Cells != pool.Cells || payload.HasChord != pool.HasChord || payload.HasChord != data.UsesRuntimeParentChord
                || !pool.TryAcquire(out slot))
            {
                RingProxyBuildCounters.AddSlotFallback();
                return null;
            }
            pool.Upload(slot, payload.Blocks);
        }
        _activeSlots[data.Coordinate] = slot;
        RingProxyBuildCounters.NoteSlotsInUse(SampleSpacingMeters, pool.UsedSlots);
        node.SetInstanceShaderParameter(ProxySlotName, slot);
        node.CustomAabb = data.VisualBounds;
        residentBytes = pool.BytesPerSlot;
        if (data.OceanIndices.Length == 0)
        {
            ClearOceanChild(node);
            RingProxyBuildCounters.AddSlotOceanKind(0);
        }
        else
        {
            MeshInstance3D child = GetOrCreateOceanChild(node);
            if (payload.WaterGrid)
            {
                child.Mesh = GetWaterGridMesh(pool.Cells);
                RingProxyBuildCounters.AddSlotOceanKind(1);
                child.CustomAabb = payload.MeshBounds;
            }
            else
            {
                child.Mesh = BuildCompactOceanMesh(payload.CompactOcean!);
                child.CustomAabb = payload.MeshBounds;
                residentBytes += payload.CompactOcean!.Bytes;
                surfaceUploads = 1;
                RingProxyBuildCounters.AddSlotOceanKind(2);
            }
            child.MaterialOverride = _oceanMaterial;
            child.SetInstanceShaderParameter(ProxySlotName, payload.WaterGrid ? slot : -1);
            // The child's AABB is the mesh-mode one (sort parity); the morph reach beyond it rides on the cull margin.
            Aabb b = payload.MeshBounds, v = data.VisualBounds;
            float reach = MathF.Max(0.0f, MathF.Max(
                MathF.Max(b.Position.X - v.Position.X, b.Position.Y - v.Position.Y),
                MathF.Max(b.Position.Z - v.Position.Z, MathF.Max((v.Position + v.Size - b.Position - b.Size).X, MathF.Max((v.Position + v.Size - b.Position - b.Size).Y, (v.Position + v.Size - b.Position - b.Size).Z)))));
            _oceanChildReach[child] = reach;
            child.ExtraCullMargin = reach + (SampleSpacingMeters is 2 or 4 or 8 ? _fftOceanCullMarginMeters : 0.0f);
            child.Visible = true;
        }
        return GetTerrainGridMesh(pool.Cells, data.UsesRuntimeParentChord);
    }

    private ArrayMesh BuildCompactOceanMesh(CompactOceanArrays ocean)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = ocean.Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = ocean.Normals;
        arrays[(int)Mesh.ArrayType.Color] = ocean.Colors;
        arrays[(int)Mesh.ArrayType.TexUV] = ocean.Uvs;
        arrays[(int)Mesh.ArrayType.TexUV2] = ocean.MorphData;
        arrays[(int)Mesh.ArrayType.Index] = ocean.Indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private MeshInstance3D GetOrCreateOceanChild(MeshInstance3D node)
    {
        if (_oceanChildren.TryGetValue(node, out MeshInstance3D? child)) return child;
        // Ocean surfaces never cast shadows (the ocean material is shadows_disabled), so the child casts none either.
        child = new MeshInstance3D { Name = OceanSurfaceName, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        node.AddChild(child);
        _oceanChildren[node] = child;
        return child;
    }

    private static readonly StringName ProxyWaterRowsName = "proxy_water_rows";
    private const string OceanShadowCasterName = "OceanShadowCaster";

    /// <summary>
    /// Mesh install gives the opaque shadow caster the node's whole mesh, so the water surface casts the same opaque shadow
    /// as the terrain (it darkens the seabed under it). The slot node's mesh is the terrain grid only, so the ocean child
    /// gets a matching shadow-only caster that reuses the opaque shadow material and draws the water rows (or the compact mesh).
    /// </summary>
    private void ConfigureOceanShadowCaster(MeshInstance3D visualNode, bool enabled)
    {
        if (!_oceanChildren.TryGetValue(visualNode, out MeshInstance3D? child)) return;
        MeshInstance3D? caster = child.GetNodeOrNull<MeshInstance3D>(OceanShadowCasterName);
        bool active = enabled && child.Mesh != null && _opaqueShadowMaterial != null;
        if (caster == null)
        {
            if (!active) return;
            caster = new MeshInstance3D { Name = OceanShadowCasterName, CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly };
            child.AddChild(caster);
        }
        caster.Mesh = active ? child.Mesh : null;
        caster.MaterialOverride = active ? _opaqueShadowMaterial : null;
        caster.CastShadow = active ? GeometryInstance3D.ShadowCastingSetting.ShadowsOnly : GeometryInstance3D.ShadowCastingSetting.Off;
        caster.Visible = active;
        if (!active) return;
        caster.CustomAabb = child.CustomAabb;
        Variant slot = child.GetInstanceShaderParameter("proxy_slot");
        int slotValue = slot.VariantType == Variant.Type.Nil ? -1 : slot.AsInt32();
        caster.SetInstanceShaderParameter(ProxySlotName, slotValue);
        caster.SetInstanceShaderParameter(ProxyWaterRowsName, slotValue >= 0 ? 1 : 0);
        Variant rect = visualNode.GetInstanceShaderParameter("coverage_atlas_rect");
        if (rect.VariantType != Variant.Type.Nil) caster.SetInstanceShaderParameter(CoverageAtlasRectName, rect);
    }

    private void ClearOceanChild(MeshInstance3D node)
    {
        if (!_oceanChildren.TryGetValue(node, out MeshInstance3D? child)) return;
        child.Mesh = null;
        child.MaterialOverride = null;
        child.CustomAabb = default;
        child.ExtraCullMargin = 0.0f;
        _oceanChildReach.Remove(child);
        child.Visible = false;
        child.SetInstanceShaderParameter(ProxySlotName, -1);
    }

    /// <summary>Mirrors an ocean-shader instance parameter onto the node's OceanSurface child (no-op for mesh-installed nodes).</summary>
    private void SetOceanChildParameter(MeshInstance3D node, StringName name, Variant value)
    {
        if (_oceanChildren.Count > 0 && _oceanChildren.TryGetValue(node, out MeshInstance3D? child))
        {
            child.SetInstanceShaderParameter(name, value);
            if (name == CoverageAtlasRectName) child.GetNodeOrNull<MeshInstance3D>(OceanShadowCasterName)?.SetInstanceShaderParameter(name, value);
        }
    }

    private void SetOceanChildCullMargin(MeshInstance3D node, float margin)
    {
        if (_oceanChildren.Count > 0 && _oceanChildren.TryGetValue(node, out MeshInstance3D? child) && child.Mesh != null)
            child.ExtraCullMargin = margin + (_oceanChildReach.TryGetValue(child, out float reach) ? reach : 0.0f);
    }

    /// <summary>Slot release for a sector leaving a node; the slot may be re-acquired in the same frame.</summary>
    private void ReleaseSectorSlot(RingTerrainSectorCoordinate coordinate, MeshInstance3D node)
    {
        if (_activeSlots.Remove(coordinate, out int slot)) _vertexTable?.Release(slot);
        if (_vertexTable == null && _oceanChildren.Count == 0) return;
        ClearOceanChild(node);
        node.SetInstanceShaderParameter(ProxySlotName, -1);
        node.CustomAabb = default;
    }

    /// <summary>Mesh-install fallback on a slot-capable level: make sure a reused node carries no slot state.</summary>
    private void ResetNodeForMeshInstall(MeshInstance3D node)
    {
        if (_vertexTable == null && _oceanChildren.Count == 0) return;
        node.SetInstanceShaderParameter(ProxySlotName, -1);
        node.CustomAabb = default;
        ClearOceanChild(node);
    }

    // ---- Contract-test fixtures -----------------------------------------------------------------------------

    /// <summary>One real worker sector with its packed slot, for the vertex-table contract test.</summary>
    public sealed class VertexTableFixtureSector
    {
        private readonly SectorData _data;
        private readonly int _cells;
        private readonly SkirtPlan _plan;
        internal VertexTableFixtureSector(object data, int cells, SkirtPlan plan, VertexTableSlotBlocks blocks)
        {
            _data = (SectorData)data; _cells = cells; _plan = plan; Blocks = blocks;
        }

        public VertexTableSlotBlocks Blocks { get; }
        public bool UsesRuntimeParentChord => _data.UsesRuntimeParentChord;
        public bool FullyOceanSurface => _data.FullyOceanSurface;
        public int OceanIndexCount => _data.OceanIndices.Length;
        public bool[] OceanCells => _data.OceanSurfaceMask?.CoveredCells ?? Array.Empty<bool>();
        public byte[] OceanDepthBytes => _data.OceanSurfaceMask?.FftDeepWaterVertexDepths ?? Array.Empty<byte>();
        public Aabb MeshBounds => ComputeVertexBounds(_data.Vertices);

        /// <summary>The terrain surface exactly as the mesh-mode install builds it (per-sector ArrayMesh), for render A/B.</summary>
        public ArrayMesh BuildReferenceTerrainMesh()
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = _data.Vertices;
            arrays[(int)Mesh.ArrayType.Normal] = _data.Normals;
            arrays[(int)Mesh.ArrayType.Color] = _data.Colors;
            arrays[(int)Mesh.ArrayType.TexUV] = _data.Uvs;
            arrays[(int)Mesh.ArrayType.TexUV2] = _data.MorphData;
            arrays[(int)Mesh.ArrayType.Index] = _data.Indices;
            Mesh.ArrayFormat format = 0;
            if (_data.UsesRuntimeParentChord)
            {
                var custom = new float[_data.ParentChordOffsets.Length * 3];
                for (int i = 0; i < _data.ParentChordOffsets.Length; i++)
                { custom[i * 3] = _data.ParentChordOffsets[i].X; custom[i * 3 + 1] = _data.ParentChordOffsets[i].Y; custom[i * 3 + 2] = _data.ParentChordOffsets[i].Z; }
                arrays[(int)Mesh.ArrayType.Custom0] = custom;
                format = (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
            }
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, format);
            return mesh;
        }

        /// <summary>Centre, mean surface normal and extent of the terrain lattice, to frame a camera.</summary>
        public (Vector3 Center, Vector3 Normal, float Extent) ViewFrame()
        {
            int lattice = new VertexTableLayout(_cells).LatticeVertices;
            Vector3 min = _data.Vertices[0], max = _data.Vertices[0], normal = Vector3.Zero;
            for (int i = 0; i < lattice; i++)
            {
                min = new Vector3(Math.Min(min.X, _data.Vertices[i].X), Math.Min(min.Y, _data.Vertices[i].Y), Math.Min(min.Z, _data.Vertices[i].Z));
                max = new Vector3(Math.Max(max.X, _data.Vertices[i].X), Math.Max(max.Y, _data.Vertices[i].Y), Math.Max(max.Z, _data.Vertices[i].Z));
                normal += _data.Normals[i];
            }
            return ((min + max) * 0.5f, normal.Normalized(), (max - min).Length());
        }

        /// <summary>Position/morph bounds as the worker computed them (what CustomAabb will carry in WO-S1).</summary>
        public Aabb VisualBounds => _data.VisualBounds;
        public int SourceVertexCount => _data.Vertices.Length;
        public int SkirtVertexCount { get { ComputeSkirtTexelMap(_data, _cells, _plan, out int count); return count; } }

        /// <summary>Compares slot bytes read back from the GPU against the worker arrays. Returns null on success.</summary>
        public string? VerifyReadback(byte[] pos, byte[]? chord, byte[] attr, out string stats)
        {
            var layout = new VertexTableLayout(_cells);
            stats = "";
            if (pos.Length != layout.BytesPerBlock || attr.Length != layout.BytesPerBlock)
                return $"readback size pos={pos.Length} attr={attr.Length} expected={layout.BytesPerBlock}";
            bool hasChord = _data.UsesRuntimeParentChord;
            if (hasChord && (chord == null || chord.Length != layout.BytesPerBlock)) return "chord readback missing";
            int[]? map = ComputeSkirtTexelMap(_data, _cells, _plan, out int skirtCount);
            if (map == null) return "skirt layout not representable";
            int lattice2 = layout.LatticeVertices * 2;
            ReadOnlySpan<float> posF = MemoryMarshal.Cast<byte, float>(pos);
            ReadOnlySpan<uint> attrU = MemoryMarshal.Cast<byte, uint>(attr);
            ReadOnlySpan<float> chordF = hasChord ? MemoryMarshal.Cast<byte, float>(chord!) : default;
            double maxNormalDegrees = 0.0;
            int zeroNormals = 0, colorMismatch = 0, skirtVertices = 0;
            for (int i = 0; i < map.Length; i++)
            {
                int texel = map[i];
                if (texel < 0) continue; // coastal clip vertices stay per-sector
                Vector3 v = _data.Vertices[i];
                if (BitConverter.SingleToInt32Bits(posF[texel * 4]) != BitConverter.SingleToInt32Bits(v.X)
                    || BitConverter.SingleToInt32Bits(posF[texel * 4 + 1]) != BitConverter.SingleToInt32Bits(v.Y)
                    || BitConverter.SingleToInt32Bits(posF[texel * 4 + 2]) != BitConverter.SingleToInt32Bits(v.Z))
                    return $"position differs at vertex {i}";
                if (BitConverter.SingleToInt32Bits(posF[texel * 4 + 3]) != BitConverter.SingleToInt32Bits(_data.MorphData[i].X))
                    return $"morph differs at vertex {i}";
                if (hasChord)
                {
                    Vector3 c = _data.ParentChordOffsets[i];
                    if (BitConverter.SingleToInt32Bits(chordF[texel * 4]) != BitConverter.SingleToInt32Bits(c.X)
                        || BitConverter.SingleToInt32Bits(chordF[texel * 4 + 1]) != BitConverter.SingleToInt32Bits(c.Y)
                        || BitConverter.SingleToInt32Bits(chordF[texel * 4 + 2]) != BitConverter.SingleToInt32Bits(c.Z))
                        return $"chord differs at vertex {i}";
                }
                Vector3 sourceNormal = _data.Normals[i];
                Vector3 decoded = DecodeOctNormal16(attrU[texel * 4]);
                if (sourceNormal.LengthSquared() < 1.0e-12f) zeroNormals++;
                else
                {
                    // Double precision: a float dot cannot resolve angles below about 0.02 degrees.
                    double cx = (double)sourceNormal.Y * decoded.Z - (double)sourceNormal.Z * decoded.Y;
                    double cy = (double)sourceNormal.Z * decoded.X - (double)sourceNormal.X * decoded.Z;
                    double cz = (double)sourceNormal.X * decoded.Y - (double)sourceNormal.Y * decoded.X;
                    double dot = (double)sourceNormal.X * decoded.X + (double)sourceNormal.Y * decoded.Y + (double)sourceNormal.Z * decoded.Z;
                    double degrees = Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), dot) * 180.0 / Math.PI;
                    maxNormalDegrees = Math.Max(maxNormalDegrees, degrees);
                    if (degrees > 0.01) return $"normal differs by {degrees:0.0000} deg at vertex {i}";
                }
                if (attrU[texel * 4 + 1] != EncodeColor8(_data.Colors[i])) { colorMismatch++; return $"colour differs at vertex {i}"; }
                bool skirt = i >= lattice2;
                uint flags = attrU[texel * 4 + 2];
                if (((flags & 1u) != 0u) != (_data.MorphData[i].Y >= 0.5f)) return $"flag differs at vertex {i}";
                if (((flags & 2u) != 0u) != skirt) return $"skirt flag differs at vertex {i}";
                Vector2 uv;
                if (skirt)
                {
                    skirtVertices++;
                    uv = DecodeSkirtUv(attrU[texel * 4 + 2], attrU[texel * 4 + 3], _cells);
                }
                else
                {
                    // Exactly what the shader derives: column / cells and lattice row / cells.
                    int lattice = texel % layout.LatticeVertices;
                    uv = new Vector2((lattice % layout.Width) / (float)_cells, (lattice / layout.Width) / (float)_cells);
                }
                if (BitConverter.SingleToInt32Bits(uv.X) != BitConverter.SingleToInt32Bits(_data.Uvs[i].X)
                    || BitConverter.SingleToInt32Bits(uv.Y) != BitConverter.SingleToInt32Bits(_data.Uvs[i].Y))
                    return $"uv differs at vertex {i}: slot {uv} source {_data.Uvs[i]}";
            }
            string? triangleError = VerifyTriangles(posF, map);
            stats = $"vertices={map.Length} skirt={skirtVertices} max_normal_deg={maxNormalDegrees:0.00000} zero_normals={zeroNormals}";
            return triangleError;
        }

        // The shared grids must reproduce the worker's triangles: same positions, same winding, same diagonal.
        private string? VerifyTriangles(ReadOnlySpan<float> pos, int[] map)
        {
            var layout = new VertexTableLayout(_cells);
            int lattice = layout.LatticeVertices;
            var expected = new Dictionary<ulong, int>();
            for (int t = 0; t + 2 < _data.Indices.Length; t += 3)
                AddKeyedTriangle(expected, _data.Vertices[_data.Indices[t]], _data.Vertices[_data.Indices[t + 1]], _data.Vertices[_data.Indices[t + 2]], 1);
            int[] terrain = BuildTerrainGridIndices(_cells, _data.UsesRuntimeParentChord);
            var actual = new Dictionary<ulong, int>();
            for (int t = 0; t + 2 < terrain.Length; t += 3)
                AddKeyedTriangle(actual, SlotPosition(pos, TerrainTexel(terrain[t], layout)), SlotPosition(pos, TerrainTexel(terrain[t + 1], layout)),
                    SlotPosition(pos, TerrainTexel(terrain[t + 2], layout)), 1);
            foreach ((ulong key, int count) in expected)
                if (!actual.TryGetValue(key, out int have) || have != count)
                    return $"terrain triangle set differs ({expected.Count} source keys, {actual.Count} grid keys)";
            if (actual.Count != expected.Count) return $"terrain grid has extra triangles ({expected.Count} source keys, {actual.Count} grid keys)";

            // Water: wet triangles of the worker (excluding clip vertices, which stay per-sector) must be grid triangles.
            int[] water = BuildWaterGridIndices(_cells);
            var waterSet = new Dictionary<ulong, int>();
            for (int t = 0; t + 2 < water.Length; t += 3)
                AddKeyedTriangle(waterSet, SlotPosition(pos, lattice + water[t]), SlotPosition(pos, lattice + water[t + 1]), SlotPosition(pos, lattice + water[t + 2]), 1);
            int lattice2 = lattice * 2;
            for (int t = 0; t + 2 < _data.OceanIndices.Length; t += 3)
            {
                int a = _data.OceanIndices[t], b = _data.OceanIndices[t + 1], c = _data.OceanIndices[t + 2];
                if (a >= lattice2 || b >= lattice2 || c >= lattice2) continue;
                var one = new Dictionary<ulong, int>();
                AddKeyedTriangle(one, _data.Vertices[a], _data.Vertices[b], _data.Vertices[c], 1);
                foreach (ulong key in one.Keys)
                    if (!waterSet.ContainsKey(key)) return "water triangle missing from the shared water grid";
            }
            return null;
        }

        private static Vector3 SlotPosition(ReadOnlySpan<float> pos, int texel)
            => new(pos[texel * 4], pos[texel * 4 + 1], pos[texel * 4 + 2]);

        private static int TerrainTexel(int vertexId, VertexTableLayout layout)
            => vertexId < layout.LatticeVertices ? vertexId : layout.SkirtTexel(vertexId - layout.LatticeVertices);

        private static ulong VertexKey(Vector3 v)
        {
            ulong h = 14695981039346656037UL;
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(v.X)) * 1099511628211UL;
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(v.Y)) * 1099511628211UL;
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(v.Z)) * 1099511628211UL;
            return h;
        }

        // Rotation-canonical, zero-area triangles skipped (a degenerate triangle draws nothing either way).
        private static void AddKeyedTriangle(Dictionary<ulong, int> set, Vector3 a, Vector3 b, Vector3 c, int weight)
        {
            ulong ha = VertexKey(a), hb = VertexKey(b), hc = VertexKey(c);
            if (hb < ha && hb <= hc) { (a, b, c) = (b, c, a); (ha, hb, hc) = (hb, hc, ha); }
            else if (hc < ha && hc < hb) { (a, b, c) = (c, a, b); (ha, hb, hc) = (hc, ha, hb); }
            double ux = (double)b.X - a.X, uy = (double)b.Y - a.Y, uz = (double)b.Z - a.Z;
            double vx = (double)c.X - a.X, vy = (double)c.Y - a.Y, vz = (double)c.Z - a.Z;
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            if (cx == 0.0 && cy == 0.0 && cz == 0.0) return;
            ulong key = ((ha * 1000003UL) ^ hb) * 1000003UL ^ hc;
            set[key] = set.GetValueOrDefault(key) + weight;
        }
    }

    /// <summary>Builds real worker sectors (no scene, no streaming) for the vertex-table contract test.</summary>
    public sealed class VertexTableFixtureSession
    {
        private readonly RingLodSurfaceEditSnapshot _edits;
        private readonly RingTerrainSampler _sampler;
        private readonly int _ringWidth;

        internal VertexTableFixtureSession(RingLodSurfaceEditSnapshot edits, RingTerrainSampler sampler, int ringWidth)
        {
            _edits = edits; _sampler = sampler; _ringWidth = ringWidth;
        }

        public VertexTableFixtureSector? Build(RingTerrainSectorCoordinate coordinate, int spacing, int cells,
            RingLodGeometrySnapshot geometry, bool buildWalls, bool buildSkirts)
            => Build(coordinate, spacing, cells, geometry, buildWalls, buildSkirts, 0.0f, 0.0f, Transform3D.Identity, 32.0f, Colors.Black, Colors.White, false);

        /// <summary>As above with the per-level constants the GPU parity test varies (underlays, owner basis, skirt depth, terrain colours, parent water restore).</summary>
        public VertexTableFixtureSector? Build(RingTerrainSectorCoordinate coordinate, int spacing, int cells,
            RingLodGeometrySnapshot geometry, bool buildWalls, bool buildSkirts, float underlay, float parentUnderlay,
            Transform3D ownerInverse, float skirtDepth, Color low, Color high, bool parentRestoresWaterUnderlay)
        {
            using var cache = new TerrainBuildSampleCache(_edits, _sampler, _ringWidth);
            SectorData data = BuildRawSector(
                requestVersion: 1, coordinate: coordinate, editSnapshot: _edits, sampler: _sampler, sampleCache: cache,
                geometry: geometry, ownerInverse: ownerInverse, size: cells * spacing, spacing: spacing,
                parentSpacing: spacing * 2, globalMorphTarget: null, cells: cells, underlay: underlay, parentUnderlay: parentUnderlay,
                parentRestoresWaterUnderlay: parentRestoresWaterUnderlay, ringWidth: _ringWidth, buildWalls: buildWalls, buildSkirts: buildSkirts,
                skirtDepth: skirtDepth, blocky: false, alignSurfaceToTerrainSdf: false, heightQuantum: 1,
                low: low, high: high, coverageMask: null);
            var plan = new SkirtPlan(cells * spacing, _ringWidth, buildWalls);
            VertexTableSlotBlocks? blocks = PackVertexTableSlot(data, cells, plan);
            return blocks == null ? null : new VertexTableFixtureSector(data, cells, plan, blocks);
        }
    }

    public static VertexTableFixtureSession CreateVertexTableFixtureSession(
        RingLodSurfaceEditSnapshot edits, RingTerrainSampler sampler, int ringWidth)
        => new(edits, sampler, ringWidth);

    /// <summary>Layout numbers for the report: bytes of one slot and a pool of the given size.</summary>
    public static (int Width, int Height, long BytesPerSlot) DescribeVertexTableSlot(int cells, bool hasChord)
    {
        var layout = new VertexTableLayout(cells);
        return (layout.Width, layout.Height, (long)layout.BytesPerBlock * (hasChord ? 3 : 2));
    }

    public static int VertexTableCapacity(int radiusAround, int radiusAcross, int warmSectors)
        => VertexTableCapacityFor(radiusAround, radiusAcross, warmSectors);

    /// <summary>Test hook: color quantisation check against Godot's own mesh round trip.</summary>
    public static bool VertexTableColorQuantisationMatchesMesh(out string detail)
    {
        var colors = new Color[256];
        var vertices = new Vector3[256];
        var random = new Random(7);
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = new Color((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
            vertices[i] = new Vector3(i, 0, 0);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        using var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arrays);
        Color[] back = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Color].AsColorArray();
        for (int i = 0; i < colors.Length; i++)
        {
            uint packed = EncodeColor8(colors[i]);
            uint expected = (uint)Math.Round(back[i].R * 255.0f) | ((uint)Math.Round(back[i].G * 255.0f) << 8)
                | ((uint)Math.Round(back[i].B * 255.0f) << 16) | ((uint)Math.Round(back[i].A * 255.0f) << 24);
            if (packed != expected) { detail = $"colour {i} packed={packed:X8} mesh={expected:X8}"; return false; }
        }
        detail = "256 random colours identical to Godot mesh quantisation";
        return true;
    }
}
