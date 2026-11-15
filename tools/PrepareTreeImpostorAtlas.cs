using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>TR-7a: writes lolipop_impostor_atlas.res and its manifest, the mip table, and one contact sheet per variant (pack picture beside the bake).</summary>
public partial class PrepareTreeImpostorAtlas : RefCounted
{
    /// <summary>Bakes every variant (variants are independent, so they run in parallel; each bake is single-threaded and deterministic).</summary>
    public static TreeImpostorAtlasBaker.VariantBake[] BakeAll(IReadOnlyList<ImportedTreeVariant> variants, out TreeImpostorAtlasBaker.Input[] inputs)
    {
        var extracted = variants.Select(TreeImpostorAtlasBaker.Extract).ToArray();
        var bakes = new TreeImpostorAtlasBaker.VariantBake[extracted.Length];
        Parallel.For(0, extracted.Length, i => bakes[i] = TreeImpostorAtlasBaker.Bake(extracted[i]));
        inputs = extracted;
        return bakes;
    }

    public static PreparedTreeImpostorAtlas Pack(IReadOnlyList<ImportedTreeVariant> variants, TreeImpostorAtlasBaker.VariantBake[] bakes)
    {
        var res = new PreparedTreeImpostorAtlas
        {
            ResourceSceneUniqueId = "ImpostorAtlas", // Godot names sub-resources randomly on save; fixed ids make the file identical run to run
            VariantNames = variants.Select(v => v.Name).ToArray(),
            SourceHashes = variants.Select(ImportedTreeImpostorAtlasBake.HashSources).ToArray(),
            AlbedoBase = new byte[variants.Count * PreparedTreeImpostorAtlas.AlbedoLayerBytes],
            NormalMips = new byte[variants.Count * PreparedTreeImpostorAtlas.NormalLayerBytes]
        };
        for (int i = 0; i < bakes.Length; i++)
        {
            Array.Copy(bakes[i].Albedo, 0, res.AlbedoBase, i * PreparedTreeImpostorAtlas.AlbedoLayerBytes, PreparedTreeImpostorAtlas.AlbedoLayerBytes);
            Array.Copy(bakes[i].NormalMips, 0, res.NormalMips, i * PreparedTreeImpostorAtlas.NormalLayerBytes, PreparedTreeImpostorAtlas.NormalLayerBytes);
        }
        return res;
    }

    public const ResourceSaver.SaverFlags SaveFlags = ResourceSaver.SaverFlags.Compress;

    public int Run(string output, string manifestPath, string scratchDir)
    {
        try
        {
            IReadOnlyList<ImportedTreeVariant> variants = ImportedTreeCatalog.Load();
            var bakes = BakeAll(variants, out var inputs);
            PreparedTreeImpostorAtlas res = Pack(variants, bakes);
            if (ResourceSaver.Save(res, output, SaveFlags) != Error.Ok) throw new IOException("save " + output);
            string artifactHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ProjectSettings.GlobalizePath(output))));
            var entries = new List<object>();
            for (int i = 0; i < variants.Count; i++)
                entries.Add(new
                {
                    index = i, name = variants[i].Name, source_hash = res.SourceHashes[i], cards = bakes[i].CardCount, top_cards = bakes[i].TopCards,
                    owned_texels = bakes[i].OwnedTexels, covered_texels = bakes[i].CoveredTexels, fit_residual = Math.Round(bakes[i].WorstFitResidual, 6)
                });
            var manifest = new
            {
                schema_version = PreparedTreeImpostorAtlas.CurrentSchemaVersion, tool_version = PreparedTreeImpostorAtlas.CurrentToolVersion,
                engine_identity = PreparedImportedTreeCatalogIdentity.CurrentEngineIdentity, resource_path = "resources/trees/prepared/lolipop_impostor_atlas.res",
                artifact_sha256 = artifactHash, layer_size = PreparedTreeImpostorAtlas.LayerSize, sub_samples = TreeImpostorAtlasBaker.Sub,
                method = "orthographic CPU raster of the authored LOD2 along each LOD3 card normal into the card's UV region; normal mips selected (never averaged) per card by error diffusion on the six-axis clamped light response among the covered children",
                variants = entries
            };
            File.WriteAllText(ProjectSettings.GlobalizePath(ImportedTreeImpostorAtlasBake.ManifestPath), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            Directory.CreateDirectory(ProjectSettings.GlobalizePath(scratchDir));
            File.WriteAllText(ProjectSettings.GlobalizePath(scratchDir + "/mip_table.md"), MipTable(variants, bakes, out _));
            WriteSheets(variants, bakes, scratchDir + "/sheets");
            GD.Print($"PREPARE_TREE_IMPOSTOR_ATLAS=PASS variants={variants.Count} sha256={artifactHash[..16]}");
            return 0;
        }
        catch (Exception exception)
        {
            GD.PushError("PREPARE_TREE_IMPOSTOR_ATLAS=FAIL " + exception);
            return 1;
        }
    }

    /// <summary>Per variant and mip level 0 to 3: the sun-only response at noon, 30 and 8 degrees, and its difference from level 0.</summary>
    public static string MipTable(IReadOnlyList<ImportedTreeVariant> variants, TreeImpostorAtlasBaker.VariantBake[] bakes, out float worstRelative)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| # | variant | sun | L0 | L1 | L2 | L3 | worst L1-3 vs L0 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        worstRelative = 0;
        for (int i = 0; i < variants.Count; i++)
            foreach (float el in TreeImpostorAtlasBaker.SunElevations)
            {
                float[] r = Enumerable.Range(0, 4).Select(l => TreeImpostorAtlasBaker.SunResponse(bakes[i], l, el)).ToArray();
                float worst = Enumerable.Range(1, 3).Max(l => r[0] > 1e-6f ? MathF.Abs(r[l] - r[0]) / r[0] : 0);
                worstRelative = MathF.Max(worstRelative, worst);
                sb.AppendLine($"| {i} | {variants[i].Name} | {(el >= 89 ? "noon" : el + " deg")} | {r[0]:F4} | {r[1]:F4} | {r[2]:F4} | {r[3]:F4} | {worst * 100:F2}% |");
            }
        sb.AppendLine($"worst relative difference over all variants, suns and levels 1 to 3: {worstRelative * 100:F2}%");
        return sb.ToString();
    }

    // Contact sheet: pack albedo | baked albedo | pack normal | baked normal, 2x, on a grey background, card UV rectangles outlined (top card in yellow).
    private static void WriteSheets(IReadOnlyList<ImportedTreeVariant> variants, TreeImpostorAtlasBaker.VariantBake[] bakes, string dir)
    {
        Directory.CreateDirectory(ProjectSettings.GlobalizePath(dir));
        const int panel = PreparedTreeImpostorAtlas.LayerSize * 2;
        for (int v = 0; v < variants.Count; v++)
        {
            ShaderMaterial? pack = variants[v].Lods[3].SurfaceGetMaterial(0) as ShaderMaterial;
            Image packAlbedo = LoadPack(pack, "albedo_texture", new Color(1, 1, 1, 0)), packNormal = LoadPack(pack, "normal_texture", new Color(.5f, .5f, 1, 1));
            Image bakedAlbedo = Image.CreateFromData(PreparedTreeImpostorAtlas.LayerSize, PreparedTreeImpostorAtlas.LayerSize, false, Image.Format.Rgba8, bakes[v].Albedo);
            var normal = new byte[PreparedTreeImpostorAtlas.LayerSize * PreparedTreeImpostorAtlas.LayerSize * 4];
            Array.Copy(bakes[v].NormalMips, normal, normal.Length);
            Image bakedNormal = Image.CreateFromData(PreparedTreeImpostorAtlas.LayerSize, PreparedTreeImpostorAtlas.LayerSize, false, Image.Format.Rgba8, normal);
            var sheet = Image.CreateEmpty(panel * 4, panel, false, Image.Format.Rgba8);
            sheet.Fill(new Color(.35f, .35f, .35f, 1));
            int column = 0;
            foreach ((Image image, bool alpha) in new[] { (packAlbedo, true), (bakedAlbedo, true), (packNormal, false), (bakedNormal, false) })
            {
                Image big = (Image)image.Duplicate();
                big.Resize(panel, panel, Image.Interpolation.Nearest);
                sheet.BlendRect(big, new Rect2I(0, 0, panel, panel), new Vector2I(column * panel, 0));
                column++;
            }
            // outlines on the baked albedo and baked normal panels
            for (int c = 0; c < bakes[v].Cards.Count; c++)
            {
                Vector4 r = bakes[v].Cards[c].UvRect; Color colour = bakes[v].Cards[c].IsTop ? new Color(1, 1, 0) : new Color(1, .2f, .2f);
                foreach (int col in new[] { 1, 3 })
                {
                    int x0 = col * panel + (int)(r.X * panel), x1 = col * panel + (int)(r.Z * panel) - 1, y0 = (int)(r.Y * panel), y1 = (int)(r.W * panel) - 1;
                    for (int x = x0; x <= x1; x++) { Put(sheet, x, y0, colour); Put(sheet, x, y1, colour); }
                    for (int y = y0; y <= y1; y++) { Put(sheet, x0, y, colour); Put(sheet, x1, y, colour); }
                }
            }
            sheet.SavePng(ProjectSettings.GlobalizePath($"{dir}/v{v:D2}_{variants[v].Name}.png"));
        }
    }

    private static void Put(Image image, int x, int y, Color c) { if (x >= 0 && y >= 0 && x < image.GetWidth() && y < image.GetHeight()) image.SetPixel(x, y, c); }

    private static Image LoadPack(ShaderMaterial? material, string parameter, Color fallback)
    {
        Image? source = (material?.GetShaderParameter(parameter).AsGodotObject() as Texture2D)?.GetImage();
        Image image;
        if (source != null && !source.IsEmpty())
        {
            image = (Image)source.Duplicate();
            if (image.IsCompressed()) image.Decompress();
            image.Convert(Image.Format.Rgba8);
            image.Resize(PreparedTreeImpostorAtlas.LayerSize, PreparedTreeImpostorAtlas.LayerSize, Image.Interpolation.Lanczos);
        }
        else { image = Image.CreateEmpty(PreparedTreeImpostorAtlas.LayerSize, PreparedTreeImpostorAtlas.LayerSize, false, Image.Format.Rgba8); image.Fill(fallback); }
        return image;
    }
}
