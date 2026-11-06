using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.ContainmentField;

/// <summary>
/// Exact 2D queries on the containment field (docs/containment-field-design.md 1.3-1.4, extended for the vertical skirt). The field is a surface of
/// revolution, so every distance is the Euclidean distance in the point's own meridian plane, in (across, vertical) metres. The curve is the left skirt
/// (across = RootAcross0, vertical WallTop..Crown), the upper half ellipse (semi-axes HalfWidth and Rise about the crown plane) and the right skirt.
/// Arch length runs from the across-0 wall top: skirt, ellipse, skirt.
/// </summary>
public static class ContainmentFieldGeometry
{
    /// <summary>The one cloud ceiling cap (RingCloudLayers.CeilingMeters, asserted equal by the test).</summary>
    public const double CloudCeilingMeters = 18_000.0;
    public const double CloudCeilingBelowFieldMeters = 300.0;
    /// <summary>Density feather under the ceiling (narrows to nothing as the ceiling reaches 18 km) and the band from the roots outside which the factor is exactly 1.</summary>
    public const double CloudCeilingFeatherMeters = 500.0, CloudCeilingRimBandMeters = 7_000.0;

    // ---- Height --------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Field underside at <paramref name="acrossMeters"/>: NaN outside [RootAcross0, RootAcrossW], exactly WallTop at the two roots (the foot of the
    /// skirt), the ellipse height (at least the crown) strictly between them.
    /// </summary>
    public static double HeightAt(in ContainmentFieldProfile p, double acrossMeters)
    {
        if (!(acrossMeters >= p.RootAcross0 && acrossMeters <= p.RootAcrossW)) return double.NaN;
        if (acrossMeters == p.RootAcross0 || acrossMeters == p.RootAcrossW) return p.WallTopMeters;
        double x = (acrossMeters - p.Centre) / p.HalfWidth;
        double t = 1.0 - x * x;
        return p.CrownMeters + p.Rise * Math.Sqrt(t > 0.0 ? t : 0.0);
    }

    /// <summary>dH/dacross: +inf at the across-0 root, -inf at the across-W root; NaN outside the roots.</summary>
    public static double SlopeAt(in ContainmentFieldProfile p, double acrossMeters)
    {
        if (!(acrossMeters >= p.RootAcross0 && acrossMeters <= p.RootAcrossW)) return double.NaN;
        double a = p.HalfWidth;
        double x = acrossMeters - p.Centre;
        double t = 1.0 - (x / a) * (x / a);
        if (acrossMeters == p.RootAcross0 || acrossMeters == p.RootAcrossW || t <= 0.0)
            return acrossMeters <= p.Centre ? double.PositiveInfinity : double.NegativeInfinity;
        return -p.Rise * x / (a * a * Math.Sqrt(t));
    }

    /// <summary>THE cloud ceiling: min(18 km, H(across) - 300 m); exactly 18 km where H - 300 >= 18 km and wherever there is no field (NaN H).</summary>
    public static double CloudCeilingAt(in ContainmentFieldProfile p, double acrossMeters)
    {
        double h = HeightAt(p, acrossMeters);
        return double.IsNaN(h) ? CloudCeilingMeters : CloudCeilingFromUnderside(h);
    }

    /// <summary>The same rule for provider consumers that already hold the underside (matches the MegastormFieldCoupling stub).</summary>
    public static double CloudCeilingFromUnderside(double undersideMeters) => Math.Min(CloudCeilingMeters, undersideMeters - CloudCeilingBelowFieldMeters);

    /// <summary>
    /// CPU twin of RingContainmentCeiling.glslinc <c>ring_containment_ceiling</c>: the cloud density factor at (across, altitude). 1 below the ceiling's
    /// feather, a smoothstep to 0 at the ceiling, exactly 0 above it, exactly 1 with no field or more than 7 km from both roots.
    /// </summary>
    public static double CloudDensityFactor(in ContainmentFieldProfile p, double acrossMeters, double altitudeMeters)
    {
        if (Math.Min(acrossMeters - p.RootAcross0, p.RootAcrossW - acrossMeters) > CloudCeilingRimBandMeters) return 1.0;
        double ceiling = CloudCeilingAt(p, acrossMeters);
        if (ceiling >= CloudCeilingMeters) return 1.0;
        double feather = Math.Clamp(CloudCeilingMeters - ceiling, 1.0, CloudCeilingFeatherMeters);
        double t = Math.Clamp((altitudeMeters - (ceiling - feather)) / feather, 0.0, 1.0);
        return 1.0 - t * t * (3.0 - 2.0 * t);
    }

    // ---- Arc length ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>Total arch length: skirt + half ellipse + skirt.</summary>
    public static double ArchLength(in ContainmentFieldProfile p) => 2.0 * p.SkirtHeightMeters + EllipseArc.For(p.HalfWidth, p.Rise).Total;

    private sealed class EllipseArc
    {
        private const int Panels = 2048;
        private static readonly double[] GlX = { 0.0, 0.5384693101056831, -0.5384693101056831, 0.9061798459386640, -0.9061798459386640 };
        private static readonly double[] GlW = { 0.5688888888888889, 0.4786286704993665, 0.4786286704993665, 0.2369268850561891, 0.2369268850561891 };
        private static readonly Dictionary<(double, double), EllipseArc> Cache = new();

        private readonly double _a, _b;
        private readonly double[] _cum = new double[Panels + 1];
        public double Total => _cum[Panels];

        public static EllipseArc For(double a, double b)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue((a, b), out EllipseArc? hit)) return hit;
                if (Cache.Count > 32) Cache.Clear();
                return Cache[(a, b)] = new EllipseArc(a, b);
            }
        }

        private EllipseArc(double a, double b)
        {
            _a = a; _b = b;
            for (int i = 0; i < Panels; i++) _cum[i + 1] = _cum[i] + Integrate(PhiAtPanel(i), PhiAtPanel(i + 1));
        }

        private static double PhiAtPanel(int i) => Math.PI * i / Panels;
        private double Speed(double phi) { (double s, double c) = Math.SinCos(phi); return Math.Sqrt(_a * _a * s * s + _b * _b * c * c); }

        private double Integrate(double p0, double p1)
        {
            double mid = 0.5 * (p0 + p1), half = 0.5 * (p1 - p0), sum = 0.0;
            for (int k = 0; k < 5; k++) sum += GlW[k] * Speed(mid + half * GlX[k]);
            return sum * half;
        }

        /// <summary>Arc length from phi = 0.</summary>
        public double LengthAt(double phi)
        {
            phi = Math.Clamp(phi, 0.0, Math.PI);
            int i = Math.Min(Panels - 1, (int)(phi / Math.PI * Panels));
            return _cum[i] + Integrate(PhiAtPanel(i), phi);
        }

        /// <summary>Inverse of <see cref="LengthAt"/>.</summary>
        public double PhiAt(double s)
        {
            if (s <= 0.0) return 0.0;
            if (s >= Total) return Math.PI;
            int lo = 0, hi = Panels;
            while (hi - lo > 1) { int m = (lo + hi) >> 1; if (_cum[m] <= s) lo = m; else hi = m; }
            double p0 = PhiAtPanel(lo), p1 = PhiAtPanel(lo + 1);
            double phi = p0 + (s - _cum[lo]) / Speed(p0);
            for (int it = 0; it < 6; it++)
            {
                phi = Math.Clamp(phi, p0, p1);
                double err = _cum[lo] + Integrate(p0, phi) - s;
                phi -= err / Speed(phi);
            }
            return Math.Clamp(phi, p0, p1);
        }
    }

    // ---- Surface points ------------------------------------------------------------------------------------------------------------------------

    private static ContainmentFieldPoint LeftSkirtPoint(in ContainmentFieldProfile p, double around, double v)
        => new(around, 0.0, p.RootAcross0, v, 0.0, 1.0, v - p.WallTopMeters);

    private static ContainmentFieldPoint RightSkirtPoint(in ContainmentFieldProfile p, double around, double v, double archLength)
        => new(around, Math.PI, p.RootAcrossW, v, 0.0, -1.0, archLength);

    /// <summary>Ellipse point at parameter phi in [0, pi] (across = c - a cos phi, vertical = crown + b sin phi).</summary>
    public static ContainmentFieldPoint SurfaceAtPhi(in ContainmentFieldProfile p, double around, double phi)
    {
        phi = Math.Clamp(phi, 0.0, Math.PI);
        double a = p.HalfWidth, b = p.Rise;
        (double sin, double cos) = Math.SinCos(phi);
        double na = cos / a, nv = -sin / b, len = Math.Sqrt(na * na + nv * nv);
        double arch = p.SkirtHeightMeters + EllipseArc.For(a, b).LengthAt(phi);
        return new ContainmentFieldPoint(around, phi, p.Centre - a * cos, p.CrownMeters + b * sin, nv / len, na / len, arch);
    }

    /// <summary>Point at arch length <paramref name="s"/> from the across-0 wall top (clamped to [0, ArchLength]); arc-length LUT, well under 1 cm.</summary>
    public static ContainmentFieldPoint SurfaceAtArchLength(in ContainmentFieldProfile p, double around, double s)
    {
        EllipseArc arc = EllipseArc.For(p.HalfWidth, p.Rise);
        double skirt = p.SkirtHeightMeters, total = 2.0 * skirt + arc.Total;
        s = Math.Clamp(s, 0.0, total);
        if (s <= skirt) return LeftSkirtPoint(p, around, p.WallTopMeters + s);
        if (s >= skirt + arc.Total) return RightSkirtPoint(p, around, p.CrownMeters - (s - skirt - arc.Total), s);
        ContainmentFieldPoint pt = SurfaceAtPhi(p, around, arc.PhiAt(s - skirt));
        return pt with { ArchLengthMeters = s };
    }

    /// <summary>
    /// Fills <paramref name="rows"/> with <see cref="SurfaceAtArchLength"/> at evenly spaced arch lengths s0..s1 (rows.Length >= 2), sharing one arc lookup and
    /// one ArchLength; same results as the public call.
    /// </summary>
    internal static void SurfaceRows(in ContainmentFieldProfile p, double around, double s0, double s1, ContainmentFieldPoint[] rows)
    {
        EllipseArc arc = EllipseArc.For(p.HalfWidth, p.Rise);
        double skirt = p.SkirtHeightMeters, total = 2.0 * skirt + arc.Total, a = p.HalfWidth, b = p.Rise;
        int last = rows.Length - 1;
        for (int j = 0; j <= last; j++)
        {
            double s = Math.Clamp(s0 + (s1 - s0) * j / last, 0.0, total);
            if (s <= skirt) { rows[j] = LeftSkirtPoint(p, around, p.WallTopMeters + s); continue; }
            if (s >= skirt + arc.Total) { rows[j] = RightSkirtPoint(p, around, p.CrownMeters - (s - skirt - arc.Total), s); continue; }
            double phi = Math.Clamp(arc.PhiAt(s - skirt), 0.0, Math.PI);
            (double sin, double cos) = Math.SinCos(phi);
            double na = cos / a, nv = -sin / b, len = Math.Sqrt(na * na + nv * nv);
            rows[j] = new ContainmentFieldPoint(around, phi, p.Centre - a * cos, p.CrownMeters + b * sin, nv / len, na / len, s);
        }
    }

    // ---- Nearest -------------------------------------------------------------------------------------------------------------------------------

    private enum Piece { LeftSkirt, Ellipse, RightSkirt }

    /// <summary>Exact nearest point on the curve in the query's meridian plane (it keeps the query's around).</summary>
    public static ContainmentFieldPoint Nearest(in ContainmentFieldProfile p, in RingCanonicalPosition q)
    {
        NearestCore(p, q.AcrossMeters, q.VerticalMeters, out Piece piece, out double param, out _);
        switch (piece)
        {
            case Piece.LeftSkirt:
                return LeftSkirtPoint(p, q.AroundMeters, param);
            case Piece.RightSkirt:
                double arch = p.SkirtHeightMeters + EllipseArc.For(p.HalfWidth, p.Rise).Total + (p.CrownMeters - param);
                return RightSkirtPoint(p, q.AroundMeters, param, arch);
            default:
                return SurfaceAtPhi(p, q.AroundMeters, param);
        }
    }

    /// <summary><see cref="Nearest"/> and <see cref="SignedDistance"/> from one solve.</summary>
    internal static ContainmentFieldPoint NearestAndSigned(in ContainmentFieldProfile p, in RingCanonicalPosition q, out double signedDistance)
    {
        NearestCore(p, q.AcrossMeters, q.VerticalMeters, out Piece piece, out double param, out double dist);
        signedDistance = InsideRegion(p, q.AcrossMeters, q.VerticalMeters) ? -dist : dist;
        switch (piece)
        {
            case Piece.LeftSkirt:
                return LeftSkirtPoint(p, q.AroundMeters, param);
            case Piece.RightSkirt:
                double arch = p.SkirtHeightMeters + EllipseArc.For(p.HalfWidth, p.Rise).Total + (p.CrownMeters - param);
                return RightSkirtPoint(p, q.AroundMeters, param, arch);
            default:
                return SurfaceAtPhi(p, q.AroundMeters, param);
        }
    }

    private static void NearestCore(in ContainmentFieldProfile p, double across, double vertical, out Piece piece, out double param, out double dist)
    {
        double a = p.HalfWidth, b = p.Rise;
        double vClamp = Math.Clamp(vertical, p.WallTopMeters, p.CrownMeters);
        double dxL = across - p.RootAcross0, dxR = across - p.RootAcrossW, dv = vertical - vClamp;
        double dL2 = dxL * dxL + dv * dv, dR2 = dxR * dxR + dv * dv;
        piece = Piece.LeftSkirt; param = vClamp; double best2 = dL2;
        if (dR2 < best2) { best2 = dR2; piece = Piece.RightSkirt; }
        // Every ellipse point is at or above the crown, so below the crown the ellipse is at least (crown - vertical) away: when a skirt is already
        // that close, the (expensive) lower-half ellipse scan cannot win. Exact; it only skips work (the wall top and the vacuum side near a wall).
        double yc = vertical - p.CrownMeters;
        if (yc < 0.0 && best2 <= yc * yc) { dist = Math.Sqrt(best2); return; }
        double phi = NearestEllipsePhi(a, b, across - p.Centre, yc, out double eD2);
        if (eD2 < best2) { best2 = eD2; piece = Piece.Ellipse; param = phi; }
        dist = Math.Sqrt(best2);
    }

    /// <summary>Nearest point on the UPPER half ellipse (x = -a cos phi, y = b sin phi). Eberly bisection for y >= 0; a bracketed stationary scan below.</summary>
    private static double NearestEllipsePhi(double a, double b, double x, double y, out double dist2)
    {
        if (y >= 0.0)
        {
            double ex = Math.Abs(x), x0, y0;
            if (y > 0.0)
            {
                if (ex > 0.0)
                {
                    double z0 = ex / a, z1 = y / b, g = z0 * z0 + z1 * z1 - 1.0;
                    if (g != 0.0)
                    {
                        double r0 = (a / b) * (a / b), n0 = r0 * z0, s0 = z1 - 1.0, s1 = g < 0.0 ? 0.0 : Math.Sqrt(n0 * n0 + z1 * z1) - 1.0, s = 0.0;
                        for (int i = 0; i < 200; i++)
                        {
                            s = 0.5 * (s0 + s1);
                            if (s == s0 || s == s1) break;
                            double rr0 = n0 / (s + r0), rr1 = z1 / (s + 1.0), gg = rr0 * rr0 + rr1 * rr1 - 1.0;
                            if (gg > 0.0) s0 = s; else if (gg < 0.0) s1 = s; else break;
                        }
                        x0 = r0 * ex / (s + r0); y0 = y / (s + 1.0);
                    }
                    else { x0 = ex; y0 = y; }
                }
                else { x0 = 0.0; y0 = b; }
            }
            else
            {
                double numer = a * ex, denom = a * a - b * b;
                if (numer < denom) { double xe = numer / denom; x0 = a * xe; y0 = b * Math.Sqrt(Math.Max(0.0, 1.0 - xe * xe)); }
                else { x0 = a; y0 = 0.0; }
            }
            double sx = x < 0.0 ? -x0 : x0;
            dist2 = (sx - x) * (sx - x) + (y0 - y) * (y0 - y);
            return Math.Atan2(y0 / b, -sx / a);
        }

        // y < 0: minimise over psi in [0, pi] with curve (a cos psi, b sin psi); phi = pi - psi.
        const int N = 256;
        double a2b2 = a * a - b * b;
        double G(double psi) { (double sn, double cs) = Math.SinCos(psi); return a * x * sn - b * y * cs - a2b2 * sn * cs; }
        double D2(double psi) { (double sn, double cs) = Math.SinCos(psi); double dx = a * cs - x, dy = b * sn - y; return dx * dx + dy * dy; }
        double bestPsi = 0.0, best = D2(0.0);
        double d2End = D2(Math.PI);
        if (d2End < best) { best = d2End; bestPsi = Math.PI; }
        double prevPsi = 0.0, prevG = G(0.0);
        for (int i = 1; i <= N; i++)
        {
            double psi = Math.PI * i / N, gv = G(psi);
            if ((prevG < 0.0 && gv > 0.0) || (prevG > 0.0 && gv < 0.0) || gv == 0.0)
            {
                double lo = prevPsi, hi = psi, gLo = prevG;
                for (int it = 0; it < 60; it++)
                {
                    double mid = 0.5 * (lo + hi), gm = G(mid);
                    if ((gm < 0.0) == (gLo < 0.0)) { lo = mid; gLo = gm; } else hi = mid;
                }
                double root = 0.5 * (lo + hi), d2 = D2(root);
                if (d2 < best) { best = d2; bestPsi = root; }
            }
            prevPsi = psi; prevG = gv;
        }
        dist2 = best;
        return Math.PI - bestPsi;
    }

    // ---- Signed queries ------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Signed Euclidean distance to the curve: negative inside the contained region (between the roots and below the curve, which includes the
    /// wall body below the wall top), positive above the field or outboard of the roots.
    /// </summary>
    public static double SignedDistance(in ContainmentFieldProfile p, in RingCanonicalPosition q)
    {
        NearestCore(p, q.AcrossMeters, q.VerticalMeters, out _, out _, out double d);
        return InsideRegion(p, q.AcrossMeters, q.VerticalMeters) ? -d : d;
    }

    private static bool InsideRegion(in ContainmentFieldProfile p, double across, double vertical)
        => across > p.RootAcross0 && across < p.RootAcrossW && vertical < HeightAt(p, across);

    /// <summary>Vertical clearance H(across) - vertical; NaN outside the habitat [0, W].</summary>
    public static double ClearanceBelow(in ContainmentFieldProfile p, in RingCanonicalPosition q)
        => q.AcrossMeters >= 0.0 && q.AcrossMeters <= p.WidthMeters ? HeightAt(p, q.AcrossMeters) - q.VerticalMeters : double.NaN;

    /// <summary>
    /// Root0 &lt;= across &lt;= RootW and vertical &lt; H(across), and over a wall (across outside [0, W]) only above the wall top (the wall body is
    /// solid, not air).
    /// </summary>
    public static bool IsContained(in ContainmentFieldProfile p, in RingCanonicalPosition q)
    {
        double across = q.AcrossMeters;
        if (!(across >= p.RootAcross0 && across <= p.RootAcrossW)) return false;
        if (!(q.VerticalMeters < HeightAt(p, across))) return false;
        return (across >= 0.0 && across <= p.WidthMeters) || q.VerticalMeters > p.WallTopMeters;
    }

    // ---- World segment -------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// First crossing of the field by the straight world segment A..B. Samples at most 500 m apart (canonical), brackets sign changes of
    /// <see cref="SignedDistance"/>, bisects to 1 cm, and rejects sign flips that are not surface zeros (the region's open bottom under the wall top).
    /// Intervals that stay on one side but come within a step of the curve are refined once so near-grazing dips are not skipped.
    /// </summary>
    public static bool TryIntersectWorldSegment(in ContainmentFieldProfile p, in RingLodGeometrySnapshot s, Vector3 worldA, Vector3 worldB,
        out ContainmentFieldPoint hit, out double t)
    {
        ContainmentFieldProfile prof = p;
        RingLodGeometrySnapshot snap = s;
        Vector3 a = worldA, d = worldB - worldA;
        double length = d.Length();
        RingCanonicalPosition At(double tt) => ContainmentFieldPlacement.WorldToCanonical(snap, a + d * (float)tt);
        double F(double tt) => SignedDistance(prof, At(tt));

        hit = default; t = 0.0;
        int steps = Math.Max(1, (int)Math.Ceiling(length / 500.0));
        double stepLen = length / steps;
        double t0 = 0.0, f0 = F(0.0);
        for (int i = 1; i <= steps; i++)
        {
            double t1 = (double)i / steps, f1 = F(t1);
            if (Bracket(t0, f0, t1, f1, stepLen, 1, out double tHit)) { t = tHit; hit = Nearest(prof, At(tHit)); return true; }
            t0 = t1; f0 = f1;
        }
        return false;

        bool Bracket(double ta, double fa, double tb, double fb, double span, int depth, out double result)
        {
            result = 0.0;
            bool flip = fa == 0.0 || (fa < 0.0) != (fb < 0.0);
            if (flip)
            {
                double lo = ta, hi = tb, fLo = fa;
                if (fa == 0.0) hi = ta;
                else
                {
                    int iters = 0;
                    while ((hi - lo) * length > 0.005 && iters++ < 80)
                    {
                        double mid = 0.5 * (lo + hi), fm = F(mid);
                        if (fm == 0.0) { lo = hi = mid; break; }
                        if ((fm < 0.0) == (fLo < 0.0)) { lo = mid; fLo = fm; } else hi = mid;
                    }
                }
                double tm = 0.5 * (lo + hi);
                if (Math.Abs(F(tm)) <= 0.05) { result = tm; return true; }
                return false;
            }
            if (depth > 0 && Math.Min(Math.Abs(fa), Math.Abs(fb)) < span)
            {
                const int Sub = 32;
                double ts = ta, fs = fa;
                for (int k = 1; k <= Sub; k++)
                {
                    double te = ta + (tb - ta) * k / Sub, fe = k == Sub ? fb : F(te);
                    if (Bracket(ts, fs, te, fe, span / Sub, depth - 1, out result)) return true;
                    ts = te; fs = fe;
                }
            }
            return false;
        }
    }
}
