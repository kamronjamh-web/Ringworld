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
}
