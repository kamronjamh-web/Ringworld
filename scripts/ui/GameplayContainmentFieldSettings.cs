using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Persisted containment field choices (docs/containment-field-design.md section 7). Older files without a field load its default.</summary>
[GlobalClass]
public partial class GameplayContainmentFieldSettings : Resource
{
    /// <summary>Near refraction amplitude, 0..2.</summary>
    [Export] public float ShimmerStrength { get; set; } = 1f;
    /// <summary>Distance (km) from the field inside which the close-up shimmer and sheen show.</summary>
    [Export] public float ShimmerRangeKm { get; set; } = 3f;
    /// <summary>Faint cool sheen multiplied into what is seen through the field, 0..2.</summary>
    [Export] public float SheenStrength { get; set; } = 1f;
    /// <summary>Faint hexagonal lattice seen close up through the field, 0..2.</summary>
    [Export] public float LatticeStrength { get; set; } = 1f;
    /// <summary>Ripples and field strikes seen on the field from below (refraction, reveal light, crawl and glow), 0..2.</summary>
    [Export] public float RippleStrength { get; set; } = 1f;
    /// <summary>Volume of the field hum (a user-supplied loop; no file, no hum) in dB, added to its -12 dB base. Older files without it load 0.</summary>
    [Export] public float FieldHumVolumeDb { get; set; } = 0f;
}
