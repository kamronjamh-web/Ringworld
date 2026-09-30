using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Player-selected rendering policy for proxy terrain partially covered by finer LODs.</summary>
[GlobalClass]
public partial class GameplayProxyCoverageCullingSettings : Resource
{
    [Export] public bool PartialCoverageMeshCullingEnabled { get; set; } = true;
}
