using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Rendering;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Pure frame tests of the precipitation occlusion map (docs/storm-revamp-s1-design.md section 6): snap stability, recentre thresholds, plane height clamp,
/// floating-origin rebase invariance, frame vectors against the real canonical-to-world mapping, curvature bound, projection, and the render-layer policy.
/// Run: Godot --headless --path . --script res://tools/precipitation_occlusion_frame_test.gd. Prints PRECIP_OCCLUSION_FRAME=PASS.
/// </summary>
public partial class PrecipitationOcclusionFrameTest : RefCounted
{
    private static RingLodGeometrySnapshot Geometry(double radius, Basis basis, Vector3 gridOrigin, Vector3 floatingOffset, RingCanonicalPosition? anchor) =>
        new(new Transform3D(basis, gridOrigin), floatingOffset, Vector3.Zero, radius, 0.0, 0.0, radius * Math.Tau, -1000.0, 1000.0, anchor);

    public int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        var nodes = new List<Node>();
        try
        {
            double radius = CertifiedRingScale.EffectiveRadiusMeters, circ = radius * Math.Tau;
            // --- snapping
            var a = new RingCanonicalPosition(123456.3, 40.0, 54321.4);
            PrecipitationOcclusionFrame f = PrecipitationOcclusionFrame.Create(a, 600, circ);
            Check(f.CentreAround == 123456.0 && f.CentreAcross == 54322.0, $"snap to 2 m: {f.CentreAround},{f.CentreAcross}");
            foreach (double jitter in new[] { -0.9, -0.4, 0.0, 0.6, 0.99 })
                Check(PrecipitationOcclusionFrame.Create(new RingCanonicalPosition(123456.0 + jitter, 40, 54322.0 - jitter), 600, circ) == PrecipitationOcclusionFrame.Create(new RingCanonicalPosition(123456.0, 40, 54322.0), 600, circ), $"cell stable at jitter {jitter}");
            Check(PrecipitationOcclusionFrame.Create(new RingCanonicalPosition(123458.1, 40, 54322.0), 600, circ).CentreAround == 123458.0, "next cell moves by 2 m");
            Check(f.PlaneVertical % PrecipitationOcclusionFrame.HeightQuantumMeters == 0, "plane quantised");
            // --- plane height clamp
            Check(PrecipitationOcclusionFrame.DesiredHeight(100, 110) == 40, "min height 40");
            Check(PrecipitationOcclusionFrame.DesiredHeight(100, 5000) == 1000, "max height 1000");
            Check(PrecipitationOcclusionFrame.DesiredHeight(100, double.NaN) == 500, "NaN base falls back");
            PrecipitationOcclusionFrame low = PrecipitationOcclusionFrame.Create(new RingCanonicalPosition(0, 100, 0), 105, circ);
            Check(low.PlaneHeightMeters >= 40 && low.FarMeters == low.PlaneHeightMeters + 64, $"low base plane height {low.PlaneHeightMeters}");
            // --- recentre / rebuild thresholds
            Check(!f.NeedsRebuild(new RingCanonicalPosition(123456.3 + 15.0, 40, 54321.4), 600, circ), "no rebuild within 16 m");
            Check(f.NeedsRebuild(new RingCanonicalPosition(123456.0 + 16.5, 40, 54322.0), 600, circ), "rebuild past 16 m around");
            Check(f.NeedsRebuild(new RingCanonicalPosition(123456.0, 40, 54322.0 - 16.5), 600, circ), "rebuild past 16 m across");
            Check(f.NeedsRebuild(new RingCanonicalPosition(123456.0, 40 + 17, 54322.0), 600, circ), "rebuild on 17 m climb");
            Check(!f.NeedsRebuild(new RingCanonicalPosition(123456.0, 40 + 10, 54322.0), 600, circ), "no rebuild on 10 m climb");
            Check(f.NeedsRebuild(new RingCanonicalPosition(123456.0, 40, 54322.0), 700, circ), "rebuild when the cloud base moves 100 m");
            PrecipitationOcclusionFrame seam = PrecipitationOcclusionFrame.Create(new RingCanonicalPosition(circ - 1.0, 10, 5), 300, circ);
            Check(!seam.NeedsRebuild(new RingCanonicalPosition(3.0, 10, 5), 300, circ), $"seam wrap-aware (centre {seam.CentreAround})");
            Check(seam.CentreAround >= 0 && seam.CentreAround < circ, "centre wrapped into [0, circumference)");
            // --- rebase invariance and frame vectors under a rotated grid
            Basis grid = new Basis(new Vector3(0.3f, 0.8f, 0.5f).Normalized(), 0.7f);
            var observer = new RingCanonicalPosition(2_500_000.7, 55.0, 41_000.2);
            PrecipitationOcclusionFrame frame = PrecipitationOcclusionFrame.Create(observer, 800, circ);
            RingLodGeometrySnapshot g1 = Geometry(radius, grid, new Vector3(100, -20, 30), Vector3.Zero, new RingCanonicalPosition(2_499_000, 40, 40_000));
            RingLodGeometrySnapshot g2 = Geometry(radius, grid, new Vector3(100, -20, 30), Vector3.Zero, new RingCanonicalPosition(2_500_900, 60, 41_200));
            Transform3D t1 = frame.WorldTransform(g1), t2 = frame.WorldTransform(g2);
            Check(t1.Basis.IsEqualApprox(t2.Basis), "basis unchanged by a rebase");
            bool origin1Small = t1.Origin.Length() < 5000f && t2.Origin.Length() < 5000f;
            Check(origin1Small, "frame origins stay floating-origin-relative");
            var rng = new Random(11);
            for (int i = 0; i < 40; i++)
            {
                double da = (rng.NextDouble() - .5) * 120, dv = (rng.NextDouble() - .5) * 200, dc = (rng.NextDouble() - .5) * 120;
                double ca = frame.CentreAround + da, cv = frame.PlaneVertical + dv, cc = frame.CentreAcross + dc;
                PrecipitationOcclusionFrame.Project(g1.CanonicalToWorld(ca, cv, cc), t1, out Vector2 uv1, out float d1);
                PrecipitationOcclusionFrame.Project(g2.CanonicalToWorld(ca, cv, cc), t2, out Vector2 uv2, out float d2);
                Check(uv1.DistanceTo(uv2) < 0.05f && Math.Abs(d1 - d2) < 0.01f, $"rebase invariant projection #{i}: {uv1} vs {uv2}, {d1} vs {d2}");
            }
            // Frame vectors agree with finite differences of the true canonical-to-world mapping.
            Basis cb = t1.Basis;
            Vector3 p0 = g1.CanonicalToWorld(frame.CentreAround, frame.PlaneVertical, frame.CentreAcross);
            Vector3 up = (g1.CanonicalToWorld(frame.CentreAround, frame.PlaneVertical + 1, frame.CentreAcross) - p0).Normalized();
            Vector3 around = (g1.CanonicalToWorld(frame.CentreAround + 1, frame.PlaneVertical, frame.CentreAcross) - p0).Normalized();
            Vector3 across = (g1.CanonicalToWorld(frame.CentreAround, frame.PlaneVertical, frame.CentreAcross + 1) - p0).Normalized();
            Check(cb.Z.DistanceTo(up) < 1e-3f, $"Z is canonical up {cb.Z} vs {up}");
            Check(cb.Y.DistanceTo(around) < 1e-3f, $"Y is the around tangent {cb.Y} vs {around}");
            Check(Math.Abs(cb.X.Dot(across)) > 0.999f, "X is the across axis (sign free)");
            Check(Math.Abs(cb.X.Length() - 1) < 1e-4f && Math.Abs(cb.X.Dot(cb.Y)) < 1e-4f && Math.Abs(cb.Y.Dot(cb.Z)) < 1e-4f && Math.Abs(cb.Determinant() - 1) < 1e-3f, "orthonormal and right-handed");
            // --- projection convention
            PrecipitationOcclusionFrame.Project(t1.Origin, t1, out Vector2 centre, out float centreDepth);
            Check(centre.DistanceTo(new Vector2(256, 256)) < 1e-3f && Math.Abs(centreDepth) < 1e-3f, "plane centre projects to the middle at depth 0");
            PrecipitationOcclusionFrame.Project(t1.Origin + cb.Y * 4 - cb.Z * 10, t1, out Vector2 moved, out float movedDepth);
            Check(moved.DistanceTo(new Vector2(256, 256 - 16)) < 1e-2f && Math.Abs(movedDepth - 10) < 1e-3f, $"4 m along image-up is 16 texels up, 10 m down is depth 10: {moved} {movedDepth}");
            Check(PrecipitationOcclusionFrame.InsideMap(new Vector2(0.5f, 511.5f)) && !PrecipitationOcclusionFrame.InsideMap(new Vector2(-0.1f, 5)) && !PrecipitationOcclusionFrame.InsideMap(new Vector2(5, 512)), "inside-map bounds");
            // --- curvature bound at the map corner (ring surface under the flat plane)
            double corner = PrecipitationOcclusionFrame.HalfExtentMeters * Math.Sqrt(2.0);
            double sag80 = PrecipitationOcclusionFrame.CurvatureSagMeters(corner, 80_000.0), edge80 = PrecipitationOcclusionFrame.CurvatureSagMeters(PrecipitationOcclusionFrame.HalfExtentMeters, 80_000.0);
            Check(sag80 < 0.06 && Math.Abs(edge80 - 0.0256) < 0.002, $"curvature sag corner {sag80:0.0000} edge {edge80:0.0000} at 80 km");
            // The same sag measured through the real mapping: a point at constant canonical vertical, 64 m along, sits sag metres below the flat plane.
            Vector3 along = g1.CanonicalToWorld(frame.CentreAround + 64, frame.PlaneVertical, frame.CentreAcross);
            PrecipitationOcclusionFrame.Project(along, t1, out _, out float edgeDepth);
            double expected = PrecipitationOcclusionFrame.CurvatureSagMeters(64, radius);
            Check(Math.Abs(edgeDepth - expected) < 0.01, $"measured edge sag {edgeDepth:0.0000} vs {expected:0.0000}");
            // --- layer policy
            Check((RingRenderLayers.PrecipitationMapCullMask & RingRenderLayers.NoPrecipitationOcclusion) == 0 && (RingRenderLayers.PrecipitationMapCullMask & RingRenderLayers.Lights) == 0, "map cull mask excludes both dedicated bits");
            Check((RingRenderLayers.PrecipitationMapCullMask & 1u) != 0 && (RingRenderLayers.PrecipitationMapCullMask & 2u) != 0, "map cull mask keeps the default and POI layers");
            Check((RingRenderLayers.AllCameraLayers & RingRenderLayers.NoPrecipitationOcclusion) != 0 && (RingRenderLayers.AllCameraLayers & RingRenderLayers.Lights) != 0, "default camera still draws the dedicated bits");
            Check(RingRenderLayers.NoPrecipitationOcclusion != RingRenderLayers.Lights && (RingRenderLayers.NoPrecipitationOcclusion & 3u) == 0 && (RingRenderLayers.Lights & 3u) == 0, "bits distinct from layers 1-2");
            var light = new OmniLight3D(); var sun = new DirectionalLight3D(); nodes.Add(light); nodes.Add(sun);
            RingRenderLayers.Apply(light); RingRenderLayers.Apply(sun);
            Check(light.Layers == RingRenderLayers.Lights && sun.Layers == RingRenderLayers.Lights, "lights move to the Lights bit");
            Check(!RingRenderLayers.Apply(light), "apply is idempotent");
            var plain = new MeshInstance3D(); nodes.Add(plain);
            Check(!RingRenderLayers.IsNoPrecipitationOcclusion(plain) && !RingRenderLayers.Apply(plain) && plain.Layers == 1, "terrain-like geometry stays on layer 1");
            var grassOwner = new Voxel.LOD.RingStandingGrassLayer(); var grassMesh = new MultiMeshInstance3D(); grassOwner.AddChild(grassMesh); nodes.Add(grassOwner);
            var treeOwner = new Voxel.LOD.RingBiomeDecorationLayer(); var treeMesh = new MultiMeshInstance3D(); treeOwner.AddChild(treeMesh); nodes.Add(treeOwner);
            var weatherOwner = new RingLocalWeatherEffects(); var boltMesh = new MeshInstance3D(); weatherOwner.AddChild(boltMesh); nodes.Add(weatherOwner);
            var playerOwner = new Voxel.Integration.RingVoxelExplorerController(); var bodyMesh = new MeshInstance3D(); playerOwner.AddChild(bodyMesh); nodes.Add(playerOwner);
            foreach ((string name, GeometryInstance3D mesh) in new[] { ("grass", (GeometryInstance3D)grassMesh), ("tree", treeMesh), ("weather", boltMesh), ("player", bodyMesh) })
            {
                Check(RingRenderLayers.IsNoPrecipitationOcclusion(mesh), $"{name} owner rule");
                Check(RingRenderLayers.Apply(mesh) && mesh.Layers == RingRenderLayers.NoPrecipitationOcclusion, $"{name} moves to the exclusion layer");
            }
            var hidden = new MeshInstance3D { Layers = 0 }; grassOwner.AddChild(hidden);
            Check(!RingRenderLayers.Apply(hidden) && hidden.Layers == 0, "layers == 0 (hidden by toggle) is left alone");
            var grouped = new MeshInstance3D(); grouped.AddToGroup(RingRenderLayers.NoPrecipitationOcclusionGroup); var child = new MeshInstance3D(); grouped.AddChild(child); nodes.Add(grouped);
            Check(RingRenderLayers.IsNoPrecipitationOcclusion(child), "group rule covers descendants");
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        finally { foreach (Node n in nodes) if (GodotObject.IsInstanceValid(n)) n.Free(); }
        if (failures.Count != 0) { Console.Error.WriteLine("PRECIP_OCCLUSION_FRAME=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("PRECIP_OCCLUSION_FRAME=PASS");
        return 0;
    }
}
