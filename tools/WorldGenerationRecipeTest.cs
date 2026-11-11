using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Generation.Gpu;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;
using GodotDictionary = Godot.Collections.Dictionary;

namespace RingworldTechDemo.Tools;

/// <summary>Run: tools/run_tests.ps1 -Tests world_generation_recipe_test. Checks v9 recipe shape, identity and v8 terrain parity.</summary>
public partial class WorldGenerationRecipeTest : RefCounted
{
    private const string Golden = "res://tools/fixtures/world_generation_recipe_golden.json";
    private const string TerrainGolden = "res://tools/fixtures/terrain_v7_v8_freeze_golden.json";
    private static readonly int[] Seeds = { 1337, 90210, 2147483000 };
    private static readonly string[] NewFields =
    {
        "world_generation", "review_features",
        "continental_frequency",
        "erosion_frequency",
        "ridge_frequency",
        "temperature_frequency",
        "moisture_frequency",
        "mountain_warp_frequency",
        "valley_frequency",
        "continental_secondary_strength",
        "continental_contrast",
        "continental_ocean_bias",
        "mountain_warp_distance_m",
        "peak_rarity_exponent",
        "mountain_belt_broadness",
        "valley_narrowness",
        "ocean_moisture_influence",
        "climate_latitude_influence",
        "coastal_beach_strength",
        "altitude_temperature_drop_per_kilometre",
        "mountain_rain_shadow_strength",
        "macro_region_size_m",
    };

    public string CanonicalDefault(int seed) => TerrainGenerationParameters.WorldDefault(1, seed).CanonicalJson;

    public string CanonicalTuned() => TerrainGenerationParameters.WorldDefault().WithFields(new Dictionary<string, double>
    {
        ["continental_frequency"] = 4.1234e-6,
        ["mountain_rain_shadow_strength"] = 0.6
    }).CanonicalJson;

    public int Run(bool capture, int generation)
    {
        try
        {
            if (capture) CheckBaseRecipe();
            else CheckBaseRecipeAndGolden();
            CheckExtensionFields();
            CheckTerrainParity();
            CheckSiteParity();
            if (capture) CheckGolden(true, generation);
            Console.WriteLine("WORLD_GENERATION_RECIPE=PASS");
            return 0;
        }
        catch (Exception exception)
        {
            GD.PrintErr("WORLD_GENERATION_RECIPE=FAIL " + exception);
            return 1;
        }
    }

    private static void CheckBaseRecipeAndGolden()
    {
        var failures = new List<Exception>();
        foreach (Action check in new Action[] { CheckBaseRecipe, () => CheckGolden(false, 1) })
        {
            try { check(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static void CheckBaseRecipe()
    {
        var v8 = TerrainGenerationParameters.WeatherLungDefault();
        var v9 = TerrainGenerationParameters.WorldDefault();
        using JsonDocument v8Json = JsonDocument.Parse(v8.CanonicalJson);
        using JsonDocument v9Json = JsonDocument.Parse(v9.CanonicalJson);
        var v8Keys = v8Json.RootElement.EnumerateObject().Select(field => field.Name);
        var v9Keys = v9Json.RootElement.EnumerateObject().Select(field => field.Name);
        Require(v9Keys.ToHashSet(StringComparer.Ordinal).SetEquals(v8Keys.Concat(NewFields)), "v9 canonical key set differs");
        Require(v9Keys.SequenceEqual(v8Keys.Concat(NewFields)), "v9 canonical field order differs");
        Require(v8.WorldGeneration == 1 && v8.ReviewFeatures.Count == 0 && !v8.IsWorldGenerationRecipe,
            "legacy recipe generation metadata differs");
        Require(v8.DefinitionHash != v9.DefinitionHash, "v8 and v9 definition hashes match");
        Require(ProxyGpuHeightKernel.IsSupported(v9, out string reason), "GPU host rejects v9: " + reason);

        string canonical = v9.CanonicalJson;
        Require(TerrainGenerationParameters.FromJson(canonical).CanonicalJson == canonical, "v9 JSON round trip differs");
        Require(TerrainGenerationParameters.FromDictionary(ToDictionary(v9Json.RootElement)).CanonicalJson == canonical,
            "v9 Variant round trip differs");
        var wrongKind = ToDictionary(v9Json.RootElement);
        wrongKind["macro_region_size_m"] = 128000d;
        Reject(() => TerrainGenerationParameters.FromDictionary(wrongKind), "float Variant for integer macro region size");
        Require(v9Json.RootElement.GetProperty("continental_contrast").GetDouble() == (double)1.35f, "contrast default lost float32 bits");
        Require(v9Json.RootElement.GetProperty("altitude_temperature_drop_per_kilometre").GetDouble() == (double)0.075f,
            "altitude lapse default lost float32 bits");

        var tuned = TerrainGenerationParameters.FromJson(CanonicalWith(canonical, "continental_frequency", "0.0000041234"));
        tuned = TerrainGenerationParameters.FromJson(CanonicalWith(tuned.CanonicalJson, "mountain_rain_shadow_strength", "0.6"));
        using JsonDocument tunedJson = JsonDocument.Parse(tuned.CanonicalJson);
        Require(tuned.ContinentalFrequency == (double)4.1234e-6f && tuned.MountainRainShadowStrength == (double)0.6f,
            "tuned values lost float32 bits");
        Require(TerrainGenerationParameters.FromDictionary(ToDictionary(tunedJson.RootElement)).CanonicalJson == tuned.CanonicalJson,
            "tuned Variant round trip differs");
        var negativeZero = TerrainGenerationParameters.FromJson(CanonicalWith(canonical, "continental_ocean_bias", "-0.0"));
        Require(negativeZero.CanonicalJson == canonical, "negative climate zero changed the canonical recipe");

        foreach (string invalid in new[] { "0", "-1", "1.5" })
            Reject(() => TerrainGenerationParameters.FromJson(CanonicalWith(canonical, "world_generation", invalid)), "world_generation " + invalid);
        foreach (string invalid in new[] { "\"test.z,test.a\"", "\"test.a,test.a\"", "\"test!a\"" })
            Reject(() => TerrainGenerationParameters.FromJson(CanonicalWith(canonical, "review_features", invalid)), "review_features " + invalid);
        foreach (string invalid in new[] { "1e999", "1e40", "0.7" })
            Reject(() => TerrainGenerationParameters.FromJson(CanonicalWith(canonical, "mountain_rain_shadow_strength", invalid)), "climate " + invalid);
        Reject(() => TerrainGenerationParameters.FromJson(CanonicalWith(canonical, "macro_region_size_m", "128000.5")), "fractional macro region size");
        Reject(() => TerrainGenerationParameters.FromJson(CanonicalWith(canonical, "world_generation", null)), "missing field");
        Reject(() => TerrainGenerationParameters.FromJson(canonical[..^1] + ",\"unknown\":1}"), "unknown field");
        Reject(() => TerrainGenerationParameters.WorldDefault(2), "generation beyond latest frozen");
        Reject(() => TerrainGenerationParameters.WorldDefault(review: new[] { "test.unknown" }), "unknown review feature");

        foreach (bool climate in new[] { false, true })
        {
            var legacy = EmitterSiteTestWorlds.Gen17WeatherLungRecipe(1337, climate);
            var world = TerrainGenerationParameters.WorldDefault(climateMachines: climate);
            Require(world.HasEmitterSites == legacy.HasEmitterSites && world.HasClimateMachines == legacy.HasClimateMachines
                && world.HasLowlandLandforms == legacy.HasLowlandLandforms && world.HasWeatherLung == legacy.HasWeatherLung,
                $"model flags differ with climate_machines={climate}");
        }
    }

    private static void CheckExtensionFields()
    {
        var rows = new[]
        {
            new WorldGenerationLedger.Row(1, "baseline", Array.Empty<string>(), ""),
            new WorldGenerationLedger.Row(2, "relief", new[] { "test.relief" }, "")
        };
        var relief = new WorldGenerationLedger.FeatureInfo("test.relief", 3, "test", 2);
        var open = new WorldGenerationLedger.FeatureInfo("test.open", 30, "test", 0);
        var extra = new WorldGenerationLedger.FeatureInfo("test.extra", 4, "test", 0);
        var ledger = WorldGenerationLedger.CreateForTests(rows, new[] { relief, open, extra });
        var schema = new WorldRecipeSchema(ledger, new RecipeFieldSpec[]
        {
            new("test_relief_m", relief.Id, RecipeFieldKind.Float, 3.5, 0, 20),
            new("test_relief_octaves", relief.Id, RecipeFieldKind.Int, 2, 1, 4),
            new("test_open_w", open.Id, RecipeFieldKind.Float, 0.25, 0, 1)
        });
        var production = TerrainGenerationParameters.WorldDefault();
        var gen1 = TerrainGenerationParameters.WorldDefault(1, schema: schema);
        var gen2 = TerrainGenerationParameters.WorldDefault(2, schema: schema);
        Require(gen1.CanonicalJson == production.CanonicalJson && gen1.DefinitionHash == production.DefinitionHash,
            "inactive extension fields changed generation 1");
        Require(gen2.DefinitionHash != gen1.DefinitionHash && gen2.CanonicalJson.EndsWith(",\"test_relief_m\":3.5,\"test_relief_octaves\":2}", StringComparison.Ordinal),
            "generation 2 active extension field order or defaults differ");
        Require(!gen1.HasWorldFeature(relief.Id) && gen2.HasWorldFeature(relief.Id), "frozen feature lookup differs");
        Require(schema.ActiveRecipeFields(2, Array.Empty<string>()).Select(field => field.Name).SequenceEqual(new[] { "test_relief_m", "test_relief_octaves" }),
            "active recipe field query differs");
        Require(ledger.MaskFor(2, new[] { open.Id }) == (1 << 3 | 1 << 30) && ledger.Has(1 << 30, open.Id),
            "feature mask or high bit differs");
        Reject(() => WorldGenerationLedger.CreateForTests(rows, new[] { relief, open with { Bit = 31 } }), "bit 31");
        Reject(() => WorldGenerationLedger.CreateForTests(rows, new[] { relief, open with { Bit = relief.Bit } }), "duplicate bit");
        Reject(() => WorldGenerationLedger.CreateForTests(rows, new[] { relief, open with { Id = relief.Id } }), "duplicate feature ID");

        Reject(() => TerrainGenerationParameters.FromJson(gen1.CanonicalJson[..^1] + ",\"test_relief_m\":3.5}", schema), "inactive extension field");
        Reject(() => TerrainGenerationParameters.FromJson(CanonicalWith(gen2.CanonicalJson, "test_relief_m", null), schema), "missing active extension field");
        Reject(() => TerrainGenerationParameters.FromJson(CanonicalWith(gen2.CanonicalJson, "test_relief_m", "25"), schema), "extension float outside range");
        Reject(() => TerrainGenerationParameters.FromJson(CanonicalWith(gen2.CanonicalJson, "test_relief_octaves", "2.5"), schema), "fractional extension integer");
        Require(gen2.GetExtensionField("test_relief_m") == 3.5 && gen2.GetExtensionField("test_relief_octaves") == 2, "active field values differ");
        Reject(() => gen1.GetExtensionField("test_relief_m"), "reading inactive extension field");

        var review = TerrainGenerationParameters.WorldDefault(2, review: new[] { open.Id }, schema: schema);
        Require(review.HasWorldFeature(open.Id) && review.GetExtensionField("test_open_w") == 0.25
            && !gen2.CanonicalJson.Contains("test_open_w", StringComparison.Ordinal), "review field activation differs");
        Reject(() => TerrainGenerationParameters.WorldDefault(2, review: new[] { relief.Id }, schema: schema), "review of already frozen feature");
        Reject(() => TerrainGenerationParameters.WorldDefault(2, review: new[] { "test.extra,test.open" }, schema: schema), "multiple IDs in one review selection");
        var fields = new Dictionary<string, double> { ["test_relief_m"] = 5 };
        var tuned = gen2.WithFields(fields);
        Require(tuned.CanonicalJson == gen2.CanonicalJson.Replace("\"test_relief_m\":3.5", "\"test_relief_m\":5", StringComparison.Ordinal)
            && tuned.DefinitionHash != gen2.DefinitionHash, "WithFields changed more than the requested value or preserved its hash");
        Reject(() => gen1.WithFields(fields), "overriding inactive extension field");
    }

    private static void CheckTerrainParity()
    {
        var actual = TerrainRecipeFreezeTest.Capture(new[] { "weather_lung_v8" },
            (_, seed) => TerrainGenerationParameters.WorldDefault(1, seed),
            (_, seed) => CreateSampler(TerrainGenerationParameters.WorldDefault(1, seed)));
        using JsonDocument golden = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(TerrainGolden)));
        var expected = golden.RootElement.GetProperty("entries").EnumerateObject()
            .Where(field => field.Name.StartsWith("weather_lung_v8/", StringComparison.Ordinal) && IsContent(field.Name))
            .ToDictionary(field => field.Name, field => field.Value.GetString(), StringComparer.Ordinal);
        Require(expected.Count == actual.Keys.Count(IsContent), "terrain freeze content key count differs");
        foreach (var (key, value) in expected)
            Require(actual.TryGetValue(key, out string? now) && now == value, "v9 terrain content differs from v8 at " + key);
    }

    private static bool IsContent(string name)
        => name.Split('/')[^1] is not ("canonical_json_sha256" or "definition_hash" or "terrain_definition_hash");

    private static void CheckSiteParity()
    {
        foreach (int seed in Seeds)
            CheckSites(EmitterSiteTestWorlds.Gen17WeatherLungRecipe(seed), TerrainGenerationParameters.WorldDefault(1, seed));
        var shape = new Dictionary<string, double> { ["land_balance"] = 0.03 };
        CheckSites(EmitterSiteTestWorlds.Gen17WeatherLungRecipe(1337, climate: true).WithFields(shape),
            TerrainGenerationParameters.WorldDefault(climateMachines: true).WithFields(shape));
    }

    private static void CheckSites(TerrainGenerationParameters legacy, TerrainGenerationParameters world)
    {
        using var legacySampler = CreateSampler(legacy);
        using var worldSampler = CreateSampler(world);
        using var legacySource = EmitterSiteHeightSources.Create(legacySampler);
        using var worldSource = EmitterSiteHeightSources.Create(worldSampler);
        var legacyShaper = EmitterSiteTerrainShaper.GetOrCreate(legacy);
        var worldShaper = EmitterSiteTerrainShaper.GetOrCreate(world);
        Require(legacySource.Identity == legacy.DefinitionHash && worldSource.Identity == legacySource.Identity
            && world.NaturalSiteIdentity == legacy.DefinitionHash, $"natural site projection differs for seed {world.Seed}");
        Require(legacyShaper.Registry.NaturalIdentity == worldShaper.Registry.NaturalIdentity
            && worldShaper.Registry.NaturalIdentity == worldSource.Identity && legacyShaper.Registry.Identity == worldShaper.Registry.Identity,
            $"emitter registry identity differs for seed {world.Seed}");
        Require(legacyShaper.WeatherLung != null && worldShaper.WeatherLung != null
            && legacyShaper.WeatherLung.Identity == worldShaper.WeatherLung.Identity
            && legacyShaper.WeatherLung.Site.SiteHash == worldShaper.WeatherLung.Site.SiteHash,
            $"Weather Lung identity differs for seed {world.Seed}");
        if (world.HasClimateMachines)
            Require(legacyShaper.Climate != null && worldShaper.Climate != null && legacyShaper.Climate.Identity == worldShaper.Climate.Identity,
                "climate registry identity differs");
        Require(legacySampler.TerrainDefinitionHash != worldSampler.TerrainDefinitionHash, "full sampler identity did not separate v8 and v9");
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

    private static void CheckGolden(bool capture, int generation)
    {
        string path = ProjectSettings.GlobalizePath(Golden);
        bool exists = File.Exists(path);
        JsonObject golden = exists
            ? JsonNode.Parse(File.ReadAllText(path))!.AsObject()
            : new JsonObject { ["schema"] = 1, ["rows"] = new JsonArray() };
        Require(golden["schema"]!.GetValue<int>() == 1, "unexpected recipe golden schema");
        JsonArray rows = golden["rows"]!.AsArray();
        var seen = new HashSet<int>();
        foreach (JsonNode? row in rows)
        {
            int frozen = row!["generation"]!.GetValue<int>();
            Require(seen.Add(frozen), $"duplicate recipe golden row {frozen}");
            Require(JsonNode.DeepEquals(row, GoldenRow(frozen)), $"recipe golden mismatch for generation {frozen}");
        }
        if (capture)
        {
            Require(generation >= 1 && generation <= WorldGenerationLedger.Production.LatestFrozen,
                $"cannot capture unavailable generation {generation}");
            Require(!seen.Contains(generation), $"refusing to overwrite recipe golden generation {generation}");
            rows.Add(GoldenRow(generation));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, golden.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
                File.Move(temporary, path, overwrite: exists);
            }
            finally { File.Delete(temporary); }
            Console.WriteLine($"WORLD_GENERATION_RECIPE_CAPTURED generation={generation}");
        }
        else
            Require(seen.Count == WorldGenerationLedger.Production.Rows.Count, "missing recipe golden row for a frozen generation");
    }

    private static JsonObject GoldenRow(int generation)
    {
        var seeds = new JsonObject();
        foreach (int seed in Seeds)
        {
            var recipe = TerrainGenerationParameters.WorldDefault(generation, seed);
            seeds[seed.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["canonical_sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recipe.CanonicalJson))).ToLowerInvariant(),
                ["definition_hash"] = $"0x{recipe.DefinitionHash:x16}"
            };
        }
        return new JsonObject { ["generation"] = generation, ["seeds"] = seeds };
    }

    private static GodotDictionary ToDictionary(JsonElement json)
    {
        var result = new GodotDictionary();
        foreach (JsonProperty field in json.EnumerateObject())
        {
            if (field.Value.ValueKind == JsonValueKind.String)
                result[field.Name] = field.Value.GetString()!;
            else if (field.Value.TryGetInt64(out long integer))
                result[field.Name] = integer;
            else
                result[field.Name] = field.Value.GetDouble();
        }
        return result;
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
