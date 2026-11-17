using System;
using System.Collections.Generic;
using System.Globalization;
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
    public const string SettingsPath = EmitterSiteTestWorlds.ProductionSettingsPath;

    /// <summary>Lattice used by every scan: around = floor(i * 78539.25) + 0.125 for i in 0..399, across = 9000 + j * 7500 for j in 0..39.</summary>
    public const int LatticeAround = 400;
    public const int LatticeAcross = 40;
    public static double LatticeAroundMeters(int i) => Math.Floor(i * 78539.25) + 0.125;
    public static double LatticeAcrossMeters(int j) => 9000.0 + j * 7500.0;

    /// <summary>The pinned weather-lung (v8) world at seed 1337 with climate machines off.</summary>
    public static Spec Default { get; } = new(EmitterSiteTestWorlds.Gen17WeatherLungRecipe(Seed));

    public static Spec For(int generation, IReadOnlyList<string>? review = null, int seed = Seed)
    {
        if (generation != 0) return new Spec(EmitterSiteTestWorlds.Gen17WorldRecipe(seed, generation, review));
        if (review?.Count > 0) throw new ArgumentException("The legacy world does not support review features.", nameof(review));
        return seed == Seed ? Default : new Spec(EmitterSiteTestWorlds.Gen17WeatherLungRecipe(seed));
    }

    public static Spec FromArgs(IEnumerable<string> args, int seed = Seed)
    {
        var selection = ParseGenerationArgs(args);
        int? flagSeed = ParseSeedArg(args);
        if (flagSeed.HasValue)
        {
            // T4-2: --seed=<n> for a review copy of another world (e.g. slot 3). Only v9 worlds carry an explicit seed through the launcher.
            if (selection.Generation == 0) throw new ArgumentException("--seed needs an explicit --generation=N (N >= 1).");
            seed = flagSeed.Value;
        }
        return For(selection.Generation, selection.Review, seed);
    }

    public static int? ParseSeedArg(IEnumerable<string> args)
    {
        int? seed = null;
        foreach (string arg in args)
            if (arg == "--seed" || arg.StartsWith("--seed=", StringComparison.Ordinal))
            {
                if (seed.HasValue || arg == "--seed" ||
                    !int.TryParse(arg["--seed=".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
                    throw new ArgumentException("Use one --seed=N flag with a nonnegative integer.");
                seed = parsed;
            }
        return seed;
    }

    public static (int Generation, IReadOnlyList<string> Review, bool IsExplicit) ParseGenerationArgs(IEnumerable<string> args)
    {
        int? generation = null;
        string[]? review = null;
        foreach (string arg in args)
        {
            if (arg == "--generation" || arg.StartsWith("--generation=", StringComparison.Ordinal))
            {
                if (generation.HasValue || arg == "--generation" ||
                    !int.TryParse(arg["--generation=".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
                    throw new ArgumentException("Use one --generation=N flag with a nonnegative integer.");
                generation = parsed;
            }
            else if (arg == "--review-features" || arg.StartsWith("--review-features=", StringComparison.Ordinal))
            {
                if (review != null || arg == "--review-features" || arg.Length == "--review-features=".Length)
                    throw new ArgumentException("Use one --review-features=id[,id] flag with at least one feature ID.");
                review = arg["--review-features=".Length..].Split(',');
            }
        }
        int latest = WorldGenerationLedger.Production.LatestFrozen;
        int selected = generation ?? 0;
        if (review != null && selected != latest)
            throw new ArgumentException($"Review features need --generation={latest}.");
        return (selected, review ?? Array.Empty<string>(), generation.HasValue || review != null);
    }

    public static TerrainGenerationParameters Recipe => Default.Recipe;
    public static string CanonicalJson => Default.CanonicalJson;
    public static string DefinitionHashHex => Default.DefinitionHashHex;
    public static string JsonSha256 => Default.JsonSha256;

    public static string Sha256Hex(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Production settings for the pinned world. Caller disposes.</summary>
    public static VoxelTerrainSettings CreateSettings() => Default.CreateSettings();

    /// <summary>A sampler the way the game builds it for the certified ring (31,415,936 m around, 318 km wide, 8400 m build ceiling).</summary>
    public static RingTerrainSampler CreateSampler() => Default.CreateSampler();

    /// <summary>Shaped model (emitter sites and the weather lung applied): the game's height authority. One per worker thread.</summary>
    public static TerrainGenerationHeightModel CreateShapedModel() => Default.CreateShapedModel();

    /// <summary>Natural (unshaped) model, one per worker thread.</summary>
    public static TerrainGenerationHeightModel CreateNaturalModel() => Default.CreateNaturalModel();

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

    public sealed class Spec
    {
        public TerrainGenerationParameters Recipe { get; }
        public bool IsDefault => !Recipe.IsWorldGenerationRecipe;
        public string Tag { get; }
        public string CanonicalJson => Recipe.CanonicalJson;
        public string DefinitionHashHex => "0x" + Recipe.DefinitionHash.ToString("X16");
        public string JsonSha256 => Sha256Hex(CanonicalJson);

        internal Spec(TerrainGenerationParameters recipe)
        {
            Recipe = recipe;
            Tag = IsDefault ? "" : $"g{recipe.WorldGeneration:D2}";
            if (recipe.ReviewFeatures.Count > 0)
                Tag += "-review-" + string.Join("+", recipe.ReviewFeatures);
            if (!IsDefault && recipe.Seed != Seed) Tag += "-s" + recipe.Seed;
        }

        public VoxelTerrainSettings CreateSettings() => EmitterSiteTestWorlds.CreateProductionSettings(checked((int)Recipe.Seed), Recipe);

        public RingTerrainSampler CreateSampler()
        {
            using VoxelTerrainSettings settings = CreateSettings();
            return new RingTerrainSampler(settings, true, Circumference, Circumference / Math.Tau, Width, 8400);
        }

        public TerrainGenerationHeightModel CreateShapedModel() => new(Recipe, true);
        public TerrainGenerationHeightModel CreateNaturalModel() => new(Recipe, false);

        /// <summary>Main thread only: one root sampler, then an independent copy per worker.</summary>
        public Worker[] CreateWorkers(int count)
        {
            using RingTerrainSampler root = CreateSampler();
            var workers = new Worker[Math.Max(1, count)];
            for (int i = 0; i < workers.Length; i++) workers[i] = new Worker(this, root.CreateIndependentCopy());
            return workers;
        }
    }

    /// <summary>One thread's models and samplers. Create on the main thread with <see cref="CreateWorkers"/>, then hand each worker to exactly one thread.</summary>
    public sealed class Worker : IDisposable
    {
        public readonly TerrainGenerationHeightModel Shaped;
        public readonly TerrainGenerationHeightModel Natural;
        public readonly TerrainGenerationHeightModel Gen1Shaped;
        public readonly RingTerrainSampler Sampler;
        public readonly ForestRegionSampler Forest;
        internal Worker(Spec spec, RingTerrainSampler sampler)
        {
            int seed = checked((int)spec.Recipe.Seed);
            Shaped = spec.CreateShapedModel();
            Natural = spec.CreateNaturalModel();
            Gen1Shaped = spec.IsDefault ? Shaped : For(0, seed: seed).CreateShapedModel();
            Sampler = sampler;
            Forest = new ForestRegionSampler(seed, Circumference, Width);
        }

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

        public void Dispose()
        {
            if (!ReferenceEquals(Gen1Shaped, Shaped)) Gen1Shaped.Dispose();
            Shaped.Dispose();
            Natural.Dispose();
            Sampler.Dispose();
        }
    }

    /// <summary>Main thread only: one root sampler, then an independent copy per worker.</summary>
    public static Worker[] CreateWorkers(int count) => Default.CreateWorkers(count);

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
