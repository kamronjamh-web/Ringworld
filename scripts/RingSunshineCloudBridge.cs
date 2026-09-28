using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Diagnostics;

namespace RingworldTechDemo;

/// <summary>
/// Adapts Sunshine Clouds 2 to the ring's canonical weather and curved local
/// frame. The existing cloud layer keeps ownership of far clouds and shadows.
/// </summary>
public partial class RingSunshineCloudBridge : Node
{
    private const string SunshinePreviewWeatherOwnerKey = "sunshine_preview";
    private const int SunshinePreviewWeatherSystemId = -1003;
    // Sunshine advects its coverage-defining extra-large/large structures at
    // 0.22x/0.55x weather wind. The GPU map is rebaked from that field, so
    // receiver-side motion must follow the large-structure bulk component
    // rather than the 1.0x medium-detail velocity or it visibly sawtooths at
    // every map refresh.
    private const float GpuShadowBulkWindFactor = 0.55f;
    [Export] public bool Enabled { get; set; } = true;
    /// <summary>Mirrors the approved global GPU timestamp capture state to Sunshine's compositor.</summary>
    [Export] public bool GpuTimestampDiagnosticsEnabled { get; set; }
    private bool _appliedGpuTimestampDiagnosticsEnabled;
    private string _appliedGpuTimestampBeginMarker = string.Empty, _appliedGpuTimestampEndMarker = string.Empty;
    /// <summary>Project-saved gameplay cloud overrides shared by the runtime drawer.</summary>
    public UI.GameplayCloudDefaults GameplayCloudDefaults { get; } = new();
    [Export] public NodePath DriverPath { get; set; } = new("../SunshineCloudsDriver");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath WeatherSourcePath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath WorldEnvironmentPath { get; set; } = new("../WorldEnvironment");
    [Export] public NodePath CameraPath { get; set; } = new("../Explorer/Head/Camera3D");
    [Export] public NodePath ScaledFarViewportPath { get; set; } = new("../ScaledFarViewport");
    [Export(PropertyHint.Range, "5000,80000,500")] public float HandoffStartMeters { get; set; } = 36000.0f;
    [Export(PropertyHint.Range, "6000,100000,500")] public float HandoffEndMeters { get; set; } = 48000.0f;
    [Export(PropertyHint.Range, "5000,1000000,1000")] public float SuspendSpeedMetersPerSecond { get; set; } = 10000.0f;
    [Export(PropertyHint.Range, "5000,50000,500")] public float AltitudeFadeStartMeters { get; set; } = 12000.0f;
    [Export(PropertyHint.Range, "6000,60000,500")] public float AltitudeFadeEndMeters { get; set; } = 22000.0f;
    [Export(PropertyHint.Range, "0.05,2,0.05")] public float WeatherRefreshSeconds { get; set; } = 0.25f;
    [ExportGroup("Cloud shadows")]
    [Export] public bool CastCloudShadows { get; set; } = true;
    [Export] public bool PreferGpuCloudShadows { get; set; } = true;
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export(PropertyHint.Range, "8000,150000,500")] public float ShadowRadiusMeters { get; set; } = 24000.0f;
    [Export(PropertyHint.Range, "16000,200000,1000")] public float MaximumShadowRadiusMeters { get; set; } = 96000.0f;
    [Export(PropertyHint.Range, "16,128,8")] public int ShadowMapResolution { get; set; } = 64;
    [Export(PropertyHint.Range, "64,2048,64")] public int GpuNearShadowMapResolution { get; set; } = 1024;
    [Export(PropertyHint.Range, "32,2048,32")] public int GpuFarShadowMapResolution { get; set; } = 256;
    [Export(PropertyHint.Range, "8000,80000,500")] public float GpuNearShadowRadiusMeters { get; set; } = 24000.0f;
    [Export(PropertyHint.Range, "16000,200000,1000")] public float GpuFarShadowRadiusMeters { get; set; } = 96000.0f;
    [Export(PropertyHint.Range, "1,20,0.25")] public float GpuNearShadowUpdatesPerSecond { get; set; } = 10.0f;
    [Export(PropertyHint.Range, "0.1,5,0.1")] public float GpuFarShadowUpdatesPerSecond { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "1,32,1")] public int GpuShadowVerticalSamples { get; set; } = 4;
    [ExportGroup("Cloud shadow profiling")]
    [Export] public bool GpuShadowGenerationEnabled { get; set; } = true;
    [Export] public bool CloudShadowReceiversEnabled { get; set; } = true;
    [Export(PropertyHint.Range, "1,8,1")] public int ShadowVerticalSamples { get; set; } = 5;
    [Export(PropertyHint.Range, "0.1,15,0.1")] public float ShadowRefreshSeconds { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ShadowStrength { get; set; } = 0.98f;
    [Export(PropertyHint.Range, "0.01,0.95,0.01")] public float ShadowDensityThreshold { get; set; } = 0.06f;
    [Export(PropertyHint.Range, "0.05,2,0.05")] public float ShadowExtinctionPerKilometer { get; set; } = 1.4f;
    [Export(PropertyHint.Range, "0.02,0.5,0.01")] public float StormAmbientFloor { get; set; } = 0.14f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float StormAmbientOcclusionStrength { get; set; } = 0.86f;
    [Export(PropertyHint.Range, "0.1,8,0.1")] public float LightingResponseSpeed { get; set; } = 1.4f;
    [Export] public bool LogStartupDensityProbe { get; set; } = false;

    [ExportGroup("Local day/night cloud lighting")]
    [Export] public Color DayCloudAmbientColor { get; set; } = new(0.91f, 0.95f, 1.0f, 1.0f);
    [Export] public Color NightCloudAmbientColor { get; set; } = new(0.18f, 0.23f, 0.35f, 1.0f);
    [Export] public Color NightCloudAmbientTint { get; set; } = new(0.28f, 0.36f, 0.55f, 1.0f);
    [Export] public Color StormCloudAmbientColor { get; set; } = new(0.10f, 0.13f, 0.19f, 1.0f);
    [Export(PropertyHint.Range, "0.4,1,0.01")] public float HeavyOvercastStartCoverage { get; set; } = 0.60f;
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float HeavyOvercastAmbientTransmittance { get; set; } = 0.22f;

    [ExportGroup("Localized storm sculpting")]
    [Export] public bool SculptLocalizedStorms { get; set; } = true;
    [Export(PropertyHint.Range, "1000,8000,250")] public float StormPillarRadiusMeters { get; set; } = 4200.0f;
    [Export(PropertyHint.Range, "1.2,4.0,0.1")] public float AnvilRadiusMultiplier { get; set; } = 2.6f;
    [Export(PropertyHint.Range, "0.5,0.9,0.01")] public float AnvilStartFraction { get; set; } = 0.70f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float StormSculptStrength { get; set; } = 0.92f;

    [ExportGroup("Cloud altitude anchoring")]
    [Export(PropertyHint.Range, "-5000,10000,50")] public float FixedCloudAltitudeDatumMeters { get; set; } = 0.0f;
    // The ordinary field stays above the playable skyline; localized fronts
    // deliberately retain their own lower 500 m bases.
    [Export(PropertyHint.Range, "0,19000,50")] public float NaturalCloudBaseAltitudeMeters { get; set; } = 2000.0f;
    [Export(PropertyHint.Range, "500,20000,50")] public float NaturalCloudTopAltitudeMeters { get; set; } = 6000.0f;
    [Export(PropertyHint.Range, "0,5000,50")] public float StormFrontCloudBaseAltitudeMeters { get; set; } = 500.0f;

    [ExportGroup("Temporal quality")]
    [Export(PropertyHint.Range, "0,2,1")] public int CloudResolutionScale { get; set; } = 0;
    [Export(PropertyHint.Range, "0,0.95,0.01")] public float StationaryHistoryWeight { get; set; } = 0.95f;
    [Export(PropertyHint.Range, "0,0.95,0.01")] public float MovingHistoryWeight { get; set; } = 0.13f;
    [Export(PropertyHint.Range, "1,250,1")] public float MinimumHistorySpeedMetersPerSecond { get; set; } = 32.0f;
    [Export(PropertyHint.Range, "1,360,1")] public float MinimumHistoryRotationDegreesPerSecond { get; set; } = 42.0f;
    [Export(PropertyHint.Range, "0.1,90,0.1")] public float MinimumHistorySunRotationDegreesPerSecond { get; set; } = 3.0f;
    [Export(PropertyHint.Range, "0.01,45,0.01")] public float SunHistoryDiscontinuityDegrees { get; set; } = 8.0f;
    [Export(PropertyHint.Range, "64,256,8")] public int RaymarchSteps { get; set; } = 128;
    [Export(PropertyHint.Range, "0,120,1")] public float DitherSpeed { get; set; } = 0.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RayStartJitterStrength { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0,6,0.1")] public float BlurPower { get; set; } = 0.1f;
    // These remain bridge-owned scalar overrides: weather refreshes continue
    // to own coverage, density, lighting color, altitude, and wind.
    [Export(PropertyHint.Range, "10,500,1")] public float MinStepDistanceMeters { get; set; } = 90.0f;
    [Export(PropertyHint.Range, "100,4000,1")] public float MaxStepDistanceMeters { get; set; } = 560.0f;
    [Export(PropertyHint.Range, "0.5,20,0.1")] public float LightingTravelKm { get; set; } = 7.0f;
    [Export(PropertyHint.Range, "0.1,2,0.01")] public float CloudLodBias { get; set; } = 0.9f;
    [Export(PropertyHint.Range, "1,8,1")] public int LightingSteps { get; set; } = 8;
    [Export(PropertyHint.Range, "1,4,1")] public int BlurQuality { get; set; } = 1;
    [Export] public bool FastReconstruction { get; set; } = false;

    [ExportGroup("Gameplay cloud appearance")]
    [Export] public bool CloudRenderingEnabled { get; set; } = true;
    [Export(PropertyHint.Range, "-1,1,0.01")] public float CloudCoverageBias { get; set; } = 0.0f;
    [Export(PropertyHint.Range, "0,3,0.01")] public float CloudDensityMultiplier { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0,3,0.01")] public float CloudWindMultiplier { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float CloudDirectLightMultiplier { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float CloudAmbientMultiplier { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float CloudSharpness { get; set; } = 0.62f;

    [ExportGroup("Spawned weather preview")]
    [Export(PropertyHint.Range, "3000,50000,500")] public float SpawnDistanceMeters { get; set; } = 12000.0f;
    [Export(PropertyHint.Range, "10000,100000,1000")] public float SpawnFrontMajorRadiusMeters { get; set; } = 55000.0f;
    [Export(PropertyHint.Range, "5000,50000,1000")] public float SpawnFrontMinorRadiusMeters { get; set; } = 26000.0f;
    [Export(PropertyHint.Range, "5000,50000,1000")] public float SpawnAnvilRadiusMeters { get; set; } = 24000.0f;

    public bool RuntimeVisible { get; private set; }
    public RingVolumetricCloudLayer.LocalWeatherState CurrentWeather => _currentWeather;
    public float EffectiveHistoryWeight { get; private set; }
    /// <summary>Read-only solar temporal-history diagnostics for continuous captures.</summary>
    public float SunHistorySuppression => _sunHistorySuppression;
    public bool SunHistoryWasSuppressed => _sunHistoryWasSuppressed;
    public float AppliedHistoryWeight => _lastAppliedHistoryWeight;
    /// <summary>Most recent camera-motion blend used to resolve temporal history.</summary>
    public float CameraHistoryMotion { get; private set; }
    /// <summary>Number of explicit temporal-quality pushes to the live Sunshine resource.</summary>
    public int TemporalHistoryApplyCount { get; private set; }
    public int SunTemporalHistoryRefreshCount { get; private set; }
    public string SpawnedWeatherLabel => _spawnedWeather?.Label ?? "Natural weather";
    public string CloudShowcaseLabel { get; private set; } = "No catalog cloud spawned";
    public Vector2 BackgroundCloudAltitudeMeters => new(_backgroundCloudFloorMeters, _backgroundCloudCeilingMeters);
    /// <summary>The complete vertical band currently raymarched by Sunshine.</summary>
    public Vector2 CloudTraversalAltitudeMeters { get; private set; } = new(2_000.0f, 3_000.0f);
    public Vector2 SpawnedStormAltitudeMeters => _spawnedWeather is { } spawned
        ? new Vector2(
            spawned.AltitudeDatumMeters + spawned.CloudBaseAboveDatumMeters,
            spawned.AltitudeDatumMeters + spawned.CloudTopAboveDatumMeters)
        : Vector2.Zero;
    public float CameraDirectSunTransmittance { get; private set; } = 1.0f;
    public float CameraAmbientTransmittance { get; private set; } = 1.0f;
    public float InstalledShadowRadiusMeters { get; private set; }
    public int InstalledShadowResolution { get; private set; }
    public float InstalledShadowPeakOpacity { get; private set; }
    public bool UsingGpuCloudShadows { get; private set; }
    public string GpuCloudShadowStatus { get; private set; } = "not requested";

    /// <summary>Enables mutually exclusive compositor density capture modes.</summary>
    public bool SetDiagnosticDensityMattes(bool rawDensity, bool depthTestedDensity)
    {
        if (_clouds == null) return false;
        // A density matte and a lit history-off frame answer different
        // diagnostic questions, so never leave both modes active.
        if (rawDensity || depthTestedDensity)
            _clouds.Set("diagnostic_disable_temporal_history", false);
        _clouds.Set("diagnostic_raw_density_matte", rawDensity);
        _clouds.Set("diagnostic_depth_density_matte", depthTestedDensity);
        RefreshCloudCompute();
        return true;
    }

    /// <summary>Disables lit temporal history for one diagnostic capture mode.</summary>
    public bool SetDiagnosticTemporalHistoryDisabled(bool disabled)
    {
        if (_clouds == null) return false;
        if (disabled)
        {
            _clouds.Set("diagnostic_raw_density_matte", false);
            _clouds.Set("diagnostic_depth_density_matte", false);
        }
        _clouds.Set("diagnostic_disable_temporal_history", disabled);
        RefreshCloudCompute();
        return true;
    }

    public bool DiagnosticRawDensityMatteEnabled => _clouds?.Get("diagnostic_raw_density_matte").AsBool() ?? false;
    public bool DiagnosticDepthDensityMatteEnabled => _clouds?.Get("diagnostic_depth_density_matte").AsBool() ?? false;
    public bool DiagnosticTemporalHistoryDisabled => _clouds?.Get("diagnostic_disable_temporal_history").AsBool() ?? false;
    public Vector3 CloudFrameUp => _clouds == null
        ? Vector3.Up
        : new Vector3(_clouds.Get("cloud_frame_up").AsVector4().X, _clouds.Get("cloud_frame_up").AsVector4().Y, _clouds.Get("cloud_frame_up").AsVector4().Z).Normalized();

    private Node? _driver;
    private Resource? _clouds;
    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private RingVolumetricCloudLayer? _weather;
    private WorldEnvironment? _worldEnvironment;
    private DirectionalLight3D? _sunLight;
    private Camera3D? _camera;
    private RingScaledFarViewport? _scaledFarViewport;
    private Callable? _externalTextureRetirementCallable;
    private double _weatherElapsed = double.PositiveInfinity;
    private RingVolumetricCloudLayer.LocalWeatherState _currentWeather;
    private bool _lastVisible;
    private float _publishedHandoffStartMeters = float.NaN;
    private float _publishedHandoffEndMeters = float.NaN;
    private int _pendingDensityProbes;
    private float _maximumDensityProbe;
    private ImageTexture? _shadowTexture;
    private float[]? _shadowDensitySums;
    private int[]? _shadowDensitySampleCounts;
    private float[]? _shadowPathKilometers;
    private byte[]? _pendingShadowMapData;
    private int _shadowSamplesRemaining;
    private int _shadowBuildResolution;
    // Sunshine recreates GPU resources from this setter, so avoid assigning it
    // again unless the requested compositor resolution really changed.
    private int? _appliedCloudResolutionScale;
    private int? _appliedLightingSteps;
    private float _authoredDirectionalLightPowerMultiplier = 1.0f;
    private bool _hasAuthoredDirectionalLightPowerMultiplier;
    private int _shadowBuildGeneration;
    private int _pendingShadowOccupiedTexels;
    private byte _pendingShadowPeak;
    private double _shadowElapsed = double.PositiveInfinity;
    private bool _shadowBuildPending;
    private bool _shadowMapReady;
    private bool _shadowProjectorEnabled;
    private bool _shadowDiagnosticsReported;
    private bool _hasShadowCenter;
    private RingCanonicalPosition _shadowCenterCanonical;
    private Vector3 _lastShadowCenterWorld = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
    private bool _hasPreviousCameraPose;
    private Vector3 _previousCameraPosition;
    private Vector3 _previousCameraForward;
    private float _shadowBuildRadiusMeters;
    private float _targetCameraDirectSunTransmittance = 1.0f;
    private float _targetCameraAmbientTransmittance = 1.0f;
    private float _pendingCameraDirectSunTransmittance = 1.0f;
    private float _pendingCameraAmbientTransmittance = 1.0f;
    private SpawnedWeatherPreview? _spawnedWeather;
    private float _backgroundCloudFloorMeters;
    private float _backgroundCloudCeilingMeters;
    private float _localDayFactor = 1.0f;
    private float _localStormFactor;
    private float _heavyOvercastFactor;
    private float _lightningHistorySuppression;
    private float _sunHistorySuppression;
    private bool _hasPreviousSunDirection;
    private Vector3 _previousSunDirection;
    private bool _sunHistoryWasSuppressed;
    private Vector3 _lastCloudFrameOrigin = new(float.PositiveInfinity, 0.0f, 0.0f);
    private Vector4 _lastCloudFrameAround = new(float.PositiveInfinity, 0.0f, 0.0f, 0.0f);
    private Vector4 _lastCloudFrameUp = new(float.PositiveInfinity, 0.0f, 0.0f, 0.0f);
    private Vector4 _lastCloudFrameAcross = new(float.PositiveInfinity, 0.0f, 0.0f, 0.0f);
    private float _lastCloudFrameRingWidth = float.NaN;
    private float _lastAppliedHistoryWeight = float.NaN;
    private readonly RingCanonicalPosition[] _gpuShadowCenters = new RingCanonicalPosition[2];
    private readonly bool[] _hasGpuShadowCenter = new bool[2];
    // Cascade configuration is consumed by Sunshine's compositor callback after
    // the main scene has rendered. Keep a requested state separate so receivers
    // never switch to a new projection one frame before its compute map exists.
    private readonly RingCanonicalPosition[] _gpuShadowRequestedCenters = new RingCanonicalPosition[2];
    private readonly bool[] _gpuShadowPublishPending = new bool[2];
    private readonly int[] _gpuShadowRequestedDispatchCount = new int[2];
    private readonly Vector3[] _gpuShadowRequestedBakedSun = new Vector3[2];
    private readonly Vector2[] _gpuShadowRequestedBakedProjection = new Vector2[2];
    private readonly int[] _gpuShadowRequestedBakedResolution = new int[2];
    private readonly float[] _gpuShadowRequestedBakedRadius = new float[2];
    private readonly Vector2[] _gpuShadowRequestedWindOffsetMeters = new Vector2[2];
    private readonly double[] _gpuShadowElapsed = { double.PositiveInfinity, double.PositiveInfinity };
    private readonly Vector2[] _gpuShadowWindOffsetMeters = new Vector2[2];
    private readonly Vector3[] _gpuShadowBakedSun = new Vector3[2];
    private readonly Vector2[] _gpuShadowBakedProjection = new Vector2[2];
    private Vector2 _gpuShadowFrameWindDisplacement;
    private double _gpuPipelineWaitSeconds;
    private readonly int[] _gpuShadowBakedResolution = new int[2];
    private readonly float[] _gpuShadowBakedRadius = new float[2];
    private int _nextTimedGpuCascade;
    // A Texture2DRD belongs to the compositor.  When a resolution change
    // replaces either cascade, keep every receiver on its clear fallback until
    // both replacement maps have completed their first dispatch.
    private bool _gpuShadowReceiverRebuildPending;

    private readonly record struct SpawnedWeatherPreview(
        string Label,
        RingVolumetricCloudLayer.WeatherSystemKind Kind,
        CloudFormationProfile Profile,
        Vector2 CenterMeters,
        Vector2 RadiiMeters,
        float OrientationRadians,
        float Coverage,
        float Precipitation,
        float CloudBaseAboveDatumMeters,
        float CloudTopAboveDatumMeters,
        float AltitudeDatumMeters);

    private readonly record struct CloudAltitudeRange(float FloorMeters, float CeilingMeters);

    public override void _Ready()
    {
        _driver = GetNodeOrNull<Node>(DriverPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherSourcePath);
        _worldEnvironment = GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath);
        _sunLight = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        _camera = GetNodeOrNull<Camera3D>(CameraPath);
        _scaledFarViewport = GetNodeOrNull<RingScaledFarViewport>(ScaledFarViewportPath);
        if (_driver != null)
        {
            float authoredMultiplier = _driver.Get("directional_light_power_multiplier").AsSingle();
            _authoredDirectionalLightPowerMultiplier = float.IsFinite(authoredMultiplier)
                ? authoredMultiplier
                : 1.0f;
            _hasAuthoredDirectionalLightPowerMultiplier = true;
        }
        _clouds = _driver?.Get("clouds_resource").AsGodotObject() as Resource;
        if (_driver == null || _clouds == null || _explorer == null || _grid?.TerrainSampler == null || _weather == null || _worldEnvironment == null)
        {
            _weather?.SetExternalLocalVolumetricCloudsState(
                ownsLocalClouds: false,
                localVolumeVisible: false,
                HandoffStartMeters,
                HandoffEndMeters);
            GD.PushError("Sunshine cloud bridge disabled: driver, resource, explorer, grid, or weather source is missing.");
            SetProcess(false);
            return;
        }
        if (_clouds.HasSignal("external_texture_retirement_requested"))
        {
            _externalTextureRetirementCallable = Callable.From<long>(OnExternalTextureRetirementRequested);
            _clouds.Connect("external_texture_retirement_requested", _externalTextureRetirementCallable.Value);
        }

        if (_grid.GetCanonicalRingCircumferenceMeters64() > int.MaxValue)
        {
            _driver.Set("update_continuously", false);
            SetProcess(false);
            GD.Print("Sunshine cloud bridge disabled: Mega Ring uses the scaled far backdrop without legacy cloud compositing.");
            return;
        }

        if (CastCloudShadows && _sunLight == null)
            GD.PushWarning("Sunshine cloud shadows disabled: the directional sunlight node is missing.");

        _driver.Set("update_continuously", true);
        CallDeferred(nameof(InitializeRuntime));
    }

    private void InitializeRuntime()
    {
        GameplayCloudDefaults.Initialize(this);
        ApplyTuningToResource(refreshHistory: false);
        // A provider controller may have disabled Sunshine before this
        // deferred initialization runs. In that case it must not only remain
        // detached; it must also avoid UpdateVisibility(), which would publish
        // a false external-owner state and make the legacy macro renderer
        // reclaim CLP's local-cloud and terrain-shadow ownership.
        if (!Enabled)
        {
            DetachCompositorFromTargetEnvironment();
            _clouds?.Set("enabled", false);
            _driver?.Set("update_continuously", false);
            RuntimeVisible = false;
            _lastVisible = false;
            SetProcess(false);
            int inactiveEffectCount = _worldEnvironment?.Compositor?.CompositorEffects.Count ?? 0;
            GD.Print($"Sunshine Clouds 2 ring bridge ready but provider-inactive; compositorEffects={inactiveEffectCount}.");
            return;
        }

        AttachCompositorToTargetEnvironment();
        RefreshWeather();
        UpdateCloudFrame();
        UpdateVisibility(force: true);
        UpdateCloudShadows(0.0, force: true);
        int compositorEffectCount = _worldEnvironment?.Compositor?.CompositorEffects.Count ?? 0;
        GD.Print($"Sunshine Clouds 2 ring bridge ready: local handoff {HandoffStartMeters:0}-{HandoffEndMeters:0}m, compositorEffects={compositorEffectCount}.");
        if (LogStartupDensityProbe) GetTree().CreateTimer(1.0).Timeout += QueueStartupDensityProbe;
    }

    private void QueueStartupDensityProbe()
    {
        if (_clouds == null || _grid == null || _explorer == null) return;
        RingCanonicalPosition viewer = _explorer.CanonicalPosition;
        float middle = (_clouds.Get("cloud_floor").AsSingle() + _clouds.Get("cloud_ceiling").AsSingle()) * 0.5f;
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        Vector3 centerPoint = geometry.CanonicalToWorld(viewer.AroundMeters, middle, viewer.AcrossMeters);
        Vector3 frameOrigin = _clouds.Get("cloud_frame_origin").AsVector3();
        Vector4 frameUp = _clouds.Get("cloud_frame_up").AsVector4();
        float cpuFrameHeight = (centerPoint - frameOrigin).Dot(new Vector3(frameUp.X, frameUp.Y, frameUp.Z));
        GD.Print(
            $"Sunshine density probe frame: floor={_clouds.Get("cloud_floor").AsSingle():0.0}, "
            + $"ceiling={_clouds.Get("cloud_ceiling").AsSingle():0.0}, target={middle:0.0}, "
            + $"cpuFrameHeight={cpuFrameHeight:0.0}, origin={frameOrigin}, point={centerPoint}.");
        _pendingDensityProbes = 0;
        _maximumDensityProbe = 0.0f;
        for (int aroundOffset = -8000; aroundOffset <= 8000; aroundOffset += 4000)
        {
            for (int acrossOffset = -8000; acrossOffset <= 8000; acrossOffset += 4000)
            {
                double across = Math.Clamp(viewer.AcrossMeters + acrossOffset, 0.0, _grid.RingAcrossWidthMeters);
                Vector3 point = geometry.CanonicalToWorld(viewer.AroundMeters + aroundOffset, middle, across);
                _pendingDensityProbes++;
                _clouds.Call("add_sample", Callable.From<Vector3, float>(OnDensityProbeResult), point);
            }
        }
    }

    private void OnDensityProbeResult(Vector3 position, float density)
    {
        _maximumDensityProbe = Mathf.Max(_maximumDensityProbe, density);
        _pendingDensityProbes--;
        if (_pendingDensityProbes <= 0)
            GD.Print($"Sunshine GPU density probe: max={_maximumDensityProbe:0.000} across 25 in-layer samples.");
    }

    private void AttachCompositorToTargetEnvironment()
    {
        if (_worldEnvironment == null || _clouds is not CompositorEffect effect) return;
        _worldEnvironment.Compositor ??= new Compositor();
        Godot.Collections.Array<CompositorEffect> effects = _worldEnvironment.Compositor.CompositorEffects;
        if (!effects.Contains(effect))
        {
            effects.Add(effect);
            _worldEnvironment.Compositor.CompositorEffects = effects;
        }
    }

    /// <summary>
    /// Gives a mutually-exclusive cloud provider ownership of the compositor.
    /// The Sunshine effect is physically removed while inactive so it cannot
    /// consume compositor work behind another provider.
    /// </summary>
    public void SetProviderActive(bool active)
    {
        Enabled = active;
        if (active)
        {
            _driver?.Set("update_continuously", true);
            AttachCompositorToTargetEnvironment();
            // Provider switches must restore gameplay-selected scalars without
            // touching weather-owned values or rebuilding an unchanged target.
            ApplyTemporalQualityTuning();
            SetProcess(true);
            UpdateVisibility(force: true);
            return;
        }

        CancelCloudShadowBuild();
        RingCloudShadowProjector.ReleaseTextures();
        _shadowProjectorEnabled = false;
        _clouds?.Set("enabled", false);
        _driver?.Set("update_continuously", false);
        _weather?.SetExternalLocalVolumetricCloudsState(
            ownsLocalClouds: false,
            localVolumeVisible: false,
            HandoffStartMeters,
            HandoffEndMeters);
        DetachCompositorFromTargetEnvironment();
        RuntimeVisible = false;
        _lastVisible = false;
        SetProcess(false);
    }

    private void DetachCompositorFromTargetEnvironment()
    {
        if (_worldEnvironment?.Compositor == null || _clouds is not CompositorEffect effect) return;
        Godot.Collections.Array<CompositorEffect> effects = _worldEnvironment.Compositor.CompositorEffects;
        if (effects.Remove(effect))
            _worldEnvironment.Compositor.CompositorEffects = effects;
    }

    public override void _ExitTree()
    {
        if (_clouds != null && _externalTextureRetirementCallable is { } callback
            && _clouds.IsConnected("external_texture_retirement_requested", callback))
            _clouds.Disconnect("external_texture_retirement_requested", callback);
        CancelCloudShadowBuild();
        RingCloudShadowProjector.ReleaseTextures();
        _weather?.SetExternalLocalVolumetricCloudsState(false, false, HandoffStartMeters, HandoffEndMeters);
        _weather?.UnregisterManualWeather(SunshinePreviewWeatherOwnerKey);
        if (_clouds != null) _clouds.Set("enabled", false);
    }

    private void OnExternalTextureRetirementRequested(long generationId)
    {
        ReleaseExternalSunshineTextureReceivers();
        Resource? clouds = _clouds;
        if (clouds == null || !GodotObject.IsInstanceValid(clouds)) return;
        // The deferred signal runs on the main thread after the compositor has
        // retired its resources. Order the acknowledgement on the render thread
        // only after fallback material bindings have been submitted to Godot.
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(clouds)
                && clouds.HasMethod("acknowledge_external_texture_retirement"))
                clouds.Call("acknowledge_external_texture_retirement", generationId);
        }));
    }

    private void ReleaseExternalSunshineTextureReceivers()
    {
        if (RingCloudShadowProjector.HasExternalTextures)
            RingCloudShadowProjector.ReleaseTextures();
        _shadowProjectorEnabled = false;
        _gpuShadowReceiverRebuildPending = true;
        _scaledFarViewport?.ReleaseSunshineBackdropTexture();
    }

    public override void _Process(double delta)
    {
        if (_clouds == null || _driver == null || _explorer == null || _grid == null || _weather == null) return;
        SyncGpuTimestampDiagnostics();
        UpdateTemporalHistory(delta);
        UpdateSunTemporalHistory(delta);
        _weatherElapsed += Math.Max(0.0, delta);
        if (_weatherElapsed >= Math.Max(0.05, WeatherRefreshSeconds)) RefreshWeather();
        UpdateCloudFrame();
        UpdateVisibility(force: false);
        UpdateCloudShadows(delta, force: false);
        UpdateCameraLightingState(delta);
    }

    private void SyncGpuTimestampDiagnostics()
    {
        if (_clouds == null) return;
        bool enabled = RingPerformanceGpu.Enabled;
        string beginMarker = RingPerformanceGpu.CloudBeginMarker, endMarker = RingPerformanceGpu.CloudEndMarker;
        if (_appliedGpuTimestampDiagnosticsEnabled == enabled && _appliedGpuTimestampBeginMarker == beginMarker && _appliedGpuTimestampEndMarker == endMarker) return;
        GpuTimestampDiagnosticsEnabled = enabled;
        _clouds.Set("gpu_timestamp_begin_marker", beginMarker);
        _clouds.Set("gpu_timestamp_end_marker", endMarker);
        _clouds.Set("gpu_timestamp_diagnostics_enabled", enabled);
        _appliedGpuTimestampDiagnosticsEnabled = enabled;
        _appliedGpuTimestampBeginMarker = beginMarker; _appliedGpuTimestampEndMarker = endMarker;
    }

    public float GetCameraDirectSunTransmittance() => CameraDirectSunTransmittance;
    public float GetCameraAmbientTransmittance() => CameraAmbientTransmittance;

    /// <summary>
    /// Returns Sunshine's published screen-space cloud color/density texture.
    /// The addon returns null while a render-device generation is rebuilding.
    /// </summary>
    public Texture2D? GetCloudBackdropTexture()
    {
        if (_clouds == null || !_clouds.HasMethod("get_cloud_backdrop_texture")) return null;
        return _clouds.Call("get_cloud_backdrop_texture", 0).AsGodotObject() as Texture2D;
    }

    /// <summary>Publishes the scaled far viewport's GPU-only physical-depth texture.
    /// The Sunshine resource owns the RD binding and falls back to main depth until
    /// the far compositor has produced its first frame.</summary>
    public void SetFarOpaqueDepthTexture(Texture2D? texture)
    {
        if (_clouds == null) return;
        Texture2D? previous = _clouds.Get("far_opaque_depth_texture").AsGodotObject() as Texture2D;
        if (ReferenceEquals(previous, texture)) return;
        _clouds.Set("far_opaque_depth_texture", texture is null ? default(Variant) : (Variant)texture);
        // Descriptor sets capture Texture2DRD RIDs.  Rebuild only when the
        // far producer publishes a replacement generation, never per frame.
        _clouds.Call("refresh_compute");
    }

    public void SetLocalDayFactor(float dayFactor)
    {
        float clampedDayFactor = Mathf.Clamp(dayFactor, 0.0f, 1.0f);
        if (Mathf.IsEqualApprox(_localDayFactor, clampedDayFactor)) return;

        _localDayFactor = clampedDayFactor;
        ApplyLocalCloudAmbientLighting();
        ApplyCloudAppearanceScalarsToResource();
    }

    /// <summary>
    /// Applies player-facing appearance overrides while retaining live weather's
    /// coverage, storm, eclipse, and noise-origin ownership.
    /// </summary>
    public void ApplyCloudAppearanceTuning()
    {
        SanitizeCloudAppearanceTuning();
        RefreshWeather();
        ApplyCloudAppearanceScalarsToResource();
        // A non-selected provider owns the external-cloud handoff. Appearance
        // values may be prepared privately while inactive, but must not publish
        // Sunshine's visibility/ownership state over that provider.
        if (Enabled) UpdateVisibility(force: false);
    }

    /// <summary>
    /// Lets a very short point-light flash retire stale temporal cloud history
    /// without rebuilding the compositor. The normal history weight returns as
    /// soon as the flash fades.
    /// </summary>
    public void SetLightningFlashIntensity(float intensity)
    {
        _lightningHistorySuppression = SmoothUnit(Mathf.Clamp(intensity, 0.0f, 1.0f));
        ApplyEffectiveHistoryWeight();
    }

    public int GetCloudShadowReceiverCount() => RingCloudShadowProjector.ActiveReceiverCount;
    public int GetInstalledShadowResolution() => InstalledShadowResolution;
    public float GetInstalledShadowRadiusMeters() => InstalledShadowRadiusMeters;
    public float GetInstalledShadowPeakOpacity() => InstalledShadowPeakOpacity;
    public Vector2 GetInstalledGpuShadowCenter(int cascade) => cascade is >= 0 and < 2 && _hasGpuShadowCenter[cascade]
        ? new Vector2((float)_gpuShadowCenters[cascade].AroundMeters, (float)_gpuShadowCenters[cascade].AcrossMeters)
        : new Vector2(float.NaN, float.NaN);
    public bool IsGpuShadowCascadePublishPending(int cascade) => cascade is >= 0 and < 2 && _gpuShadowPublishPending[cascade];
    public Vector3 GetExplorerCanonicalPosition() => _explorer == null
        ? new Vector3(float.NaN, float.NaN, float.NaN)
        : new Vector3((float)_explorer.CanonicalPosition.AroundMeters, (float)_explorer.CanonicalPosition.VerticalMeters, (float)_explorer.CanonicalPosition.AcrossMeters);
    public bool IsUsingGpuCloudShadows() => UsingGpuCloudShadows;
    public bool IsCloudShadowProjectorEnabled() => _shadowProjectorEnabled;
    public string GetGpuCloudShadowStatus() => GpuCloudShadowStatus;
    public string GetCloudLightingDiagnostics()
    {
        string state = _shadowProjectorEnabled ? "projecting" : _shadowBuildPending ? "building" : "idle";
        return $"Cloud light: {state} | sun {CameraDirectSunTransmittance:0.00} | ambient {CameraAmbientTransmittance:0.00} | peak {InstalledShadowPeakOpacity:0.00} | "
            + $"{InstalledShadowResolution}px / {InstalledShadowRadiusMeters / 1000.0f:0} km | receivers {RingCloudShadowProjector.ActiveReceiverCount}";
    }

    public void ApplyLightingTuning(
        float shadowStrength,
        float extinctionPerKilometer,
        float ambientFloor,
        float ambientOcclusionStrength)
    {
        ShadowStrength = Mathf.Clamp(shadowStrength, 0.0f, 1.0f);
        ShadowExtinctionPerKilometer = Mathf.Clamp(extinctionPerKilometer, 0.05f, 2.0f);
        StormAmbientFloor = Mathf.Clamp(ambientFloor, 0.02f, 0.5f);
        StormAmbientOcclusionStrength = Mathf.Clamp(ambientOcclusionStrength, 0.0f, 1.0f);
        CancelCloudShadowBuild();
        _shadowElapsed = double.PositiveInfinity;
    }

    private void UpdateCameraLightingState(double delta)
    {
        if (!_shadowProjectorEnabled && !_shadowBuildPending && !_shadowMapReady)
        {
            _targetCameraDirectSunTransmittance = 1.0f;
            _targetCameraAmbientTransmittance = 1.0f;
        }

        float blend = 1.0f - MathF.Exp(
            -Mathf.Max(0.1f, LightingResponseSpeed) * (float)Math.Max(0.0, delta));
        CameraDirectSunTransmittance = Mathf.Lerp(
            CameraDirectSunTransmittance,
            _targetCameraDirectSunTransmittance,
            blend);
        CameraAmbientTransmittance = Mathf.Lerp(
            CameraAmbientTransmittance,
            Mathf.Min(
                _targetCameraAmbientTransmittance,
                Mathf.Lerp(1.0f, HeavyOvercastAmbientTransmittance, _heavyOvercastFactor)),
            blend);
    }

    private void RefreshWeather()
    {
        if (_weather == null || _explorer == null || _grid?.TerrainSampler == null || _clouds == null || _driver == null) return;
        _weatherElapsed = 0.0;
        RingVolumetricCloudLayer.LocalWeatherState naturalWeather =
            _weather.QueryLocalWeather(_explorer.CanonicalPosition);
        _currentWeather = ResolveCurrentWeather(naturalWeather);

        CloudAltitudeRange backgroundAltitude = ResolveNaturalCloudAltitude();
        _backgroundCloudFloorMeters = backgroundAltitude.FloorMeters;
        _backgroundCloudCeilingMeters = backgroundAltitude.CeilingMeters;
        CloudAltitudeRange stormAltitude = _spawnedWeather is { } spawned
            ? ResolveCloudAltitude(
                spawned.AltitudeDatumMeters,
                spawned.CloudBaseAboveDatumMeters,
                spawned.CloudTopAboveDatumMeters)
            : ResolveNaturalStormCloudAltitude(_currentWeather, backgroundAltitude);
        float cloudBase = Math.Min(_backgroundCloudFloorMeters, stormAltitude.FloorMeters);
        float cloudTop = Math.Max(_backgroundCloudCeilingMeters, stormAltitude.CeilingMeters);
        CloudTraversalAltitudeMeters = new Vector2(cloudBase, cloudTop);
        float storm = Mathf.Clamp(_currentWeather.SystemInfluence * 0.65f + _currentWeather.PrecipitationPotential * 0.55f, 0.0f, 1.0f);
        _heavyOvercastFactor = SmoothUnit(
            (_currentWeather.CloudCoverage - HeavyOvercastStartCoverage)
            / Mathf.Max(0.01f, 1.0f - HeavyOvercastStartCoverage));
        _localStormFactor = Mathf.Max(storm, _heavyOvercastFactor);

        _clouds.Set("cloud_floor", cloudBase);
        _clouds.Set("cloud_ceiling", cloudTop);
        _clouds.Set("background_altitude", new Vector4(
            _backgroundCloudFloorMeters,
            _backgroundCloudCeilingMeters,
            0.0f,
            0.0f));
        _clouds.Set("storm_altitude", new Vector4(
            stormAltitude.FloorMeters,
            stormAltitude.CeilingMeters,
            0.0f,
            0.0f));
        // Sunshine's coverage value is a threshold: larger values expose more
        // of the procedural volume. Mapping it downward made stormier game
        // weather clearer and left the low-coverage spawn cell nearly empty.
        SanitizeCloudAppearanceTuning();
        float visibleCoverage = Mathf.Clamp(
            Mathf.Max(_currentWeather.CloudCoverage, storm) + CloudCoverageBias,
            0.0f,
            1.0f);
        _clouds.Set("clouds_coverage", Mathf.Lerp(0.70f, 0.90f, visibleCoverage));
        _clouds.Set("clouds_density", Mathf.Lerp(1.20f, 1.90f, visibleCoverage) * CloudDensityMultiplier);
        // Sunshine uses this value as the density sampled along the light ray:
        // higher values absorb more directional light. Storms therefore need
        // to raise it, rather than lower it, or their cloud tops brighten.
        _clouds.Set(
            "lighting_density",
            Mathf.Lerp(0.72f, 1.55f, Mathf.Max(storm, _heavyOvercastFactor)));
        ApplyLocalCloudAmbientLighting();
        ApplyCloudAppearanceScalarsToResource();

        bool stormProfile = _currentWeather.DominantFormationProfile
            is CloudFormationProfile.StormTower or CloudFormationProfile.AnvilStorm;
        bool hasLocalizedSystem = _currentWeather.SystemKind
                != RingVolumetricCloudLayer.WeatherSystemKind.None
            && _currentWeather.SystemRadiiMeters.X > 0.0f
            && _currentWeather.SystemRadiiMeters.Y > 0.0f;
        bool spawnedLocalizedCloud = _spawnedWeather.HasValue;
        float sculptInfluence = SculptLocalizedStorms
            && (stormProfile || spawnedLocalizedCloud)
            && hasLocalizedSystem
            ? Mathf.Clamp(StormSculptStrength, 0.0f, 1.0f)
            : 0.0f;
        _clouds.Set("storm_system", new Vector4(
            _currentWeather.SystemCenterMeters.X,
            _currentWeather.SystemCenterMeters.Y,
            _currentWeather.SystemRadiiMeters.X,
            _currentWeather.SystemRadiiMeters.Y));
        _clouds.Set("storm_shape", new Vector4(
            Mathf.Cos(_currentWeather.SystemOrientationRadians),
            Mathf.Sin(_currentWeather.SystemOrientationRadians),
            sculptInfluence,
            CloudFormationProfiles.ToShaderProfile(_currentWeather.DominantFormationProfile)));
        _clouds.Set("storm_detail", new Vector4(
            Mathf.Clamp(StormPillarRadiusMeters, 1000.0f, 8000.0f),
            Mathf.Clamp(AnvilRadiusMultiplier, 1.2f, 4.0f),
            Mathf.Clamp(AnvilStartFraction, 0.5f, 0.9f),
            (float)_currentWeather.DominantFormationProfile));

        Vector2 wind = _currentWeather.WindMetersPerSecond;
        Vector3 windDirection = new(wind.X, 0.0f, wind.Y);
        float unscaledSpeed = windDirection.Length();
        float speed = unscaledSpeed * CloudWindMultiplier;
        _driver.Set("wind_direction", unscaledSpeed > 0.001f ? windDirection / unscaledSpeed : Vector3.Zero);
        _driver.Set("extra_large_structures_wind_speed", speed * 0.22f);
        _driver.Set("large_structures_wind_speed", speed * 0.55f);
        _driver.Set("medium_structures_wind_speed", speed);
        _driver.Set("small_structures_wind_speed", speed * 1.8f);
    }

    private void ApplyLocalCloudAmbientLighting()
    {
        if (_clouds == null) return;

        Color dayTint = new(
            Mathf.Lerp(0.30f, 0.13f, _localStormFactor),
            Mathf.Lerp(0.38f, 0.18f, _localStormFactor),
            Mathf.Lerp(0.50f, 0.27f, _localStormFactor),
            1.0f);
        Color normalAmbient = NightCloudAmbientColor.Lerp(DayCloudAmbientColor, _localDayFactor);
        Color stormAmbient = NightCloudAmbientColor.Lerp(StormCloudAmbientColor, _localDayFactor);
        Color ambient = normalAmbient.Lerp(stormAmbient, _localStormFactor);
        _clouds.Set("cloud_ambient_color", new Color(
            ambient.R * CloudAmbientMultiplier,
            ambient.G * CloudAmbientMultiplier,
            ambient.B * CloudAmbientMultiplier,
            ambient.A));
        _clouds.Set("cloud_ambient_tint", NightCloudAmbientTint.Lerp(dayTint, _localDayFactor));
    }

    private void UpdateCloudFrame()
    {
        if (_grid == null || _explorer == null || _clouds == null) return;
        RingCanonicalPosition viewer = _explorer.CanonicalPosition;
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        Vector3 origin = geometry.CanonicalToWorld(viewer.AroundMeters, 0.0, viewer.AcrossMeters);
        Vector3 around = (geometry.CanonicalToWorld(viewer.AroundMeters + 10.0, 0.0, viewer.AcrossMeters) - origin).Normalized();
        Vector3 up = (geometry.CanonicalToWorld(viewer.AroundMeters, 10.0, viewer.AcrossMeters) - origin).Normalized();
        Vector3 across = (geometry.CanonicalToWorld(viewer.AroundMeters, 0.0, viewer.AcrossMeters + 10.0) - origin).Normalized();
        double preciseRadius = _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau;
        float radius = (float)preciseRadius;

        Vector4 frameAround = new(around.X, around.Y, around.Z, (float)viewer.AroundMeters);
        Vector4 frameUp = new(up.X, up.Y, up.Z, radius);
        Vector4 frameAcross = new(across.X, across.Y, across.Z, (float)viewer.AcrossMeters);
        float ringWidth = (float)_grid.RingAcrossWidthMeters;
        // Canonical .w values can be hundreds of metres apart while still
        // comparing approximately equal. These are callback-frame inputs.
        if (_lastCloudFrameOrigin != origin)
        {
            _clouds.Set("cloud_frame_origin", origin);
            _lastCloudFrameOrigin = origin;
        }
        if (_lastCloudFrameAround != frameAround)
        {
            _clouds.Set("cloud_frame_around", frameAround);
            _lastCloudFrameAround = frameAround;
        }
        if (_lastCloudFrameUp != frameUp)
        {
            _clouds.Set("cloud_frame_up", frameUp);
            _lastCloudFrameUp = frameUp;
        }
        if (_lastCloudFrameAcross != frameAcross)
        {
            _clouds.Set("cloud_frame_across", frameAcross);
            _lastCloudFrameAcross = frameAcross;
        }
        if (!Mathf.IsEqualApprox(_lastCloudFrameRingWidth, ringWidth))
        {
            _clouds.Set("ring_width_meters", ringWidth);
            _lastCloudFrameRingWidth = ringWidth;
        }
        // This is deliberately a CPU-only, callback-paired contract. The
        // compositor snapshots it once when it uploads its existing UBO.
        _clouds.Call("set_gameplay_cloud_frame", new Transform3D(new Basis(around, up, across), origin), viewer.AroundMeters, viewer.AcrossMeters, preciseRadius);
    }

    public void ApplyLiveTuning(
        int resolutionScale,
        float stationaryHistoryWeight,
        float movingHistoryWeight,
        int raymarchSteps,
        float ditherSpeed,
        float rayStartJitterStrength,
        float blurPower,
        float pillarRadiusMeters,
        float anvilRadiusMultiplier,
        float stormStrength)
    {
        int previousResolutionScale = CloudResolutionScale;
        CloudResolutionScale = Mathf.Clamp(resolutionScale, 0, 2);
        StationaryHistoryWeight = Mathf.Clamp(stationaryHistoryWeight, 0.0f, 0.95f);
        MovingHistoryWeight = Mathf.Clamp(movingHistoryWeight, 0.0f, StationaryHistoryWeight);
        RaymarchSteps = Mathf.Clamp(raymarchSteps, 64, 256);
        DitherSpeed = Mathf.Clamp(ditherSpeed, 0.0f, 120.0f);
        RayStartJitterStrength = Mathf.Clamp(rayStartJitterStrength, 0.0f, 1.0f);
        BlurPower = Mathf.Clamp(blurPower, 0.0f, 6.0f);
        StormPillarRadiusMeters = Mathf.Clamp(pillarRadiusMeters, 1000.0f, 8000.0f);
        AnvilRadiusMultiplier = Mathf.Clamp(anvilRadiusMultiplier, 1.2f, 4.0f);
        StormSculptStrength = Mathf.Clamp(stormStrength, 0.0f, 1.0f);
        ApplyTuningToResource(refreshHistory: previousResolutionScale != CloudResolutionScale);
        RefreshWeather();
    }

    /// <summary>
    /// Updates the two temporal-history controls and immediately pushes the
    /// resolved, motion-aware value to Sunshine's live compositor resource.
    /// This is intentionally separate from the exported properties because
    /// Godot inspector/property writes do not notify an already-running
    /// CompositorEffect.
    /// </summary>
    public void SetTemporalHistoryWeights(float stationaryHistoryWeight, float movingHistoryWeight)
    {
        StationaryHistoryWeight = Mathf.Clamp(stationaryHistoryWeight, 0.0f, 0.95f);
        MovingHistoryWeight = Mathf.Clamp(movingHistoryWeight, 0.0f, StationaryHistoryWeight);
        ApplyTuningToResource(refreshHistory: false, forceHistoryApply: true);
    }

    /// <summary>
    /// Pushes the inspector/live-drawer temporal quality values into Sunshine.
    /// The resource is separate from this bridge, so changing an exported
    /// bridge property alone does not update an already-running compositor.
    /// </summary>
    public void ApplyTemporalQualityTuning(bool refreshHistory = false)
        => ApplyTuningToResource(refreshHistory, forceHistoryApply: true);

    public void SpawnStormFrontAhead()
    {
        CloudShowcaseLabel = "No catalog cloud spawned";
        _weather?.ClearPhysicalCloudShowcase();
        Vector2 direction = ResolveSpawnDirection();
        Vector2 majorAxis = new(-direction.Y, direction.X);
        Vector2 center = ResolveSpawnCenter(direction);
        SpawnWeatherPreview(new SpawnedWeatherPreview(
            "Spawned storm front",
            RingVolumetricCloudLayer.WeatherSystemKind.MajorFront,
            CloudFormationProfile.StormTower,
            center,
            new Vector2(
                Mathf.Max(1000.0f, SpawnFrontMajorRadiusMeters),
                Mathf.Max(1000.0f, SpawnFrontMinorRadiusMeters)),
            Mathf.Atan2(majorAxis.Y, majorAxis.X),
            0.94f,
            0.86f,
            StormFrontCloudBaseAltitudeMeters,
            9000.0f,
            FixedCloudAltitudeDatumMeters));
    }

    public void SpawnAnvilClusterAhead()
    {
        CloudShowcaseLabel = "No catalog cloud spawned";
        _weather?.ClearPhysicalCloudShowcase();
        Vector2 direction = ResolveSpawnDirection();
        float radius = Mathf.Max(1000.0f, SpawnAnvilRadiusMeters);
        Vector2 center = ResolveSpawnCenter(direction);
        SpawnWeatherPreview(new SpawnedWeatherPreview(
            "Spawned anvil cluster",
            RingVolumetricCloudLayer.WeatherSystemKind.ExceptionalFront,
            CloudFormationProfile.AnvilStorm,
            center,
            new Vector2(radius * 1.25f, radius),
            Mathf.Atan2(direction.Y, direction.X),
            0.97f,
            0.94f,
            500.0f,
            18000.0f,
            FixedCloudAltitudeDatumMeters));
    }

    public void SpawnCatalogCloudAhead(int catalogIndex)
    {
        if (_weather == null || CloudPhysicalCatalog.All.Length == 0) return;
        int index = Math.Clamp(catalogIndex, 0, CloudPhysicalCatalog.All.Length - 1);
        CloudPhysicalDefinition physical = CloudPhysicalCatalog.All[index];
        Vector2 direction = ResolveSpawnDirection();
        float representativeWidth = MathF.Sqrt(
            Math.Max(1.0f, physical.MinimumWidthMeters)
            * Math.Max(1.0f, physical.MaximumWidthMeters));
        float previewDistance = Mathf.Clamp(
            representativeWidth * 1.5f,
            3000.0f,
            Mathf.Max(3000.0f, SpawnDistanceMeters));
        Vector2 center = ResolveSpawnCenter(direction, previewDistance);
        float broadsideOrientation = Mathf.Atan2(direction.Y, direction.X) + Mathf.Pi * 0.5f;
        float depthFraction = physical.Profile switch
        {
            CloudFormationProfile.Cirrus => 0.14f,
            CloudFormationProfile.Cirrostratus
                or CloudFormationProfile.Altostratus
                or CloudFormationProfile.Stratus
                or CloudFormationProfile.Nimbostratus => 0.76f,
            _ => 0.68f
        };
        float representativeThickness = MathF.Sqrt(
            Math.Max(1.0f, physical.MinimumThicknessMeters)
            * Math.Max(1.0f, physical.MaximumThicknessMeters));
        float availableAltitude = Math.Max(
            100.0f,
            physical.MaximumAltitudeMeters - physical.MinimumAltitudeMeters);
        representativeThickness = Mathf.Clamp(
            representativeThickness,
            100.0f,
            availableAltitude);
        float maximumBase = Math.Max(
            physical.MinimumAltitudeMeters,
            physical.MaximumAltitudeMeters - representativeThickness);
        float baseAltitude = physical.Profile == CloudFormationProfile.Cumulonimbus
            ? physical.MinimumAltitudeMeters
            : (physical.MinimumAltitudeMeters + maximumBase) * 0.5f;
        float topAltitude = Math.Min(
            physical.MaximumAltitudeMeters,
            baseAltitude + representativeThickness);
        float precipitation = physical.Profile switch
        {
            CloudFormationProfile.Nimbostratus => 0.78f,
            CloudFormationProfile.Cumulonimbus => 0.88f,
            _ => 0.0f
        };
        float coverage = physical.Density switch
        {
            CloudDensityClass.Thin => 0.42f,
            CloudDensityClass.Medium => 0.68f,
            CloudDensityClass.MediumToDense => 0.78f,
            CloudDensityClass.VeryDense => 0.92f,
            CloudDensityClass.ExtremelyDense => 0.97f,
            _ => 0.68f
        };
        string label = $"{physical.Name} ({physical.Abbreviation}) — "
            + $"Sunshine volumetric, {representativeWidth / 1000.0f:0.0} km wide, "
            + $"{representativeThickness / 1000.0f:0.0} km thick, "
            + $"altitude {baseAltitude / 1000.0f:0.0}-{topAltitude / 1000.0f:0.0} km";

        if (_clouds != null && _driver != null)
        {
            _weather.ClearPhysicalCloudShowcase();
            CloudShowcaseLabel = label;
            SpawnWeatherPreview(new SpawnedWeatherPreview(
                $"Sunshine {physical.Name} showcase",
                ResolveCatalogWeatherKind(physical.Profile),
                physical.Profile,
                center,
                new Vector2(
                    Math.Max(250.0f, representativeWidth * 0.5f),
                    Math.Max(250.0f, representativeWidth * depthFraction * 0.5f)),
                broadsideOrientation,
                coverage,
                precipitation,
                baseAltitude,
                topAltitude,
                FixedCloudAltitudeDatumMeters));
            return;
        }

        CloudShowcaseLabel = $"Fallback mesh: {label}";
        _weather.SpawnPhysicalCloudShowcase(
            physical.Profile,
            center,
            broadsideOrientation);
    }

    public void ClearCatalogCloudShowcase()
    {
        if (_spawnedWeather is { Label: var label }
            && label.StartsWith("Sunshine ", StringComparison.Ordinal))
        {
            CancelCloudShadowBuild();
            _spawnedWeather = null;
            _weather?.UnregisterManualWeather(SunshinePreviewWeatherOwnerKey);
            RefreshWeather();
            RefreshCloudCompute();
        }
        _weather?.ClearPhysicalCloudShowcase();
        CloudShowcaseLabel = "No catalog cloud spawned";
    }

    public void ClearSpawnedWeather()
    {
        CancelCloudShadowBuild();
        _spawnedWeather = null;
        _weather?.UnregisterManualWeather(SunshinePreviewWeatherOwnerKey);
        _weather?.ClearPhysicalCloudShowcase();
        CloudShowcaseLabel = "No catalog cloud spawned";
        RefreshWeather();
        RefreshCloudCompute();
        GD.Print("Sunshine weather preview cleared; natural procedural weather restored.");
    }

    public void SetBenchmarkViewerPosition(
        double aroundMeters,
        double verticalMeters,
        double acrossMeters)
    {
        if (_explorer == null || _grid == null) return;
        _explorer.SetBenchmarkCanonicalPosition(new RingCanonicalPosition(
            aroundMeters,
            verticalMeters,
            acrossMeters),
            simulatedSpeedMetersPerSecond: 1000.0f,
            simulatedNoclip: true);
        CancelCloudShadowBuild();
        RefreshWeather();
        UpdateCloudFrame();
    }

    private static float SanitizeFinite(float value, float fallback, float minimum, float maximum)
        => float.IsFinite(value) ? Mathf.Clamp(value, minimum, maximum) : fallback;

    private void SanitizeCloudAppearanceTuning()
    {
        CloudCoverageBias = SanitizeFinite(CloudCoverageBias, 0.0f, -1.0f, 1.0f);
        CloudDensityMultiplier = SanitizeFinite(CloudDensityMultiplier, 1.0f, 0.0f, 3.0f);
        CloudWindMultiplier = SanitizeFinite(CloudWindMultiplier, 1.0f, 0.0f, 3.0f);
        CloudDirectLightMultiplier = SanitizeFinite(CloudDirectLightMultiplier, 1.0f, 0.0f, 2.0f);
        CloudAmbientMultiplier = SanitizeFinite(CloudAmbientMultiplier, 1.0f, 0.0f, 2.0f);
        CloudSharpness = SanitizeFinite(CloudSharpness, 0.62f, 0.0f, 1.0f);
    }

    private void ApplyCloudAppearanceScalarsToResource()
    {
        SanitizeCloudAppearanceTuning();
        _clouds?.Set("clouds_sharpness", CloudSharpness);
        // The driver applies this only to tracked directional lights; point
        // lights (including lightning) retain their independently authored power.
        _driver?.Set("directional_light_power_multiplier",
            (_hasAuthoredDirectionalLightPowerMultiplier
                ? _authoredDirectionalLightPowerMultiplier
                : 1.0f) * CloudDirectLightMultiplier);
    }

    private void ApplyLightingStepsToDriver()
    {
        if (_driver == null || _appliedLightingSteps == LightingSteps) return;

        Godot.Collections.Array trackedLights = _driver
            .Get("tracked_directional_lights")
            .AsGodotArray();
        var shadowSteps = new Godot.Collections.Array<int>();
        for (int i = 0; i < trackedLights.Count; i++) shadowSteps.Add(LightingSteps);
        _driver.Set("tracked_directional_light_shadow_steps", shadowSteps);
        _appliedLightingSteps = LightingSteps;
    }

    private void ApplyTuningToResource(bool refreshHistory, bool forceHistoryApply = true)
    {
        CloudResolutionScale = Mathf.Clamp(CloudResolutionScale, 0, 2);
        RaymarchSteps = Mathf.Clamp(RaymarchSteps, 64, 256);
        DitherSpeed = SanitizeFinite(DitherSpeed, 0.0f, 0.0f, 120.0f);
        RayStartJitterStrength = SanitizeFinite(RayStartJitterStrength, 1.0f, 0.0f, 1.0f);
        BlurPower = SanitizeFinite(BlurPower, 0.1f, 0.0f, 6.0f);
        MinStepDistanceMeters = SanitizeFinite(MinStepDistanceMeters, 90.0f, 10.0f, 500.0f);
        MaxStepDistanceMeters = SanitizeFinite(MaxStepDistanceMeters, 560.0f, 100.0f, 4000.0f);
        MaxStepDistanceMeters = Mathf.Max(MinStepDistanceMeters + 10.0f, MaxStepDistanceMeters);
        LightingTravelKm = SanitizeFinite(LightingTravelKm, 7.0f, 0.5f, 20.0f);
        CloudLodBias = SanitizeFinite(CloudLodBias, 0.9f, 0.1f, 2.0f);
        LightingSteps = Mathf.Clamp(LightingSteps, 1, 8);
        BlurQuality = Mathf.Clamp(BlurQuality, 1, 4);
        if (_clouds == null) return;
        bool resolutionChanged = _appliedCloudResolutionScale != CloudResolutionScale;
        if (resolutionChanged)
        {
            // Setting Sunshine's resolution_scale immediately recreates its RD
            // generation, including GPU-shadow textures held by receivers.
            if (Enabled)
                BeginGpuShadowReceiverRebuildForCompositorRefresh();
            _clouds.Set("resolution_scale", CloudResolutionScale);
            _appliedCloudResolutionScale = CloudResolutionScale;
        }
        _clouds.Set("max_step_count", (float)RaymarchSteps);
        _clouds.Set("dither_speed", DitherSpeed);
        _clouds.Set("ray_start_jitter_strength", RayStartJitterStrength);
        _clouds.Set("blur_power", BlurPower);
        _clouds.Set("min_step_distance", MinStepDistanceMeters);
        _clouds.Set("max_step_distance", MaxStepDistanceMeters);
        _clouds.Set("lighting_travel_distance", LightingTravelKm * 1000.0f);
        _clouds.Set("lod_bias", CloudLodBias);
        _clouds.Set("max_lighting_steps", (float)LightingSteps);
        ApplyLightingStepsToDriver();
        _clouds.Set("blur_quality", (float)BlurQuality);
        _clouds.Set("menu_fast_reconstruction", FastReconstruction);
        SyncGpuTimestampDiagnostics();
        // A quality/preset change must not temporarily replace the moving
        // camera's selected history weight with the stationary slider value.
        // That made a preset change look as if the history controls had no
        // effect until the next Process tick.
        EffectiveHistoryWeight = ResolveTemporalHistoryWeight(
            StationaryHistoryWeight,
            MovingHistoryWeight,
            CameraHistoryMotion);
        ApplyEffectiveHistoryWeight(forceHistoryApply);
        TemporalHistoryApplyCount++;
        // resolution_scale already performs this compositor refresh itself.
        if (refreshHistory && !resolutionChanged)
            RefreshCloudCompute();
    }

    private void RefreshCloudCompute()
    {
        if (_clouds == null) return;
        // refresh_compute rebuilds the compositor's whole RD generation,
        // including published cloud-shadow Texture2DRD objects. Detach scene
        // materials before requesting that rebuild and do not bind an old
        // wrapper again until both replacement dispatches finish.
        if (Enabled)
            BeginGpuShadowReceiverRebuildForCompositorRefresh();
        _clouds.Call("refresh_compute");
    }

    private void BeginGpuShadowReceiverRebuildForCompositorRefresh()
    {
        if (_clouds == null) return;

        ReleaseExternalSunshineTextureReceivers();
        if (_clouds.HasMethod("invalidate_gpu_cloud_shadow_publication"))
            _clouds.Call("invalidate_gpu_cloud_shadow_publication");
        if (_clouds.HasMethod("invalidate_cloud_backdrop_texture_publication"))
            _clouds.Call("invalidate_cloud_backdrop_texture_publication");

        // A whole-compositor refresh can land between the two cascade updates.
        // Do not guess which implicit rebuild dispatch belongs to the staged
        // receiver metadata. Force both cascades through the normal explicit
        // configure/publish handshake after the new compositor generation is
        // available instead.
        for (int cascade = 0; cascade < 2; cascade++)
        {
            _gpuShadowPublishPending[cascade] = false;
            _hasGpuShadowCenter[cascade] = false;
            _gpuShadowElapsed[cascade] = double.PositiveInfinity;
        }
    }

    private void UpdateTemporalHistory(double delta)
    {
        if (_clouds == null || _explorer == null) return;
        _camera ??= GetViewport().GetCamera3D();
        if (_camera == null) return;

        float dt = (float)Math.Max(delta, 0.0001);
        Vector3 position = _camera.GlobalPosition;
        Vector3 forward = (-_camera.GlobalBasis.Z).Normalized();
        float motion = 1.0f;
        if (_hasPreviousCameraPose)
        {
            float measuredSpeed = position.DistanceTo(_previousCameraPosition) / dt;
            float translationSpeed = Mathf.Max(measuredSpeed, _explorer.ActualTravelSpeedMetersPerSecond);
            float forwardDot = Mathf.Clamp(forward.Dot(_previousCameraForward), -1.0f, 1.0f);
            float angularSpeed = Mathf.RadToDeg(Mathf.Acos(forwardDot)) / dt;
            float translationMotion = translationSpeed / Mathf.Max(1.0f, MinimumHistorySpeedMetersPerSecond);
            float rotationMotion = angularSpeed / Mathf.Max(1.0f, MinimumHistoryRotationDegreesPerSecond);
            motion = SmoothUnit(Mathf.Max(translationMotion, rotationMotion));
        }

        _previousCameraPosition = position;
        _previousCameraForward = forward;
        _hasPreviousCameraPose = true;
        CameraHistoryMotion = motion;
        EffectiveHistoryWeight = ResolveTemporalHistoryWeight(
            StationaryHistoryWeight,
            MovingHistoryWeight,
            CameraHistoryMotion);
        ApplyEffectiveHistoryWeight();
    }

    /// <summary>
    /// Pure temporal-history blend used by runtime code and deterministic
    /// regression checks. A higher motion value moves from stationary toward
    /// moving history; invalid inputs are clamped to safe compositor ranges.
    /// </summary>
    public static float ResolveTemporalHistoryWeight(
        float stationaryHistoryWeight,
        float movingHistoryWeight,
        float motion)
    {
        float stationary = Mathf.Clamp(stationaryHistoryWeight, 0.0f, 0.95f);
        float moving = Mathf.Clamp(movingHistoryWeight, 0.0f, stationary);
        return Mathf.Lerp(stationary, moving, Mathf.Clamp(motion, 0.0f, 1.0f));
    }

    /// <summary>
    /// Applies short-lived lightning and solar rejection to an already
    /// motion-resolved history weight. Kept pure so captures can verify that
    /// user slider values reach the expected compositor input.
    /// </summary>
    public static float ResolveAppliedHistoryWeight(
        float effectiveHistoryWeight,
        float lightningSuppression,
        float solarSuppression)
    {
        float effective = Mathf.Clamp(effectiveHistoryWeight, 0.0f, 0.95f);
        float flash = Mathf.Lerp(effective, Mathf.Min(0.04f, effective), Mathf.Clamp(lightningSuppression, 0.0f, 1.0f));
        return Mathf.Lerp(flash, Mathf.Min(0.04f, flash), Mathf.Clamp(solarSuppression, 0.0f, 1.0f));
    }

    // A changing sun legitimately changes cloud shading, but Sunshine's screen
    // history contains the previous lighting result. At accelerated ring-spin
    // speeds that old result looks like clouds sliding across the screen. Keep
    // normal history at the authored ring-spin rate, then retire it while the sun is
    // moving quickly; this is independent of camera-motion reprojection.
    private void UpdateSunTemporalHistory(double delta)
    {
        if (_clouds == null || _sunLight == null) return;

        Vector3 direction = _sunLight.GlobalBasis.Z.Normalized();
        float angularSpeed = 0.0f;
        float angularDistance = 0.0f;
        if (_hasPreviousSunDirection)
        {
            angularDistance = SunAngularDistanceDegrees(_previousSunDirection, direction);
            // Do not turn a zero/invalid process delta into an arbitrarily
            // large solar velocity. This can occur while the editor pauses or
            // the viewport is rebuilt, and is not a lighting discontinuity.
            if (delta > 0.000001 && double.IsFinite(delta))
                angularSpeed = angularDistance / (float)delta;
        }
        _previousSunDirection = direction;
        _hasPreviousSunDirection = true;

        float threshold = Mathf.Max(0.1f, MinimumHistorySunRotationDegreesPerSecond);
        // atan2(|cross|, dot) remains accurate for sub-degree per-frame
        // motion, unlike acos(dot), whose float precision makes ordinary 1x
        // motion orientation-dependent at high frame rates. Keep a strict
        // quiet window below the configured threshold: the normal authored
        // cycle must retain its history completely.
        // A real discontinuity remains immediate even when it occurs in one
        // frame (teleport/clock jump), while sustained accelerated motion
        // ramps to full suppression over one threshold-width window.
        float continuousSuppression = SmoothUnit((angularSpeed - threshold) / threshold);
        bool discontinuity = angularDistance >= Mathf.Max(0.01f, SunHistoryDiscontinuityDegrees);
        _sunHistorySuppression = discontinuity ? 1.0f : continuousSuppression;
        bool suppressing = _sunHistorySuppression >= 0.5f;
        if (suppressing && !_sunHistoryWasSuppressed)
        {
            RefreshCloudCompute();
            SunTemporalHistoryRefreshCount++;
        }
        _sunHistoryWasSuppressed = suppressing;
        ApplyEffectiveHistoryWeight();
    }

    /// <summary>Stable unsigned angle between two sun directions, in degrees.</summary>
    public static float SunAngularDistanceDegrees(Vector3 previous, Vector3 current)
    {
        if (!previous.IsFinite() || !current.IsFinite()) return 0.0f;
        Vector3 a = previous.Normalized();
        Vector3 b = current.Normalized();
        if (a.LengthSquared() < 0.000001f || b.LengthSquared() < 0.000001f) return 0.0f;
        float sine = a.Cross(b).Length();
        float cosine = Mathf.Clamp(a.Dot(b), -1.0f, 1.0f);
        return Mathf.RadToDeg(Mathf.Atan2(sine, cosine));
    }

    private void ApplyEffectiveHistoryWeight(bool force = false)
    {
        if (_clouds == null) return;
        float historyWeight = ResolveAppliedHistoryWeight(
            EffectiveHistoryWeight,
            _lightningHistorySuppression,
            _sunHistorySuppression);
        if (!force && Mathf.IsEqualApprox(_lastAppliedHistoryWeight, historyWeight)) return;
        _clouds.Set("accumulation_decay", historyWeight);
        _lastAppliedHistoryWeight = historyWeight;
    }

    private RingVolumetricCloudLayer.LocalWeatherState ResolveCurrentWeather(
        in RingVolumetricCloudLayer.LocalWeatherState natural)
    {
        // The cloud layer owns the registered preview's smooth ellipse query.
        // Applying this bridge's old local blend again would square the falloff.
        if (_spawnedWeather is not { } spawned || _grid == null || _explorer == null) return natural;
        if (natural.SystemId == SunshinePreviewWeatherSystemId) return natural;
        // Outside a small catalog specimen's ellipse the shared query correctly
        // falls back to natural weather.  Only catalog display volumes retain
        // their profile/altitude metadata; never turn a distant storm front
        // into full coverage or precipitation at the observer.
        if (!spawned.Label.StartsWith("Sunshine ", StringComparison.Ordinal)) return natural;
        return new RingVolumetricCloudLayer.LocalWeatherState(
            natural.SystemId, natural.SystemKind, natural.SystemInfluence, spawned.RadiiMeters.X,
            spawned.CenterMeters, spawned.RadiiMeters, spawned.OrientationRadians,
            natural.CloudCoverage, natural.PrecipitationPotential, natural.WindMetersPerSecond,
            spawned.Profile, spawned.CloudBaseAboveDatumMeters, spawned.CloudTopAboveDatumMeters);

    }

    private CloudAltitudeRange ResolveNaturalCloudAltitude()
    {
        return ResolveCloudAltitude(
            FixedCloudAltitudeDatumMeters,
            NaturalCloudBaseAltitudeMeters,
            NaturalCloudTopAltitudeMeters);
    }

    private CloudAltitudeRange ResolveNaturalStormCloudAltitude(
        in RingVolumetricCloudLayer.LocalWeatherState weather,
        in CloudAltitudeRange backgroundAltitude)
    {
        if (weather.SystemKind is not (
                RingVolumetricCloudLayer.WeatherSystemKind.MajorFront
                or RingVolumetricCloudLayer.WeatherSystemKind.ExceptionalFront))
            return backgroundAltitude;

        return ResolveCloudAltitude(
            FixedCloudAltitudeDatumMeters,
            StormFrontCloudBaseAltitudeMeters,
            Math.Max(StormFrontCloudBaseAltitudeMeters + 500.0f, weather.CloudTopAltitudeMeters));
    }

    private static CloudAltitudeRange ResolveCloudAltitude(
        float altitudeDatumMeters,
        float baseAboveDatumMeters,
        float topAboveDatumMeters)
    {
        float cloudBase = altitudeDatumMeters + Math.Max(100.0f, baseAboveDatumMeters);
        float cloudTop = altitudeDatumMeters
            + Math.Max(baseAboveDatumMeters + 500.0f, topAboveDatumMeters);
        return new CloudAltitudeRange(cloudBase, cloudTop);
    }

    private void SpawnWeatherPreview(in SpawnedWeatherPreview preview)
    {
        CancelCloudShadowBuild();
        _spawnedWeather = preview;
        _weather?.RegisterManualWeather(SunshinePreviewWeatherOwnerKey,
            new RingVolumetricCloudLayer.LocalWeatherState(
                SunshinePreviewWeatherSystemId, preview.Kind, 1.0f, preview.RadiiMeters.X,
                preview.CenterMeters, preview.RadiiMeters, preview.OrientationRadians,
                preview.Coverage, preview.Precipitation, Vector2.Zero, preview.Profile,
                preview.CloudBaseAboveDatumMeters, preview.CloudTopAboveDatumMeters));
        RefreshWeather();
        RefreshCloudCompute();
        GD.Print($"{preview.Label} at canonical {preview.CenterMeters}, radii={preview.RadiiMeters}m.");
    }

    private static RingVolumetricCloudLayer.WeatherSystemKind ResolveCatalogWeatherKind(
        CloudFormationProfile profile)
    {
        return profile switch
        {
            CloudFormationProfile.Cumulonimbus => RingVolumetricCloudLayer.WeatherSystemKind.ExceptionalFront,
            CloudFormationProfile.Nimbostratus
                or CloudFormationProfile.Altostratus
                or CloudFormationProfile.Cirrostratus => RingVolumetricCloudLayer.WeatherSystemKind.CloudBank,
            CloudFormationProfile.Stratus
                or CloudFormationProfile.Stratocumulus => RingVolumetricCloudLayer.WeatherSystemKind.CloudBank,
            _ => RingVolumetricCloudLayer.WeatherSystemKind.CumulusCluster
        };
    }

    private Vector2 ResolveSpawnCenter(Vector2 direction, float? distanceMeters = null)
    {
        if (_explorer == null || _grid == null) return Vector2.Zero;
        RingCanonicalPosition viewer = _explorer.CanonicalPosition;
        double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        float distance = Math.Max(0.0f, distanceMeters ?? SpawnDistanceMeters);
        double around = viewer.AroundMeters + direction.X * distance;
        around %= circumference;
        if (around < 0.0) around += circumference;
        float across = Mathf.Clamp(
            (float)viewer.AcrossMeters + direction.Y * distance,
            0.0f,
            _grid.RingAcrossWidthMeters);
        return new Vector2((float)around, across);
    }

    private Vector2 ResolveSpawnDirection()
    {
        if (_grid == null || _explorer == null) return Vector2.Right;
        _camera ??= GetViewport().GetCamera3D();
        if (_camera != null)
        {
            RingCanonicalPosition viewer = _explorer.CanonicalPosition;
            RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
            Vector3 origin = geometry.CanonicalToWorld(viewer.AroundMeters, 0.0, viewer.AcrossMeters);
            Vector3 around = (geometry.CanonicalToWorld(viewer.AroundMeters + 10.0, 0.0, viewer.AcrossMeters) - origin).Normalized();
            Vector3 across = (geometry.CanonicalToWorld(viewer.AroundMeters, 0.0, viewer.AcrossMeters + 10.0) - origin).Normalized();
            Vector3 forward = (-_camera.GlobalBasis.Z).Normalized();
            Vector2 horizontal = new(forward.Dot(around), forward.Dot(across));
            if (horizontal.LengthSquared() > 0.01f) return horizontal.Normalized();
        }

        Vector2 velocity = new(_explorer.CanonicalVelocity.X, _explorer.CanonicalVelocity.Z);
        return velocity.LengthSquared() > 0.01f ? velocity.Normalized() : Vector2.Right;
    }

    private static float SmoothUnit(float value)
    {
        float clamped = Mathf.Clamp(value, 0.0f, 1.0f);
        return clamped * clamped * (3.0f - 2.0f * clamped);
    }

    private void UpdateCloudShadows(double delta, bool force)
    {
        bool shouldProject = CastCloudShadows
            && RuntimeVisible
            && _clouds != null
            && _grid?.TerrainSampler != null
            && _explorer != null
            && _sunLight != null;
        if (!shouldProject)
        {
            if (_shadowBuildPending || _shadowMapReady) CancelCloudShadowBuild();
            if (_shadowProjectorEnabled)
            {
                RingCloudShadowProjector.Disable();
                _shadowProjectorEnabled = false;
            }
            _targetCameraDirectSunTransmittance = 1.0f;
            _targetCameraAmbientTransmittance = 1.0f;
            UsingGpuCloudShadows = false;
            return;
        }

        // The addon owns the compute textures and never readbacks them.  If its
        // render-device pipeline is unavailable, retain the older callback map.
        if (PreferGpuCloudShadows && TryUpdateGpuCloudShadows(delta, force))
            return;
        UsingGpuCloudShadows = false;

        if (_shadowMapReady && CloudShadowReceiversEnabled) InstallPendingShadowMap();
        else if (!CloudShadowReceiversEnabled && _shadowProjectorEnabled)
        {
            RingCloudShadowProjector.Disable();
            _shadowProjectorEnabled = false;
        }
        else if (CloudShadowReceiversEnabled && !_shadowProjectorEnabled)
        {
            RestoreInstalledCpuShadowReceiver();
        }
        UpdateInstalledShadowTransform();
        _shadowElapsed += Math.Max(0.0, delta);

        RingCanonicalPosition viewer = _explorer!.CanonicalPosition;
        float activeRadius = Mathf.Max(
            ShadowRadiusMeters,
            Mathf.Max(_shadowBuildRadiusMeters, InstalledShadowRadiusMeters));
        float refreshDistance = Mathf.Clamp(activeRadius * 0.08f, 500.0f, 4000.0f);
        bool movedOutsideRefreshCell = !_hasShadowCenter
            || ShadowCenterDistanceMeters(viewer) >= refreshDistance;
        if (!_shadowBuildPending
            && !_shadowMapReady
            && (force
                || _shadowTexture == null
                || movedOutsideRefreshCell
                || _shadowElapsed >= Math.Max(1.0f, ShadowRefreshSeconds)))
        {
            QueueCloudShadowMap(viewer);
        }
    }

    private float ShadowCenterDistanceMeters(in RingCanonicalPosition viewer)
    {
        if (_grid == null || !_hasShadowCenter) return float.PositiveInfinity;
        double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        double around = viewer.AroundMeters - _shadowCenterCanonical.AroundMeters;
        around -= Math.Round(around / circumference) * circumference;
        double across = viewer.AcrossMeters - _shadowCenterCanonical.AcrossMeters;
        return (float)Math.Sqrt(around * around + across * across);
    }

    private bool TryUpdateGpuCloudShadows(double delta, bool force)
    {
        if (_clouds == null || _grid == null || _explorer == null || _sunLight == null
            || !_clouds.HasMethod("configure_gpu_cloud_shadow_cascade"))
        {
            GpuCloudShadowStatus = "addon GPU shadow API unavailable";
            return false;
        }

        if (_clouds.HasMethod("set_gpu_cloud_shadow_generation_enabled"))
            _clouds.Call("set_gpu_cloud_shadow_generation_enabled", GpuShadowGenerationEnabled);

        Godot.Collections.Dictionary status = _clouds.Call("get_gpu_cloud_shadow_status").AsGodotDictionary();
        bool available = status.TryGetValue("available", out Variant availableValue) && availableValue.AsBool();
        GpuCloudShadowStatus = status.TryGetValue("error", out Variant errorValue)
            ? errorValue.AsString() : "";
        if (!available)
        {
            _gpuPipelineWaitSeconds += Math.Max(0.0, delta);
            // The compositor may already have retired its Texture2DRD maps.
            // Clear receiver bindings before either waiting or falling back.
            if (UsingGpuCloudShadows && RingCloudShadowProjector.HasExternalTextures)
            {
                RingCloudShadowProjector.ReleaseTextures();
                _shadowProjectorEnabled = false;
            }
            if (!string.IsNullOrEmpty(GpuCloudShadowStatus) || _gpuPipelineWaitSeconds >= 2.0)
            {
                if (string.IsNullOrEmpty(GpuCloudShadowStatus))
                    GpuCloudShadowStatus = "GPU pipeline initialization timed out";
                return false;
            }
            // A normal compositor refresh briefly destroys and recreates its RD
            // resources. Do not launch the expensive CPU builder or leave a
            // material sampling the old freed Texture2DRD during that window.
            UsingGpuCloudShadows = true;
            GpuCloudShadowStatus = "GPU pipeline initializing";
            return true;
        }
        _gpuPipelineWaitSeconds = 0.0;
        PromoteCompletedGpuShadowCascade(0, GetGpuShadowDispatchCount(status, "near_dispatches"));
        PromoteCompletedGpuShadowCascade(1, GetGpuShadowDispatchCount(status, "far_dispatches"));
        if (_shadowBuildPending || _shadowMapReady) CancelCloudShadowBuild();

        RingCanonicalPosition viewer = _explorer.CanonicalPosition;
        ResolveGpuShadowFrame(0, viewer, out _, out _, out _, out Vector3 viewerUp);
        Vector3 currentSun = _sunLight.GlobalBasis.Z.Normalized();
        if (currentSun.Dot(viewerUp) <= 0.04f)
        {
            if (RingCloudShadowProjector.HasExternalTextures)
            {
                RingCloudShadowProjector.ReleaseTextures();
                _shadowProjectorEnabled = false;
            }
            UsingGpuCloudShadows = true;
            GpuCloudShadowStatus = "inactive: sun below horizon";
            // Force both maps to rebake on the first sunrise frame instead of
            // briefly reusing the last sunset projection.
            _gpuShadowElapsed[0] = double.PositiveInfinity;
            _gpuShadowElapsed[1] = double.PositiveInfinity;
            _hasGpuShadowCenter[0] = false;
            _hasGpuShadowCenter[1] = false;
            _gpuShadowPublishPending[0] = false;
            _gpuShadowPublishPending[1] = false;
            _targetCameraDirectSunTransmittance = 1.0f;
            _targetCameraAmbientTransmittance = 1.0f;
            return true;
        }
        _gpuShadowFrameWindDisplacement = ResolveGpuShadowBulkWindDisplacement(
            _currentWeather.WindMetersPerSecond * CloudWindMultiplier,
            (float)Math.Max(0.0, delta));
        bool[] due = new bool[2];
        bool[] relocateCascade = new bool[2];
        bool requiresReceiverDetach = false;
        for (int cascade = 0; cascade < 2; cascade++)
        {
            _gpuShadowElapsed[cascade] += Math.Max(0.0, delta);
            float radius = cascade == 0 ? GpuNearShadowRadiusMeters : GpuFarShadowRadiusMeters;
            float frequency = cascade == 0 ? GpuNearShadowUpdatesPerSecond : GpuFarShadowUpdatesPerSecond;
            int resolution = cascade == 0 ? GpuNearShadowMapResolution : GpuFarShadowMapResolution;
            float refreshDistance = Mathf.Clamp(radius * 0.08f, 500.0f, 4000.0f);
            bool relocate = !_hasGpuShadowCenter[cascade]
                || GpuShadowCenterDistanceMeters(cascade, viewer) >= refreshDistance;
            relocateCascade[cascade] = relocate;
            bool configurationChanged = _gpuShadowBakedResolution[cascade] != resolution
                || !Mathf.IsEqualApprox(_gpuShadowBakedRadius[cascade], radius);
            requiresReceiverDetach |= configurationChanged;
            due[cascade] = !_gpuShadowPublishPending[cascade]
                && (force || relocate || configurationChanged || _gpuShadowElapsed[cascade] >= 1.0 / Mathf.Max(0.1f, frequency));
        }
        // This must happen on the main thread before configure_gpu_* asks the
        // compositor to replace an RD texture.  A normal Disable only hides a
        // receiver but retains its externally owned Texture2DRD reference.
        if (requiresReceiverDetach && RingCloudShadowProjector.HasExternalTextures)
        {
            RingCloudShadowProjector.ReleaseTextures();
            _shadowProjectorEnabled = false;
            _gpuShadowReceiverRebuildPending = true;
        }
        if (GpuShadowGenerationEnabled)
        {
            // Never issue both full-screen compute passes in one frame. Missing
            // or relocated cascades get priority, and otherwise ties alternate
            // so the slower far map cannot be starved by the near map.
            if (due[0] || due[1])
            {
                int cascade;
                if (due[0] && due[1] && relocateCascade[0] != relocateCascade[1])
                    cascade = relocateCascade[0] ? 0 : 1;
                else
                    cascade = due[0] && due[1] ? _nextTimedGpuCascade : due[0] ? 0 : 1;
                _nextTimedGpuCascade = 1 - cascade;
                ConfigureGpuShadowCascade(
                    cascade,
                    relocateCascade[cascade] ? viewer : _gpuShadowCenters[cascade],
                    cascade == 0 ? GpuNearShadowRadiusMeters : GpuFarShadowRadiusMeters,
                    force || relocateCascade[cascade],
                    GetGpuShadowDispatchCount(status, cascade == 0 ? "near_dispatches" : "far_dispatches"));
            }
        }

        if (_gpuShadowReceiverRebuildPending)
        {
            bool replacementsReady = !_gpuShadowPublishPending[0]
                && !_gpuShadowPublishPending[1]
                && _hasGpuShadowCenter[0]
                && _hasGpuShadowCenter[1]
                && _gpuShadowBakedResolution[0] == GpuNearShadowMapResolution
                && _gpuShadowBakedResolution[1] == GpuFarShadowMapResolution;
            if (!replacementsReady)
            {
                UsingGpuCloudShadows = true;
                GpuCloudShadowStatus = "GPU receiver maps rebuilding";
                return true;
            }
            _gpuShadowReceiverRebuildPending = false;
        }

        Texture2D? near = _hasGpuShadowCenter[0]
            ? _clouds.Call("get_gpu_cloud_shadow_texture", 0).AsGodotObject() as Texture2D
            : null;
        Texture2D? far = _hasGpuShadowCenter[1]
            ? _clouds.Call("get_gpu_cloud_shadow_texture", 1).AsGodotObject() as Texture2D
            : null;
        // A configured render-device pipeline is authoritative even during its
        // first compositor frame; do not start the costly CPU readback fallback.
        if (near == null && far == null)
        {
            if (RingCloudShadowProjector.HasExternalTextures)
            {
                RingCloudShadowProjector.ReleaseTextures();
                _shadowProjectorEnabled = false;
            }
            GpuCloudShadowStatus = "GPU dispatch pending";
            return true;
        }

        // Keep every receiver-side projection value on the last completed
        // dispatch. Inspector radius/resolution changes are staged along with
        // the replacement map and must not leak into the old map's UV frame.
        float nearInstalledRadius = _hasGpuShadowCenter[0]
            ? Mathf.Max(1.0f, _gpuShadowBakedRadius[0])
            : Mathf.Max(1.0f, GpuNearShadowRadiusMeters);
        float farInstalledRadius = _hasGpuShadowCenter[1]
            ? Mathf.Max(1.0f, _gpuShadowBakedRadius[1])
            : Mathf.Max(1.0f, GpuFarShadowRadiusMeters);
        ResolveGpuShadowFrame(0, out Vector3 nearCenter, out Vector3 nearAround, out Vector3 nearAcross, out Vector3 nearUp);
        ResolveGpuShadowFrame(1, out Vector3 farCenter, out Vector3 farAround, out Vector3 farAcross, out Vector3 farUp);
        Vector2 nearOffset = ResolveGpuShadowUvOffset(0, nearAround, nearAcross, nearUp);
        Vector2 farOffset = ResolveGpuShadowUvOffset(1, farAround, farAcross, farUp);
        if (CloudShadowReceiversEnabled)
        {
            RingCloudShadowProjector.UpdateCascades(
                near, nearCenter, nearAround, nearAcross, nearUp, nearInstalledRadius, nearOffset,
                far, farCenter, farAround, farAcross, farUp, farInstalledRadius, farOffset,
                ResolveShadowRingRadiusMeters(), currentSun,
                Mathf.Clamp(ShadowStrength, 0.0f, 1.0f));
            _shadowProjectorEnabled = true;
        }
        else
        {
            // These Texture2DRD objects are owned by Sunshine and can be
            // replaced during any viewport resource rebuild. Producer-only
            // profiling therefore releases them instead of retaining a stale
            // disabled binding. They are rebound directly from the addon when
            // receivers are enabled again.
            if (RingCloudShadowProjector.HasExternalTextures)
                RingCloudShadowProjector.ReleaseTextures();
            _shadowProjectorEnabled = false;
        }
        UsingGpuCloudShadows = true;
        InstalledShadowRadiusMeters = nearInstalledRadius;
        InstalledShadowResolution = _hasGpuShadowCenter[0]
            ? _gpuShadowBakedResolution[0]
            : GpuNearShadowMapResolution;
        GpuCloudShadowStatus = string.IsNullOrEmpty(GpuCloudShadowStatus) ? "active" : GpuCloudShadowStatus;
        return true;
    }

    private float GpuShadowCenterDistanceMeters(int cascade, in RingCanonicalPosition viewer)
    {
        if (_grid == null || !_hasGpuShadowCenter[cascade]) return float.PositiveInfinity;
        double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        double around = viewer.AroundMeters - _gpuShadowCenters[cascade].AroundMeters;
        around -= Math.Round(around / circumference) * circumference;
        double across = viewer.AcrossMeters - _gpuShadowCenters[cascade].AcrossMeters;
        return (float)Math.Sqrt(around * around + across * across);
    }

    private static int GetGpuShadowDispatchCount(Godot.Collections.Dictionary status, string key)
        => status.TryGetValue(key, out Variant value)
            ? (int)Math.Clamp(value.AsInt64(), 0L, int.MaxValue)
            : 0;

    private void PromoteCompletedGpuShadowCascade(int cascade, int completedDispatchCount)
    {
        if (!_gpuShadowPublishPending[cascade] || completedDispatchCount < _gpuShadowRequestedDispatchCount[cascade])
            return;

        _gpuShadowCenters[cascade] = _gpuShadowRequestedCenters[cascade];
        _hasGpuShadowCenter[cascade] = true;
        _gpuShadowWindOffsetMeters[cascade] = _gpuShadowRequestedWindOffsetMeters[cascade];
        _gpuShadowBakedSun[cascade] = _gpuShadowRequestedBakedSun[cascade];
        _gpuShadowBakedProjection[cascade] = _gpuShadowRequestedBakedProjection[cascade];
        _gpuShadowBakedResolution[cascade] = _gpuShadowRequestedBakedResolution[cascade];
        _gpuShadowBakedRadius[cascade] = _gpuShadowRequestedBakedRadius[cascade];
        _gpuShadowPublishPending[cascade] = false;
    }

    private void ConfigureGpuShadowCascade(int cascade, in RingCanonicalPosition centerCanonical, float radius, bool forceReset, int dispatchCount)
    {
        if (_clouds == null || _sunLight == null) return;
        ResolveGpuShadowFrame(cascade, centerCanonical, out Vector3 center, out Vector3 around, out Vector3 across, out Vector3 up);
        Vector3 sun = _sunLight.GlobalBasis.Z.Normalized();
        if (sun.Dot(up) <= 0.04f) return;
        float floor = _clouds.Get("cloud_floor").AsSingle();
        float ceiling = Mathf.Max(floor + 1.0f, _clouds.Get("cloud_ceiling").AsSingle());
        int resolution = cascade == 0 ? GpuNearShadowMapResolution : GpuFarShadowMapResolution;
        bool resetHistory = forceReset || !_hasGpuShadowCenter[cascade]
            || _gpuShadowBakedResolution[cascade] != resolution
            || !Mathf.IsEqualApprox(_gpuShadowBakedRadius[cascade], radius);
        bool accepted = _clouds.Call("configure_gpu_cloud_shadow_cascade", cascade,
            Math.Clamp(resolution, 8, 2048), center, around, across, up,
            new Vector2((float)centerCanonical.AroundMeters, (float)centerCanonical.AcrossMeters),
            ResolveShadowRingRadiusMeters(), Mathf.Max(1000.0f, radius),
            sun, floor, ceiling, Math.Clamp(GpuShadowVerticalSamples, 1, 32), Mathf.Max(0.0f, ShadowExtinctionPerKilometer), resetHistory).AsBool();
        if (!accepted)
            return;
        _clouds.Call("request_gpu_cloud_shadow_update", cascade);
        _gpuShadowElapsed[cascade] = 0.0;
        _gpuShadowRequestedCenters[cascade] = centerCanonical;
        _gpuShadowRequestedWindOffsetMeters[cascade] = Vector2.Zero;
        _gpuShadowRequestedBakedSun[cascade] = sun;
        _gpuShadowRequestedBakedProjection[cascade] = ResolveSunProjectionMeters(sun, around, across, up, (floor + ceiling) * 0.5f);
        _gpuShadowRequestedBakedResolution[cascade] = resolution;
        _gpuShadowRequestedBakedRadius[cascade] = radius;
        _gpuShadowRequestedDispatchCount[cascade] = dispatchCount + 1;
        _gpuShadowPublishPending[cascade] = true;
    }

    private void ResolveGpuShadowFrame(int cascade, out Vector3 center, out Vector3 around, out Vector3 across, out Vector3 up)
        => ResolveGpuShadowFrame(cascade, _gpuShadowCenters[cascade], out center, out around, out across, out up);

    private void ResolveGpuShadowFrame(int cascade, in RingCanonicalPosition canonical, out Vector3 center, out Vector3 around, out Vector3 across, out Vector3 up)
    {
        RingLodGeometrySnapshot geometry = _grid!.CaptureLodGeometrySnapshot();
        center = geometry.CanonicalToWorld(canonical.AroundMeters, 0.0, canonical.AcrossMeters);
        around = (geometry.CanonicalToWorld(canonical.AroundMeters + 10.0, 0.0, canonical.AcrossMeters) - center).Normalized();
        across = (geometry.CanonicalToWorld(canonical.AroundMeters, 0.0, canonical.AcrossMeters + 10.0) - center).Normalized();
        up = (geometry.CanonicalToWorld(canonical.AroundMeters, 10.0, canonical.AcrossMeters) - center).Normalized();
    }

    private Vector2 ResolveGpuShadowUvOffset(int cascade, Vector3 around, Vector3 across, Vector3 up)
    {
        float configuredRadius = cascade == 0 ? GpuNearShadowRadiusMeters : GpuFarShadowRadiusMeters;
        float radius = _hasGpuShadowCenter[cascade]
            ? Mathf.Max(1.0f, _gpuShadowBakedRadius[cascade])
            : Mathf.Max(1.0f, configuredRadius);
        _gpuShadowWindOffsetMeters[cascade] += _gpuShadowFrameWindDisplacement;
        Vector3 sun = _sunLight!.GlobalBasis.Z.Normalized();
        float representativeHeight = _clouds == null ? 0.0f : (_clouds.Get("cloud_floor").AsSingle() + _clouds.Get("cloud_ceiling").AsSingle()) * 0.5f;
        Vector2 sunOffset = ResolveSunProjectionMeters(sun, around, across, up, representativeHeight) - _gpuShadowBakedProjection[cascade];
        // Receiver-side wind and solar-projection translation compensates between
        // lower-frequency rebakes. A cloud moving +wind is sampled at -wind.
        // A sun ray tilting +planar shifts its ground shadow in the opposite
        // direction, so reconstructing the new projection samples at +sun.
        Vector2 samplingOffset = sunOffset - _gpuShadowWindOffsetMeters[cascade];
        return samplingOffset / (radius * 2.0f);
    }

    private static Vector2 ResolveSunProjectionMeters(Vector3 sun, Vector3 around, Vector3 across, Vector3 up, float height)
    {
        float vertical = Mathf.Max(0.04f, sun.Dot(up));
        Vector3 planar = sun - up * sun.Dot(up);
        return new Vector2(planar.Dot(around), planar.Dot(across)) * (height / vertical);
    }

    /// <summary>
    /// Advances a baked projection between dispatches using the coherent
    /// coverage layer's bulk velocity, not the faster erosion/detail layers.
    /// </summary>
    public static Vector2 ResolveGpuShadowBulkWindDisplacement(Vector2 weatherWindMetersPerSecond, float deltaSeconds)
        => weatherWindMetersPerSecond * (GpuShadowBulkWindFactor * Mathf.Max(0.0f, deltaSeconds));

    private float ResolveShadowRingRadiusMeters()
    {
        if (_grid == null) return 1.0f;
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        return (float)Math.Max(
            1.0,
            geometry.RingRadiusMeters - geometry.RingVerticalBaseOffsetMeters);
    }

    private float ResolveEffectiveShadowRadius(in RingCanonicalPosition viewer)
    {
        float maximum = Mathf.Max(ShadowRadiusMeters, MaximumShadowRadiusMeters);
        float radius = Mathf.Max(ShadowRadiusMeters, HandoffStartMeters);
        if (_currentWeather.SystemKind != RingVolumetricCloudLayer.WeatherSystemKind.None
            && _currentWeather.SystemRadiiMeters.X > 0.0f
            && _currentWeather.SystemRadiiMeters.Y > 0.0f
            && _grid != null)
        {
            double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
            double around = _currentWeather.SystemCenterMeters.X - viewer.AroundMeters;
            around -= Math.Round(around / circumference) * circumference;
            double across = _currentWeather.SystemCenterMeters.Y - viewer.AcrossMeters;
            float distance = (float)Math.Sqrt(around * around + across * across);
            float formationRadius = Mathf.Max(
                _currentWeather.SystemRadiiMeters.X,
                _currentWeather.SystemRadiiMeters.Y);
            radius = Mathf.Max(radius, distance + formationRadius + 4000.0f);
        }
        return Mathf.Clamp(radius, 8000.0f, maximum);
    }

    private void QueueCloudShadowMap(in RingCanonicalPosition viewer)
    {
        if (_clouds == null || _grid?.TerrainSampler == null || _sunLight == null) return;

        int resolution = Math.Clamp(Mathf.RoundToInt(ShadowMapResolution / 8.0f) * 8, 16, 128);
        int verticalSamples = Math.Clamp(ShadowVerticalSamples, 1, 8);
        float radius = ResolveEffectiveShadowRadius(viewer);
        float floor = _clouds.Get("cloud_floor").AsSingle();
        float ceiling = Mathf.Max(floor + 1.0f, _clouds.Get("cloud_ceiling").AsSingle());
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        Vector3 toSun = _sunLight.GlobalBasis.Z.Normalized();
        Vector3 centerGround = geometry.CanonicalToWorld(viewer.AroundMeters, 0.0, viewer.AcrossMeters);
        Vector3 centerUp = (geometry.CanonicalToWorld(viewer.AroundMeters, 10.0, viewer.AcrossMeters) - centerGround).Normalized();
        if (toSun.Dot(centerUp) <= 0.04f)
        {
            if (_shadowProjectorEnabled)
            {
                RingCloudShadowProjector.Disable();
                _shadowProjectorEnabled = false;
            }
            _shadowElapsed = 0.0;
            return;
        }

        _shadowCenterCanonical = viewer;
        _hasShadowCenter = true;
        _shadowBuildResolution = resolution;
        _shadowDensitySums = new float[resolution * resolution];
        _shadowDensitySampleCounts = new int[resolution * resolution];
        _shadowPathKilometers = new float[resolution * resolution];
        _shadowSamplesRemaining = resolution * resolution * verticalSamples;
        _shadowBuildPending = true;
        _shadowMapReady = false;
        _shadowElapsed = 0.0;
        int generation = ++_shadowBuildGeneration;
        var surfaceSnapshot = _grid.CaptureLodSurfaceEditSnapshot();
        float diameter = radius * 2.0f;
        _shadowBuildRadiusMeters = radius;
        float maximumSunShift = radius * 0.95f;
        float singleSampleHeightFraction = _currentWeather.DominantFormationProfile
                == CloudFormationProfile.AnvilStorm
            ? Mathf.Clamp(AnvilStartFraction + 0.10f, 0.55f, 0.90f)
            : 0.50f;

        for (int y = 0; y < resolution; y++)
        {
            float acrossOffset = ((y + 0.5f) / resolution - 0.5f) * diameter;
            double targetAcross = Math.Clamp(viewer.AcrossMeters + acrossOffset, 0.0, _grid.RingAcrossWidthMeters);
            for (int x = 0; x < resolution; x++)
            {
                float aroundOffset = ((x + 0.5f) / resolution - 0.5f) * diameter;
                double targetAround = viewer.AroundMeters + aroundOffset;
                int aroundVoxel = (int)Math.Round(targetAround);
                int acrossVoxel = Mathf.Clamp(
                    Mathf.RoundToInt((float)targetAcross),
                    0,
                    Mathf.RoundToInt(_grid.RingAcrossWidthMeters));
				float surface = _grid.TerrainSampler.UsesRuntimeSurfaceGeneration || surfaceSnapshot.UsesFloatSurface
					? surfaceSnapshot.GetPresentationSurfaceHeight(_grid.TerrainSampler, aroundVoxel, acrossVoxel)
					: surfaceSnapshot.GetSurfaceHeight(_grid.TerrainSampler, aroundVoxel, acrossVoxel);
                Vector3 ground = geometry.CanonicalToWorld(targetAround, surface, targetAcross);
                Vector3 up = (geometry.CanonicalToWorld(targetAround, surface + 10.0, targetAcross) - ground).Normalized();
                float sunVertical = Mathf.Max(0.10f, toSun.Dot(up));
                Vector3 sunPlanar = toSun - up * toSun.Dot(up);
                int pixelIndex = y * resolution + x;
                _shadowPathKilometers[pixelIndex] = Mathf.Max(
                    0.05f,
                    (ceiling - floor) / sunVertical * 0.001f);

                for (int verticalIndex = 0; verticalIndex < verticalSamples; verticalIndex++)
                {
                    float heightFraction = verticalSamples == 1
                        ? singleSampleHeightFraction
                        : (verticalIndex + 0.5f) / verticalSamples;
                    float sampleHeight = Mathf.Lerp(floor, ceiling, heightFraction);
                    float heightAboveGround = Mathf.Max(0.0f, sampleHeight - surface);
                    Vector3 samplePoint = ground + up * heightAboveGround;
                    if (sunPlanar.LengthSquared() > 0.000001f)
                    {
                        float planarShift = Mathf.Min(
                            maximumSunShift,
                            heightAboveGround * sunPlanar.Length() / sunVertical);
                        samplePoint += sunPlanar.Normalized() * planarShift;
                    }

                    int capturedIndex = pixelIndex;
                    _clouds.Call(
                        "add_sample",
                        Callable.From<Vector3, float>((_, density) =>
                            OnCloudShadowDensitySample(generation, capturedIndex, density)),
                        samplePoint);
                }
            }
        }
    }

    private void OnCloudShadowDensitySample(int generation, int pixelIndex, float density)
    {
        if (generation != _shadowBuildGeneration
            || !_shadowBuildPending
            || _shadowDensitySums == null
            || _shadowDensitySampleCounts == null
            || _shadowPathKilometers == null
            || pixelIndex < 0
            || pixelIndex >= _shadowDensitySums.Length)
        {
            return;
        }

        _shadowDensitySums[pixelIndex] += Mathf.Max(0.0f, density);
        _shadowDensitySampleCounts[pixelIndex]++;
        _shadowSamplesRemaining--;
        if (_shadowSamplesRemaining > 0) return;

        var mask = new byte[_shadowDensitySums.Length];
        float threshold = Mathf.Clamp(ShadowDensityThreshold, 0.01f, 0.95f);
        float extinction = Mathf.Clamp(ShadowExtinctionPerKilometer, 0.05f, 2.0f);
        for (int index = 0; index < mask.Length; index++)
        {
            float averageDensity = _shadowDensitySampleCounts[index] > 0
                ? _shadowDensitySums[index] / _shadowDensitySampleCounts[index]
                : 0.0f;
            float normalizedDensity = Mathf.Clamp(
                (averageDensity - threshold) / Math.Max(0.01f, 1.0f - threshold),
                0.0f,
                1.0f);
            // Beer-Lambert extinction along the complete slanted path from the
            // cloud floor to ceiling. Thick storm towers can now approach full
            // solar occlusion while thin cirrus remains translucent.
            float opticalDepth = normalizedDensity * _shadowPathKilometers[index] * extinction;
            float opacity = 1.0f - MathF.Exp(-opticalDepth);
            mask[index] = (byte)Math.Clamp(Mathf.RoundToInt(opacity * 255.0f), 0, 255);
        }
        mask = BlurShadowMask(mask, _shadowBuildResolution);

        int center = (_shadowBuildResolution / 2) * _shadowBuildResolution
            + _shadowBuildResolution / 2;
        float centerOcclusion = mask[Math.Clamp(center, 0, mask.Length - 1)] / 255.0f;
        float neighborhoodOcclusion = SampleShadowNeighborhood(mask, _shadowBuildResolution, 3);
        float strength = Mathf.Clamp(ShadowStrength, 0.0f, 1.0f);
        _pendingCameraDirectSunTransmittance = Mathf.Clamp(
            1.0f - centerOcclusion * strength,
            0.01f,
            1.0f);
        _pendingCameraAmbientTransmittance = Mathf.Clamp(
            1.0f - neighborhoodOcclusion * Mathf.Clamp(StormAmbientOcclusionStrength, 0.0f, 1.0f),
            Mathf.Clamp(StormAmbientFloor, 0.02f, 0.5f),
            1.0f);

        int occupied = 0;
        byte peak = 0;
        foreach (byte value in mask)
        {
            if (value > 2) occupied++;
            if (value > peak) peak = value;
        }
        _pendingShadowMapData = mask;
        _pendingShadowOccupiedTexels = occupied;
        _pendingShadowPeak = peak;
        _shadowDensitySums = null;
        _shadowDensitySampleCounts = null;
        _shadowPathKilometers = null;
        _shadowBuildPending = false;
        _shadowMapReady = true;
    }

    private static byte[] BlurShadowMask(byte[] source, int resolution)
    {
        var blurred = new byte[source.Length];
        for (int y = 0; y < resolution; y++)
        for (int x = 0; x < resolution; x++)
        {
            int sum = 0;
            int count = 0;
            for (int oy = -1; oy <= 1; oy++)
            for (int ox = -1; ox <= 1; ox++)
            {
                int sampleX = Math.Clamp(x + ox, 0, resolution - 1);
                int sampleY = Math.Clamp(y + oy, 0, resolution - 1);
                sum += source[sampleY * resolution + sampleX];
                count++;
            }
            blurred[y * resolution + x] = (byte)(sum / Math.Max(1, count));
        }
        return blurred;
    }

    private static float SampleShadowNeighborhood(byte[] source, int resolution, int radius)
    {
        int center = resolution / 2;
        int sum = 0;
        int count = 0;
        for (int y = center - radius; y <= center + radius; y++)
        for (int x = center - radius; x <= center + radius; x++)
        {
            int sampleX = Math.Clamp(x, 0, resolution - 1);
            int sampleY = Math.Clamp(y, 0, resolution - 1);
            sum += source[sampleY * resolution + sampleX];
            count++;
        }
        return count > 0 ? sum / (255.0f * count) : 0.0f;
    }

    private void InstallPendingShadowMap()
    {
        _shadowMapReady = false;
        if (_pendingShadowMapData == null || _grid == null || !_hasShadowCenter) return;

        Image image = Image.CreateFromData(
            _shadowBuildResolution,
            _shadowBuildResolution,
            false,
            Image.Format.R8,
            _pendingShadowMapData);
        if (_shadowTexture == null
            || _shadowTexture.GetWidth() != _shadowBuildResolution
            || _shadowTexture.GetHeight() != _shadowBuildResolution)
        {
            _shadowTexture = ImageTexture.CreateFromImage(image);
        }
        else
        {
            _shadowTexture.Update(image);
        }
        _pendingShadowMapData = null;

        ResolveShadowProjectionFrame(out Vector3 center, out Vector3 around, out Vector3 across);
        _lastShadowCenterWorld = center;
        float radius = Mathf.Max(1000.0f, _shadowBuildRadiusMeters);
        float strength = Mathf.Clamp(ShadowStrength, 0.0f, 1.0f);
        RingCloudShadowProjector.Update(
            _shadowTexture,
            center,
            around,
            across,
            radius,
            strength,
            strength,
            0.0f,
            1.0f,
            radius,
            radius + 1.0f,
            across.Cross(around).Normalized(),
            ResolveShadowRingRadiusMeters());
        _shadowProjectorEnabled = true;
        InstalledShadowRadiusMeters = radius;
        InstalledShadowResolution = _shadowBuildResolution;
        InstalledShadowPeakOpacity = _pendingShadowPeak / 255.0f;
        _targetCameraDirectSunTransmittance = _pendingCameraDirectSunTransmittance;
        _targetCameraAmbientTransmittance = _pendingCameraAmbientTransmittance;

        if (!_shadowDiagnosticsReported)
        {
            _shadowDiagnosticsReported = true;
            GD.Print(
                $"Sunshine cloud shadows ready: receivers={RingCloudShadowProjector.ActiveReceiverCount}, "
                + $"map={_shadowBuildResolution}px, occupied={_pendingShadowOccupiedTexels}/{_shadowBuildResolution * _shadowBuildResolution}, "
                + $"peak={_pendingShadowPeak}, strength={strength:0.00}, radius={radius:0}m, "
                + $"cameraSun={_targetCameraDirectSunTransmittance:0.00}, cameraAmbient={_targetCameraAmbientTransmittance:0.00}.");
        }
    }

    private void UpdateInstalledShadowTransform()
    {
        if (!_shadowProjectorEnabled || _grid == null || !_hasShadowCenter) return;
        ResolveShadowProjectionFrame(out Vector3 center, out Vector3 around, out Vector3 across);
        if (_lastShadowCenterWorld.IsFinite()
            && _lastShadowCenterWorld.DistanceSquaredTo(center) < 0.25f * 0.25f)
        {
            return;
        }
        _lastShadowCenterWorld = center;
        RingCloudShadowProjector.UpdateTransform(center, around, across);
    }

    private void RestoreInstalledCpuShadowReceiver()
    {
        if (_shadowTexture == null || _grid == null || !_hasShadowCenter) return;
        ResolveShadowProjectionFrame(out Vector3 center, out Vector3 around, out Vector3 across);
        _lastShadowCenterWorld = center;
        float radius = Mathf.Max(
            1000.0f,
            InstalledShadowRadiusMeters > 0.0f
                ? InstalledShadowRadiusMeters
                : _shadowBuildRadiusMeters);
        float strength = Mathf.Clamp(ShadowStrength, 0.0f, 1.0f);
        RingCloudShadowProjector.Update(
            _shadowTexture,
            center,
            around,
            across,
            radius,
            strength,
            strength,
            0.0f,
            1.0f,
            radius,
            radius + 1.0f,
            across.Cross(around).Normalized(),
            ResolveShadowRingRadiusMeters());
        _shadowProjectorEnabled = true;
    }

    private void ResolveShadowProjectionFrame(out Vector3 center, out Vector3 around, out Vector3 across)
    {
        RingLodGeometrySnapshot geometry = _grid!.CaptureLodGeometrySnapshot();
        center = geometry.CanonicalToWorld(
            _shadowCenterCanonical.AroundMeters,
            0.0,
            _shadowCenterCanonical.AcrossMeters);
        around = (geometry.CanonicalToWorld(
            _shadowCenterCanonical.AroundMeters + 10.0,
            0.0,
            _shadowCenterCanonical.AcrossMeters) - center).Normalized();
        across = (geometry.CanonicalToWorld(
            _shadowCenterCanonical.AroundMeters,
            0.0,
            _shadowCenterCanonical.AcrossMeters + 10.0) - center).Normalized();
    }

    private void CancelCloudShadowBuild()
    {
        _shadowBuildGeneration++;
        if (_clouds?.HasMethod("clear_samples") == true)
            _clouds.Call("clear_samples");
        _shadowBuildPending = false;
        _shadowMapReady = false;
        _shadowSamplesRemaining = 0;
        _shadowDensitySums = null;
        _shadowDensitySampleCounts = null;
        _shadowPathKilometers = null;
        _pendingShadowMapData = null;
    }

    private void UpdateVisibility(bool force)
    {
        if (_clouds == null || _explorer == null || _weather == null) return;
        float altitude = Math.Max(0.0f, (float)_explorer.CanonicalPosition.VerticalMeters - ResolveSurfaceHeight(_explorer.CanonicalPosition));
        bool visible = Enabled
            && CloudRenderingEnabled
            && _weather.Quality != RingVolumetricCloudLayer.CloudQuality.Off
            && _explorer.ActualTravelSpeedMetersPerSecond < SuspendSpeedMetersPerSecond
            && altitude < AltitudeFadeEndMeters;
        RuntimeVisible = visible;
        // Sunshine keeps temporal color/data textures while its compositor is
        // disabled. Rebuild them before the first post-fast-travel frame so a
        // suspended or previously corrupted accumulation cannot be presented
        // again when the player stops.
        if (visible && !_lastVisible)
            RefreshCloudCompute();
        _clouds.Set("enabled", visible);
        if (force || visible != _lastVisible
            || HandoffStartMeters != _publishedHandoffStartMeters
            || HandoffEndMeters != _publishedHandoffEndMeters)
        {
            _weather.SetExternalLocalVolumetricCloudsState(
                // A disabled compositor cannot own local cloud presentation;
                // hand ownership to the curved shell during high-speed noclip.
                visible,
                visible,
                HandoffStartMeters,
                HandoffEndMeters);
            _publishedHandoffStartMeters = HandoffStartMeters;
            _publishedHandoffEndMeters = HandoffEndMeters;
            GD.Print($"Sunshine local clouds: {(visible ? "visible" : "far-shell fallback")}, altitude={altitude:0}m.");
        }
        _lastVisible = visible;
    }

	private float ResolveSurfaceHeight(in RingCanonicalPosition position)
    {
        if (_grid?.TerrainSampler == null) return 0;
        long around = (long)Math.Round(position.AroundMeters);
        int across = Math.Clamp(
            (int)Math.Round(position.AcrossMeters),
            0,
            (int)Math.Round(_grid.RingAcrossWidthMeters));
		RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
		return _grid.TerrainSampler.UsesRuntimeSurfaceGeneration || edits.UsesFloatSurface
			? edits.GetPresentationSurfaceHeight(_grid.TerrainSampler, around, across)
			: edits.GetSurfaceHeight(_grid.TerrainSampler, around, across);
    }
}
