using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// PT2-3 (docs/work-orders/terrain-vegetation/T2-rules.md, play-test 2): the vertex shader restores a level's underlay only for FFT ocean vertices, so lake and river water must carry no restore
/// terms in its morph (UV2.x). Builds the 4 m and 8 m sectors over river reaches twice, with level underlays 0.2 / 0.5 (the 4 m band, parent restoring) and 0.5 / 1.0 (the 8 m band) and with none,
/// and compares the water morph: a river vertex's morph must shift by exactly (underlay - parentUnderlay), the same as terrain, so the surface stays within a few cm of the sampler level against
/// its bed in the band. Before the fix the restore terms cancelled the shift (0.3 m / 0.5 m off the bed). Prints PROXY_INLAND_WATER_MORPH=PASS|FAIL. Headless.
/// </summary>
public partial class ProxyInlandWaterMorphTest : RefCounted
{
    private const int Seed = 1337;
    private const double StripStart = 9_804_000d;
    private const int StripKm = 400;
    private const int Cells = 64;
    private const float Tolerance = 0.02f;

    public int Run()
    {
        var failures = new List<string>();
        TerrainGenerationParameters recipe = EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1, new[] { "water.rivers" });
        using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, recipe);
        try
        {
            RiverTestGraphs.Install(recipe, settings, StripStart, StripKm, Math.Clamp(System.Environment.ProcessorCount / 2, 2, 6));
            using var sampler = new RingTerrainSampler(settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
            RiverNetwork net = sampler.RuntimeRiverGraph!.Network;
            var edits = new RingLodSurfaceEditSnapshot(0, sampler.AroundRingVoxelLength, new Dictionary<RingTerrainColumnCoordinate, int>(),
                new Dictionary<RingTerrainColumnCoordinate, float>(), sampler.UsesRuntimeSurfaceGeneration);
            var session = RingTerrainProxySector.CreateRiverWidthFixtureSession(edits, sampler, sampler.RingAcrossWidthMeters);
            double radius = EmitterSiteTestWorlds.Circumference / Math.Tau;
            (int Spacing, float Under, float ParentUnder)[] bands = { (4, 0.2f, 0.5f), (8, 0.5f, 1.0f) };
            int reaches = 0;
            for (int stem = 0; stem < net.Stems.Length && reaches < 6; stem++)
            {
                if (net.Curves[stem].Length < 700d) continue;
                net.CentreFrame(stem, 300d, out double around, out double across, out _, out _);
                if (across < 5000d || across > net.WidthM - 5000d) continue;
                reaches++;
                foreach ((int spacing, float under, float parentUnder) in bands)
                {
                    int size = Cells * spacing;
                    var coordinate = new RingTerrainSectorCoordinate((long)Math.Floor(around / size), (int)Math.Floor(across / size));
                    var plain = session.Build(coordinate, spacing, Cells, radius);
                    var banded = session.Build(coordinate, spacing, Cells, radius, under, parentUnder, parentRestoresWaterUnderlay: true);
                    int inland = 0, off = 0;
                    float want = under - parentUnder, worst = 0.0f;
                    for (int i = 0; i < plain.LatticeVertices; i++)
                    {
                        int w = plain.LatticeVertices + i;
                        TerrainSample sample = sampler.GetTerrainSample(coordinate.AroundSector * (long)size + (i % (Cells + 1)) * spacing,
                            Math.Clamp(coordinate.AcrossSector * size + (i / (Cells + 1)) * spacing, 0, sampler.RingAcrossWidthMeters));
                        if (!(sample.HasSurfaceWater && sample.WaterKind is SurfaceWaterKind.Lake or SurfaceWaterKind.River)) continue; // dry or ocean
                        float shift = banded.MorphData[w].X - plain.MorphData[w].X;
                        if (Mathf.Abs(shift - want) <= Tolerance) { inland++; continue; }
                        if (Mathf.Abs(shift) <= Tolerance) continue; // ocean or dry vertex: the shader restores the underlay
                        off++; worst = Mathf.Max(worst, Mathf.Abs(shift - want));
                    }
                    Console.WriteLine($"PROXY_INLAND_WATER_MORPH_DETAIL stem {stem} spacing {spacing} m: {inland} inland-shifted vertices, {off} other, worst {worst:F3} m");
                    if (inland == 0) failures.Add($"stem {stem} {spacing} m: no water vertex carries the level shift {want:F2} m (restore terms still applied to inland water)");
                    if (off > 0) failures.Add($"stem {stem} {spacing} m: {off} water vertices shifted by neither 0 nor {want:F2} m");
                }
            }
            if (reaches == 0) failures.Add("no river reach found");
        }
        catch (Exception e) { Console.Error.WriteLine("PROXY_INLAND_WATER_MORPH=FAIL exception " + e); return 1; }
        finally { RiverGraphStore.Clear(); }
        if (failures.Count != 0) { Console.Error.WriteLine("PROXY_INLAND_WATER_MORPH=FAIL " + string.Join(" | ", failures)); return 1; }
        Console.WriteLine("PROXY_INLAND_WATER_MORPH=PASS");
        return 0;
    }
}
