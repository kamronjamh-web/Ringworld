using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Persisted underground-mode enclosure choices.</summary>
[GlobalClass]
public partial class GameplayUndergroundModeSettings : Resource
{
    [Export] public bool Enabled { get; set; } = true;
    [Export] public float EnterDepthMeters { get; set; } = 24f;
    [Export] public float ExitDepthMeters { get; set; } = 12f;
    [Export] public float EnterOpennessMax { get; set; } = .06f;
    [Export] public float ExitOpennessMin { get; set; } = .18f;
    [Export] public float EnterHoldSeconds { get; set; } = 1f;
    [Export] public float ExitHoldSeconds { get; set; } = .15f;
    [Export] public float BlendSeconds { get; set; } = 1f;
}
