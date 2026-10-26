using Godot;
namespace RingworldTechDemo.UI;
/// <summary>Persisted sparse overrides of <see cref="RingworldTechDemo.Weather.RingWetWorldSettings"/> (user://gameplay_wet_world_defaults.tres).</summary>
[GlobalClass]
public partial class GameplayWetWorldSettings : Resource
{
    [Export] public Godot.Collections.Dictionary Overrides { get; set; } = new();
}
