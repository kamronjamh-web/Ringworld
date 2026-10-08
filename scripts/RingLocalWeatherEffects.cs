using System;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo;

/// <summary>
/// Camera-local snow particles, depth-aware rain haze and storm lighting driven by the same weather
/// state as the volumetric clouds. Particle cost is bounded independently of
/// ring size; SurfaceWetness is exposed for later terrain/material consumers.
/// </summary>
public partial class RingLocalWeatherEffects : Node3D
{
    [Export] public bool Enabled { get; set; } = false;
    [Export] public bool EnableVegetationWind { get; set; } = true;
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath WeatherSourcePath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath SunshineBridgePath { get; set; } = new("../SunshineCloudBridge");
    [Export] public NodePath VolumetricCloudRendererPath { get; set; } = new("../LocalVolumetricCloudRenderer");
    [Export] public NodePath LightningLightPath { get; set; } = new("LocalStormFlash");
    [Export] public NodePath FarViewportPath { get; set; } = new("../ScaledFarViewport");

    [ExportGroup("Precipitation")]
    [Export(PropertyHint.Range, "0,1,0.01")] public float PrecipitationStart { get; set; } = 0.18f;
    [Export(PropertyHint.Range, "256,12000,128")] public int MaximumParticleCount { get; set; } = 4096;
    [Export(PropertyHint.Range, "10,150,5")] public float PrecipitationRadiusMeters { get; set; } = 55.0f;
    [Export(PropertyHint.Range, "10,100,5")] public float PrecipitationHeightMeters { get; set; } = 45.0f;
    [Export(PropertyHint.Range, "5,80,1")] public float RainFallSpeedMetersPerSecond { get; set; } = 34.0f;
    [Export(PropertyHint.Range, "0.1,5,0.1")] public float RainStreakLengthMeters { get; set; } = 1.7f;
    [Export] public Color RainColor { get; set; } = new(0.52f, 0.68f, 0.82f, 0.42f);
    [Export] public Color SnowColor { get; set; } = new(0.92f, 0.96f, 1.0f, 0.82f);
    [Export(PropertyHint.Range, "-1,1,0.01")] public float SnowTemperatureThreshold { get; set; } = -0.08f;
    [Export(PropertyHint.Range, "0,8000,100")] public float SnowGuaranteedAboveAltitudeMeters { get; set; } = 4200.0f;

    [ExportGroup("Surface wetness")]
    [Export(PropertyHint.Range, "0,1,0.01")] public float WetnessGainPerSecond { get; set; } = 0.12f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float WetnessDryingPerSecond { get; set; } = 0.012f;

    [ExportGroup("Lightning")]
    [Export] public bool EnableLightning { get; set; } = true;
    [Export(PropertyHint.Range, "0,1,0.01")] public float LightningPrecipitationThreshold { get; set; } = 0.68f;
    [Export(PropertyHint.Range, "2,60,1")] public float MinimumLightningIntervalSeconds { get; set; } = 7.0f;
    [Export(PropertyHint.Range, "3,120,1")] public float MaximumLightningIntervalSeconds { get; set; } = 24.0f;
    [Export(PropertyHint.Range, "0,64,0.5")] public float LightningEnergy { get; set; } = 18.0f;
    [Export(PropertyHint.Range, "100,20000,100")] public float LightningRangeMeters { get; set; } = 6000.0f;
    [Export(PropertyHint.Range, "250,5000,50")] public float MinimumBoltHeightMeters { get; set; } = 1200.0f;
    [Export(PropertyHint.Range, "1000,20000,100")] public float MaximumBoltHeightMeters { get; set; } = 9000.0f;
    [Export(PropertyHint.Range, "100,5000,100")] public float StrikeHorizontalRadiusMeters { get; set; } = 1800.0f;
    [Export] public Color LightningColor { get; set; } = new(0.72f, 0.86f, 1.0f, 1.0f);

    [ExportGroup("Lightning bolt geometry")]
    [Export] public LightningLod BoltLod { get; set; } = LightningLod.High;
    [Export(PropertyHint.Range, "0.01,2,0.01")] public float BoltCoreWidthMeters { get; set; } = 0.35f;
    [Export(PropertyHint.Range, "0.1,16,0.1")] public float BoltGlowWidthMeters { get; set; } = 4.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float BoltJitter { get; set; } = 0.30f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float BoltEndpointAttraction { get; set; } = 0.72f;
    [Export(PropertyHint.Range, "0,8,1")] public int BoltMaximumBranches { get; set; } = 3;
    [Export(PropertyHint.Range, "0,2,1")] public int BoltSecondaryBranchDepth { get; set; } = 1;
    [Export(PropertyHint.Range, "0,32,0.5")] public float BoltCoreEmissionEnergy { get; set; } = 14.0f;
    [Export(PropertyHint.Range, "0,32,0.5")] public float BoltGlowEmissionEnergy { get; set; } = 5.0f;

    public float PrecipitationIntensity { get; private set; }
    /// <summary>The exact resolved local wind currently driving snow particles and rain haze.</summary>
    public Vector2 ResolvedWindMetersPerSecond { get; private set; }
    public RingVolumetricCloudLayer.LocalWeatherState LastResolvedWeather { get; private set; }
    private bool _externalWeatherOverrideActive;
    private RingVolumetricCloudLayer.LocalWeatherState _externalWeatherOverride;

    /// <summary>Lets an alternate cloud compositor drive existing rain and lightning.</summary>
    public void SetExternalWeatherOverride(bool active, RingVolumetricCloudLayer.LocalWeatherState weather = default)
    {
        if (active && weather.InheritWind && !_externalWindEnvelope) {
            _externalFilteredMean = HasCurrentWindSnapshot ? CurrentWindSnapshot.MeanWind : _weatherSource?.WeatherWindMetersPerSecond ?? Vector2.Zero;
            _externalWindTime = _weatherSource?.CloudSimulationTimeSeconds ?? double.NaN;
            _externalWindEnvelope = true;
        }
        if (active && !weather.InheritWind) _externalWindEnvelope = false;
        _externalWeatherOverrideActive = active;
        _externalWeatherOverride = weather;
    }
    public float SurfaceWetness { get; private set; }
    public bool Snowing { get; private set; }
    public int LightningFlashCount { get; private set; }
    public int VegetationWindPushCount { get; private set; }
    public Vector2 PublishedVegetationWind { get; private set; }
    public double PublishedVegetationWindTime { get; private set; }
    private ulong _vegetationWindOwner;
    private static ulong _activeVegetationWindOwner;
    private bool _vegetationWindClaimed;
    private float _lastVegetationEnabled = float.NaN;
    private Vector2 _lastVegetationWind = new(float.NaN, 0);
    private Vector4 _lastVegetationWave = new(float.NaN, 0, 0, 0);
    private Vector3 _lastVegetationCenter = new(float.NaN, 0, 0);
    private Vector3 _lastVegetationAxis = new(float.NaN, 0, 0);
    public int ParticleRestartCount { get; private set; }
    public int RainConfigurationPushCount { get; private set; }

    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private RingVolumetricCloudLayer? _weatherSource;
    private RingSunshineCloudBridge? _sunshineBridge;
    private RingLocalVolumetricCloudRenderer? _cloudRenderer;
    private RingScaledFarViewport? _farViewport;
    private GpuParticles3D? _particles;
    private ParticleProcessMaterial? _particleProcess;
    private QuadMesh? _particleMesh;
    private StandardMaterial3D? _particleMaterial;
    private OmniLight3D? _lightningLight;
    private Node3D? _boltRoot;
    private MeshInstance3D? _boltCore;
    private MeshInstance3D? _boltGlow;
    private ShaderMaterial? _boltCoreMaterial;
    private ShaderMaterial? _boltGlowMaterial;
    private LightningBolt? _activeBolt;
    private float _activeBoltElapsed;
    private double _activeBoltStartTime;
    private double _lastObservedSharedLightningTime = double.NegativeInfinity;
    private long _activeSharedEventId;
    private RingLightningEvent? _activeSharedEvent;
    private Vector3 _lastSharedBoltStart = new(float.PositiveInfinity, 0, 0);
    private Vector3 _lastSharedBoltEnd = new(float.PositiveInfinity, 0, 0);
    public long CurrentRenderedLightningEventId { get; private set; }
    public long LastRenderedLightningEventId { get; private set; }
    public int LastRenderedLightningShapeSeed { get; private set; }
    public RingCanonicalPosition LastRenderedLightningGroundPosition { get; private set; }
    public RingCanonicalPosition LastRenderedLightningCloudPosition { get; private set; }
    public float CurrentRenderedLightningPulse { get; private set; }
    /// <summary>Camera-distance ownership only; the shared source pulse remains raw.</summary>
    public float CurrentRenderedLightningVisualWeight { get; private set; }
    private bool _lastSnowing;
    private bool _lightningSmokeTest;
    private bool _stormCellLightningSmokeTest;
    private readonly RandomNumberGenerator _random = new();
    private RingTerrainColumnCoordinate _lastPrecipitationAnchor = new(long.MinValue, int.MinValue);
    private Vector3 _lastGridPosition = new(float.PositiveInfinity, 0.0f, 0.0f);
    private RingTerrainColumnCoordinate _lastTemperatureCell = new(long.MinValue, int.MinValue);
    private float _cachedTemperature;

    public override void _Ready()
    {
        if (!Enabled || ProcessMode == ProcessModeEnum.Disabled)
        {
            SetProcess(false);
            GD.Print("Local precipitation and lightning effects are disabled for this world preset.");
            return;
        }

        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _vegetationWindOwner = GetInstanceId();
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _weatherSource = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherSourcePath);
        _sunshineBridge = GetNodeOrNull<RingSunshineCloudBridge>(SunshineBridgePath);
        _cloudRenderer = GetNodeOrNull<RingLocalVolumetricCloudRenderer>(VolumetricCloudRendererPath);
        _farViewport = GetNodeOrNull<RingScaledFarViewport>(FarViewportPath);
        if (_explorer == null || _grid?.TerrainSampler == null || _weatherSource == null)
        {
            GD.PushWarning("Local weather effects disabled: explorer, grid, or weather source is missing.");
            SetProcess(false);
            return;
        }
        _vegetationWindClaimed = true;
        _activeVegetationWindOwner = _vegetationWindOwner;
        _weatherSource.SharedWindField.InvalidatePublication();

        CreatePrecipitationParticles();
        CreateRainHaze();
        if (GetParent().GetNodeOrNull<RingDistantRainCurtains>("DistantRainCurtains") == null)
        {
            var curtains = new RingDistantRainCurtains { Name = "DistantRainCurtains" };
            GetParent().CallDeferred(Node.MethodName.AddChild, curtains);
        }
        CreateLightningLight();
        CreateBoltVisuals();
        UpdatePrecipitationTransform(force: true);
        GD.Print(
            $"Local weather effects ready: snow pool={MaximumParticleCount}, rain haze enabled, radius={PrecipitationRadiusMeters:0}m, "
            + $"sharedLightning={(EnableLightning ? "on" : "off")}, range={LightningRangeMeters:0}m, boltLod={BoltLod}.");

        _lightningSmokeTest = Array.Exists(
            OS.GetCmdlineUserArgs(),
            static argument => argument == "--lightning-smoke-test");
        _stormCellLightningSmokeTest = Array.Exists(
            OS.GetCmdlineUserArgs(),
            static argument => argument == "--storm-cell-lightning-smoke-test");
        if (_lightningSmokeTest)
        {
            GetTree().CreateTimer(0.5).Timeout += TriggerLightningPreview;
        }
        if (_stormCellLightningSmokeTest)
        {
            GetTree().CreateTimer(0.6).Timeout += RunStormCellLightningSmokeTest;
        }
    }

    public void TriggerLightningPreview()
    {
        if (_activeBolt != null)
        {
            ClearActiveBolt();
        }

        RingVolumetricCloudLayer.LocalWeatherState weather = ResolveCurrentWeather();
        RingCanonicalPosition player = _explorer?.CanonicalPosition ?? default;
        // A preview is an explicit shared event even in clear weather.  Keep it
        // away from the observer so its endpoint is useful for inspecting it.
        if (weather.SystemId == 0 || weather.PrecipitationPotential < LightningPrecipitationThreshold)
        {
            weather = new RingVolumetricCloudLayer.LocalWeatherState(
                -9001, RingVolumetricCloudLayer.WeatherSystemKind.ExceptionalFront, 1.0f,
                2400.0f, new Vector2((float)player.AroundMeters, (float)player.AcrossMeters),
                new Vector2(2400.0f, 1800.0f), 0.0f, 1.0f, 1.0f, Vector2.Zero,
                CloudFormationProfile.Cumulonimbus, MinimumBoltHeightMeters, MaximumBoltHeightMeters);
        }
        RingCanonicalPosition target = player with { AroundMeters = player.AroundMeters + 1800.0 };
        RingLightningEvent? preview = _weatherSource?.TriggerSharedLightningPreview(target, weather);
        if (preview.HasValue) SpawnLightningBolt(weather, preview.Value.GroundPosition, preview.Value);
    }

    public Godot.Collections.Dictionary GetLocalSharedLightningDiagnostics()
    {
        RingCanonicalPosition ground = LastRenderedLightningGroundPosition;
        RingCanonicalPosition cloud = LastRenderedLightningCloudPosition;
        return new Godot.Collections.Dictionary
        {
            ["event_id"] = CurrentRenderedLightningEventId,
            ["last_event_id"] = LastRenderedLightningEventId,
            ["shape_seed"] = LastRenderedLightningShapeSeed,
            ["flash_count"] = LightningFlashCount,
            ["pulse"] = CurrentRenderedLightningPulse,
            ["visual_weight"] = CurrentRenderedLightningVisualWeight,
            ["ground_around_meters"] = ground.AroundMeters, ["ground_across_meters"] = ground.AcrossMeters,
            ["cloud_around_meters"] = cloud.AroundMeters, ["cloud_across_meters"] = cloud.AcrossMeters,
            ["world_ground"] = _grid?.CanonicalRingPositionToWorld(ground) ?? Vector3.Zero,
            ["world_cloud"] = _grid?.CanonicalRingPositionToWorld(cloud) ?? Vector3.Zero
        };
    }
    public Godot.Collections.Dictionary get_local_shared_lightning_diagnostics() => GetLocalSharedLightningDiagnostics();

    private void RunStormCellLightningSmokeTest()
    {
        if (_explorer == null || _grid == null || _weatherSource == null)
        {
            GD.PushError("STORM_CELL_LIGHTNING_SMOKE_TEST_FAIL: required weather dependencies are unavailable.");
            return;
        }

        RingCanonicalPosition player = _explorer.CanonicalPosition;
        const float majorRadius = 1800.0f;
        const float minorRadius = 1100.0f;
        if (_grid.RingAcrossWidthMeters < minorRadius * 2.0f + 2.0f)
        {
            GD.PushError("STORM_CELL_LIGHTNING_SMOKE_TEST_FAIL: ring is too narrow for the synthetic storm ellipse.");
            return;
        }

        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        double centerAround = WrapSmokeTestAround(player.AroundMeters + 12000.0, circumference);
        double centerAcross = Math.Clamp(
            player.AcrossMeters,
            minorRadius + 1.0f,
            _grid.RingAcrossWidthMeters - minorRadius - 1.0f);
        const float orientation = 0.73f;
        var storm = new RingVolumetricCloudLayer.LocalWeatherState(
            -777,
            RingVolumetricCloudLayer.WeatherSystemKind.MajorFront,
            1.0f,
            majorRadius,
            new Vector2((float)centerAround, (float)centerAcross),
            new Vector2(majorRadius, minorRadius),
            orientation,
            1.0f,
            1.0f,
            Vector2.Zero,
            CloudFormationProfile.Cumulonimbus,
            MinimumBoltHeightMeters,
            MaximumBoltHeightMeters);
        // The source owns event sampling; this fixed in-ellipse point only
        // supplies the preview request and avoids a legacy local random path.
        RingCanonicalPosition target = new(centerAround, 0.0, centerAcross);
        RingVolumetricCloudLayer.LocalWeatherState targetWeather = storm;

        double aroundDelta = ShortestSmokeTestAroundDelta(target.AroundMeters, centerAround, circumference);
        double acrossDelta = target.AcrossMeters - centerAcross;
        float cos = Mathf.Cos(orientation);
        float sin = Mathf.Sin(orientation);
        double localX = aroundDelta * cos + acrossDelta * sin;
        double localY = -aroundDelta * sin + acrossDelta * cos;
        double normalizedRadiusSquared = localX * localX / (majorRadius * majorRadius)
            + localY * localY / (minorRadius * minorRadius);
        double playerDistanceSquared = ShortestSmokeTestAroundDelta(
            target.AroundMeters,
            player.AroundMeters,
            circumference);
        playerDistanceSquared *= playerDistanceSquared;
        double playerAcrossDelta = target.AcrossMeters - player.AcrossMeters;
        playerDistanceSquared += playerAcrossDelta * playerAcrossDelta;
        bool passed = normalizedRadiusSquared <= 1.0001
            && target.AcrossMeters >= 0.0
            && target.AcrossMeters <= _grid.RingAcrossWidthMeters
            && playerDistanceSquared > 4000.0 * 4000.0;
        if (!passed)
        {
            GD.PushError(
                $"STORM_CELL_LIGHTNING_SMOKE_TEST_FAIL: normalizedRadiusSq={normalizedRadiusSquared:0.0000}, "
                + $"across={target.AcrossMeters:0.0}, playerDistance={Math.Sqrt(playerDistanceSquared):0.0}m.");
            return;
        }

        RingLightningEvent? preview = _weatherSource.TriggerSharedLightningPreview(target, targetWeather);
        if (!preview.HasValue)
        {
            GD.PushError("STORM_CELL_LIGHTNING_SMOKE_TEST_FAIL: shared weather source rejected the synthetic storm preview.");
            return;
        }
        SpawnLightningBolt(targetWeather, preview.Value.GroundPosition, preview.Value);
        GD.Print(
            $"STORM_CELL_LIGHTNING_SMOKE_TEST_PASS: target=({target.AroundMeters:0.0}, {target.AcrossMeters:0.0}), "
            + $"normalizedRadiusSq={normalizedRadiusSquared:0.0000}, "
            + $"playerDistance={Math.Sqrt(playerDistanceSquared):0.0}m.");
    }

    private static double WrapSmokeTestAround(double around, double circumference)
    {
        double wrapped = around % circumference;
        return wrapped < 0.0 ? wrapped + circumference : wrapped;
    }

    private static double ShortestSmokeTestAroundDelta(double from, double to, double circumference)
    {
        double delta = (from - to) % circumference;
        if (delta > circumference * 0.5) delta -= circumference;
        if (delta < -circumference * 0.5) delta += circumference;
        return delta;
    }

    public RingWindSnapshot CurrentWindSnapshot { get; private set; }
    public bool HasCurrentWindSnapshot { get; private set; }
    private Vector2 _externalFilteredMean;
    private bool _externalWindEnvelope;
    private double _externalWindTime = double.NaN;
    public bool SharedWindFieldReady => _weatherSource?.SharedWindField.Ready == true;
    public Vector2 GustOffsetMeters => _weatherSource?.SharedWindField.OffsetMeters ?? Vector2.Zero;
    public double GustSimulationTime => CurrentWindSnapshot.SimulationTime;
    public double GustAroundPeriodMeters => _weatherSource?.SharedWindField.AroundPeriodMeters ?? 600;
    public double GustAcrossPeriodMeters => _weatherSource?.SharedWindField.AcrossPeriodMeters ?? 600;
    private Vector2 _treeTrunkWind, _treeCanopyWind;
    private double _lastWindTime = double.NaN;

    public override void _Process(double delta)
    {
        if (_explorer == null || _grid == null || _weatherSource == null || _particles == null) return;
        float dt = (float)Math.Clamp(delta, 0.0, 0.25);
        RingVolumetricCloudLayer.LocalWeatherState weather = ResolveCurrentWeather();
        LastResolvedWeather = weather;
        if (_externalWindEnvelope) {
            double now = _weatherSource.CloudSimulationTimeSeconds;
            double elapsed = double.IsFinite(_externalWindTime) && now >= _externalWindTime ? now - _externalWindTime : 0;
            if (now < _externalWindTime) _externalFilteredMean = weather.WindMetersPerSecond;
            float seconds = _externalWeatherOverrideActive ? _weatherSource.WindSettings.StormRiseSeconds : _weatherSource.WindSettings.StormDecaySeconds;
            _externalFilteredMean = _externalFilteredMean.Lerp(weather.WindMetersPerSecond, (float)(1-Math.Exp(-elapsed/Math.Max(.1,seconds))));
            _externalWindTime = now;
            weather = weather with { WindMetersPerSecond = _externalFilteredMean, InheritWind = true };
            if (!_externalWeatherOverrideActive && _externalFilteredMean.DistanceSquaredTo(LastResolvedWeather.WindMetersPerSecond) < .0001f) _externalWindEnvelope = false;
        }
        CurrentWindSnapshot = _weatherSource.CaptureWindSnapshot(_explorer.CanonicalPosition, weather, _weatherSource.CloudSimulationTimeSeconds, _externalWeatherOverrideActive && !weather.InheritWind);
        HasCurrentWindSnapshot = true;
        ResolvedWindMetersPerSecond = CurrentWindSnapshot.InstantWind;
        weather = weather with { WindMetersPerSecond = ResolvedWindMetersPerSecond };
        var settings = _weatherSource.WindSettings;
        double sourceTime = CurrentWindSnapshot.SimulationTime;
        double step = double.IsFinite(_lastWindTime) ? Math.Clamp(sourceTime - _lastWindTime, 0, 1) : 0;
        if (!double.IsFinite(_lastWindTime) || sourceTime < _lastWindTime) _treeTrunkWind = _treeCanopyWind = CurrentWindSnapshot.MeanWind;
        _lastWindTime = sourceTime;
        _treeTrunkWind = _treeTrunkWind.Lerp(CurrentWindSnapshot.MeanWind, (float)(1-Math.Exp(-step/Math.Max(.05,settings.TreeTrunkResponseSeconds))));
        _treeCanopyWind = _treeCanopyWind.Lerp(CurrentWindSnapshot.MeanWind, (float)(1-Math.Exp(-step/Math.Max(.05,settings.TreeCanopyResponseSeconds))));
        PublishVegetationWind();
        bool localCloudsVisible = LocalCloudsVisible();
        float start = Mathf.Clamp(PrecipitationStart, 0.0f, 0.99f);
        float targetIntensity = Enabled && localCloudsVisible
            ? SmoothUnit((weather.PrecipitationPotential - start) / (1.0f - start))
            : 0.0f;
        PrecipitationIntensity = Mathf.MoveToward(
            PrecipitationIntensity,
            targetIntensity,
            dt * (targetIntensity > PrecipitationIntensity ? 0.85f : 0.42f));
        Snowing = PrecipitationIntensity > 0.01f && ResolveSnowing();
        if (Snowing != _lastSnowing) ConfigureParticleAppearance();
        _lastSnowing = Snowing;

        // Liquid rain is a soft depth-aware veil; this stable pool is snow only.
        _particles.Emitting = Snowing && PrecipitationIntensity > 0.01f;
        _particles.Visible = _particles.Emitting;
        UpdateSnowParticleProfile();
        UpdateRainHaze();
        UpdateParticleMotion(weather);
        UpdatePrecipitationTransform(force: false);
        UpdateWetness(dt);
        UpdateLightning(delta, weather, localCloudsVisible);
    }

    private RingVolumetricCloudLayer.LocalWeatherState ResolveCurrentWeather()
    {
        if (_externalWeatherOverrideActive) return _externalWeatherOverride;
        if (_sunshineBridge?.RuntimeVisible == true) return _sunshineBridge.CurrentWeather;
        if (_cloudRenderer?.LocalVolumeVisible == true) return _cloudRenderer.CurrentWeather;
        return _weatherSource?.QueryLocalWeather(_explorer?.CanonicalPosition ?? default) ?? default;
    }

    private void PublishVegetationWind()
    {
        // A closing/overlapping gameplay scene must not replace the new scene's field.
        if (!_vegetationWindClaimed || _activeVegetationWindOwner != _vegetationWindOwner) return;
        Vector2 wind = ResolvedWindMetersPerSecond;
        if (!EnableVegetationWind || !Enabled || _weatherSource?.Enabled != true
            || !float.IsFinite(wind.X) || !float.IsFinite(wind.Y))
        {
            wind = Vector2.Zero;
        }
        wind = wind.LimitLength(80.0f);
        double time = _weatherSource?.CloudSimulationTimeSeconds ?? 0.0;
        if (!double.IsFinite(time)) time = 0.0;
        // Evaluate in double precision before sending bounded values to the GPU:
        // a large float TIME/phase would lose frame-sized increments after long play.
        double phase = time * .19;
        Vector4 wave = new(
            (float)Math.Sin(phase), (float)Math.Cos(phase),
            (float)Math.Sin(phase * .47), (float)Math.Cos(phase * .47));
        Vector3 center = _grid?.GetCanonicalRingCenterWorld() ?? Vector3.Zero;
        if (!center.IsFinite()) center = Vector3.Zero;
        Vector3 axis = _grid?.GlobalBasis.Y.Normalized() ?? Vector3.Up;
        if (!axis.IsFinite() || axis.LengthSquared() < .001f) axis = Vector3.Up;
        bool active = EnableVegetationWind && Enabled && _weatherSource?.Enabled == true && CurrentWindSnapshot.Enabled;
        if (!active) _treeTrunkWind = _treeCanopyWind = Vector2.Zero;
        float enabled = active && (wind.LengthSquared() + _treeTrunkWind.LengthSquared() + _treeCanopyWind.LengthSquared() > .0001f) ? 1f : 0f;
        if (_grid != null && _weatherSource != null) {
            var settings = _weatherSource.WindSettings;
            _weatherSource.SharedWindField.Publish(_grid.CaptureLodGeometrySnapshot(), _explorer?.CanonicalPosition ?? default, CurrentWindSnapshot.MeanWind, CurrentWindSnapshot.MeanWind, settings, active);
            _weatherSource.SharedWindField.SetCached("ring_tree_wind_response", new Vector4(settings.TreeBendStrength,settings.TreeMaximumBendRatio,settings.TreeTrunkResponseSeconds,settings.TreeCanopyResponseSeconds));
            _weatherSource.SharedWindField.SetCached("ring_tree_wind_parts", new Vector2(
                float.IsFinite(settings.TreeTrunkBendStrength) ? Mathf.Clamp(settings.TreeTrunkBendStrength, 0, 1) : .15f,
                float.IsFinite(settings.TreeCanopyFlexStrength) ? Mathf.Clamp(settings.TreeCanopyFlexStrength, 0, 3) : 1f));
            _weatherSource.SharedWindField.SetCached("ring_tree_wind_trunk", _treeTrunkWind);
            _weatherSource.SharedWindField.SetCached("ring_tree_wind_canopy", _treeCanopyWind);
            _weatherSource.SharedWindField.SetCached("ring_wind_phase", wave);
        }

        // Only changed payloads cross the rendering boundary. Exact vector checks
        // retain metre-sized rebases even when the ring center is millions away.
        if (!Mathf.IsEqualApprox(enabled, _lastVegetationEnabled))
        {
            RenderingServer.GlobalShaderParameterSet("ring_local_wind_enabled", enabled);
            _lastVegetationEnabled = enabled;
            VegetationWindPushCount++;
        }
        if (_lastVegetationWind != wind)
        {
            RenderingServer.GlobalShaderParameterSet("ring_local_wind_vector", wind);
            _lastVegetationWind = wind;
            VegetationWindPushCount++;
        }
        if (_lastVegetationWave != wave)
        {
            RenderingServer.GlobalShaderParameterSet("ring_local_wind_wave", wave);
            _lastVegetationWave = wave;
            VegetationWindPushCount++;
        }
        if (_lastVegetationCenter != center)
        {
            RenderingServer.GlobalShaderParameterSet("ring_local_wind_center", center);
            _lastVegetationCenter = center;
            VegetationWindPushCount++;
        }
        if (_lastVegetationAxis != axis)
        {
            RenderingServer.GlobalShaderParameterSet("ring_local_wind_axis", axis);
            _lastVegetationAxis = axis;
            VegetationWindPushCount++;
        }
        PublishedVegetationWind = wind;
        PublishedVegetationWindTime = time;
    }

    public Godot.Collections.Dictionary GetVegetationWindDiagnostics() => new()
    {
        ["enabled"] = _lastVegetationEnabled,
        ["wind"] = PublishedVegetationWind,
        ["wave"] = _lastVegetationWave,
        ["center"] = _lastVegetationCenter,
        ["axis"] = _lastVegetationAxis,
        ["source_time"] = PublishedVegetationWindTime,
        ["push_count"] = VegetationWindPushCount,
        ["owner"] = (long)_vegetationWindOwner,
        ["active_owner"] = (long)_activeVegetationWindOwner
    };
    public Godot.Collections.Dictionary get_vegetation_wind_diagnostics() => GetVegetationWindDiagnostics();

    public override void _ExitTree()
    {
        ReleaseRainHaze();
        HasCurrentWindSnapshot = false;
        if (_vegetationWindClaimed && _activeVegetationWindOwner == _vegetationWindOwner)
        {
            RenderingServer.GlobalShaderParameterSet("ring_local_wind_enabled", 0.0f);
            RenderingServer.GlobalShaderParameterSet("ring_wind_field_enabled", 0.0f);
            RenderingServer.GlobalShaderParameterSet("ring_wind_mean_vector", Vector2.Zero);
            RenderingServer.GlobalShaderParameterSet("ring_tree_wind_trunk", Vector2.Zero);
            RenderingServer.GlobalShaderParameterSet("ring_tree_wind_canopy", Vector2.Zero);
            RenderingServer.GlobalShaderParameterSet("ring_tree_wind_parts", new Vector2(.15f, 1f));
            RenderingServer.GlobalShaderParameterSet("ring_wind_gust_texture", ResourceLoader.Load<Texture2D>("res://materials/ring_wind_neutral.tres"));
            _activeVegetationWindOwner = 0;
        }
    }

    private bool LocalCloudsVisible() => _externalWeatherOverrideActive
        || _sunshineBridge?.RuntimeVisible == true || _cloudRenderer?.LocalVolumeVisible == true;

    private void CreatePrecipitationParticles()
    {
        _particleProcess = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(
                Mathf.Max(5.0f, PrecipitationRadiusMeters),
                Mathf.Max(5.0f, PrecipitationHeightMeters) * 0.5f,
                Mathf.Max(5.0f, PrecipitationRadiusMeters)),
            Direction = Vector3.Down,
            Spread = 4.0f,
            InitialVelocityMin = 1.2f,
            InitialVelocityMax = 3.8f,
            Gravity = new Vector3(0.0f, -.8f, 0.0f)
        };
        _particleMaterial = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Disabled,
            AlbedoColor = SnowColor
        };
        _particleMesh = new QuadMesh
        {
            Size = new Vector2(.11f, .11f),
            Material = _particleMaterial
        };
        _particles = new GpuParticles3D
        {
            Name = "CameraLocalPrecipitation",
            TopLevel = true,
            Amount = Math.Clamp(MaximumParticleCount, 256, 12000),
            AmountRatio = 0.0f,
            Lifetime = 6.0,
            Randomness = .82f,
            VisibilityAabb = new Aabb(
                new Vector3(-PrecipitationRadiusMeters, -PrecipitationHeightMeters, -PrecipitationRadiusMeters),
                new Vector3(PrecipitationRadiusMeters * 2.0f, PrecipitationHeightMeters * 2.0f, PrecipitationRadiusMeters * 2.0f)),
            ProcessMaterial = _particleProcess,
            DrawPass1 = _particleMesh,
            Emitting = false,
            Visible = false,
            LocalCoords = true,
            TransformAlign = GpuParticles3D.TransformAlignEnum.Disabled
        };
        AddChild(_particles);
    }

    private void CreateLightningLight()
    {
        _lightningLight = GetNodeOrNull<OmniLight3D>(LightningLightPath);
        if (_lightningLight == null)
        {
            _lightningLight = new OmniLight3D { Name = "LocalStormFlash" };
            AddChild(_lightningLight);
        }

        _lightningLight.TopLevel = true;
        _lightningLight.LightColor = LightningColor;
        _lightningLight.LightEnergy = 0.0f;
        _lightningLight.OmniRange = Mathf.Max(100.0f, LightningRangeMeters);
        _lightningLight.ShadowEnabled = false;
        _lightningLight.LightVolumetricFogEnergy = 2.4f;
    }

    private void CreateBoltVisuals()
    {
        Shader? shader = GD.Load<Shader>("res://materials/ring_lightning_bolt.gdshader");
        if (shader == null)
        {
            GD.PushWarning("Lightning flash will run without a visible bolt: bolt shader is missing.");
            return;
        }

        _boltCoreMaterial = new ShaderMaterial { Shader = shader };
        _boltCoreMaterial.SetShaderParameter("bolt_color", LightningColor);
        _boltCoreMaterial.SetShaderParameter("intensity", 0.0f);
        _boltCoreMaterial.SetShaderParameter("ownership_weight", 1.0f);
        _boltCoreMaterial.SetShaderParameter("emission_energy", Mathf.Max(0.0f, BoltCoreEmissionEnergy));
        _boltGlowMaterial = new ShaderMaterial { Shader = shader };
        _boltGlowMaterial.SetShaderParameter("bolt_color", new Color(
            LightningColor.R * 0.62f,
            LightningColor.G * 0.76f,
            LightningColor.B,
            0.34f));
        _boltGlowMaterial.SetShaderParameter("intensity", 0.0f);
        _boltGlowMaterial.SetShaderParameter("ownership_weight", 1.0f);
        _boltGlowMaterial.SetShaderParameter("emission_energy", Mathf.Max(0.0f, BoltGlowEmissionEnergy));

        _boltRoot = new Node3D { Name = "ProceduralLightningBolt", TopLevel = true, Visible = false };
        _boltGlow = new MeshInstance3D
        {
            Name = "Glow",
            MaterialOverride = _boltGlowMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true
        };
        _boltCore = new MeshInstance3D
        {
            Name = "Core",
            MaterialOverride = _boltCoreMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true
        };
        AddChild(_boltRoot);
        _boltRoot.AddChild(_boltGlow);
        _boltRoot.AddChild(_boltCore);
    }

    private void ConfigureParticleAppearance()
    {
        if (_particleMesh == null || _particleMaterial == null || _particles == null) return;
        if (Snowing)
        {
            _particles.TransformAlign = GpuParticles3D.TransformAlignEnum.Disabled;
            _particleMaterial.BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled;
            _particleMesh.Size = new Vector2(0.11f, 0.11f);
            _particleMaterial.AlbedoColor = SnowColor;
            _particles.Lifetime = 6.0;
            _particles.Randomness = 0.82f;
        }
        else
        {
            // Hide immediately; stopping emission alone leaves live flakes on
            // screen for the remaining snow lifetime when liquid rain begins.
            _particles.Visible = false;
            _particles.Emitting = false;
            _particles.AmountRatio = 0;
        }
        _particles.Restart();
        ParticleRestartCount++;
    }

    private void UpdateParticleMotion(in RingVolumetricCloudLayer.LocalWeatherState weather)
    {
        if (_particleProcess == null) return;
        if (Snowing)
        {
            _particleProcess.Direction = new Vector3(0.12f, -1.0f, 0.08f).Normalized();
            _particleProcess.Spread = 28.0f;
            _particleProcess.InitialVelocityMin = 1.2f;
            _particleProcess.InitialVelocityMax = 3.8f;
            _particleProcess.Gravity = new Vector3(
                weather.WindMetersPerSecond.X * 0.35f,
                -0.8f,
                weather.WindMetersPerSecond.Y * 0.35f);
        }
    }

    private void UpdateSnowParticleProfile()
    {
        if (_particles == null) return;
        float ratio = Snowing && float.IsFinite(PrecipitationIntensity) ? Mathf.Clamp(PrecipitationIntensity, 0, 1) : 0;
        if (Mathf.IsEqualApprox(_particles.AmountRatio, ratio)) return;
        _particles.AmountRatio = ratio;
    }

    public Godot.Collections.Dictionary GetPrecipitationDiagnostics()
    {
        float i = float.IsFinite(PrecipitationIntensity) ? Mathf.Clamp(PrecipitationIntensity, 0.0f, 1.0f) : 0.0f;
        RainPrecipitationProfile p = RainPrecipitationProfile.Resolve(i);
        float velocityMin = _particleProcess?.InitialVelocityMin ?? 0.0f;
        float velocityMax = _particleProcess?.InitialVelocityMax ?? 0.0f;
        return new Godot.Collections.Dictionary
        {
            ["stage"] = p.Stage(i, Snowing),
            ["scattered_weight"] = p.ScatteredWeight,
            ["steady_weight"] = p.SteadyWeight,
            ["heavy_weight"] = p.HeavyWeight,
            ["shared_intensity"] = PrecipitationIntensity,
            ["amount_ratio"] = _particles?.AmountRatio ?? 0.0f,
            ["effective_count"] = (_particles?.Visible == true ? _particles.Amount : 0) * (_particles?.AmountRatio ?? 0.0f),
            ["particle_visible"] = _particles?.Visible == true,
            ["particle_emitting"] = _particles?.Emitting == true,
            ["presentation"] = Snowing ? "Snow particles" : "Rain haze",
            ["allocated_pool"] = _particles?.Amount ?? 0,
            ["streak_size"] = _particleMesh?.Size ?? Vector2.Zero,
            ["material_alpha"] = _particleMaterial?.AlbedoColor.A ?? 0.0f,
            ["fall_velocity"] = (velocityMin + velocityMax) * .5f,
            ["initial_velocity_min"] = velocityMin,
            ["initial_velocity_max"] = velocityMax,
            ["gravity"] = _particleProcess?.Gravity ?? Vector3.Zero,
            ["spread"] = _particleProcess?.Spread ?? 0.0f,
            ["snow"] = Snowing,
            ["restart_count"] = ParticleRestartCount,
            ["emitter_id"] = _particles?.GetInstanceId() ?? 0,
            ["mesh_id"] = _particleMesh?.GetInstanceId() ?? 0,
            ["material_id"] = _particleMaterial?.GetInstanceId() ?? 0,
            ["process_id"] = _particleProcess?.GetInstanceId() ?? 0,
            ["config_push_count"] = RainConfigurationPushCount
        };
    }
    public Godot.Collections.Dictionary get_precipitation_diagnostics() => GetPrecipitationDiagnostics();

    private void UpdatePrecipitationTransform(bool force)
    {
        if (_particles == null || _lightningLight == null || _grid == null || _explorer == null) return;
        const float snap = 8.0f;
        RingCanonicalPosition canonical = _explorer.CanonicalPosition;
        var anchor = new RingTerrainColumnCoordinate(
            (long)Math.Round(canonical.AroundMeters / snap),
            Mathf.RoundToInt((float)(canonical.AcrossMeters / snap)));
        bool gridMoved = !_lastGridPosition.IsFinite()
            || !_lastGridPosition.IsEqualApprox(_grid.GlobalPosition);
        if (!force && anchor == _lastPrecipitationAnchor && !gridMoved) return;
        _lastPrecipitationAnchor = anchor;
        _lastGridPosition = _grid.GlobalPosition;
        double around = anchor.AroundVoxel * snap;
        double across = Math.Clamp(anchor.AcrossVoxel * snap, 0.0, _grid.RingAcrossWidthMeters);
        double vertical = canonical.VerticalMeters + PrecipitationHeightMeters * 0.34;
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        Vector3 center = geometry.CanonicalToWorld(around, vertical, across);
        Vector3 aroundAxis = (geometry.CanonicalToWorld(around + 10.0, vertical, across) - center).Normalized();
        Vector3 upAxis = (geometry.CanonicalToWorld(around, vertical + 10.0, across) - center).Normalized();
        Vector3 acrossAxis = (geometry.CanonicalToWorld(around, vertical, across + 10.0) - center).Normalized();
        Basis basis = new Basis(aroundAxis, upAxis, acrossAxis).Orthonormalized();
        _particles.GlobalTransform = new Transform3D(basis, center);
        if (_activeBolt == null)
            _lightningLight.GlobalTransform = new Transform3D(basis, center + upAxis * 800.0f);
    }

    private bool ResolveSnowing()
    {
        if (_grid?.TerrainSampler == null || _explorer == null) return false;
        RingCanonicalPosition position = _explorer.CanonicalPosition;
        if (position.VerticalMeters >= SnowGuaranteedAboveAltitudeMeters) return true;
        long around = (long)Math.Round(position.AroundMeters);
        int across = Mathf.Clamp(
            Mathf.RoundToInt((float)position.AcrossMeters),
            0,
            Mathf.Max(0, Mathf.RoundToInt(_grid.RingAcrossWidthMeters)));
        const int temperatureCellMeters = 256;
        var cell = new RingTerrainColumnCoordinate(
            (long)Math.Floor(around / (double)temperatureCellMeters),
            Mathf.FloorToInt(across / (float)temperatureCellMeters));
        if (cell != _lastTemperatureCell)
        {
            _cachedTemperature = _grid.TerrainSampler.GetTerrainSample(around, across).Temperature;
            _lastTemperatureCell = cell;
        }
        return _cachedTemperature <= SnowTemperatureThreshold;
    }

    private void UpdateWetness(float delta)
    {
        // Snow does not create liquid sheen. Both accumulation and drying use
        // liquid-rain intensity, so stored rain moisture can dry during snow.
        float rainIntensity = Snowing ? 0.0f : PrecipitationIntensity;
        float gain = rainIntensity * Mathf.Max(0.0f, WetnessGainPerSecond);
        float drying = (1.0f - rainIntensity) * Mathf.Max(0.0f, WetnessDryingPerSecond);
        SurfaceWetness = Mathf.Clamp(SurfaceWetness + (gain - drying) * delta, 0.0f, 1.0f);
    }

    private void UpdateLightning(
        double delta,
        in RingVolumetricCloudLayer.LocalWeatherState weather,
        bool localCloudsVisible)
    {
        if (_lightningLight == null || _explorer == null || _grid == null || _weatherSource == null) return;
        if (!Enabled || !EnableLightning || !_weatherSource.Enabled || !_weatherSource.EnableSharedLightning)
        {
            if (_activeBolt != null) ClearActiveBolt();
            if (!_weatherSource.Enabled || !_weatherSource.EnableSharedLightning) LastRenderedLightningEventId = 0;
            return;
        }
        double now = _weatherSource.CloudSimulationTimeSeconds;
        if (now < _lastObservedSharedLightningTime)
        {
            LastRenderedLightningEventId = 0;
            if (_activeBolt != null) ClearActiveBolt();
        }
        _lastObservedSharedLightningTime = now;
        if (_activeBolt != null)
        {
            if (now < _activeBoltStartTime || !_weatherSource.EnableSharedLightning)
            {
                ClearActiveBolt();
                return;
            }
            _activeBoltElapsed = (float)Math.Max(0.0, now - _activeBoltStartTime);
            UpdateActiveBoltPulse();
            if (_activeBoltElapsed >= RingLightningEvent.LifetimeSeconds) ClearActiveBolt();
            return;
        }
        RingLightningEvent? closest = null;
        double closestDistanceSquared = double.PositiveInfinity;
        double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        foreach (RingLightningEvent e in _weatherSource.ActiveLightningEvents)
        {
            double age = now - e.StartTimeSeconds;
            if (e.EventId == LastRenderedLightningEventId || age < 0.0 || age > RingLightningEvent.LifetimeSeconds) continue;
            double around = ShortestAroundDelta(e.GroundPosition.AroundMeters, _explorer.CanonicalPosition.AroundMeters, circumference);
            double across = e.GroundPosition.AcrossMeters - _explorer.CanonicalPosition.AcrossMeters;
            double distanceSquared = around * around + across * across;
            if (distanceSquared > 72000.0 * 72000.0) continue;
            if (!closest.HasValue || distanceSquared < closestDistanceSquared
                || (Math.Abs(distanceSquared - closestDistanceSquared) < 0.001 && e.EventId < closest.Value.EventId))
            { closest = e; closestDistanceSquared = distanceSquared; }
        }
        if (closest.HasValue) SpawnLightningBolt(weather, closest.Value.GroundPosition, closest.Value);
    }

    private void SpawnLightningBolt(
        in RingVolumetricCloudLayer.LocalWeatherState weather,
        RingCanonicalPosition? target, RingLightningEvent? sharedEvent = null)
    {
        if (_grid?.TerrainSampler == null || _explorer == null || _lightningLight == null) return;
        RingCanonicalPosition player = _explorer.CanonicalPosition;
        float horizontalRadius = Mathf.Max(100.0f, StrikeHorizontalRadiusMeters);
        double targetAround = target?.AroundMeters
            ?? player.AroundMeters + _random.RandfRange(-horizontalRadius, horizontalRadius);
        double targetAcross = target?.AcrossMeters
            ?? Math.Clamp(
                player.AcrossMeters + _random.RandfRange(-horizontalRadius * 0.78f, horizontalRadius * 0.78f),
                0.0,
                _grid.RingAcrossWidthMeters);
        long aroundVoxel = (long)Math.Round(targetAround);
        int acrossVoxel = Mathf.Clamp(
            Mathf.RoundToInt((float)targetAcross),
            0,
            Mathf.Max(0, Mathf.RoundToInt(_grid.RingAcrossWidthMeters)));
		RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
		bool floatSurface = _grid.TerrainSampler.UsesRuntimeSurfaceGeneration || edits.UsesFloatSurface;
		double surface = floatSurface
			? edits.GetPresentationSurfaceHeight(_grid.TerrainSampler, aroundVoxel, acrossVoxel)
			: edits.GetSurfaceHeight(_grid.TerrainSampler, aroundVoxel, acrossVoxel);
		if (sharedEvent.HasValue) surface = sharedEvent.Value.GroundPosition.VerticalMeters;
        double cloudBase = sharedEvent?.CloudPosition.VerticalMeters ?? Math.Max(surface + MinimumBoltHeightMeters, weather.CloudBaseAltitudeMeters);
        double cloudTop = Math.Max(cloudBase + 250.0, weather.CloudTopAltitudeMeters);
        double desiredSourceHeight = sharedEvent?.CloudPosition.VerticalMeters ?? _random.RandfRange((float)cloudBase, (float)cloudTop);
        double sourceVertical = sharedEvent?.CloudPosition.VerticalMeters ?? Math.Clamp(
            desiredSourceHeight, surface + Math.Max(250.0f, MinimumBoltHeightMeters),
            surface + Math.Max(MinimumBoltHeightMeters + 100.0f, MaximumBoltHeightMeters));
        double sourceAround = sharedEvent?.CloudPosition.AroundMeters ?? targetAround + _random.RandfRange(-320.0f, 320.0f);
        double sourceAcross = sharedEvent?.CloudPosition.AcrossMeters ?? Math.Clamp(
            targetAcross + _random.RandfRange(-240.0f, 240.0f),
            0.0,
            _grid.RingAcrossWidthMeters);

        Vector3 startWorld = _grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(
            sourceAround,
            sourceVertical,
            sourceAcross));
        Vector3 endWorld = _grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(
            targetAround, sharedEvent?.GroundPosition.VerticalMeters ?? surface + 1.0, targetAcross));
        Vector3 downAxis = endWorld - startWorld;
        float boltLength = downAxis.Length();
        if (!startWorld.IsFinite() || !endWorld.IsFinite() || boltLength < 10.0f) return;
        downAxis /= boltLength;

        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        Vector3 targetCenter = geometry.CanonicalToWorld(targetAround, surface, targetAcross);
        Vector3 aroundAxis = (geometry.CanonicalToWorld(targetAround + 10.0, surface, targetAcross) - targetCenter).Normalized();
        Vector3 acrossAxis = (geometry.CanonicalToWorld(targetAround, surface, targetAcross + 10.0) - targetCenter).Normalized();
        float planeAngle = sharedEvent?.PlaneAngleRadians ?? _random.RandfRange(0.0f, Mathf.Tau);
        Vector3 sideAxis = aroundAxis * Mathf.Cos(planeAngle) + acrossAxis * Mathf.Sin(planeAngle);
        sideAxis = (sideAxis - downAxis * sideAxis.Dot(downAxis)).Normalized();
        if (!sideAxis.IsFinite() || sideAxis.LengthSquared() < 0.5f)
            sideAxis = Math.Abs(downAxis.Dot(Vector3.Up)) < 0.95f
                ? downAxis.Cross(Vector3.Up).Normalized()
                : downAxis.Cross(Vector3.Right).Normalized();

        int seed = sharedEvent?.ShapeSeed ?? unchecked(weather.SystemId * 73856093 ^ LightningFlashCount * 19349663 ^ (int)_random.Randi());
        var settings = new LightningSettings
        {
            Lod = BoltLod,
            Jitter = Mathf.Clamp(BoltJitter, 0.0f, 1.0f),
            EndpointAttraction = Mathf.Clamp(BoltEndpointAttraction, 0.0f, 1.0f),
            MaxBranches = Math.Clamp(BoltMaximumBranches, 0, 8),
            MaxSecondaryBranchDepth = Math.Clamp(BoltSecondaryBranchDepth, 0, 2),
            CoreWidth = Mathf.Max(0.01f, BoltCoreWidthMeters),
            GlowWidth = Mathf.Max(BoltCoreWidthMeters, BoltGlowWidthMeters)
        };
        _activeBolt = ProceduralLightningGenerator.Generate(
            Vector2.Zero,
            new Vector2(0.0f, boltLength),
            seed,
            settings);
        _activeBoltElapsed = sharedEvent.HasValue ? (float)Math.Max(0.0, _weatherSource!.CloudSimulationTimeSeconds - sharedEvent.Value.StartTimeSeconds) : 0.0f;
        _activeBoltStartTime = sharedEvent?.StartTimeSeconds ?? _weatherSource?.CloudSimulationTimeSeconds ?? 0.0;
        _activeSharedEventId = sharedEvent?.EventId ?? 0;
        _activeSharedEvent = sharedEvent;
        CurrentRenderedLightningEventId = _activeSharedEventId;
        LastRenderedLightningEventId = _activeSharedEventId;
        LastRenderedLightningShapeSeed = seed;
        LastRenderedLightningGroundPosition = sharedEvent?.GroundPosition ?? new RingCanonicalPosition(targetAround, surface, targetAcross);
        LastRenderedLightningCloudPosition = sharedEvent?.CloudPosition ?? new RingCanonicalPosition(sourceAround, sourceVertical, sourceAcross);
        InstallBoltMeshes(_activeBolt, startWorld, sideAxis, downAxis);

        _lightningLight.GlobalPosition = startWorld.Lerp(endWorld, 0.48f);
        _lightningLight.OmniRange = Mathf.Max(100.0f, LightningRangeMeters);
        _lightningLight.LightColor = LightningColor;
        LightningFlashCount++;
        if (sharedEvent.HasValue)
            GD.Print($"Local shared lightning consumed: event={sharedEvent.Value.EventId}, source={sharedEvent.Value.SystemId}, ground={sharedEvent.Value.GroundPosition}, cloud={sharedEvent.Value.CloudPosition}, worldStart={startWorld}, worldEnd={endWorld}, pulse={RingLightningEvent.PulseAtAge(_activeBoltElapsed):0.000}.");
        UpdateActiveBoltPulse();
    }

    private void InstallBoltMeshes(
        LightningBolt bolt,
        Vector3 startWorld,
        Vector3 sideAxis,
        Vector3 downAxis)
    {
        if (_boltRoot == null || _boltCore == null || _boltGlow == null) return;
        _boltRoot.GlobalTransform = new Transform3D(Basis.Identity, startWorld);
        _boltCore.Mesh = BuildBoltMesh(bolt, sideAxis, downAxis, Mathf.Max(0.01f, BoltCoreWidthMeters));
        _boltGlow.Mesh = BuildBoltMesh(bolt, sideAxis, downAxis, Mathf.Max(BoltCoreWidthMeters, BoltGlowWidthMeters));
        _boltRoot.Visible = true;
    }

    private static ArrayMesh? BuildBoltMesh(
        LightningBolt bolt,
        Vector3 sideAxis,
        Vector3 downAxis,
        float widthMeters)
    {
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        AppendPathTubes(surface, bolt.MainPath, sideAxis, downAxis, widthMeters);
        foreach (LightningBranch branch in bolt.Branches)
            AppendBranchTubes(surface, branch, sideAxis, downAxis, widthMeters);
        return surface.Commit();
    }

    private static void AppendBranchTubes(
        SurfaceTool surface,
        LightningBranch branch,
        Vector3 sideAxis,
        Vector3 downAxis,
        float widthMeters)
    {
        AppendPathTubes(surface, branch.Path, sideAxis, downAxis, widthMeters);
        foreach (LightningBranch child in branch.Children)
            AppendBranchTubes(surface, child, sideAxis, downAxis, widthMeters);
    }

    private static void AppendPathTubes(
        SurfaceTool surface,
        LightningPath path,
        Vector3 sideAxis,
        Vector3 downAxis,
        float widthMeters)
    {
        const int TubeSides = 4;
        for (int segment = 1; segment < path.Points.Count; segment++)
        {
            LightningPoint previous = path.Points[segment - 1];
            LightningPoint current = path.Points[segment];
            Vector3 start = MapBoltPoint(previous.Position, sideAxis, downAxis);
            Vector3 end = MapBoltPoint(current.Position, sideAxis, downAxis);
            Vector3 direction = end - start;
            if (direction.LengthSquared() < 0.000001f) continue;
            direction = direction.Normalized();
            Vector3 reference = Math.Abs(direction.Dot(sideAxis)) < 0.92f ? sideAxis : downAxis;
            Vector3 radialA = direction.Cross(reference).Normalized();
            if (!radialA.IsFinite() || radialA.LengthSquared() < 0.5f)
                radialA = direction.Cross(Vector3.Right).Normalized();
            Vector3 radialB = direction.Cross(radialA).Normalized();
            float startRadius = widthMeters * Mathf.Max(0.05f, previous.Width) * 0.5f;
            float endRadius = widthMeters * Mathf.Max(0.05f, current.Width) * 0.5f;
            for (int side = 0; side < TubeSides; side++)
            {
                float angle0 = Mathf.Tau * side / TubeSides;
                float angle1 = Mathf.Tau * (side + 1) / TubeSides;
                Vector3 normal0 = radialA * Mathf.Cos(angle0) + radialB * Mathf.Sin(angle0);
                Vector3 normal1 = radialA * Mathf.Cos(angle1) + radialB * Mathf.Sin(angle1);
                Vector3 a = start + normal0 * startRadius;
                Vector3 b = start + normal1 * startRadius;
                Vector3 c = end + normal1 * endRadius;
                Vector3 d = end + normal0 * endRadius;
                AddBoltTriangle(surface, a, b, c, normal0, normal1, normal1);
                AddBoltTriangle(surface, a, c, d, normal0, normal1, normal0);
            }
        }
    }

    private static Vector3 MapBoltPoint(Vector2 point, Vector3 sideAxis, Vector3 downAxis) =>
        sideAxis * point.X + downAxis * point.Y;

    private static void AddBoltTriangle(
        SurfaceTool surface,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 normalA,
        Vector3 normalB,
        Vector3 normalC)
    {
        surface.SetNormal(normalA);
        surface.AddVertex(a);
        surface.SetNormal(normalB);
        surface.AddVertex(b);
        surface.SetNormal(normalC);
        surface.AddVertex(c);
    }

    private void UpdateActiveBoltPulse()
    {
        if (_activeBolt == null || _lightningLight == null) return;
        if (_activeSharedEventId != 0)
        {
            RefreshSharedBoltTransform();
            float shared = RingLightningEvent.PulseAtAge(_activeBoltElapsed);
            float visualWeight = _activeSharedEvent.HasValue && _explorer != null && _grid != null
                ? LightningDistanceHandoff.Evaluate(
                    _explorer.CanonicalPosition,
                    _activeSharedEvent.Value.GroundPosition,
                    _grid.GetCanonicalRingCircumferenceMeters64(),
                    _farViewport?.EffectiveFarCloudClipmapRadiusMeters ?? 180000.0f,
                    (float)(_grid.RingAcrossWidthMeters * 0.5),
                    _farViewport?.FarCloudClipmapEnabled == true ? 3 : 0).Local
                : 1.0f;
            CurrentRenderedLightningPulse = shared;
            CurrentRenderedLightningVisualWeight = visualWeight;
            _lightningLight.LightEnergy = Mathf.Max(0.0f, LightningEnergy)
                * Mathf.Max(0.0f, _activeSharedEvent?.Energy ?? 1.0f) * shared * visualWeight;
            _sunshineBridge?.SetLightningFlashIntensity(shared * visualWeight);
            _boltCoreMaterial?.SetShaderParameter("intensity", shared);
            _boltGlowMaterial?.SetShaderParameter("intensity", shared * 0.65f);
            _boltCoreMaterial?.SetShaderParameter("ownership_weight", visualWeight);
            _boltGlowMaterial?.SetShaderParameter("ownership_weight", visualWeight);
            return;
        }
        float core = 0.0f;
        float glow = 0.0f;
        foreach (LightningStrike strike in _activeBolt.Strikes)
        {
            float age = _activeBoltElapsed - strike.Time;
            if (age < 0.0f) continue;
            float coreEnvelope = 1.0f - age / Mathf.Max(0.01f, strike.CoreFadeTime);
            float glowEnvelope = 1.0f - age / Mathf.Max(0.01f, strike.GlowFadeTime);
            core = Mathf.Max(core, Mathf.Max(0.0f, coreEnvelope) * strike.Intensity);
            glow = Mathf.Max(glow, Mathf.Max(0.0f, glowEnvelope) * strike.Intensity);
        }
        float sharedIntensity = Mathf.Max(core, glow * 0.42f);
        _lightningLight.LightEnergy = Mathf.Max(0.0f, LightningEnergy) * sharedIntensity;
        _sunshineBridge?.SetLightningFlashIntensity(sharedIntensity);
        _boltCoreMaterial?.SetShaderParameter("intensity", core);
        _boltGlowMaterial?.SetShaderParameter("intensity", glow);
        _boltCoreMaterial?.SetShaderParameter("ownership_weight", 1.0f);
        _boltGlowMaterial?.SetShaderParameter("ownership_weight", 1.0f);
    }

    private void ClearActiveBolt()
    {
        LightningBolt? completedBolt = _activeBolt;
        bool smokeTestPassed = completedBolt != null
            && LightningFlashCount > 0
            && _boltCore?.Mesh is ArrayMesh coreMesh
            && coreMesh.GetSurfaceCount() > 0
            && _boltGlow?.Mesh is ArrayMesh glowMesh
            && glowMesh.GetSurfaceCount() > 0;

        _activeBolt = null;
        _activeBoltElapsed = 0.0f;
        CurrentRenderedLightningPulse = 0.0f;
        CurrentRenderedLightningVisualWeight = 0.0f;
        _activeSharedEventId = 0;
        _activeSharedEvent = null;
        _lastSharedBoltStart = new Vector3(float.PositiveInfinity, 0, 0);
        _lastSharedBoltEnd = new Vector3(float.PositiveInfinity, 0, 0);
        CurrentRenderedLightningEventId = 0;
        if (_lightningLight != null) _lightningLight.LightEnergy = 0.0f;
        _sunshineBridge?.SetLightningFlashIntensity(0.0f);
        if (_boltRoot != null) _boltRoot.Visible = false;
        _boltCoreMaterial?.SetShaderParameter("intensity", 0.0f);
        _boltGlowMaterial?.SetShaderParameter("intensity", 0.0f);
        _boltCoreMaterial?.SetShaderParameter("ownership_weight", 1.0f);
        _boltGlowMaterial?.SetShaderParameter("ownership_weight", 1.0f);

        if (_lightningSmokeTest)
        {
            if (smokeTestPassed)
            {
                LightningBolt bolt = completedBolt!;
                GD.Print(
                    $"Shared lightning smoke test passed: points={bolt.MainPath.Points.Count}, "
                    + $"branches={bolt.Branches.Count}, strikes={bolt.Strikes.Count}.");
            }
            else
            {
                GD.PushError("Shared lightning smoke test failed: no complete procedural bolt mesh was produced.");
            }

            _lightningSmokeTest = false;
        }
    }

    private static double ShortestAroundDelta(double from, double to, double circumference)
    {
        double delta = (from - to) % circumference;
        if (delta > circumference * 0.5) delta -= circumference;
        if (delta < -circumference * 0.5) delta += circumference;
        return delta;
    }

    // The canonical endpoints are authoritative.  Recreate only the mesh
    // transform when the floating origin or curved-grid basis changes; the
    // generated bolt, event id, and deterministic seed stay untouched.
    private void RefreshSharedBoltTransform()
    {
        if (_activeSharedEvent is not { } e || _activeBolt == null || _grid == null) return;
        Vector3 start = _grid.CanonicalRingPositionToWorld(e.CloudPosition);
        Vector3 end = _grid.CanonicalRingPositionToWorld(e.GroundPosition);
        Vector3 down = end - start;
        float length = down.Length();
        if (!start.IsFinite() || !end.IsFinite() || length < 10.0f) return;
        down /= length;
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        Vector3 center = geometry.CanonicalToWorld(e.GroundPosition.AroundMeters, e.GroundPosition.VerticalMeters, e.GroundPosition.AcrossMeters);
        Vector3 around = (geometry.CanonicalToWorld(e.GroundPosition.AroundMeters + 10.0, e.GroundPosition.VerticalMeters, e.GroundPosition.AcrossMeters) - center).Normalized();
        Vector3 across = (geometry.CanonicalToWorld(e.GroundPosition.AroundMeters, e.GroundPosition.VerticalMeters, e.GroundPosition.AcrossMeters + 10.0) - center).Normalized();
        Vector3 side = (around * Mathf.Cos(e.PlaneAngleRadians) + across * Mathf.Sin(e.PlaneAngleRadians));
        side = (side - down * side.Dot(down)).Normalized();
        if (!side.IsFinite() || side.LengthSquared() < 0.5f)
            side = Math.Abs(down.Dot(Vector3.Up)) < 0.95f ? down.Cross(Vector3.Up).Normalized() : down.Cross(Vector3.Right).Normalized();
        if (!_lastSharedBoltStart.IsEqualApprox(start) || !_lastSharedBoltEnd.IsEqualApprox(end))
        {
            InstallBoltMeshes(_activeBolt, start, side, down);
            _lastSharedBoltStart = start;
            _lastSharedBoltEnd = end;
        }
        if (_lightningLight != null) _lightningLight.GlobalPosition = start.Lerp(end, 0.48f);
    }

    private static float SmoothUnit(float value)
    {
        float t = Mathf.Clamp(value, 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }
}
