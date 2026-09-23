using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>Creates the two mesh variants which need different per-surface shader state.
/// The catalog meshes remain shared by normal tree rendering; shadow meshes have their
/// own lightweight material copies so a shadow policy never changes visible LOD2 trees.</summary>
public static class ImportedTreeRenderMaterials
{
    private static readonly Dictionary<Mesh, Mesh> SimplifiedMeshes = new();
    private static readonly Dictionary<Mesh, Mesh> ShadowMeshes = new();

    public static Mesh GetSimplifiedMesh(Mesh source) => GetOrCreate(SimplifiedMeshes, source);
    public static Mesh GetShadowMesh(Mesh source) => GetOrCreate(ShadowMeshes, source);

    public static IEnumerable<ShaderMaterial> ShaderMaterials(Mesh mesh)
    {
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            if (mesh.SurfaceGetMaterial(surface) is ShaderMaterial material) yield return material;
    }

    private static Mesh GetOrCreate(Dictionary<Mesh, Mesh> cache, Mesh source)
    {
        if (cache.TryGetValue(source, out Mesh? cached) && GodotObject.IsInstanceValid(cached)) return cached;
        Mesh copy = (Mesh)source.Duplicate(false);
        for (int surface = 0; surface < copy.GetSurfaceCount(); surface++)
        {
            if (copy.SurfaceGetMaterial(surface) is not ShaderMaterial original) continue;
            var material = (ShaderMaterial)original.Duplicate(false);
            material.SetShaderParameter("simplified_tree_shading", true);
            copy.SurfaceSetMaterial(surface, material);
        }
        cache[source] = copy;
        return copy;
    }
}
