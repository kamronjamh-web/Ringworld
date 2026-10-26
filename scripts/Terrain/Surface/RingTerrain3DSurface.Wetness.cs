using Godot;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>
/// Look parameters shared by every prepared Terrain3D owner. Wetness no longer lives here: the surface shader reads the per-location wet map
/// (materials/ring_wetness.gdshaderinc, scripts/Weather/RingWetWorld.cs); only the terrain's own dry roughness and smooth shading are forwarded.
/// </summary>
public partial class RingTerrain3DSurface
{
    private float _surfaceDryRoughness = .92f;
    private bool _smoothTerrainShading = true;
    private bool _appliedSmoothTerrainShading;
    private ulong _appearanceTerrainId;
    private float _appliedDryRoughness = float.NaN;

    public void SetSurfaceAppearance(float dryRoughness)
    {
        dryRoughness = Mathf.Clamp(float.IsFinite(dryRoughness) ? dryRoughness : .92f, .65f, .98f);
        _surfaceDryRoughness = dryRoughness;
        ForwardSurfaceAppearance();
    }

    public void SetSmoothTerrainShading(bool enabled)
    {
        if (_smoothTerrainShading == enabled) return;
        _smoothTerrainShading = enabled;
        ForwardSurfaceAppearance();
    }

    private void ForwardSurfaceAppearance()
    {
        if (_activePreparedSurface != null)
        {
            _activePreparedSurface.SetSmoothTerrainShading(_smoothTerrainShading);
            _activePreparedSurface.SetSurfaceAppearance(_surfaceDryRoughness);
            _stagedPreparedSurface?.SetSmoothTerrainShading(_smoothTerrainShading);
            _stagedPreparedSurface?.SetSurfaceAppearance(_surfaceDryRoughness);
            _idlePreparedSurface?.SetSmoothTerrainShading(_smoothTerrainShading);
            _idlePreparedSurface?.SetSurfaceAppearance(_surfaceDryRoughness);
            return;
        }
        if (_terrain == null) return;
        ulong terrainId = _terrain.GetInstanceId();
        bool newOwner = _appearanceTerrainId != terrainId;
        if (newOwner || !Mathf.IsEqualApprox(_appliedDryRoughness, _surfaceDryRoughness)) SetMaterialParameter("surface_dry_roughness", _surfaceDryRoughness);
        if (newOwner || _appliedSmoothTerrainShading != _smoothTerrainShading) SetMaterialParameter("smooth_terrain_shading", _smoothTerrainShading);
        _appearanceTerrainId = terrainId;
        _appliedDryRoughness = _surfaceDryRoughness;
        _appliedSmoothTerrainShading = _smoothTerrainShading;
    }

    /// <summary>Read-only test hook for the currently staged native material.</summary>
    public GodotObject? GetStagedTerrainMaterialForDiagnostics() => _stagedPreparedSurface?._terrain?.Call("get_material").AsGodotObject();
}
