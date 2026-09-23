using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Deliberately sparse, player-authored cloud overrides.  An absent key
/// means that the scene/graphics preset remains the authority for that value.</summary>
[GlobalClass]
public partial class GameplayCloudSettings : Resource
{
    [Export] public Godot.Collections.Dictionary Overrides { get; set; } = new();
}
