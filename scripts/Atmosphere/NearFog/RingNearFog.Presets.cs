using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Atmosphere.NearFog;

/// <summary>
/// NF-6 preset gate (docs/sun-flare-shafts-near-fog-plan.md, NF-1 design N6 and the N8 NF-6 row): the near ground fog is drawn on High and above and is off on Medium and Low.
/// <see cref="PresetAllowsFog"/> is read by the owner's activation rate (RingNearFog._Process); with the gate false the owner is inactive, which is byte-identical to the feature absent.
/// The preset is re-read from the graphics quality controller at most every <see cref="PresetPollSeconds"/> (the controller has no preset-changed signal).
/// </summary>
public partial class RingNearFog
{
    public const double PresetPollSeconds = 0.5;
    /// <summary>Froxel grid while the gate allows fog (High and above; the same as the project setting, so writing it is idempotent).</summary>
    public const int FroxelVolumeSize = 96, FroxelVolumeDepth = 80;

    private static bool _presetAllows = true;
    private static ulong _presetCheckedMsec;
    private static bool _presetChecked;
    private static RingGraphicsQualityController? _presetController;

    /// <summary>Test hook: forces the gate (null = follow the graphics controller). Also re-applies the froxel grid when it switches on.</summary>
    public static bool? PresetOverride { get; set; }

    /// <summary>
    /// True when the current graphics preset allows the fog. Without a controller to read (a fixture without the graphics node) the gate is open, so scene fixtures
    /// behave as before NF-6.
    /// </summary>
    public static bool PresetAllowsFog
    {
        get
        {
            if (PresetOverride is bool forced) return forced;
            ulong now = Time.GetTicksMsec();
            if (_presetChecked && (now - _presetCheckedMsec) < (ulong)(PresetPollSeconds * 1000.0)) return _presetAllows;
            bool first = !_presetChecked;
            _presetChecked = true;
            _presetCheckedMsec = now;
            if (_presetController == null || !GodotObject.IsInstanceValid(_presetController))
                _presetController = Instance != null && GodotObject.IsInstanceValid(Instance) && Instance.IsInsideTree()
                    ? Instance.GetNodeOrNull<RingGraphicsQualityController>("../GraphicsQuality")
                    : null;
            bool allows = _presetController == null || AllowsFog(_presetController.CurrentPreset);
            if (allows && (first || !_presetAllows)) ApplyFroxelGrid();
            _presetAllows = allows;
            return allows;
        }
    }

    /// <summary>High, VeryHigh, UltraVideo and Cinematic5090 allow the fog; Low and Medium do not.</summary>
    public static bool AllowsFog(RingTerrainLodCoordinator.QualityPreset preset) => preset >= RingTerrainLodCoordinator.QualityPreset.High;

    private static void ApplyFroxelGrid() => RenderingServer.EnvironmentSetVolumetricFogVolumeSize(FroxelVolumeSize, FroxelVolumeDepth);
}
