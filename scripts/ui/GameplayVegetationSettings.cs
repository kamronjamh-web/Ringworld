using Godot;
namespace RingworldTechDemo.UI;
[GlobalClass]
public partial class GameplayVegetationSettings : Resource { [Export] public Godot.Collections.Dictionary Overrides { get; set; } = new(); }
