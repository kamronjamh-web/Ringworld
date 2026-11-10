using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T0-5 tree pack audit (docs/landscape-audit-t0-spec.md, section 3). Reads the prepared catalog the game loads, the compact LOD
/// profile defaults and the variant histogram of production tree tiles; asserts the facts T6 builds on and writes
/// tree_pack_audit.json/.md under .godot/audit_baseline/common/t0/trees/. Changes no game state.
/// </summary>
public partial class LandscapeTreePackAuditTest : RefCounted
{
    const long Circumference = 31_415_936L;
    const int Width = 318_000;
    const double Cell = 30;
    const int TileCount = 64;

    static readonly string[] Classes = { "sapling", "small", "medium", "big", "large" };
    static readonly string[] TextureUniforms = { "albedo_texture", "normal_texture", "metallic_texture", "roughness_texture" };

    readonly List<string> _failures = new();

    public bool Run()
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outDir = ProjectSettings.GlobalizePath("res://.godot/audit_baseline/common/t0/trees/" + stamp);
            Directory.CreateDirectory(outDir);

            var catalog = ImportedTreeCatalog.Load();
            var report = new Dictionary<string, object?>
            {
                ["schema"] = 1,
                ["catalog_loaded_from"] = ImportedTreeCatalog.LoadedFrom,
                ["catalog_fallback_reason"] = ImportedTreeCatalog.FallbackReason,
            };
            Require(ImportedTreeCatalog.LoadedFrom == "prepared", "catalog did not load from the prepared resource: " + ImportedTreeCatalog.LoadedFrom + " " + ImportedTreeCatalog.FallbackReason);

            var variants = AuditVariants(catalog);
            report["variants"] = variants;
            AssertContracts(catalog, variants);
            report["impostor"] = AuditImpostor();
            report["lod_profile"] = AuditLodProfile();
            report["selection"] = AuditSelection(catalog);
            report["findings"] = _failures.ToList();

            File.WriteAllText(Path.Combine(outDir, "tree_pack_audit.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(outDir, "tree_pack_audit.md"), BuildMarkdown(report));
            GD.Print("LANDSCAPE_TREE_PACK_OUT=" + outDir);
            if (_failures.Count > 0) { GD.PushError("LANDSCAPE_TREE_PACK=FAIL " + string.Join(" | ", _failures)); return false; }
            GD.Print("LANDSCAPE_TREE_PACK=PASS variants=" + catalog.Count);
            return true;
        }
        catch (Exception e) { GD.PushError("LANDSCAPE_TREE_PACK=FAIL " + e); return false; }
    }

    // ---- variant table ----

    static List<Dictionary<string, object?>> AuditVariants(IReadOnlyList<ImportedTreeVariant> catalog)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var species in new[] { TreeSpecies.Maple, TreeSpecies.Fir, TreeSpecies.Pine })
        {
            var members = catalog.Where(v => v.Species == species).ToList();
            var byHeight = members.OrderBy(v => v.HeightMeters).ToList();
            foreach (var v in members)
            {
                string? parsed = ParseSizeClass(v.Name);
                string sizeClass = parsed ?? InferClass(byHeight.IndexOf(v), byHeight.Count);
                var lods = new List<Dictionary<string, object?>>();
                for (int lod = 0; lod < v.Lods.Length; lod++) lods.Add(AuditLod(v.Lods[lod], lod, v.HeightMeters));
                rows.Add(new Dictionary<string, object?>
                {
                    ["index"] = v.Index, ["species"] = species.ToString(), ["name"] = v.Name,
                    ["size_class"] = sizeClass, ["size_class_inferred"] = parsed == null,
                    ["height_m"] = Math.Round(v.HeightMeters, 3), ["width_m"] = Math.Round(v.WidthMeters, 3),
                    ["trunk_radius_m"] = Math.Round(v.TrunkRadiusMeters, 3), ["trunk_height_m"] = Math.Round(v.TrunkHeightMeters, 3),
                    ["lod_count"] = v.Lods.Length, ["lods"] = lods,
                });
            }
        }
        return rows.OrderBy(r => (int)r["index"]!).ToList();
    }

    static string? ParseSizeClass(string name)
    {
        string lower = name.ToLowerInvariant();
        foreach (string c in Classes) if (lower.Contains(c)) return c;
        return null;
    }

    // The fir pack names no sizes; rank by height (3 variants: small, medium, large).
    static string InferClass(int rank, int count) => count == 3 ? new[] { "small", "medium", "large" }[rank] : "rank" + rank;

    static Dictionary<string, object?> AuditLod(Mesh mesh, int lod, float heightMeters)
    {
        var surfaces = new List<Dictionary<string, object?>>();
        long triangles = 0;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            int indexLen = mesh is ArrayMesh am ? am.SurfaceGetArrayIndexLen(s) : 0;
            int tris = (indexLen > 0 ? indexLen : mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length) / 3;
            triangles += tris;
            var material = mesh.SurfaceGetMaterial(s) as ShaderMaterial;
            var row = new Dictionary<string, object?> { ["surface"] = s, ["triangles"] = tris };
            if (material == null) { row["material"] = mesh.SurfaceGetMaterial(s)?.GetType().Name ?? "none"; surfaces.Add(row); continue; }
            int mode = material.GetShaderParameter("tree_foliage_mode").AsInt32();
            row["shader"] = material.Shader?.ResourcePath ?? "";
            row["foliage_mode"] = mode;
            row["role"] = mode == 0 ? "bark" : mode == 1 ? "foliage" : mode == 2 ? "billboard" : "mode" + mode;
            row["alpha_cutout"] = material.GetShaderParameter("use_alpha_cutout").AsBool();
            var textures = new Dictionary<string, object?>();
            foreach (string uniform in TextureUniforms)
            {
                var texture = material.GetShaderParameter(uniform).AsGodotObject() as Texture2D;
                textures[uniform] = texture == null ? null : new Dictionary<string, object?> { ["size"] = texture.GetWidth() + "x" + texture.GetHeight(), ["path"] = texture.ResourcePath };
            }
            row["textures"] = textures;
            row["has_normal_flag"] = material.GetShaderParameter("has_normal_texture").AsBool();
            row["has_metallic_flag"] = material.GetShaderParameter("has_metallic_texture").AsBool();
            row["has_roughness_flag"] = material.GetShaderParameter("has_roughness_texture").AsBool();
            surfaces.Add(row);
        }
        Aabb aabb = mesh.GetAabb();
        return new Dictionary<string, object?>
        {
            ["lod"] = lod, ["surface_count"] = mesh.GetSurfaceCount(), ["triangles"] = triangles,
            ["aabb_height_norm"] = Math.Round(aabb.Size.Y, 4), ["aabb_height_m"] = Math.Round(aabb.Size.Y * heightMeters, 3),
            ["surfaces"] = surfaces,
        };
    }

    // ---- contract assertions ----

    void AssertContracts(IReadOnlyList<ImportedTreeVariant> catalog, List<Dictionary<string, object?>> rows)
    {
        Check(ImportedTreeCatalog.VariantCount == 30 && catalog.Count == 30, "VariantCount is not 30 (" + catalog.Count + ")");
        int maple = catalog.Count(v => v.Species == TreeSpecies.Maple), fir = catalog.Count(v => v.Species == TreeSpecies.Fir), pine = catalog.Count(v => v.Species == TreeSpecies.Pine);
        Check(maple == 12 && fir == 3 && pine == 15, $"species split is {maple}/{fir}/{pine}, expected 12/3/15");
        foreach (var row in rows)
        {
            string name = (string)row["name"]!;
            var lods = (List<Dictionary<string, object?>>)row["lods"]!;
            Check(lods.Count == 4, name + " has " + lods.Count + " LODs");
            foreach (var lod in lods)
            {
                int surfaces = (int)lod["surface_count"]!;
                var roles = string.Join("+", ((List<Dictionary<string, object?>>)lod["surfaces"]!).Select(s => s.TryGetValue("role", out var r) ? r : "?"));
                // Contract: every LOD has separate bark and foliage surfaces. Exception named by the audit: LOD3 is one billboard surface.
                int lodIndex = (int)lod["lod"]!;
                if (lodIndex < 3) Check(surfaces == 2 && roles == "bark+foliage", $"{name} LOD{lodIndex} surfaces={surfaces} roles={roles}, expected bark+foliage");
                else Check(surfaces == 1 && roles == "billboard", $"{name} LOD3 surfaces={surfaces} roles={roles}, expected one billboard surface (plan 7.1 claims bark and foliage separate in every LOD)");
                if (lodIndex == 3) Check((long)lod["triangles"]! <= 64, $"{name} LOD3 has {lod["triangles"]} triangles, expected 64 or fewer");
            }
        }
    }

    // ---- shared far impostor ----

    Dictionary<string, object?> AuditImpostor()
    {
        var result = new Dictionary<string, object?> { ["source"] = "ImportedTreeImpostorCatalog (scripts/Vegetation/ImportedTreeImpostorCatalog.cs); ImportedTreeCatalog.Prepared.cs holds no impostor" };
        try
        {
            var impostor = ImportedTreeImpostorCatalog.Load();
            result["layer_count"] = impostor.VariantCount;
            result["texture_layer_size"] = ImportedTreeImpostorCatalog.TextureLayerSize;
            result["shared_mesh_vertex_count"] = impostor.VertexCount;
            result["expanded_vertex_counts"] = impostor.ExpandedVertexCounts.ToList();
            result["shader"] = impostor.Material.Shader?.ResourcePath ?? "";
            var arrays = new Dictionary<string, object?>();
            foreach (string name in new[] { "albedo_layers", "normal_layers", "metallic_roughness_layers" })
                if (impostor.Material.GetShaderParameter(name).AsGodotObject() is Texture2DArray array)
                    arrays[name] = new Dictionary<string, object?> { ["layers"] = array.GetLayers(), ["size"] = array.GetWidth() + "x" + array.GetHeight(), ["format"] = array.GetFormat().ToString() };
            foreach (string name in new[] { "vertex_data", "normal_data", "uv_data", "tangent_data", "attachment_data", "variant_parameters" })
                if (impostor.Material.GetShaderParameter(name).AsGodotObject() is Texture2D data)
                    arrays[name] = new Dictionary<string, object?> { ["size"] = data.GetWidth() + "x" + data.GetHeight() };
            result["textures"] = arrays;
        }
        catch (Exception e) { result["error"] = e.Message; _failures.Add("impostor catalog failed to load headless: " + e.Message); }
        return result;
    }

    // ---- compact LOD profile defaults ----

    Dictionary<string, object?> AuditLodProfile()
    {
        var layer = new RingBiomeDecorationLayer();
        var result = new Dictionary<string, object?>();
        try
        {
            result["layer_defaults"] = new Dictionary<string, object?>
            {
                ["UseCompactTreeLodProfile"] = layer.UseCompactTreeLodProfile,
                ["TreeNearDetailFadeStartMeters"] = layer.TreeNearDetailFadeStartMeters,
                ["TreeNearDetailFadeEndMeters"] = layer.TreeNearDetailFadeEndMeters,
                ["TreeBillboardFadeStartMeters"] = layer.TreeBillboardFadeStartMeters,
                ["TreeBillboardFadeEndMeters"] = layer.TreeBillboardFadeEndMeters,
                ["Lod1DistanceMeters"] = layer.Lod1DistanceMeters,
                ["Lod2DistanceMeters"] = layer.Lod2DistanceMeters,
                ["Lod3DistanceMeters"] = layer.Lod3DistanceMeters,
                ["MinimumLod5DistanceMeters"] = layer.MinimumLod5DistanceMeters,
                ["MaximumLod5DistanceMeters"] = layer.MaximumLod5DistanceMeters,
                ["VisibilityRadiusMeters"] = layer.VisibilityRadiusMeters,
            };
        }
        finally { layer.Free(); }
        var presets = new Dictionary<string, object?>();
        foreach (RingTerrainLodCoordinator.QualityPreset preset in new[] { RingTerrainLodCoordinator.QualityPreset.Low, RingTerrainLodCoordinator.QualityPreset.Medium, RingTerrainLodCoordinator.QualityPreset.High, RingTerrainLodCoordinator.QualityPreset.UltraVideo })
        {
            var t = RingGraphicsQualityController.PresetTrees(preset);
            presets[preset.ToString()] = new Dictionary<string, object?> { ["near_start"] = t.NearStart, ["near_end"] = t.NearEnd, ["billboard_start"] = t.BillboardStart, ["billboard_end"] = t.BillboardEnd, ["sun_shadow"] = t.SunShadow };
        }
        result["quality_preset_tree_distances"] = presets;
        result["user_override_fields"] = new Dictionary<string, object?>
        {
            ["GameplayVegetationDefaults.UseCompactTreeLodProfile"] = "UseCompactTreeLodProfile",
            ["GameplayVegetationDefaults.TreeNearDetailFadeStartMeters"] = "TreeNearDetailFadeStartMeters",
            ["GameplayVegetationDefaults.TreeNearDetailFadeEndMeters"] = "TreeNearDetailFadeEndMeters",
            ["GameplayVegetationDefaults.TreeBillboardFadeStartMeters"] = "TreeBillboardFadeStartMeters",
            ["GameplayVegetationDefaults.TreeBillboardFadeEndMeters"] = "TreeBillboardFadeEndMeters",
            ["unmapped (no GameplayVegetationDefaults field)"] = "Lod1DistanceMeters, Lod2DistanceMeters, Lod3DistanceMeters, MinimumLod5DistanceMeters, MaximumLod5DistanceMeters, VisibilityRadiusMeters",
        };
        return result;
    }

    // ---- selection check ----

    Dictionary<string, object?> AuditSelection(IReadOnlyList<ImportedTreeVariant> catalog)
    {
        var production = GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres") ?? throw new InvalidOperationException("terrain settings missing");
        var copy = (VoxelTerrainSettings)production.Duplicate(true);
        var recipe = TerrainGenerationParameters.FromJson(TerrainGenerationParameters.WeatherLungDefault().CanonicalJson);
        copy.Seed = 1337; copy.BedrockY = -8000; copy.SeaLevelY = 0;
        copy.SurfaceGenerationRecipeJson = recipe.CanonicalJson; copy.SurfaceGenerationContractId = "surface_runtime_generation_v1";
        copy.EnableUnified3dDensityTerrain = false; copy.EnableProceduralCaves = false; copy.EnableNaturalOverhangs = false; copy.EnableDesignedCaveDensityComposition = false;
        using var sampler = new RingTerrainSampler(copy, true, Circumference, Circumference / Math.Tau, Width, 8400);
        var forest = new ForestRegionSampler(sampler.Seed, Circumference, Width);
        long cells = (long)Math.Ceiling(Circumference / Cell);

        // Fallback pose set (T0-1 fixture absent): the first 64 distinct production tiles whose lattice point is DenseForest.
        var tiles = new List<(long Tx, long Tz, long Around, int Across)>();
        var seen = new HashSet<(long, long)>();
        int scanned = 0;
        for (int i = 0; i < 400 && tiles.Count < TileCount; i++)
            for (int j = 0; j < 40 && tiles.Count < TileCount; j++)
            {
                long around = (long)Math.Floor(i * 78539.25) ;
                int across = 9000 + j * 7500;
                scanned++;
                TerrainSample terrain = sampler.GetTerrainSample(around, across);
                if (forest.Sample(around, across, terrain, SlopeDegrees(sampler, around, across)).Habitat != ForestHabitat.DenseForest) continue;
                long tx = (long)Math.Floor(around / Cell) / 8, tz = (long)Math.Floor(across / Cell) / 8;
                if (seen.Add((tx, tz))) tiles.Add((tx, tz, around, across));
            }
        Check(tiles.Count == TileCount, $"only {tiles.Count} DenseForest tiles found in the lattice ({scanned} points scanned)");

        var counts = new int[catalog.Count];
        int treeCount = 0;
        foreach (var tile in tiles)
            foreach (var record in RingBiomeDecorationLayer.BuildImportedForestReviewTile(sampler, tile.Tx, tile.Tz, cells, Cell, copy.Seed, Circumference, Width))
            { counts[record.Variant]++; treeCount++; }

        var species = new Dictionary<string, object?>();
        foreach (var sp in new[] { TreeSpecies.Maple, TreeSpecies.Fir, TreeSpecies.Pine })
        {
            var members = catalog.Where(v => v.Species == sp).ToList();
            int n = members.Sum(v => counts[v.Index]);
            double p = 1.0 / members.Count;
            double se = n > 0 ? Math.Sqrt(p * (1 - p) / n) : double.NaN;
            var perVariant = new List<Dictionary<string, object?>>();
            double maxZ = 0;
            foreach (var v in members)
            {
                double share = n > 0 ? (double)counts[v.Index] / n : 0;
                double z = n > 0 ? (share - p) / se : double.NaN;
                maxZ = Math.Max(maxZ, Math.Abs(z));
                perVariant.Add(new Dictionary<string, object?> { ["index"] = v.Index, ["name"] = v.Name, ["count"] = counts[v.Index], ["share"] = Math.Round(share, 4), ["z"] = Math.Round(z, 2) });
            }
            double sapling = n > 0 ? members.Where(v => ParseSizeClass(v.Name) == "sapling").Sum(v => counts[v.Index]) / (double)n : 0;
            Check(n >= 5 * members.Count, $"{sp}: only {n} trees, too few for the uniformity check");
            Check(maxZ <= 3.0, $"{sp}: variant share deviates from uniform by {maxZ:F2} standard errors (limit 3)");
            species[sp.ToString()] = new Dictionary<string, object?> { ["trees"] = n, ["expected_share"] = Math.Round(p, 4), ["max_abs_z"] = Math.Round(maxZ, 2), ["sapling_share"] = Math.Round(sapling, 4), ["variants"] = perVariant };
        }
        return new Dictionary<string, object?>
        {
            ["tiles"] = tiles.Count, ["lattice_points_scanned"] = scanned, ["total_trees"] = treeCount,
            ["pose_source"] = "fallback: first 64 distinct 240 m tiles at DenseForest lattice points (around floor(i*78539.25), across 9000+j*7500); T0-1 fixture and LandscapeWorld not present",
            ["sampler"] = "production settings resource, seed 1337, weather-lung v8 default recipe, no landform/habitat overrides beyond the launcher's certified ones listed in the spec",
            ["species"] = species,
        };
    }

    static float SlopeDegrees(RingTerrainSampler s, long a, int z)
    {
        const int d = 2;
        float x = s.GetTerrainSample(a + d, z).SurfaceHeight - s.GetTerrainSample(a - d, z).SurfaceHeight;
        float q = s.GetTerrainSample(a, z + d).SurfaceHeight - s.GetTerrainSample(a, z - d).SurfaceHeight;
        return Mathf.RadToDeg(Mathf.Atan(Mathf.Sqrt(x * x + q * q) / (2 * d)));
    }

    // ---- markdown ----

    static string BuildMarkdown(Dictionary<string, object?> report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Tree pack audit (T0-5)\n");
        sb.AppendLine($"Catalog loaded from: {report["catalog_loaded_from"]}\n");
        sb.AppendLine("## Variants\n");
        sb.AppendLine("| # | Species | Name | Class | H m | W m | Trunk r | LOD0 tris | LOD1 tris | LOD2 tris | LOD3 tris | Surfaces L0/L1/L2/L3 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var v in (List<Dictionary<string, object?>>)report["variants"]!)
        {
            var lods = (List<Dictionary<string, object?>>)v["lods"]!;
            sb.AppendLine($"| {v["index"]} | {v["species"]} | {v["name"]} | {v["size_class"]}{((bool)v["size_class_inferred"]! ? " (inferred)" : "")} | {v["height_m"]} | {v["width_m"]} | {v["trunk_radius_m"]} | {lods[0]["triangles"]} | {lods[1]["triangles"]} | {lods[2]["triangles"]} | {lods[3]["triangles"]} | {string.Join("/", lods.Select(l => l["surface_count"]))} |");
        }
        sb.AppendLine("\nFull per-surface materials and texture sizes are in tree_pack_audit.json.");
        return sb.ToString();
    }

    void Check(bool ok, string message) { if (!ok) _failures.Add(message); }
    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
