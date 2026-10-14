using System;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>Reference worlds shared by the emitter-site baseline and registry tests.</summary>
public static class EmitterSiteTestWorlds
{
    public const long Circumference = 31_415_936L;
    public const int Width = 318_000;
    public const string Contract = "surface_runtime_generation_v1";

    /// <summary>Gen-17 v4 (ringworld_surface_erosion_v4) default recipe with the given seed.</summary>
    public static TerrainGenerationParameters Gen17Recipe(int seed)
        => TerrainGenerationParameters.FromJson(
            TerrainGenerationParameters.ErosionDefault().CanonicalJson.Replace("\"seed\":1337", "\"seed\":" + seed));

    public static RingTerrainSampler CreateGen17(int seed)
    {
        TerrainGenerationParameters recipe = Gen17Recipe(seed);
        using var settings = new VoxelTerrainSettings
        {
            Seed = seed, SurfaceGenerationRecipeJson = recipe.CanonicalJson,
            SurfaceGenerationContractId = Contract, BedrockY = -8000, SeaLevelY = 0,
            EnableUnified3dDensityTerrain = false, EnableProceduralCaves = false,
            EnableNaturalOverhangs = false, EnableDesignedCaveDensityComposition = false
        };
        return new RingTerrainSampler(settings, true, Circumference, Circumference / Math.Tau, Width, 8400);
    }

    /// <summary>
    /// Gen-16 Certified-style smooth world: ring-integration settings resource with the Certified overrides
    /// (BedrockY -2000, sea 0, rare peak 1848, no auto-raise) and the requested seed.
    /// </summary>
    public static RingTerrainSampler CreateGen16(int seed)
    {
        var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true)
            ?? throw new InvalidOperationException("gen-16 terrain settings resource missing"));
        settings.Seed = seed;
        settings.BedrockY = -2000;
        settings.SeaLevelY = 0;
        settings.RarePeakY = 1848;
        settings.AutoRaiseRarePeakToBuildLimit = false;
        try { return new RingTerrainSampler(settings, true, Circumference, Circumference / Math.Tau, Width, 8400); }
        finally { settings.Dispose(); }
    }
}
