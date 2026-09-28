using Godot;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Weather-driven material parameters shared by every prepared Terrain3D owner.</summary>
public partial class RingTerrain3DSurface
{
    private float _surfaceWetness;
    private float _surfaceDryRoughness = .92f;
    private float _surfaceWetRoughness = .35f;
    private float _rainWetnessStrength = 1.0f;
    private ulong _appearanceTerrainId;
    private Vector4 _appliedAppearance = new(float.NaN, float.NaN, float.NaN, float.NaN);

    public void SetSurfaceWetness(float wetness)
    {
        wetness = float.IsFinite(wetness) ? Mathf.Clamp(wetness, 0.0f, 1.0f) : 0.0f;
        if (!Mathf.IsEqualApprox(_surfaceWetness, wetness)) _surfaceWetness = wetness;
        ForwardSurfaceAppearance();
    }

    public void SetSurfaceAppearance(float dryRoughness, float wetRoughness, float rainWetnessStrength)
    {
        dryRoughness = Mathf.Clamp(float.IsFinite(dryRoughness) ? dryRoughness : .92f, .65f, .98f);
        wetRoughness = Mathf.Clamp(float.IsFinite(wetRoughness) ? wetRoughness : .35f, .10f, .75f);
        rainWetnessStrength = Mathf.Clamp(float.IsFinite(rainWetnessStrength) ? rainWetnessStrength : 1.0f, 0.0f, 1.0f);
        _surfaceDryRoughness = dryRoughness;
        _surfaceWetRoughness = wetRoughness;
        _rainWetnessStrength = rainWetnessStrength;
        ForwardSurfaceAppearance();
    }

    private void ForwardSurfaceAppearance()
    {
        if (_activePreparedSurface != null)
        {
            _activePreparedSurface.SetSurfaceAppearance(_surfaceDryRoughness, _surfaceWetRoughness, _rainWetnessStrength);
            _activePreparedSurface.SetSurfaceWetness(_surfaceWetness);
            _stagedPreparedSurface?.SetSurfaceAppearance(_surfaceDryRoughness, _surfaceWetRoughness, _rainWetnessStrength);
            _stagedPreparedSurface?.SetSurfaceWetness(_surfaceWetness);
            _idlePreparedSurface?.SetSurfaceAppearance(_surfaceDryRoughness, _surfaceWetRoughness, _rainWetnessStrength);
            _idlePreparedSurface?.SetSurfaceWetness(_surfaceWetness);
            return;
        }
        if (_terrain == null) return;
        var values = new Vector4(_surfaceWetness, _surfaceDryRoughness, _surfaceWetRoughness, _rainWetnessStrength);
        ulong terrainId = _terrain.GetInstanceId();
        bool newOwner = _appearanceTerrainId != terrainId;
        if (newOwner || !Mathf.IsEqualApprox(_appliedAppearance.X, values.X)) SetMaterialParameter("surface_wetness", values.X);
        if (newOwner || !Mathf.IsEqualApprox(_appliedAppearance.Y, values.Y)) SetMaterialParameter("surface_dry_roughness", values.Y);
        if (newOwner || !Mathf.IsEqualApprox(_appliedAppearance.Z, values.Z)) SetMaterialParameter("surface_wet_roughness", values.Z);
        if (newOwner || !Mathf.IsEqualApprox(_appliedAppearance.W, values.W)) SetMaterialParameter("surface_wetness_strength", values.W);
        _appearanceTerrainId = terrainId;
        _appliedAppearance = values;
    }

    /// <summary>Read-only test hook for the currently staged native material.</summary>
    public GodotObject? GetStagedTerrainMaterialForDiagnostics() => _stagedPreparedSurface?._terrain?.Call("get_material").AsGodotObject();
}
