using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingWaterFftController : Node
{
    public static RingWaterFftController? Instance { get; private set; }
    [Export] public bool Enabled { get; set; } = true;
    [Export(PropertyHint.Enum, "128,256,512,1024")] public int Resolution { get; set; } = 128;
    [Export(PropertyHint.Range, "1,4,1")] public int CascadeCount { get; set; } = 2;
    /// <summary>Target update rate for non-geometry FFT cascades.</summary>
    [Export(PropertyHint.Range, "5,120,1")] public float UpdatesPerSecond { get; set; } = 24;
    /// <summary>Target update rate for the primary visible geometry cascade.</summary>
    [Export(PropertyHint.Range, "5,120,1")] public float GeometryUpdatesPerSecond { get; set; } = 60;
    [Export] public bool AdaptiveUpdatesEnabled { get; set; } = true;
    [Export(PropertyHint.Range, "1,30,1")] public float MinimumUpdatesPerSecond { get; set; } = 5;
    [Export(PropertyHint.Range, "25,1000,1")] public float FullRateDistanceMeters { get; set; } = 200;
    [Export(PropertyHint.Range, ".01,16,.01")] public float FullRateAreaSquareKilometers { get; set; } = .25f;
    [Export(PropertyHint.Range, "0,5,.01")] public float IdleGraceSeconds { get; set; } = 1;
    [Export(PropertyHint.Range, "0,1,.01")] public float NormalStrength { get; set; } = .25f;
    [Export(PropertyHint.Range, "0,1,.01")] public float FoamStrength { get; set; } = .35f;
    [Export(PropertyHint.Range, "0,10,.1")] public float FoamAmount { get; set; } = 3;
    [Export(PropertyHint.Range, "0,2,.01")] public float Whitecap { get; set; } = .5f;
    [Export(PropertyHint.Range, ".1,60,.1")] public float WindSpeedMetersPerSecond { get; set; } = 6;
    [Export(PropertyHint.Range, "-180,180,1")] public float WindDirectionDegrees { get; set; }
    [Export] public bool FollowWeatherWind { get; set; } = true;
    [Export(PropertyHint.Range, "4,256,1")] public float SmallWaveLengthMeters { get; set; } = 12;
    [Export(PropertyHint.Range, "16,2048,1")] public float LargeWaveLengthMeters { get; set; } = 79;
    [Export(PropertyHint.Range, "25,4000,5")] public float DetailDistanceMeters { get; set; } = 1100;
    [Export(PropertyHint.Range, "0,10,.01")] public float GeometryHeightScale { get; set; } = 1;
    [Export(PropertyHint.Range, "0,3,.01")] public float GeometryChoppiness { get; set; } = .75f;
    [Export(PropertyHint.Range, "25,2000,5")] public float GeometryDistanceMeters { get; set; } = 1100;
    [Export(PropertyHint.Range, "1,8,.1")] public float GeometryFilterMeters { get; set; } = 2;
    [Export(PropertyHint.Range, "0,1,.01")] public float CrestLightingStrength { get; set; } = .35f;
    // The shoreline field suppresses only the low-frequency geometry which
    // would otherwise pull the ocean through terrain. Micro-normal detail is
    // deliberately retained until the water is at the contact line.
    [Export(PropertyHint.Range, "1,64,.1")] public float ShorelineFadeDistanceMeters { get; set; } = 10f;
    [Export(PropertyHint.Range, "1,64,.1")] public float ShorelineChopFadeDistanceMeters { get; set; } = 20f;
    [Export(PropertyHint.Range, ".1,20,.1")] public float ShorelineShallowDepthMeters { get; set; } = 3f;
    [Export(PropertyHint.Range, "5,500,1")] public float DeepWaterWaveDepthMeters { get; set; } = 100f;
    /// <summary>Production terrain-contact water pins safely until its shared field is valid.</summary>
    [Export] public bool RequireShorelineField { get; set; } = true;
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath WeatherSourcePath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath LocalWeatherEffectsPath { get; set; } = new("../LocalWeatherEffects");
    public string Status { get; private set; } = "Waiting for scene";
    public new bool Ready => Enabled && _provider?.Ready == true;
    public Texture2DArrayRD? NormalFoamTexture => Ready ? _provider?.NormalFoamTexture : null;
    public Texture2DArrayRD? DisplacementTexture => Ready ? _provider?.DisplacementTexture : null;
    public Vector2 Period0 { get; private set; } = new(12, 12);
    public Vector2 Period1 { get; private set; } = new(79, 79);
    public Vector2 Period2 { get; private set; } = new(316, 316);
    public Vector2 Period3 { get; private set; } = new(1264, 1264);
    public Vector2 Phase0 { get; private set; }
    public Vector2 Phase1 { get; private set; }
    public Vector2 Phase2 { get; private set; }
    public Vector2 Phase3 { get; private set; }
    public int ActiveCascadeCount { get; private set; }
    public float ActualWindSpeedMetersPerSecond => _wind.Length();
    public float ActualWindDirectionDegrees => Mathf.RadToDeg(Mathf.Atan2(_wind.Y, _wind.X));
    public long SpectrumConfigurationCount { get; private set; }
    public float StormResponseSeconds => global::RingworldTechDemo.Weather.RingWindSettings.Current.WaterResponseSeconds;
    public float ActualGeometryUpdatesPerSecond => WaterDemandStatus == "dormant" ? 0f : _provider?.PrimaryGeometryUpdatesPerSecond ?? 0f;
    public float ActualDetailUpdatesPerSecond => WaterDemandStatus == "dormant" ? 0f : _provider?.DetailUpdatesPerSecond ?? 0f;
    public int RelevantWaterSectorCount { get; private set; }
    public int RelevantWaterCellCount { get; private set; }
    public double RelevantWaterAreaSquareKilometers { get; private set; }
    public float NearestRelevantWaterDistanceMeters { get; private set; } = float.PositiveInfinity;
    public float TargetGeometryUpdatesPerSecond { get; private set; }
    public float TargetDetailUpdatesPerSecond { get; private set; }
    public string WaterDemandStatus { get; private set; } = "dormant";
    public double WaterDemandCheckMilliseconds { get; private set; }
    public long CompletedFftUpdates => _provider?.CompletedUpdates ?? 0;
    /// <summary>The topology currently published by the provider, retained while a new setting debounces.</summary>
    public int ActiveResolution { get; private set; } = 128;
    private VoxelChunkGridManager? _grid;
    private RingVoxelExplorerController? _explorer;
    private global::RingworldTechDemo.RingVolumetricCloudLayer? _weather;
    private global::RingworldTechDemo.RingLocalWeatherEffects? _localWeatherEffects;
    private RingWaterFftProvider? _provider;
    private RingWaterFftSettings _settings;
    private RingWaterFftSettings _configuredSettings;
    private readonly RingWaterWindResponse _windResponse = new();
    private Vector2 _wind;
    private Vector3 _canonicalAnchorWorld;
    private Vector3 _canonicalAroundAxis = Vector3.Forward;
    private Vector3 _canonicalAcrossAxis = Vector3.Up;
    private double _period0Around64 = 12;
    private double _period1Around64 = 79;
    private double _period2Around64 = 316;
    private double _period3Around64 = 1264;
    private double _fallbackClock;
    private bool _hasSettings;
    private bool _pending;
    private bool _wasEnabled;
    private double _stable;
    private readonly RingWaterFftUpdateScheduler _updateScheduler = new();
    private RingCanonicalPosition _surfaceQueryPosition;
    private float[]? _surfaceQueryOffsets;
    private float _surfaceQueryWaterBase;
    private double _surfaceQueryTime = double.NegativeInfinity;
    private double _lastSurfaceQueryRequest = double.NegativeInfinity;
    private int _surfaceQueryGeneration;
    private SurfaceQueryIdentity _surfaceQueryIdentity;
    private bool _hasSurfaceQueryIdentity;
    private ulong _shorelineSignatureRevision = ulong.MaxValue, _shorelineSignatureValue;
    private RingCanonicalPosition _shorelineSignatureCenter;
    private float _shorelineSignatureFade, _shorelineSignatureChop, _shorelineSignatureDepth, _shorelineSignatureDeepDepth;

    public override void _Ready()
    {
        Instance = this;
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _weather = GetNodeOrNull<global::RingworldTechDemo.RingVolumetricCloudLayer>(WeatherSourcePath);
        _localWeatherEffects = GetNodeOrNull<global::RingworldTechDemo.RingLocalWeatherEffects>(LocalWeatherEffectsPath);
        _wasEnabled = Enabled;
        if (!Enabled)
            Status = "Disabled: analytic water";
        else if (DisplayServer.GetName() == "headless")
            Status = "Headless: analytic water fallback";
        else
            CreateProvider();
    }
    public override void _Process(double delta)
    {
        // Scene replacement may overlap controller lifetimes for a frame. Only
        // the active owner may advance/publish shared water clocks and bindings.
        if (!ReferenceEquals(Instance, this)) return;
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Water update");
        _fallbackClock += double.IsFinite(delta) ? Math.Max(0, delta) : 0;
        double now = _weather?.CloudSimulationTimeSeconds ?? _fallbackClock;
        if (!double.IsFinite(now)) now = _fallbackClock;
        _wind = _windResponse.Step(ResolveWind(), now,
            global::RingworldTechDemo.Weather.RingWindSettings.Current.WaterResponseSeconds, FollowWeatherWind);
        RingWaterModel.UpdateWindClock(now, _wind, FollowWeatherWind);
        // Analytic, disabled and headless water share the same direction/clock.
        RingWaterModel.RefreshOceanMaterials();
        if (!Enabled) { if (_wasEnabled) { ClearSurfaceQueryCache(); _updateScheduler.Reset(); RingWaterModel.RefreshOceanMaterials(); _provider?.Dispose(); _provider = null; Status = "Disabled: analytic water"; } _wasEnabled = false; ActiveCascadeCount = 0; WaterDemandStatus = "dormant"; TargetGeometryUpdatesPerSecond = 0; TargetDetailUpdatesPerSecond = 0; return; }
        if (!_wasEnabled) { ClearSurfaceQueryCache(); _hasSettings = false; _pending = true; _stable = 0; _updateScheduler.Reset(); CreateProvider(); RingWaterModel.RefreshOceanMaterials(); } _wasEnabled = true;
        if (_provider == null || _grid == null || _explorer == null) return;
        UpdateCanonicalFrame();
        RingWaterFftSettings next = Settings(_wind);
        // Debounce artistic/topology edits, but let a continuously changing storm
        // mean reach the spectrum at a bounded cadence instead of starving it.
        if (!_hasSettings || ArtisticSettingsDifferent(next, _settings)
            || (!FollowWeatherWind && Different(next, _settings))) _stable = 0;
        else _stable += Math.Max(0, delta);
        if (!_hasSettings || Different(next, _configuredSettings)) _pending = true;
        _settings = next;
        _hasSettings = true;
        if (_pending && _stable >= .30 && (ArtisticSettingsDifferent(_settings, _configuredSettings)
            || _windResponse.SpectrumUpdateDue(now, FollowWeatherWind)))
        {
            bool needsInitialDispatch = ActiveCascadeCount == 0;
            bool topologyChanged = _provider.Configure(_settings, RingWaterModel.RetireFftTextureBindings);
            SpectrumConfigurationCount++;
            _pending = false;
            _configuredSettings = _settings;
            _windResponse.MarkSpectrumUpdated(now);
            ActiveCascadeCount = _settings.CascadeCount;
            ActiveResolution = _settings.Resolution;
            // Recompute from the requested across period in double precision.
            // Widening the published float X would lose seam closure on large rings.
            double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
            _period0Around64 = Tile(_settings.Period0.Y, circumference);
            _period1Around64 = Tile(_settings.Period1.Y, circumference);
            // Each derived cascade receives its own closed ring period.  Using
            // Period1 * N directly would drift at the circumference seam.
            _period2Around64 = Tile(_settings.Period1.Y * 4.0, circumference);
            _period3Around64 = Tile(_settings.Period1.Y * 16.0, circumference);
            Period0 = _settings.Period0;
            Period1 = _settings.Period1;
            Period2 = new((float)_period2Around64, _settings.Period1.Y * 4.0f);
            Period3 = new((float)_period3Around64, _settings.Period1.Y * 16.0f);
            Phase0 = Phase(Period0, _period0Around64);
            Phase1 = Phase(Period1, _period1Around64);
            Phase2 = Phase(Period2, _period2Around64);
            Phase3 = Phase(Period3, _period3Around64);
            if (topologyChanged || needsInitialDispatch) _updateScheduler.ForceUpdateAll();
        }
        RingWaterFftDemandSnapshot demand;
        ulong demandStarted = Time.GetTicksUsec();
        using (RingPerformanceTelemetry.MeasureCpu("Water demand check")) demand = CaptureWaterDemand();
        WaterDemandCheckMilliseconds = (Time.GetTicksUsec() - demandStarted) * .001;
        RelevantWaterSectorCount = demand.RelevantSectorCount;
        RelevantWaterCellCount = demand.RelevantCellCount;
        RelevantWaterAreaSquareKilometers = demand.WeightedAreaSquareMeters / 1_000_000.0;
        NearestRelevantWaterDistanceMeters = demand.NearestDistanceMeters;
        var schedule = _updateScheduler.Step(demand, new RingWaterFftUpdateSchedulerConfig(
            AdaptiveUpdatesEnabled, MinimumUpdatesPerSecond, FullRateDistanceMeters, FullRateAreaSquareKilometers,
            IdleGraceSeconds, GeometryUpdatesPerSecond, UpdatesPerSecond, ActiveCascadeCount), delta);
        TargetGeometryUpdatesPerSecond = schedule.GeometryUpdatesPerSecond;
        TargetDetailUpdatesPerSecond = schedule.DetailUpdatesPerSecond;
        bool enteredDormant = schedule.Status == "dormant" && WaterDemandStatus != "dormant";
        WaterDemandStatus = schedule.Status;
        if (schedule.Resume || enteredDormant) ClearSurfaceQueryCache();
        if (ActiveCascadeCount > 0 && schedule.DueMask != 0) _provider.RequestUpdate(now, schedule.DueMask);
        Status = _provider.Status; RingWaterModel.RefreshOceanMaterials();
        if (WaterDemandStatus != "dormant") RequestSurfaceQuery();
    }
    public override void _ExitTree() { if (ReferenceEquals(Instance, this)) { Instance = null; RingWaterModel.ClearWindClock(); } ClearSurfaceQueryCache(); RingWaterModel.RefreshOceanMaterials(); _provider?.Dispose(); _provider = null; ActiveCascadeCount = 0; }
    private void CreateProvider() { if (DisplayServer.GetName() == "headless") { Status = "Headless: analytic water fallback"; return; } _provider ??= new RingWaterFftProvider(); Status = "Preparing FFT water"; }
    private RingWaterFftSettings Settings(Vector2 wind)
    {
        double circumference = _grid!.GetCanonicalRingCircumferenceMeters64(); Vector2 period0 = new((float)Tile(SmallWaveLengthMeters, circumference), SmallWaveLengthMeters); Vector2 period1 = new((float)Tile(LargeWaveLengthMeters, circumference), LargeWaveLengthMeters);
        Vector2 period2 = new((float)Tile(LargeWaveLengthMeters * 4.0, circumference), LargeWaveLengthMeters * 4.0f);
        Vector2 period3 = new((float)Tile(LargeWaveLengthMeters * 16.0, circumference), LargeWaveLengthMeters * 16.0f);
        return new(Resolution is 128 or 256 or 512 or 1024 ? Resolution : 128, Mathf.Clamp(CascadeCount, 1, 4), 12017, period0, period1, Mathf.Clamp(wind.Length(), .1f, 60), Mathf.RadToDeg(Mathf.Atan2(wind.Y, wind.X)), Mathf.Clamp(FoamAmount, 0, 10), Mathf.Clamp(Whitecap, 0, 2), period2, period3);
    }
    private RingWaterFftDemandSnapshot CaptureWaterDemand()
    {
        if (_grid is null || _explorer is null) return new(0, 0, 0.0, float.PositiveInfinity);
        float waveMargin = Math.Max(2.0f, GeometryHeightScale * Math.Max(1, CascadeCount) * 3.0f + 2.0f);
        float demandRange = Mathf.Clamp(DetailDistanceMeters, 25.0f, 4000.0f);
        RingCanonicalPosition explorerPosition = _explorer.CanonicalPosition;
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera is null || !GodotObject.IsInstanceValid(camera))
            return RingTerrainProxySector.CaptureFftWaterDemand(explorerPosition, demandRange, waveMargin);
        RingCanonicalPosition cameraPosition = _grid.WorldToCanonicalRingPosition64(camera.GlobalPosition);
        RingWaterFftDemandSnapshot cameraDemand = RingTerrainProxySector.CaptureFftWaterDemand(cameraPosition, demandRange, waveMargin);
        if (camera.GlobalPosition.DistanceTo(_explorer.GlobalPosition) <= 8.0f) return cameraDemand;

        // The provider only exposes single-centre approximate unique footprints.
        // For detached cameras, maximum fields form a conservative lower-bound
        // union estimate without falsely double-counting overlapping footprints.
        RingWaterFftDemandSnapshot explorerDemand = RingTerrainProxySector.CaptureFftWaterDemand(explorerPosition, demandRange, waveMargin);
        return new(
            Math.Max(cameraDemand.RelevantSectorCount, explorerDemand.RelevantSectorCount),
            Math.Max(cameraDemand.RelevantCellCount, explorerDemand.RelevantCellCount),
            Math.Max(cameraDemand.WeightedAreaSquareMeters, explorerDemand.WeightedAreaSquareMeters),
            Math.Min(cameraDemand.NearestDistanceMeters, explorerDemand.NearestDistanceMeters));
    }
    private static bool ArtisticSettingsDifferent(in RingWaterFftSettings a, in RingWaterFftSettings b)
        => a.Resolution != b.Resolution || a.CascadeCount != b.CascadeCount
            || a.Period0 != b.Period0 || a.Period1 != b.Period1
            || !Mathf.IsEqualApprox(a.FoamAmount, b.FoamAmount)
            || !Mathf.IsEqualApprox(a.Whitecap, b.Whitecap);

    private bool Different(in RingWaterFftSettings a, in RingWaterFftSettings b)
    {
        if (a.Resolution != b.Resolution
            || a.CascadeCount != b.CascadeCount
            || a.Period0 != b.Period0
            || a.Period1 != b.Period1
            || !Mathf.IsEqualApprox(a.FoamAmount, b.FoamAmount)
            || !Mathf.IsEqualApprox(a.Whitecap, b.Whitecap))
            return true;

        if (!FollowWeatherWind)
            return !Mathf.IsEqualApprox(a.WindSpeedMetersPerSecond, b.WindSpeedMetersPerSecond)
                || !Mathf.IsEqualApprox(a.WindDirectionDegrees, b.WindDirectionDegrees);

        return Math.Abs(a.WindSpeedMetersPerSecond - b.WindSpeedMetersPerSecond) > .35f
            || RingWaterWindResponse.DirectionDifferenceDegrees(a.WindDirectionDegrees, b.WindDirectionDegrees) > 3;
    }
    private Vector2 ResolveWind()
    {
        float radians = Mathf.DegToRad(WindDirectionDegrees);
        Vector2 manual = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)) * WindSpeedMetersPerSecond;
        if (!FollowWeatherWind) return manual;
        // This observer snapshot includes CLP/external weather overrides and is
        // the same resolved source consumed by foliage, rain, and radar.
        if (_localWeatherEffects != null && GodotObject.IsInstanceValid(_localWeatherEffects)
            && _localWeatherEffects.HasCurrentWindSnapshot)
        {
            var snapshot = _localWeatherEffects.CurrentWindSnapshot;
            if (float.IsFinite(snapshot.MeanWind.X) && float.IsFinite(snapshot.MeanWind.Y)) return snapshot.MeanWind;
        }
        if (_weather != null && GodotObject.IsInstanceValid(_weather) && _explorer != null)
            return _weather.QueryWindSnapshot(_explorer.CanonicalPosition).MeanWind;
        return manual;
    }
    public Vector3 CanonicalAnchorWorld => _canonicalAnchorWorld;
    public Vector3 CanonicalAroundAxis => _canonicalAroundAxis;
    public Vector3 CanonicalAcrossAxis => _canonicalAcrossAxis;
    private readonly Dictionary<string, long> _surfaceQueryRejections = new();
    private readonly Dictionary<string, long> _surfaceCallbackRejections = new();
    private static long _surfaceQuerySerialSequence;
    private long _surfaceQuerySampleSerial;
    public long SurfaceQuerySampleSerial => _surfaceQuerySampleSerial;
    public double SurfaceQuerySampleTimeSeconds => _surfaceQueryTime;
    private long _acceptedSurfaceQueries, _rejectedSurfaceCallbacks, _rejectedSurfaceRequests, _diagnosticSpectrumBaseline;
    private string _lastSurfaceQueryStatus = "not_cached", _lastSurfaceCallbackStatus = "none";
    public void ResetSurfaceQueryDiagnostics()
    {
        _surfaceQueryRejections.Clear();
        _surfaceCallbackRejections.Clear();
        _acceptedSurfaceQueries = _rejectedSurfaceCallbacks = _rejectedSurfaceRequests = 0;
        _diagnosticSpectrumBaseline = SpectrumConfigurationCount;
    }
    public Godot.Collections.Dictionary GetSurfaceQueryDiagnostics()
    {
        var rejections = new Godot.Collections.Dictionary();
        foreach (var pair in _surfaceQueryRejections) rejections[pair.Key] = pair.Value;
        var callbackRejections = new Godot.Collections.Dictionary();
        foreach (var pair in _surfaceCallbackRejections) callbackRejections[pair.Key] = pair.Value;
        string terrainIdentity = _explorer is null ? "unavailable"
            : (CaptureSurfaceQueryIdentity(_explorer.CanonicalPosition) with { ConfigurationVersion = 0 }).ToString();
        return new()
        {
            ["geometry_shoreline_identity"] = terrainIdentity,
            ["last_status"] = _lastSurfaceQueryStatus,
            ["last_callback_status"] = _lastSurfaceCallbackStatus,
            ["rejections"] = rejections,
            ["callback_rejections"] = callbackRejections,
            ["accepted_callbacks"] = _acceptedSurfaceQueries,
            ["rejected_callbacks"] = _rejectedSurfaceCallbacks,
            ["rejected_requests"] = _rejectedSurfaceRequests,
            ["cache_age_seconds"] = double.IsFinite(_surfaceQueryTime) ? Time.GetTicksMsec() * .001 - _surfaceQueryTime : -1,
            ["provider_generation"] = _provider?.Generation ?? -1,
            ["provider_configuration"] = _provider?.ConfigurationVersion ?? -1,
            ["cached_configuration"] = _hasSurfaceQueryIdentity ? _surfaceQueryIdentity.ConfigurationVersion : -1,
            ["spectrum_configurations"] = SpectrumConfigurationCount,
            ["spectrum_configurations_since_reset"] = SpectrumConfigurationCount - _diagnosticSpectrumBaseline
        };
    }
    private bool RejectSurfaceQuery(string reason)
    {
        _lastSurfaceQueryStatus = reason;
        _surfaceQueryRejections.TryGetValue(reason, out long count);
        _surfaceQueryRejections[reason] = count + 1;
        return false;
    }
    private void RejectSurfaceCallback(string reason)
    {
        _lastSurfaceCallbackStatus = reason;
        _rejectedSurfaceCallbacks++;
        _surfaceCallbackRejections.TryGetValue(reason, out long count);
        _surfaceCallbackRejections[reason] = count + 1;
    }
    private static string SurfaceIdentityDifference(in SurfaceQueryIdentity a, in SurfaceQueryIdentity b)
    {
        if (a.Generation != b.Generation) return "generation_changed";
        if (a.ConfigurationVersion != b.ConfigurationVersion) return "configuration_changed";
        if (a.Resolution != b.Resolution || a.CascadeCount != b.CascadeCount || a.Cascade != b.Cascade) return "topology_changed";
        if (a.Period != b.Period || a.Lod != b.Lod) return "period_or_lod_changed";
        if (a.HeightScale != b.HeightScale || a.Choppiness != b.Choppiness || a.FilterMeters != b.FilterMeters) return "geometry_controls_changed";
        if (a.GeometryDistanceMeters != b.GeometryDistanceMeters) return "geometry_distance_changed";
        if (a.RequireShorelineField != b.RequireShorelineField) return "shoreline_mode_changed";
        if (a.ShorelineFootprintSignature != b.ShorelineFootprintSignature) return "shoreline_footprint_changed";
        return "shoreline_controls_changed";
    }

    /// <summary>
    /// Returns an FFT-only delta above gameplay's existing ocean base height.
    /// It intentionally returns false until the bounded GPU point-query cache has
    /// a fresh result for this coordinate; callers must retain their normal base
    /// water height rather than treating false as an offset of zero.
    /// </summary>
    public bool TryGetOceanSurfaceOffset(in RingCanonicalPosition position, out float offsetMeters)
    {
        offsetMeters = 0;
        if (!RingTerrainProxySector.IsFftOceanGeometryAvailableAt(position)) return RejectSurfaceQuery("geometry_unavailable");
        if (!Ready) return RejectSurfaceQuery("provider_not_ready");
        if (_surfaceQueryOffsets is null || !_hasSurfaceQueryIdentity) return RejectSurfaceQuery("not_cached");
        if (_surfaceQueryGeneration != _provider?.Generation) return RejectSurfaceQuery("generation_changed");
        SurfaceQueryIdentity currentIdentity = CaptureSurfaceQueryIdentity(_surfaceQueryPosition);
        if (_surfaceQueryIdentity.ConfigurationVersion != currentIdentity.ConfigurationVersion) return RejectSurfaceQuery("configuration_changed");
        if (_surfaceQueryIdentity != currentIdentity) return RejectSurfaceQuery(SurfaceIdentityDifference(_surfaceQueryIdentity, currentIdentity));
        if (Time.GetTicksMsec() * .001 - _surfaceQueryTime > .35) return RejectSurfaceQuery("expired");
        double signedAround = position.AroundMeters - _surfaceQueryPosition.AroundMeters;
        double circumference = _grid?.GetCanonicalRingCircumferenceMeters64() ?? 0;
        if (circumference > 0) signedAround = Modulo(signedAround + circumference * .5, circumference) - circumference * .5;
        double da = Math.Abs(signedAround);
        double dx = position.AcrossMeters - _surfaceQueryPosition.AcrossMeters;
        if (!TryGetOceanBaseSurface(position, out float currentWaterBase)) return RejectSurfaceQuery("invalid_water_base");
        float cachedVerticalToBody = (float)_surfaceQueryPosition.VerticalMeters - _surfaceQueryWaterBase;
        float currentVerticalToBody = (float)position.VerticalMeters - currentWaterBase;
        // The 3x3 cache has a one-metre horizontal footprint. Within the
        // fully opaque part of the geometry fade, a one-metre vertical move
        // cannot change the sampled displacement. Near its transition, retain
        // a tight vertical band so swimming never reuses a visibly wrong fade.
        float fullFadeRadius = _surfaceQueryIdentity.GeometryDistanceMeters * .65f - 2.0f;
        bool fullyFaded = fullFadeRadius > 0.0f
            && Math.Abs(cachedVerticalToBody) <= fullFadeRadius
            && Math.Sqrt(currentVerticalToBody * currentVerticalToBody + da * da + dx * dx) <= fullFadeRadius;
        float verticalLimit = fullyFaded ? 1.0f : .05f;
        if (Math.Abs(currentVerticalToBody - cachedVerticalToBody) > verticalLimit) return RejectSurfaceQuery("vertical_footprint");
        if (da > 1 || Math.Abs(dx) > 1) return RejectSurfaceQuery("horizontal_footprint");
        float fx = Mathf.Clamp((float)signedAround + 1, 0, 2), fy = Mathf.Clamp((float)dx + 1, 0, 2);
        int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy), x1 = Math.Min(2, x0 + 1), y1 = Math.Min(2, y0 + 1);
        float tx = fx - x0, ty = fy - y0;
        float a = Mathf.Lerp(_surfaceQueryOffsets[y0 * 3 + x0], _surfaceQueryOffsets[y0 * 3 + x1], tx);
        float b = Mathf.Lerp(_surfaceQueryOffsets[y1 * 3 + x0], _surfaceQueryOffsets[y1 * 3 + x1], tx);
        offsetMeters = Mathf.Lerp(a, b, ty);
        if (!float.IsFinite(offsetMeters)) return RejectSurfaceQuery("nonfinite");
        _lastSurfaceQueryStatus = "ready";
        return true;
    }
    /// <summary>Shared geometry range, clamped so vertex displacement always has matching FFT shading.</summary>
    public float EffectiveGeometryDistanceMeters
    {
        get
        {
            float requested = Mathf.Min(Mathf.Clamp(GeometryDistanceMeters, 25, 2000), Mathf.Max(25, Mathf.Clamp(DetailDistanceMeters, 25, 4000)));
            float safeDistance = RingTerrainProxySector.FinestRuntimeSafeFftGeometryDistanceMeters;
            return safeDistance <= 0.0f ? requested : Mathf.Min(requested, safeDistance);
        }
    }
    private void RequestSurfaceQuery()
    {
        if (!Ready || _provider is null || _explorer is null || _grid is null) return;
        RingCanonicalPosition p = _explorer.CanonicalPosition;
        if (!RingTerrainProxySector.IsFftOceanGeometryAvailableAt(p)) return;
        int cascade = ActiveCascadeCount >= 2 ? 1 : 0;
        double requestTime = Time.GetTicksMsec() * .001;
        if (requestTime - _lastSurfaceQueryRequest < 1.0 / 25.0) return;
        Vector2[] periods = { Period0, Period1, Period2, Period3 };
        double[] aroundPeriods = { _period0Around64, _period1Around64, _period2Around64, _period3Around64 };
        Vector2[] uvs = new Vector2[4]; float[] lods = new float[4];
        for (int layer = 0; layer < 4; ++layer) { uvs[layer] = new((float)(Modulo(p.AroundMeters, aroundPeriods[layer]) / aroundPeriods[layer]), (float)(Modulo(p.AcrossMeters, periods[layer].Y) / periods[layer].Y)); float layerTexel = Math.Max(periods[layer].X, periods[layer].Y) / Math.Max(1, ActiveResolution); lods[layer] = Mathf.Clamp(Mathf.Log(Mathf.Max(GeometryFilterMeters, layerTexel) / layerTexel) / Mathf.Log(2), 0, (int)Math.Log2(ActiveResolution)); }
        Vector2 period = periods[cascade];
        Vector2 uv = uvs[cascade];
        float texel = Math.Max(period.X, period.Y) / Math.Max(1, ActiveResolution);
        float lod = Mathf.Clamp(Mathf.Log(Mathf.Max(GeometryFilterMeters, texel) / texel) / Mathf.Log(2), 0, (int)Math.Log2(ActiveResolution));
        int generation = _provider.Generation;
        int configurationVersion = _provider.ConfigurationVersion;
        float heightScale = GeometryHeightScale, choppiness = GeometryChoppiness, filter = GeometryFilterMeters;
        if (!TryGetOceanBaseSurface(p, out float waterBase)) return;
        float verticalToBody = (float)p.VerticalMeters - waterBase;
        // Gameplay only needs a new GPU point query close enough to meet the
        // rendered surface. This avoids a per-frame readback while flying.
        float queryBand = Math.Max(12.0f, GeometryHeightScale * Math.Max(1, ActiveCascadeCount) * 3.0f + 2.0f);
        if (Math.Abs(verticalToBody) > queryBand) return;
        float geometryDistance = EffectiveGeometryDistanceMeters;
        if (geometryDistance <= .001f) return;
        SurfaceQueryIdentity identity = CaptureSurfaceQueryIdentity(p);
        RingWaterFftProvider requestProvider = _provider;
        _lastSurfaceQueryRequest = requestTime;
        int geometryMask = ActiveCascadeCount <= 1 ? 1 : ((1 << ActiveCascadeCount) - 2);
        bool shorelineValid = RingTerrainProxySector.SharedFftShorelineFieldValid
            && RingTerrainProxySector.SharedFftShorelineFieldTexture is not null;
        int shorelineMode = !RequireShorelineField ? 0 : shorelineValid ? 2 : 1;
        const float shorelineSpan = 512f * RingTerrainProxySector.SharedFftShorelineFieldCellMeters;
        // The material center is camera-frame relative, whereas this query
        // position is absolute canonical. Terrain owns the wrapped conversion
        // so a billion-metre around coordinate cannot pin swimming out of atlas.
        bool shorelineUvAvailable = RingTerrainProxySector.TryGetFftShorelineFieldUv(p, out Vector2 shorelineUvOrigin);
        if (shorelineMode == 2 && !shorelineUvAvailable) shorelineMode = 1;
        if (!_provider.RequestSurfaceOffsets(uv, cascade, lod, period, heightScale, choppiness, geometryDistance, verticalToBody, result =>
        {
            if (!Ready || !ReferenceEquals(_provider, requestProvider) || _provider?.Generation != generation)
            { RejectSurfaceCallback("owner_or_generation_changed"); return; }
            if (_provider?.ConfigurationVersion != configurationVersion)
            { RejectSurfaceCallback("configuration_changed"); return; }
            if (result is null) { RejectSurfaceCallback("provider_null"); return; }
            SurfaceQueryIdentity callbackIdentity = CaptureSurfaceQueryIdentity(p);
            if (identity != callbackIdentity)
            { RejectSurfaceCallback(SurfaceIdentityDifference(identity, callbackIdentity)); return; }
            if (!Mathf.IsEqualApprox(heightScale, GeometryHeightScale)
                || !Mathf.IsEqualApprox(choppiness, GeometryChoppiness)
                || !Mathf.IsEqualApprox(filter, GeometryFilterMeters))
            { RejectSurfaceCallback("geometry_controls_changed"); return; }
            if (!ValidSurfaceQuery(result.Value)) { RejectSurfaceCallback("invalid_residual"); return; }
            _acceptedSurfaceQueries++;
            _surfaceQuerySampleSerial = System.Threading.Interlocked.Increment(ref _surfaceQuerySerialSequence);
            _lastSurfaceCallbackStatus = "accepted";
            _surfaceQueryPosition = p; _surfaceQueryWaterBase = waterBase; _surfaceQueryOffsets = result.Value.Heights; _surfaceQueryTime = requestTime; _surfaceQueryGeneration = generation; _surfaceQueryIdentity = identity; _hasSurfaceQueryIdentity = true;
        }, uvs, periods, lods, geometryMask, RingTerrainProxySector.SharedFftShorelineFieldTexture,
            shorelineUvOrigin, 1f / shorelineSpan,
            Mathf.Clamp(ShorelineFadeDistanceMeters, 1f, 64f),
            Mathf.Clamp(ShorelineChopFadeDistanceMeters, 1f, 64f),
            Mathf.Clamp(ShorelineShallowDepthMeters, .1f, 20f),
            Mathf.Clamp(DeepWaterWaveDepthMeters, 5f, 500f), shorelineMode)) { _rejectedSurfaceRequests++; return; }
    }
    private bool TryGetOceanBaseSurface(in RingCanonicalPosition position, out float waterBase)
    {
        waterBase = 0;
        if (_grid?.TerrainSampler is null) return false;
        long around = (long)Math.Round(position.AroundMeters);
        int across = Mathf.Clamp(Mathf.RoundToInt((float)position.AcrossMeters), 0, Mathf.Max(0, Mathf.RoundToInt(_grid.RingAcrossWidthMeters)));
        TerrainSample sample = _grid.TerrainSampler.GetTerrainSample(around, across);
        if (sample.WaterKind != SurfaceWaterKind.Ocean || !float.IsFinite(sample.WaterSurfaceHeight)) return false;
        waterBase = sample.WaterSurfaceHeight + 1.0f;
        return float.IsFinite(waterBase);
    }
    private SurfaceQueryIdentity CaptureSurfaceQueryIdentity(in RingCanonicalPosition shorelinePosition)
    {
        int providerConfiguration = _provider?.ConfigurationVersion ?? -1;
        int providerGeneration = _provider?.Generation ?? -1;
        int cascade = ActiveCascadeCount >= 2 ? 1 : 0;
        Vector2 period = cascade == 0 ? Period0 : Period1;
        float texel = Math.Max(period.X, period.Y) / Math.Max(1, ActiveResolution);
        float lod = Mathf.Clamp(Mathf.Log(Mathf.Max(GeometryFilterMeters, texel) / texel) / Mathf.Log(2), 0, (int)Math.Log2(ActiveResolution));
        return new(providerGeneration, providerConfiguration, ActiveResolution, ActiveCascadeCount, cascade, period, lod,
            GeometryHeightScale, GeometryChoppiness, GeometryFilterMeters, EffectiveGeometryDistanceMeters,
            RequireShorelineField, CaptureShorelineFootprintSignature(shorelinePosition),
            ShorelineFadeDistanceMeters, ShorelineChopFadeDistanceMeters, ShorelineShallowDepthMeters, DeepWaterWaveDepthMeters);
    }
    // The GPU rejects inversion paths beyond 190m. A 192m lattice footprint
    // includes that path plus rounding/filtering support. Revalidate after a field
    // publication, then memoize it for regular swimming cache reads. This
    // avoids accepting a global update unless every possibly sampled damping
    // factor is unchanged, while distant refreshes over saturated deep water
    // no longer churn the cache every frame.
    private ulong CaptureShorelineFootprintSignature(in RingCanonicalPosition center)
    {
        if (!RequireShorelineField) return 0;
        const int stride = (int)RingTerrainProxySector.SharedFftShorelineFieldCellMeters;
        // The atlas lattice is two metres; quantizing avoids rehashing the
        // same conservative region while the swimmer moves inside one cell.
        RingCanonicalPosition latticeCenter = center with
        {
            AroundMeters = Math.Round(center.AroundMeters / stride) * stride,
            AcrossMeters = Math.Round(center.AcrossMeters / stride) * stride,
            VerticalMeters = 0
        };
        ulong revision = RingTerrainProxySector.SharedFftShorelineFieldRevision;
        if (revision == _shorelineSignatureRevision && _shorelineSignatureCenter == latticeCenter
            && Mathf.IsEqualApprox(_shorelineSignatureFade, ShorelineFadeDistanceMeters)
            && Mathf.IsEqualApprox(_shorelineSignatureChop, ShorelineChopFadeDistanceMeters)
            && Mathf.IsEqualApprox(_shorelineSignatureDepth, ShorelineShallowDepthMeters)
            && Mathf.IsEqualApprox(_shorelineSignatureDeepDepth, DeepWaterWaveDepthMeters)) return _shorelineSignatureValue;
        const int radius = 192;
        bool fullySaturated = RingTerrainProxySector.IsFftShorelineFootprintFullySaturated(
            latticeCenter, radius, DeepWaterWaveDepthMeters,
            Mathf.Max(ShorelineFadeDistanceMeters, ShorelineChopFadeDistanceMeters));
        _shorelineSignatureRevision = revision; _shorelineSignatureCenter = latticeCenter;
        _shorelineSignatureFade = ShorelineFadeDistanceMeters; _shorelineSignatureChop = ShorelineChopFadeDistanceMeters; _shorelineSignatureDepth = ShorelineShallowDepthMeters; _shorelineSignatureDeepDepth = DeepWaterWaveDepthMeters;
        // Only unity factors commute with bilinear interpolation. A mixed
        // footprint carries the publication revision so edits cannot hide in
        // a texel whose endpoint itself happened to be saturated.
        _shorelineSignatureValue = fullySaturated ? 1UL : (revision | (1UL << 63));
        return _shorelineSignatureValue;
    }
    private static bool ValidSurfaceQuery(in RingWaterFftSurfaceQueryResult result)
    {
        if (result.Heights is null || result.ResidualMeters is null || result.Heights.Length != 9 || result.ResidualMeters.Length != 9) return false;
        for (int i = 0; i < 9; ++i)
            if (!float.IsFinite(result.Heights[i]) || !float.IsFinite(result.ResidualMeters[i]) || Math.Abs(result.ResidualMeters[i]) > .03f) return false;
        return true;
    }
    private void ClearSurfaceQueryCache()
    {
        _surfaceQueryOffsets = null;
        _surfaceQueryTime = double.NegativeInfinity;
        _lastSurfaceQueryRequest = double.NegativeInfinity;
        _surfaceQueryGeneration = -1;
        _surfaceQueryWaterBase = 0.0f;
        _hasSurfaceQueryIdentity = false;
    }
    private void UpdateCanonicalFrame()
    {
        RingCanonicalPosition canonical = _explorer!.CanonicalPosition;
        _canonicalAnchorWorld = _grid!.CanonicalRingPositionToWorld(canonical with { VerticalMeters = 0 });
        _canonicalAroundAxis = Axis(canonical with { AroundMeters = canonical.AroundMeters + 1, VerticalMeters = 0 }, Vector3.Forward);
        _canonicalAcrossAxis = Axis(canonical with { AcrossMeters = canonical.AcrossMeters + 1, VerticalMeters = 0 }, Vector3.Up);
        Phase0 = Phase(Period0, _period0Around64);
        Phase1 = Phase(Period1, _period1Around64);
        Phase2 = Phase(Period2, _period2Around64);
        Phase3 = Phase(Period3, _period3Around64);
    }
    private Vector3 Axis(RingCanonicalPosition offset, Vector3 fallback) { Vector3 axis = _grid!.CanonicalRingPositionToWorld(offset) - _canonicalAnchorWorld; return axis.LengthSquared() > .000001f ? axis.Normalized() : fallback; }
    private Vector2 Phase(Vector2 period, double aroundPeriod) { if (_explorer == null) return Vector2.Zero; return new((float)(Modulo(_explorer.CanonicalPosition.AroundMeters, aroundPeriod)/aroundPeriod),(float)(Modulo(_explorer.CanonicalPosition.AcrossMeters, period.Y)/period.Y)); }
    public static double Tile(double requested, double circumference) => !double.IsFinite(requested) || requested <= 0 ? 1 : !double.IsFinite(circumference) || circumference <= requested ? requested : circumference / Math.Max(1, Math.Round(circumference / requested));
    public static double Modulo(double value, double period) { if (!double.IsFinite(value) || !double.IsFinite(period) || period <= 0) return 0; double result = value % period; return result < 0 ? result + period : result; }
    public static double PeriodicLength(double requestedMeters, double circumferenceMeters) => Tile(requestedMeters, circumferenceMeters);
    public static double PositiveModulo(double value, double period) => Modulo(value, period);
    private readonly record struct SurfaceQueryIdentity(
        int Generation, int ConfigurationVersion, int Resolution, int CascadeCount, int Cascade, Vector2 Period,
        float Lod, float HeightScale, float Choppiness, float FilterMeters, float GeometryDistanceMeters,
        bool RequireShorelineField, ulong ShorelineFootprintSignature, float ShorelineFadeDistance, float ShorelineChopFadeDistance, float ShorelineShallowDepth, float DeepWaterWaveDepth);
}
