using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RingworldTechDemo.Vegetation.Canopy;

/// <summary>
/// One crown for the canopy raster (docs/forest-lighting-plan.md section 5). Positions are canonical (around wraps at the ring circumference);
/// altitudes are metres above the floor (canonical vertical), absolute: the builder subtracts the reference altitude.
/// </summary>
/// <param name="Radius">Crown disk radius in metres (instance width / 2).</param>
/// <param name="BaseAltitude">Crown base altitude above the floor.</param>
/// <param name="TopAltitude">Crown top altitude above the floor.</param>
/// <param name="Porosity">Species crown porosity p (sky gap fraction through the crown).</param>
/// <param name="SpeciesFactor">1 broadleaf, 0.6 conifer (scales the transmitted canopy light).</param>
public readonly record struct CanopyTree(ulong Id, double Around, double Across, float Radius, float BaseAltitude, float TopAltitude, float Porosity, float SpeciesFactor);

/// <summary>A built canopy raster: RGBA half texels, full mip chain, tightly packed level after level (level 0 first), ready for Image.CreateFromData(..., true, Rgbah).</summary>
public sealed class CanopyRasterResult
{
    /// <summary>Owned by the builder that made it and overwritten by that builder's next Build: upload or copy it before building again.</summary>
    public required byte[] Data;
    public long AnchorAround, AnchorAcross;
    /// <summary>Metres subtracted from the base and top altitudes before the premultiplied G and B channels were written.</summary>
    public float ReferenceAltitude;
    public int TreeCount;
    public double BuildMilliseconds;

    /// <summary>One decoded channel (0 R, 1 G, 2 B, 3 A) of a texel of a mip level.</summary>
    public float Texel(int level, int x, int y, int channel)
    {
        int size = ForestCanopyRaster.Size >> level;
        int index = (ForestCanopyRaster.LevelOffsetTexels(level) + y * size + x) * 4 + channel;
        return (float)BitConverter.ToHalf(Data, index * 2);
    }
}

/// <summary>
/// Camera-centred canopy raster, pure C# (no Godot types). 512 x 512 texels of 2 m centred on an anchor (canonical around and across snapped to 16 m): texel (i, j)
/// covers anchor-local around [i * 2 - 512, i * 2 - 510) and across [j * 2 - 512, j * 2 - 510); u = (aroundLocal + 512) / 1024, v = (acrossLocal + 512) / 1024.
/// Channels, premultiplied by the blocked fraction b: R = b, G = b * (base - reference), B = b * (top - reference), A = b * species factor,
/// with b = 1 - prod(1 - c_i * (1 - p_i)) over the crowns covering the texel (c_i the area coverage of crown i), and base, top, species the
/// contribution-weighted means. Mips are 2 x 2 box averages of the premultiplied channels. Not thread safe: one build at a time per instance.
/// </summary>
public sealed class ForestCanopyRaster
{
    public const int Size = 512;
    public const int Levels = 10;
    public const float TexelMeters = 2f;
    public const float HalfSizeMeters = Size * TexelMeters * 0.5f;
    public const int AnchorSnapMeters = 16;
    private const float MaxRadius = 24f;

    private static readonly int[] LevelOffsets = BuildLevelOffsets();
    public static int TotalTexels => LevelOffsets[Levels];
    public static int LevelOffsetTexels(int level) => LevelOffsets[level];
    private static int[] BuildLevelOffsets()
    {
        var offsets = new int[Levels + 1];
        for (int l = 0; l < Levels; l++) offsets[l + 1] = offsets[l] + (Size >> l) * (Size >> l);
        return offsets;
    }

    /// <summary>Anchor coordinate: the value snapped to 16 m.</summary>
    public static long SnapAnchor(double meters) => (long)(Math.Round(meters / AnchorSnapMeters) * AnchorSnapMeters);

    /// <summary>Anchor around coordinate snapped to 16 m and wrapped into [0, circumference).</summary>
    public static long SnapAnchorAround(double around, long circumference)
    {
        long snapped = SnapAnchor(around) % circumference;
        return snapped < 0 ? snapped + circumference : snapped;
    }

    // Interleaved per-texel accumulators: one cache line per deposit instead of five.
    private struct Cell { public float Gap, Weight, WeightBase, WeightTop, WeightSpecies; }
    private Cell[]? _cells;
    private float[]? _pyramid;
    private byte[]? _output;

    /// <summary>float -> IEEE half bits, round to nearest even; magnitudes under the half normal range (6.1e-5) flush to zero, overflow clamps to 65504. Much cheaper than the Half operator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ToHalfBits(float value)
    {
        // Branch-free apart from the two range selects (compiled to conditional moves): a = |value| bits; rounding adds 0xFFF plus the lowest kept bit, and the exponent rebias
        // (112 = 127 - 15) is a subtraction, so a mantissa carry rolls into the exponent by itself.
        uint x = BitConverter.SingleToUInt32Bits(value), a = x & 0x7FFFFFFFu, sign = (x >> 16) & 0x8000u;
        uint half = (a + 0xFFFu + ((a >> 13) & 1u) - (112u << 23)) >> 13;
        half = a < (113u << 23) ? 0u : half;
        half = a >= (143u << 23) ? 0x7BFFu : half;
        return (ushort)(sign | half);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public CanopyRasterResult Build(ReadOnlySpan<CanopyTree> trees, long anchorAround, long anchorAcross, double circumference, float referenceAltitude)
    {
        long start = Stopwatch.GetTimestamp();
        const int n0 = Size * Size;
        _cells ??= new Cell[n0];
        _pyramid ??= new float[TotalTexels * 4];
        _output ??= new byte[TotalTexels * 4 * 2];
        Cell[] cells = _cells;
        float[] p = _pyramid;
        byte[] bytes = _output;
        Array.Fill(cells, new Cell { Gap = 1f });
        Array.Clear(p);
        Array.Clear(bytes);

        // Deterministic order: by Id, never by input order (equal Ids keep an unspecified relative order).
        var keys = new ulong[trees.Length];
        var order = new int[trees.Length];
        for (int i = 0; i < trees.Length; i++) { keys[i] = trees[i].Id; order[i] = i; }
        Array.Sort(keys, order);
        Span<float> scratch = stackalloc float[32 * 32];
        for (int i = 0; i < order.Length; i++) Splat(cells, trees[order[i]], anchorAround, anchorAcross, circumference, referenceAltitude, scratch);

        // A texel with R = 0 is zero in every channel (premultiplied), so empty texels are skipped everywhere (both buffers start cleared).
        Span<ushort> halves = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
        for (int i = 0; i < n0; i++)
        {
            ref Cell cell = ref cells[i];
            if (cell.Weight <= 0f) continue;
            int o = i * 4;
            float b = 1f - cell.Gap, inv = b / cell.Weight;
            float g = cell.WeightBase * inv, t = cell.WeightTop * inv, a = cell.WeightSpecies * inv;
            p[o] = b; p[o + 1] = g; p[o + 2] = t; p[o + 3] = a;
            halves[o] = ToHalfBits(b); halves[o + 1] = ToHalfBits(g); halves[o + 2] = ToHalfBits(t); halves[o + 3] = ToHalfBits(a);
        }
        for (int level = 1; level < Levels; level++)
        {
            int size = Size >> level, srcSize = size * 2, src = LevelOffsets[level - 1] * 4, dst = LevelOffsets[level] * 4;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int a = src + ((2 * y) * srcSize + 2 * x) * 4, c = a + srcSize * 4, d = dst + (y * size + x) * 4;
                    if (p[a] + p[a + 4] + p[c] + p[c + 4] <= 0f) continue;
                    for (int k = 0; k < 4; k++)
                    {
                        float v = (p[a + k] + p[a + 4 + k] + p[c + k] + p[c + 4 + k]) * 0.25f;
                        p[d + k] = v; halves[d + k] = ToHalfBits(v);
                    }
                }
        }

        return new CanopyRasterResult
        {
            Data = bytes, AnchorAround = anchorAround, AnchorAcross = anchorAcross, ReferenceAltitude = referenceAltitude, TreeCount = trees.Length,
            BuildMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency,
        };
    }

    // Area-weighted coverage. A crown under 1 m radius (area under 4 m^2) is spread bilinearly over the four texels around it, so a sub-texel crown still deposits its
    // whole area. A larger crown takes a one-texel linear ramp across its edge (distance to the texel centre) and is then rescaled so its deposited area is exactly pi r^2.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Splat(Cell[] cells, in CanopyTree tree, long anchorAround, long anchorAcross, double circumference, float reference, Span<float> scratch)
    {
        double around = tree.Around - anchorAround;
        if (around > circumference * 0.5) around -= circumference; else if (around < -circumference * 0.5) around += circumference;
        double across = tree.Across - anchorAcross;
        float r = Math.Clamp(tree.Radius, 0.05f, MaxRadius);
        double x = (around + HalfSizeMeters) / TexelMeters, y = (across + HalfSizeMeters) / TexelMeters;
        double reach = (r + 1f) / TexelMeters + 1.0;
        if (x < -reach || y < -reach || x > Size + reach || y > Size + reach) return;
        float area = MathF.PI * r * r / (TexelMeters * TexelMeters); // in texels
        float baseA = tree.BaseAltitude - reference, topA = tree.TopAltitude - reference;
        float gapScale = 1f - tree.Porosity;

        if (r < 1f)
        {
            double gx = x - 0.5, gy = y - 0.5;
            int i0 = (int)Math.Floor(gx), j0 = (int)Math.Floor(gy);
            float fx = (float)(gx - i0), fy = (float)(gy - j0);
            for (int k = 0; k < 4; k++)
            {
                int i = i0 + (k & 1), j = j0 + (k >> 1);
                if ((uint)i >= Size || (uint)j >= Size) continue;
                float weight = ((k & 1) == 0 ? 1f - fx : fx) * ((k >> 1) == 0 ? 1f - fy : fy);
                Deposit(cells, j * Size + i, area * weight, gapScale, baseA, topA, tree.SpeciesFactor);
            }
            return;
        }

        double half = (r + 1f) / TexelMeters;
        int bi0 = (int)Math.Ceiling(x - 0.5 - half), bi1 = (int)Math.Floor(x - 0.5 + half);
        int bj0 = (int)Math.Ceiling(y - 0.5 - half), bj1 = (int)Math.Floor(y - 0.5 + half);
        int wi = bi1 - bi0 + 1, wj = bj1 - bj0 + 1;
        if (wi <= 0 || wj <= 0 || wi > 32 || wj > 32) return;
        // A linear ramp of half-width 1 m adds pi/3 m^2 to the disk, so the ramp is built on the radius that cancels it; fully covered texels stay exactly 1
        // and only the edge texels are rescaled to make the total area exact.
        float ramp = MathF.Sqrt(MathF.Max(r * r - 1f / 3f, 0.01f));
        float inner = ramp > 1f ? (ramp - 1f) * (ramp - 1f) : -1f, outer = (ramp + 1f) * (ramp + 1f);
        float full = 0f, edge = 0f;
        for (int j = 0; j < wj; j++)
            for (int i = 0; i < wi; i++)
            {
                float dx = (float)((bi0 + i + 0.5 - x) * TexelMeters), dy = (float)((bj0 + j + 0.5 - y) * TexelMeters);
                float d2 = dx * dx + dy * dy;
                float c = d2 >= outer ? 0f : d2 <= inner ? 1f : (ramp + 1f - MathF.Sqrt(d2)) * 0.5f;
                scratch[j * 32 + i] = c;
                if (c >= 1f) full += 1f; else edge += c;
            }
        if (full + edge <= 0f) return;
        float scale = edge > 0f ? Math.Clamp((area - full) / edge, 0f, 4f) : 1f;
        for (int j = 0; j < wj; j++)
        {
            int gj = bj0 + j;
            if ((uint)gj >= Size) continue;
            for (int i = 0; i < wi; i++)
            {
                int gi = bi0 + i;
                float c = scratch[j * 32 + i];
                if ((uint)gi >= Size || c <= 0f) continue;
                Deposit(cells, gj * Size + gi, c >= 1f ? 1f : c * scale, gapScale, baseA, topA, tree.SpeciesFactor);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Deposit(Cell[] cells, int index, float coverage, float gapScale, float baseA, float topA, float species)
    {
        float w = MathF.Min(coverage, 1f) * gapScale;
        ref Cell cell = ref cells[index];
        cell.Gap *= 1f - w;
        cell.Weight += w; cell.WeightBase += w * baseA; cell.WeightTop += w * topA; cell.WeightSpecies += w * species;
    }
}
