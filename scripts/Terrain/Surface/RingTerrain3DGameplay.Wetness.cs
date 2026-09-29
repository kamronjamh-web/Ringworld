using Godot;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Connects local weather wetness to the active Terrain3D material owners.</summary>
public partial class RingTerrain3DGameplay
{
    [Export] public NodePath LocalWeatherEffectsPath { get; set; } = new("../LocalWeatherEffects");
    private RingLocalWeatherEffects? _localWeatherEffects;
    private float _dryRoughness = .92f, _wetRoughness = .35f, _wetnessStrength = 1.0f;
    private bool _smoothTerrainShading = true;

    public void ApplySurfaceAppearanceSettings(GameplaySurfaceTerrainSettings settings)
    {
        _dryRoughness = GameplaySurfaceTerrainDefaults.ClampDryRoughness(settings.DryRoughness);
        _wetRoughness = GameplaySurfaceTerrainDefaults.ClampWetRoughness(settings.WetRoughness);
        _wetnessStrength = GameplaySurfaceTerrainDefaults.ClampRainWetnessStrength(settings.RainWetnessStrength);
        _smoothTerrainShading = settings.SmoothTerrainShading;
        _surface?.SetSmoothTerrainShading(_smoothTerrainShading);
        _surface?.SetSurfaceAppearance(_dryRoughness, _wetRoughness, _wetnessStrength);
    }

    public void UpdateSurfaceAppearance()
    {
        _localWeatherEffects ??= GetNodeOrNull<RingLocalWeatherEffects>(LocalWeatherEffectsPath);
        float wetness = _localWeatherEffects?.SurfaceWetness ?? 0.0f;
        _surface?.SetSmoothTerrainShading(_smoothTerrainShading);
        _surface?.SetSurfaceAppearance(_dryRoughness, _wetRoughness, _wetnessStrength);
        _surface?.SetSurfaceWetness(wetness);
    }
}
