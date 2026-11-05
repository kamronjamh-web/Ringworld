using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Containment field WO8 (docs/containment-field-design.md decision 9, 6.1, 8 WO8): the even C / 126 cue spacing, its single source, and
/// RimWallTopAnchors (126 per wall, across from the wall description, vertical = wall top, positions match the hull shader's line formula).
/// Pure C#; no scene. Run: Godot --headless --path . --script res://tools/rim_wall_top_anchors_test.gd. Prints RIM_WALL_TOP_ANCHORS=PASS.
/// </summary>
public partial class RimWallTopAnchorsTest : RefCounted
{
    private const double W = 318_000.0;
    private const double Top = 10_400.0;

    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok) _failures.Add(message); }
    private void Near(double a, double b, double tol, string message)
    {
        if (!(Math.Abs(a - b) <= tol))
            _failures.Add($"{message}: {a.ToString("R", CultureInfo.InvariantCulture)} vs {b.ToString("R", CultureInfo.InvariantCulture)} tol {tol}");
    }

    private static RingLodGeometrySnapshot Snap(double circumference, double radius)
        => new(Transform3D.Identity, Vector3.Zero, Vector3.Zero, radius, 0.0, 0.0, circumference, -22_304.0, Top, null);

    public int Run()
    {
        try
        {
            double circ = CertifiedRingScale.EffectiveCircumferenceMeters;
            RingLodGeometrySnapshot certified = Snap(circ, CertifiedRingScale.EffectiveRadiusMeters);
            Spacing(circ);
            CountAndEvenness(certified);
            ShaderLineFormula(certified);
            WallModes(certified);
            SourceAgreement(circ);
            OtherCircumference();
        }
        catch (Exception exception)
        {
            _failures.Add("exception: " + exception);
        }
        if (_failures.Count == 0)
        {
            GD.Print("RIM_WALL_TOP_ANCHORS=PASS");
            return 0;
        }
        foreach (string failure in _failures) GD.PrintErr("RIM_WALL_TOP_ANCHORS FAIL: " + failure);
        GD.Print("RIM_WALL_TOP_ANCHORS=FAIL");
        return 1;
    }

    private void Spacing(double circ)
    {
        Check(RingStructuralCues.LineCount == 126, "126 lines");
        double spacing = RingStructuralCues.LongitudinalSpacingMeters(circ);
        Near(spacing, circ / 126.0, 1e-9, "spacing is C / 126");
        Near(spacing, 249_332.8, 0.5, "Certified spacing is about 249,332.8 m");
        Near(RingStructuralCues.CueLineAround(126, circ), 0.0, 1e-9, "line 126 wraps to 0");
        Near(RingStructuralCues.CueLineAround(-1, circ), 125.0 * spacing, 1e-6, "line -1 wraps to 125");
    }

    private void CountAndEvenness(in RingLodGeometrySnapshot s)
    {
        double spacing = RingStructuralCues.LongitudinalSpacingMeters(s.CircumferenceMeters);
        IReadOnlyList<RimWallTopAnchor> anchors = RimWallTopAnchors.Build(s, W, RimWallDescription.Current);
        Check(anchors.Count == 252, "252 anchors in total, got " + anchors.Count);
        for (int side = 0; side < 2; side++)
        {
            List<RimWallTopAnchor> wall = anchors.Where(a => a.Side == side).ToList();
            Check(wall.Count == 126, $"side {side} has 126 anchors, got {wall.Count}");
            for (int i = 0; i < wall.Count; i++)
            {
                Check(wall[i].CueLine == i && wall[i].Index == side * 126 + i, $"side {side} anchor {i} index/cue line");
                Near(wall[i].AroundMeters, i * spacing, 1e-6, $"side {side} anchor {i} around");
                double next = i + 1 < wall.Count ? wall[i + 1].AroundMeters : wall[0].AroundMeters + s.CircumferenceMeters;
                Near(next - wall[i].AroundMeters, spacing, 1e-6, $"side {side} gap after anchor {i} (including the wrap) equals the spacing");
                Check(wall[i].AroundMeters >= 0.0 && wall[i].AroundMeters < s.CircumferenceMeters, $"side {side} anchor {i} in [0, C)");
            }
            Near(wall[0].AroundMeters, 0.0, 0.0, $"side {side} anchor 0 is at around 0");
        }
        Check(anchors.Take(126).All(a => a.Side == 0) && anchors.Skip(126).All(a => a.Side == 1), "ordered by side then around");
    }

    // The hull shader (sample_structural_scale_cues): around = atan(relative.z, relative.x) * ring_radius; a line where
    // |mod(around + s/2, s) - s/2| = 0. With the identity grid, canonical around 0 must be shader angle 0.
    private void ShaderLineFormula(in RingLodGeometrySnapshot s)
    {
        double spacing = RingStructuralCues.LongitudinalSpacingMeters(s.CircumferenceMeters);
        double worst = 0.0;
        foreach (RimWallTopAnchor a in RimWallTopAnchors.Build(s, W, RimWallDescription.Current))
        {
            Vector3 world = s.CanonicalToWorld(a.AroundMeters, a.VerticalMeters, a.AcrossMeters);
            double shaderAround = Math.Atan2(world.Z, world.X) * s.RingRadiusMeters;
            double cell = Math.Abs(PosMod(shaderAround + spacing * 0.5, spacing) - spacing * 0.5);
            worst = Math.Max(worst, cell);
            Check(cell <= 1.0, $"anchor {a.Index} is {cell:F3} m off the shader line");
            Near(world.Y, a.AcrossMeters, 0.01, $"anchor {a.Index} world y is its across");
        }
        Vector3 zero = s.CanonicalToWorld(0.0, Top, RimWallDescription.Current.CentreLine(0, W));
        Near(Math.Atan2(zero.Z, zero.X), 0.0, 1e-9, "anchor k = 0 sits at angle 0");
        GD.Print($"RIM_WALL_TOP_ANCHORS worst shader-line offset {worst:F4} m");
    }

    private static double PosMod(double x, double m) { double r = x % m; return r < 0.0 ? r + m : r; }

    private void WallModes(RingLodGeometrySnapshot s)
    {
        foreach ((string name, RimWallDescription wall, double a0, double a1) in new[]
        {
            ("depth 0", new RimWallDescription(0, RimFieldRoot.InnerEdge), 0.0, W),
            ("depth 100", new RimWallDescription(100, RimFieldRoot.CentreLine), -50.0, W + 50.0),
            ("live", RimWallDescription.Current, -64.0, W + 64.0),
        })
        {
            IReadOnlyList<RimWallTopAnchor> anchors = RimWallTopAnchors.Build(s, W, wall);
            Check(anchors.Count == 252, name + " count");
            Check(anchors.Where(a => a.Side == 0).All(a => a.AcrossMeters == a0), name + " side 0 across " + a0);
            Check(anchors.Where(a => a.Side == 1).All(a => a.AcrossMeters == a1), name + " side 1 across " + a1);
            Check(anchors.All(a => a.VerticalMeters == s.StructuralWallTopGlobalY), name + " vertical is the wall top");
        }
        Check(RimWallDescription.Current.DepthMeters == 128.0 && RimWallDescription.Current.Root == RimFieldRoot.CentreLine, "live wall is 128 m, centre line");
    }

    // The shader default, both owners and the anchor builder read one value.
    private void SourceAgreement(double circ)
    {
        string shader = FileAccess.GetFileAsString("res://materials/ring_hull_global.gdshader");
        Match m = Regex.Match(shader, @"uniform\s+float\s+structural_longitudinal_spacing_meters\s*=\s*([0-9.]+)\s*;");
        Check(m.Success, "shader default found");
        if (m.Success)
        {
            double def = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            Near(def, RingStructuralCues.ShaderDefaultSpacingMeters, 0.01, "shader default equals RingStructuralCues.ShaderDefaultSpacingMeters");
            Near(def, RingStructuralCues.LongitudinalSpacingMeters(circ), 0.1, "shader default equals Certified C / 126");
        }
        Near(RingStructuralCues.ShaderDefaultSpacingMeters, RingStructuralCues.LongitudinalSpacingMeters(circ), 0.1, "C# shader default constant equals Certified C / 126");
        foreach (string path in new[] { "res://scripts/Voxel/LOD/RingTerrainGlobalLod.cs", "res://scripts/Voxel/LOD/RingScaledFarViewport.cs" })
        {
            string source = FileAccess.GetFileAsString(path);
            Check(source.Contains("RingStructuralCues.LongitudinalSpacingMeters("), path + " reads RingStructuralCues");
            Check(!source.Contains("StructuralLongitudinalSpacingMeters"), path + " has no spacing property of its own");
        }
    }

    private void OtherCircumference()
    {
        const double c = 12_600_000.0;
        RingLodGeometrySnapshot s = Snap(c, c / Math.Tau);
        IReadOnlyList<RimWallTopAnchor> anchors = RimWallTopAnchors.Build(s, 100_000.0, RimWallDescription.Current);
        Near(anchors[1].AroundMeters, 100_000.0, 1e-6, "spacing follows the live circumference (12,600 km / 126 = 100 km)");
        Check(anchors.Count == 252, "count is independent of the circumference");
    }
}
