using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Persisted artist controls for the shared analytic ring atmosphere.</summary>
public partial class AtmosphereVisualSettings : Resource
{
    // Defaults (2026-10-23, atmosphere-haze-revamp-plan.md decision 1): the user's in-game Physical look. Preset 1 (Cinematic) only picks the
    // Legacy clear-distance fraction in RingAtmosphereModel.ApplyProfile; it never overwrites the values below on load (only SetPreset does).
    [Export] public int Preset { get; set; } = 1;
    [Export] public float VisibilityFullKm { get; set; } = 400.0f;
    [Export] public float Density { get; set; } = 0.5f;
    [Export] public float AltitudeFalloff { get; set; } = 1.0f;
    [Export] public float Aerosol { get; set; } = 0.0f;
    [Export] public float ForwardScattering { get; set; } = 0.0f;
    [Export] public float SunWarmth { get; set; } = 0.0f;
    /// <summary>Temporary A/B toggle (removed in P8): 0 = Legacy, 1 = Physical (default). Files without the key load as Physical; files that saved Legacy before <see cref="SettingsVersion"/> 1 are migrated to Physical once by AtmosphereVisualDefaults.</summary>
    [Export] public int HazeModel { get; set; } = 1;
    /// <summary>Migration marker. The default is 0 so every file saved before the marker existed reads as 0; AtmosphereVisualDefaults.Load then moves HazeModel to Physical once and stamps <see cref="CurrentSettingsVersion"/>.</summary>
    [Export] public int SettingsVersion { get; set; } = 0;
    public const int CurrentSettingsVersion = 1;
    /// <summary>Physical-haze sun irradiance multiplier (DaySideAtmosphere.haze_brightness). Absent in older files, which load as the default.</summary>
    [Export] public float HazeBrightness { get; set; } = 0.52f;
    // Physical-haze controls (P4). Absent in older files, which load with these defaults.
    [Export] public float MistDensity { get; set; } = 1.0f;
    [Export] public float MistHeight { get; set; } = 400.0f;
    [Export] public float AerosolHeight { get; set; } = 1500.0f;
    [Export] public float RayleighStrength { get; set; } = 1.0f;
    [Export] public float GroundAlbedo { get; set; } = 0.11f;
    [Export] public float MultiScatter { get; set; } = 0.99f;
    [Export] public float SkyTintR { get; set; } = 1.0f;
    [Export] public float SkyTintG { get; set; } = 1.0f;
    [Export] public float SkyTintB { get; set; } = 1.01f;
    /// <summary>P9: how strongly cloud shadow at the camera dims the haze sun term (DaySideAtmosphere.haze_cloud_shadowing). Absent in older files, which load as 1.</summary>
    [Export] public float CloudShadowing { get; set; } = 1.0f;
    /// <summary>P9: strength (0-1) of the storm grey-Mie haze driven by the local storm factor (DaySideAtmosphere.haze_storm_strength). Absent in older files, which load as 1.</summary>
    [Export] public float StormHaze { get; set; } = 1.0f;
    /// <summary>P6: gain (0-1) on the Godot-fog fade of Sunshine clouds in caves and underwater (RingAtmosphereModel.EnclosedCloudFog). Absent in older files, which load as 1.</summary>
    [Export] public float EnclosedCloudFog { get; set; } = 1.0f;
    /// <summary>P7/P8: render the Physical sky from the cached sky-view LUT (RingAtmosphereLuts) instead of the analytic function. Absent in older files, which load as on.</summary>
    [Export] public bool SkyHazeLut { get; set; } = true;
    /// <summary>Per-instance Physical haze for GPU-culled grass cards and trees (RingVegetationHaze) instead of per-vertex haze. Absent in older files, which load as on.</summary>
    [Export] public bool VegetationInstanceHaze { get; set; } = true;
    /// <summary>Optical-depth quadrature points: 4, 8 or 16.</summary>
    [Export] public int HazeQuality { get; set; } = 4;
}
