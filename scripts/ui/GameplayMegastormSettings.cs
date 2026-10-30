using Godot;
namespace RingworldTechDemo.UI;
[GlobalClass]
public partial class GameplayMegastormSettings : Resource
{
    // Sparse overrides; keys absent from an older file keep the RingMegastormSettings defaults.
    [Export] public Godot.Collections.Dictionary Overrides { get; set; } = new();
}
