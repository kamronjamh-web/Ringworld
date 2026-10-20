using System;

namespace RingworldTechDemo.Shadows;

/// <summary>Which light the height shadow currently serves (global ringworld_height_shadow_state.w).</summary>
public enum HeightShadowLight { None = 0, Sun = 1, Planet = 2 }

/// <summary>What the shader globals show: layer indices of the front/previous arrays layers (-1 = none) and the crossfade 0..1 (display = mix(prev, front, t)).</summary>
public readonly record struct HeightShadowPublished(int Front, int Prev, float Crossfade, HeightShadowLight Light, ulong Generation);

/// <summary>
/// Pure state machine of the three array layers (front / previous / build) shared by both levels. A build writes only the build layer; the layer
/// the globals point to are changed in one <see cref="Publish"/> call (the main thread then writes them all in the same frame). The invariant
/// <c>Build is neither Front nor Prev</c> holds at every call, so no global ever points at a layer that is being written. A finished build is
/// only published when the previous crossfade has reached t = 1 (or the publish is a snap), so the layer that becomes the next build target is never
/// still sampled by an unfinished crossfade.
/// </summary>
public sealed class HeightShadowLayerRoles
{
    public const int LayerCount = 3;
    public int Front { get; private set; } = -1;
    public int Prev { get; private set; } = -1;
    public int Build { get; private set; }
    public bool BuildInFlight { get; private set; }
    public bool BuildComplete { get; private set; }
    public float Crossfade { get; private set; } = 1f;
    public ulong Generation { get; private set; }
    public HeightShadowLight Light { get; private set; } = HeightShadowLight.None;

    public HeightShadowPublished Current => new(Front, Prev, Crossfade, Light, Generation);
    public bool CanBeginBuild => !BuildInFlight && !BuildComplete;
    public bool CrossfadeActive => Front >= 0 && Prev >= 0 && Crossfade < 1f;

    public void Reset()
    {
        Front = -1; Prev = -1; Build = 0; BuildInFlight = false; BuildComplete = false; Crossfade = 1f; Light = HeightShadowLight.None;
    }

    /// <summary>Starts a build and returns the layer it writes.</summary>
    public int BeginBuild()
    {
        if (!CanBeginBuild) throw new InvalidOperationException("a build is already in flight or waiting to publish");
        BuildInFlight = true;
        return Build;
    }

    /// <summary>All dispatches of the build have been recorded on the render thread.</summary>
    public void CompleteBuild()
    {
        if (!BuildInFlight) throw new InvalidOperationException("no build in flight");
        BuildInFlight = false; BuildComplete = true;
    }

    /// <summary>Drops an unpublished build (settings or world changed). The globals are not touched.</summary>
    public void DiscardBuild() { BuildInFlight = false; BuildComplete = false; }

    public bool CanPublish(bool snap) => BuildComplete && (snap || Front < 0 || Prev < 0 || Crossfade >= 1f);

    /// <summary>Makes the finished build the front layer. A snap (first publish, band, light source change) shows it immediately with no previous layer.</summary>
    public HeightShadowPublished Publish(bool snap, HeightShadowLight light)
    {
        if (!CanPublish(snap)) throw new InvalidOperationException("nothing to publish or the previous crossfade is still running");
        int oldFront = Front;
        Front = Build;
        bool crossfade = !snap && oldFront >= 0;
        Prev = crossfade ? oldFront : -1;
        Crossfade = crossfade ? 0f : 1f;
        Build = FirstFree(Front, Prev);
        BuildComplete = false;
        Light = light;
        Generation++;
        return Current;
    }

    /// <summary>Marks the globals invalid without touching the layers (light off, disabled): the next publish is a snap.</summary>
    public void Invalidate() { Front = -1; Prev = -1; Crossfade = 1f; Light = HeightShadowLight.None; }

    public void Advance(float deltaSeconds, float crossfadeSeconds)
    {
        if (Prev < 0 || Crossfade >= 1f) return;
        Crossfade = crossfadeSeconds <= 1e-4f ? 1f : Math.Min(1f, Crossfade + Math.Max(0f, deltaSeconds) / crossfadeSeconds);
    }

    private static int FirstFree(int a, int b)
    {
        for (int i = 0; i < LayerCount; i++) if (i != a && i != b) return i;
        return 0;
    }

    /// <summary>True when the invariant holds (used by tests).</summary>
    public bool InvariantHolds => Build != Front && Build != Prev && Build is >= 0 and < LayerCount && (Front is >= -1 and < LayerCount) && (Prev is >= -1 and < LayerCount)
        && (Front != Prev || Front < 0);
}

/// <summary>Walks the rows of a build over its levels (L0 rows, then L1 rows).</summary>
public sealed class HeightShadowBuildCursor
{
    public int LevelCount { get; }
    public int Level { get; private set; }
    public int Row { get; private set; }
    public bool Done => Level >= LevelCount;
    public long RowsIssued { get; private set; }
    public HeightShadowBuildCursor(int levelCount) { LevelCount = levelCount; }

    /// <summary>Next slice of at most <paramref name="rows"/> rows of the current level (never crosses a level boundary).</summary>
    public (int Level, int Row, int Count) Take(int rows)
    {
        if (Done) return (LevelCount, 0, 0);
        int count = Math.Clamp(rows, 1, RingHeightShadowLattice.TextureSize - Row);
        var slice = (Level, Row, count);
        Row += count; RowsIssued += count;
        if (Row >= RingHeightShadowLattice.TextureSize) { Level++; Row = 0; }
        return slice;
    }
}

/// <summary>
/// Adaptive slicing rule. <c>RowsPerFrame</c> (default 64) is the cap. The scheduler keeps a per-level estimate of GPU milliseconds per row (a peak-following
/// estimate of the render thread's own timestamp pairs around each slice; until the first measurement arrives the 4070-calibrated <see cref="ModelMsPerRow"/> is used) and
/// issues <c>floor(budget / msPerRow)</c> rows rounded down to a multiple of 8 (one workgroup row), clamped to [8, RowsPerFrame]. At noon the cost is
/// ~0.01 ms per 64 rows so the cap rules; at 3 degrees sun L0 costs ~3.5 us per row on the RTX 4070 so ~40 rows fit 0.15 ms and a GPU 8x slower
/// shrinks to the 8-row floor. The build just takes more frames.
/// </summary>
public static class HeightShadowSlicing
{
    public const int MinRows = 8;
    public const double MeasurementDecay = 0.15;

    public static int Rows(int maxRows, double budgetMs, double msPerRow)
    {
        int cap = Math.Max(MinRows, maxRows);
        if (!double.IsFinite(msPerRow) || msPerRow <= 0 || !double.IsFinite(budgetMs) || budgetMs <= 0) return cap;
        int rows = (int)Math.Floor(budgetMs / msPerRow / MinRows) * MinRows;
        return Math.Clamp(rows, MinRows, cap);
    }

    /// <summary>Peak-following estimate: rises to a slower sample at once (cost varies a lot between rows of terrain), decays 15% per sample towards faster ones, so a slice's worst case stays near the budget.</summary>
    public static double Smooth(double previous, double sample) => !double.IsFinite(previous) || previous <= 0 || sample >= previous ? sample : previous + (sample - previous) * MeasurementDecay;

    /// <summary>Estimated ms per window row on the development RTX 4070 (whole-level measurements of S4b, see the plan). Elevation in radians at the camera.</summary>
    public static double ModelMsPerRow(int level, double sunElevationRadians)
    {
        double tan = Math.Tan(Math.Clamp(sunElevationRadians, 0.0087, 1.5));
        return level == 0 ? 0.00022 * (1.0 + 0.9 / tan) : 0.00017 * (1.0 + 0.3 / tan);
    }
}

/// <summary>Double-precision math of the ringworld_height_shadow_frame global and the bounds/params globals (no Godot types; verified against RingLodGeometrySnapshot).</summary>
public static class HeightShadowFrameMath
{
    /// <summary>
    /// Builds the 3x4 world -> anchor-tangent matrix (row major, 12 doubles). f = M * (world, 1) = (rho sin d, across - Ac, R - rho cos d) with
    /// d = ring angle - anchor angle, rho = radial distance from the ring axis, R the floor radius, across the canonical across coordinate. Receivers
    /// recover around-local metres = R * atan(f.x, R - f.z), height above the floor = R - hypot(f.x, R - f.z). The translation is relative to the anchor
    /// floor point, so every term stays below ~24 km and float32 is enough.
    /// </summary>
    /// <param name="inverseBasis">row-major inverse of the grid basis (world to ring-local)</param>
    /// <param name="origin">GridGlobalTransform.Origin - FloatingOriginWorldOffset</param>
    /// <param name="anchorAbsoluteLocal">absolute local position of the floating-origin canonical anchor (zero when none)</param>
    public static double[] BuildFrame(ReadOnlySpan<double> inverseBasis, ReadOnlySpan<double> origin, ReadOnlySpan<double> anchorAbsoluteLocal, ReadOnlySpan<double> ringCentreLocal,
        double floorRadius, double acrossBase, long anchorAroundMeters, long anchorAcrossMeters)
    {
        double theta = anchorAroundMeters / floorRadius;
        double c = Math.Cos(theta), s = Math.Sin(theta);
        Span<double> ea = stackalloc double[] { -s, 0, c };
        Span<double> ec = stackalloc double[] { 0, 1, 0 };
        Span<double> up = stackalloc double[] { -c, 0, -s };
        double[] v0 = new double[3];
        for (int i = 0; i < 3; i++)
            v0[i] = -(inverseBasis[i * 3] * origin[0] + inverseBasis[i * 3 + 1] * origin[1] + inverseBasis[i * 3 + 2] * origin[2]) + anchorAbsoluteLocal[i] - ringCentreLocal[i];
        var m = new double[12];
        for (int row = 0; row < 3; row++)
        {
            ReadOnlySpan<double> e = row == 0 ? ea : row == 1 ? ec : up;
            for (int j = 0; j < 3; j++) m[row * 4 + j] = e[0] * inverseBasis[j] + e[1] * inverseBasis[3 + j] + e[2] * inverseBasis[6 + j];
            m[row * 4 + 3] = e[0] * v0[0] + e[1] * v0[1] + e[2] * v0[2];
        }
        m[7] -= acrossBase + anchorAcrossMeters;
        m[11] += floorRadius;
        return m;
    }

    public static (double X, double Y, double Z) Apply(ReadOnlySpan<double> m, double x, double y, double z)
        => (m[0] * x + m[1] * y + m[2] * z + m[3], m[4] * x + m[5] * y + m[6] * z + m[7], m[8] * x + m[9] * y + m[10] * z + m[11]);

    /// <summary>Receiver-side decode of a frame result: around-local metres (relative to the anchor), anchor-local across metres, height above the floor.</summary>
    public static (double Around, double Across, double Height) Decode((double X, double Y, double Z) f, double floorRadius)
    {
        double rho = Math.Sqrt(f.X * f.X + (floorRadius - f.Z) * (floorRadius - f.Z));
        return (floorRadius * Math.Atan2(f.X, floorRadius - f.Z), f.Y, floorRadius - rho);
    }

    /// <summary>Texture coordinates of an anchor-local position, identical to <see cref="RingHeightShadowLattice.Uv"/> with x = A + around.</summary>
    public static (double U, double V) Uv(double aroundLocal, double acrossLocal, int texelMeters)
    {
        double u = ((aroundLocal / texelMeters) + 0.5) / RingHeightShadowLattice.TextureSize;
        double v = ((acrossLocal / texelMeters) + 0.5) / RingHeightShadowLattice.TextureSize;
        return (u - Math.Floor(u), v - Math.Floor(v));
    }

    /// <summary>Anchor-local bilinear-valid bounds (around min, around max, across min, across max) of a window: texel centres, anchor-relative.</summary>
    public static (double AroundMin, double AroundMax, double AcrossMin, double AcrossMax) Bounds(HeightShadowIndexRect rect, int texelMeters, long anchorAroundMeters, long anchorAcrossMeters)
        => (rect.I0 * (double)texelMeters - anchorAroundMeters, (rect.I1 - 1) * (double)texelMeters - anchorAroundMeters,
            rect.J0 * (double)texelMeters - anchorAcrossMeters, (rect.J1 - 1) * (double)texelMeters - anchorAcrossMeters);

    /// <summary>Overlap of two windows of the same level (empty when disjoint).</summary>
    public static HeightShadowIndexRect Intersect(HeightShadowIndexRect a, HeightShadowIndexRect b)
    {
        long i0 = Math.Max(a.I0, b.I0), i1 = Math.Min(a.I1, b.I1), j0 = Math.Max(a.J0, b.J0), j1 = Math.Min(a.J1, b.J1);
        return i1 <= i0 || j1 <= j0 ? new HeightShadowIndexRect(i0, j0, 0, 0) : new HeightShadowIndexRect(i0, j0, (int)(i1 - i0), (int)(j1 - j0));
    }
}
