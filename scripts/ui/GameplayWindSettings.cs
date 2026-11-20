using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Sparse player wind response overrides, separate from weather and world saves.</summary>
[GlobalClass]
public partial class GameplayWindSettings : Resource
{
    [Export] public Godot.Collections.Dictionary Overrides { get; set; } = new();
    [Export] public bool LegacyGrassMigrationCompleted { get; set; }
    /// <summary>Set once the steady-wind time constants moved from seconds to minutes; older saved values of those keys are dropped.</summary>
    [Export] public bool SteadyWindMigrationCompleted { get; set; }
}
