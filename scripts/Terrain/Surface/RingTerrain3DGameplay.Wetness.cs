using Godot;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Forwards the terrain look settings (dry roughness, smooth shading) to the active Terrain3D material owners. Wetness is read from the wet map by the shader.</summary>
public partial class RingTerrain3DGameplay
{
    private float _dryRoughness = .92f;
    private bool _smoothTerrainShading = true;

    public void ApplySurfaceAppearanceSettings(GameplaySurfaceTerrainSettings settings)
    {
        _dryRoughness = GameplaySurfaceTerrainDefaults.ClampDryRoughness(settings.DryRoughness);
        _smoothTerrainShading = settings.SmoothTerrainShading;
        _surface?.SetSmoothTerrainShading(_smoothTerrainShading);
        _surface?.SetSurfaceAppearance(_dryRoughness);
        ForwardSurfaceLighting();
    }

    public void UpdateSurfaceAppearance()
    {
        _surface?.SetSmoothTerrainShading(_smoothTerrainShading);
        _surface?.SetSurfaceAppearance(_dryRoughness);
        ForwardSurfaceLighting();
    }

    private void ForwardSurfaceLighting()
    {
        if (_grid == null) return;
        _surface?.SetSurfaceLighting(
            _grid.RingDayStartDot,
            _grid.RingDayFullDot,
            _grid.RingNightTint,
            _grid.RingNightDirectLightFactor,
            _grid.RingDayDirectLightFactor,
            _grid.RingShadowFloor);
    }
}
