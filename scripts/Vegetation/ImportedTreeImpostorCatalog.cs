using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

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
    public const int FarQuadVertexCount = 6;
    /// <summary>Row 3 of variant_parameters: (u at the plane's left edge, u at its right edge, v at its bottom, v at its top). Row 4: (width, bottom y, top y, 0), in catalog-normalized tree space.</summary>
    public readonly record struct FarQuadPlane(float ULeft, float URight, float VBottom, float VTop, float Width, float BottomY, float TopY, int TriangleCount, float Area);
    public int VertexCount { get; }
    public int VariantCount { get; }
    /// <summary>Expanded, unindexed source vertex count for each authored LOD3 mesh.</summary>
    public IReadOnlyList<int> ExpandedVertexCounts { get; }
    /// <summary>CPU copies used by the structural asset regression; positions are in catalog-normalized space.</summary>
    public IReadOnlyList<Vector3[]> ExpandedPositions { get; }
    public IReadOnlyList<Vector2[]> ExpandedUvs { get; }

    private static ImportedTreeImpostorCatalog? _cached;
    private ImportedTreeImpostorCatalog(ArrayMesh mesh, ShaderMaterial material, ArrayMesh quadMesh, ShaderMaterial quadMaterial, IReadOnlyList<FarQuadPlane> planes, int vertexCount,
        IReadOnlyList<int> expandedVertexCounts, IReadOnlyList<Vector3[]> expandedPositions, IReadOnlyList<Vector2[]> expandedUvs)
    {
        Mesh = mesh; Material = material; FarQuadMesh = quadMesh; FarQuadMaterial = quadMaterial; FarQuadPlanes = planes; VertexCount = vertexCount;
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

        var images = new Godot.Collections.Array<Image>();
        foreach (bool unit in PrepareTextureImages(variants, "albedo_texture", new Color(1, 1, 1, 1), images)) yield return unit;
        Texture2DArray albedoLayers = CreateTextureArray(images, "albedo_texture"); yield return true;
        images = new Godot.Collections.Array<Image>();
        foreach (bool unit in PrepareTextureImages(variants, "normal_texture", new Color(.5f, .5f, 1, 1), images)) yield return unit;
        Texture2DArray normalLayers = CreateTextureArray(images, "normal_texture"); yield return true;
        images = new Godot.Collections.Array<Image>();
        foreach (bool unit in PrepareMetallicRoughnessImages(variants, images)) yield return unit;
        Texture2DArray metallicRoughnessLayers = CreateTextureArray(images, "metallic/roughness"); yield return true;

        var planes = new List<FarQuadPlane>(geometries.Count);
        foreach (Geometry geometry in geometries) planes.Add(geometry.LargestPlane());
        ShaderMaterial material = CreateMaterial(variants, planes, positions, normals, uvs, tangents, attachments, albedoLayers, normalLayers, metallicRoughnessLayers);
        mesh.SurfaceSetMaterial(0, material);
        ArrayMesh quadMesh = CreateFarQuadMesh(bounds);
        var quadMaterial = (ShaderMaterial)material.Duplicate();
        quadMaterial.SetShaderParameter("far_quad_mode", true);
        quadMesh.SurfaceSetMaterial(0, quadMaterial);

        var counts = new List<int>(geometries.Count);
        var expandedPositions = new List<Vector3[]>(geometries.Count); var expandedUvs = new List<Vector2[]>(geometries.Count);
        foreach (Geometry geometry in geometries) { counts.Add(geometry.Positions.Length); expandedPositions.Add(geometry.Positions); expandedUvs.Add(geometry.Uvs); }
        _cached ??= new ImportedTreeImpostorCatalog(mesh, material, quadMesh, quadMaterial, planes.AsReadOnly(), maxVertices, counts.AsReadOnly(), expandedPositions.AsReadOnly(), expandedUvs.AsReadOnly());
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

    private static ShaderMaterial CreateMaterial(IReadOnlyList<ImportedTreeVariant> variants, IReadOnlyList<FarQuadPlane> planes, Texture2D positions, Texture2D normals, Texture2D uvs, Texture2D tangents, Texture2D attachments,
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
        material.SetShaderParameter("variant_parameters", CreateParameterTexture(variants, planes));
        CopySharedParameters(variants[0].Lods[3].SurfaceGetMaterial(0) as ShaderMaterial, material);
        return material;
    }

    private static void CopySharedParameters(ShaderMaterial? source, ShaderMaterial destination)
    {
        if (source == null) return;
        foreach (string parameter in new[] { "ring_center", "sun_direction", "day_start_dot", "day_full_dot", "night_tint", "night_direct_light_factor", "day_direct_light_factor", "vegetation_wind_enabled" })
            destination.SetShaderParameter(parameter, source.GetShaderParameter(parameter));
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
        image.Convert(Image.Format.Rgba8); image.Resize(TextureLayerSize, TextureLayerSize, Image.Interpolation.Lanczos); if (mipmaps) image.GenerateMipmaps(); return image;
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

    private static ImageTexture CreateParameterTexture(IReadOnlyList<ImportedTreeVariant> variants, IReadOnlyList<FarQuadPlane> planes)
    {
        var image = Image.CreateEmpty(variants.Count, 5, false, Image.Format.Rgbaf);
        for (int i = 0; i < variants.Count; i++)
        {
            ShaderMaterial? material = variants[i].Lods[3].SurfaceGetMaterial(0) as ShaderMaterial;
            Color tint = material == null ? Colors.White : material.GetShaderParameter("albedo_tint").AsColor();
            image.SetPixel(i, 0, tint);
            image.SetPixel(i, 1, new Color(material == null ? 1 : material.GetShaderParameter("normal_scale").AsSingle(), material == null ? 0 : material.GetShaderParameter("metallic_factor").AsSingle(), material == null ? 1 : material.GetShaderParameter("roughness_factor").AsSingle(), material == null ? .25f : material.GetShaderParameter("alpha_cutoff").AsSingle()));
            image.SetPixel(i, 2, new Color(material != null && material.GetShaderParameter("use_alpha_cutout").AsBool() ? 1 : 0,
                material == null ? 2 : material.GetShaderParameter("tree_foliage_mode").AsInt32(),
                material != null && material.GetShaderParameter("tree_foliage_attachments").AsBool() ? 1 : 0, 1));
            FarQuadPlane plane = planes[i];
            image.SetPixel(i, 3, new Color(plane.ULeft, plane.URight, plane.VBottom, plane.VTop));
            image.SetPixel(i, 4, new Color(plane.Width, plane.BottomY, plane.TopY, 0));
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
            return new FarQuadPlane(uLeft, uRight, vBottom, vTop, s1 - s0, y0, y1, bestTriangles, bestArea);
        }
        public void Add(Vector3 p, Vector3 n, Vector2 uv, Vector2 uv2, Vector4 tangent, Vector4 attachment) { _positions.Add(p); _normals.Add(n); _uvs.Add(uv); _uv2s.Add(uv2); _tangents.Add(tangent); _attachments.Add(attachment); }
        public Color Get(DataChannel channel, int index) { if (index >= _positions.Count) return channel == DataChannel.Position || channel == DataChannel.Attachment ? new Color(0, 0, 0, 0) : new Color(0, 1, 0, 1); return channel switch { DataChannel.Position => new Color(_positions[index].X, _positions[index].Y, _positions[index].Z, 1), DataChannel.Normal => new Color(_normals[index].X, _normals[index].Y, _normals[index].Z, 1), DataChannel.Uv => new Color(_uvs[index].X, _uvs[index].Y, _uv2s[index].X, _uv2s[index].Y), DataChannel.Attachment => new Color(_attachments[index].X,_attachments[index].Y,_attachments[index].Z,_attachments[index].W), _ => new Color(_tangents[index].X, _tangents[index].Y, _tangents[index].Z, _tangents[index].W) }; }
    }
}
