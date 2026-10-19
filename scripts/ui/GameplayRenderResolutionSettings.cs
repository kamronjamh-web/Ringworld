using Godot;

namespace RingworldTechDemo.UI;

[GlobalClass]
public partial class GameplayRenderResolutionSettings : Resource
{
    [Export] public int Preset { get; set; } = (int)GameplayRenderResolutionPreset.Native;
    [Export] public bool AdaptiveResolutionEnabled { get; set; }
    /// <summary>Godot FSR sharpness: 0 is sharpest, each whole step halves it (range 0-2).</summary>
    [Export(PropertyHint.Range, "0,2,0.05")] public float FsrSharpness { get; set; } = GameplayRenderResolutionDefaults.DefaultFsrSharpness;
    /// <summary>Additive offset on top of Godot's automatic log2(scale) mipmap bias; only applied while FSR2 is active.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float MipBiasOffset { get; set; } = 0f;
    /// <summary>Forces Sunshine CloudInterleave to 0 while FSR2 is active; the user's value is restored otherwise.</summary>
    [Export] public bool DisableCloudInterleaveUnderFsr { get; set; } = true;
    /// <summary>Sizes the far-ring SubViewports by the FSR render scale as well while FSR2 is active.</summary>
    [Export] public bool FarViewportFollowsFsrScale { get; set; } = true;
    /// <summary>Resamples the centred (unjittered) Sunshine cloud and far-backdrop layers at the FSR2 / TAA jitter offset so they stop shaking.</summary>
    [Export] public bool CompositorJitterCompensation { get; set; } = true;
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
