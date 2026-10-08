using Godot;
namespace RingworldTechDemo.Weather;
[GlobalClass]
public partial class RingWindSettings : Resource
{
    public static RingWindSettings Current { get; set; } = new();
    [Export] public bool Enabled { get; set; } = true;
    [Export(PropertyHint.Range, "0,4")] public float BaseWindMultiplier { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,80")] public float StormWindSpeed { get; set; } = 28f;
    [Export(PropertyHint.Range, "0,90")] public float StormDirectionDegrees { get; set; } = 18f;
    [Export(PropertyHint.Range, "0.1,120")] public float StormRiseSeconds { get; set; } = 5f;
    [Export(PropertyHint.Range, "0.1,300")] public float StormDecaySeconds { get; set; } = 18f;
    [Export(PropertyHint.Range, "0,2")] public float GustStrength { get; set; } = .9f;
    [Export(PropertyHint.Range, "48,8000")] public float GustScaleMeters { get; set; } = 600f;
    [Export(PropertyHint.Range, "0,4")] public float GustTravelMultiplier { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,1")] public float GustBaseBreeze { get; set; } = .55f;
    [Export(PropertyHint.Range, "0,4")] public float TreeBendStrength { get; set; } = 1.7f;
    [Export(PropertyHint.Range, "0,1")] public float TreeTrunkBendStrength { get; set; } = .15f;
    [Export(PropertyHint.Range, "0,3")] public float TreeCanopyFlexStrength { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,0.6")] public float TreeMaximumBendRatio { get; set; } = .28f;
    [Export(PropertyHint.Range, "0.05,20")] public float TreeTrunkResponseSeconds { get; set; } = 2f;
    [Export(PropertyHint.Range, "0.05,10")] public float TreeCanopyResponseSeconds { get; set; } = .6f;
    [Export(PropertyHint.Range, "0.1,120")] public float WaterResponseSeconds { get; set; } = 8f;
    [Export(PropertyHint.Range, "0,3")] public float WaterGustResponseStrength { get; set; } = 1.3f;
    [Export(PropertyHint.Range, "0,4")] public float AnalyticWaveResponseStrength { get; set; } = 1.4f;
    [Export] public bool RadarWindArrowEnabled { get; set; } = true;
    [Export(PropertyHint.Range, "0,5")] public float RadarWindCalmThreshold { get; set; } = .1f;
}
