using System;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// GDScript-facing view of <see cref="LandscapeWorld"/> (a Godot script class needs its own file named after it). Used by tools/landscape_harness_common.gd to
/// check that the booted game runs the selected reference world.
/// </summary>
public partial class LandscapeWorldBridge : RefCounted
{
    LandscapeWorld.Spec _spec = LandscapeWorld.Default;
    TerrainGenerationHeightModel? _model;
    internal LandscapeWorld.Spec SelectedSpec => _spec;

    public void Configure(int generation, string reviewCsv)
    {
        SetSpec(LandscapeWorld.For(generation,
            string.IsNullOrEmpty(reviewCsv) ? null : reviewCsv.Split(',')));
    }

    public Godot.Collections.Dictionary ConfigureFromArgs(string[] args)
    {
        try
        {
            SetSpec(LandscapeWorld.FromArgs(args));
            var selection = new Godot.Collections.Dictionary
            {
                ["generation"] = _spec.IsDefault ? 0 : _spec.Recipe.WorldGeneration,
                ["review_features"] = System.Linq.Enumerable.ToArray(_spec.Recipe.ReviewFeatures),
                ["world_tag"] = _spec.Tag
            };
            if (_spec.Recipe.Seed != LandscapeWorld.Seed) selection["seed"] = (int)_spec.Recipe.Seed; // recorded only for a non-default seed, so default records are unchanged
            return selection;
        }
        catch (Exception exception)
        {
            return new Godot.Collections.Dictionary { ["error"] = exception.Message };
        }
    }

    void SetSpec(LandscapeWorld.Spec spec)
    {
        _model?.Dispose();
        _model = null;
        _spec = spec;
    }

    public string CanonicalRecipeJson() => _spec.CanonicalJson;
    public string DefinitionHashHex() => _spec.DefinitionHashHex;
    public string JsonSha256() => _spec.JsonSha256;

    /// <summary>Shaped (game) ground height at a metre coordinate; main thread only.</summary>
    public double GroundHeight(double around, double across)
    {
        _model ??= _spec.CreateShapedModel();
        return _model.CalculateHeight(around, across);
    }

    /// <summary>
    /// T4-2: a river bank dressing pose resolved from the booted game's baked river graph (a water.rivers review world; the fixture cannot hold it, because the graph is baked per recipe).
    /// Takes the longest stem, its midpoint, walks along the left normal until the terrain sample is no longer River, and stands 10 m beyond the bank looking at the centreline.
    /// Returns {ok, pose, message}; `pose` has the fixture row fields (agl, 1.7 m eye).
    /// </summary>
    public Godot.Collections.Dictionary ResolveRiverBankPose(Node game)
    {
        var result = new Godot.Collections.Dictionary { ["ok"] = false, ["message"] = "" };
        try
        {
            var grid = game.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager");
            using RingTerrainSampler? sampler = grid?.CreateIndependentTerrainSampler();
            RingworldTechDemo.Terrain.Hydrology.RiverGraph? graph = sampler?.RuntimeRiverGraph;
            if (graph == null) { result["message"] = "the booted world has no river graph (needs --generation=1 --review-features=water.rivers)"; return result; }
            var net = graph.Network;
            int stem = 0;
            for (int i = 1; i < net.Curves.Length; i++) if (net.Curves[i].Length > net.Curves[stem].Length) stem = i;
            net.CentreFrame(stem, net.Curves[stem].Length * 0.5, out double a, out double z, out double nx, out double ny);
            double bank = -1;
            for (double d = 0; d <= 400; d += 1)
            {
                double pa = net.WrapAround(a + nx * d), pz = z + ny * d;
                TerrainSample s = sampler!.GetTerrainSample((long)Math.Floor(pa), (int)Math.Floor(pz));
                if (s.WaterKind != SurfaceWaterKind.River) { bank = d; break; }
            }
            if (bank < 0) { result["message"] = "no bank within 400 m of the longest stem's midpoint"; return result; }
            double standA = net.WrapAround(a + nx * (bank + 10)), standZ = z + ny * (bank + 10);
            double yaw = (Math.Round(Math.Atan2(-ny, -nx) * 180d / Math.PI) % 360d + 360d) % 360d;
            result["pose"] = new Godot.Collections.Dictionary
            {
                ["name"] = "river_bank_eye", ["reports"] = new Godot.Collections.Array { "t4" }, ["class"] = "River", ["habitat"] = "None",
                ["around_m"] = standA, ["across_m"] = standZ, ["ground_m"] = GroundHeight(standA, standZ), ["vertical_mode"] = "agl", ["lift_m"] = 1.7,
                ["yaw_deg"] = yaw, ["pitch_deg"] = -4.0,
                ["notes"] = $"resolved at run time: longest river stem {stem} (length {net.Curves[stem].Length:F0} m), midpoint ({a:F0}, {z:F0}), bank {bank:F0} m left of the centreline, 10 m beyond it, looking at the river"
            };
            result["ok"] = true;
        }
        catch (Exception exception) { result["message"] = "river bank search failed: " + exception.Message; }
        return result;
    }

    /// <summary>Exported-property differences between the game's terrain settings and the pinned ones (diagnostic for a sampler mismatch).</summary>
    string DescribeSettingsDifferences(VoxelTerrainSettings game)
    {
        using VoxelTerrainSettings ours = _spec.CreateSettings();
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
            bool recipeEqual = gameJson == _spec.CanonicalJson;
            result["recipe_equal"] = recipeEqual;
            result["game_recipe_sha256"] = LandscapeWorld.Sha256Hex(gameJson);
            result["pinned_recipe_sha256"] = _spec.JsonSha256;
            if (!recipeEqual)
            {
                result["message"] = "recipe differs: game " + LandscapeWorld.Sha256Hex(gameJson)[..12] + " vs pinned " + _spec.JsonSha256[..12];
                return result;
            }
            using RingTerrainSampler? theirs = grid.CreateIndependentTerrainSampler();
            if (theirs == null) { result["message"] = "game terrain sampler unavailable"; return result; }
            using RingTerrainSampler ours = _spec.CreateSampler();
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
