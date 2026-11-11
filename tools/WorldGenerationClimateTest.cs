using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Generation.Gpu;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>Checks v9 climate authority and generation 1 parity. Run: tools/run_tests.ps1 -Tests world_generation_climate_test.</summary>
public partial class WorldGenerationClimateTest : RefCounted
{
    private static readonly (string Key, string Property, double Tuned)[] Scalars =
    {
        ("continental_frequency", nameof(VoxelTerrainSettings.ContinentalFrequency), 7e-6),
        ("erosion_frequency", nameof(VoxelTerrainSettings.ErosionFrequency), 2.3e-5),
        ("ridge_frequency", nameof(VoxelTerrainSettings.RidgeFrequency), 9e-6),
        ("temperature_frequency", nameof(VoxelTerrainSettings.TemperatureFrequency), 3e-6),
        ("moisture_frequency", nameof(VoxelTerrainSettings.MoistureFrequency), 1.5e-5),
        ("mountain_warp_frequency", nameof(VoxelTerrainSettings.MountainWarpFrequency), 9e-6),
        ("valley_frequency", nameof(VoxelTerrainSettings.ValleyFrequency), 3.2e-5),
        ("continental_secondary_strength", nameof(VoxelTerrainSettings.ContinentalSecondaryStrength), 0.4),
        ("continental_contrast", nameof(VoxelTerrainSettings.ContinentalContrast), 1.5),
        ("continental_ocean_bias", nameof(VoxelTerrainSettings.ContinentalOceanBias), 0.1),
        ("mountain_warp_distance_m", nameof(VoxelTerrainSettings.MountainWarpDistanceMeters), 14000),
        ("peak_rarity_exponent", nameof(VoxelTerrainSettings.PeakRarityExponent), 19),
        ("mountain_belt_broadness", nameof(VoxelTerrainSettings.MountainBeltBroadness), 0.6),
        ("valley_narrowness", nameof(VoxelTerrainSettings.ValleyNarrowness), 6.5),
        ("ocean_moisture_influence", nameof(VoxelTerrainSettings.OceanMoistureInfluence), 0.4),
        ("climate_latitude_influence", nameof(VoxelTerrainSettings.ClimateLatitudeInfluence), 0.65),
        ("coastal_beach_strength", nameof(VoxelTerrainSettings.CoastalBeachStrength), 0.9),
        ("altitude_temperature_drop_per_kilometre", nameof(VoxelTerrainSettings.AltitudeTemperatureDropPerKilometre), 0.12),
        ("mountain_rain_shadow_strength", nameof(VoxelTerrainSettings.MountainRainShadowStrength), 0.3),
        ("macro_region_size_m", nameof(VoxelTerrainSettings.MacroRegionSizeMeters), 180000)
    };

    private static readonly FieldInfo SettingsField = typeof(RingTerrainSampler).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Sampler private settings are unavailable.");

    public int Run()
    {
        try
        {
            CheckGenerationOne();
            CheckTunedScalars();
            CheckTestWorlds();
            Console.WriteLine("WORLD_GENERATION_CLIMATE=PASS");
            return 0;
        }
        catch (Exception exception)
        {
            GD.PrintErr("WORLD_GENERATION_CLIMATE=FAIL " + exception);
            return 1;
        }
    }

    private static void CheckGenerationOne()
    {
        var recipe = TerrainGenerationParameters.WorldDefault(1, LandscapeWorld.Seed);
        using var productionV8 = LandscapeWorld.CreateSampler();
        using var productionSettings = LandscapeWorld.CreateSettings();
        productionSettings.SurfaceGenerationRecipeJson = recipe.CanonicalJson;
        using var productionV9 = CreateSampler(productionSettings);
        using var codeDefaults = CreateCodeDefaultSettings(recipe);
        uint[] callerClimate = ClimateBits(codeDefaults);
        using var codeDefaultV9 = CreateSampler(codeDefaults);
        using var worker = codeDefaultV9.CreateIndependentCopy();

        CheckEffective(productionV9, recipe, "v9 production");
        CheckEffective(codeDefaultV9, recipe, "v9 code defaults");
        CheckEffective(worker, recipe, "v9 independent worker");
        Require(callerClimate.SequenceEqual(ClimateBits(codeDefaults)), "sampler changed the caller's climate settings");
        Require(!ReferenceEquals(codeDefaults, EffectiveSettings(codeDefaultV9)), "root sampler did not freeze a private settings copy");
        Require(ReferenceEquals(EffectiveSettings(codeDefaultV9), EffectiveSettings(worker)), "worker duplicated the effective settings resource");
        Require(worker.TerrainDefinitionHash == codeDefaultV9.TerrainDefinitionHash, "independent worker terrain hash differs");

        var moistureRecipe = recipe.WithFields(new Dictionary<string, double> { ["moisture_frequency"] = 1.5e-5 });
        using var moistureSettings = LandscapeWorld.CreateSettings();
        moistureSettings.SurfaceGenerationRecipeJson = moistureRecipe.CanonicalJson;
        using var moistureSampler = CreateSampler(moistureSettings);
        Require(moistureSampler.TerrainDefinitionHash != productionV9.TerrainDefinitionHash,
            "changing only recipe moisture frequency preserved the terrain hash");
        int count = 0;
        bool moistureChanged = false;
        foreach (var (around, across) in WorldGenerationFingerprint.HeightPointsSequence())
        {
            long a = (long)Math.Floor(around);
            int z = (int)Math.Floor(across);
            TerrainSample expected = productionV8.GetTerrainSample(a, z);
            TerrainSample actual = productionV9.GetTerrainSample(a, z);
            Require(expected.SurfaceHeight == actual.SurfaceHeight && expected.WaterKind == actual.WaterKind
                && expected.WaterSurfaceHeight == actual.WaterSurfaceHeight && expected.Biome == actual.Biome
                && expected.Region == actual.Region && SameClimate(expected, actual),
                $"production v8/v9 terrain samples differ at fingerprint point {count} ({a}, {z})");
            TerrainSample codeDefault = codeDefaultV9.GetTerrainSample(a, z);
            Require(SameClimate(actual, codeDefault), $"raw climate differs with code-default settings at point {count} ({a}, {z})");
            Require(SameClimate(codeDefault, worker.GetTerrainSample(a, z)), $"independent worker climate differs at point {count} ({a}, {z})");
            if (!moistureChanged)
                moistureChanged = Bits(actual.Moisture) != Bits(moistureSampler.GetTerrainSample(a, z).Moisture);
            count++;
        }
        Require(count == 4096, $"fingerprint point count changed to {count}");
        Require(moistureChanged, "changing only recipe moisture frequency did not affect sampled moisture");
        CheckGpuPacks(productionV8, productionV9, codeDefaultV9, worker);
    }

    private static void CheckTunedScalars()
    {
        var recipe = TerrainGenerationParameters.WorldDefault(1, LandscapeWorld.Seed)
            .WithFields(Scalars.ToDictionary(scalar => scalar.Key, scalar => scalar.Tuned, StringComparer.Ordinal));
        using var caller = CreateCodeDefaultSettings(recipe);
        uint[] callerClimate = ClimateBits(caller);
        using var sampler = CreateSampler(caller);
        using var worker = sampler.CreateIndependentCopy();
        CheckEffective(sampler, recipe, "tuned v9");
        CheckEffective(worker, recipe, "tuned v9 worker");
        Require(callerClimate.SequenceEqual(ClimateBits(caller)), "tuned recipe changed the caller's climate settings");
        Require(Bits(worker.CreateGpuClimateSettings()!.AltitudeTemperatureDropPerKilometre)
            == Bits((float)recipe.AltitudeTemperatureDropPerKilometre), "worker lost the tuned runtime altitude lapse");
        Require(sampler.GetMacroRegion(200000, 200000).CenterAroundVoxel == 270000,
            "runtime macro-region sampling did not use the tuned region size");
    }

    private static VoxelTerrainSettings CreateCodeDefaultSettings(TerrainGenerationParameters recipe) => new()
    {
        Seed = checked((int)recipe.Seed),
        SurfaceGenerationRecipeJson = recipe.CanonicalJson,
        SurfaceGenerationContractId = RingTerrainSampler.RuntimeSurfaceGenerationContractId,
        BedrockY = -8000, SeaLevelY = 0,
        EnableUnified3dDensityTerrain = false, EnableProceduralCaves = false,
        EnableNaturalOverhangs = false, EnableDesignedCaveDensityComposition = false
    };

    private static RingTerrainSampler CreateSampler(VoxelTerrainSettings settings)
        => new(settings, true, LandscapeWorld.Circumference, LandscapeWorld.Circumference / Math.Tau, LandscapeWorld.Width, 8400);

    private static VoxelTerrainSettings EffectiveSettings(RingTerrainSampler sampler) => (VoxelTerrainSettings)SettingsField.GetValue(sampler)!;

    private static uint[] ClimateBits(VoxelTerrainSettings settings)
        => Scalars.Select(scalar => ScalarBits(typeof(VoxelTerrainSettings).GetProperty(scalar.Property)!.GetValue(settings)!)).ToArray();

    private static void CheckEffective(RingTerrainSampler sampler, TerrainGenerationParameters recipe, string label)
    {
        uint[] actual = ClimateBits(EffectiveSettings(sampler));
        for (int i = 0; i < Scalars.Length; i++)
        {
            string name = Scalars[i].Property;
            uint expected = ScalarBits(typeof(TerrainGenerationParameters).GetProperty(name)!.GetValue(recipe)!);
            Require(actual[i] == expected, $"{label} {name} differs from recipe bits ({actual[i]:x8} != {expected:x8})");
        }
    }

    private static uint ScalarBits(object value) => value switch
    {
        float single => Bits(single),
        double real => Bits((float)real),
        int integer => unchecked((uint)integer),
        _ => throw new InvalidOperationException("Unsupported climate scalar type.")
    };

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private static bool SameClimate(TerrainSample a, TerrainSample b)
        => Bits(a.Temperature) == Bits(b.Temperature) && Bits(a.Moisture) == Bits(b.Moisture) && Bits(a.Erosion) == Bits(b.Erosion);

    private static void CheckGpuPacks(RingTerrainSampler v8, RingTerrainSampler v9, RingTerrainSampler codeDefault, RingTerrainSampler worker)
    {
        const string path = "res://materials/terrain_gpu/";
        var layout = ProxyGpuHeightKernel.ParseLayout(Godot.FileAccess.GetFileAsString(path + "ring_height_model.glslinc") + "\n"
            + Godot.FileAccess.GetFileAsString(path + "ring_regional_climate.glslinc"));
        uint[] Pack(RingTerrainSampler sampler)
            => ProxyGpuHeightKernel.PackRecipe(sampler.RuntimeSurfaceRecipe!, sampler.CreateGpuClimateSettings()!, layout);
        uint[] expected = Pack(v8);
        uint[] actual = Pack(v9);
        uint[] alternate = Pack(codeDefault);
        uint[] workerPack = Pack(worker);
        int profileStart = layout["RC_P_PROFILE_COUNT"], noiseStart = layout["RC_P_NOISE"];
        for (int i = layout["RC_P_K"]; i < layout["RC_P_COUNT"]; i++)
        {
            bool isProfile = i >= profileStart && i < noiseStart;
            Require(expected[i] == actual[i], $"v8/v9 GPU {(isProfile ? "profile" : "scalar")} word {i} differs");
            if (!isProfile)
                Require(expected[i] == alternate[i] && expected[i] == workerPack[i], $"code-default or worker GPU scalar word {i} differs");
        }
    }

    private static void CheckTestWorlds()
    {
        using var legacySettings = CreatePinnedSettingsBeforeRefactor();
        using var legacyProduction = CreateSampler(legacySettings);
        using var production = LandscapeWorld.CreateSampler();
        Require(Bits(EffectiveSettings(production).ContinentalContrast) == Bits(legacySettings.ContinentalContrast),
            "pinned v8 sampler lost the certified ContinentalContrast override");
        Require(legacyProduction.TerrainDefinitionHash == production.TerrainDefinitionHash,
            $"pinned v8 sampler hash changed from {legacyProduction.TerrainDefinitionHash:X16} to {production.TerrainDefinitionHash:X16}");

        using var legacyHeight = EmitterSiteTestWorlds.CreateGen17WeatherLung(LandscapeWorld.Seed);
        using var world = EmitterSiteTestWorlds.CreateGen17World(LandscapeWorld.Seed, 1);
        var spec = LandscapeWorld.For(1);
        Require(ReferenceEquals(LandscapeWorld.For(0), LandscapeWorld.Default), "legacy selection did not return the pinned spec");
        Require(!spec.IsDefault && spec.Tag == "g01" && spec.CanonicalJson != LandscapeWorld.CanonicalJson,
            "explicit generation 1 did not select a distinct v9 recipe and tag");

        using var bridge = new LandscapeWorldBridge();
        bridge.Configure(1, "");
        Require(bridge.CanonicalRecipeJson() == spec.CanonicalJson && bridge.DefinitionHashHex() == spec.DefinitionHashHex,
            "bridge did not select the v9 recipe");
        bridge.Configure(0, "");
        Require(bridge.CanonicalRecipeJson() == LandscapeWorld.CanonicalJson && bridge.DefinitionHashHex() == LandscapeWorld.DefinitionHashHex,
            "bridge did not restore the pinned v8 recipe");

        using var worker = spec.CreateWorkers(1)[0];
        Require(worker.Sampler.RuntimeSurfaceRecipe?.CanonicalJson == spec.CanonicalJson, "worker sampler did not use the selected spec");
        Require(!ReferenceEquals(worker.Shaped, worker.Gen1Shaped), "v9 worker lacks its own pinned v8 classification model");
        using var pinnedShaped = LandscapeWorld.Default.CreateShapedModel();
        int count = 0;
        foreach (var (around, across) in WorldGenerationFingerprint.HeightPointsSequence())
        {
            long a = (long)Math.Floor(around);
            int z = (int)Math.Floor(across);
            TerrainSample expected = legacyHeight.GetTerrainSample(a, z);
            TerrainSample actual = world.GetTerrainSample(a, z);
            Require(expected.SurfaceHeight == actual.SurfaceHeight, $"test-world height differs at fingerprint point {count}");
            Require(SameClimate(production.GetTerrainSample(a, z), actual), $"test-world production climate differs at fingerprint point {count}");
            long pinned = BitConverter.DoubleToInt64Bits(pinnedShaped.CalculateHeight(around, across));
            Require(pinned == BitConverter.DoubleToInt64Bits(worker.Shaped.CalculateHeight(around, across))
                && pinned == BitConverter.DoubleToInt64Bits(worker.Gen1Shaped.CalculateHeight(around, across)),
                $"landscape spec shaped height differs at fingerprint point {count}");
            count++;
        }
        Require(count == 4096, $"test-world fingerprint point count changed to {count}");
    }

    // Snapshot of the old LandscapeWorld.CreateSettings body for the full v8 sampler hash check.
    private static VoxelTerrainSettings CreatePinnedSettingsBeforeRefactor()
    {
        var settings = (VoxelTerrainSettings)GD.Load<VoxelTerrainSettings>(LandscapeWorld.SettingsPath).Duplicate(true);
        settings.Seed = LandscapeWorld.Seed;
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
        settings.SurfaceGenerationRecipeJson = EmitterSiteTestWorlds.Gen17WeatherLungRecipe(LandscapeWorld.Seed).CanonicalJson;
        settings.SurfaceGenerationContractId = EmitterSiteTestWorlds.Contract;
        settings.EnableProceduralCaves = false;
        settings.EnableDesignedCaveDensityComposition = false;
        settings.EnableUnified3dDensityTerrain = false;
        settings.EnableNaturalOverhangs = false;
        return settings;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
