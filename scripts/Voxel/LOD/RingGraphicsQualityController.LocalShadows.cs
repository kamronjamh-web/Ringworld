using System;
using Godot;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingGraphicsQualityController
{
    private const float DefaultSunShadowOffElevationDegrees = -18.0f;
    private const float DefaultSunShadowOnElevationDegrees = -16.0f;
    private const float MinimumSunShadowElevationDegrees = -45.0f;
    private const float MaximumSunShadowElevationDegrees = -1.0f;
    private const float MinimumShadowHandoffHysteresisDegrees = .1f;
    private RingVoxelExplorerController? _localShadowExplorer;
    private bool _localSunShadowSuspended;
    private string _localShadowHandoffReason = "waiting for shadow tuning";
    private float _effectiveSunShadowOffElevationDegrees = DefaultSunShadowOffElevationDegrees;
    private float _effectiveSunShadowOnElevationDegrees = DefaultSunShadowOnElevationDegrees;
    private float _maxLocalSunElevationDegrees = float.NaN;

    public bool LocalShadowHandoffEnabled => !_shadowTuningOverrides.TryGetValue("LocalShadowHandoffEnabled", out Variant enabled) || enabled.AsBool();
    public float EffectiveSunShadowOffElevationDegrees => _effectiveSunShadowOffElevationDegrees;
    public float EffectiveSunShadowOnElevationDegrees => _effectiveSunShadowOnElevationDegrees;
    public bool LocalSunShadowSuspended => _localSunShadowSuspended;
    public string LocalShadowHandoffReason => _localShadowHandoffReason;
    /// <summary>Underground-mode reason: sun shadow maps are skipped while the player is fully enclosed (sun energy is already zero then).</summary>
    public bool UndergroundShadowSuspended { get; set; }

    public Godot.Collections.Dictionary GetLocalShadowHandoffDiagnostics() => new()
    {
        ["requested_sun_shadows"] = RequestedSunShadowsEnabled,
        ["actual_sun_shadows"] = _sun != null && GodotObject.IsInstanceValid(_sun) && _sun.ShadowEnabled,
        ["suspended"] = _localSunShadowSuspended,
        ["handoff_enabled"] = LocalShadowHandoffEnabled,
        ["off_elevation_degrees"] = _effectiveSunShadowOffElevationDegrees,
        ["on_elevation_degrees"] = _effectiveSunShadowOnElevationDegrees,
        ["max_local_sun_elevation_degrees"] = float.IsFinite(_maxLocalSunElevationDegrees)
            ? (Variant)_maxLocalSunElevationDegrees : default(Variant),
        ["reason"] = _localShadowHandoffReason
    };

    private void UpdateLocalShadowHandoff()
    {
        _maxLocalSunElevationDegrees = float.NaN;
        bool thresholdsValid = ResolveLocalShadowThresholds();
        if (_sun == null || !GodotObject.IsInstanceValid(_sun) || !_sun.IsInsideTree()) { _localSunShadowSuspended = false; _localShadowHandoffReason = "sun unavailable"; return; }
        bool suspend = false;
        if (!RequestedSunShadowsEnabled) _localShadowHandoffReason = "sun shadows disabled by preference";
        else if (!LocalShadowHandoffEnabled) _localShadowHandoffReason = "automatic handoff disabled";
        else if (!thresholdsValid) _localShadowHandoffReason = "ring day threshold cannot safely wake shadows";
        else if (!TryGetMaximumLocalSunElevation(out float elevation, out string reason)) _localShadowHandoffReason = reason;
        else
        {
            _maxLocalSunElevationDegrees = elevation;
            if (_localSunShadowSuspended)
                suspend = elevation < _effectiveSunShadowOnElevationDegrees;
            else
                suspend = elevation <= _effectiveSunShadowOffElevationDegrees;
            _localShadowHandoffReason = suspend ? "sun safely below local shadow horizon" : "sun at or above local shadow horizon";
        }
        if (UndergroundShadowSuspended && RequestedSunShadowsEnabled) { suspend = true; _localShadowHandoffReason = "underground enclosed"; }
        _localSunShadowSuspended = suspend;
        bool effectiveShadowEnabled = RequestedSunShadowsEnabled && !suspend;
        if (_sun.ShadowEnabled != effectiveShadowEnabled) _sun.ShadowEnabled = effectiveShadowEnabled;
    }

    /// <summary>
    /// The proxy caster chain must cover whichever local directional atlas is
    /// currently illuminating terrain. Daylight uses the quality-controlled
    /// sun range; night can use the independently ranged planet light.
    /// </summary>
    private void UpdateTerrainShadowCasterCoverage()
    {
        if (_coordinator == null || !GodotObject.IsInstanceValid(_coordinator)) return;
        float coverage = ResolveTerrainShadowCasterCoverage(_sun, _planet);
        if (coverage > 0.0f) _coordinator.SetTerrainShadowCoverageMeters(coverage);
    }

    internal static float ResolveTerrainShadowCasterCoverage(
        DirectionalLight3D? sun,
        DirectionalLight3D? planet)
    {
        float coverage = 0.0f;
        AccumulateActiveDirectionalShadowRange(sun, ref coverage);
        AccumulateActiveDirectionalShadowRange(planet, ref coverage);
        return coverage;
    }

    private static void AccumulateActiveDirectionalShadowRange(
        DirectionalLight3D? light,
        ref float coverage)
    {
        if (light == null || !GodotObject.IsInstanceValid(light)
            || !light.ShadowEnabled || light.LightEnergy <= 0.001f) return;
        float distance = light.DirectionalShadowMaxDistance;
        if (float.IsFinite(distance) && distance > 0.0f) coverage = Mathf.Max(coverage, distance);
    }

    private bool ResolveLocalShadowThresholds()
    {
        float off = _shadowTuningOverrides.TryGetValue("SunShadowOffElevationDegrees", out Variant offValue) ? offValue.AsSingle() : DefaultSunShadowOffElevationDegrees;
        float on = _shadowTuningOverrides.TryGetValue("SunShadowOnElevationDegrees", out Variant onValue) ? onValue.AsSingle() : DefaultSunShadowOnElevationDegrees;
        // Resume before the ring's daytime direct lighting can resume, and preserve hysteresis.
        float dayStart = _grid != null && GodotObject.IsInstanceValid(_grid) && _grid.IsInsideTree()
            ? _grid.RingDayStartDot : float.NaN;
        if (!float.IsFinite(dayStart) || dayStart <= -1.0f || dayStart >= 1.0f)
        {
            _effectiveSunShadowOffElevationDegrees = DefaultSunShadowOffElevationDegrees;
            _effectiveSunShadowOnElevationDegrees = DefaultSunShadowOnElevationDegrees;
            return false;
        }
        float lightingWake = Mathf.RadToDeg(Mathf.Asin(dayStart));
        float latestSafeWake = Mathf.Min(MaximumSunShadowElevationDegrees, lightingWake - .01f);
        if (latestSafeWake < MinimumSunShadowElevationDegrees + MinimumShadowHandoffHysteresisDegrees)
        {
            _effectiveSunShadowOffElevationDegrees = DefaultSunShadowOffElevationDegrees;
            _effectiveSunShadowOnElevationDegrees = DefaultSunShadowOnElevationDegrees;
            return false;
        }
        _effectiveSunShadowOffElevationDegrees = Mathf.Min(Mathf.Clamp(off, MinimumSunShadowElevationDegrees, MaximumSunShadowElevationDegrees), latestSafeWake - MinimumShadowHandoffHysteresisDegrees);
        _effectiveSunShadowOnElevationDegrees = Mathf.Clamp(on, _effectiveSunShadowOffElevationDegrees + MinimumShadowHandoffHysteresisDegrees, latestSafeWake);
        return true;
    }

    private bool TryGetMaximumLocalSunElevation(out float elevationDegrees, out string reason)
    {
        elevationDegrees = float.NaN;
        reason = "invalid local shadow geometry";
        if (_grid == null || !GodotObject.IsInstanceValid(_grid) || !_grid.IsInsideTree() || !_grid.UseRingCurvedPlacement || !_grid.UseHabitableRingAxisLayout || !_grid.EnableRingHemisphereLighting) { reason = "ring hemisphere lighting unavailable"; return false; }
        if (!float.IsFinite(_grid.RingVerticalBaseOffsetMeters) || !float.IsFinite(_grid.RingAcrossWidthMeters) || _grid.RingAcrossWidthMeters <= 0
            || !float.IsFinite(_grid.RingDayStartDot) || !float.IsFinite(_grid.RingNightDirectLightFactor)) { reason = "ring lighting geometry invalid"; return false; }
        if (_grid.RingNightDirectLightFactor > 0) { reason = "night direct lighting is active"; return false; }
        if (_dayNightCycle == null || !GodotObject.IsInstanceValid(_dayNightCycle) || !_dayNightCycle.IsInsideTree()) { reason = "day/night cycle unavailable"; return false; }
        if (_localShadowExplorer == null || !GodotObject.IsInstanceValid(_localShadowExplorer) || !_localShadowExplorer.IsInsideTree())
            _localShadowExplorer = _dayNightCycle.GetNodeOrNull<RingVoxelExplorerController>(_dayNightCycle.ExplorerPath);
        if (_localShadowExplorer == null || !_localShadowExplorer.IsInsideTree()) { reason = "observer unavailable"; return false; }
        var observer = _localShadowExplorer.CanonicalPosition;
        if (!double.IsFinite(observer.AroundMeters) || !double.IsFinite(observer.AcrossMeters) || !double.IsFinite(observer.VerticalMeters)) { reason = "observer position invalid"; return false; }
        if (!_grid.EnforceRingAcrossBounds || observer.AcrossMeters < 0 || observer.AcrossMeters >= _grid.RingAcrossWidthMeters
            || observer.VerticalMeters < _grid.GetStructuralBottomGlobalY()) { reason = "observer outside proven ring shell"; return false; }
        double radius = _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau;
        double radialDistance = radius - (_grid.RingVerticalBaseOffsetMeters + observer.VerticalMeters);
        double shadowDistance = _sun?.DirectionalShadowMaxDistance ?? 0;
        if (!double.IsFinite(radius) || !double.IsFinite(radialDistance) || !double.IsFinite(shadowDistance) || radius <= 0 || radialDistance <= 0 || shadowDistance <= 0 || shadowDistance >= radialDistance) { reason = "shadow receiver range reaches ring axis"; return false; }
        double angle = observer.AroundMeters / radius;
        Vector3 surfaceUp = (_grid.GlobalBasis * new Vector3((float)-Math.Cos(angle), 0, (float)-Math.Sin(angle))).Normalized();
        Vector3 sunDirection = _sun!.GlobalBasis.Z.Normalized();
        if (surfaceUp.LengthSquared() <= .000001f || sunDirection.LengthSquared() <= .000001f
            || !IsFinite(surfaceUp) || !IsFinite(sunDirection)) { reason = "sun direction unavailable"; return false; }
        float centerElevation = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(surfaceUp.Dot(sunDirection), -1, 1)));
        float receiverBound = Mathf.RadToDeg(Mathf.Asin((float)(shadowDistance / radialDistance)));
        elevationDegrees = centerElevation + receiverBound;
        return float.IsFinite(elevationDegrees);
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
