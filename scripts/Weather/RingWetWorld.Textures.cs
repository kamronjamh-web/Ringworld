using System;
using Godot;

namespace RingworldTechDemo.Weather;

/// <summary>Procedural textures of the wet world (no assets): the baked ripple atlas (Lagarde's technique) and the tiling puddle noise.</summary>
public partial class RingWetWorld
{
    public const int NoiseSize = 256;
    public const int RippleSize = 256;
    public const int RippleCells = 8;

    private ImageTexture? _rippleTexture, _noiseTexture;

    private void EnsureTextures()
    {
        if (_textured) return;
        _textured = true;
        _rippleTexture = ImageTexture.CreateFromImage(BuildRippleImage());
        _noiseTexture = ImageTexture.CreateFromImage(BuildNoiseImage());
        RenderingServer.GlobalShaderParameterSet(GlobalRippleTexture, _rippleTexture);
        RenderingServer.GlobalShaderParameterSet(GlobalNoiseTexture, _noiseTexture);
    }

    private static uint Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u ^ (uint)seed * 0xC2B2AE3Du;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return h;
        }
    }

    private static float Hash01(int x, int y, int seed) => (Hash(x, y, seed) & 0xFFFFFF) / 16777216.0f;

    /// <summary>
    /// Ripple atlas, RGBA8: R = ring distance from the cell's drop (0 at the drop, 1 at 0.75 cell widths, which is the maximum ring radius), G/B = outward direction * 0.5 + 0.5,
    /// A = the drop's time offset. One jittered drop per cell, tiling over the 4 m tile.
    /// </summary>
    public static Image BuildRippleImage()
    {
        var image = Image.CreateEmpty(RippleSize, RippleSize, true, Image.Format.Rgba8);
        int cell = RippleSize / RippleCells;
        float maxRadius = cell * 0.75f;
        for (int y = 0; y < RippleSize; y++)
            for (int x = 0; x < RippleSize; x++)
            {
                int cx = x / cell, cy = y / cell;
                float dropX = (cx + 0.25f + 0.5f * Hash01(cx, cy, 11)) * cell;
                float dropY = (cy + 0.25f + 0.5f * Hash01(cx, cy, 12)) * cell;
                var offset = new Vector2(x + 0.5f - dropX, y + 0.5f - dropY);
                float distance = offset.Length();
                Vector2 direction = distance > 1e-4f ? offset / distance : Vector2.Right;
                image.SetPixel(x, y, new Color(Mathf.Clamp(distance / maxRadius, 0.0f, 1.0f), direction.X * 0.5f + 0.5f, direction.Y * 0.5f + 0.5f, Hash01(cx, cy, 13)));
            }
        image.GenerateMipmaps();
        return image;
    }

    /// <summary>Tileable value-noise fbm in R8 (four octaves, 4/8/16/32 cells over the 51.2 m tile), normalised to 0..1 with a mild contrast curve.</summary>
    public static Image BuildNoiseImage()
    {
        var image = Image.CreateEmpty(NoiseSize, NoiseSize, true, Image.Format.R8);
        int[] cells = { 4, 8, 16, 32 };
        float[] weights = { 0.50f, 0.28f, 0.15f, 0.07f };
        for (int y = 0; y < NoiseSize; y++)
            for (int x = 0; x < NoiseSize; x++)
            {
                float sum = 0.0f;
                for (int o = 0; o < cells.Length; o++)
                {
                    float fx = (float)x / NoiseSize * cells[o], fy = (float)y / NoiseSize * cells[o];
                    int ix = (int)MathF.Floor(fx), iy = (int)MathF.Floor(fy);
                    float tx = fx - ix, ty = fy - iy;
                    tx = tx * tx * (3.0f - 2.0f * tx); ty = ty * ty * (3.0f - 2.0f * ty);
                    int n = cells[o];
                    float a = Hash01(ix % n, iy % n, 100 + o), b = Hash01((ix + 1) % n, iy % n, 100 + o);
                    float c = Hash01(ix % n, (iy + 1) % n, 100 + o), d = Hash01((ix + 1) % n, (iy + 1) % n, 100 + o);
                    sum += weights[o] * Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
                }
                float v = Mathf.Clamp((sum - 0.5f) * 1.6f + 0.5f, 0.0f, 1.0f);
                image.SetPixel(x, y, new Color(v, 0, 0, 1));
            }
        image.GenerateMipmaps();
        return image;
    }
}
