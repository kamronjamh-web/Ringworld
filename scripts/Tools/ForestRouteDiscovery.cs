using System;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>Offline, bounded route search for the Certified preset. It creates
/// only a terrain sampler; no world scene, saves, streaming, or tree renderer.
/// </summary>
public partial class ForestRouteDiscovery : RefCounted
{
    const long Circumference = 981_748L * 32L;
    const int Width = 318_000, CoarseStep = 2_000, LocalStep = 64;
    public Godot.Collections.Dictionary Run()
    {
        var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true)
            ?? throw new InvalidOperationException("terrain settings missing"));
        settings.BedrockY = -2000;
        using var terrain = new RingTerrainSampler(settings, true, Circumference, 5_000_000.0, Width, 8000);
        var forest = new ForestRegionSampler(settings.Seed, Circumference, Width);
        int dense = 0, open = 0, clearing = 0, viable = 0;
        float maxDensity = -1; Point best = default; TerrainSample bestTerrain = default;
        // The preview's 128x96 km window is centered at (0,159 km). This
        // deliberately expands just two regions around it, still below 50k
        // samples and entirely outside gameplay startup.
        for (int a = -256_000; a <= 256_000; a += CoarseStep)
        for (int z = 31_000; z <= 287_000; z += CoarseStep)
        {
            TerrainSample t = terrain.GetTerrainSample(a, z);
            float slope = Slope(terrain, a, z, CoarseStep / 8);
            ForestRegionSample f = forest.Sample(a, z, t, slope);
            if (f.Density > 0) viable++;
            if (f.Habitat == ForestHabitat.DenseForest) dense++;
            if (f.Habitat == ForestHabitat.OpenWoodland) open++;
            if (f.Habitat == ForestHabitat.Clearing) clearing++;
            if (f.Density > maxDensity) { maxDensity = f.Density; best = new Point(a, z); bestTerrain = t; }
        }
        Godot.Collections.Dictionary result = new()
        {
            ["seed"] = settings.Seed, ["max_density"] = maxDensity,
            ["max_around"] = best.Around, ["max_across"] = best.Across,
            ["max_biome"] = bestTerrain.Biome.ToString(), ["max_temperature"] = bestTerrain.Temperature,
            ["max_moisture"] = bestTerrain.Moisture, ["dense_count"] = dense,
            ["open_count"] = open, ["clearing_count"] = clearing, ["viable_count"] = viable
        };
        if (TryFindLocalRoute(terrain, forest, best, out Point start, out Point woodland, out Point grove))
        {
            result["route_found"] = true;
            result["start_around"] = start.Around; result["start_across"] = start.Across;
            result["woodland_around"] = woodland.Around; result["woodland_across"] = woodland.Across;
            result["grove_around"] = grove.Around; result["grove_across"] = grove.Across;
        }
        else result["route_found"] = false;
        GD.Print("FOREST_ROUTE_DISCOVERY=", result);
        return result;
    }

    static bool TryFindLocalRoute(RingTerrainSampler terrain, ForestRegionSampler forest, Point grove, out Point start, out Point woodland, out Point dense)
    {
        start = woodland = dense = default;
        for (int dx = -2_400; dx <= 2_400; dx += 192)
        for (int dz = -2_400; dz <= 2_400; dz += 192)
        {
            int a = grove.Around + dx, z = grove.Across + dz;
            if (!Sample(forest, terrain, a, z, out ForestRegionSample candidate) || candidate.Habitat != ForestHabitat.Clearing) continue;
            // Select a genuine nearby dense point and verify every 64m along
            // the path stays dry and walkable. The midpoint must be woodland.
            int da = grove.Around - a, dz2 = grove.Across - z;
            int middleA = a + da / 2, middleZ = z + dz2 / 2;
            if (!Sample(forest, terrain, middleA, middleZ, out ForestRegionSample middle)
                || middle.Habitat != ForestHabitat.OpenWoodland || !WalkableLine(terrain, a, z, grove.Around, grove.Across)) continue;
            start = new Point(a, z); woodland = new Point(middleA, middleZ); dense = grove; return true;
        }
        return false;
    }
    static bool Sample(ForestRegionSampler forest, RingTerrainSampler terrain, int a, int z, out ForestRegionSample sample)
    {
        TerrainSample t = terrain.GetTerrainSample(a, z);
        sample = forest.Sample(a, z, t, Slope(terrain, a, z, 8));
        return !t.HasSurfaceWater && Slope(terrain, a, z, 8) < 22;
    }
    static bool WalkableLine(RingTerrainSampler terrain, int a0, int z0, int a1, int z1)
    {
        int count = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(a1-a0), Math.Abs(z1-z0)) / (double)LocalStep));
        for (int i=0;i<=count;i++) { int a=a0+(a1-a0)*i/count, z=z0+(z1-z0)*i/count; if (terrain.GetTerrainSample(a,z).HasSurfaceWater || Slope(terrain,a,z,8)>=25) return false; }
        return true;
    }
    static float Slope(RingTerrainSampler terrain, int a, int z, int d) => Mathf.RadToDeg(Mathf.Atan(Mathf.Max(Math.Abs(terrain.GetTerrainSample(a+d,z).SurfaceHeight-terrain.GetTerrainSample(a-d,z).SurfaceHeight), Math.Abs(terrain.GetTerrainSample(a,z+d).SurfaceHeight-terrain.GetTerrainSample(a,z-d).SurfaceHeight))/(2f*d)));
    readonly record struct Point(int Around, int Across);
}
