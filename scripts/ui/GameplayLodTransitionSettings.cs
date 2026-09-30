using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Player-selected policy for the intermediate terrain LOD transition bands.</summary>
[GlobalClass]
public partial class GameplayLodTransitionSettings : Resource
{
    [Export] public bool IntermediateLodTiersEnabled { get; set; } = true;
}
