using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Vegetation.Scatter;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T5-6 (docs/work-orders/terrain-vegetation/T5.md, design B1): the deterministic rock mesh set generator. 16 boulders, 8 outcrops and 6 cobble clusters, three LODs each, in unit space
/// (boulders: height above ground contact 1; outcrops: horizontal length 1; cobble clusters: footprint diameter 1; origin at ground contact, +y up).
/// Pure C#: double arithmetic, an integer-hash value noise, positions quantised to 1/4096 before normals and hashes, so the mesh hashes and the artifact bytes do not depend on the engine.
/// The artifact is a little-endian binary blob (magic RCK1, version 1) with a JSON manifest next to it; <see cref="RockCatalog"/> builds Godot objects from it at load.
/// Triangles are stored in Godot's front-face order (clockwise seen from outside); normals are computed before that flip, from the counter-clockwise order.
/// Run through tools/prepare_rock_assets.ps1 (generate, or -Check to regenerate and compare).
/// </summary>
public partial class PrepareRockAssets : RefCounted
{
    public const int GeneratorVersion = 2;
    public const int CommittedSeed = 5006;
    public const double Quantum = 4096.0;
    public const int LodCount = 3;
    public const int BoulderVariants = 16, OutcropVariants = 8, ClusterVariants = 6, StonesPerCluster = 6;
    public const string ArtifactRelative = "resources/rocks/prepared/rock_catalog.bin";
    public const string ManifestRelative = "resources/rocks/prepared/rock_catalog.manifest.json";

    public sealed class RockLod
    {
        public int VertexCount;
        public int StoneVertexCount; // cobble clusters: vertices per stone
        public ushort[] Indices = Array.Empty<ushort>(); // Godot front-face order
        public int[][] Q = Array.Empty<int[]>(); // per variant, xyz quantised (1/4096)
        public float[][] Normals = Array.Empty<float[]>(); // per variant, xyz
        public List<int[]> BedTopRings = new(); // outcrops: per bed, the start vertex of every ring whose vertices lie on the bed's flat top
        public int RingSize;
        public double X(int variant, int vertex) => Q[variant][vertex * 3] / Quantum;
        public double Y(int variant, int vertex) => Q[variant][vertex * 3 + 1] / Quantum;
        public double Z(int variant, int vertex) => Q[variant][vertex * 3 + 2] / Quantum;
    }

    public sealed class RockGroup
    {
        public RingScatterRenderGroup Id;
        public string Name = "";
        public int VariantCount;
        public int StonesPerVariant;
        public int[] ActiveStones = Array.Empty<int>(); // cobble clusters: stones that are not collapsed
        public RockLod[] Lods = new RockLod[LodCount];
    }

    public sealed class RockSet
    {
        public int Seed;
        public RockGroup[] Groups = Array.Empty<RockGroup>(); // Outcrops, Boulders, Cobbles
        public RockGroup Group(RingScatterRenderGroup id) { foreach (RockGroup g in Groups) if (g.Id == id) return g; throw new ArgumentException(id.ToString()); }
    }

    // ------------------------------------------------------------------ entry points

    public static RockSet Generate(int seed)
    {
        Ico ico = Ico.Build(3);
        return new RockSet { Seed = seed, Groups = new[] { BuildOutcrops(seed), BuildBoulders(seed, ico), BuildClusters(seed, ico) } };
    }

    public static void Write(RockSet set, string binPath, string manifestPath)
    {
        byte[] bytes = Serialize(set);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(binPath))!);
        File.WriteAllBytes(binPath, bytes);
        File.WriteAllBytes(manifestPath, BuildManifest(set, bytes));
    }

    public static bool Check(string binPath, string manifestPath)
    {
        try
        {
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            JsonElement root = manifest.RootElement;
            int seed = root.GetProperty("seed").GetInt32();
            byte[] committed = File.ReadAllBytes(binPath);
            RockSet set = Generate(seed);
            byte[] regenerated = Serialize(set);
            bool ok = true;
            if (!regenerated.AsSpan().SequenceEqual(committed)) { Console.Error.WriteLine("PREPARE_ROCK_ASSETS check: regenerated artifact bytes differ from " + binPath); ok = false; }
            if (!string.Equals(Hex(SHA256.HashData(committed)), root.GetProperty("artifact_sha256").GetString(), StringComparison.Ordinal)) { Console.Error.WriteLine("PREPARE_ROCK_ASSETS check: artifact_sha256 does not match " + binPath); ok = false; }
            var expected = new Dictionary<string, string>();
            foreach (JsonElement mesh in root.GetProperty("meshes").EnumerateArray())
                expected[MeshKey(mesh.GetProperty("group").GetString()!, mesh.GetProperty("lod").GetInt32(), mesh.GetProperty("variant").GetInt32())] = mesh.GetProperty("sha256").GetString()!;
            int seen = 0;
            foreach ((string key, string hash) in MeshHashes(set))
            {
                seen++;
                if (!expected.TryGetValue(key, out string? want) || want != hash) { Console.Error.WriteLine("PREPARE_ROCK_ASSETS check: mesh hash differs for " + key); ok = false; }
            }
            if (seen != expected.Count) { Console.Error.WriteLine($"PREPARE_ROCK_ASSETS check: mesh count {seen} != manifest {expected.Count}"); ok = false; }
            return ok;
        }
        catch (Exception e) { Console.Error.WriteLine("PREPARE_ROCK_ASSETS check: " + e.Message); return false; }
    }

    /// <summary>Called by tools/prepare_rock_assets.gd. Paths are res:// or absolute.</summary>
    public int Run(string output, string manifest, bool check)
    {
        string bin = ProjectSettings.GlobalizePath(output), man = ProjectSettings.GlobalizePath(manifest);
        if (check)
        {
            bool ok = Check(bin, man);
            Console.WriteLine(ok ? "PREPARE_ROCK_ASSETS=PASS check" : "PREPARE_ROCK_ASSETS=FAIL check");
            return ok ? 0 : 1;
        }
        RockSet set = Generate(CommittedSeed);
        Write(set, bin, man);
        Console.WriteLine($"PREPARE_ROCK_ASSETS=PASS wrote {new FileInfo(bin).Length} bytes");
        return 0;
    }

    // ------------------------------------------------------------------ serialisation and manifest

    public static byte[] Serialize(RockSet set)
    {
        var ms = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); ms.Write(b); }
        void F32(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); ms.Write(b); }
        ms.Write(Encoding.ASCII.GetBytes("RCK1"));
        U32(RockCatalog.CatalogVersion);
        U32((uint)set.Seed);
        U32((uint)set.Groups.Length);
        foreach (RockGroup g in set.Groups)
        {
            U32((uint)g.Id); U32((uint)g.VariantCount); U32((uint)g.StonesPerVariant);
            for (int v = 0; v < g.VariantCount; v++) U32((uint)g.ActiveStones[v]);
            U32(LodCount);
            foreach (RockLod lod in g.Lods)
            {
                U32((uint)lod.VertexCount); U32((uint)lod.Indices.Length);
                foreach (ushort i in lod.Indices) U16(i);
                for (int v = 0; v < g.VariantCount; v++)
                {
                    foreach (int q in lod.Q[v]) F32((float)(q / Quantum)); // exact: |q| < 2^24
                    foreach (float n in lod.Normals[v]) F32(n);
                }
            }
        }
        return ms.ToArray();
    }

    private static string MeshKey(string group, int lod, int variant) => group + "/" + lod + "/" + variant;

    public static IEnumerable<(string Key, string Hash)> MeshHashes(RockSet set)
    {
        foreach (RockGroup g in set.Groups)
            for (int l = 0; l < LodCount; l++)
                for (int v = 0; v < g.VariantCount; v++)
                    yield return (MeshKey(g.Name, l, v), MeshHash(g.Lods[l], v));
    }

    /// <summary>SHA-256 over the triangle indices and the quantised positions of one mesh.</summary>
    public static string MeshHash(RockLod lod, int variant)
    {
        var bytes = new byte[lod.Indices.Length * 2 + lod.Q[variant].Length * 4];
        int o = 0;
        foreach (ushort i in lod.Indices) { BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(o), i); o += 2; }
        foreach (int q in lod.Q[variant]) { BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(o), q); o += 4; }
        return Hex(SHA256.HashData(bytes));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    private static byte[] BuildManifest(RockSet set, byte[] artifact)
    {
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema_version", 1);
            w.WriteNumber("generator_version", GeneratorVersion);
            w.WriteNumber("catalog_version", RockCatalog.CatalogVersion);
            w.WriteNumber("seed", set.Seed);
            w.WriteString("engine_identity", "none: pure C# generator, little-endian binary artifact, no engine types in the geometry");
            w.WriteString("implementation_identity", "tools/PrepareRockAssets.cs generator_version " + GeneratorVersion);
            w.WriteString("artifact_sha256", Hex(SHA256.HashData(artifact)));
            w.WriteString("artifact_path", ArtifactRelative);
            w.WriteNumber("artifact_bytes", artifact.Length);
            w.WriteStartArray("groups");
            foreach (RockGroup g in set.Groups)
            {
                w.WriteStartObject();
                w.WriteString("name", g.Name); w.WriteNumber("variants", g.VariantCount);
                w.WriteStartArray("lods");
                foreach (RockLod lod in g.Lods) { w.WriteStartObject(); w.WriteNumber("vertices", lod.VertexCount); w.WriteNumber("triangles", lod.Indices.Length / 3); w.WriteEndObject(); }
                w.WriteEndArray(); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("meshes");
            foreach (RockGroup g in set.Groups)
                for (int l = 0; l < LodCount; l++)
                    for (int v = 0; v < g.VariantCount; v++)
                    {
                        w.WriteStartObject();
                        w.WriteString("group", g.Name); w.WriteNumber("lod", l); w.WriteNumber("variant", v);
                        w.WriteString("sha256", MeshHash(g.Lods[l], v));
                        w.WriteEndObject();
                    }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ hash and noise

    private static uint Mix(uint h)
    {
        unchecked { h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16; return h; }
    }

    private static uint Hash(uint seed, int a, int b = 0, int c = 0, int d = 0)
    {
        unchecked
        {
            uint h = Mix(seed * 0x9E3779B1u + 0x85ebca6bu);
            h = Mix(h ^ ((uint)a * 0xc2b2ae35u));
            h = Mix(h ^ ((uint)b * 0x27d4eb2fu));
            h = Mix(h ^ ((uint)c * 0x165667b1u));
            h = Mix(h ^ ((uint)d * 0x9E3779B9u));
            return h;
        }
    }

    private static double Unit(uint h) => h / 4294967296.0;
    private static double Rand(int seed, int a, int b, int c, int d = 0) => Unit(Hash((uint)seed, a, b, c, d));

    private static double Noise3(uint seed, double x, double y, double z)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y), zi = (int)Math.Floor(z);
        double fx = x - xi, fy = y - yi, fz = z - zi;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
        double L(int i, int j, int k) => Unit(Hash(seed, xi + i, yi + j, zi + k)) * 2 - 1;
        double x00 = L(0, 0, 0) + (L(1, 0, 0) - L(0, 0, 0)) * fx, x10 = L(0, 1, 0) + (L(1, 1, 0) - L(0, 1, 0)) * fx;
        double x01 = L(0, 0, 1) + (L(1, 0, 1) - L(0, 0, 1)) * fx, x11 = L(0, 1, 1) + (L(1, 1, 1) - L(0, 1, 1)) * fx;
        double y0 = x00 + (x10 - x00) * fy, y1 = x01 + (x11 - x01) * fy;
        return y0 + (y1 - y0) * fz;
    }

    /// <summary>Three octaves of value noise in [-1, 1].</summary>
    private static double Fbm3(uint seed, double x, double y, double z)
    {
        double sum = 0, amp = 1, f = 1;
        for (int o = 0; o < 3; o++) { sum += amp * Noise3(seed + (uint)o * 7919u, x * f, y * f, z * f); amp *= 0.5; f *= 2; }
        return sum / 1.75;
    }

    // ------------------------------------------------------------------ icosphere

    private sealed class Ico
    {
        public readonly List<double[]> V = new();
        public readonly List<int[]> LevelFaces = new();
        public readonly int[] VertexCounts;

        private Ico(int maxLevel) { VertexCounts = new int[maxLevel + 1]; }

        public static Ico Build(int maxLevel)
        {
            var ico = new Ico(maxLevel);
            double t = (1 + Math.Sqrt(5)) / 2;
            double[][] seed =
            {
                new[] { -1, t, 0 }, new[] { 1, t, 0 }, new[] { -1, -t, 0 }, new[] { 1, -t, 0 }, new[] { 0, -1, t }, new[] { 0, 1, t },
                new[] { 0, -1, -t }, new[] { 0, 1, -t }, new[] { t, 0, -1 }, new[] { t, 0, 1 }, new[] { -t, 0, -1 }, new[] { -t, 0, 1 },
            };
            foreach (double[] p in seed) ico.V.Add(Normalise(p));
            int[] faces =
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };
            ico.LevelFaces.Add(faces);
            ico.VertexCounts[0] = ico.V.Count;
            for (int level = 1; level <= maxLevel; level++)
            {
                int[] prev = ico.LevelFaces[level - 1];
                var next = new List<int>(prev.Length * 4);
                var mids = new Dictionary<long, int>();
                int Mid(int a, int b)
                {
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (mids.TryGetValue(key, out int m)) return m;
                    double[] pa = ico.V[a], pb = ico.V[b];
                    ico.V.Add(Normalise(new[] { pa[0] + pb[0], pa[1] + pb[1], pa[2] + pb[2] }));
                    return mids[key] = ico.V.Count - 1;
                }
                for (int f = 0; f < prev.Length; f += 3)
                {
                    int a = prev[f], b = prev[f + 1], c = prev[f + 2];
                    int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    next.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
                }
                ico.LevelFaces.Add(next.ToArray());
                ico.VertexCounts[level] = ico.V.Count;
            }
            return ico;
        }
    }

    private static double[] Normalise(double[] p)
    {
        double l = Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]);
        return new[] { p[0] / l, p[1] / l, p[2] / l };
    }

    private static ushort[] ToIndices(int[] faces, int offset = 0)
    {
        var r = new ushort[faces.Length];
        for (int i = 0; i < faces.Length; i++) r[i] = (ushort)(faces[i] + offset);
        return r;
    }

    // ------------------------------------------------------------------ finishing (quantise, normals, flip)

    /// <summary>Quantises, computes smooth normals from the CCW triangles, then flips the winding to Godot's front-face order.</summary>
    private static RockLod Finish(List<double[]> variants, ushort[] ccwIndices, int vertexCount)
    {
        var lod = new RockLod { VertexCount = vertexCount, Q = new int[variants.Count][], Normals = new float[variants.Count][] };
        for (int v = 0; v < variants.Count; v++)
        {
            double[] p = variants[v];
            var q = new int[p.Length];
            for (int i = 0; i < p.Length; i++) q[i] = (int)Math.Floor(p[i] * Quantum + 0.5);
            lod.Q[v] = q;
            lod.Normals[v] = ComputeNormals(q, ccwIndices, vertexCount);
        }
        var flipped = (ushort[])ccwIndices.Clone();
        for (int i = 0; i < flipped.Length; i += 3) (flipped[i + 1], flipped[i + 2]) = (flipped[i + 2], flipped[i + 1]);
        lod.Indices = flipped;
        return lod;
    }

    private static float[] ComputeNormals(int[] q, ushort[] idx, int vertexCount)
    {
        var acc = new double[vertexCount * 3];
        for (int t = 0; t < idx.Length; t += 3)
        {
            int a = idx[t] * 3, b = idx[t + 1] * 3, c = idx[t + 2] * 3;
            double e1x = (q[b] - q[a]) / Quantum, e1y = (q[b + 1] - q[a + 1]) / Quantum, e1z = (q[b + 2] - q[a + 2]) / Quantum;
            double e2x = (q[c] - q[a]) / Quantum, e2y = (q[c + 1] - q[a + 1]) / Quantum, e2z = (q[c + 2] - q[a + 2]) / Quantum;
            double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
            foreach (int vtx in new[] { a, b, c }) { acc[vtx] += nx; acc[vtx + 1] += ny; acc[vtx + 2] += nz; }
        }
        var n = new float[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            double x = acc[i * 3], y = acc[i * 3 + 1], z = acc[i * 3 + 2], l = Math.Sqrt(x * x + y * y + z * z);
            if (l < 1e-12) { n[i * 3 + 1] = 1; continue; }
            n[i * 3] = (float)(x / l); n[i * 3 + 1] = (float)(y / l); n[i * 3 + 2] = (float)(z / l);
        }
        return n;
    }

    private static double[] Sub(double[] a, int vertexCount) { var r = new double[vertexCount * 3]; Array.Copy(a, r, r.Length); return r; }

    // ------------------------------------------------------------------ boulders

    private sealed class BoulderParams
    {
        public double Ay, Az, Ox, Oy, Oz;
        public uint NoiseSeed;
        public readonly List<(double Nx, double Ny, double Nz, double C)> Cuts = new();
    }

    private static BoulderParams MakeBoulder(int seed, int variant)
    {
        var p = new BoulderParams { NoiseSeed = Hash((uint)seed, 2, variant, 99) };
        double a = 0.6 + 0.3 * Rand(seed, 2, variant, 1), b = 0.45 + 0.3 * Rand(seed, 2, variant, 2);
        if (Rand(seed, 2, variant, 3) > 0.5) (a, b) = (b, a); // some are tall, some flat
        p.Ay = a; p.Az = b;
        p.Ox = Rand(seed, 2, variant, 4) * 50; p.Oy = Rand(seed, 2, variant, 5) * 50; p.Oz = Rand(seed, 2, variant, 6) * 50;
        int cuts = 3 + (int)(Rand(seed, 2, variant, 7) * 4); // 3..6
        for (int k = 0; k < cuts; k++)
        {
            double z = Rand(seed, 2, variant, 10, k) * 2 - 1, phi = Rand(seed, 2, variant, 11, k) * Math.PI * 2, r = Math.Sqrt(1 - z * z);
            p.Cuts.Add((r * Math.Cos(phi), z, r * Math.Sin(phi), 0.36 + 0.3 * Rand(seed, 2, variant, 12, k)));
        }
        return p;
    }

    private static double[] BoulderPoint(BoulderParams p, double[] d)
    {
        double x = d[0], y = d[1] * p.Ay, z = d[2] * p.Az;
        double n = 1 + 0.12 * Fbm3(p.NoiseSeed, d[0] * 2.5 + p.Ox, d[1] * 2.5 + p.Oy, d[2] * 2.5 + p.Oz);
        x *= n; y *= n; z *= n;
        foreach ((double nx, double ny, double nz, double c) in p.Cuts)
        {
            double t = x * nx + y * ny + z * nz - c;
            if (t > 0) { x -= t * nx; y -= t * ny; z -= t * nz; }
        }
        return new[] { x, y, z };
    }

    private static RockGroup BuildBoulders(int seed, Ico ico)
    {
        int[] levels = { 3, 2, 1 };
        var group = new RockGroup { Id = RingScatterRenderGroup.Boulders, Name = "boulders", VariantCount = BoulderVariants, StonesPerVariant = 0, ActiveStones = new int[BoulderVariants] };
        var all = new List<double[]>();
        int full = ico.VertexCounts[3];
        for (int v = 0; v < BoulderVariants; v++)
        {
            BoulderParams p = MakeBoulder(seed, v);
            var raw = new double[full][];
            double yMin = double.MaxValue, yMax = double.MinValue;
            for (int i = 0; i < full; i++) { raw[i] = BoulderPoint(p, ico.V[i]); yMin = Math.Min(yMin, raw[i][1]); yMax = Math.Max(yMax, raw[i][1]); }
            double yb = yMin + 0.18 * (yMax - yMin); // the flat base plane cuts away the lowest 18 % of the raw height
            double xMin = double.MaxValue, xMax = double.MinValue, zMin = double.MaxValue, zMax = double.MinValue;
            foreach (double[] r in raw) { r[1] = Math.Max(r[1], yb); xMin = Math.Min(xMin, r[0]); xMax = Math.Max(xMax, r[0]); zMin = Math.Min(zMin, r[2]); zMax = Math.Max(zMax, r[2]); }
            double s = 1.25 / (yMax - yb), cx = (xMin + xMax) / 2, cz = (zMin + zMax) / 2; // above ground (y >= 0) the rock is 1 high; the base plane is at y = -0.25
            var pos = new double[full * 3];
            for (int i = 0; i < full; i++) { pos[i * 3] = (raw[i][0] - cx) * s; pos[i * 3 + 1] = -0.25 + (raw[i][1] - yb) * s; pos[i * 3 + 2] = (raw[i][2] - cz) * s; }
            all.Add(pos);
        }
        for (int l = 0; l < LodCount; l++)
        {
            int vc = ico.VertexCounts[levels[l]];
            var perVariant = new List<double[]>();
            foreach (double[] pos in all) perVariant.Add(Sub(pos, vc)); // the lower levels' vertices are a prefix of level 3's, so they lie on LOD0's vertex set exactly
            group.Lods[l] = Finish(perVariant, ToIndices(ico.LevelFaces[levels[l]]), vc);
        }
        return group;
    }

    // ------------------------------------------------------------------ cobble clusters

    private static readonly double[][] OctaDirs = { new[] { 1.0, 0, 0 }, new[] { -1.0, 0, 0 }, new[] { 0, 1.0, 0 }, new[] { 0, -1.0, 0 }, new[] { 0, 0, 1.0 }, new[] { 0, 0, -1.0 } };
    private static readonly int[] OctaFaces = { 0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4, 2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5 };
    private static readonly int[] ClusterStones = { 6, 6, 5, 5, 4, 3 };

    private sealed class Stone
    {
        public double Ay, Az, Yaw, Ox, Oy, Oz, CutNx, CutNy, CutNz, CutC, Size, Px, Pz;
        public uint NoiseSeed;
        public double Scale, Bcx, Bcz, MinY, Height;
    }

    private static double[] StonePoint(Stone s, double[] d)
    {
        double x = d[0], y = d[1] * s.Ay, z = d[2] * s.Az;
        double n = 1 + 0.14 * Fbm3(s.NoiseSeed, d[0] * 1.7 + s.Ox, d[1] * 1.7 + s.Oy, d[2] * 1.7 + s.Oz);
        x *= n; y *= n; z *= n;
        double t = x * s.CutNx + y * s.CutNy + z * s.CutNz - s.CutC;
        if (t > 0) { x -= t * s.CutNx; y -= t * s.CutNy; z -= t * s.CutNz; }
        double cs = Math.Cos(s.Yaw), sn = Math.Sin(s.Yaw);
        return new[] { x * cs + z * sn, y, -x * sn + z * cs };
    }

    private static RockGroup BuildClusters(int seed, Ico ico)
    {
        var group = new RockGroup { Id = RingScatterRenderGroup.Cobbles, Name = "cobbles", VariantCount = ClusterVariants, StonesPerVariant = StonesPerCluster, ActiveStones = (int[])ClusterStones.Clone() };
        int[] icoVerts = { ico.VertexCounts[1], ico.VertexCounts[0], 6 }; // 42 / 12 / 6
        var perLod = new List<double[]>[LodCount];
        for (int l = 0; l < LodCount; l++) perLod[l] = new List<double[]>();
        for (int v = 0; v < ClusterVariants; v++)
        {
            var stones = new List<Stone>();
            var placed = new List<(double X, double Z, double D)>();
            for (int s = 0; s < StonesPerCluster; s++)
            {
                int b = v * 10 + s;
                var st = new Stone
                {
                    NoiseSeed = Hash((uint)seed, 3, b, 99), Ay = 0.5 + 0.3 * Rand(seed, 3, b, 1), Az = 0.6 + 0.35 * Rand(seed, 3, b, 2), Yaw = Rand(seed, 3, b, 3) * Math.PI * 2,
                    Ox = Rand(seed, 3, b, 4) * 50, Oy = Rand(seed, 3, b, 5) * 50, Oz = Rand(seed, 3, b, 6) * 50,
                    Size = s == 0 ? 0.40 + 0.10 * Rand(seed, 3, b, 7) : 0.15 + 0.25 * Rand(seed, 3, b, 7),
                };
                double cz = Rand(seed, 3, b, 8) * 2 - 1, cphi = Rand(seed, 3, b, 9) * Math.PI * 2, cr = Math.Sqrt(1 - cz * cz);
                st.CutNx = cr * Math.Cos(cphi); st.CutNy = cz; st.CutNz = cr * Math.Sin(cphi); st.CutC = 0.5 + 0.2 * Rand(seed, 3, b, 10);
                // placement: the best of 48 hashed candidates in the unit-diameter footprint, preferring separation from the stones already placed
                double bestScore = -1, bx = 0, bz = 0;
                for (int c = 0; c < 48; c++)
                {
                    double ang = Rand(seed, 3, b, 20, c) * Math.PI * 2, rad = (0.5 - st.Size / 2) * Math.Sqrt(Rand(seed, 3, b, 21, c));
                    double x = rad * Math.Cos(ang), z = rad * Math.Sin(ang), score = double.MaxValue;
                    foreach ((double px, double pz, double pd) in placed) score = Math.Min(score, Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz)) / (0.5 * (st.Size + pd)));
                    if (score > bestScore) { bestScore = score; bx = x; bz = z; }
                    if (score >= 0.9) break;
                }
                st.Px = bx; st.Pz = bz;
                placed.Add((bx, bz, st.Size));
                // LOD0 extents fix the scale, the xz centring and the embed; the lower LODs reuse them.
                double xMin = double.MaxValue, xMax = double.MinValue, zMin = double.MaxValue, zMax = double.MinValue, yMin = double.MaxValue, yMax = double.MinValue;
                for (int i = 0; i < icoVerts[0]; i++)
                {
                    double[] p = StonePoint(st, ico.V[i]);
                    xMin = Math.Min(xMin, p[0]); xMax = Math.Max(xMax, p[0]); zMin = Math.Min(zMin, p[2]); zMax = Math.Max(zMax, p[2]); yMin = Math.Min(yMin, p[1]); yMax = Math.Max(yMax, p[1]);
                }
                st.Scale = st.Size / Math.Max(xMax - xMin, zMax - zMin);
                st.Bcx = (xMin + xMax) / 2; st.Bcz = (zMin + zMax) / 2; st.MinY = yMin; st.Height = (yMax - yMin) * st.Scale;
                stones.Add(st);
            }
            for (int l = 0; l < LodCount; l++)
            {
                int sv = icoVerts[l];
                var pos = new double[StonesPerCluster * sv * 3];
                for (int s = 0; s < StonesPerCluster; s++)
                {
                    Stone st = stones[s];
                    bool active = s < ClusterStones[v];
                    for (int i = 0; i < sv; i++)
                    {
                        int o = (s * sv + i) * 3;
                        if (!active) { pos[o] = 0; pos[o + 1] = -0.02; pos[o + 2] = 0; continue; } // collapsed to a point
                        double[] p = StonePoint(st, l == 2 ? OctaDirs[i] : ico.V[i]);
                        pos[o] = (p[0] - st.Bcx) * st.Scale + st.Px;
                        pos[o + 1] = (p[1] - st.MinY) * st.Scale - 0.3 * st.Height; // the lowest point sits 30 % of the stone's height below the ground
                        pos[o + 2] = (p[2] - st.Bcz) * st.Scale + st.Pz;
                    }
                }
                perLod[l].Add(pos);
            }
        }
        for (int l = 0; l < LodCount; l++)
        {
            int sv = icoVerts[l];
            int[] faces = l == 2 ? OctaFaces : ico.LevelFaces[l == 0 ? 1 : 0];
            var idx = new List<ushort>();
            for (int s = 0; s < StonesPerCluster; s++) idx.AddRange(ToIndices(faces, s * sv));
            RockLod lod = Finish(perLod[l], idx.ToArray(), StonesPerCluster * sv);
            lod.StoneVertexCount = sv;
            group.Lods[l] = lod;
        }
        return group;
    }

    // ------------------------------------------------------------------ outcrops

    private const int OutcropBeds = 7, OutcropRing = 48;
    /// <summary>T5-10b: the outcrop's buried foot (unit length below the root). The bed boundaries still start at -0.75; only the bottom ring of bed 0 and the bottom cap sit deeper.</summary>
    public const double OutcropBaseY = -1.25;
    private static readonly int[] ExtraRingBeds = { 1, 3, 5 }; // LOD0 walls with one extra ring

    private sealed class OutcropRings
    {
        public double[][] Bottom = new double[OutcropBeds][], Top = new double[OutcropBeds][], Extra = new double[OutcropBeds][];
        public double[] CapInner = Array.Empty<double>();
        public double[] Centre = new double[3];
    }

    private static OutcropRings MakeOutcrop(int seed, int variant)
    {
        double zr = 0.35 + 0.35 * Rand(seed, 1, variant, 1), top = 0.25 + 0.35 * Rand(seed, 1, variant, 2);
        var weights = new double[OutcropBeds];
        double wsum = 0;
        for (int i = 0; i < OutcropBeds; i++) { weights[i] = 0.7 + 0.6 * Rand(seed, 1, variant, 3, i); wsum += weights[i]; }
        var yb = new double[OutcropBeds + 1];
        yb[0] = -0.75;
        for (int i = 0; i < OutcropBeds; i++) yb[i + 1] = yb[i] + weights[i] / wsum * (top + 0.75);
        var rb = new double[OutcropBeds][]; var rt = new double[OutcropBeds][]; var rx = new double[OutcropBeds][];
        uint ns = Hash((uint)seed, 1, variant, 99);
        double ox = Rand(seed, 1, variant, 4) * 40, oy = Rand(seed, 1, variant, 5) * 40;
        for (int i = 0; i < OutcropBeds; i++)
        {
            rb[i] = new double[OutcropRing]; rt[i] = new double[OutcropRing]; rx[i] = new double[OutcropRing];
            for (int k = 0; k < OutcropRing; k++)
            {
                double th = 2 * Math.PI * k / OutcropRing, c = Math.Cos(th), s = Math.Sin(th);
                double n = Fbm3(ns, c * 1.4 + ox + 11 * i, s * 1.4 + oy, i * 7.7);
                double u1 = 0.5 + 0.5 * Noise3(ns + 1, c * 1.8 + ox, s * 1.8 + oy, i * 3.3), u2 = 0.5 + 0.5 * Noise3(ns + 2, c * 2.2 + ox, s * 2.2 + oy, i * 5.1 + 40);
                rb[i][k] = i == 0 ? 1 + 0.30 * n : rt[i - 1][k] * (1 - (0.04 + 0.11 * u2)); // each bed starts inside the one below: a ledge of varying width
                rt[i][k] = rb[i][k] * (1 - (0.01 + 0.02 * u1));
                rx[i][k] = 0.5 * (rb[i][k] + rt[i][k]) * (1 + 0.08 * Noise3(ns + 3, c * 3 + ox, s * 3 + oy, i * 2.9));
            }
        }
        var rings = new OutcropRings();
        double xMin = double.MaxValue, xMax = double.MinValue, zMin = double.MaxValue, zMax = double.MinValue;
        for (int i = 0; i < OutcropBeds; i++)
            for (int k = 0; k < OutcropRing; k++)
            {
                double th = 2 * Math.PI * k / OutcropRing;
                foreach (double r in new[] { rb[i][k], rt[i][k] })
                { xMin = Math.Min(xMin, Math.Cos(th) * r); xMax = Math.Max(xMax, Math.Cos(th) * r); zMin = Math.Min(zMin, zr * Math.Sin(th) * r); zMax = Math.Max(zMax, zr * Math.Sin(th) * r); }
            }
        double sc = 1.0 / (xMax - xMin), cx = (xMin + xMax) / 2, cz = (zMin + zMax) / 2; // horizontal length 1
        double[] Ring(double[] radii, double y)
        {
            var ring = new double[OutcropRing * 3];
            for (int k = 0; k < OutcropRing; k++)
            {
                double th = 2 * Math.PI * k / OutcropRing;
                ring[k * 3] = (Math.Cos(th) * radii[k] - cx) * sc; ring[k * 3 + 1] = y; ring[k * 3 + 2] = (zr * Math.Sin(th) * radii[k] - cz) * sc;
            }
            return ring;
        }
        for (int i = 0; i < OutcropBeds; i++)
        {
            rings.Bottom[i] = Ring(rb[i], i == 0 ? OutcropBaseY : yb[i]); rings.Top[i] = Ring(rt[i], yb[i + 1]);
            if (Array.IndexOf(ExtraRingBeds, i) >= 0) rings.Extra[i] = Ring(rx[i], 0.5 * (yb[i] + yb[i + 1]));
        }
        double[] topRing = rings.Top[OutcropBeds - 1];
        double mx = 0, mz = 0;
        for (int k = 0; k < OutcropRing; k++) { mx += topRing[k * 3]; mz += topRing[k * 3 + 2]; }
        mx /= OutcropRing; mz /= OutcropRing;
        rings.Centre = new[] { mx, topRing[1], mz };
        rings.CapInner = new double[OutcropRing * 3];
        for (int k = 0; k < OutcropRing; k++) { rings.CapInner[k * 3] = mx + 0.55 * (topRing[k * 3] - mx); rings.CapInner[k * 3 + 1] = topRing[1]; rings.CapInner[k * 3 + 2] = mz + 0.55 * (topRing[k * 3 + 2] - mz); }
        return rings;
    }

    private sealed class OutcropLodSpec
    {
        public int N, Stride;
        public int[][] BedGroups = Array.Empty<int[]>();
        public bool Extras, TopAnnulus;
    }

    private static readonly OutcropLodSpec[] OutcropSpecs =
    {
        new() { N = 48, Stride = 1, BedGroups = new[] { new[] { 0, 0 }, new[] { 1, 1 }, new[] { 2, 2 }, new[] { 3, 3 }, new[] { 4, 4 }, new[] { 5, 5 }, new[] { 6, 6 } }, Extras = true, TopAnnulus = true },
        new() { N = 24, Stride = 2, BedGroups = new[] { new[] { 0, 1 }, new[] { 2, 3 }, new[] { 4, 5 }, new[] { 6, 6 } }, TopAnnulus = true },
        new() { N = 12, Stride = 4, BedGroups = new[] { new[] { 0, 1 }, new[] { 2, 3 }, new[] { 4, 5 }, new[] { 6, 6 } } },
    };

    private static RockGroup BuildOutcrops(int seed)
    {
        var group = new RockGroup { Id = RingScatterRenderGroup.Outcrops, Name = "outcrops", VariantCount = OutcropVariants, ActiveStones = new int[OutcropVariants] };
        var rings = new OutcropRings[OutcropVariants];
        for (int v = 0; v < OutcropVariants; v++) rings[v] = MakeOutcrop(seed, v);
        for (int l = 0; l < LodCount; l++)
        {
            OutcropLodSpec spec = OutcropSpecs[l];
            var positions = new List<double[]>();
            ushort[]? topology = null;
            List<int[]>? bedTops = null;
            int vertexCount = 0;
            for (int v = 0; v < OutcropVariants; v++)
            {
                var pos = new List<double>();
                var idx = new List<ushort>();
                var tops = new List<int[]>();
                var topLists = new List<List<int>>();
                int N = spec.N;
                int AddRing(double[] ring)
                {
                    int start = pos.Count / 3;
                    for (int k = 0; k < N; k++) { pos.Add(ring[k * spec.Stride * 3]); pos.Add(ring[k * spec.Stride * 3 + 1]); pos.Add(ring[k * spec.Stride * 3 + 2]); }
                    return start;
                }
                int AddPoint(double x, double y, double z) { pos.Add(x); pos.Add(y); pos.Add(z); return pos.Count / 3 - 1; }
                void Wall(int lo, int up) { for (int k = 0; k < N; k++) { int k1 = (k + 1) % N; idx.AddRange(new[] { (ushort)(lo + k), (ushort)(up + k), (ushort)(lo + k1), (ushort)(lo + k1), (ushort)(up + k), (ushort)(up + k1) }); } }
                void Ledge(int outer, int inner) { for (int k = 0; k < N; k++) { int k1 = (k + 1) % N; idx.AddRange(new[] { (ushort)(outer + k), (ushort)(inner + k), (ushort)(outer + k1), (ushort)(outer + k1), (ushort)(inner + k), (ushort)(inner + k1) }); } }
                OutcropRings r = rings[v];
                int firstBottom = -1;
                for (int g = 0; g < spec.BedGroups.Length; g++)
                {
                    int first = spec.BedGroups[g][0], last = spec.BedGroups[g][1];
                    if (g > 0)
                    {
                        int outer = AddRing(r.Top[spec.BedGroups[g - 1][1]]), inner = AddRing(r.Bottom[first]);
                        Ledge(outer, inner);
                        topLists[g - 1].Add(outer);
                    }
                    int prev = AddRing(r.Bottom[first]);
                    if (g == 0) firstBottom = prev;
                    if (spec.Extras && first == last && r.Extra[first] != null) { int mid = AddRing(r.Extra[first]); Wall(prev, mid); prev = mid; }
                    int wallTop = AddRing(r.Top[last]);
                    Wall(prev, wallTop);
                    topLists.Add(new List<int> { wallTop });
                }
                int capOuter = AddRing(r.Top[OutcropBeds - 1]);
                topLists[^1].Add(capOuter);
                int centre = AddPoint(r.Centre[0], r.Centre[1], r.Centre[2]);
                int fanRing = capOuter;
                if (spec.TopAnnulus)
                {
                    int inner = AddRing(r.CapInner);
                    Ledge(capOuter, inner);
                    topLists[^1].Add(inner);
                    fanRing = inner;
                }
                for (int k = 0; k < N; k++) { int k1 = (k + 1) % N; idx.AddRange(new[] { (ushort)(fanRing + k), (ushort)centre, (ushort)(fanRing + k1) }); }
                double bx = 0, bz = 0;
                for (int k = 0; k < N; k++) { bx += pos[(firstBottom + k) * 3]; bz += pos[(firstBottom + k) * 3 + 2]; }
                int bottomCentre = AddPoint(bx / N, OutcropBaseY, bz / N);
                for (int k = 0; k < N; k++) { int k1 = (k + 1) % N; idx.AddRange(new[] { (ushort)(firstBottom + k), (ushort)(firstBottom + k1), (ushort)bottomCentre }); }
                foreach (List<int> list in topLists) tops.Add(list.ToArray());
                positions.Add(pos.ToArray());
                ushort[] indexArray = idx.ToArray();
                if (topology == null) { topology = indexArray; bedTops = tops; vertexCount = pos.Count / 3; }
                else if (!topology.AsSpan().SequenceEqual(indexArray) || vertexCount != pos.Count / 3) throw new InvalidOperationException("outcrop topology differs between variants");
            }
            RockLod lod = Finish(positions, topology!, vertexCount);
            lod.BedTopRings = bedTops!;
            lod.RingSize = spec.N;
            group.Lods[l] = lod;
        }
        return group;
    }
}
