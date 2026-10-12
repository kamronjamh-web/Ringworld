using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingScaledFarViewport
{
    public string RunStandaloneFarSurfaceTileContract()
    {
        const long circumference = 8_000_000;
        const int width = 256;
        using var sampler = new RingTerrainSampler(new VoxelTerrainSettings(), true,
            circumference, circumference / Math.Tau, width, 2048);
        var edits = new RingLodSurfaceEditSnapshot(0UL, circumference,
            new Dictionary<RingTerrainColumnCoordinate, int>());
        SurfaceTileKey Key(double around, ulong revision = 0) => MakeSurfaceTileKey(sampler,
            revision, 32, circumference, width, around, 0, 256, 256, LowTerrainColor, HighTerrainColor);
        var key = Key(circumference - 256);
        if (Key(-256) != key || Key(circumference * 2 - 256) != key)
            return "FAIL: Periodic tile keys differ at equivalent ring positions.";
        if (Key(0, 1) == Key(0) || key with { Generator = key.Generator + 1 } == key
            || key with { Definition = key.Definition + 1 } == key || key with { Width = width + 1 } == key
            || key with { Resolution = 64 } == key || key with { High = Colors.Red } == key
            || key with { DisplayWater = true } == key)
            return "FAIL: Tile identity missed a terrain/edit/layout/palette dependency.";
        var left = BuildSurfaceTilePixels(key, sampler, edits, CancellationToken.None);
        var right = BuildSurfaceTilePixels(Key(0), sampler, edits, CancellationToken.None);
        for (int y = 1; y <= 32; y++)
        for (int channel = 0; channel < 4; channel++)
        {
            int a = (y * left.Size + 32) * 4 + channel;
            int b = (y * right.Size + 1) * 4 + channel;
            if (left.Color[a] != right.Color[b] || left.Normal[a] != right.Normal[b])
                return "FAIL: Color or curved-frame normal discontinuity at ring-wrap tile boundary.";
        }
        // With zero slopes, inward-up decodes to (-cos(angle),0,-sin(angle)).
        for (int i = 0; i < left.Normal.Length; i += 4)
        {
            Vector3 n = new(left.Normal[i] / 255f * 2 - 1, left.Normal[i + 1] / 255f * 2 - 1,
                left.Normal[i + 2] / 255f * 2 - 1);
            if (!n.IsFinite() || Math.Abs(n.Length() - 1) > .012 || n.Y <= 0)
                return "FAIL: Terrain normal encoding is invalid or faces outward.";
        }
        var editedColumns = new Dictionary<RingTerrainColumnCoordinate, int>
        {
            [new(64, 0)] = (int)edits.GetPresentationSurfaceHeight(sampler, 64, 0) + 128
        };
        var changedEdits = new RingLodSurfaceEditSnapshot(1UL, circumference, editedColumns);
        var changed = BuildSurfaceTilePixels(Key(0, 1), sampler, changedEdits, CancellationToken.None);
        int firstPixel = (right.Size + 1) * 4;
        if (right.Normal[firstPixel] == changed.Normal[firstPixel]
            && right.Normal[firstPixel + 1] == changed.Normal[firstPixel + 1])
            return "FAIL: Cached normal generation ignored edited terrain slopes.";
        long waterAround = -1;
        TerrainSample waterSample = default;
        for (long probe = 0; probe < circumference; probe += 2048)
        {
            TerrainSample candidate = sampler.GetTerrainSample(probe, 128);
            if (!candidate.HasSurfaceWater) continue;
            bool flatWaterNeighborhood = true;
            foreach ((long a, int z) in new[] { (probe - 64, 128), (probe + 64, 128), (probe, 64), (probe, 192) })
            {
                TerrainSample neighbor = sampler.GetTerrainSample((long)WrapSurfaceTileAround(a, circumference), z);
                flatWaterNeighborhood &= neighbor.HasSurfaceWater && neighbor.WaterSurfaceHeight == candidate.WaterSurfaceHeight;
            }
            if (!flatWaterNeighborhood) continue;
            waterAround = probe;
            waterSample = candidate;
            break;
        }
        if (waterAround < 0) return "FAIL: Water contract could not find a seeded water sample.";
        var submergedColumns = new Dictionary<RingTerrainColumnCoordinate, int>
        {
            [new(waterAround, 128)] = waterSample.WaterSurfaceHeight - 120,
            [new((long)WrapSurfaceTileAround(waterAround - 64, circumference), 128)] = waterSample.WaterSurfaceHeight - 200,
            [new((long)WrapSurfaceTileAround(waterAround + 64, circumference), 128)] = waterSample.WaterSurfaceHeight - 50,
            [new(waterAround, 64)] = waterSample.WaterSurfaceHeight - 120,
            [new(waterAround, 192)] = waterSample.WaterSurfaceHeight - 120
        };
        var submerged = new RingLodSurfaceEditSnapshot(2UL, circumference, submergedColumns);
        var rawWaterKey = Key(waterAround, 2) with { Across = 128, AroundExtent = .001, AcrossExtent = .001 };
        var displayedWaterKey = rawWaterKey with { DisplayWater = true };
        var rawWater = BuildSurfaceTilePixels(rawWaterKey, sampler, submerged, CancellationToken.None);
        var displayedWater = BuildSurfaceTilePixels(displayedWaterKey, sampler, submerged, CancellationToken.None);
        if (displayedWater.Normal[firstPixel] != 128 || displayedWater.Normal[firstPixel + 1] != 255
            || displayedWater.Normal[firstPixel + 2] != 128 || rawWater.Normal[firstPixel] == displayedWater.Normal[firstPixel])
            return "FAIL: Regional water normal was not flat or global terrain lost its submerged slope.";
        Color expectedWater = RingTerrainVisuals.ResolveMacroTextureColor(waterSample, waterSample.WaterSurfaceHeight,
            sampler, LowTerrainColor, HighTerrainColor);
        if (displayedWater.Color[firstPixel] != PackSurfaceTileByte(expectedWater.R)
            || displayedWater.Color[firstPixel + 1] != PackSurfaceTileByte(expectedWater.G)
            || displayedWater.Color[firstPixel + 2] != PackSurfaceTileByte(expectedWater.B))
            return "FAIL: Regional surface tile did not preserve displayed water color.";
        Color expectedSubmerged = RingTerrainVisuals.ResolveMacroTextureColor(waterSample, waterSample.WaterSurfaceHeight - 120,
            sampler, LowTerrainColor, HighTerrainColor);
        if (rawWater.Color[firstPixel] != PackSurfaceTileByte(expectedSubmerged.R)
            || rawWater.Color[firstPixel + 1] != PackSurfaceTileByte(expectedSubmerged.G)
            || rawWater.Color[firstPixel + 2] != PackSurfaceTileByte(expectedSubmerged.B))
            return "FAIL: Global surface tile did not preserve separate submerged terrain color.";
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { BuildSurfaceTilePixels(key, sampler, edits, cancelled.Token); return "FAIL: Cancelled tile generation completed."; }
        catch (OperationCanceledException) { }
        int savedBudget = FarSurfaceTileCpuBudgetMb;
        FarSurfaceTileCpuBudgetMb = 1;
        using var template = new ShaderMaterial { Shader = GD.Load<Shader>("res://materials/ring_terrain_global.gdshader") };
        using var bound = BindFarSurfaceMaterial(template, key.Around, key.Across, key.AroundExtent, key.AcrossExtent);
        SurfaceTileBinding binding = _surfaceTileBindings[^1];
        binding.Key = key;
        bound.SetShaderParameter("surface_tile_enabled", true);
        var entry = new SurfaceTileEntry { Pixels = left };
        _surfaceTileCache[key] = entry;
        // Reserve a whole budget for newly requested staging data: the actual
        // generated pixel payload must be evicted and its material use fallback.
        bool bounded = MakeSurfaceTileRoom(1024L * 1024L, 0, default) && _surfaceTileCache.Count == 0
            && !bound.GetShaderParameter("surface_tile_enabled").AsBool();
        ApplySurfaceTile(binding, new SurfaceTileEntry { Pixels = right });
        bounded &= !bound.GetShaderParameter("surface_tile_enabled").AsBool();
        ReleaseFarSurfaceMaterial(bound);
        FarSurfaceTileCpuBudgetMb = savedBudget;
        return bounded ? "PASS" : "FAIL: Surface tile residency exceeded its CPU bound.";
    }
}
