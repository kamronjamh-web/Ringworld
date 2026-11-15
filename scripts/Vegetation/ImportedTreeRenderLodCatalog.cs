using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>Loader for lolipop_render_lods.res. GetLod2b returns the decimated mesh of a variant (the catalog's shared materials assigned
/// per surface), or the authored LOD2 when the resource is missing, unsupported, or made from a different LOD2 (stale hash); one warning is
/// logged per cause. Nothing at runtime calls this yet (TR-5).</summary>
public static class ImportedTreeRenderLodCatalog
{
    public const string ResourcePath = "res://resources/trees/prepared/lolipop_render_lods.res";
    public const int LodIndex = 2;

    private static string _path = ResourcePath;
    private static PreparedTreeRenderLods? _resource;
    private static bool _loaded;
    private static readonly Dictionary<int, Mesh> Cache = new();

    public static string FallbackReason { get; private set; } = "";
    public static int StaleCount { get; private set; }
    public static int WarningCount { get; private set; }

    /// <summary>Tool/test selection of another resource; clears every cache.</summary>
    public static void ConfigureForDiagnostics(string? path, PreparedTreeRenderLods? resource = null)
    {
        _path = path ?? ResourcePath; _resource = resource; _loaded = resource != null;
        Cache.Clear(); FallbackReason = ""; StaleCount = 0; WarningCount = 0;
    }

    public static Mesh GetLod2b(ImportedTreeVariant variant)
    {
        if (Cache.TryGetValue(variant.Index, out Mesh? cached)) return cached;
        Mesh authored = variant.Lods[LodIndex];
        Mesh result = authored;
        PreparedTreeRenderLods? resource = Resource();
        if (resource != null && variant.Index < resource.VariantNames.Length && resource.VariantNames[variant.Index] == variant.Name)
        {
            if (resource.SourceHashes[variant.Index] != HashLod((ArrayMesh)authored))
            {
                StaleCount++;
                Warn($"render LOD source hash is stale for {variant.Name}; drawing the authored LOD2");
            }
            else if (resource.IsSource[variant.Index] == 0 && resource.Meshes[variant.Index] != null)
            {
                var mesh = (ArrayMesh)resource.Meshes[variant.Index].Duplicate(false);
                for (int s = 0; s < mesh.GetSurfaceCount() && s < authored.GetSurfaceCount(); s++)
                    mesh.SurfaceSetMaterial(s, authored.SurfaceGetMaterial(s));
                result = mesh;
            }
        }
        else if (resource != null) Warn($"render LOD resource does not list {variant.Name}; drawing the authored LOD2");
        Cache[variant.Index] = result;
        return result;
    }

    /// <summary>Hash of the arrays the decimator reads: every channel of every surface, in surface order.</summary>
    public static string HashLod(ArrayMesh mesh)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surface);
            for (int channel = 0; channel < arrays.Count; channel++)
            {
                if (arrays[channel].VariantType == Variant.Type.Nil) continue;
                sha.AppendData(BitConverter.GetBytes(channel));
                sha.AppendData(GD.VarToBytes(arrays[channel]));
            }
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static PreparedTreeRenderLods? Resource()
    {
        if (_loaded) return _resource;
        _loaded = true;
        if (!ResourceLoader.Exists(_path)) { Warn("render LOD resource missing; drawing the authored LOD2"); return null; }
        var resource = ResourceLoader.Load<PreparedTreeRenderLods>(_path);
        if (resource == null || resource.SchemaVersion != PreparedTreeRenderLods.CurrentSchemaVersion
            || resource.Meshes.Count != resource.VariantNames.Length || resource.SourceHashes.Length != resource.VariantNames.Length
            || resource.IsSource.Length != resource.VariantNames.Length)
        {
            Warn("render LOD resource unreadable or unsupported; drawing the authored LOD2");
            return null;
        }
        return _resource = resource;
    }

    private static void Warn(string message)
    {
        FallbackReason = message;
        if (WarningCount++ == 0) GD.PushWarning("Tree render LODs: " + message);
    }
}
