using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>Baked card atlas (TR-7a): per variant one 256 x 256 albedo layer (RGBA8 sRGB, alpha = covered fraction, base level only; the
/// catalog builds the alpha mips) and one tangent-space normal layer with its selected (not averaged) mip chain, both made from the
/// authored LOD2 in the pack's LOD3 card layout. Kept apart from lolipop_catalog.res, which stays byte-identical.</summary>
[GlobalClass]
public partial class PreparedTreeImpostorAtlas : Resource
{
    public const int CurrentSchemaVersion = 1;
    /// <summary>Bump when the bake method changes; it is part of every source hash.</summary>
    public const int CurrentToolVersion = 1;
    public const int LayerSize = 256;
    public const int MipCount = 9; // 256 .. 1

    [Export] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [Export] public int ToolVersion { get; set; } = CurrentToolVersion;
    [Export] public string[] VariantNames { get; set; } = System.Array.Empty<string>();
    /// <summary>Hash of everything one variant's bake read (see ImportedTreeImpostorAtlasBake.HashSources).</summary>
    [Export] public string[] SourceHashes { get; set; } = System.Array.Empty<string>();
    /// <summary>Layer-major, 256 x 256 x 4 bytes per layer.</summary>
    [Export] public byte[] AlbedoBase { get; set; } = System.Array.Empty<byte>();
    /// <summary>Layer-major; per layer the levels 0 to 8 back to back, RGBA8 (rgb = tangent-space normal * 0.5 + 0.5, a = 255).</summary>
    [Export] public byte[] NormalMips { get; set; } = System.Array.Empty<byte>();

    public static int AlbedoLayerBytes => LayerSize * LayerSize * 4;
    public static int LevelSize(int level) => System.Math.Max(1, LayerSize >> level);
    public static int NormalLevelOffsetBytes(int level) { int o = 0; for (int l = 0; l < level; l++) o += LevelSize(l) * LevelSize(l) * 4; return o; }
    public static int NormalLayerBytes => NormalLevelOffsetBytes(MipCount);
}
