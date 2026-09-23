using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Player-selected anti-aliasing mode for the gameplay viewport.</summary>
[GlobalClass]
public partial class GameplayAntiAliasingSettings : Resource
{
    [Export] public AntiAliasingMode Mode { get; set; } = AntiAliasingMode.Fxaa;
}

public enum AntiAliasingMode
{
    Off,
    Fxaa,
    Smaa,
    Taa,
    Msaa2X,
    Msaa4X,
    Msaa8X
}
