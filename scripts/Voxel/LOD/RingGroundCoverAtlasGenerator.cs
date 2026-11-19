using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Inputs of the procedural ground-cover atlas (T6 design B2). Only the seed is a parameter; every shape is drawn by the generator's fixed tables.</summary>
public readonly record struct RingGroundCoverAtlasParameters(int Seed)
{
    public static readonly RingGroundCoverAtlasParameters Default = new(1337);

    /// <summary>Stable 64-bit key of the parameters plus <see cref="RingGroundCoverAtlasGenerator.GeneratorVersion"/>.</summary>
    public ulong CacheKey()
    {
        ulong h = 14695981039346656037UL;
        void Mix(uint v) { for (int i = 0; i < 4; i++) { h ^= (v >> (i * 8)) & 0xFFu; h *= 1099511628211UL; } }
        Mix(0x47434F56u); // "GCOV": a key of this atlas never equals a grass atlas key
        Mix((uint)RingGroundCoverAtlasGenerator.GeneratorVersion);
        Mix((uint)Seed);
        return h;
    }
}

/// <summary>CPU-side ground-cover atlas (safe to build on any thread): three layers (flower, fern, tall grass), each a full mip chain. The Godot array texture is created lazily on the main thread.</summary>
public sealed class RingGroundCoverAtlasData
{
    public RingGroundCoverAtlasParameters Parameters { get; init; }
    public int Layers { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int MipCount { get; init; }
    /// <summary>Per layer: all mip levels, RGBA8, concatenated from mip 0 down to 1x1.</summary>
    public byte[][] LayerPixels { get; init; } = Array.Empty<byte[]>();
    /// <summary>FNV-1a 64 of each layer's whole chain (each layer's hash is pinned, so a later reed layer leaves these unchanged).</summary>
    public ulong[] LayerHashes { get; init; } = Array.Empty<ulong>();
    /// <summary>FNV-1a 64 of all layer chains in layer order.</summary>
    public ulong FullHash { get; init; }
    public double BuildMilliseconds { get; init; }

    Texture2DArray? _texture;

    /// <summary>Main thread only. Returns the same texture every call.</summary>
    public Texture2DArray GetTexture()
    {
        if (_texture != null) return _texture;
        var images = new Godot.Collections.Array<Image>();
        foreach (byte[] pixels in LayerPixels) images.Add(Image.CreateFromData(Width, Height, true, Image.Format.Rgba8, pixels));
        var texture = new Texture2DArray();
        Error error = texture.CreateFromImages(images);
        if (error != Error.Ok) GD.PushError("Ground cover atlas texture failed: " + error);
        return _texture = texture;
    }

    /// <summary>Mip 0 of one layer as RGBA8 bytes (Width x Height x 4).</summary>
    public ReadOnlySpan<byte> Mip0(int layer) => LayerPixels[layer].AsSpan(0, Width * Height * 4);
}

/// <summary>
/// Procedural ground-cover card atlas (T6 design B2): a Texture2DArray, layer = kind - 3 (0 flower, 1 fern, 2 tall grass), each 1024 x 512 (4 variants of 256 x 512), RGBA8, full mip chain.
/// R = coverage (the alpha-scissor mask, coverage-preserving mips shared with the grass atlas), G = height gradient, B = per-element random, A = the petal mask (flowers), the seed-head mask
/// (tall grass) or 0 (ferns), mipped as an area mean. Deterministic: integer RNG, fixed loop order. Shapes live in card-width units (x in -0.5..0.5, y in 0..1 up the card) and are clipped to the
/// card mesh outline, like the grass atlas. The grass atlas, its GeneratorVersion 2 and VariantD are not touched.
/// </summary>
public static class RingGroundCoverAtlasGenerator
{
    /// <summary>Bump when the generated pixels of any layer change so cached assets are invalidated. The reed layer (T6-12) is a separate layer version.</summary>
    public const int GeneratorVersion = 1;
    public const int LayerCount = 3;
    public const int Variants = RingGrassCardAtlasGenerator.Variants;
    public const int TileWidth = 256, TileHeight = 512;
    public const int AtlasWidth = TileWidth * Variants, AtlasHeight = TileHeight;
    public const int FlowerLayer = 0, FernLayer = 1, TallGrassLayer = 2;

    public static RingGroundCoverAtlasData Build(RingGroundCoverAtlasParameters parameters)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var layers = new byte[LayerCount][];
        var hashes = new ulong[LayerCount];
        int mips = 0;
        ulong full = 14695981039346656037UL;
        for (int layer = 0; layer < LayerCount; layer++)
        {
            var canvas = new Canvas();
            for (int v = 0; v < Variants; v++)
            {
                canvas.TileX = v * TileWidth;
                var rng = new RingGrassCardAtlasGenerator.Rng(RingGrassCardAtlasGenerator.Hash((uint)parameters.Seed, (uint)(layer * Variants + v), 0x6C0Eu));
                switch (layer)
                {
                    case FlowerLayer: DrawFlowers(canvas, rng, v); break;
                    case FernLayer: DrawFerns(canvas, rng, v); break;
                    default: DrawTallGrass(canvas, rng, v); break;
                }
            }
            // Clip to the mesh outline so the mesh never truncates visible elements.
            for (int v = 0; v < Variants; v++)
                for (int py = 0; py < TileHeight; py++)
                {
                    float hw = RingGrassCardAtlasGenerator.OutlineHalfWidth(1f - (py + 0.5f) / TileHeight);
                    for (int px = 0; px < TileWidth; px++)
                    {
                        int idx = py * AtlasWidth + v * TileWidth + px;
                        if (MathF.Abs((px + 0.5f) / TileWidth - 0.5f) > hw) { canvas.Mask[idx] = 0f; canvas.Alpha[idx] = 0f; }
                        else if (canvas.Mask[idx] <= 0f) canvas.Alpha[idx] = 0f;
                    }
                }
            var levels = RingGrassCardAtlasGenerator.BuildMipChain(canvas.Mask, canvas.Grad, canvas.Rand, AtlasWidth, AtlasHeight, new System.Text.StringBuilder(), canvas.Alpha);
            int total = 0; foreach (byte[] level in levels) total += level.Length;
            var all = new byte[total]; int offset = 0;
            foreach (byte[] level in levels) { Buffer.BlockCopy(level, 0, all, offset, level.Length); offset += level.Length; }
            ulong h = 14695981039346656037UL;
            foreach (byte b in all) { h ^= b; h *= 1099511628211UL; full ^= b; full *= 1099511628211UL; }
            layers[layer] = all; hashes[layer] = h; mips = levels.Count;
        }
        return new RingGroundCoverAtlasData
        {
            Parameters = parameters, Layers = LayerCount, Width = AtlasWidth, Height = AtlasHeight, MipCount = mips, LayerPixels = layers, LayerHashes = hashes, FullHash = full,
            BuildMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
        };
    }

    // ------------------------------------------------------------------ raster

    private sealed class Canvas
    {
        /// <summary>Pixels per card-width unit, for the anti-aliasing ramp.</summary>
        public const float Scale = 340f;
        public readonly float[] Mask = new float[AtlasWidth * AtlasHeight];
        public readonly float[] Grad = new float[AtlasWidth * AtlasHeight];
        public readonly float[] Rand = new float[AtlasWidth * AtlasHeight];
        public readonly float[] Alpha = new float[AtlasWidth * AtlasHeight];
        public int TileX;

        private void Write(int idx, float cov, float grad, float rand, float alpha, bool over)
        {
            if (!(cov > Mask[idx] || (over && cov >= 0.5f))) return;
            Mask[idx] = over ? MathF.Max(Mask[idx], cov) : cov;
            Grad[idx] = grad; Rand[idx] = rand; Alpha[idx] = alpha;
        }

        /// <summary>A tapered capsule from a to b. The gradient is 0.5 t + 0.5 y with t running from tA to tB along it (the grass atlas rule); <paramref name="over"/> lets a head cover a stem.</summary>
        public void Capsule(float ax, float ay, float bx, float by, float hwA, float hwB, float tA, float tB, float rand, float alpha, bool over = false)
        {
            float pad = MathF.Max(hwA, hwB) + 2f / Scale;
            int x0 = Math.Max(0, (int)MathF.Floor((MathF.Min(ax, bx) - pad + 0.5f) * TileWidth)), x1 = Math.Min(TileWidth - 1, (int)MathF.Ceiling((MathF.Max(ax, bx) + pad + 0.5f) * TileWidth));
            int y0 = Math.Max(0, (int)MathF.Floor((1f - MathF.Max(ay, by) - pad) * TileHeight)), y1 = Math.Min(TileHeight - 1, (int)MathF.Ceiling((1f - MathF.Min(ay, by) + pad) * TileHeight));
            float dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float qx = (x + 0.5f) / TileWidth - 0.5f, qy = 1f - (y + 0.5f) / TileHeight;
                    float s = len2 > 0f ? Math.Clamp(((qx - ax) * dx + (qy - ay) * dy) / len2, 0f, 1f) : 0f;
                    float cx = ax + dx * s - qx, cy = ay + dy * s - qy;
                    float dist = MathF.Sqrt(cx * cx + cy * cy);
                    float half = hwA + (hwB - hwA) * s;
                    float cov = Math.Clamp((half - dist) * Scale + 0.5f, 0f, 1f);
                    if (cov <= 0f) continue;
                    float t = tA + (tB - tA) * s;
                    Write(y * AtlasWidth + TileX + x, cov, 0.5f * t + 0.5f * Math.Clamp(qy, 0f, 1f), rand, alpha, over);
                }
        }

        /// <summary>A rotated ellipse (a petal, a bell, a seed head). The gradient is the height of the pixel.</summary>
        public void Ellipse(float cx, float cy, float rx, float ry, float angle, float rand, float alpha)
        {
            float pad = MathF.Max(rx, ry) + 2f / Scale;
            int x0 = Math.Max(0, (int)MathF.Floor((cx - pad + 0.5f) * TileWidth)), x1 = Math.Min(TileWidth - 1, (int)MathF.Ceiling((cx + pad + 0.5f) * TileWidth));
            int y0 = Math.Max(0, (int)MathF.Floor((1f - cy - pad) * TileHeight)), y1 = Math.Min(TileHeight - 1, (int)MathF.Ceiling((1f - cy + pad) * TileHeight));
            float ca = MathF.Cos(angle), sa = MathF.Sin(angle);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float qx = (x + 0.5f) / TileWidth - 0.5f, qy = 1f - (y + 0.5f) / TileHeight;
                    float dx = qx - cx, dy = qy - cy;
                    float u = dx * ca + dy * sa, v = -dx * sa + dy * ca;
                    float f = MathF.Sqrt(u * u / (rx * rx) + v * v / (ry * ry));
                    float gu = u / (rx * rx), gv = v / (ry * ry);
                    float gl = MathF.Sqrt(gu * gu + gv * gv);
                    float dist = f < 1e-5f || gl < 1e-9f ? -MathF.Min(rx, ry) : (f - 1f) * f / gl; // first-order signed distance to the outline
                    float cov = Math.Clamp(-dist * Scale + 0.5f, 0f, 1f);
                    if (cov <= 0f) continue;
                    Write(y * AtlasWidth + TileX + x, cov, 0.5f * Math.Clamp(qy, 0f, 1f) + 0.5f * Math.Clamp(qy, 0f, 1f), rand, alpha, true);
                }
        }
    }

    private static float Bezier(float a, float b, float c, float t) { float o = 1f - t; return o * o * a + 2f * o * t * b + t * t * c; }

    /// <summary>A quadratic curve as a chain of capsules. The half width runs from <paramref name="hwBase"/> to <paramref name="hwTip"/> as (1 - t)^pow; the stroke's t gradient spans t0..t1.</summary>
    private static void Stroke(Canvas c, float x0, float y0, float x1, float y1, float x2, float y2, int segs, float hwBase, float hwTip, float pow, float t0, float t1, float rand, float alpha, bool over = false)
    {
        float px = x0, py = y0, ph = hwTip + (hwBase - hwTip) * 1f;
        for (int i = 1; i <= segs; i++)
        {
            float t = i / (float)segs;
            float x = Bezier(x0, x1, x2, t), y = Bezier(y0, y1, y2, t);
            float hw = hwTip + (hwBase - hwTip) * MathF.Pow(1f - t, pow);
            c.Capsule(px, py, x, y, ph, hw, t0 + (t1 - t0) * (i - 1) / segs, t0 + (t1 - t0) * i / segs, rand, alpha, over);
            px = x; py = y; ph = hw;
        }
    }

    /// <summary>A lanceolate leaf from a base point out to a tip with a drooping arch: widest at 40 % of its length.</summary>
    private static void Leaf(Canvas c, float bx, float by, float side, float angle, float length, float maxHalfWidth, float tBase, float rand)
    {
        float dx = side * MathF.Sin(angle), dy = MathF.Cos(angle);
        float tipX = bx + dx * length, tipY = by + dy * length - 0.12f * length;
        float ctlX = bx + dx * length * 0.55f, ctlY = by + dy * length * 0.75f;
        float px = bx, py = by, ph = 0.0006f;
        const int segs = 10;
        for (int i = 1; i <= segs; i++)
        {
            float t = i / (float)segs;
            float x = Bezier(bx, ctlX, tipX, t), y = Bezier(by, ctlY, tipY, t);
            float hw = 0.0006f + maxHalfWidth * MathF.Pow(MathF.Sin(MathF.PI * MathF.Pow(t, 0.75f)), 0.8f);
            c.Capsule(px, py, x, y, ph, hw, tBase, tBase, rand, 0f);
            px = x; py = y; ph = hw;
        }
    }

    // ------------------------------------------------------------------ flowers

    private static void DrawFlowers(Canvas c, RingGrassCardAtlasGenerator.Rng r, int variant)
    {
        int stems = 3 + (int)(r.Next() * 4f);
        for (int s = 0; s < stems; s++)
        {
            float xr = r.Range(-0.10f, 0.10f);
            float yt = r.Range(0.52f, 0.93f);
            float xt = Math.Clamp(xr + r.Range(-0.16f, 0.16f), -0.30f, 0.30f);
            float x1 = xr + (xt - xr) * 0.3f, y1 = yt * r.Range(0.5f, 0.7f);
            float rand = r.Next();
            float head = r.Range(0.5f, 0.75f);
            Stroke(c, xr, 0f, x1, y1, xt, yt, 14, 0.0075f, 0.0045f, 0.5f, 0f, 1f, rand, 0f);
            // Two or three lanceolate leaves up the lower stem, alternating sides.
            int leaves = 2 + (int)(r.Next() * 2f);
            for (int l = 0; l < leaves; l++)
            {
                float t = 0.10f + 0.40f * l / leaves + r.Range(0f, 0.06f);
                float side = ((s + l) & 1) == 0 ? 1f : -1f;
                Leaf(c, Bezier(xr, x1, xt, t), Bezier(0f, y1, yt, t), side, r.Range(0.6f, 1.1f), r.Range(0.17f, 0.30f), r.Range(0.022f, 0.036f), t, rand);
            }
            switch (variant)
            {
                case 0: Daisy(c, xt, yt, head, r, rand); break;
                case 1: Buttercups(c, xt, yt, head, r, rand); break;
                case 2: Bells(c, xr, x1, xt, y1, yt, head, r, rand); break;
                default: Raceme(c, xr, x1, xt, y1, yt, head, r, rand); break;
            }
        }
    }

    /// <summary>White umbel or daisy: flat radial petals around the stem tip.</summary>
    private static void Daisy(Canvas c, float x, float y, float scale, RingGrassCardAtlasGenerator.Rng r, float rand)
    {
        int petals = 10 + (int)(r.Next() * 3f);
        float turn = r.Next() * MathF.Tau;
        for (int i = 0; i < petals; i++)
        {
            float a = turn + i * MathF.Tau / petals;
            c.Ellipse(x + MathF.Cos(a) * 0.032f * scale, y + MathF.Sin(a) * 0.032f * scale, 0.032f * scale, 0.0105f * scale, a, rand, 1f);
        }
        c.Ellipse(x, y, 0.017f * scale, 0.017f * scale, 0f, rand, 1f);
    }

    /// <summary>Yellow cup cluster (buttercup): three or four cups on short pedicels at the stem tip.</summary>
    private static void Buttercups(Canvas c, float x, float y, float scale, RingGrassCardAtlasGenerator.Rng r, float rand)
    {
        int cups = 3 + (int)(r.Next() * 2f);
        for (int i = 0; i < cups; i++)
        {
            float ox = r.Range(-0.055f, 0.055f) * scale, oy = r.Range(-0.045f, 0.03f) * scale;
            c.Capsule(x, y - 0.01f, x + ox, y + oy, 0.0035f, 0.0028f, 0.9f, 1f, rand, 0f);
            c.Ellipse(x + ox, y + oy + 0.012f * scale, 0.026f * scale, 0.023f * scale, r.Range(-0.4f, 0.4f), rand, 1f);
        }
    }

    /// <summary>Violet-blue bells hanging along the upper stem (campanula).</summary>
    private static void Bells(Canvas c, float xr, float x1, float xt, float y1, float yt, float scale, RingGrassCardAtlasGenerator.Rng r, float rand)
    {
        int bells = 5 + (int)(r.Next() * 3f);
        for (int i = 0; i < bells; i++)
        {
            float t = 0.50f + 0.48f * (i + 0.5f) / bells;
            float sx = Bezier(xr, x1, xt, t), sy = Bezier(0f, y1, yt, t);
            float side = (i & 1) == 0 ? 1f : -1f;
            float bx = sx + side * 0.038f * scale, by = sy - 0.006f;
            c.Capsule(sx, sy, bx, by, 0.0032f, 0.0026f, t, t, rand, 0f);
            c.Ellipse(bx + side * 0.004f, by - 0.026f * scale, 0.019f * scale, 0.029f * scale, side * 0.18f, rand, 1f);
        }
        c.Ellipse(xt, yt + 0.012f, 0.011f * scale, 0.018f * scale, 0f, rand, 1f);
    }

    /// <summary>Pink-magenta raceme (fireweed, loosestrife): small florets up the top of the stem, smaller toward the tip.</summary>
    private static void Raceme(Canvas c, float xr, float x1, float xt, float y1, float yt, float scale, RingGrassCardAtlasGenerator.Rng r, float rand)
    {
        int florets = 10 + (int)(r.Next() * 5f);
        for (int i = 0; i < florets; i++)
        {
            float f = i / (float)florets;
            float t = 0.62f + 0.38f * f;
            float side = (i & 1) == 0 ? 1f : -1f;
            float radius = Mathf.Lerp(0.021f, 0.008f, f) * scale;
            c.Ellipse(Bezier(xr, x1, xt, t) + side * 0.014f * scale, Bezier(0f, y1, yt, t), radius, radius * 1.1f, 0f, rand, 1f);
        }
        c.Ellipse(xt, yt + 0.008f, 0.008f * scale, 0.012f * scale, 0f, rand, 1f);
    }

    // ------------------------------------------------------------------ ferns

    private static void DrawFerns(Canvas c, RingGrassCardAtlasGenerator.Rng r, int variant)
    {
        bool bracken = variant == 3;
        int fronds = bracken ? 4 + (int)(r.Next() * 3f) : 5 + (int)(r.Next() * 5f);
        float pinnaMax = variant switch { 0 => 0.11f, 1 => 0.14f, 2 => 0.095f, _ => 0.16f };
        int pairs = variant switch { 0 => 18, 1 => 14, 2 => 22, _ => 12 };
        for (int f = 0; f < fronds; f++)
        {
            float angle = MathF.PI / 180f * Mathf.Lerp(-68f, 68f, (f + r.Range(-0.3f, 0.3f) + 0.5f) / fronds);
            float length = bracken ? r.Range(0.62f, 0.98f) : r.Range(0.55f, 0.95f);
            float sin = MathF.Sin(angle), cos = MathF.Cos(angle);
            float rx0 = r.Range(-0.04f, 0.04f);
            float tipX = Math.Clamp(rx0 + length * sin * 0.95f, -0.40f, 0.40f);
            float tipY = Math.Clamp(length * (0.75f * cos + 0.05f), 0.15f, 0.96f);
            float ctlX = rx0 + (tipX - rx0) * 0.45f, ctlY = tipY * 0.9f + 0.08f;
            float rand = r.Next();
            Stroke(c, rx0, 0f, ctlX, ctlY, tipX, tipY, 16, 0.0055f, 0.0018f, 0.8f, 0f, 1f, rand, 0f);
            for (int k = 0; k < pairs; k++)
            {
                float t = 0.14f + 0.84f * (k + 0.5f) / pairs;
                float st = (t - 0.14f) / 0.84f;
                float px = Bezier(rx0, ctlX, tipX, t), py = Bezier(0f, ctlY, tipY, t);
                float tx = 2f * ((1f - t) * (ctlX - rx0) + t * (tipX - ctlX)), ty = 2f * ((1f - t) * ctlY + t * (tipY - ctlY));
                float tl = MathF.Sqrt(tx * tx + ty * ty); if (tl < 1e-5f) continue;
                tx /= tl; ty /= tl;
                float nx = -ty, ny = tx;
                float profile = bracken ? MathF.Pow(1f - st, 0.85f) + 0.08f : MathF.Sin(MathF.PI * Math.Clamp(0.15f + 0.85f * st, 0f, 1f));
                float lp = pinnaMax * profile * r.Range(0.9f, 1.1f);
                for (int side = -1; side <= 1; side += 2)
                {
                    float dx = nx * side * 0.82f + tx * 0.57f, dy = ny * side * 0.82f + ty * 0.57f - 0.12f;
                    float dl = MathF.Sqrt(dx * dx + dy * dy); dx /= dl; dy /= dl;
                    float ex = px + dx * lp, ey = py + dy * lp;
                    c.Capsule(px, py, ex, ey, 0.0052f, 0.0014f, t, t, rand, 0f);
                    if (!bracken) continue;
                    // Tripinnate: three small pinnules up each pinna, swept toward the tip.
                    for (int m = 1; m <= 3; m++)
                    {
                        float u = m * 0.25f;
                        float mx = px + dx * lp * u, my = py + dy * lp * u;
                        float ml = lp * 0.34f * (1f - 0.35f * u);
                        for (int ms = -1; ms <= 1; ms += 2)
                        {
                            float qx = -dy * ms * 0.8f + dx * 0.6f, qy = dx * ms * 0.8f + dy * 0.6f;
                            c.Capsule(mx, my, mx + qx * ml, my + qy * ml, 0.0032f, 0.0010f, t, t, rand, 0f);
                        }
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ tall grass

    private static void DrawTallGrass(Canvas c, RingGrassCardAtlasGenerator.Rng r, int variant)
    {
        bool sedge = variant == 0;
        int blades = sedge ? 22 + (int)(r.Next() * 9f) : 14 + (int)(r.Next() * 9f);
        for (int b = 0; b < blades; b++)
        {
            float xr = r.Range(-0.07f, 0.07f);
            float yt = 0.55f + 0.43f * MathF.Pow(r.Next(), 0.6f);
            float reach = RingGrassCardAtlasGenerator.OutlineHalfWidth(yt) * 0.85f;
            float xt = Math.Clamp(xr + r.Range(-1f, 1f) * reach * (sedge ? 1.05f : 0.7f), -reach, reach);
            float w0 = r.Range(0.0075f, 0.0115f) * (sedge ? 1.25f : 1f);
            float rand = r.Next();
            float bendY = r.Range(0.5f, 0.8f), bendX = r.Range(0.05f, sedge ? 0.5f : 0.25f);
            Stroke(c, xr, 0f, xr + (xt - xr) * bendX, yt * bendY, xt, yt, 16, w0, 0.0004f, 0.7f, 0f, 1f, rand, 0f);
        }
        if (sedge) return; // v0: sedge, no heads
        int culms = 6 + (int)(r.Next() * 4f);
        for (int s = 0; s < culms; s++)
        {
            float xr = r.Range(-0.06f, 0.06f);
            float yt = r.Range(0.72f, 0.96f);
            float xt = Math.Clamp(xr + r.Range(-0.08f, 0.08f), -0.20f, 0.20f);
            float rand = r.Next();
            if (variant == 3)
            {
                // Drooping panicle: the culm arches over and the head hangs from its end.
                float side = r.Next() < 0.5f ? -1f : 1f;
                float top = yt - 0.06f;
                float ex = Math.Clamp(xt + side * 0.15f, -0.28f, 0.28f), ey = top - 0.10f;
                Stroke(c, xr, 0f, xr + (xt - xr) * 0.2f, top * 0.8f, xt, top, 12, 0.0042f, 0.0030f, 0.5f, 0f, 0.9f, rand, 0f);
                Stroke(c, xt, top, (xt + ex) * 0.5f + side * 0.02f, top + 0.045f, ex, ey, 8, 0.0030f, 0.0020f, 0.5f, 0.9f, 1f, rand, 0f);
                for (int i = 0; i < 7; i++)
                {
                    float f = (i + 0.5f) / 7f;
                    float hx = Bezier(xt, (xt + ex) * 0.5f + side * 0.02f, ex, 0.35f + 0.65f * f) + (i & 1) * 0.006f, hy = Bezier(top, top + 0.045f, ey, 0.35f + 0.65f * f) - 0.014f;
                    c.Ellipse(hx, hy, 0.012f, 0.021f, side * 0.25f, rand, 1f);
                }
                continue;
            }
            Stroke(c, xr, 0f, xr + (xt - xr) * 0.3f, yt * 0.6f, xt, yt - 0.12f, 14, 0.0042f, 0.0030f, 0.4f, 0f, 1f, rand, 0f);
            float baseY = yt - 0.12f;
            if (variant == 1)
            {
                // Feathery panicle: a rachis with alternating slender spikelets, shorter toward the tip.
                c.Capsule(xt, baseY, xt, yt + 0.02f, 0.0034f, 0.0020f, 1f, 1f, rand, 1f, true);
                for (int i = 0; i < 12; i++)
                {
                    float f = i / 12f;
                    float py = baseY + (yt + 0.02f - baseY) * f;
                    float len = Mathf.Lerp(0.065f, 0.020f, f);
                    for (int side = -1; side <= 1; side += 2)
                        c.Capsule(xt, py, xt + side * len * 0.62f, py + len * 0.78f, 0.0034f, 0.0012f, 1f, 1f, rand, 1f, true);
                }
            }
            else
            {
                // Spike (v2): a dense narrow head with short awns.
                c.Ellipse(xt, baseY + 0.075f, 0.014f, 0.078f, 0f, rand, 1f);
                for (int i = 0; i < 10; i++)
                {
                    float py = baseY + 0.02f + i * 0.0155f;
                    float side = (i & 1) == 0 ? 1f : -1f;
                    c.Capsule(xt, py, xt + side * 0.020f, py + 0.045f, 0.0020f, 0.0007f, 1f, 1f, rand, 1f, true);
                }
            }
        }
    }
}
