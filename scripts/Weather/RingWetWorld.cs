using System;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Weather;

/// <summary>
/// Wet world producer (docs/storm-revamp-s2-wet-world-design.md 2). Owns the camera-centred wet map (R film, G soak, B standing water, A rain now; see
/// <see cref="RingWetWorld.Gpu"/> and scripts/render/ring_wet_map.glsl) and every ring_wet_* shader global that materials/ring_wetness.gdshaderinc reads.
/// The map is stateless: each texel is recomputed from S3's rain history snapshot plus local drying, so there is no rain integrator, no persistence and
/// no rewind handling. Receivers add shelter from the S1 precipitation occlusion map themselves. valid = 0 (the fast path) makes every receiver skip all wet
/// work, so clear weather costs nothing and the output is bitwise the pre-wet-world output.
/// Globals: ring_wet_map (sampler2D RGBA16F), ring_wet_map_frame = (camera offset from the published map corner around, across in m, texel m, size),
/// ring_wet_frame (mat4: columns 0/1 = around/across axis.xyz + camera canonical metres modulo 16384, column 2 = up axis.xyz + ring radius, column 3 = camera-ring centre in world),
/// ring_wet_params = (wetness strength, wet roughness, puddle amount, valid), ring_wet_clock = (ripple phase = sim clock mod 64 s, ripple layers, window half extent m, far blend m),
/// ring_wet_events = (FatDrop, HailIntensity, HailSize, hail cover), ring_wet_wind = (world wind direction xyz, wind speed 0..1), ring_wet_ripple_tex, ring_wet_noise_tex, ring_wet_debug_mode.
/// </summary>
public partial class RingWetWorld : Node
{
    public static RingWetWorld? Instance { get; private set; }

    [Export] public NodePath GridPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath WeatherPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath BridgePath { get; set; } = new("../SunshineCloudBridge");
    [Export] public NodePath EffectsPath { get; set; } = new("../LocalWeatherEffects");
    [Export] public NodePath SunPath { get; set; } = new("../StarLight");
    [Export] public bool Enabled { get; set; } = true;
    [Export] public string PreferencesPath { get; set; } = GameplayWetWorldDefaults.UserPath;

    public static readonly StringName GlobalMap = "ring_wet_map", GlobalMapFrame = "ring_wet_map_frame", GlobalFrame = "ring_wet_frame", GlobalParams = "ring_wet_params",
        GlobalClock = "ring_wet_clock", GlobalEvents = "ring_wet_events", GlobalWind = "ring_wet_wind", GlobalRippleTexture = "ring_wet_ripple_tex",
        GlobalNoiseTexture = "ring_wet_noise_tex", GlobalDebugMode = "ring_wet_debug_mode";

    /// <summary>Seconds between map dispatches (and a dispatch whenever the texel window moves).</summary>
    public const double RefreshSeconds = 0.5;
    public const float FarBlendMeters = 1000.0f;
    public const double ClockPeriodSeconds = 64.0;
    private const double DryHoldSeconds = 4.0;
    private const double DryProbeSeconds = 1.0;

    /// <summary>Camera override for tests and captures (default: the main viewport's current camera).</summary>
    public Camera3D? CameraSource { get; set; }
    /// <summary>True while ring_wet_params.w is 1 (receivers evaluate wetness).</summary>
    public bool Valid { get; private set; }
    public long DispatchCount { get; private set; }
    public WetMapFrame PublishedFrame { get; private set; }
    public bool HasPublishedFrame { get; private set; }
    /// <summary>Camera-level wetness: the CPU mirror of the map at the camera texel (S3 observer history + local drying), shelter applied. Consumers: StandardMaterial3D tint, lens droplets.</summary>
    public WetnessState CameraWetness { get; private set; } = WetnessState.Dry;
    /// <summary>Instantaneous rain at the camera x exposure (0..1), the lens and drip activity input.</summary>
    public float CameraRainNow { get; private set; }
    public float CameraExposure { get; private set; } = 1.0f;
    /// <summary>CameraWetness as (film, soak, standing, rain-now term) for scripts (a record struct is not a Variant).</summary>
    public Vector4 CameraWetnessVector => new(CameraWetness.Film, CameraWetness.Soak, CameraWetness.Standing, CameraWetness.RainNowTerm);
    public float CameraDryingRate { get; private set; }
    /// <summary>Test hook: pins the sun visibility used by the drying rate (CPU and GPU) and disables the storm map in the compute (rain now and the storm-map sun term are then zero). Negative = off.</summary>
    public float SunOverride { get; set; } = -1.0f;
    /// <summary>Mean wind speed (m/s) feeding the drying rate.</summary>
    public float WindMetersPerSecond => _windMetersPerSecond;
    /// <summary>World-space up axis at the camera (from the ring frame); the lens droplets use it for the look-up gain.</summary>
    public Vector3 CameraUpWorld { get; private set; } = Vector3.Up;

    private VoxelChunkGridManager? _grid;
    private RingVolumetricCloudLayer? _weather;
    private RingSunshineCloudBridge? _bridge;
    private RingLocalWeatherEffects? _effects;
    private DirectionalLight3D? _sun;
    private bool _textured, _forced;
    private Vector4 _forceValue;
    private double _lastDispatchSeconds = double.NegativeInfinity, _lastDispatchSimTime = double.NaN;
    private double _lastDryProbeSeconds = double.NegativeInfinity, _lastWetSeconds = double.NegativeInfinity;
    private bool _probeWet;
    private RingCanonicalPosition _lastDispatchCamera;
    private RainHistorySnapshot? _snapshotOverride;
    private RingCanonicalPosition _snapshotOverrideObserver;
    private Vector4 _paramsPublished = new(float.NaN, 0, 0, 0), _clockPublished = new(float.NaN, 0, 0, 0), _eventsPublished = new(float.NaN, 0, 0, 0), _windPublished = new(float.NaN, 0, 0, 0), _mapFramePublished = new(float.NaN, 0, 0, 0);
    private Projection _framePublished;
    private bool _framePublishedSet;
    private float _debugModePublished = float.NaN;
    /// <summary>Debug view of one channel (0 off, 1 film, 2 soak, 3 standing, 4 rain, 5 exposure), see ring_wetness.gdshaderinc.</summary>
    public int DebugMode { get; set; }

    public override void _EnterTree()
    {
        if (GameplayWetWorldDefaults.StartupDefaults?.GetPath() != PreferencesPath)
        {
            RingWetWorldSettings.Current = new RingWetWorldSettings();
            GameplayWetWorldDefaults.ApplyStartup(PreferencesPath);
        }
    }

    public override void _Ready()
    {
        Instance?.ReleaseOnExit();
        Instance = this;
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridPath);
        _weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherPath);
        _bridge = GetNodeOrNull<RingSunshineCloudBridge>(BridgePath);
        _effects = GetNodeOrNull<RingLocalWeatherEffects>(EffectsPath);
        _sun = GetNodeOrNull<DirectionalLight3D>(SunPath);
        EnsureTextures();
    }

    public override void _ExitTree()
    {
        if (!ReferenceEquals(Instance, this)) return;
        ReleaseOnExit();
        Instance = null;
    }

    private void ReleaseOnExit()
    {
        PublishParams(false);
        ReleaseGpu();
    }

    private const string SsrHalfSizeSetting = "rendering/environment/screen_space_reflection/half_size";
    private int _ssrHalfSizeApplied = -1; // -1 never set by the wet world, 0 / 1 last value it set
    /// <summary>True while the wet world holds the engine SSR at half size (tests and perf captures).</summary>
    public bool SsrHalfSizeActive => _ssrHalfSizeApplied == 1;

    /// <summary>Half-size engine SSR while wet on the presets that ask for it (<see cref="RingWetWorldSettings.HalfSizeSsr"/>); otherwise, and when dry, the project / hardware-tier value.
    /// Dry frames therefore keep the exact pre-wet-world SSR. Only writes on a change (the renderer setting is global).</summary>
    private void ApplySsrHalfSize(bool wet)
    {
        bool projectHalf = ProjectSettings.GetSetting(SsrHalfSizeSetting, false).AsBool();
        bool want = projectHalf || (wet && RingWetWorldSettings.Current.HalfSizeSsr);
        int value = want ? 1 : 0;
        if (value == _ssrHalfSizeApplied || (_ssrHalfSizeApplied < 0 && want == projectHalf)) return;
        _ssrHalfSizeApplied = value;
        RenderingServer.EnvironmentSetSsrHalfSize(want);
    }

    /// <summary>Test/tool hook: bypasses the history and the storm map; every texel gets exactly these values (and the map stays valid).</summary>
    public void ForceUniform(float film, float soak, float standing, float rain)
    {
        _forced = true;
        _forceValue = new Vector4(film, soak, standing, rain);
        _lastDispatchSeconds = double.NegativeInfinity;
    }

    public void ClearForce() { _forced = false; _lastDispatchSeconds = double.NegativeInfinity; }
    public bool IsForced => _forced;

    /// <summary>Test hook: evaluates the map from this snapshot (observer = where the snapshot's offsets are measured from) instead of S3's observer snapshot.</summary>
    public void SetSnapshotOverride(RainHistorySnapshot? snapshot, RingCanonicalPosition observer)
    {
        _snapshotOverride = snapshot;
        _snapshotOverrideObserver = observer;
        _lastDispatchSeconds = double.NegativeInfinity;
    }

    private Vector3? ObserverWorld()
    {
        Camera3D? camera = CameraSource != null && IsInstanceValid(CameraSource) ? CameraSource : GetViewport()?.GetCamera3D();
        return camera != null && IsInstanceValid(camera) ? camera.GlobalPosition : null;
    }

    public override void _Process(double delta)
    {
        if (!ReferenceEquals(Instance, this)) return;
        _grid ??= GetNodeOrNull<VoxelChunkGridManager>(GridPath);
        _weather ??= GetNodeOrNull<RingVolumetricCloudLayer>(WeatherPath);
        _bridge ??= GetNodeOrNull<RingSunshineCloudBridge>(BridgePath);
        _effects ??= GetNodeOrNull<RingLocalWeatherEffects>(EffectsPath);
        _sun ??= GetNodeOrNull<DirectionalLight3D>(SunPath);
        if (!Enabled || _grid == null || _grid.TerrainSampler == null) { PublishParams(false); RingStormLightTint.ApplyWetness(0.0f, 0.0f, 1.0f, 0.18f); return; }
        Vector3? world = ObserverWorld();
        if (world == null) { PublishParams(false); return; }
        RingCanonicalPosition camera = _grid.WorldToCanonicalRingPosition64(world.Value);
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        double circumference = geometry.CircumferenceMeters;
        WetMapFrame frame = WetMapFrame.Create(camera.AroundMeters, camera.AcrossMeters, circumference);
        double now = Time.GetTicksMsec() / 1000.0;
        double simTime = _weather?.CloudSimulationTimeSeconds ?? 0.0;

        UpdateEvents();
        UpdateWindAndClock(frame, geometry, simTime);
        UpdateCameraWetness(frame, geometry, camera, simTime);
        RingStormLightTint.ApplyWetness(CameraWetness.Soak, CameraWetness.Film, RingWetWorldSettings.Current.Wetness, RingWetWorldSettings.Current.WetGloss);
        bool wetPossible = _forced || IsWetPossible(camera, now);
        if (wetPossible) _lastWetSeconds = now;
        bool valid = _forced || (now - _lastWetSeconds < DryHoldSeconds);
        bool dispatched = false;
        if (valid && EnsureGpu() && GpuReady)
        {
            bool moved = !HasPublishedFrame || !PublishedFrame.SameWindow(frame);
            bool timeJump = double.IsNaN(_lastDispatchSimTime) || Math.Abs(simTime - _lastDispatchSimTime) > 5.0;
            if (moved || timeJump || now - _lastDispatchSeconds >= RefreshSeconds)
            {
                dispatched = DispatchMap(frame, camera, simTime, now, moved, timeJump, wetPossible);
            }
        }
        PublishFrameGlobals(frame, geometry);
        PublishParams(valid && HasPublishedFrame && MapPublished);
        _ = dispatched;
    }

    private void PublishFrameGlobals(in WetMapFrame frame, in RingLodGeometrySnapshot geometry)
    {
        Projection matrix = frame.WorldFrame(geometry);
        if (!_framePublishedSet || !matrix.Equals(_framePublished))
        {
            _framePublished = matrix; _framePublishedSet = true;
            RenderingServer.GlobalShaderParameterSet(GlobalFrame, matrix);
        }
        if (HasPublishedFrame)
        {
            Vector2 offset = PublishedFrame.WithCamera(frame.CameraAround, frame.CameraAcross).CameraOffsetMeters;
            var mapFrame = new Vector4(offset.X, offset.Y, WetMapFrame.TexelMeters, WetMapFrame.Size);
            if (mapFrame != _mapFramePublished) { _mapFramePublished = mapFrame; RenderingServer.GlobalShaderParameterSet(GlobalMapFrame, mapFrame); }
        }
        if (_debugModePublished != DebugMode) { _debugModePublished = DebugMode; RenderingServer.GlobalShaderParameterSet(GlobalDebugMode, (float)DebugMode); }
    }

    private void PublishParams(bool valid)
    {
        RingWetWorldSettings s = RingWetWorldSettings.Current;
        float strength = Mathf.Clamp(float.IsFinite(s.Wetness) ? s.Wetness : 1.0f, 0.0f, 1.5f);
        float gloss = Mathf.Clamp(float.IsFinite(s.WetGloss) ? s.WetGloss : 0.18f, 0.05f, 0.6f);
        float puddles = Mathf.Clamp(float.IsFinite(s.Puddles) ? s.Puddles : 1.0f, 0.0f, 2.0f);
        Valid = valid && strength > 0.0f;
        ApplySsrHalfSize(Valid);
        var value = new Vector4(strength, gloss, puddles, Valid ? 1.0f : 0.0f);
        if (value != _paramsPublished) { _paramsPublished = value; RenderingServer.GlobalShaderParameterSet(GlobalParams, value); }
    }

    private void UpdateEvents()
    {
        StormArrivalState arrival = StormArrivalMath.Observer;
        float fat = arrival.Valid ? arrival.FatDrop : 0.0f;
        float hail = arrival.Valid ? arrival.HailIntensity : 0.0f;
        var events = new Vector4(fat, hail, arrival.Valid ? arrival.HailSize : 0.0f, 0.0f);
        if (events != _eventsPublished) { _eventsPublished = events; RenderingServer.GlobalShaderParameterSet(GlobalEvents, events); }
    }

    private void UpdateWindAndClock(in WetMapFrame frame, in RingLodGeometrySnapshot geometry, double simTime)
    {
        Vector2 wind = _effects != null && _effects.ResolvedWindMetersPerSecond.LengthSquared() > 1e-4f ? _effects.ResolvedWindMetersPerSecond : (_weather?.WeatherWindMetersPerSecond ?? Vector2.Zero);
        Projection matrix = frame.WorldFrame(geometry);
        Vector3 around = new(matrix.X.X, matrix.X.Y, matrix.X.Z), across = new(matrix.Y.X, matrix.Y.Y, matrix.Y.Z);
        CameraUpWorld = new Vector3(matrix.Z.X, matrix.Z.Y, matrix.Z.Z);
        Vector3 direction = wind.LengthSquared() > 1e-6f ? (around * wind.X + across * wind.Y).Normalized() : Vector3.Zero;
        var windValue = new Vector4(direction.X, direction.Y, direction.Z, Mathf.Clamp(wind.Length() / 12.0f, 0.0f, 1.0f));
        if (windValue != _windPublished) { _windPublished = windValue; RenderingServer.GlobalShaderParameterSet(GlobalWind, windValue); }
        // The drying rate uses the mean weather wind (constant between gusts), not the gusting local wind: the map stays a pure function of the clock.
        _windMetersPerSecond = (_weather?.WeatherWindMetersPerSecond ?? wind).Length();
        float phase = (float)WetMapFrame.Mod(simTime, ClockPeriodSeconds);
        var clock = new Vector4(phase, Mathf.Clamp(RingWetWorldSettings.Current.RippleLayers, 1, 4), WetMapFrame.ExtentMeters * 0.5f, FarBlendMeters);
        if (clock != _clockPublished) { _clockPublished = clock; RenderingServer.GlobalShaderParameterSet(GlobalClock, clock); }
    }

    private float _windMetersPerSecond;

    /// <summary>Sun visibility 0..1 at the camera for the drying rate: the day factor of the ring (sun elevation) times the clear-sky fraction from the camera storm level.</summary>
    private float CameraSun(in WetMapFrame frame, in RingLodGeometrySnapshot geometry)
    {
        if (SunOverride >= 0.0f) { _cameraDay = SunOverride; return SunOverride; }
        if (_sun == null || _grid == null) return 0.5f;
        Projection matrix = frame.WorldFrame(geometry);
        Vector3 up = new(matrix.Z.X, matrix.Z.Y, matrix.Z.Z);
        Vector3 toSun = _sun.GlobalBasis.Z.Normalized();
        float dot = up.Dot(toSun);
        float day = WetnessModel.SmoothStep(_grid.RingDayStartDot, Mathf.Max(_grid.RingDayStartDot + 0.001f, _grid.RingDayFullDot), dot);
        float storm = _bridge != null ? _bridge.GetCameraStormLevel() : 0.0f;
        _cameraDay = day;
        return day * (1.0f - Mathf.Clamp(storm, 0.0f, 1.0f));
    }

    private void UpdateCameraWetness(in WetMapFrame frame, in RingLodGeometrySnapshot geometry, RingCanonicalPosition camera, double simTime)
    {
        RingWetWorldSettings s = RingWetWorldSettings.Current;
        float rain = _effects != null && float.IsFinite(_effects.PrecipitationIntensity) ? Mathf.Clamp(_effects.PrecipitationIntensity, 0.0f, 1.0f) : 0.0f;
        bool snowing = _effects?.Snowing ?? false;
        IPrecipitationCoverProbe? probe = PrecipitationCover.Probe;
        float exposure = probe is { IsFresh: true } ? Mathf.Clamp(probe.ListenerOpenFraction, 0.0f, 1.0f) : 1.0f;
        RainHistorySample history = _weather != null ? _weather.ObserverRainHistory : RainHistorySample.None;
        float sun = CameraSun(frame, geometry);
        float prior = -1.0f;
        if (_weather != null && _weather.TryGetRainHistorySnapshot(out RainHistorySnapshot snapshot))
            prior = WetnessModel.PriorRain(WetnessModel.LagProxies(snapshot, Vector2.Zero, RingStormArrivalSettings.Current.ToTuning(RingVolumetricCloudLayer.StormArrivalPrecipitationStart, _weather.Coverage)), history.TimeSinceRainSeconds,
                RingStormArrivalSettings.Current.ToTuning(RingVolumetricCloudLayer.StormArrivalPrecipitationStart, _weather.Coverage));
        var input = new WetnessInputs(history.RecentRainAmount, history.TimeSinceRainSeconds, rain, sun, _windMetersPerSecond, s.DryingSpeed, snowing, prior);
        WetnessState state = _forced ? new WetnessState(_forceValue.X, _forceValue.Y, _forceValue.Z, _forceValue.W) : WetnessModel.Evaluate(input);
        CameraRainNow = rain * exposure;
        CameraExposure = exposure;
        CameraDryingRate = WetnessModel.DryingRate(sun, _windMetersPerSecond, s.DryingSpeed);
        // Shelter: film and soak of a roofed camera follow the shelter, rain-now terms are already scaled by exposure.
        CameraWetness = new WetnessState(state.Film * Mathf.Lerp(0.35f, 1.0f, exposure), state.Soak * Mathf.Lerp(0.5f, 1.0f, exposure), state.Standing * exposure, state.RainNowTerm * exposure);
        _cameraSun = sun;
    }

    private float _cameraSun, _cameraDay = 0.5f;
    public float CameraSunVisibility => _cameraSun;

    /// <summary>Dry fast path: false only when no system with rain potential can reach the 16 km window at any lag, the camera is not rained on and no probe around the camera shows precipitation.</summary>
    private bool IsWetPossible(RingCanonicalPosition camera, double now)
    {
        if (_effects != null && _effects.PrecipitationIntensity > 0.001f) return true;
        if (_effects?.Snowing == true) return false;
        StormArrivalState arrival = StormArrivalMath.Observer;
        if (arrival.Valid && (arrival.FatDrop > 0.01f || arrival.HailIntensity > 0.01f || arrival.Core + arrival.Trailing > 0.01f)) return true;
        if (now - _lastDryProbeSeconds < DryProbeSeconds) return _probeWet;
        _lastDryProbeSeconds = now;
        _probeWet = ProbeWet(camera);
        return _probeWet;
    }

    private bool ProbeWet(RingCanonicalPosition camera)
    {
        if (_weather == null) return false;
        if (_weather.TryGetRainHistorySnapshot(out RainHistorySnapshot snapshot))
        {
            for (int l = 0; l < snapshot.LagCount; l++)
                for (int i = 0; i < snapshot.Counts[l]; i++)
                {
                    StormSystemGeometry g = snapshot.Systems[l * RainHistorySnapshot.MaxSystemsPerLag + i];
                    if (g.PrecipitationPotential <= 0.05f) continue;
                    float reach = Mathf.Max(g.MajorRadiusMeters, g.MinorRadiusMeters);
                    if (g.OffsetMeters.Length() - reach < 12000.0f) return true;
                }
        }
        float start = _effects != null ? Mathf.Clamp(_effects.PrecipitationStart, 0.0f, 0.99f) : 0.18f;
        const double Spacing = 6000.0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var position = new RingCanonicalPosition(camera.AroundMeters + dx * Spacing, camera.VerticalMeters, Math.Clamp(camera.AcrossMeters + dy * Spacing, 0.0, Math.Max(1.0, _grid!.RingAcrossWidthMeters)));
                if (_weather.QueryLocalWeather(position).PrecipitationPotential > start * 0.9f) return true;
            }
        return false;
    }
}
