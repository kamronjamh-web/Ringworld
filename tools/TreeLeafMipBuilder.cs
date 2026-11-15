using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Tools;

/// <summary>TR-7c-PV: the distance-stable leaf mip rule (docs/work-orders/terrain-vegetation/TR.md, TR-7c, design B and C), a C# port of .godot/tr7c_lead/leaf_mips_reference.py.
/// Pure CPU, fixed iteration order, doubles for the bisection: two builds on one machine give the same bytes. Preview only: nothing here is used by the game.
/// Alpha: the covered fraction of each mip texel, scaled per leaf-cluster island and level so the usage-weighted area that passes the cutoff equals the close-up area.
/// Colour: linear-light mean of the covered level-0 texels, smoothed, with pull-push into texels that have no covered texel under them.</summary>
public static class TreeLeafMipBuilder
{
    public const int Grid = 512;
    public const int IslandGrid = 64;
    public const int BisectSteps = 26;

    public sealed class Built
    {
        public int Size, Levels, Islands;
        /// <summary>Per level (index 0 unused): alpha bytes, s*s.</summary>
        public byte[][] Alpha = Array.Empty<byte[]>();
        /// <summary>Per level (index 0 unused): sRGB colour bytes, s*s*3.</summary>
        public byte[][] Rgb = Array.Empty<byte[]>();
        /// <summary>Per level (index 0 unused): the scale of each island (one entry for levels without islands).</summary>
        public double[][] Scales = Array.Empty<double[]>();
    }

    private static readonly float[] SrgbToLinear = BuildSrgbTable();

    private static float[] BuildSrgbTable()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            t[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }
        return t;
    }

    private static byte LinearToSrgbByte(double x)
    {
        x = Math.Clamp(x, 0, 1);
        double c = x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(c, 0, 1) * 255, MidpointRounding.ToEven);
    }

    private static int Log2(int v) { int l = 0; while ((1 << l) < v) l++; return l; }

    private static float[] Box(float[] a, int s, int channels = 1)
    {
        int h = s / 2;
        var r = new float[h * h * channels];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < h; x++)
                for (int c = 0; c < channels; c++)
                    r[(y * h + x) * channels + c] = (a[((2 * y) * s + 2 * x) * channels + c] + a[((2 * y) * s + 2 * x + 1) * channels + c]
                        + a[((2 * y + 1) * s + 2 * x) * channels + c] + a[((2 * y + 1) * s + 2 * x + 1) * channels + c]) * 0.25f;
        return r;
    }

    /// <summary>Alpha byte of one texel at a level: round(clip(cover * scale, 0, 1) * 255), half to even.</summary>
    private static byte Q(float cover, double scale) => (byte)Math.Round(Math.Clamp(cover * scale, 0, 1) * 255, MidpointRounding.ToEven);

    /// <summary>Taps of the bilinear (repeat) upsample from s texels to the 512 grid (centre aligned), per axis.</summary>
    private sealed class Taps
    {
        public readonly int[] I0 = new int[Grid], I1 = new int[Grid];
        public readonly double[] F = new double[Grid];
        public Taps(int s)
        {
            for (int n = 0; n < Grid; n++)
            {
                double c = (n + .5) / Grid * s - .5;
                int i0 = (int)Math.Floor(c);
                F[n] = c - i0;
                I1[n] = (((i0 + 1) % s) + s) % s;
                I0[n] = ((i0 % s) + s) % s;
            }
        }
    }

    /// <summary>Usage-weighted area of the 512 grid that passes the cutoff, for a level given as an alpha accessor. Support lists the grid cells with weight.</summary>
    private static double PassArea(Func<int, double> alpha, int s, double cut, int[] cells, double[] weights, Taps? taps)
    {
        double total = 0, sum = 0;
        for (int k = 0; k < cells.Length; k++)
        {
            int cell = cells[k]; int cy = cell / Grid, cx = cell % Grid;
            double pass;
            if (s > Grid)
            {
                int b = s / Grid; int n = 0;
                for (int y = 0; y < b; y++)
                    for (int x = 0; x < b; x++)
                        if (alpha((cy * b + y) * s + cx * b + x) >= cut) n++;
                pass = n / (double)(b * b);
            }
            else if (s == Grid) pass = alpha(cell) >= cut ? 1 : 0;
            else
            {
                double v = (1 - taps!.F[cy]) * ((1 - taps.F[cx]) * alpha(taps.I0[cy] * s + taps.I0[cx]) + taps.F[cx] * alpha(taps.I0[cy] * s + taps.I1[cx]))
                         + taps.F[cy] * ((1 - taps.F[cx]) * alpha(taps.I1[cy] * s + taps.I0[cx]) + taps.F[cx] * alpha(taps.I1[cy] * s + taps.I1[cx]));
                pass = v >= cut ? 1 : 0;
            }
            total += pass * weights[k]; sum += weights[k];
        }
        return sum > 0 ? total / sum : 0;
    }

    private static double Bisect(Func<double, double> f, double target)
    {
        double lo = 0, hi = 64;
        for (int i = 0; i < BisectSteps; i++)
        {
            double mid = (lo + hi) * 0.5;
            if (f(mid) < target) lo = mid; else hi = mid;
        }
        return (lo + hi) * 0.5;
    }

    /// <summary>4-connected islands of the 64 x 64 cover grid; components under 1/256 of the covered mass join the nearest. Returns the owner index (into the big components) of every cell.</summary>
    private static int[] Islands(float[] grid, out int count)
    {
        int g = IslandGrid;
        var label = new int[g * g]; int n = 0;
        var stack = new Stack<int>();
        for (int y = 0; y < g; y++)
            for (int x = 0; x < g; x++)
            {
                if (grid[y * g + x] <= 0 || label[y * g + x] != 0) continue;
                n++; label[y * g + x] = n; stack.Push(y * g + x);
                while (stack.Count > 0)
                {
                    int c = stack.Pop(); int cy = c / g, cx = c % g;
                    for (int d = 0; d < 4; d++)
                    {
                        int ny = cy + (d == 0 ? 1 : d == 1 ? -1 : 0), nx = cx + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if (ny < 0 || ny >= g || nx < 0 || nx >= g) continue;
                        if (grid[ny * g + nx] <= 0 || label[ny * g + nx] != 0) continue;
                        label[ny * g + nx] = n; stack.Push(ny * g + nx);
                    }
                }
            }
        var mass = new double[n + 1]; double tot = 0;
        for (int i = 0; i < g * g; i++) if (label[i] > 0) { mass[label[i]] += grid[i]; tot += grid[i]; }
        var big = new List<int>();
        for (int k = 1; k <= n; k++) if (mass[k] >= tot / 256.0) big.Add(k);
        var own = new int[g * g]; var best = new long[g * g]; Array.Fill(best, long.MaxValue);
        for (int i = 0; i < big.Count; i++)
        {
            var cells = new List<int>();
            for (int c = 0; c < g * g; c++) if (label[c] == big[i]) cells.Add(c);
            for (int c = 0; c < g * g; c++)
            {
                int cy = c / g, cx = c % g; long d2 = long.MaxValue;
                foreach (int p in cells) { long dy = cy - p / g, dx = cx - p % g; long d = dy * dy + dx * dx; if (d < d2) d2 = d; }
                if (d2 < best[c]) { best[c] = d2; own[c] = i; }
            }
        }
        count = big.Count;
        return own;
    }

    /// <summary>Island owner of each texel of an s x s level (s >= 4).</summary>
    private static int[] Owners(int[] own, float[] grid, int s, int count)
    {
        var ot = new int[s * s];
        if (s >= IslandGrid)
        {
            int b = s / IslandGrid;
            for (int y = 0; y < s; y++) for (int x = 0; x < s; x++) ot[y * s + x] = own[(y / b) * IslandGrid + x / b];
        }
        else
        {
            int f = IslandGrid / s;
            var w = new double[count];
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    Array.Clear(w);
                    for (int yy = 0; yy < f; yy++) for (int xx = 0; xx < f; xx++) { int c = (y * f + yy) * IslandGrid + x * f + xx; w[own[c]] += grid[c] + 1e-9; }
                    int arg = 0; for (int i = 1; i < count; i++) if (w[i] > w[arg]) arg = i;
                    ot[y * s + x] = arg;
                }
        }
        return ot;
    }

    /// <summary>Builds the stable mip chain (levels 1 to log2(size)) from level-0 RGBA8 bytes (sRGB colour), the cutoff, and the species usage map (512 x 512, any scale).</summary>
    public static Built Build(byte[] rgba, int size, double cutoff, double[] usage, Func<byte[], int, byte[]>? codec = null, int codecMaxSize = 256)
    {
        int levels = Log2(size);
        if ((1 << levels) != size || size < Grid) throw new ArgumentException("atlas must be a power of two of at least " + Grid);
        // cover pyramid and colour sums, from the mask
        var cover = new float[levels + 1][];
        var num = new float[levels + 1][]; var den = new float[levels + 1][];
        int h1 = size / 2;
        cover[1] = new float[h1 * h1]; den[1] = new float[h1 * h1]; num[1] = new float[h1 * h1 * 3];
        for (int y = 0; y < h1; y++)
            for (int x = 0; x < h1; x++)
            {
                float m = 0; float r = 0, g = 0, b = 0;
                for (int d = 0; d < 4; d++)
                {
                    int px = ((2 * y + (d >> 1)) * size + 2 * x + (d & 1)) * 4;
                    if (rgba[px + 3] / 255.0 >= cutoff) { m += 1; r += SrgbToLinear[rgba[px]]; g += SrgbToLinear[rgba[px + 1]]; b += SrgbToLinear[rgba[px + 2]]; }
                }
                int i = y * h1 + x;
                cover[1][i] = m * 0.25f; den[1][i] = m * 0.25f; num[1][i * 3] = r * 0.25f; num[1][i * 3 + 1] = g * 0.25f; num[1][i * 3 + 2] = b * 0.25f;
            }
        for (int l = 2; l <= levels; l++)
        {
            int s = size >> (l - 1);
            cover[l] = Box(cover[l - 1], s); den[l] = cover[l]; num[l] = Box(num[l - 1], s, 3);
        }
        // the base grid: the level-0 mask averaged onto the 512 grid
        float[] baseGrid = cover[Log2(size / Grid)];
        if (size == Grid) throw new ArgumentException("size 512 not supported");
        int gridLevel = levels - 6;
        float[] islandGrid = cover[gridLevel];
        int[] own = Islands(islandGrid, out int islands);
        var ownG = new int[Grid * Grid];
        for (int y = 0; y < Grid; y++) for (int x = 0; x < Grid; x++) ownG[y * Grid + x] = own[(y / (Grid / IslandGrid)) * IslandGrid + x / (Grid / IslandGrid)];
        // per-island support (cells with usage weight; an island no triangle uses is uniform over its own covered cells)
        var supCells = new int[islands][]; var supW = new double[islands][]; var supTarget = new double[islands];
        var allCells = new List<int>(); var allW = new List<double>();
        for (int c = 0; c < Grid * Grid; c++) if (usage[c] > 0) { allCells.Add(c); allW.Add(usage[c]); }
        int[] globalCells = allCells.ToArray(); double[] globalW = allW.ToArray();
        double globalTarget = Dot(baseGrid, globalCells, globalW);
        for (int k = 0; k < islands; k++)
        {
            var cl = new List<int>(); var wl = new List<double>();
            for (int c = 0; c < Grid * Grid; c++) if (ownG[c] == k && usage[c] > 0) { cl.Add(c); wl.Add(usage[c]); }
            if (cl.Count == 0)
                for (int c = 0; c < Grid * Grid; c++)
                {
                    int ic = ((c / Grid) / (Grid / IslandGrid)) * IslandGrid + (c % Grid) / (Grid / IslandGrid);
                    if (ownG[c] == k && islandGrid[ic] > 0) { cl.Add(c); wl.Add(1); }
                }
            supCells[k] = cl.ToArray(); supW[k] = wl.ToArray(); supTarget[k] = Dot(baseGrid, supCells[k], supW[k]);
        }
        var built = new Built { Size = size, Levels = levels, Islands = islands, Alpha = new byte[levels + 1][], Rgb = new byte[levels + 1][], Scales = new double[levels + 1][] };
        for (int l = 1; l <= levels; l++)
        {
            int s = size >> l; int n = s * s;
            var alpha = new byte[n];
            if (s == 1)
            {
                alpha[0] = (byte)Math.Floor(cutoff * 255 - 1); built.Alpha[l] = alpha; built.Scales[l] = new[] { 0.0 }; continue;
            }
            float[] cv = cover[l];
            Taps? taps = s < Grid ? new Taps(s) : null;
            // With a codec (closed loop) the area is measured on the level as the codec returns it (the DXT5 round trip), so the scale compensates for the block compression.
            bool loop = codec != null && s <= codecMaxSize;
            byte[] Decoded(Func<int, byte> texel) { var a = new byte[n]; for (int i = 0; i < n; i++) a[i] = texel(i); return codec!(a, s); }
            double Area(Func<int, byte> texel, int[] cells, double[] weights)
            {
                if (!loop) return PassArea(i => texel(i) / 255.0, s, cutoff, cells, weights, taps);
                byte[] d = Decoded(texel);
                return PassArea(i => d[i] / 255.0, s, cutoff, cells, weights, taps);
            }
            double global = Bisect(m => Area(i => Q(cv[i], m), globalCells, globalW), globalTarget);
            var scale = new double[Math.Max(islands, 1)]; Array.Fill(scale, global);
            if (s >= 4)
            {
                int[] ot = Owners(own, islandGrid, s, islands);
                for (int pass = 0; pass < 3; pass++)
                    for (int k = 0; k < islands; k++)
                    {
                        if (supCells[k].Length == 0) continue;
                        bool any = false; for (int i = 0; i < n && !any; i++) any = ot[i] == k;
                        if (!any) continue;
                        int kk = k;
                        scale[k] = Bisect(m => { scale[kk] = m; return Area(i => Q(cv[i], scale[ot[i]]), supCells[kk], supW[kk]); }, supTarget[k]);
                    }
                for (int i = 0; i < n; i++) alpha[i] = Q(cv[i], scale[ot[i]]);
            }
            else for (int i = 0; i < n; i++) alpha[i] = Q(cv[i], global);
            built.Alpha[l] = alpha; built.Scales[l] = scale;
        }
        // colour
        var col = new float[levels + 1][];
        for (int l = levels; l >= 1; l--)
        {
            int s = size >> l;
            float[] sn = Smooth(num[l], s, 3), sd = Smooth(den[l], s, 1);
            var c = new float[s * s * 3];
            for (int i = 0; i < s * s; i++)
            {
                if (sd[i] > 1e-6f) for (int k = 0; k < 3; k++) c[i * 3 + k] = sn[i * 3 + k] / sd[i];
                else if (l < levels) { int px = i % s / 2, py = i / s / 2; int hs = s / 2; for (int k = 0; k < 3; k++) c[i * 3 + k] = col[l + 1][(py * hs + px) * 3 + k]; }
            }
            col[l] = c;
        }
        for (int l = 1; l <= levels; l++)
        {
            int n = (size >> l) * (size >> l); var rgb = new byte[n * 3];
            for (int i = 0; i < n * 3; i++) rgb[i] = LinearToSrgbByte(col[l][i]);
            built.Rgb[l] = rgb;
        }
        return built;
    }

    private static double Dot(float[] grid, int[] cells, double[] w)
    {
        double t = 0, s = 0;
        for (int k = 0; k < cells.Length; k++) { t += grid[cells[k]] * w[k]; s += w[k]; }
        return s > 0 ? t / s : 0;
    }

    /// <summary>Two passes of [1 2 1] / 4 in each axis, wrapping (the atlas is sampled with repeat).</summary>
    private static float[] Smooth(float[] a, int s, int channels)
    {
        float[] cur = a;
        for (int pass = 0; pass < 2; pass++)
        {
            var tmp = new float[cur.Length]; var dst = new float[cur.Length];
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                    for (int c = 0; c < channels; c++)
                        tmp[(y * s + x) * channels + c] = (cur[(y * s + (x + s - 1) % s) * channels + c] + 2 * cur[(y * s + x) * channels + c] + cur[(y * s + (x + 1) % s) * channels + c]) * 0.25f;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                    for (int c = 0; c < channels; c++)
                        dst[(y * s + x) * channels + c] = (tmp[(((y + s - 1) % s) * s + x) * channels + c] + 2 * tmp[(y * s + x) * channels + c] + tmp[(((y + 1) % s) * s + x) * channels + c]) * 0.25f;
            cur = dst;
        }
        return cur;
    }

    // ---- measurement (shared by the builder's table and the tool's gates) ----------------------------------------------------------------------------------

    /// <summary>Usage-weighted area (fraction of the weight) that passes the cutoff for a level given as alpha bytes (s x s). s may be above 512 (block mean) or below (bilinear repeat).</summary>
    public static double AreaOf(byte[] alpha, int s, double cutoff, double[] usage)
    {
        var cells = new List<int>(); var w = new List<double>();
        for (int c = 0; c < Grid * Grid; c++) if (usage[c] > 0) { cells.Add(c); w.Add(usage[c]); }
        return PassArea(i => alpha[i] / 255.0, s, cutoff, cells.ToArray(), w.ToArray(), s < Grid ? new Taps(s) : null);
    }

    /// <summary>The same halfway to the next level: the mean of the two levels' alpha on the grid, tested against the cutoff.</summary>
    public static double AreaHalfway(byte[] a, int sa, byte[] b, int sb, double cutoff, double[] usage)
    {
        double[] ua = OnGrid(a, sa), ub = OnGrid(b, sb);
        double t = 0, s = 0;
        for (int c = 0; c < Grid * Grid; c++) if (usage[c] > 0) { t += usage[c] * ((0.5 * ua[c] + 0.5 * ub[c]) >= cutoff ? 1 : 0); s += usage[c]; }
        return s > 0 ? t / s : 0;
    }

    private static double[] OnGrid(byte[] a, int s)
    {
        var r = new double[Grid * Grid];
        var taps = new Taps(s);
        for (int cy = 0; cy < Grid; cy++)
            for (int cx = 0; cx < Grid; cx++)
                r[cy * Grid + cx] = (1 - taps.F[cy]) * ((1 - taps.F[cx]) * a[taps.I0[cy] * s + taps.I0[cx]] + taps.F[cx] * a[taps.I0[cy] * s + taps.I1[cx]]) / 255.0
                    + taps.F[cy] * ((1 - taps.F[cx]) * a[taps.I1[cy] * s + taps.I0[cx]] + taps.F[cx] * a[taps.I1[cy] * s + taps.I1[cx]]) / 255.0;
        return r;
    }

    /// <summary>The level-0 covered area on the 512 grid (the close-up baseline), weighted by usage.</summary>
    public static double BaselineArea(byte[] rgba, int size, double cutoff, double[] usage)
    {
        int b = size / Grid; double t = 0, s = 0;
        for (int c = 0; c < Grid * Grid; c++)
        {
            if (usage[c] <= 0) continue;
            int cy = c / Grid, cx = c % Grid; int n = 0;
            for (int y = 0; y < b; y++) for (int x = 0; x < b; x++) if (rgba[(((cy * b + y) * size) + cx * b + x) * 4 + 3] / 255.0 >= cutoff) n++;
            t += usage[c] * n / (double)(b * b); s += usage[c];
        }
        return s > 0 ? t / s : 0;
    }
}
