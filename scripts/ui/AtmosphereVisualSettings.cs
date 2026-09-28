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
}
