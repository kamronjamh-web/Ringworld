using System;
using Godot;
using RingworldTechDemo.Shadows;
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
    private const float SunShadowLightingCutoffHysteresisDegrees = 1.0f;
    private RingVoxelExplorerController? _localShadowExplorer;
    private bool _localSunShadowSuspended;
    private string _localShadowHandoffReason = "waiting for shadow tuning";
    private float _effectiveSunShadowOffElevationDegrees = DefaultSunShadowOffElevationDegrees;
    private float _effectiveSunShadowOnElevationDegrees = DefaultSunShadowOnElevationDegrees;
    private float _maxLocalSunElevationDegrees = float.NaN;
    /// <summary>Sun energy below which the shadow pass is skipped (docs/eclipse-false-sun-spec.md finding 4 / F17). The planet light uses the same threshold.</summary>
    public const float SunShadowMinimumEnergy = 0.001f;
    private bool _sunShadowZeroEnergySuspended;
    /// <summary>Near sun map (docs/shadow-revamp-plan.md S5b): fade start of the 2-cascade near map, so the heightfield authority distance is 0.8 x the near distance (320 m at 400 m).</summary>
    public const float NearSunMapFadeStart = .8f;
    public const float NearSunMapLeaveSeconds = 1.0f;
    /// <summary>Fade start of the POI-extended 4-cascade near map.</summary>
    public const float PoiSunMapFadeStart = .9f;
    private bool _nearSunMapActive;
    private float _nearSunMapNotReadySeconds;
    public bool NearSunMapActive => _nearSunMapActive;
    /// <summary>Tests: replaces the "sun heightfield ready" query.</summary>
    internal System.Func<bool>? NearSunMapReadyProbe { get; set; }
    internal void StepNearSunMapForTest(double delta) => UpdateNearSunMap(delta);

    /// <summary>
    /// POI sun map extension: while the near sun map is active and a large POI (RingLargePoiShadowExtents) has its detail mounted, the near map becomes a 4-cascade map out to
    /// D (the camera's distance to the POI's farthest corner, 500 m steps, between the near distance and HeightShadowPoiMapDistanceMeters) so the POI shadows itself and the terrain
    /// around it. Terrain casters and the heightfield authority stay at the near distance.
    /// </summary>
    private bool _poiSunMapActive;
    private float _poiSunMapDistanceMeters;
    private float _poiSunMapNoPoiSeconds;
    public const float PoiSunMapStepMeters = 500.0f;
    /// <summary>Meta key on the sun light while the extension is active: the near distance terrain casters and trees keep using (the light's own max distance is D then).</summary>
    public const string SunNearDistanceMetaKey = "ringworld_sun_near_distance_meters";
    public bool PoiSunMapExtensionActive => _nearSunMapActive && _poiSunMapActive;
    public float PoiSunMapDistanceMeters => PoiSunMapExtensionActive ? _poiSunMapDistanceMeters : 0.0f;
    /// <summary>Diagnostics: POIs registered with RingLargePoiShadowExtents (detail mounted).</summary>
    public int PoiSunMapRegisteredCount => RingLargePoiShadowExtents.Count;
    /// <summary>Diagnostics: camera distance to the farthest corner of a registered POI within reach of the map, or -1 without one (or without a camera).</summary>
    public float PoiSunMapFarthestDistanceMeters => ResolvePoiFarthestDistance() ?? -1.0f;
    /// <summary>Tests and captures: switches the extension off without touching the POI registry (before/after comparisons).</summary>
    public bool PoiSunMapExtensionForcedOff { get; set; }
    /// <summary>Tests: replaces the registry query; returns the camera's distance to the farthest corner of a registered POI within reach of the map, or null when there is none.</summary>
    internal System.Func<float?>? PoiFarthestDistanceProbe { get; set; }
    internal void StepPoiSunMapForTest(double delta) => UpdatePoiSunMapExtension(delta);

    /// <summary>Distance the sun terrain casters cover: the near distance in near mode (extended or not), otherwise the sun map's own range.</summary>
    public float SunTerrainCasterDistanceMeters => _nearSunMapActive ? ResolveHeightShadowSettings().NearMapDistanceMeters
        : _sun != null && GodotObject.IsInstanceValid(_sun) ? _sun.DirectionalShadowMaxDistance : 0.0f;
    /// <summary>Distance A the heightfield treats as the sun map's authority: near x fade start in near or extended mode (the heightfield owns terrain past it), otherwise max distance x fade start.</summary>
    public float SunHeightShadowAuthorityMeters => _nearSunMapActive ? ResolveHeightShadowSettings().NearMapDistanceMeters * NearSunMapFadeStart
        : _sun != null && GodotObject.IsInstanceValid(_sun) ? _sun.DirectionalShadowMaxDistance * _sun.DirectionalShadowFadeStart : 0.0f;

    internal static float QuantizePoiSunMapDistance(float farthestMeters, float nearMeters, float maximumMeters)
    {
        float clamped = Mathf.Clamp(farthestMeters, nearMeters, Mathf.Max(maximumMeters, nearMeters));
        return Mathf.Min(Mathf.Ceil(clamped / PoiSunMapStepMeters) * PoiSunMapStepMeters, Mathf.Max(maximumMeters, nearMeters));
    }

    private float? ResolvePoiFarthestDistance()
    {
        if (PoiFarthestDistanceProbe != null) return PoiFarthestDistanceProbe();
        if (RingLargePoiShadowExtents.Count == 0) return null;
        Camera3D? camera = GetViewport()?.GetCamera3D();
        return camera != null && RingLargePoiShadowExtents.TryGetFarthestCornerDistance(camera.GlobalPosition, ResolveHeightShadowSettings().PoiMapDistanceMeters, out float distance) ? distance : null;
    }

    private void UpdatePoiSunMapExtension(double delta)
    {
        RingHeightShadowSettings settings = ResolveHeightShadowSettings();
        float? farthest = _nearSunMapActive && !PoiSunMapExtensionForcedOff ? ResolvePoiFarthestDistance() : null;
        bool next = _poiSunMapActive;
        float distance = _poiSunMapDistanceMeters;
        if (!_nearSunMapActive || PoiSunMapExtensionForcedOff) { next = false; _poiSunMapNoPoiSeconds = 0.0f; }
        else if (farthest is { } far && float.IsFinite(far))
        {
            next = true;
            _poiSunMapNoPoiSeconds = 0.0f;
            distance = QuantizePoiSunMapDistance(far, settings.NearMapDistanceMeters, settings.PoiMapDistanceMeters);
        }
        else if (_poiSunMapActive)
        {
            _poiSunMapNoPoiSeconds += (float)Math.Max(delta, 0.0);
            if (_poiSunMapNoPoiSeconds >= NearSunMapLeaveSeconds) next = false;
        }
        if (next == _poiSunMapActive && (!next || Mathf.IsEqualApprox(distance, _poiSunMapDistanceMeters))) return;
        _poiSunMapActive = next;
        _poiSunMapDistanceMeters = next ? distance : 0.0f;
        ApplyShadowTuningOverrides();
    }
    public bool SunShadowZeroEnergySuspended => _sunShadowZeroEnergySuspended;

    /// <summary>Near planet map (S7a): while the published front layer is a planet layer covering the camera, the planet shadow map shrinks to a 2-cascade near map (fade 0.8).</summary>
    private bool _nearPlanetMapActive;
    private float _nearPlanetMapNotReadySeconds;
    public bool NearPlanetMapActive => _nearPlanetMapActive;
    /// <summary>Tests: replaces the height-shadow node query (Ready: planet layer published and covering the camera; LeaveNow: receivers off or another light published).</summary>
    internal System.Func<(bool Ready, bool LeaveNow)>? NearPlanetMapProbe { get; set; }
    internal void StepNearPlanetMapForTest(double delta) => UpdateNearPlanetMap(delta);

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

    /// <summary>
    /// Enters the near sun map as soon as the sun heightfield is published and covers the camera; leaves it one second after the heightfield stops being ready (or at once
    /// when the feature or the near-map option is switched off), so a hiccup in the producer cannot flip the map between 6 km and 400 m.
    /// </summary>
    private void UpdateNearSunMap(double delta)
    {
        RingHeightShadowSettings settings = ResolveHeightShadowSettings();
        bool wanted = settings.Enabled && settings.NearMapEnabled;
        bool ready = wanted && (NearSunMapReadyProbe != null ? NearSunMapReadyProbe() : RingTerrainHeightShadow.Instance is { } node && GodotObject.IsInstanceValid(node) && node.IsSunHeightfieldReady);
        bool next = _nearSunMapActive;
        if (!wanted) { next = false; _nearSunMapNotReadySeconds = 0.0f; }
        else if (ready) { next = true; _nearSunMapNotReadySeconds = 0.0f; }
        else if (_nearSunMapActive)
        {
            _nearSunMapNotReadySeconds += (float)Math.Max(delta, 0.0);
            if (_nearSunMapNotReadySeconds >= NearSunMapLeaveSeconds) next = false;
        }
        if (next == _nearSunMapActive) return;
        _nearSunMapActive = next;
        ApplyShadowTuningOverrides();
    }

    /// <summary>
    /// Enters the near planet map once the planet heightfield is published and covers the camera. Leaves at once when the feature, the option or the receivers are switched off
    /// or a sun layer has been published (at dawn the old planet front stays published until the sun layer is ready, so there is no gap); leaves one second after the
    /// front disappears or stops covering the camera (teleport refill), so a hiccup cannot flip the planet map between 250 m and 6 km.
    /// </summary>
    private void UpdateNearPlanetMap(double delta)
    {
        RingHeightShadowSettings settings = ResolveHeightShadowSettings();
        bool wanted = settings.Enabled && settings.PlanetNearMapEnabled;
        bool ready = false, leaveNow = false;
        if (wanted)
        {
            if (NearPlanetMapProbe != null) (ready, leaveNow) = NearPlanetMapProbe();
            else if (RingTerrainHeightShadow.Instance is { } node && GodotObject.IsInstanceValid(node))
            {
                ready = node.IsHeightfieldReadyFor(HeightShadowLight.Planet);
                leaveNow = !node.ReceiversEnabled || node.PublishedLight == HeightShadowLight.Sun;
            }
        }
        bool next = _nearPlanetMapActive;
        if (!wanted || leaveNow) { next = false; _nearPlanetMapNotReadySeconds = 0.0f; }
        else if (ready) { next = true; _nearPlanetMapNotReadySeconds = 0.0f; }
        else if (_nearPlanetMapActive)
        {
            _nearPlanetMapNotReadySeconds += (float)Math.Max(delta, 0.0);
            if (_nearPlanetMapNotReadySeconds >= NearSunMapLeaveSeconds) next = false;
        }
        if (next == _nearPlanetMapActive) return;
        _nearPlanetMapActive = next;
        ApplyShadowTuningOverrides();
    }

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
            suspend = NextSunShadowSuspended(_localSunShadowSuspended, elevation, _effectiveSunShadowOffElevationDegrees, _effectiveSunShadowOnElevationDegrees);
            // The hub event re-aims the StarLight toward the zenith (elevation above already reads the live light basis); never suspend while it is active.
            if (suspend && RingworldTechDemo.Celestial.Eclipse.EclipseHubLighting.Weight > 0f) suspend = false;
            _localShadowHandoffReason = suspend ? "sun safely below local shadow horizon" : "sun at or above local shadow horizon";
        }
        // Eclipse totality (and any other zero-energy sun) lights nothing: skip the shadow pass until the energy returns.
        if (UpdateSunZeroEnergyGate() && RequestedSunShadowsEnabled) { suspend = true; _localShadowHandoffReason = "sun energy below " + SunShadowMinimumEnergy.ToString("0.###"); }
        if (UndergroundShadowSuspended && RequestedSunShadowsEnabled) { suspend = true; _localShadowHandoffReason = "underground enclosed"; }
        _localSunShadowSuspended = suspend;
        bool effectiveShadowEnabled = RequestedSunShadowsEnabled && !suspend;
        if (_sun.ShadowEnabled != effectiveShadowEnabled) _sun.ShadowEnabled = effectiveShadowEnabled;
    }

    /// <summary>
    /// True while the sun is too dim to light anything (totality at zero energy): the shadow map would be rendered for nothing. A small
    /// hysteresis band (2x the threshold) stops flapping while the energy ramps; the pass resumes on the first frame the energy is back.
    /// </summary>
    private bool UpdateSunZeroEnergyGate()
    {
        float energy = _sun != null && GodotObject.IsInstanceValid(_sun) ? _sun.LightEnergy : float.PositiveInfinity;
        _sunShadowZeroEnergySuspended = _sunShadowZeroEnergySuspended
            ? energy < SunShadowMinimumEnergy * 2.0f
            : energy < SunShadowMinimumEnergy;
        return _sunShadowZeroEnergySuspended;
    }

    /// <summary>
    /// The proxy caster chain must cover whichever local directional atlas is
    /// currently illuminating terrain. Daylight uses the quality-controlled
    /// sun range; night can use the independently ranged planet light.
    /// </summary>
    private void UpdateTerrainShadowCasterCoverage()
    {
        if (_coordinator == null || !GodotObject.IsInstanceValid(_coordinator)) return;
        float coverage = ResolveTerrainShadowCasterCoverage(_sun, _planet, _nearSunMapActive ? SunTerrainCasterDistanceMeters : 0.0f);
        if (coverage > 0.0f) _coordinator.SetTerrainShadowCoverageMeters(coverage);
    }

    /// <summary>The sun's contribution is capped at <paramref name="sunCasterCapMeters"/> when positive (near mode: terrain casters stay at the near distance even when the POI extension widens the light's range).</summary>
    internal static float ResolveTerrainShadowCasterCoverage(
        DirectionalLight3D? sun,
        DirectionalLight3D? planet,
        float sunCasterCapMeters = 0.0f)
    {
        float coverage = 0.0f;
        AccumulateActiveDirectionalShadowRange(sun, ref coverage, sunCasterCapMeters);
        AccumulateActiveDirectionalShadowRange(planet, ref coverage);
        return coverage;
    }

    private static void AccumulateActiveDirectionalShadowRange(
        DirectionalLight3D? light,
        ref float coverage,
        float capMeters = 0.0f)
    {
        if (light == null || !GodotObject.IsInstanceValid(light)
            || !light.ShadowEnabled || light.LightEnergy <= 0.001f) return;
        float distance = light.DirectionalShadowMaxDistance;
        if (capMeters > 0.0f && float.IsFinite(distance)) distance = Mathf.Min(distance, capMeters);
        if (float.IsFinite(distance) && distance > 0.0f) coverage = Mathf.Max(coverage, distance);
    }

    internal static bool NextSunShadowSuspended(bool suspended, float maxElevationDegrees, float offDegrees, float onDegrees)
        => suspended ? maxElevationDegrees < onDegrees : maxElevationDegrees <= offDegrees;

    /// <summary>Default thresholds: suspend where the sun's direct light is zero (asin of the ring day-start dot), wake one degree above.</summary>
    internal static (float Off, float On) DeriveSunShadowThresholds(float dayStartDot)
    {
        float off = Mathf.Clamp(Mathf.RadToDeg(Mathf.Asin(dayStartDot)), MinimumSunShadowElevationDegrees, MaximumSunShadowElevationDegrees);
        return (off, off + SunShadowLightingCutoffHysteresisDegrees);
    }

    private bool ResolveLocalShadowThresholds()
    {
        bool offOverridden = _shadowTuningOverrides.TryGetValue("SunShadowOffElevationDegrees", out Variant offValue);
        bool onOverridden = _shadowTuningOverrides.TryGetValue("SunShadowOnElevationDegrees", out Variant onValue);
        float off = offOverridden ? offValue.AsSingle() : DefaultSunShadowOffElevationDegrees;
        float on = onOverridden ? onValue.AsSingle() : DefaultSunShadowOnElevationDegrees;
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
        if (!offOverridden && !onOverridden)
        {
            // No explicit thresholds: the sun lights nothing below asin(RingDayStartDot) (max local elevation already includes the receiver bound), so
            // suspend there and wake one degree above. Setting either override restores the legacy fixed thresholds below (explicit values win).
            (_effectiveSunShadowOffElevationDegrees, _effectiveSunShadowOnElevationDegrees) = DeriveSunShadowThresholds(dayStart);
            return true;
        }
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
