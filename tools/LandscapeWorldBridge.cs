using System;
using Godot;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// GDScript-facing view of <see cref="LandscapeWorld"/> (a Godot script class needs its own file named after it). Used by tools/landscape_harness_common.gd to
/// check that the booted game really runs the pinned generation 1 world.
/// </summary>
public partial class LandscapeWorldBridge : RefCounted
{
    RingworldTechDemo.Terrain.Generation.TerrainGenerationHeightModel? _model;

    public string CanonicalRecipeJson() => LandscapeWorld.CanonicalJson;
    public string DefinitionHashHex() => LandscapeWorld.DefinitionHashHex;
    public string JsonSha256() => LandscapeWorld.JsonSha256;

    /// <summary>Shaped (game) ground height at a metre coordinate; main thread only.</summary>
    public double GroundHeight(double around, double across)
    {
        _model ??= LandscapeWorld.CreateShapedModel();
        return _model.CalculateHeight(around, across);
    }

    /// <summary>Exported-property differences between the game's terrain settings and the pinned ones (diagnostic for a sampler mismatch).</summary>
    static string DescribeSettingsDifferences(VoxelTerrainSettings game)
    {
        using VoxelTerrainSettings ours = LandscapeWorld.CreateSettings();
        var parts = new System.Collections.Generic.List<string>();
        foreach (var info in typeof(VoxelTerrainSettings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!info.CanRead || info.GetIndexParameters().Length > 0 || info.DeclaringType == typeof(Resource) || info.DeclaringType == typeof(GodotObject) || info.DeclaringType == typeof(RefCounted)) continue;
            object? a = info.GetValue(game), b = info.GetValue(ours);
            if (a is GodotObject || b is GodotObject) continue;
            if (!Equals(a, b) && info.Name != "SurfaceGenerationRecipeJson") parts.Add($"{info.Name}: game {a} vs pinned {b}");
        }
        return parts.Count == 0 ? "no scalar property differs" : string.Join("; ", parts);
    }

    /// <summary>
    /// Recipe JSON equality plus a 64-point comparison between the game's terrain sampler and the pinned world's sampler: presentation height (bitwise),
    /// surface height, moisture, temperature, biome and water. Returns {ok, recipe_equal, points, mismatches, message, first_mismatch}.
    /// </summary>
    public Godot.Collections.Dictionary VerifyGame(Node game)
    {
        var result = new Godot.Collections.Dictionary { ["ok"] = false, ["recipe_equal"] = false, ["points"] = 0, ["mismatches"] = 0, ["message"] = "", ["first_mismatch"] = "" };
        try
        {
            var grid = game.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager");
            if (grid == null) { result["message"] = "game has no ChunkGridManager"; return result; }
            string gameJson = grid.TerrainSettings?.SurfaceGenerationRecipeJson ?? string.Empty;
            bool recipeEqual = gameJson == LandscapeWorld.CanonicalJson;
            result["recipe_equal"] = recipeEqual;
            result["game_recipe_sha256"] = LandscapeWorld.Sha256Hex(gameJson);
            result["pinned_recipe_sha256"] = LandscapeWorld.JsonSha256;
            if (!recipeEqual)
            {
                result["message"] = "recipe differs: game " + LandscapeWorld.Sha256Hex(gameJson)[..12] + " vs pinned " + LandscapeWorld.JsonSha256[..12];
                return result;
            }
            using RingTerrainSampler? theirs = grid.CreateIndependentTerrainSampler();
            if (theirs == null) { result["message"] = "game terrain sampler unavailable"; return result; }
            using RingTerrainSampler ours = LandscapeWorld.CreateSampler();
            int mismatches = 0;
            string first = "";
            const int points = 64;
            for (int k = 0; k < points; k++)
            {
                long around = 12_345L + k * 491_017L;
                int across = 9_000 + (int)((k * 4_733L) % 300_000L);
                around &= ~1L; across &= ~1; // lattice-aligned like the in-game 2 m grid
                float h0 = theirs.GetPresentationSurfaceHeight(around, across), h1 = ours.GetPresentationSurfaceHeight(around, across);
                TerrainSample s0 = theirs.GetTerrainSample(around, across), s1 = ours.GetTerrainSample(around, across);
                string why = "";
                if (BitConverter.SingleToInt32Bits(h0) != BitConverter.SingleToInt32Bits(h1)) why = $"height {h0} vs {h1}";
                else if (s0.SurfaceHeight != s1.SurfaceHeight) why = $"surface {s0.SurfaceHeight} vs {s1.SurfaceHeight}";
                else if (BitConverter.SingleToInt32Bits(s0.Moisture) != BitConverter.SingleToInt32Bits(s1.Moisture)) why = $"moisture {s0.Moisture} vs {s1.Moisture}";
                else if (BitConverter.SingleToInt32Bits(s0.Temperature) != BitConverter.SingleToInt32Bits(s1.Temperature)) why = $"temperature {s0.Temperature} vs {s1.Temperature}";
                else if (s0.Biome != s1.Biome) why = $"biome {s0.Biome} vs {s1.Biome}";
                else if (s0.WaterKind != s1.WaterKind || s0.WaterSurfaceHeight != s1.WaterSurfaceHeight) why = $"water {s0.WaterKind}/{s0.WaterSurfaceHeight} vs {s1.WaterKind}/{s1.WaterSurfaceHeight}";
                if (why.Length > 0) { mismatches++; if (first.Length == 0) first = $"point {k} ({around}, {across}): {why}"; }
            }
            result["points"] = points;
            result["mismatches"] = mismatches;
            result["first_mismatch"] = first;
            result["ok"] = mismatches == 0;
            result["message"] = mismatches == 0 ? "recipe and 64 sample points match" : $"{mismatches}/{points} sample points differ; first: {first}. Settings differences: {DescribeSettingsDifferences(grid.TerrainSettings!)}";
        }
        catch (Exception exception) { result["message"] = "verify failed: " + exception.Message; }
        return result;
    }
}
