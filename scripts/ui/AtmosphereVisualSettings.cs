using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Persisted artist controls for the shared analytic ring atmosphere.</summary>
public partial class AtmosphereVisualSettings : Resource
{
    [Export] public int Preset { get; set; } = 0;
    [Export] public float VisibilityFullKm { get; set; } = 120.0f;
    [Export] public float Density { get; set; } = 1.0f;
    [Export] public float AltitudeFalloff { get; set; } = 0.5f;
    [Export] public float Aerosol { get; set; } = 0.35f;
    [Export] public float ForwardScattering { get; set; } = 0.22f;
    [Export] public float SunWarmth { get; set; } = 0.25f;
    /// <summary>Temporary A/B toggle (P2a, removed in P8): 0 = Legacy, 1 = Physical. Absent in older files, which therefore load as Legacy.</summary>
    [Export] public int HazeModel { get; set; } = 0;
    /// <summary>Physical-haze sun irradiance multiplier (DaySideAtmosphere.haze_brightness). Absent in older files, which load as 1.</summary>
    [Export] public float HazeBrightness { get; set; } = 1.0f;
    // Physical-haze controls (P4). Absent in older files, which load with these defaults (= the pre-P4 effective values).
    [Export] public float MistDensity { get; set; } = 1.0f;
    [Export] public float MistHeight { get; set; } = 400.0f;
    [Export] public float AerosolHeight { get; set; } = 1500.0f;
    [Export] public float RayleighStrength { get; set; } = 1.0f;
    [Export] public float GroundAlbedo { get; set; } = 0.1f;
    [Export] public float MultiScatter { get; set; } = 1.0f;
    [Export] public float SkyTintR { get; set; } = 1.0f;
    [Export] public float SkyTintG { get; set; } = 1.0f;
    [Export] public float SkyTintB { get; set; } = 1.0f;
    /// <summary>P9: how strongly cloud shadow at the camera dims the haze sun term (DaySideAtmosphere.haze_cloud_shadowing). Absent in older files, which load as 1.</summary>
    [Export] public float CloudShadowing { get; set; } = 1.0f;
    /// <summary>P9: strength (0-1) of the storm grey-Mie haze driven by the local storm factor (DaySideAtmosphere.haze_storm_strength). Absent in older files, which load as 1.</summary>
    [Export] public float StormHaze { get; set; } = 1.0f;
    /// <summary>P6: gain (0-1) on the Godot-fog fade of Sunshine clouds in caves and underwater (RingAtmosphereModel.EnclosedCloudFog). Absent in older files, which load as 1.</summary>
    [Export] public float EnclosedCloudFog { get; set; } = 1.0f;
    /// <summary>Optical-depth quadrature points: 4, 8 or 16.</summary>
    [Export] public int HazeQuality { get; set; } = 8;
}
