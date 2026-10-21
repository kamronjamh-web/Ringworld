using Godot;
using System;
using System.Collections.Generic;

namespace RingworldTechDemo.UI;

/// <summary>Menu-owned SunshineClouds2 compositor adapter; never uses the plugin driver.</summary>
public sealed partial class MenuFlyoverSunshineLayer : Node
{
    // Godot may keep compositor/capture descriptors in flight for several
    // draws. This static queue deliberately owns no adapter/scene references.
    private sealed class RetiredGeneration
    {
        public required CompositorEffect Effect;
        public required Compositor Compositor;
        public int RemainingPostDraws = 6;
    }

    private static readonly List<RetiredGeneration> RetiredGenerations = new();
    private static bool _retirementHooked;
    private const float DefaultFloorMeters = 2200f, DefaultCeilingMeters = 6000f;
    private const float DefaultShadowRadiusKm = 128f;
    private WorldEnvironment? _world;
    private Compositor? _ownedCompositor, _previousCompositor;
    private CompositorEffect? _effect;
    private Texture2D? _clearShadow, _publishedShadow;
    private double _radius;
    private int _width;
    private Vector3 _sun, _seedOffset;
    private Vector3 _bulkWindMeters, _detailWindMeters;
    private Vector2 _publishedBulkWindMeters, _requestedBulkWindMeters;
    private double _requestedShadowTime, _publishedShadowTime;
    private double _lastWindAnimationTime = double.NaN;
    private double _lastCloudFrameAround = double.NaN;
    private ulong _lastShadowRealMsec;
    private int _requestedDispatch, _requestTarget, _publishedDispatch;
    private ulong _publishedWrapperInstanceId;
    private int _shadowWrapperSerial;
    private bool _shadowPending, _shadowDirty = true, _configured;
    // Requested state is distinct from the provider's actual dispatch state:
    // pausing must not discard an already valid published map.
    private bool _shadowGenerationEnabled = true, _shadowGenerationRequested = true;
    private bool _cloudsEnabled = true, _cloudShadowsEnabled = true, _shadowReceiversEnabled = true;
    private float _cloudCoverage = .74f, _cloudDensity = 1.3f;
    private float _cloudFloorMeters = DefaultFloorMeters, _cloudCeilingMeters = DefaultCeilingMeters;
    private float _cloudDriftMps = 30f, _cloudLightEnergy = .45f, _cloudAmbientStrength = 1f, _cloudSharpness = .62f, _sunEnergy = 2.15f;
    private int _cloudResolution = 1, _cloudViewSteps = 128, _cloudLightingSteps = 5, _shadowResolution = 512, _shadowVerticalSamples = 5;
    private float _cloudMinStepMeters = 90f, _cloudMaxStepMeters = 1600f, _cloudLightingTravelMeters = 7000f;
    private float _cloudLodBias = .9f, _cloudBlurPower = .1f, _cloudBlurQuality = 1f;
    private float _cloudDitherSpeed, _cloudRayStartJitterStrength, _cloudStationaryHistoryWeight = .10f, _cloudMovingHistoryWeight;
    private float _cloudAmbientDay = 1f, _cloudAmbientNight = 1f, _effectiveAmbientFactor = 1f;
    private float _shadowExtinctionPerKm = 1.35f, _shadowRadiusKm = DefaultShadowRadiusKm;
    private float _requestedShadowSpanMeters, _publishedShadowSpanMeters;
    private float _effectiveHistoryWeight;
    private bool _cameraBlending;
    private float _lastHistoryFov = float.NaN, _lastHistoryPitch = float.NaN, _lastHistoryAltitude = float.NaN;
    private bool _historyResetPending;
    private int _historyResetObservedCallbackSerial, _historyResetCallbacksRemaining;
    private bool _cameraStationary;
    private bool _cloudFastReconstruction = true;
    private float _shadowUpdatesPerSecond = 5f;
    private Vector2 _requestedCenter = new(float.PositiveInfinity, float.PositiveInfinity);
    private double _lastCameraAround = double.NaN;

    public bool ShadowReady => _cloudsEnabled && _cloudShadowsEnabled && _shadowReceiversEnabled && _publishedShadow is not null;
    public Texture2D? ShadowTexture => ShadowReady ? _publishedShadow : _clearShadow;
    public CompositorEffect? Effect => _effect;
    public Vector2 ShadowCenterCanonicalMeters { get; private set; }
    public float ShadowSpanMeters => _publishedShadow is not null && _publishedShadowSpanMeters > 0f ? _publishedShadowSpanMeters : ShadowSpanForRadius();
    public Vector2 ShadowWindOffsetMeters { get; private set; }
    public Vector2 ShadowShaderUvOffset => ShadowWindOffsetMeters / Math.Max(1f, ShadowSpanMeters);

    public void Configure(WorldEnvironment menuEnvironment, int seed, double radiusMeters, int widthMeters, Vector3 sunDirection)
    {
        Detach();
        _world = menuEnvironment ?? throw new ArgumentNullException(nameof(menuEnvironment));
        if (_world.Environment is null) throw new InvalidOperationException("Menu WorldEnvironment requires an Environment.");
        _radius = Math.Max(1.0, radiusMeters);
        _width = Math.Max(1, widthMeters);
        _sun = sunDirection.LengthSquared() > .000001f ? sunDirection.Normalized() : new Vector3(-.4f, .75f, -.5f).Normalized();
        Resource source = GD.Load<Resource>("res://resources/clouds/sunshine_ring_local.tres") ?? throw new InvalidOperationException("Missing Sunshine resource.");
        _effect = source.Duplicate(false) as CompositorEffect ?? throw new InvalidOperationException("Sunshine resource is not a CompositorEffect.");
        // Menu geometry is opaque terrain/water, so pre-transparent composition
        // preserves the resolved 4x-MSAA menu presentation path.
        _effect.EffectCallbackType = CompositorEffect.EffectCallbackTypeEnum.PreTransparent;
        int boundedSeed = Mathf.PosMod(seed, 4096);
        _seedOffset = new Vector3(boundedSeed * 97f, boundedSeed * 53f, boundedSeed * 31f);
        _bulkWindMeters = Vector3.Zero;
        _detailWindMeters = Vector3.Zero;
        _lastWindAnimationTime = double.NaN;
        SetEffect("cloud_floor", _cloudFloorMeters); SetEffect("cloud_ceiling", _cloudCeilingMeters);
        SetEffect("background_altitude", new Vector4(_cloudFloorMeters, _cloudCeilingMeters, 0, 0)); SetEffect("storm_altitude", new Vector4(_cloudFloorMeters, _cloudCeilingMeters, 0, 0));
        SetEffect("resolution_scale", _cloudResolution); SetEffect("max_step_count", (float)_cloudViewSteps); SetEffect("max_lighting_steps", (float)_cloudLightingSteps);
        SetEffect("min_step_distance", _cloudMinStepMeters); SetEffect("max_step_distance", _cloudMaxStepMeters);
        SetEffect("lighting_travel_distance", _cloudLightingTravelMeters); SetEffect("lod_bias", _cloudLodBias);
        SetEffect("blur_power", _cloudBlurPower); SetEffect("blur_quality", _cloudBlurQuality); SetEffect("menu_fast_reconstruction", _cloudFastReconstruction);
        // The menu has no Sunshine driver ticking its noise clock. The effect
        // owns that callback clock; this is precise canonical input for it.
        SetEffect("menu_temporal_sampling", true); SetEffect("menu_cloud_frame_around_precise", 0.0);
        SetEffect("atmospheric_density", 0f); SetEffect("fog_effect_ground", 0f); SetEffect("use_environment_fog", 0f); SetEffect("accumulation_decay", 0f);
        SetEffect("dither_speed", _cloudDitherSpeed); SetEffect("ray_start_jitter_strength", _cloudRayStartJitterStrength); SetEffect("ring_width_meters", (float)_width);
        SetEffect("cloud_frame_origin", Vector3.Zero); SetEffect("cloud_frame_up", new Vector4(0, 1, 0, (float)_radius)); SetEffect("cloud_frame_across", new Vector4(1, 0, 0, _width * .5f));
        SetEffect("clouds_coverage", _cloudCoverage); SetEffect("clouds_density", _cloudDensity);
        SetEffect("clouds_sharpness", _cloudSharpness);
        SetAmbientColor();
        SetEffect("cloud_ambient_tint", new Color(.45f, .52f, .65f, 1f));
        SetEffect("extra_large_noise_scale", 96000f); SetEffect("large_noise_scale", 36000f); SetEffect("medium_noise_scale", 9000f); SetEffect("small_noise_scale", 2800f);
        SetEffect("origin_offset", _seedOffset); SetEffect("wind_direction", new Vector3(1, 0, .35f).Normalized());
        SetEffect("point_lights_data", new Godot.Collections.Array<Vector4>()); SetEffect("point_effector_data", new Godot.Collections.Array<Vector4>());
        SetSunlightData();
        _previousCompositor = _world.Compositor;
        var compositorEffects = new Godot.Collections.Array<CompositorEffect> { _effect };
        using Godot.Collections.Array compositorEffectsLifetime = (Godot.Collections.Array)compositorEffects;
        _ownedCompositor = new Compositor { CompositorEffects = compositorEffects };
        _world.Compositor = _ownedCompositor;
        _effect.Enabled = _cloudsEnabled;
        SetShadowGenerationEnabled(_cloudsEnabled && _cloudShadowsEnabled && _shadowGenerationRequested, true);
        _clearShadow = CreateClearShadow();
        _configured = true;
    }

    /// <summary>Copies validated scalar menu options into this private effect.</summary>
    public void ApplyTuning(MenuFlyoverSettings settings, Vector3 sunDirection)
    {
        ArgumentNullException.ThrowIfNull(settings);

        bool enabledChanged = SetChanged(ref _cloudsEnabled, settings.CloudsEnabled);
        bool cameraTuningChanged = ChangedHistoryTuning(ref _lastHistoryFov, settings.FieldOfView)
            | ChangedHistoryTuning(ref _lastHistoryPitch, settings.PitchDegrees)
            | ChangedHistoryTuning(ref _lastHistoryAltitude, settings.AltitudeKm);
        bool shadowsChanged = SetChanged(ref _cloudShadowsEnabled, settings.CloudShadowsEnabled);
        bool coverageChanged = SetChanged(ref _cloudCoverage, Mathf.Clamp(settings.CloudCoverage, 0f, 1f));
        bool densityChanged = SetChanged(ref _cloudDensity, Mathf.Max(0f, settings.CloudDensity));
        float floor = Mathf.Max(0f, settings.CloudFloorKm * 1000f);
        float ceiling = Mathf.Max(floor + 1f, settings.CloudCeilingKm * 1000f);
        bool altitudeChanged = SetChanged(ref _cloudFloorMeters, floor) | SetChanged(ref _cloudCeilingMeters, ceiling);
        SetChanged(ref _cloudDriftMps, Mathf.Max(0f, settings.CloudDriftMps));
        bool energyChanged = SetChanged(ref _cloudLightEnergy, Mathf.Max(0f, settings.CloudLightEnergy));
        bool sunEnergyChanged = SetChanged(ref _sunEnergy, Mathf.Max(0f, settings.SunEnergy));
        bool ambientChanged = SetChanged(ref _cloudAmbientStrength, Mathf.Max(0f, settings.CloudAmbientStrength));
        bool ambientDayChanged = SetChanged(ref _cloudAmbientDay, Mathf.Clamp(settings.CloudAmbientDay, 0f, 1f));
        bool ambientNightChanged = SetChanged(ref _cloudAmbientNight, Mathf.Clamp(settings.CloudAmbientNight, 0f, 1f));
        bool sharpnessChanged = SetChanged(ref _cloudSharpness, Mathf.Clamp(settings.CloudSharpness, 0f, 1f));
        bool resolutionChanged = SetChanged(ref _cloudResolution, Mathf.Clamp(settings.CloudResolution, 0, 2));
        bool viewStepsChanged = SetChanged(ref _cloudViewSteps, Mathf.Clamp(settings.CloudViewSteps, 64, 256));
        bool lightingStepsChanged = SetChanged(ref _cloudLightingSteps, Mathf.Clamp(settings.CloudLightingSteps, 1, 8));
        float minStep = Mathf.Clamp(settings.CloudMinStepMeters, 10f, 500f);
        bool minStepChanged = SetChanged(ref _cloudMinStepMeters, minStep);
        bool maxStepChanged = SetChanged(ref _cloudMaxStepMeters, Mathf.Max(minStep + 10f, Mathf.Clamp(settings.CloudMaxStepMeters, 100f, 4000f)));
        bool lightingTravelChanged = SetChanged(ref _cloudLightingTravelMeters, Mathf.Clamp(settings.CloudLightingTravelKm, .5f, 20f) * 1000f);
        bool lodBiasChanged = SetChanged(ref _cloudLodBias, Mathf.Clamp(settings.CloudLodBias, .1f, 2f));
        bool blurPowerChanged = SetChanged(ref _cloudBlurPower, Mathf.Clamp(settings.CloudBlurPower, 0f, 6f));
        bool blurQualityChanged = SetChanged(ref _cloudBlurQuality, Mathf.Clamp(settings.CloudBlurQuality, 1, 4));
        bool fastReconstructionChanged = SetChanged(ref _cloudFastReconstruction, settings.CloudFastReconstruction);
        bool ditherSpeedChanged = SetChanged(ref _cloudDitherSpeed, Mathf.Clamp(settings.CloudDitherSpeed, 0f, 120f));
        bool jitterChanged = SetChanged(ref _cloudRayStartJitterStrength, Mathf.Clamp(settings.CloudRayStartJitterStrength, 0f, 1f));
        SetChanged(ref _cloudStationaryHistoryWeight, Mathf.Clamp(settings.CloudStationaryHistoryWeight, 0f, .95f));
        SetChanged(ref _cloudMovingHistoryWeight, Mathf.Clamp(settings.CloudMovingHistoryWeight, 0f, _cloudStationaryHistoryWeight));
        SetChanged(ref _shadowGenerationRequested, settings.ShadowGenerationEnabled);
        SetChanged(ref _shadowReceiversEnabled, settings.ShadowReceiversEnabled);
        bool extinctionChanged = SetChanged(ref _shadowExtinctionPerKm, Mathf.Clamp(settings.ShadowExtinctionPerKm, 0f, 10f));
        bool radiusChanged = SetChanged(ref _shadowRadiusKm, Mathf.Clamp(settings.ShadowRadiusKm, 8f, 200f));
        bool shadowResolutionChanged = SetChanged(ref _shadowResolution, ClampShadowResolution(settings.ShadowResolution));
        bool shadowSamplesChanged = SetChanged(ref _shadowVerticalSamples, Mathf.Clamp(settings.ShadowVerticalSamples, 1, 64));
        SetChanged(ref _shadowUpdatesPerSecond, Mathf.Clamp(settings.ShadowUpdatesPerSecond, .25f, 20f));

        Vector3 normalizedSun = NormalizeSun(sunDirection);
        bool sunChanged = !_sun.IsEqualApprox(normalizedSun);
        if (sunChanged) _sun = normalizedSun;
        if (!_configured || _effect is null) return;

        if (enabledChanged) _effect.Enabled = _cloudsEnabled;
        if (coverageChanged) SetEffect("clouds_coverage", _cloudCoverage);
        if (densityChanged) SetEffect("clouds_density", _cloudDensity);
        if (altitudeChanged)
        {
            SetEffect("cloud_floor", _cloudFloorMeters); SetEffect("cloud_ceiling", _cloudCeilingMeters);
            SetEffect("background_altitude", new Vector4(_cloudFloorMeters, _cloudCeilingMeters, 0, 0));
            SetEffect("storm_altitude", new Vector4(_cloudFloorMeters, _cloudCeilingMeters, 0, 0));
        }
        if (sharpnessChanged) SetEffect("clouds_sharpness", _cloudSharpness);
        // This property rebuilds Sunshine GPU state, so it is sent only after a real quality edit.
        if (resolutionChanged) SetEffect("resolution_scale", _cloudResolution);
        if (viewStepsChanged) SetEffect("max_step_count", (float)_cloudViewSteps);
        if (minStepChanged) SetEffect("min_step_distance", _cloudMinStepMeters);
        if (maxStepChanged) SetEffect("max_step_distance", _cloudMaxStepMeters);
        if (lightingTravelChanged) SetEffect("lighting_travel_distance", _cloudLightingTravelMeters);
        if (lodBiasChanged) SetEffect("lod_bias", _cloudLodBias);
        if (blurPowerChanged) SetEffect("blur_power", _cloudBlurPower);
        if (blurQualityChanged) SetEffect("blur_quality", _cloudBlurQuality);
        if (fastReconstructionChanged) SetEffect("menu_fast_reconstruction", _cloudFastReconstruction);
        if (ditherSpeedChanged) SetEffect("dither_speed", _cloudDitherSpeed);
        if (jitterChanged) SetEffect("ray_start_jitter_strength", _cloudRayStartJitterStrength);
        if (ambientChanged || ambientDayChanged || ambientNightChanged || sunChanged) SetAmbientColor();
        if (lightingStepsChanged) SetEffect("max_lighting_steps", (float)_cloudLightingSteps);
        if (energyChanged || sunEnergyChanged || lightingStepsChanged || sunChanged) SetSunlightData();

        // Camera projection/pose and cloud/light appearance changes invalidate
        // temporal reprojection assumptions. Keep history clear through real
        // render callbacks, without forcing Sunshine to rebuild its resources.
        bool appearanceChanged = coverageChanged || densityChanged || altitudeChanged || sharpnessChanged || resolutionChanged
            || viewStepsChanged || lightingStepsChanged || minStepChanged || maxStepChanged || lightingTravelChanged || lodBiasChanged
            || blurPowerChanged || blurQualityChanged || fastReconstructionChanged || ditherSpeedChanged || jitterChanged
            || ambientChanged || ambientDayChanged || ambientNightChanged || energyChanged || sunEnergyChanged || sunChanged;
        if (enabledChanged || cameraTuningChanged || appearanceChanged)
        {
            _historyResetPending = true;
            _historyResetObservedCallbackSerial = EffectCallbackSerial();
            // Retain zero history through two completed callbacks. This covers
            // an in-flight sample as well as the first frame using new inputs.
            _historyResetCallbacksRemaining = 2;
            _effectiveHistoryWeight = 0f;
            SetEffect("accumulation_decay", 0f);
        }

        bool generationEnabled = _cloudsEnabled && _cloudShadowsEnabled && _shadowGenerationRequested;
        SetShadowGenerationEnabled(generationEnabled);

        if (enabledChanged || shadowsChanged || coverageChanged || densityChanged || altitudeChanged || sharpnessChanged || resolutionChanged
            || radiusChanged || extinctionChanged || viewStepsChanged || shadowResolutionChanged || shadowSamplesChanged || sunChanged)
        {
            InvalidateShadowPublication();
        }
    }

    /// <summary>
    /// Low-rate sun-only update for slow time-of-day drift. Unlike ApplyTuning it neither
    /// resets temporal history nor clears the published shadow map: the map is
    /// regenerated by the normal moving-camera cadence with the new sun direction.
    /// </summary>
    public void UpdateSunDirection(Vector3 sunDirection)
    {
        Vector3 normalizedSun = NormalizeSun(sunDirection);
        if (_sun.IsEqualApprox(normalizedSun)) return;
        _sun = normalizedSun;
        if (!_configured || _effect is null) return;
        SetAmbientColor();
        SetSunlightData();
    }

    /// <summary>
    /// True while the backdrop is blending camera pose (rotation/FOV/altitude change).
    /// Reprojection only accounts for scroll and wind, so history is treated as moving
    /// (moving history weight) until the blend settles. No reset or resource rebuild.
    /// </summary>
    public void SetCameraBlending(bool blending) => _cameraBlending = blending;

    public void UpdateFrame(double canonicalAroundMeters, double animationSeconds, bool frozen)
    {
        if (!_configured || _effect is null) return;
        bool cameraStationary = !_cameraBlending && (frozen || (!double.IsNaN(_lastCameraAround) && Math.Abs(_lastCameraAround - canonicalAroundMeters) <= .001));
        _cameraStationary = cameraStationary;
        _lastCameraAround = canonicalAroundMeters;
        UpdateCloudFrame(canonicalAroundMeters); UpdateWind(animationSeconds, cameraStationary);
        if (!_cloudsEnabled || !_cloudShadowsEnabled)
        {
            ClearPublishedShadow();
            return;
        }
        RefreshPublishedShadow(animationSeconds);
        float ahead = ShadowAheadMeters();
        Vector2 desiredCenter = new((float)(canonicalAroundMeters + ahead), _width * .5f);
        ulong now = Time.GetTicksMsec();
        bool moved = Math.Abs(desiredCenter.X - _requestedCenter.X) >= 8000f;
        bool due = now - _lastShadowRealMsec >= (ulong)Math.Max(1.0, 1000.0 / _shadowUpdatesPerSecond);
        if (_shadowGenerationEnabled && !_shadowPending && (_requestTarget == 0 || (due && (_shadowDirty || moved || !frozen))))
            RequestShadow(canonicalAroundMeters, animationSeconds, now);
        // Keep wind relative to the published map while a replacement is pending.
        // Its center, texture and sampling time must become visible together.
        ShadowWindOffsetMeters = ShadowReady ? _publishedBulkWindMeters - CurrentBulkWind() : Vector2.Zero;
    }

    public Texture2D? GetCloudTexture() => _cloudsEnabled && _effect?.HasMethod("get_cloud_backdrop_texture") == true ? _effect.Call("get_cloud_backdrop_texture", 0).AsGodotObject() as Texture2D : null;

    public Godot.Collections.Dictionary GetCloudDiagnostics() => new() {
        ["provider"] = "SunshineClouds2", ["configured"] = _configured, ["cloud_enabled"] = _cloudsEnabled,
        ["cloud_shadows_enabled"] = _cloudShadowsEnabled, ["shadow_generation_enabled"] = _shadowGenerationEnabled,
        ["shadow_generation_requested"] = _shadowGenerationRequested,
        ["shadow_receivers_enabled"] = _shadowReceiversEnabled, ["shadow_generation_actual"] = _shadowGenerationEnabled,
        ["shadow_receivers_actual"] = ShadowReady, ["cloud_resolution_scale"] = _cloudResolution,
        ["cloud_view_steps"] = _cloudViewSteps, ["cloud_lighting_steps"] = _cloudLightingSteps,
        ["cloud_coverage"] = _cloudCoverage, ["cloud_density"] = _cloudDensity, ["cloud_sharpness"] = _cloudSharpness,
        ["cloud_drift_mps"] = _cloudDriftMps, ["cloud_light_energy"] = _cloudLightEnergy,
        ["sun_energy"] = _sunEnergy, ["effective_cloud_light_energy"] = _cloudLightEnergy * _sunEnergy / 2.15f,
        ["cloud_ambient_strength"] = _cloudAmbientStrength, ["cloud_ambient_day"] = _cloudAmbientDay,
        ["cloud_ambient_night"] = _cloudAmbientNight, ["effective_cloud_ambient_factor"] = _effectiveAmbientFactor,
        ["effective_cloud_ambient_strength"] = _cloudAmbientStrength * _effectiveAmbientFactor,
        ["shadow_resolution"] = new Vector2I(_shadowResolution, _shadowResolution),
        ["shadow_updates_per_second"] = _shadowUpdatesPerSecond, ["shadow_extinction_per_km"] = _shadowExtinctionPerKm,
        ["shadow_radius_km"] = _shadowRadiusKm, ["requested_shadow_span_meters"] = _requestedShadowSpanMeters,
        ["published_shadow_span_meters"] = _publishedShadowSpanMeters, ["shadow_span_meters"] = ShadowSpanMeters,
        ["shadow_ready"] = ShadowReady, ["shadow_pending"] = _shadowPending, ["shadow_dirty"] = _shadowDirty,
        ["requested_dispatch_count"] = _requestedDispatch, ["published_dispatch_count"] = _publishedDispatch,
        ["shadow_wrapper_serial"] = _shadowWrapperSerial, ["shadow_wrapper_instance_id"] = _publishedWrapperInstanceId,
        ["actual_effect_callback_serial"] = EffectCallbackSerial(), ["shadow_center_canonical_meters"] = ShadowCenterCanonicalMeters,
        ["cloud_floor_meters"] = _cloudFloorMeters, ["cloud_ceiling_meters"] = _cloudCeilingMeters,
        ["min_step_distance_meters"] = _cloudMinStepMeters, ["max_step_distance_meters"] = _cloudMaxStepMeters,
        ["lighting_travel_distance_meters"] = _cloudLightingTravelMeters, ["cloud_lod_bias"] = _cloudLodBias,
        ["cloud_blur_power"] = _cloudBlurPower, ["cloud_blur_quality"] = _cloudBlurQuality,
        ["cloud_fast_reconstruction"] = _cloudFastReconstruction, ["shadow_vertical_samples"] = _shadowVerticalSamples,
        ["cloud_dither_speed"] = _cloudDitherSpeed, ["cloud_ray_start_jitter_strength"] = _cloudRayStartJitterStrength,
        ["cloud_stationary_history_weight"] = _cloudStationaryHistoryWeight, ["cloud_moving_history_weight"] = _cloudMovingHistoryWeight,
        ["effective_cloud_history_weight"] = _effectiveHistoryWeight, ["camera_stationary"] = _cameraStationary,
        ["history_reset_pending"] = _historyResetPending, ["history_reset_callbacks_remaining"] = _historyResetCallbacksRemaining
    };

    private void UpdateCloudFrame(double around)
    {
        // Sunshine snapshots this double only from real compositor callbacks.
        SetEffect("menu_cloud_frame_around_precise", around);
        // Relative tolerances grow to hundreds of meters around this ring.
        // Skip only identical shader inputs, not small legitimate camera moves.
        if ((float)_lastCloudFrameAround == (float)around) return;
        _lastCloudFrameAround = around;
        SetEffect("cloud_frame_around", new Vector4(0, 0, -1, (float)around));
    }
    private void UpdateWind(double seconds, bool cameraStationary)
    {
        if (!double.IsNaN(_lastWindAnimationTime))
        {
            float delta = (float)Math.Max(0.0, seconds - _lastWindAnimationTime);
            Vector3 direction = new(1, 0, .35f);
            _bulkWindMeters += direction * (delta * _cloudDriftMps);
            _detailWindMeters += (direction + Vector3.Up) * (delta * _cloudDriftMps * 3.3f);
        }
        _lastWindAnimationTime = seconds;
        Vector3 bulk = _bulkWindMeters + _seedOffset;
        SetEffect("extra_large_scale_clouds_position", bulk); SetEffect("large_scale_clouds_position", bulk); SetEffect("medium_scale_clouds_position", bulk);
        SetEffect("detail_clouds_position", _detailWindMeters + _seedOffset); SetEffect("current_time", (float)seconds); SetEffect("menu_animation_seconds_precise", seconds);
        if (_historyResetPending)
        {
            int callbackSerial = EffectCallbackSerial();
            if (callbackSerial > _historyResetObservedCallbackSerial)
            {
                _historyResetCallbacksRemaining -= callbackSerial - _historyResetObservedCallbackSerial;
                _historyResetObservedCallbackSerial = callbackSerial;
            }
            _effectiveHistoryWeight = 0f;
            if (_historyResetCallbacksRemaining <= 0) _historyResetPending = false;
        }
        else _effectiveHistoryWeight = cameraStationary ? _cloudStationaryHistoryWeight : _cloudMovingHistoryWeight;
        SetEffect("accumulation_decay", _effectiveHistoryWeight);
    }
    private void SetSunlightData()
    {
        // Sunshine integrates light over multiple cloud samples. Its menu
        // exposure is intentionally lower than the terrain's directional light.
        float effectiveEnergy = _cloudLightEnergy * _sunEnergy / 2.15f;
        SetEffect("directional_lights_data", new Godot.Collections.Array<Vector4> { new(_sun.X, _sun.Y, _sun.Z, _cloudLightingSteps), new(1, 1, 1, effectiveEnergy) }); SetEffect("lights_updated", true);
    }
    private void SetAmbientColor()
    {
        float dayBlend = Mathf.SmoothStep(-.08f, .12f, _sun.Y);
        _effectiveAmbientFactor = Mathf.Lerp(_cloudAmbientNight, _cloudAmbientDay, dayBlend);
        float intensity = _cloudAmbientStrength * _effectiveAmbientFactor;
        SetEffect("cloud_ambient_color", new Color(.75f * intensity, .80f * intensity, .88f * intensity, 1f));
    }
    private void SetShadowGenerationEnabled(bool enabled, bool force = false)
    {
        if (!force && _shadowGenerationEnabled == enabled) return;
        _shadowGenerationEnabled = enabled;
        if (_effect?.HasMethod("set_gpu_cloud_shadow_generation_enabled") == true)
            _effect.Call("set_gpu_cloud_shadow_generation_enabled", enabled);
    }
    private void RequestShadow(double around, double animationSeconds, ulong now)
    {
        if (_effect is null || !_shadowGenerationEnabled || !_effect.HasMethod("configure_gpu_cloud_shadow_cascade")) return;
        float halfExtent = ShadowHalfMeters(), ahead = ShadowAheadMeters();
        float centerAround = (float)(around + ahead), angle = ahead / (float)_radius, s = Mathf.Sin(angle), c = Mathf.Cos(angle);
        Vector3 center = new(0, (float)_radius * (1f - c), -(float)_radius * s), axisAround = new(0, s, -c), axisUp = new(0, c, s);
        bool accepted = _effect.Call("configure_gpu_cloud_shadow_cascade", 0, _shadowResolution, center, axisAround, Vector3.Right, axisUp,
            new Vector2(centerAround, _width * .5f), (float)_radius, halfExtent, _sun, _cloudFloorMeters, _cloudCeilingMeters, _shadowVerticalSamples, _shadowExtinctionPerKm, true).AsBool();
        if (!accepted) return;
        _requestedCenter = new Vector2(centerAround, _width * .5f);
        _requestedDispatch++;
        _requestTarget = DispatchCount() + 1;
        _requestedShadowTime = animationSeconds;
        _requestedBulkWindMeters = CurrentBulkWind();
        _requestedShadowSpanMeters = halfExtent * 2f;
        _lastShadowRealMsec = now;
        _shadowDirty = false;
        _shadowPending = true;
        _effect.Call("request_gpu_cloud_shadow_update", 0);
    }
    /// <summary>
    /// Texture2DRD wrappers are replaced when Sunshine rebuilds on a viewport
    /// resize. Poll every menu frame: the old wrapper can be retired even while
    /// no menu-issued cascade request is pending.
    /// </summary>
    private void RefreshPublishedShadow(double animationSeconds)
    {
        int dispatch = DispatchCount();
        Texture2D? current = GetProviderShadowTexture();
        // Edits can arrive while a request is in flight or after its texture was
        // produced but before publication. Retire that request without exposing
        // its stale settings, then coalesce the latest edit at the configured Hz.
        if (_shadowDirty)
        {
            if (_shadowPending && dispatch >= _requestTarget) _shadowPending = false;
            ClearPublishedShadow();
            return;
        }
        if (current is null)
        {
            ClearPublishedShadow();
            return;
        }

        if (_shadowPending)
        {
            // Keep an existing valid wrapper while the requested replacement is
            // computing, but never expose a new wrapper with old map metadata.
            if (dispatch < _requestTarget) return;
            _publishedShadow = current;
            _publishedDispatch = dispatch;
            ShadowCenterCanonicalMeters = _requestedCenter;
            _publishedShadowTime = _requestedShadowTime;
            _publishedBulkWindMeters = _requestedBulkWindMeters;
            _publishedShadowSpanMeters = _requestedShadowSpanMeters;
            _shadowPending = false;
            NoteWrapper(current);
            return;
        }

        // Automatic generations retain the configured cascade center, but
        // resample the current wind. Restart advection from this publication.
        if (!ReferenceEquals(_publishedShadow, current))
        {
            _publishedShadow = current;
            _publishedDispatch = dispatch;
            _publishedShadowTime = animationSeconds;
            _publishedBulkWindMeters = CurrentBulkWind();
            _publishedShadowSpanMeters = ShadowSpanForRadius();
            NoteWrapper(current);
        }
    }

    private void InvalidateShadowPublication()
    {
        ClearPublishedShadow();
        _shadowDirty = true;
        if (_effect?.HasMethod("invalidate_gpu_cloud_shadow_publication") == true)
            _effect.Call("invalidate_gpu_cloud_shadow_publication");
    }

    private void ClearPublishedShadow()
    {
        if (_publishedShadow is not null) _shadowWrapperSerial++;
        _publishedShadow = null;
        _publishedWrapperInstanceId = 0;
        _publishedShadowSpanMeters = 0f;
        ShadowWindOffsetMeters = Vector2.Zero;
    }

    private Texture2D? GetProviderShadowTexture() => _effect?.HasMethod("get_gpu_cloud_shadow_texture") == true
        ? _effect.Call("get_gpu_cloud_shadow_texture", 0).AsGodotObject() as Texture2D
        : null;

    private Vector2 CurrentBulkWind() => new(_bulkWindMeters.X, _bulkWindMeters.Z);
    private float ShadowHalfMeters() => _shadowRadiusKm * 1000f;
    private float ShadowAheadMeters() => ShadowHalfMeters() * .5f;
    private float ShadowSpanForRadius() => ShadowHalfMeters() * 2f;

    private void NoteWrapper(Texture2D texture)
    {
        ulong instanceId = texture.GetInstanceId();
        if (instanceId == _publishedWrapperInstanceId) return;
        _publishedWrapperInstanceId = instanceId;
        _shadowWrapperSerial++;
    }
    private int DispatchCount()
    {
        if (_effect is null || !_effect.HasMethod("get_gpu_cloud_shadow_status")) return 0;
        var status = _effect.Call("get_gpu_cloud_shadow_status").AsGodotDictionary();
        return status.TryGetValue("near_dispatches", out Variant value) ? value.AsInt32() : 0;
    }
    private int EffectCallbackSerial() => _effect is null ? 0 : _effect.Get("render_callback_serial").AsInt32();
    private void SetEffect(StringName property, Variant value) => _effect!.Set(property, value);
    private static Vector3 NormalizeSun(Vector3 direction) => direction.LengthSquared() > .000001f ? direction.Normalized() : new Vector3(-.4f, .75f, -.5f).Normalized();
    private static bool SetChanged(ref bool target, bool value) { if (target == value) return false; target = value; return true; }
    private static bool SetChanged(ref int target, int value) { if (target == value) return false; target = value; return true; }
    private static bool SetChanged(ref float target, float value) { if (Mathf.IsEqualApprox(target, value)) return false; target = value; return true; }
    private static bool ChangedHistoryTuning(ref float previous, float value)
    {
        bool changed = !float.IsFinite(previous) || !Mathf.IsEqualApprox(previous, value);
        previous = value;
        return changed;
    }
    private static int ClampShadowResolution(int value) => value <= 128 ? 128 : value <= 256 ? 256 : value <= 512 ? 512 : value <= 1024 ? 1024 : 2048;
    private static Texture2D CreateClearShadow()
    {
        using Image image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8); image.SetPixel(0, 0, Colors.Black); return ImageTexture.CreateFromImage(image);
    }
    public void Detach()
    {
        if (_world is not null && ReferenceEquals(_world.Compositor, _ownedCompositor)) _world.Compositor = _previousCompositor;
        // Stop scheduling callbacks before releasing the owned GPU generation.
        if (_ownedCompositor is not null)
        {
            var emptyEffects = new Godot.Collections.Array<CompositorEffect>();
            using Godot.Collections.Array emptyEffectsLifetime = (Godot.Collections.Array)emptyEffects;
            _ownedCompositor.CompositorEffects = emptyEffects;
        }
        if (_effect is not null && _ownedCompositor is not null)
        {
            _effect.Enabled = false;
            QueueRetirement(_effect, _ownedCompositor);
        }
        else
        {
            _effect?.Dispose();
            _ownedCompositor?.Dispose();
        }
        _clearShadow?.Dispose();
        _world = null; _ownedCompositor = null; _previousCompositor = null; _effect = null; _clearShadow = null; _publishedShadow = null;
        _configured = false; _shadowPending = false; _shadowDirty = true; _requestedDispatch = 0; _requestTarget = 0; _publishedDispatch = 0; _publishedWrapperInstanceId = 0; _shadowWrapperSerial = 0;
        _requestedShadowTime = 0; _publishedShadowTime = 0; _lastShadowRealMsec = 0;
        _requestedShadowSpanMeters = 0; _publishedShadowSpanMeters = 0;
        _lastWindAnimationTime = double.NaN; _bulkWindMeters = Vector3.Zero; _detailWindMeters = Vector3.Zero;
        _lastCloudFrameAround = double.NaN;
        _lastCameraAround = double.NaN;
        _lastHistoryFov = float.NaN; _lastHistoryPitch = float.NaN; _lastHistoryAltitude = float.NaN; _historyResetPending = false;
        _historyResetObservedCallbackSerial = 0; _historyResetCallbacksRemaining = 0;
        _cameraStationary = false;
        _shadowGenerationEnabled = true;
        _requestedCenter = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        ShadowCenterCanonicalMeters = Vector2.Zero;
        ShadowWindOffsetMeters = Vector2.Zero;
    }

    private static void QueueRetirement(CompositorEffect effect, Compositor compositor)
    {
        if (RenderingServer.GetRenderingDevice() is null)
        {
            effect.Dispose();
            compositor.Dispose();
            return;
        }
        RetiredGenerations.Add(new RetiredGeneration { Effect = effect, Compositor = compositor });
        if (_retirementHooked) return;
        _retirementHooked = true;
        RenderingServer.FramePostDraw += RetireAfterPostDraw;
    }

    private static void RetireAfterPostDraw()
    {
        for (int index = RetiredGenerations.Count - 1; index >= 0; index--)
        {
            RetiredGeneration retired = RetiredGenerations[index];
            if (--retired.RemainingPostDraws > 0) continue;
            RetiredGenerations.RemoveAt(index);
            // Sunshine itself uses this exact render-thread clear path from its
            // PREDELETE notification. The closure keeps only owned resources.
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                if (retired.Effect.HasMethod("clear_compute")) retired.Effect.Call("clear_compute");
                retired.Effect.Dispose();
                retired.Compositor.Dispose();
            }));
        }
        if (RetiredGenerations.Count != 0) return;
        RenderingServer.FramePostDraw -= RetireAfterPostDraw;
        _retirementHooked = false;
    }
    public override void _ExitTree() => Detach();
}
