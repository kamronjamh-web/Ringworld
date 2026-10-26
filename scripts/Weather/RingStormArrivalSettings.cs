using Godot;
namespace RingworldTechDemo.Weather;

/// <summary>Runtime tunables of the storm arrival sequence (docs/storm-revamp-s3-storm-arrival-design.md section 10). Older preference files without a key load these defaults.</summary>
[GlobalClass]
public partial class RingStormArrivalSettings : Resource
{
    public static RingStormArrivalSettings Current { get; set; } = new();
    /// <summary>Scales the arrival wind modifiers and the gust-front debris; 0 makes them the identity.</summary>
    [Export(PropertyHint.Range, "0,2,0.01")] public float GustFrontStrength { get; set; } = 1f;
    /// <summary>Pacing of the rim bands: a fast rim sweep takes about this many seconds to cross the gust front band.</summary>
    [Export(PropertyHint.Range, "20,180,1")] public float GustFrontLeadSeconds { get; set; } = 60f;
    /// <summary>Hail swath count multiplier; 0 disables hail.</summary>
    [Export(PropertyHint.Range, "0,2,0.01")] public float Hail { get; set; } = 1f;
    /// <summary>Strength of the post-rain mist in the haze; 0 leaves the haze unchanged.</summary>
    [Export(PropertyHint.Range, "0,2,0.01")] public float PostRainMist { get; set; } = 1f;
    /// <summary>Rainbow strength; 0 means no rainbow draw at all.</summary>
    [Export(PropertyHint.Range, "0,2,0.01")] public float Rainbows { get; set; } = 1f;

    /// <summary>The maths tuning derived from the current settings.</summary>
    public StormArrivalTuning ToTuning(float precipitationStart, float baseCoverage)
        => new(GustFrontLeadSeconds, Hail, precipitationStart, baseCoverage);
}
