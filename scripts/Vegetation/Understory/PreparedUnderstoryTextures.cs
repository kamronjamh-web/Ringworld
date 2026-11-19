using Godot;

namespace RingworldTechDemo.Vegetation.Understory;

/// <summary>
/// T6-4b: the understory's texture layers, written by tools/PrepareUnderstoryTextures.cs to resources/trees/prepared/understory/understory_textures.res. One image per layer, layers in TreeSpecies order (maple, fir, pine):
/// foliage 1024 x 1024 (the distance-stable leaf mips' levels for albedo, the imports' RGTC levels for normals) and bark 512 x 512, all VRAM compressed with full mip chains. The loader builds the four Texture2DArrays from them
/// with no decompression. Roughness and metallic are per-layer constants (the means of the source metallic-roughness maps). The catalog fingerprint is the lolipop_catalog.res SourceFingerprint of the leaf mips it was built against.
/// </summary>
[GlobalClass]
public partial class PreparedUnderstoryTextures : Resource
{
    public const int CurrentSchemaVersion = 1;

    [Export] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [Export] public string ToolVersion { get; set; } = "";
    [Export] public string CatalogFingerprint { get; set; } = "";
    [Export] public Godot.Collections.Array<Image> FoliageAlbedo { get; set; } = new();
    [Export] public Godot.Collections.Array<Image> FoliageNormal { get; set; } = new();
    [Export] public Godot.Collections.Array<Image> BarkAlbedo { get; set; } = new();
    [Export] public Godot.Collections.Array<Image> BarkNormal { get; set; } = new();
    /// <summary>Per layer: mean green of the foliage metallic-roughness map (roughness), mean blue (metallic), then the same for bark: (foliage roughness, foliage metallic, bark roughness, bark metallic).</summary>
    [Export] public Vector4[] Constants { get; set; } = System.Array.Empty<Vector4>();
}
