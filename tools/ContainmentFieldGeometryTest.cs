using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Containment field WO1 geometry core (docs/containment-field-design.md 1.1-1.6 and 8 WO1, with the 2026-11-04 decisions and the 2026-11-05 vertical
/// skirt): height, skirt, arch length, Nearest against brute force, signed queries, around invariance, placement, world segments, FromSnapshot, wall-depth
/// modes and the cloud ceiling. Pure C#; no scene.
/// Run: Godot --headless --path . --script res://tools/containment_field_geometry_test.gd. Prints CONTAINMENT_FIELD_GEOMETRY=PASS.
/// </summary>
public partial class ContainmentFieldGeometryTest : RefCounted
{
    private const double W = 318_000.0;
    // Certified, Current wall (128 m, centre line), skirt 1600: a = 159,064, crown 11,600, rise 28,400; independent midpoint integration of the half ellipse
    // (2e6 panels) gives 331,483.595 m, so the arch is 2 * 1600 + 331,483.595 = 334,683.595 m (the body's 332.6 km had no skirt and a 159 km half-width).
    private const double ExpectedArchCertified = 334_683.595;

    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok) _failures.Add(message); }
    private void Near(double a, double b, double tol, string message) { if (!(Math.Abs(a - b) <= tol)) _failures.Add($"{message}: {a.ToString("R", CultureInfo.InvariantCulture)} vs {b.ToString("R", CultureInfo.InvariantCulture)} tol {tol}"); }

    private static RingLodGeometrySnapshot Snap(double wallTop, RingCanonicalPosition? anchor = null, Transform3D? grid = null)
        => new(grid ?? Transform3D.Identity, Vector3.Zero, Vector3.Zero, CertifiedRingScale.EffectiveRadiusMeters, 0.0, 0.0,
            CertifiedRingScale.EffectiveCircumferenceMeters, -8192.0, wallTop, anchor);

    private static ContainmentFieldProfile Prof(double wallTop, RimWallDescription wall, double skirt = 1600.0)
        => ContainmentFieldProfile.FromSnapshot(Snap(wallTop), W, wall, 40_000.0, skirt);

    public int Run()
    {
        try
        {
            double circ = CertifiedRingScale.EffectiveCircumferenceMeters;
            ContainmentFieldProfile cur = Prof(10_000.0, RimWallDescription.Current);
            var profiles = new List<(string Name, ContainmentFieldProfile P)>
            {
                ("current", cur),
                ("current-runtime", Prof(10_400.0, RimWallDescription.Current)),
                ("d0-inner", Prof(10_000.0, new(0, RimFieldRoot.InnerEdge))),
                ("d100-outer", Prof(10_000.0, new(100, RimFieldRoot.OuterEdge))),
                ("d100-centre", Prof(10_000.0, new(100, RimFieldRoot.CentreLine))),
                ("d100-inner", Prof(10_000.0, new(100, RimFieldRoot.InnerEdge))),
                ("skirt0", Prof(10_000.0, new(100, RimFieldRoot.OuterEdge), 0.0)),
            };
            foreach ((string name, ContainmentFieldProfile p) in profiles)
            {
                Check(p.IsValid, name + " valid");
                Shape(name, p);
                ArchLengths(name, p);
            }
            FromSnapshot();
            WallModes();
            foreach ((string name, ContainmentFieldProfile p) in profiles.Where(x => x.Name is "current" or "d100-outer" or "skirt0" or "current-runtime"))
            {
                BruteForce(name, p, name == "current" ? 360 : 90);
            }
            Signed(cur);
            AroundInvariance(cur, circ);
            Placement(cur);
            Segments(cur);
            CloudCeiling(cur);
            CloudCeiling(profiles[1].P);
            Check(ContainmentFieldPolicy.FlightPermeability == ContainmentPermeability.Pass && ContainmentFieldPolicy.ProjectilePermeability == ContainmentPermeability.Pass, "policy pass");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.ToString());
            Console.Error.WriteLine("CONTAINMENT_FIELD_GEOMETRY=FAIL exception " + e.Message);
            return 1;
        }
        if (_failures.Count != 0) { Console.Error.WriteLine("CONTAINMENT_FIELD_GEOMETRY=FAIL " + string.Join(" | ", _failures.Take(25))); return 1; }
        Console.WriteLine("CONTAINMENT_FIELD_GEOMETRY=PASS");
        return 0;
    }

    // ---- Shape ---------------------------------------------------------------------------------------------------------------------------------

    private void Shape(string name, in ContainmentFieldProfile p)
    {
        double c = p.Centre, a = p.HalfWidth;
        Near(ContainmentFieldGeometry.HeightAt(p, p.RootAcross0), p.WallTopMeters, 0.0, name + " H(root0)");
        Near(ContainmentFieldGeometry.HeightAt(p, p.RootAcrossW), p.WallTopMeters, 0.0, name + " H(rootW)");
        Near(ContainmentFieldGeometry.HeightAt(p, c), p.ApexMeters, 1e-6, name + " H(centre)");
        Near(c, W * 0.5, 1e-9, name + " centre is W/2");
        Check(double.IsNaN(ContainmentFieldGeometry.HeightAt(p, p.RootAcross0 - 1.0)) && double.IsNaN(ContainmentFieldGeometry.HeightAt(p, p.RootAcrossW + 1.0)), name + " NaN outside roots");
        Check(double.IsPositiveInfinity(ContainmentFieldGeometry.SlopeAt(p, p.RootAcross0)) && double.IsNegativeInfinity(ContainmentFieldGeometry.SlopeAt(p, p.RootAcrossW)), name + " slope inf at roots");
        // vertical skirt, then the ellipse starts at the crown
        foreach (double f in new[] { 0.0, 0.25, 0.5, 1.0 })
        {
            double s = p.SkirtHeightMeters * f;
            ContainmentFieldPoint pt = ContainmentFieldGeometry.SurfaceAtArchLength(p, 5.0, s);
            Near(pt.AcrossMeters, p.RootAcross0, 1e-9, name + " skirt0 across"); Near(pt.VerticalMeters, p.WallTopMeters + s, 1e-9, name + " skirt0 vertical");
            Near(pt.NormalAcross, 1.0, 1e-12, name + " skirt normal");
            double total = ContainmentFieldGeometry.ArchLength(p);
            ContainmentFieldPoint pr = ContainmentFieldGeometry.SurfaceAtArchLength(p, 5.0, total - s);
            Near(pr.AcrossMeters, p.RootAcrossW, 1e-9, name + " skirtW across"); Near(pr.VerticalMeters, p.WallTopMeters + s, 1e-6, name + " skirtW vertical");
        }
        Check(ContainmentFieldGeometry.HeightAt(p, p.RootAcross0 + 0.001) >= p.CrownMeters, name + " ellipse above crown");
        Near(ContainmentFieldGeometry.HeightAt(p, p.RootAcross0 + 0.001), p.CrownMeters, 5.0, name + " ellipse starts at crown");
        // symmetric, monotonic per half
        double prev = ContainmentFieldGeometry.HeightAt(p, p.RootAcross0 + 1e-3);
        bool mono = true, sym = true;
        for (int i = 1; i <= 2000; i++)
        {
            double d = a * i / 2000.0;
            double hl = ContainmentFieldGeometry.HeightAt(p, c - a + d * 0.999999), hr = ContainmentFieldGeometry.HeightAt(p, c + a - d * 0.999999);
            if (hl < prev - 1e-9) mono = false;
            if (Math.Abs(hl - hr) > 1e-6) sym = false;
            prev = hl;
        }
        Check(mono, name + " monotonic left half"); Check(sym, name + " symmetric");
        for (double x = 1000.0; x < a; x += 24_000.0)
        {
            Near(ContainmentFieldGeometry.HeightAt(p, c + x), ContainmentFieldGeometry.HeightAt(p, c - x), 1e-6, name + " symmetric exact");
        }
        // slope vs numeric derivative
        foreach (double x in new[] { -120_000.0, -20_000.0, 30_000.0, 150_000.0 })
        {
            double num = (ContainmentFieldGeometry.HeightAt(p, c + x + 0.5) - ContainmentFieldGeometry.HeightAt(p, c + x - 0.5));
            Near(ContainmentFieldGeometry.SlopeAt(p, c + x), num, 1e-6, name + " slope");
        }
    }

    private void ArchLengths(string name, in ContainmentFieldProfile p)
    {
        double a = p.HalfWidth, b = p.Rise;
        // independent midpoint integration of the half ellipse
        const int N = 400_000;
        double sum = 0.0, h = Math.PI / N;
        for (int i = 0; i < N; i++) { double ph = (i + 0.5) * h; sum += Math.Sqrt(a * a * Math.Sin(ph) * Math.Sin(ph) + b * b * Math.Cos(ph) * Math.Cos(ph)); }
        double total = ContainmentFieldGeometry.ArchLength(p);
        Near(total, 2.0 * p.SkirtHeightMeters + sum * h, 0.001, name + " arch length vs independent integration");
        if (name == "current") Near(total, ExpectedArchCertified, 0.01, name + " arch length constant");
        // round trip s -> point -> Nearest -> s, and unit-step chords
        double worst = 0.0, chordWorst = 0.0;
        for (int i = 0; i <= 2000; i++)
        {
            double s = total * i / 2000.0;
            ContainmentFieldPoint pt = ContainmentFieldGeometry.SurfaceAtArchLength(p, 123.0, s);
            Near(pt.ArchLengthMeters, s, 1e-9, name + " arch stored");
            ContainmentFieldPoint back = ContainmentFieldGeometry.Nearest(p, new RingCanonicalPosition(123.0, pt.VerticalMeters, pt.AcrossMeters));
            worst = Math.Max(worst, Math.Abs(back.ArchLengthMeters - s));
            if (i % 5 == 0 && s + 1.0 <= total)
            {
                ContainmentFieldPoint nx = ContainmentFieldGeometry.SurfaceAtArchLength(p, 123.0, s + 1.0);
                chordWorst = Math.Max(chordWorst, Math.Abs(Math.Sqrt((nx.AcrossMeters - pt.AcrossMeters) * (nx.AcrossMeters - pt.AcrossMeters) + (nx.VerticalMeters - pt.VerticalMeters) * (nx.VerticalMeters - pt.VerticalMeters)) - 1.0));
            }
        }
        Check(worst < 0.01, $"{name} arch round trip worst {worst}");
        Check(chordWorst < 1e-6, $"{name} unit-step chord error {chordWorst}");
    }

    // ---- Brute force Nearest -------------------------------------------------------------------------------------------------------------------

    private void BruteForce(string name, ContainmentFieldProfile p, int randomPoints)
    {
        const int Coarse = 200_000;
        double total = ContainmentFieldGeometry.ArchLength(p);
        var ca = new double[Coarse + 1]; var cv = new double[Coarse + 1];
        for (int i = 0; i <= Coarse; i++) { ContainmentFieldPoint pt = ContainmentFieldGeometry.SurfaceAtArchLength(p, 0.0, total * i / Coarse); ca[i] = pt.AcrossMeters; cv[i] = pt.VerticalMeters; }
        double step = total / Coarse;
        var pts = new List<(double X, double V)>();
        var rng = new Random(20261105);
        for (int i = 0; i < randomPoints; i++) pts.Add((-4000.0 + (W + 8000.0) * rng.NextDouble(), -3000.0 + 46_000.0 * rng.NextDouble()));
        double c = p.Centre, crown = p.CrownMeters, r0 = p.RootAcross0, rw = p.RootAcrossW, top = p.WallTopMeters;
        // near the skirt/ellipse join, both sides
        foreach (double dx in new[] { -300.0, -30.0, -2.0, -0.01, 0.0, 0.01, 2.0, 30.0, 300.0 })
        {
            foreach (double dv in new[] { -300.0, -1.0, 0.0, 1.0, 300.0 }) { pts.Add((r0 + dx, crown + dv)); pts.Add((rw - dx, crown + dv)); }
        }
        // on and around the wall top, under it, beyond the walls
        foreach (double x in new[] { r0 - 50.0, r0, r0 + 30.0, 0.0, 10.0, W - 10.0, W, rw - 30.0, rw, rw + 50.0 })
        {
            foreach (double dv in new[] { -3000.0, -1.0, 0.0, 1.0, 50.0 }) pts.Add((x, top + dv));
        }
        // centreline and the apex
        foreach (double v in new[] { -8000.0, 0.0, 20_000.0, 39_999.0, 40_000.0, 40_050.0, 46_000.0 }) { pts.Add((c, v)); pts.Add((c + 0.5, v)); }
        double worstD = 0.0, worstPar = 0.0;
        foreach ((double qx, double qv) in pts)
        {
            double best = double.MaxValue;
            var cands = new List<int>();
            var d2 = new double[Coarse + 1];
            for (int i = 0; i <= Coarse; i++) { double dx = ca[i] - qx, dv = cv[i] - qv; d2[i] = dx * dx + dv * dv; }
            for (int i = 0; i <= Coarse; i++) if ((i == 0 || d2[i] <= d2[i - 1]) && (i == Coarse || d2[i] <= d2[i + 1])) cands.Add(i);
            foreach (int i in cands.OrderBy(i => d2[i]).Take(4))
            {
                double lo = Math.Max(0.0, total * i / Coarse - step), hi = Math.Min(total, total * i / Coarse + step);
                double f(double s) { ContainmentFieldPoint pt = ContainmentFieldGeometry.SurfaceAtArchLength(p, 0.0, s); double dx = pt.AcrossMeters - qx, dv = pt.VerticalMeters - qv; return Math.Sqrt(dx * dx + dv * dv); }
                for (int it = 0; it < 70; it++) { double m1 = lo + (hi - lo) / 3.0, m2 = hi - (hi - lo) / 3.0; if (f(m1) < f(m2)) hi = m2; else lo = m1; }
                best = Math.Min(best, f(0.5 * (lo + hi)));
            }
            var q = new RingCanonicalPosition(777.0, qv, qx);
            ContainmentFieldPoint n = ContainmentFieldGeometry.Nearest(p, q);
            double nd = Math.Sqrt((n.AcrossMeters - qx) * (n.AcrossMeters - qx) + (n.VerticalMeters - qv) * (n.VerticalMeters - qv));
            double diff = Math.Abs(nd - best);
            worstD = Math.Max(worstD, diff);
            if (diff > 0.01) _failures.Add($"{name} nearest ({qx:F2},{qv:F2}) distance {nd} vs brute {best}");
            Check(nd <= best + 1e-6, $"{name} nearest not minimal ({qx:F2},{qv:F2}) {nd} > {best}");
            double ds = Math.Abs(Math.Abs(ContainmentFieldGeometry.SignedDistance(p, q)) - nd);
            Check(ds < 1e-9, name + " |signed| equals nearest distance");
            Check(n.AroundMeters == 777.0, name + " nearest keeps around");
            // residual parallel to the normal away from the root ends
            double ra = qx - n.AcrossMeters, rv = qv - n.VerticalMeters, rd = Math.Sqrt(ra * ra + rv * rv);
            if (rd > 1e-3 && n.ArchLengthMeters > 1e-3 && n.ArchLengthMeters < total - 1e-3)
            {
                double cross = Math.Abs(ra * n.NormalVertical - rv * n.NormalAcross) / rd;
                worstPar = Math.Max(worstPar, cross);
                // a skirt end that is the nearest (clamped at the crown/wall top) is the only non-perpendicular case, and that is at arch 0 / total
            }
        }
        Check(worstPar < 1e-6, $"{name} residual not parallel to normal: {worstPar}");
        Console.WriteLine($"containment field brute force {name}: {pts.Count} points, worst distance difference {worstD:E2} m, worst normal misalignment {worstPar:E2}");
    }

    // ---- Signed queries ------------------------------------------------------------------------------------------------------------------------

    private void Signed(in ContainmentFieldProfile p)
    {
        double c = p.Centre, top = p.WallTopMeters, crown = p.CrownMeters, r0 = p.RootAcross0, rw = p.RootAcrossW;
        RingCanonicalPosition Q(double across, double v) => new(1000.0, v, across);
        Near(ContainmentFieldGeometry.SignedDistance(p, Q(c, 40_000.0 + 100.0)), 100.0, 1e-6, "above apex");
        Near(ContainmentFieldGeometry.SignedDistance(p, Q(c, 40_000.0 - 100.0)), -100.0, 1e-6, "below apex");
        Near(ContainmentFieldGeometry.SignedDistance(p, Q(r0 + 10.0, top + 500.0)), -10.0, 1e-9, "inside skirt");
        Near(ContainmentFieldGeometry.SignedDistance(p, Q(r0 - 10.0, top + 500.0)), 10.0, 1e-9, "outside skirt");
        Near(ContainmentFieldGeometry.SignedDistance(p, Q(rw - 7.0, top + 100.0)), -7.0, 1e-9, "inside skirt W");
        Near(ContainmentFieldGeometry.SignedDistance(p, Q(r0 - 30.0, top - 30.0)), Math.Sqrt(1800.0), 1e-9, "outboard below wall top");
        Near(ContainmentFieldGeometry.SignedDistance(p, Q(c, 0.0)), -Math.Min(40_000.0, ContainmentFieldGeometry.HeightAt(p, c)) * 0.0 - SignedMag(p, Q(c, 0.0)), 1e-9, "centre floor magnitude");
        Check(ContainmentFieldGeometry.SignedDistance(p, Q(c, 0.0)) < 0.0, "inside at floor");
        Check(ContainmentFieldGeometry.SignedDistance(p, Q(r0 + 1.0, crown + 5.0)) < 0.0 && ContainmentFieldGeometry.SignedDistance(p, Q(r0 - 1.0, crown + 5.0)) > 0.0, "sign across crown");
        Check(ContainmentFieldGeometry.SignedDistance(p, Q(c, 50_000.0)) > 0.0, "above field positive");

        Check(ContainmentFieldGeometry.IsContained(p, Q(c, 0.0)), "contained centre");
        Check(!ContainmentFieldGeometry.IsContained(p, Q(c, 40_000.5)), "not contained above apex");
        Check(ContainmentFieldGeometry.IsContained(p, Q(0.0, top + 10.0)), "contained above the habitat half of the wall top");
        Check(ContainmentFieldGeometry.IsContained(p, Q(r0 + 5.0, top + 10.0)), "contained between root and inner face above the wall top");
        Check(!ContainmentFieldGeometry.IsContained(p, Q(r0 + 5.0, top - 10.0)), "wall body is not contained");
        Check(!ContainmentFieldGeometry.IsContained(p, Q(r0 - 5.0, top + 10.0)), "outboard of the root is vacuum");
        Check(!ContainmentFieldGeometry.IsContained(p, Q(rw + 5.0, top + 10.0)), "outboard of root W");
        Check(ContainmentFieldGeometry.IsContained(p, Q(W - 5.0, 500.0)), "contained near inner face W");
        var rng = new Random(7);
        int mismatch = 0;
        for (int i = 0; i < 4000; i++)
        {
            RingCanonicalPosition q = Q(W * rng.NextDouble(), -5000.0 + 50_000.0 * rng.NextDouble());
            if (ContainmentFieldGeometry.IsContained(p, q) != (ContainmentFieldGeometry.SignedDistance(p, q) < 0.0)) mismatch++;
        }
        Check(mismatch == 0, $"IsContained vs SignedDistance over the habitat: {mismatch} mismatches");

        Near(ContainmentFieldGeometry.ClearanceBelow(p, Q(c, 0.0)), p.ApexMeters, 1e-6, "clearance centre");
        Near(ContainmentFieldGeometry.ClearanceBelow(p, Q(1000.0, 5000.0)), ContainmentFieldGeometry.HeightAt(p, 1000.0) - 5000.0, 1e-9, "clearance 1 km");
        Check(double.IsNaN(ContainmentFieldGeometry.ClearanceBelow(p, Q(-1.0, 0.0))) && double.IsNaN(ContainmentFieldGeometry.ClearanceBelow(p, Q(W + 1.0, 0.0))), "clearance NaN outside the habitat");
        Check(ContainmentFieldGeometry.ClearanceBelow(p, Q(0.0, 0.0)) > 0.0 && !double.IsNaN(ContainmentFieldGeometry.ClearanceBelow(p, Q(W, 0.0))), "clearance on the inner faces");
    }

    private static double SignedMag(in ContainmentFieldProfile p, RingCanonicalPosition q) => Math.Abs(ContainmentFieldGeometry.SignedDistance(p, q));

    // ---- around invariance and wrap ------------------------------------------------------------------------------------------------------------

    private void AroundInvariance(in ContainmentFieldProfile p, double circ)
    {
        var rng = new Random(99);
        for (int i = 0; i < 200; i++)
        {
            double across = -300.0 + (W + 600.0) * rng.NextDouble(), v = -2000.0 + 44_000.0 * rng.NextDouble(), around = circ * rng.NextDouble();
            ContainmentFieldPoint n0 = ContainmentFieldGeometry.Nearest(p, new(around, v, across));
            foreach (double shifted in new[] { around + circ, around - circ, around + 3.0 * circ, 0.0, circ - 1.0 })
            {
                ContainmentFieldPoint n1 = ContainmentFieldGeometry.Nearest(p, new(shifted, v, across));
                if (n0.AcrossMeters != n1.AcrossMeters || n0.VerticalMeters != n1.VerticalMeters || n0.ArchLengthMeters != n1.ArchLengthMeters || n1.AroundMeters != shifted) { _failures.Add("nearest depends on around"); break; }
            }
            if (ContainmentFieldGeometry.SignedDistance(p, new(around, v, across)) != ContainmentFieldGeometry.SignedDistance(p, new(around + circ, v, across))) { _failures.Add("signed distance depends on around"); break; }
        }
        // wrap: the world position and the inverse mapping agree across the seam
        RingLodGeometrySnapshot s = Snap(p.WallTopMeters, new RingCanonicalPosition(circ - 50.0, 5000.0, 100_000.0));
        Vector3 w1 = s.CanonicalToWorld(circ - 10.0, 8000.0, 50_000.0), w2 = s.CanonicalToWorld(-10.0, 8000.0, 50_000.0), w3 = s.CanonicalToWorld(2.0 * circ - 10.0, 8000.0, 50_000.0);
        Check(w1.DistanceTo(w2) < 0.001f && w1.DistanceTo(w3) < 0.001f, "world position wraps");
        RingCanonicalPosition back = ContainmentFieldPlacement.WorldToCanonical(s, w1);
        Near(back.AroundMeters, circ - 10.0, 0.01, "WorldToCanonical around"); Near(back.VerticalMeters, 8000.0, 0.01, "WorldToCanonical vertical"); Near(back.AcrossMeters, 50_000.0, 0.01, "WorldToCanonical across");
        Check(ContainmentFieldPlacement.WorldToCanonical(s, w2).AroundMeters >= circ - 20.0, "WorldToCanonical wraps to [0, C)");
    }

    // ---- Placement -----------------------------------------------------------------------------------------------------------------------------

    private void Placement(in ContainmentFieldProfile p)
    {
        double total = ContainmentFieldGeometry.ArchLength(p);
        var grid = new Transform3D(new Basis(Vector3.Up, 0.7f), new Vector3(10f, 20f, 30f));
        foreach (double s in new[] { 800.0, p.SkirtHeightMeters + 60_000.0, total * 0.5 })
        {
            double around = 1_234_567.0;
            ContainmentFieldPoint pt = ContainmentFieldGeometry.SurfaceAtArchLength(p, around, s);
            // a snapshot whose floating origin sits near the point, like the running game
            var anchor = new RingCanonicalPosition(around + 150.0, pt.VerticalMeters - 40.0, pt.AcrossMeters + 80.0);
            RingLodGeometrySnapshot snap = Snap(p.WallTopMeters, anchor, grid);
            Transform3D wt = ContainmentFieldPlacement.WorldTransform(snap, pt);
            Check(wt.Origin.DistanceTo(snap.CanonicalToWorld(pt.AroundMeters, pt.VerticalMeters, pt.AcrossMeters)) < 0.001f, "WorldTransform origin vs CanonicalToWorld");
            Check(Math.Abs(wt.Basis.Determinant() - 1.0f) < 1e-4f && wt.Basis.X.IsNormalized() && wt.Basis.Y.IsNormalized() && wt.Basis.Z.IsNormalized(), "WorldTransform right-handed orthonormal");
            // Y = -N: an offset along N moves the world point along -Y
            Transform3D off = ContainmentFieldPlacement.WorldTransform(snap, pt, 100.0);
            Check(((off.Origin - wt.Origin) + wt.Basis.Y * 100f).Length() < 0.01f, "normal offset follows -Y");
            // X follows the around axis
            Vector3 dAround = snap.CanonicalToWorld(pt.AroundMeters + 1.0, pt.VerticalMeters, pt.AcrossMeters) - snap.CanonicalToWorld(pt.AroundMeters, pt.VerticalMeters, pt.AcrossMeters);
            Check(dAround.Normalized().Dot(wt.Basis.X) > 0.999999f, "X is the around tangent");
            // arch tangent: a small step along the curve moves along -Z
            ContainmentFieldPoint nx = ContainmentFieldGeometry.SurfaceAtArchLength(p, around, Math.Min(total, s + 0.5));
            ContainmentFieldPoint pv = ContainmentFieldGeometry.SurfaceAtArchLength(p, around, Math.Max(0.0, s - 0.5));
            Vector3 dArch = snap.CanonicalToWorld(nx.AroundMeters, nx.VerticalMeters, nx.AcrossMeters) - snap.CanonicalToWorld(pv.AroundMeters, pv.VerticalMeters, pv.AcrossMeters);
            Check(dArch.Normalized().Dot(-wt.Basis.Z) > 0.9999f, "arch tangent is -Z");
            // AnchoredAt: vertices relative to the anchored snapshot plus the point's world position give the same world position
            RingLodGeometrySnapshot anchored = ContainmentFieldPlacement.AnchoredAt(snap, pt);
            double worst = 0.0;
            var rng = new Random((int)s);
            for (int i = 0; i < 200; i++)
            {
                double da = (rng.NextDouble() - 0.5) * 4000.0, dv = (rng.NextDouble() - 0.5) * 4000.0, dc = (rng.NextDouble() - 0.5) * 4000.0;
                Vector3 direct = snap.CanonicalToWorld(pt.AroundMeters + da, pt.VerticalMeters + dv, pt.AcrossMeters + dc);
                Vector3 viaAnchor = anchored.CanonicalToWorld(pt.AroundMeters + da, pt.VerticalMeters + dv, pt.AcrossMeters + dc) + wt.Origin;
                worst = Math.Max(worst, direct.DistanceTo(viaAnchor));
            }
            Check(worst < 0.001, $"AnchoredAt vs CanonicalToWorld worst {worst} m at s={s}");
        }
        ContainmentFieldPoint r0 = ContainmentFieldPlacement.RimRoot(p, 0, 5.0), r1 = ContainmentFieldPlacement.RimRoot(p, 1, 5.0);
        Near(r0.AcrossMeters, p.RootAcross0, 1e-9, "rim root 0"); Near(r0.VerticalMeters, p.WallTopMeters, 1e-9, "rim root 0 vertical");
        Near(r1.AcrossMeters, p.RootAcrossW, 1e-9, "rim root W"); Near(r1.VerticalMeters, p.WallTopMeters, 1e-6, "rim root W vertical");
        var (ar, ac, nm) = ContainmentFieldPlacement.CanonicalFrame(ContainmentFieldGeometry.SurfaceAtArchLength(p, 0.0, total * 0.3));
        Check(Math.Abs(ac.Across * nm.Across + ac.Vertical * nm.Vertical) < 1e-12 && Math.Abs(ar.Around - 1.0) < 1e-12, "canonical frame orthogonal");
    }

    // ---- World segments ------------------------------------------------------------------------------------------------------------------------

    private void Segments(ContainmentFieldProfile p)
    {
        double circ = CertifiedRingScale.EffectiveCircumferenceMeters;
        double c = p.Centre, top = p.WallTopMeters, r0 = p.RootAcross0;
        RingLodGeometrySnapshot S(double around, double v, double across) => Snap(top, new RingCanonicalPosition(around, v, across), new Transform3D(new Basis(Vector3.Up, 0.3f), new Vector3(5f, -7f, 9f)));
        bool Hit(RingLodGeometrySnapshot s, (double a, double v, double x) from, (double a, double v, double x) to, out ContainmentFieldPoint hit, out double t)
            => ContainmentFieldGeometry.TryIntersectWorldSegment(p, s, s.CanonicalToWorld(from.a, from.v, from.x), s.CanonicalToWorld(to.a, to.v, to.x), out hit, out t);

        // climb through the apex
        RingLodGeometrySnapshot s1 = S(2000.0, 38_000.0, c);
        Check(Hit(s1, (2000.0, 30_000.0, c), (2000.0, 45_000.0, c), out ContainmentFieldPoint h1, out double t1), "climb hits");
        Near(h1.VerticalMeters, 40_000.0, 0.05, "apex hit height"); Near(t1, 10_000.0 / 15_000.0, 1e-5, "apex hit t");
        Check(Hit(s1, (2000.0, 45_000.0, c), (2000.0, 30_000.0, c), out ContainmentFieldPoint h1r, out double t1r) && Math.Abs(h1r.VerticalMeters - 40_000.0) < 0.05 && Math.Abs(t1r - 5000.0 / 15000.0) < 1e-5, "descent hits");
        // crossing the skirt through the projector stack (horizontal, around fixed)
        RingLodGeometrySnapshot s2 = S(9000.0, top + 800.0, r0);
        Check(Hit(s2, (9000.0, top + 800.0, r0 - 100.0), (9000.0, top + 800.0, r0 + 100.0), out ContainmentFieldPoint h2, out double t2), "skirt crossing hits");
        Near(h2.AcrossMeters, r0, 0.01, "skirt hit across"); Near(h2.VerticalMeters, top + 800.0, 0.01, "skirt hit vertical"); Near(t2, 0.5, 1e-4, "skirt hit t");
        // walking across the wall top crosses the field (decision 3), in and out
        RingLodGeometrySnapshot s3 = S(40_000.0, top + 2.0, r0);
        Check(Hit(s3, (40_000.0, top + 2.0, r0 - 64.0), (40_000.0, top + 2.0, r0 + 128.0), out ContainmentFieldPoint h3, out double t3), "wall-top walk hits");
        Near(h3.AcrossMeters, r0, 0.01, "wall-top walk across"); Near(t3, 64.0 / 192.0, 1e-4, "wall-top walk t");
        Check(Hit(s3, (40_000.0, top + 2.0, r0 + 128.0), (40_000.0, top + 2.0, r0 - 64.0), out ContainmentFieldPoint h3r, out _) && Math.Abs(h3r.AcrossMeters - r0) < 0.01, "wall-top walk outward hits");
        // misses: well inside, well outside, below the wall top through the wall body (no skirt there), and fully beyond the wall
        RingLodGeometrySnapshot s4 = S(50_000.0, 5000.0, 150_000.0);
        Check(!Hit(s4, (50_000.0, 5000.0, 10_000.0), (50_000.0, 5000.0, 200_000.0), out _, out _), "inside segment misses");
        Check(!Hit(s4, (50_000.0, 45_000.0, 10_000.0), (50_000.0, 45_000.0, 200_000.0), out _, out _), "outside segment above the apex misses");
        RingLodGeometrySnapshot s5 = S(50_000.0, top - 100.0, r0);
        Check(!Hit(s5, (50_000.0, top - 100.0, r0 - 100.0), (50_000.0, top - 100.0, r0 + 100.0), out _, out _), "crossing the wall body below the wall top is not a field crossing");
        Check(!Hit(s5, (50_000.0, top + 5000.0, r0 - 400.0), (50_000.0, top + 8000.0, r0 - 100.0), out _, out _), "outboard segment misses");
        // across the around seam
        RingLodGeometrySnapshot s6 = S(circ - 20.0, top + 900.0, r0);
        Check(Hit(s6, (circ - 400.0, top + 900.0, r0 - 40.0), (circ + 400.0, top + 900.0, r0 + 40.0), out ContainmentFieldPoint h6, out double t6), "seam-crossing segment hits");
        Near(h6.AcrossMeters, r0, 0.02, "seam hit across"); Near(t6, 0.5, 1e-3, "seam hit t");
        // grazing: a 1.2 km segment slipping 1 cm through the skirt plane near the crown, and the same stopping 1 cm short
        RingLodGeometrySnapshot s7 = S(70_000.0, p.CrownMeters - 100.0, r0);
        Check(Hit(s7, (69_400.0, p.CrownMeters - 100.0, r0 + 0.01), (70_600.0, p.CrownMeters - 100.0, r0 - 0.01), out ContainmentFieldPoint h7, out double t7), "grazing crossing hits");
        Near(h7.AcrossMeters, r0, 0.02, "grazing hit across"); Near(t7, 0.5, 0.01, "grazing hit t");
        Check(!Hit(s7, (69_400.0, p.CrownMeters - 100.0, r0 + 0.02), (70_600.0, p.CrownMeters - 100.0, r0 + 0.01), out _, out _), "grazing near miss stays inside");
        // a segment that dips through the apex by 1 mm between samples 500 m apart (both ends and every coarse sample outside)
        RingLodGeometrySnapshot s8 = S(80_000.0, 39_999.0, c);
        Check(Hit(s8, (80_000.0, 39_999.999, c - 1200.0), (80_000.0, 39_999.999, c + 800.0), out ContainmentFieldPoint h8, out double t8), "dip below the apex is found");
        Check(Math.Abs(h8.AcrossMeters - (c - 42.0)) < 3.0 && t8 > 0.5 && t8 < 0.65, "dip hit at the first crossing");
        Check(!Hit(s8, (80_000.0, 40_000.001, c - 1200.0), (80_000.0, 40_000.001, c + 800.0), out _, out _), "skim above the apex misses");
        // starting on the field and a zero-length segment
        Check(Hit(s1, (2000.0, 40_000.0, c), (2000.0, 41_000.0, c), out ContainmentFieldPoint h9, out double t9) && t9 < 1e-3 && Math.Abs(h9.VerticalMeters - 40_000.0) < 0.05, "segment starting on the field");
        Check(!Hit(s1, (2000.0, 30_000.0, c), (2000.0, 30_000.0, c), out _, out _), "zero-length segment misses");
    }

    // ---- FromSnapshot and wall-depth modes -----------------------------------------------------------------------------------------------------

    private void FromSnapshot()
    {
        Check(CertifiedRingScale.StructuralWallTopMeters == 10_000, "Certified wall top is 10 km");
        foreach ((double top, double expectRise) in new[] { ((double)CertifiedRingScale.StructuralWallTopMeters, 28_400.0), (10_400.0, 28_000.0) })
        {
            ContainmentFieldProfile p = ContainmentFieldProfile.FromSnapshot(Snap(top), W, RimWallDescription.Current);
            Near(p.WallTopMeters, top, 0.0, "FromSnapshot wall top"); Near(p.ApexMeters, 40_000.0, 0.0, "FromSnapshot apex");
            Near(p.RootAcross0, -64.0, 0.0, "FromSnapshot root 0"); Near(p.RootAcrossW, W + 64.0, 0.0, "FromSnapshot root W");
            Near(p.HalfWidth, (W + 128.0) * 0.5, 0.0, "FromSnapshot half width"); Near(p.Rise, expectRise, 1e-9, "FromSnapshot rise");
            Near(p.SkirtHeightMeters, 1600.0, 0.0, "FromSnapshot skirt"); Check(p.IsValid, "FromSnapshot valid");
            Near(ContainmentFieldGeometry.HeightAt(p, W * 0.5), 40_000.0, 1e-6, "FromSnapshot apex height");
        }
        Check(RimWallDescription.Current.DepthMeters == 128.0 && RimWallDescription.Current.Root == RimFieldRoot.CentreLine, "RimWallDescription.Current");
        Check(!new ContainmentFieldProfile(W, 10_000.0, 10_050.0, 0.0, W).IsValid, "profile with no rise is invalid");
    }

    private void WallModes()
    {
        double top = 10_000.0;
        var m0 = new[] { Prof(top, new(0, RimFieldRoot.InnerEdge)), Prof(top, new(0, RimFieldRoot.CentreLine)), Prof(top, new(0, RimFieldRoot.OuterEdge)) };
        Check(m0[0] == m0[1] && m0[1] == m0[2], "depth 0: profiles equal in every mode");
        bool identical = true;
        var rng = new Random(5);
        for (int i = 0; i < 500 && identical; i++)
        {
            double x = W * rng.NextDouble(), v = -1000.0 + 45_000.0 * rng.NextDouble();
            var q = new RingCanonicalPosition(10.0, v, x);
            long h0 = BitConverter.DoubleToInt64Bits(ContainmentFieldGeometry.HeightAt(m0[0], x));
            long d0 = BitConverter.DoubleToInt64Bits(ContainmentFieldGeometry.SignedDistance(m0[0], q));
            ContainmentFieldPoint n0 = ContainmentFieldGeometry.Nearest(m0[0], q);
            for (int k = 1; k < 3; k++)
            {
                ContainmentFieldPoint nk = ContainmentFieldGeometry.Nearest(m0[k], q);
                if (BitConverter.DoubleToInt64Bits(ContainmentFieldGeometry.HeightAt(m0[k], x)) != h0 || BitConverter.DoubleToInt64Bits(ContainmentFieldGeometry.SignedDistance(m0[k], q)) != d0
                    || n0 != nk || ContainmentFieldGeometry.ArchLength(m0[k]) != ContainmentFieldGeometry.ArchLength(m0[0])) identical = false;
            }
        }
        Check(identical, "depth 0: every mode is byte-identical");
        foreach ((RimFieldRoot root, double e) in new[] { (RimFieldRoot.OuterEdge, 100.0), (RimFieldRoot.CentreLine, 50.0), (RimFieldRoot.InnerEdge, 0.0) })
        {
            ContainmentFieldProfile p = Prof(top, new(100, root));
            Near(p.RootAcross0, -e, 0.0, root + " root 0"); Near(p.RootAcrossW, W + e, 0.0, root + " root W");
            Near(ContainmentFieldGeometry.HeightAt(p, p.RootAcross0), top, 0.0, root + " H(root)"); Near(ContainmentFieldGeometry.HeightAt(p, p.Centre), 40_000.0, 1e-6, root + " H(c)");
            Near(p.Centre, W * 0.5, 1e-9, root + " centre");
        }
    }

    // ---- Cloud ceiling -------------------------------------------------------------------------------------------------------------------------

    private void CloudCeiling(in ContainmentFieldProfile p)
    {
        Check(ContainmentFieldGeometry.CloudCeilingMeters == RingCloudLayers.CeilingMeters, "cap equals RingCloudLayers.CeilingMeters");
        double firstBelow = -1.0;
        for (int i = 0; i <= 6000; i++)
        {
            double x = W * i / 6000.0, h = ContainmentFieldGeometry.HeightAt(p, x), ceil = ContainmentFieldGeometry.CloudCeilingAt(p, x);
            double expect = h - 300.0 >= 18_000.0 ? 18_000.0 : h - 300.0;
            if (ceil != expect) { _failures.Add($"ceiling at {x}: {ceil} vs {expect}"); break; }
            if (ContainmentFieldGeometry.CloudCeilingFromUnderside(h) != ceil) { _failures.Add($"CloudCeilingFromUnderside != CloudCeilingAt at {x}"); break; }
            if (MegastormFieldCoupling.CeilingFromUnderside(h) != ceil) { _failures.Add($"MegastormFieldCoupling stub != CloudCeilingAt at {x}"); break; }
            if (firstBelow < 0.0 && ceil >= 18_000.0) firstBelow = x;
        }
        Check(ContainmentFieldGeometry.CloudCeilingAt(p, W * 0.5) == 18_000.0, "ceiling exactly 18 km at the centre");
        Check(ContainmentFieldGeometry.CloudCeilingAt(p, 0.0) < 18_000.0, "ceiling below 18 km at the inner face");
        Check(ContainmentFieldGeometry.CloudCeilingAt(p, -500.0) == 18_000.0 && ContainmentFieldGeometry.CloudCeilingAt(p, W + 5000.0) == 18_000.0, "ceiling is the cap with no field");
        Near(ContainmentFieldGeometry.CloudCeilingFromUnderside(p.ApexMeters), 18_000.0, 0.0, "underside at apex");
        Console.WriteLine($"containment field cloud ceiling wall top {p.WallTopMeters}: inner-face ceiling {ContainmentFieldGeometry.CloudCeilingAt(p, 0.0):F1} m, reaches 18 km {firstBelow:F0} m from the inner face");
    }
}
