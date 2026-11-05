using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Containment field near patch (docs/containment-field-design.md 3.3, 1.6): mesh build, window clamping, coverage, noise continuity, planner, bounds, timing.
/// Pure C#; no scene. Run: Godot --headless --path . --script res://tools/containment_field_near_patch_test.gd. Prints CONTAINMENT_FIELD_NEAR_PATCH=PASS.
/// </summary>
public partial class ContainmentFieldNearPatchTest : RefCounted
{
    private const double W = 318_000.0;
    private const int V = ContainmentNearPatchMesh.Quads + 1;
    private static readonly double L = ContainmentFieldNearPatch.NoisePeriodMeters;

    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok) _failures.Add(message); }
    private void Near(double a, double b, double tol, string message) { if (!(Math.Abs(a - b) <= tol)) _failures.Add($"{message}: {a.ToString("R", CultureInfo.InvariantCulture)} vs {b.ToString("R", CultureInfo.InvariantCulture)} tol {tol}"); }

    private static RingLodGeometrySnapshot Snap(double wallTop, RingCanonicalPosition? anchor = null, Transform3D? grid = null)
        => new(grid ?? Transform3D.Identity, Vector3.Zero, Vector3.Zero, CertifiedRingScale.EffectiveRadiusMeters, 0.0, 0.0,
            CertifiedRingScale.EffectiveCircumferenceMeters, -8192.0, wallTop, anchor);

    private static ContainmentFieldProfile Prof(double wallTop)
        => ContainmentFieldProfile.FromSnapshot(Snap(wallTop), W, RimWallDescription.Current);

    private static double CircDiff(double a, double b, double m)
    {
        double d = (a - b) % m;
        if (d > m * 0.5) d -= m; else if (d < -m * 0.5) d += m;
        return d;
    }

    public int Run()
    {
        try
        {
            double circ = CertifiedRingScale.EffectiveCircumferenceMeters;
            var grid = new Transform3D(new Basis(Vector3.Up, 0.37f), new Vector3(1234f, -56f, 789f));
            foreach (double wallTop in new[] { 10_000.0, 10_400.0 })
            {
                ContainmentFieldProfile p = Prof(wallTop);
                Check(p.IsValid, "valid " + wallTop);
                RingLodGeometrySnapshot live = Snap(wallTop, new RingCanonicalPosition(circ * 0.4 + 321.0, wallTop + 77.0, 6543.0), grid);
                string tag = "wt" + wallTop;
                MeshBasics(tag, p, live, circ);
                WindowClamp(tag, p, live);
                Coverage(tag, p, live, circ);
                NoiseContinuity(tag, p, live, circ);
                Planner(tag, p, live, circ);
            }
            Timing(Prof(10_000.0), Snap(10_000.0, new RingCanonicalPosition(5000.0, 10_100.0, 100.0), grid));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.ToString());
            Console.Error.WriteLine("CONTAINMENT_FIELD_NEAR_PATCH=FAIL exception " + e.Message);
            return 1;
        }
        if (_failures.Count != 0) { Console.Error.WriteLine("CONTAINMENT_FIELD_NEAR_PATCH=FAIL " + string.Join(" | ", _failures.Take(25))); return 1; }
        Console.WriteLine("CONTAINMENT_FIELD_NEAR_PATCH=PASS");
        return 0;
    }

    private static ContainmentNearPatchMesh BuildAt(in ContainmentFieldProfile p, in RingLodGeometrySnapshot live, in RingCanonicalPosition cam, double n)
        => ContainmentFieldNearPatch.Build(p, live, ContainmentFieldGeometry.Nearest(p, cam), n);

    // ---- 1, 2, 7 ---------------------------------------------------------------------------------------------------------------------------------

    private void MeshBasics(string tag, in ContainmentFieldProfile p, in RingLodGeometrySnapshot live, double circ)
    {
        var cam = new RingCanonicalPosition(circ * 0.4 + 321.0, 38_000.0, W * 0.5 + 12_000.0);
        const double N = 3000.0;
        ContainmentNearPatchMesh m = BuildAt(p, live, cam, N);
        Check(m.Positions.Length == V * V && m.Normals.Length == V * V && m.Tangents.Length == V * V && m.Uv.Length == V * V && m.Uv2.Length == V * V, tag + " vertex counts");
        Check(m.Indices.Length == 48 * 48 * 6, tag + " index count");
        Check(m.Indices.All(i => i >= 0 && i < V * V), tag + " indices in range");
        bool degenerate = false;
        for (int t = 0; t < m.Indices.Length; t += 3)
        {
            Vector3 a = m.Positions[m.Indices[t]], b = m.Positions[m.Indices[t + 1]], c = m.Positions[m.Indices[t + 2]];
            if ((b - a).Cross(c - a).Length() < 1e-3f) degenerate = true;
        }
        Check(!degenerate, tag + " non-degenerate triangles");

        // Reference in double (the live snapshot's own float CanonicalToWorld is only good to ~2 mm at its floating-origin distance).
        (double ax0, double ay0, double az0) = live.CanonicalToAbsoluteLocal(m.Anchor.AroundMeters, m.Anchor.VerticalMeters, m.Anchor.AcrossMeters);
        Basis gb = live.GridGlobalTransform.Basis;
        double archLength = ContainmentFieldGeometry.ArchLength(p);
        int sampled = 0;
        for (int j = 0; j < V; j += 4)
        {
            double s = m.ArchStart + (m.ArchEnd - m.ArchStart) * j / 48.0;
            ContainmentFieldPoint pt = ContainmentFieldGeometry.SurfaceAtArchLength(p, m.Anchor.AroundMeters, s);
            for (int i = 0; i < V; i += 4)
            {
                double around = m.Anchor.AroundMeters - m.AroundHalfSpan + 2.0 * m.AroundHalfSpan * i / 48.0;
                int k = j * V + i;
                (double ex, double ey, double ez) = live.CanonicalToAbsoluteLocal(around, pt.VerticalMeters, pt.AcrossMeters);
                Vector3 expectRel = gb * new Vector3((float)(ex - ax0), (float)(ey - ay0), (float)(ez - az0));
                Check(m.Positions[k].DistanceTo(expectRel) <= 0.001f, tag + $" position ({i},{j}) {m.Positions[k].DistanceTo(expectRel)}");
                ContainmentFieldPlacement.AxesAt(live, around, out Vector3 ad, out Vector3 ud, out Vector3 ac);
                Vector3 en = ud * (float)pt.NormalVertical + ac * (float)pt.NormalAcross;
                Check(Math.Abs(m.Normals[k].Length() - 1f) < 1e-4f && (m.Normals[k] - en).Length() < 1e-4f, tag + $" normal ({i},{j})");
                Check(Math.Abs(m.Tangents[k].Length() - 1f) < 1e-4f && (m.Tangents[k] - ad).Length() < 1e-4f, tag + $" tangent ({i},{j})");
                Check(Math.Abs(m.Tangents[k].Dot(m.Normals[k])) < 1e-4f, tag + $" tangent perpendicular ({i},{j})");
                // Normal points into the contained volume: moving along it from a point on the arch must be inside.
                Near(m.Uv2[k].X, Math.Min(s, archLength - s), 1e-2, tag + " uv2.x");
                sampled++;
            }
        }
        Check(sampled >= 30, tag + " sampled " + sampled);
        Check(m.GridBasis == live.GridGlobalTransform.Basis, tag + " grid basis");

        // 7 bounds
        Aabb grown = m.Bounds.Grow(1e-3f);
        Check(m.Positions.All(grown.HasPoint), tag + " bounds contain positions");
        Vector3 mn = m.Positions[0], mx = m.Positions[0];
        foreach (Vector3 v in m.Positions) { mn = new Vector3(Math.Min(mn.X, v.X), Math.Min(mn.Y, v.Y), Math.Min(mn.Z, v.Z)); mx = new Vector3(Math.Max(mx.X, v.X), Math.Max(mx.Y, v.Y), Math.Max(mx.Z, v.Z)); }
        Check((m.Bounds.Position - mn).Length() < 1e-3f && (m.Bounds.End - mx).Length() < 1e-3f, tag + " bounds tight");
    }

    // ---- 3 ---------------------------------------------------------------------------------------------------------------------------------------

    private void WindowClamp(string tag, in ContainmentFieldProfile p, in RingLodGeometrySnapshot live)
    {
        double archLength = ContainmentFieldGeometry.ArchLength(p), around = 50_000.0, n = 3000.0;
        ContainmentNearPatchMesh m0 = BuildAt(p, live, new RingCanonicalPosition(around, p.WallTopMeters + 50.0, 200.0), n);
        Near(m0.ArchStart, 0.0, 0.0, tag + " ArchStart clamped");
        Near(m0.Uv2[0].X, 0.0, 1e-6, tag + " Uv2.x at root row (left)");
        Check(m0.ArchEnd < archLength, tag + " left ArchEnd short of far wall");
        ContainmentNearPatchMesh m1 = BuildAt(p, live, new RingCanonicalPosition(around, p.WallTopMeters + 50.0, W - 200.0), n);
        Near(m1.ArchEnd, archLength, 0.0, tag + " ArchEnd clamped");
        Near(m1.Uv2[48 * V].X, 0.0, 1e-3, tag + " Uv2.x at root row (right)");
        ContainmentNearPatchMesh ma = BuildAt(p, live, new RingCanonicalPosition(around, p.ApexMeters - 100.0, W * 0.5), n);
        Near((ma.ArchStart + ma.ArchEnd) * 0.5, archLength * 0.5, 1.0, tag + " apex window centred");
        Near(ma.ArchEnd - ma.ArchStart, 2.2 * n, 1e-6, tag + " apex window span");
        Near(ma.Uv2[24 * V].X, archLength * 0.5, 1.0, tag + " Uv2.x at apex row");
        Near(ma.Uv[24 * V + 24].X, 0.0, 1e-3, tag + " Uv centre around");
    }

    // ---- 4 ---------------------------------------------------------------------------------------------------------------------------------------

    private void Coverage(string tag, in ContainmentFieldProfile p, in RingLodGeometrySnapshot live, double circ)
    {
        double archLength = ContainmentFieldGeometry.ArchLength(p), wt = p.WallTopMeters;
        var poses = new List<RingCanonicalPosition>();
        double[] arounds = { 0.3 * circ, 300.0, circ - 300.0 };
        foreach (double a in arounds)
        {
            poses.Add(new(a, p.CrownMeters - 100.0, 100.0));                                           // inside near crown/skirt junction
            poses.Add(new(a, wt + 800.0, 100.0));                                                      // mid skirt, habitat side
            poses.Add(new(a, wt, 200.0));                                                              // wall-top height, habitat side
            poses.Add(new(a, wt + 500.0, p.RootAcross0 - 300.0));                                      // vacuum side beyond the root
            poses.Add(new(a, wt + 500.0, p.RootAcrossW + 300.0));
            poses.Add(new(a, p.ApexMeters - 1500.0, W * 0.5));                                         // under apex
            poses.Add(new(a, p.ApexMeters + 1500.0, W * 0.5));                                         // above apex
            poses.Add(new(a, ContainmentFieldGeometry.HeightAt(p, 30_000.0) - 700.0, 30_000.0));       // under the ellipse flank
            poses.Add(new(a, ContainmentFieldGeometry.HeightAt(p, 80_000.0) + 700.0, 80_000.0));       // above the flank
        }
        var rng = new Random(1234);
        foreach (double n in new[] { 3000.0, 10_000.0 })
        {
            int used = 0, covered = 0;
            foreach (RingCanonicalPosition cam in poses)
            {
                double sd = ContainmentFieldGeometry.SignedDistance(p, cam);
                if (Math.Abs(sd) >= n * ContainmentFieldNearPatch.VisibleRangeFactor) continue;
                used++;
                ContainmentNearPatchMesh m = BuildAt(p, live, cam, n);
                (double cx, double cy, double cz) = live.CanonicalToAbsoluteLocal(cam.AroundMeters, cam.VerticalMeters, cam.AcrossMeters);
                int local = 0;
                for (int k = 0; k < 4000 || local < 2000; k++)
                {
                    if (k > 200_000) break;
                    double around = m.Anchor.AroundMeters + (rng.NextDouble() * 2.0 - 1.0) * 2.2 * m.AroundHalfSpan;
                    double s = Math.Clamp(m.Anchor.ArchLengthMeters + (rng.NextDouble() * 2.0 - 1.0) * 2.2 * n, 0.0, archLength);
                    ContainmentFieldPoint pt = ContainmentFieldGeometry.SurfaceAtArchLength(p, around, s);
                    (double x, double y, double z) = live.CanonicalToAbsoluteLocal(around, pt.VerticalMeters, pt.AcrossMeters);
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy) + (z - cz) * (z - cz));
                    if (d >= n) continue;
                    local++;
                    bool inside = Math.Abs(CircDiff(around, m.Anchor.AroundMeters, circ)) <= m.AroundHalfSpan && s >= m.ArchStart && s <= m.ArchEnd;
                    if (!inside)
                    {
                        Check(false, $"{tag} coverage miss N={n} cam=({cam.AroundMeters:F0},{cam.VerticalMeters:F0},{cam.AcrossMeters:F0}) d={d:F1} dAround={CircDiff(around, m.Anchor.AroundMeters, circ):F1} H={m.AroundHalfSpan:F1} s={s:F1} [{m.ArchStart:F1},{m.ArchEnd:F1}]");
                        break;
                    }
                }
                covered += local;
                Check(local >= 2000, $"{tag} coverage samples {local} at pose ({cam.AroundMeters:F0},{cam.VerticalMeters:F0},{cam.AcrossMeters:F0}) N={n}");
            }
            Check(used >= 12, $"{tag} coverage poses used {used} N={n}");
        }
    }

    // ---- 5 ---------------------------------------------------------------------------------------------------------------------------------------

    private void NoiseContinuity(string tag, in ContainmentFieldProfile p, in RingLodGeometrySnapshot live, double circ)
    {
        double n = 3000.0;
        double scale = ContainmentFieldNearPatch.NoiseAroundScale(circ);
        Near(L / ContainmentFieldNearPatch.LatticeSpacingMeters, 45.0, 0.0, tag + " noise period is 45 lattice spacings");
        Near(circ * scale / L, Math.Round(circ * scale / L), 1e-9, tag + " circumference is whole noise periods");
        var pairs = new (RingCanonicalPosition A, RingCanonicalPosition B)[]
        {
            (new(200_000.0, p.ApexMeters - 100.0, W * 0.5), new(200_000.0 + 1500.0, p.ApexMeters - 100.0, W * 0.5 + 1500.0)),
            (new(circ - 500.0, p.ApexMeters - 100.0, W * 0.5), new(500.0, p.ApexMeters - 100.0, W * 0.5 + 1200.0)),
        };
        foreach ((RingCanonicalPosition ca, RingCanonicalPosition cb) in pairs)
        {
            ContainmentNearPatchMesh ma = BuildAt(p, live, ca, n), mb = BuildAt(p, live, cb, n);
            Check(ma.Anchor.ArchLengthMeters != mb.Anchor.ArchLengthMeters || ca.AroundMeters != cb.AroundMeters, tag + " distinct anchors");
            // Compare every vertex of each mesh against the canonical expectation, and a shared physical point between the two.
            foreach (ContainmentNearPatchMesh m in new[] { ma, mb })
            {
                for (int j = 0; j < V; j += 3)
                {
                    double s = m.ArchStart + (m.ArchEnd - m.ArchStart) * j / 48.0;
                    for (int i = 0; i < V; i += 3)
                    {
                        double around = m.Anchor.AroundMeters - m.AroundHalfSpan + 2.0 * m.AroundHalfSpan * i / 48.0;
                        Vector2 coord = m.Uv[j * V + i] + m.NoiseOriginMeters;
                        Vector2 expect = ContainmentFieldNearPatch.NoiseOrigin(around, s, circ);
                        double dx = CircDiff(coord.X, expect.X, L), dy = CircDiff(coord.Y, expect.Y, L);
                        if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01) { Check(false, $"{tag} noise coord ({i},{j}) off by ({dx:F4},{dy:F4})"); i = V; j = V; }
                    }
                }
            }
            // Same physical point in both: around shared by absolute value mod C, arch shared exactly, uses ma's row 24 / column 24 as the reference.
            double sharedS = Math.Max(ma.ArchStart, mb.ArchStart) + 100.0, sharedAround = ca.AroundMeters + 50.0;
            Vector2 va = NoiseAt(ma, sharedAround, sharedS, circ), vb = NoiseAt(mb, sharedAround, sharedS, circ);
            Check(Math.Abs(CircDiff(va.X, vb.X, L)) <= 0.01 && Math.Abs(CircDiff(va.Y, vb.Y, L)) <= 0.01, $"{tag} shared point noise differs ({va}) vs ({vb})");
            // Hexagonal lattice: same physical points agree mod (1024, 1024 sqrt 3)
            double lp = ContainmentFieldNearPatch.LatticeArchPeriodMeters, ls = ContainmentFieldNearPatch.LatticeSpacingMeters;
            for (int q = 0; q < 6; q++)
            {
                double qs = sharedS + 137.0 * q, qa = sharedAround + 211.0 * q - 300.0;
                Vector2 la = LatticeAt(ma, qa, qs, circ), lb = LatticeAt(mb, qa, qs, circ);
                Check(Math.Abs(CircDiff(la.X, lb.X, ls)) <= 0.01 && Math.Abs(CircDiff(la.Y, lb.Y, lp)) <= 0.01, $"{tag} lattice shared point {q} differs ({la}) vs ({lb})");
            }
            // Drift
            ContainmentNearPatchMesh d = ma;
            foreach (double t in new[] { 0.0, 1234.5, 1e6 })
            {
                Vector4 o0 = ContainmentFieldNearPatch.NoiseOrigins(d, t), o1 = ContainmentFieldNearPatch.NoiseOrigins(d, t + 10.0);
                Check(o0.X >= 0f && o0.X < L && o0.Y >= 0f && o0.Y < L && o0.Z >= 0f && o0.Z < L && o0.W >= 0f && o0.W < L && o1.X >= 0f && o1.X < L && o1.W < L, tag + " noise origins in [0, L)");
                Near(CircDiff(o1.X - o0.X, 10.0 * ContainmentFieldNearPatch.DriftOctave1.X, L), 0.0, 5e-3 + t * 1e-7, tag + " drift o1.x");
                Near(CircDiff(o1.Y - o0.Y, 10.0 * ContainmentFieldNearPatch.DriftOctave1.Y, L), 0.0, 5e-3 + t * 1e-7, tag + " drift o1.y");
                Near(CircDiff(o1.Z - o0.Z, 10.0 * ContainmentFieldNearPatch.DriftOctave2.X, L), 0.0, 5e-3 + t * 1e-7, tag + " drift o2.x");
                Near(CircDiff(o1.W - o0.W, 10.0 * ContainmentFieldNearPatch.DriftOctave2.Y, L), 0.0, 5e-3 + t * 1e-7, tag + " drift o2.y");
            }
        }
    }

    /// <summary>The noise coordinate a mesh assigns to a canonical (around, arch) point: (Uv + origin), with around taken as the mesh's unwrapped value.</summary>
    private static Vector2 NoiseAt(ContainmentNearPatchMesh m, double around, double s, double circ)
    {
        double dAround = CircDiff(around, m.Anchor.AroundMeters, circ);
        double ux = dAround * m.NoiseAroundScale, uy = s - m.Anchor.ArchLengthMeters;
        Vector2 o = m.NoiseOriginMeters;
        return new Vector2((float)(((ux + o.X) % L + L) % L), (float)(((uy + o.Y) % L + L) % L));
    }

    /// <summary>Lattice coordinate (Uv + LatticeOrigin) of a canonical point reduced to one cell, as the shader evaluates it.</summary>
    private static Vector2 LatticeAt(ContainmentNearPatchMesh m, double around, double s, double circ)
    {
        double ls = ContainmentFieldNearPatch.LatticeSpacingMeters, lp = ContainmentFieldNearPatch.LatticeArchPeriodMeters;
        Vector2 o = ContainmentFieldNearPatch.LatticeOrigin(m);
        double ux = CircDiff(around, m.Anchor.AroundMeters, circ) * m.NoiseAroundScale + o.X, uy = s - m.Anchor.ArchLengthMeters + o.Y;
        return new Vector2((float)(((ux % ls) + ls) % ls), (float)(((uy % lp) + lp) % lp));
    }

    // ---- 6 ---------------------------------------------------------------------------------------------------------------------------------------

    private void Planner(string tag, in ContainmentFieldProfile p, in RingLodGeometrySnapshot live, double circ)
    {
        const double N = 3000.0;
        double apex = p.ApexMeters, mid = W * 0.5;
        var pl = new ContainmentNearPatchPlanner();
        ContainmentNearPatchView v = pl.Update(p, live, new RingCanonicalPosition(1000.0, 0.0, mid), N);
        Check(!v.Visible && v.Side == ContainmentFieldViewSide.None && pl.BuildCount == 0 && pl.Mesh == null && !v.Rebuilt, tag + " ground not visible");
        v = pl.Update(p, live, new RingCanonicalPosition(1000.0, apex - 50_000.0, mid), N);
        Check(!v.Visible && pl.BuildCount == 0, tag + " 50 km away not visible");
        v = pl.Update(p, live, new RingCanonicalPosition(1000.0, apex - 2000.0, mid), N);
        Check(v.Visible && v.Side == ContainmentFieldViewSide.Below && v.Rebuilt && pl.BuildCount == 1 && pl.Mesh != null, tag + " 2 km under apex visible Below");
        Near(v.SignedDistanceMeters, -2000.0, 1.0, tag + " sd under apex");
        ContainmentNearPatchMesh first = pl.Mesh!;
        v = pl.Update(p, live, new RingCanonicalPosition(1000.0, apex - 2000.0, mid), N);
        Check(v.Visible && !v.Rebuilt && pl.BuildCount == 1, tag + " same pose no rebuild");
        v = pl.Update(p, live, new RingCanonicalPosition(1000.0 + N / 5.0, apex - 2000.0, mid), N);
        Check(v.Visible && !v.Rebuilt && pl.BuildCount == 1 && ReferenceEquals(pl.Mesh, first), tag + " N/5 along around no rebuild");
        v = pl.Update(p, live, new RingCanonicalPosition(1000.0 + N / 3.0, apex - 2000.0, mid), N);
        Check(v.Visible && v.Rebuilt && pl.BuildCount == 2, tag + " N/3 along around rebuilds");
        // along arch
        v = pl.Update(p, live, new RingCanonicalPosition(1000.0 + N / 3.0, apex - 2000.0, mid + N / 5.0), N);
        Check(!v.Rebuilt, tag + " N/5 along arch no rebuild");
        v = pl.Update(p, live, new RingCanonicalPosition(1000.0 + N / 3.0, apex - 2000.0, mid + N / 3.0), N);
        Check(v.Rebuilt, tag + " N/3 along arch rebuilds");
        // around wrap
        pl = new ContainmentNearPatchPlanner();
        pl.Update(p, live, new RingCanonicalPosition(circ - 100.0, apex - 2000.0, mid), N);
        v = pl.Update(p, live, new RingCanonicalPosition(100.0, apex - 2000.0, mid), N);
        Check(!v.Rebuilt && pl.BuildCount == 1, tag + " wrapped 200 m move no rebuild");
        // above, vacuum
        v = pl.Update(p, live, new RingCanonicalPosition(100.0, apex + 2000.0, mid), N);
        Check(v.Visible && v.Side == ContainmentFieldViewSide.Above, tag + " above apex Above");
        v = pl.Update(p, live, new RingCanonicalPosition(100.0, p.WallTopMeters + 500.0, p.RootAcross0 - 300.0), N);
        Check(v.Visible && v.Side == ContainmentFieldViewSide.Above, tag + " vacuum beyond root Above");
        // range edge
        pl = new ContainmentNearPatchPlanner();
        Check(pl.Update(p, live, new RingCanonicalPosition(0.0, apex - 1.1 * N + 5.0, mid), N).Visible, tag + " just under 1.1 N visible");
        Check(!pl.Update(p, live, new RingCanonicalPosition(0.0, apex - 1.1 * N - 5.0, mid), N).Visible, tag + " just over 1.1 N not visible");
        // range change, profile change
        pl = new ContainmentNearPatchPlanner();
        var cam = new RingCanonicalPosition(0.0, apex - 2000.0, mid);
        pl.Update(p, live, cam, N);
        Check(pl.Update(p, live, cam, N * 2.0).Rebuilt && pl.BuildCount == 2, tag + " range change rebuilds");
        Check(pl.Update(p with { WallTopMeters = p.WallTopMeters + 5.0 }, live, cam, N * 2.0).Rebuilt && pl.BuildCount == 3, tag + " profile change rebuilds");
        Check(pl.Update(p with { WallTopMeters = p.WallTopMeters + 5.0 }, live with { GridGlobalTransform = new Transform3D(new Basis(Vector3.Up, 0.9f), Vector3.Zero) }, cam, N * 2.0).Rebuilt, tag + " grid basis change rebuilds");
        // kept when not visible
        ContainmentNearPatchMesh kept = pl.Mesh!;
        int count = pl.BuildCount;
        v = pl.Update(p with { WallTopMeters = p.WallTopMeters + 5.0 }, live, new RingCanonicalPosition(0.0, 0.0, mid), N * 2.0);
        Check(!v.Visible && v.Side == ContainmentFieldViewSide.None && !v.Rebuilt && pl.BuildCount == count && ReferenceEquals(pl.Mesh, kept), tag + " mesh kept when not visible");
        pl.Invalidate();
        Check(pl.Mesh == null, tag + " invalidate");
    }

    // ---- 8 ---------------------------------------------------------------------------------------------------------------------------------------

    private void Timing(in ContainmentFieldProfile p, in RingLodGeometrySnapshot live)
    {
        ContainmentFieldPoint anchor = ContainmentFieldGeometry.Nearest(p, new RingCanonicalPosition(5000.0, p.ApexMeters - 1000.0, W * 0.5));
        for (int i = 0; i < 5; i++) ContainmentFieldNearPatch.Build(p, live, anchor, 3000.0);
        var times = new double[20];
        for (int i = 0; i < times.Length; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            ContainmentFieldNearPatch.Build(p, live, anchor, 3000.0);
            times[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }
        Array.Sort(times);
        double median = 0.5 * (times[9] + times[10]);
        Console.WriteLine($"CONTAINMENT_FIELD_NEAR_PATCH build median {median.ToString("F3", CultureInfo.InvariantCulture)} ms (min {times[0]:F3}, max {times[^1]:F3})");
        Check(median < 5.0, $"build median {median:F3} ms >= 5 ms");

        // Per-frame costs (median over 9 batches of 2000 calls, microseconds).
        var vis = new RingCanonicalPosition(5000.0, p.ApexMeters - 1000.0, W * 0.5 + 400.0);
        var far = new RingCanonicalPosition(5000.0, 0.0, W * 0.5);
        const double N = 3000.0;
        ContainmentFieldProfile pp = p;
        RingLodGeometrySnapshot lv = live;
        var pl = new ContainmentNearPatchPlanner();
        pl.Update(pp, lv, vis, N);
        double sink = 0.0;
        double sdUs = MedianUs(() => sink += ContainmentFieldGeometry.SignedDistance(pp, vis));
        double nearUs = MedianUs(() => sink += ContainmentFieldGeometry.Nearest(pp, vis).Phi);
        double updVis = MedianUs(() => sink += pl.Update(pp, lv, vis, N).SignedDistanceMeters);
        double updFar = MedianUs(() => sink += pl.Update(pp, lv, far, N).SignedDistanceMeters);
        Console.WriteLine($"CONTAINMENT_FIELD_NEAR_PATCH per-call us: SignedDistance {sdUs:F2}, Nearest {nearUs:F2}, Update(visible, no rebuild) {updVis:F2}, Update(out of range) {updFar:F2} (sink {sink:F0})");
        Check(pl.BuildCount == 1, "timing loop caused rebuilds");
        Check(updVis < 15.0, $"Update(visible, no rebuild) {updVis:F2} us >= 15 us");
    }

    private static double MedianUs(Action body)
    {
        for (int i = 0; i < 200; i++) body();
        var t = new double[9];
        for (int b = 0; b < t.Length; b++)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < 2000; i++) body();
            t[b] = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency / 2000.0;
        }
        Array.Sort(t);
        return t[4];
    }
}
