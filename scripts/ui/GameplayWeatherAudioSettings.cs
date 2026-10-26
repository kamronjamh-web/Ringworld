using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Persisted weather audio choices (grave-key menu, Lighting tab). Files saved before a field existed load its default.</summary>
[GlobalClass]
public partial class GameplayWeatherAudioSettings : Resource
{
    [Export] public bool ThunderEnabled { get; set; } = true;
    [Export] public float ThunderVolumeDb { get; set; } = -8f;
    [Export] public bool AmbienceEnabled { get; set; } = true;
    [Export] public float RainVolumeDb { get; set; } = -10f;
    [Export] public float WindVolumeDb { get; set; } = -13f;
    [Export] public float DistantStormVolumeDb { get; set; } = -18f;
    /// <summary>How strongly the four directional rain, wind and storm speakers pan against the camera (0.2 narrow, 1 wide).</summary>
    [Export] public float SpatialWidth { get; set; } = 0.6f;
    /// <summary>Level of near rain one-shots; applies once the one-shot pool has assets.</summary>
    [Export] public float NearDetailVolumeDb { get; set; } = 0f;
}
