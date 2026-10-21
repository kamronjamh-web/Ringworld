using Godot;
using System.Collections.Generic;
using RingworldTechDemo.Shadows;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingGraphicsQualityController
{
    private static readonly IReadOnlyList<int> ShadowAtlasSizes = System.Array.AsReadOnly(new[] { 2048, 4096, 8192, 16384, 32768, 65536, 131072 });
    public static IReadOnlyList<int> DirectionalShadowAtlasSizes => ShadowAtlasSizes;
    public const int UnverifiedDirectionalShadowAtlasLimit = 16384;
    public const float DefaultNearCascadeMeters = 60.0f;
    public const float MinimumNearCascadeMeters = 10.0f;
    public const float MaximumNearCascadeMeters = 20000.0f;
    [Export] public NodePath DayNightCyclePath { get; set; } = new("../RingDayNightCycle");

    private readonly Dictionary<string, Variant> _shadowTuningOverrides = new();
    private bool _shadowBaselineCaptured;
    private float _shadowBaselineBias;
    private float _shadowBaselineNormalBias;
    private bool _shadowBaselineEnabled;
    private bool _shadowBaselineCycleCaptured;
    private bool _shadowBaselineSunStabilizationEnabled;
    private float _shadowBaselineSunStepDegrees;
    private global::RingworldTechDemo.RingDayNightCycle? _dayNightCycle;
    private int _lastAppliedShadowAtlas = -1;
    private int _lastAppliedShadowDepthBits = -1;
    private int _lastAppliedShadowFilter = -1;
    private bool _macroShadowBaselineCaptured;
    private bool _macroShadowBaselineEnabled;
    private bool _macroShadowBaselineWallShadowsEnabled;
    private bool _macroShadowOptionsApplied;
    private bool _macroShadowOptionsInitialized;
    private bool _lastAppliedMacroShadowEnabled;
    private bool _lastAppliedMacroWallShadowsEnabled;
    private bool _shadowAtlasLimitResolved;
    private float _presetShadowDistanceMeters;
    private DirectionalLight3D.ShadowMode _shadowBaselineMode = DirectionalLight3D.ShadowMode.Parallel4Splits;
    private bool _planetShadowBaselineCaptured;
    private ulong _planetShadowBaselineAtmosphereId;
    private bool _planetShadowBaselineEnabled = true;
    private float _planetShadowBaselineDistanceMeters = 6000.0f;
    private int _planetShadowBaselineCascades = 4;
    private float _planetShadowBaselineFadeRatio = 0.85f;
    private bool _planetFadeRatioTouched, _planetSplitSaved;
    private float _planetSplitBaseline;
    public const float MinimumShadowDistanceMeters = 150.0f;
    public const float MaximumShadowDistanceMeters = 20000.0f;
    public const float MinimumPlanetShadowDistanceMeters = 100.0f;
    public const float MaximumPlanetShadowDistanceMeters = 20000.0f;
    public int EffectiveSunCascadeCount => _sun?.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel2Splits ? 2 : 4;
    public bool EffectivePlanetShadowsEnabled => GetDaySideAtmosphere() is { } atmosphere ? (bool)atmosphere.Get("planet_light_cast_shadows") : _planetShadowBaselineEnabled;
    public float EffectivePlanetShadowDistanceMeters => GetDaySideAtmosphere() is { } atmosphere ? (float)atmosphere.Get("planet_light_shadow_distance_meters") : _planetShadowBaselineDistanceMeters;
    public int EffectivePlanetCascadeCount => GetDaySideAtmosphere() is { } atmosphere ? (int)atmosphere.Get("planet_light_shadow_cascades") : _planetShadowBaselineCascades;
    public float EffectiveShadowDistanceMeters => _sun?.DirectionalShadowMaxDistance ?? 0.0f;
    /// <summary>The user/preset choice before the local nighttime handoff is applied.</summary>
    public bool RequestedSunShadowsEnabled { get; private set; } = true;
    public int RequestedDirectionalShadowAtlasSize { get; private set; }
    public int EffectiveDirectionalShadowAtlasSize { get; private set; }
    public int EffectiveDirectionalShadowDepthBits { get; private set; } = 16;
    public ulong EffectiveDirectionalShadowAtlasMinimumDepthBytes => DirectionalShadowAtlasMinimumDepthBytes(EffectiveDirectionalShadowAtlasSize, EffectiveDirectionalShadowDepthBits);
    public float EffectiveNearCascadeMeters => _sun == null ? DefaultNearCascadeMeters : _sun.DirectionalShadowSplit1 * _sun.DirectionalShadowMaxDistance;
    public int EffectiveDirectionalShadowFilter { get; private set; }
    public int DirectionalShadowAtlasHardwareLimit { get; private set; } = UnverifiedDirectionalShadowAtlasLimit;
    public bool DirectionalShadowAtlasLimitIsUnverified { get; private set; } = true;
    public bool EffectiveTerrainMacroShadowsEnabled => _globalLod?.EnableMacroTerrainShadows ?? true;
    public bool EffectiveTerrainMacroWallShadowsEnabled => _globalLod?.EnableMacroTerrainWallShadows ?? true;
    public float EffectiveTerrainMacroShadowStrength => _globalLod?.MacroTerrainShadowStrength ?? 0.0f;
    public float EffectiveTerrainMacroShadowFadeStartMeters => _globalLod?.MacroTerrainShadowFadeStartMeters ?? 0.0f;
    public float EffectiveTerrainMacroShadowFadeEndMeters => _globalLod?.MacroTerrainShadowFadeEndMeters ?? 0.0f;
    public int EffectiveTerrainMacroShadowTraceSteps => _globalLod?.MacroTerrainShadowTraceSteps ?? 1;
    public float EffectiveTerrainMacroShadowMaxTraceMeters => _globalLod?.MacroTerrainShadowMaxTraceMeters ?? 0.0f;

    /// <summary>Incremented whenever the override set changes; the height shadow node re-reads its settings when this moves.</summary>
    public int HeightShadowRevision { get; private set; }

    /// <summary>Height-shadow settings from the validated overrides (defaults for absent keys). HeightShadowL0TexelMeters and HeightShadowRangeMeters are two views of one choice: the last one set wins.</summary>
    public RingHeightShadowSettings ResolveHeightShadowSettings()
    {
        Dictionary<string, Variant> o = _shadowTuningOverrides;
        int texel = RingHeightShadowSettings.DefaultL0TexelMeters;
        if (o.TryGetValue("HeightShadowL0TexelMeters", out Variant texelValue)) texel = texelValue.AsInt32();
        else if (o.TryGetValue("HeightShadowRangeMeters", out Variant rangeValue))
        {
            int fromRange = RingHeightShadowSettings.TexelForRange(rangeValue.AsInt32());
            if (fromRange > 0) texel = fromRange;
        }
        return new RingHeightShadowSettings
        {
            Enabled = o.TryGetValue("HeightShadowEnabled", out Variant enabled) && enabled.AsBool(),
            L0TexelMeters = texel,
            Levels = o.TryGetValue("HeightShadowLevels", out Variant levels) ? levels.AsInt32() : RingHeightShadowSettings.DefaultLevels,
            SunStepDegrees = o.TryGetValue("HeightShadowSunStepDegrees", out Variant step) ? step.AsSingle() : RingHeightShadowSettings.DefaultSunStepDegrees,
            RowsPerFrame = o.TryGetValue("HeightShadowRowsPerFrame", out Variant rows) ? rows.AsInt32() : RingHeightShadowSettings.DefaultRowsPerFrame,
            PenumbraDegrees = o.TryGetValue("HeightShadowPenumbraDegrees", out Variant penumbra) ? penumbra.AsSingle() : RingHeightShadowSettings.DefaultPenumbraDegrees,
            CrossfadeSeconds = o.TryGetValue("HeightShadowCrossfadeSeconds", out Variant crossfade) ? crossfade.AsSingle() : RingHeightShadowSettings.DefaultCrossfadeSeconds,
            FillWorkers = o.TryGetValue("HeightShadowFillWorkers", out Variant workers) ? workers.AsInt32() : RingHeightShadowSettings.DefaultFillWorkers,
            BudgetMs = o.TryGetValue("HeightShadowBudgetMs", out Variant budget) ? budget.AsSingle() : RingHeightShadowSettings.DefaultBudgetMs,
            NearMapEnabled = !o.TryGetValue("HeightShadowNearMapEnabled", out Variant nearEnabled) || nearEnabled.AsBool(),
            NearMapDistanceMeters = o.TryGetValue("HeightShadowNearMapDistanceMeters", out Variant nearDistance) ? nearDistance.AsInt32() : RingHeightShadowSettings.DefaultNearMapDistanceMeters,
            PoiMapDistanceMeters = o.TryGetValue("HeightShadowPoiMapDistanceMeters", out Variant poiDistance) ? poiDistance.AsSingle() : RingHeightShadowSettings.DefaultPoiMapDistanceMeters,
            PlanetNearMapEnabled = !o.TryGetValue("HeightShadowPlanetNearMapEnabled", out Variant planetNearEnabled) || planetNearEnabled.AsBool(),
            PlanetNearMapDistanceMeters = o.TryGetValue("HeightShadowPlanetNearMapDistanceMeters", out Variant planetNearDistance) ? planetNearDistance.AsInt32() : RingHeightShadowSettings.DefaultPlanetNearMapDistanceMeters,
        }.Normalized();
    }
    public bool EffectiveHeightShadowEnabled => ResolveHeightShadowSettings().Enabled;
    public int EffectiveHeightShadowL0TexelMeters => ResolveHeightShadowSettings().L0TexelMeters;
    public int EffectiveHeightShadowRangeMeters => ResolveHeightShadowSettings().RangeMeters;
    public int EffectiveHeightShadowLevels => ResolveHeightShadowSettings().Levels;
    public float EffectiveHeightShadowSunStepDegrees => ResolveHeightShadowSettings().SunStepDegrees;
    public int EffectiveHeightShadowRowsPerFrame => ResolveHeightShadowSettings().RowsPerFrame;
    public float EffectiveHeightShadowPenumbraDegrees => ResolveHeightShadowSettings().PenumbraDegrees;
    public float EffectiveHeightShadowCrossfadeSeconds => ResolveHeightShadowSettings().CrossfadeSeconds;
    public int EffectiveHeightShadowFillWorkers => ResolveHeightShadowSettings().FillWorkers;
    public float EffectiveHeightShadowBudgetMs => ResolveHeightShadowSettings().BudgetMs;
    public bool EffectiveHeightShadowNearMapEnabled => ResolveHeightShadowSettings().NearMapEnabled;
    public int EffectiveHeightShadowNearMapDistanceMeters => ResolveHeightShadowSettings().NearMapDistanceMeters;
    public float EffectiveHeightShadowPoiMapDistanceMeters => ResolveHeightShadowSettings().PoiMapDistanceMeters;
    public bool EffectiveHeightShadowPlanetNearMapEnabled => ResolveHeightShadowSettings().PlanetNearMapEnabled;
    public int EffectiveHeightShadowPlanetNearMapDistanceMeters => ResolveHeightShadowSettings().PlanetNearMapDistanceMeters;
    public string EffectiveHeightShadowStatus => RingTerrainHeightShadow.Instance is { } node && GodotObject.IsInstanceValid(node) ? node.GetStatusText()
        : ResolveHeightShadowSettings().Enabled ? "Height shadows: starting." : "Height shadows: off.";

    /// <summary>Contact-shadow settings (S6) from the validated overrides; defaults for absent keys.</summary>
    public RingContactShadowSettings ResolveContactShadowSettings()
    {
        Dictionary<string, Variant> o = _shadowTuningOverrides;
        return new RingContactShadowSettings
        {
            Enabled = o.TryGetValue("ContactShadowEnabled", out Variant enabled) && enabled.AsBool(),
            LengthMeters = o.TryGetValue("ContactShadowLengthMeters", out Variant length) ? length.AsSingle() : RingContactShadowSettings.DefaultLengthMeters,
            ThicknessMeters = o.TryGetValue("ContactShadowThicknessMeters", out Variant thickness) ? thickness.AsSingle() : RingContactShadowSettings.DefaultThicknessMeters,
            Strength = o.TryGetValue("ContactShadowStrength", out Variant strength) ? strength.AsSingle() : RingContactShadowSettings.DefaultStrength,
            FadeMeters = o.TryGetValue("ContactShadowFadeMeters", out Variant fade) ? fade.AsSingle() : RingContactShadowSettings.DefaultFadeMeters,
            GrassStrength = o.TryGetValue("ContactShadowGrassStrength", out Variant grass) ? grass.AsSingle() : RingContactShadowSettings.DefaultGrassStrength,
            Steps = o.TryGetValue("ContactShadowSteps", out Variant steps) ? steps.AsInt32() : RingContactShadowSettings.DefaultSteps,
        }.Normalized();
    }
    public bool EffectiveContactShadowEnabled => ResolveContactShadowSettings().Enabled;
    public float EffectiveContactShadowLengthMeters => ResolveContactShadowSettings().LengthMeters;
    public float EffectiveContactShadowThicknessMeters => ResolveContactShadowSettings().ThicknessMeters;
    public float EffectiveContactShadowStrength => ResolveContactShadowSettings().Strength;
    public float EffectiveContactShadowFadeMeters => ResolveContactShadowSettings().FadeMeters;
    public float EffectiveContactShadowGrassStrength => ResolveContactShadowSettings().GrassStrength;
    public int EffectiveContactShadowSteps => ResolveContactShadowSettings().Steps;
    public string EffectiveContactShadowStatus => RingContactShadows.Instance is { } node && GodotObject.IsInstanceValid(node) ? node.StatusText
        : ResolveContactShadowSettings().Enabled ? "Contact shadows: starting." : "Contact shadows: off.";

    public void SetShadowTuningOverrides(Godot.Collections.Dictionary overrides)
    {
        _shadowTuningOverrides.Clear();
        foreach (Variant key in overrides.Keys)
            if (key.VariantType == Variant.Type.String && TryValidateShadowOverride(key.AsString(), overrides[key], out Variant value))
                _shadowTuningOverrides[key.AsString()] = value;
        ApplyShadowTuningOverrides();
        ApplyTerrainMacroShadowOverrides();
    }

    public bool SetShadowTuningOverride(string key, Variant value)
    {
        if (!TryValidateSupportedShadowOverride(key, value, out Variant clean)) return false;
        if (key == "NearCascadeMeters") _shadowTuningOverrides.Remove("Split1");
        else if (key == "Split1") _shadowTuningOverrides.Remove("NearCascadeMeters");
        else if (key == "HeightShadowL0TexelMeters") _shadowTuningOverrides.Remove("HeightShadowRangeMeters");
        else if (key == "HeightShadowRangeMeters") _shadowTuningOverrides.Remove("HeightShadowL0TexelMeters");
        _shadowTuningOverrides[key] = clean;
        ApplyShadowTuningOverrides();
        ApplyTerrainMacroShadowOverrides();
        return true;
    }

    private void ApplyTerrainMacroShadowOverrides()
    {
        Tuning profile = ResolveTuning(CurrentPreset);
        float strength = UseInspectorShadowTransitionOverrides
            ? InspectorTerrainMacroShadowStrength
            : profile.MacroShadowStrength;
        float fadeStart = UseInspectorShadowTransitionOverrides
            ? InspectorTerrainMacroFadeStartMeters
            : profile.MacroFadeStartMeters;
        float fadeEnd = UseInspectorShadowTransitionOverrides
            ? InspectorTerrainMacroFadeEndMeters
            : profile.MacroFadeEndMeters;
        ApplyTerrainMacroShadowOverrides(strength, fadeStart, fadeEnd, profile.MacroTraceSteps, profile.MacroTraceMeters);
    }

    private void ApplyTerrainMacroShadowOverrides(
        float defaultStrength,
        float defaultFadeStartMeters,
        float defaultFadeEndMeters,
        int defaultTraceSteps,
        float defaultTraceMeters)
    {
        if (_globalLod == null) return;
        if (!_macroShadowBaselineCaptured)
        {
            _macroShadowBaselineEnabled = _globalLod.EnableMacroTerrainShadows;
            _macroShadowBaselineWallShadowsEnabled = _globalLod.EnableMacroTerrainWallShadows;
            _macroShadowBaselineCaptured = true;
        }

        float strength = _shadowTuningOverrides.TryGetValue("TerrainMacroStrength", out Variant strengthOverride)
            ? strengthOverride.AsSingle() : defaultStrength;
        float fadeStart = _shadowTuningOverrides.TryGetValue("TerrainMacroFadeStartMeters", out Variant fadeStartOverride)
            ? fadeStartOverride.AsSingle() : defaultFadeStartMeters;
        float fadeEnd = _shadowTuningOverrides.TryGetValue("TerrainMacroFadeEndMeters", out Variant fadeEndOverride)
            ? fadeEndOverride.AsSingle() : defaultFadeEndMeters;
        int traceSteps = _shadowTuningOverrides.TryGetValue("TerrainMacroTraceSteps", out Variant traceStepsOverride)
            ? traceStepsOverride.AsInt32() : defaultTraceSteps;
        float traceMeters = _shadowTuningOverrides.TryGetValue("TerrainMacroMaxTraceMeters", out Variant traceMetersOverride)
            ? traceMetersOverride.AsSingle() : defaultTraceMeters;
        _globalLod.ApplyRuntimeShadowQuality(strength, fadeStart, fadeEnd, traceSteps, traceMeters);

        bool hasEnabledOverride = _shadowTuningOverrides.TryGetValue("TerrainMacroEnabled", out Variant enabledOverride);
        bool hasWallOverride = _shadowTuningOverrides.TryGetValue("TerrainMacroWallShadowsEnabled", out Variant wallOverride);
        if (hasEnabledOverride || hasWallOverride || _macroShadowOptionsApplied)
        {
            bool enabled = hasEnabledOverride ? enabledOverride.AsBool() : _macroShadowBaselineEnabled;
            bool wallShadowsEnabled = hasWallOverride ? wallOverride.AsBool() : _macroShadowBaselineWallShadowsEnabled;
            if (!_macroShadowOptionsInitialized
                || _lastAppliedMacroShadowEnabled != enabled
                || _lastAppliedMacroWallShadowsEnabled != wallShadowsEnabled)
            {
                _globalLod.ApplyRuntimeMacroShadowOptions(enabled, wallShadowsEnabled);
                _lastAppliedMacroShadowEnabled = enabled;
                _lastAppliedMacroWallShadowsEnabled = wallShadowsEnabled;
                _macroShadowOptionsInitialized = true;
            }
            _macroShadowOptionsApplied = hasEnabledOverride || hasWallOverride;
        }
    }

    /// <summary>Shadow debug view (0 off, 1 sun visibility, 2 geometry, 3 contact, 4 terrain, 5 cloud) read by materials/ring_shadow_compose.gdshaderinc.</summary>
    public int ShadowDebugMode => _shadowTuningOverrides.TryGetValue("ShadowDebugMode", out Variant mode) ? mode.AsInt32() : 0;

    private void ApplyShadowDebugMode() => RenderingServer.GlobalShaderParameterSet("ringworld_shadow_debug_mode", ShadowDebugMode);

    /// <summary>Per-instance vegetation shadows (S7b, default off): grass and trees read terrain height-shadow and cloud terms from a per-instance atlas the vegetation compute writes.</summary>
    public bool EffectiveVegetationShadowInstanceEnabled => _shadowTuningOverrides.TryGetValue("VegetationShadowInstanceEnabled", out Variant value) && value.AsBool();
    /// <summary>Look-change key (default off): trees and standing grass compose their shadow terms like the other receivers.</summary>
    public bool EffectiveVegetationUnifiedShadowCompose => _shadowTuningOverrides.TryGetValue("VegetationUnifiedShadowCompose", out Variant value) && value.AsBool();
    public int EffectiveVegetationShadowRefreshHz => _shadowTuningOverrides.TryGetValue("VegetationShadowRefreshHz", out Variant value) ? System.Math.Clamp(value.AsInt32(), RingVegetationShadow.MinimumRefreshHz, RingVegetationShadow.MaximumRefreshHz) : RingVegetationShadow.DefaultRefreshHz;
    public string EffectiveVegetationShadowStatus
    {
        get
        {
            if (!EffectiveVegetationShadowInstanceEnabled) return "Per-instance vegetation shadows: off.";
            Vector4 state = RingVegetationShadow.State;
            return state.X > 0.5f || state.Y > 0.5f ? $"Per-instance vegetation shadows: live (grass {(state.X > 0.5f ? "on" : "off")}, trees {(state.Y > 0.5f ? "on" : "off")})." : "Per-instance vegetation shadows: starting.";
        }
    }

    private void ApplyVegetationShadowSettings()
    {
        RingVegetationShadow.Enabled = EffectiveVegetationShadowInstanceEnabled;
        RingVegetationShadow.UnifiedCompose = EffectiveVegetationUnifiedShadowCompose;
        RingVegetationShadow.RefreshHz = EffectiveVegetationShadowRefreshHz;
    }

    private void ApplyShadowTuningOverrides()
    {
        HeightShadowRevision++;
        Tuning profile = ResolveTuning(CurrentPreset);
        int atlas = _shadowTuningOverrides.TryGetValue("AtlasSize", out Variant atlasValue) ? atlasValue.AsInt32() : profile.ShadowAtlasSize;
        int depthBits = _shadowTuningOverrides.TryGetValue("DepthBits", out Variant depthValue) ? depthValue.AsInt32() : 16;
        int filter = _shadowTuningOverrides.TryGetValue("Filter", out Variant filterValue) ? filterValue.AsInt32() : (int)profile.ShadowFilter;
        ResolveDirectionalShadowAtlasHardwareLimit();
        RequestedDirectionalShadowAtlasSize = atlas;
        atlas = ResolveDirectionalShadowAtlasSize(atlas, DirectionalShadowAtlasHardwareLimit);
        EffectiveDirectionalShadowAtlasSize = atlas;
        EffectiveDirectionalShadowDepthBits = depthBits;
        EffectiveDirectionalShadowFilter = filter;
        if (_lastAppliedShadowAtlas != atlas || _lastAppliedShadowDepthBits != depthBits)
        {
            RenderingServer.DirectionalShadowAtlasSetSize(atlas, depthBits == 16);
            _lastAppliedShadowAtlas = atlas;
            _lastAppliedShadowDepthBits = depthBits;
        }
        if (_lastAppliedShadowFilter != filter)
        {
            RenderingServer.DirectionalSoftShadowFilterSetQuality((RenderingServer.ShadowQuality)filter);
            _lastAppliedShadowFilter = filter;
        }
        ApplySunShadowStabilizationOverrides();
        ApplyShadowDebugMode();
        ApplyVegetationShadowSettings();
        ApplyPlanetShadowOverrides();
        if (_sun == null) return;
        _sun.SetMeta("ringworld_shadow_quality_owned", true);
        if (!_shadowBaselineCaptured)
        {
            _shadowBaselineBias = _sun.ShadowBias;
            _shadowBaselineNormalBias = _sun.ShadowNormalBias;
            _shadowBaselineEnabled = _sun.ShadowEnabled;
            _shadowBaselineMode = _sun.DirectionalShadowMode;
            _shadowBaselineCaptured = true;
        }
        // Range and cascade count: explicit overrides may go below the preset floor; presets keep their own (>= 1000 m) range.
        // Near sun map (S5b): while the heightfield owns everything past it, the map shrinks to a 2-cascade near map. MaxDistanceMeters, CascadeCount and FadeStart
        // overrides are ignored then (the menu says so); the near cascade distance still applies, in metres.
        bool near = _nearSunMapActive;
        // POI extension: the near map widens to a 4-cascade map out to D while a large POI is mounted (split2/3 and the fade are fixed then, like near mode already ignores them).
        bool extended = near && _poiSunMapActive;
        float nearDistance = ResolveHeightShadowSettings().NearMapDistanceMeters;
        float requestedDistance = _shadowTuningOverrides.TryGetValue("MaxDistanceMeters", out Variant requestedDistanceOverride) ? requestedDistanceOverride.AsSingle() : _presetShadowDistanceMeters;
        if (_presetShadowDistanceMeters > 0.0f)
        {
            float distance = extended ? _poiSunMapDistanceMeters : near ? nearDistance : requestedDistance;
            if (!Mathf.IsEqualApprox(_sun.DirectionalShadowMaxDistance, distance)) _sun.DirectionalShadowMaxDistance = distance;
        }
        if (extended) _sun.SetMeta(SunNearDistanceMetaKey, nearDistance);
        else if (_sun.HasMeta(SunNearDistanceMetaKey)) _sun.RemoveMeta(SunNearDistanceMetaKey);
        DirectionalLight3D.ShadowMode mode = extended ? DirectionalLight3D.ShadowMode.Parallel4Splits
            : near ? DirectionalLight3D.ShadowMode.Parallel2Splits
            : _shadowTuningOverrides.TryGetValue("CascadeCount", out Variant cascades)
            ? (cascades.AsInt32() == 2 ? DirectionalLight3D.ShadowMode.Parallel2Splits : DirectionalLight3D.ShadowMode.Parallel4Splits)
            : _shadowBaselineMode;
        if (_sun.DirectionalShadowMode != mode) _sun.DirectionalShadowMode = mode;
        _sun.DirectionalShadowSplit2 = CurrentPreset == RingTerrainLodCoordinator.QualityPreset.Cinematic5090 ? .05f : .06f;
        _sun.DirectionalShadowSplit3 = CurrentPreset == RingTerrainLodCoordinator.QualityPreset.Cinematic5090 ? .24f : .28f;
        _sun.DirectionalShadowFadeStart = extended ? PoiSunMapFadeStart : near ? NearSunMapFadeStart : .88f;
        _sun.ShadowBias = _shadowBaselineBias;
        _sun.ShadowNormalBias = _shadowBaselineNormalBias;
        RequestedSunShadowsEnabled = _shadowBaselineEnabled;
        _sun.LightAngularDistance = profile.SunAngularDistanceDegrees;
        if (_shadowTuningOverrides.TryGetValue("Enabled", out Variant enabled)) RequestedSunShadowsEnabled = enabled.AsBool();
        if (near)
        {
            float nearMeters = _shadowTuningOverrides.TryGetValue("NearCascadeMeters", out Variant nearMetersOverride) ? nearMetersOverride.AsSingle()
                : _shadowTuningOverrides.TryGetValue("Split1", out Variant nearSplit) ? nearSplit.AsSingle() * requestedDistance : DefaultNearCascadeMeters;
            _sun.DirectionalShadowSplit1 = Mathf.Clamp(nearMeters / Mathf.Max(_sun.DirectionalShadowMaxDistance, .0001f), .0001f, .40f);
        }
        else if (_shadowTuningOverrides.TryGetValue("NearCascadeMeters", out Variant nearCascade))
            _sun.DirectionalShadowSplit1 = Mathf.Clamp(nearCascade.AsSingle() / Mathf.Max(_sun.DirectionalShadowMaxDistance, .0001f), .0001f, .40f);
        else if (_shadowTuningOverrides.TryGetValue("Split1", out Variant split1)) _sun.DirectionalShadowSplit1 = split1.AsSingle();
        else _sun.DirectionalShadowSplit1 = Mathf.Clamp(DefaultNearCascadeMeters / Mathf.Max(_sun.DirectionalShadowMaxDistance, .0001f), .0001f, .40f);
        if (extended)
        {
            float d = Mathf.Max(_sun.DirectionalShadowMaxDistance, .0001f);
            _sun.DirectionalShadowSplit2 = nearDistance / d;
            _sun.DirectionalShadowSplit3 = Mathf.Sqrt(nearDistance * d) / d;
        }
        else
        {
            if (_shadowTuningOverrides.TryGetValue("Split2", out Variant split2)) _sun.DirectionalShadowSplit2 = split2.AsSingle();
            if (_shadowTuningOverrides.TryGetValue("Split3", out Variant split3)) _sun.DirectionalShadowSplit3 = split3.AsSingle();
        }
        if (!near && _shadowTuningOverrides.TryGetValue("FadeStart", out Variant fade)) _sun.DirectionalShadowFadeStart = fade.AsSingle();
        if (_shadowTuningOverrides.TryGetValue("Bias", out Variant bias)) _sun.ShadowBias = bias.AsSingle();
        if (_shadowTuningOverrides.TryGetValue("NormalBias", out Variant normalBias)) _sun.ShadowNormalBias = normalBias.AsSingle();
        if (_shadowTuningOverrides.TryGetValue("SunAngularDistance", out Variant angularDistance)) _sun.LightAngularDistance = angularDistance.AsSingle();
        NormalizeShadowSplits();
        UpdateLocalShadowHandoff();
        UpdateTerrainShadowCasterCoverage();
    }

    private Node? GetDaySideAtmosphere()
    {
        if (!IsInsideTree()) return null;
        Node? atmosphere = GetParent()?.GetNodeOrNull("DaySideAtmosphere");
        return atmosphere != null && GodotObject.IsInstanceValid(atmosphere) && atmosphere.HasMethod("_update_planet_light") ? atmosphere : null;
    }

    /// <summary>Forwards the planet-light overrides to the day-side atmosphere script; removing an override restores what the scene had.</summary>
    private void ApplyPlanetShadowOverrides()
    {
        Node? atmosphere = GetDaySideAtmosphere();
        bool near = _nearPlanetMapActive;
        float nearDistance = ResolveHeightShadowSettings().PlanetNearMapDistanceMeters;
        if (atmosphere != null)
        {
            if (!_planetShadowBaselineCaptured || _planetShadowBaselineAtmosphereId != atmosphere.GetInstanceId())
            {
                _planetShadowBaselineEnabled = (bool)atmosphere.Get("planet_light_cast_shadows");
                _planetShadowBaselineDistanceMeters = (float)atmosphere.Get("planet_light_shadow_distance_meters");
                _planetShadowBaselineCascades = (int)atmosphere.Get("planet_light_shadow_cascades");
                _planetShadowBaselineFadeRatio = (float)atmosphere.Get("planet_light_shadow_fade_ratio");
                _planetShadowBaselineAtmosphereId = atmosphere.GetInstanceId();
                _planetShadowBaselineCaptured = true;
            }
            atmosphere.Set("planet_light_cast_shadows", _shadowTuningOverrides.TryGetValue("PlanetShadowsEnabled", out Variant enabled) ? enabled.AsBool() : _planetShadowBaselineEnabled);
            // Near planet map (S7a): the PlanetShadowDistanceMeters and PlanetCascadeCount overrides are ignored while it is active; the baseline fade ratio comes back on exit.
            atmosphere.Set("planet_light_shadow_distance_meters", near ? nearDistance : _shadowTuningOverrides.TryGetValue("PlanetShadowDistanceMeters", out Variant distance) ? distance.AsSingle() : _planetShadowBaselineDistanceMeters);
            atmosphere.Set("planet_light_shadow_cascades", near ? 2 : _shadowTuningOverrides.TryGetValue("PlanetCascadeCount", out Variant cascades) ? cascades.AsInt32() : _planetShadowBaselineCascades);
            if (near || _planetFadeRatioTouched)
            {
                atmosphere.Set("planet_light_shadow_fade_ratio", near ? NearSunMapFadeStart : _planetShadowBaselineFadeRatio);
                _planetFadeRatioTouched = near;
            }
        }
        if (_planet == null || !GodotObject.IsInstanceValid(_planet)) return;
        if (near)
        {
            if (!_planetSplitSaved) { _planetSplitBaseline = _planet.DirectionalShadowSplit1; _planetSplitSaved = true; }
            _planet.DirectionalShadowSplit1 = Mathf.Clamp(ResolveNearCascadeMeters() / Mathf.Max(nearDistance, .0001f), .0001f, .40f);
        }
        else if (_planetSplitSaved)
        {
            _planet.DirectionalShadowSplit1 = _planetSplitBaseline;
            _planetSplitSaved = false;
        }
    }

    /// <summary>Near cascade distance in metres as the sun uses it: NearCascadeMeters, or Split1 x the requested sun distance, or the default.</summary>
    private float ResolveNearCascadeMeters()
    {
        float requestedDistance = _shadowTuningOverrides.TryGetValue("MaxDistanceMeters", out Variant requestedDistanceOverride) ? requestedDistanceOverride.AsSingle() : _presetShadowDistanceMeters;
        return _shadowTuningOverrides.TryGetValue("NearCascadeMeters", out Variant nearMetersOverride) ? nearMetersOverride.AsSingle()
            : _shadowTuningOverrides.TryGetValue("Split1", out Variant nearSplit) ? nearSplit.AsSingle() * requestedDistance : DefaultNearCascadeMeters;
    }

    private void ResolveDirectionalShadowAtlasHardwareLimit()
    {
        if (_shadowAtlasLimitResolved) return;
        _shadowAtlasLimitResolved = true;
        try
        {
            RenderingDevice? device = RenderingServer.GetRenderingDevice();
            if (device is null) return;
            ulong texture = device.LimitGet(RenderingDevice.Limit.MaxTextureSize2D);
            ulong width = device.LimitGet(RenderingDevice.Limit.MaxFramebufferWidth);
            ulong height = device.LimitGet(RenderingDevice.Limit.MaxFramebufferHeight);
            DirectionalShadowAtlasHardwareLimit = ResolveDirectionalShadowAtlasHardwareLimit(texture, width, height);
            DirectionalShadowAtlasLimitIsUnverified = false;
        }
        catch (System.Exception)
        {
            // Compatibility renderers and unavailable devices retain the conservative fallback.
        }
    }

    public bool IsDirectionalShadowAtlasSizeSupported(int size)
    {
        ResolveDirectionalShadowAtlasHardwareLimit();
        return IsDirectionalShadowAtlasSize(size) && size <= DirectionalShadowAtlasHardwareLimit;
    }

    public bool TryValidateSupportedShadowOverride(string key, Variant value, out Variant clean)
    {
        if (!TryValidateShadowOverride(key, value, out clean)) return false;
        return key != "AtlasSize" || IsDirectionalShadowAtlasSizeSupported(clean.AsInt32());
    }

    public static int ResolveDirectionalShadowAtlasSize(int requestedSize, int maximumSize)
    {
        int ceiling = System.Math.Min(requestedSize, maximumSize);
        if (ceiling <= 0) return 0;
        int resolved = 0;
        foreach (int candidate in DirectionalShadowAtlasSizes)
        {
            if (candidate > requestedSize || candidate > maximumSize) break;
            resolved = candidate;
        }
        if (resolved > 0) return resolved;
        int floorPowerOfTwo = 1;
        while (floorPowerOfTwo <= ceiling / 2) floorPowerOfTwo *= 2;
        return floorPowerOfTwo;
    }

    public static int ResolveDirectionalShadowAtlasHardwareLimit(ulong textureLimit, ulong framebufferWidthLimit, ulong framebufferHeightLimit)
    {
        ulong limit = System.Math.Min(textureLimit, System.Math.Min(framebufferWidthLimit, framebufferHeightLimit));
        return limit > int.MaxValue ? int.MaxValue : (int)limit;
    }

    public static ulong DirectionalShadowAtlasMinimumDepthBytes(int size) => DirectionalShadowAtlasMinimumDepthBytes(size, 32);

    public static ulong DirectionalShadowAtlasMinimumDepthBytes(int size, int depthBits)
    {
        if (size <= 0 || depthBits is not (16 or 32)) return 0UL;
        return (ulong)size * (ulong)size * ((ulong)depthBits / 8UL);
    }

    private static bool IsDirectionalShadowAtlasSize(int size)
    {
        foreach (int candidate in DirectionalShadowAtlasSizes)
            if (candidate == size) return true;
        return false;
    }

    private void ApplySunShadowStabilizationOverrides()
    {
        if (_dayNightCycle == null || !GodotObject.IsInstanceValid(_dayNightCycle) || !_dayNightCycle.IsInsideTree())
        {
            _dayNightCycle = IsInsideTree()
                ? GetNodeOrNull<global::RingworldTechDemo.RingDayNightCycle>(DayNightCyclePath)
                : null;
            _shadowBaselineCycleCaptured = false;
        }
        if (_dayNightCycle == null) return;
        if (!_shadowBaselineCycleCaptured)
        {
            _shadowBaselineSunStabilizationEnabled = _dayNightCycle.SunShadowStabilizationEnabled;
            _shadowBaselineSunStepDegrees = _dayNightCycle.SunShadowStepDegrees;
            _shadowBaselineCycleCaptured = true;
        }
        _dayNightCycle.SunShadowStabilizationEnabled = _shadowTuningOverrides.TryGetValue("SunStabilizationEnabled", out Variant enabled)
            ? enabled.AsBool()
            : _shadowBaselineSunStabilizationEnabled;
        _dayNightCycle.SunShadowStepDegrees = _shadowTuningOverrides.TryGetValue("SunStepDegrees", out Variant step)
            ? step.AsSingle()
            : _shadowBaselineSunStepDegrees;
    }

    private void NormalizeShadowSplits()
    {
        if (_sun == null) return;
        _sun.DirectionalShadowSplit1 = Mathf.Clamp(_sun.DirectionalShadowSplit1, .0001f, .40f);
        _sun.DirectionalShadowSplit2 = Mathf.Clamp(_sun.DirectionalShadowSplit2, _sun.DirectionalShadowSplit1 + .01f, .70f);
        _sun.DirectionalShadowSplit3 = Mathf.Clamp(_sun.DirectionalShadowSplit3, _sun.DirectionalShadowSplit2 + .01f, .95f);
    }

    public static bool TryValidateShadowOverride(string key, Variant value, out Variant clean)
    {
        clean = default;
        if (key == "Enabled" || key == "TerrainMacroEnabled" || key == "TerrainMacroWallShadowsEnabled" || key == "LocalShadowHandoffEnabled") { if (value.VariantType != Variant.Type.Bool) return false; clean = value.AsBool(); return true; }
        if (key is "HeightShadowEnabled" or "HeightShadowNearMapEnabled" or "HeightShadowPlanetNearMapEnabled" or "ContactShadowEnabled" or "VegetationShadowInstanceEnabled" or "VegetationUnifiedShadowCompose") { if (value.VariantType != Variant.Type.Bool) return false; clean = value.AsBool(); return true; }
        if ((key == "HeightShadowL0TexelMeters" || key == "HeightShadowRangeMeters" || key == "HeightShadowLevels" || key == "HeightShadowNearMapDistanceMeters") && value.VariantType is Variant.Type.Int or Variant.Type.Float)
        {
            double raw = value.AsDouble();
            bool member = key switch { "HeightShadowL0TexelMeters" => raw is 2.0 or 4.0 or 8.0, "HeightShadowRangeMeters" => raw is 4000.0 or 8000.0 or 16000.0, "HeightShadowNearMapDistanceMeters" => raw is 300.0 or 400.0 or 500.0, _ => raw is 1.0 or 2.0 };
            if (!member) return false;
            clean = (int)raw; return true;
        }
        if (key == "ContactShadowSteps" && value.VariantType is Variant.Type.Int or Variant.Type.Float)
        {
            double raw = value.AsDouble();
            if (raw is not (8.0 or 12.0 or 16.0 or 24.0 or 32.0)) return false;
            clean = (int)raw; return true;
        }
        if (key == "SunStabilizationEnabled" || key == "PlanetShadowsEnabled") { if (value.VariantType != Variant.Type.Bool) return false; clean = value.AsBool(); return true; }
        if (key == "AtlasSize" && value.VariantType is Variant.Type.Int or Variant.Type.Float)
        { double raw = value.AsDouble(); if (!double.IsFinite(raw) || raw != System.Math.Truncate(raw) || raw < int.MinValue || raw > int.MaxValue) return false; int v = (int)raw; if (IsDirectionalShadowAtlasSize(v)) { clean = v; return true; } return false; }
        if (key == "DepthBits" && value.VariantType is Variant.Type.Int or Variant.Type.Float)
        { double raw = value.AsDouble(); if (raw == 16.0 || raw == 32.0) { clean = (int)raw; return true; } return false; }
        if ((key == "CascadeCount" || key == "PlanetCascadeCount") && value.VariantType is Variant.Type.Int or Variant.Type.Float)
        { double raw = value.AsDouble(); if (raw == 2.0 || raw == 4.0) { clean = (int)raw; return true; } return false; }
        if (key == "ShadowDebugMode" && value.VariantType is Variant.Type.Int or Variant.Type.Float)
        { float raw = value.AsSingle(); if (!float.IsFinite(raw) || raw != Mathf.Round(raw)) return false; int v = (int)raw; if (v is >= 0 and <= 5) { clean = v; return true; } return false; }
        if (key == "Filter" && value.VariantType is Variant.Type.Int or Variant.Type.Float)
        { float raw = value.AsSingle(); if (!float.IsFinite(raw) || raw != Mathf.Round(raw)) return false; int v = (int)raw; if (v is >= 0 and <= 5) { clean = v; return true; } return false; }
        if (value.VariantType is not (Variant.Type.Int or Variant.Type.Float) || !float.IsFinite(value.AsSingle())) return false;
        (float min, float max) = key switch { "NearCascadeMeters" => (MinimumNearCascadeMeters, MaximumNearCascadeMeters), "Split1" => (.0001f, .40f), "Split2" => (.02f, .70f), "Split3" => (.03f, .95f), "FadeStart" => (.50f, .98f), "Bias" => (0, 1), "NormalBias" => (0, 4), "SunAngularDistance" => (0, 3), "SunStepDegrees" => (0, .05f), "SunShadowOffElevationDegrees" or "SunShadowOnElevationDegrees" => (-45, -1), "TerrainMacroStrength" => (0, .8f), "TerrainMacroFadeStartMeters" => (0, 50000), "TerrainMacroFadeEndMeters" => (100, 60000), "TerrainMacroTraceSteps" => (1, 32), "TerrainMacroMaxTraceMeters" => (4000, 320000), "MaxDistanceMeters" => (MinimumShadowDistanceMeters, MaximumShadowDistanceMeters), "PlanetShadowDistanceMeters" => (MinimumPlanetShadowDistanceMeters, MaximumPlanetShadowDistanceMeters),
            "HeightShadowSunStepDegrees" => (.02f, .5f), "HeightShadowRowsPerFrame" => (16, 1024), "HeightShadowPenumbraDegrees" => (0, 2), "HeightShadowCrossfadeSeconds" => (0, 3), "HeightShadowFillWorkers" => (1, 4), "HeightShadowBudgetMs" => (.05f, 2), "HeightShadowPoiMapDistanceMeters" => (RingHeightShadowSettings.MinimumPoiMapDistanceMeters, RingHeightShadowSettings.MaximumPoiMapDistanceMeters), "HeightShadowPlanetNearMapDistanceMeters" => (RingHeightShadowSettings.MinimumPlanetNearMapDistanceMeters, RingHeightShadowSettings.MaximumPlanetNearMapDistanceMeters),
            "ContactShadowLengthMeters" => (.25f, 4), "ContactShadowThicknessMeters" => (.05f, 1), "ContactShadowStrength" => (0, 1), "ContactShadowFadeMeters" => (25, 400), "ContactShadowGrassStrength" => (0, 1), "VegetationShadowRefreshHz" => (RingVegetationShadow.MinimumRefreshHz, RingVegetationShadow.MaximumRefreshHz), _ => (1, 0) };
        if (min > max) return false;
        float clamped = Mathf.Clamp(value.AsSingle(), min, max);
        if (key is "TerrainMacroTraceSteps" or "HeightShadowRowsPerFrame" or "HeightShadowFillWorkers" or "HeightShadowPlanetNearMapDistanceMeters" or "VegetationShadowRefreshHz") clean = Mathf.RoundToInt(clamped); else clean = clamped;
        return true;
    }
}
