using Godot;
using RingworldTechDemo.Terrain.Editable;

namespace RingworldTechDemo.UI;

/// <summary>Persisted smooth-terrain streaming throughput choices.</summary>
[GlobalClass]
public partial class GameplayTerrainStreamingSettings : Resource
{
    [Export] public int MaxWorkers { get; set; } = SmoothTerrainGrid.DefaultMaxWorkers;
    [Export] public int MaxInstallsPerFrame { get; set; } = 8;
    [Export] public float InstallBudgetMilliseconds { get; set; } = 1.5f;
}
