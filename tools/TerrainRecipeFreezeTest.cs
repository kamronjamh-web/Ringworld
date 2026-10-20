using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Bit-identity freeze for recipes v4, v5 and v6 (docs/lowland-landform-overhaul-plan.md, Step 0). Capture mode writes
/// tools/fixtures/terrain_v4_v6_freeze_golden.json once from a known-good build and refuses to overwrite it; normal mode compares.
/// Covers CanonicalJson, DefinitionHash, RingTerrainSampler.TerrainDefinitionHash, SampleGrid float32 hashes at 2 m and 512 m spacing
/// and float64 diagnostics hashes. Hashed diagnostics fields are listed explicitly so later additions to the record do not move the golden.
/// </summary>
public partial class TerrainRecipeFreezeTest : RefCounted
{
    const string GoldenPath = "res://tools/fixtures/terrain_v4_v6_freeze_golden.json";
    const long Circumference = EmitterSiteTestWorlds.Circumference;
    const int Width = EmitterSiteTestWorlds.Width;
    const int GridSize = 64, DiagnosticPoints = 64;
    static readonly int[] Seeds = { 1337, 90210, 2147483000 };
    static readonly string[] RecipeNames = { "erosion_v4", "sites_v5", "climate_v6" };
    // Added with v8 (docs/weather-lung-integration-plan.md S4b): a separate golden so the v4-v6 golden above stays untouched.
    const string GoldenPathV8 = "res://tools/fixtures/terrain_v7_v8_freeze_golden.json";
    static readonly string[] RecipeNamesV8 = { "landform_v7", "weather_lung_v8" };

    public int Run(bool capture)
    {
        var failures = new List<string>();
        try
        {
            string path = ProjectSettings.GlobalizePath(GoldenPath);
            if (capture && File.Exists(path)) { GD.PrintErr("FREEZE_FAIL refusing to overwrite " + GoldenPath); return 1; }
            SortedDictionary<string, string> actual = Capture();
            if (capture)
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new SortedDictionary<string, object> { ["format"] = "terrain_v4_v6_freeze_v1", ["entries"] = actual }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
                Console.WriteLine($"FREEZE_CAPTURED entries={actual.Count} path={GoldenPath}");
            }
            else
            {
                if (!File.Exists(path)) { failures.Add("golden fixture missing: " + GoldenPath); }
                else
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                    var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
                    foreach (JsonProperty p in doc.RootElement.GetProperty("entries").EnumerateObject()) expected[p.Name] = p.Value.GetString() ?? "";
                    foreach ((string key, string value) in expected)
                    {
                        if (!actual.TryGetValue(key, out string? now)) failures.Add("missing entry " + key);
                        else if (now != value) failures.Add($"mismatch {key}");
                    }
                    foreach (string key in actual.Keys) if (!expected.ContainsKey(key)) failures.Add("unexpected entry " + key);
                    Console.WriteLine($"FREEZE_COMPARED entries={expected.Count} mismatches={failures.Count}");
                }
            }
        }
        catch (Exception exception) { failures.Add(exception.ToString()); }
        foreach (string failure in failures) GD.PrintErr("FREEZE_FAIL " + failure);
        Console.WriteLine(failures.Count == 0 ? "TERRAIN_RECIPE_FREEZE=PASS" : "TERRAIN_RECIPE_FREEZE=FAIL failures=" + failures.Count);
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Freeze of v7 and v8 (the v4-v6 golden above is untouched). Capture mode writes tools/fixtures/terrain_v7_v8_freeze_golden.json once and never overwrites it;
    /// normal mode compares. v8 entries additionally cover the Weather Lung site record and the shaped terrain around it.
    /// </summary>
    public int RunV8(bool capture)
    {
        var failures = new List<string>();
        try
        {
            string path = ProjectSettings.GlobalizePath(GoldenPathV8);
            if (capture && File.Exists(path)) { GD.PrintErr("FREEZE_V8_FAIL refusing to overwrite " + GoldenPathV8); return 1; }
            SortedDictionary<string, string> actual = Capture(RecipeNamesV8);
            if (capture)
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new SortedDictionary<string, object> { ["format"] = "terrain_v7_v8_freeze_v1", ["entries"] = actual }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
                Console.WriteLine($"FREEZE_V8_CAPTURED entries={actual.Count} path={GoldenPathV8}");
            }
            else if (!File.Exists(path)) failures.Add("golden fixture missing: " + GoldenPathV8);
            else
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (JsonProperty p in doc.RootElement.GetProperty("entries").EnumerateObject()) expected[p.Name] = p.Value.GetString() ?? "";
                foreach ((string key, string value) in expected)
                {
                    if (!actual.TryGetValue(key, out string? now)) failures.Add("missing entry " + key);
                    else if (now != value) failures.Add($"mismatch {key}");
                }
                foreach (string key in actual.Keys) if (!expected.ContainsKey(key)) failures.Add("unexpected entry " + key);
                Console.WriteLine($"FREEZE_V8_COMPARED entries={expected.Count} mismatches={failures.Count}");
            }
        }
        catch (Exception exception) { failures.Add(exception.ToString()); }
        foreach (string failure in failures) GD.PrintErr("FREEZE_V8_FAIL " + failure);
        Console.WriteLine(failures.Count == 0 ? "TERRAIN_RECIPE_FREEZE_V8=PASS" : "TERRAIN_RECIPE_FREEZE_V8=FAIL failures=" + failures.Count);
        return failures.Count == 0 ? 0 : 1;
    }

    static TerrainGenerationParameters Recipe(string name, int seed) => name switch
    {
        "erosion_v4" => EmitterSiteTestWorlds.Gen17Recipe(seed),
        "sites_v5" => EmitterSiteTestWorlds.Gen17SitesRecipe(seed),
        "landform_v7" => EmitterSiteTestWorlds.Gen17LandformRecipe(seed),
        "weather_lung_v8" => EmitterSiteTestWorlds.Gen17WeatherLungRecipe(seed),
        _ => EmitterSiteTestWorlds.Gen17ClimateRecipe(seed),
    };

    static RingTerrainSampler Sampler(string name, int seed) => name switch
    {
        "erosion_v4" => EmitterSiteTestWorlds.CreateGen17(seed),
        "sites_v5" => EmitterSiteTestWorlds.CreateGen17Sites(seed),
        "landform_v7" => EmitterSiteTestWorlds.CreateGen17Landform(seed),
        "weather_lung_v8" => EmitterSiteTestWorlds.CreateGen17WeatherLung(seed),
        _ => EmitterSiteTestWorlds.CreateGen17Climate(seed),
    };

    static SortedDictionary<string, string> Capture() => Capture(RecipeNames);

    static SortedDictionary<string, string> Capture(string[] recipeNames)
    {
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in recipeNames)
            foreach (int seed in Seeds)
            {
                string prefix = $"{name}/{seed}/";
                TerrainGenerationParameters p = Recipe(name, seed);
                entries[prefix + "canonical_json_sha256"] = Sha(System.Text.Encoding.UTF8.GetBytes(p.CanonicalJson));
                entries[prefix + "definition_hash"] = p.DefinitionHash.ToString("X16");
                using (RingTerrainSampler sampler = Sampler(name, seed)) entries[prefix + "terrain_definition_hash"] = sampler.TerrainDefinitionHash.ToString("X16");
                // Representative origins are found with the unshaped natural model; the same model class that is hashed below.
                var origins = FindOrigins(seed);
                using var model = new TerrainGenerationHeightModel(p);
                foreach ((string label, double around, double across) in origins)
                {
                    entries[prefix + $"grid2m/{label}"] = Sha(FloatBytes(model.SampleGrid(around, across, GridSize, GridSize, 2d)));
                    entries[prefix + $"grid512m/{label}"] = Sha(FloatBytes(model.SampleGrid(around, across, GridSize, GridSize, 512d)));
                }
                var rng = new Random(seed ^ 0x5eed);
                var bytes = new List<byte>();
                for (int i = 0; i < DiagnosticPoints; i++)
                {
                    double around = rng.NextDouble() * Circumference, across = rng.NextDouble() * (Width - 2);
                    AppendDiagnostics(bytes, model.SampleDiagnostics(around, across));
                }
                entries[prefix + "diagnostics64"] = Sha(bytes.ToArray());
                if (name == "weather_lung_v8")
                {
                    // Around the Weather Lung centre (basin, rim, skirt) the v8 output itself is frozen.
                    using RingTerrainSampler lungSampler = Sampler(name, seed);
                    WeatherLungSite lung = (lungSampler.WeatherLung ?? throw new InvalidOperationException("v8 sampler has no Weather Lung")).Site;
                    entries[prefix + "wl_site"] = $"{lung.Around}/{lung.Across}/{lung.PadHeight}/{lung.YawDegrees}/{lung.SiteHash:X16}";
                    entries[prefix + "wl_grid2m"] = Sha(FloatBytes(model.SampleGrid(lung.Around - 64d, lung.Across - 64d, GridSize, GridSize, 2d)));
                    entries[prefix + "wl_grid512m"] = Sha(FloatBytes(model.SampleGrid(lung.Around - 16_384d, lung.Across - 16_384d, GridSize, GridSize, 512d)));
                    var lungBytes = new List<byte>();
                    var lungRng = new Random(seed ^ 0x1a2b);
                    for (int i = 0; i < DiagnosticPoints; i++)
                        AppendDiagnostics(lungBytes, model.SampleDiagnostics(lung.Around + (lungRng.NextDouble() - 0.5d) * 40_000d, lung.Across + (lungRng.NextDouble() - 0.5d) * 30_000d));
                    entries[prefix + "wl_diagnostics64"] = Sha(lungBytes.ToArray());
                }
            }
        return entries;
    }

    /// <summary>Fixed seam/edge origins plus scanned plains, foothill, mountain, coast and ocean origins for the seed.</summary>
    static List<(string Label, double Around, double Across)> FindOrigins(int seed)
    {
        double edge = 2d * (GridSize - 1);
        var origins = new List<(string, double, double)>
        {
            ("seam_before", Circumference - edge / 2d - 1d, 40_000d),
            ("seam_at", Circumference - 64d, 150_000d),
            ("seam_after", 0d, 150_000d),
            ("across_0", 100_000d, 0d),
            ("across_max", 100_000d, Width - 2 - edge),
        };
        using var scan = new TerrainGenerationHeightModel(TerrainGenerationParameters.FromJson(
            TerrainGenerationParameters.ErosionDefault().CanonicalJson.Replace("\"seed\":1337", "\"seed\":" + seed)), false);
        var wanted = new Dictionary<string, Func<TerrainGenerationHeightModel.Diagnostics, bool>>
        {
            ["plains"] = d => d.LowlandWeight > .98 && d.MacroHeight > 150 && d.MacroHeight < 800,
            ["foothill"] = d => d.FoothillHeight > 60 && d.AlpineWeight < .1,
            ["mountain"] = d => d.AlpineWeight > .95 && d.RidgeWeight > .5 && d.MacroHeight > 2500,
            ["coast"] = d => d.Continentalness > 0 && d.Continentalness < .01,
            ["ocean"] = d => d.MacroHeight < -1500,
        };
        var found = new Dictionary<string, (double, double)>();
        for (int i = 0; i < 20000 && found.Count < wanted.Count; i++)
        {
            double around = Math.Floor((i * 1_573_193.0) % Circumference / 2d) * 2d;
            double across = Math.Floor((20_000d + (i * 40_961.0) % (Width - 40_000)) / 2d) * 2d;
            TerrainGenerationHeightModel.Diagnostics d = scan.SampleNaturalDiagnostics(around, across);
            foreach ((string key, var test) in wanted)
                if (!found.ContainsKey(key) && test(d)) found[key] = (around, across);
        }
        foreach (string key in wanted.Keys)
            origins.Add(found.TryGetValue(key, out var o) ? (key, o.Item1, o.Item2) : (key + "_fallback", 1_000_000d * (origins.Count + 1), 60_000d));
        return origins;
    }

    static void AppendDiagnostics(List<byte> bytes, TerrainGenerationHeightModel.Diagnostics d)
    {
        double[] values =
        {
            d.Height, d.MacroHeight, d.Continentalness, d.SeaWeight, d.LowlandWeight, d.AlpineWeight, d.DetailMask, d.ValleyWeight, d.RidgeWeight,
            d.MountainUplift, d.TrenchDepth, d.DetailHeight, d.FoothillHeight, d.SpurHeight, d.ErosionHeight, d.SiteWeight, d.Clamped ? 1d : 0d, d.SiteIndex, d.ClimateMachineIndex,
        };
        foreach (double v in values) bytes.AddRange(BitConverter.GetBytes(v));
    }

    static byte[] FloatBytes(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
