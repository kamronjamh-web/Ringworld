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
    // Streak keys: older files without them load these defaults.
    /// <summary>Storm level (precipitation intensity, severe systems at full weight) where the grey haze veil starts to fade in; light and moderate rain show streaks and splashes only.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float HazeOnset { get; set; } = .78f;
    /// <summary>Share of lattice drops drawn as streaks (1 = the preset budget at heavy rain, 2 doubles the drop columns).</summary>
    [Export(PropertyHint.Range, "0,2,0.01")] public float StreakDensity { get; set; } = 1f;
    /// <summary>Opacity of streaks and splashes before scene lighting (they are lit by sun, sky and storm shade, never emissive).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float StreakBrightness { get; set; } = .5f;
    [Export(PropertyHint.Range, "0.5,2,0.01")] public float StreakLength { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float SplashAmount { get; set; } = 1f;
    [Export] public Color Tint { get; set; } = new(.48f, .53f, .58f, 1f);
}
