using Godot;

namespace RingworldTechDemo.UI;

[GlobalClass]
public partial class GameplayRenderResolutionSettings : Resource
{
    [Export] public int Preset { get; set; } = (int)GameplayRenderResolutionPreset.Native;
    [Export] public bool AdaptiveResolutionEnabled { get; set; }
}

public enum GameplayRenderResolutionPreset
{
    Native = 0,
    Fsr2NativeAntiAliasing = 1,
    Fsr2Quality = 2,
    Fsr2Balanced = 3,
    Fsr2Performance = 4,
    Fsr2UltraPerformance = 5
}
