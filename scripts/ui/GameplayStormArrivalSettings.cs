using Godot;
namespace RingworldTechDemo.UI;
[GlobalClass]
public partial class GameplayStormArrivalSettings : Resource
{
    [Export] public Godot.Collections.Dictionary Overrides { get; set; } = new();
}
