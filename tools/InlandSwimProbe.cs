using System;
using Godot;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T2R-10: GDScript-facing helper of tools/inland_swim_smoke_gpu_test.gd. Finds a deep lake point and a deep river pool point of the booted river world (from its baked graph and the runtime sampler), each with the
/// nearest dry bank along a straight line, and reads the explorer's swim state. TEST ONLY.
/// </summary>
public partial class InlandSwimProbe : RefCounted
{
    private static bool Wet(RingTerrainSampler s, long a, int z, SurfaceWaterKind kind, out float level, out float bed)
    {
        TerrainSample sample = s.GetTerrainSample(a, z);
        bed = s.GetPresentationSurfaceHeight(a, z);
        level = sample.WaterLevel;
        return sample.WaterKind == kind && sample.HasExactWaterLevel;
    }

    private static Godot.Collections.Dictionary Describe(RingTerrainSampler s, string kind, long a, int z, SurfaceWaterKind want)
    {
        Wet(s, a, z, want, out float level, out float bed);
        // The nearest dry column along each of four directions (2 m steps up to 1500 m); the shortest wins.
        long bestA = 0; int bestZ = 0; double best = double.MaxValue; float ground = 0f;
        (int dx, int dz)[] directions = { (1, 0), (-1, 0), (0, 1), (0, -1) };
        foreach ((int dx, int dz) in directions)
        {
            for (int step = 1; step <= 750; step++)
            {
                long ca = a + dx * 2L * step; int cz = z + dz * 2 * step;
                if (cz < 0 || cz > 317_000) break;
                if (!s.GetTerrainSample(ca, cz).HasSurfaceWater)
                {
                    if (step * 2.0 < best) { best = step * 2.0; bestA = ca; bestZ = cz; ground = s.GetPresentationSurfaceHeight(ca, cz); }
                    break;
                }
            }
        }
        return new Godot.Collections.Dictionary
        {
            ["kind"] = kind, ["around"] = (double)a, ["across"] = (double)z, ["level"] = level, ["bed"] = bed,
            ["dry_around"] = (double)bestA, ["dry_across"] = (double)bestZ, ["dry_ground"] = ground, ["bank_distance"] = best
        };
    }

    /// <summary>{ok, error, lake: {...}, river: {...}} from the booted game's graph. A lake point is Lake kind and at least 2 m deep with wet neighbours at 8 m; a river point is River kind and at least 1.5 m deep.</summary>
    public Godot.Collections.Dictionary FindPoints(Node game)
    {
        var result = new Godot.Collections.Dictionary { ["ok"] = false, ["error"] = "" };
        try
        {
            using RingTerrainSampler? sampler = game.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager")?.CreateIndependentTerrainSampler();
            RiverGraph? graph = sampler?.RuntimeRiverGraph;
            if (sampler == null || graph == null) { result["error"] = "the game's sampler has no river graph"; return result; }
            double step = graph.AroundStepMeters, acrossStep = graph.AcrossStepMeters;
            // Lakes: large ones first, up to 300, a few flooded cells each.
            var lakeOrder = new System.Collections.Generic.List<int>();
            for (int i = 0; i < graph.Lakes.Length; i++) if (graph.Lakes[i].CellCount >= 40) lakeOrder.Add(i);
            lakeOrder.Sort((x, y) => graph.Lakes[y].CellCount.CompareTo(graph.Lakes[x].CellCount));
            var byLake = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<LakeRun>>();
            foreach (LakeRun run in graph.Runs) { if (!byLake.TryGetValue(run.LakeId, out var list)) byLake[run.LakeId] = list = new(); list.Add(run); }
            foreach (int id in lakeOrder.GetRange(0, Math.Min(300, lakeOrder.Count)))
            {
                var runs = byLake[id];
                for (int k = 0; k < Math.Min(runs.Count, 40); k++)
                {
                    LakeRun run = runs[k * runs.Count / Math.Min(runs.Count, 40)];
                    long a = (long)Math.Round((run.Col + run.Length * 0.5) * step / 2d) * 2L; int z = (int)Math.Round((run.Row + 0.5) * acrossStep / 2d) * 2;
                    if (!Wet(sampler, a, z, SurfaceWaterKind.Lake, out float level, out float bed) || level - bed < 2.0f || level - bed > 30.0f || z < 3000 || z > 315_000) continue;
                    bool ring = true;
                    for (int d = 0; d < 8 && ring; d++) ring = Wet(sampler, a + (long)Math.Round(8 * Math.Cos(d * Math.PI / 4)), z + (int)Math.Round(8 * Math.Sin(d * Math.PI / 4)), SurfaceWaterKind.Lake, out _, out _);
                    if (!ring) continue;
                    Godot.Collections.Dictionary lake = Describe(sampler, "lake", a, z, SurfaceWaterKind.Lake);
                    if (lake["bank_distance"].AsDouble() > 1e8) continue;
                    result["lake"] = lake;
                    goto river;
                }
            }
            result["error"] = "no deep lake point found";
            return result;
        river:
            RiverNetwork net = graph.Network;
            for (int r = 0; r < net.Reaches.Length; r += 3)
            {
                RiverReach reach = net.Reaches[r];
                if (reach.SEnd - reach.SStart < 200f) continue;
                net.Centre(reach.Stem, 0.5 * (reach.SStart + reach.SEnd), out double ca, out double cz, out _, out _);
                long a = (long)Math.Round(ca / 2d) * 2L; int z = (int)Math.Round(cz / 2d) * 2;
                if (z < 3000 || z > 315_000 || !Wet(sampler, a, z, SurfaceWaterKind.River, out float level, out float bed) || level - bed < 1.5f) continue;
                result["river"] = Describe(sampler, "river", a, z, SurfaceWaterKind.River);
                result["ok"] = true;
                return result;
            }
            result["error"] = "no deep river pool found";
        }
        catch (Exception e) { result["error"] = e.ToString(); }
        return result;
    }

    /// <summary>
    /// T2R-12b: the widest river reach whose middle lies at across in [minAcross, maxAcross] and holds water at least 1.5 m deep: {ok, around, across, level, width}. The glint poses need a river far
    /// enough from the ring edge for a camera 230 km off along the sun azimuth, and near <paramref name="nearAround"/> so the time of day set there holds.
    /// </summary>
    public Godot.Collections.Dictionary FindRiverAcross(Node game, double minAcross, double maxAcross, double nearAround, double aroundHalfRange)
    {
        var result = new Godot.Collections.Dictionary { ["ok"] = false, ["error"] = "" };
        try
        {
            using RingTerrainSampler? sampler = game.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager")?.CreateIndependentTerrainSampler();
            RiverGraph? graph = sampler?.RuntimeRiverGraph;
            if (sampler == null || graph == null) { result["error"] = "the game's sampler has no river graph"; return result; }
            RiverNetwork net = graph.Network;
            double bestWidth = 0d;
            for (int r = 0; r < net.Reaches.Length; r++)
            {
                RiverReach reach = net.Reaches[r];
                if (reach.SEnd - reach.SStart < 200f) continue;
                double mid = 0.5 * (reach.SStart + reach.SEnd);
                net.Centre(reach.Stem, mid, out double ca, out double cz, out _, out _);
                if (cz < minAcross || cz > maxAcross) continue;
                if (Math.Abs(net.WrapDelta(ca - nearAround)) > aroundHalfRange) continue;
                net.ChannelAt(reach.Stem, mid, out RiverChannel channel);
                if (2d * channel.HalfWidth <= bestWidth) continue;
                long a = (long)Math.Round(ca / 2d) * 2L; int z = (int)Math.Round(cz / 2d) * 2;
                if (!Wet(sampler, a, z, SurfaceWaterKind.River, out float level, out float bed) || level - bed < 1.5f) continue;
                bestWidth = 2d * channel.HalfWidth;
                result["around"] = (double)a; result["across"] = (double)z; result["level"] = (double)level; result["width"] = bestWidth;
                result["ok"] = true;
            }
            if (!result["ok"].AsBool()) result["error"] = "no river pool in the across range";
        }
        catch (Exception e) { result["error"] = e.ToString(); }
        return result;
    }

    /// <summary>
    /// T2R-11 escalation measure: of the land sectors of a proxy level (64 cells of <paramref name="spacing"/> metres, parent 2x) the share whose build routes to the CPU because a river or lake reaches it
    /// (S10), over the whole ring, sampling every <paramref name="stride"/>th sector on both axes. Land = the sector centre is not Ocean. Returns {sectors, land, river_land, share}.
    /// </summary>
    public Godot.Collections.Dictionary RiverRoutingShare(Node game, int spacing, int stride)
    {
        var result = new Godot.Collections.Dictionary { ["sectors"] = 0, ["land"] = 0, ["river_land"] = 0, ["share"] = 0.0 };
        using RingTerrainSampler? sampler = game.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager")?.CreateIndependentTerrainSampler();
        if (sampler == null || sampler.RuntimeRiverGraph == null) return result;
        int size = spacing * 64;
        long aroundSectors = sampler.AroundRingVoxelLength / size;
        int acrossSectors = sampler.RingAcrossWidthMeters / size;
        int sectors = 0, land = 0, riverLand = 0;
        for (long a = 0; a < aroundSectors; a += stride)
            for (int z = 0; z < acrossSectors; z += stride)
            {
                sectors++;
                long ca = a * size + size / 2; int cz = Math.Min(z * size + size / 2, sampler.RingAcrossWidthMeters - 2);
                if (sampler.GetTerrainSample(ca & ~1L, cz & ~1).WaterKind == SurfaceWaterKind.Ocean) continue;
                land++;
                if (RingworldTechDemo.Voxel.LOD.RingTerrainProxySector.SectorTouchesRiver(sampler, new RingworldTechDemo.Voxel.Core.RingTerrainSectorCoordinate(a, z), size, spacing, spacing * 2)) riverLand++;
            }
        result["sectors"] = sectors; result["land"] = land; result["river_land"] = riverLand; result["share"] = land == 0 ? 0.0 : riverLand / (double)land;
        return result;
    }

    private static Godot.Collections.Dictionary Shot(string name, double around, double across, double vertical, double yawDeg, double pitchDeg)
        => new() { ["name"] = name, ["around_m"] = around, ["across_m"] = across, ["lift_m"] = vertical, ["vertical_mode"] = "abs", ["ground_m"] = vertical, ["yaw_deg"] = yawDeg, ["pitch_deg"] = pitchDeg };

    private static double YawTo(double fromAround, double fromAcross, double toAround, double toAcross) => Math.Atan2(toAcross - fromAcross, toAround - fromAround) * 180d / Math.PI;

    /// <summary>
    /// T2R-11: the four milestone views of the booted river world as harness poses (absolute heights): "river_bank" (standing on the bank beside the river point, looking at the water), "lake_shore" (the same
    /// at the lake point), "confluence_300m" (300 m up the receiving stem from a confluence, 60 m above the water, looking at the junction) and "valley_2km" (2 km upstream of a river reach, 250 m up, looking down it).
    /// Yaw follows the explorer's convention: forward = (cos yaw, sin yaw) in (around, across).
    /// </summary>
    public Godot.Collections.Dictionary FindShots(Node game, Godot.Collections.Dictionary points)
    {
        var shots = new Godot.Collections.Dictionary { ["ok"] = false, ["error"] = "" };
        try
        {
            using RingTerrainSampler? sampler = game.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager")?.CreateIndependentTerrainSampler();
            RiverGraph? graph = sampler?.RuntimeRiverGraph;
            if (sampler == null || graph == null) { shots["error"] = "the game's sampler has no river graph"; return shots; }
            foreach (string key in new[] { "river", "lake" })
            {
                var p = (Godot.Collections.Dictionary)points[key];
                double a = p["around"].AsDouble(), z = p["across"].AsDouble(), da = p["dry_around"].AsDouble(), dz = p["dry_across"].AsDouble();
                shots[key == "river" ? "river_bank" : "lake_shore"] = Shot(key == "river" ? "river_bank" : "lake_shore", da, dz, p["dry_ground"].AsDouble() + 1.8, YawTo(da, dz, a, z), -4.0);
                // T2R-11b: the same bank seen from 12 m above the water point, looking down at the waterline (the bank strip check of inland_water_render_gpu_test).
                shots[key == "river" ? "river_bank_top" : "lake_shore_top"] = BankTopShot(key == "river" ? "river_bank_top" : "lake_shore_top", p);
            }
            // T2R-11b: an ocean shore of the same world seen the same way (the no-change reference for the ocean look).
            if (FindOceanShore(sampler, out Godot.Collections.Dictionary oceanPoint))
            {
                shots["ocean_point"] = oceanPoint;
                shots["ocean_shore_top"] = BankTopShot("ocean_shore_top", oceanPoint);
            }
            {
                var lp = (Godot.Collections.Dictionary)points["lake"];
                shots["lake_open"] = Shot("lake_open", lp["around"].AsDouble(), lp["across"].AsDouble(), lp["level"].AsDouble() + 31d, 0.0, -3.0);
                // Ocean reference: the first deep Ocean point of a coarse scan, seen from 31 m with the same attitude.
                for (int i = 0; i < 4000; i++)
                {
                    long oa = (long)(RiverHash.Mix(77, i, 1) % (ulong)EmitterSiteTestWorlds.Circumference) / 2 * 2;
                    int oz = (int)(RiverHash.Mix(77, i, 2) % 300_000UL) / 2 * 2 + 6000;
                    TerrainSample os = sampler.GetTerrainSample(oa, oz);
                    if (os.WaterKind != SurfaceWaterKind.Ocean || sampler.GetPresentationSurfaceHeight(oa, oz) > -40f) continue;
                    shots["ocean_ref"] = Shot("ocean_ref", oa, oz, 31d, 0.0, -3.0);
                    break;
                }
            }
            RiverNetwork net = graph.Network;
            // River aerial: the river point of FindPoints seen from 120 m upstream along the centreline, 16 m above the water, looking downstream.
            {
                var rp = (Godot.Collections.Dictionary)points["river"];
                double ra = rp["around"].AsDouble(), rz = rp["across"].AsDouble(), rl = rp["level"].AsDouble();
                int bestReach = -1; double bestS = 0, bestD = double.MaxValue;
                for (int r = 0; r < net.Reaches.Length; r++)
                {
                    RiverReach reach = net.Reaches[r];
                    double mid = 0.5 * (reach.SStart + reach.SEnd);
                    net.Centre(reach.Stem, mid, out double ca, out double cz, out _, out _);
                    double d = (ca - ra) * (ca - ra) + (cz - rz) * (cz - rz);
                    if (d < bestD) { bestD = d; bestReach = r; bestS = mid; }
                }
                if (bestReach >= 0 && bestS > 200d)
                {
                    int stem = net.Reaches[bestReach].Stem;
                    net.Centre(stem, bestS - 120d, out double ua, out double uz, out _, out _);
                    net.Centre(stem, bestS, out double da, out double dz, out _, out _);
                    shots["river_aerial"] = Shot("river_aerial", ua, uz, rl + 16d, YawTo(ua, uz, da, dz), -12.0);
                }
            }
            // Confluence: a stem that ends in a confluence, its receiving stem long enough on both sides of the junction.
            for (int i = 0; i < net.Stems.Length; i++)
            {
                RiverStem st = net.Stems[i];
                if (st.Terminal != RiverTerminal.Confluence || st.TerminalId < 0) continue;
                int rec = st.TerminalId;
                double length = net.Curves[rec].Length, js = st.JunctionS;
                if (js < 500d || js > length - 500d) continue;
                net.Centre(rec, js, out double ja, out double jz, out double tx, out double ty);
                double level = sampler.GetTerrainSample((long)Math.Round(ja), (int)Math.Round(jz)).WaterLevel;
                if (level == 0f) continue;
                shots["confluence_300m"] = Shot("confluence_300m", ja - tx * 300d, jz - ty * 300d, level + 60d, YawTo(ja - tx * 300d, jz - ty * 300d, ja, jz), -12.0);
                break;
            }
            // Valley: the longest reach with 2 km of stem upstream of its middle, looking downstream.
            for (int r = 0; r < net.Reaches.Length; r += 2)
            {
                RiverReach reach = net.Reaches[r];
                double mid = 0.5 * (reach.SStart + reach.SEnd);
                if (reach.SEnd - reach.SStart < 600f || mid < 2100d) continue;
                net.Centre(reach.Stem, mid, out double ca, out double cz, out double tx, out double ty);
                if (cz < 6000 || cz > 312_000) continue;
                double level = sampler.GetTerrainSample((long)Math.Round(ca), (int)Math.Round(cz)).WaterLevel;
                if (level == 0f) continue;
                net.Centre(reach.Stem, mid - 2000d, out double ua, out double uz, out _, out _);
                shots["valley_2km"] = Shot("valley_2km", ua, uz, sampler.GetPresentationSurfaceHeight((long)Math.Round(ua), (int)Math.Round(uz)) + 250d, YawTo(ua, uz, ca, cz), -9.0);
                break;
            }
            shots["ok"] = shots.ContainsKey("river_bank") && shots.ContainsKey("lake_shore");
        }
        catch (Exception e) { shots["error"] = e.ToString(); }
        return shots;
    }

    /// <summary>T2R-11b: 12 m above the water on the line from a water point (FindPoints / FindOceanShore layout) to its nearest dry bank, at most 15 m from the bank, looking at the bank with the waterline
    /// near the screen centre.</summary>
    private static Godot.Collections.Dictionary BankTopShot(string name, Godot.Collections.Dictionary p)
    {
        double a = p["around"].AsDouble(), z = p["across"].AsDouble(), da = p["dry_around"].AsDouble(), dz = p["dry_across"].AsDouble();
        double length = Math.Max(1.0, Math.Sqrt((da - a) * (da - a) + (dz - z) * (dz - z)));
        double back = Math.Min(length, 15.0);
        double ca = da - (da - a) / length * back, cz = dz - (dz - z) / length * back;
        return Shot(name, ca, cz, p["level"].AsDouble() + 12.0, YawTo(ca, cz, da, dz), -Math.Atan2(12.0, Math.Max(4.0, back)) * 180d / Math.PI);
    }

    /// <summary>
    /// T2R-11b: an ocean point 3 to 12 m off a shore, with the nearest dry column along the across axis, in the layout of <see cref="Describe"/> (around, across, level, bed, dry_around, dry_across, dry_ground,
    /// bank_distance). Scans lines across the ring at hashed arounds for a land-ocean transition; deterministic for a world.
    /// </summary>
    private static bool FindOceanShore(RingTerrainSampler s, out Godot.Collections.Dictionary result)
    {
        result = new Godot.Collections.Dictionary();
        long circumference = Math.Max(2L, s.AroundRingVoxelLength);
        int width = s.RingAcrossWidthMeters;
        for (int i = 0; i < 64; i++)
        {
            long a = (long)(RiverHash.Mix(91, i, 1) % (ulong)circumference) / 2 * 2;
            bool previousOcean = s.GetTerrainSample(a, 6000).WaterKind == SurfaceWaterKind.Ocean;
            for (int z = 6064; z < width - 6000; z += 64)
            {
                bool ocean = s.GetTerrainSample(a, z).WaterKind == SurfaceWaterKind.Ocean;
                if (ocean == previousOcean) continue;
                // Refine to the first ocean column next to land (2 m steps), then step 3 to 12 m out to sea.
                int direction = ocean ? 1 : -1;
                int zLand = ocean ? z - 64 : z;
                int zSea = zLand;
                while (Math.Abs(zSea - zLand) <= 64 && s.GetTerrainSample(a, zSea).WaterKind != SurfaceWaterKind.Ocean) zSea += 2 * direction;
                if (Math.Abs(zSea - zLand) > 64) { previousOcean = ocean; continue; }
                for (int step = 3; step <= 12; step++)
                {
                    int zWater = zSea + 2 * step * direction;
                    TerrainSample sample = s.GetTerrainSample(a, zWater);
                    float bed = s.GetPresentationSurfaceHeight(a, zWater);
                    float level = sample.WaterLevel;
                    if (sample.WaterKind != SurfaceWaterKind.Ocean || level - bed < 0.8f || level - bed > 12f) continue;
                    int zDry = zSea - 2 * direction;
                    result = new Godot.Collections.Dictionary
                    {
                        ["kind"] = "ocean", ["around"] = (double)a, ["across"] = (double)zWater, ["level"] = level, ["bed"] = bed,
                        ["dry_around"] = (double)a, ["dry_across"] = (double)zDry, ["dry_ground"] = s.GetPresentationSurfaceHeight(a, zDry), ["bank_distance"] = (double)Math.Abs(zWater - zDry)
                    };
                    return true;
                }
                previousOcean = ocean;
            }
        }
        return false;
    }

    /// <summary>
    /// T2R-11b bank strip check: samples the straight line from a water point (FindPoints / FindOceanShore layout) to its nearest dry column and <paramref name="beyondMeters"/> past it, every 0.25 m. Each
    /// entry is [s (m from the water point), wet (1/0), depth (level - bed, m), screen x, screen y, behind camera (1/0)] for the visible surface (water level where wet, ground where dry) projected with the
    /// current camera. Returns {ok, error, entries, viewport}.
    /// </summary>
    public Godot.Collections.Dictionary BankProfile(Node game, Godot.Collections.Dictionary point, double beyondMeters)
    {
        var result = new Godot.Collections.Dictionary { ["ok"] = false, ["error"] = "" };
        try
        {
            var grid = game.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager");
            using RingTerrainSampler? sampler = grid?.CreateIndependentTerrainSampler();
            Camera3D? camera = game.GetViewport()?.GetCamera3D();
            if (grid == null || sampler == null || camera == null) { result["error"] = "grid, sampler or camera missing"; return result; }
            RingLodGeometrySnapshot geometry = grid.CaptureLodGeometrySnapshot();
            double a = point["around"].AsDouble(), z = point["across"].AsDouble(), da = point["dry_around"].AsDouble(), dz = point["dry_across"].AsDouble();
            double length = Math.Sqrt((da - a) * (da - a) + (dz - z) * (dz - z));
            if (length < 1.0) { result["error"] = "dry column too close"; return result; }
            double ua = (da - a) / length, uz = (dz - z) / length;
            var entries = new Godot.Collections.Array();
            for (double t = 0.0; t <= length + beyondMeters; t += 0.25)
            {
                double ca = a + ua * t, cz = z + uz * t;
                long ia = (long)Math.Round(ca); int iz = (int)Math.Round(cz);
                TerrainSample sample = sampler.GetTerrainSample(ia, iz);
                float bed = sampler.GetPresentationSurfaceHeight(ia, iz);
                bool wet = sample.HasSurfaceWater && bed < sample.WaterLevel;
                float depth = sample.HasSurfaceWater ? sample.WaterLevel - bed : 0f;
                Vector3 world = geometry.CanonicalToWorld(ca, wet ? sample.WaterLevel : bed, cz);
                bool behind = camera.IsPositionBehind(world);
                Vector2 screen = behind ? Vector2.Zero : camera.UnprojectPosition(world);
                entries.Add(new Godot.Collections.Array { t, wet ? 1 : 0, depth, screen.X, screen.Y, behind ? 1 : 0 });
            }
            result["entries"] = entries;
            result["viewport"] = game.GetViewport().GetVisibleRect().Size;
            result["ok"] = true;
        }
        catch (Exception e) { result["error"] = e.ToString(); }
        return result;
    }

    /// <summary>The explorer's swim state: {swimming, surface, depth, vertical}.</summary>
    public Godot.Collections.Dictionary Read(Node explorer)
    {
        var e = (RingVoxelExplorerController)explorer;
        return new Godot.Collections.Dictionary
        {
            ["swimming"] = e.IsSwimming, ["surface"] = e.CurrentWaterSurfaceHeightMeters, ["depth"] = e.CurrentWaterDepthMeters, ["vertical"] = e.CanonicalPosition.VerticalMeters
        };
    }
}
