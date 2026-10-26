using Godot;
namespace RingworldTechDemo.Weather;

/// <summary>Runtime tunables of the wet world (docs/storm-revamp-s2-wet-world-design.md section 4). Older preference files without a key load these defaults.</summary>
[GlobalClass]
public partial class RingWetWorldSettings : Resource
{
    public static RingWetWorldSettings Current { get; set; } = new();
    /// <summary>Darkening and gloss amount of wet surfaces; 0 = nothing gets wet visibly.</summary>
    [Export(PropertyHint.Range, "0,1.5,0.01")] public float Wetness { get; set; } = 1f;
    /// <summary>Wet roughness of glossy wet ground (lower = shinier).</summary>
    [Export(PropertyHint.Range, "0.05,0.6,0.01")] public float WetGloss { get; set; } = 0.18f;
    /// <summary>Puddle amount; 0 = no puddles.</summary>
    [Export(PropertyHint.Range, "0,2,0.01")] public float Puddles { get; set; } = 1f;
    /// <summary>Scales how fast wet ground dries (the rain history itself is the storm arrival's).</summary>
    [Export(PropertyHint.Range, "0.25,4,0.01")] public float DryingSpeed { get; set; } = 1f;
    /// <summary>Rain drops on the camera lens; 0 = off.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float LensDroplets { get; set; } = 0.7f;

    // Owned by the graphics quality preset (not persisted by the menu).
    /// <summary>Ripple layers per surface (1 Low .. 4 Very High).</summary>
    public int RippleLayers { get; set; } = 4;
    /// <summary>Drip instance budget (128 Low .. 512 Ultra); drips are not built yet, the value is carried for the preset contract.</summary>
    public int DripBudget { get; set; } = 512;
}
