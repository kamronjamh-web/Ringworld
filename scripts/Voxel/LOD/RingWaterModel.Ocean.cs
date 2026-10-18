using Godot;
using System;
using RingworldTechDemo.Weather;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace RingworldTechDemo.Voxel.LOD;

public static partial class RingWaterModel
{
    // Keys are weak, so cache entries disappear with their material and cannot
    // keep pooled or discarded sector resources alive.
    private sealed class OceanMaterialSnapshot
    {
        public bool Initialized;
        public long WindClockVersion = -1;
        public bool Ready;
        public Texture2DArrayRD? NormalFoam;
        public Texture2DArrayRD? Displacement;
        public Vector3 AnchorWorld;
        public Vector2 CoverageCenter;
        public Vector3 AroundAxis;
        public Vector3 AcrossAxis;
        public Vector2 Period0;
        public Vector2 Period1;
        public Vector2 Period2;
        public Vector2 Period3;
        public Vector2 Phase0;
        public Vector2 Phase1;
        public Vector2 Phase2;
        public Vector2 Phase3;
        public int CascadeCount;
        public float DetailDistance;
        public float NormalStrength;
        public float FoamStrength;
        public float GeometryHeight;
        public float GeometryChoppiness;
        public float GeometryDistance;
        public float GeometryFilter;
        public float CrestLighting;
        public float Resolution;
        public int ShorelineFieldMode;
        public Texture2D? ShorelineField;
        public Vector2 ShorelineFieldCenter;
        public float ShorelineFieldCellMeters;
        public float ShorelineFadeDistance;
        public float ShorelineChopFadeDistance;
        public float ShorelineShallowDepth;
        public float DeepWaterWaveDepth;
        // Last wind values written. Phases/advection/translation are shared globals unless the slot is -1.
        public bool WindApplied;
        public int WindSlot = -1;
        public bool WindEnabled;
        public Vector2 WindDirection;
        public float WindSpeed;
        public float WindMeanResponse;
        public float WindGustStrength;
        public bool WindSpatialGust;
    }

    private static readonly ConditionalWeakTable<ShaderMaterial, OceanMaterialSnapshot> OceanMaterialSnapshots = new();
    private static readonly StringName FftAcrossAxisName = "fft_across_axis";
    private static readonly StringName FftAnchorWorldName = "fft_anchor_world";
    private static readonly StringName FftAroundAxisName = "fft_around_axis";
    private static readonly StringName FftCascadeCountName = "fft_cascade_count";
    private static readonly StringName FftCrestLightingStrengthName = "fft_crest_lighting_strength";
    private static readonly StringName FftDeepWaterWaveDepthMetersName = "fft_deep_water_wave_depth_meters";
    private static readonly StringName FftDetailDistanceMetersName = "fft_detail_distance_meters";
    private static readonly StringName FftDisplacementName = "fft_displacement";
    private static readonly StringName FftFoamStrengthName = "fft_foam_strength";
    private static readonly StringName FftGeometryChoppinessName = "fft_geometry_choppiness";
    private static readonly StringName FftGeometryDistanceMetersName = "fft_geometry_distance_meters";
    private static readonly StringName FftGeometryFilterMetersName = "fft_geometry_filter_meters";
    private static readonly StringName FftGeometryHeightScaleName = "fft_geometry_height_scale";
    private static readonly StringName FftNormalFoamName = "fft_normal_foam";
    private static readonly StringName FftNormalStrengthName = "fft_normal_strength";
    private static readonly StringName FftOceanCoverageCenterCanonicalName = "fft_ocean_coverage_center_canonical";
    private static readonly StringName FftPeriod0Name = "fft_period_0";
    private static readonly StringName FftPeriod1Name = "fft_period_1";
    private static readonly StringName FftPeriod2Name = "fft_period_2";
    private static readonly StringName FftPeriod3Name = "fft_period_3";
    private static readonly StringName FftPhase0Name = "fft_phase_0";
    private static readonly StringName FftPhase1Name = "fft_phase_1";
    private static readonly StringName FftPhase2Name = "fft_phase_2";
    private static readonly StringName FftPhase3Name = "fft_phase_3";
    private static readonly StringName FftReadyName = "fft_ready";
    private static readonly StringName FftResolutionName = "fft_resolution";
    private static readonly StringName FftShorelineChopFadeDistanceMetersName = "fft_shoreline_chop_fade_distance_meters";
    private static readonly StringName FftShorelineFadeDistanceMetersName = "fft_shoreline_fade_distance_meters";
    private static readonly StringName FftShorelineFieldName = "fft_shoreline_field";
    private static readonly StringName FftShorelineFieldCellMetersName = "fft_shoreline_field_cell_meters";
    private static readonly StringName FftShorelineFieldCenterCanonicalName = "fft_shoreline_field_center_canonical";
    private static readonly StringName FftShorelineFieldModeName = "fft_shoreline_field_mode";
    private static readonly StringName FftShorelineShallowDepthMetersName = "fft_shoreline_shallow_depth_meters";
    private static readonly StringName RingRadiusMetersName = "ring_radius_meters";
    private static readonly StringName FarGroupTranslationName = "water_far_group_translation";
    private static readonly StringName GustResponseStrengthName = "water_gust_response_strength";
    private static readonly StringName MeanResponseName = "water_mean_response";
    private static readonly StringName MeanWindDirectionName = "water_mean_wind_direction";
    private static readonly StringName MeanWindSpeedName = "water_mean_wind_speed";
    private static readonly StringName RiverAdvectionName = "water_river_advection";
    private static readonly StringName SpatialGustEnabledName = "water_spatial_gust_enabled";
    private static readonly StringName WavePhasesName = "water_wave_phases";
    private static readonly StringName WindClockEnabledName = "water_wind_clock_enabled";
    private static readonly StringName WindGlobalSlotName = "water_wind_global_slot";
    // Frame-global wind clock values live in shader globals; arrays are unsupported, so the 12 phase
    // vectors are packed into four mat3 (column c of matrix m holds phase 3m+c).
    private static readonly StringName[] WavePhaseGlobalNames =
        { "ring_water_wave_phases_0", "ring_water_wave_phases_1", "ring_water_wave_phases_2", "ring_water_wave_phases_3" };
    private static readonly StringName[] RiverAdvectionGlobalNames = { "ring_water_river_advection_main", "ring_water_river_advection_far" };
    private static readonly StringName[] GroupTranslationGlobalNames = { "ring_water_far_group_translation_main", "ring_water_far_group_translation_far" };
    public static long OceanMaterialParameterPushes { get; private set; }
    public static long OceanMaterialSkippedRefreshes { get; private set; }
    public static int LastOceanMaterialRefreshPushes { get; private set; }

    public static Godot.Collections.Dictionary GetOceanMaterialPushDiagnostics() => new()
    {
        ["parameter_pushes"] = OceanMaterialParameterPushes,
        ["skipped_refreshes"] = OceanMaterialSkippedRefreshes,
        ["last_refresh_pushes"] = LastOceanMaterialRefreshPushes
    };

    private static double _windClock;
    private static Vector2 _waterMeanWind;
    private static bool _windClockEnabled;
    private static bool _waterFollowWeather = true;
    private static long _windClockVersion;
    private static float _windWaveSpeed = float.NaN, _windAnalyticStrength = float.NaN, _windGustStrength = float.NaN;
    private static readonly double[] WaterPhaseRates = { 1, -1.173, 1.619, -2.071, 2.513, .071, -.053, .041, -.063, .037, .23, -.13 };
    private static readonly Vector3[] WaterPhases = new Vector3[12];
    private static readonly Basis[] WaterPhaseBases = new Basis[4];
    private static bool _waterPhaseBasesPublished;
    // Slot 0 = unscaled materials, slot 1 = scaled-far materials. Each slot's drift globals are valid for
    // one (scale, ring radius) key per wind clock version; materials with a different key stay per-material.
    private static readonly long[] WindSlotVersion = { -1, -1 };
    private static readonly float[] WindSlotScale = new float[2], WindSlotRadius = new float[2];
    private static readonly float[] WindSlotRiver = new float[2];
    private static readonly Vector4[] WindSlotGroup = new Vector4[2];
    private static readonly bool[] WindSlotPublished = new bool[2];
    public static long WaterWindGlobalWriteCount { get; private set; }
    /// <summary>Last published phase vector 0..11 (headless-safe stand-in for reading the global back).</summary>
    public static Vector3 PublishedWavePhase(int index) => WaterPhaseBases[index / 3][index % 3];
    public static float PublishedRiverAdvection(int slot) => WindSlotRiver[slot];
    public static Vector4 PublishedFarGroupTranslation(int slot) => WindSlotGroup[slot];
    // Shared packed copy, rebuilt once per wind clock update instead of once per material write.
    private static Variant _waterPhasesVariant;

    public static void UpdateWindClock(double simulationTime, Vector2 meanWind, bool followWeather = true)
    {
        if (!double.IsFinite(simulationTime)) return;
        var settings = RingWindSettings.Current;
        if (_windClockEnabled && _windClock == simulationTime && _waterMeanWind == meanWind
            && _windWaveSpeed == WaveSpeed && _windAnalyticStrength == settings.AnalyticWaveResponseStrength
            && _windGustStrength == settings.WaterGustResponseStrength && _waterFollowWeather == followWeather) return;
        _waterFollowWeather = followWeather;
        _windWaveSpeed = WaveSpeed;
        _windAnalyticStrength = settings.AnalyticWaveResponseStrength;
        _windGustStrength = settings.WaterGustResponseStrength;
        _windClock = simulationTime;
        _waterMeanWind = meanWind;
        _windClockEnabled = true;
        _windClockVersion++;
        for (int index = 0; index < WaterPhases.Length; index++)
            WaterPhases[index] = new Vector3(
                RingWaterWindResponse.BoundedPhase(simulationTime, WaveSpeed, WaterPhaseRates[index]),
                RingWaterWindResponse.BoundedPhase(simulationTime, WaveSpeed, WaterPhaseRates[index] * .42),
                RingWaterWindResponse.BoundedPhase(simulationTime, WaveSpeed, WaterPhaseRates[index] * .18));
        _waterPhasesVariant.Dispose();
        _waterPhasesVariant = Variant.From(WaterPhases);
        for (int matrix = 0; matrix < WaterPhaseBases.Length; matrix++)
        {
            // Basis(c0, c1, c2) takes columns, and the global mat3 uploads as shader columns c0..c2.
            Basis packed = new(WaterPhases[matrix * 3], WaterPhases[matrix * 3 + 1], WaterPhases[matrix * 3 + 2]);
            if (_waterPhaseBasesPublished && WaterPhaseBases[matrix] == packed) continue;
            WaterPhaseBases[matrix] = packed;
            PublishWaterGlobal(WavePhaseGlobalNames[matrix], packed);
        }
        _waterPhaseBasesPublished = true;
    }

    private static void PublishWaterGlobal(StringName name, Variant value)
    {
        RenderingServer.GlobalShaderParameterSet(name, value);
        WaterWindGlobalWriteCount++;
        RingworldTechDemo.Diagnostics.RingMaterialWriteCounters.Record(RingworldTechDemo.Diagnostics.RingMaterialWriteCounters.Site.WaterWindGlobal);
    }

    private static void ComputeWindDrift(float coordinateScale, float radius, out float river, out Vector4 group)
    {
        double circumference = Math.Tau * Math.Max(1, radius);
        static float Wrap(double value, double period) => (float)(value - Math.Floor(value / period) * period);
        river = Wrap(_windClock * WaveSpeed * 1.8 * coordinateScale, circumference * 2.15);
        // Bounded broad group drift avoids narrowing an unbounded across offset.
        group = new Vector4(
            Wrap(_windClock * .47 * coordinateScale, circumference), Mathf.Sin(RingWaterWindResponse.BoundedPhase(_windClock, 1, -.00023)) * 1000f * coordinateScale,
            Wrap(-_windClock * .29 * coordinateScale, circumference), Mathf.Sin(RingWaterWindResponse.BoundedPhase(_windClock, 1, .00041)) * 1000f * coordinateScale);
    }

    public static long CurrentWindClockVersion => _windClockVersion;
    public static double CurrentWindClock => _windClock;
    public static void ClearWindClock() { _windClockEnabled = false; _windClockVersion++; }

    private static void ApplyWaterWind(ShaderMaterial material, float coordinateScale, string? path)
    {
        if (path != "res://materials/ring_ocean_proxy.gdshader" && path != "res://materials/ring_ocean_global.gdshader") return;
        OceanMaterialSnapshot snapshot = OceanMaterialSnapshots.GetOrCreateValue(material);
        if (snapshot.WindClockVersion == _windClockVersion) return;
        snapshot.WindClockVersion = _windClockVersion;
        bool first = !snapshot.WindApplied;
        snapshot.WindApplied = true;
        if (first || snapshot.WindEnabled != _windClockEnabled)
        {
            SetWindParameter(material, WindClockEnabledName, _windClockEnabled);
            snapshot.WindEnabled = _windClockEnabled;
        }
        if (!_windClockEnabled)
        {
            if (first || snapshot.WindMeanResponse != 1f) { SetWindParameter(material, MeanResponseName, 1f); snapshot.WindMeanResponse = 1f; }
            if (first || snapshot.WindSpeed != 0f) { SetWindParameter(material, MeanWindSpeedName, 0f); snapshot.WindSpeed = 0f; }
            if (first || snapshot.WindSpatialGust) { SetWindParameter(material, SpatialGustEnabledName, false); snapshot.WindSpatialGust = false; }
            return;
        }
        Vector2 direction = _waterMeanWind.LengthSquared() > .0001f ? _waterMeanWind.Normalized() : new Vector2(.916f, .401f).Normalized();
        if (first || snapshot.WindDirection != direction) { SetWindParameter(material, MeanWindDirectionName, direction); snapshot.WindDirection = direction; }
        float speed = _waterMeanWind.Length();
        if (first || snapshot.WindSpeed != speed) { SetWindParameter(material, MeanWindSpeedName, speed); snapshot.WindSpeed = speed; }
        var settings = RingWindSettings.Current;
        float meanResponse = 1f + settings.AnalyticWaveResponseStrength * Mathf.Clamp(speed / 24f, 0, 1);
        if (first || snapshot.WindMeanResponse != meanResponse) { SetWindParameter(material, MeanResponseName, meanResponse); snapshot.WindMeanResponse = meanResponse; }
        float gust = settings.WaterGustResponseStrength;
        if (first || snapshot.WindGustStrength != gust) { SetWindParameter(material, GustResponseStrengthName, gust); snapshot.WindGustStrength = gust; }
        // Scaled-far positions cannot sample the main scene's tangent gust atlas.
        bool spatialGust = _waterFollowWeather && settings.Enabled && Mathf.IsEqualApprox(coordinateScale, 1f);
        if (first || snapshot.WindSpatialGust != spatialGust) { SetWindParameter(material, SpatialGustEnabledName, spatialGust); snapshot.WindSpatialGust = spatialGust; }
        // The ring radius is per-material, so it still gates which materials may share a slot's drift globals.
        float radius = material.GetShaderParameter(RingRadiusMetersName).AsSingle();
        int slot = Mathf.IsEqualApprox(coordinateScale, 1f) ? 0 : 1;
        if (WindSlotVersion[slot] != _windClockVersion)
        {
            ComputeWindDrift(coordinateScale, radius, out float slotRiver, out Vector4 slotGroup);
            if (!WindSlotPublished[slot] || WindSlotRiver[slot] != slotRiver)
                PublishWaterGlobal(RiverAdvectionGlobalNames[slot], slotRiver);
            if (!WindSlotPublished[slot] || WindSlotGroup[slot] != slotGroup)
                PublishWaterGlobal(GroupTranslationGlobalNames[slot], slotGroup);
            WindSlotRiver[slot] = slotRiver;
            WindSlotGroup[slot] = slotGroup;
            WindSlotPublished[slot] = true;
            WindSlotScale[slot] = coordinateScale;
            WindSlotRadius[slot] = radius;
            WindSlotVersion[slot] = _windClockVersion;
        }
        else if (WindSlotScale[slot] != coordinateScale || WindSlotRadius[slot] != radius) slot = -1;
        if (snapshot.WindSlot != slot) { SetWindParameter(material, WindGlobalSlotName, slot); snapshot.WindSlot = slot; }
        if (slot >= 0) return;
        // Mismatched (scale, radius): exact per-material fallback, identical to the pre-global behavior.
        SetWindParameter(material, WavePhasesName, _waterPhasesVariant);
        ComputeWindDrift(coordinateScale, radius, out float river, out Vector4 group);
        SetWindParameter(material, RiverAdvectionName, river);
        SetWindParameter(material, FarGroupTranslationName, group);
    }

    private static void SetWindParameter(ShaderMaterial material, StringName name, Variant value)
    {
        SetOceanParameter(material, name, value, RingworldTechDemo.Diagnostics.RingMaterialWriteCounters.Site.WaterWind);
    }

    /// <summary>Refresh only the nearby ocean bindings as the canonical frame moves.</summary>
    public static void RefreshOceanMaterials()
    {
        LastOceanMaterialRefreshPushes = 0;
        for (int index = Materials.Count - 1; index >= 0; index--)
        {
            MaterialBinding binding = Materials[index];
            if (!binding.Material.TryGetTarget(out ShaderMaterial? material)
                || !GodotObject.IsInstanceValid(material))
            {
                Materials.RemoveAt(index);
                continue;
            }
            int before = LastOceanMaterialRefreshPushes;
            ApplyOceanMaterial(material, binding.CoordinateScale);
            if (before == LastOceanMaterialRefreshPushes) OceanMaterialSkippedRefreshes++;
        }
    }

    /// <summary>
    /// Releases the Texture2DArrayRD wrappers currently sampled by proxy-ocean
    /// materials. This must run on the main thread before an FFT topology
    /// rebuild frees the backing RenderingDevice RIDs on the render thread.
    /// </summary>
    public static void RetireFftTextureBindings()
    {
        for (int index = Materials.Count - 1; index >= 0; index--)
        {
            MaterialBinding binding = Materials[index];
            if (!binding.Material.TryGetTarget(out ShaderMaterial? material)
                || !GodotObject.IsInstanceValid(material))
            {
                Materials.RemoveAt(index);
                continue;
            }

            if (!Mathf.IsEqualApprox(binding.CoordinateScale, 1f)
                || material.Shader?.ResourcePath != "res://materials/ring_ocean_proxy.gdshader") continue;

            OceanMaterialSnapshot snapshot = OceanMaterialSnapshots.GetOrCreateValue(material);
            SetOceanParameter(material, FftReadyName, false);
            SetOceanParameter(material, FftNormalFoamName, default(Variant));
            SetOceanParameter(material, FftDisplacementName, default(Variant));
            snapshot.Ready = false;
            snapshot.NormalFoam = null;
            snapshot.Displacement = null;
            snapshot.Initialized = true;
        }
    }

    private static void ApplyOceanMaterial(ShaderMaterial material, float coordinateScale)
    {
        string? shaderPath = material.Shader?.ResourcePath;
        ApplyWaterWind(material, coordinateScale, shaderPath);
        // Global and scaled-far water deliberately retain their opaque, inexpensive path.
        if (!Mathf.IsEqualApprox(coordinateScale, 1f)
            || shaderPath != "res://materials/ring_ocean_proxy.gdshader") return;

        OceanMaterialSnapshot snapshot = OceanMaterialSnapshots.GetOrCreateValue(material);
        RingWaterFftController? controller = RingWaterFftController.Instance;
        bool ready = controller != null && GodotObject.IsInstanceValid(controller)
            && controller.Enabled && controller.Ready && controller.NormalFoamTexture != null && controller.DisplacementTexture != null;
        bool previouslyReady = snapshot.Ready;
        SetIfChanged(material, FftReadyName, ready, ref snapshot.Ready, !snapshot.Initialized);
        if (!ready)
        {
            if (!snapshot.Initialized || previouslyReady || snapshot.NormalFoam != null)
                SetOceanParameter(material, FftNormalFoamName, default(Variant));
            if (!snapshot.Initialized || previouslyReady || snapshot.Displacement != null)
                SetOceanParameter(material, FftDisplacementName, default(Variant));
            snapshot.NormalFoam = null;
            snapshot.Displacement = null;
            snapshot.Initialized = true;
            return;
        }

        // A disabled binding only contains cleared textures.  On the first
        // ready frame, force every value through even if its cached default
        // equals the desired value, because a shader resource may have a
        // non-default authored uniform.
        bool forceReady = !snapshot.Initialized || !previouslyReady;
        Texture2DArrayRD normalFoam = controller!.NormalFoamTexture!;
        Texture2DArrayRD displacement = controller.DisplacementTexture!;
        SetIfChanged(material, FftNormalFoamName, normalFoam, ref snapshot.NormalFoam, forceReady);
        SetIfChanged(material, FftDisplacementName, displacement, ref snapshot.Displacement, forceReady);
        SetIfChanged(material, FftAnchorWorldName, controller.CanonicalAnchorWorld, ref snapshot.AnchorWorld, forceReady);
        // Resolve after the controller updates its anchor, so a moving or
        // rebased observer cannot shift the coverage proof by one frame.
        SetIfChanged(material, FftOceanCoverageCenterCanonicalName, RingTerrainProxySector.SharedFftCoverageShaderCenter, ref snapshot.CoverageCenter, forceReady);
        SetIfChanged(material, FftAroundAxisName, controller.CanonicalAroundAxis, ref snapshot.AroundAxis, forceReady);
        SetIfChanged(material, FftAcrossAxisName, controller.CanonicalAcrossAxis, ref snapshot.AcrossAxis, forceReady);
        SetIfChanged(material, FftPeriod0Name, controller.Period0, ref snapshot.Period0, forceReady);
        SetIfChanged(material, FftPeriod1Name, controller.Period1, ref snapshot.Period1, forceReady);
        SetIfChanged(material, FftPeriod2Name, controller.Period2, ref snapshot.Period2, forceReady);
        SetIfChanged(material, FftPeriod3Name, controller.Period3, ref snapshot.Period3, forceReady);
        SetIfChanged(material, FftPhase0Name, controller.Phase0, ref snapshot.Phase0, forceReady);
        SetIfChanged(material, FftPhase1Name, controller.Phase1, ref snapshot.Phase1, forceReady);
        SetIfChanged(material, FftPhase2Name, controller.Phase2, ref snapshot.Phase2, forceReady);
        SetIfChanged(material, FftPhase3Name, controller.Phase3, ref snapshot.Phase3, forceReady);
        SetIfChanged(material, FftCascadeCountName, controller.ActiveCascadeCount, ref snapshot.CascadeCount, forceReady);
        SetIfChanged(material, FftDetailDistanceMetersName, controller.DetailDistanceMeters, ref snapshot.DetailDistance, forceReady);
        SetIfChanged(material, FftNormalStrengthName, controller.NormalStrength, ref snapshot.NormalStrength, forceReady);
        SetIfChanged(material, FftFoamStrengthName, controller.FoamStrength, ref snapshot.FoamStrength, forceReady);
        SetIfChanged(material, FftGeometryHeightScaleName, controller.GeometryHeightScale, ref snapshot.GeometryHeight, forceReady);
        SetIfChanged(material, FftGeometryChoppinessName, controller.GeometryChoppiness, ref snapshot.GeometryChoppiness, forceReady);
        SetIfChanged(material, FftGeometryDistanceMetersName, controller.EffectiveGeometryDistanceMeters, ref snapshot.GeometryDistance, forceReady);
        SetIfChanged(material, FftGeometryFilterMetersName, controller.GeometryFilterMeters, ref snapshot.GeometryFilter, forceReady);
        SetIfChanged(material, FftCrestLightingStrengthName, controller.CrestLightingStrength, ref snapshot.CrestLighting, forceReady);
        SetIfChanged(material, FftResolutionName, (float)controller.ActiveResolution, ref snapshot.Resolution, forceReady);
        bool shorelineReady = RingTerrainProxySector.SharedFftShorelineFieldValid
            && RingTerrainProxySector.SharedFftShorelineFieldTexture is not null;
        int shorelineMode = !controller.RequireShorelineField ? 0 : shorelineReady ? 2 : 1;
        SetIfChanged(material, FftShorelineFieldModeName, shorelineMode, ref snapshot.ShorelineFieldMode, forceReady);
        Texture2D? shorelineField = shorelineReady ? RingTerrainProxySector.SharedFftShorelineFieldTexture : null;
        SetIfChanged(material, FftShorelineFieldName, shorelineField, ref snapshot.ShorelineField, forceReady);
        SetIfChanged(material, FftShorelineFieldCenterCanonicalName, RingTerrainProxySector.SharedFftShorelineFieldShaderCenter, ref snapshot.ShorelineFieldCenter, forceReady);
        SetIfChanged(material, FftShorelineFieldCellMetersName, RingTerrainProxySector.SharedFftShorelineFieldCellMeters, ref snapshot.ShorelineFieldCellMeters, forceReady);
        SetIfChanged(material, FftShorelineFadeDistanceMetersName, controller.ShorelineFadeDistanceMeters, ref snapshot.ShorelineFadeDistance, forceReady);
        SetIfChanged(material, FftShorelineChopFadeDistanceMetersName, controller.ShorelineChopFadeDistanceMeters, ref snapshot.ShorelineChopFadeDistance, forceReady);
        SetIfChanged(material, FftShorelineShallowDepthMetersName, controller.ShorelineShallowDepthMeters, ref snapshot.ShorelineShallowDepth, forceReady);
        SetIfChanged(material, FftDeepWaterWaveDepthMetersName, controller.DeepWaterWaveDepthMeters, ref snapshot.DeepWaterWaveDepth, forceReady);
        snapshot.Initialized = true;
    }

    private static void SetOceanParameter(ShaderMaterial material, StringName name, Variant value,
        RingworldTechDemo.Diagnostics.RingMaterialWriteCounters.Site site = RingworldTechDemo.Diagnostics.RingMaterialWriteCounters.Site.OceanFft)
    {
        material.SetShaderParameter(name, value);
        OceanMaterialParameterPushes++;
        LastOceanMaterialRefreshPushes++;
        RingworldTechDemo.Diagnostics.RingMaterialWriteCounters.Record(site, 1, value.VariantType == Variant.Type.Object ? 1 : 0);
    }

    private static void SetIfChanged<T>(ShaderMaterial material, StringName name, T value, ref T previous, bool force)
    {
        if (force || !EqualityComparer<T>.Default.Equals(previous, value))
            SetOceanParameter(material, name, Variant.From(value));
        previous = value;
    }
}
