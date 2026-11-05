using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Persisted lightning choices (grave-key menu, Lighting tab). Files saved before a field existed load its default.</summary>
[GlobalClass]
public partial class GameplayLightningSettings : Resource
{
    /// <summary>Natural strike rate multiplier (0 none, 1 default, 2 double).</summary>
    [Export] public float LightningFrequency { get; set; } = 1f;
    /// <summary>Red sprite / blue jet rate multiplier above storm tops (0 none, 1 default, 2 double).</summary>
    [Export] public float UpperAtmosphereLightning { get; set; } = 1f;
    /// <summary>Surface light strength (0 none, 1 default, 2 double). Older files use this default.</summary>
    [Export] public float LightningIllumination { get; set; } = 1f;
    /// <summary>Surface light reach in kilometres. Older files use this default.</summary>
    [Export] public float LightningIlluminationReachKm { get; set; } = 20f;
    /// <summary>Accessibility: dims and softens every flash, thins natural strikes and sprites, and caps per-frame luminance change.</summary>
    [Export] public bool ReducedFlashes { get; set; } = false;
}
