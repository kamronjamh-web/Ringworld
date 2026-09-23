using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Player-authored water overrides. Missing entries retain authored tuning.</summary>
[GlobalClass]
public partial class GameplayWaterSettings : Resource
{
    [Export] public Godot.Collections.Dictionary Overrides { get; set; } = new();
}
