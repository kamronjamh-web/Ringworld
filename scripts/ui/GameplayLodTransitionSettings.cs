using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Player-selected policy for the intermediate terrain LOD transition bands.</summary>
[GlobalClass]
public partial class GameplayLodTransitionSettings : Resource
{
    [Export] public bool IntermediateLodTiersEnabled { get; set; } = true;
    // Older files without the field below load its default (off). Saves that still carry the retired Lod1ReachCapMeters are ignored.
    /// <summary>Far proxy terrain pixel-error multiplier cap (1 = off, 1..4).</summary>
    [Export] public float FarProxyErrorScale { get; set; } = DefaultFarProxyErrorScale;
    public const float DefaultFarProxyErrorScale = 2f; // user choice 2026-11-17: -0.2..-0.56 ms GPU, smoother far ridges from altitude
}
