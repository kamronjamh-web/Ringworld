using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Vegetation.Canopy;

namespace RingworldTechDemo.Vegetation.Understory;

/// <summary>One LOD of the understory saplings: the carrier mesh (all variants in one unindexed triangle list), its material and the bounds of the largest variant, in the catalog's normalised tree space (the tree is 1 unit tall).</summary>
public sealed class UnderstoryLodCarrier
{
    /// <summary>Unindexed triangle list whose vertex positions are all zero (the vertex shader fetches the real ones); a custom AABB covers every variant.</summary>
    public ArrayMesh Carrier = null!;
    public ShaderMaterial Material = null!;
    public int VertexCount;
    /// <summary>Triangles of the largest variant (the rest is padded with degenerate triangles).</summary>
    public int TriangleCount;
    public Aabb Bounds;
    /// <summary>Largest distance of any vertex of any variant from the root, normalised.</summary>
    public float UnitRadius;
    /// <summary>The source LOD of the base catalog this carrier was built from.</summary>
    public int SourceLod;
}

/// <summary>What the dead-wood forms measure, per form row (index minus 30). Needs no textures or materials, so the collider pool (and headless tests) read it without building the carriers.</summary>
public sealed class UnderstoryDeadWoodDimensions
{
    /// <summary>Authored instance height in metres of each form row (a record's scale is its Size times this): the source variant's height.</summary>
    public float[] HeightMeters = Array.Empty<float>();
    /// <summary>Authored length in metres (snag height, log length, stump height) and trunk radius of each form row, before the record's Size.</summary>
    public float[] LengthMeters = Array.Empty<float>(), TrunkRadiusMeters = Array.Empty<float>();
    public TreeFormKind[] Forms = Array.Empty<TreeFormKind>();
}

/// <summary>The dead wood of T6-6 (A7): snags, fallen trees and stumps (derived forms 30-42) as two carriers, one per distance band, whose variant rows are the form rows (index minus 30).</summary>
public sealed class UnderstoryDeadWood
{
    /// <summary>Snag LOD1 bark, log slot 0, stump slot 0: 0 to 60 m, cross-fading to the far carrier across 50 to 70 m.</summary>
    public UnderstoryLodCarrier Near = null!;
    /// <summary>Snag and log LODF trunks and stump slot 0: 50 m outwards (snags to 300 m).</summary>
    public UnderstoryLodCarrier Far = null!;
    public UnderstoryDeadWoodDimensions Dimensions = null!;
    public float[] HeightMeters => Dimensions.HeightMeters;
}

/// <summary>The shrubs of T6-7 (A8): the derived forms 43-54 as two carriers, one per LOD, whose variant rows are the shrub rows (index minus 43). Foliage only, in the foliage layer of the source species; a record's Size is the height in metres (the carrier is 1 unit tall).</summary>
public sealed class UnderstoryShrubs
{
    /// <summary>Shrub LOD0 (14, 10 or 12 cards): 0 to 45 m, fading out across 30 to 45 m.</summary>
    public UnderstoryLodCarrier Lod0 = null!;
    /// <summary>Shrub LOD1 (3 crossed cards and a top card): 30 m outwards, fading in across 30 to 45 m and out across 130 to 150 m.</summary>
    public UnderstoryLodCarrier Lod1 = null!;
    /// <summary>Authored height of each shrub row: 1 (the mesh is 1 unit tall).</summary>
    public float[] HeightMeters = Array.Empty<float>();
    /// <summary>The crown hull of each shrub row (also in the carriers' parameter texture rows 8 to 10).</summary>
    public TreeCrownHull[] Hulls = Array.Empty<TreeCrownHull>();
}

/// <summary>
/// T6-4b (T6 design A7): the understory saplings as vertex-fetch carriers, built on the main thread at load from the base catalog (without editing it): per LOD (0 and 2) one carrier whose vertices are fetched by
/// (VERTEX_ID, variant row) from data textures. Bark and foliage surfaces of a variant are merged into one unindexed triangle list, padded with degenerate triangles to the largest variant; the per-vertex layer index in
/// position.w is the species (0 maple, 1 fir, 2 pine) plus 4 for a foliage vertex. Rows wrap at 4,096 texels (<see cref="MaximumTextureWidth"/>). The variant parameter texture carries what the material of each surface held
/// (tint, roughness, metallic, normal scale, cutoff, wind flags) and the crown hull rows of TreeCrownHullTable (rows 8 to 10, the impostor's layout). Texture arrays come from the prepared resource
/// (tools/PrepareUnderstoryTextures.cs). Main thread only; <see cref="Load"/> is cached.
/// </summary>
public sealed class UnderstoryCatalog
{
    public const string TexturesPath = "res://resources/trees/prepared/understory/understory_textures.res";
    public const string ShaderPath = "res://materials/ring_understory.gdshader";
    public const int ParameterRowCount = 11;
    public const int MaximumTextureWidth = 4096;
    /// <summary>The base catalog variants used as saplings (ImportedTreeSourceTable understory role), in variant-row order.</summary>
    public static readonly int[] SaplingSources = { 0, 1, 2, 14, 24, 25, 26 };
    /// <summary>The base catalog LODs of the two carriers.</summary>
    public static readonly int[] SourceLods = { 0, 2 };

    /// <summary>The first placeable index of the dead-wood forms (DerivedTreeCatalog.FirstIndex) and how many there are.</summary>
    public const int DeadWoodFirstIndex = DerivedTreeCatalog.FirstIndex, DeadWoodCount = DerivedTreeCatalog.DeadWoodFormCount;

    private static UnderstoryCatalog? _cached;
    private static UnderstoryDeadWood? _deadWood;
    private static UnderstoryShrubs? _shrubs;
    private static UnderstoryDeadWoodDimensions? _deadWoodDimensions;
    private static TextureSet? _textures;

    public UnderstoryLodCarrier[] Lods = Array.Empty<UnderstoryLodCarrier>();
    /// <summary>Authored LOD0 height in metres of each variant row (a record's instance scale is its Size times this).</summary>
    public float[] HeightMeters = Array.Empty<float>();
    public long TextureBytes;
    public int TextureCount;

    public static int VariantCount => SaplingSources.Length;

    /// <summary>The variant row of a base catalog (ImportedTreeSourceTable) index, or -1.</summary>
    public static int VariantRowOf(int sourceIndex) => Array.IndexOf(SaplingSources, sourceIndex);

    /// <summary>The dead-wood variant row of a placeable index (30 to 42), or -1.</summary>
    public static int DeadWoodRowOf(int index) => index >= DeadWoodFirstIndex && index < DeadWoodFirstIndex + DeadWoodCount ? index - DeadWoodFirstIndex : -1;

    public static UnderstoryCatalog Load() => _cached ??= Build();

    /// <summary>The dead-wood carriers (T6-6); main thread, cached.</summary>
    public static UnderstoryDeadWood LoadDeadWood() => _deadWood ??= BuildDeadWood();

    /// <summary>The shrub carriers (T6-7); main thread, cached.</summary>
    public static UnderstoryShrubs LoadShrubs() => _shrubs ??= BuildShrubs();

    /// <summary>The first placeable index of the shrubs (DerivedTreeCatalog.ShrubFirstIndex) and how many there are.</summary>
    public const int ShrubFirstIndex = DerivedTreeCatalog.ShrubFirstIndex, ShrubCount = DerivedTreeCatalog.ShrubCount;

    /// <summary>The shrub variant row of a placeable index (43 to 54), or -1.</summary>
    public static int ShrubRowOf(int index) => index >= ShrubFirstIndex && index < ShrubFirstIndex + ShrubCount ? index - ShrubFirstIndex : -1;

    /// <summary>The dead-wood forms' measures without any texture or carrier (main thread, cached).</summary>
    public static UnderstoryDeadWoodDimensions LoadDeadWoodDimensions() => _deadWoodDimensions ??= BuildDeadWoodDimensions();

    private static UnderstoryDeadWoodDimensions BuildDeadWoodDimensions()
    {
        IReadOnlyList<ImportedTreeVariant> variants = ImportedTreeCatalog.Load();
        IReadOnlyList<DerivedTreeForm> forms = DerivedTreeCatalog.Load().Take(DeadWoodCount).ToList(); // the shrubs (43-54) are not dead wood
        var result = new UnderstoryDeadWoodDimensions
        {
            HeightMeters = new float[forms.Count], LengthMeters = new float[forms.Count], TrunkRadiusMeters = new float[forms.Count], Forms = new TreeFormKind[forms.Count],
        };
        for (int i = 0; i < forms.Count; i++)
        {
            result.HeightMeters[i] = variants[forms[i].SourceBaseVariant].HeightMeters;
            result.LengthMeters[i] = forms[i].LengthMeters;
            result.TrunkRadiusMeters[i] = forms[i].TrunkRadiusMeters;
            result.Forms[i] = forms[i].Form;
        }
        return result;
    }

    /// <summary>Test hook: forget the cached catalogs.</summary>
    public static void Reset() { _cached = null; _deadWood = null; _shrubs = null; _deadWoodDimensions = null; _textures = null; }

    private sealed record TextureSet(PreparedUnderstoryTextures Resource, Texture2DArray FoliageAlbedo, Texture2DArray FoliageNormal, Texture2DArray BarkAlbedo, Texture2DArray BarkNormal, long Bytes);

    private static TextureSet Textures() => _textures ??= LoadTextures();

    private static TextureSet LoadTextures()
    {
        PreparedUnderstoryTextures resource = ResourceLoader.Load<PreparedUnderstoryTextures>(TexturesPath) ?? throw new InvalidOperationException("missing " + TexturesPath + " (run tools/prepare_understory_textures.gd)");
        if (resource.SchemaVersion != PreparedUnderstoryTextures.CurrentSchemaVersion || resource.Constants.Length != 3 || resource.FoliageAlbedo.Count != 3 || resource.FoliageNormal.Count != 3 || resource.BarkAlbedo.Count != 3 || resource.BarkNormal.Count != 3)
            throw new InvalidOperationException("understory texture resource unreadable or unsupported");
        long bytes = 0;
        foreach (var images in new[] { resource.FoliageAlbedo, resource.FoliageNormal, resource.BarkAlbedo, resource.BarkNormal }) foreach (Image image in images) bytes += image.GetData().Length;
        return new TextureSet(resource, TextureArray(resource.FoliageAlbedo), TextureArray(resource.FoliageNormal), TextureArray(resource.BarkAlbedo), TextureArray(resource.BarkNormal), bytes);
    }

    private readonly record struct SurfaceTerms(Color Tint, float Roughness, float Metallic, bool RoughnessMap, bool MetallicMap, float NormalScale, float Cutoff, int Mode, bool Attachments);

    private sealed class Geometry
    {
        public readonly List<Vector4> Positions = new(), Tangents = new(), Attachments = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Vector4> Uvs = new();
        public SurfaceTerms? Bark, Foliage;
    }

    private static UnderstoryCatalog Build()
    {
        IReadOnlyList<ImportedTreeVariant> variants = ImportedTreeCatalog.Load();
        TextureSet textures = Textures();
        PreparedUnderstoryTextures resource = textures.Resource;
        Texture2DArray foliageAlbedo = textures.FoliageAlbedo, foliageNormal = textures.FoliageNormal, barkAlbedo = textures.BarkAlbedo, barkNormal = textures.BarkNormal;
        long bytes = textures.Bytes;

        var catalog = new UnderstoryCatalog { TextureBytes = bytes, TextureCount = 4, HeightMeters = new float[VariantCount] };
        TreeCrownHull[] hulls = TreeCrownHullTable.Get();
        for (int row = 0; row < VariantCount; row++) catalog.HeightMeters[row] = variants[SaplingSources[row]].HeightMeters;
        Shader shader = ResourceLoader.Load<Shader>(ShaderPath) ?? throw new InvalidOperationException("missing " + ShaderPath);
        catalog.Lods = new UnderstoryLodCarrier[SourceLods.Length];
        for (int l = 0; l < SourceLods.Length; l++)
        {
            int lod = SourceLods[l];
            var geometries = new Geometry[VariantCount];
            int maxVertices = 0;
            for (int row = 0; row < VariantCount; row++)
            {
                ImportedTreeVariant variant = variants[SaplingSources[row]];
                geometries[row] = Expand(variant.Lods[lod], (int)variant.Species);
                maxVertices = Math.Max(maxVertices, geometries[row].Positions.Count);
            }
            maxVertices = (maxVertices + 2) / 3 * 3;
            int width = Math.Min(maxVertices, MaximumTextureWidth), rowsPerVariant = (maxVertices + width - 1) / width;
            Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity), max = -min;
            float radius = 0f;
            foreach (Geometry g in geometries)
                foreach (Vector4 p in g.Positions) { var v = new Vector3(p.X, p.Y, p.Z); min = min.Min(v); max = max.Max(v); radius = Math.Max(radius, v.Length()); }
            var bounds = new Aabb(min, max - min);
            var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[maxVertices];
            var carrier = new ArrayMesh();
            carrier.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            carrier.CustomAabb = bounds;
            var material = new ShaderMaterial { Shader = shader, ResourceName = $"UnderstorySapling_L{lod}" };
            material.SetShaderParameter("vertex_data", DataTexture(width, rowsPerVariant, maxVertices, geometries, g => g.Positions, Vector4.Zero));
            material.SetShaderParameter("normal_data", DataTexture(width, rowsPerVariant, maxVertices, geometries, g => ToVector4(g.Normals), new Vector4(0, 1, 0, 1)));
            material.SetShaderParameter("tangent_data", DataTexture(width, rowsPerVariant, maxVertices, geometries, g => g.Tangents, new Vector4(1, 0, 0, 1)));
            material.SetShaderParameter("uv_data", DataTexture(width, rowsPerVariant, maxVertices, geometries, g => g.Uvs, Vector4.Zero));
            material.SetShaderParameter("attachment_data", DataTexture(width, rowsPerVariant, maxVertices, geometries, g => g.Attachments, Vector4.Zero));
            material.SetShaderParameter("fetch_width", width);
            material.SetShaderParameter("fetch_rows_per_variant", rowsPerVariant);
            material.SetShaderParameter("variant_parameters", ParameterTexture(geometries, hulls, resource.Constants, variants));
            material.SetShaderParameter("foliage_albedo", foliageAlbedo); material.SetShaderParameter("foliage_alpha", foliageAlbedo); material.SetShaderParameter("foliage_normal", foliageNormal);
            material.SetShaderParameter("bark_albedo", barkAlbedo); material.SetShaderParameter("bark_normal", barkNormal);
            material.SetShaderParameter("leaf_alpha_max_lod", MathF.Log2(PreparedFoliageSize(resource)) - 2f);
            CopySharedParameters(variants[SaplingSources[0]].Lods[lod], material);
            catalog.Lods[l] = new UnderstoryLodCarrier { Carrier = carrier, Material = material, VertexCount = maxVertices, TriangleCount = maxVertices / 3, Bounds = bounds, UnitRadius = radius, SourceLod = lod };
        }
        return catalog;
    }

    /// <summary>
    /// T6-6: the derived forms as two carriers (near: snag LOD1, log slot 0, stump slot 0; far: snag and log LODF, stump slot 0). Bark only: every vertex carries its species layer (no foliage flag); the variant parameter rows hold the bark terms of the
    /// source variant's material and no crown hull (so no translucency or crown terms); row 5 x = 1 switches the wind off for logs and stumps (snags sway as the trees do).
    /// </summary>
    private static UnderstoryDeadWood BuildDeadWood()
    {
        IReadOnlyList<ImportedTreeVariant> variants = ImportedTreeCatalog.Load();
        IReadOnlyList<DerivedTreeForm> forms = DerivedTreeCatalog.Load().Take(DeadWoodCount).ToList(); // the shrubs (43-54) are not dead wood
        TextureSet textures = Textures();
        Shader shader = ResourceLoader.Load<Shader>(ShaderPath) ?? throw new InvalidOperationException("missing " + ShaderPath);
        var result = new UnderstoryDeadWood { Dimensions = LoadDeadWoodDimensions() };
        for (int far = 0; far < 2; far++)
        {
            var geometries = new Geometry[forms.Count];
            for (int i = 0; i < forms.Count; i++)
            {
                // Slot 3 is a snag's or log's LODF trunk; the near carrier takes a snag's LOD1 and a log's or stump's slot 0.
                int slot = far == 1 && forms[i].Form != TreeFormKind.Stump ? 3 : forms[i].Form == TreeFormKind.Snag && far == 0 ? 1 : 0;
                Mesh mesh = forms[i].Lods[slot] ?? throw new InvalidOperationException($"derived form {forms[i].Index} has no mesh in slot {slot}");
                geometries[i] = Expand(mesh, (int)forms[i].Species);
            }
            Texture2D parameters = DeadWoodParameterTexture(geometries, forms, textures.Resource.Constants);
            UnderstoryLodCarrier carrier = MakeCarrier(far == 1 ? "UnderstoryDeadWood_Far" : "UnderstoryDeadWood_Near", far == 1 ? 3 : 1, geometries, parameters, textures, shader, variants[forms[0].SourceBaseVariant].Lods[0]);
            if (far == 1) result.Far = carrier; else result.Near = carrier;
        }
        return result;
    }

    /// <summary>
    /// T6-7: the shrubs as two carriers (LOD0 = mesh slot 0, LOD1 = slot 1 of each derived shrub form). Every vertex is a foliage vertex of the source species' layer. The parameter rows are the source foliage material's terms,
    /// wind mode 1 with attachments (the cards' Custom0, see PrepareDerivedShrubs), and the crown hull of the shrub's own card bounds (translucency and crown occlusion apply; shrubs are not in the canopy raster).
    /// </summary>
    private static UnderstoryShrubs BuildShrubs()
    {
        IReadOnlyList<ImportedTreeVariant> variants = ImportedTreeCatalog.Load();
        IReadOnlyList<DerivedTreeForm> forms = DerivedTreeCatalog.Load().Skip(DeadWoodCount).Take(ShrubCount).ToList();
        TextureSet textures = Textures();
        Shader shader = ResourceLoader.Load<Shader>(ShaderPath) ?? throw new InvalidOperationException("missing " + ShaderPath);
        var result = new UnderstoryShrubs { HeightMeters = new float[forms.Count], Hulls = new TreeCrownHull[forms.Count] };
        for (int i = 0; i < forms.Count; i++)
        {
            result.HeightMeters[i] = 1f;
            result.Hulls[i] = TreeCrownHullTable.FromShrub(forms[i].Index, forms[i].Species, forms[i].Lods[0] ?? throw new InvalidOperationException($"shrub {forms[i].Index} has no LOD0"));
        }
        for (int lod = 0; lod < 2; lod++)
        {
            var geometries = new Geometry[forms.Count];
            for (int i = 0; i < forms.Count; i++) geometries[i] = Expand(forms[i].Lods[lod] ?? throw new InvalidOperationException($"shrub {forms[i].Index} has no LOD{lod}"), (int)forms[i].Species);
            UnderstoryLodCarrier carrier = MakeCarrier($"UnderstoryShrub_L{lod}", lod, geometries, ShrubParameterTexture(geometries, forms, result.Hulls, textures.Resource.Constants), textures, shader, variants[forms[0].SourceBaseVariant].Lods[0]);
            if (lod == 0) result.Lod0 = carrier; else result.Lod1 = carrier;
        }
        return result;
    }

    /// <summary>Rows as <see cref="ParameterTexture"/> for the shrubs: the foliage terms in the bark and foliage slots, row 4 = wind mode 1 with attachments, rows 8 to 10 the shrub's crown hull.</summary>
    private static ImageTexture ShrubParameterTexture(Geometry[] geometries, IReadOnlyList<DerivedTreeForm> forms, TreeCrownHull[] hulls, Vector4[] constants)
    {
        var image = Image.CreateEmpty(geometries.Length, ParameterRowCount, false, Image.Format.Rgbaf);
        for (int i = 0; i < geometries.Length; i++)
        {
            Vector4 layer = constants[(int)forms[i].Species];
            SurfaceTerms foliage = geometries[i].Foliage ?? throw new InvalidOperationException("shrub variant without a foliage surface");
            float roughness = Fold(foliage.Roughness, foliage.RoughnessMap, layer.X), metallic = Fold(foliage.Metallic, foliage.MetallicMap, layer.Y);
            image.SetPixel(i, 0, foliage.Tint);
            image.SetPixel(i, 1, foliage.Tint);
            image.SetPixel(i, 2, new Color(roughness, metallic, roughness, metallic));
            image.SetPixel(i, 3, new Color(foliage.NormalScale, foliage.NormalScale, foliage.Cutoff, 0f));
            image.SetPixel(i, 4, new Color(1f, 1f, 1f, 1f));
            TreeCrownHullTable.Pack(hulls[i], out Vector4 hull, out Vector4 extra, out Vector4 lad);
            image.SetPixel(i, 8, new Color(hull.X, hull.Y, hull.Z, hull.W));
            image.SetPixel(i, 9, new Color(extra.X, extra.Y, extra.Z, extra.W));
            image.SetPixel(i, 10, new Color(lad.X, lad.Y, lad.Z, lad.W));
        }
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>Rows as <see cref="ParameterTexture"/> for bark only, plus row 5 (x = 1: wind off for this variant).</summary>
    private static ImageTexture DeadWoodParameterTexture(Geometry[] geometries, IReadOnlyList<DerivedTreeForm> forms, Vector4[] constants)
    {
        var image = Image.CreateEmpty(geometries.Length, ParameterRowCount, false, Image.Format.Rgbaf);
        for (int i = 0; i < geometries.Length; i++)
        {
            Vector4 layer = constants[(int)forms[i].Species];
            SurfaceTerms bark = geometries[i].Bark ?? throw new InvalidOperationException("dead wood variant without a bark surface");
            image.SetPixel(i, 0, bark.Tint);
            image.SetPixel(i, 1, bark.Tint);
            image.SetPixel(i, 2, new Color(Fold(bark.Roughness, bark.RoughnessMap, layer.Z), Fold(bark.Metallic, bark.MetallicMap, layer.W), 1f, 0f));
            image.SetPixel(i, 3, new Color(bark.NormalScale, bark.NormalScale, bark.Cutoff, 0f));
            image.SetPixel(i, 4, new Color(bark.Mode, bark.Attachments ? 1f : 0f, bark.Mode, bark.Attachments ? 1f : 0f));
            image.SetPixel(i, 5, new Color(forms[i].Form == TreeFormKind.Snag ? 0f : 1f, 0f, 0f, 0f));
        }
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>One vertex-fetch carrier over the geometries (variant rows in order): the mesh, the data textures and the shader parameters shared by every carrier.</summary>
    private static UnderstoryLodCarrier MakeCarrier(string name, int sourceLod, Geometry[] geometries, Texture2D parameters, TextureSet textures, Shader shader, Mesh sharedParameterSource)
    {
        int maxVertices = 0;
        foreach (Geometry g in geometries) maxVertices = Math.Max(maxVertices, g.Positions.Count);
        maxVertices = (maxVertices + 2) / 3 * 3;
        int width = Math.Min(maxVertices, MaximumTextureWidth), rowsPerVariant = (maxVertices + width - 1) / width;
        Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity), max = -min;
        float radius = 0f;
        foreach (Geometry g in geometries)
            foreach (Vector4 p in g.Positions) { var v = new Vector3(p.X, p.Y, p.Z); min = min.Min(v); max = max.Max(v); radius = Math.Max(radius, v.Length()); }
        var bounds = new Aabb(min, max - min);
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[maxVertices];
        var carrier = new ArrayMesh();
        carrier.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        carrier.CustomAabb = bounds;
        var material = new ShaderMaterial { Shader = shader, ResourceName = name };
        material.SetShaderParameter("vertex_data", DataTexture(width, rowsPerVariant, maxVertices, geometries, g => g.Positions, Vector4.Zero));
        material.SetShaderParameter("normal_data", DataTexture(width, rowsPerVariant, maxVertices, geometries, g => ToVector4(g.Normals), new Vector4(0, 1, 0, 1)));
        material.SetShaderParameter("tangent_data", DataTexture(width, rowsPerVariant, maxVertices, geometries, g => g.Tangents, new Vector4(1, 0, 0, 1)));
        material.SetShaderParameter("uv_data", DataTexture(width, rowsPerVariant, maxVertices, geometries, g => g.Uvs, Vector4.Zero));
        material.SetShaderParameter("attachment_data", DataTexture(width, rowsPerVariant, maxVertices, geometries, g => g.Attachments, Vector4.Zero));
        material.SetShaderParameter("fetch_width", width);
        material.SetShaderParameter("fetch_rows_per_variant", rowsPerVariant);
        material.SetShaderParameter("variant_parameters", parameters);
        material.SetShaderParameter("foliage_albedo", textures.FoliageAlbedo); material.SetShaderParameter("foliage_alpha", textures.FoliageAlbedo); material.SetShaderParameter("foliage_normal", textures.FoliageNormal);
        material.SetShaderParameter("bark_albedo", textures.BarkAlbedo); material.SetShaderParameter("bark_normal", textures.BarkNormal);
        material.SetShaderParameter("leaf_alpha_max_lod", MathF.Log2(PreparedFoliageSize(textures.Resource)) - 2f);
        CopySharedParameters(sharedParameterSource, material);
        return new UnderstoryLodCarrier { Carrier = carrier, Material = material, VertexCount = maxVertices, TriangleCount = maxVertices / 3, Bounds = bounds, UnitRadius = radius, SourceLod = sourceLod };
    }

    private static int PreparedFoliageSize(PreparedUnderstoryTextures resource) => resource.FoliageAlbedo[0].GetWidth();

    private static Texture2DArray TextureArray(Godot.Collections.Array<Image> images)
    {
        var array = new Texture2DArray();
        if (array.CreateFromImages(images) != Error.Ok) throw new InvalidOperationException("could not create an understory texture array");
        return array;
    }

    private static void CopySharedParameters(Mesh mesh, ShaderMaterial destination)
    {
        if (mesh.SurfaceGetMaterial(0) is not ShaderMaterial source) return;
        foreach (string parameter in new[] { "ring_center", "sun_direction", "day_start_dot", "day_full_dot", "night_tint", "night_direct_light_factor", "day_direct_light_factor", "vegetation_wind_enabled" })
            destination.SetShaderParameter(parameter, source.GetShaderParameter(parameter));
        destination.SetShaderParameter("simplified_tree_shading", source.GetShaderParameter("simplified_tree_shading"));
    }

    private static List<Vector4> ToVector4(List<Vector3> values)
    {
        var result = new List<Vector4>(values.Count);
        foreach (Vector3 v in values) result.Add(new Vector4(v.X, v.Y, v.Z, 1f));
        return result;
    }

    private static Geometry Expand(Mesh mesh, int species)
    {
        var result = new Geometry();
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            if (mesh.SurfaceGetMaterial(surface) is not ShaderMaterial material) throw new InvalidOperationException("understory source surface without a shader material");
            bool foliage = material.GetShaderParameter("use_alpha_cutout").AsBool();
            var terms = new SurfaceTerms(material.GetShaderParameter("albedo_tint").AsColor(), material.GetShaderParameter("roughness_factor").AsSingle(), material.GetShaderParameter("metallic_factor").AsSingle(),
                material.GetShaderParameter("has_roughness_texture").AsBool(), material.GetShaderParameter("has_metallic_texture").AsBool(),
                material.GetShaderParameter("normal_scale").AsSingle(), material.GetShaderParameter("alpha_cutoff").AsSingle(), material.GetShaderParameter("tree_foliage_mode").AsInt32(), material.GetShaderParameter("tree_foliage_attachments").AsBool());
            // The surfaces of one kind (bark, foliage) of a variant must agree: they share one material per texture set in the catalog.
            SurfaceTerms? existing = foliage ? result.Foliage : result.Bark;
            if (existing is { } e && e != terms) throw new InvalidOperationException($"understory source variant has two different {(foliage ? "foliage" : "bark")} materials (species {species})");
            if (foliage) result.Foliage = terms; else result.Bark = terms;
            Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surface);
            Vector3[] positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Vector3[] normals = Read3(arrays, Mesh.ArrayType.Normal);
            Vector2[] uvs = Read2(arrays, Mesh.ArrayType.TexUV), uv2s = Read2(arrays, Mesh.ArrayType.TexUV2);
            float[] tangents = ReadF(arrays, Mesh.ArrayType.Tangent), attachments = ReadF(arrays, Mesh.ArrayType.Custom0);
            int[] indices = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? Sequential(positions.Length) : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            float layer = species + (foliage ? 4f : 0f);
            foreach (int i in indices)
            {
                result.Positions.Add(new Vector4(positions[i].X, positions[i].Y, positions[i].Z, layer));
                result.Normals.Add(normals.Length > i ? normals[i] : Vector3.Up);
                Vector2 uv = uvs.Length > i ? uvs[i] : Vector2.Zero, uv2 = uv2s.Length > i ? uv2s[i] : Vector2.Zero;
                result.Uvs.Add(new Vector4(uv.X, uv.Y, uv2.X, uv2.Y));
                result.Tangents.Add(tangents.Length >= (i + 1) * 4 ? new Vector4(tangents[i * 4], tangents[i * 4 + 1], tangents[i * 4 + 2], tangents[i * 4 + 3]) : new Vector4(1, 0, 0, 1));
                result.Attachments.Add(attachments.Length >= (i + 1) * 4 ? new Vector4(attachments[i * 4], attachments[i * 4 + 1], attachments[i * 4 + 2], attachments[i * 4 + 3]) : Vector4.Zero);
            }
        }
        return result;
    }

    private static Texture2D DataTexture(int width, int rowsPerVariant, int vertexCount, Geometry[] geometries, Func<Geometry, List<Vector4>> channel, Vector4 padding)
    {
        int height = geometries.Length * rowsPerVariant;
        var texels = new float[width * height * 4];
        for (int row = 0; row < geometries.Length; row++)
        {
            List<Vector4> values = channel(geometries[row]);
            for (int i = 0; i < vertexCount; i++)
            {
                Vector4 value = i < values.Count ? values[i] : padding;
                int at = ((row * rowsPerVariant + i / width) * width + i % width) * 4;
                texels[at] = value.X; texels[at + 1] = value.Y; texels[at + 2] = value.Z; texels[at + 3] = value.W;
            }
        }
        var bytes = new byte[texels.Length * sizeof(float)];
        Buffer.BlockCopy(texels, 0, bytes, 0, bytes.Length);
        return ImageTexture.CreateFromImage(Image.CreateFromData(width, height, false, Image.Format.Rgbaf, bytes));
    }

    /// <summary>
    /// Rows (variant per column): 0 bark tint, 1 foliage tint, 2 (bark roughness, bark metallic, foliage roughness, foliage metallic) as the material would compute them (the map is replaced by its layer mean),
    /// 3 (bark normal scale, foliage normal scale, foliage alpha cutoff, 0), 4 (bark wind mode, bark attachments, foliage wind mode, foliage attachments), 8 crown hull, 9 hull extra, 10 leaf inclination (TreeCrownHullTable.Pack, the impostor's rows).
    /// </summary>
    private static ImageTexture ParameterTexture(Geometry[] geometries, TreeCrownHull[] hulls, Vector4[] constants, IReadOnlyList<ImportedTreeVariant> variants)
    {
        var image = Image.CreateEmpty(geometries.Length, ParameterRowCount, false, Image.Format.Rgbaf);
        for (int i = 0; i < geometries.Length; i++)
        {
            int species = (int)variants[SaplingSources[i]].Species;
            Vector4 layer = constants[species]; // (foliage roughness, foliage metallic, bark roughness, bark metallic)
            SurfaceTerms bark = geometries[i].Bark ?? geometries[i].Foliage ?? throw new InvalidOperationException("understory variant without surfaces");
            SurfaceTerms foliage = geometries[i].Foliage ?? bark;
                        image.SetPixel(i, 0, bark.Tint);
            image.SetPixel(i, 1, foliage.Tint);
            image.SetPixel(i, 2, new Color(Fold(bark.Roughness, bark.RoughnessMap, layer.Z), Fold(bark.Metallic, bark.MetallicMap, layer.W), Fold(foliage.Roughness, foliage.RoughnessMap, layer.X), Fold(foliage.Metallic, foliage.MetallicMap, layer.Y)));
            image.SetPixel(i, 3, new Color(bark.NormalScale, foliage.NormalScale, foliage.Cutoff, 0f));
            image.SetPixel(i, 4, new Color(bark.Mode, bark.Attachments ? 1f : 0f, foliage.Mode, foliage.Attachments ? 1f : 0f));
            TreeCrownHullTable.Pack(hulls[SaplingSources[i]], out Vector4 hull, out Vector4 extra, out Vector4 lad);
            image.SetPixel(i, 8, new Color(hull.X, hull.Y, hull.Z, hull.W));
            image.SetPixel(i, 9, new Color(extra.X, extra.Y, extra.Z, extra.W));
            image.SetPixel(i, 10, new Color(lad.X, lad.Y, lad.Z, lad.W));
        }
        return ImageTexture.CreateFromImage(image);
    }

    // imported_tree.gdshader: roughness = (map ? texture.g : 1) * factor, metallic = (map ? texture.b : 1) * factor; the map is replaced by its layer mean.
    private static float Fold(float factor, bool hasMap, float mean) => (hasMap ? mean : 1f) * factor;

    private static Vector3[] Read3(Godot.Collections.Array arrays, Mesh.ArrayType type) => arrays[(int)type].VariantType == Variant.Type.Nil ? System.Array.Empty<Vector3>() : arrays[(int)type].AsVector3Array();
    private static Vector2[] Read2(Godot.Collections.Array arrays, Mesh.ArrayType type) => arrays[(int)type].VariantType == Variant.Type.Nil ? System.Array.Empty<Vector2>() : arrays[(int)type].AsVector2Array();
    private static float[] ReadF(Godot.Collections.Array arrays, Mesh.ArrayType type) => arrays[(int)type].VariantType == Variant.Type.Nil ? System.Array.Empty<float>() : arrays[(int)type].AsFloat32Array();
    private static int[] Sequential(int count) { var result = new int[count]; for (int i = 0; i < count; i++) result[i] = i; return result; }
}
