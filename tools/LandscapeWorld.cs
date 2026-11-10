using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>Landscape classes of the generation 1 world (docs/landscape-audit-t0-spec.md section 2.3). The first matching rule wins.</summary>
public enum LandscapeClass { Site, Ocean, Mountain, CoastalBelt, Breaks, PlateauInterior, HillCountry, Plains, Rolling }

/// <summary>
/// The pinned generation 1 world every landscape (T0) and vastness (V0) tool measures: the v8 weather-lung recipe, seed 1337, the production
/// terrain settings resource with the overrides the game launcher applies to a certified runtime-generation world (docs/landscape-audit-t0-spec.md 2.2).
/// Settings and samplers must be created on the main thread (Godot resources); the models, samplers and workers it hands out are then confined to one thread each.
/// </summary>
public static class LandscapeWorld
{
    public const int Seed = 1337;
    public const long Circumference = EmitterSiteTestWorlds.Circumference;
    public const int Width = EmitterSiteTestWorlds.Width;
    public const string GeneratorId = TerrainGenerationParameters.WeatherLungGeneratorIdentity;
    public const string SettingsPath = "res://resources/voxel/voxel_terrain_settings_ring_integration.tres";

    /// <summary>Lattice used by every scan: around = floor(i * 78539.25) + 0.125 for i in 0..399, across = 9000 + j * 7500 for j in 0..39.</summary>
    public const int LatticeAround = 400;
    public const int LatticeAcross = 40;
    public static double LatticeAroundMeters(int i) => Math.Floor(i * 78539.25) + 0.125;
    public static double LatticeAcrossMeters(int j) => 9000.0 + j * 7500.0;

    static TerrainGenerationParameters? _recipe;

    /// <summary>The default weather-lung (v8) recipe at seed 1337 with climate_machines off, exactly what the game's new-world path produces for slot_1.</summary>
    public static TerrainGenerationParameters Recipe => _recipe ??= EmitterSiteTestWorlds.Gen17WeatherLungRecipe(Seed, climate: false);
    public static string CanonicalJson => Recipe.CanonicalJson;
    public static string DefinitionHashHex => "0x" + Recipe.DefinitionHash.ToString("X16");
    public static string JsonSha256 => Sha256Hex(CanonicalJson);

    public static string Sha256Hex(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
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
    public static VoxelTerrainSettings CreateSettings()
    {
        var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>(SettingsPath)?.Duplicate(true)
            ?? throw new InvalidOperationException("production terrain settings resource missing: " + SettingsPath));
        settings.Seed = Seed;
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
        settings.SurfaceGenerationRecipeJson = CanonicalJson;
        settings.SurfaceGenerationContractId = EmitterSiteTestWorlds.Contract;
        settings.EnableProceduralCaves = false;
        settings.EnableDesignedCaveDensityComposition = false;
        settings.EnableUnified3dDensityTerrain = false;
        settings.EnableNaturalOverhangs = false;
        return settings;
    }

    /// <summary>A sampler the way the game builds it for the certified ring (31,415,936 m around, 318 km wide, 8400 m build ceiling).</summary>
    public static RingTerrainSampler CreateSampler()
    {
        using VoxelTerrainSettings settings = CreateSettings();
        return new RingTerrainSampler(settings, true, Circumference, Circumference / Math.Tau, Width, 8400);
    }

    /// <summary>Shaped model (emitter sites and the weather lung applied): the game's height authority. One per worker thread.</summary>
    public static TerrainGenerationHeightModel CreateShapedModel() => new(Recipe, true);

    /// <summary>Natural (unshaped) model, one per worker thread.</summary>
    public static TerrainGenerationHeightModel CreateNaturalModel() => new(Recipe, false);

    /// <summary>The natural height rebuilt from shaped diagnostics (the shaped call replaces only Height, so Macro and Detail are the natural ones).</summary>
    public static double NaturalHeight(in TerrainGenerationHeightModel.Diagnostics d)
        => Math.Clamp(d.MacroHeight + d.DetailHeight, Recipe.MinHeightM, Recipe.MaxHeightM);

    /// <summary>Spec 2.3: the first matching rule wins. Takes shaped-model diagnostics.</summary>
    public static LandscapeClass Classify(in TerrainGenerationHeightModel.Diagnostics d)
    {
        if (d.SiteWeight > 0d || d.WeatherLungWeight > 0d) return LandscapeClass.Site;
        if (d.Height < 0d) return LandscapeClass.Ocean;
        if (d.AlpineWeight >= 0.02d) return LandscapeClass.Mountain;
        if (d.LandformGate < 0.999d) return LandscapeClass.CoastalBelt;
        if (d.PlateauWeight >= 0.5d && d.Ruggedness >= 0.7d) return LandscapeClass.Breaks;
        if (d.PlateauWeight >= 0.8d && d.Ruggedness < 0.3d) return LandscapeClass.PlateauInterior;
        if (d.Ruggedness >= 1d) return LandscapeClass.HillCountry;
        if (d.Ruggedness == 0d) return LandscapeClass.Plains;
        return LandscapeClass.Rolling;
    }

    /// <summary>One thread's models and samplers. Create on the main thread with <see cref="CreateWorkers"/>, then hand each worker to exactly one thread.</summary>
    public sealed class Worker : IDisposable
    {
        public readonly TerrainGenerationHeightModel Shaped = CreateShapedModel();
        public readonly TerrainGenerationHeightModel Natural = CreateNaturalModel();
        public readonly RingTerrainSampler Sampler;
        public readonly ForestRegionSampler Forest = new(Seed, Circumference, Width);
        internal Worker(RingTerrainSampler sampler) { Sampler = sampler; }

        public TerrainGenerationHeightModel.Diagnostics Diag(double around, double across) => Shaped.SampleDiagnostics(around, across);
        public double Ground(double around, double across) => Shaped.CalculateHeight(around, across);
        public LandscapeClass Class(double around, double across) => Classify(Diag(around, across));

        /// <summary>True when SiteWeight and WeatherLungWeight are zero at the point and at 8 points on a ring of the given radius.</summary>
        public bool SiteFree(double around, double across, double ringMeters)
        {
            for (int k = -1; k < 8; k++)
            {
                double a = around, z = across;
                if (k >= 0) { double angle = k * Math.PI / 4d; a += ringMeters * Math.Cos(angle); z += ringMeters * Math.Sin(angle); }
                TerrainGenerationHeightModel.Diagnostics d = Diag(a, z);
                if (d.SiteWeight > 0d || d.WeatherLungWeight > 0d) return false;
            }
            return true;
        }

        /// <summary>True when the class is <paramref name="expected"/> at the point and at 8 points on a ring.</summary>
        public bool ClassRing(double around, double across, LandscapeClass expected, double ringMeters, int needed = 8)
        {
            if (Class(around, across) != expected) return false;
            int matches = 0;
            for (int k = 0; k < 8; k++)
            {
                double angle = k * Math.PI / 4d;
                if (Class(around + ringMeters * Math.Cos(angle), across + ringMeters * Math.Sin(angle)) == expected) matches++;
            }
            return matches >= needed;
        }

        /// <summary>Production forest habitat at a metre coordinate: the production TerrainSample plus the +-2 m presentation slope the tree builder uses.</summary>
        public ForestHabitat Habitat(double around, double across) => ForestSample(around, across).Habitat;

        public ForestRegionSample ForestSample(double around, double across)
        {
            long a = (long)Math.Floor(around);
            int z = (int)Math.Floor(across);
            TerrainSample terrain = Sampler.GetTerrainSample(a, z);
            float riseX = Sampler.GetPresentationSurfaceHeight(a + 2, z) - Sampler.GetPresentationSurfaceHeight(a - 2, z);
            float riseZ = Sampler.GetPresentationSurfaceHeight(a, z + 2) - Sampler.GetPresentationSurfaceHeight(a, z - 2);
            float slope = Mathf.RadToDeg(Mathf.Atan(Mathf.Sqrt(riseX * riseX + riseZ * riseZ) / 4.0f));
            return Forest.Sample(a, z, terrain, slope);
        }

        public void Dispose() { Shaped.Dispose(); Natural.Dispose(); Sampler.Dispose(); }
    }

    /// <summary>Main thread only: one root sampler, then an independent copy per worker.</summary>
    public static Worker[] CreateWorkers(int count)
    {
        using RingTerrainSampler root = CreateSampler();
        var workers = new Worker[Math.Max(1, count)];
        for (int i = 0; i < workers.Length; i++) workers[i] = new Worker(root.CreateIndependentCopy());
        return workers;
    }

    public static int DefaultWorkerCount => Math.Clamp(System.Environment.ProcessorCount, 2, 16);

    /// <summary>The lattice in scan order (outer i, inner j).</summary>
    public static IEnumerable<(int I, int J, double Around, double Across)> Lattice()
    {
        for (int i = 0; i < LatticeAround; i++)
            for (int j = 0; j < LatticeAcross; j++)
                yield return (i, j, LatticeAroundMeters(i), LatticeAcrossMeters(j));
    }

    public static string Num(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
