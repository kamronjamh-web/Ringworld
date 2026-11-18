using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace RingworldTechDemo.Vegetation.Scatter;

/// <summary>One LOD of one rock group: the carrier mesh, the vertex-fetch data textures and the unit-space bounds of every variant.</summary>
public sealed class RockLodMesh
{
    /// <summary>One indexed surface whose vertex positions are all zero (the vertex shader fetches the real ones); a custom AABB covers every variant.</summary>
    public ArrayMesh Carrier = null!;
    /// <summary>RGBA32F, width = vertex count, height = variant count. xyz = unit-space position, w = the vertex's y (height above ground contact, negative below; the base blend runs over w in [0, 0.15]).</summary>
    public ImageTexture Positions = null!;
    /// <summary>RGBA16F, same layout. xyz = smooth normal, w = 0.</summary>
    public ImageTexture Normals = null!;
    public Aabb UnitAabb;
    public int VertexCount, TriangleCount;
}

public sealed class RockGroupCatalog
{
    public RingScatterRenderGroup Group;
    public int VariantCount;
    public RockLodMesh[] Lods = Array.Empty<RockLodMesh>();
    internal float[][][] RawPositions = Array.Empty<float[][]>(); // [lod][variant] xyz
    internal ushort[][] RawIndices = Array.Empty<ushort[]>(); // [lod], Godot front-face order

    /// <summary>Boulder collision: the LOD2 vertices (a convex hull is built from them).</summary>
    public Vector3[] HullPoints(int variant)
    {
        float[] p = RawPositions[LodCountMinusOne][variant];
        var r = new Vector3[p.Length / 3];
        for (int i = 0; i < r.Length; i++) r[i] = new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
        return r;
    }

    /// <summary>Outcrop collision: the LOD1 triangles, three vertices each, in the mesh's front-face order.</summary>
    public Vector3[] TriangleSoup(int variant)
    {
        float[] p = RawPositions[1][variant];
        ushort[] idx = RawIndices[1];
        var r = new Vector3[idx.Length];
        for (int i = 0; i < idx.Length; i++) r[i] = new Vector3(p[idx[i] * 3], p[idx[i] * 3 + 1], p[idx[i] * 3 + 2]);
        return r;
    }

    private const int LodCountMinusOne = 2;
}

/// <summary>
/// T5-6 (design B1): the prepared rock mesh set, loaded from resources/rocks/prepared/rock_catalog.bin (own little-endian format, magic RCK1) written by tools/PrepareRockAssets.cs.
/// Builds carrier meshes, data textures and collision data at load. <see cref="Load"/> is cached and main-thread only; <see cref="ArtifactSha256"/> reads only the manifest and is safe anywhere.
/// </summary>
public sealed class RockCatalog
{
    public const int CatalogVersion = 1;
    public const string ArtifactPath = "res://resources/rocks/prepared/rock_catalog.bin";
    public const string ManifestPath = "res://resources/rocks/prepared/rock_catalog.manifest.json";
    private const int LodCount = 3;

    private static RockCatalog? _cached;
    private static string? _sha;
    private static readonly object ShaLock = new();

    public RockGroupCatalog Outcrops = null!, Boulders = null!, Cobbles = null!;
    /// <summary>Byte length of the binary artifact.</summary>
    public long TotalBytes;
    public int Seed;

    /// <summary>The artifact SHA-256 (upper-case hex) from the manifest. No Godot textures are created, so the fingerprint may call it from a worker.</summary>
    public static string ArtifactSha256
    {
        get
        {
            lock (ShaLock)
            {
                if (_sha != null) return _sha;
                using JsonDocument doc = JsonDocument.Parse(FileAccess.GetFileAsBytes(ManifestPath));
                return _sha = doc.RootElement.GetProperty("artifact_sha256").GetString() ?? throw new InvalidOperationException("rock manifest has no artifact_sha256");
            }
        }
    }

    public RockGroupCatalog Group(RingScatterRenderGroup group) => group switch
    {
        RingScatterRenderGroup.Outcrops => Outcrops,
        RingScatterRenderGroup.Boulders => Boulders,
        RingScatterRenderGroup.Cobbles => Cobbles,
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    public static RockCatalog Load() => _cached ??= Parse(FileAccess.GetFileAsBytes(ArtifactPath));

    public static RockCatalog Parse(byte[] bytes)
    {
        var s = new Reader(bytes);
        if (bytes.Length < 16 || bytes[0] != 'R' || bytes[1] != 'C' || bytes[2] != 'K' || bytes[3] != '1') throw new InvalidOperationException("rock catalog: bad magic");
        s.Pos = 4;
        uint version = s.U32();
        if (version != CatalogVersion) throw new InvalidOperationException($"rock catalog: version {version}, expected {CatalogVersion}");
        var catalog = new RockCatalog { TotalBytes = bytes.Length, Seed = (int)s.U32() };
        uint groups = s.U32();
        for (uint gi = 0; gi < groups; gi++)
        {
            var group = new RockGroupCatalog { Group = (RingScatterRenderGroup)s.U32(), VariantCount = (int)s.U32() };
            s.U32(); // stones per variant (cobbles)
            for (int v = 0; v < group.VariantCount; v++) s.U32(); // active stones
            if (s.U32() != LodCount) throw new InvalidOperationException("rock catalog: LOD count");
            group.Lods = new RockLodMesh[LodCount];
            group.RawPositions = new float[LodCount][][];
            group.RawIndices = new ushort[LodCount][];
            for (int l = 0; l < LodCount; l++)
            {
                int vc = (int)s.U32(), ic = (int)s.U32();
                var indices = new ushort[ic];
                for (int i = 0; i < ic; i++) indices[i] = s.U16();
                var positions = new float[group.VariantCount][];
                var posBytes = new byte[vc * group.VariantCount * 16];
                var nrmBytes = new byte[vc * group.VariantCount * 8];
                Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue), max = new(float.MinValue, float.MinValue, float.MinValue);
                for (int v = 0; v < group.VariantCount; v++)
                {
                    var p = new float[vc * 3];
                    for (int i = 0; i < p.Length; i++) p[i] = s.F32();
                    positions[v] = p;
                    int row = v * vc;
                    for (int i = 0; i < vc; i++)
                    {
                        float x = p[i * 3], y = p[i * 3 + 1], z = p[i * 3 + 2];
                        min = new Vector3(Math.Min(min.X, x), Math.Min(min.Y, y), Math.Min(min.Z, z)); max = new Vector3(Math.Max(max.X, x), Math.Max(max.Y, y), Math.Max(max.Z, z));
                        int o = (row + i) * 16;
                        BinaryPrimitives.WriteSingleLittleEndian(posBytes.AsSpan(o), x); BinaryPrimitives.WriteSingleLittleEndian(posBytes.AsSpan(o + 4), y);
                        BinaryPrimitives.WriteSingleLittleEndian(posBytes.AsSpan(o + 8), z); BinaryPrimitives.WriteSingleLittleEndian(posBytes.AsSpan(o + 12), y); // w = height above ground contact
                    }
                    for (int i = 0; i < vc; i++)
                    {
                        int o = (row + i) * 8;
                        for (int c = 0; c < 3; c++) BinaryPrimitives.WriteHalfLittleEndian(nrmBytes.AsSpan(o + c * 2), (Half)s.F32());
                        BinaryPrimitives.WriteHalfLittleEndian(nrmBytes.AsSpan(o + 6), (Half)0f);
                    }
                }
                var aabb = new Aabb(min, max - min);
                var carrier = new ArrayMesh();
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[vc];
                var gi32 = new int[ic];
                for (int i = 0; i < ic; i++) gi32[i] = indices[i];
                arrays[(int)Mesh.ArrayType.Index] = gi32;
                carrier.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                carrier.CustomAabb = aabb;
                group.Lods[l] = new RockLodMesh
                {
                    Carrier = carrier, UnitAabb = aabb, VertexCount = vc, TriangleCount = ic / 3,
                    Positions = ImageTexture.CreateFromImage(Image.CreateFromData(vc, group.VariantCount, false, Image.Format.Rgbaf, posBytes)),
                    Normals = ImageTexture.CreateFromImage(Image.CreateFromData(vc, group.VariantCount, false, Image.Format.Rgbah, nrmBytes)),
                };
                group.RawPositions[l] = positions;
                group.RawIndices[l] = indices;
            }
            switch (group.Group)
            {
                case RingScatterRenderGroup.Outcrops: catalog.Outcrops = group; break;
                case RingScatterRenderGroup.Boulders: catalog.Boulders = group; break;
                case RingScatterRenderGroup.Cobbles: catalog.Cobbles = group; break;
            }
        }
        if (s.Pos != bytes.Length) throw new InvalidOperationException("rock catalog: trailing bytes");
        return catalog;
    }

    private sealed class Reader
    {
        private readonly byte[] _b;
        public int Pos;
        public Reader(byte[] b) { _b = b; }
        public uint U32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(_b.AsSpan(Pos)); Pos += 4; return v; }
        public ushort U16() { ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(Pos)); Pos += 2; return v; }
        public float F32() { float v = BinaryPrimitives.ReadSingleLittleEndian(_b.AsSpan(Pos)); Pos += 4; return v; }
    }
}
