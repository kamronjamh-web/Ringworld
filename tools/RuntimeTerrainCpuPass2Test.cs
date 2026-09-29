using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Tools;

/// <summary>Standalone frozen-output capture/compare driver for CPU pass 2.</summary>
public partial class RuntimeTerrainCpuPass2Test : RefCounted
{
    const long Circumference = 31_415_936L;
    const int Width = 318_000;
    const string Contract = "surface_runtime_generation_v1";

    public int Run(bool capture)
        => RunGolden(ProjectSettings.GlobalizePath("res://tools/fixtures/runtime_terrain_cpu_pass2_golden.json"), capture);

    public static int RunGolden(string fixture, bool capture)
    {
        if (capture && File.Exists(fixture)) return Fail("Refusing to overwrite existing golden fixture: " + fixture);
        try
        {
            Golden actual = Capture();
            if (capture)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fixture))!);
                File.WriteAllText(fixture, JsonSerializer.Serialize(actual, JsonOptions) + System.Environment.NewLine);
                Console.WriteLine("RUNTIME_TERRAIN_CPU_PASS2_CAPTURE=PASS cases=" + actual.Cases.Count);
                return 0;
            }
            if (!File.Exists(fixture)) return Fail("Golden fixture missing: " + fixture);
            Golden? expected = JsonSerializer.Deserialize<Golden>(File.ReadAllText(fixture), JsonOptions);
            if (expected == null) return Fail("Golden fixture could not be read.");
            var mismatches = Compare(expected, actual);
            if (mismatches.Count != 0) return Fail("RUNTIME_TERRAIN_CPU_PASS2=FAIL mismatch_keys=" + string.Join(",", mismatches.Take(64)));
            Console.WriteLine("RUNTIME_TERRAIN_CPU_PASS2=PASS cases=" + actual.Cases.Count);
            return 0;
        }
        catch (Exception exception) { return Fail(exception.ToString()); }
    }

    static Golden Capture()
    {
        var golden = new Golden { Format = "runtime_terrain_cpu_pass2_v1", Cases = new List<Case>() };
        foreach ((string name, TerrainGenerationParameters recipe) in Recipes())
        using (var sampler = CreateSampler(recipe))
        {
            var item = new Case { Name = name, RecipeJson = recipe.CanonicalJson, RecipeHash = recipe.DefinitionHash.ToString("X16"), TerrainHash = sampler.TerrainDefinitionHash.ToString("X16") };
            foreach ((string point, long around, int across) in Points()) item.Points.Add(Sample(point, sampler, around, across));
            foreach ((string point, long around, int across) in new[] { ("foothill", 2_356_195L, 159_000), ("mountain", 1_570_796L, 159_000) })
                item.Grids.Add(SampleGrid(point, sampler, around, across));
            golden.Cases.Add(item);
        }
        return golden;
    }

    static IEnumerable<(string, TerrainGenerationParameters)> Recipes()
    {
        foreach (int seed in new[] { 1337, 7331 })
        {
            yield return ($"v1_seed_{seed}", From(TerrainGenerationParameters.LegacyDefault(), seed));
            yield return ($"v2_seed_{seed}", From(TerrainGenerationParameters.CoastalDefault(), seed));
            for (int mode = 0; mode < 4; mode++)
                yield return ($"v3_mode_{mode}_seed_{seed}", From(TerrainGenerationParameters.Default(), seed, mode));
        }
    }

    static TerrainGenerationParameters From(TerrainGenerationParameters source, int seed, int? mode = null)
    {
        string json = source.CanonicalJson.Replace("\"seed\":1337", "\"seed\":" + seed);
        if (mode.HasValue) json = json.Replace("\"mountain_detail_mode\":2", "\"mountain_detail_mode\":" + mode.Value);
        return TerrainGenerationParameters.FromJson(json);
    }

    static RingTerrainSampler CreateSampler(TerrainGenerationParameters recipe)
    {
        using var settings = new VoxelTerrainSettings {
            Seed = unchecked((int)recipe.Seed), SurfaceGenerationRecipeJson = recipe.CanonicalJson,
            SurfaceGenerationContractId = Contract, BedrockY = -8000, SeaLevelY = 0,
            EnableUnified3dDensityTerrain = false, EnableProceduralCaves = false,
            EnableNaturalOverhangs = false, EnableDesignedCaveDensityComposition = false
        };
        return new RingTerrainSampler(settings, true, Circumference, Circumference / Math.Tau, Width, 8400);
    }

    static IEnumerable<(string, long, int)> Points() => new[] {
        ("lattice", 1_570_796L, 159_000), ("odd", 1_570_797L, 159_001), ("negative_even", -2L, 159_000),
        ("negative_seam", -1L, 159_000), ("seam_left", Circumference - 2, 159_000),
        ("seam_right", Circumference, 159_000), ("across_zero", 64L, 0),
        ("across_end", 64L, Width), ("boundary_odd", Circumference - 1, Width - 1),
        ("multi_turn_even", Circumference * 2 + 64L, 159_000)
    };

    static Point Sample(string name, RingTerrainSampler sampler, long around, int across)
    {
        TerrainSample s = sampler.GetTerrainSample(around, across);
        var w = sampler.GetBiomeTextureWeights(s);
        sampler.GetPresentationSurfaceHeightAndBiomeWeights(around, across, out float fusedHeight, out var fusedWeights);
        RequireFinite(sampler.GetPresentationSurfaceHeight(around, across), "height", name);
        RequireFinite(fusedHeight, "fused_height", name);
        RequireFinite(s.Continentalness, "continentalness", name); RequireFinite(s.Erosion, "erosion", name);
        RequireFinite(s.RidgeStrength, "ridge", name); RequireFinite(s.Temperature, "temperature", name); RequireFinite(s.Moisture, "moisture", name);
        RequireFinite(w.Desert, "desert", name); RequireFinite(w.Forest, "forest", name); RequireFinite(w.Tundra, "tundra", name); RequireFinite(w.Alpine, "alpine", name);
        return new Point {
            Name = name, Around = around, Across = across, HeightBits = Bits(sampler.GetPresentationSurfaceHeight(around, across)),
            FusedHeightBits = Bits(fusedHeight), SurfaceHeight = s.SurfaceHeight, Region = (int)s.Region,
            ContinentalnessBits = Bits(s.Continentalness), ErosionBits = Bits(s.Erosion), RidgeBits = Bits(s.RidgeStrength),
            TemperatureBits = Bits(s.Temperature), MoistureBits = Bits(s.Moisture), Biome = (int)s.Biome,
            WaterKind = (int)s.WaterKind, WaterSurfaceHeight = s.WaterSurfaceHeight,
            WeightBits = Weights(w), FusedWeightBits = Weights(fusedWeights)
        };
    }

    static Grid SampleGrid(string name, RingTerrainSampler sampler, long originAround, int originAcross)
    {
        var heights = new byte[128 * 128 * 4]; var biomes = new byte[128 * 128 * 4]; int offset = 0;
        for (int z = 0; z < 128; z++) for (int x = 0; x < 128; x++)
        {
            long around = originAround + x * 2L; int across = originAcross + z * 2;
            float height = sampler.GetPresentationSurfaceHeight(around, across); RequireFinite(height, "grid_height", name);
            BitConverter.TryWriteBytes(heights.AsSpan(offset, 4), height);
            var w = sampler.GetBiomeTextureWeights(around, across);
            RequireFinite(w.Desert, "grid_desert", name); RequireFinite(w.Forest, "grid_forest", name); RequireFinite(w.Tundra, "grid_tundra", name); RequireFinite(w.Alpine, "grid_alpine", name);
            biomes[offset] = ToByte(w.Desert); biomes[offset + 1] = ToByte(w.Forest); biomes[offset + 2] = ToByte(w.Tundra); biomes[offset + 3] = ToByte(w.Alpine); offset += 4;
        }
        return new Grid { Name = name, OriginAround = originAround, OriginAcross = originAcross, HeightRfSha256 = Hash(heights), BiomeRgba8Sha256 = Hash(biomes) };
    }

    static List<string> Compare(Golden expected, Golden actual)
    {
        var bad = new List<string>();
        if (expected.Format != actual.Format) bad.Add("format");
        foreach (Case current in actual.Cases)
        {
            Case? old = expected.Cases.FirstOrDefault(c => c.Name == current.Name);
            if (old == null) { bad.Add(current.Name + "/missing"); continue; }
            if (old.RecipeJson != current.RecipeJson) bad.Add(current.Name + "/recipe_json");
            if (old.RecipeHash != current.RecipeHash) bad.Add(current.Name + "/recipe_hash");
            if (old.TerrainHash != current.TerrainHash) bad.Add(current.Name + "/terrain_hash");
            foreach (Point point in current.Points) { Point? prior = old.Points.FirstOrDefault(p => p.Name == point.Name); if (prior == null || JsonSerializer.Serialize(prior) != JsonSerializer.Serialize(point)) bad.Add(current.Name + "/point/" + point.Name); }
            foreach (Grid grid in current.Grids) { Grid? prior = old.Grids.FirstOrDefault(g => g.Name == grid.Name); if (prior == null || prior.HeightRfSha256 != grid.HeightRfSha256 || prior.BiomeRgba8Sha256 != grid.BiomeRgba8Sha256) bad.Add(current.Name + "/grid/" + grid.Name); }
        }
        if (expected.Cases.Count != actual.Cases.Count) bad.Add("case_count"); return bad;
    }

    static string Bits(float value) => unchecked((uint)BitConverter.SingleToInt32Bits(value)).ToString("X8");
    static void RequireFinite(float value, string field, string point) { if (!float.IsFinite(value)) throw new InvalidDataException($"Non-finite {field} at {point}."); }
    static string Weights(RingTerrainSampler.BiomeTextureWeights value) => string.Join(",", Bits(value.Desert), Bits(value.Forest), Bits(value.Tundra), Bits(value.Alpine));
    static byte ToByte(float value) => (byte)Math.Clamp((int)(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public sealed class Golden { public string Format { get; set; } = ""; public List<Case> Cases { get; set; } = new(); }
    public sealed class Case { public string Name { get; set; } = ""; public string RecipeJson { get; set; } = ""; public string RecipeHash { get; set; } = ""; public string TerrainHash { get; set; } = ""; public List<Point> Points { get; set; } = new(); public List<Grid> Grids { get; set; } = new(); }
    public sealed class Point { public string Name { get; set; } = ""; public long Around { get; set; } public int Across { get; set; } public string HeightBits { get; set; } = ""; public string FusedHeightBits { get; set; } = ""; public int SurfaceHeight { get; set; } public int Region { get; set; } public string ContinentalnessBits { get; set; } = ""; public string ErosionBits { get; set; } = ""; public string RidgeBits { get; set; } = ""; public string TemperatureBits { get; set; } = ""; public string MoistureBits { get; set; } = ""; public int Biome { get; set; } public int WaterKind { get; set; } public int WaterSurfaceHeight { get; set; } public string WeightBits { get; set; } = ""; public string FusedWeightBits { get; set; } = ""; }
    public sealed class Grid { public string Name { get; set; } = ""; public long OriginAround { get; set; } public int OriginAcross { get; set; } public string HeightRfSha256 { get; set; } = ""; public string BiomeRgba8Sha256 { get; set; } = ""; }
}
