using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Shadows;

/// <summary>Rectangle of lattice texels in UNWRAPPED index space (I = around, J = across), half-open at I0 + Width / J0 + Height.</summary>
public readonly record struct HeightShadowIndexRect(long I0, long J0, int Width, int Height)
{
    public long Count => (long)Width * Height;
    public long I1 => I0 + Width;
    public long J1 => J0 + Height;
    public bool Contains(long i, long j) => i >= I0 && i < I1 && j >= J0 && j < J1;
}

/// <summary>Rectangle in toroidal slot space (column = I mod N, row = J mod N); never wraps.</summary>
public readonly record struct HeightShadowSlotRect(int X, int Y, int Width, int Height);

/// <summary>N x N window of one level, centred on unwrapped texel (Ic, Jc): I in [Ic - N/2, Ic + N/2 - 1].</summary>
public readonly record struct HeightShadowWindow(long Ic, long Jc)
{
    public long I0 => Ic - RingHeightShadowLattice.TextureSize / 2;
    public long I1 => Ic + RingHeightShadowLattice.TextureSize / 2 - 1;
    public long J0 => Jc - RingHeightShadowLattice.TextureSize / 2;
    public long J1 => Jc + RingHeightShadowLattice.TextureSize / 2 - 1;
    public HeightShadowIndexRect Rect => new(I0, J0, RingHeightShadowLattice.TextureSize, RingHeightShadowLattice.TextureSize);
}

/// <summary>Sun direction in the tangent frame at an anchor: up = toward the ring axis (-radial), around = +angle tangent, across = ring axis.</summary>
public readonly record struct HeightShadowSunFrame(double Up, double Around, double Across);

/// <summary>
/// Pure math of the S4 terrain height-shadow clipmap lattice (docs/shadow-revamp-plan.md S4, scratchpad s4_spec.md section 2). Texel (I, J) of a level with
/// texel size t has its centre at around = I * t, across = J * t. I is an UNWRAPPED long (the camera crossing the seam continues at I >= C / t, the ring wrap
/// is applied only when the height source is queried), so a seam crossing is an ordinary recentre and never a refill. Slots are toroidal (I mod N, J mod N);
/// the anchor A = P * floor(a / P), P = N * t1, keeps shader-side offsets small while uv = fract(((x - A) / t + 0.5) / N) hits the slot centre exactly.
/// No Godot types: usable from headless tests.
/// </summary>
public static class RingHeightShadowLattice
{
    public const int TextureSize = 1024;
    public const int RecentreThreshold = TextureSize / 8;
    public const int LevelRatio = 4;
    public const int MaxTexelMeters = 32;
    /// <summary>Mips 1..MipCount are kept (mip m is TextureSize / 2^m square); mip 0 is the height buffer itself.</summary>
    public const int MipCount = 5;
    public const int ScatterChunkFloats = 65536; // 256 KB staging
    public static readonly double[] BandUpMeters = { 300.0, 1500.0, 6000.0 };
    public const double BandDownFraction = 0.75;

    // ---- modular helpers ----

    public static long PosMod(long value, long modulus) { long r = value % modulus; return r < 0 ? r + modulus : r; }
    public static int SlotOf(long unwrappedIndex) => (int)PosMod(unwrappedIndex, TextureSize);
    public static long WrapAroundMeters(long unwrappedIndex, int texelMeters, long circumference) => PosMod(unwrappedIndex * texelMeters, circumference);
    public static int ClampAcrossMeters(long acrossIndex, int texelMeters, int widthMeters) => (int)Math.Clamp(acrossIndex * texelMeters, 0L, (long)widthMeters - 2);
    public static long NearestIndex(double meters, int texelMeters) => (long)Math.Floor(meters / texelMeters + 0.5);

    /// <summary>
    /// Nearest unwrapped index of a wrapped ring position to <paramref name="previousIndex"/> (adds the right k * C on a seam crossing).
    /// Requires the texel size to divide the circumference.
    /// </summary>
    public static long UnwrapNear(double wrappedMeters, long previousIndex, int texelMeters, long circumference)
    {
        double wrapped = wrappedMeters - Math.Floor(wrappedMeters / circumference) * circumference;
        double k = Math.Round((previousIndex * (double)texelMeters - wrapped) / circumference);
        return NearestIndex(wrapped + k * circumference, texelMeters);
    }

    // ---- configuration asserts ----

    public static int Level1Texel(int level0TexelMeters) => level0TexelMeters * LevelRatio;

    /// <summary>Throws unless texel sizes t0 and t1 = 4 * t0 divide the circumference and both level windows fit between the walls.</summary>
    public static void AssertConfiguration(long circumference, int widthMeters, int level0TexelMeters)
    {
        if (level0TexelMeters < 1 || (level0TexelMeters & (level0TexelMeters - 1)) != 0 || level0TexelMeters > MaxTexelMeters)
            throw new ArgumentException($"level 0 texel size {level0TexelMeters} m must be a power of two in [1, {MaxTexelMeters}]");
        int t1 = Level1Texel(level0TexelMeters);
        if (circumference % t1 != 0) throw new ArgumentException($"texel size {t1} m does not divide the circumference {circumference} m");
        if (circumference % level0TexelMeters != 0) throw new ArgumentException($"texel size {level0TexelMeters} m does not divide the circumference {circumference} m");
        if (widthMeters - 2 < (long)TextureSize * t1) throw new ArgumentException($"ring width {widthMeters} m cannot hold a {TextureSize} x {t1} m window");
    }

    // ---- anchors and uv ----

    /// <summary>Anchor period P = N * t1 in metres.</summary>
    public static long AnchorPeriodMeters(int level1TexelMeters) => (long)TextureSize * level1TexelMeters;
    public static long AnchorMeters(double unwrappedMeters, int level1TexelMeters)
    {
        long period = AnchorPeriodMeters(level1TexelMeters);
        return period * (long)Math.Floor(unwrappedMeters / period);
    }

    /// <summary>uv = fract(((x - A) / t + 0.5) / N) for one axis. x - A is the small anchor-local offset the shaders see.</summary>
    public static double Uv(double xMeters, long anchorMeters, int texelMeters)
    {
        double v = (((xMeters - anchorMeters) / texelMeters) + 0.5) / TextureSize;
        return v - Math.Floor(v);
    }

    /// <summary>Slot (0..N-1) addressed by a uv produced by <see cref="Uv"/>.</summary>
    public static int SlotFromUv(double uv) => (int)Math.Min(Math.Floor(uv * TextureSize), TextureSize - 1);

    // ---- windows and recentring ----

    public static (long Ic, long Jc) CentreIndices(double unwrappedAroundMeters, double acrossMeters, int texelMeters)
        => (NearestIndex(unwrappedAroundMeters, texelMeters), NearestIndex(acrossMeters, texelMeters));

    /// <summary>Clamps Jc so the whole window stays between the walls: J * t in [0, W - 2].</summary>
    public static long ClampJc(long jc, int texelMeters, int widthMeters)
    {
        long jMax = (widthMeters - 2L) / texelMeters;
        long lo = TextureSize / 2, hi = jMax - (TextureSize / 2 - 1);
        if (hi < lo) return jMax / 2;
        return Math.Clamp(jc, lo, hi);
    }

    /// <summary>The window after a recentre request: unchanged while the target moved no more than N/8 on both axes, else centred on the (J-clamped) target.</summary>
    public static HeightShadowWindow NextWindow(HeightShadowWindow current, long wantIc, long wantJc, int texelMeters, int widthMeters, out bool moved)
    {
        long jc = ClampJc(wantJc, texelMeters, widthMeters);
        moved = Math.Abs(wantIc - current.Ic) > RecentreThreshold || Math.Abs(jc - current.Jc) > RecentreThreshold;
        return moved ? new HeightShadowWindow(wantIc, jc) : current;
    }

    /// <summary>
    /// Appends the rectangles that must be (re)filled when going from <paramref name="old"/> to <paramref name="next"/>: the whole new window when
    /// there is no old window or the windows no longer overlap, else an I strip (full height) plus a J strip over the overlapping I range.
    /// Returns the exact texel count, which equals N^2 minus the overlap.
    /// </summary>
    public static long StripRects(HeightShadowWindow? old, HeightShadowWindow next, List<HeightShadowIndexRect> output)
    {
        if (old is not { } o || Math.Abs(next.Ic - o.Ic) >= TextureSize || Math.Abs(next.Jc - o.Jc) >= TextureSize)
        {
            output.Add(next.Rect);
            return (long)TextureSize * TextureSize;
        }
        long total = 0;
        long dI = next.Ic - o.Ic, dJ = next.Jc - o.Jc;
        if (dI > 0) { var r = new HeightShadowIndexRect(o.I1 + 1, next.J0, (int)dI, TextureSize); output.Add(r); total += r.Count; }
        else if (dI < 0) { var r = new HeightShadowIndexRect(next.I0, next.J0, (int)-dI, TextureSize); output.Add(r); total += r.Count; }
        long overlapI0 = Math.Max(next.I0, o.I0), overlapI1 = Math.Min(next.I1, o.I1);
        int overlapWidth = (int)(overlapI1 - overlapI0 + 1);
        if (dJ > 0) { var r = new HeightShadowIndexRect(overlapI0, o.J1 + 1, overlapWidth, (int)dJ); output.Add(r); total += r.Count; }
        else if (dJ < 0) { var r = new HeightShadowIndexRect(overlapI0, next.J0, overlapWidth, (int)-dJ); output.Add(r); total += r.Count; }
        return total;
    }

    /// <summary>Splits an index rect (width/height &lt;= N) into up to 4 non-wrapping slot rects; returns how many were written.</summary>
    public static int SplitToSlots(HeightShadowIndexRect rect, Span<HeightShadowSlotRect> output)
    {
        int x = SlotOf(rect.I0), y = SlotOf(rect.J0);
        int w1 = Math.Min(rect.Width, TextureSize - x), h1 = Math.Min(rect.Height, TextureSize - y);
        int w2 = rect.Width - w1, h2 = rect.Height - h1;
        int count = 0;
        output[count++] = new HeightShadowSlotRect(x, y, w1, h1);
        if (w2 > 0) output[count++] = new HeightShadowSlotRect(0, y, w2, h1);
        if (h2 > 0) output[count++] = new HeightShadowSlotRect(x, 0, w1, h2);
        if (w2 > 0 && h2 > 0) output[count++] = new HeightShadowSlotRect(0, 0, w2, h2);
        return count;
    }

    // ---- altitude bands ----

    /// <summary>
    /// Band k = 0..3 from the height above terrain (&lt; 300 m / 1.5 km / 6 km / above). Going up requires exceeding the band's upper limit; going back
    /// down requires dropping below 75% of it (25% hysteresis), so a camera hovering at a limit never flips.
    /// </summary>
    public static int UpdateBand(int currentBand, double heightAboveTerrainMeters)
    {
        int band = Math.Clamp(currentBand, 0, BandUpMeters.Length);
        while (band < BandUpMeters.Length && heightAboveTerrainMeters > BandUpMeters[band]) band++;
        while (band > 0 && heightAboveTerrainMeters < BandUpMeters[band - 1] * BandDownFraction) band--;
        return band;
    }

    /// <summary>Level 0 texel size for a band: base * 2^band capped at 32 m.</summary>
    public static int TexelForBand(int baseTexelMeters, int band) => Math.Min(baseTexelMeters << Math.Clamp(band, 0, 8), MaxTexelMeters);

    // ---- mip layout (mips 1..5 packed contiguously in one buffer) ----

    public static int MipSize(int mip) => TextureSize >> mip;
    public static int MipOffset(int mip) { int offset = 0; for (int m = 1; m < mip; m++) offset += MipSize(m) * MipSize(m); return offset; }
    public static int MipBufferFloats => MipOffset(MipCount + 1);

    /// <summary>
    /// Reference max-mips of a slot-space height array (the exact result the mips shader must produce). Mip 1 cell (x, y) = max over texels
    /// 2x..2x+2 by 2y..2y+2 (wrapping), the footprint of every bilinear sample whose texel-centre coordinate u = uv * N - 0.5 lies in [2x, 2x + 2);
    /// mips 2..5 are plain 2x2 maxima of the mip below. Returned array is the packed mip buffer.
    /// </summary>
    public static float[] BuildReferenceMips(float[] heights)
    {
        var mips = new float[MipBufferFloats];
        int n1 = MipSize(1), o1 = MipOffset(1);
        for (int y = 0; y < n1; y++)
            for (int x = 0; x < n1; x++)
            {
                float m = float.NegativeInfinity;
                for (int dy = 0; dy < 3; dy++)
                    for (int dx = 0; dx < 3; dx++)
                        m = Math.Max(m, heights[((2 * y + dy) & (TextureSize - 1)) * TextureSize + ((2 * x + dx) & (TextureSize - 1))]);
                mips[o1 + y * n1 + x] = m;
            }
        for (int mip = 2; mip <= MipCount; mip++)
        {
            int n = MipSize(mip), src = MipSize(mip - 1), o = MipOffset(mip), so = MipOffset(mip - 1);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int b = so + 2 * y * src + 2 * x;
                    mips[o + y * n + x] = Math.Max(Math.Max(mips[b], mips[b + 1]), Math.Max(mips[b + src], mips[b + src + 1]));
                }
        }
        return mips;
    }

    // ---- sun direction in the anchor tangent frame ----

    /// <summary>
    /// Projects a ring-local direction (ring axis = Y, position at angle theta = (R cos, y, R sin)) onto the tangent frame at <paramref name="theta"/>:
    /// up = -(cos, 0, sin), e_a = (-sin, 0, cos), e_c = Y. Done in double on the CPU.
    /// </summary>
    public static HeightShadowSunFrame ProjectToTangentFrame(double x, double y, double z, double theta)
    {
        double c = Math.Cos(theta), s = Math.Sin(theta);
        return new HeightShadowSunFrame(-(x * c + z * s), -x * s + z * c, y);
    }

    /// <summary>Re-expresses the frame components at an around offset of <paramref name="delta"/> radians (a_l / R) from the anchor.</summary>
    public static HeightShadowSunFrame RotateByAroundAngle(HeightShadowSunFrame sun, double delta)
    {
        double c = Math.Cos(delta), s = Math.Sin(delta);
        return new HeightShadowSunFrame(sun.Up * c - sun.Around * s, sun.Around * c + sun.Up * s, sun.Across);
    }
}
