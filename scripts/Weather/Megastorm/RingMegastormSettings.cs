using Godot;
namespace RingworldTechDemo.Weather;

/// <summary>
/// Runtime tunables of the megastorm (docs/megastorm-design.md section 7). Older preference files without a key load these defaults.
/// Ranges must match <see cref="RingworldTechDemo.UI.GameplayMegastormDefaults.Limits"/>.
/// </summary>
[GlobalClass]
public partial class RingMegastormSettings : Resource
{
    public static RingMegastormSettings Current { get; set; } = new();
    /// <summary>Wall speed in m/s (350 = a lap of about 20 in-game days). A change re-anchors the running storm at "now".</summary>
    [Export(PropertyHint.Range, "5,500,1")] public float SpeedMetersPerSecond { get; set; } = 350f;
    /// <summary>Trailing rain length L in km: the pass is 50 km + L (390 km by default).</summary>
    [Export(PropertyHint.Range, "100,600,1")] public float TrailingLengthKm { get; set; } = 340f;
    /// <summary>Warning reach R in km: precursor extent and the ground-hum stages (hum at 10 R, water and wildlife at 2 R).</summary>
    [Export(PropertyHint.Range, "50,600,1")] public float WarningReachKm { get; set; } = 220f;
    /// <summary>Master level of the megastorm's own sound (ground hum, wall singing, boom, roar) in dB, before the shelter muffling. Older files load 0.</summary>
    [Export(PropertyHint.Range, "-24,6,0.5")] public float SoundVolumeDb { get; set; } = 0f;
    /// <summary>Debug spawn distance in km: how far ahead, in the direction the explorer faces, the menu's "Spawn megastorm" places the wall. The model never reads it. Older files load 60.</summary>
    [Export(PropertyHint.Range, "5,600,1")] public float SpawnDistanceKm { get; set; } = 60f;
}
