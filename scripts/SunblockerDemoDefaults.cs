using Godot;

namespace RingworldTechDemo;

/// <summary>Only the standalone sunblocker scene reads this resource.</summary>
public partial class SunblockerDemoDefaults : Resource
{
    [Export] public float Elevation { get; set; } = 38;
    [Export] public float Azimuth { get; set; } = 35;
    [Export] public float Distance { get; set; } = 150;
    [Export] public int Atlas { get; set; } = 2048;
    [Export] public int Trees { get; set; } = 6;
    [Export] public bool Handoff { get; set; }
    [Export] public bool Debug { get; set; }
    [Export] public bool Single { get; set; }
    [Export] public int Method { get; set; }
    [Export] public int TreeVariantIndex { get; set; } = SunblockerDemoController.DefaultTreeVariantIndex;
}
