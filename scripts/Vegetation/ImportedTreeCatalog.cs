using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>Source species ordering used by the imported LOLIPOP tree catalog.</summary>
public enum TreeSpecies { Maple, Fir, Pine }

/// <summary>
/// A root-anchored, authored tree. Every LOD is normalized with the LOD0
/// height on every axis, matching the placement layer's uniform tree scale.
/// </summary>
public sealed class ImportedTreeVariant
{
    public int Index { get; }
    public TreeSpecies Species { get; }
    public string Name { get; }
    public Mesh[] Lods { get; }
    /// <summary>Per-surface shader materials used by all LODs; update these for shared celestial state.</summary>
    public IReadOnlyList<ShaderMaterial> Materials { get; }
    public float WidthMeters { get; }
    public float HeightMeters { get; }
    public float TrunkRadiusMeters { get; }
    public float TrunkHeightMeters { get; }

    internal ImportedTreeVariant(int index, TreeSpecies species, string name, Mesh[] lods, IReadOnlyList<ShaderMaterial> materials,
        float widthMeters, float heightMeters, float trunkRadiusMeters, float trunkHeightMeters)
    {
        Index = index; Species = species; Name = name; Lods = lods; Materials = materials;
        WidthMeters = widthMeters; HeightMeters = heightMeters;
        TrunkRadiusMeters = trunkRadiusMeters; TrunkHeightMeters = trunkHeightMeters;
    }
}

/// <summary>
/// Converts the imported LOLIPOP scene layout into production MultiMesh-ready
/// meshes.  This is intentionally main-thread only: Godot scene resources and
/// imported textures are not safe to construct on worker threads.
/// </summary>
public static class ImportedTreeCatalog
{
    public const int CatalogVersion = 1;
    public const int VariantCount = 30;
    private const string LodPattern = "_LOD";
    private static readonly TreeSpecies[] OrderedSpecies = { TreeSpecies.Maple, TreeSpecies.Fir, TreeSpecies.Pine };
    private static IReadOnlyList<ImportedTreeVariant>? _variants;

    public static IReadOnlyList<ImportedTreeVariant> Load()
    {
        if (_variants != null) return _variants;
        var variants = new List<ImportedTreeVariant>(30);
        foreach (TreeSpecies species in OrderedSpecies)
            LoadSpecies(species, variants);
        if (variants.Count != VariantCount) throw new InvalidOperationException($"Imported tree catalog contains {variants.Count}, expected {VariantCount} variants.");
        _variants = variants.AsReadOnly();
        return _variants;
    }

    public static IReadOnlyList<ImportedTreeVariant> GetVariants(TreeSpecies species)
    {
        var all = Load();
        var result = new List<ImportedTreeVariant>();
        foreach (ImportedTreeVariant variant in all)
            if (variant.Species == species) result.Add(variant);
        return result.AsReadOnly();
    }

    private static void LoadSpecies(TreeSpecies species, List<ImportedTreeVariant> variants)
    {
        string folder = species.ToString().ToLowerInvariant();
        PackedScene? packed = ResourceLoader.Load<PackedScene>($"res://resources/trees/lolipop/{folder}/scene.gltf");
        if (packed == null) throw new InvalidOperationException($"Could not load imported {species} tree scene.");
        Node root = packed.Instantiate();
        try
        {
            var anchors = new Dictionary<string, Node3D>(StringComparer.Ordinal);
            FindLodAnchors(root, anchors);
            var names = new List<string>();
            foreach ((string key, Node3D _) in anchors)
                if (key.EndsWith(":0", StringComparison.Ordinal)) names.Add(key[..^2]);
            names.Sort(StringComparer.Ordinal);
            int expected = species == TreeSpecies.Maple ? 12 : species == TreeSpecies.Fir ? 3 : 15;
            if (names.Count != expected) throw new InvalidOperationException($"{species} has {names.Count} authored variants, expected {expected}.");
            foreach (string name in names)
                variants.Add(BuildVariant(variants.Count, species, name, anchors));
        }
        finally { root.Free(); }
    }

    private static ImportedTreeVariant BuildVariant(int index, TreeSpecies species, string name, Dictionary<string, Node3D> anchors)
    {
        Node3D lod0 = RequireAnchor(anchors, name, 0);
        Bounds bounds = GetBounds(lod0, TransformWithoutLayoutTranslation(lod0));
        float height = bounds.Max.Y - bounds.Min.Y;
        float width = Mathf.Max(bounds.Max.X - bounds.Min.X, bounds.Max.Z - bounds.Min.Z);
        if (height <= Mathf.Epsilon || width <= Mathf.Epsilon) throw new InvalidOperationException($"{name} LOD0 has invalid bounds.");
        var lods = new Mesh[4];
        var materials = new List<ShaderMaterial>();
        for (int lod = 0; lod < lods.Length; lod++)
            lods[lod] = BakeLod(RequireAnchor(anchors, name, lod), bounds.Min.Y, width, height, materials, species);

        // The asset has no semantic trunk tags.  A conservative geometric
        // estimate keeps collision/pacing decisions independent of foliage.
        float trunkHeight = height * (species == TreeSpecies.Maple ? .43f : .56f);
        float trunkRadius = Mathf.Max(.08f, width * (species == TreeSpecies.Maple ? .035f : .025f));
        return new ImportedTreeVariant(index, species, name, lods, materials.AsReadOnly(), width, height, trunkRadius, trunkHeight);
    }

    private static Node3D RequireAnchor(Dictionary<string, Node3D> anchors, string name, int lod) =>
        anchors.TryGetValue($"{name}:{lod}", out Node3D? node) ? node : throw new InvalidOperationException($"{name} is missing LOD{lod}.");

    private static void FindLodAnchors(Node node, Dictionary<string, Node3D> anchors)
    {
        if (node is Node3D spatial && TryParseLodAnchor(spatial.Name.ToString(), out string name, out int lod))
            anchors.Add($"{name}:{lod}", spatial);
        foreach (Node child in node.GetChildren()) FindLodAnchors(child, anchors);
    }

    private static bool TryParseLodAnchor(string nodeName, out string name, out int lod)
    {
        name = string.Empty; lod = -1;
        int marker = nodeName.LastIndexOf(LodPattern, StringComparison.Ordinal);
        if (marker <= 0 || marker + LodPattern.Length >= nodeName.Length || nodeName.Contains("_Mat_0", StringComparison.Ordinal)) return false;
        if (!int.TryParse(nodeName.AsSpan(marker + LodPattern.Length), out lod) || lod is < 0 or > 3) return false;
        name = nodeName[..marker];
        if (name.EndsWith("_Billboard", StringComparison.Ordinal)) name = name[..^10];
        return true;
    }

    private static Mesh BakeLod(Node3D anchor, float rootY, float width, float height, List<ShaderMaterial> materials, TreeSpecies species)
    {
        Transform3D anchorTransform = TransformWithoutLayoutTranslation(anchor);
        var output = new ArrayMesh();
        foreach (MeshInstance3D instance in DescendantMeshes(anchor))
        {
            Transform3D transform = anchorTransform * TransformFromAncestor(anchor, instance);
            Basis normalTransform = transform.Basis.Inverse().Transposed();
            for (int surface = 0; surface < instance.Mesh!.GetSurfaceCount(); surface++)
            {
                Godot.Collections.Array source = instance.Mesh.SurfaceGetArrays(surface);
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                Vector3[] vertices = source[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var baked = new Vector3[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 vertex = transform * vertices[i];
                    // The existing non-billboard placement transform has one
                    // uniform scale (its height parameter).  Normalize all
                    // axes by LOD0 height to retain the authored silhouette.
                    baked[i] = new Vector3(vertex.X / height, (vertex.Y - rootY) / height, vertex.Z / height);
                }
                arrays[(int)Mesh.ArrayType.Vertex] = baked;
                CopyChannel(source, arrays, Mesh.ArrayType.Color);
                CopyChannel(source, arrays, Mesh.ArrayType.TexUV);
                CopyChannel(source, arrays, Mesh.ArrayType.TexUV2);
                CopyChannel(source, arrays, Mesh.ArrayType.Index);
                if (source[(int)Mesh.ArrayType.Normal].VariantType != Variant.Type.Nil)
                {
                    Vector3[] normals = source[(int)Mesh.ArrayType.Normal].AsVector3Array();
                    var transformed = new Vector3[normals.Length];
                    for (int i = 0; i < normals.Length; i++) transformed[i] = (normalTransform * normals[i]).Normalized();
                    arrays[(int)Mesh.ArrayType.Normal] = transformed;
                }
                if (source[(int)Mesh.ArrayType.Tangent].VariantType != Variant.Type.Nil)
                {
                    float[] tangents = source[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
                    var transformed = new float[tangents.Length];
                    float handedness = transform.Basis.Determinant() < 0.0f ? -1.0f : 1.0f;
                    for (int i = 0; i < tangents.Length; i += 4)
                    {
                        Vector3 xyz = (transform.Basis * new Vector3(tangents[i], tangents[i + 1], tangents[i + 2])).Normalized();
                        if (source[(int)Mesh.ArrayType.Normal].VariantType != Variant.Type.Nil)
                        {
                            Vector3 normal = ((Vector3[])arrays[(int)Mesh.ArrayType.Normal])[i / 4];
                            xyz = (xyz - normal * xyz.Dot(normal)).Normalized();
                        }
                        transformed[i] = xyz.X; transformed[i + 1] = xyz.Y; transformed[i + 2] = xyz.Z; transformed[i + 3] = tangents[i + 3] * handedness;
                    }
                    arrays[(int)Mesh.ArrayType.Tangent] = transformed;
                }
                if (transform.Basis.Determinant() < 0.0f && source[(int)Mesh.ArrayType.Index].VariantType != Variant.Type.Nil)
                {
                    int[] indices = source[(int)Mesh.ArrayType.Index].AsInt32Array();
                    for (int i = 0; i + 2 < indices.Length; i += 3) (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
                    arrays[(int)Mesh.ArrayType.Index] = indices;
                }
                output.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                Material? material = CreateProductionMaterial(instance.GetActiveMaterial(surface), species);
                output.SurfaceSetMaterial(output.GetSurfaceCount() - 1, material);
                if (material is ShaderMaterial shaderMaterial) materials.Add(shaderMaterial);
            }
        }
        if (output.GetSurfaceCount() == 0) throw new InvalidOperationException($"{anchor.Name} has no mesh surfaces.");
        return output;
    }

    private static void CopyChannel(Godot.Collections.Array source, Godot.Collections.Array destination, Mesh.ArrayType channel)
    {
        if (source[(int)channel].VariantType != Variant.Type.Nil) destination[(int)channel] = source[(int)channel];
    }

    private static Material? CreateProductionMaterial(Material? source, TreeSpecies species)
    {
        if (source is not BaseMaterial3D baseMaterial) return source;
        var shader = ResourceLoader.Load<Shader>("res://materials/imported_tree.gdshader")
            ?? throw new InvalidOperationException("Missing imported tree shader.");
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("albedo_texture", baseMaterial.AlbedoTexture);
        material.SetShaderParameter("normal_texture", baseMaterial.NormalTexture);
        material.SetShaderParameter("metallic_texture", baseMaterial.MetallicTexture);
        material.SetShaderParameter("roughness_texture", baseMaterial.RoughnessTexture);
        material.SetShaderParameter("has_metallic_texture", baseMaterial.MetallicTexture != null);
        material.SetShaderParameter("has_roughness_texture", baseMaterial.RoughnessTexture != null);
        material.SetShaderParameter("has_normal_texture", baseMaterial.NormalTexture != null);
        material.SetShaderParameter("normal_scale", baseMaterial.NormalScale);
        material.SetShaderParameter("albedo_tint", baseMaterial.AlbedoColor);
        material.SetShaderParameter("metallic_factor", baseMaterial.Metallic);
        material.SetShaderParameter("roughness_factor", baseMaterial.Roughness);
        bool foliageCutout = baseMaterial.Transparency != BaseMaterial3D.TransparencyEnum.Disabled;
        material.SetShaderParameter("use_alpha_cutout", foliageCutout);
        material.SetShaderParameter("alpha_cutoff", baseMaterial.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor ? baseMaterial.AlphaScissorThreshold : .25f);
        // Maple's glTF bark metallic/roughness map explicitly uses TEXCOORD_1.
        // Godot's BaseMaterial importer drops that assignment, so restore it in
        // this production shader without touching the source import.
        bool mapleBark = species == TreeSpecies.Maple && baseMaterial.ResourceName.Contains("Bark", StringComparison.OrdinalIgnoreCase);
        material.SetShaderParameter("use_uv2_for_metallic_roughness", mapleBark);
        return material;
    }

    private static Transform3D TransformWithoutLayoutTranslation(Node3D node)
    {
        Transform3D transform = TransformFromRoot(node);
        transform.Origin = Vector3.Zero;
        return transform;
    }

    private static Transform3D TransformFromRoot(Node3D node)
    {
        Transform3D result = Transform3D.Identity;
        for (Node? current = node; current is Node3D spatial; current = current.GetParent()) result = spatial.Transform * result;
        return result;
    }

    private static Transform3D TransformFromAncestor(Node ancestor, Node node)
    {
        Transform3D result = Transform3D.Identity;
        for (Node? current = node; current != null && current != ancestor; current = current.GetParent())
            if (current is Node3D spatial) result = spatial.Transform * result;
        return result;
    }

    private static IEnumerable<MeshInstance3D> DescendantMeshes(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is MeshInstance3D mesh && mesh.Mesh != null) yield return mesh;
            foreach (MeshInstance3D nested in DescendantMeshes(child)) yield return nested;
        }
    }

    private static Bounds GetBounds(Node3D anchor, Transform3D anchorTransform)
    {
        var bounds = Bounds.Empty;
        foreach (MeshInstance3D instance in DescendantMeshes(anchor))
        {
            Transform3D transform = anchorTransform * TransformFromAncestor(anchor, instance);
            foreach (int surface in EnumerableRange(instance.Mesh!.GetSurfaceCount()))
                foreach (Vector3 vertex in instance.Mesh.SurfaceGetArrays(surface)[(int)Mesh.ArrayType.Vertex].AsVector3Array()) bounds.Include(transform * vertex);
        }
        if (!bounds.HasValue) throw new InvalidOperationException($"{anchor.Name} has no vertices.");
        return bounds;
    }

    private static IEnumerable<int> EnumerableRange(int count) { for (int i = 0; i < count; i++) yield return i; }
    private struct Bounds { public Vector3 Min, Max; public bool HasValue; public static Bounds Empty => new() { Min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity), Max = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity) }; public void Include(Vector3 p) { Min = Min.Min(p); Max = Max.Max(p); HasValue = true; } }
}
