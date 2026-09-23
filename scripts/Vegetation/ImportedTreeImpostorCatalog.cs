using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>One shared LOD3 mesh whose per-instance custom X selects an authored tree billboard.</summary>
public sealed class ImportedTreeImpostorCatalog
{
    public const int TextureLayerSize = 256;
    public Mesh Mesh { get; }
    public ShaderMaterial Material { get; }
    public int VertexCount { get; }
    public int VariantCount { get; }
    /// <summary>Expanded, unindexed source vertex count for each authored LOD3 mesh.</summary>
    public IReadOnlyList<int> ExpandedVertexCounts { get; }
    /// <summary>CPU copies used by the structural asset regression; positions are in catalog-normalized space.</summary>
    public IReadOnlyList<Vector3[]> ExpandedPositions { get; }

    private static ImportedTreeImpostorCatalog? _cached;
    private ImportedTreeImpostorCatalog(ArrayMesh mesh, ShaderMaterial material, int vertexCount,
        IReadOnlyList<int> expandedVertexCounts, IReadOnlyList<Vector3[]> expandedPositions)
    {
        Mesh = mesh; Material = material; VertexCount = vertexCount;
        VariantCount = ImportedTreeCatalog.VariantCount;
        ExpandedVertexCounts = expandedVertexCounts; ExpandedPositions = expandedPositions;
    }

    public static ImportedTreeImpostorCatalog Load()
    {
        if (_cached != null) return _cached;
        var variants = ImportedTreeCatalog.Load();
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

        ImageTexture positions = CreateDataTexture(maxVertices, geometries, DataChannel.Position);
        ImageTexture normals = CreateDataTexture(maxVertices, geometries, DataChannel.Normal);
        ImageTexture uvs = CreateDataTexture(maxVertices, geometries, DataChannel.Uv);
        ImageTexture tangents = CreateDataTexture(maxVertices, geometries, DataChannel.Tangent);
        ArrayMesh mesh = CreateSharedMesh(maxVertices, bounds);
        ShaderMaterial material = CreateMaterial(variants, positions, normals, uvs, tangents);
        mesh.SurfaceSetMaterial(0, material);

        var counts = new List<int>(geometries.Count);
        var expandedPositions = new List<Vector3[]>(geometries.Count);
        foreach (Geometry geometry in geometries) { counts.Add(geometry.Positions.Length); expandedPositions.Add(geometry.Positions); }
        _cached = new ImportedTreeImpostorCatalog(mesh, material, maxVertices, counts.AsReadOnly(), expandedPositions.AsReadOnly());
        return _cached;
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

    private static ShaderMaterial CreateMaterial(IReadOnlyList<ImportedTreeVariant> variants, Texture2D positions, Texture2D normals, Texture2D uvs, Texture2D tangents)
    {
        Shader shader = ResourceLoader.Load<Shader>("res://materials/imported_tree_impostor.gdshader") ?? throw new InvalidOperationException("Missing imported-tree impostor shader.");
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("vertex_data", positions); material.SetShaderParameter("normal_data", normals);
        material.SetShaderParameter("uv_data", uvs); material.SetShaderParameter("tangent_data", tangents);
        material.SetShaderParameter("albedo_layers", CreateTextureArray(variants, "albedo_texture", new Color(1, 1, 1, 1)));
        material.SetShaderParameter("normal_layers", CreateTextureArray(variants, "normal_texture", new Color(.5f, .5f, 1, 1)));
        material.SetShaderParameter("metallic_roughness_layers", CreateMetallicRoughnessArray(variants));
        material.SetShaderParameter("variant_parameters", CreateParameterTexture(variants));
        CopySharedParameters(variants[0].Lods[3].SurfaceGetMaterial(0) as ShaderMaterial, material);
        return material;
    }

    private static void CopySharedParameters(ShaderMaterial? source, ShaderMaterial destination)
    {
        if (source == null) return;
        foreach (string parameter in new[] { "ring_center", "sun_direction", "day_start_dot", "day_full_dot", "night_tint", "night_direct_light_factor", "day_direct_light_factor", "vegetation_wind_enabled" })
            destination.SetShaderParameter(parameter, source.GetShaderParameter(parameter));
    }

    private static Texture2DArray CreateTextureArray(IReadOnlyList<ImportedTreeVariant> variants, string parameter, Color fallback)
    {
        var images = new Godot.Collections.Array<Image>();
        foreach (ImportedTreeVariant variant in variants)
        {
            ShaderMaterial? material = variant.Lods[3].SurfaceGetMaterial(0) as ShaderMaterial;
            Texture2D? texture = material == null ? null : material.GetShaderParameter(parameter).AsGodotObject() as Texture2D;
            images.Add(PrepareImage(texture, fallback));
        }
        var array = new Texture2DArray();
        if (array.CreateFromImages(images) != Error.Ok) throw new InvalidOperationException($"Could not create {parameter} texture array.");
        return array;
    }

    private static Texture2DArray CreateMetallicRoughnessArray(IReadOnlyList<ImportedTreeVariant> variants)
    {
        var images = new Godot.Collections.Array<Image>();
        foreach (ImportedTreeVariant variant in variants)
        {
            ShaderMaterial? material = variant.Lods[3].SurfaceGetMaterial(0) as ShaderMaterial;
            Image metallic = PrepareImage(material == null ? null : material.GetShaderParameter("metallic_texture").AsGodotObject() as Texture2D, new Color(0, 0, 1, 1));
            Image roughness = PrepareImage(material == null ? null : material.GetShaderParameter("roughness_texture").AsGodotObject() as Texture2D, new Color(0, 1, 0, 1));
            var packed = Image.CreateEmpty(TextureLayerSize, TextureLayerSize, false, Image.Format.Rgba8);
            for (int y = 0; y < TextureLayerSize; y++) for (int x = 0; x < TextureLayerSize; x++) { Color a = metallic.GetPixel(x, y); Color r = roughness.GetPixel(x, y); packed.SetPixel(x, y, new Color(0, r.G, a.B, 1)); }
            packed.GenerateMipmaps(); images.Add(packed);
        }
        var array = new Texture2DArray(); if (array.CreateFromImages(images) != Error.Ok) throw new InvalidOperationException("Could not create metallic/roughness texture array."); return array;
    }

    private static Image PrepareImage(Texture2D? texture, Color fallback)
    {
        Image image;
        if (texture?.GetImage() is Image source && !source.IsEmpty())
        {
            image = (Image)source.Duplicate();
            if (image.IsCompressed() && image.Decompress() != Error.Ok)
                throw new InvalidOperationException("Could not decompress imported tree texture image.");
        }
        else
        {
            image = Image.CreateEmpty(TextureLayerSize, TextureLayerSize, false, Image.Format.Rgba8);
            image.Fill(fallback); image.GenerateMipmaps();
            return image;
        }
        image.Convert(Image.Format.Rgba8); image.Resize(TextureLayerSize, TextureLayerSize, Image.Interpolation.Lanczos); image.GenerateMipmaps(); return image;
    }

    private static ImageTexture CreateDataTexture(int width, IReadOnlyList<Geometry> geometries, DataChannel channel)
    {
        var image = Image.CreateEmpty(width, geometries.Count, false, Image.Format.Rgbaf);
        for (int row = 0; row < geometries.Count; row++)
            for (int column = 0; column < width; column++) image.SetPixel(column, row, geometries[row].Get(channel, column));
        return ImageTexture.CreateFromImage(image);
    }

    private static ImageTexture CreateParameterTexture(IReadOnlyList<ImportedTreeVariant> variants)
    {
        var image = Image.CreateEmpty(variants.Count, 3, false, Image.Format.Rgbaf);
        for (int i = 0; i < variants.Count; i++)
        {
            ShaderMaterial? material = variants[i].Lods[3].SurfaceGetMaterial(0) as ShaderMaterial;
            Color tint = material == null ? Colors.White : material.GetShaderParameter("albedo_tint").AsColor();
            image.SetPixel(i, 0, tint);
            image.SetPixel(i, 1, new Color(material == null ? 1 : material.GetShaderParameter("normal_scale").AsSingle(), material == null ? 0 : material.GetShaderParameter("metallic_factor").AsSingle(), material == null ? 1 : material.GetShaderParameter("roughness_factor").AsSingle(), material == null ? .25f : material.GetShaderParameter("alpha_cutoff").AsSingle()));
            image.SetPixel(i, 2, new Color(material != null && material.GetShaderParameter("use_alpha_cutout").AsBool() ? 1 : 0, 0, 0, 1));
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
            int[] indices = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? Sequential(positions.Length) : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            foreach (int index in indices) result.Add(positions[index], normals.Length > index ? normals[index] : Vector3.Up, uvs.Length > index ? uvs[index] : Vector2.Zero, uv2s.Length > index ? uv2s[index] : Vector2.Zero, tangents.Length >= (index + 1) * 4 ? new Vector4(tangents[index * 4], tangents[index * 4 + 1], tangents[index * 4 + 2], tangents[index * 4 + 3]) : new Vector4(1, 0, 0, 1));
        }
        return result;
    }
    private static Vector3[] ReadVector3Array(Godot.Collections.Array arrays, Mesh.ArrayType type) => arrays[(int)type].VariantType == Variant.Type.Nil ? Array.Empty<Vector3>() : arrays[(int)type].AsVector3Array();
    private static Vector2[] ReadVector2Array(Godot.Collections.Array arrays, Mesh.ArrayType type) => arrays[(int)type].VariantType == Variant.Type.Nil ? Array.Empty<Vector2>() : arrays[(int)type].AsVector2Array();
    private static float[] ReadFloatArray(Godot.Collections.Array arrays, Mesh.ArrayType type) => arrays[(int)type].VariantType == Variant.Type.Nil ? Array.Empty<float>() : arrays[(int)type].AsFloat32Array();
    private static int[] Sequential(int count) { var result = new int[count]; for (int i = 0; i < count; i++) result[i] = i; return result; }
    private enum DataChannel { Position, Normal, Uv, Tangent }
    private sealed class Geometry
    {
        private readonly List<Vector3> _positions = new(); private readonly List<Vector3> _normals = new(); private readonly List<Vector2> _uvs = new(); private readonly List<Vector2> _uv2s = new(); private readonly List<Vector4> _tangents = new();
        public Vector3[] Positions => _positions.ToArray();
        public void Add(Vector3 p, Vector3 n, Vector2 uv, Vector2 uv2, Vector4 tangent) { _positions.Add(p); _normals.Add(n); _uvs.Add(uv); _uv2s.Add(uv2); _tangents.Add(tangent); }
        public Color Get(DataChannel channel, int index) { if (index >= _positions.Count) return channel == DataChannel.Position ? new Color(0, 0, 0, 0) : new Color(0, 1, 0, 1); return channel switch { DataChannel.Position => new Color(_positions[index].X, _positions[index].Y, _positions[index].Z, 1), DataChannel.Normal => new Color(_normals[index].X, _normals[index].Y, _normals[index].Z, 1), DataChannel.Uv => new Color(_uvs[index].X, _uvs[index].Y, _uv2s[index].X, _uv2s[index].Y), _ => new Color(_tangents[index].X, _tangents[index].Y, _tangents[index].Z, _tangents[index].W) }; }
    }
}
