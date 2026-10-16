using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Spike B1 capture poses (identical for the Terrain3D runtime world and the smooth-voxel spike): main spire pad ground level, a regular land site,
/// an island beach, a terraced mountain cut and generic grassland about 50 m up. Deterministic for seed 1337 / the default v5 recipe.
/// </summary>
public partial class SmoothRuntimeSpikePoses : RefCounted
{
    const long Circumference = EmitterSiteTestWorlds.Circumference;
    const int Width = EmitterSiteTestWorlds.Width;

    sealed record Pose(string Name, double Around, double Vertical, double Across, double Yaw, double Pitch, bool Flight, string Note);

    public float Gen16Height(long around, int across)
    {
        using RingTerrainSampler g = EmitterSiteTestWorlds.CreateGen16(1337);
        return g.GetPresentationSurfaceHeight(around, across);
    }

    public string GetPosesJson()
    {
        using RingTerrainSampler s = EmitterSiteTestWorlds.CreateGen17Sites(1337);
        EmitterSiteRegistry registry = s.EmitterSites!;
        var nav = new EmitterSiteNavigator(new EmitterSitePlacementSource(registry), Circumference / Math.Tau);
        Func<double, double, float> ground = (a, x) => s.GetPresentationSurfaceHeight(a, x);
        var poses = new List<Pose>();

        EmitterViewpointPose main = nav.GetPose(registry.MainIndex, EmitterViewpoint.Entry, ground, 20, 150);
        poses.Add(new Pose("main_pad", main.Around, main.Vertical, main.Across, main.YawDeg, main.PitchDeg, false, "main spire entry approach, standing on the pad"));
        poses.Add(new Pose("main_pad_out", main.Around, main.Vertical, main.Across, main.YawDeg + 180.0, -6, false, "main pad entry point, turned away from the spire to show the pad skirt terrain"));
        int land = nav.FindNearest(5_000_000, 158_000, EmitterSiteClass.Land);
        EmitterViewpointPose lp = nav.GetPose(land, EmitterViewpoint.Entry, ground, 20, 150);
        poses.Add(new Pose("land_site", lp.Around, lp.Vertical, lp.Across, lp.YawDeg, lp.PitchDeg, false, $"regular land site {land} entry approach"));

        poses.Add(new Pose("land_site_out", lp.Around, lp.Vertical, lp.Across, lp.YawDeg + 180.0, -6, false, "land site entry point, turned away from the spire"));
        int island = nav.FindNearest(5_000_000, 158_000, EmitterSiteClass.Island);
        EmitterSite isl = registry[island];
        for (int dw = 2; dw < (int)isl.InfluenceRadius; dw += 2)
        {
            if (ground(isl.Around, isl.Across + dw) <= 0.5f)
            {
                // Stand 6 m inland of the first sea-level crossing, looking along the shore.
                double x = isl.Across + dw - 6, a = isl.Around, h = ground(a, x);
                nav.LookAt(a, h + 1.55, x, a, h + 1.55, x + 200, out double yaw, out _);
                poses.Add(new Pose("island_beach", a, h + 0.05, x, yaw, -4, false, $"island site {island}, 6 m inland of the shoreline, looking seaward-along-shore"));
                break;
            }
        }

        EmitterSite mountain = registry.Sites.ToArray().First(x => !x.IsMain && x.IsMountain);
        double best = -1; (long A, int X) at = (mountain.Around, mountain.Across);
        double radius = Math.Min(mountain.InfluenceRadius, 1500d);
        for (int du = -(int)radius; du <= radius; du += 8)
        for (int dw = -(int)radius; dw <= radius; dw += 8)
        {
            long a = mountain.Around + du; int x = mountain.Across + dw;
            if (x < 2 || x > Width - 4) continue;
            float h = s.GetPresentationSurfaceHeight(a, x);
            double slope = Math.Max(Math.Abs(s.GetPresentationSurfaceHeight(a + 2, x) - h), Math.Abs(s.GetPresentationSurfaceHeight(a, x + 2) - h));
            if (slope > best) { best = slope; at = (a, x); }
        }
        // Hover 70 m out on the pad side of the steepest cut face (towards the site), 25 m above its ground, aimed at the face.
        double ux = mountain.Around - at.A, uz = mountain.Across - at.X, ul = Math.Max(1.0, Math.Sqrt(ux * ux + uz * uz));
        double ca = at.A + ux / ul * 70.0, cx = at.X + uz / ul * 70.0;
        double cv = ground(ca, cx) + 25.0, fv = ground(at.A, at.X);
        nav.LookAt(ca, cv, cx, at.A, fv, at.X, out double myaw, out double mpitch);
        poses.Add(new Pose("mountain_cut", ca, cv, cx, myaw, mpitch, true, $"mountain site cut face ({best:F1} m per 2 m) at ({at.A},{at.X}), pad {mountain.PadHeight}"));

        double fa = at.A + ux / ul * 260.0, fx = at.X + uz / ul * 260.0, fvv = ground(fa, fx) + 90.0;
        nav.LookAt(fa, fvv, fx, at.A, ground(at.A, at.X), at.X, out double fyaw, out double fpitch);
        poses.Add(new Pose("mountain_cut_far", fa, fvv, fx, fyaw, fpitch, true, "same cut face from 260 m, 90 m up"));

        // Generic grassland: deterministic search for gentle land 20..80 m, camera 50 m above ground.
        var rng = new Random(2024 + "flat_land".Length);
        for (int i = 0; i < 400000; i++)
        {
            long a = ((long)(rng.NextDouble() * (Circumference / 2 - 100)) * 2L) + 20;
            int x = (int)(rng.NextDouble() * (Width / 2 - 200)) * 2 + 400;
            float h = s.GetPresentationSurfaceHeight(a, x);
            float slope = Math.Max(Math.Abs(s.GetPresentationSurfaceHeight(a + 4, x) - h), Math.Abs(s.GetPresentationSurfaceHeight(a, x + 4) - h));
            if (h > 20 && h < 80 && slope < 0.4f && Math.Abs(s.GetPresentationSurfaceHeight(a + 400, x) - h) < 40)
            {
                poses.Add(new Pose("grassland_50m", a, h + 50, x, 0, -12, true, "generic gentle land, camera 50 m above ground"));
                break;
            }
        }
        return JsonSerializer.Serialize(poses.Select(p => new { name = p.Name, around = p.Around, vertical = p.Vertical, across = p.Across, yaw = p.Yaw, pitch = p.Pitch, flight = p.Flight, note = p.Note, ground = ground(p.Around, p.Across) }));
    }
}
