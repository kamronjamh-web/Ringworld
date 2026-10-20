using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;
using RingworldTechDemo.Shadows;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Tools;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo;

/// <summary>
/// Shadow revamp S4a coverage (docs/shadow-revamp-plan.md S4): lattice math, CPU clipmap fill, recentre strips, bands, apertures and fill timing
/// (<see cref="Run"/>, headless-safe), plus the GPU upload / mip contract on a local RenderingDevice (<see cref="RunGpu"/>, needs a real Vulkan renderer).
/// </summary>
public partial class HeightShadowLatticeTest : RefCounted
{
    private const long Circumference = 31_415_936L;
    private const int Width = 318_000;
    private int _checks;

    public bool Run()
    {
        try
        {
            VerifyConfigurationAsserts();
            VerifySlotAnchorRoundTrips();
            VerifySeamUnwrap();
            VerifyStripCounts();
            VerifyWindowClamp();
            VerifyBandHysteresis();
            VerifySunFrame();
            VerifyReferenceMipsBound();
            VerifySeamCrossingFill();
            VerifyApertureCarve();
            VerifySeaLevelClamp();
            ReportFillTiming();
            GD.Print($"HEIGHT_SHADOW_LATTICE=PASS checks={_checks}");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("HEIGHT_SHADOW_LATTICE=FAIL " + exception.Message + "\n" + exception.StackTrace);
            return false;
        }
    }

    // ---- pure lattice ----

    private void VerifyConfigurationAsserts()
    {
        foreach (int t0 in new[] { 2, 4, 8, 16, 32 }) RingHeightShadowLattice.AssertConfiguration(Circumference, Width, t0);
        Require(Throws(() => RingHeightShadowLattice.AssertConfiguration(Circumference, Width, 64)), "t0 = 64 exceeds the 32 m cap");
        Require(Throws(() => RingHeightShadowLattice.AssertConfiguration(Circumference, Width, 3)), "t0 = 3 is not a power of two");
        Require(Throws(() => RingHeightShadowLattice.AssertConfiguration(31_415_936L + 2, Width, 4)), "C + 2 is not divisible by 16");
        Require(Throws(() => RingHeightShadowLattice.AssertConfiguration(1_000_000L, Width, 32)), "1,000,000 is not divisible by 128");
        Require(Throws(() => RingHeightShadowLattice.AssertConfiguration(Circumference, 100_000, 32)), "a 100 km ring cannot hold a 131 km window");
        Require(Circumference % 128 == 0 && Circumference % 256 != 0, "Certified C is divisible by 128 but not 256 (documents why t1 is capped at 128 m)");
    }

    private void VerifySlotAnchorRoundTrips()
    {
        var cameras = new double[] { 100.0, Circumference * 0.5, Circumference - 1000.0, Circumference - 1.0, Circumference + 500.0, 2.0 * Circumference - 3.0 };
        foreach (int t0 in new[] { 2, 4, 8, 16, 32 })
            foreach (double camera in cameras)
                for (int level = 0; level < 2; level++)
                {
                    int t = level == 0 ? t0 : t0 * 4, t1 = t0 * 4;
                    long anchor = RingHeightShadowLattice.AnchorMeters(camera, t1);
                    long period = RingHeightShadowLattice.AnchorPeriodMeters(t1);
                    Require(anchor % t == 0 && anchor % period == 0, "anchor must be a multiple of every level's texel and of P");
                    long centre = RingHeightShadowLattice.NearestIndex(camera, t);
                    for (long i = centre - 512; i < centre + 512; i += 3)
                    {
                        double x = (double)i * t;
                        double uv = RingHeightShadowLattice.Uv(x, anchor, t);
                        int slot = RingHeightShadowLattice.SlotOf(i);
                        Require(RingHeightShadowLattice.SlotFromUv(uv) == slot, $"slot round trip failed t={t} I={i} camera={camera}");
                        Require(Math.Abs(uv * RingHeightShadowLattice.TextureSize - (slot + 0.5)) < 1e-6, "uv is not the slot centre");
                        Require(Math.Abs(x - anchor) < 3.0 * period, "anchor-local offset out of range");
                    }
                    // Same round trip for the wrapped world position: the shader sees x - A of the unwrapped camera frame only.
                    long wrappedIndex = RingHeightShadowLattice.WrapAroundMeters(centre, t, Circumference) / t;
                    Require(wrappedIndex >= 0 && wrappedIndex < Circumference / t, "wrapped index outside [0, C/t)");
                }
    }

    private void VerifySeamUnwrap()
    {
        const int t = 4;
        long below = (Circumference - 1000) / t;
        Require(RingHeightShadowLattice.UnwrapNear(Circumference - 1000.0, 0, t, Circumference) == (Circumference - 1000) / t - Circumference / t,
            "unwrap from index 0 should land one lap below");
        long unwrapped = RingHeightShadowLattice.UnwrapNear(500.0, below, t, Circumference);
        Require(unwrapped == Circumference / t + 125, "camera crossing the seam must continue at C/t + 125, got " + unwrapped);
        Require(RingHeightShadowLattice.WrapAroundMeters(unwrapped, t, Circumference) == 500, "wrapped meters of the continued index");
        Require(RingHeightShadowLattice.WrapAroundMeters(-1, t, Circumference) == Circumference - t, "negative index wraps to C - t");
        long second = RingHeightShadowLattice.UnwrapNear(Circumference - 1000.0, unwrapped, t, Circumference);
        Require(second == below, "camera moving back across the seam returns to the lap below: got " + second);
        Require(RingHeightShadowLattice.UnwrapNear(500.0, second + 3 * Circumference / t, t, Circumference) == 4 * (Circumference / t) + 125, "lap counting beyond the first seam");
    }

    private void VerifyStripCounts()
    {
        const int n = RingHeightShadowLattice.TextureSize;
        var rng = new Random(11);
        var deltas = new List<(int, int)> { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1), (128, 0), (-128, 0), (129, 0), (0, 129), (300, -200), (-1023, 5), (1023, 1023), (1024, 0), (0, -1024), (5000, 5000) };
        for (int k = 0; k < 40; k++) deltas.Add((rng.Next(-1100, 1100), rng.Next(-1100, 1100)));
        foreach ((int dI, int dJ) in deltas)
        {
            var old = new HeightShadowWindow(7_853_734L, 20_000L);
            var next = new HeightShadowWindow(old.Ic + dI, old.Jc + dJ);
            var rects = new List<HeightShadowIndexRect>();
            long count = RingHeightShadowLattice.StripRects(old, next, rects);
            long overlap = Math.Max(0, n - Math.Abs(dI)) * (long)Math.Max(0, n - Math.Abs(dJ));
            Require(count == (long)n * n - overlap, $"strip count for d=({dI},{dJ}): {count} != {(long)n * n - overlap}");
            long sum = 0;
            for (int a = 0; a < rects.Count; a++)
            {
                HeightShadowIndexRect r = rects[a];
                sum += r.Count;
                Require(r.I0 >= next.I0 && r.I1 <= next.I1 + 1 && r.J0 >= next.J0 && r.J1 <= next.J1 + 1, "strip outside the new window");
                if (Math.Abs(dI) < n && Math.Abs(dJ) < n) Require(!Intersects(r, old.Rect), "strip overlaps the old window");
                for (int b = a + 1; b < rects.Count; b++) Require(!Intersects(r, rects[b]), "strips overlap each other");
            }
            Require(sum == count, "returned count != sum of rects");
        }
        // First fill (no old window) is one full rect.
        var first = new List<HeightShadowIndexRect>();
        Require(RingHeightShadowLattice.StripRects(null, new HeightShadowWindow(5, 600), first) == (long)n * n && first.Count == 1, "first fill must be one full rect");
        // The documented recentre threshold: N/8 = 128 is the largest move that does not recentre.
        var cur = new HeightShadowWindow(10_000, 5_000);
        RingHeightShadowLattice.NextWindow(cur, 10_128, 5_000, 4, Width, out bool m128);
        RingHeightShadowLattice.NextWindow(cur, 10_129, 5_000, 4, Width, out bool m129);
        Require(!m128 && m129, "recentre threshold must be strictly greater than N/8");
        // Slot splits cover the rect exactly once and never wrap.
        Span<HeightShadowSlotRect> slots = stackalloc HeightShadowSlotRect[4];
        foreach (var rect in new[] { new HeightShadowIndexRect(1000, 500, 128, 1024), new HeightShadowIndexRect(-5, -7, 1024, 1024), new HeightShadowIndexRect(2048, 3, 10, 10) })
        {
            int parts = RingHeightShadowLattice.SplitToSlots(rect, slots);
            long covered = 0;
            for (int i = 0; i < parts; i++)
            {
                Require(slots[i].X + slots[i].Width <= n && slots[i].Y + slots[i].Height <= n, "slot rect wraps");
                covered += (long)slots[i].Width * slots[i].Height;
            }
            Require(covered == rect.Count, "slot split does not cover the rect");
        }
    }

    private void VerifyWindowClamp()
    {
        foreach (int t in new[] { 4, 16, 32 })
        {
            long jc = RingHeightShadowLattice.ClampJc(-50_000, t, Width);
            var low = new HeightShadowWindow(0, jc);
            Require(low.J0 >= 0, "window below the left wall");
            long jcHigh = RingHeightShadowLattice.ClampJc(10_000_000, t, Width);
            var high = new HeightShadowWindow(0, jcHigh);
            Require(high.J1 * (long)t <= Width - 2, "window past the right wall");
            Require(RingHeightShadowLattice.ClampJc(5_000, t, Width) == 5_000, "interior Jc must not move");
        }
    }

    private void VerifyBandHysteresis()
    {
        int b = 0;
        b = RingHeightShadowLattice.UpdateBand(b, 299.0); Require(b == 0, "299 m stays band 0");
        b = RingHeightShadowLattice.UpdateBand(b, 301.0); Require(b == 1, "301 m enters band 1");
        b = RingHeightShadowLattice.UpdateBand(b, 260.0); Require(b == 1, "260 m must stay band 1 (hysteresis)");
        b = RingHeightShadowLattice.UpdateBand(b, 226.0); Require(b == 1, "226 m must stay band 1");
        b = RingHeightShadowLattice.UpdateBand(b, 224.0); Require(b == 0, "224 m returns to band 0");
        b = RingHeightShadowLattice.UpdateBand(b, 7000.0); Require(b == 3, "a jump to 7 km goes straight to band 3");
        b = RingHeightShadowLattice.UpdateBand(b, 4600.0); Require(b == 3, "4.6 km stays band 3");
        b = RingHeightShadowLattice.UpdateBand(b, 4400.0); Require(b == 2, "4.4 km returns to band 2");
        b = RingHeightShadowLattice.UpdateBand(b, 100.0); Require(b == 0, "dropping to 100 m goes straight to band 0");
        // A camera dithering across a limit never flips more than once.
        int flips = 0; b = 0;
        for (int i = 0; i < 1000; i++)
        {
            int nb = RingHeightShadowLattice.UpdateBand(b, 300.0 + ((i & 1) == 0 ? 3.0 : -3.0));
            if (nb != b) flips++;
            b = nb;
        }
        Require(flips == 1, "dither around 300 m flipped " + flips + " times");
        Require(RingHeightShadowLattice.TexelForBand(4, 0) == 4 && RingHeightShadowLattice.TexelForBand(4, 3) == 32 && RingHeightShadowLattice.TexelForBand(8, 3) == 32
            && RingHeightShadowLattice.TexelForBand(2, 2) == 8, "texel for band");
    }

    private void VerifySunFrame()
    {
        var rng = new Random(3);
        for (int k = 0; k < 200; k++)
        {
            double x = rng.NextDouble() - 0.5, y = rng.NextDouble() - 0.5, z = rng.NextDouble() - 0.5, len = Math.Sqrt(x * x + y * y + z * z);
            x /= len; y /= len; z /= len;
            double theta = rng.NextDouble() * Math.Tau, delta = (rng.NextDouble() - 0.5) * 0.01;
            HeightShadowSunFrame f = RingHeightShadowLattice.ProjectToTangentFrame(x, y, z, theta);
            Require(Math.Abs(f.Up * f.Up + f.Around * f.Around + f.Across * f.Across - 1.0) < 1e-12, "frame projection must preserve length");
            HeightShadowSunFrame rotated = RingHeightShadowLattice.RotateByAroundAngle(f, delta);
            HeightShadowSunFrame direct = RingHeightShadowLattice.ProjectToTangentFrame(x, y, z, theta + delta);
            Require(Math.Abs(rotated.Up - direct.Up) < 1e-12 && Math.Abs(rotated.Around - direct.Around) < 1e-12 && rotated.Across == direct.Across,
                "rotating the anchor frame by a_l/R must equal projecting at theta + a_l/R");
        }
        HeightShadowSunFrame overhead = RingHeightShadowLattice.ProjectToTangentFrame(-1, 0, 0, 0); // radial inward at theta 0
        Require(Math.Abs(overhead.Up - 1.0) < 1e-12, "toward-axis direction at theta 0 is +up");
    }

    private void VerifyReferenceMipsBound()
    {
        const int n = RingHeightShadowLattice.TextureSize;
        var rng = new Random(5);
        var heights = new float[n * n];
        for (int i = 0; i < heights.Length; i++) heights[i] = (float)(rng.NextDouble() * 100.0 - 50.0);
        float[] mips = RingHeightShadowLattice.BuildReferenceMips(heights);
        for (int k = 0; k < 20000; k++)
        {
            double u = rng.NextDouble() * n, v = rng.NextDouble() * n;
            float bilinear = Bilinear(heights, u, v);
            for (int mip = 1; mip <= RingHeightShadowLattice.MipCount; mip++)
            {
                int size = RingHeightShadowLattice.MipSize(mip), cx = (int)(u / (1 << mip)), cy = (int)(v / (1 << mip));
                float cell = mips[RingHeightShadowLattice.MipOffset(mip) + cy * size + cx];
                Require(cell >= bilinear - 1e-4f, $"mip {mip} cell below a bilinear sample inside it");
            }
        }
    }

    private static float Bilinear(float[] h, double u, double v)
    {
        const int n = RingHeightShadowLattice.TextureSize;
        int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
        float fx = (float)(u - x0), fy = (float)(v - y0);
        float a = h[(y0 & (n - 1)) * n + (x0 & (n - 1))], b = h[(y0 & (n - 1)) * n + ((x0 + 1) & (n - 1))];
        float c = h[((y0 + 1) & (n - 1)) * n + (x0 & (n - 1))], d = h[((y0 + 1) & (n - 1)) * n + ((x0 + 1) & (n - 1))];
        return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fy;
    }

    // ---- CPU clipmap ----

    private static float SyntheticHeight(long around, int across)
        => (float)(40.0 * Math.Sin(around * 0.0123) + 25.0 * Math.Cos(across * 0.0171) + 5.0 * Math.Sin((around + across) * 0.31)) + ((around * 7 + across * 13) % 11 == 0 ? 60f : 0f);

    private void VerifySeamCrossingFill()
    {
        const int t0 = 4;
        long calls = 0;
        var sources = new IHeightShadowSource[] { new SyntheticHeightShadowSource(Circumference, Width, (a, c) => { Interlocked.Increment(ref calls); return SyntheticHeight(a, c); }) };
        var clip = new RingHeightShadowClipmap(Circumference, Width, t0);
        long ic = (Circumference - 1000) / t0, jc = 40_000;
        HeightShadowFillReport full = clip.FillLevel(0, new HeightShadowWindow(ic, jc), sources, CancellationToken.None);
        Require(full.Texels == 1024L * 1024 && Interlocked.Read(ref calls) == 1024L * 1024, "first fill must sample every texel exactly once");
        CheckMaster(clip, 0, 400);
        float[] before = (float[])clip.Levels[0].Heights.Clone();

        // A move of 100 texels (< N/8) must not touch the source or the master.
        Interlocked.Exchange(ref calls, 0);
        HeightShadowFillReport still = clip.Recentre(0, ic + 100, jc, sources, CancellationToken.None, out bool moved);
        Require(!moved && still.Texels == 0 && Interlocked.Read(ref calls) == 0, "a sub-threshold move must not refill");

        // Camera crosses the seam: window centre goes from C - 1 km to C + 1.4 km (index distance 600). The window straddles C/t already.
        long target = ic + 600;
        Require(target * t0 > Circumference, "test target must be past the seam");
        var filledRects = new List<HeightShadowIndexRect>();
        HeightShadowFillReport strip = clip.Recentre(0, target, jc, sources, CancellationToken.None, out moved, filledRects);
        Require(moved, "seam move should recentre");
        Require(strip.Texels == 600L * 1024 && Interlocked.Read(ref calls) == 600L * 1024, $"seam strip must sample exactly 600x1024 texels, sampled {Interlocked.Read(ref calls)}");
        Require(filledRects.Count == 1 && filledRects[0].Width == 600 && filledRects[0].Height == 1024, "one I strip expected");
        CheckMaster(clip, 0, 600);

        // Overlap slots are bit-identical to before (only the strip's slots changed).
        float[] after = clip.Levels[0].Heights;
        var stripSlots = new HashSet<int>();
        for (long i = filledRects[0].I0; i < filledRects[0].I1; i++) stripSlots.Add(RingHeightShadowLattice.SlotOf(i));
        for (int slotJ = 0; slotJ < 1024; slotJ += 37)
            for (int slotI = 0; slotI < 1024; slotI++)
                if (!stripSlots.Contains(slotI))
                    Require(BitConverter.SingleToInt32Bits(before[slotJ * 1024 + slotI]) == BitConverter.SingleToInt32Bits(after[slotJ * 1024 + slotI]), "overlap texel changed by recentre");

        // Across strips too, diagonal move.
        Interlocked.Exchange(ref calls, 0);
        strip = clip.Recentre(0, target + 200, jc - 150, sources, CancellationToken.None, out moved);
        long expected = 1024L * 1024 - (1024 - 200) * (1024 - 150);
        Require(moved && strip.Texels == expected && Interlocked.Read(ref calls) == expected, "diagonal strip count");
        CheckMaster(clip, 0, 600);
        _checks++;
    }

    private void CheckMaster(RingHeightShadowClipmap clip, int level, int samples)
    {
        RingHeightShadowLevel state = clip.Levels[level];
        HeightShadowWindow w = state.Window!.Value;
        var rng = new Random(17);
        for (int k = 0; k < samples; k++)
        {
            long i = w.I0 + rng.Next(1024), j = w.J0 + rng.Next(1024);
            float expected = SyntheticHeight(RingHeightShadowLattice.WrapAroundMeters(i, state.TexelMeters, Circumference), RingHeightShadowLattice.ClampAcrossMeters(j, state.TexelMeters, Width));
            float actual = state.Heights[RingHeightShadowLattice.SlotOf(j) * 1024 + RingHeightShadowLattice.SlotOf(i)];
            Require(expected == actual, $"master texel ({i},{j}) = {actual}, expected {expected}");
        }
    }

    // ---- real sampler: apertures and timing ----

    private static IHeightShadowSource[] RealSources(RingTerrainSampler root, int workers)
    {
        var sources = new IHeightShadowSource[workers];
        for (int i = 0; i < workers; i++) sources[i] = RingTerrainHeightShadowSource.Wrap(root.CreateIndependentCopy());
        return sources;
    }

    private void VerifyApertureCarve()
    {
        using RingTerrainSampler root = EmitterSiteTestWorlds.CreateGen17Landform(20260419);
        EmitterSiteRegistry registry = root.EmitterSites ?? throw new InvalidOperationException("landform sampler has no emitter registry");
        IHeightShadowSource[] sources = RealSources(root, 1);
        try
        {
            foreach (int siteIndex in new[] { registry.MainIndex, 0 })
            {
                EmitterSite site = registry[siteIndex];
                float liner = EmitterSiteApertures.LinerRadius(site.Kind);
                const int t = 4;
                var clip = new RingHeightShadowClipmap(Circumference, Width, t);
                long ic = site.Around / t, jc = RingHeightShadowLattice.ClampJc(site.Across / t, t, Width);
                // Fill only a 128 x 128 texel patch centred on the site (the carve operates on whatever rect was filled).
                var rect = new HeightShadowIndexRect(ic - 64, jc - 64, 128, 128);
                if (site.Across / t - 64 < 0) rect = new HeightShadowIndexRect(ic - 64, 0, 128, 128);
                clip.FillRects(0, new[] { rect }, new HeightShadowWindow(ic, jc), sources, CancellationToken.None);
                RingHeightShadowLevel level = clip.Levels[0];
                float rim = site.PadHeight - EmitterSiteApertures.PlugDepthM;
                int carved = 0, expectedCarved = 0, outsideChecked = 0;
                for (long j = rect.J0; j < rect.J1; j++)
                    for (long i = rect.I0; i < rect.I1; i++)
                    {
                        double da = i * (double)t - site.Around, dc = RingHeightShadowLattice.ClampAcrossMeters(j, t, Width) - (double)site.Across;
                        double distance = Math.Sqrt(da * da + dc * dc);
                        float value = level.Heights[RingHeightShadowLattice.SlotOf(j) * 1024 + RingHeightShadowLattice.SlotOf(i)];
                        if (distance < liner - 1e-9)
                        {
                            expectedCarved++;
                            Require(value == rim, $"texel inside the liner ({distance:F1} m < {liner} m) not carved");
                            carved++;
                        }
                        else
                        {
                            float natural = sources[0].GetHeight(RingHeightShadowLattice.WrapAroundMeters(i, t, Circumference), RingHeightShadowLattice.ClampAcrossMeters(j, t, Width));
                            Require(value == natural, $"texel outside the liner ({distance:F1} m >= {liner} m) was modified");
                            if (distance < liner + 2 * t) outsideChecked++;
                        }
                    }
                int analytic = (int)Math.Round(Math.PI * liner * liner / (t * t));
                Require(carved == expectedCarved && Math.Abs(carved - analytic) <= analytic * 0.05 + 8, $"carved {carved} texels, disc estimate {analytic}");
                Require(outsideChecked > 20, "no texels near the liner edge were checked");
                GD.Print($"HEIGHT_SHADOW_APERTURE site={siteIndex} kind={site.Kind} liner={liner}m carved={carved} (disc estimate {analytic}) rim={rim}");
            }
        }
        finally { foreach (IHeightShadowSource s in sources) s.Dispose(); }
        _checks++;
    }

    /// <summary>S5: the production source clamps the fill height to the sea level (water receivers then read the terrain rule, a seabed under water takes the surface value).</summary>
    private void VerifySeaLevelClamp()
    {
        Require(RingTerrainHeightShadowSource.ClampToSeaLevel(-12f, 0f) == 0f && RingTerrainHeightShadowSource.ClampToSeaLevel(35.5f, 0f) == 35.5f
            && RingTerrainHeightShadowSource.ClampToSeaLevel(-3f, -3f) == -3f, "sea level clamp helper");
        using RingTerrainSampler root = EmitterSiteTestWorlds.CreateGen17Landform(20260419);
        using RingTerrainSampler copy = root.CreateIndependentCopy();
        IHeightShadowSource source = RingTerrainHeightShadowSource.Wrap(root.CreateIndependentCopy());
        try
        {
            int below = 0, total = 0;
            for (long around = 2_900_000; around < 3_300_000; around += 97)
                for (int across = 20_000; across < 60_000; across += 1_013)
                {
                    float raw = copy.GetPresentationSurfaceHeight(around, across), clamped = source.GetHeight(around, across);
                    total++;
                    Require(clamped >= copy.SeaLevelY, $"clamped height {clamped} below sea {copy.SeaLevelY} at {around},{across}");
                    if (raw < copy.SeaLevelY) { below++; Require(clamped == copy.SeaLevelY, $"sub-sea sample {raw} not clamped ({clamped})"); }
                    else Require(clamped == raw, "above-sea sample altered");
                }
            GD.Print($"HEIGHT_SHADOW_SEA_CLAMP samples={total} below_sea={below} sea={copy.SeaLevelY}");
        }
        finally { source.Dispose(); }
        _checks++;
    }

    private void ReportFillTiming()
    {
        using RingTerrainSampler root = EmitterSiteTestWorlds.CreateGen17Landform(20260419);
#if DEBUG
        const string config = "Debug";
#else
        const string config = "Release";
#endif
        GD.Print($"HEIGHT_SHADOW_FILL_TIMING config={config} cores={System.Environment.ProcessorCount}");
        long ic = 3_000_000 / 4, jc = 40_000;
        foreach (int workers in new[] { 1, 2 })
        {
            IHeightShadowSource[] sources = RealSources(root, workers);
            try
            {
                var clip = new RingHeightShadowClipmap(Circumference, Width, 4);
                HeightShadowFillReport l0 = clip.FillLevel(0, new HeightShadowWindow(ic, jc), sources, CancellationToken.None);
                HeightShadowFillReport l1 = clip.FillLevel(1, new HeightShadowWindow(3_000_000 / 16, 10_000), sources, CancellationToken.None);
                HeightShadowFillReport strip = clip.Recentre(0, ic + 129, jc, sources, CancellationToken.None, out bool moved);
                HeightShadowFillReport strip1 = clip.Recentre(1, 3_000_000 / 16 + 129, 10_000, sources, CancellationToken.None, out bool moved1);
                Require(moved && moved1 && l0.Texels == 1024L * 1024 && strip.Texels == 129L * 1024, "timing fill counts");
                GD.Print($"HEIGHT_SHADOW_FILL_TIMING workers={workers} L0_full={l0.Seconds * 1000:F0}ms ({l0.Seconds * 1e6 / l0.Texels:F2} us/texel) " +
                         $"L1_full={l1.Seconds * 1000:F0}ms strip129x1024_L0={strip.Seconds * 1000:F0}ms strip129x1024_L1={strip1.Seconds * 1000:F0}ms apertureTexels={l0.ApertureTexels + l1.ApertureTexels}");
            }
            finally { foreach (IHeightShadowSource s in sources) s.Dispose(); }
        }
        // Point-sample under-representation: how far the 4 m / 16 m lattice max falls below the true 1 m max inside each texel footprint (the error size, not a gate).
        IHeightShadowSource probe = RingTerrainHeightShadowSource.Wrap(root.CreateIndependentCopy());
        try
        {
            foreach (int t in new[] { 4, 16 })
            {
                double sum = 0.0, worst = 0.0; int samples = 0;
                var rng = new Random(9);
                for (int k = 0; k < 400; k++)
                {
                    long a0 = (3_000_000L + rng.Next(2000) * t) / t * t; int c0 = 40_000 + rng.Next(2000) * t / t * t;
                    float centre = probe.GetHeight(a0, c0), max = centre;
                    for (int dy = -t / 2; dy < t / 2; dy += 1)
                        for (int dx = -t / 2; dx < t / 2; dx += 1)
                            max = Math.Max(max, probe.GetHeight(a0 + dx, c0 + dy));
                    double deficit = max - centre;
                    sum += deficit; worst = Math.Max(worst, deficit); samples++;
                }
                GD.Print($"HEIGHT_SHADOW_POINT_SAMPLE_ERROR t={t}m mean={sum / samples:F3}m worst={worst:F2}m over {samples} texels (centre sample vs max of the 1 m lattice inside the footprint)");
            }
        }
        finally { probe.Dispose(); }
        _checks++;
    }

    // ---- GPU (windowed) ----

    public bool RunGpu()
    {
        try
        {
            RenderingDevice rd = RenderingServer.CreateLocalRenderingDevice() ?? throw new InvalidOperationException("no local RenderingDevice (run windowed, not --headless)");
            using var gpu = new RingTerrainHeightShadowGpu(rd);
            if (!gpu.Initialize()) throw new InvalidOperationException("GPU init failed: " + gpu.Error);
            var sources = new IHeightShadowSource[] { new SyntheticHeightShadowSource(Circumference, Width, SyntheticHeight) };
            var clip = new RingHeightShadowClipmap(Circumference, Width, 4);
            long ic = (Circumference - 1000) / 4 + 37, jc = 40_000 + 5;
            var uploaded = new List<HeightShadowIndexRect>();
            for (int level = 0; level < 2; level++)
            {
                long levelIc = level == 0 ? ic : (Circumference - 1000) / 16 + 11;
                clip.FillLevel(level, new HeightShadowWindow(levelIc, level == 0 ? jc : jc / 4), sources, CancellationToken.None);
                gpu.UploadIndexRect(level, clip, clip.Levels[level].Window!.Value.Rect);
                gpu.BuildMips(level);
            }
            // Recentre both levels across the seam and upload only the strips.
            for (int level = 0; level < 2; level++)
            {
                uploaded.Clear();
                HeightShadowWindow w = clip.Levels[level].Window!.Value;
                clip.Recentre(level, w.Ic + 700, w.Jc - 300, sources, CancellationToken.None, out bool moved, uploaded);
                Require(moved && uploaded.Count == 2, "gpu test recentre should produce two strips");
                foreach (HeightShadowIndexRect rect in uploaded) gpu.UploadIndexRect(level, clip, rect);
                gpu.BuildMips(level);
            }
            rd.Submit();
            rd.Sync();
            for (int level = 0; level < 2; level++)
            {
                float[] gpuHeights = gpu.ReadHeights(level);
                float[] master = clip.Levels[level].Heights;
                Require(gpuHeights.Length == master.Length, "height buffer size");
                for (int i = 0; i < master.Length; i++)
                    Require(BitConverter.SingleToInt32Bits(gpuHeights[i]) == BitConverter.SingleToInt32Bits(master[i]), $"level {level} slot {i}: gpu {gpuHeights[i]} != master {master[i]}");
                float[] gpuMips = gpu.ReadMips(level);
                float[] reference = RingHeightShadowLattice.BuildReferenceMips(master);
                Require(gpuMips.Length == reference.Length, "mip buffer size");
                for (int i = 0; i < reference.Length; i++)
                    Require(BitConverter.SingleToInt32Bits(gpuMips[i]) == BitConverter.SingleToInt32Bits(reference[i]), $"level {level} mip float {i}: gpu {gpuMips[i]} != reference {reference[i]}");
                var rng = new Random(23 + level);
                for (int k = 0; k < 50000; k++)
                {
                    double u = rng.NextDouble() * 1024.0, v = rng.NextDouble() * 1024.0;
                    float bilinear = Bilinear(master, u, v);
                    for (int mip = 1; mip <= RingHeightShadowLattice.MipCount; mip++)
                    {
                        int size = RingHeightShadowLattice.MipSize(mip);
                        float cell = gpuMips[RingHeightShadowLattice.MipOffset(mip) + (int)(v / (1 << mip)) * size + (int)(u / (1 << mip))];
                        Require(cell >= bilinear - 1e-4f, $"level {level} mip {mip} below the bilinear sample");
                    }
                }
            }
            GD.Print("HEIGHT_SHADOW_GPU=PASS levels=2 heights=bit-exact mips=bit-exact bilinear-bound=ok");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("HEIGHT_SHADOW_GPU=FAIL " + exception.Message);
            return false;
        }
    }

    // ---- helpers ----

    private static bool Intersects(HeightShadowIndexRect a, HeightShadowIndexRect b)
        => a.I0 < b.I1 && b.I0 < a.I1 && a.J0 < b.J1 && b.J0 < a.J1;

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
    }

    private void Require(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
