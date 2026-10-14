using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Persisted LOD build-start scheduling choices.</summary>
[GlobalClass]
public partial class GameplayLodSchedulingSettings : Resource
{
    [Export] public int MaxBuildStartsPerFrame { get; set; } = 1;
    [Export] public float BuildStartBudgetMilliseconds { get; set; } = 2.0f;
    [Export] public int StarvationFrames { get; set; } = 30;
}
