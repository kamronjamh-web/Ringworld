using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>Decimated render meshes (LOD2b, TR-4). Kept apart from PreparedImportedTreeCatalog on purpose: the world fingerprint and the
/// vegetation tile cache see only the catalog, so this resource never invalidates either. Surfaces carry no materials; the loader assigns the
/// authored LOD2's shared materials.</summary>
[GlobalClass]
public partial class PreparedTreeRenderLods : Resource
{
    public const int CurrentSchemaVersion = 1;

    [Export] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [Export] public int ToolVersion { get; set; }
    /// <summary>Which of the three candidate levels this resource holds: "A", "B" or "C".</summary>
    [Export] public string Level { get; set; } = "B";
    [Export] public string[] VariantNames { get; set; } = System.Array.Empty<string>();
    /// <summary>SHA-256 of the authored LOD2 arrays each entry was made from.</summary>
    [Export] public string[] SourceHashes { get; set; } = System.Array.Empty<string>();
    /// <summary>1 where the authored LOD2 is used unchanged (the mesh slot is then null).</summary>
    [Export] public int[] IsSource { get; set; } = System.Array.Empty<int>();
    [Export] public Godot.Collections.Array<ArrayMesh> Meshes { get; set; } = new();
}
