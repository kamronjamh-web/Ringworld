using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>Distance-stable leaf mips (TR-7c): one DXT5 texture per leaf atlas whose level 0 is the import's own bytes and whose levels 1 and up are built by tools/TreeLeafMipBuilder.cs.
/// Kept apart from PreparedImportedTreeCatalog on purpose: the world fingerprint and the vegetation tile cache see only the catalog, so this resource never invalidates either.
/// It records the catalog's SourceFingerprint it was built against; the loader (ImportedTreeLeafMipCatalog) treats a different fingerprint, cutoff or size as stale.</summary>
[GlobalClass]
public partial class PreparedTreeLeafMips : Resource
{
    public const int CurrentSchemaVersion = 1;

    [Export] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [Export] public string ToolVersion { get; set; } = "";
    /// <summary>SourceFingerprint of the lolipop_catalog.res this was built against.</summary>
    [Export] public string CatalogFingerprint { get; set; } = "";
    [Export] public string[] AtlasNames { get; set; } = System.Array.Empty<string>();
    /// <summary>Resource path of the import each atlas replaces (the material's albedo_texture before the swap).</summary>
    [Export] public string[] SourcePaths { get; set; } = System.Array.Empty<string>();
    /// <summary>The materials' alpha_cutoff the mips were built for.</summary>
    [Export] public float[] Cutoffs { get; set; } = System.Array.Empty<float>();
    [Export] public int[] Sizes { get; set; } = System.Array.Empty<int>();
    [Export] public Godot.Collections.Array<ImageTexture> Textures { get; set; } = new();
}
