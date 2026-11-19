using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>Derived tree forms (T6-5: snags, fallen trees, stumps; indices 30-42), built from the prepared base catalog by tools/PrepareDerivedTreeAssets.cs. Kept apart from
/// PreparedImportedTreeCatalog on purpose (like PreparedTreeRenderLods): the base resource never changes. Surfaces carry no materials; DerivedTreeCatalog assigns the source variant's bark.
/// Mesh slot = form slot (index - 30) * LodsPerForm + lod. Snags and logs fill LOD0, LOD1, LOD2 and LODF (slot 3); stumps fill slot 0 only; unused slots are null.</summary>
[GlobalClass]
public partial class PreparedDerivedTreeCatalog : Resource
{
    public const int CurrentSchemaVersion = 1;
    public const int LodsPerForm = 4;

    [Export] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [Export] public int ToolVersion { get; set; }
    [Export] public int CatalogVersion { get; set; }
    [Export] public int[] FormIndices { get; set; } = System.Array.Empty<int>();
    /// <summary>TreeFormKind of each form.</summary>
    [Export] public int[] FormKinds { get; set; } = System.Array.Empty<int>();
    [Export] public int[] SourceVariants { get; set; } = System.Array.Empty<int>();
    /// <summary>Per mesh slot: ImportedTreeRenderLodCatalog.HashLod of the base-catalog LOD the slot was made from (the LODF slot holds the hash of the LOD0 its pole is fitted to); empty for an unused slot.</summary>
    [Export] public string[] SourceHashes { get; set; } = System.Array.Empty<string>();
    /// <summary>Per form: snag height, log length or stump height, in authored metres.</summary>
    [Export] public float[] LengthMeters { get; set; } = System.Array.Empty<float>();
    /// <summary>Per form: the source variant's trunk radius in authored metres.</summary>
    [Export] public float[] TrunkRadiusMeters { get; set; } = System.Array.Empty<float>();
    [Export] public Godot.Collections.Array<ArrayMesh> Meshes { get; set; } = new();
}
