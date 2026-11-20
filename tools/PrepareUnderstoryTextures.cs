using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Vegetation.Understory;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T6-4b (T6 design A7): builds the understory texture arrays under resources/trees/prepared/understory/ (read by RingworldTechDemo.Vegetation.Understory.UnderstoryCatalog).
/// Layers are in TreeSpecies order (maple, fir, pine).
/// Foliage (1024 x 1024, mips down to 1 x 1): the albedo layers are the levels of the distance-stable leaf mips (lolipop_leaf_mips.res, DXT5, TR-7c) from the level whose size is 1024 downwards, byte for byte (no decompression), so the cutout keeps its
/// stable coverage; the normal layers are the same levels of the imports' own RGTC chains. Bark (512 x 512): the source PNGs resized (Lanczos) and compressed here (the imports are not square), DXT5 albedo and RGTC normals.
/// SF-1 adds the stand filler caster's billboards (BillboardAlbedo, schema 2): the authored LOD3 albedo of each UnderstoryCatalog.StandFillSources variant, resized (Lanczos) to 512 x 512, DXT5 with mips; the layers above are unchanged.
/// Also records the mean roughness (green) and metallic (blue) of each layer's metallic-roughness map: the carrier shader uses them as per-layer constants. Deterministic; writes understory_textures.manifest.json with SHA-256 of every output.
/// </summary>
public partial class PrepareUnderstoryTextures : RefCounted
{
    public const string ToolVersion = "t6-4b-2";
    public const string OutputFolder = "res://resources/trees/prepared/understory/";
    public const int FoliageSize = 1024, BarkSize = 512, BillboardSize = 512;
    private static readonly string[] Species = { "maple", "fir", "pine" };

    public int Run()
    {
        try
        {
            PreparedTreeLeafMips? leaf = ImportedTreeLeafMipCatalog.Load();
            if (leaf == null) throw new InvalidOperationException("leaf mip resource unavailable");
            string folder = ProjectSettings.GlobalizePath(OutputFolder);
            Directory.CreateDirectory(folder);
            var foliageAlbedo = new Godot.Collections.Array<Image>();
            var foliageNormal = new Godot.Collections.Array<Image>();
            var barkAlbedo = new Godot.Collections.Array<Image>();
            var barkNormal = new Godot.Collections.Array<Image>();
            var billboardAlbedo = new Godot.Collections.Array<Image>();
            var billboards = new List<object>();
            var layers = new List<object>();
            var constants = new Vector4[3];
            for (int s = 0; s < 3; s++)
            {
                string name = Species[s];
                int atlas = Array.IndexOf(leaf.AtlasNames, name);
                if (atlas < 0) throw new InvalidOperationException("leaf mips have no atlas " + name);
                Image albedo = leaf.Textures[atlas].GetImage() ?? throw new InvalidOperationException("no image for " + name);
                if (albedo.GetFormat() != Image.Format.Dxt5) throw new InvalidOperationException($"{name} leaf mips are {albedo.GetFormat()}, expected Dxt5");
                foliageAlbedo.Add(SubChain(albedo, FoliageSize));
                string source = leaf.SourcePaths[atlas];
                string normalPath = source.Replace("baseColor", "normal");
                Image normal = (ResourceLoader.Load<Texture2D>(normalPath) ?? throw new InvalidOperationException("missing " + normalPath)).GetImage();
                foliageNormal.Add(SubChain(normal, FoliageSize));
                (double fr, double fm) = Means(source.Replace("baseColor", "metallicRoughness"));
                string textures = source[..(source.LastIndexOf('/') + 1)];
                (double br, double bm) = Means(textures + "Bark_Mat_metallicRoughness.png");
                barkAlbedo.Add(Bark(textures + "Bark_Mat_baseColor.png", false));
                barkNormal.Add(Bark(textures + "Bark_Mat_normal.png", true));
                constants[s] = new Vector4((float)fr, (float)fm, (float)br, (float)bm);
                layers.Add(new { species = name, foliage_source = source, foliage_normal_source = normalPath, foliage_roughness_mean = fr, foliage_metallic_mean = fm, bark_roughness_mean = br, bark_metallic_mean = bm,
                    foliage_albedo_format = albedo.GetFormat().ToString(), foliage_normal_format = normal.GetFormat().ToString() });
            }
            // SF-1: the stand filler caster's billboards: the authored LOD3 albedo of each UnderstoryCatalog.StandFillSources variant (the tree layer's own LOD2 shadow proxy), in that order.
            IReadOnlyList<ImportedTreeVariant> catalog = ImportedTreeCatalog.Load();
            foreach (int source in UnderstoryCatalog.StandFillSources)
            {
                ImportedTreeVariant variant = catalog[source];
                var material = variant.Lods[3].SurfaceGetMaterial(0) as ShaderMaterial ?? throw new InvalidOperationException($"variant {source} LOD3 has no shader material");
                string billboardPath = material.GetShaderParameter("albedo_texture").As<Texture2D>()?.ResourcePath ?? throw new InvalidOperationException($"variant {source} LOD3 has no albedo texture");
                billboardAlbedo.Add(Square(billboardPath, BillboardSize, false));
                billboards.Add(new { variant = source, name = variant.Name, source = billboardPath });
            }
            long bytes = 0;
            foreach (var images in new[] { foliageAlbedo, foliageNormal, barkAlbedo, barkNormal }) foreach (Image image in images) bytes += image.GetData().Length;
            long billboardBytes = 0;
            foreach (Image image in billboardAlbedo) billboardBytes += image.GetData().Length;
            var resource = new PreparedUnderstoryTextures
            {
                ToolVersion = ToolVersion, CatalogFingerprint = leaf.CatalogFingerprint, FoliageAlbedo = foliageAlbedo, FoliageNormal = foliageNormal, BarkAlbedo = barkAlbedo, BarkNormal = barkNormal, BillboardAlbedo = billboardAlbedo, Constants = constants,
            };
            if (ResourceSaver.Save(resource, OutputFolder + "understory_textures.res") != Error.Ok) throw new InvalidOperationException("could not save understory_textures.res");
            string path = Path.Combine(folder, "understory_textures.res");
            var outputs = new { sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), file_bytes = new FileInfo(path).Length, vram_bytes = bytes,
                foliage = $"{foliageAlbedo[0].GetWidth()} px, {foliageAlbedo[0].GetMipmapCount()} mips, {foliageAlbedo[0].GetFormat()} / {foliageNormal[0].GetFormat()}", bark = $"{barkAlbedo[0].GetWidth()} px, {barkAlbedo[0].GetMipmapCount()} mips, {barkAlbedo[0].GetFormat()} / {barkNormal[0].GetFormat()}",
                billboard = $"{billboardAlbedo[0].GetWidth()} px x {billboardAlbedo.Count}, {billboardAlbedo[0].GetMipmapCount()} mips, {billboardAlbedo[0].GetFormat()}" };
            var manifest = new { schema_version = PreparedUnderstoryTextures.CurrentSchemaVersion, tool_version = ToolVersion, catalog_fingerprint = leaf.CatalogFingerprint, leaf_mips_tool_version = leaf.ToolVersion, vram_bytes = bytes, billboard_vram_bytes = billboardBytes, layers, billboards, outputs };
            File.WriteAllText(Path.Combine(folder, "understory_textures.manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            GD.Print($"PREPARE_UNDERSTORY_TEXTURES=PASS vram {bytes / 1048576.0:0.00} MiB, billboards {billboardBytes / 1048576.0:0.00} MiB");
            return 0;
        }
        catch (Exception error)
        {
            GD.PrintErr("PREPARE_UNDERSTORY_TEXTURES=FAIL " + error);
            GD.Print("PREPARE_UNDERSTORY_TEXTURES=FAIL " + error.Message);
            return 1;
        }
    }

    /// <summary>The mip chain of a compressed image from the level whose width is <paramref name="size"/> down to 1 x 1, byte for byte.</summary>
    internal static Image SubChain(Image image, int size)
    {
        if (!image.IsCompressed() || image.GetMipmapCount() <= 0) throw new InvalidOperationException("expected a compressed image with mipmaps");
        int level = 0, width = image.GetWidth();
        while (width > size) { width >>= 1; level++; }
        if (width != size || image.GetHeight() >> level != size) throw new InvalidOperationException($"no {size} x {size} level in a {image.GetWidth()} x {image.GetHeight()} image");
        byte[] data = image.GetData();
        int offset = (int)image.GetMipmapOffset(level);
        byte[] sub = data[offset..];
        Image result = Image.CreateFromData(size, size, true, image.GetFormat(), sub);
        if (result == null || result.IsEmpty()) throw new InvalidOperationException("sub-chain image creation failed");
        return result;
    }

    private static Image Bark(string path, bool normal) => Square(path, BarkSize, normal);

    /// <summary>The source image resized (Lanczos) to size x size, with mips, compressed DXT5 (or RGTC for a normal map).</summary>
    private static Image Square(string path, int size, bool normal)
    {
        Image image = Image.LoadFromFile(ProjectSettings.GlobalizePath(path)) ?? throw new InvalidOperationException("cannot read " + path);
        if (image.IsCompressed()) image.Decompress();
        image.Convert(Image.Format.Rgba8);
        image.Resize(size, size, Image.Interpolation.Lanczos);
        image.GenerateMipmaps(normal);
        Error error = image.CompressFromChannels(Image.CompressMode.S3Tc, normal ? Image.UsedChannels.Rg : Image.UsedChannels.Rgba);
        if (error != Error.Ok) throw new InvalidOperationException($"cannot compress {path}: {error}");
        return image;
    }

    private static (double Roughness, double Metallic) Means(string path)
    {
        Image image = Image.LoadFromFile(ProjectSettings.GlobalizePath(path)) ?? throw new InvalidOperationException("cannot read " + path);
        if (image.IsCompressed()) image.Decompress();
        image.Convert(Image.Format.Rgba8);
        byte[] data = image.GetData();
        double g = 0, b = 0;
        int pixels = data.Length / 4;
        for (int i = 0; i < data.Length; i += 4) { g += data[i + 1]; b += data[i + 2]; }
        return (g / pixels / 255.0, b / pixels / 255.0);
    }
}
