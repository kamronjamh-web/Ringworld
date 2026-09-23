using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Player-selected first-person motion smoothing preference.</summary>
[GlobalClass]
public partial class GameplayMotionSettings : Resource
{
    [Export] public bool PhysicsInterpolationEnabled { get; set; } = true;
    [Export] public float MaxWalkableSlopeDegrees { get; set; } = 55.0f;
}
