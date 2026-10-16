using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.GrassCardsSpike;

/// <summary>
/// P0 spike only (docs/grass-cards-redesign.md): procedural tuft atlas with
/// coverage-preserving mips, the trimmed three-card mesh, deterministic distance-banded
/// placement and a rolling ground mesh. Nothing here is referenced by gameplay code.
/// </summary>
public partial class GrassCardsSpikeAssets : RefCounted
{
    public const int Variants = 4;
    public const int TileSize = 512;
    public const int AtlasWidth = TileSize * Variants;
    public const int AtlasHeight = TileSize;
    public const float AlphaCutoff = 0.5f;

    // Card outline rows (height 0, .5, 1) and half widths in card-width units. Shared by mesh and atlas clip.
    static readonly float[] RowY = { 0f, 0.5f, 1f };
    static readonly float[] RowHalfWidth = { 0.26f, 0.46f, 0.40f };

    public string LastCoverageReport { get; private set; } = "";
    public string LastAtlasHash { get; private set; } = "";
    public float LastOutlineWasteFraction { get; private set; }

    static float OutlineHalfWidth(float y)
    {
        y = Mathf.Clamp(y, 0f, 1f);
        return y <= 0.5f ? Mathf.Lerp(RowHalfWidth[0], RowHalfWidth[1], y / 0.5f) : Mathf.Lerp(RowHalfWidth[1], RowHalfWidth[2], (y - 0.5f) / 0.5f);
    }

    // ---------------------------------------------------------------- hashing

    static uint Hash(uint a, uint b, uint c)
    {
        uint h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u) * 0x85EBCA6Bu ^ (c + 0x165667B1u) * 0xC2B2AE35u;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return h;
    }
    static float Unit(uint h) => (h & 0xFFFFFF) / 16777216f;

    sealed class Rng
    {
        uint s;
        public Rng(uint seed) { s = seed == 0 ? 1u : seed; }
        public float Next() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s & 0xFFFFFF) / 16777216f; }
        public float Range(float a, float b) => a + (b - a) * Next();
    }

    // ---------------------------------------------------------------- atlas

    /// <summary>R mask, G height gradient, B per-blade random, A constant. Mips preserve coverage at the cutoff.</summary>
    public ImageTexture BuildAtlas(int bladesPerVariant, int seed) => BuildAtlasFull(bladesPerVariant, seed, 0.09f, 0.38f, 1f);

    /// <summary>P0b: rootSpread = half width of root cluster, minTall = shortest blade tip height, widthMul = blade width gain.</summary>
    public ImageTexture BuildAtlasFull(int bladesPerVariant, int seed, float rootSpread, float minTall, float widthMul)
    {
        bladesPerVariant = Math.Clamp(bladesPerVariant, 4, 96);
        int w = AtlasWidth, h = AtlasHeight;
        var mask = new float[w * h];
        var grad = new float[w * h];
        var rand = new float[w * h];
        for (int v = 0; v < Variants; v++)
        {
            var rng = new Rng(Hash((uint)seed, (uint)v, 77u));
            DrawTuft(mask, grad, rand, v * TileSize, bladesPerVariant, rng, rootSpread, minTall, widthMul);
        }
        // Clip to the mesh outline so the mesh never truncates visible blades; measure how much of the
        // outline interior is actually covered (the rest is wasted fragments).
        long inside = 0, covered = 0;
        for (int v = 0; v < Variants; v++)
            for (int py = 0; py < TileSize; py++)
            {
                float yu = 1f - (py + 0.5f) / TileSize;
                float hw = OutlineHalfWidth(yu);
                for (int px = 0; px < TileSize; px++)
                {
                    float xu = (px + 0.5f) / TileSize - 0.5f;
                    int idx = py * w + v * TileSize + px;
                    if (Mathf.Abs(xu) > hw) { mask[idx] = 0f; continue; }
                    inside++;
                    if (mask[idx] >= AlphaCutoff) covered++;
                }
            }
        LastOutlineWasteFraction = inside > 0 ? 1f - covered / (float)inside : 0f;

        // Mip chain: raw box filter chain for G/B/A; R is box filtered then rescaled per tile so the
        // fraction of pixels at/above the cutoff equals mip 0 (Castano-style coverage preservation).
        var levelsData = new List<byte[]>();
        var rawR = mask; var rawG = grad; var rawB = rand;
        int lw = w, lh = h;
        var report = new System.Text.StringBuilder();
        var cov0 = new float[Variants];
        int level = 0;
        var naiveCov = new float[Variants];
        while (true)
        {
            var scaledR = (float[])rawR.Clone();
            int tileW = Math.Max(1, lw / Variants), tileH = lh;
            report.Append("mip").Append(level).Append(' ').Append(lw).Append('x').Append(lh).Append(':');
            if (tileW >= 1 && lw >= Variants)
            {
                for (int v = 0; v < Variants; v++)
                {
                    var vals = new List<float>(tileW * tileH);
                    for (int y = 0; y < tileH; y++) for (int x = 0; x < tileW; x++) vals.Add(rawR[y * lw + v * tileW + x]);
                    int n = vals.Count;
                    int atCut = 0; foreach (var f in vals) if (f >= AlphaCutoff) atCut++;
                    naiveCov[v] = atCut / (float)n;
                    if (level == 0) cov0[v] = naiveCov[v];
                    float scale = 1f;
                    if (level > 0 && cov0[v] > 0f)
                    {
                        // Quantile threshold: choose the scale that makes exactly cov0 of the pixels reach the cutoff.
                        var sorted = vals.ToArray(); Array.Sort(sorted); Array.Reverse(sorted);
                        int k = Math.Clamp((int)Math.Round(cov0[v] * n) - 1, 0, n - 1);
                        float t = sorted[k];
                        scale = t > 1e-5f ? Math.Clamp(AlphaCutoff / t, 1f, 32f) : 1f;
                        for (int y = 0; y < tileH; y++) for (int x = 0; x < tileW; x++)
                        { int i = y * lw + v * tileW + x; scaledR[i] = Math.Min(1f, rawR[i] * scale); }
                    }
                    int cut = 0;
                    for (int y = 0; y < tileH; y++) for (int x = 0; x < tileW; x++) if (scaledR[y * lw + v * tileW + x] >= AlphaCutoff) cut++;
                    report.Append(" v").Append(v).Append(" naive=").Append((naiveCov[v] * 100f).ToString("0.0")).Append("% kept=").Append((cut / (float)n * 100f).ToString("0.0")).Append("%");
                }
            }
            report.Append('\n');
            var bytes = new byte[lw * lh * 4];
            for (int i = 0; i < lw * lh; i++)
            {
                bytes[i * 4 + 0] = (byte)Math.Clamp((int)Math.Round(scaledR[i] * 255f), 0, 255);
                bytes[i * 4 + 1] = (byte)Math.Clamp((int)Math.Round(rawG[i] * 255f), 0, 255);
                bytes[i * 4 + 2] = (byte)Math.Clamp((int)Math.Round(rawB[i] * 255f), 0, 255);
                bytes[i * 4 + 3] = 128;
            }
            levelsData.Add(bytes);
            if (lw == 1 && lh == 1) break;
            int nw = Math.Max(1, lw / 2), nh = Math.Max(1, lh / 2);
            rawR = Down(rawR, lw, lh, nw, nh); rawG = Down(rawG, lw, lh, nw, nh); rawB = Down(rawB, lw, lh, nw, nh);
            lw = nw; lh = nh; level++;
        }
        LastCoverageReport = report.ToString();
        int total = 0; foreach (var b in levelsData) total += b.Length;
        var all = new byte[total]; int o = 0;
        foreach (var b in levelsData) { Buffer.BlockCopy(b, 0, all, o, b.Length); o += b.Length; }
        uint hsh = 2166136261u; foreach (var b in levelsData[0]) { hsh ^= b; hsh *= 16777619u; }
        LastAtlasHash = hsh.ToString("X8");
        var image = Image.CreateFromData(w, h, true, Image.Format.Rgba8, all);
        return ImageTexture.CreateFromImage(image);
    }

    static float[] Down(float[] src, int w, int h, int nw, int nh)
    {
        var dst = new float[nw * nh];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1);
                int y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                dst[y * nw + x] = (src[y0 * w + x0] + src[y0 * w + x1] + src[y1 * w + x0] + src[y1 * w + x1]) * 0.25f;
            }
        return dst;
    }

    static void DrawTuft(float[] mask, float[] grad, float[] rand, int tileX, int blades, Rng rng, float rootSpread, float minTall, float widthMul)
    {
        const int Segs = 16;
        for (int b = 0; b < blades; b++)
        {
            // Root cluster is tight; tips fan out inside the outline. Taller blades prefer the centre.
            float xr = rng.Range(-rootSpread, rootSpread);
            float yt = minTall + (0.98f - minTall) * MathF.Pow(rng.Next(), 0.65f);
            float reach = OutlineHalfWidth(yt) * 0.88f;
            float xt = Mathf.Clamp(xr + rng.Range(-1f, 1f) * reach * (1.15f - 0.45f * yt), -reach, reach);
            float w0 = rng.Range(0.0075f, 0.0125f) * widthMul;
            float bladeRand = rng.Next();
            float bendY = rng.Range(0.45f, 0.75f);
            float bendX = rng.Range(0.05f, 0.30f);
            var px = new float[Segs + 1]; var py = new float[Segs + 1]; var hw = new float[Segs + 1];
            for (int i = 0; i <= Segs; i++)
            {
                float t = i / (float)Segs;
                float p1x = xr + (xt - xr) * bendX, p1y = yt * bendY;
                float omt = 1f - t;
                px[i] = omt * omt * xr + 2f * omt * t * p1x + t * t * xt;
                py[i] = omt * omt * 0f + 2f * omt * t * p1y + t * t * yt;
                hw[i] = w0 * MathF.Pow(omt, 0.7f) + 0.0004f * omt;
            }
            for (int i = 0; i < Segs; i++)
            {
                float ax = px[i], ay = py[i], bx = px[i + 1], by = py[i + 1];
                float pad = Math.Max(hw[i], hw[i + 1]) + 2f / TileSize;
                int x0 = (int)Math.Floor((Math.Min(ax, bx) - pad + 0.5f) * TileSize), x1 = (int)Math.Ceiling((Math.Max(ax, bx) + pad + 0.5f) * TileSize);
                int y0 = (int)Math.Floor((1f - Math.Max(ay, by) - pad) * TileSize), y1 = (int)Math.Ceiling((1f - Math.Min(ay, by) + pad) * TileSize);
                x0 = Math.Max(0, x0); x1 = Math.Min(TileSize - 1, x1); y0 = Math.Max(0, y0); y1 = Math.Min(TileSize - 1, y1);
                float dx = bx - ax, dy = by - ay; float len2 = dx * dx + dy * dy;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float qx = (x + 0.5f) / TileSize - 0.5f, qy = 1f - (y + 0.5f) / TileSize;
                        float s = len2 > 0f ? Mathf.Clamp(((qx - ax) * dx + (qy - ay) * dy) / len2, 0f, 1f) : 0f;
                        float cx = ax + dx * s - qx, cy = ay + dy * s - qy;
                        float dist = MathF.Sqrt(cx * cx + cy * cy);
                        float half = Mathf.Lerp(hw[i], hw[i + 1], s);
                        float cov = Mathf.Clamp((half - dist) * TileSize + 0.5f, 0f, 1f);
                        if (cov <= 0f) continue;
                        int idx = y * AtlasWidth + tileX + x;
                        if (cov > mask[idx])
                        {
                            float t = (i + s) / Segs;
                            mask[idx] = cov;
                            grad[idx] = 0.5f * t + 0.5f * Mathf.Clamp(qy, 0f, 1f);
                            rand[idx] = bladeRand;
                        }
                    }
            }
        }
    }

    public void SaveAtlasPreview(ImageTexture atlas, string path)
    {
        var img = atlas.GetImage();
        // Preview: mask as luminance on dark green so blade shapes are readable.
        var prev = Image.CreateEmpty(img.GetWidth(), img.GetHeight(), false, Image.Format.Rgba8);
        for (int y = 0; y < img.GetHeight(); y++)
            for (int x = 0; x < img.GetWidth(); x++)
            {
                var c = img.GetPixel(x, y);
                float m = c.R >= AlphaCutoff ? 1f : 0f;
                prev.SetPixel(x, y, new Color(0.08f + 0.5f * c.G * m, 0.15f + 0.6f * c.G * m + 0.1f * c.B * m, 0.05f, 1f));
            }
        prev.SavePng(path);
    }

    // ---------------------------------------------------------------- mesh

    /// <summary>
    /// Three trimmed cards at 0/60/120 degrees, 3 rows each: 18 vertices, 12 triangles. UV is the card-local
    /// atlas coordinate (0..1), UV2 = (card-local x offset, card index). Position is only a model-space
    /// fallback; the shader rebuilds it so cards can collapse to a camera-facing card.
    /// </summary>
    public ArrayMesh BuildMesh()
    {
        var verts = new List<Vector3>(); var normals = new List<Vector3>(); var uv = new List<Vector2>(); var uv2 = new List<Vector2>(); var idx = new List<int>();
        for (int k = 0; k < 3; k++)
        {
            float a = k * Mathf.Pi / 3f;
            var u = new Vector3(MathF.Cos(a), 0f, -MathF.Sin(a));
            int baseIdx = verts.Count;
            for (int r = 0; r < 3; r++)
                for (int side = 0; side < 2; side++)
                {
                    float lx = (side == 0 ? -1f : 1f) * RowHalfWidth[r];
                    verts.Add(u * lx + new Vector3(0f, RowY[r], 0f));
                    normals.Add(Vector3.Up);
                    uv.Add(new Vector2(0.5f + lx, 1f - RowY[r]));
                    uv2.Add(new Vector2(lx, k));
                }
            for (int r = 0; r < 2; r++)
            {
                int i0 = baseIdx + r * 2, i1 = i0 + 1, i2 = i0 + 2, i3 = i0 + 3;
                idx.AddRange(new[] { i0, i2, i1, i1, i2, i3 });
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uv.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.CustomAabb = new Aabb(new Vector3(-0.8f, 0f, -0.8f), new Vector3(1.6f, 1.5f, 1.6f));
        return mesh;
    }

    public int MeshTriangleCount => 12;

    // ---------------------------------------------------------------- ground + height field

    public double GroundHeight(double x, double z) => 10.0 * Math.Sin(x * 0.006) * Math.Sin(z * 0.0045 + 1.0);

    public ArrayMesh BuildGround(double halfExtent, double cell)
    {
        int n = (int)Math.Round(halfExtent * 2.0 / cell);
        var verts = new Vector3[(n + 1) * (n + 1)];
        var norms = new Vector3[verts.Length];
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                double x = -halfExtent + i * cell, z = -halfExtent + j * cell;
                verts[j * (n + 1) + i] = new Vector3((float)x, (float)GroundHeight(x, z), (float)z);
                double e = 0.5;
                double hx = (GroundHeight(x + e, z) - GroundHeight(x - e, z)) / (2 * e);
                double hz = (GroundHeight(x, z + e) - GroundHeight(x, z - e)) / (2 * e);
                norms[j * (n + 1) + i] = new Vector3((float)-hx, 1f, (float)-hz).Normalized();
            }
        var indices = new int[n * n * 6]; int o = 0;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
                indices[o++] = a; indices[o++] = b; indices[o++] = c;
                indices[o++] = b; indices[o++] = d; indices[o++] = c;
            }
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts; arrays[(int)Mesh.ArrayType.Normal] = norms; arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // ---------------------------------------------------------------- placement

    static float Smooth(float a, float b, float x) { float t = Mathf.Clamp((x - a) / (b - a), 0f, 1f); return t * t * (3f - 2f * t); }

    static float ValueNoise(float x, float z, uint seed)
    {
        int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
        float fx = x - ix, fz = z - iz; fx = fx * fx * (3f - 2f * fx); fz = fz * fz * (3f - 2f * fz);
        float a = Unit(Hash((uint)ix, (uint)iz, seed)), b = Unit(Hash((uint)(ix + 1), (uint)iz, seed));
        float c = Unit(Hash((uint)ix, (uint)(iz + 1), seed)), d = Unit(Hash((uint)(ix + 1), (uint)(iz + 1), seed));
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fz);
    }

    public float LastNearDensity { get; private set; }

    /// <summary>
    /// Returns an array of Dictionaries {buffer: PackedFloat32Array, count: int, aabb: Aabb} (one per chunk).
    /// Buffer layout is MultiMesh 3D transform (12 floats) + custom data (4 floats).
    /// Density(d) = clamp(near*(d0/d)^1.3, floor, near) per m^2, with rank-prefix thinning,
    /// grow-in window and 1/sqrt(keep) scale compensation (capped).
    /// </summary>
    public Godot.Collections.Array BuildField(double reach, double densityScale, int seed, double centerX, double centerZ, double compensationCap)
    {
        double near = 2.5 * densityScale, floorD = 0.15 * densityScale;
        LastNearDensity = (float)near;
        double d0 = 20.0;
        double spacing = 1.0 / Math.Sqrt(near);
        int cells = (int)Math.Ceiling(reach / spacing);
        var chunks = new Dictionary<long, List<float>>();
        var chunkInfo = new Dictionary<long, (float cx, float cz, float size)>();
        const double fineLimit = 150.0; const float fineSize = 32f, coarseSize = 128f;
        for (int jz = -cells; jz <= cells; jz++)
            for (int jx = -cells; jx <= cells; jx++)
            {
                uint hx = Hash((uint)jx, (uint)jz, (uint)seed);
                double x = centerX + (jx + Unit(hx)) * spacing;
                double z = centerZ + (jz + Unit(Hash(hx, 1u, 2u))) * spacing;
                double dx = x - centerX, dz = z - centerZ;
                double dist = Math.Sqrt(dx * dx + dz * dz);
                if (dist > reach) continue;
                double dens = Math.Clamp(near * Math.Pow(d0 / Math.Max(dist, d0), 1.3), floorD, near);
                float keep = (float)(dens / near);
                float rank = Unit(Hash(hx, 3u, 4u));
                if (rank >= keep) continue;
                float growIn = Smooth(0f, 1f, (keep - rank) / Math.Max(0.02f, 0.2f * keep));
                if (growIn < 0.08f) continue;
                float comp = Math.Min(1f / MathF.Sqrt(keep), (float)compensationCap);

                float n = ValueNoise((float)x / 70f, (float)z / 70f, 991u) * 0.65f + ValueNoise((float)x / 23f, (float)z / 23f, 313u) * 0.35f;
                float tallWeight = Smooth(0.42f, 0.58f, n);
                bool tall = Unit(Hash(hx, 5u, 6u)) < tallWeight;
                float height = (tall ? 0.6f : 0.3f) * (0.85f + 0.3f * Unit(Hash(hx, 7u, 8u)));
                float width = 0.45f + height * 1.1f;
                float s = comp * growIn;
                float yaw = Unit(Hash(hx, 9u, 10u)) * Mathf.Tau;
                float cs = MathF.Cos(yaw), sn = MathF.Sin(yaw);
                float y = (float)GroundHeight(x, z);
                int variant = (int)(Unit(Hash(hx, 11u, 12u)) * Variants) % Variants;

                float size = dist < fineLimit ? fineSize : coarseSize;
                int ci = (int)Math.Floor(x / size), cj = (int)Math.Floor(z / size);
                long key = ((dist < fineLimit ? 0L : 1L) << 60) ^ ((long)(ci + 100000) << 30) ^ (long)(cj + 100000);
                if (!chunks.TryGetValue(key, out var list))
                {
                    list = new List<float>(4096); chunks[key] = list;
                    chunkInfo[key] = ((ci + 0.5f) * size, (cj + 0.5f) * size, size);
                }
                float sx = width * s, sy = height * s;
                // Row-major 3x4: basis columns scaled; Y rotation by yaw.
                list.Add(cs * sx); list.Add(0f); list.Add(sn * sx); list.Add((float)x);
                list.Add(0f); list.Add(sy); list.Add(0f); list.Add(y);
                list.Add(-sn * sx); list.Add(0f); list.Add(cs * sx); list.Add((float)z);
                // custom: phase, variant, tall flag, random
                list.Add(Unit(Hash(hx, 13u, 14u))); list.Add(variant); list.Add(tall ? 1f : 0f); list.Add(Unit(Hash(hx, 15u, 16u)));
            }
        var result = new Godot.Collections.Array();
        foreach (var kv in chunks)
        {
            var list = kv.Value; int count = list.Count / 16;
            var info = chunkInfo[kv.Key];
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < count; i++) { float yy = list[i * 16 + 7]; minY = Math.Min(minY, yy); maxY = Math.Max(maxY, yy); }
            var d = new Godot.Collections.Dictionary
            {
                ["buffer"] = list.ToArray(),
                ["count"] = count,
                ["aabb"] = new Aabb(new Vector3(info.cx - info.size * 0.5f - 2f, minY - 0.2f, info.cz - info.size * 0.5f - 2f), new Vector3(info.size + 4f, maxY - minY + 2.5f, info.size + 4f)),
            };
            result.Add(d);
        }
        return result;
    }

    static double Opt(Godot.Collections.Dictionary o, string k, double d) => o.ContainsKey(k) ? (double)o[k] : d;

    /// <summary>
    /// P0b field builder. Options: reach, density_scale, seed, comp_cap, near_density (0 = legacy P0 curve),
    /// near_full/near_end (m, where the near boost starts/finishes blending into the legacy (d0/d)^1.3 curve),
    /// near_width/near_height (clump size multipliers inside the near zone), lean (instance up-vector lean),
    /// low_density/low_full/low_end (short upright low layer). Distance bands use their own lattices so the
    /// rank-prefix thinning stays deterministic and reach-independent.
    /// </summary>
    public Godot.Collections.Array BuildFieldV2(Godot.Collections.Dictionary o)
    {
        double reach = Opt(o, "reach", 400), ds = Opt(o, "density_scale", 1), cap = Opt(o, "comp_cap", 2.5);
        uint seed = (uint)(int)Opt(o, "seed", 20260);
        double nearD = Opt(o, "near_density", 0) * ds, nearFull = Opt(o, "near_full", 8), nearEnd = Opt(o, "near_end", 45);
        double nearW = Opt(o, "near_width", 1), nearH = Opt(o, "near_height", 1), lean = Opt(o, "lean", 0);
        double lowD = Opt(o, "low_density", 0) * ds, lowFull = Opt(o, "low_full", 8), lowEnd = Opt(o, "low_end", 22);
        double baseNear = 2.5 * ds, floorD = 0.15 * ds, d0 = 20.0;
        LastNearDensity = (float)Math.Max(nearD, baseNear);
        double Legacy(double d) => Math.Clamp(baseNear * Math.Pow(d0 / Math.Max(d, d0), 1.3), floorD, baseNear);
        double Dens(double d)
        {
            double b = Legacy(d);
            if (nearD <= baseNear) return b;
            return b + (nearD - b) * (1.0 - Smooth((float)nearFull, (float)nearEnd, (float)d));
        }
        var chunks = new Dictionary<long, List<float>>();
        var chunkInfo = new Dictionary<long, (float cx, float cz, float size)>();
        const double fineLimit = 150.0; const float fineSize = 32f, coarseSize = 128f;

        void Emit(uint hx, double x, double z, double dist, float height, float width, float s, bool low)
        {
            float yaw = Unit(Hash(hx, 9u, 10u)) * Mathf.Tau;
            float cs = MathF.Cos(yaw), sn = MathF.Sin(yaw);
            float y = (float)GroundHeight(x, z);
            int variant = (int)(Unit(Hash(hx, 11u, 12u)) * Variants) % Variants;
            float lx = (Unit(Hash(hx, 17u, 18u)) - 0.5f) * 2f * (float)lean, lz = (Unit(Hash(hx, 19u, 20u)) - 0.5f) * 2f * (float)lean;
            float size = dist < fineLimit ? fineSize : coarseSize;
            int ci = (int)Math.Floor(x / size), cj = (int)Math.Floor(z / size);
            long key = ((dist < fineLimit ? 0L : 1L) << 60) ^ ((long)(ci + 100000) << 30) ^ (long)(cj + 100000);
            if (!chunks.TryGetValue(key, out var list))
            {
                list = new List<float>(4096); chunks[key] = list;
                chunkInfo[key] = ((ci + 0.5f) * size, (cj + 0.5f) * size, size);
            }
            float sx = width * s, sy = height * s;
            list.Add(cs * sx); list.Add(lx * sy); list.Add(sn * sx); list.Add((float)x);
            list.Add(0f); list.Add(sy); list.Add(0f); list.Add(y);
            list.Add(-sn * sx); list.Add(lz * sy); list.Add(cs * sx); list.Add((float)z);
            list.Add(Unit(Hash(hx, 13u, 14u))); list.Add(variant); list.Add(low ? 2f : 0f); list.Add(Unit(Hash(hx, 15u, 16u)));
        }

        double[] edges = { 0.0, 30.0, 100.0, reach };
        for (int band = 0; band < 3; band++)
        {
            double r0 = edges[band], r1 = Math.Min(edges[band + 1], reach);
            if (r0 >= reach || r1 <= r0) continue;
            double maxDens = Dens(r0);
            double spacing = 1.0 / Math.Sqrt(maxDens);
            int cells = (int)Math.Ceiling(r1 / spacing);
            uint bseed = seed + (uint)band * 7919u;
            for (int jz = -cells; jz <= cells; jz++)
                for (int jx = -cells; jx <= cells; jx++)
                {
                    uint hx = Hash((uint)jx, (uint)jz, bseed);
                    double x = (jx + Unit(hx)) * spacing;
                    double z = (jz + Unit(Hash(hx, 1u, 2u))) * spacing;
                    double dist = Math.Sqrt(x * x + z * z);
                    if (dist < r0 || dist >= r1) continue;
                    double dens = Dens(dist);
                    float keep = (float)(dens / maxDens);
                    float rank = Unit(Hash(hx, 3u, 4u));
                    if (rank >= keep) continue;
                    float growIn = Smooth(0f, 1f, (keep - rank) / Math.Max(0.02f, 0.2f * keep));
                    if (growIn < 0.08f) continue;
                    float compKeep = (float)Math.Min(1.0, dens / baseNear);
                    float comp = Math.Min(1f / MathF.Sqrt(compKeep), (float)cap);
                    float nz = Smooth((float)nearFull, (float)nearEnd, (float)dist);
                    float n = ValueNoise((float)x / 70f, (float)z / 70f, 991u) * 0.65f + ValueNoise((float)x / 23f, (float)z / 23f, 313u) * 0.35f;
                    bool tall = Unit(Hash(hx, 5u, 6u)) < Smooth(0.42f, 0.58f, n);
                    float height = (tall ? 0.6f : 0.3f) * (0.85f + 0.3f * Unit(Hash(hx, 7u, 8u))) * Mathf.Lerp((float)nearH, 1f, nz);
                    float width = (0.45f + (tall ? 0.6f : 0.3f) * 1.1f) * Mathf.Lerp((float)nearW, 1f, nz);
                    Emit(hx, x, z, dist, height, width, comp * growIn, false);
                }
        }
        if (lowD > 0.0)
        {
            double spacing = 1.0 / Math.Sqrt(lowD);
            int cells = (int)Math.Ceiling(lowEnd / spacing);
            for (int jz = -cells; jz <= cells; jz++)
                for (int jx = -cells; jx <= cells; jx++)
                {
                    uint hx = Hash((uint)jx, (uint)jz, seed + 4242u);
                    double x = (jx + Unit(hx)) * spacing;
                    double z = (jz + Unit(Hash(hx, 1u, 2u))) * spacing;
                    double dist = Math.Sqrt(x * x + z * z);
                    if (dist >= lowEnd || dist >= reach) continue;
                    float keep = 1f - Smooth((float)lowFull, (float)lowEnd, (float)dist);
                    float rank = Unit(Hash(hx, 3u, 4u));
                    if (rank >= keep) continue;
                    float growIn = Smooth(0f, 1f, (keep - rank) / Math.Max(0.02f, 0.2f * keep));
                    if (growIn < 0.08f) continue;
                    float height = 0.11f + 0.09f * Unit(Hash(hx, 7u, 8u));
                    float width = 0.30f + 0.18f * Unit(Hash(hx, 21u, 22u));
                    Emit(hx, x, z, dist, height, width, growIn, true);
                }
        }
        var result = new Godot.Collections.Array();
        foreach (var kv in chunks)
        {
            var list = kv.Value; int count = list.Count / 16;
            var info = chunkInfo[kv.Key];
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < count; i++) { float yy = list[i * 16 + 7]; minY = Math.Min(minY, yy); maxY = Math.Max(maxY, yy); }
            result.Add(new Godot.Collections.Dictionary
            {
                ["buffer"] = list.ToArray(),
                ["count"] = count,
                ["aabb"] = new Aabb(new Vector3(info.cx - info.size * 0.5f - 2f, minY - 0.2f, info.cz - info.size * 0.5f - 2f), new Vector3(info.size + 4f, maxY - minY + 2.5f, info.size + 4f)),
            });
        }
        return result;
    }

    // ---------------------------------------------------------------- P0c hero layer

    public const float HeroUnitHeight = 0.45f;   // tuft footprint scales with instance height / this
    public const float HeroRootRange = 0.8f;     // COLOR.rg encodes blade root offset as 0.5 + o / this
    public int HeroMeshTriangles { get; private set; }

    /// <summary>
    /// One tuft of tapered, curved solid blades (4 rows + tip, 9 vertices / 7 triangles per blade). Heights are in
    /// unit tuft space (0..1, scaled by the instance Y scale); widths are metres at unit scale (UV2.x half width).
    /// </summary>
    public ArrayMesh BuildHeroTuftMesh(int blades, int seed, float footprint, float halfWidth)
    {
        var verts = new List<Vector3>(); var normals = new List<Vector3>(); var uv = new List<Vector2>(); var uv2 = new List<Vector2>(); var cols = new List<Color>(); var idx = new List<int>();
        var rng = new Rng(Hash((uint)seed, 9001u, (uint)blades));
        float[] rowT = { 0f, 0.3f, 0.6f, 0.85f };
        float[] rowW = { 1f, 0.88f, 0.62f, 0.32f };
        for (int b = 0; b < blades; b++)
        {
            // Fibonacci-ish disc placement for an even footprint, jittered.
            float rr = footprint * MathF.Sqrt((b + 0.5f) / blades) * rng.Range(0.8f, 1.1f);
            float ra = b * 2.39996f + rng.Range(-0.4f, 0.4f);
            float rx = rr * MathF.Cos(ra), rz = rr * MathF.Sin(ra);
            // Lean direction: mostly outward from the tuft centre, partly random.
            float la = Mathf.Lerp(ra, rng.Range(0f, Mathf.Tau), 0.35f);
            var l = new Vector3(MathF.Cos(la), 0f, MathF.Sin(la));
            var u = new Vector3(l.Z, 0f, -l.X);
            float hb = rng.Range(0.55f, 1.0f);
            float bend = rng.Range(0.18f, 0.5f) * hb;
            float w0 = halfWidth * rng.Range(0.8f, 1.2f) * (0.7f + 0.3f * hb);
            float r = rng.Next();
            int baseIdx = verts.Count;
            for (int i = 0; i <= rowT.Length; i++)
            {
                bool tip = i == rowT.Length;
                float t = tip ? 1f : rowT[i];
                var c = new Vector3(rx, t * hb, rz) + l * (bend * t * t);
                verts.Add(c); normals.Add(l);
                uv.Add(new Vector2(tip ? 0f : -1f, t));
                uv2.Add(new Vector2(tip ? 0.0003f : w0 * rowW[i], r));
                cols.Add(new Color(0.5f + rx / HeroRootRange, 0.5f + rz / HeroRootRange, 0f, 1f));
                if (!tip)
                {
                    verts.Add(c); normals.Add(l);
                    uv.Add(new Vector2(1f, t));
                    uv2.Add(new Vector2(w0 * rowW[i], r));
                    cols.Add(new Color(0.5f + rx / HeroRootRange, 0.5f + rz / HeroRootRange, 0f, 1f));
                }
            }
            // Vertex layout: rows 0..3 as (left,right) pairs at baseIdx + 2*i, tip at baseIdx + 8.
            for (int i = 0; i < rowT.Length - 1; i++)
            {
                int i0 = baseIdx + i * 2, i1 = i0 + 1, i2 = i0 + 2, i3 = i0 + 3;
                idx.AddRange(new[] { i0, i2, i1, i1, i2, i3 });
            }
            int last = baseIdx + (rowT.Length - 1) * 2;
            idx.AddRange(new[] { last, last + 2, last + 1 });
        }
        HeroMeshTriangles = idx.Count / 3;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = cols.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uv.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.CustomAabb = new Aabb(new Vector3(-0.7f, 0f, -0.7f), new Vector3(1.4f, 1.3f, 1.4f));
        return mesh;
    }

    /// <summary>
    /// Deterministic hero cell: depends only on (cx, cz, seed, density, cellSize) and the terrain, never on the camera.
    /// Returns {buffer (16 floats per instance), count, aabb}. Height follows the same biome rule as the cards
    /// (tall/short noise, 0.3 / 0.6 m) times heightMul.
    /// </summary>
    public Godot.Collections.Dictionary BuildHeroCell(int cx, int cz, double cellSize, double density, int seed, double heightMul)
    {
        int n = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(density * cellSize * cellSize)));
        double step = cellSize / n;
        var list = new List<float>(n * n * 16);
        float minY = float.MaxValue, maxY = float.MinValue;
        uint s = (uint)seed;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                uint hx = Hash((uint)(cx * n + i), (uint)(cz * n + j), s);
                double x = cx * cellSize + (i + Unit(hx)) * step;
                double z = cz * cellSize + (j + Unit(Hash(hx, 1u, 2u))) * step;
                float nz = ValueNoise((float)x / 70f, (float)z / 70f, 991u) * 0.65f + ValueNoise((float)x / 23f, (float)z / 23f, 313u) * 0.35f;
                bool tall = Unit(Hash(hx, 5u, 6u)) < Smooth(0.42f, 0.58f, nz);
                float height = (tall ? 0.6f : 0.3f) * (0.85f + 0.3f * Unit(Hash(hx, 7u, 8u))) * (float)heightMul;
                float sx = height / HeroUnitHeight * (0.9f + 0.2f * Unit(Hash(hx, 21u, 22u)));
                float yaw = Unit(Hash(hx, 9u, 10u)) * Mathf.Tau;
                float cs = MathF.Cos(yaw), sn = MathF.Sin(yaw);
                float y = (float)GroundHeight(x, z);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                list.Add(cs * sx); list.Add(0f); list.Add(sn * sx); list.Add((float)x);
                list.Add(0f); list.Add(height); list.Add(0f); list.Add(y);
                list.Add(-sn * sx); list.Add(0f); list.Add(cs * sx); list.Add((float)z);
                list.Add(Unit(Hash(hx, 13u, 14u))); list.Add(0f); list.Add(0f); list.Add(Unit(Hash(hx, 15u, 16u)));
            }
        if (minY > maxY) { minY = 0f; maxY = 0f; }
        return new Godot.Collections.Dictionary
        {
            ["buffer"] = list.ToArray(),
            ["count"] = list.Count / 16,
            ["aabb"] = new Aabb(new Vector3((float)(cx * cellSize) - 1f, minY - 0.1f, (float)(cz * cellSize) - 1f), new Vector3((float)cellSize + 2f, maxY - minY + 1.2f, (float)cellSize + 2f)),
        };
    }
}
