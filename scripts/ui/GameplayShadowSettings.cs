using Godot;
namespace RingworldTechDemo.UI;
[GlobalClass]
public partial class GameplayShadowSettings : Resource { [Export] public Godot.Collections.Dictionary Overrides { get; set; } = new(); }
