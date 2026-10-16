using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Inputs of the procedural tuft atlas. Defaults are grass-cards variant D.</summary>
public readonly record struct RingGrassCardAtlasParameters(int BladesPerVariant, int Seed, float RootSpread, float MinTall, float WidthMultiplier)
{
    public static readonly RingGrassCardAtlasParameters VariantD = new(56, 1337, 0.16f, 0.24f, 1.2f);

    public RingGrassCardAtlasParameters Normalized() => this with { BladesPerVariant = Math.Clamp(BladesPerVariant, 4, 96) };

    /// <summary>Stable 64-bit key of the normalized parameters plus <see cref="RingGrassCardAtlasGenerator.GeneratorVersion"/>.</summary>
    public ulong CacheKey()
    {
        RingGrassCardAtlasParameters p = Normalized();
        ulong h = 14695981039346656037UL;
        void Mix(uint v) { for (int i = 0; i < 4; i++) { h ^= (v >> (i * 8)) & 0xFFu; h *= 1099511628211UL; } }
        Mix((uint)RingGrassCardAtlasGenerator.GeneratorVersion);
        Mix((uint)p.BladesPerVariant); Mix((uint)p.Seed);
        Mix((uint)BitConverter.SingleToInt32Bits(p.RootSpread)); Mix((uint)BitConverter.SingleToInt32Bits(p.MinTall)); Mix((uint)BitConverter.SingleToInt32Bits(p.WidthMultiplier));
        return h;
    }
}

/// <summary>CPU-side atlas result (safe to build on any thread). Godot resources are created lazily on the main thread.</summary>
public sealed class RingGrassCardAtlasData
{
    public RingGrassCardAtlasParameters Parameters { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int MipCount { get; init; }
    /// <summary>All mip levels, RGBA8, concatenated from mip 0 down to 1x1.</summary>
    public byte[] Pixels { get; init; } = Array.Empty<byte>();
    /// <summary>FNV-1a 32 of mip 0 (same definition as the spike's LastAtlasHash).</summary>
    public string Mip0Hash { get; init; } = "";
    /// <summary>FNV-1a 64 of the whole chain.</summary>
    public ulong FullHash { get; init; }
    public string CoverageReport { get; init; } = "";
    public float OutlineWasteFraction { get; init; }
    public double BuildMilliseconds { get; init; }

    Image? _image; ImageTexture? _texture;

    /// <summary>Main thread only. Returns the same texture every call.</summary>
    public ImageTexture GetTexture()
    {
        if (_texture != null) return _texture;
        _image = Image.CreateFromData(Width, Height, true, Image.Format.Rgba8, Pixels);
        _texture = ImageTexture.CreateFromImage(_image);
        return _texture;
    }

    /// <summary>Fraction of pixels of one variant tile at/above the 0.5 alpha cutoff on the R channel of the given mip.</summary>
    public float Coverage(int mip, int variant)
    {
        int offset = 0, lw = Width, lh = Height;
        for (int m = 0; m < mip; m++) { offset += lw * lh * 4; lw = Math.Max(1, lw / 2); lh = Math.Max(1, lh / 2); }
        int tileW = Math.Max(1, lw / RingGrassCardAtlasGenerator.Variants);
        int hit = 0;
        for (int y = 0; y < lh; y++)
            for (int x = 0; x < tileW; x++)
                if (Pixels[offset + (y * lw + variant * tileW + x) * 4] >= 128) hit++;
        return hit / (float)(tileW * lh);
    }
}

/// <summary>
/// Procedural grass-card tuft atlas (RGBA8 2048x512, 4 variants, full mip chain). R = coverage mask,
/// G = height gradient, B = per-blade random, A = constant. Ported from the P0 spike (GrassCardsSpikeAssets.BuildAtlasFull)
/// with identical math; mips preserve coverage at the 0.5 cutoff. Deterministic: integer RNG, fixed loop order.
/// </summary>
public static class RingGrassCardAtlasGenerator
{
    /// <summary>Bump when the generated pixels change so cached assets are invalidated.</summary>
    public const int GeneratorVersion = 1;
    public const int Variants = 4;
    public const int TileSize = 512;
    public const int AtlasWidth = TileSize * Variants;
    public const int AtlasHeight = TileSize;
    public const float AlphaCutoff = 0.5f;

    // Card outline rows (height 0, .5, 1) and half widths in card-width units. Shared with RingGrassCardMeshFactory.
    internal static readonly float[] RowY = { 0f, 0.5f, 1f };
    internal static readonly float[] RowHalfWidth = { 0.26f, 0.46f, 0.40f };

    internal static float OutlineHalfWidth(float y)
    {
        y = Mathf.Clamp(y, 0f, 1f);
        return y <= 0.5f ? Mathf.Lerp(RowHalfWidth[0], RowHalfWidth[1], y / 0.5f) : Mathf.Lerp(RowHalfWidth[1], RowHalfWidth[2], (y - 0.5f) / 0.5f);
    }

    internal static uint Hash(uint a, uint b, uint c)
    {
        uint h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u) * 0x85EBCA6Bu ^ (c + 0x165667B1u) * 0xC2B2AE35u;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return h;
    }

    internal sealed class Rng
    {
        uint s;
        public Rng(uint seed) { s = seed == 0 ? 1u : seed; }
        public float Next() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s & 0xFFFFFF) / 16777216f; }
        public float Range(float a, float b) => a + (b - a) * Next();
    }

    public static RingGrassCardAtlasData Build(RingGrassCardAtlasParameters parameters)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        RingGrassCardAtlasParameters p = parameters.Normalized();
        int w = AtlasWidth, h = AtlasHeight;
        var mask = new float[w * h];
        var grad = new float[w * h];
        var rand = new float[w * h];
        for (int v = 0; v < Variants; v++)
        {
            var rng = new Rng(Hash((uint)p.Seed, (uint)v, 77u));
            DrawTuft(mask, grad, rand, v * TileSize, p.BladesPerVariant, rng, p.RootSpread, p.MinTall, p.WidthMultiplier);
        }
        // Clip to the mesh outline so the mesh never truncates visible blades.
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
        float waste = inside > 0 ? 1f - covered / (float)inside : 0f;

        // Mip chain: box filter for G/B/A; R is box filtered then rescaled per variant tile so the fraction of
        // pixels at/above the cutoff equals mip 0 (quantile scale).
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
            if (lw >= Variants)
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
                        var sorted = vals.ToArray(); Array.Sort(sorted); Array.Reverse(sorted);
                        int k = Math.Clamp((int)Math.Round(cov0[v] * n) - 1, 0, n - 1);
                        float t = sorted[k];
                        scale = t > 1e-5f ? Math.Clamp(AlphaCutoff / t, 1f, 32f) : 1f;
                        for (int y = 0; y < tileH; y++) for (int x = 0; x < tileW; x++)
                        { int i = y * lw + v * tileW + x; scaledR[i] = Math.Min(1f, rawR[i] * scale); }
                    }
                    int cut = 0;
                    for (int y = 0; y < tileH; y++) for (int x = 0; x < tileW; x++) if (scaledR[y * lw + v * tileW + x] >= AlphaCutoff) cut++;
                    report.Append(" v").Append(v).Append(" naive=").Append((naiveCov[v] * 100f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append("% kept=").Append((cut / (float)n * 100f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append('%');
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
        int total = 0; foreach (var b in levelsData) total += b.Length;
        var all = new byte[total]; int o = 0;
        foreach (var b in levelsData) { Buffer.BlockCopy(b, 0, all, o, b.Length); o += b.Length; }
        uint hsh = 2166136261u; foreach (var b in levelsData[0]) { hsh ^= b; hsh *= 16777619u; }
        ulong full = 14695981039346656037UL; foreach (byte b in all) { full ^= b; full *= 1099511628211UL; }
        return new RingGrassCardAtlasData
        {
            Parameters = p, Width = w, Height = h, MipCount = levelsData.Count, Pixels = all,
            Mip0Hash = hsh.ToString("X8"), FullHash = full, CoverageReport = report.ToString(), OutlineWasteFraction = waste,
            BuildMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
        };
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
}
