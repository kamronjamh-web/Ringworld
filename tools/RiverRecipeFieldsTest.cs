using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>Run: tools/run_tests.ps1 -Tests river_recipe_fields_test. Checks the water.rivers ledger entry, its six recipe fields, HasRivers and the runtime hash terms.</summary>
public partial class RiverRecipeFieldsTest : RefCounted
{
    private const string Golden = "res://tools/fixtures/world_generation_recipe_golden.json";
    private static readonly string[] RiverFields = { "river_density", "lake_abundance", "river_valley_width", "channel_depth", "meander_strength", "river_ferocity" };
    private static readonly string[] Review = { "water.rivers" };

    public string CanonicalReview(int seed) => TerrainGenerationParameters.WorldDefault(1, seed, Review).CanonicalJson;

    public string CanonicalTuned() => TerrainGenerationParameters.WorldDefault(1, 1337, Review)
        .WithFields(new Dictionary<string, double> { ["meander_strength"] = 1.5, ["lake_abundance"] = 0.25 }).CanonicalJson;

    public int Run()
    {
        try
        {
            CheckGenerationOneGolden();
            CheckReviewRecipe();
            CheckRejections();
            CheckRuntimeHash();
            Console.WriteLine("RIVER_RECIPE_FIELDS=PASS");
            return 0;
        }
        catch (Exception exception)
        {
            GD.PrintErr("RIVER_RECIPE_FIELDS=FAIL " + exception);
            return 1;
        }
    }

    private static void CheckGenerationOneGolden()
    {
        using JsonDocument golden = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(Golden)));
        JsonElement row = golden.RootElement.GetProperty("rows").EnumerateArray().First(r => r.GetProperty("generation").GetInt32() == 1);
        foreach (JsonProperty seed in row.GetProperty("seeds").EnumerateObject())
        {
            var recipe = TerrainGenerationParameters.WorldDefault(1, int.Parse(seed.Name));
            Require(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recipe.CanonicalJson))).ToLowerInvariant() == seed.Value.GetProperty("canonical_sha256").GetString()
                && $"0x{recipe.DefinitionHash:x16}" == seed.Value.GetProperty("definition_hash").GetString(), "generation 1 recipe differs from the golden for seed " + seed.Name);
            Require(!recipe.HasRivers && RiverFields.All(name => !recipe.CanonicalJson.Contains(name, StringComparison.Ordinal)), "generation 1 recipe carries river state");
        }
    }

    private static void CheckReviewRecipe()
    {
        var gen1 = TerrainGenerationParameters.WorldDefault(1, 1337);
        var rivers = TerrainGenerationParameters.WorldDefault(1, 1337, Review);
        Require(rivers.HasRivers && rivers.ReviewFeatures.SequenceEqual(Review) && rivers.DefinitionHash != gen1.DefinitionHash, "review recipe flags or hash differ");
        Require(WorldRecipeFields.Specs.Select(s => s.Name).SequenceEqual(RiverFields), "river field order differs");
        Require(rivers.CanonicalJson.EndsWith(",\"river_density\":1,\"lake_abundance\":0.5,\"river_valley_width\":1,\"channel_depth\":1,\"meander_strength\":1,\"river_ferocity\":1}", StringComparison.Ordinal),
            "river field defaults or canonical order differ");
        Require(TerrainGenerationParameters.FromJson(rivers.CanonicalJson).CanonicalJson == rivers.CanonicalJson, "river recipe JSON round trip differs");
        var tuned = rivers.WithFields(new Dictionary<string, double> { ["river_density"] = 2.5 });
        Require(tuned.GetExtensionField("river_density") == 2.5 && tuned.DefinitionHash != rivers.DefinitionHash, "river WithFields differs");
        // The versions are bumped on purpose, so assert only that they are valid; CheckRuntimeHash proves the hash carries the river terms.
        Require(HydrologyVersions.DrainageAlgorithm >= 1 && HydrologyVersions.CarveOperator >= 1 && HydrologyVersions.GraphFormat >= 1, "hydrology versions are not positive");
    }

    private static void CheckRejections()
    {
        string canonical = TerrainGenerationParameters.WorldDefault(1, 1337, Review).CanonicalJson;
        foreach ((string name, string value) in new[] { ("river_density", "0.1"), ("river_density", "5"), ("lake_abundance", "1.5"), ("river_valley_width", "0.4"),
            ("channel_depth", "2.5"), ("meander_strength", "-0.1"), ("river_ferocity", "2"), ("river_ferocity", "0.5") })
            Reject(() => TerrainGenerationParameters.FromJson(CanonicalWith(canonical, name, value)), $"{name}={value}");
        // Negative control: a recipe with any one river field removed must be rejected.
        foreach (string name in RiverFields)
            Reject(() => TerrainGenerationParameters.FromJson(CanonicalWith(canonical, name, null)), "missing " + name);
        string gen1 = TerrainGenerationParameters.WorldDefault(1, 1337).CanonicalJson;
        Reject(() => TerrainGenerationParameters.FromJson(gen1[..^1] + ",\"river_density\":1}"), "river field without the feature");
    }

    private static void CheckRuntimeHash()
    {
        var gen1 = TerrainGenerationParameters.WorldDefault(1, 1337);
        var rivers = TerrainGenerationParameters.WorldDefault(1, 1337, Review);
        using var a = CreateSampler(gen1);
        using var b = CreateSampler(gen1);
        // S12: a water.rivers runtime sampler needs its graph; a strip graph stands in for the ring bake.
        using var riverSettings = new VoxelTerrainSettings { Seed = checked((int)rivers.Seed), SurfaceGenerationRecipeJson = rivers.CanonicalJson, SurfaceGenerationContractId = EmitterSiteTestWorlds.Contract };
        RiverTestGraphs.Install(rivers, riverSettings);
        using var c = CreateSampler(rivers);
        Require(a.TerrainDefinitionHash == b.TerrainDefinitionHash, "runtime hash is not stable without rivers");
        Require(a.TerrainDefinitionHash != c.TerrainDefinitionHash, "runtime hash does not change with water.rivers");
        // T2R-9: the graph identity hash joins the runtime hash, so another bake of the same recipe is another definition.
        RiverTestGraphs.Install(rivers, riverSettings, 10_200_000d);
        using var d = CreateSampler(rivers);
        Require(d.TerrainDefinitionHash != c.TerrainDefinitionHash, "runtime hash does not change with the river graph");
    }

    private static RingTerrainSampler CreateSampler(TerrainGenerationParameters recipe)
    {
        const long circumference = EmitterSiteTestWorlds.Circumference;
        using var settings = new VoxelTerrainSettings
        {
            Seed = checked((int)recipe.Seed), SurfaceGenerationRecipeJson = recipe.CanonicalJson,
            SurfaceGenerationContractId = EmitterSiteTestWorlds.Contract, BedrockY = -8000, SeaLevelY = 0,
            EnableUnified3dDensityTerrain = false, EnableProceduralCaves = false,
            EnableNaturalOverhangs = false, EnableDesignedCaveDensityComposition = false
        };
        return new RingTerrainSampler(settings, true, circumference, circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
    }

    private static string CanonicalWith(string json, string name, string? replacement)
    {
        using JsonDocument original = JsonDocument.Parse(json);
        var fields = original.RootElement.EnumerateObject()
            .Where(field => field.Name != name || replacement != null)
            .Select(field => JsonSerializer.Serialize(field.Name) + ":" + (field.Name == name ? replacement : field.Value.GetRawText()));
        return "{" + string.Join(",", fields) + "}";
    }

    private static void Reject(Action operation, string label)
    {
        try { operation(); }
        catch (ArgumentException) { return; }
        catch (JsonException) { return; }
        throw new InvalidOperationException("accepted " + label);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
