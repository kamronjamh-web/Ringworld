using Godot;
namespace RingworldTechDemo.Diagnostics;
public partial class RingPerformanceTelemetrySettings : Resource
{
    [Export] public bool Enabled { get; set; } = true;
    [Export] public bool DetailedGpuEnabled { get; set; }
    [Export] public int HistorySeconds { get; set; } = 30;
    [Export] public int MemoryBudgetMiB { get; set; } = 4;
    [Export] public float RefreshSeconds { get; set; } = .2f;
}
