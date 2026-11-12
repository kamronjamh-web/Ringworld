using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>Checks frozen and review feature reachability. Run: tools/run_tests.ps1 -Tests world_generation_contract_test.</summary>
public partial class WorldGenerationContractTest : RefCounted
{
    private const int Seed = 1337;
    private static readonly string[] NoReview = Array.Empty<string>();

    public int Run()
    {
        try
        {
            WorldGenerationLedger production = WorldGenerationLedger.Production;
            CheckLedger(production, synthetic: false);
            CheckLegacy();
            CheckGenerationArgs(production.LatestFrozen);

            var synthetic = WorldGenerationLedger.CreateForTests(
                new WorldGenerationLedger.Row[]
                {
                    new(1, "baseline", NoReview, "test"),
                    new(2, "water", new[] { "test.rivers", "test.current" }, "test"),
                    new(3, "relief", new[] { "test.relief" }, "test")
                },
                new WorldGenerationLedger.FeatureInfo[]
                {
                    new("test.rivers", 0, "test", 2),
                    new("test.current", 13, "test", 2),
                    new("test.relief", 30, "test", 3),
                    new("test.open", 6, "test", 0)
                });
            CheckLedger(synthetic, synthetic: true);

            var rows = WorldGenerationFingerprintTest.ReadRows(ProjectSettings.GlobalizePath(WorldGenerationFingerprintTest.GoldenPath));
            foreach (var row in production.Rows)
                Require(rows.ContainsKey(row.Generation), $"fingerprint golden missing generation {row.Generation}");

            Console.WriteLine("WORLD_GENERATION_CONTRACT=PASS");
            return 0;
        }
        catch (Exception exception)
        {
            GD.PrintErr("WORLD_GENERATION_CONTRACT=FAIL " + exception);
            return 1;
        }
        finally { WorldGeneration.PublishLegacy("contract-test-cleanup"); }
    }

    private static void CheckLedger(WorldGenerationLedger ledger, bool synthetic)
    {
        Require(ledger.Rows.Select(row => row.Generation).SequenceEqual(Enumerable.Range(1, ledger.LatestFrozen)),
            "ledger generations are not contiguous");
        Require(ledger.Rows[0].Features.Count == 0 && ledger.Features.All(feature => feature.FrozenIn != 1),
            "generation 1 has features");
        Require(ledger.RetirementFloor >= 1 && ledger.RetirementFloor <= ledger.LatestFrozen, "retirement floor is outside the ledger");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var bits = new HashSet<int>();
        foreach (var feature in ledger.Features)
        {
            Require(Regex.IsMatch(feature.Id, @"\A[a-z][a-z0-9_.]*\z") && ids.Add(feature.Id), $"invalid or duplicate feature {feature.Id}");
            Require(feature.Bit is >= 0 and <= 30 && bits.Add(feature.Bit), $"invalid or duplicate bit {feature.Bit}");
            Require(feature.FrozenIn == 0 || ledger.Rows.Any(row => row.Generation == feature.FrozenIn && row.Features.Contains(feature.Id)),
                $"invalid freeze row for {feature.Id}");
        }

        var schema = synthetic ? new WorldRecipeSchema(ledger, Array.Empty<RecipeFieldSpec>()) : WorldRecipeSchema.Production;
        foreach (var row in ledger.Rows)
        {
            int generation = row.Generation;
            int expected = 0;
            foreach (var feature in ledger.Features)
                if (IsFrozenAt(feature, generation)) expected |= 1 << feature.Bit;
            Require(ledger.MaskFor(generation, NoReview) == expected, $"incorrect mask for generation {generation}");
            foreach (var feature in ledger.Features)
                Require(ledger.Has(expected, feature.Id) == IsFrozenAt(feature, generation), $"incorrect mask membership for {feature.Id} at generation {generation}");
            if (!ledger.IsLoadable(generation))
            {
                Require(!ledger.Validate(generation, NoReview, out _), $"retired generation {generation} was accepted");
                continue;
            }
            var recipe = TerrainGenerationParameters.WorldDefault(generation, Seed, schema: schema);
            var parsed = TerrainGenerationParameters.FromJson(recipe.CanonicalJson, schema);
            Require(parsed.CanonicalJson == recipe.CanonicalJson && parsed.WorldGeneration == generation && ledger.Validate(parsed, out _),
                $"generation {generation} recipe did not round trip");
            if (synthetic)
                Require(WorldGeneration.PublishForTests(generation, ledger.MaskFor(parsed.WorldGeneration, parsed.ReviewFeatures)),
                    $"synthetic generation {generation} publication failed");
            else
            {
                using RingTerrainSampler sampler = CreateWorld(generation, null);
                CheckSampler(sampler, generation, expected);
                Require(WorldGeneration.Publish(parsed, "contract-test"), $"generation {generation} publication failed");
            }
            Require(WorldGeneration.Current.Generation == generation && WorldGeneration.Current.FeatureMask == expected,
                $"incorrect published state for generation {generation}");
            foreach (var feature in ledger.Features)
            {
                bool active = IsFrozenAt(feature, generation);
                bool published = synthetic ? ledger.Has(WorldGeneration.Current.FeatureMask, feature.Id) : WorldGeneration.Has(feature.Id);
                Require(parsed.HasWorldFeature(feature.Id) == active && published == active,
                    $"incorrect feature reachability for {feature.Id} at generation {generation}");
            }
        }

        int latest = ledger.LatestFrozen;
        int baseMask = ledger.MaskFor(latest, NoReview);
        foreach (var feature in ledger.Features)
        {
            string[] review = { feature.Id };
            if (feature.FrozenIn != 0)
            {
                Require(!ledger.Validate(latest, review, out _), $"frozen feature {feature.Id} accepted for review");
                continue;
            }
            int expected = baseMask | (1 << feature.Bit);
            Require(ledger.Validate(latest, review, out _) && ledger.MaskFor(latest, review) == expected,
                $"review feature {feature.Id} did not add exactly its bit");
            var recipe = TerrainGenerationParameters.WorldDefault(latest, Seed, review, schema: schema);
            var parsed = TerrainGenerationParameters.FromJson(recipe.CanonicalJson, schema);
            Require(parsed.ReviewFeatures.SequenceEqual(review) && ledger.Validate(parsed, out _),
                $"review feature {feature.Id} did not survive recipe round trip");
            bool published = synthetic
                ? WorldGeneration.PublishForTests(latest, ledger.MaskFor(parsed.WorldGeneration, parsed.ReviewFeatures))
                : WorldGeneration.Publish(parsed, "contract-review");
            Require(published && WorldGeneration.Current.Generation == latest && WorldGeneration.Current.FeatureMask == expected,
                $"incorrect published review mask for {feature.Id}");
            foreach (var candidate in ledger.Features)
            {
                bool active = IsFrozenAt(candidate, latest) || candidate.Id == feature.Id;
                bool reachable = synthetic
                    ? ledger.Has(WorldGeneration.Current.FeatureMask, candidate.Id)
                    : WorldGeneration.Has(candidate.Id);
                Require(parsed.HasWorldFeature(candidate.Id) == active && reachable == active,
                    $"incorrect reachability of {candidate.Id} while reviewing {feature.Id}");
            }
            if (!synthetic)
            {
                using RingTerrainSampler sampler = CreateWorld(latest, review);
                CheckSampler(sampler, latest, expected);
                Require(WorldGeneration.Current.ReviewFeatures.SequenceEqual(review), $"publication lost review feature {feature.Id}");
            }
        }
        Require(!ledger.Validate(latest, new[] { "unknown.contract_test" }, out _), "unknown review ID accepted");
    }

    /// <summary>A runtime sampler of the world recipe; a water.rivers recipe needs its graph resident first (T2R-9), so a strip graph is installed.</summary>
    private static RingTerrainSampler CreateWorld(int generation, IReadOnlyList<string>? review)
    {
        TerrainGenerationParameters recipe = EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, generation, review);
        using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, recipe);
        if (recipe.HasRivers) RiverTestGraphs.Install(recipe, settings);
        return new RingTerrainSampler(settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
    }

    private static bool IsFrozenAt(WorldGenerationLedger.FeatureInfo feature, int generation)
        => feature.FrozenIn > 0 && feature.FrozenIn <= generation;

    private static void CheckGenerationArgs(int latest)
    {
        var implicitLegacy = LandscapeWorld.ParseGenerationArgs(new[] { "--print" });
        Require(implicitLegacy.Generation == 0 && implicitLegacy.Review.Count == 0 && !implicitLegacy.IsExplicit,
            "no generation flags must select the pinned world");
        var explicitLegacy = LandscapeWorld.ParseGenerationArgs(new[] { "--generation=0" });
        Require(explicitLegacy.Generation == 0 && explicitLegacy.Review.Count == 0 && explicitLegacy.IsExplicit,
            "explicit generation zero must select the pinned world");
        var review = LandscapeWorld.ParseGenerationArgs(new[] { $"--generation={latest}", "--review-features=test.open" });
        Require(review.Generation == latest && review.Review.SequenceEqual(new[] { "test.open" }) && review.IsExplicit,
            "explicit latest generation did not retain the requested review selection");

        string[][] rejected =
        {
            new[] { "--generation=0", "--generation=1" },
            new[] { "--generation=oops" },
            new[] { "--generation" },
            new[] { $"--generation={latest}", "--review-features=test.open", "--review-features=test.other" },
            new[] { $"--generation={latest}", "--review-features=" },
            new[] { "--generation=0", "--review-features=test.open" },
            new[] { "--review-features=test.open" }
        };
        foreach (string[] args in rejected)
        {
            try { LandscapeWorld.ParseGenerationArgs(args); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException($"invalid generation arguments accepted: {string.Join(" ", args)}");
        }
    }

    private static void CheckLegacy()
    {
        foreach (bool climate in new[] { false, true })
        {
            var recipes = new[]
            {
                EmitterSiteTestWorlds.Gen17Recipe(Seed),
                EmitterSiteTestWorlds.Gen17SitesRecipe(Seed),
                EmitterSiteTestWorlds.Gen17ClimateRecipe(Seed),
                EmitterSiteTestWorlds.Gen17LandformRecipe(Seed, climate),
                EmitterSiteTestWorlds.Gen17WeatherLungRecipe(Seed, climate)
            };
            foreach (var recipe in recipes)
            {
                using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, recipe);
                long circumference = EmitterSiteTestWorlds.Circumference;
                using var sampler = new RingTerrainSampler(settings, true, circumference, circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
                CheckSampler(sampler, 1, 0);
                Require(WorldGeneration.Publish(recipe, "contract-legacy") && WorldGeneration.Current.Generation == 1
                    && WorldGeneration.Current.FeatureMask == 0, $"legacy publication failed for {recipe.GeneratorId}");
            }
        }
    }

    private static void CheckSampler(RingTerrainSampler sampler, int generation, int mask)
    {
        Require(sampler.UsesRuntimeSurfaceGeneration, "contract sampler did not enable runtime generation");
        Require(sampler.WorldGenerationNumber == generation && sampler.WorldFeatureMask == mask, "incorrect sampler generation or mask");
        using RingTerrainSampler worker = sampler.CreateIndependentCopy();
        Require(worker.WorldGenerationNumber == generation && worker.WorldFeatureMask == mask, "incorrect worker generation or mask");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
