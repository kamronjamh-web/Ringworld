using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>Loader and staleness check for lolipop_impostor_atlas.res (TR-7a). Nothing at runtime calls it yet (TR-7b wires the catalog):
/// a missing, unsupported or stale resource means "use the pack's pictures", with one warning.</summary>
public static class ImportedTreeImpostorAtlasBake
{
    public const string ResourcePath = "res://resources/trees/prepared/lolipop_impostor_atlas.res";
    public const string ManifestPath = "res://resources/trees/prepared/lolipop_impostor_atlas.manifest.json";

    public static string FallbackReason { get; private set; } = "";
    private static int _warnings;

    /// <summary>Everything one variant's bake reads: the tool version, the authored LOD2 and LOD3 arrays, and each LOD2 surface's albedo image bytes, tint and cutoff.</summary>
    public static string HashSources(ImportedTreeVariant variant)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(BitConverter.GetBytes(PreparedTreeImpostorAtlas.CurrentToolVersion));
        sha.AppendData(System.Text.Encoding.UTF8.GetBytes(ImportedTreeRenderLodCatalog.HashLod((ArrayMesh)variant.Lods[2])));
        sha.AppendData(System.Text.Encoding.UTF8.GetBytes(ImportedTreeRenderLodCatalog.HashLod((ArrayMesh)variant.Lods[3])));
        Mesh lod2 = variant.Lods[2];
        for (int s = 0; s < lod2.GetSurfaceCount(); s++)
        {
            if (lod2.SurfaceGetMaterial(s) is not ShaderMaterial m) continue;
            sha.AppendData(BitConverter.GetBytes(m.GetShaderParameter("alpha_cutoff").AsSingle()));
            sha.AppendData(BitConverter.GetBytes(m.GetShaderParameter("use_alpha_cutout").AsBool()));
            Color tint = m.GetShaderParameter("albedo_tint").AsColor();
            sha.AppendData(BitConverter.GetBytes(tint.R)); sha.AppendData(BitConverter.GetBytes(tint.G)); sha.AppendData(BitConverter.GetBytes(tint.B)); sha.AppendData(BitConverter.GetBytes(tint.A));
            if (m.GetShaderParameter("albedo_texture").AsGodotObject() is Texture2D texture && texture.GetImage() is Image image && !image.IsEmpty())
            {
                byte[] data = image.GetData();
                int length = image.HasMipmaps() ? (int)image.GetMipmapOffset(1) : data.Length; // level 0 only: TR-7c swaps the mips of the leaf atlases, never level 0
                sha.AppendData(data, 0, Math.Min(length, data.Length));
            }
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>True when the resource lists every variant by name with a matching source hash and has the expected layer sizes.</summary>
    public static bool IsFresh(PreparedTreeImpostorAtlas? resource, IReadOnlyList<ImportedTreeVariant> variants, out int staleCount, out string reason)
    {
        staleCount = 0; reason = "";
        if (resource == null) { reason = "impostor atlas resource missing"; return false; }
        if (resource.SchemaVersion != PreparedTreeImpostorAtlas.CurrentSchemaVersion || resource.ToolVersion != PreparedTreeImpostorAtlas.CurrentToolVersion
            || resource.VariantNames.Length != variants.Count || resource.SourceHashes.Length != variants.Count
            || resource.AlbedoBase.Length != variants.Count * PreparedTreeImpostorAtlas.AlbedoLayerBytes
            || resource.NormalMips.Length != variants.Count * PreparedTreeImpostorAtlas.NormalLayerBytes)
        { reason = "impostor atlas resource unreadable or unsupported"; return false; }
        for (int i = 0; i < variants.Count; i++)
            if (resource.VariantNames[i] != variants[i].Name || resource.SourceHashes[i] != HashSources(variants[i])) staleCount++;
        if (staleCount > 0) reason = $"impostor atlas source hash is stale for {staleCount} variants";
        return staleCount == 0;
    }

    /// <summary>The committed resource if it is present and fresh; otherwise null (the pack's pictures stay) with one warning.</summary>
    public static PreparedTreeImpostorAtlas? Load(IReadOnlyList<ImportedTreeVariant> variants)
    {
        PreparedTreeImpostorAtlas? resource = ResourceLoader.Exists(ResourcePath) ? ResourceLoader.Load<PreparedTreeImpostorAtlas>(ResourcePath) : null;
        if (IsFresh(resource, variants, out _, out string reason)) return resource;
        FallbackReason = reason;
        if (_warnings++ == 0) GD.PushWarning("Tree impostor atlas: " + reason + "; using the pack's card pictures");
        return null;
    }
}
