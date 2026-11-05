using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>Exact authoring-time parity checks; never called by the runtime catalog loader.</summary>
public static class ImportedTreePreparedParity
{
    public static void CompareMesh(ArrayMesh source, ArrayMesh loaded, int lod, SortedSet<string> textureClasses)
    {
        string label = $"LOD{lod}";
        Require(source.GetClass() == loaded.GetClass(), label + " mesh class");
        EqualVariant(source.GetAabb(), loaded.GetAabb(), label + " AABB");
        EqualVariant(source.CustomAabb, loaded.CustomAabb, label + " custom AABB");
        Require(source.GetBlendShapeCount() == loaded.GetBlendShapeCount(), label + " blend shape count");
        Require(source.GetSurfaceCount() == loaded.GetSurfaceCount(), label + " surface count/order");
        for (int surface = 0; surface < source.GetSurfaceCount(); surface++)
        {
            string part = $"{label} surface{surface}";
            Require(source.SurfaceGetFormat(surface) == loaded.SurfaceGetFormat(surface), part + " channel/storage format");
            Require(source.SurfaceGetPrimitiveType(surface) == loaded.SurfaceGetPrimitiveType(surface), part + " primitive type");
            Require(source.SurfaceGetName(surface) == loaded.SurfaceGetName(surface), part + " name");
            var before = source.SurfaceGetArrays(surface);
            var after = loaded.SurfaceGetArrays(surface);
            Require(before.Count == after.Count, part + " channel count");
            for (int channel = 0; channel < before.Count; channel++)
                EqualVariant(before[channel], after[channel], $"{part} channel{channel}");
            EqualVariant(source.SurfaceGetBlendShapeArrays(surface), loaded.SurfaceGetBlendShapeArrays(surface), part + " blend shape arrays");
            CompareMaterial(source.SurfaceGetMaterial(surface), loaded.SurfaceGetMaterial(surface), part, textureClasses);
        }
    }

    public static void CompareMaterial(Material? source, Material? loaded, string label, SortedSet<string> textureClasses)
    {
        Require(source is ShaderMaterial && loaded is ShaderMaterial, label + " shader material presence");
        var before = (ShaderMaterial)source!;
        var after = (ShaderMaterial)loaded!;
        Require(before.GetInstanceId() != after.GetInstanceId(), label + " reload reused original material");
        Require(before.GetClass() == after.GetClass(), label + " material class");
        Require(before.RenderPriority == after.RenderPriority && before.NextPass == null && after.NextPass == null, label + " render state");
        Require(before.Shader != null && after.Shader != null && before.Shader.ResourcePath == after.Shader.ResourcePath, label + " external shader path");
        var sourceUniforms = before.Shader!.GetShaderUniformList();
        var loadedUniforms = after.Shader!.GetShaderUniformList();
        EqualVariant(sourceUniforms, loadedUniforms, label + " shader uniform contract");
        foreach (Variant uniform in sourceUniforms)
        {
            string name = uniform.AsGodotDictionary()["name"].AsString();
            Variant a = before.GetShaderParameter(name), b = after.GetShaderParameter(name);
            Require(a.VariantType == b.VariantType, $"{label} uniform {name} type");
            if (a.VariantType != Variant.Type.Object)
            {
                EqualVariant(a, b, $"{label} uniform {name}");
                continue;
            }
            if (a.AsGodotObject() == null || b.AsGodotObject() == null)
            {
                Require(a.AsGodotObject() == null && b.AsGodotObject() == null, $"{label} uniform {name} null object");
                continue;
            }
            Require(a.AsGodotObject() is Texture2D && b.AsGodotObject() is Texture2D, $"{label} uniform {name} texture type");
            var originalTexture = (Texture2D)a.AsGodotObject();
            var loadedTexture = (Texture2D)b.AsGodotObject();
            Require(originalTexture.ResourcePath.StartsWith("res://", StringComparison.Ordinal)
                && originalTexture.ResourcePath == loadedTexture.ResourcePath, $"{label} uniform {name} external texture path");
            Require(originalTexture.GetClass() == loadedTexture.GetClass(), $"{label} uniform {name} native texture class");
            Require(originalTexture.GetWidth() == loadedTexture.GetWidth() && originalTexture.GetHeight() == loadedTexture.GetHeight(), $"{label} uniform {name} texture dimensions");
            textureClasses.Add(loadedTexture.GetClass());
        }
    }

    public static void CheckWind(ArrayMesh mesh, ref int foliageSurfaces, ref int pinnedVertices)
    {
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            var material = (ShaderMaterial)mesh.SurfaceGetMaterial(surface);
            if (material.GetShaderParameter("tree_foliage_mode").AsInt32() != 1) continue;
            foliageSurfaces++;
            Require(material.GetShaderParameter("tree_foliage_attachments").AsBool(), "foliage attachment flag");
            ulong customFormat = ((ulong)mesh.SurfaceGetFormat(surface) >> (int)Mesh.ArrayFormat.FormatCustom0Shift) & 7;
            Require(customFormat == (ulong)Mesh.ArrayCustomFormat.RgbaFloat, "lossless float RGBA wind channel");
            var arrays = mesh.SurfaceGetArrays(surface);
            Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            float[] data = arrays[(int)Mesh.ArrayType.Custom0].AsFloat32Array();
            Require(data.Length == vertices.Length * 4, "wind attachment channel size");
            var anchors = new HashSet<Vector3>();
            var pinned = new HashSet<Vector3>();
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 anchor = new(data[i * 4], data[i * 4 + 1], data[i * 4 + 2]);
                float inverse = data[i * 4 + 3];
                Require(anchor.IsFinite() && float.IsFinite(inverse) && inverse >= 0, "finite wind attachment metadata");
                if (inverse <= 0) continue;
                anchors.Add(anchor);
                if (vertices[i] == anchor) { pinned.Add(anchor); pinnedVertices++; }
            }
            Require(anchors.IsSubsetOf(pinned), "every wind attachment has an exactly pinned vertex");
        }
    }

    public static void CheckDependencies(string path, HashSet<string> visited, SortedSet<string> dependencies)
    {
        if (!visited.Add(path)) return;
        foreach (string dependency in ResourceLoader.GetDependencies(path))
        {
            string resolved = dependency.Split("::")[^1];
            Require(!resolved.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)
                && !resolved.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)
                && !resolved.EndsWith(".scn", StringComparison.OrdinalIgnoreCase)
                && !resolved.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase), "source scene dependency: " + dependency);
            dependencies.Add(resolved);
            if (resolved.StartsWith("res://", StringComparison.Ordinal)) CheckDependencies(resolved, visited, dependencies);
        }
    }

    public static void EqualVariant(Variant source, Variant loaded, string label) =>
        Require(source.VariantType == loaded.VariantType && GD.VarToBytes(source).AsSpan().SequenceEqual(GD.VarToBytes(loaded)), label + " exact serialized value");

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
