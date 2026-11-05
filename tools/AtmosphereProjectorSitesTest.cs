using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Atmosphere-shield projector sites (docs/containment-field-design.md 6.1): one tower per wall-top anchor with the right transform, the walk footprint
/// data, and the analytic wall-top collision (RimWallTopWalk.Step with AtmosphereProjectorWallObstacles). Pure C#; no scene.
/// Run: Godot --headless --path . --script res://tools/atmosphere_projector_sites_test.gd. Prints ATMOSPHERE_PROJECTOR_SITES=PASS.
/// </summary>
public partial class AtmosphereProjectorSitesTest : RefCounted
{
    private const double W = 318_000.0;
    private const double D = 128.0;
    private const double Top = 10_400.0;
    private const double Half = 0.3;
    private const double Dt = 1.0 / 60.0;
    private const float Speed = 8f;

    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok) _failures.Add(message); }
    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static RingLodGeometrySnapshot Identity(double circumference, double radius)
        => new(Transform3D.Identity, Vector3.Zero, Vector3.Zero, radius, 0.0, 0.0, circumference, -22_304.0, Top, null);

    public int Run()
    {
        try
        {
            double circ = CertifiedRingScale.EffectiveCircumferenceMeters;
            double radius = CertifiedRingScale.EffectiveRadiusMeters;
            RingLodGeometrySnapshot identity = Identity(circ, radius);
            Placement("identity", identity);
            RimWallTopAnchor first = RimWallTopAnchors.Build(identity, W, RimWallDescription.Current)[0];
            RingLodGeometrySnapshot moved = identity with
            {
                GridGlobalTransform = new Transform3D(new Basis(new Vector3(0.3f, 0.8f, -0.5f).Normalized(), 0.7f), new Vector3(1234.5f, -987.6f, 4321f)),
                FloatingOriginWorldOffset = new Vector3(10f, 20f, -30f),
                FloatingOriginCanonicalAnchor = new RingCanonicalPosition(first.AroundMeters, first.VerticalMeters, first.AcrossMeters),
            };
            Placement("rotated+anchored", moved);
            Footprint();
            WalkTests(circ);
        }
        catch (Exception exception)
        {
            _failures.Add("exception: " + exception);
        }
        if (_failures.Count == 0)
        {
            GD.Print("ATMOSPHERE_PROJECTOR_SITES=PASS");
            return 0;
        }
        foreach (string failure in _failures) GD.PrintErr("ATMOSPHERE_PROJECTOR_SITES FAIL: " + failure);
        GD.Print("ATMOSPHERE_PROJECTOR_SITES=FAIL");
        return 1;
    }

    // Unit direction (in world axes) of the canonical displacement; computed in absolute local doubles so the ring-scale positions do not lose the 1 m step.
    private static Vector3 Dir(in RingLodGeometrySnapshot s, double a0, double v0, double x0, double a1, double v1, double x1)
    {
        (double ax, double ay, double az) = s.CanonicalToAbsoluteLocal(a0, v0, x0);
        (double bx, double by, double bz) = s.CanonicalToAbsoluteLocal(a1, v1, x1);
        return (s.GridGlobalTransform.Basis * new Vector3((float)(bx - ax), (float)(by - ay), (float)(bz - az))).Normalized();
    }

    private void Placement(string name, RingLodGeometrySnapshot s)
    {
        RimWallDescription wall = RimWallDescription.Current;
        IReadOnlyList<RimWallTopAnchor> anchors = RimWallTopAnchors.Build(s, W, wall);
        AtmosphereProjectorSites sites = AtmosphereProjectorSites.Build(s, W, wall);
        Check(sites.Count == 252, $"{name}: 252 sites, got {sites.Count}");
        Check(sites.Matches(s.CircumferenceMeters, W, s.StructuralWallTopGlobalY, wall), name + ": Matches the build values");
        Check(!sites.Matches(s.CircumferenceMeters, W + 1.0, s.StructuralWallTopGlobalY, wall), name + ": does not match another width");
        var all = new Transform3D[sites.Count];
        sites.FillWorldTransforms(s, all);
        Check(wall.CentreLine(0, W) == -64.0 && wall.CentreLine(1, W) == W + 64.0, "live wall centre lines are -64 and W + 64");
        double worstOrigin = 0.0;
        for (int i = 0; i < sites.Count; i++)
        {
            RimWallTopAnchor a = anchors[i], site = sites[i];
            Check(a.Index == site.Index && a.Side == site.Side && a.CueLine == site.CueLine && a.AroundMeters == site.AroundMeters
                && a.AcrossMeters == site.AcrossMeters && a.VerticalMeters == site.VerticalMeters, $"{name}: site {i} equals the anchor");
            Check(site.VerticalMeters == s.StructuralWallTopGlobalY, $"{name}: site {i} vertical is the wall top");
            Check(site.AcrossMeters == (site.Side == 0 ? -64.0 : W + 64.0), $"{name}: site {i} across {F(site.AcrossMeters)} is the wall centre line");
            Transform3D t = sites.GetWorldTransform(s, i);
            Check(t == all[i], $"{name}: FillWorldTransforms equals GetWorldTransform for site {i}");
            Vector3 p = s.CanonicalToWorld(site.AroundMeters, site.VerticalMeters, site.AcrossMeters);
            double err = (t.Origin - p).Length();
            worstOrigin = Math.Max(worstOrigin, err);
            Check(err <= 1e-6 * p.Length() + 0.01, $"{name}: site {i} origin off by {F(err)} m");
            Basis b = t.Basis;
            double det = b.Determinant();
            Check(Math.Abs(det - 1.0) < 1e-5, $"{name}: site {i} determinant {F(det)}");
            Check(Math.Abs(b.X.Length() - 1f) < 1e-5 && Math.Abs(b.Y.Length() - 1f) < 1e-5 && Math.Abs(b.Z.Length() - 1f) < 1e-5
                && Math.Abs(b.X.Dot(b.Y)) < 1e-5 && Math.Abs(b.Y.Dot(b.Z)) < 1e-5 && Math.Abs(b.X.Dot(b.Z)) < 1e-5, $"{name}: site {i} basis not orthonormal");
            double up = b.Y.Dot(Dir(s, site.AroundMeters, site.VerticalMeters, site.AcrossMeters, site.AroundMeters, site.VerticalMeters + 1.0, site.AcrossMeters));
            double across = b.X.Dot(Dir(s, site.AroundMeters, site.VerticalMeters, site.AcrossMeters, site.AroundMeters, site.VerticalMeters, site.AcrossMeters + 1.0));
            double around = b.Z.Dot(Dir(s, site.AroundMeters, site.VerticalMeters, site.AcrossMeters, site.AroundMeters + 1.0, site.VerticalMeters, site.AcrossMeters));
            Check(up > 0.99999, $"{name}: site {i} basis Y vs up dot {F(up)}");
            Check(across > 0.99999, $"{name}: site {i} basis X vs +across dot {F(across)}");
            Check(around > 0.99999, $"{name}: site {i} basis Z vs +around dot {F(around)}");
        }
        if (s.FloatingOriginCanonicalAnchor != null)
        {
            Vector3 p0 = s.CanonicalToWorld(sites[0].AroundMeters, sites[0].VerticalMeters, sites[0].AcrossMeters);
            Check((all[0].Origin - p0).Length() <= 0.01, $"{name}: site 0 within 0.01 m of CanonicalToWorld");
        }
        GD.Print($"ATMOSPHERE_PROJECTOR_SITES {name}: worst origin error {worstOrigin:F5} m");
    }

    private void Footprint()
    {
        AtmosphereProjectorFootprint? fp = AtmosphereProjectorFootprint.Shared;
        Check(fp != null, "Footprint.Shared loads");
        if (fp == null) return;
        GD.Print($"ATMOSPHERE_PROJECTOR_SITES footprint rects {fp.Rects.Count}, slab top {fp.SlabTopMeters} m, max |x| {fp.MaxAbsX}, max |z| {fp.MaxAbsZ}");
        Check(fp.Rects.Count > 0, "footprint has rects");
        Check(fp.Rects.Count == 54, "footprint has 54 rects, got " + fp.Rects.Count);
        foreach (var r in fp.Rects)
            Check(r.X0 >= -40.0 && r.X1 <= 40.0 && r.Z0 >= -90.0 && r.Z1 <= 90.0 && r.X0 < r.X1 && r.Z0 < r.Z1, $"rect ({r.X0}, {r.Z0}, {r.X1}, {r.Z1}) in range");
        Check(fp.Blocks(20, 0, 0.3) && fp.Blocks(-20, 0, 0.3), "body blocks at (+-20, 0)");
        Check(fp.Blocks(30, 60, 0.3) && fp.Blocks(-30, -60, 0.3), "feet block at (30, 60), (-30, -60)");
        Check(!fp.Blocks(0, 0, 0.3), "central channel (0, 0) is open for a 0.6 m body");
        Check(!fp.Blocks(0, 60, 0.3), "court (0, 60) is open");
        Check(!fp.Blocks(50, 0, 0.3) && !fp.Blocks(0, 100, 0.3), "beside the tower is open");
        for (double z = -100.0; z <= 100.0; z += 0.25) Check(!fp.Blocks(0, z, Half), $"channel blocked at z {z}");
        var one = AtmosphereProjectorFootprint.FromRects(new[] { (0.0, 0.0, 10.0, 10.0) }, 3.0);
        Check(one.Blocks(5, 5, 0) && !one.Blocks(10, 5, 0) && one.Blocks(10.2, 5, 0.3) && !one.Blocks(10.3, 5, 0.3), "Blocks is strict on the grown edge");
    }

    private sealed class WalkResult
    {
        public double X, Z, Vertical;
        public Vector3 Velocity;
        public bool EnteredRect;
    }

    private static WalkResult Walk(AtmosphereProjectorSites sites, AtmosphereProjectorFootprint fp, IRimWallTopObstacles? obstacles, int side, int k,
        double x, double z, Vector3 command, double seconds, double aroundOverride = double.NaN)
    {
        double c = sites.CircumferenceMeters;
        RimWallTopAnchor site = sites[sites.IndexOf(side, k)];
        double around = double.IsNaN(aroundOverride) ? site.AroundMeters + z : aroundOverride;
        around = ((around % c) + c) % c;
        double across = site.AcrossMeters + x, vertical = Top;
        Vector3 velocity = command;
        var result = new WalkResult();
        int steps = (int)Math.Round(seconds / Dt);
        for (int i = 0; i < steps; i++)
        {
            velocity = command;
            RimWallTopWalk.Step(ref around, ref vertical, ref across, ref velocity, Dt, side, W, D, Top, Half, out _, obstacles);
            sites.NearestOnWall(side, around, across, out double lx, out double lz);
            if (fp.Blocks(lx, lz, 0.0)) result.EnteredRect = true;
        }
        sites.NearestOnWall(side, around, across, out result.X, out result.Z);
        result.Vertical = vertical;
        result.Velocity = velocity;
        return result;
    }

    private void WalkTests(double circ)
    {
        AtmosphereProjectorFootprint? fp = AtmosphereProjectorFootprint.Shared;
        if (fp == null) return;
        RingLodGeometrySnapshot s = Identity(circ, CertifiedRingScale.EffectiveRadiusMeters);
        AtmosphereProjectorSites sites = AtmosphereProjectorSites.Build(s, W, RimWallDescription.Current);
        var obstacles = new AtmosphereProjectorWallObstacles(sites, fp);
        Vector3 along = new(Speed, 0, 0), acrossPlus = new(0, 0, Speed), acrossMinus = new(0, 0, -Speed);

        WalkResult r = Walk(sites, fp, obstacles, 0, 3, 24, -200, along, 40);
        Check(r.Z > -90.6 && r.Z < -90.0 && r.Velocity.X == 0f && r.Vertical == Top, $"foot: stops at z {F(r.Z)}, vel.X {r.Velocity.X}, vertical {F(r.Vertical)}");
        Check(Math.Abs(r.X - 24.0) < 1e-6, "foot: x unchanged while blocked, got " + F(r.X));

        r = Walk(sites, fp, obstacles, 0, 3, 54, -200, along, 60);
        Check(r.Z > 150.0 && !r.EnteredRect, $"beside: z {F(r.Z)}");

        r = Walk(sites, fp, obstacles, 0, 3, 0, -200, along, 60);
        Check(r.Z > 150.0 && !r.EnteredRect, $"channel: z {F(r.Z)}, entered rect {r.EnteredRect}");

        r = Walk(sites, fp, obstacles, 0, 3, 54, 0, acrossMinus, 15);
        Check(r.X > 40.0 && r.X < 40.6 && r.Velocity.Z == 0f, $"body from +x: stops at x {F(r.X)}");

        r = Walk(sites, fp, obstacles, 0, 3, 10, 60, acrossPlus, 10);
        Check(r.X > 21.4 && r.X < 22.0 && r.Velocity.Z == 0f, $"court: stops at x {F(r.X)}");

        r = Walk(sites, fp, obstacles, 1, 3, -54, 0, acrossPlus, 15);
        Check(r.X > -40.6 && r.X < -40.0, $"side 1: stops at x {F(r.X)}");

        r = Walk(sites, fp, obstacles, 0, 0, 24, -200, along, 40, circ - 200.0);
        double around = ((sites[0].AroundMeters + r.Z) % circ + circ) % circ;
        Check(r.Z > -90.6 && r.Z < -90.0 && around > circ - 90.6 && around < circ - 90.0, $"wrap: local z {F(r.Z)}, around {F(around)}");

        r = Walk(sites, fp, obstacles, 0, 3, 20, 0, Vector3.Zero, Dt);
        Check(!fp.Blocks(r.X, r.Z, Half - 1e-6), $"spawn inside: still blocked at ({F(r.X)}, {F(r.Z)})");
        r = Walk(sites, fp, obstacles, 1, 125, -25, 70, Vector3.Zero, Dt);
        Check(!fp.Blocks(r.X, r.Z, Half - 1e-6), $"spawn inside a foot: still blocked at ({F(r.X)}, {F(r.Z)})");

        // Above the slab top nothing blocks: a body 10 m up crosses the footprint.
        {
            RimWallTopAnchor site = sites[sites.IndexOf(0, 3)];
            double aroundV = site.AroundMeters - 60.0, vert = Top + 10.0, acr = site.AcrossMeters + 24.0;
            Vector3 v = along;
            for (int i = 0; i < 60 * 10; i++)
            {
                v = along;
                RimWallTopWalk.Step(ref aroundV, ref vert, ref acr, ref v, Dt, 0, W, D, Top, Half, out _, obstacles);
            }
            Check(aroundV > site.AroundMeters + 10.0, "a body above the slab top is not blocked");
        }

        r = Walk(sites, fp, null, 0, 3, 24, -200, along, 40);
        Check(r.Z > 0.0 && r.Velocity.X == Speed, $"no obstacles: passes straight through, z {F(r.Z)}");
    }
}
