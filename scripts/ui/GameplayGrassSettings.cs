using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Player-authored grass keys. Missing keys inherit the graphics profile.</summary>
[GlobalClass]
public partial class GameplayGrassSettings : Resource
{
    [Export] public Godot.Collections.Dictionary Overrides { get; set; } = new();
}
