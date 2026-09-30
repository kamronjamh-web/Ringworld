using Godot;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Forwards the shared terrain lighting contract to Terrain3D's native material.</summary>
public partial class RingTerrain3DSurface
{
    private float _surfaceDayStartDot = -.2f;
    private float _surfaceDayFullDot = .35f;
    private Vector3 _surfaceNightTint = new(.30f, .45f, .70f);
    private float _surfaceNightDirectLightFactor;
    private float _surfaceDayDirectLightFactor = 1.0f;
    private float _surfaceShadowFloor;
    private ulong _lightingTerrainId;
    private Vector3 _appliedLightingSunDirection = new(float.PositiveInfinity, 0.0f, 0.0f);
    private float _appliedDayStartDot = float.NaN, _appliedDayFullDot = float.NaN;
    private Vector3 _appliedNightTint = new(float.NaN, float.NaN, float.NaN);
    private float _appliedNightDirectLightFactor = float.NaN, _appliedDayDirectLightFactor = float.NaN, _appliedShadowFloor = float.NaN;

    public void SetSurfaceLighting(
        float dayStartDot,
        float dayFullDot,
        Color nightTint,
        float nightDirectLightFactor,
        float dayDirectLightFactor,
        float shadowFloor)
    {
        _surfaceDayStartDot = Mathf.Clamp(dayStartDot, -1.0f, 1.0f);
        _surfaceDayFullDot = Mathf.Max(_surfaceDayStartDot + .001f, Mathf.Clamp(dayFullDot, -1.0f, 1.0f));
        _surfaceNightTint = new Vector3(nightTint.R, nightTint.G, nightTint.B);
        _surfaceNightDirectLightFactor = Mathf.Clamp(nightDirectLightFactor, 0.0f, 1.0f);
        _surfaceDayDirectLightFactor = Mathf.Clamp(dayDirectLightFactor, 0.0f, 2.0f);
        _surfaceShadowFloor = Mathf.Clamp(shadowFloor, 0.0f, 1.0f);
        ForwardSurfaceLighting(ResolveLightingSunDirection());
    }

    private Vector3 ResolveLightingSunDirection()
        => ResolveAtmosphereSun()?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Up;

    private void ForwardSurfaceLighting(Vector3 sunDirection)
    {
        if (_activePreparedSurface != null)
        {
            Color tint = new(_surfaceNightTint.X, _surfaceNightTint.Y, _surfaceNightTint.Z);
            _activePreparedSurface.SetSurfaceLighting(_surfaceDayStartDot, _surfaceDayFullDot, tint, _surfaceNightDirectLightFactor, _surfaceDayDirectLightFactor, _surfaceShadowFloor);
            _stagedPreparedSurface?.SetSurfaceLighting(_surfaceDayStartDot, _surfaceDayFullDot, tint, _surfaceNightDirectLightFactor, _surfaceDayDirectLightFactor, _surfaceShadowFloor);
            _idlePreparedSurface?.SetSurfaceLighting(_surfaceDayStartDot, _surfaceDayFullDot, tint, _surfaceNightDirectLightFactor, _surfaceDayDirectLightFactor, _surfaceShadowFloor);
            return;
        }
        if (_terrain == null) return;
        ulong terrainId = _terrain.GetInstanceId();
        bool newOwner = _lightingTerrainId != terrainId;
        if (newOwner || !_appliedLightingSunDirection.IsEqualApprox(sunDirection)) SetMaterialParameter("sun_direction", sunDirection);
        if (newOwner || !Mathf.IsEqualApprox(_appliedDayStartDot, _surfaceDayStartDot)) SetMaterialParameter("day_start_dot", _surfaceDayStartDot);
        if (newOwner || !Mathf.IsEqualApprox(_appliedDayFullDot, _surfaceDayFullDot)) SetMaterialParameter("day_full_dot", _surfaceDayFullDot);
        if (newOwner || !_appliedNightTint.IsEqualApprox(_surfaceNightTint)) SetMaterialParameter("night_tint", _surfaceNightTint);
        if (newOwner || !Mathf.IsEqualApprox(_appliedNightDirectLightFactor, _surfaceNightDirectLightFactor)) SetMaterialParameter("night_direct_light_factor", _surfaceNightDirectLightFactor);
        if (newOwner || !Mathf.IsEqualApprox(_appliedDayDirectLightFactor, _surfaceDayDirectLightFactor)) SetMaterialParameter("day_direct_light_factor", _surfaceDayDirectLightFactor);
        if (newOwner || !Mathf.IsEqualApprox(_appliedShadowFloor, _surfaceShadowFloor)) SetMaterialParameter("shadow_floor", _surfaceShadowFloor);
        _lightingTerrainId = terrainId;
        _appliedLightingSunDirection = sunDirection;
        _appliedDayStartDot = _surfaceDayStartDot;
        _appliedDayFullDot = _surfaceDayFullDot;
        _appliedNightTint = _surfaceNightTint;
        _appliedNightDirectLightFactor = _surfaceNightDirectLightFactor;
        _appliedDayDirectLightFactor = _surfaceDayDirectLightFactor;
        _appliedShadowFloor = _surfaceShadowFloor;
    }
}
