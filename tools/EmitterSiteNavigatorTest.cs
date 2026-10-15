using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// M1-9/10 navigator: nearest-site lookups against the registry and a brute force, class filters, entry/gallery/far/high poses,
/// the Far land check with synthetic ground, the real gen-17 main Far pose and the fresh-world spawn helper.
/// </summary>
public partial class EmitterSiteNavigatorTest : RefCounted
{
    const double Circumference = EmitterSiteTestWorlds.Circumference;
    const double Width = EmitterSiteTestWorlds.Width;
    static readonly double AngleRadius = Circumference / Math.Tau;
    static readonly double Radius = AngleRadius - 50d;

    public int Run(bool unused)
    {
        var failures = new List<string>();
        try
        {
            using RingTerrainSampler v5 = EmitterSiteTestWorlds.CreateGen17Sites(1337);
            using RingTerrainSampler v4 = EmitterSiteTestWorlds.CreateGen17(1337);
            EmitterSiteRegistry registry = v5.EmitterSites ?? throw new InvalidOperationException("v5 sampler has no registry");
            var nav = new EmitterSiteNavigator(new EmitterSitePlacementSource(registry), Radius, 0d);
            CheckLookups(nav, registry, failures);
            CheckBounds(nav, registry, failures);
            CheckEntryAndMarkers(nav, failures);
            CheckPosesAligned(nav, failures);
            CheckLandCheck(nav, failures);
            CheckRealMain(nav, v5, failures);
            CheckFreshSpawn(nav, v5, v4, failures);
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("EMITTER_SITE_NAVIGATOR=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("EMITTER_SITE_NAVIGATOR=PASS");
        return 0;
    }

    static void CheckLookups(EmitterSiteNavigator nav, EmitterSiteRegistry registry, List<string> failures)
    {
        var random = new Random(4242);
        int mismatches = 0, seam = 0;
        for (int i = 0; i < 2000; i++)
        {
            // Every fifth point sits in the seam band around the ring origin.
            bool inSeam = i % 5 == 0;
            long around = inSeam ? (long)(Circumference - 3000d + random.NextDouble() * 6000d) % (long)Circumference : (long)(random.NextDouble() * Circumference);
            if (inSeam) seam++;
            double across = random.NextDouble() * Width;
            int expected = registry.TryGetNearestSite(around, across, out int e, out _, out _) ? e : -1;
            int found = nav.FindNearest(around, across, EmitterSiteClass.Any, true);
            if (found != expected && Math.Abs(Dist(registry, found, around, across) - Dist(registry, expected, around, across)) > 1e-6)
            {
                mismatches++;
                if (mismatches < 4) failures.Add($"nearest mismatch at ({around},{across:0}): {found} vs {expected}");
            }
            foreach (EmitterSiteClass cls in new[] { EmitterSiteClass.Land, EmitterSiteClass.Island, EmitterSiteClass.Mountain })
            {
                int result = nav.FindNearest(around, across, cls);
                int brute = BruteForce(registry, around, across, cls);
                if (result != brute) failures.Add($"{cls} nearest {result} != brute force {brute} at ({around},{across:0})");
                if (result >= 0)
                {
                    EmitterSite site = registry[result];
                    bool ok = cls switch { EmitterSiteClass.Land => !site.IsIsland, EmitterSiteClass.Island => site.IsIsland, _ => site.IsMountain };
                    if (!ok || site.IsMain || result == registry.MainIndex) failures.Add($"{cls} result {result} has the wrong class or is main");
                }
            }
            // Without includeMain the main site is never returned.
            int noMain = nav.FindNearest(around, across, EmitterSiteClass.Any, false);
            if (noMain == registry.MainIndex || noMain < 0) failures.Add("Any without main returned main or nothing");
            if (failures.Count > 12) return;
        }
        // Standing on the main site returns it only when main is included.
        EmitterSite main = registry.MainSite;
        if (nav.FindNearest(main.Around, main.Across, EmitterSiteClass.Any, true) != registry.MainIndex) failures.Add("main not found at its own position");
        if (nav.FindNearest(main.Around, main.Across, EmitterSiteClass.Any, false) == registry.MainIndex) failures.Add("main returned although excluded");
        int islands = 0, mountains = 0;
        for (int i = 0; i < registry.Count; i++) { if (registry[i].IsIsland) islands++; if (registry[i].IsMountain) mountains++; }
        Console.WriteLine($"EMITTER_SITE_NAVIGATOR lookups points=2000 seam={seam} nearest_mismatch={mismatches} islands={islands} mountains={mountains}");
    }

    static double Dist(EmitterSiteRegistry registry, int index, double around, double across)
    {
        if (index < 0) return double.MaxValue;
        EmitterSite site = registry[index];
        double du = Wrap(around - site.Around), dw = across - site.Across;
        return Math.Sqrt(du * du + dw * dw);
    }

    static double Wrap(double delta)
    {
        delta %= Circumference;
        if (delta > Circumference / 2) delta -= Circumference; else if (delta < -Circumference / 2) delta += Circumference;
        return delta;
    }

    static int BruteForce(EmitterSiteRegistry registry, double around, double across, EmitterSiteClass cls)
    {
        int best = -1; double bestD = double.MaxValue;
        for (int i = 0; i < registry.Count - 1; i++)
        {
            EmitterSiteFlags flags = registry[i].Flags;
            bool match = cls switch
            {
                EmitterSiteClass.Island => (flags & EmitterSiteFlags.Island) != 0,
                EmitterSiteClass.Mountain => (flags & EmitterSiteFlags.Mountain) != 0,
                _ => (flags & EmitterSiteFlags.Island) == 0
            };
            if (!match) continue;
            double du = Wrap(around - registry[i].Around), dw = across - registry[i].Across, d = du * du + dw * dw;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    static void CheckBounds(EmitterSiteNavigator nav, EmitterSiteRegistry registry, List<string> failures)
    {
        if (nav.TryGetSite(-1, out _) || nav.TryGetSite(nav.Count, out _) || nav.TryGetSite(int.MaxValue, out _)) failures.Add("out-of-range site accepted");
        if (!nav.TryGetSite(0, out _) || !nav.TryGetSite(nav.Count - 1, out _)) failures.Add("valid site rejected");
        if (nav.MainIndex != registry.MainIndex || nav.MainIndex != nav.Count - 1) failures.Add("MainIndex mismatch");
        (int row, int col) = nav.GetRowColumn(0);
        if (row != 0 || col != 0) failures.Add($"site 0 row/col {row}/{col}");
        (int mainRow, int mainCol) = nav.GetRowColumn(nav.MainIndex);
        if (mainRow != registry.MainRow || mainCol != -1) failures.Add("main row/col");
        for (int i = 0; i < nav.Count - 1; i++)
        {
            (int r, int c) = nav.GetRowColumn(i);
            if (r == registry.MainRow || registry.RegularIndex(r, c) != i) { failures.Add($"row/col round trip failed at {i}"); break; }
        }
    }

    static void CheckEntryAndMarkers(EmitterSiteNavigator nav, List<string> failures)
    {
        foreach ((string path, double x, double z) in new[] { ("res://assets/poi/beam_emitter/BeamEmitterPOI.tscn", EmitterSiteNavigator.RegularEntryX, EmitterSiteNavigator.RegularEntryZ),
                     ("res://assets/poi/main_spire/MainSpirePOI.tscn", EmitterSiteNavigator.MainEntryX, EmitterSiteNavigator.MainEntryZ) })
        {
            if (!EmitterPoiMarkers.TryParse(File.ReadAllText(ProjectSettings.GlobalizePath(path)), out EmitterPoiMarkers m)) { failures.Add("marker parse " + path); continue; }
            if (Math.Abs(m.EntryApproach.X - x) > 1e-3 || Math.Abs(m.EntryApproach.Z - z) > 1e-3 || m.EntryApproach.Y != 0f) failures.Add($"entry constants {x},{z} != marker {m.EntryApproach}");
        }
        double worst = 0d;
        for (int i = 0; i < nav.Count; i++)
        {
            EmitterSitePlacement p = nav.Placements.GetPlacement(i);
            bool main = i == nav.MainIndex;
            double r = main ? 930d : 232d;
            EmitterViewpointPose pose = nav.GetPose(i, EmitterViewpoint.Entry, null, 20d, 150d);
            double beta = p.BearingDegrees * Math.PI / 180d;
            double da = pose.Around - (p.AroundMeters + r * Math.Cos(beta)), dx = pose.Across - (p.AcrossMeters + r * Math.Sin(beta));
            worst = Math.Max(worst, Math.Max(Math.Abs(da), Math.Abs(dx)));
            if (Math.Abs(da) > 1e-3 || Math.Abs(dx) > 1e-3) { failures.Add($"entry offset site {i}: {da},{dx}"); break; }
            if (r <= EmitterSiteApertures.LinerRadius(p.Kind) + 10d) failures.Add($"entry radius {r} inside liner+10 at {i}");
            if (Math.Abs(pose.Vertical - (p.PadHeightMeters + 0.05)) > 1e-9 || pose.Flight) failures.Add($"entry vertical/flight at {i}");
            EmitterViewpointPose high = nav.GetPose(i, EmitterViewpoint.High, null, 20d, 150d);
            if (Math.Abs(high.Vertical - (p.PadHeightMeters + 150000d)) > 1e-6 || !high.Flight) failures.Add($"high vertical at {i}: {high.Vertical}");
        }
        Console.WriteLine($"EMITTER_SITE_NAVIGATOR entry worst_offset_m={worst:E2}");
    }

    // Independent 3D check: forward from yaw/pitch (explorer convention) against the direction to the aim point on the ring cylinder.
    static double Alignment(in EmitterViewpointPose pose, bool horizontalOnly, double eye)
    {
        static (double x, double y, double z) Pos(double a, double v, double x) => (Math.Cos(a / AngleRadius) * (Radius - v), x, Math.Sin(a / AngleRadius) * (Radius - v));
        double phi = pose.Around / AngleRadius;
        var from = Pos(pose.Around, pose.Vertical + eye, pose.Across);
        var to = Pos(pose.AimAround, pose.AimVertical, pose.AimAcross);
        double dx = to.x - from.x, dy = to.y - from.y, dz = to.z - from.z;
        double yaw = pose.YawDeg * Math.PI / 180d, pitch = pose.PitchDeg * Math.PI / 180d;
        // tangent (-sin, 0, cos), across (0,1,0), up (-cos, 0, -sin)
        double tx = -Math.Sin(phi), tz = Math.Cos(phi), ux = -Math.Cos(phi), uz = -Math.Sin(phi);
        double fT = Math.Cos(pitch) * Math.Cos(yaw), fA = Math.Cos(pitch) * Math.Sin(yaw), fU = Math.Sin(pitch);
        double fx = fT * tx + fU * ux, fy = fA, fz = fT * tz + fU * uz;
        if (horizontalOnly)
        {
            double dUp = dx * ux + dz * uz, fUp = fx * ux + fz * uz;
            dx -= dUp * ux; dz -= dUp * uz; fx -= fUp * ux; fz -= fUp * uz;
        }
        double dl = Math.Sqrt(dx * dx + dy * dy + dz * dz), fl = Math.Sqrt(fx * fx + fy * fy + fz * fz);
        return (dx * fx + dy * fy + dz * fz) / (dl * fl);
    }

    static void CheckPosesAligned(EmitterSiteNavigator nav, List<string> failures)
    {
        Func<double, double, float> flat = (_, _) => 50f;
        double worst = 1d;
        for (int i = 0; i < nav.Count; i++)
            foreach (EmitterViewpoint vp in Enum.GetValues<EmitterViewpoint>())
            {
                EmitterViewpointPose pose = nav.GetPose(i, vp, flat, 20d, 150d);
                bool entry = vp == EmitterViewpoint.Entry;
                double eye = pose.Flight ? (vp == EmitterViewpoint.Gallery ? EmitterSiteNavigator.EyeHeightMeters : 0d) : EmitterSiteNavigator.EyeHeightMeters;
                double dot = Alignment(pose, entry, eye);
                worst = Math.Min(worst, dot);
                if (!(dot > 0.99)) { failures.Add($"alignment site {i} {vp}: {dot:F4}"); return; }
                if (entry && Math.Abs(pose.PitchDeg - EmitterSiteNavigator.EntryPitchDegrees) > 1e-9) failures.Add("entry pitch");
                if (vp == EmitterViewpoint.Gallery && !pose.Flight) failures.Add("gallery must be a flight pose");
            }
        Console.WriteLine($"EMITTER_SITE_NAVIGATOR alignment worst_dot={worst:F5}");
    }

    static void CheckLandCheck(EmitterSiteNavigator nav, List<string> failures)
    {
        int site = 100;
        EmitterSitePlacement p = nav.Placements.GetPlacement(site);
        double side = Math.Cos(p.BearingDegrees * Math.PI / 180d) < 0d ? -1d : 1d;
        double ta = p.AroundMeters + side * 20000d, tx = p.AcrossMeters;

        EmitterViewpointPose dry = nav.GetPose(site, EmitterViewpoint.Far, (_, _) => 50f, 20d, 150d);
        if (dry.Flight || Math.Abs(dry.Around - ta) > 1e-9 || Math.Abs(dry.Across - tx) > 1e-9) failures.Add($"all-dry: expected grounded at the target, got {dry}");

        // Water within 700 m of the target, land beyond: first dry ring is 750 m, azimuth 0.
        EmitterViewpointPose shore = nav.GetPose(site, EmitterViewpoint.Far, (a, x) => Math.Sqrt((a - ta) * (a - ta) + (x - tx) * (x - tx)) < 700d ? -10f : 50f, 20d, 150d);
        double shoreDistance = Math.Sqrt((shore.Around - ta) * (shore.Around - ta) + (shore.Across - tx) * (shore.Across - tx));
        if (shore.Flight || Math.Abs(shoreDistance - 750d) > 1e-6 || Math.Abs(shore.Around - (ta + 750d)) > 1e-6) failures.Add($"shore: expected grounded 750 m east of the target, got {shore} ({shoreDistance:0.0} m)");

        EmitterViewpointPose sea = nav.GetPose(site, EmitterViewpoint.Far, (_, _) => -10f, 20d, 150d);
        if (!sea.Flight || Math.Abs(sea.Vertical - 200d) > 1e-9 || Math.Abs(sea.Around - ta) > 1e-9) failures.Add($"all-water: expected hover at sea+200, got {sea}");

        // Steep terrain at the target is rejected: 45 degree ramp within 100 m, flat beyond.
        EmitterViewpointPose steep = nav.GetPose(site, EmitterViewpoint.Far, (a, x) => Math.Abs(a - ta) < 100d && Math.Abs(x - tx) < 100d ? (float)(50d + (a - ta)) : 50f, 20d, 150d);
        double steepDistance = Math.Sqrt((steep.Around - ta) * (steep.Around - ta) + (steep.Across - tx) * (steep.Across - tx));
        if (steep.Flight || Math.Abs(steepDistance - 250d) > 1e-6) failures.Add($"steep: expected the 250 m ring, got {steep} ({steepDistance:0.0} m)");

        // Non-finite ground never counts as dry.
        EmitterViewpointPose nan = nav.GetPose(site, EmitterViewpoint.Far, (_, _) => float.NaN, 20d, 150d);
        if (!nan.Flight) failures.Add("NaN ground produced a grounded pose");

        // The main site uses twice the distance.
        EmitterSitePlacement mp = nav.Placements.GetPlacement(nav.MainIndex);
        EmitterViewpointPose mainFar = nav.GetPose(nav.MainIndex, EmitterViewpoint.Far, (_, _) => 50f, 20d, 150d);
        if (Math.Abs(Math.Abs(mainFar.Around - mp.AroundMeters) - 40000d) > 1e-6) failures.Add($"main far distance {mainFar.Around - mp.AroundMeters}");

        // Slope and sea-level boundaries.
        double lowSlope = Math.Tan(19d * Math.PI / 180d), highSlope = Math.Tan(21d * Math.PI / 180d);
        if (!EmitterSiteNavigator.IsDryFlat((a, _) => (float)(50d + a * lowSlope), 0d, 1000d, 5000d, Width, out _)) failures.Add("19 degree ramp rejected");
        if (EmitterSiteNavigator.IsDryFlat((a, _) => (float)(50d + a * highSlope), 0d, 1000d, 5000d, Width, out _)) failures.Add("21 degree ramp accepted");
        if (EmitterSiteNavigator.IsDryFlat((_, _) => 1.5f, 0d, 1000d, 5000d, Width, out _)) failures.Add("1.5 m above sea accepted as dry");
        if (!EmitterSiteNavigator.IsDryFlat((_, _) => 2.5f, 0d, 1000d, 5000d, Width, out _)) failures.Add("2.5 m above sea rejected");
        Console.WriteLine($"EMITTER_SITE_NAVIGATOR land_check dry={dry.Detail} | shore={shore.Detail} | sea={sea.Detail} | steep={steep.Detail}");
    }

    static void CheckRealMain(EmitterSiteNavigator nav, RingTerrainSampler sampler, List<string> failures)
    {
        Func<double, double, float> ground = (a, x) => sampler.GetPresentationSurfaceHeight(a, x);
        EmitterViewpointPose first = nav.GetPose(nav.MainIndex, EmitterViewpoint.Far, ground, 20d, 150d);
        EmitterViewpointPose second = nav.GetPose(nav.MainIndex, EmitterViewpoint.Far, ground, 20d, 150d);
        if (!first.Equals(second)) failures.Add("main Far pose is not deterministic");
        if (!first.Flight)
        {
            if (!EmitterSiteNavigator.IsDryFlat(ground, sampler.SeaLevelY, first.Around, first.Across, Width, out double h) || Math.Abs(h + 0.05 - first.Vertical) > 1e-3) failures.Add($"main Far grounded pose is not on dry flat land: {first}");
        }
        else if (first.Vertical < sampler.SeaLevelY + 200d - 1e-6) failures.Add($"main Far hover below sea+200: {first}");
        double dot = Alignment(first, false, first.Flight ? 0d : EmitterSiteNavigator.EyeHeightMeters);
        if (!(dot > 0.99)) failures.Add($"main Far alignment {dot}");
        Console.WriteLine($"EMITTER_SITE_NAVIGATOR real_main {first.Detail} around={first.Around:0} vertical={first.Vertical:0.0} across={first.Across:0} yaw={first.YawDeg:0.0} pitch={first.PitchDeg:0.0} flight={first.Flight}");
    }

    static void CheckFreshSpawn(EmitterSiteNavigator nav, RingTerrainSampler v5, RingTerrainSampler v4, List<string> failures)
    {
        if (!EmitterSiteNavigator.TryComputeFreshSpawn(v5, Radius, EmitterSiteNavigator.DefaultFarKm, out EmitterViewpointPose pose)) { failures.Add("fresh spawn unavailable on v5"); return; }
        EmitterViewpointPose expected = nav.GetPose(nav.MainIndex, EmitterViewpoint.Far, (a, x) => v5.GetPresentationSurfaceHeight(a, x), 20d, 150d);
        if (pose.Site != nav.MainIndex || pose.Viewpoint != EmitterViewpoint.Far || !pose.Equals(expected)) failures.Add($"fresh spawn is not the main Far pose: {pose}");
        if (EmitterSiteNavigator.TryComputeFreshSpawn(v4, Radius, 20d, out _)) failures.Add("fresh spawn offered on v4 (no registry)");
        if (EmitterSiteNavigator.TryComputeFreshSpawn(null, Radius, 20d, out _)) failures.Add("fresh spawn offered without a sampler");
    }
}
