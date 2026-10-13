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
    [Export] public float DeepHideDepthMeters { get; set; } = 128f;
    [Export] public bool AudioWeatherEnabled { get; set; } = true;
    [Export] public bool VegetationEnabled { get; set; } = true;
    [Export] public bool CloudsEnabled { get; set; } = true;
    [Export] public bool WaterEnabled { get; set; } = true;
    [Export] public bool FarTerrainEnabled { get; set; } = true;
    [Export] public bool ShadowsEnabled { get; set; } = true;
    [Export] public float EnclosureLightingWeight { get; set; } = 1f;
    [Export] public Color CaveAmbientColor { get; set; } = new(.16f, .20f, .30f, 1f);
    [Export] public float CaveAmbientEnergy { get; set; } = .035f;
    [Export] public Color CaveFogColor { get; set; } = new(.05f, .07f, .11f, 1f);
}
