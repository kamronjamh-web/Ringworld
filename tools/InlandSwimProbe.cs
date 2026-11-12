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
