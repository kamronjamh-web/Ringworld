using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>Serialized normalized meshes; preparation versions are independent of placement/cache identity.</summary>
[GlobalClass]
public partial class PreparedImportedTreeCatalog : Resource
{
    public const int CurrentSchemaVersion = 1;
    public const int CurrentBakeVersion = 1;
    public const int LodsPerVariant = 4;

    [Export] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [Export] public int BakeVersion { get; set; } = CurrentBakeVersion;
    [Export] public int CatalogVersion { get; set; } = ImportedTreeCatalog.CatalogVersion;
    [Export] public string EngineIdentity { get; set; } = "";
    [Export] public string ImplementationIdentity { get; set; } = "";
    [Export] public string SourceFingerprint { get; set; } = "";
    [Export] public string[] FingerprintInputs { get; set; } = System.Array.Empty<string>();
    [Export] public string[] VariantNames { get; set; } = System.Array.Empty<string>();
    [Export] public int[] VariantIndices { get; set; } = System.Array.Empty<int>();
    [Export] public int[] SpeciesIds { get; set; } = System.Array.Empty<int>();
    // Width, height, trunk radius and trunk height, in authored meters.
    [Export] public Godot.Collections.Array<Vector4> Dimensions { get; set; } = new();
    // Mesh index = variant slot * LodsPerVariant + authored LOD index.
    [Export] public Godot.Collections.Array<ArrayMesh> Meshes { get; set; } = new();
}
