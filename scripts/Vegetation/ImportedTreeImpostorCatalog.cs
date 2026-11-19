using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Vegetation.Canopy;

namespace RingworldTechDemo.Vegetation;

/// <summary>One shared LOD3 mesh whose per-instance custom X selects an authored tree billboard.</summary>
public sealed class ImportedTreeImpostorCatalog
{
    public const int TextureLayerSize = 256;
    public Mesh Mesh { get; }
    public ShaderMaterial Material { get; }
    /// <summary>One camera-facing quad per tree (two triangles) drawn by the same shader with far_quad_mode on; texture, bounds and look come from the largest LOD3 plane (TR-6).</summary>
    public Mesh FarQuadMesh { get; }
    public ShaderMaterial FarQuadMaterial { get; }
    /// <summary>The LOD3 card (largest plane component) each variant's far quad copies, as stored in variant_parameters rows 3 and 4.</summary>
    public IReadOnlyList<FarQuadPlane> FarQuadPlanes { get; }
    /// <summary>The variant's horizontal LOD3 card (TR-6b), the far quad's second quad, as stored in variant_parameters rows 5 to 7.</summary>
    public IReadOnlyList<FarQuadTop> FarQuadTops { get; }
    /// <summary>12 vertices per tree: the camera-facing quad (vertices 0 to 5) and the horizontal card (6 to 11).</summary>
    public const int FarQuadVertexCount = 12;
    /// <summary>Rows 0 to 7 as before; row 8 = crown hull (centre y, horizontal semi-axis, vertical semi-axis, kappa), row 9 = (centre x, centre z, k_species, 1 = valid), row 10 = leaf inclination class weights, see TreeCrownHullTable.Pack.</summary>
    public const int ParameterRowCount = 11;
    /// <summary>The alpha cutoff each variant's material authored (used while there is no calibration).</summary>
    public IReadOnlyList<float> DefaultCutoffs { get; }
    /// <summary>Hash of everything the calibration depends on: the variant count and the base level of the card albedo atlas (the bake's, or the pack's pictures resized), never its mips or cutoffs; a calibration made for another value is stale.</summary>
    public string CalibrationFingerprint { get; }
    /// <summary>The cutoff calibration currently applied (cards and far quad, each with its own set); null is neutral (the authored cutoffs for both).</summary>
    public ImportedTreeImpostorCalibration? Calibration { get; private set; }
    private readonly IReadOnlyList<ImportedTreeVariant> _variants;
    private readonly IReadOnlyList<FarQuadPlane> _planes;
    private readonly IReadOnlyList<FarQuadTop> _tops;
    /// <summary>The impostor atlas bake (TR-7a) in use, or null while the pack's own card pictures are in use.</summary>
    public PreparedTreeImpostorAtlas? Bake { get; private set; }
    private PreparedTreeImpostorAtlas? _loadedBake;
    private Texture2DArray? _bakedNormal, _packNormal;
    // Base level (256 x 256 RGBA8, no mips) of each variant's card picture, per source; the albedo arrays are rebuilt from these for the cutoffs in use (see RebuildAlbedoArrays).
    private Image[]? _bakedBase, _packBase;
    private readonly bool[] _cutout;
    /// <summary>Row 3 of variant_parameters: (u at the plane's left edge, u at its right edge, v at its bottom, v at its top). Row 4: (width, bottom y, top y, frame code), in catalog-normalized tree space.
    /// The frame code relates the baked normal frame of the card the quad copies to its picture, for the quad's own frame in the shader: bit 0 the card's frame sign s (the shader negates the frame
    /// of a back face; the bake compensates for the baked side), bit 1 T0 along +u, bit 2 B0 along +v, bit 3 (u x v) along N0, all as set in the bit.</summary>
    public readonly record struct FarQuadPlane(float ULeft, float URight, float VBottom, float VTop, float Width, float BottomY, float TopY, int TriangleCount, float Area, int FrameCode = 0);
    /// <summary>A variant's horizontal LOD3 card as a quad covering its UV rectangle (min u, min v, max u, max v): the tree-space XZ positions of the corners at (u min, v min), (u max, v min) and
    /// (u min, v max) (the fourth follows, the card's UVs being an affine map of the plane), the card's height, and the frame code with the same bits as <see cref="FarQuadPlane"/> relating the baked
    /// normal frame to the picture's u and v directions. CardCount is the number of connected horizontal cards found (the pack has exactly one); FrameCode is -1 when there is none or its map is degenerate.</summary>
    public readonly record struct FarQuadTop(float ULow, float VLow, float UHigh, float VHigh, Vector2 P00, Vector2 P10, Vector2 P01, float Y, int FrameCode, int CardCount, float Area);
    public int VertexCount { get; }
    public int VariantCount { get; }
    /// <summary>Expanded, unindexed source vertex count for each authored LOD3 mesh.</summary>
    public IReadOnlyList<int> ExpandedVertexCounts { get; }
    /// <summary>CPU copies used by the structural asset regression; positions are in catalog-normalized space.</summary>
    public IReadOnlyList<Vector3[]> ExpandedPositions { get; }
    public IReadOnlyList<Vector2[]> ExpandedUvs { get; }

    private static ImportedTreeImpostorCatalog? _cached;
    private ImportedTreeImpostorCatalog(ArrayMesh mesh, ShaderMaterial material, ArrayMesh quadMesh, ShaderMaterial quadMaterial, IReadOnlyList<FarQuadPlane> planes, IReadOnlyList<FarQuadTop> tops, int vertexCount,
        IReadOnlyList<int> expandedVertexCounts, IReadOnlyList<Vector3[]> expandedPositions, IReadOnlyList<Vector2[]> expandedUvs,
        IReadOnlyList<ImportedTreeVariant> variants, IReadOnlyList<float> defaultCutoffs, string fingerprint, ImportedTreeImpostorCalibration? calibration,
        PreparedTreeImpostorAtlas? bake, Image[] albedoBase, Texture2DArray? bakedNormal, Texture2DArray? packNormal, bool[] cutout)
    {
        Mesh = mesh; Material = material; FarQuadMesh = quadMesh; FarQuadMaterial = quadMaterial; FarQuadPlanes = planes; FarQuadTops = tops; VertexCount = vertexCount;
        _variants = variants; _planes = planes; _tops = tops; Bake = bake; _loadedBake = bake; _cutout = cutout; _bakedNormal = bakedNormal; _packNormal = packNormal;
        if (bake != null) _bakedBase = albedoBase; else _packBase = albedoBase;
        DefaultCutoffs = defaultCutoffs; CalibrationFingerprint = fingerprint; Calibration = calibration;
        VariantCount = ImportedTreeCatalog.VariantCount;
        ExpandedVertexCounts = expandedVertexCounts; ExpandedPositions = expandedPositions; ExpandedUvs = expandedUvs;
    }

    public static ImportedTreeImpostorCatalog Load()
    {
        if (_cached != null) return _cached;
        foreach (bool _ in BuildSteps()) { }
        return _cached!;
    }

    /// <summary>Poll and advance on the main thread, one image or lookup texture per step; Load() drains the same steps in one call.</summary>
    public static IEnumerable<bool> PrepareCooperatively()
    {
        if (_cached != null) yield break;
        foreach (bool unit in BuildSteps()) yield return unit;
    }

    private static IEnumerable<bool> BuildSteps()
    {
        if (_cached != null) yield break;
        var variants = ImportedTreeCatalog.Load();
        // TR-7a's bake replaces the pack's card pictures (albedo and shading normal); a missing or stale resource keeps them, with one warning.
        PreparedTreeImpostorAtlas? bake = ImportedTreeImpostorAtlasBake.Load(variants);
        var geometries = new List<Geometry>(variants.Count);
        int maxVertices = 0;
        Aabb bounds = new(Vector3.Zero, Vector3.Zero);
        bool hasBounds = false;
        foreach (ImportedTreeVariant variant in variants)
        {
            if (variant.Lods[3].GetSurfaceCount() != 1)
                throw new InvalidOperationException($"{variant.Name} LOD3 has multiple surfaces; one-material impostor packing is not implemented.");
            Geometry geometry = Expand(variant.Lods[3]);
            geometries.Add(geometry);
            maxVertices = Math.Max(maxVertices, geometry.Positions.Length);
            Aabb current = variant.Lods[3].GetAabb();
            bounds = hasBounds ? bounds.Merge(current) : current;
            hasBounds = true;
        }
        if (maxVertices == 0 || maxVertices % 3 != 0) throw new InvalidOperationException("Imported LOD3 billboard geometry is invalid.");
        yield return true;

        ImageTexture positions = CreateDataTexture(maxVertices, geometries, DataChannel.Position); yield return true;
        ImageTexture normals = CreateDataTexture(maxVertices, geometries, DataChannel.Normal); yield return true;
        ImageTexture uvs = CreateDataTexture(maxVertices, geometries, DataChannel.Uv); yield return true;
        ImageTexture tangents = CreateDataTexture(maxVertices, geometries, DataChannel.Tangent); yield return true;
        ImageTexture attachments = CreateDataTexture(maxVertices, geometries, DataChannel.Attachment); yield return true;
        ArrayMesh mesh = CreateSharedMesh(maxVertices, bounds);
        yield return true;

        var baseImages = new Godot.Collections.Array<Image>();
        if (bake != null) PrepareBakedBaseImages(variants.Count, bake, baseImages);
        else foreach (bool unit in PrepareBaseImages(variants, baseImages)) yield return unit;
        string fingerprint = Fingerprint(baseImages, variants.Count);
        var cutout = new bool[variants.Count];
        var defaultCutoffs = new float[variants.Count];
        for (int i = 0; i < variants.Count; i++) { cutout[i] = UsesCutout(variants[i]); defaultCutoffs[i] = AuthoredCutoff(variants[i]); }
        ImportedTreeImpostorCalibration? calibration = ImportedTreeImpostorCalibration.Load(fingerprint, variants.Count);
        // Alpha mips are built for the cutoff in use: one array for the cards and one for the far quad, shared while the two cutoff sets are equal.
        var cardImages = new Godot.Collections.Array<Image>();
        foreach (bool unit in BuildAlbedoImages(baseImages, EffectiveCutoffs(cutout, defaultCutoffs, calibration?.Cutoffs), cardImages)) yield return unit;
        Texture2DArray cardAlbedo = CreateTextureArray(cardImages, "albedo_texture"); yield return true;
        Texture2DArray quadAlbedo = cardAlbedo;
        if (calibration != null && !SameCutoffs(calibration.Cutoffs, calibration.QuadCutoffs))
        {
            var quadImages = new Godot.Collections.Array<Image>();
            foreach (bool unit in BuildAlbedoImages(baseImages, EffectiveCutoffs(cutout, defaultCutoffs, calibration.QuadCutoffs), quadImages)) yield return unit;
            quadAlbedo = CreateTextureArray(quadImages, "albedo_texture"); yield return true;
        }
        Texture2DArray normalLayers;
        if (bake != null) { normalLayers = CreateBakedNormalArray(bake, variants.Count); yield return true; }
        else
        {
            var normalImages = new Godot.Collections.Array<Image>();
            foreach (bool unit in PrepareTextureImages(variants, "normal_texture", new Color(.5f, .5f, 1, 1), normalImages)) yield return unit;
            normalLayers = CreateTextureArray(normalImages, "normal_texture"); yield return true;
        }
        var images = new Godot.Collections.Array<Image>();
        foreach (bool unit in PrepareMetallicRoughnessImages(variants, images)) yield return unit;
        Texture2DArray metallicRoughnessLayers = CreateTextureArray(images, "metallic/roughness"); yield return true;

        var planes = new List<FarQuadPlane>(geometries.Count);
        foreach (Geometry geometry in geometries) planes.Add(geometry.LargestPlane());
        var tops = new List<FarQuadTop>(geometries.Count);
        foreach (Geometry geometry in geometries) tops.Add(geometry.HorizontalCard());
        ShaderMaterial material = CreateMaterial(variants, planes, tops, defaultCutoffs, calibration, bake != null, positions, normals, uvs, tangents, attachments, cardAlbedo, normalLayers, metallicRoughnessLayers);
        mesh.SurfaceSetMaterial(0, material);
        ArrayMesh quadMesh = CreateFarQuadMesh(bounds);
        var quadMaterial = (ShaderMaterial)material.Duplicate();
        quadMaterial.SetShaderParameter("far_quad_mode", true);
        quadMaterial.SetShaderParameter("albedo_layers", quadAlbedo);
        quadMesh.SurfaceSetMaterial(0, quadMaterial);

        var counts = new List<int>(geometries.Count);
        var expandedPositions = new List<Vector3[]>(geometries.Count); var expandedUvs = new List<Vector2[]>(geometries.Count);
        foreach (Geometry geometry in geometries) { counts.Add(geometry.Positions.Length); expandedPositions.Add(geometry.Positions); expandedUvs.Add(geometry.Uvs); }
        _cached ??= new ImportedTreeImpostorCatalog(mesh, material, quadMesh, quadMaterial, planes.AsReadOnly(), tops.AsReadOnly(), maxVertices, counts.AsReadOnly(), expandedPositions.AsReadOnly(), expandedUvs.AsReadOnly(),
            variants, defaultCutoffs, fingerprint, calibration, bake, ToArray(baseImages), bake != null ? normalLayers : null, bake != null ? null : normalLayers, cutout);
    }

    /// <summary>Swap the cutoff calibration (null = the authored cutoffs for cards and quad). Rebuilds the albedo arrays, whose alpha mips are made for the cutoff in use, and republishes the parameters.
    /// The test's calibration mode and its controls use this.</summary>
    public void ApplyCalibration(ImportedTreeImpostorCalibration? calibration)
    {
        Calibration = calibration;
        RebuildAlbedoArrays();
        PublishParameters();
    }

    /// <summary>Test aid: false puts the pack's own card pictures back in place of the bake (the control of tree_billboard_match_gpu_test, which must fail without the bake); true restores the bake
    /// when one was loaded. Returns whether the bake is in use afterwards. The pack's pictures are built on first use.</summary>
    public bool UseBake(bool enabled)
    {
        PreparedTreeImpostorAtlas? wanted = enabled ? _loadedBake : null;
        if (wanted == null && _packBase == null)
        {
            var albedo = new Godot.Collections.Array<Image>(); var normal = new Godot.Collections.Array<Image>();
            foreach (bool _ in PrepareBaseImages(_variants, albedo)) { }
            foreach (bool _ in PrepareTextureImages(_variants, "normal_texture", new Color(.5f, .5f, 1, 1), normal)) { }
            _packBase = ToArray(albedo); _packNormal = CreateTextureArray(normal, "normal_texture");
        }
        Bake = wanted;
        Texture2DArray? normalArray = wanted != null ? _bakedNormal : _packNormal;
        foreach (ShaderMaterial material in new[] { Material, FarQuadMaterial }) material.SetShaderParameter("normal_layers", normalArray);
        RebuildAlbedoArrays();
        PublishParameters();
        return Bake != null;
    }

    private void RebuildAlbedoArrays()
    {
        Image[] bases = (Bake != null ? _bakedBase : _packBase)!;
        float[] cardCutoffs = EffectiveCutoffs(_cutout, DefaultCutoffs, Calibration?.Cutoffs), quadCutoffs = EffectiveCutoffs(_cutout, DefaultCutoffs, Calibration?.QuadCutoffs);
        Texture2DArray cards = BuildAlbedoArray(bases, cardCutoffs);
        Texture2DArray quads = SameCutoffs(cardCutoffs, quadCutoffs) ? cards : BuildAlbedoArray(bases, quadCutoffs);
        Material.SetShaderParameter("albedo_layers", cards); FarQuadMaterial.SetShaderParameter("albedo_layers", quads);
    }

    private static Texture2DArray BuildAlbedoArray(Image[] bases, float[] cutoffs)
    {
        var images = new Godot.Collections.Array<Image>();
        foreach (bool _ in BuildAlbedoImages(bases, cutoffs, images)) { }
        return CreateTextureArray(images, "albedo_texture");
    }

    private void PublishParameters()
    {
        ImageTexture parameters = CreateParameterTexture(_variants, _planes, _tops, DefaultCutoffs, Calibration, Bake != null);
        Material.SetShaderParameter("variant_parameters", parameters);
        FarQuadMaterial.SetShaderParameter("variant_parameters", parameters);
    }

    private static bool UsesCutout(ImportedTreeVariant variant) =>
        variant.Lods[3].SurfaceGetMaterial(0) is ShaderMaterial material && material.GetShaderParameter("use_alpha_cutout").AsBool();

    /// <summary>The cutoff each variant's alpha mips are built for: the calibrated one (or the authored one without), -1 for a variant whose material is not alpha-cut.</summary>
    private static float[] EffectiveCutoffs(bool[] cutout, IReadOnlyList<float> defaults, float[]? calibrated)
    {
        var result = new float[cutout.Length];
        for (int i = 0; i < result.Length; i++) result[i] = cutout[i] ? (calibrated != null ? calibrated[i] : defaults[i]) : -1f;
        return result;
    }

    private static bool SameCutoffs(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static Image[] ToArray(Godot.Collections.Array<Image> images)
    {
        var result = new Image[images.Count];
        for (int i = 0; i < result.Length; i++) result[i] = images[i];
        return result;
    }

    private static float AuthoredCutoff(ImportedTreeVariant variant) =>
        variant.Lods[3].SurfaceGetMaterial(0) is ShaderMaterial material ? material.GetShaderParameter("alpha_cutoff").AsSingle() : .25f;

    private static string Fingerprint(Godot.Collections.Array<Image> albedo, int variantCount)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(variantCount));
        foreach (Image image in albedo) hash.AppendData(image.GetData());
        return Convert.ToHexString(hash.GetHashAndReset(), 0, 12);
    }

    private static ArrayMesh CreateSharedMesh(int vertexCount, Aabb bounds)
    {
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var uvs = new Vector2[vertexCount];
        var uv2s = new Vector2[vertexCount];
        var tangents = new float[vertexCount * 4];
        for (int i = 0; i < vertexCount; i++) { normals[i] = Vector3.Up; tangents[i * 4] = 1; tangents[i * 4 + 3] = 1; }
        arrays[(int)Mesh.ArrayType.Vertex] = vertices; arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs; arrays[(int)Mesh.ArrayType.TexUV2] = uv2s; arrays[(int)Mesh.ArrayType.Tangent] = tangents;
        var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays); mesh.CustomAabb = bounds;
        return mesh;
    }

    private static ArrayMesh CreateFarQuadMesh(Aabb bounds)
    {
        // Positions are placeholders: the vertex shader builds the corners from VERTEX_ID.
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        var normals = new Vector3[FarQuadVertexCount];
        var uvs = new Vector2[FarQuadVertexCount];
        var tangents = new float[FarQuadVertexCount * 4];
        for (int i = 0; i < FarQuadVertexCount; i++) { normals[i] = Vector3.Up; tangents[i * 4] = 1; tangents[i * 4 + 3] = 1; }
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[FarQuadVertexCount]; arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs; arrays[(int)Mesh.ArrayType.TexUV2] = new Vector2[FarQuadVertexCount]; arrays[(int)Mesh.ArrayType.Tangent] = tangents;
        var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays); mesh.CustomAabb = bounds;
        return mesh;
    }

    private static ShaderMaterial CreateMaterial(IReadOnlyList<ImportedTreeVariant> variants, IReadOnlyList<FarQuadPlane> planes, IReadOnlyList<FarQuadTop> tops, IReadOnlyList<float> defaultCutoffs, ImportedTreeImpostorCalibration? calibration, bool baked, Texture2D positions, Texture2D normals, Texture2D uvs, Texture2D tangents, Texture2D attachments,
        Texture2DArray albedoLayers, Texture2DArray normalLayers, Texture2DArray metallicRoughnessLayers)
    {
        Shader shader = ResourceLoader.Load<Shader>("res://materials/imported_tree_impostor.gdshader") ?? throw new InvalidOperationException("Missing imported-tree impostor shader.");
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("vertex_data", positions); material.SetShaderParameter("normal_data", normals);
        material.SetShaderParameter("uv_data", uvs); material.SetShaderParameter("tangent_data", tangents);
        material.SetShaderParameter("attachment_data", attachments);
        material.SetShaderParameter("albedo_layers", albedoLayers);
        material.SetShaderParameter("normal_layers", normalLayers);
        material.SetShaderParameter("metallic_roughness_layers", metallicRoughnessLayers);
        material.SetShaderParameter("variant_parameters", CreateParameterTexture(variants, planes, tops, defaultCutoffs, calibration, baked));
        CopySharedParameters(variants[0].Lods[3].SurfaceGetMaterial(0) as ShaderMaterial, material);
        return material;
    }

    private static void CopySharedParameters(ShaderMaterial? source, ShaderMaterial destination)
    {
        if (source == null) return;
        foreach (string parameter in new[] { "ring_center", "sun_direction", "day_start_dot", "day_full_dot", "night_tint", "night_direct_light_factor", "day_direct_light_factor", "vegetation_wind_enabled" })
            destination.SetShaderParameter(parameter, source.GetShaderParameter(parameter));
    }

    // The baked albedo layer (RGBA8 sRGB, alpha = covered fraction): the base level only; the mips are built for the cutoff in use by BuildAlbedoImages.
    private static void PrepareBakedBaseImages(int count, PreparedTreeImpostorAtlas bake, Godot.Collections.Array<Image> images)
    {
        for (int i = 0; i < count; i++)
        {
            byte[] layer = new byte[PreparedTreeImpostorAtlas.AlbedoLayerBytes];
            Buffer.BlockCopy(bake.AlbedoBase, i * layer.Length, layer, 0, layer.Length);
            images.Add(Image.CreateFromData(TextureLayerSize, TextureLayerSize, false, Image.Format.Rgba8, layer));
        }
    }

    // The pack's card pictures at the layer size, base level only (no mips).
    private static IEnumerable<bool> PrepareBaseImages(IReadOnlyList<ImportedTreeVariant> variants, Godot.Collections.Array<Image> images)
    {
        var tasks = new List<Task<Image>>(variants.Count);
        foreach (ImportedTreeVariant variant in variants)
        {
            ShaderMaterial? material = variant.Lods[3].SurfaceGetMaterial(0) as ShaderMaterial;
            Texture2D? texture = material == null ? null : material.GetShaderParameter("albedo_texture").AsGodotObject() as Texture2D;
            Image? source = texture?.GetImage();
            tasks.Add(Task.Run(() => PrepareImage(source, new Color(1, 1, 1, 1), false)));
            yield return true;
        }
        foreach (bool unit in AwaitImages(tasks, images)) yield return unit;
    }

    /// <summary>One albedo layer per base image: GenerateMipmaps, then each alpha mip keeps the base level's covered fraction at that variant's cutoff in use (design E4; a negative cutoff leaves the mips alone).
    /// The mips must be built for the cutoff the shader tests against, or the coverage drifts with distance.</summary>
    private static IEnumerable<bool> BuildAlbedoImages(IReadOnlyList<Image> bases, float[] cutoffs, Godot.Collections.Array<Image> images)
    {
        var tasks = new List<Task<Image>>(bases.Count);
        for (int i = 0; i < bases.Count; i++)
        {
            Image source = bases[i]; float cutoff = cutoffs[i];
            tasks.Add(Task.Run(() =>
            {
                var image = (Image)source.Duplicate();
                image.GenerateMipmaps();
                return cutoff > 0f ? PreserveAlphaCoverage(image, cutoff) : image;
            }));
            yield return true;
        }
        foreach (bool unit in AwaitImages(tasks, images)) yield return unit;
    }

    // Baked normal layers carry their selected mip chain (level 0 to 8, back to back): every stored level is uploaded as is.
    private static Texture2DArray CreateBakedNormalArray(PreparedTreeImpostorAtlas bake, int count)
    {
        var images = new Godot.Collections.Array<Image>();
        int layerBytes = PreparedTreeImpostorAtlas.NormalLayerBytes;
        for (int i = 0; i < count; i++)
        {
            byte[] layer = new byte[layerBytes];
            Buffer.BlockCopy(bake.NormalMips, i * layerBytes, layer, 0, layerBytes);
            images.Add(Image.CreateFromData(TextureLayerSize, TextureLayerSize, true, Image.Format.Rgba8, layer));
        }
        return CreateTextureArray(images, "baked normal_texture");
    }

    // Decompress/convert/resize/mip work runs on workers (one task per layer); only the texture read-back stays on the main thread.
    private static IEnumerable<bool> PrepareTextureImages(IReadOnlyList<ImportedTreeVariant> variants, string parameter, Color fallback, Godot.Collections.Array<Image> images)
    {
        var tasks = new List<Task<Image>>(variants.Count);
        foreach (ImportedTreeVariant variant in variants)
        {
            ShaderMaterial? material = variant.Lods[3].SurfaceGetMaterial(0) as ShaderMaterial;
            Texture2D? texture = material == null ? null : material.GetShaderParameter(parameter).AsGodotObject() as Texture2D;
            Image? source = texture?.GetImage();
            tasks.Add(Task.Run(() => PrepareImage(source, fallback, true)));
            yield return true;
        }
        foreach (bool unit in AwaitImages(tasks, images)) yield return unit;
    }

    private static IEnumerable<bool> AwaitImages(List<Task<Image>> tasks, Godot.Collections.Array<Image> images)
    {
        foreach (Task<Image> task in tasks)
        {
            while (!task.IsCompleted) yield return true;
            images.Add(task.GetAwaiter().GetResult());
        }
    }

    private static Texture2DArray CreateTextureArray(Godot.Collections.Array<Image> images, string label)
    {
        var array = new Texture2DArray();
        if (array.CreateFromImages(images) != Error.Ok) throw new InvalidOperationException($"Could not create {label} texture array.");
        return array;
    }

    private static IEnumerable<bool> PrepareMetallicRoughnessImages(IReadOnlyList<ImportedTreeVariant> variants, Godot.Collections.Array<Image> images)
    {
        var tasks = new List<Task<Image>>(variants.Count);
        foreach (ImportedTreeVariant variant in variants)
        {
            ShaderMaterial? material = variant.Lods[3].SurfaceGetMaterial(0) as ShaderMaterial;
            Image? metallicSource = (material == null ? null : material.GetShaderParameter("metallic_texture").AsGodotObject() as Texture2D)?.GetImage();
            Image? roughnessSource = (material == null ? null : material.GetShaderParameter("roughness_texture").AsGodotObject() as Texture2D)?.GetImage();
            tasks.Add(Task.Run(() => PackMetallicRoughness(metallicSource, roughnessSource)));
            yield return true;
        }
        foreach (bool unit in AwaitImages(tasks, images)) yield return unit;
    }

    private static Image PackMetallicRoughness(Image? metallicSource, Image? roughnessSource)
    {
        const int pixelBytes = TextureLayerSize * TextureLayerSize * 4;
        // Only the base level of these is read, so skip their mip chains.
        Image metallic = PrepareImage(metallicSource, new Color(0, 0, 1, 1), false);
        Image roughness = PrepareImage(roughnessSource, new Color(0, 1, 0, 1), false);
        byte[] metallicData = metallic.GetData(), roughnessData = roughness.GetData();
        var packedData = new byte[pixelBytes];
        for (int i = 0; i < pixelBytes; i += 4) { packedData[i + 1] = roughnessData[i + 1]; packedData[i + 2] = metallicData[i + 2]; packedData[i + 3] = 255; }
        Image packed = Image.CreateFromData(TextureLayerSize, TextureLayerSize, false, Image.Format.Rgba8, packedData);
        packed.GenerateMipmaps();
        return packed;
    }

    private static Image PrepareImage(Image? textureImage, Color fallback, bool mipmaps)
    {
        Image image;
        if (textureImage is Image source && !source.IsEmpty())
        {
            image = (Image)source.Duplicate();
            if (image.IsCompressed() && image.Decompress() != Error.Ok)
                throw new InvalidOperationException("Could not decompress imported tree texture image.");
        }
        else
        {
            image = Image.CreateEmpty(TextureLayerSize, TextureLayerSize, false, Image.Format.Rgba8);
            image.Fill(fallback); if (mipmaps) image.GenerateMipmaps();
            return image;
        }
        image.Convert(Image.Format.Rgba8); image.Resize(TextureLayerSize, TextureLayerSize, Image.Interpolation.Lanczos); if (mipmaps) image.GenerateMipmaps();
        return image;
    }

    /// <summary>Scales the alpha of every mip level (not the base) so that the covered area at the cutoff equals the base level's; colour is untouched. Area is counted on a bilinear 4 x 4 sub-sample
    /// grid per texel (what the shader's filtering sees), not on whole texels: the baked alpha has only a few levels, so whole-texel counts jump with the cutoff and the calibration could not tune the area.</summary>
    internal static Image PreserveAlphaCoverage(Image image, float cutoff)
    {
        int levels = image.GetMipmapCount();
        if (levels <= 0) return image;
        byte[] data = image.GetData();
        float threshold = cutoff * 255f;
        float baseCount = CoveredTexels(data, 0, TextureLayerSize, 1f, threshold);
        for (int level = 1; level <= levels; level++)
        {
            int size = Math.Max(1, TextureLayerSize >> level), texels = size * size, offset = (int)image.GetMipmapOffset(level);
            float target = baseCount * (texels / (float)(TextureLayerSize * TextureLayerSize));
            float low = .25f, high = 4f;
            for (int step = 0; step < 14; step++)
            {
                float mid = MathF.Sqrt(low * high);
                if (CoveredTexels(data, offset, size, mid, threshold) < target) low = mid; else high = mid;
            }
            float scale = MathF.Sqrt(low * high);
            for (int i = 0; i < texels; i++) data[offset + i * 4 + 3] = (byte)Math.Min(255, (int)MathF.Round(data[offset + i * 4 + 3] * scale));
        }
        return Image.CreateFromData(TextureLayerSize, TextureLayerSize, true, Image.Format.Rgba8, data);
    }

    // Covered area of one level in texel units: the fraction of a 4 x 4 grid of sub-samples per texel whose bilinearly filtered alpha (times scale) reaches the threshold (0 to 255).
    private static float CoveredTexels(byte[] data, int offset, int size, float scale, float threshold)
    {
        int covered = 0, last = size - 1;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                for (int sy = 0; sy < 4; sy++)
                {
                    float py = y + (sy + .5f) * .25f - .5f;
                    int y0 = (int)MathF.Floor(py); float fy = py - y0; int ya = Math.Clamp(y0, 0, last), yb = Math.Clamp(y0 + 1, 0, last);
                    for (int sx = 0; sx < 4; sx++)
                    {
                        float px = x + (sx + .5f) * .25f - .5f;
                        int x0 = (int)MathF.Floor(px); float fx = px - x0; int xa = Math.Clamp(x0, 0, last), xb = Math.Clamp(x0 + 1, 0, last);
                        float top = data[offset + (ya * size + xa) * 4 + 3] * (1f - fx) + data[offset + (ya * size + xb) * 4 + 3] * fx;
                        float bottom = data[offset + (yb * size + xa) * 4 + 3] * (1f - fx) + data[offset + (yb * size + xb) * 4 + 3] * fx;
                        if ((top * (1f - fy) + bottom * fy) * scale >= threshold) covered++;
                    }
                }
        return covered / 16f;
    }

    private static ImageTexture CreateDataTexture(int width, IReadOnlyList<Geometry> geometries, DataChannel channel)
    {
        var texels = new float[width * geometries.Count * 4];
        int offset = 0;
        for (int row = 0; row < geometries.Count; row++)
            for (int column = 0; column < width; column++)
            {
                Color texel = geometries[row].Get(channel, column);
                texels[offset++] = texel.R; texels[offset++] = texel.G; texels[offset++] = texel.B; texels[offset++] = texel.A;
            }
        var bytes = new byte[texels.Length * sizeof(float)];
        Buffer.BlockCopy(texels, 0, bytes, 0, bytes.Length);
        Image image = Image.CreateFromData(width, geometries.Count, false, Image.Format.Rgbaf, bytes);
        return ImageTexture.CreateFromImage(image);
    }

    private static IReadOnlyList<ImportedTreeVariant>? _hullSource;
    private static TreeCrownHull[]? _hulls;

    /// <summary>The crown hulls of these variants (read from the authored LOD0 meshes once per list).</summary>
    private static TreeCrownHull[] CrownHulls(IReadOnlyList<ImportedTreeVariant> variants)
    {
        if (_hulls != null && ReferenceEquals(_hullSource, variants) && _hulls.Length == variants.Count) return _hulls;
        var hulls = new TreeCrownHull[variants.Count];
        for (int i = 0; i < hulls.Length; i++) hulls[i] = TreeCrownHullTable.FromVariant(i, variants[i]);
        _hullSource = variants; _hulls = hulls;
        return hulls;
    }

    private static ImageTexture CreateParameterTexture(IReadOnlyList<ImportedTreeVariant> variants, IReadOnlyList<FarQuadPlane> planes, IReadOnlyList<FarQuadTop> tops, IReadOnlyList<float> defaultCutoffs, ImportedTreeImpostorCalibration? calibration, bool baked)
    {
        TreeCrownHull[] hulls = CrownHulls(variants);
        var image = Image.CreateEmpty(variants.Count, ParameterRowCount, false, Image.Format.Rgbaf);
        for (int i = 0; i < variants.Count; i++)
        {
            ShaderMaterial? material = variants[i].Lods[3].SurfaceGetMaterial(0) as ShaderMaterial;
            Color tint = material == null ? Colors.White : material.GetShaderParameter("albedo_tint").AsColor();
            // TR-7: the bake stores albedo with the LOD2's tint already applied, so row 0 is white while it is in use (the tint would otherwise count twice). The calibration's cutoffs
            // replace the authored one in row 1 (cards) and row 2 alpha (far quad); there is no brightness multiplier. T6-8's per-instance tint multiplies after this, once.
            if (baked) tint = Colors.White;
            image.SetPixel(i, 0, tint);
            image.SetPixel(i, 1, new Color(baked || material == null ? 1 : material.GetShaderParameter("normal_scale").AsSingle(), material == null ? 0 : material.GetShaderParameter("metallic_factor").AsSingle(), material == null ? 1 : material.GetShaderParameter("roughness_factor").AsSingle(), calibration != null ? calibration.Cutoffs[i] : defaultCutoffs[i]));
            image.SetPixel(i, 2, new Color(material != null && material.GetShaderParameter("use_alpha_cutout").AsBool() ? 1 : 0,
                material == null ? 2 : material.GetShaderParameter("tree_foliage_mode").AsInt32(),
                material != null && material.GetShaderParameter("tree_foliage_attachments").AsBool() ? 1 : 0, calibration != null ? calibration.QuadCutoffs[i] : defaultCutoffs[i]));
            FarQuadPlane plane = planes[i];
            image.SetPixel(i, 3, new Color(plane.ULeft, plane.URight, plane.VBottom, plane.VTop));
            image.SetPixel(i, 4, new Color(plane.Width, plane.BottomY, plane.TopY, plane.FrameCode));
            FarQuadTop top = tops[i];
            image.SetPixel(i, 5, new Color(top.ULow, top.VLow, top.UHigh, top.VHigh));
            image.SetPixel(i, 6, new Color(top.P00.X, top.P00.Y, top.P10.X, top.P10.Y));
            image.SetPixel(i, 7, new Color(top.P01.X, top.P01.Y, top.Y, top.FrameCode));
            TreeCrownHullTable.Pack(hulls[i], out Vector4 hullRow, out Vector4 hullExtra, out Vector4 hullLad);
            image.SetPixel(i, 8, new Color(hullRow.X, hullRow.Y, hullRow.Z, hullRow.W));
            image.SetPixel(i, 9, new Color(hullExtra.X, hullExtra.Y, hullExtra.Z, hullExtra.W));
            image.SetPixel(i, 10, new Color(hullLad.X, hullLad.Y, hullLad.Z, hullLad.W));
        }
        return ImageTexture.CreateFromImage(image);
    }

    private static Geometry Expand(Mesh mesh)
    {
        var result = new Geometry();
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surface);
            Vector3[] positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Vector3[] normals = ReadVector3Array(arrays, Mesh.ArrayType.Normal);
            Vector2[] uvs = ReadVector2Array(arrays, Mesh.ArrayType.TexUV);
            Vector2[] uv2s = ReadVector2Array(arrays, Mesh.ArrayType.TexUV2);
            float[] tangents = ReadFloatArray(arrays, Mesh.ArrayType.Tangent);
            float[] attachments = ReadFloatArray(arrays, Mesh.ArrayType.Custom0);
            int[] indices = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? Sequential(positions.Length) : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            foreach (int index in indices) result.Add(positions[index], normals.Length > index ? normals[index] : Vector3.Up, uvs.Length > index ? uvs[index] : Vector2.Zero, uv2s.Length > index ? uv2s[index] : Vector2.Zero, tangents.Length >= (index + 1) * 4 ? new Vector4(tangents[index * 4], tangents[index * 4 + 1], tangents[index * 4 + 2], tangents[index * 4 + 3]) : new Vector4(1, 0, 0, 1),
                attachments.Length >= (index + 1) * 4 ? new Vector4(attachments[index*4],attachments[index*4+1],attachments[index*4+2],attachments[index*4+3]) : Vector4.Zero);
        }
        return result;
    }
    private static Vector3[] ReadVector3Array(Godot.Collections.Array arrays, Mesh.ArrayType type) => arrays[(int)type].VariantType == Variant.Type.Nil ? Array.Empty<Vector3>() : arrays[(int)type].AsVector3Array();
    private static Vector2[] ReadVector2Array(Godot.Collections.Array arrays, Mesh.ArrayType type) => arrays[(int)type].VariantType == Variant.Type.Nil ? Array.Empty<Vector2>() : arrays[(int)type].AsVector2Array();
    private static float[] ReadFloatArray(Godot.Collections.Array arrays, Mesh.ArrayType type) => arrays[(int)type].VariantType == Variant.Type.Nil ? Array.Empty<float>() : arrays[(int)type].AsFloat32Array();
    private static int[] Sequential(int count) { var result = new int[count]; for (int i = 0; i < count; i++) result[i] = i; return result; }
    private enum DataChannel { Position, Normal, Uv, Tangent, Attachment }
    private sealed class Geometry
    {
        private readonly List<Vector3> _positions = new(); private readonly List<Vector3> _normals = new(); private readonly List<Vector2> _uvs = new(); private readonly List<Vector2> _uv2s = new(); private readonly List<Vector4> _tangents = new();
        private readonly List<Vector4> _attachments = new();
        public Vector3[] Positions => _positions.ToArray();
        public Vector2[] Uvs => _uvs.ToArray();

        /// <summary>The largest card (connected coplanar triangles, the first on a tie), as a camera-facing quad: its horizontal extent along cross(up, normal), its vertical extent, and the UVs at its edges.</summary>
        private static (float AtMin, float AtMax) FitEdges(List<int> triangles, Func<int, float> coordinate, Func<int, float> value, float min, float max)
        {
            double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
            foreach (int t in triangles)
                for (int k = 0; k < 3; k++) { double x = coordinate(t + k), y = value(t + k); n++; sx += x; sy += y; sxx += x * x; sxy += x * y; }
            double denominator = n * sxx - sx * sx;
            if (Math.Abs(denominator) < 1e-12) return ((float)(sy / n), (float)(sy / n));
            double slope = (n * sxy - sx * sy) / denominator, offset = (sy - slope * sx) / n;
            return ((float)(offset + slope * min), (float)(offset + slope * max));
        }

        public FarQuadPlane LargestPlane()
        {
            var normals = new List<Vector3>(); var offsets = new List<float>(); var areas = new List<float>(); var triangles = new List<int>(); var members = new List<List<int>>();
            for (int t = 0; t + 2 < _positions.Count; t += 3)
            {
                Vector3 cross = (_positions[t + 1] - _positions[t]).Cross(_positions[t + 2] - _positions[t]);
                float area = cross.Length() * .5f;
                if (area < 1e-9f) continue;
                Vector3 n = cross.Normalized();
                if (n.X < -1e-4f || (Mathf.Abs(n.X) <= 1e-4f && (n.Z < -1e-4f || (Mathf.Abs(n.Z) <= 1e-4f && n.Y < 0)))) n = -n;
                float d = n.Dot(_positions[t]);
                int group = -1;
                for (int g = 0; g < normals.Count; g++)
                    if (normals[g].Dot(n) > .9995f && Mathf.Abs(offsets[g] - d) < .01f) { group = g; break; }
                if (group < 0) { normals.Add(n); offsets.Add(d); areas.Add(0); triangles.Add(0); members.Add(new List<int>()); group = normals.Count - 1; }
                areas[group] += area; triangles[group]++; members[group].Add(t);
            }
            if (normals.Count == 0) return new FarQuadPlane(0, 1, 1, 0, 1, 0, 1, 0, 0);
            // A "plane" of the LOD3 mesh holds several cards that share one plane but sample different atlas regions; the quad copies one card: the connected
            // component (triangles sharing a vertex position and UV) of largest area, the first on a tie.
            int best = 0; List<int> bestCard = new(); float bestArea = -1; int bestTriangles = 0;
            bool anyUpright = false;
            foreach (Vector3 n in normals) anyUpright |= Mathf.Abs(n.Y) <= .7f; // a near-horizontal disc has no side picture: only upright cards compete when there are any
            for (int g = 0; g < normals.Count; g++)
            {
                if (anyUpright && Mathf.Abs(normals[g].Y) > .7f) continue;
                List<int> tris = members[g]; var parent = new int[tris.Count]; for (int i = 0; i < parent.Length; i++) parent[i] = i;
                int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
                var owner = new Dictionary<(int, int, int, int, int), int>();
                for (int i = 0; i < tris.Count; i++)
                    for (int k = 0; k < 3; k++)
                    {
                        Vector3 p = _positions[tris[i] + k]; Vector2 uv = _uvs[tris[i] + k];
                        var key = ((int)MathF.Round(p.X * 1000), (int)MathF.Round(p.Y * 1000), (int)MathF.Round(p.Z * 1000), (int)MathF.Round(uv.X * 1000), (int)MathF.Round(uv.Y * 1000));
                        if (owner.TryGetValue(key, out int other)) parent[Find(i)] = Find(other); else owner[key] = i;
                    }
                var componentArea = new Dictionary<int, float>(); var componentOrder = new List<int>();
                for (int i = 0; i < tris.Count; i++)
                {
                    int root = Find(i); int t = tris[i];
                    if (!componentArea.ContainsKey(root)) { componentArea[root] = 0; componentOrder.Add(root); }
                    componentArea[root] += (_positions[t + 1] - _positions[t]).Cross(_positions[t + 2] - _positions[t]).Length() * .5f;
                }
                foreach (int root in componentOrder)
                    if (componentArea[root] > bestArea + 1e-7f)
                    {
                        bestArea = componentArea[root]; best = g; bestCard = new List<int>(); bestTriangles = 0;
                        for (int i = 0; i < tris.Count; i++) if (Find(i) == root) { bestCard.Add(tris[i]); bestTriangles++; }
                    }
            }
            areas[best] = bestArea; triangles[best] = bestTriangles; members[best] = bestCard;
            Vector3 normal = normals[best];
            var horizontal = new Vector3(normal.Z, 0, -normal.X);
            horizontal = horizontal.LengthSquared() > 1e-8f ? horizontal.Normalized() : Vector3.Right;
            float s0 = float.MaxValue, s1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            foreach (int t in members[best])
                for (int k = 0; k < 3; k++)
                {
                    float s = horizontal.Dot(_positions[t + k]), y = _positions[t + k].Y;
                    s0 = Mathf.Min(s0, s); s1 = Mathf.Max(s1, s); y0 = Mathf.Min(y0, y); y1 = Mathf.Max(y1, y);
                }
            // The card UVs are a planar projection, so u is linear in the horizontal coordinate and v in the height: fit both by least squares over
            // the plane's vertices and read the edges off the fit (the vertices at the extremes are tips of a cut-out outline, not the picture's edges).
            (float left, float right) = FitEdges(members[best], t => horizontal.Dot(_positions[t]), t => _uvs[t].X, s0, s1);
            (float bottom, float top) = FitEdges(members[best], t => _positions[t].Y, t => _uvs[t].Y, y0, y1);
            float uLeft = left, uRight = right, vBottom = bottom, vTop = top;
            // Frame code (see FarQuadPlane): the card's baked frame as TR-7a's baker builds it (summed vertex normal, summed tangent, handedness of its first vertex).
            Vector3 vertexNormal = Vector3.Zero, tangent = Vector3.Zero, winding = Vector3.Zero; float handedness = 1; bool first = true;
            foreach (int t in members[best])
            {
                winding -= (_positions[t + 1] - _positions[t]).Cross(_positions[t + 2] - _positions[t]);
                for (int k = 0; k < 3; k++)
                {
                    vertexNormal += _normals[t + k]; tangent += new Vector3(_tangents[t + k].X, _tangents[t + k].Y, _tangents[t + k].Z);
                    if (first) { handedness = _tangents[t].W; first = false; }
                }
            }
            vertexNormal = vertexNormal.LengthSquared() > 1e-12f ? vertexNormal.Normalized() : Vector3.Up;
            tangent = tangent.LengthSquared() > 1e-12f ? tangent.Normalized() : Vector3.Right;
            Vector3 binormal = vertexNormal.Cross(tangent).Normalized() * handedness;
            Vector3 view = Mathf.Abs(normal.Y) > .7f ? (normal.Y >= 0 ? normal : -normal) : (normal.Dot(vertexNormal) >= 0 ? normal : -normal);
            Vector3 uAxis = horizontal * (uRight >= uLeft ? 1f : -1f), vAxis = Vector3.Up * (vTop >= vBottom ? 1f : -1f);
            int frameCode = (winding.Dot(view) >= 0 ? 1 : 0) | (tangent.Dot(uAxis) >= 0 ? 2 : 0) | (binormal.Dot(vAxis) >= 0 ? 4 : 0) | (uAxis.Cross(vAxis).Dot(vertexNormal) >= 0 ? 8 : 0);
            return new FarQuadPlane(uLeft, uRight, vBottom, vTop, s1 - s0, y0, y1, bestTriangles, bestArea, frameCode);
        }
        /// <summary>The horizontal LOD3 card (TR-6b): connected triangles (sharing a vertex position and UV) whose plane is near-horizontal (|n.y| > .7, as TR-7a's baker decides). The pack has exactly one;
        /// with several the largest is used and CardCount says so. The card's UVs are an affine map of (x, z); the quad covers the card's UV rectangle, its corners found by inverting that map.</summary>
        public FarQuadTop HorizontalCard()
        {
            var triangles = new List<int>();
            for (int t = 0; t + 2 < _positions.Count; t += 3)
            {
                Vector3 cross = (_positions[t + 1] - _positions[t]).Cross(_positions[t + 2] - _positions[t]);
                if (cross.Length() * .5f < 1e-9f) continue;
                if (Mathf.Abs(cross.Normalized().Y) > .7f) triangles.Add(t);
            }
            var none = new FarQuadTop(0, 0, 0, 0, Vector2.Zero, Vector2.Zero, Vector2.Zero, 0, -1, 0, 0);
            if (triangles.Count == 0) return none;
            var parent = new int[triangles.Count]; for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            var owner = new Dictionary<(int, int, int, int, int), int>();
            for (int i = 0; i < triangles.Count; i++)
                for (int k = 0; k < 3; k++)
                {
                    Vector3 p = _positions[triangles[i] + k]; Vector2 uv = _uvs[triangles[i] + k];
                    var key = ((int)MathF.Round(p.X * 1000), (int)MathF.Round(p.Y * 1000), (int)MathF.Round(p.Z * 1000), (int)MathF.Round(uv.X * 1000), (int)MathF.Round(uv.Y * 1000));
                    if (owner.TryGetValue(key, out int other)) parent[Find(i)] = Find(other); else owner[key] = i;
                }
            var area = new Dictionary<int, float>(); var order = new List<int>();
            for (int i = 0; i < triangles.Count; i++)
            {
                int root = Find(i); int t = triangles[i];
                if (!area.ContainsKey(root)) { area[root] = 0; order.Add(root); }
                area[root] += (_positions[t + 1] - _positions[t]).Cross(_positions[t + 2] - _positions[t]).Length() * .5f;
            }
            int bestRoot = order[0];
            foreach (int root in order) if (area[root] > area[bestRoot] + 1e-7f) bestRoot = root;
            var card = new List<int>();
            for (int i = 0; i < triangles.Count; i++) if (Find(i) == bestRoot) card.Add(triangles[i]);

            // Least-squares affine map uv = M (x, z) + b over the card's vertices, and the card's UV rectangle, height and frame sums.
            double[,] a = new double[3, 3]; double[] rhsU = new double[3], rhsV = new double[3];
            float u0 = float.MaxValue, v0 = float.MaxValue, u1 = float.MinValue, v1 = float.MinValue; double ySum = 0; int count = 0;
            Vector3 vertexNormal = Vector3.Zero, tangent = Vector3.Zero, winding = Vector3.Zero; float handedness = 1; bool first = true;
            foreach (int t in card)
            {
                winding -= (_positions[t + 1] - _positions[t]).Cross(_positions[t + 2] - _positions[t]);
                for (int k = 0; k < 3; k++)
                {
                    Vector3 p = _positions[t + k]; Vector2 uv = _uvs[t + k];
                    double[] x = { p.X, p.Z, 1 };
                    for (int r = 0; r < 3; r++) { for (int c = 0; c < 3; c++) a[r, c] += x[r] * x[c]; rhsU[r] += x[r] * uv.X; rhsV[r] += x[r] * uv.Y; }
                    u0 = Mathf.Min(u0, uv.X); u1 = Mathf.Max(u1, uv.X); v0 = Mathf.Min(v0, uv.Y); v1 = Mathf.Max(v1, uv.Y);
                    ySum += p.Y; count++;
                    vertexNormal += _normals[t + k]; tangent += new Vector3(_tangents[t + k].X, _tangents[t + k].Y, _tangents[t + k].Z);
                    if (first) { handedness = _tangents[t].W; first = false; }
                }
            }
            for (int i = 0; i < 3; i++) a[i, i] += 1e-12;
            double[]? su = SolveThree(a, rhsU), sv = SolveThree(a, rhsV);
            if (su == null || sv == null) return none with { CardCount = order.Count };
            double det = su[0] * sv[1] - su[1] * sv[0];
            if (Math.Abs(det) < 1e-12 || u1 <= u0 || v1 <= v0) return none with { CardCount = order.Count };
            // (x, z) = M^-1 (uv - b)
            Vector2 Corner(float u, float v)
            {
                double du = u - su[2], dv = v - sv[2];
                return new Vector2((float)((sv[1] * du - su[1] * dv) / det), (float)((-sv[0] * du + su[0] * dv) / det));
            }
            Vector2 c00 = Corner(u0, v0), c10 = Corner(u1, v0), c01 = Corner(u0, v1);
            Vector3 uDir = new Vector3(c10.X - c00.X, 0, c10.Y - c00.Y), vDir = new Vector3(c01.X - c00.X, 0, c01.Y - c00.Y);
            if (uDir.LengthSquared() < 1e-12f || vDir.LengthSquared() < 1e-12f) return none with { CardCount = order.Count };
            uDir = uDir.Normalized(); vDir = vDir.Normalized();
            vertexNormal = vertexNormal.LengthSquared() > 1e-12f ? vertexNormal.Normalized() : Vector3.Up;
            tangent = tangent.LengthSquared() > 1e-12f ? tangent.Normalized() : Vector3.Right;
            Vector3 binormal = vertexNormal.Cross(tangent).Normalized() * handedness;
            // As LargestPlane's frame code, for the baked side of a horizontal card (straight up).
            int frameCode = (winding.Dot(Vector3.Up) >= 0 ? 1 : 0) | (tangent.Dot(uDir) >= 0 ? 2 : 0) | (binormal.Dot(vDir) >= 0 ? 4 : 0) | (uDir.Cross(vDir).Dot(vertexNormal) >= 0 ? 8 : 0);
            return new FarQuadTop(u0, v0, u1, v1, c00, c10, c01, (float)(ySum / count), frameCode, order.Count, area[bestRoot]);
        }

        private static double[]? SolveThree(double[,] matrix, double[] right)
        {
            var m = new double[3, 4];
            for (int r = 0; r < 3; r++) { for (int c = 0; c < 3; c++) m[r, c] = matrix[r, c]; m[r, 3] = right[r]; }
            for (int c = 0; c < 3; c++)
            {
                int pivot = c; for (int r = c + 1; r < 3; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[pivot, c])) pivot = r;
                if (Math.Abs(m[pivot, c]) < 1e-15) return null;
                for (int j = 0; j < 4; j++) (m[c, j], m[pivot, j]) = (m[pivot, j], m[c, j]);
                for (int r = 0; r < 3; r++)
                {
                    if (r == c) continue;
                    double f = m[r, c] / m[c, c];
                    for (int j = c; j < 4; j++) m[r, j] -= f * m[c, j];
                }
            }
            return new[] { m[0, 3] / m[0, 0], m[1, 3] / m[1, 1], m[2, 3] / m[2, 2] };
        }
        public void Add(Vector3 p, Vector3 n, Vector2 uv, Vector2 uv2, Vector4 tangent, Vector4 attachment) { _positions.Add(p); _normals.Add(n); _uvs.Add(uv); _uv2s.Add(uv2); _tangents.Add(tangent); _attachments.Add(attachment); }
        public Color Get(DataChannel channel, int index) { if (index >= _positions.Count) return channel == DataChannel.Position || channel == DataChannel.Attachment ? new Color(0, 0, 0, 0) : new Color(0, 1, 0, 1); return channel switch { DataChannel.Position => new Color(_positions[index].X, _positions[index].Y, _positions[index].Z, 1), DataChannel.Normal => new Color(_normals[index].X, _normals[index].Y, _normals[index].Z, 1), DataChannel.Uv => new Color(_uvs[index].X, _uvs[index].Y, _uv2s[index].X, _uv2s[index].Y), DataChannel.Attachment => new Color(_attachments[index].X,_attachments[index].Y,_attachments[index].Z,_attachments[index].W), _ => new Color(_tangents[index].X, _tangents[index].Y, _tangents[index].Z, _tangents[index].W) }; }
    }
}
