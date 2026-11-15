using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>Loader for lolipop_leaf_mips.res (TR-7c). Apply swaps the albedo texture of every cutout foliage material for the stable-mip texture of its atlas and switches the shader's
/// isotropic leaf-alpha read on. Missing, unsupported or stale (catalog fingerprint, cutoff or size differs) means the import's own mips stay, with one warning per cause. Startup reads
/// metadata only; no pixels are touched.</summary>
public static class ImportedTreeLeafMipCatalog
{
    public const string ResourcePath = "res://resources/trees/prepared/lolipop_leaf_mips.res";

    private sealed record Swap(ShaderMaterial Material, Texture2D Import, Texture2D Mips);

    private static string _path = ResourcePath;
    private static bool _enabled = true;
    private static bool _active = true;
    private static readonly List<Swap> Applied = new();
    private static readonly HashSet<string> Warned = new();

    public static string FallbackReason { get; private set; } = "";
    public static int WarningCount { get; private set; }
    /// <summary>Materials currently carrying a stable-mip texture.</summary>
    public static int AppliedCount => Applied.Count;
    public static bool Enabled => _enabled;
    public static bool Active => _active && Applied.Count > 0;

    /// <summary>Tool/test selection of another resource path, or off (enabled false: the import's mips, no warning). Call before the catalog loads.</summary>
    public static void ConfigureForDiagnostics(string? path, bool enabled = true)
    {
        _path = path ?? ResourcePath; _enabled = enabled; _active = true;
        Applied.Clear(); Warned.Clear(); FallbackReason = ""; WarningCount = 0;
    }

    /// <summary>The path to request when the resource should be loaded, or null (disabled, or missing with one warning).</summary>
    public static string? RequestPath()
    {
        if (!_enabled) return null;
        if (!ResourceLoader.Exists(_path)) { Warn("missing", "leaf mip resource missing; drawing the import's mips"); return null; }
        return _path;
    }

    /// <summary>Synchronous load of the resource, or null.</summary>
    public static PreparedTreeLeafMips? Load()
    {
        string? path = RequestPath();
        return path == null ? null : Validate(ResourceLoader.Load<PreparedTreeLeafMips>(path));
    }

    /// <summary>The resource when it has the supported shape, otherwise null with one warning.</summary>
    public static PreparedTreeLeafMips? Validate(PreparedTreeLeafMips? resource)
    {
        if (resource == null || resource.SchemaVersion != PreparedTreeLeafMips.CurrentSchemaVersion || resource.AtlasNames.Length == 0
            || resource.SourcePaths.Length != resource.AtlasNames.Length || resource.Cutoffs.Length != resource.AtlasNames.Length
            || resource.Sizes.Length != resource.AtlasNames.Length || resource.Textures.Count != resource.AtlasNames.Length)
        {
            Warn("unreadable", "leaf mip resource unreadable or unsupported; drawing the import's mips");
            return null;
        }
        for (int i = 0; i < resource.Textures.Count; i++)
            if (resource.Textures[i] == null) { Warn("unreadable", "leaf mip resource has an empty texture; drawing the import's mips"); return null; }
        return resource;
    }

    /// <summary>Swaps the leaf atlases of every cutout foliage material of every LOD. Returns the number of materials swapped.</summary>
    public static int Apply(IReadOnlyList<ImportedTreeVariant> variants, PreparedTreeLeafMips? resource, string catalogFingerprint)
    {
        if (!_enabled || resource == null) return 0;
        if (resource.CatalogFingerprint != catalogFingerprint)
        {
            Warn("fingerprint", "leaf mip resource was built for another catalog (fingerprint differs); drawing the import's mips");
            return 0;
        }
        var byPath = new Dictionary<string, int>();
        for (int i = 0; i < resource.SourcePaths.Length; i++) byPath[resource.SourcePaths[i]] = i;
        var seen = new HashSet<ShaderMaterial>();
        int count = 0;
        foreach (ImportedTreeVariant variant in variants)
            foreach (Mesh mesh in variant.Lods)
                for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                {
                    if (mesh.SurfaceGetMaterial(surface) is not ShaderMaterial material || !seen.Add(material)) continue;
                    if (material.GetShaderParameter("tree_foliage_mode").AsInt32() != 1 || !material.GetShaderParameter("use_alpha_cutout").AsBool()) continue;
                    if (material.GetShaderParameter("albedo_texture").AsGodotObject() is not Texture2D import) continue;
                    if (!byPath.TryGetValue(import.ResourcePath, out int index))
                    {
                        Warn("atlas:" + import.ResourcePath, $"leaf mip resource has no atlas for {import.ResourcePath}; drawing the import's mips there");
                        continue;
                    }
                    if (material.GetShaderParameter("alpha_cutoff").AsSingle() != resource.Cutoffs[index])
                    {
                        Warn("cutoff:" + import.ResourcePath, $"leaf mip resource is stale for {resource.AtlasNames[index]} (cutoff differs); drawing the import's mips there");
                        continue;
                    }
                    if (import.GetWidth() != resource.Sizes[index] || import.GetHeight() != resource.Sizes[index])
                    {
                        Warn("size:" + import.ResourcePath, $"leaf mip resource is stale for {resource.AtlasNames[index]} (size differs); drawing the import's mips there");
                        continue;
                    }
                    Texture2D mips = resource.Textures[index];
                    Applied.Add(new Swap(material, import, mips));
                    SetMaterial(material, mips, true);
                    count++;
                }
        return count;
    }

    /// <summary>Control of the tests: false puts the import's textures and the anisotropic alpha read back on the swapped materials, true the stable mips. Returns whether they are in use afterwards.</summary>
    public static bool SetActive(bool active)
    {
        _active = active;
        foreach (Swap swap in Applied) SetMaterial(swap.Material, active ? swap.Mips : swap.Import, active);
        return Active;
    }

    private static void SetMaterial(ShaderMaterial material, Texture2D texture, bool isotropic)
    {
        material.SetShaderParameter("albedo_texture", texture);
        material.SetShaderParameter("leaf_alpha_texture", texture);
        material.SetShaderParameter("leaf_alpha_isotropic", isotropic);
        material.SetShaderParameter("leaf_alpha_max_lod", isotropic ? MaxLod(texture) : 16f);
    }

    /// <summary>The 4 x 4 level: log2(width) - 2.</summary>
    public static float MaxLod(Texture2D texture) => System.Math.Max(0, System.Numerics.BitOperations.Log2((uint)texture.GetWidth()) - 2);

    private static void Warn(string cause, string message)
    {
        FallbackReason = message;
        if (Warned.Add(cause)) { WarningCount++; GD.PushWarning("Tree leaf mips: " + message); }
    }
}
