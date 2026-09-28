using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Player-selected controls for the isolated Certified Terrain3D surface prototype.</summary>
[GlobalClass]
public partial class GameplaySurfaceTerrainSettings : Resource
{
    [Export] public float BrushRadiusMeters { get; set; } = 8.0f;
    [Export] public float BrushStepMeters { get; set; } = 2.0f;
    [Export] public int MeshLods { get; set; } = 4;
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
}
