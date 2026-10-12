using Godot;

namespace RingworldTechDemo.UI;

[GlobalClass]
public partial class GameplayLoadingPresentationSettings : Resource
{
    [Export] public float AnimationSpeed { get; set; } = .18f;
    [Export] public bool MotionEnabled { get; set; } = true;
    [Export] public float HologramBrightness { get; set; } = 1f;
}
