using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Reference worlds shared by the emitter-site baseline and registry tests. The existing Gen17 fixtures are generation 1
/// and use code-default climate settings. Use Gen17World fixtures for generation-aware tests with production settings.
/// </summary>
public static class EmitterSiteTestWorlds
{
    public const long Circumference = 31_415_936L;
    public const int Width = 318_000;
    public const string Contract = "surface_runtime_generation_v1";
    internal const string ProductionSettingsPath = "res://resources/voxel/voxel_terrain_settings_ring_integration.tres";

    /// <summary>Gen-17 v4 (ringworld_surface_erosion_v4) default recipe with the given seed.</summary>
    public static TerrainGenerationParameters Gen17Recipe(int seed)
        => TerrainGenerationParameters.FromJson(
            TerrainGenerationParameters.ErosionDefault().CanonicalJson.Replace("\"seed\":1337", "\"seed\":" + seed));

    /// <summary>Gen-17 v5 (ringworld_surface_sites_v5) default recipe with the given seed.</summary>
    public static TerrainGenerationParameters Gen17SitesRecipe(int seed)
        => TerrainGenerationParameters.FromJson(
            TerrainGenerationParameters.SitesDefault().CanonicalJson.Replace("\"seed\":1337", "\"seed\":" + seed));

    public static RingTerrainSampler CreateGen17(int seed) => CreateGen17(Gen17Recipe(seed), seed);

    public static RingTerrainSampler CreateGen17Sites(int seed) => CreateGen17(Gen17SitesRecipe(seed), seed);

    /// <summary>Gen-17 v6 (ringworld_surface_climate_v6) default recipe with the given seed.</summary>
    public static TerrainGenerationParameters Gen17ClimateRecipe(int seed)
        => TerrainGenerationParameters.FromJson(
            TerrainGenerationParameters.ClimateDefault().CanonicalJson.Replace("\"seed\":1337", "\"seed\":" + seed));

    public static RingTerrainSampler CreateGen17Climate(int seed) => CreateGen17(Gen17ClimateRecipe(seed), seed);

    /// <summary>Gen-17 v7 (ringworld_surface_landform_v7) default recipe with the given seed; <paramref name="climate"/> sets climate_machines.</summary>
    public static TerrainGenerationParameters Gen17LandformRecipe(int seed, bool climate = false)
        => TerrainGenerationParameters.FromJson(
            TerrainGenerationParameters.LandformDefault().CanonicalJson.Replace("\"seed\":1337", "\"seed\":" + seed)
                .Replace("\"climate_machines\":0", "\"climate_machines\":" + (climate ? 1 : 0)));

    public static RingTerrainSampler CreateGen17Landform(int seed, bool climate = false) => CreateGen17(Gen17LandformRecipe(seed, climate), seed);

    /// <summary>Gen-17 v8 (ringworld_surface_weather_lung_v8) default recipe with the given seed; <paramref name="climate"/> sets climate_machines.</summary>
    public static TerrainGenerationParameters Gen17WeatherLungRecipe(int seed, bool climate = false)
        => TerrainGenerationParameters.FromJson(
            TerrainGenerationParameters.WeatherLungDefault().CanonicalJson.Replace("\"seed\":1337", "\"seed\":" + seed)
                .Replace("\"climate_machines\":0", "\"climate_machines\":" + (climate ? 1 : 0)));

    public static RingTerrainSampler CreateGen17WeatherLung(int seed, bool climate = false) => CreateGen17(Gen17WeatherLungRecipe(seed, climate), seed);

    public static TerrainGenerationParameters Gen17WorldRecipe(int seed, int generation, IReadOnlyList<string>? review = null, bool climate = false)
        => TerrainGenerationParameters.WorldDefault(generation, seed, review, climate);

    public static RingTerrainSampler CreateGen17World(int seed, int generation, IReadOnlyList<string>? review = null, bool climate = false)
    {
        using VoxelTerrainSettings settings = CreateProductionSettings(seed, Gen17WorldRecipe(seed, generation, review, climate));
        return new RingTerrainSampler(settings, true, Circumference, Circumference / Math.Tau, Width, 8400);
    }

    /// <summary>
    /// Production settings resource plus the overrides scripts/world_size_launcher.gd applies for a certified runtime-generation world.
    /// Caller disposes. Mirrored overrides (launcher line numbers as of 2026-11-09):
    ///   _build_runtime_terrain_settings (1951-2000): Seed (1971), cave ContractVersion (1981), BedrockY (1982), SeaLevelY (1983), BaseSurfaceHeightVoxels (1984),
    ///     HeightAmplitudeVoxels (1985), DetailAmplitudeVoxels (1986), ContinentalContrast (1987), RegionalDetailStrength (1988), EnableUnified3dDensityTerrain (1989),
    ///     EnableDesignedCaveDensityComposition (1990), EnableProceduralCaves (1992), RarePeakY (1997), AutoRaiseRarePeakToBuildLimit (1998).
    ///   _configure_game (1622-1632): BedrockY -8000 (1625), SeaLevelY 0 (1626), SurfaceGenerationRecipeJson (1627), SurfaceGenerationContractId (1628),
    ///     EnableProceduralCaves (1629), EnableDesignedCaveDensityComposition (1630), EnableUnified3dDensityTerrain (1631), EnableNaturalOverhangs (1632).
    /// The certified preset (resources/launcher/certified_ring_config.tres) supplies height_amplitude 180, detail_amplitude 30, rare_peak_height 5200; every other key falls back to the scripts/launcher_default_config.gd defaults (base surface 16, continental contrast 1.35 which differs from the resource's 1.45, regional detail 0.65, cave generator version 1). Found by the in-game 64-point check.
    /// </summary>
    public static VoxelTerrainSettings CreateProductionSettings(int seed, TerrainGenerationParameters recipe)
    {
        var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>(ProductionSettingsPath)?.Duplicate(true)
            ?? throw new InvalidOperationException("production terrain settings resource missing: " + ProductionSettingsPath));
        settings.Seed = seed;
        if (settings.DesignedCaveGenerationContractSettings != null) settings.DesignedCaveGenerationContractSettings.ContractVersion = 1;
        settings.BaseSurfaceHeightVoxels = 16;
        settings.HeightAmplitudeVoxels = 180;
        settings.DetailAmplitudeVoxels = 30;
        settings.ContinentalContrast = 1.35f;
        settings.RegionalDetailStrength = 0.65f;
        settings.RarePeakY = 5200;
        settings.AutoRaiseRarePeakToBuildLimit = false;
        settings.BedrockY = -8000;
        settings.SeaLevelY = 0;
        settings.SurfaceGenerationRecipeJson = recipe.CanonicalJson;
        settings.SurfaceGenerationContractId = Contract;
        settings.EnableProceduralCaves = false;
        settings.EnableDesignedCaveDensityComposition = false;
        settings.EnableUnified3dDensityTerrain = false;
        settings.EnableNaturalOverhangs = false;
        return settings;
    }

    static RingTerrainSampler CreateGen17(TerrainGenerationParameters recipe, int seed)
    {
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
