using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Pure frame tests of the wet map (docs/storm-revamp-s2-wet-world-design.md 6): texel snapping, floating-origin rebase invariance of the world to map conversion,
/// wrap at the ring circumference, plane coordinates (tile sizes divide the plane period).
/// Run: Godot --headless --path . --script res://tools/wet_map_frame_test.gd. Prints WET_MAP_FRAME=PASS.
/// </summary>
public partial class WetMapFrameTest : RefCounted
{
    private static RingLodGeometrySnapshot Geometry(double radius, Basis basis, Vector3 gridOrigin, RingCanonicalPosition? anchor) =>
        new(new Transform3D(basis, gridOrigin), Vector3.Zero, Vector3.Zero, radius, 0.0, 0.0, radius * Math.Tau, -1000.0, 1000.0, anchor);

    public int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            double radius = CertifiedRingScale.EffectiveRadiusMeters, circ = radius * Math.Tau;
            // --- snapping: stable inside a texel, moves by exactly one texel
            WetMapFrame f = WetMapFrame.Create(123456.3, 54321.4, circ);
            foreach (double jitter in new[] { -0.3, 0.0, 0.5, 20.0 })
            {
                WetMapFrame g = WetMapFrame.Create(123456.3 + jitter, 54321.4 + jitter, circ);
                bool sameCell = Math.Floor((123456.3 + jitter) / 64.0) == Math.Floor(123456.3 / 64.0) && Math.Floor((54321.4 + jitter) / 64.0) == Math.Floor(54321.4 / 64.0);
                Check(!sameCell || g.SameWindow(f), $"window stable at jitter {jitter}");
            }
            Check(WetMapFrame.Create(123456.3 + 64.0, 54321.4, circ).CornerAround - f.CornerAround == 64.0, "next texel moves the corner by 64 m");
            Check(f.CornerAround % 64.0 == 0.0 && f.CornerAcross % 64.0 == 0.0, "corner on the texel lattice");
            Vector2 offset = f.CameraOffsetMeters;
            Check(offset.X >= 128 * 64 && offset.X < 129 * 64 && offset.Y >= 128 * 64 && offset.Y < 129 * 64, $"camera inside the centre texel {offset}");
            // --- wrap at the circumference
            WetMapFrame seam = WetMapFrame.Create(circ - 10.0, 500.0, circ);
            Check(seam.CornerAround >= 0.0 && seam.CornerAround < circ, "corner wrapped into [0, circumference)");
            Vector2 seamOffset = seam.CameraOffsetMeters;
            Check(seamOffset.X >= 128 * 64 && seamOffset.X < 129 * 64, $"camera offset across the seam {seamOffset}");
            (double wrapAround, _) = seam.TexelCentre(255, 0);
            Check(wrapAround >= 0.0 && wrapAround < circ, "texel centre wrapped");
            WetMapFrame after = seam.WithCamera(5.0, 500.0);
            Check(Math.Abs(after.CameraOffsetMeters.X - (seamOffset.X + 15.0f)) < 0.01f, $"camera crossing the seam keeps the offset continuous {after.CameraOffsetMeters.X} vs {seamOffset.X + 15.0f}");
            // --- rebase invariance and arc-corrected conversion under a rotated grid
            Basis grid = new Basis(new Vector3(0.3f, 0.8f, 0.5f).Normalized(), 0.7f);
            var camera = new RingCanonicalPosition(2_500_000.7, 55.0, 41_000.2);
            WetMapFrame frame = WetMapFrame.Create(camera.AroundMeters, camera.AcrossMeters, circ);
            RingLodGeometrySnapshot g1 = Geometry(radius, grid, new Vector3(100, -20, 30), new RingCanonicalPosition(2_499_000, 40, 40_000));
            RingLodGeometrySnapshot g2 = Geometry(radius, grid, new Vector3(100, -20, 30), new RingCanonicalPosition(2_503_000, 90, 42_500));
            Projection p1 = frame.WorldFrame(g1), p2 = frame.WorldFrame(g2);
            Check((new Vector3(p1.X.X, p1.X.Y, p1.X.Z) - new Vector3(p2.X.X, p2.X.Y, p2.X.Z)).Length() < 1e-4f, "around axis unchanged by a rebase");
            Check(Math.Abs(p1.X.W - p2.X.W) < 1e-4f && Math.Abs(p1.Y.W - p2.Y.W) < 1e-4f && Math.Abs(p1.Z.W - p2.Z.W) < 1e-3f, "plane origin and ring radius unchanged by a rebase");
            Check((new Vector3(p1.W.X, p1.W.Y, p1.W.Z) - new Vector3(p2.W.X, p2.W.Y, p2.W.Z)).Length() > 1.0f, "the camera-ring centre moves with the rebase");
            double worst = 0.0, worstPlane = 0.0;
            foreach ((double da, double dc, double dv) in new[] { (300.0, 100.0, 2.0), (-1200.0, 40.0, 0.0), (5000.0, -3000.0, 15.0), (-8000.0, 8000.0, -4.0), (0.0, 0.0, 0.0) })
            {
                Vector3 w1 = g1.CanonicalToWorld(camera.AroundMeters + da, camera.VerticalMeters + dv, camera.AcrossMeters + dc);
                Vector3 w2 = g2.CanonicalToWorld(camera.AroundMeters + da, camera.VerticalMeters + dv, camera.AcrossMeters + dc);
                Vector2 expected = frame.CameraOffsetMeters + new Vector2((float)da, (float)dc);
                Vector2 m1 = frame.WorldToMap(w1, p1), m2 = frame.WorldToMap(w2, p2);
                worst = Math.Max(worst, Math.Max((m1 - expected).Length(), (m2 - expected).Length()));
                Vector2 plane1 = WetMapFrame.WorldToPlane(w1, p1), plane2 = WetMapFrame.WorldToPlane(w2, p2);
                worstPlane = Math.Max(worstPlane, (plane1 - plane2).Length());
                Vector2 expectedPlane = new((float)WetMapFrame.Mod(camera.AroundMeters, WetMapFrame.PlaneModulusMeters) + (float)da, (float)WetMapFrame.Mod(camera.AcrossMeters, WetMapFrame.PlaneModulusMeters) + (float)dc);
                Check((plane1 - expectedPlane).Length() < 0.25f, $"plane coordinates of ({da},{dc}) {plane1} vs {expectedPlane}");
            }
            Check(worst < 0.25, $"world to map is the canonical truth within 0.25 m under two rebases (worst {worst:0.000})");
            Check(worstPlane < 0.05, $"plane coordinates identical under a rebase (worst {worstPlane:0.000})");
            // --- plane tiles divide the plane period; coordinates stay finite and exact in double
            Check(Math.Abs(WetMapFrame.PlaneModulusMeters / 51.2 - Math.Round(WetMapFrame.PlaneModulusMeters / 51.2)) < 1e-6, "noise tile divides the plane period");
            Check(Math.Abs(WetMapFrame.PlaneModulusMeters / WetMapFrame.RippleTileMeters - Math.Round(WetMapFrame.PlaneModulusMeters / WetMapFrame.RippleTileMeters)) < 1e-9, "ripple tile divides the plane period");
            foreach (double tile in new[] { 8.0, 4.0, 2.0, 1.0, 0.25 }) Check(WetMapFrame.PlaneModulusMeters % tile == 0.0, $"tile {tile} divides P");
            WetMapFrame far = WetMapFrame.Create(31_000_000.123, 150_000.7, circ);
            Check(far.PlaneAroundMeters >= 0f && far.PlaneAroundMeters < 16384f && float.IsFinite(far.PlaneAcrossMeters), "plane coordinate range at a far position");
            Check(Math.Abs(far.PlaneAroundMeters - (float)WetMapFrame.Mod(31_000_000.123, 16384.0)) < 1e-3f, "plane coordinate computed in double");
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("WET_MAP_FRAME=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("WET_MAP_FRAME=PASS");
        return 0;
    }
}
