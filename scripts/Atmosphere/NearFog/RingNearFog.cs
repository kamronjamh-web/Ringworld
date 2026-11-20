using System;
using Godot;
using RingworldTechDemo.Shadows;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Atmosphere.NearFog;

/// <summary>
/// Owner of the near ground fog (docs/sun-flare-shafts-near-fog-plan.md, NF-1 design N1, N6): the only writer of the main Environment's volumetric fog while registered,
/// the one FogVolume (<c>FOG_VOLUME_SHAPE_WORLD</c>, materials/ring_near_fog.gdshader) and the drivers that feed it.
/// Dormant by default: <see cref="Amount"/> is 0 until NF-6 adds the menu row and the preset default. At amount 0 (and after any fade-out) the Environment's volumetric fog is
/// disabled and the FogVolume hidden, so the frame is byte-identical to the feature absent (measured in NF-0).
/// Per 0.5 s: <see cref="RingNearFogInputsProbe.Gather"/> -> <see cref="RingNearFogDrivers.Evaluate"/> -> <see cref="NearFogDriverSmoother"/> (reset on a teleport above 200 m in one frame), and the CPU terrain estimate of the altitude gate.
/// Per frame (only while active or fading): water field update and publish, noise offset, the gate, material uniforms (set only when changed).
/// Gate (N6) = (1 - enclosure weight) x fast-travel fade (ground speed 30 to 60 m/s) x altitude fade, times the owner fade (2 s in and out).
/// Altitude fade: the CPU estimate of the reach top = max(B + H, T0 + H_m), with T0 the sampler's presentation surface height at the camera (clamped to sea level like the height-shadow source) and
/// B the minimum of T0 over the camera and rings of 8 points at 160 m and 320 m (an estimate of the GPU basin floor, which is the same footprint blurred; it can only be a little higher than B);
/// the fade is 1 up to the top and 0 at top + 2 H (smoothstep), so "the camera above top + 2 H" gives no fog.
/// Active when amount x max_k(sigma_k D_k) x gate >= <see cref="ActivationThreshold"/> per metre; the steam term counts only with water in the near-water field (natural drive; forced drives always count it).
/// While the legacy local volumetric cloud renderer is enabled the near fog does not register and never activates (they are mutually exclusive; the legacy path is not production).
/// </summary>
public partial class RingNearFog : Node
{
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath WorldEnvironmentPath { get; set; } = new("../WorldEnvironment");
    [Export] public NodePath DaySidePath { get; set; } = new("../DaySideAtmosphere");
    [Export] public NodePath LocalWeatherPath { get; set; } = new("../LocalWeatherEffects");
    [Export] public NodePath WeatherPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath BridgePath { get; set; } = new("../SunshineCloudBridge");
    [Export] public NodePath DayNightPath { get; set; } = new("../RingDayNightCycle");
    [Export] public NodePath LegacyRendererPath { get; set; } = new("../LocalVolumetricCloudRenderer");

    /// <summary>"Ground fog amount" (0 = off and byte-identical). Persisted by the Atmosphere menu (NF-6); default 1, and the preset gate (RingNearFog.Presets.cs) keeps it off below High.</summary>
    public static float Amount { get; set; } = 1.0f;
    /// <summary>"Fog layer height" H, metres (menu row in NF-6).</summary>
    public static float LayerHeight { get; set; } = 40.0f;

    /// <summary>True while a RingNearFog is in the tree and not standing down for the legacy renderer: the cloud layer and the legacy renderer then leave the Environment's volumetric fog to it.</summary>
    public static bool OwnerRegistered { get; private set; }
    public static RingNearFog? Instance { get; private set; }

    // User 2026-11-20 (NF-7): 5e-4 per metre (~10 % opacity over the 220 m window); at 1e-4 the fog was ~2 % opaque, an invisible veil for +0.38 ms.
    public const float ActivationThreshold = 5e-4f;
    public const float FadeSeconds = 2.0f;
    public const float ProbeSeconds = 0.5f;
    public const float TeleportMeters = 200.0f;
    public const float SpeedFadeStart = 30.0f, SpeedFadeEnd = 60.0f;
    // Environment while active (N6).
    public const float FogLengthMeters = 220.0f;
    public const float DetailSpread = 2.0f, TemporalAmount = 0.9f, Anisotropy = 0.7f;
    public const string MaterialShaderPath = "res://materials/ring_near_fog.gdshader";
    private const double NoisePeriod = 4096.0;
    private static readonly float[] BasinRadii = { 160f, 320f };

    // Test and capture hooks (not menu rows). Plain C# properties: GDScript reads and writes them through get() / set().
    /// <summary>All components negative = off (the world's drivers). Otherwise the smoothed drivers are replaced by (radiation, mist, steam), each clamped to 0..1.</summary>
    public Vector3 ForcedDrive { get; set; } = new(-1f, -1f, -1f);
    /// <summary>Negative = off. Otherwise replaces the world gate (enclosure, speed, altitude); the owner fade still applies.</summary>
    public float ForcedGate { get; set; } = -1f;

    /// <summary>Test and perf hook: true = amount at least 1, ForcedDrive (1, 0.5, 0) and gate 1 (a plain bool, so an A/B variant list can switch it).</summary>
    public bool DebugForceFog { get; set; }

    /// <summary>Calibration hook: &gt;= 0 replaces the density of the froxels and of the fog end with this constant per metre inside the window (no terrain terms, no noise; the drivers still gate activation).</summary>
    public float DebugUniformSigma { get; set; } = -1f;
    /// <summary>Cost hook (NF-5): the froxels use one haze sun transmittance evaluated at the camera's altitude instead of one per froxel (the fog-end pass keeps its own per-sample evaluation).</summary>
    public bool DebugHazeCameraValue { get; set; }
    /// <summary>Test and perf hook: false keeps the fog-end pass detached (NF-2b alone).</summary>
    public bool EndPassEnabled { get; set; } = true;
    /// <summary>Test hook: the fog-end pass paints every main-empty pixel (0.8, 0, 0.8) in the HDR colour, so a capture shows which pixels are sky or far backdrop.</summary>
    public bool DebugMaskEmpty { get; set; }
    /// <summary>Perf hook: the fog-end pass runs pass A only (no apply), to split the cost of the two passes.</summary>
    public bool DebugSkipEndApply { get; set; }
    public bool EndAttached => _endAttached;
    public CompositorEffect? EndEffect => _end;
    /// <summary>The far backdrop composite was drawing as of the last fog-end publish (enabled and active): the fog end then fogs the main-empty pixels.</summary>
    public bool EndBackdropDrawing { get; private set; }

    public bool FogEnabled => _fogOn;
    public bool StoodDown => _stoodDown;
    public float Fade => _fade;
    public float LastWorldGate { get; private set; }
    public float LastRate { get; private set; }
    public Vector3 CurrentDrive => _drive;
    public RingNearFogWaterField? WaterField => _water;

    /// <summary>Test hook (GDScript cannot read <see cref="WaterField"/>, a plain C# object): water texels (mask above 0.5) in the current near-water field, -1 while none is built.</summary>
    public int DebugWaterTexelCount()
    {
        if (_water == null || !_water.HasField) return -1;
        ReadOnlySpan<float> data = _water.Data;
        int count = 0;
        for (int i = 0; i < data.Length; i += 2)
        {
            if (data[i] > 0.5f) count++;
        }
        return count;
    }

    /// <summary>Test hook: (mask, water surface height) of the near-water texel at the field centre, the camera's canonical height, and the lowest water surface in the field (NaN while none is built).</summary>
    public Vector4 DebugWaterProbe()
    {
        if (_water == null || !_water.HasField) return new Vector4(float.NaN, float.NaN, _cameraVertical, float.NaN);
        var (mask, height) = _water.Texel(RingNearFogWaterField.Size / 2, RingNearFogWaterField.Size / 2);
        ReadOnlySpan<float> data = _water.Data;
        float lowest = float.PositiveInfinity;
        for (int i = 0; i < data.Length; i += 2)
        {
            if (data[i] > 0.5f) lowest = Mathf.Min(lowest, data[i + 1]);
        }
        return new Vector4(mask, height, _cameraVertical, lowest);
    }

    public void DebugSetAmount(float amount) => Amount = amount;
    public void DebugSetLayerHeight(float height) => LayerHeight = height;

    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private WorldEnvironment? _worldEnvironment;
    private Node? _daySide;
    private RingLocalVolumetricCloudRenderer? _legacy;
    private readonly RingNearFogInputsProbe _probe = new();
    private readonly NearFogDriverSmoother _smoother = new();
    private RingNearFogWaterField? _water;
    private RingTerrainSampler? _waterSampler;
    private FogVolume? _volume;
    private ShaderMaterial? _material;

    // Fog end (NF-4): the compositor effect, its slot bookkeeping and the cached render-device texture handles.
    public const string EndScriptPath = "res://scripts/render/RingNearFogEndComposite.gd";
    public const string EndComputePath = "res://materials/ring_near_fog_end.comp";
    private const int EndPayloadFloats = 156;
    private const float AlbedoOmega = 0.99f; // RING_NEAR_FOG_OMEGA in ring_near_fog_common.gdshaderinc
    private CompositorEffect? _end;
    private bool _endAttached, _endFailed;
    private WorldEnvironment? _endEnvironment;
    private GodotObject? _endSunshine, _endFar;
    private int _endSlotCountdown;
    private readonly float[] _endPayload = new float[EndPayloadFloats];
    private Rid _endT0, _endBasin, _endWater;
    private Texture2D? _endWaterTexture;
    private double _endRidElapsed = double.PositiveInfinity;
    private int _endWaterCountdown;
    private DirectionalLight3D? _sun;
    private Vector2 _noiseOffset;

    private bool _stoodDown, _standDownLogged;
    private bool _fogOn;
    private float _fade;
    private double _probeElapsed = double.PositiveInfinity;
    private Vector3 _drive;
    private float _groundTop = float.PositiveInfinity; // CPU estimate of the reach top (canonical metres), updated with the probe
    private float _cameraVertical;
    private bool _hasLastPosition;
    private double _lastAround, _lastAcross;
    private double _windAround, _windAcross;
    private Vector2 _wind;

    // Saved Environment state.
    private bool _saved;
    private float _prevDensity, _prevLength, _prevDetail, _prevGiInject, _prevAmbientInject, _prevAnisotropy, _prevSkyAffect, _prevTemporalAmount;
    private bool _prevTemporal;

    // Lighting inputs (NF-5), one set per frame for the froxel shader's uniforms and the fog-end payload (see ring_near_fog_light.gdshaderinc).
    private Vector4 _haze0, _haze1, _light0, _light1, _light2, _tOverride;
    private Vector3 _ambient;
    private Vector4 _planetDir, _planetRad, _setPlanetDir = new(float.NaN, 0, 0, 0), _setPlanetRad = new(float.NaN, 0, 0, 0), _setAmbient = new(float.NaN, 0, 0, 0);
    private DirectionalLight3D? _planet;
    // The sun's own fog energy (see UpdateLighting): written only when the on / off decision changes, restored to Godot's default 1 when the fog deactivates.
    private float _sunFogGate = float.NaN;
    private bool _sunFogTouched;
    private Vector4 _setHaze0 = new(float.NaN, 0, 0, 0), _setHaze1 = new(float.NaN, 0, 0, 0), _setLight0 = new(float.NaN, 0, 0, 0), _setLight1 = new(float.NaN, 0, 0, 0), _setLight2 = new(float.NaN, 0, 0, 0), _setTOverride = new(float.NaN, 0, 0, 0);

    // Last values written to the material.
    private Vector3 _setDrive = new(float.NaN, 0, 0);
    private float _setAmount = float.NaN, _setGate = float.NaN, _setLayer = float.NaN, _setWindow = float.NaN;
    private Vector2 _setOffset = new(float.NaN, 0);
    private float _setDebugSigma = float.NaN;
    private Vector3 _setCamera = new(float.NaN, 0, 0);

    public override void _EnterTree()
    {
        _legacy = GetNodeOrNull<RingLocalVolumetricCloudRenderer>(LegacyRendererPath);
        if (_legacy != null && _legacy.Enabled)
        {
            StandDown();
            return;
        }
        OwnerRegistered = true;
        Instance = this;
    }

    public override void _Ready()
    {
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _worldEnvironment = GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath);
        _daySide = GetNodeOrNull(DaySidePath);
        _probe.Grid = _grid;
        _probe.Effects = GetNodeOrNull<RingLocalWeatherEffects>(LocalWeatherPath);
        _probe.Weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherPath);
        _probe.Bridge = GetNodeOrNull<RingSunshineCloudBridge>(BridgePath);
        _probe.DayNight = GetNodeOrNull<RingDayNightCycle>(DayNightPath);
        if (_stoodDown || !OwnerRegistered) return;
        // The writes the cloud layer and the legacy renderer made at start (they now skip them): volumetric fog off, density 0.
        if (_worldEnvironment?.Environment is { } environment)
        {
            environment.VolumetricFogEnabled = false;
            environment.VolumetricFogDensity = 0.0f;
        }
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
            OwnerRegistered = false;
        }
        Deactivate();
        _water?.Dispose();
        _water = null;
        _waterSampler?.Dispose();
        _waterSampler = null;
        _volume?.QueueFree();
        _volume = null;
        _material = null;
        _end = null;
    }

    private void StandDown()
    {
        _stoodDown = true;
        OwnerRegistered = false;
        if (_standDownLogged) return;
        _standDownLogged = true;
        GD.Print("Near ground fog stands down: the legacy local volumetric cloud renderer is enabled (they are mutually exclusive).");
    }

    public override void _Process(double delta)
    {
        if (_stoodDown) return;
        if (_legacy != null && _legacy.Enabled)
        {
            StandDown();
            Deactivate();
            return;
        }
        float amount = float.IsFinite(Amount) ? Mathf.Max(0f, Amount) : 0f;
        if (DebugForceFog) amount = Mathf.Max(amount, 1f);
        if (!PresetAllowsFog) amount = 0f; // NF-6 preset gate: Low and Medium are inactive (byte-identical to the feature absent)
        if (amount <= 0f && !_fogOn && _fade <= 0f) return; // dormant: no work at all
        if (_explorer == null || _worldEnvironment?.Environment == null) return;

        float dt = (float)Math.Max(0.0, delta);
        RingCanonicalPosition position = _explorer.CanonicalPosition;
        _cameraVertical = (float)position.VerticalMeters;
        bool teleported = _hasLastPosition && (Math.Abs(position.AroundMeters - _lastAround) > TeleportMeters || Math.Abs(position.AcrossMeters - _lastAcross) > TeleportMeters);
        _lastAround = position.AroundMeters;
        _lastAcross = position.AcrossMeters;
        _hasLastPosition = true;

        Vector3 forcedDrive = DebugForceFog ? new Vector3(1f, 0.5f, 0f) : ForcedDrive;
        bool forced = forcedDrive.X >= 0f || forcedDrive.Y >= 0f || forcedDrive.Z >= 0f;
        _probeElapsed += dt;
        if (teleported || _probeElapsed >= ProbeSeconds)
        {
            if (forced)
            {
                _drive = new Vector3(Mathf.Clamp(forcedDrive.X, 0f, 1f), Mathf.Clamp(forcedDrive.Y, 0f, 1f), Mathf.Clamp(forcedDrive.Z, 0f, 1f));
            }
            else
            {
                NearFogDrive target = RingNearFogDrivers.Evaluate(_probe.Gather(position));
                if (teleported) { _smoother.Reset(target); _probe.Reset(); }
                else _smoother.Update((float)_probeElapsed, target);
                _drive = new Vector3(_smoother.Radiation, _smoother.Mist, _smoother.Steam);
            }
            _probeElapsed = 0.0;
            // The altitude gate only scales a non-zero drive: clear dry air (drive 0) does no terrain sampling at all (the sampler's noise lock is shared with the streaming workers).
            if (_drive.X > 0f || _drive.Y > 0f || _drive.Z > 0f || _fogOn) UpdateGroundTop(position);
        }
        else if (forced)
        {
            _drive = new Vector3(Mathf.Clamp(forcedDrive.X, 0f, 1f), Mathf.Clamp(forcedDrive.Y, 0f, 1f), Mathf.Clamp(forcedDrive.Z, 0f, 1f));
        }

        float worldGate = DebugForceFog ? 1f : ForcedGate >= 0f ? Mathf.Clamp(ForcedGate, 0f, 1f) : ComputeGate();
        LastWorldGate = worldGate;
        // Keep the near-water field current while it can matter (the steam term needs to know whether water is near before the fog is on).
        if (_fogOn || (_drive.Z > 0f && worldGate > 0f)) UpdateWater(position);
        bool steamNear = forced || (_water != null && _water.HasField && _water.WaterTexelCount > 0);
        LastRate = ActivationRate(_drive, steamNear, amount, worldGate);
        bool want = LastRate >= ActivationThreshold;
        _fade = want ? Mathf.Min(1f, _fade + dt / FadeSeconds) : Mathf.Max(0f, _fade - dt / FadeSeconds);

        if (_fade > 0f && !_fogOn) Activate();
        if (_fogOn)
        {
            UpdateWind(dt);
            UpdateLighting();
            UpdateMaterial(amount, _fade * worldGate);
            UpdateEnd(delta, amount * _fade * worldGate);
        }
        if (_fade <= 0f && _fogOn) Deactivate();
    }

    /// <summary>Activation rate per metre: amount x max(0.01 radiation, 0.002 mist, 0.01 steam when water is near) x gate.</summary>
    public static float ActivationRate(Vector3 drive, bool steamWaterNear, float amount, float worldGate)
    {
        float sigmaDrive = Mathf.Max(0.01f * drive.X, Mathf.Max(0.002f * drive.Y, steamWaterNear ? 0.01f * drive.Z : 0f));
        return amount * sigmaDrive * worldGate;
    }

    private float ComputeGate()
    {
        float enclosure = 0f;
        if (_daySide != null && IsInstanceValid(_daySide))
        {
            Variant v = _daySide.Get("published_enclosed_fog_weight");
            if (v.VariantType == Variant.Type.Float) enclosure = Mathf.Clamp(v.AsSingle(), 0f, 1f);
        }
        float speed = _explorer != null ? _explorer.ActualTravelSpeedMetersPerSecond : 0f;
        float speedFade = 1f - Smooth(SpeedFadeStart, SpeedFadeEnd, speed);
        float h = Mathf.Max(1f, LayerHeight);
        float altitudeFade = float.IsInfinity(_groundTop) ? 1f : 1f - Smooth(_groundTop, _groundTop + 2f * h, _cameraVertical);
        return (1f - enclosure) * speedFade * altitudeFade;
    }

    /// <summary>The CPU estimate of max(B + H, T0 + H_m) described in the class summary; infinity (no altitude fade) when there is no terrain sampler.</summary>
    private void UpdateGroundTop(in RingCanonicalPosition position)
    {
        RingTerrainSampler? sampler = _grid != null && IsInstanceValid(_grid) ? _grid.TerrainSampler : null;
        if (sampler == null) { _groundTop = float.PositiveInfinity; return; }
        long around = (long)Math.Round(position.AroundMeters);
        int across = Mathf.Clamp(Mathf.RoundToInt((float)position.AcrossMeters), 0, Math.Max(0, sampler.RingAcrossWidthMeters));
        float sea = sampler.SeaLevelY;
        float t0 = RingTerrainHeightShadowSource.ClampToSeaLevel(sampler.GetPresentationSurfaceHeight(around, across), sea);
        float basin = t0;
        foreach (float radius in BasinRadii)
        {
            for (int k = 0; k < 8; k++)
            {
                float angle = k * Mathf.Pi * 0.25f;
                long a = around + (long)Math.Round(Mathf.Cos(angle) * radius);
                int c = Mathf.Clamp(across + Mathf.RoundToInt(Mathf.Sin(angle) * radius), 0, Math.Max(0, sampler.RingAcrossWidthMeters));
                basin = Mathf.Min(basin, RingTerrainHeightShadowSource.ClampToSeaLevel(sampler.GetPresentationSurfaceHeight(a, c), sea));
            }
        }
        float h = Mathf.Max(1f, LayerHeight);
        float hm = Mathf.Clamp(0.35f * h, 5f, 40f);
        _groundTop = Mathf.Max(basin + h, t0 + hm);
    }

    private void UpdateWind(float dt)
    {
        Vector2 wind = _wind;
        if (_probe.Effects != null && IsInstanceValid(_probe.Effects))
        {
            Vector2 resolved = _probe.Effects.ResolvedWindMetersPerSecond;
            wind = resolved.LengthSquared() > 1e-4f ? resolved : (_probe.Weather != null && IsInstanceValid(_probe.Weather) ? _probe.Weather.WeatherWindMetersPerSecond : Vector2.Zero);
        }
        else if (_probe.Weather != null && IsInstanceValid(_probe.Weather)) wind = _probe.Weather.WeatherWindMetersPerSecond;
        _wind = wind;
        // The noise pattern drifts with the wind: the sample point moves against it (x = around, y = across).
        _windAround = (_windAround - wind.X * (double)dt) % NoisePeriod;
        _windAcross = (_windAcross - wind.Y * (double)dt) % NoisePeriod;
    }

    private void UpdateWater(in RingCanonicalPosition position)
    {
        if (_water == null)
        {
            RingTerrainSampler? sampler = _grid?.CreateIndependentTerrainSampler();
            if (sampler == null) return;
            _waterSampler = sampler;
            _water = new RingNearFogWaterField(sampler.GetTerrainSample, sampler.AroundRingVoxelLength, sampler.RingAcrossWidthMeters);
        }
        _water.Update(position.AroundMeters, position.AcrossMeters);
        RingTerrainHeightShadow? shadow = RingTerrainHeightShadow.Instance;
        if (shadow != null) _water.Publish(shadow.PublishedAnchor.Around, shadow.PublishedAnchor.Across);
    }

    /// <summary>
    /// The lighting inputs of NF-5, from the same sources the surfaces use: the haze model's parameters (RingAtmosphereModel, storm applied to the aerosol like ring_haze_apply_storm), the haze
    /// ground albedo, the sun direction and the day weight (RingAtmosphereModel.LocalHazeDay of the local geometric day, with the solar visibility and hub day globals), ring up from the height-shadow
    /// frame, the sun light's colour x energy (0 while the light is off: no ground bounce) and A_up from the day-side atmosphere (ringworld_fog_ambient).
    /// </summary>
    private void UpdateLighting()
    {
        RingTerrainHeightShadow? shadow = RingTerrainHeightShadow.Instance;
        Vector3 up = Vector3.Up;
        if (shadow != null)
        {
            Projection frame = shadow.GetGlobalsSnapshot().Frame;
            Vector3 row = new(frame.X.Z, frame.Y.Z, frame.Z.Z);
            if (row.LengthSquared() > 0.5f) up = row.Normalized();
        }
        _sun ??= GetNodeOrNull<DirectionalLight3D>("../StarLight");
        bool sunOn = _sun != null && IsInstanceValid(_sun) && _sun.IsVisibleInTree();
        Vector3 toSun = _sun != null && IsInstanceValid(_sun) ? _sun.GlobalTransform.Basis.Z.Normalized() : up;
        // ALBEDO scales every directional light of the froxel, and the StarLight stays on (energy 4) below the horizon (the surface shaders weight it out themselves). Its fog energy is therefore
        // the sun's own on / off: 1 while any of the sun is above the haze's sun_vis edge (cos of the sun to ring up > -0.06, where ring_haze_sun_transmittance_point is 0 below), 0 otherwise,
        // so at night ALBEDO carries the planet light's factors only and cannot light the fog with the sun.
        float want = toSun.Dot(up) > -0.06f ? 1f : 0f;
        if (_sun != null && IsInstanceValid(_sun) && want != _sunFogGate)
        {
            _sunFogGate = want;
            _sunFogTouched = true;
            _sun.LightVolumetricFogEnergy = want;
        }
        float solarVisibility = DaySideFloat("current_solar_visibility", 1f), hubDay = DaySideFloat("current_hub_day", 0f);
        float day = RingAtmosphereModel.LocalHazeDay(-up, toSun, solarVisibility, hubDay);
        float sigmaA = RingAtmosphereModel.HazeSigmaAerosol, omegaA = RingAtmosphereModel.HazeOmegaAerosol, g = RingAtmosphereModel.HazeG;
        RingAtmosphereModel.ApplyStorm(RingAtmosphereModel.HazeStormVector, ref sigmaA, ref omegaA, ref g);
        Vector3 sigmaR = RingAtmosphereModel.HazeSigmaRayleigh;
        _haze0 = new Vector4(sigmaR.X, sigmaR.Y, sigmaR.Z, RingAtmosphereModel.HazeRayleighHeightMeters);
        _haze1 = new Vector4(sigmaA, RingAtmosphereModel.HazeAerosolHeightMeters, RingAtmosphereModel.HazeCapHeightMeters, RingAtmosphereModel.HazeGroundAlbedo);
        _light0 = new Vector4(toSun.X, toSun.Y, toSun.Z, day);
        _light1 = new Vector4(up.X, up.Y, up.Z, 0f);
        float volumetricEnergy = _sun != null && IsInstanceValid(_sun) ? _sun.LightVolumetricFogEnergy : 1f;
        Vector3 sunRadiance = Vector3.Zero;
        if (sunOn)
        {
            Color c = _sun!.LightColor.SrgbToLinear();
            // Godot's light data holds colour x energy x pi, and the froxel scales it by the light's volumetric fog energy.
            sunRadiance = new Vector3(c.R, c.G, c.B) * (_sun.LightEnergy * Mathf.Pi * volumetricEnergy);
        }
        _light2 = new Vector4(sunRadiance.X, sunRadiance.Y, sunRadiance.Z, Anisotropy);
        // The planet light (night) is a second directional light the froxels add with the same albedo (ring_near_fog_light_s).
        _planet ??= GetNodeOrNull<DirectionalLight3D>("../PlanetLight");
        Vector3 planetRadiance = Vector3.Zero, planetDir = Vector3.Up;
        if (_planet != null && IsInstanceValid(_planet) && _planet.IsVisibleInTree() && _planet.LightEnergy > 0f)
        {
            Color c = _planet.LightColor.SrgbToLinear();
            planetRadiance = new Vector3(c.R, c.G, c.B) * (_planet.LightEnergy * Mathf.Pi * _planet.LightVolumetricFogEnergy);
            planetDir = _planet.GlobalTransform.Basis.Z.Normalized();
        }
        _planetDir = new Vector4(planetDir.X, planetDir.Y, planetDir.Z, 0f);
        _planetRad = new Vector4(planetRadiance.X, planetRadiance.Y, planetRadiance.Z, 0f);
        if (DebugHazeCameraValue)
        {
            Vector3 t = HazeSunTransmittance(sigmaR, RingAtmosphereModel.HazeRayleighHeightMeters, sigmaA, RingAtmosphereModel.HazeAerosolHeightMeters, RingAtmosphereModel.HazeCapHeightMeters, _cameraVertical, toSun.Dot(up));
            _tOverride = new Vector4(t.X, t.Y, t.Z, 1f);
        }
        else _tOverride = default;
        // Ambient: A (rgb in-scatter radiance, published by day_side_atmosphere.gd: 0.5 (L_sky_mean + L_ground)) is delivered by the fog fill light (UpdateFill); the fog shader and the fog-end pass add A to the
        // directional in-scatter in ALBEDO_eff = omega (V S + A) / (S + A) and omega (V S + A).
        Variant ambient = _daySide != null && IsInstanceValid(_daySide) ? _daySide.Get("published_fog_ambient") : default;
        _ambient = ambient.VariantType == Variant.Type.Vector3 && ambient.AsVector3().X >= 0f ? ambient.AsVector3() : Vector3.Zero;
        LastRingUp = up;
        UpdateFill();
        if (_material == null) return;
        if (_setHaze0 != _haze0) { _setHaze0 = _haze0; _material.SetShaderParameter("nf_haze0", _haze0); }
        if (_setHaze1 != _haze1) { _setHaze1 = _haze1; _material.SetShaderParameter("nf_haze1", _haze1); }
        if (_setLight0 != _light0) { _setLight0 = _light0; _material.SetShaderParameter("nf_light0", _light0); }
        if (_setLight1 != _light1) { _setLight1 = _light1; _material.SetShaderParameter("nf_light1", _light1); }
        if (_setLight2 != _light2) { _setLight2 = _light2; _material.SetShaderParameter("nf_light2", _light2); }
        if (_setPlanetDir != _planetDir) { _setPlanetDir = _planetDir; _material.SetShaderParameter("nf_planet_dir", _planetDir); }
        if (_setPlanetRad != _planetRad) { _setPlanetRad = _planetRad; _material.SetShaderParameter("nf_planet_rad", _planetRad); }
        Vector4 packedAmbient = new(_ambient.X, _ambient.Y, _ambient.Z, 0f);
        if (_setAmbient != packedAmbient) { _setAmbient = packedAmbient; _material.SetShaderParameter("nf_ambient", packedAmbient); }
        if (_setTOverride != _tOverride) { _setTOverride = _tOverride; _material.SetShaderParameter("nf_t_override", _tOverride); }
    }

    /// <summary>The ring-up vector of the last lighting update, and the fill light (null while the fog is off), for the tests.</summary>
    public Vector3 LastRingUp { get; private set; } = Vector3.Up;
    public OmniLight3D? FillLight => _fill;
    /// <summary>Test hook: when set, the fill light uses its normal cull mask with a tiny energy (the fallback if the engine's fog ignores a light whose cull mask is 0).</summary>
    public bool DebugFillCullMaskFallback;
    /// <summary>Test hook: hides the fill light so its cost and contribution can be A-B'd.</summary>
    public bool DebugFillOff;

    private OmniLight3D? _fill;

    /// <summary>
    /// The ambient fill light (NF-5b). Godot's ambient inject samples the sky radiance along the ring axis (world +Y) and the view ray, not the ring's up, and the fog EMISSION is quantised away at fog densities,
    /// so the ambient is delivered as a light: an omni light at the camera with no shadow, no surface lighting (cull mask 0: the froxel pass ignores the mask, volumetric_fog_process.glsl) and a range of 8 fog windows,
    /// where its attenuation window (1 - (d / R)^4)^2 is 0.9995 at the window end. Every froxel then sees it along the camera-to-froxel line, so the engine's phase is HG(cos = dot(normalize(light - p), normalize(p)) = -1, g)
    /// and each froxel adds F = colour x energy x pi x vol energy x HG(-1, g) x (about 1) before the albedo. Colour and energy are chosen so F = A.
    /// </summary>
    private void UpdateFill()
    {
        if (_fill == null || !IsInstanceValid(_fill)) return;
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera != null) _fill.GlobalPosition = camera.GlobalPosition;
        if (DebugFillOff) { _fill.Visible = false; return; }
        _fill.OmniRange = 8f * FogLengthMeters;
        float peak = Mathf.Max(_ambient.X, Mathf.Max(_ambient.Y, _ambient.Z));
        if (peak <= 1e-7f) { _fill.Visible = false; return; }
        _fill.Visible = true;
        float g = Anisotropy;
        float hgBack = 0.0795774715459f * (1f - g * g) / Mathf.Pow(1f + g * g + 2f * g, 1.5f);
        float needed = peak / (Mathf.Pi * hgBack);   // light energy x volumetric energy
        float cullEnergy = DebugFillCullMaskFallback ? 1e-4f : needed;
        _fill.LightEnergy = cullEnergy;
        _fill.LightVolumetricFogEnergy = needed / cullEnergy;
        _fill.LightCullMask = DebugFillCullMaskFallback ? 0xFFFFFu : 0u;
        _fill.LightColor = new Color(_ambient.X / peak, _ambient.Y / peak, _ambient.Z / peak).LinearToSrgb();
    }

    private void CreateFill()
    {
        _fill = new OmniLight3D
        {
            Name = "NearFogFillLight",
            ShadowEnabled = false,
            OmniAttenuation = 0f,
            LightSpecular = 0f,
            LightBakeMode = Light3D.BakeMode.Disabled,
            LightCullMask = DebugFillCullMaskFallback ? 0xFFFFFu : 0u,
            OmniRange = 8f * FogLengthMeters,
            Visible = false,
        };
        AddChild(_fill);
    }

    private void FreeFill()
    {
        if (_fill != null && IsInstanceValid(_fill)) _fill.QueueFree();
        _fill = null;
    }

    /// <summary>CPU mirror of ring_haze_sun_transmittance_point (materials/ring_haze_shared.gdshaderinc): the sun transmittance at altitude h with the sun at cos_sun from local up.</summary>
    public static Vector3 HazeSunTransmittance(Vector3 sigmaR, float hR, float sigmaA, float hA, float cap, float h, float cosSun)
    {
        float colR = Mathf.Max(hR * (Mathf.Exp(Mathf.Min(-h / Mathf.Max(hR, 1f), 80f)) - Mathf.Exp(-cap / Mathf.Max(hR, 1f))), 0f);
        float colA = Mathf.Max(hA * (Mathf.Exp(Mathf.Min(-h / Mathf.Max(hA, 1f), 80f)) - Mathf.Exp(-cap / Mathf.Max(hA, 1f))), 0f);
        Vector3 tau = sigmaR * colR + new Vector3(sigmaA * colA, sigmaA * colA, sigmaA * colA);
        float airmass = 1.025f / (Mathf.Max(cosSun, 0f) + 0.025f);
        float x = Mathf.Clamp((cosSun + 0.06f) / 0.06f, 0f, 1f);
        float vis = x * x * (3f - 2f * x);
        return new Vector3(Mathf.Exp(-tau.X * airmass), Mathf.Exp(-tau.Y * airmass), Mathf.Exp(-tau.Z * airmass)) * vis;
    }

    // The day-side script's own state (the same numbers it publishes as ringworld_solar_visibility / ringworld_hub_day; GlobalShaderParameterGet is editor-only).
    private float DaySideFloat(StringName name, float fallback)
    {
        if (_daySide == null || !IsInstanceValid(_daySide)) return fallback;
        Variant v = _daySide.Get(name);
        return v.VariantType == Variant.Type.Float ? v.AsSingle() : fallback;
    }

    private void UpdateMaterial(float amount, float gate)
    {
        if (_material == null) return;
        long anchorAround = 0, anchorAcross = 0;
        RingTerrainHeightShadow? shadow = RingTerrainHeightShadow.Instance;
        if (shadow != null) (anchorAround, anchorAcross) = shadow.PublishedAnchor;
        // The pattern lives in world metres mod 4096 (anchor + advection), computed in double so a long walk or a far anchor loses no precision.
        var offset = new Vector2((float)(((anchorAround + _windAround) % NoisePeriod + NoisePeriod) % NoisePeriod), (float)(((anchorAcross + _windAcross) % NoisePeriod + NoisePeriod) % NoisePeriod));
        Camera3D? camera = GetViewport()?.GetCamera3D();
        Vector3 cameraWorld = camera != null ? camera.GlobalPosition : Vector3.Zero;
        float layer = Mathf.Max(1f, LayerHeight);
        if (_setDrive != _drive) { _setDrive = _drive; _material.SetShaderParameter("drive", _drive); }
        if (_setAmount != amount) { _setAmount = amount; _material.SetShaderParameter("amount", amount); }
        if (_setGate != gate) { _setGate = gate; _material.SetShaderParameter("gate", gate); }
        if (_setLayer != layer) { _setLayer = layer; _material.SetShaderParameter("layer_h", layer); }
        if (_setOffset != offset) { _setOffset = offset; _material.SetShaderParameter("noise_offset", offset); }
        _noiseOffset = offset;
        if (_setDebugSigma != DebugUniformSigma) { _setDebugSigma = DebugUniformSigma; _material.SetShaderParameter("debug_sigma", DebugUniformSigma); }
        if (_setWindow != FogLengthMeters) { _setWindow = FogLengthMeters; _material.SetShaderParameter("window_length", FogLengthMeters); }
        if (_setCamera != cameraWorld) { _setCamera = cameraWorld; _material.SetShaderParameter("camera_world", cameraWorld); }
    }

    // ------------------------------------------------------------------ fog end (NF-4)

    private bool EnsureEnd()
    {
        if (_end != null) return true;
        if (_endFailed || RenderingServer.GetRenderingDevice() == null) return false;
        string source = RingworldTechDemo.Atmosphere.RingAtmosphereLuts.ExpandIncludes(EndComputePath);
        _end = string.IsNullOrEmpty(source) ? null : GD.Load<GDScript>(EndScriptPath)?.New().AsGodotObject() as CompositorEffect;
        if (_end == null)
        {
            _endFailed = true;
            GD.PushError("Near ground fog end pass could not be created (shader source or script missing).");
            return false;
        }
        _end.Call("set_sources", source);
        return true;
    }

    private static string ScriptPathOf(GodotObject? o) => (o?.GetScript().AsGodotObject() as Script)?.ResourcePath ?? string.Empty;

    /// <summary>
    /// Slot of the fog end: after the last of Sunshine, the lightning channels, the containment field and the rainbow, and before the first of shock distortion, rain streaks, rain haze and
    /// the lens effects (the sun glare). The rainbow keeps itself immediately before the first of rain streaks / rain haze / this effect (RingRainbow.TailIndex), the containment field and
    /// the rain owners never look at this effect. Re-checked every 30 frames (like the containment field) and only written when out of place, so there is no reorder churn.
    /// </summary>
    private bool EnsureCompositorSlot()
    {
        if (_end == null) return false;
        if (_endEnvironment != null && IsInstanceValid(_endEnvironment) && _endAttached && _endSlotCountdown-- > 0) return true;
        _endSlotCountdown = 30;
        WorldEnvironment? environment = _endEnvironment != null && IsInstanceValid(_endEnvironment) ? _endEnvironment : _worldEnvironment;
        if (environment == null || !IsInstanceValid(environment)) return false;
        _endEnvironment = environment;
        environment.Compositor ??= new Compositor();
        var effects = environment.Compositor.CompositorEffects;
        using Godot.Collections.Array lifetime = (Godot.Collections.Array)effects;
        int self = -1, after = -1, tail = effects.Count;
        GodotObject? sunshine = null, far = null;
        for (int i = 0; i < effects.Count; i++)
        {
            CompositorEffect fx = effects[i];
            if (fx == _end) { self = i; continue; }
            string path = ScriptPathOf(fx);
            if (path.Contains("SunshineClouds2/SunshineClouds", StringComparison.Ordinal)) { sunshine ??= fx; after = Math.Max(after, i); }
            else if (path.Contains("RingFarBackdropComposite", StringComparison.Ordinal)) far ??= fx;
            else if (path.Contains("RingLightningChannelComposite", StringComparison.Ordinal) || path.Contains("RingContainmentFieldComposite", StringComparison.Ordinal)
                || path.Contains("RingRainbowComposite", StringComparison.Ordinal)) after = Math.Max(after, i);
            else if (tail == effects.Count && (path.Contains("RingShockDistortionComposite", StringComparison.Ordinal) || path.Contains("RingRainStreaksComposite", StringComparison.Ordinal)
                || path.Contains("RingRainHazeComposite", StringComparison.Ordinal) || path.Contains("RingSunGlareComposite", StringComparison.Ordinal)))
                tail = i;
        }
        if (!ReferenceEquals(sunshine, _endSunshine))
        {
            _endSunshine = sunshine;
            _end!.Call("set_sunshine", sunshine == null ? default : Variant.From(sunshine));
        }
        _endFar = far;
        if (self < 0 || self <= after || self >= tail)
        {
            if (self >= 0) { effects.RemoveAt(self); if (tail > self) tail--; if (after > self) after--; }
            effects.Insert(Math.Max(after + 1, Math.Min(tail, effects.Count)), _end);
            environment.Compositor.CompositorEffects = effects;
        }
        _endAttached = true;
        return true;
    }

    private void DetachEnd()
    {
        if (_end == null) return;
        if (_endAttached) _end.Call("publish_frame", new float[EndPayloadFloats], false, default(Rid), default(Rid), default(Rid));
        if (_endEnvironment != null && IsInstanceValid(_endEnvironment) && _endEnvironment.Compositor != null)
        {
            var effects = _endEnvironment.Compositor.CompositorEffects;
            using Godot.Collections.Array lifetime = (Godot.Collections.Array)effects;
            if (effects.Remove(_end)) _endEnvironment.Compositor.CompositorEffects = effects;
        }
        _endAttached = false;
        EndBackdropDrawing = false;
    }

    /// <summary>The far backdrop composite writes every main-empty pixel only while it is enabled and active; otherwise Godot's own sky fog already covers them.</summary>
    private bool FarBackdropDrawing()
    {
        if (_endFar == null || !IsInstanceValid(_endFar)) return false;
        return _endFar is CompositorEffect { Enabled: true } && _endFar.Get("_active").AsBool();
    }

    private void UpdateEnd(double delta, float scale)
    {
        if (!EndPassEnabled) { DetachEnd(); return; }
        if (!EnsureEnd() || !EnsureCompositorSlot()) return;
        RingTerrainHeightShadow? shadow = RingTerrainHeightShadow.Instance;
        if (shadow == null || _material == null) { _end!.Call("publish_frame", _endPayload, false, default(Rid), default(Rid), default(Rid)); return; }
        _sun ??= GetNodeOrNull<DirectionalLight3D>("../StarLight");
        Godot.Collections.Dictionary values = shadow.GetGlobalValues();
        if (!(values.TryGetValue(RingTerrainHeightShadow.GlobalFrame, out Variant frameVariant) && frameVariant.VariantType == Variant.Type.Projection))
        { _end!.Call("publish_frame", _endPayload, false, default(Rid), default(Rid), default(Rid)); return; }
        Vector4 Vec(string name) => values.TryGetValue(name, out Variant v) && v.VariantType == Variant.Type.Vector4 ? v.AsVector4() : default;
        Projection frame = frameVariant.AsProjection();
        float[] p = _endPayload;
        int k = 0;
        foreach (Vector4 column in new[] { frame.X, frame.Y, frame.Z, frame.W }) { p[k++] = column.X; p[k++] = column.Y; p[k++] = column.Z; p[k++] = column.W; }
        void Put(Vector4 v) { p[k++] = v.X; p[k++] = v.Y; p[k++] = v.Z; p[k++] = v.W; }
        Vector3 toSun = _sun != null && IsInstanceValid(_sun) ? _sun.GlobalTransform.Basis.Z.Normalized() : Vector3.Up;
        Vector3 radiance = Vector3.Zero;
        if (_sun != null && IsInstanceValid(_sun) && _sun.IsVisibleInTree())
        {
            Color c = _sun.LightColor.SrgbToLinear();
            // Godot's light data holds colour x energy x pi, and the froxel scales it by the light's volumetric fog energy.
            radiance = new Vector3(c.R, c.G, c.B) * (_sun.LightEnergy * Mathf.Pi * _sun.LightVolumetricFogEnergy);
        }
        Put(new Vector4(toSun.X, toSun.Y, toSun.Z, Anisotropy));
        Put(new Vector4(radiance.X, radiance.Y, radiance.Z, AlbedoOmega));
        Put(new Vector4(_ambient.X, _ambient.Y, _ambient.Z, 0f)); // A, the ambient in-scatter radiance
        Put(new Vector4(_drive.X, _drive.Y, _drive.Z, scale));
        int depthSlices = Math.Max(2, ProjectSettings.GetSetting("rendering/environment/volumetric_fog/volume_depth", 64).AsInt32());
        float lastSlice = 1f - 0.5f / depthSlices;
        Put(new Vector4(Mathf.Max(1f, LayerHeight), FogLengthMeters, FogLengthMeters * lastSlice * lastSlice, DebugUniformSigma)); // the engine's last slice sits at L ((N - 0.5) / N)^2 (detail spread 2)
        Put(new Vector4(_noiseOffset.X, _noiseOffset.Y, 0f, 0f));
        Put(Vec(RingTerrainHeightShadow.GlobalShape));
        Put(Vec(RingTerrainHeightShadow.GlobalFieldsParams));
        Put(Vec(RingTerrainHeightShadow.GlobalT0Bounds));
        Put(Vec(RingTerrainHeightShadow.GlobalRefWorld));
        Put(Vec(RingTerrainHeightShadow.GlobalRefFrame));
        (long anchorAround, long anchorAcross) = shadow.PublishedAnchor;
        Put(_water != null ? _water.FrameFor(anchorAround, anchorAcross) : new Vector4(0f, 0f, 8f, 0f));
        EndBackdropDrawing = FarBackdropDrawing();
        Put(new Vector4(EndBackdropDrawing ? 1f : 0f, 1f, DebugMaskEmpty ? 1f : 0f, DebugSkipEndApply ? 1f : 0f));
        // NF-5 lighting inputs: the same numbers as the froxel material's uniforms, the height-shadow globals and the cloud-shadow cascade the receivers read.
        Put(_haze0); Put(_haze1); Put(_light0); Put(_light1); Put(_light2);
        RingTerrainHeightShadow.GlobalsSnapshot hs = shadow.GetGlobalsSnapshot();
        Put(hs.L0Params); Put(hs.L1Params); Put(hs.L0Bounds); Put(hs.L0PrevBounds); Put(hs.L1Bounds); Put(hs.L1PrevBounds); Put(hs.State); Put(hs.Light);
        RingCloudShadowProjector.VegetationFrame cloud = RingCloudShadowProjector.GetVegetationFrame();
        bool cloudNear = cloud.Enabled && cloud.NearEnabled && cloud.NearRid.IsValid;
        float cloudStrength = 0f, handoffStart = 0f, handoffEnd = 1f;
        if (_material != null)
        {
            cloudStrength = ParamFloat(_material, "local_cloud_shadow_strength", 0f);
            handoffStart = ParamFloat(_material, "local_cloud_shadow_near_start_meters", 0f);
            handoffEnd = ParamFloat(_material, "local_cloud_shadow_near_end_meters", 1f);
        }
        Put(new Vector4(cloudNear ? 1f : 0f, cloud.HeightProjection ? 1f : 0f, cloudStrength, cloud.RingRadius));
        Put(new Vector4(cloud.NearCenter.X, cloud.NearCenter.Y, cloud.NearCenter.Z, 0f));
        Put(new Vector4(cloud.NearAround.X, cloud.NearAround.Y, cloud.NearAround.Z, 0f));
        Put(new Vector4(cloud.NearAcross.X, cloud.NearAcross.Y, cloud.NearAcross.Z, 0f));
        Put(new Vector4(cloud.NearUp.X, cloud.NearUp.Y, cloud.NearUp.Z, 0f));
        Put(new Vector4(cloud.NearUvOffset.X, cloud.NearUvOffset.Y, cloud.NearRadius, handoffStart));
        Put(new Vector4(cloud.SunDirection.X, cloud.SunDirection.Y, cloud.SunDirection.Z, handoffEnd));
        Put(_planetDir); Put(_planetRad);
        for (int i = 0; i < p.Length; i++) if (!float.IsFinite(p[i])) p[i] = 0f;
        // Render-device handles: T0 and basin from the producer's diagnostics (twice a second), the water texture when it changes.
        _endRidElapsed += delta;
        if (_endRidElapsed >= ProbeSeconds || !_endT0.IsValid)
        {
            _endRidElapsed = 0.0;
            if (shadow.GetDiagnostics().TryGetValue("near_fog_rids", out Variant rids) && rids.AsGodotArray<Rid>() is { Count: 2 } list) { _endT0 = list[0]; _endBasin = list[1]; }
            else { _endT0 = default; _endBasin = default; }
        }
        Texture2D? water = _water?.PublishedTexture;
        if (!ReferenceEquals(water, _endWaterTexture) || (_endWater.IsValid && ++_endWaterCountdown > 120))
        {
            _endWaterCountdown = 0;
            _endWaterTexture = water;
            _endWater = water != null ? RenderingServer.TextureGetRdTexture(water.GetRid()) : default;
        }
        _end!.Call("publish_frame", p, true, _endT0, _endBasin, _endWater, hs.L0, hs.L1, cloudNear ? cloud.NearRid : default);
    }

    private static float ParamFloat(ShaderMaterial material, StringName name, float fallback)
    {
        Variant v = material.GetShaderParameter(name);
        return v.VariantType == Variant.Type.Float ? v.AsSingle() : fallback;
    }

    private void EnsureVolume()
    {
        if (_volume != null) return;
        Shader? shader = GD.Load<Shader>(MaterialShaderPath);
        if (shader == null)
        {
            GD.PushError("Near ground fog shader could not be loaded.");
            return;
        }
        _material = new ShaderMaterial { Shader = shader };
        _volume = new FogVolume
        {
            Name = "NearGroundFogVolume",
            Shape = RenderingServer.FogVolumeShape.World,
            Material = _material,
            Visible = false,
        };
        AddChild(_volume);
        // The ground's cloud shadow (the Sunshine near cascade): the projector keeps the registered material in sync and drops it by weak reference when it dies (it has no unregister).
        RingCloudShadowProjector.Register(_material);
    }

    private void Activate()
    {
        if (_fogOn || _worldEnvironment?.Environment is not { } environment) return;
        EnsureVolume();
        if (_volume == null) return;
        if (!_saved)
        {
            _prevDensity = environment.VolumetricFogDensity;
            _prevLength = environment.VolumetricFogLength;
            _prevDetail = environment.VolumetricFogDetailSpread;
            _prevGiInject = environment.VolumetricFogGIInject;
            _prevAmbientInject = environment.VolumetricFogAmbientInject;
            _prevAnisotropy = environment.VolumetricFogAnisotropy;
            _prevSkyAffect = environment.VolumetricFogSkyAffect;
            _prevTemporal = environment.VolumetricFogTemporalReprojectionEnabled;
            _prevTemporalAmount = environment.VolumetricFogTemporalReprojectionAmount;
            _saved = true;
        }
        environment.VolumetricFogLength = FogLengthMeters;
        environment.VolumetricFogDetailSpread = DetailSpread;
        environment.VolumetricFogTemporalReprojectionEnabled = true;
        environment.VolumetricFogTemporalReprojectionAmount = TemporalAmount;
        environment.VolumetricFogGIInject = 0f;
        environment.VolumetricFogAmbientInject = 0f;
        environment.VolumetricFogDensity = 0f;
        environment.VolumetricFogAnisotropy = Anisotropy;
        environment.VolumetricFogSkyAffect = 1f;
        environment.VolumetricFogEnabled = true;
        _volume.Visible = true;
        if (_fill == null) CreateFill();
        _fogOn = true;
    }

    private void Deactivate()
    {
        DetachEnd();
        if (_sunFogTouched && _sun != null && IsInstanceValid(_sun)) _sun.LightVolumetricFogEnergy = 1f;
        _sunFogTouched = false;
        _sunFogGate = float.NaN;
        FreeFill();
        if (_volume != null) _volume.Visible = false;
        if (_worldEnvironment?.Environment is { } environment && (_fogOn || _saved))
        {
            environment.VolumetricFogEnabled = false;
            if (_saved)
            {
                environment.VolumetricFogDensity = _prevDensity;
                environment.VolumetricFogLength = _prevLength;
                environment.VolumetricFogDetailSpread = _prevDetail;
                environment.VolumetricFogGIInject = _prevGiInject;
                environment.VolumetricFogAmbientInject = _prevAmbientInject;
                environment.VolumetricFogAnisotropy = _prevAnisotropy;
                environment.VolumetricFogSkyAffect = _prevSkyAffect;
                environment.VolumetricFogTemporalReprojectionEnabled = _prevTemporal;
                environment.VolumetricFogTemporalReprojectionAmount = _prevTemporalAmount;
            }
        }
        _saved = false;
        _fogOn = false;
        _fade = 0f;
    }

    private static float Smooth(float edge0, float edge1, float x)
    {
        float u = Mathf.Clamp((x - edge0) / Mathf.Max(edge1 - edge0, 1e-6f), 0f, 1f);
        return u * u * (3f - 2f * u);
    }
}
