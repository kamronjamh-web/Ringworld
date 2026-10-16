using System;
using Godot;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    private DirectionalLight3D? _grassSunLight;
    private GrassLightingState _lastGrassLightingState;
    private bool _grassLightingStateValid;

    private readonly record struct GrassLightingState(bool Enabled, Vector3 SunDirection, float DayFactor,
        float NightDirect, float DayDirect);

    private void UpdateGrassLightingMaterials()
    {
        if (_material == null && _intermediateMaterial == null && _cardMaterial == null) return;
        if (_grid == null || _explorer == null)
        {
            ApplyGrassLightingState(new GrassLightingState(false, Vector3.Right, 1, 0, 1));
            return;
        }
        if (_grassSunLight == null || !GodotObject.IsInstanceValid(_grassSunLight))
            _grassSunLight = _grid.GetNodeOrNull<DirectionalLight3D>(_grid.RingSunLightPath);
        if (_grassSunLight == null || !GodotObject.IsInstanceValid(_grassSunLight))
        {
            ApplyGrassLightingState(new GrassLightingState(false, Vector3.Right, 1, 0, 1));
            return;
        }

        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        if (!double.IsFinite(geometry.RingRadiusMeters) || geometry.RingRadiusMeters <= 0)
            return;
        double angle = _explorer.CanonicalPosition.AroundMeters / geometry.RingRadiusMeters;
        Vector3 surfaceUp = (geometry.GridGlobalTransform.Basis * new Vector3(
            (float)-Math.Cos(angle), 0, (float)-Math.Sin(angle))).Normalized();
        Vector3 sunDirection = _grassSunLight.GlobalTransform.Basis.Z.Normalized();
        float geometricDay = Mathf.SmoothStep(_grid.RingDayStartDot,
            Mathf.Max(_grid.RingDayStartDot + .001f, _grid.RingDayFullDot), surfaceUp.Dot(sunDirection));
        ApplyGrassLightingState(new GrassLightingState(true, sunDirection, geometricDay,
            _grid.RingNightDirectLightFactor, _grid.RingDayDirectLightFactor));
    }

    private void ApplyGrassLightingState(GrassLightingState state)
    {
        ApplyCardFallbackLighting(state);
        if (_grassLightingStateValid && state == _lastGrassLightingState) return;
        Apply(_material); Apply(_intermediateMaterial);
        _lastGrassLightingState = state;
        _grassLightingStateValid = true;
        void Apply(ShaderMaterial? material)
        {
            if (material == null) return;
            material.SetShaderParameter("grass_primary_sun_gate_enabled", state.Enabled);
            material.SetShaderParameter("grass_primary_sun_direction", state.SunDirection);
            material.SetShaderParameter("grass_primary_sun_day_factor", state.DayFactor);
            material.SetShaderParameter("grass_primary_sun_night_direct", state.NightDirect);
            material.SetShaderParameter("grass_primary_sun_day_direct", state.DayDirect);
        }
    }
}
