using Godot;
namespace RingworldTechDemo.UI;
[GlobalClass]
public partial class GameplayRainHazeSettings : Resource
{
    [Export] public Godot.Collections.Dictionary Overrides { get; set; } = new();
}
