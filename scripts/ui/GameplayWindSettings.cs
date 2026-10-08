using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Sparse player wind response overrides, separate from weather and world saves.</summary>
[GlobalClass]
public partial class GameplayWindSettings : Resource
{
    [Export] public Godot.Collections.Dictionary Overrides { get; set; } = new();
    [Export] public bool LegacyGrassMigrationCompleted { get; set; }
}
