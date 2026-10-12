using Godot;
namespace RingworldTechDemo.Weather;

/// <summary>Appearance controls for liquid-rain haze; precipitation state remains weather-owned.</summary>
[GlobalClass]
public partial class RingRainHazeSettings : Resource
{
    public static RingRainHazeSettings Current { get; set; } = new();
    [Export] public bool Enabled { get; set; } = true;
    [Export(PropertyHint.Range, "0,0.03,0.0001")] public float Density { get; set; } = .004f;
    [Export(PropertyHint.Range, "0,100,1")] public float NearClearDistanceMeters { get; set; } = 8f;
    [Export(PropertyHint.Range, "0,0.9,0.01")] public float MaximumOpacity { get; set; } = .6f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float VariationStrength { get; set; } = .25f;
    [Export] public Color Tint { get; set; } = new(.48f, .53f, .58f, 1f);
}
