using Godot;

namespace RingworldTechDemo.UI;

[GlobalClass]
public partial class GameplayMinimapSettings : Resource
{
    [Export] public float RenderFramesPerSecond { get; set; } = 15f;
}
