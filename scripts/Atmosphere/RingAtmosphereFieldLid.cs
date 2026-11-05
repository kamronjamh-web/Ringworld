using System;
using Godot;
using RingworldTechDemo.ContainmentField;

namespace RingworldTechDemo.Atmosphere;

/// <summary>
/// C# twin (double precision) of the containment-field atmosphere lid in materials/ring_haze_shared.gdshaderinc (containment field WO9,
/// docs/containment-field-design.md 2.1 and 8 WO9): ring_haze_field_height, ring_haze_field_scan and ring_haze_field_runs, operation for
/// operation. The retained air is the habitat under the field: across strictly between the field roots, below H(across) (the vertical skirt
/// from the wall top to the crown, then the semi-ellipse to the apex), above the floor shell. Change one, change the other.
/// Rays are described in the ring frame with the camera at the origin and coordinate scale 1: the camera height and canonical across, and the
/// unit ray's dot products with the outward radial and the ring axis plus a = |ray - axis (ray . axis)|^2.
/// </summary>
public static class RingAtmosphereFieldLid
{
    /// <summary>Largest altitude error (m) of the chord standing in for the ray's curved meridian path in one scan piece (RING_HAZE_FIELD_SAG_METERS).</summary>
    public const double SagMeters = 20.0;

    /// <summary>The shader profile: (wall top, apex, crown, exists) and (RootAcross0, RootAcrossW). Default = no field.</summary>
    public readonly record struct Profile(double WallTop, double Apex, double Crown, double Root0, double RootW, bool Exists)
    {
        public static Profile None => default;

        public static Profile From(in ContainmentFieldProfile p) => p.IsValid
            ? new Profile(p.WallTopMeters, p.ApexMeters, p.CrownMeters, p.RootAcross0, p.RootAcrossW, true)
            : None;

        /// <summary>ringworld_field_profile0 / genericData ring_field_profile0.</summary>
        public Vector4 Packed0 => Exists ? new Vector4((float)WallTop, (float)Apex, (float)Crown, 1.0f) : Vector4.Zero;

        /// <summary>ringworld_field_profile1 / genericData ring_field_profile1.</summary>
        public Vector4 Packed1 => Exists ? new Vector4((float)Root0, (float)RootW, 0.0f, 0.0f) : Vector4.Zero;

        /// <summary>The profile exactly as a shader reads it back from the packed floats (for float-faithful CPU parity).</summary>
        public Profile AsPackedFloats() => Exists
            ? new Profile((float)WallTop, (float)Apex, (float)Crown, (float)Root0, (float)RootW, true)
            : None;
    }

    /// <summary>Air runs of one ray: (X0, X1) the first (local) run, (Z0, Z1) the second (remote) one; empty when the end is not past the start.</summary>
    public readonly record struct Runs(double X0, double X1, double Z0, double Z1)
    {
        public bool HasFirst => X1 > X0;
        public bool HasSecond => Z1 > Z0;
        public static Runs Empty => new(1.0, 0.0, 1.0, 0.0);
    }

    /// <summary>ring_haze_field_height: H(across) in metres; the wall top at or beyond the roots.</summary>
    public static double Height(in Profile f, double across)
    {
        double from0 = across - f.Root0;
        double fromW = f.RootW - across;
        if (from0 <= 0.0 || fromW <= 0.0) return f.WallTop;
        double halfW = 0.5 * (f.RootW - f.Root0);
        return f.Crown + (f.Apex - f.Crown) * Math.Sqrt(Math.Max(from0 * fromW / (halfW * halfW), 0.0));
    }

    /// <summary>ring_haze_field_column_top: the top of the sun's vertical column for a path midpoint at <paramref name="tMid"/> metres along the ray.</summary>
    public static double ColumnTop(in Profile f, double cap, double cameraAcross, double acrossDir, double tMid)
        => f.Exists ? Height(f, cameraAcross + acrossDir * tMid) : cap;

    /// <summary>
    /// Whether the canonical point (across, vertical) is in the retained air: strictly between the roots and below H (the wall body below the
    /// wall top counts as inside, as in the shader's region). No field: always false (callers keep their own box rule).
    /// </summary>
    public static bool IsInside(in Profile f, double across, double vertical)
        => f.Exists && across > f.Root0 && across < f.RootW && vertical < Height(f, across);

    // ring_atmosphere_path_interval_shared at scale 1, camera at the origin: the parameter interval where the ray is above layerHeight and
    // between the side planes, clipped to [0, maxLength]. Empty when y <= x.
    private static (double X, double Y) PathInterval(double a, double outwardDot, double acrossDir, double h, double radius, double layerHeight,
        double sideNegative, double sidePositive, double maxLength)
    {
        double b = 2.0 * (radius - h) * outwardDot;
        double c = (layerHeight - h) * (2.0 * radius - h - layerHeight);
        double r0, r1;
        if (a <= 1e-12)
        {
            if (c <= 0.0) { r0 = 0.0; r1 = maxLength; } else return (1.0, 0.0);
        }
        else
        {
            double disc = b * b - 4.0 * a * c;
            if (disc < 0.0) return (1.0, 0.0);
            double q = -0.5 * (b + (b >= 0.0 ? 1.0 : -1.0) * Math.Sqrt(disc));
            double t0 = Math.Abs(q) > 1e-6 ? q / a : -b / (2.0 * a);
            double t1 = Math.Abs(q) > 1e-6 ? c / q : t0;
            r0 = Math.Min(t0, t1);
            r1 = Math.Max(t0, t1);
        }
        if (Math.Abs(acrossDir) > 1e-6)
        {
            double wall0 = -sideNegative / acrossDir, wall1 = sidePositive / acrossDir;
            r0 = Math.Max(r0, Math.Min(wall0, wall1));
            r1 = Math.Min(r1, Math.Max(wall0, wall1));
        }
        else if (sideNegative < 0.0 || sidePositive < 0.0) return (1.0, 0.0);
        return (Math.Max(r0, 0.0), Math.Min(r1, maxLength));
    }

    // ring_haze_height
    private static double RayHeight(double t, double h0, double radial0, double b, double a)
    {
        double n = b * t + a * t * t;
        double root = Math.Sqrt(Math.Max(radial0 * radial0 + n, 0.0));
        return h0 - n / Math.Max(radial0 + root, 0.001);
    }

    private struct State
    {
        public double X0, X1, Z0, Z1;
        public int Phase;
        public bool Inside => Phase == 1 || Phase == 3;
        public void Enter(double t)
        {
            if (Phase == 0) { X0 = t; Phase = 1; }
            else if (Phase == 2) { Z0 = t; Phase = 3; }
        }
        public void Exit(double t)
        {
            if (Phase == 1) { X1 = t; Phase = 2; }
            else if (Phase == 3) { Z1 = t; Phase = 4; }
        }
    }

    // ring_haze_field_scan
    private static void Scan(in Profile f, double across0, double acrossDir, double h0, double radial0, double b, double a, double s0, double s1, ref State st)
    {
        double span = s1 - s0;
        if (span <= 0.0 || st.Phase >= 4) return;
        double sag = Math.Max(a, 0.0) * span * span / (8.0 * Math.Max(radial0, 1.0));
        int pieces = sag <= SagMeters ? 1 : Math.Min(16, (int)Math.Ceiling(Math.Sqrt(sag / SagMeters)));
        double halfW = 0.5 * (f.RootW - f.Root0);
        double invA2 = 1.0 / Math.Max(halfW * halfW, 1.0);
        double rise = Math.Max(f.Apex - f.Crown, 1.0);
        double invB2 = 1.0 / (rise * rise);
        double tStart = s0;
        double wStart = RayHeight(s0, h0, radial0, b, a) - f.Crown;
        for (int i = 0; i < 16; i++)
        {
            if (i >= pieces || st.Phase >= 4) break;
            double tEnd = i + 1 == pieces ? s1 : s0 + span * (i + 1) / pieces;
            double wEnd = RayHeight(tEnd, h0, radial0, b, a) - f.Crown;
            double dt = tEnd - tStart;
            double from0 = across0 + acrossDir * tStart - f.Root0;
            double fromW = f.RootW - across0 - acrossDir * tStart;
            double dx = acrossDir * dt;
            double dw = wEnd - wStart;
            double qa = dw * dw * invB2 + dx * dx * invA2;
            double qb = 2.0 * wStart * dw * invB2 - dx * (fromW - from0) * invA2;
            double qc = wStart * wStart * invB2 - from0 * fromW * invA2;
            bool insideStart = qc < 0.0;
            if (st.Inside != insideStart)
            {
                if (insideStart) st.Enter(tStart); else st.Exit(tStart);
            }
            double disc = qb * qb - 4.0 * qa * qc;
            if (qa > 0.0 && disc > 0.0)
            {
                double q = -0.5 * (qb + (qb >= 0.0 ? 1.0 : -1.0) * Math.Sqrt(disc));
                double r0 = q / qa;
                double r1 = q != 0.0 ? qc / q : r0;
                double lo = Math.Min(r0, r1), hi = Math.Max(r0, r1);
                // lo <= 0 < hi with qc >= 0: the piece starts exactly on the field (qc == 0) and goes in.
                if (lo > 0.0 && lo < 1.0 && !st.Inside) st.Enter(tStart + lo * dt);
                else if (lo <= 0.0 && hi > 0.0 && !st.Inside) st.Enter(tStart);
                if (hi > 0.0 && hi < 1.0 && st.Inside) st.Exit(tStart + hi * dt);
            }
            tStart = tEnd;
            wStart = wEnd;
        }
    }

    /// <summary>
    /// ring_haze_field_runs: the air runs of a ray of <paramref name="length"/> metres from a camera at height <paramref name="h0"/> and
    /// canonical across <paramref name="across0"/>. <paramref name="outwardDot"/> = ray . outward, <paramref name="acrossDir"/> = ray . axis,
    /// <paramref name="a"/> = |ray - axis (ray . axis)|^2 (unit ray, unit axis). <paramref name="wantSecond"/> = false stops after the first run.
    /// </summary>
    public static Runs Compute(in Profile f, double length, double h0, double across0, double radius, double floorDepth,
        double outwardDot, double acrossDir, double a, bool wantSecond)
    {
        double rootNegative = across0 - f.Root0;
        double rootPositive = f.RootW - across0;
        if (length > 0.0001 && length < Math.Min(Math.Min(f.Crown - h0, h0 + floorDepth), Math.Min(rootNegative, rootPositive)))
            return new Runs(0.0, length, 1.0, 0.0);
        var outer = PathInterval(a, outwardDot, acrossDir, h0, radius, -floorDepth, rootNegative, rootPositive, length);
        if (outer.Y <= outer.X) return Runs.Empty;
        var crown = PathInterval(a, outwardDot, acrossDir, h0, radius, f.Crown, rootNegative, rootPositive, length);
        crown = (Math.Max(crown.X, outer.X), Math.Min(crown.Y, outer.Y));
        if (crown.Y <= crown.X) return new Runs(outer.X, outer.Y, 1.0, 0.0);
        var apex = PathInterval(a, outwardDot, acrossDir, h0, radius, f.Apex, rootNegative, rootPositive, length);
        apex = (Math.Max(apex.X, crown.X), Math.Min(apex.Y, crown.Y));
        bool hasApex = apex.Y > apex.X;
        double radial0 = radius - h0;
        double b = 2.0 * radial0 * outwardDot;
        var st = new State { X0 = 1.0, X1 = 0.0, Z0 = 1.0, Z1 = 0.0, Phase = 0 };
        if (crown.X > outer.X) st.Enter(outer.X);
        Scan(f, across0, acrossDir, h0, radial0, b, a, crown.X, hasApex ? apex.X : crown.Y, ref st);
        if (hasApex && (wantSecond || st.Phase < 2))
        {
            st.Exit(apex.X);
            Scan(f, across0, acrossDir, h0, radial0, b, a, apex.Y, crown.Y, ref st);
        }
        if ((wantSecond || st.Phase < 2) && crown.Y < outer.Y && !st.Inside) st.Enter(crown.Y);
        if (st.Phase == 1) st.X1 = outer.Y;
        else if (st.Phase == 3) st.Z1 = outer.Y;
        return new Runs(st.X0, st.X1, st.Z0, st.Z1);
    }
}
