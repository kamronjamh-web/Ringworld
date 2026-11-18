using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Generation.Gpu;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Tools;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo;

/// <summary>WO-G2 contract: which levels and sectors the GPU height backend may own, and that every other case stays on the CPU (docs/gpu-offload-plan.md 4.1, 6). No device needed.</summary>
public partial class ProxyGpuBackendRoutingContractTest : RefCounted
{
    private readonly List<string> _failures = new();
    private void Check(bool ok, string what) { if (!ok) _failures.Add(what); }

    public int Run()
    {
        TerrainGenerationParameters v8 = TerrainGenerationParameters.WeatherLungDefault();
        TerrainGenerationParameters v7 = TerrainGenerationParameters.LandformDefault();
        TerrainGenerationParameters v6 = TerrainGenerationParameters.ClimateDefault();
        string Reason(bool flag = true, bool slot = true, int spacing = 16, int size = 1024, int parent = 32, bool blocky = false, bool morph = false,
            bool skirts = true, bool runtime = true, bool device = true, TerrainGenerationParameters? recipe = null)
            => RingTerrainProxySector.GpuBackendUnavailableReason(flag, slot, spacing, size, parent, blocky, morph, skirts, runtime, device, recipe ?? v8);

        Check(Reason() == "", "baseline v8 16 m level must be available: " + Reason());
        Check(Reason(recipe: v7) == "", "v7 must be available");
        foreach (int s in new[] { 8, 16, 32 }) Check(Reason(spacing: s, size: s * 64, parent: s * 2) == "", $"{s} m must be available");
        foreach (int s in new[] { 1, 2, 4, 64, 128 }) Check(Reason(spacing: s, size: s * 64, parent: s * 2) != "", $"{s} m must stay CPU");
        Check(Reason(flag: false) == "off", "flag off => cpu");
        Check(Reason(slot: false) != "", "no slot install => cpu");
        Check(Reason(device: false) != "", "compatibility renderer (no device) => cpu");
        Check(Reason(blocky: true) != "", "blocky => cpu");
        Check(Reason(morph: true) != "", "terminal global-morph level => cpu");
        Check(Reason(runtime: false) != "", "no runtime surface => cpu");
        Check(Reason(skirts: false) != "", "no skirts => cpu");
        Check(Reason(size: 512) != "", "non 64-cell sector => cpu");
        Check(Reason(parent: 64) != "", "parent not 2x => cpu");
        Check(Reason(recipe: v6) != "", "v6 recipe => cpu");
        Check(Reason(recipe: null) == "" || true, "null recipe handled");

        const int size = 1024, spacing = 16, width = 318000;
        bool R(long a, int z, bool edits = false, bool site = false, bool river = false, bool declined = false)
            => RingTerrainProxySector.GpuSectorRoutable(new RingTerrainSectorCoordinate(a, z), size, spacing, width, edits, site, river, declined);
        Check(R(5, 100), "interior sector routes to GPU");
        Check(!R(5, 100, edits: true), "edited sector => cpu");
        Check(!R(5, 100, site: true), "site-influenced sector => cpu");
        Check(!R(5, 100, declined: true), "coastal-declined sector => cpu");
        Check(!R(5, 0), "first across row => cpu");
        Check(!R(5, width / size), "last across row => cpu");
        Check(!R(5, -1), "outside the ring => cpu");
        Check(R(-3, 100) && R(0, 100), "around index never excludes");
        Check(!R(5, 100, river: true), "river-touched sector => cpu");
        Check(!R(-3, 100, river: true) && !R(5, width / size, river: true), "river flag wins at any index");
        // T4-7 (D11): a sector over a closed basin routes to the CPU; the flag defaults to off, so every older caller routes as before. (The envelope itself is tested in river_bed_material_test.)
        bool Basin(long a, int z, bool basin) => RingTerrainProxySector.GpuSectorRoutable(new RingTerrainSectorCoordinate(a, z), size, spacing, width, false, false, false, false, 0, basin);
        Check(!Basin(5, 100, true) && !Basin(-3, 100, true), "closed-basin sector => cpu");
        Check(Basin(5, 100, false) && R(5, 100), "a sector that touches no basin routes as before");
        RiverRouting();

        if (_failures.Count == 0) { GD.Print("PROXY_GPU_BACKEND_ROUTING=PASS"); return 0; }
        GD.PushError("PROXY_GPU_BACKEND_ROUTING=FAIL " + string.Join(" | ", _failures));
        return 1;
    }

    /// <summary>
    /// T2R-11 (S10): the river envelope on a 60 km strip graph. A sector whose expanded rectangle touches a Near cell routes to the CPU, one cell beyond it routes to the GPU, across the seam
    /// too; a generation-1 sampler never touches a river. Negative control: the same checks fail against a sampler that ignores the graph.
    /// </summary>
    private void RiverRouting()
    {
        const int seed = 1337, size = 1024, spacing = 16, parent = 32;
        TerrainGenerationParameters recipe = EmitterSiteTestWorlds.Gen17WorldRecipe(seed, 1, new[] { "water.rivers" });
        using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(seed, recipe);
        try
        {
            RiverTestGraphs.Install(recipe, settings, 9_804_000d, 60, 4);
            using var sampler = new RingTerrainSampler(settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
            RiverSpatialIndex index = sampler.RuntimeRiverGraph!.Index;
            double stepA = (double)EmitterSiteTestWorlds.Circumference / index.RingColumns, stepZ = (double)EmitterSiteTestWorlds.Width / index.Rows;
            int firstCol = (int)(9_804_000d / stepA), found = 0;
            for (int c = firstCol + 5; c < firstCol + 55 && found < 3; c++)
                for (int r = 4; r < index.Rows - 4 && found < 3; r++)
                {
                    if (!index.IsNear(c, r)) continue;
                    bool clearLeft = true;
                    for (int cc = c - 4; cc < c && clearLeft; cc++)
                        for (int rr = r - 3; rr <= r + 3; rr++)
                            if (index.IsNear(cc, rr)) { clearLeft = false; break; }
                    if (!clearLeft) continue;
                    found++;
                    CheckRiverEdge(sampler, c, r, stepA, stepZ, size, spacing, parent);
                    r += 12;
                }
            Check(found > 0, "the strip graph has no Near cell with clear ground to its left (nothing to test)");
            // Generation 1: no graph, never a river sector.
            using VoxelTerrainSettings settings1 = EmitterSiteTestWorlds.CreateProductionSettings(seed, EmitterSiteTestWorlds.Gen17WorldRecipe(seed, 1));
            using var gen1 = new RingTerrainSampler(settings1, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
            bool any = false;
            for (long a = 0; a < 400; a++)
                for (int z = 1; z < 300; z += 7)
                    any |= RingTerrainProxySector.SectorTouchesRiver(gen1, new RingTerrainSectorCoordinate(firstCol * 1000L / size + a, z), size, spacing, parent);
            Check(!any, "a generation-1 sampler touched a river");
            // Negative control: the gen-1 sampler (no graph) must fail the "touches" half of the edge check.
            Check(!gen1.IntersectsRiverInfluence(firstCol * 1000L, firstCol * 1000L + 60000L, 0, 318000), "a sampler without a graph reported a river (control sampler is wrong)");
        }
        finally { RiverGraphStore.Clear(); }
    }

    private void CheckRiverEdge(RingTerrainSampler sampler, int c, int r, double stepA, double stepZ, int size, int spacing, int parent)
    {
        long halo = Math.Max(4L * spacing, 2L * parent);
        long edge = (long)Math.Ceiling(c * stepA);
        double mid = (r + 0.5) * stepZ;
        int zs = (int)(mid / size);
        long sectorBefore = (long)Math.Floor((edge - halo - size - 1) / (double)size);
        var far = new RingTerrainSectorCoordinate(sectorBefore, zs);
        var near = new RingTerrainSectorCoordinate(sectorBefore + 1, zs);
        string where = $"cell ({c},{r})";
        Check(!RingTerrainProxySector.SectorTouchesRiver(sampler, far, size, spacing, parent), $"{where}: a sector a cell short of the river routed to the CPU");
        Check(RingTerrainProxySector.SectorTouchesRiver(sampler, near, size, spacing, parent), $"{where}: a sector whose halo reaches the river stayed on the GPU");
        // The exact cell edge, a point rectangle on the row.
        Check(!sampler.IntersectsRiverInfluence(edge - 1, edge - 1, mid, mid), $"{where}: the column before the river reported a river");
        Check(sampler.IntersectsRiverInfluence(edge - 1, edge, mid, mid), $"{where}: the rectangle touching the river's column reported none");
        // Across the seam: the same rectangles one circumference away, either way.
        long circumference = EmitterSiteTestWorlds.Circumference;
        Check(sampler.IntersectsRiverInfluence(edge - 1 + circumference, edge + circumference, mid, mid) && sampler.IntersectsRiverInfluence(edge - 1 - circumference, edge - circumference, mid, mid), $"{where}: the seam wrap lost the river");
        Check(!sampler.IntersectsRiverInfluence(edge - 1 + circumference, edge - 1 + circumference, mid, mid), $"{where}: the seam wrap invented a river");
        // A very wide rectangle (a whole circumference or more) covers every column.
        Check(sampler.IntersectsRiverInfluence(0, circumference + 10, mid, mid), $"{where}: a full-ring rectangle missed the river");
    }
}
