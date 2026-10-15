using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Persisted emitter far stand-in and POI streaming choices.</summary>
[GlobalClass]
public partial class GameplayEmitterSiteSettings : Resource
{
    [Export] public bool StandInsEnabled { get; set; } = true;
    [Export] public float StandInMaxDistanceKm { get; set; } = 700f;
    [Export] public float StandInFadeKm { get; set; } = 100f;
    [Export] public float StandInMinPixels { get; set; } = 1.5f;
    [Export] public float RegularHandoffMeters { get; set; } = 11500f;
    [Export] public float MainHandoffMeters { get; set; } = 26000f;
    [Export] public bool PoiStreamerEnabled { get; set; } = true;
    [Export] public float RegularLoadRadiusMeters { get; set; } = 14000f;
    [Export] public float MainLoadRadiusMeters { get; set; } = 32000f;
    [Export] public float UnloadMarginMeters { get; set; } = 2000f;
    [Export] public float MinDwellSeconds { get; set; } = 1f;
    [Export] public float CollisionOnMeters { get; set; } = 1500f;
    [Export] public float CollisionOffMarginMeters { get; set; } = 500f;
    [Export] public bool BoreAperturesEnabled { get; set; } = true;
    [Export] public float BoreVisibleRangeMeters { get; set; } = 16000f;
    [Export] public float BorePlugLightEnergy { get; set; } = 2f;
    [Export] public int TeleportViewpoint { get; set; }
    [Export] public float FarViewpointDistanceKm { get; set; } = 20f;
    [Export] public float HighViewpointAltitudeKm { get; set; } = 150f;
}
