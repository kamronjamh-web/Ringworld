using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>
/// Storm light (docs/storm-revamp-s0-storm-light-design.md, WO-2). Publishes the per-location storm level D = max(storm, heavy(coverage)) to every
/// receiver: the world-anchored macro weather map (R = coverage, G = storm, built by Sunshine) as the Texture2DArrayRD global
/// ringworld_storm_map, plus the camera-relative frame that turns a world position into map texels, so materials/ring_storm_light.gdshaderinc
/// can fetch D for any surface. D is the same value as the shader's g_ws_storm and as <see cref="_localStormFactor"/> at the camera; the CPU
/// mirror is <see cref="GetStormLevelAt"/>. valid = 0 (the map is off, the bridge is disabled or Sunshine has not published a build) makes
/// every receiver read D = 0, i.e. today's output.
/// Globals: ringworld_storm_map (sampler2DArray, Sunshine's front/back macro map), ringworld_storm_map_frame = (texel m, front layer,
/// heavy-overcast start coverage, storm-light layer = front + 4 holding (owned, front rain), or 0 when the map has none), ringworld_storm_map_size = (used width, used height, texture width, texture height) in texels,
/// ringworld_storm_frame (mat4: column 0 = around axis.xyz + camera canonical around relative to the map origin, column 1 = across axis.xyz +
/// camera canonical across relative to the map origin, column 2 = up axis.xyz + ring radius, column 3 = camera-ring centre in world),
/// ringworld_storm_light = (darkness strength, shadow-fade start, shadow-fade end, valid).
/// </summary>
public partial class RingSunshineCloudBridge
{
    /// <summary>Width of the storm shadow fade ramp above <see cref="StormShadowFade"/> (design: end = start + 0.35).</summary>
    public const float StormShadowFadeWidth = 0.35f;
    private const string StormMapGlobal = "ringworld_storm_map";
    private const string StormMapFrameGlobal = "ringworld_storm_map_frame";
    private const string StormMapSizeGlobal = "ringworld_storm_map_size";
    private const string StormFrameGlobal = "ringworld_storm_frame";
    private const string StormLightGlobal = "ringworld_storm_light";
    private static readonly StringName MacroMapValidName = "macro_map_valid";
    private static readonly StringName MacroMapTextureName = "macro_map_texture";
    private static readonly StringName MacroMapFrontLayerName = "macro_map_front_layer";
    private static readonly StringName MacroMapFrontOriginName = "macro_map_front_origin";
    private static readonly StringName MacroMapFrontTexelName = "macro_map_front_texel";
    private static readonly StringName MacroMapFrontSizeName = "macro_map_front_size";
    private static readonly StringName MacroMapFrontCircumferenceName = "macro_map_front_circumference";
    private static readonly StringName MacroMapBuildsCompletedName = "macro_map_builds_completed";
    private static readonly Vector2I MacroMapCapacity = new(1024, 512); // MACRO_MAP_CAPACITY in SunshineClouds.gd

    [ExportGroup("Storm light")]
    /// <summary>How dark heavy storms make the ground and the air under them (scales the ambient term 1 - 0.9 * strength * D, eye adaptation never brightens a storm). 0 = no darkening.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float StormDarkness { get; set; } = 0.9f;
    /// <summary>Storm level at which sun shadows start to fade out; they are gone at this + 0.35. Receivers read D from the macro weather map.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float StormShadowFade { get; set; } = 0.35f;

    private Texture2DArrayRD? _stormMapRd;
    private static RingSunshineCloudBridge? _stormMapGlobalOwner;
    // Exit-only retirement; provider deactivation just publishes invalid lighting and may be reversed.
    private volatile bool _stormLightReleased;
    private Rid _stormMapPlaceholder;
    private Rid _stormMapAssigned;
    private bool _stormMapAssignPending;
    private bool _stormMapGlobalBound;
    private volatile bool _stormMapRidReady;
    private long _stormBuildsSeen = -1;
    private bool _stormFrontKnown;
    private Vector2 _stormFrontOrigin;
    private float _stormFrontTexel = 1000.0f;
    private Vector2I _stormFrontSize;
    private int _stormFrontLayer;
    private double _stormFrontCircumference;
    private bool _stormPublishedValid;
    private bool _stormFramePublished;
    private Projection _stormPublishedFrame;
    private Vector4 _stormPublishedLight, _stormPublishedMapFrame, _stormPublishedMapSize;
    /// <summary>Last published validity of the storm-light globals (test and diagnostics hook).</summary>
    public bool StormLightValid => _stormPublishedValid;

    /// <summary>
    /// CPU storm level D at a canonical position: the C# mirror of ring_ws_apply_macro's g_ws_storm (max of the storm term and the heavy-overcast
    /// term of the coverage). Uses the uploaded camera-independent systems, so it is meaningful only while CameraIndependentClouds is on.
    /// </summary>
    public float GetStormLevelAt(double aroundMeters, double acrossMeters)
    {
        if (_weather == null || _grid == null) return 0.0f;
        double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        double around = WrapCircumference(aroundMeters, circumference);
        double time = _weather.CloudSimulationTimeSeconds;
        Vector2 wind = _weather.WeatherWindMetersPerSecond;
        EvaluateCameraIndependentMacro(around, acrossMeters, time, circumference, wind, out float coverage, out float storm);
        float heavy = SmoothUnit((coverage - HeavyOvercastStartCoverage) / Mathf.Max(0.01f, 1.0f - HeavyOvercastStartCoverage));
        if (!_stormOwnershipAvailable) return Mathf.Max(storm, heavy);
        EvaluateStormLightTerms(around, acrossMeters, time, circumference, wind, coverage, GetPrecipitationPotentialAt(around, acrossMeters),
            out float owned, out float front, out float influence);
        return StormOwnedLevel(storm, heavy, influence, owned, front);
    }

    // ---- Storm-owned D (2026-10-29; S0 doc "Storm-owned D", user decision "fronts dark by rain") ----
    // D = max(owned x max(storm, heavy), front, heavy x (1 - smoothstep(0, 0.25, influence))). owned: the S6 ownership weight of StormTower / Cumulonimbus sculpt
    // systems (1 inside 1.3 ellipse radii, 0 by 1.45); front: a non-sculpt MajorFront / ExceptionalFront's own precipitation through the StormFrontRain ramp;
    // influence: the macro system influence (map A): plain heavy overcast counts only where no system is present. Mirrors RingMacroWeatherMap.glsl (storm-light layer) and ring_storm_owned_level (materials/ring_storm_light.gdshaderinc).
    /// <summary>Front rain ramp of the storm-owned D: a non-storm front's own precipitation potential maps to D through smoothstep(start, start + width). Mirrors RING_STORM_FRONT_RAIN_* in RingMacroWeatherMap.glsl.</summary>
    public const float StormFrontRainStart = 0.25f;
    public const float StormFrontRainWidth = 0.40f;
    private const float StormOwnedInnerSquared = 1.69f;                                        // 1.3 ellipse radii (ring_ws_storm_owned_weight)
    private const float StormOwnedSupportSquared = 1.45f * 1.45f;                              // RING_STORM_SUPPORT
    private const string StormLightLayerOffsetName = "macro_map_storm_light_layer_offset";     // SunshineClouds.gd; 0 / missing = the map has no storm-light layer
    private bool _stormOwnershipAvailable;
    private int _stormLightLayerOffset;
    private float _ciSmoothedFront, _ciSmoothedHeavyUnowned;

    /// <summary>The storm-owned D from its terms (GPU twin: ring_storm_owned_level). owned = 1, front = 0 is exactly the pre-ownership max(storm, heavy).</summary>
    public static float StormOwnedLevel(float storm, float heavy, float influence, float owned, float front)
        => Mathf.Max(Mathf.Max(owned * Mathf.Max(storm, heavy), front), heavy * SystemFreeWeight(influence));

    /// <summary>Weight of the plain heavy-overcast term: 1 with no system present, 0 once the map's system influence reaches 0.25 (GPU: RING_STORM_SYSTEM_FREE_INFLUENCE).</summary>
    public const float StormSystemFreeInfluence = 0.25f;
    private static float SystemFreeWeight(float influence) => 1.0f - SmoothUnit(influence / StormSystemFreeInfluence);

    /// <summary>Terms of the CPU storm-owned D at a canonical position (diagnostics and captures): coverage, storm (G), heavy, owned, front, influence, D.</summary>
    public Godot.Collections.Dictionary GetStormLightTermsAt(double aroundMeters, double acrossMeters)
    {
        var result = new Godot.Collections.Dictionary();
        if (_weather == null || _grid == null) return result;
        double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        double around = WrapCircumference(aroundMeters, circumference);
        double time = _weather.CloudSimulationTimeSeconds;
        Vector2 wind = _weather.WeatherWindMetersPerSecond;
        EvaluateCameraIndependentMacro(around, acrossMeters, time, circumference, wind, out float coverage, out float storm);
        float heavy = SmoothUnit((coverage - HeavyOvercastStartCoverage) / Mathf.Max(0.01f, 1.0f - HeavyOvercastStartCoverage));
        float precipitation = GetPrecipitationPotentialAt(around, acrossMeters);
        EvaluateStormLightTerms(around, acrossMeters, time, circumference, wind, coverage, precipitation, out float owned, out float front, out float influence);
        result["precipitation"] = precipitation;
        result["coverage"] = coverage; result["storm"] = storm; result["heavy"] = heavy; result["owned"] = owned; result["front"] = front;
        result["influence"] = influence; result["ownership_available"] = _stormOwnershipAvailable; result["D"] = GetStormLevelAt(aroundMeters, acrossMeters);
        return result;
    }

    /// <summary>Storm-map layer that holds (owned, front rain) for the receivers (front layer + offset), 0 while the map has no such layer or storm light is invalid.</summary>
    public float StormLightLayer => _stormPublishedValid ? _stormPublishedMapFrame.W : 0.0f;

    /// <summary>
    /// C# twin of the macro map build's storm-light layer (RingMacroWeatherMap.glsl ring_storm_light_point) plus the map's system influence (A), at one canonical
    /// position over the uploaded systems. `coverage` is the macro coverage at that point (the build's value.x), which gates a natural front's precipitation;
    /// `precipitation` the point's precipitation potential (map B, <see cref="GetPrecipitationPotentialAt"/>), which caps the front's own rain.
    /// </summary>
    private void EvaluateStormLightTerms(double aroundMeters, double acrossMeters, double time, double circumference, Vector2 wind, float coverage,
        float precipitation, out float owned, out float front, out float influence)
    {
        double sourceAround = WrapCircumference(aroundMeters - wind.X * time, circumference);
        double sourceAcross = acrossMeters - wind.Y * time;
        float coverageRamp = SmoothUnit((coverage - 0.35f) / 0.55f);
        float combined = 0.0f, manual = 0.0f, frontRain = 0.0f;
        owned = 0.0f;
        foreach (UploadedWeatherSystem entry in _ciUploaded)
        {
            RingVolumetricCloudLayer.RenderWeatherSystem system = entry.System;
            bool isStatic = system.IsStatic;
            double deltaAround = isStatic ? ShortestCircumferenceDelta(system.CenterAroundMeters, aroundMeters, circumference)
                : ShortestCircumferenceDelta(sourceAround, system.CenterAroundMeters, circumference);
            double deltaAcross = isStatic ? acrossMeters - system.CenterAcrossMeters : system.CenterAcrossMeters - sourceAcross;
            double along = system.OrientationCosine * deltaAround + system.OrientationSine * deltaAcross;
            double across = -system.OrientationSine * deltaAround + system.OrientationCosine * deltaAcross;
            double major = Math.Max(1.0, system.MajorRadiusMeters), minor = Math.Max(1.0, system.MinorRadiusMeters);
            float normalizedSquared = (float)(along * along / (major * major) + across * across / (minor * minor));
            if (system.Sculpts)
                owned = Mathf.Max(owned, 1.0f - SmoothUnit((normalizedSquared - StormOwnedInnerSquared) / (StormOwnedSupportSquared - StormOwnedInnerSquared)));
            float systemInfluence;
            if (isStatic)
            {
                systemInfluence = (1.0f - SmoothUnit((MathF.Sqrt(normalizedSquared) - 0.55f) / 0.75f)) * system.Influence * entry.Fade;
                if (systemInfluence <= 0.0001f) systemInfluence = 0.0f;
                manual = Mathf.Max(manual, systemInfluence);
            }
            else
            {
                systemInfluence = normalizedSquared >= 1.0f ? 0.0f : (1.0f - SmoothUnit((MathF.Sqrt(normalizedSquared) - 0.32f) / 0.68f)) * system.Influence * entry.Fade;
                combined = 1.0f - (1.0f - combined) * (1.0f - systemInfluence);
            }
            if (system.IsFront && !system.Sculpts)
                frontRain = Mathf.Max(frontRain, system.Precipitation * systemInfluence * (isStatic ? 1.0f : coverageRamp));
        }
        influence = Mathf.Max(Mathf.Clamp(combined, 0.0f, 1.0f), manual);
        front = SmoothUnit((Mathf.Min(frontRain, precipitation) - StormFrontRainStart) / StormFrontRainWidth);
    }

    /// <summary>
    /// The camera storm level (<see cref="_localStormFactor"/>) from the smoothed terms, called by UpdateCameraIndependentAmbient after it has smoothed the storm and
    /// heavy terms with `blend`. The front and system-free heavy terms are smoothed on their own; the ownership weight is NOT smoothed (it is a spatial weight that
    /// already ramps over 0.15 ellipse radii), so wherever owned is 1 this is exactly the old max(smoothed storm, smoothed heavy), transients included.
    /// </summary>
    private float SmoothCameraStormLevel(in RingCanonicalPosition camera, double time, double circumference, Vector2 wind, float coverage, float heavy, float blend)
    {
        if (!_stormOwnershipAvailable) return Mathf.Max(_ciSmoothedStorm, _ciSmoothedHeavy);
        EvaluateStormLightTerms(camera.AroundMeters, camera.AcrossMeters, time, circumference, wind, coverage, GetPrecipitationPotentialAt(camera.AroundMeters, camera.AcrossMeters),
            out float owned, out float front, out float influence);
        _ciSmoothedFront = Mathf.Lerp(_ciSmoothedFront, front, blend);
        _ciSmoothedHeavyUnowned = Mathf.Lerp(_ciSmoothedHeavyUnowned, heavy * SystemFreeWeight(influence), blend);
        return Mathf.Max(Mathf.Max(owned * Mathf.Max(_ciSmoothedStorm, _ciSmoothedHeavy), _ciSmoothedFront), _ciSmoothedHeavyUnowned);
    }

    /// <summary>True while <see cref="GetPrecipitationPotentialAt"/> reads the uploaded camera-independent systems (CameraIndependentClouds on and uploaded).</summary>
    public bool PrecipitationMirrorAvailable => _weather != null && _grid != null && CameraIndependentClouds && _ciUploadedEnabled;

    /// <summary>
    /// CPU precipitation potential at a canonical position: the C# mirror of the macro map's B channel (and of RingVolumetricCloudLayer.QueryLocalWeather's PrecipitationPotential,
    /// same product strongest-precip x combined influence x smoothstep of coverage, then the static systems folded by max). A few microseconds against QueryLocalWeather's ~150;
    /// the local rain field (S1 streaks) reads it per grid cell. Meaningful only while <see cref="PrecipitationMirrorAvailable"/>.
    /// </summary>
    public float GetPrecipitationPotentialAt(double aroundMeters, double acrossMeters)
    {
        if (_weather == null || _grid == null) return 0.0f;
        double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        double time = _weather.CloudSimulationTimeSeconds;
        aroundMeters = WrapCircumference(aroundMeters, circumference);
        Vector2 wind = _weather.WeatherWindMetersPerSecond;
        double sourceAround = WrapCircumference(aroundMeters - wind.X * time, circumference);
        double sourceAcross = acrossMeters - wind.Y * time;
        float combined = 0.0f, strongest = 0.0f, strongestDensity = 0.0f, strongestPrecipitation = 0.0f;
        foreach (UploadedWeatherSystem entry in _ciUploaded)
        {
            RingVolumetricCloudLayer.RenderWeatherSystem system = entry.System;
            if (system.IsStatic) continue;
            double deltaAround = ShortestCircumferenceDelta(sourceAround, system.CenterAroundMeters, circumference);
            double deltaAcross = system.CenterAcrossMeters - sourceAcross;
            double major = system.MajorRadiusMeters;
            if (deltaAround * deltaAround + deltaAcross * deltaAcross >= major * major) continue;
            double along = system.OrientationCosine * deltaAround + system.OrientationSine * deltaAcross;
            double across = -system.OrientationSine * deltaAround + system.OrientationCosine * deltaAcross;
            double minor = system.MinorRadiusMeters;
            double normalizedSquared = along * along / Math.Max(1.0, major * major) + across * across / Math.Max(1.0, minor * minor);
            if (normalizedSquared >= 1.0) continue;
            float influence = 1.0f - SmoothUnit(((float)Math.Sqrt(normalizedSquared) - 0.32f) / 0.68f);
            float weighted = influence * system.Influence * entry.Fade;
            combined = 1.0f - (1.0f - combined) * (1.0f - weighted);
            if (weighted <= strongest) continue;
            strongest = weighted;
            strongestDensity = system.Influence;
            strongestPrecipitation = system.Precipitation;
        }
        combined = Mathf.Clamp(combined, 0.0f, 1.0f);
        strongest = Mathf.Clamp(strongest, 0.0f, 1.0f);
        float cluster = _weather.SampleContinuousClusterTerm(aroundMeters, acrossMeters, time);
        float cloudCoverage = Mathf.Clamp((_weather.Coverage) + SunshineBaseCoverageOffset + cluster + (combined - 0.28f) * 0.46f + strongest * (strongestDensity - 0.5f) * 0.24f, 0.015f, 0.985f);
        float precipitation = Mathf.Clamp(strongestPrecipitation * combined * SmoothUnit((cloudCoverage - 0.35f) / 0.55f), 0.0f, 1.0f);
        foreach (UploadedWeatherSystem entry in _ciUploaded)
        {
            RingVolumetricCloudLayer.RenderWeatherSystem system = entry.System;
            if (!system.IsStatic) continue;
            double deltaAround = ShortestCircumferenceDelta(system.CenterAroundMeters, aroundMeters, circumference);
            double deltaAcross = acrossMeters - system.CenterAcrossMeters;
            double x = deltaAround * system.OrientationCosine + deltaAcross * system.OrientationSine;
            double z = -deltaAround * system.OrientationSine + deltaAcross * system.OrientationCosine;
            float normalized = (float)Math.Sqrt(x * x / ((double)system.MajorRadiusMeters * system.MajorRadiusMeters) + z * z / ((double)system.MinorRadiusMeters * system.MinorRadiusMeters));
            float influence = (1.0f - SmoothUnit((normalized - 0.55f) / 0.75f)) * system.Influence * entry.Fade;
            if (influence <= 0.0001f) continue;
            precipitation = Mathf.Max(precipitation, system.Precipitation * influence);
        }
        return precipitation;
    }

    private void PublishStormLight(bool forceInvalid = false)
    {
        if (_stormLightReleased) return;
        if (!forceInvalid) EnsureStormMapGlobal();
        if (_stormMapGlobalOwner != null && !ReferenceEquals(_stormMapGlobalOwner, this)) return;
        _stormLightLayerOffset = _clouds != null && GodotObject.IsInstanceValid(_clouds) ? _clouds.Get(StormLightLayerOffsetName).AsInt32() : 0;
        _stormOwnershipAvailable = _stormLightLayerOffset > 0;
        bool valid = !forceInvalid && Enabled && CameraIndependentClouds && MacroWeatherMap && _clouds != null && GodotObject.IsInstanceValid(_clouds)
            && _explorer != null && _grid != null && _clouds.Get(MacroMapValidName).AsBool();
        if (valid) valid = RefreshStormFront();
        if (valid)
        {
            RingLodGeometrySnapshot geometry = _grid!.CaptureLodGeometrySnapshot();
            RingCanonicalPosition camera = _explorer!.CanonicalPosition;
            Vector3 center = geometry.CanonicalToWorld(camera.AroundMeters, 0.0, camera.AcrossMeters);
            Vector3 around = (geometry.CanonicalToWorld(camera.AroundMeters + 10.0, 0.0, camera.AcrossMeters) - center).Normalized();
            Vector3 across = (geometry.CanonicalToWorld(camera.AroundMeters, 0.0, camera.AcrossMeters + 10.0) - center).Normalized();
            Vector3 up = (geometry.CanonicalToWorld(camera.AroundMeters, 10.0, camera.AcrossMeters) - center).Normalized();
            double circumference = _stormFrontCircumference > 1.0 ? _stormFrontCircumference : Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
            double relativeAround = camera.AroundMeters - _stormFrontOrigin.X;
            relativeAround -= circumference * Math.Floor(relativeAround / circumference);
            float relativeAcross = (float)(camera.AcrossMeters - _stormFrontOrigin.Y);
            var frame = new Projection(
                new Vector4(around.X, around.Y, around.Z, (float)relativeAround),
                new Vector4(across.X, across.Y, across.Z, relativeAcross),
                new Vector4(up.X, up.Y, up.Z, ResolveShadowRingRadiusMeters()),
                new Vector4(center.X, center.Y, center.Z, 0.0f));
            if (!_stormFramePublished || !frame.Equals(_stormPublishedFrame))
            {
                _stormPublishedFrame = frame;
                _stormFramePublished = true;
                RenderingServer.GlobalShaderParameterSet(StormFrameGlobal, frame);
            }
            var mapFrame = new Vector4(_stormFrontTexel, _stormFrontLayer, Mathf.Clamp(HeavyOvercastStartCoverage, 0.0f, 0.99f),
                _stormOwnershipAvailable ? _stormFrontLayer + _stormLightLayerOffset : 0.0f);
            var mapSize = new Vector4(_stormFrontSize.X, _stormFrontSize.Y, MacroMapCapacity.X, MacroMapCapacity.Y);
            if (mapFrame != _stormPublishedMapFrame) { _stormPublishedMapFrame = mapFrame; RenderingServer.GlobalShaderParameterSet(StormMapFrameGlobal, mapFrame); }
            if (mapSize != _stormPublishedMapSize) { _stormPublishedMapSize = mapSize; RenderingServer.GlobalShaderParameterSet(StormMapSizeGlobal, mapSize); }
        }
        float fadeStart = Mathf.Clamp(StormShadowFade, 0.0f, 1.0f);
        var light = new Vector4(Mathf.Clamp(StormDarkness, 0.0f, 1.0f), fadeStart, fadeStart + StormShadowFadeWidth, valid ? 1.0f : 0.0f);
        if (light != _stormPublishedLight)
        {
            _stormPublishedLight = light;
            RenderingServer.GlobalShaderParameterSet(StormLightGlobal, light);
        }
        _stormPublishedValid = valid;
        RingStormLightTint.Apply(GetStormAmbientAtCamera());
        RingStormLightTint.ApplySunFade(_stormPublishedValid ? SmoothUnit((GetCameraStormLevel() - Mathf.Clamp(StormShadowFade, 0.0f, 1.0f)) / StormShadowFadeWidth) * Mathf.Clamp(StormDarkness, 0.0f, 1.0f) : 0.0f);
        UpdateStormCloudLighting();
    }

    // Sunshine's clouds cannot read the storm map (compute profile), so the sun the clouds are lit with fades by the shadow fade F(D at the camera) like every
    // receiver's sun, scaled by the darkness strength: 1 outside a storm, 1 - strength under a heavy one. Smoothed with the lighting response speed.
    // Eye adaptation only gives up this share of its range under a heavy storm (it still never rises above its clear maximum).
    private const float StormEyeCapScale = 0.9f;
    private float _stormCloudDirectFactor = 1.0f;

    private void UpdateStormCloudLighting()
    {
        float target = 1.0f;
        if (_stormPublishedValid) target = 1.0f - SmoothUnit((GetCameraStormLevel() - Mathf.Clamp(StormShadowFade, 0.0f, 1.0f)) / StormShadowFadeWidth) * Mathf.Clamp(StormDarkness, 0.0f, 1.0f);
        float blend = 1.0f - Mathf.Exp(-Mathf.Max(0.1f, LightingResponseSpeed) * (float)GetProcessDeltaTime());
        float next = Mathf.Lerp(_stormCloudDirectFactor, target, blend);
        if (Mathf.Abs(next - 1.0f) < 0.002f) next = 1.0f;
        if (next == _stormCloudDirectFactor) return;
        _stormCloudDirectFactor = next;
        ApplyCloudAppearanceScalarsToResource();
    }

    /// <summary>Storm level D at the camera (the CPU value the receivers' map fetch reproduces): the local storm factor of whichever weather path is live.</summary>
    public float GetCameraStormLevel() => Enabled ? Mathf.Clamp(_localStormFactor, 0.0f, 1.0f) : 0.0f;

    /// <summary>True while the storm-light globals carry a published map (receivers read D); false = every receiver reads D = 0.</summary>
    public bool IsStormLightValid() => _stormPublishedValid;

    /// <summary>Ambient multiplier A(D) = 1 - 0.55 * strength * D at the camera (1 while storm light is invalid). Shared with the StandardMaterial3D tint.</summary>
    public float GetStormAmbientAtCamera()
        => _stormPublishedValid ? 1.0f - 0.55f * Mathf.Clamp(StormDarkness, 0.0f, 1.0f) * GetCameraStormLevel() : 1.0f;

    /// <summary>How far eye adaptation is pulled from its maximum sensitivity toward its minimum: shadow-fade F(D at the camera) times the darkness strength (0 while invalid).</summary>
    public float GetStormEyeCap()
    {
        if (!_stormPublishedValid) return 0.0f;
        float start = Mathf.Clamp(StormShadowFade, 0.0f, 1.0f);
        float fade = SmoothUnit((GetCameraStormLevel() - start) / StormShadowFadeWidth);
        return fade * Mathf.Clamp(StormDarkness, 0.0f, 1.0f) * StormEyeCapScale;
    }

    /// <summary>
    /// Read-only description of the published macro weather map for compute consumers (the S2 wet map): the Sunshine map texture array RID (RGBA16F, R coverage,
    /// G storm, B precipitation potential, A influence), the canonical (around, across) origin of the front layer, texel size, used size in texels, texture
    /// capacity, front layer, ring circumference and the heavy-overcast start coverage. False until the storm-light globals carry a valid map.
    /// </summary>
    public bool TryGetStormMapDescription(out Rid texture, out Vector2 originMeters, out float texelMeters, out Vector2I usedSize, out Vector2I capacity,
        out int frontLayer, out double circumferenceMeters, out float heavyStartCoverage)
    {
        texture = _stormMapAssigned;
        originMeters = _stormFrontOrigin;
        texelMeters = _stormFrontTexel;
        usedSize = _stormFrontSize;
        capacity = MacroMapCapacity;
        frontLayer = _stormFrontLayer;
        circumferenceMeters = _stormFrontCircumference;
        heavyStartCoverage = Mathf.Clamp(HeavyOvercastStartCoverage, 0.0f, 0.99f);
        return _stormPublishedValid && _stormFrontKnown && texture.IsValid;
    }

    /// <summary>
    /// Read-only copy of what the ringworld_storm_* shader globals currently hold, for raster/compute passes that cannot read globals (the S1 rain streak pass): the macro map
    /// RID plus ringworld_storm_frame, ringworld_storm_map_frame, ringworld_storm_map_size and ringworld_storm_light, so ring_storm_light.gdshaderinc math can be mirrored in GLSL.
    /// False (and the light valid flag stays 0) until the globals carry a valid map.
    /// </summary>
    public bool TryGetStormShaderState(out Rid texture, out Projection frame, out Vector4 mapFrame, out Vector4 mapSize, out Vector4 light)
    {
        texture = _stormMapAssigned; frame = _stormPublishedFrame; mapFrame = _stormPublishedMapFrame; mapSize = _stormPublishedMapSize; light = _stormPublishedLight;
        return _stormPublishedValid && _stormFramePublished && _stormFrontKnown && texture.IsValid;
    }

    /// <summary>World position of a canonical point, for the storm-light probes (CPU D vs shader D).</summary>
    public Vector3 GetStormProbeWorldPosition(double aroundMeters, double verticalMeters, double acrossMeters)
        => _grid == null ? Vector3.Zero : _grid.CaptureLodGeometrySnapshot().CanonicalToWorld(aroundMeters, verticalMeters, acrossMeters);

    /// <summary>Re-reads the front map description when Sunshine publishes a new build; false until a build exists and the texture is bound.</summary>
    private bool RefreshStormFront()
    {
        if (_stormLightReleased) return false;
        long builds = _clouds!.Get(MacroMapBuildsCompletedName).AsInt64();
        if (!_stormFrontKnown || builds != _stormBuildsSeen)
        {
            _stormBuildsSeen = builds;
            _stormFrontOrigin = _clouds.Get(MacroMapFrontOriginName).AsVector2();
            _stormFrontTexel = (float)_clouds.Get(MacroMapFrontTexelName).AsDouble();
            _stormFrontSize = _clouds.Get(MacroMapFrontSizeName).AsVector2I();
            _stormFrontLayer = (int)_clouds.Get(MacroMapFrontLayerName).AsInt64();
            _stormFrontCircumference = _clouds.Get(MacroMapFrontCircumferenceName).AsDouble();
            _stormFrontKnown = _stormFrontSize.X > 0 && _stormFrontSize.Y > 0;
        }
        if (!_stormFrontKnown) return false;
        Rid map = _clouds.Get(MacroMapTextureName).AsRid();
        if (!map.IsValid) return false;
        if (map != _stormMapAssigned && !_stormMapAssignPending && _stormMapRd != null)
        {
            _stormMapAssignPending = true;
            Texture2DArrayRD target = _stormMapRd;
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                if (_stormLightReleased || !ReferenceEquals(_stormMapRd, target)) return;
                if (GodotObject.IsInstanceValid(target)) target.TextureRdRid = map;
                _stormMapAssigned = map;
                _stormMapAssignPending = false;
            }));
        }
        return map == _stormMapAssigned && _stormMapGlobalBound;
    }

    /// <summary>Registers the Texture2DArrayRD global once and points it at a 1x1x2 placeholder: a Texture2DArrayRD without an RID falls back to a plain 2D texture, which would invalidate every material uniform set that bound the 2DArray global.</summary>
    private void EnsureStormMapGlobal()
    {
        if (_stormLightReleased) return;
        // A Texture2DArrayRD without an RID resolves to a 2D texture, and every material uniform set built while the global holds it fails with
        // "Texture needs to be 2DArray, actual 2D". The global therefore stays on its project default until the render thread has given the
        // texture its placeholder (or the real map) RID, and is only then pointed at the texture.
        if (_stormMapRd != null)
        {
            if ((!_stormMapGlobalBound || _stormMapGlobalOwner == null) && _stormMapRidReady)
            {
                _stormMapGlobalBound = true;
                RenderingServer.GlobalShaderParameterSet(StormMapGlobal, _stormMapRd);
                _stormMapGlobalOwner = this;
            }
            return;
        }
        _stormMapRd = new Texture2DArrayRD();
        _stormMapGlobalBound = false;
        _stormMapRidReady = false;
        Texture2DArrayRD target = _stormMapRd;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            RenderingDevice? main = RenderingServer.GetRenderingDevice();
            if (main == null || _stormLightReleased || !ReferenceEquals(_stormMapRd, target) || !GodotObject.IsInstanceValid(target) || _stormMapPlaceholder.IsValid) return;
            var format = new RDTextureFormat
            {
                Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat, Width = 1, Height = 1, Depth = 1, ArrayLayers = 2, Mipmaps = 1,
                TextureType = RenderingDevice.TextureType.Type2DArray, UsageBits = RenderingDevice.TextureUsageBits.SamplingBit,
            };
            _stormMapPlaceholder = main.TextureCreate(format, new RDTextureView(), new Godot.Collections.Array<byte[]> { new byte[8], new byte[8] });
            if (!_stormMapAssigned.IsValid) target.TextureRdRid = _stormMapPlaceholder;
            _stormMapRidReady = _stormMapPlaceholder.IsValid;
        }));
    }

    /// <summary>Unbinds this owner's global before retiring its wrapper/placeholder; Sunshine owns (and frees) the map texture itself.</summary>
    private void ReleaseStormLight()
    {
        if (_stormLightReleased) return;
        if (ReferenceEquals(_stormMapGlobalOwner, this))
        {
            PublishStormLight(forceInvalid: true);
            // Restore the typed engine fallback before retiring the wrapper and its owned placeholder.
            RenderingServer.GlobalShaderParameterSet(StormMapGlobal, Variant.From(default(Rid)));
            _stormMapGlobalOwner = null;
        }
        _stormLightReleased = true;
        Texture2DArrayRD? target = _stormMapRd;
        _stormMapRd = null;
        _stormMapRidReady = false;
        _stormMapGlobalBound = false;
        _stormPublishedValid = false;
        _stormMapAssignPending = false;
        if (target == null) return;
        _stormMapAssigned = default;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(target)) target.TextureRdRid = default;
            // Read after any already queued initializer, which may still have created the placeholder at release time.
            Rid placeholder = _stormMapPlaceholder;
            _stormMapPlaceholder = default;
            _stormMapRidReady = false;
            _stormMapAssigned = default;
            _stormMapAssignPending = false;
            if (placeholder.IsValid && RenderingServer.GetRenderingDevice() is { } main) main.FreeRid(placeholder);
        }));
    }
}
