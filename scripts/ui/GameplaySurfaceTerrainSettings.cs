using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Player-selected controls for the isolated Certified Terrain3D surface prototype.</summary>
[GlobalClass]
public partial class GameplaySurfaceTerrainSettings : Resource
{
    [Export] public float BrushRadiusMeters { get; set; } = 8.0f;
    [Export] public float BrushStepMeters { get; set; } = 2.0f;
    [Export] public int MeshLods { get; set; } = 4;
    // Terrain3D native clipmap section size. Kept independent from the
    // 2m height-cache lattice so mesh tuning never changes collision data.
    [Export] public int MeshSize { get; set; } = 48;
    // Radius of the Terrain3D-owned disk. The proxy transition is centered
    // on this value and remains ready-coverage gated.
    [Export] public float HandoffDistanceMeters { get; set; } = 112.0f;
    [Export] public float MoveSpeedMetersPerSecond { get; set; } = 12.0f;
    [Export] public int GenerationWorkerCount { get; set; } = 2;
    [Export] public float GenerationPrefetchMeters { get; set; } = 64.0f;
    [Export] public int GenerationCacheMiB { get; set; } = 8;
    [Export] public float GenerationUploadBudgetMs { get; set; } = 1.0f;
    [Export] public float GenerationCollisionBudgetMs { get; set; } = .75f;
    [Export] public int GenerationColliderTileCells { get; set; } = 32;
    [Export] public float DryRoughness { get; set; } = .92f;
    [Export] public float WetRoughness { get; set; } = .35f;
    [Export] public float RainWetnessStrength { get; set; } = 1.0f;
    [Export] public bool SmoothTerrainShading { get; set; } = true;
    // Reflectance multipliers retuning the authored albedo after the P3b built-in lighting convention (shader globals).
    public const float DefaultTerrainAlbedoScale = 0.15f;
    public const float DefaultGrassAlbedoScale = 1.0f;
    [Export] public float TerrainAlbedoScale { get; set; } = DefaultTerrainAlbedoScale;
    [Export] public float GrassAlbedoScale { get; set; } = DefaultGrassAlbedoScale;
}
