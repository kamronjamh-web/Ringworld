using System;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T6-8 (docs/work-orders/terrain-vegetation/T6.md, design A9): C# side of foliage_tint_gpu_test.gd. A CPU reference of the shader's tint terms
/// (materials/ring_foliage_tint.gdshaderinc), so the fixture can choose ranks and stand positions with a known tint and compare the measured factor with the prediction.
/// </summary>
public partial class FoliageTintGpuTest : RefCounted
{
    private const int CellMeters = 512;
    private const int CellMask = 127;

    private static uint Pcg(uint input)
    {
        uint state = unchecked(input * 747796405u + 2891336453u);
        uint word = unchecked(((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u);
        return (word >> 22) ^ word;
    }

    private static float Cell(int cx, int cy)
    {
        uint x = (uint)(cx & CellMask), y = (uint)(cy & CellMask);
        uint h = Pcg(x ^ Pcg(unchecked(y + 0x9E3779B9u)));
        return (h >> 8) / 16777215.0f * 2.0f - 1.0f;
    }

    /// <summary>Stand value in [-1, 1] at a canonical position (metres): value noise on 512 m cells with a smooth fade; the cell indices wrap every 128 cells.</summary>
    public float StandValue(double around, double across)
    {
        double px = around / CellMeters, py = across / CellMeters;
        double ix = Math.Floor(px), iy = Math.Floor(py);
        float fx = (float)(px - ix), fy = (float)(py - iy);
        float ux = fx * fx * (3.0f - 2.0f * fx), uy = fy * fy * (3.0f - 2.0f * fy);
        int cx = (int)ix, cy = (int)iy;
        float a = Mathf.Lerp(Cell(cx, cy), Cell(cx + 1, cy), ux);
        float b = Mathf.Lerp(Cell(cx, cy + 1), Cell(cx + 1, cy + 1), ux);
        return Mathf.Lerp(a, b, uy);
    }

    /// <summary>Per-instance terms (v_i, h_i) of a rank.</summary>
    public Vector2 InstanceValues(float rank)
        => new(2.0f * Fract(rank * 4271.0f) - 1.0f, 2.0f * Fract(rank * 7919.0f + 0.37f) - 1.0f);

    /// <summary>The tint multiplier (r, g, b) at strength s for a rank and a stand value.</summary>
    public Vector3 Multiplier(float strength, float rank, float standValue)
    {
        Vector2 i = InstanceValues(rank);
        float value = 1.0f + strength * (0.08f * i.X + 0.06f * standValue);
        float hue = strength * (i.Y + standValue);
        return new Vector3(value * (1.0f + 0.03f * hue), value, value * (1.0f - 0.06f * hue));
    }

    private static float Fract(float x) => x - MathF.Floor(x);
}
