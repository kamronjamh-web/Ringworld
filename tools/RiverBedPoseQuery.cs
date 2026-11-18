using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T4-7 stage 2 (docs/work-orders/terrain-vegetation/T4.md, D11): headless pose query for the review stills. Checks that the ON recipe (water.rivers, ground.surface, ground.river_beds) and the OFF recipe differ
/// only in the review list and share one hydrology identity, bakes the full seed-1337 river graph on the CPU, and prints three poses: (a) a calm river bank (reach coarseness g under 0.2), (b) a gorge bed
/// (g over 0.8, steep valley sides), (c) a dry basin floor with PlayaCover over 0.5 across a large area, plus a grazing view along (a)'s river for the 4 to 8 m hand-off. Output: the --at string for the landscape harness.
/// Run windowed (GPU graph source, as the game bakes it; a CPU-baked graph can differ from the GPU one and put rivers elsewhere): tools/run_godot.ps1 -Class shared -Script res://tools/river_bed_pose_query.gd -Marker RIVER_BED_POSE_QUERY -TimeoutMin 20
/// </summary>
public partial class RiverBedPoseQuery : RefCounted
{
    private const int Seed = 1337;
    private static void Say(string text) => Console.WriteLine("RIVER_BED_POSE_QUERY " + text);

    public int Run()
    {
        string work = ProjectSettings.GlobalizePath($"res://.godot/river_bed_pose_query_{System.Environment.ProcessId}");
        try
        {
            TerrainGenerationParameters on = EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1, new[] { "water.rivers", "ground.surface", "ground.river_beds" });
            TerrainGenerationParameters off = EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1, new[] { "water.rivers", "ground.surface" });
            string jOn = on.CanonicalJson, jOff = off.CanonicalJson;
            ulong idOn = HydrologyBakeIdentity.Compute(on), idOff = HydrologyBakeIdentity.Compute(off);
            Say($"recipes: canonical json equal {jOn == jOff}; hydrology identity ON {idOn:X16} OFF {idOff:X16} equal {idOn == idOff}; definition hash ON {on.DefinitionHash:X16} OFF {off.DefinitionHash:X16}");
            Say("recipe diff: " + DescribeDiff(jOff, jOn));
            using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, on);
            Directory.CreateDirectory(work);
            HydrologyBakeInputs inputs = HydrologyBakeInputs.Capture(settings, on, HydrologyBakeInputs.DefaultWorkers, RenderingServer.GetRenderingDevice() != null ? HydrologySourcePreference.Gpu : HydrologySourcePreference.Cpu, work);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            RiverGraph graph = RiverGraphStore.GetOrBuild(inputs);
            Say($"full graph baked ({(graph0Source())}) in {clock.Elapsed.TotalSeconds:F1} s: {graph.Network.Stems.Length} stems, {graph.Lakes.Length} lakes, {graph.Basins.Length} basins, input identity {graph.InputIdentity:X16}");
            using var sampler = new RingTerrainSampler(settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
            using var model = new TerrainGenerationHeightModel(on);
            if (!sampler.HasRiverBeds) { Console.Error.WriteLine("RIVER_BED_POSE_QUERY=FAIL the ON sampler has no river beds"); return 1; }
            var poses = new List<string>();
            RiverNetwork net = graph.Network;
            // (a) calm and (b) gorge: stem midpoints and quarter points, hash order, reach from the stored channel, valley sides measured 40 m either side of the bank.
            (string, double, double, double, double, double)? calm = null, gorge = null;
            foreach (int stem in Enumerable.Range(0, net.Curves.Length).OrderBy(i => RiverHash.Mix(Seed, i, 7)))
            {
                double length = net.Curves[stem].Length;
                if (length < 3000d) continue;
                foreach (double f in new[] { 0.5, 0.3, 0.7 })
                {
                    double s = length * f;
                    net.ChannelAt(stem, s, out RiverChannel ch);
                    double width = 2d * ch.HalfWidth, speed = net.Sections[ch.Reach].Speed, g = RiverBedSurfaceModel.Coarseness(speed);
                    net.CentreFrame(stem, s, out double a, out double z, out double nx, out double ny);
                    net.Centre(stem, s, out _, out _, out double tx, out double ty);
                    if (z < 20_000d || z > net.WidthM - 20_000d) continue;
                    double h0 = model.CalculateHeight(a, z);
                    if (h0 < 8d || width < 6d || width > 60d) continue;
                    // The bank: walk along the left normal until the sample is not River (as the harness's river_bank_eye does) and stand 10 m beyond it.
                    double bank = -1d;
                    for (double d = 0; d <= 200d; d += 1d)
                        if (sampler.GetTerrainSample((long)Math.Floor(net.WrapAround(a + nx * d)), (int)Math.Floor(z + ny * d)).WaterKind != SurfaceWaterKind.River) { bank = d; break; }
                    if (bank < 0) continue;
                    double sa = net.WrapAround(a + nx * (bank + 10d)), sz = z + ny * (bank + 10d);
                    // No lake owner within 200 m (16 directions, every 25 m), so the pose is a river and not a lake membership.
                    bool clean = true;
                    for (int k = 0; k < 16 && clean; k++)
                        for (double r = 25d; r <= 200d && clean; r += 25d)
                            if (sampler.GetTerrainSample((long)Math.Floor(net.WrapAround(sa + r * Math.Cos(k * Math.PI / 8d))), (int)Math.Floor(sz + r * Math.Sin(k * Math.PI / 8d))).WaterKind == SurfaceWaterKind.Lake) clean = false;
                    if (!clean) continue;
                    double hl = model.CalculateHeight(net.WrapAround(a + nx * (bank + 40d)), z + ny * (bank + 40d)), hr = model.CalculateHeight(net.WrapAround(a - nx * (bank + 40d)), z - ny * (bank + 40d));
                    double rise = Math.Min(hl, hr) - h0;
                    // Stand 7 m back from the water's edge (bank is the first non-River distance from the centreline, measured along the left normal).
                    sa = net.WrapAround(a + nx * (bank + 7d)); sz = z + ny * (bank + 7d);
                    // Look across the river: yaw = atan2(d across, d around) of (-n), the harness convention (checked against the dressing fixtures steep, scree and cliff).
                    double yaw = Norm(Math.Atan2(-ny, -nx) * 180d / Math.PI);
                    // Upstream-leaning view for the gorge: across rotated 35 degrees toward -t (flow assumed to run with increasing arc length).
                    double upYaw = Norm(Math.Atan2(-ny * Math.Cos(0.61) - ty * Math.Sin(0.61), -nx * Math.Cos(0.61) - tx * Math.Sin(0.61)) * 180d / Math.PI);
                    // Along view: the direction (+t or -t) in which the centreline stays within 60 m of the straight ray for the longest run (up to 1.6 km).
                    double bestRun = 0d, alongYaw = 0d;
                    foreach (double sign in new[] { 1d, -1d })
                    {
                        double run = 0d;
                        for (double u = 50d; u <= 1600d; u += 50d)
                        {
                            double ss = s + sign * u;
                            if (ss < 0d || ss > length) break;
                            net.Centre(stem, ss, out double ca, out double cz, out _, out _);
                            double da = ca - a, dc = cz - z; if (da > net.CircumferenceM / 2) da -= net.CircumferenceM; if (da < -net.CircumferenceM / 2) da += net.CircumferenceM;
                            double along = da * tx * sign + dc * ty * sign, lateral = -da * ty * sign + dc * tx * sign;
                            if (Math.Abs(lateral) > 60d || along < u * 0.8) break;
                            run = u;
                        }
                        if (run > bestRun) { bestRun = run; alongYaw = Norm(Math.Atan2(ty * sign, tx * sign) * 180d / Math.PI); }
                    }
                    Say($"candidate stem {stem} s {s:F0}: width {width:F1} m speed {speed:F2} m/s g {g:F2} ground {h0:F0} m valley rise {rise:F1} m, bank {bank:F0} m, straight run {bestRun:F0} m");
                    if (calm == null && g < 0.2 && width >= 8d && rise < 15d && bestRun >= 1500d) calm = ($"stem{stem}", sa, sz, yaw, alongYaw, g);
                    if (gorge == null && g > 0.8 && rise >= 8d) gorge = ($"stem{stem}", sa, sz, upYaw, alongYaw, g);
                    if (calm != null && gorge != null) break;
                }
            }
            if (calm is { } c)
            {
                Say($"pose a calm bank: {c.Item1} g {c.Item6:F2}");
                poses.Add($"calm_eye:{c.Item2:F0},{c.Item3:F0},3,{c.Item4:F0},-10");
                poses.Add($"calm_300m:{c.Item2:F0},{c.Item3:F0},300,{c.Item4:F0},-80");
                poses.Add($"calm_along:{c.Item2:F0},{c.Item3:F0},8,{c.Item5:F0},-2");
            }
            else Say("NO calm bank found");
            if (gorge is { } gg)
            {
                Say($"pose b gorge: {gg.Item1} g {gg.Item6:F2}");
                poses.Add($"gorge_eye:{gg.Item2:F0},{gg.Item3:F0},2.5,{gg.Item4:F0},-6");
                poses.Add($"gorge_300m:{gg.Item2:F0},{gg.Item3:F0},300,{gg.Item4:F0},-80");
            }
            else Say("NO gorge found");
            // (c) basin floor: dry-run cells, score by the number of 5 x 5 samples with PlayaCover over 127 (0.5), take the best.
            double stepA = graph.AroundStepMeters, stepZ = graph.AcrossStepMeters;
            double bestScore = 0; (double A, double Z)? best = null; int scanned = 0;
            foreach (LakeRun run in graph.DryRuns.Where(r => r.Row >= 6 && r.Row <= graph.Rows - 6).OrderBy(r => RiverHash.Mix(Seed, r.Row, r.Col)).Take(1500))
            {
                scanned++;
                int hits = 0; double sa = 0, sz = 0;
                for (int j = 0; j < 5; j++)
                    for (int i = 0; i < 5; i++)
                    {
                        long a = (long)Math.Round((run.Col + 0.1 + 0.2 * i) * stepA / 2d) * 2L; int zz = (int)Math.Round((run.Row + 0.1 + 0.2 * j) * stepZ / 2d) * 2;
                        TerrainSample s = sampler.GetTerrainSample(a, zz);
                        if (s.RiverBeds.PlayaCover > 127 && !s.HasSurfaceWater) { hits++; sa += a; sz += zz; }
                    }
                if (hits > bestScore) { bestScore = hits; best = (sa / hits, sz / hits); }
            }
            Say($"pose c basin floor: scanned {scanned} dry cells, best has {bestScore} of 25 samples with PlayaCover over 0.5");
            if (best is { } bpos)
            {
                double ground = model.CalculateHeight(bpos.A, bpos.Z);
                poses.Add($"playa_eye:{bpos.A:F0},{bpos.Z:F0},1.7,90,-4");
                poses.Add($"playa_eye4:{bpos.A:F0},{bpos.Z:F0},4,90,-4");
                poses.Add($"playa_300m:{bpos.A:F0},{bpos.Z:F0},300,90,-80");
                Say($"pose c ground {ground:F1} m");
            }
            Say("AT=" + string.Join(";", poses));
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("RIVER_BED_POSE_QUERY=FAIL " + e); return 1; }
        finally
        {
            RiverGraphStore.Clear();
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    private static string graph0Source() => RenderingServer.GetRenderingDevice() != null ? "GPU source" : "CPU source";

    private static double Norm(double deg) => (Math.Round(deg) % 360d + 360d) % 360d;

    private static string DescribeDiff(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        if (i == a.Length && i == b.Length) return "none";
        int from = Math.Max(0, i - 40);
        return $"first difference at char {i}: OFF ...{a.Substring(from, Math.Min(120, a.Length - from))} | ON ...{b.Substring(from, Math.Min(120, b.Length - from))}";
    }
}
