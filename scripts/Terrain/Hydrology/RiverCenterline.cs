using System;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>Deterministic hashes of the river centrelines: jitter per cell, wander phases per stem, meander phase per stem. Independent of worker count.</summary>
public static class RiverHash
{
    public static ulong Mix(long seed, long a, long b = 0)
    {
        unchecked
        {
            ulong h = (ulong)seed * 0x9E3779B97F4A7C15UL ^ ((ulong)a + 0x632BE59BD9B4E019UL) * 0xBF58476D1CE4E5B9UL ^ ((ulong)b + 0x1B873593UL) * 0x94D049BB133111EBUL;
            h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 27; h *= 0x94D049BB133111EBUL; h ^= h >> 31;
            return h;
        }
    }

    /// <summary>Uniform in [-1, 1) from the hash <paramref name="salt"/> steps after (seed, a, b).</summary>
    public static double Signed(long seed, long a, long b, int salt)
    {
        ulong h = Mix(seed, a, b);
        for (int i = 0; i < salt; i++) h = Mix((long)h, salt, i);
        return (h >> 11) * (1.0 / (1UL << 53)) * 2.0 - 1.0;
    }

    public static double Unit(long seed, long a, long b, int salt) => 0.5 * (Signed(seed, a, b, salt) + 1d);
}

/// <summary>
/// One stem's base curve (docs/work-orders/terrain-vegetation/T2.md, S5 and S7): a uniform quadratic B-spline through its control nodes with clamped ends.
/// N nodes give N pieces; piece k has Bezier controls (M_k, P_k, M_k+1) with M_0 = P_0, M_k = (P_k-1 + P_k) / 2 and M_N = P_N-1, so the first and last
/// pieces are straight half segments. The curve is C1 inside the control polygon and ends on the first and last node. The arc-length parameter s is
/// defined as <c>S0_k + Len_k * u</c> (linear in u within a piece, 5-point Gauss-Legendre length), which makes position at s a direct evaluation.
/// Coordinates are metres relative to the first node, around unwrapped.
/// </summary>
public sealed class RiverStemCurve
{
    private static readonly double[] GlX = { 0.0, -0.5384693101056831, 0.5384693101056831, -0.906179845938664, 0.906179845938664 };
    private static readonly double[] GlW = { 0.5688888888888889, 0.47862867049936647, 0.47862867049936647, 0.23692688505618908, 0.23692688505618908 };

    public double OriginAround { get; }
    public double OriginAcross { get; }
    /// <summary>Held as floats (T2R-6, resident budget). Metres relative to the first node; bake and load build the curve through the same constructor, so the rounding agrees.</summary>
    public float[] X { get; }
    public float[] Y { get; }
    public float[] S0 { get; }
    public float[] Len { get; }
    public double Length { get; }
    public int Pieces => X.Length;

    public RiverStemCurve(double originAround, double originAcross, double[] x, double[] y)
    {
        OriginAround = originAround; OriginAcross = originAcross;
        int n = x.Length;
        X = new float[n]; Y = new float[n];
        for (int i = 0; i < n; i++) { X[i] = (float)x[i]; Y[i] = (float)y[i]; }
        S0 = new float[n];
        Len = new float[n];
        double s = 0;
        for (int k = 0; k < n; k++)
        {
            Controls(k, out double ax, out double ay, out double px, out double py, out double cx, out double cy);
            double len = 0;
            for (int i = 0; i < GlX.Length; i++)
            {
                double u = 0.5 + 0.5 * GlX[i];
                double dx = 2 * (1 - u) * (px - ax) + 2 * u * (cx - px), dy = 2 * (1 - u) * (py - ay) + 2 * u * (cy - py);
                len += 0.5 * GlW[i] * Math.Sqrt(dx * dx + dy * dy);
            }
            S0[k] = (float)s;
            Len[k] = (float)Math.Max(len, 1e-6);
            s += Math.Max(len, 1e-6);
        }
        Length = s;
    }

    public void Controls(int k, out double ax, out double ay, out double px, out double py, out double cx, out double cy)
    {
        int n = X.Length;
        px = X[k]; py = Y[k];
        if (k == 0) { ax = px; ay = py; }
        else { ax = 0.5 * (X[k - 1] + px); ay = 0.5 * (Y[k - 1] + py); }
        if (k == n - 1) { cx = px; cy = py; }
        else { cx = 0.5 * (px + X[k + 1]); cy = 0.5 * (py + Y[k + 1]); }
    }

    public int PieceAt(double s)
    {
        int lo = 0, hi = S0.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (S0[mid] <= s) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Position and unit tangent at arc parameter <paramref name="s"/> (clamped to [0, Length]).</summary>
    public void Eval(double s, out double x, out double y, out double tx, out double ty)
    {
        s = Math.Clamp(s, 0d, Length);
        int k = PieceAt(s);
        EvalPiece(k, Math.Clamp((s - S0[k]) / Len[k], 0d, 1d), out x, out y, out tx, out ty);
    }

    public void EvalPiece(int k, double u, out double x, out double y, out double tx, out double ty)
    {
        Controls(k, out double ax, out double ay, out double px, out double py, out double cx, out double cy);
        double a = (1 - u) * (1 - u), b = 2 * u * (1 - u), c = u * u;
        x = a * ax + b * px + c * cx;
        y = a * ay + b * py + c * cy;
        double dx = 2 * (1 - u) * (px - ax) + 2 * u * (cx - px), dy = 2 * (1 - u) * (py - ay) + 2 * u * (cy - py);
        double norm = Math.Sqrt(dx * dx + dy * dy);
        if (norm < 1e-9) { dx = cx - ax; dy = cy - ay; norm = Math.Sqrt(dx * dx + dy * dy); }
        if (norm < 1e-12) { tx = 1; ty = 0; return; }
        tx = dx / norm; ty = dy / norm;
    }

    /// <summary>Closest point to (px, py) on pieces [<paramref name="pieceLo"/>, <paramref name="pieceHi"/>]: arc parameter and distance.</summary>
    public void Closest(double qx, double qy, int pieceLo, int pieceHi, out double s, out double distance)
    {
        pieceLo = Math.Clamp(pieceLo, 0, Pieces - 1);
        pieceHi = Math.Clamp(pieceHi, pieceLo, Pieces - 1);
        double bestD2 = double.PositiveInfinity, bestS = 0;
        const int steps = 32;
        for (int k = pieceLo; k <= pieceHi; k++)
        {
            int bestI = 0;
            double pieceBest = double.PositiveInfinity;
            for (int i = 0; i <= steps; i++)
            {
                double d2 = Distance2(k, (double)i / steps, qx, qy);
                if (d2 < pieceBest) { pieceBest = d2; bestI = i; }
            }
            double lo = Math.Max(0, (bestI - 1d) / steps), hi = Math.Min(1, (bestI + 1d) / steps);
            for (int it = 0; it < 24; it++)
            {
                double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                if (Distance2(k, m1, qx, qy) < Distance2(k, m2, qx, qy)) hi = m2; else lo = m1;
            }
            double u = 0.5 * (lo + hi), best = Distance2(k, u, qx, qy);
            if (pieceBest < best) { u = (double)bestI / steps; best = pieceBest; }
            if (best < bestD2) { bestD2 = best; bestS = S0[k] + Len[k] * u; }
        }
        s = bestS;
        distance = Math.Sqrt(bestD2);
    }

    private double Distance2(int k, double u, double qx, double qy)
    {
        EvalPiece(k, u, out double x, out double y, out _, out _);
        return (x - qx) * (x - qx) + (y - qy) * (y - qy);
    }
}

/// <summary>
/// Wander of S7: interior nodes move along the node-chain normal by a smooth function of the cumulative chord length. Tuned in T2R-5b: three
/// incommensurate wavelengths (7.0, 4.3 and 2.6 km) with hashed phases make the wander irregular, and a soft saturation
/// <c>tanh(1.5 N) / tanh(1.5)</c> raises its typical size while keeping <c>|N| &lt;= 1</c> (so the 250 m cap holds).
/// </summary>
public static class RiverWander
{
    public const double LongWavelengthMeters = 7000d;
    public const double MidWavelengthMeters = 4300d;
    public const double ShortWavelengthMeters = 2600d;
    public const double TaperMeters = 1500d;
    private const double Boost = 1.5;

    /// <summary>The normalised wander N(sigma) in [-1, 1] for one stem's phases.</summary>
    public static double Noise(double sigma, double phi1, double phi2, double phi3)
    {
        double n = 0.45 * Math.Sin(2 * Math.PI * sigma / LongWavelengthMeters + phi1) + 0.35 * Math.Sin(2 * Math.PI * sigma / MidWavelengthMeters + phi2)
            + 0.20 * Math.Sin(2 * Math.PI * sigma / ShortWavelengthMeters + phi3);
        return Math.Tanh(Boost * n) / Math.Tanh(Boost);
    }

    /// <summary>
    /// Moves the interior nodes (and returns the largest shift) of (<paramref name="x"/>, <paramref name="y"/>) in place. <paramref name="amplitude"/> is A_w per node. The phases come
    /// from the hash of (seed, stem). sigma is the cumulative chord length of the unmoved nodes. End nodes never move.
    /// </summary>
    public static double Apply(double[] x, double[] y, double[] amplitude, long seed, int stem)
    {
        int n = x.Length;
        if (n < 3) return 0;
        double phi1 = Math.PI * (1d + RiverHash.Signed(seed, stem, 1, 1)), phi2 = Math.PI * (1d + RiverHash.Signed(seed, stem, 2, 2)), phi3 = Math.PI * (1d + RiverHash.Signed(seed, stem, 3, 3));
        var sigma = new double[n];
        for (int k = 1; k < n; k++) sigma[k] = sigma[k - 1] + Math.Sqrt((x[k] - x[k - 1]) * (x[k] - x[k - 1]) + (y[k] - y[k - 1]) * (y[k] - y[k - 1]));
        double total = sigma[n - 1];
        var nx = new double[n];
        var ny = new double[n];
        double maxShift = 0;
        for (int k = 1; k < n - 1; k++)
        {
            double dx = x[k + 1] - x[k - 1], dy = y[k + 1] - y[k - 1];
            double norm = Math.Sqrt(dx * dx + dy * dy);
            if (norm < 1e-9) continue;
            double taper = RiverHydraulics.Smoothstep(0, TaperMeters, sigma[k]) * RiverHydraulics.Smoothstep(0, TaperMeters, total - sigma[k]);
            double shift = amplitude[k] * taper * Noise(sigma[k], phi1, phi2, phi3);
            nx[k] = -dy / norm * shift;
            ny[k] = dx / norm * shift;
            maxShift = Math.Max(maxShift, Math.Abs(shift));
        }
        for (int k = 1; k < n - 1; k++) { x[k] += nx[k]; y[k] += ny[k]; }
        return maxShift;
    }

    /// <summary>T2R-5c: lateral cap in metres of a node from its eased position (smoothing, wander and valley-floor snap together).</summary>
    public const double NodeShiftCapMeters = 350d;
    /// <summary>T2R-5c: Gaussian window (nodes either side) and sigma (nodes) of the gentle-ground smoothing, and the reach slopes between which it fades out.</summary>
    public const int SmoothHalfWindow = 3;
    public const double SmoothSigmaNodes = 1.5, SmoothFullBelowSlope = 0.006, SmoothNoneAboveSlope = 0.01;

    /// <summary>
    /// T2R-5c: smooths the node chain with a Gaussian over +-<see cref="SmoothHalfWindow"/> nodes (window truncated at the stem ends, which never move), faded
    /// out between reach slopes 0.006 and 0.01 so steep reaches keep their shape. One pass over the unsmoothed positions. Returns the largest shift
    /// (never above <see cref="NodeShiftCapMeters"/>). It removes the old D8 45 and 90 degree runs on gentle ground.
    /// </summary>
    public static double SmoothGentle(double[] x, double[] y, double[] slope)
    {
        int n = x.Length;
        if (n < 3) return 0;
        var weights = new double[SmoothHalfWindow + 1];
        for (int i = 0; i <= SmoothHalfWindow; i++) weights[i] = Math.Exp(-0.5 * i * i / (SmoothSigmaNodes * SmoothSigmaNodes));
        var mx = new double[n];
        var my = new double[n];
        double maxShift = 0;
        for (int k = 1; k < n - 1; k++)
        {
            double fade = 1d - RiverHydraulics.Smoothstep(SmoothFullBelowSlope, SmoothNoneAboveSlope, slope[k]);
            if (fade <= 0) continue;
            double sx = 0, sy = 0, sw = 0;
            for (int i = Math.Max(0, k - SmoothHalfWindow); i <= Math.Min(n - 1, k + SmoothHalfWindow); i++)
            {
                double w = weights[Math.Abs(i - k)];
                sx += w * x[i]; sy += w * y[i]; sw += w;
            }
            double dx = fade * (sx / sw - x[k]), dy = fade * (sy / sw - y[k]);
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d > NodeShiftCapMeters) { dx *= NodeShiftCapMeters / d; dy *= NodeShiftCapMeters / d; d = NodeShiftCapMeters; }
            mx[k] = dx; my[k] = dy;
            maxShift = Math.Max(maxShift, d);
        }
        for (int k = 1; k < n - 1; k++) { x[k] += mx[k]; y[k] += my[k]; }
        return maxShift;
    }

    /// <summary>T2R-5c: pulls every node back onto the circle of <see cref="NodeShiftCapMeters"/> around its reference position when it is further away. Returns the largest remaining distance.</summary>
    public static double ClampShift(double[] x, double[] y, double[] refX, double[] refY)
    {
        double max = 0;
        for (int k = 0; k < x.Length; k++)
        {
            double dx = x[k] - refX[k], dy = y[k] - refY[k], d = Math.Sqrt(dx * dx + dy * dy);
            if (d > NodeShiftCapMeters) { x[k] = refX[k] + dx * NodeShiftCapMeters / d; y[k] = refY[k] + dy * NodeShiftCapMeters / d; d = NodeShiftCapMeters; }
            max = Math.Max(max, d);
        }
        return max;
    }

    /// <summary>T2R-5b hairpin easing: turn limit in degrees, pull towards the neighbours' midpoint, and the largest move.</summary>
    public const double HairpinDegrees = 65d, HairpinPull = 0.75, HairpinMaxMeters = 250d;

    /// <summary>
    /// T2R-5b: eases hairpins of the lowest-sample node chain before the wander. An interior node where the chain turns by more than
    /// <see cref="HairpinDegrees"/> moves towards the midpoint of its neighbours by <see cref="HairpinPull"/> of the distance, at most
    /// <see cref="HairpinMaxMeters"/>. End nodes never move. One pass over the unmoved positions, so the result is order independent.
    /// </summary>
    public static int EaseHairpins(double[] x, double[] y)
    {
        int n = x.Length, moved = 0;
        if (n < 3) return 0;
        var mx = new double[n];
        var my = new double[n];
        double cosLimit = Math.Cos(HairpinDegrees * Math.PI / 180d);
        for (int k = 1; k < n - 1; k++)
        {
            double ax = x[k] - x[k - 1], ay = y[k] - y[k - 1], bx = x[k + 1] - x[k], by = y[k + 1] - y[k];
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            if (la < 1e-6 || lb < 1e-6) continue;
            if ((ax * bx + ay * by) / (la * lb) >= cosLimit) continue;
            double dx = 0.5 * (x[k - 1] + x[k + 1]) - x[k], dy = 0.5 * (y[k - 1] + y[k + 1]) - y[k];
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-6) continue;
            double move = Math.Min(HairpinPull * d, HairpinMaxMeters) / d;
            mx[k] = dx * move; my[k] = dy * move;
            moved++;
        }
        for (int k = 1; k < n - 1; k++) { x[k] += mx[k]; y[k] += my[k]; }
        return moved;
    }
}
