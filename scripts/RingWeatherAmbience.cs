using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Weather;
using RingworldTechDemo.Terrain.Editable;

namespace RingworldTechDemo;

/// <summary>
/// Weather ambience as an enveloping sound field instead of point sources. Rain, wind and the distant storm play from four
/// ring-fixed directional speakers that follow the listener (direction carries the cue, distance carries none); a stereo
/// bed fills the space; a roof voice pair covers cover overhead. What each speaker plays comes from sampled rain exposure
/// (<see cref="IRainExposureProvider"/>), not from fixed world positions. Thunder stays independent in <see cref="RingWeatherAudio"/>.
/// </summary>
public partial class RingWeatherAmbience : Node
{
    public const string DuckMetadata = "ring_weather_ambience_duck";
    // _Process sets this every frame; SetMeta takes a StringName, so convert the name once.
    private static readonly StringName DuckMetadataName = DuckMetadata;
    private const int ProbeCount = 16;
    private const ulong ProbeIntervalUsec = 125_000;
    private const double ProbeLifetimeSeconds = 3.0;
    private const double ExposureProbeIntervalSeconds = .025;
    // Natural cover must start this far above the listener's eyes: generated terrain heights can sit about 2 m above the collision ground the player stands on.
    private const float NaturalCoverClearanceMeters = 1.5f;
    // The map provider is dropped for the terrain provider only after the map stayed unavailable this long (a late readback must not flap the source).
    private const double MapLossGraceSeconds = 1.0;
    private const double SilentStopSeconds = 3.0;
    private const float AudibleGain = .001f;
    private const float FadedGain = .00001f;
    private const float FieldRadiusMeters = 8f;
    private const float RoofHeightMeters = 3f;
    private const double GoldenRatio = .6180339887498949;
    // Level calibration of each layer before the user volumes (listening items).
    private const float BedLevel = .6f, FieldLevel = .45f, RoofLevel = .4f, WindLevel = .5f, DistantLevel = .5f;
    private static readonly float[] SectorPitch = { .97f, .99f, 1.01f, 1.03f };
    private static readonly string[] MaterialNames = { "Soil", "Foliage", "Hard", "Water", "Metal" };
    // Per-kind level of the near one-shots in dB (Plip, LeafDrip, Tick, RoofPlink, CaveDrip, EavesDribble): listening calibration, not derived.
    // FatPlip and HailHit (storm arrival) follow the original six.
    private static readonly float[] KindLevelDb = { -6f, -8f, -8f, -4f, -2f, -6f, -3f, -5f };
    private static readonly string[] ShotFolders = { "plip", "leaf_drip", "hard_tick", "roof_plink", "cave_drip", "eaves_dribble", "plip", "hail_hit" };
    private const string WeatherAudioRoot = "res://resources/audio/weather/";
    // Storm arrival layers (S3 WO-C3): level calibration before the user volumes, and the gust roar's pitch (the wind loop pitched down).
    private const float GustRoarLevel = .8f, HailLevel = .6f, SkitterLevel = .5f, GustRoarPitch = .6f, FatPlipPitch = .7f;
    private const string ShotLibraryRoot = "res://resources/audio/weather/shots/";
    private const int ShotLibraryMax = 16;
    private const float ShotMaxDistanceMeters = 20f;
    private static readonly string[] SectorNames = { "AroundPos", "AcrossPos", "AroundNeg", "AcrossNeg" };

    [Export] public bool Enabled { get; set; } = true;
    [Export(PropertyHint.Range, "-48,6,0.5")] public float RainVolumeDb { get; set; } = -10;
    [Export(PropertyHint.Range, "-48,6,0.5")] public float WindVolumeDb { get; set; } = -13;
    [Export(PropertyHint.Range, "-48,6,0.5")] public float DistantStormVolumeDb { get; set; } = -18;
    /// <summary>How strongly the directional speakers pan against the camera (0.2 narrow, 1 wide).</summary>
    [Export(PropertyHint.Range, "0.2,1,0.05")] public float SpatialWidth { get; set; } = .6f;
    /// <summary>Level of the near rain one-shots (plips, drips, ticks, roof plinks) on top of the rain volume.</summary>
    [Export(PropertyHint.Range, "-24,6,0.5")] public float NearDetailVolumeDb { get; set; } = 0;
    /// <summary>Stereo rain bed, light and heavy.</summary>
    [Export] public AudioStream? RainLightStream { get; set; }
    [Export] public AudioStream? RainHeavyStream { get; set; }
    [Export] public AudioStream? RainSoilStream { get; set; }
    /// <summary>Optional canopy texture; foliage columns play soil when this is empty.</summary>
    [Export] public AudioStream? RainFoliageStream { get; set; }
    [Export] public AudioStream? RainHardStream { get; set; }
    [Export] public AudioStream? RainWaterStream { get; set; }
    /// <summary>Optional sheet-metal texture for POI, building and roof columns; those columns play the hard stream when this is empty.</summary>
    [Export] public AudioStream? RainMetalStream { get; set; }
    /// <summary>Optional interior roof drum played by the roof voices; they play the hard stream when this is empty.</summary>
    [Export] public AudioStream? RainRoofStream { get; set; }
    /// <summary>Near one-shot libraries, indexed by <see cref="RainShotKind"/>. An empty array is filled by convention from resources/audio/weather/shots/.</summary>
    [Export] public AudioStream[] ShotPlips { get; set; } = Array.Empty<AudioStream>();
    [Export] public AudioStream[] ShotLeafDrips { get; set; } = Array.Empty<AudioStream>();
    [Export] public AudioStream[] ShotTicks { get; set; } = Array.Empty<AudioStream>();
    [Export] public AudioStream[] ShotRoofPlinks { get; set; } = Array.Empty<AudioStream>();
    [Export] public AudioStream[] ShotCaveDrips { get; set; } = Array.Empty<AudioStream>();
    [Export] public AudioStream[] ShotEavesDribbles { get; set; } = Array.Empty<AudioStream>();
    /// <summary>Hail impact one-shots (kind HailHit); fat-drop plips (kind FatPlip) reuse <see cref="ShotPlips"/> pitched down.</summary>
    [Export] public AudioStream[] ShotHailHits { get; set; } = Array.Empty<AudioStream>();
    /// <summary>Hail bed played by the four hail speakers; filled by convention from resources/audio/weather/hail_loop.ogg when empty.</summary>
    [Export] public AudioStream? HailStream { get; set; }
    /// <summary>Dry-leaf skitter loop driven by the gust front over forest and grassland; filled by convention from leaf_skitter_loop.ogg when empty. The gust roar reuses the wind stream.</summary>
    [Export] public AudioStream? LeafSkitterStream { get; set; }
    /// <summary>Mono wind loop played by the four directional wind speakers.</summary>
    [Export] public AudioStream? WindStream { get; set; }
    /// <summary>Mono distant storm loop played by the four directional storm speakers.</summary>
    [Export] public AudioStream? DistantStormStream { get; set; }
    [Export] public NodePath LocalWeatherEffectsPath { get; set; } = new("../LocalWeatherEffects");
    [Export] public NodePath WeatherSourcePath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath SmoothTerrainPath { get; set; } = new("../SmoothTerrainGrid");
    [Export] public NodePath WeatherAudioPath { get; set; } = new("../WeatherAudio");

    private readonly Voice[] _bed = new Voice[2];
    private readonly Voice[] _field = new Voice[RainSoundField.SectorCount * RainSoundField.MaterialCount];
    private readonly Voice[] _roof = new Voice[2];
    private readonly Voice[] _wind = new Voice[RainSoundField.SectorCount];
    private readonly Voice[] _distant = new Voice[RainSoundField.SectorCount];
    private readonly Voice[] _gust = new Voice[RainSoundField.SectorCount];
    private readonly Voice[] _hail = new Voice[RainSoundField.SectorCount];
    private readonly Voice[] _skitter = new Voice[RainSoundField.SectorCount];
    private readonly float[] _current = new float[4];
    private readonly float[] _target = new float[4];
    private readonly Probe[] _probes = new Probe[ProbeCount];
    private readonly float[] _probeAngles = new float[ProbeCount];
    private readonly float[] _probeStrengths = new float[ProbeCount];
    private readonly float[] _distantShare = new float[RainSoundField.SectorCount];
    private readonly float[] _scratchGains = new float[RainSoundField.MaterialCount];
    private readonly float[] _scratchFractions = new float[RainSoundField.MaterialCount];
    private readonly float[] _scratchSector = new float[RainSoundField.SectorCount];
    private readonly float[] _scratchHailExposure = new float[RainSoundField.SectorCount];
    private readonly float[] _exposedMaterial = new float[RainSoundField.MaterialCount];
    private readonly float[] _shotRates = new float[RainSoundField.ShotKindCount];
    private readonly int[] _shotLibraryCounts = new int[RainSoundField.ShotKindCount];
    private readonly int[] _shotKindTotals = new int[RainSoundField.ShotKindCount];
    private readonly int[] _lastShot = { -1, -1, -1, -1, -1, -1, -1, -1 };
    private readonly ShotVoice[] _shots = new ShotVoice[RainShotScheduler.MaxVoices];
    private readonly System.Collections.Generic.Dictionary<AudioStream, AudioStream> _shotStreams = new();
    private readonly RainShotScheduler _shotScheduler = new(4242);
    private readonly RainExposureField _exposure = new();
    private readonly RainShelterClassifier _classifier = new();
    private Node3D? _rig;
    private IRainExposureProvider? _provider;
    private TerrainRainExposureProvider? _terrainProvider;
    private OcclusionMapRainExposureProvider? _mapProvider;
    private IRainExposureProvider? _providerOverride;
    private bool _usingMap;
    private double _mapLostSeconds;
    private bool _providerChanged;
    private AudioEffectLowPassFilter? _bedFilter, _fieldFilter, _windFilter;
    private bool _bedFilterOwned, _fieldFilterOwned, _windFilterOwned;
    private RingLocalWeatherEffects? _local;
    private RingVolumetricCloudLayer? _weather;
    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private SmoothTerrainGrid? _smoothTerrain;
    private RingWeatherAudio? _thunder;
    private ulong _lastTicks;
    private ulong _lastProbeTicks;
    private int _probeIndex;
    private int _probesThisFrame;
    private int _probeTotal;
    private int _resetCount;
    private double _lastWeatherTime = double.NaN;
    private ulong _lastManualRevision;
    private double _audioClock;
    private float _stormProbe;
    private float _sourceRain;
    private float _sourceWind;
    private Vector2 _sourceWindVector;
    private bool _sourceSnow;
    private bool _canHear;
    private bool _suspended;
    private bool _warnedMissingStream;
    private RingCanonicalPosition _listener;
    private RingCanonicalPosition _probeAnchor;
    private bool _hasProbeAnchor;
    private ThunderAcousticContext _lastContext;
    // Exposure scheduling and state.
    private ulong _editVersion;
    private ulong _smoothEditVersion;
    private ThunderAcousticContext _lastExposureContext;
    private RingCanonicalPosition _lastFieldListener;
    private bool _hasFieldListener;
    private bool _rainActive;
    private double _nextExposureProbe;
    private int _slot;
    private int _columnCursor;
    private int _sweepRemaining;
    private int _exposureProbeTotal;
    private int _fieldInvalidations;
    private double _fastGainUntil;
    private OverheadReading _overhead;
    private float _rainIntensity;
    // Shelter outputs (smoothed).
    private float _coverTarget;
    private bool _deepShelter;
    private float _opennessCurrent = 1f;
    private float _bedCutoffHz = RainSoundField.OpenCutoffHz, _fieldCutoffHz = RainSoundField.OpenCutoffHz;
    private float _thunderDb, _thunderCutoffHz = RainSoundField.OpenCutoffHz;
    // Voice bookkeeping for diagnostics.
    private int _voiceStartCount;
    private float _maxGainStep;
    private int _gustSeed = 1337;
    // Near one-shot bookkeeping: kept apart from the loop voices so continuity checks on voice starts and gain steps stay meaningful.
    private Node3D? _shotRoot;
    private float _wetness;
    private float _shotRateTotal;
    private int _shotStartedTotal;
    private int _shotMaxActive;
    private int _shotActive;
    private double _shotMinSpacing = double.PositiveInfinity;
    private double _lastShotClock = double.NaN;
    // Storm arrival state as the audio reads it (observer state, or a fixture override), smoothed leaf share of the biome under the listener.
    private StormArrivalState _arrival = StormArrivalState.Calm;
    private StormArrivalState? _arrivalOverride;
    private float _arrivalFat, _arrivalHail, _arrivalGust, _leafShare, _gustLevel, _hailLevel;
    private double _leafSampleClock = double.NegativeInfinity;
    private float _stormAngle;

    /// <summary>Fixture hook: replaces the observer's storm arrival state (gust front, fat drop, hail weights 0..1 and the bearing of the storm as an angle in the ring frame, 0 = around-positive).</summary>
    public void SetArrivalOverrideForFixture(float gustFront, float fatDrop, float hailIntensity, float stormAngleRadians)
    {
        var bearing = new Vector2(MathF.Cos(stormAngleRadians), MathF.Sin(stormAngleRadians));
        _arrivalOverride = StormArrivalState.Calm with { Valid = true, GustFront = gustFront, FatDrop = fatDrop, HailIntensity = hailIntensity, HailSize = .6f, StormBearing = bearing, OutflowDirection = -bearing };
    }
    public void ClearArrivalOverrideForFixture() => _arrivalOverride = null;

    /// <summary>Underground-mode enclosure blend (0..1); fades the exterior beds without stopping their streams.</summary>
    public float UndergroundBlend { get; set; }
    /// <summary>Underground-mode hold: skips exposure probing while fully enclosed and treats the listener as deep underground.</summary>
    public bool UndergroundSuspended { get; set; }
    /// <summary>True under any cover: a porch roof, a room, a cave. Hysteresis and dwell come from the classifier.</summary>
    public bool IsRainSheltered => _deepShelter || _coverTarget >= .5f;
    /// <summary>Where rain exposure comes from. By default the occlusion map while it is valid and fresh, otherwise the terrain; a test fixture can replace both (null restores the automatic choice).</summary>
    public IRainExposureProvider? ExposureProvider
    {
        get => _provider;
        set { _providerOverride = value; _provider = value ?? (_usingMap ? _mapProvider : _terrainProvider); _providerChanged = true; }
    }
    /// <summary>A/B switch for tests and tools: false keeps the terrain provider even when the occlusion map is available.</summary>
    public bool PreferOcclusionMap { get; set; } = true;
    /// <summary>Where the exposure currently comes from: "map", "terrain" or "fixture" (a provider installed through <see cref="ExposureProvider"/>).</summary>
    public string CoverSource => _providerOverride != null ? "fixture" : _usingMap ? "map" : "terrain";
    /// <summary>Scripting entry point for fixtures: installs a provider object (null restores the terrain provider).</summary>
    public void SetExposureProviderForFixture(GodotObject? provider) => ExposureProvider = provider as IRainExposureProvider;
    /// <summary>The current shelter class (Open, Roof, Indoors, Cave, DeepCave).</summary>
    public RainShelterClass ShelterClass => _classifier.Current;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        ProcessPriority = 10; // Consume this frame's already-resolved particle weather.
        _local = GetNodeOrNull<RingLocalWeatherEffects>(LocalWeatherEffectsPath);
        _weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherSourcePath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridPath);
        _smoothTerrain = GetNodeOrNull<SmoothTerrainGrid>(SmoothTerrainPath);
        _thunder = GetNodeOrNull<RingWeatherAudio>(WeatherAudioPath);
        if (ShotPlips.Length == 0) ShotPlips = LoadShotLibrary(0);
        if (ShotLeafDrips.Length == 0) ShotLeafDrips = LoadShotLibrary(1);
        if (ShotTicks.Length == 0) ShotTicks = LoadShotLibrary(2);
        if (ShotRoofPlinks.Length == 0) ShotRoofPlinks = LoadShotLibrary(3);
        if (ShotCaveDrips.Length == 0) ShotCaveDrips = LoadShotLibrary(4);
        if (ShotEavesDribbles.Length == 0) ShotEavesDribbles = LoadShotLibrary(5);
        if (ShotHailHits.Length == 0) ShotHailHits = LoadShotLibrary(7);
        HailStream ??= LoadWeatherLoop("hail_loop");
        LeafSkitterStream ??= LoadWeatherLoop("leaf_skitter_loop");
        if (_grid != null) _provider = _terrainProvider = new TerrainRainExposureProvider(_grid, _smoothTerrain);
        if (_terrainProvider != null) _mapProvider = new OcclusionMapRainExposureProvider(_terrainProvider);
        _lastTicks = Time.GetTicksUsec();
        BuildSoundField();
        _bedFilter = AcquireFilter("RainBed", out _bedFilterOwned);
        _fieldFilter = AcquireFilter("RainField", out _fieldFilterOwned);
        _windFilter = AcquireFilter("Wind", out _windFilterOwned);
    }

    public override void _Process(double delta)
    {
        ulong ticks = Time.GetTicksUsec();
        _probesThisFrame = 0;
        _terrainProvider?.BeginFrame();
        if (GetTree().Paused)
        {
            PauseVoices(true);
            _lastTicks = ticks;
            return;
        }
        PauseVoices(false);
        double realDelta = ticks >= _lastTicks ? (ticks - _lastTicks) / 1_000_000.0 : 0.0;
        _lastTicks = ticks;
        _canHear = false;
        if (!Enabled || _explorer == null || _grid == null || _grid.TerrainSampler == null || _weather == null
            || _local == null || !_weather.Enabled || !_local.Enabled || !_local.IsProcessing()
            || !double.IsFinite(_weather.CloudSimulationTimeSeconds))
        {
            Suspend();
            return;
        }
        ThunderAcousticContext context = ResolveAcousticContext();
        _listener = ResolveCameraListener();
        _canHear = !_explorer.IsCameraUnderwater
            && ThunderPropagation.TryDistance(_listener, _listener, context, out _);
        if (!_canHear)
        {
            Suspend();
            return;
        }

        _suspended = false;
        _audioClock += realDelta;
        _sourceRain = FiniteUnit(_local.PrecipitationIntensity);
        Vector2 sourceWind = _local.ResolvedWindMetersPerSecond;
        _sourceWindVector = sourceWind.IsFinite() ? sourceWind : Vector2.Zero;
        _sourceWind = _sourceWindVector.Length();
        _sourceSnow = _local.Snowing;
        ResolveArrivalInputs();
        UpdateProbes(ticks, context);
        WeatherAmbienceMix.Targets(_sourceRain, _sourceSnow, _sourceWind, _stormProbe,
            out _target[0], out _target[1], out _target[2], out _target[3]);
        float rawRain = _target[0] + _target[1];
        // A layer whose stream is missing is silent and never ducks other audio.
        if (!_bed[0].HasStream) _target[0] = 0;
        if (!_bed[1].HasStream) _target[1] = 0;
        if (!_wind[0].HasStream) _target[2] = 0;
        if (!_distant[0].HasStream) _target[3] = 0;
        for (int i = 0; i < 4; i++) _current[i] = WeatherAmbienceMix.Smooth(_current[i], _target[i], realDelta);
        _rainIntensity = WeatherAmbienceMix.Smooth(_rainIntensity, rawRain, realDelta);
        _wetness = RainSoundField.UpdateWetness(_wetness, _rainIntensity, realDelta);
        UpdateExposure(context, realDelta);
        UpdateShelter(realDelta);
        UpdateRig();
        ApplyVoices(realDelta);
        UpdateShots(realDelta);
        PushThunderShelter(realDelta);
        // Only audible layers duck the exterior ambience bed. This ownership is scene-local and leaves the water mix independent.
        float rainDuck = AnyRainStream() ? Math.Max(_current[0], _current[1]) * _opennessCurrent : 0f;
        float strongest = Math.Max(Math.Max(Math.Max(rainDuck, _current[2]), _current[3]), Math.Max(_gustLevel, _hailLevel * _opennessCurrent));
        _explorer.SetMeta(DuckMetadataName, 1.0f - strongest * 0.32f);
    }

    public override void _ExitTree()
    {
        if (GodotObject.IsInstanceValid(_explorer)) _explorer!.RemoveMeta(DuckMetadata);
        ReleaseFilter("RainBed", ref _bedFilter, _bedFilterOwned);
        ReleaseFilter("RainField", ref _fieldFilter, _fieldFilterOwned);
        ReleaseFilter("Wind", ref _windFilter, _windFilterOwned);
        ResetThunderShelter();
        StopVoices();
    }

    public Godot.Collections.Dictionary GetWeatherAmbienceDiagnostics()
    {
        // Distinct valid streams actually loaded on the voices (the first sector carries one voice per material).
        int assets = (_bed[0].HasStream ? 1 : 0) + (_bed[1].HasStream ? 1 : 0) + (_wind[0].HasStream ? 1 : 0) + (_distant[0].HasStream ? 1 : 0);
        for (int m = 0; m < RainSoundField.MaterialCount; m++) if (_field[m].HasStream) assets++;
        if (_roof[0].HasStream && ValidStream(RainRoofStream)) assets++;
        int probes = 0;
        foreach (Probe probe in _probes)
            if (probe.Valid && _audioClock - probe.TimeSeconds <= ProbeLifetimeSeconds) probes++;
        int rainPlaying = CountPlaying(_bed) + CountPlaying(_field) + CountPlaying(_roof);
        int stormPlaying = CountPlaying(_gust) + CountPlaying(_hail) + CountPlaying(_skitter);
        int playing = rainPlaying + CountPlaying(_wind) + CountPlaying(_distant) + stormPlaying;
        var exposure = new float[RainSoundField.SectorCount];
        var materials = new Godot.Collections.Array();
        Span<float> fractions = stackalloc float[RainSoundField.MaterialCount];
        for (int s = 0; s < RainSoundField.SectorCount; s++)
        {
            exposure[s] = _exposure.SectorExposure(s);
            _exposure.SectorMaterials(s, fractions);
            materials.Add(Variant.From(fractions.ToArray()));
        }
        // Distant stays last: the continuity test expects its range to end the gain array.
        Voice[][] layers = { _bed, _field, _roof, _wind, _gust, _hail, _skitter, _distant };
        string[] layerNames = { "bed", "field", "roof", "wind", "gust", "hail", "skitter", "distant" };
        int voiceTotal = 0;
        foreach (Voice[] layer in layers) voiceTotal += layer.Length;
        var gains = new float[voiceTotal];
        var layout = new Godot.Collections.Dictionary();
        int g = 0;
        for (int l = 0; l < layers.Length; l++)
        {
            int start = g;
            foreach (Voice voice in layers[l]) gains[g++] = voice.Gain;
            layout[layerNames[l]] = new[] { start, g };
        }
        var libraryCounts = new int[RainSoundField.ShotKindCount];
        for (int k = 0; k < libraryCounts.Length; k++) libraryCounts[k] = LibraryCount(k);
        int shotsActive = 0;
        foreach (ShotVoice shot in _shots) if (shot != null && shot.Active && shot.Player.Playing) shotsActive++;
        TerrainRainExposureProvider? provider = _terrainProvider;
        return new Godot.Collections.Dictionary
        {
            ["source_rain"] = _sourceRain, ["source_wind"] = _sourceWind, ["source_snow"] = _sourceSnow,
            ["target"] = (float[])_target.Clone(), ["current"] = (float[])_current.Clone(),
            ["asset_count"] = assets, ["player_count"] = _bed.Length + _field.Length + _roof.Length + _wind.Length + _distant.Length,
            ["playing_count"] = playing, ["rain_spatial_player_count"] = _field.Length + _roof.Length,
            ["rain_playing_count"] = rainPlaying, ["nonpositional_player_count"] = _bed.Length,
            ["probe_frame"] = _probesThisFrame, ["probe_total"] = _probeTotal, ["probe_slots"] = ProbeCount,
            ["valid_probes"] = probes, ["distant_strength"] = _stormProbe,
            ["can_hear"] = _canHear, ["enabled"] = Enabled, ["clock"] = _audioClock,
            ["reset_count"] = _resetCount, ["duck"] = _explorer?.GetMeta(DuckMetadata, 1.0f) ?? Variant.From(1.0f),
            ["listener_around"] = _listener.AroundMeters, ["listener_vertical"] = _listener.VerticalMeters,
            ["listener_across"] = _listener.AcrossMeters,
            ["sector_exposure"] = exposure, ["sector_material"] = materials, ["sector_names"] = SectorNames,
            ["shelter_class"] = _classifier.Current.ToString(), ["shelter_class_index"] = (int)_classifier.Current,
            ["cover_source"] = CoverSource, ["sides_open"] = _classifier.SidesOpen, ["field_seeded"] = _exposure.FullySeeded,
            ["voice_start_count"] = _voiceStartCount, ["max_gain_step"] = _maxGainStep, ["voice_gains"] = gains, ["voice_layout"] = layout,
            ["cutoff_hz"] = _bedCutoffHz, ["field_cutoff_hz"] = _fieldCutoffHz, ["thunder_cutoff_hz"] = _thunderCutoffHz, ["thunder_db"] = _thunderDb,
            ["surface_cover_target"] = _coverTarget, ["surface_cover_current"] = _opennessCurrent < 1f ? 1f - _opennessCurrent : 0f,
            ["surface_queries_frame"] = provider?.SurfaceQueriesFrame ?? 0, ["surface_query_total"] = provider?.SurfaceQueriesTotal ?? 0,
            ["surface_density_query_frame"] = provider?.DensityQueriesFrame ?? 0, ["surface_density_query_total"] = provider?.DensityQueriesTotal ?? 0,
            ["surface_invalidations"] = _fieldInvalidations, ["exposure_probe_total"] = _exposureProbeTotal,
            ["shot_voice_count"] = _shots.Length, ["shot_active_count"] = shotsActive, ["shot_started_total"] = _shotStartedTotal,
            ["shot_kind_totals"] = (int[])_shotKindTotals.Clone(), ["shot_rates"] = (float[])_shotRates.Clone(), ["shot_rate"] = _shotRateTotal,
            ["shot_library_counts"] = libraryCounts, ["shot_max_active"] = _shotMaxActive, ["shot_min_spacing"] = _shotMinSpacing,
            ["wetness"] = _wetness, ["near_detail_db"] = NearDetailVolumeDb,
            ["storm_player_count"] = _gust.Length + _hail.Length + _skitter.Length, ["storm_playing_count"] = stormPlaying,
            ["arrival_gust"] = _arrivalGust, ["arrival_fat"] = _arrivalFat, ["arrival_hail"] = _arrivalHail, ["leaf_share"] = _leafShare,
            ["gust_level"] = _gustLevel, ["hail_level"] = _hailLevel, ["gust_roar_stream"] = _gust[0].HasStream, ["hail_stream"] = _hail[0].HasStream, ["skitter_stream"] = _skitter[0].HasStream,
            ["wind_tier_duck"] = RainSoundField.WindTierDuck(_rainIntensity), ["hail_rain_duck"] = RainSoundField.HailRainDuck(_arrivalHail), ["gust_light_bed_duck"] = RainSoundField.GustLightBedDuck(_arrivalGust),
            ["deep_shelter"] = _deepShelter, ["rig_position"] = _rig?.GlobalPosition ?? Vector3.Zero, ["spatial_width"] = SpatialWidth
        };
    }

    /// <summary>Largest per-second gain change any voice made since the last reset, for continuity checks.</summary>
    public void ResetGainStepPeak() => _maxGainStep = 0f;

    /// <summary>Clears the near one-shot counters, peak and spacing (the rate and library state are untouched).</summary>
    public void ResetShotStats()
    {
        _shotStartedTotal = 0;
        _shotMaxActive = _shotActive;
        _shotMinSpacing = double.PositiveInfinity;
        _lastShotClock = double.NaN;
        Array.Clear(_shotKindTotals);
    }

    public Godot.Collections.Dictionary GetRainSurfaceDiagnostic(long around, int across)
    {
        if (_terrainProvider == null) return new Godot.Collections.Dictionary { ["found"] = false, ["top"] = 0f, ["natural"] = 0f, ["material"] = "None" };
        var saved = _terrainProvider.SaveCounters();
        try
        {
            bool found = _terrainProvider.TryProbeColumn(around, across, out RainColumnProbe column);
            string material = !found ? "None" : column.Material == RainMaterial.Foliage ? RainMaterial.Soil.ToString() : column.Material.ToString();
            return new Godot.Collections.Dictionary { ["found"] = found, ["top"] = found ? column.Top : 0f, ["natural"] = found ? column.NaturalTop : 0f, ["material"] = material };
        }
        finally { _terrainProvider.RestoreCounters(saved); }
    }

    private void BuildSoundField()
    {
        _rig = new Node3D { Name = "RainFieldRig", TopLevel = true };
        AddChild(_rig);
        AudioStream? light = DuplicateLoop(RainLightStream), heavy = DuplicateLoop(RainHeavyStream);
        _bed[0] = CreateFlat("BedLight", "RainBed", light, 0);
        _bed[1] = CreateFlat("BedHeavy", "RainBed", heavy, 1);
        AudioStream? soil = DuplicateLoop(RainSoilStream), hard = DuplicateLoop(RainHardStream), water = DuplicateLoop(RainWaterStream), foliage = DuplicateLoop(RainFoliageStream);
        AudioStream? metal = DuplicateLoop(RainMetalStream), roofStream = DuplicateLoop(RainRoofStream) ?? hard;
        AudioStream?[] surfaces = { soil, foliage, hard, water, metal };
        AudioStream? wind = DuplicateLoop(WindStream), distant = DuplicateLoop(DistantStormStream);
        int id = 2;
        for (int s = 0; s < RainSoundField.SectorCount; s++)
            for (int m = 0; m < RainSoundField.MaterialCount; m++)
                _field[s * RainSoundField.MaterialCount + m] = CreateSpatial($"Field{SectorNames[s]}{MaterialNames[m]}", "RainField", surfaces[m], SectorPitch[s], id++);
        for (int r = 0; r < 2; r++) _roof[r] = CreateSpatial("Roof" + (r == 0 ? "AcrossPos" : "AcrossNeg"), "RainField", roofStream, 1f + r * .02f, id++);
        for (int s = 0; s < RainSoundField.SectorCount; s++) _wind[s] = CreateSpatial("Wind" + SectorNames[s], "Wind", wind, SectorPitch[s], id++);
        for (int s = 0; s < RainSoundField.SectorCount; s++) _distant[s] = CreateSpatial("Distant" + SectorNames[s], "DistantStorm", distant, SectorPitch[s], id++);
        // Storm arrival: the gust roar is the wind loop pitched down, the hail bed plays on the rain field bus (so shelter muffling applies), the skitter on the wind bus.
        AudioStream? hailBed = DuplicateLoop(HailStream), skitter = DuplicateLoop(LeafSkitterStream);
        for (int s = 0; s < RainSoundField.SectorCount; s++) _gust[s] = CreateSpatial("GustRoar" + SectorNames[s], "Wind", wind, GustRoarPitch * SectorPitch[s], id++);
        for (int s = 0; s < RainSoundField.SectorCount; s++) _hail[s] = CreateSpatial("Hail" + SectorNames[s], "RainField", hailBed, SectorPitch[s], id++);
        for (int s = 0; s < RainSoundField.SectorCount; s++) _skitter[s] = CreateSpatial("LeafSkitter" + SectorNames[s], "Wind", skitter, SectorPitch[s], id++);
        _shotRoot = new Node3D { Name = "RainShots", TopLevel = true };
        AddChild(_shotRoot);
        for (int i = 0; i < _shots.Length; i++)
        {
            var player = new AudioStreamPlayer3D
            {
                Name = "Shot" + (i + 1), Bus = "RainField", VolumeDb = -80,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance, UnitSize = 3f, MaxDistance = ShotMaxDistanceMeters,
                AttenuationFilterDb = 0, DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled, MaxPolyphony = 1
            };
            _shotRoot.AddChild(player);
            _shots[i] = new ShotVoice(player);
        }
        if ((light == null || heavy == null || soil == null || hard == null || water == null || wind == null || distant == null) && !_warnedMissingStream)
        {
            GD.PushWarning("Weather ambience stream missing or invalid; its layer stays silent.");
            _warnedMissingStream = true;
        }
    }

    private Voice CreateFlat(string name, string bus, AudioStream? stream, int id)
    {
        var player = new AudioStreamPlayer { Name = name, Bus = bus, VolumeDb = -80, PitchScale = 1, Stream = stream };
        AddChild(player);
        return new Voice(player, null, id);
    }

    private Voice CreateSpatial(string name, string bus, AudioStream? stream, float pitch, int id)
    {
        var player = new AudioStreamPlayer3D
        {
            Name = name, Bus = bus, VolumeDb = -80, PitchScale = pitch, Stream = stream,
            // Direction only: no distance cue, no Doppler, so a speaker can never be located as an object.
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled,
            AttenuationFilterDb = 0, AttenuationFilterCutoffHz = 20500,
            DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled,
            PanningStrength = SpatialWidth, MaxPolyphony = 1
        };
        _rig!.AddChild(player);
        return new Voice(null, player, id);
    }

    private static AudioStream? DuplicateLoop(AudioStream? source)
    {
        if (!ValidStream(source)) return null;
        if (source is AudioStreamOggVorbis ogg) { var copy = (AudioStreamOggVorbis)ogg.Duplicate(); copy.Loop = true; return copy; }
        return source;
    }

    private ThunderAcousticContext ResolveAcousticContext() => new(
        _grid!.GetCanonicalRingCircumferenceMeters64(), _grid.RingAcrossWidthMeters,
        RingAtmosphereModel.UpperHeightMeters, 25_000.0,
        MinimumAltitudeMeters: -RingAtmosphereModel.FloorDepthMeters,
        DatumInwardOffsetMeters: _grid.RingVerticalBaseOffsetMeters);

    private void UpdateProbes(ulong ticks, in ThunderAcousticContext context)
    {
        double now = _weather!.CloudSimulationTimeSeconds;
        bool timeJump = double.IsFinite(_lastWeatherTime)
            && (now < _lastWeatherTime || now - _lastWeatherTime > 5.0);
        bool moved = _hasProbeAnchor && MovedFromProbeAnchor(context.CircumferenceMeters);
        if (timeJump || moved || _weather.ManualWeatherRevision != _lastManualRevision || context != _lastContext)
            InvalidateProbes();
        _lastWeatherTime = now;
        _lastManualRevision = _weather.ManualWeatherRevision;
        _lastContext = context;
        if (!_hasProbeAnchor)
        {
            _probeAnchor = _listener;
            _hasProbeAnchor = true;
        }
        // A complete sweep takes at least two real seconds. Never perform
        // catch-up queries after slow frames, pausing, jumps, or invalidation.
        if (ticks >= _lastProbeTicks && ticks - _lastProbeTicks >= ProbeIntervalUsec)
        {
            int direction = _probeIndex % 8;
            double range = _probeIndex < 8 ? 4_000.0 : 12_000.0;
            double angle = direction * Math.Tau / 8.0;
            // Surface-rain roar is approximated at the ring datum. This does
            // not inspect terrain/shelter or reconstruct every storm footprint.
            RingCanonicalPosition sample = new(
                _listener.AroundMeters + Math.Cos(angle) * range, 0.0,
                _listener.AcrossMeters + Math.Sin(angle) * range);
            sample = sample.Wrap(context.CircumferenceMeters);
            float strength = 0.0f;
            bool validPath = ThunderPropagation.TryDistance(sample, _listener, context, out double distance);
            if (validPath)
            {
                RingVolumetricCloudLayer.LocalWeatherState weather = _weather.QueryLocalWeatherAtTime(sample, now);
                float distanceGain = 1.0f - Mathf.SmoothStep(2_000.0f, 18_000.0f, (float)distance);
                strength = FiniteUnit(weather.PrecipitationPotential) * distanceGain;
                _probesThisFrame = 1;
                _probeTotal++;
            }
            _probes[_probeIndex] = new Probe(sample, strength, _audioClock, validPath);
            _probeIndex = (_probeIndex + 1) % ProbeCount;
            _lastProbeTicks = ticks;
        }

        _stormProbe = 0.0f;
        for (int i = 0; i < ProbeCount; i++)
        {
            Probe probe = _probes[i];
            _probeAngles[i] = (i % 8) * MathF.Tau / 8f;
            _probeStrengths[i] = 0f;
            if (!probe.Valid || _audioClock - probe.TimeSeconds > ProbeLifetimeSeconds) continue;
            // The listener may move within the cache's 1km invalidation radius.
            // Recheck cheap geometry every frame before accepting an old sample.
            if (!ThunderPropagation.TryDistance(probe.Position, _listener, context, out _)) continue;
            _probeStrengths[i] = probe.Strength;
            _stormProbe = Math.Max(_stormProbe, probe.Strength);
        }
    }

    private bool MovedFromProbeAnchor(double circumference)
    {
        double around = (_listener.AroundMeters - _probeAnchor.AroundMeters) % circumference;
        if (around > circumference * 0.5) around -= circumference;
        if (around < -circumference * 0.5) around += circumference;
        double across = _listener.AcrossMeters - _probeAnchor.AcrossMeters;
        double height = _listener.VerticalMeters - _probeAnchor.VerticalMeters;
        return around * around + across * across + height * height > 1_000_000.0;
    }

    // ---- Exposure -------------------------------------------------------------------------------------------------

    private void UpdateExposure(in ThunderAcousticContext context, double realDelta)
    {
        bool active = RainSoundField.ShouldProbeExposure(_sourceSnow, UndergroundSuspended, _sourceRain, _arrivalFat, _wetness);
        if (!active)
        {
            // Dry (wetness under 0.05), snowing or fully enclosed: no terrain queries at all. The stale field is dropped when rain returns.
            // Post-rain wetness above 0.05 keeps probing so the shelter class and eave drips continue after the rain stops.
            _rainActive = false;
            _sweepRemaining = 0;
            return;
        }
        SelectProvider(context, realDelta);
        ulong revision = _grid!.RainSurfaceEditVersion;
        ulong smoothRevision = _smoothTerrain?.EditRevision ?? 0;
        bool teleported = _hasFieldListener && CanonicalDistance(_lastFieldListener, _listener, context.CircumferenceMeters) > RainSoundField.TeleportMeters;
        if (!_rainActive || teleported || context != _lastExposureContext)
        {
            _exposure.Invalidate();
            _overhead = default;
            _slot = 0; _columnCursor = 0; _sweepRemaining = 0;
            _fieldInvalidations++;
            if (teleported || context != _lastExposureContext) _fastGainUntil = _audioClock + .3;
            _editVersion = revision; _smoothEditVersion = smoothRevision;
        }
        else if (revision != _editVersion || smoothRevision != _smoothEditVersion || _providerChanged)
        {
            // A terrain edit invalidates what each column last saw, never the audible voices: re-probe all columns quickly.
            _editVersion = revision; _smoothEditVersion = smoothRevision;
            _sweepRemaining = RainSoundField.ColumnCount + RainSoundField.ColumnCount / 3 + 1;
        }
        _providerChanged = false;
        _rainActive = true;
        _lastExposureContext = context;
        _lastFieldListener = _listener; _hasFieldListener = true;
        bool seeding = !_exposure.FullySeeded || _sweepRemaining > 0;
        if (seeding)
        {
            // Seeding and edit sweeps take one column per frame instead of waiting for the cadence.
            ProbeNext(context);
            if (_sweepRemaining > 0) _sweepRemaining--;
            _nextExposureProbe = _audioClock + ExposureProbeIntervalSeconds;
        }
        else if (_audioClock >= _nextExposureProbe)
        {
            ProbeNext(context);
            // At most one column per frame and no catch-up bursts: low frame rates just sweep more slowly.
            _nextExposureProbe = Math.Max(_nextExposureProbe + ExposureProbeIntervalSeconds, _audioClock);
        }
    }

    // The occlusion map answers while it is valid and fresh (published, listener inside it, newest probe readback under 0.5 s old); the terrain answers otherwise.
    // A provider change re-sweeps every column (never the audible voices), so sector exposure and the shelter class move through their usual slews and dwell.
    private void SelectProvider(in ThunderAcousticContext context, double realDelta)
    {
        if (_mapProvider == null || _terrainProvider == null) return;
        _mapProvider.SetListener(_listener, context.CircumferenceMeters);
        if (_providerOverride != null) return;
        PushMapColumnFrame();
        if (PreferOcclusionMap && _mapProvider.Available)
        {
            _mapLostSeconds = 0;
            if (!_usingMap) SetUsingMap(true);
        }
        else if (_usingMap)
        {
            _mapLostSeconds += Math.Max(0.0, realDelta);
            if (!PreferOcclusionMap || _mapLostSeconds >= MapLossGraceSeconds) SetUsingMap(false);
        }
    }

    private void SetUsingMap(bool value)
    {
        _usingMap = value;
        _provider = value ? _mapProvider : _terrainProvider;
        _providerChanged = true;
    }

    // Tells the map's probe where the 24 columns lie: the listener and the ring axes at it in world space (4 m steps, so a floating origin never degrades them).
    private void PushMapColumnFrame()
    {
        RingPrecipitationOcclusion? map = RingPrecipitationOcclusion.Instance;
        if (map == null || _grid == null) return;
        Vector3 here = _grid.CanonicalRingPositionToWorld(_listener);
        Vector3 around = (_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(_listener.AroundMeters + 4, _listener.VerticalMeters, _listener.AcrossMeters)) - here).Normalized();
        Vector3 across = (_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(_listener.AroundMeters, _listener.VerticalMeters, _listener.AcrossMeters + 4)) - here).Normalized();
        map.SetProbeColumnFrame(here, around, across);
    }

    // Slot cycle: overhead, three columns. That is 10 overhead reads and 30 column reads per second, a 0.8 s sweep.
    private void ProbeNext(in ThunderAcousticContext context)
    {
        if (_provider == null) return;
        _exposureProbeTotal++;
        int slot = _slot++ & 3;
        if (slot == 0) { ProbeOverhead(context); return; }
        int index = _columnCursor;
        _columnCursor = (_columnCursor + 1) % RainSoundField.ColumnCount;
        RainSoundField.ColumnGeometry(index, out _, out float offsetAround, out float offsetAcross);
        RingCanonicalPosition column = new RingCanonicalPosition(_listener.AroundMeters + offsetAround, 0, _listener.AcrossMeters + offsetAcross).Wrap(context.CircumferenceMeters);
        int across = Mathf.FloorToInt((float)column.AcrossMeters);
        if (across < 0 || across >= Mathf.RoundToInt(_grid!.RingAcrossWidthMeters))
        {
            _exposure.ApplyColumn(index, 0, RainMaterial.Soil);
            return;
        }
        long around = (long)Math.Floor(column.AroundMeters);
        if (!_provider.TryProbeColumn(around, across, out RainColumnProbe probe))
        {
            _exposure.ApplyColumn(index, 0, RainMaterial.Soil);
            return;
        }
        bool wall = WallBetween(offsetAround, offsetAcross, context);
        _exposure.ApplyColumn(index, RainSoundField.ColumnExposure(true, probe.Top - (float)_listener.VerticalMeters, wall), probe.Material);
    }

    // Head-height solid tests along the ray: one per metre for the first 3 m (a thin wall is one cell), then mid and late points for far columns.
    private bool WallBetween(float offsetAround, float offsetAcross, in ThunderAcousticContext context)
    {
        float radius = MathF.Sqrt(offsetAround * offsetAround + offsetAcross * offsetAcross);
        for (int i = 0; i < 3; i++)
            if (1.25f + i < radius - .5f && SolidAtFraction((1.25f + i) / radius, offsetAround, offsetAcross, context)) return true;
        return radius > 4f && (SolidAtFraction(.5f, offsetAround, offsetAcross, context) || SolidAtFraction(.8f, offsetAround, offsetAcross, context));
    }

    private bool SolidAtFraction(float fraction, float offsetAround, float offsetAcross, in ThunderAcousticContext context)
    {
        RingCanonicalPosition point = new RingCanonicalPosition(_listener.AroundMeters + offsetAround * fraction, 0, _listener.AcrossMeters + offsetAcross * fraction).Wrap(context.CircumferenceMeters);
        return _provider!.IsSolidAt((long)Math.Floor(point.AroundMeters), _listener.VerticalMeters, Mathf.FloorToInt((float)point.AcrossMeters));
    }

    private void ProbeOverhead(in ThunderAcousticContext context)
    {
        long around = (long)Math.Floor(_listener.AroundMeters);
        int across = Mathf.Clamp(Mathf.FloorToInt((float)_listener.AcrossMeters), 0, Mathf.RoundToInt(_grid!.RingAcrossWidthMeters) - 1);
        _overhead = default;
        if (!_provider!.TryProbeColumn(around, across, out RainColumnProbe column)) return;
        float vertical = (float)_listener.VerticalMeters;
        if (column.NaturalTop > vertical + NaturalCoverClearanceMeters)
            _overhead = new OverheadReading(true, true, column.NaturalTop - vertical);
        else if (column.Constructed && column.Top > vertical + .15f && column.Material != RainMaterial.Water)
            _overhead = new OverheadReading(true, false, column.Top - vertical);
    }

    private void UpdateShelter(double dt)
    {
        // Dry (wetness under 0.05) or snowing weather does not probe, so the class relaxes to open air rather than keeping stale cover.
        RainShelterInput input = _rainActive
            ? new RainShelterInput(UndergroundSuspended, _overhead.HasCover, _overhead.Natural, _overhead.Depth, _exposure.MeanExposure())
            : new RainShelterInput(UndergroundSuspended, false, false, 0, 1f);
        RainShelterClass shelter = _classifier.Update(dt, input);
        _coverTarget = shelter == RainShelterClass.Open ? 0f : 1f;
        _deepShelter = shelter == RainShelterClass.DeepCave;
        RainShelterParams p = RainSoundField.Params(shelter);
        float underground = Mathf.Clamp(UndergroundBlend, 0f, 1f);
        _opennessCurrent = RainSoundField.SlewGain(_opennessCurrent, p.BedGain * (1f - underground), dt);
        _bedCutoffHz = RainSoundField.SlewCutoffHz(_bedCutoffHz, p.BedCutoffHz, dt);
        _fieldCutoffHz = RainSoundField.SlewCutoffHz(_fieldCutoffHz, p.FieldCutoffHz, dt);
        _thunderCutoffHz = RainSoundField.SlewCutoffHz(_thunderCutoffHz, p.ThunderCutoffHz, dt);
        float dbStep = (float)(12.0 * Math.Max(0.0, dt));
        _thunderDb += Mathf.Clamp(p.ThunderDb - _thunderDb, -dbStep, dbStep);
        if (_bedFilter != null) _bedFilter.CutoffHz = _bedCutoffHz;
        if (_fieldFilter != null) _fieldFilter.CutoffHz = _fieldCutoffHz;
        if (_windFilter != null) _windFilter.CutoffHz = _bedCutoffHz;
    }

    // ---- Voices ---------------------------------------------------------------------------------------------------

    private void UpdateRig()
    {
        if (_rig == null || _grid == null) return;
        Vector3 here = _grid.CanonicalRingPositionToWorld(_listener);
        Camera3D? camera = GetViewport().GetCamera3D();
        Vector3 origin = camera != null ? camera.GlobalPosition : here;
        _rig.GlobalPosition = origin;
        // Ring-frame axes at the listener, from a 4 m step so a floating origin never degrades them.
        Vector3 around = (_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(_listener.AroundMeters + 4, _listener.VerticalMeters, _listener.AcrossMeters)) - here).Normalized();
        Vector3 across = (_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(_listener.AroundMeters, _listener.VerticalMeters, _listener.AcrossMeters + 4)) - here).Normalized();
        Vector3 up = (_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(_listener.AroundMeters, _listener.VerticalMeters + 4, _listener.AcrossMeters)) - here).Normalized();
        if (!around.IsFinite() || !across.IsFinite() || !up.IsFinite()) return;
        Vector3[] axes = { around, across, -around, -across };
        float width = Mathf.Clamp(float.IsFinite(SpatialWidth) ? SpatialWidth : .6f, .2f, 1f);
        for (int s = 0; s < RainSoundField.SectorCount; s++)
        {
            Vector3 offset = axes[s] * FieldRadiusMeters;
            for (int m = 0; m < RainSoundField.MaterialCount; m++) Place(_field[s * RainSoundField.MaterialCount + m], offset, width);
            Place(_wind[s], offset, width);
            Place(_distant[s], offset, width);
            Place(_gust[s], offset, width);
            Place(_hail[s], offset, width);
            Place(_skitter[s], offset, width);
        }
        Place(_roof[0], across * FieldRadiusMeters * .4f + up * RoofHeightMeters, width * (.4f / .6f));
        Place(_roof[1], -across * FieldRadiusMeters * .4f + up * RoofHeightMeters, width * (.4f / .6f));
    }

    private static void Place(Voice voice, Vector3 offset, float panning)
    {
        AudioStreamPlayer3D player = voice.Spatial!;
        player.Position = offset;
        if (!Mathf.IsEqualApprox(player.PanningStrength, panning)) player.PanningStrength = panning;
    }

    private void ApplyVoices(double dt)
    {
        RainShelterParams p = RainSoundField.Params(_classifier.Current);
        float underground = 1f - Mathf.Clamp(UndergroundBlend, 0f, 1f);
        float rainDb = ClampDb(RainVolumeDb, -10f), windDb = ClampDb(WindVolumeDb, -13f), distantDb = ClampDb(DistantStormVolumeDb, -18f);
        bool fast = _audioClock < _fastGainUntil;
        float tau = fast ? .3f : RainSoundField.GainTauSeconds;
        float cap = fast ? 1f / .3f : RainSoundField.MaxGainRatePerSecond;

        // Rain-tier ducking: hail owns the high end (rain beds and surface field give up to 40 percent), the gust roar takes up to 30 percent off the light bed,
        // and heavy rain takes up to 25 percent off the wind speakers; the gust roar and hail have their own layers below.
        float hailDuck = RainSoundField.HailRainDuck(_arrivalHail), gustDuck = RainSoundField.GustLightBedDuck(_arrivalGust), windDuck = RainSoundField.WindTierDuck(_rainIntensity);

        // Bed: stereo, intensity crossfade times openness.
        for (int i = 0; i < 2; i++)
            Drive(_bed[i], _current[i] * p.BedGain * underground * BedLevel * hailDuck * (i == 0 ? gustDuck : 1f), rainDb, dt, tau, cap);

        // Surface field: per sector, equal-power across the materials that land there.
        bool foliage = ValidStream(RainFoliageStream), metal = ValidStream(RainMetalStream);
        float fieldOn = _classifier.Current == RainShelterClass.DeepCave ? 0f : underground;
        for (int s = 0; s < RainSoundField.SectorCount; s++)
        {
            _exposure.SectorMaterials(s, _scratchFractions);
            if (!foliage) { _scratchFractions[(int)RainMaterial.Soil] += _scratchFractions[(int)RainMaterial.Foliage]; _scratchFractions[(int)RainMaterial.Foliage] = 0; }
            if (!metal) { _scratchFractions[(int)RainMaterial.Hard] += _scratchFractions[(int)RainMaterial.Metal]; _scratchFractions[(int)RainMaterial.Metal] = 0; }
            RainSoundField.SurfaceGains(_rainIntensity, _exposure.SectorExposure(s), _scratchFractions, _scratchGains);
            for (int m = 0; m < RainSoundField.MaterialCount; m++)
            {
                Voice voice = _field[s * RainSoundField.MaterialCount + m];
                Drive(voice, voice.HasStream ? _scratchGains[m] * fieldOn * FieldLevel * hailDuck : 0f, rainDb, dt, tau, cap);
            }
        }
        for (int r = 0; r < 2; r++)
            Drive(_roof[r], _rainIntensity * p.RoofGain * underground * RoofLevel, rainDb, dt, tau, cap);

        // Wind: louder upwind, slow seeded gusts, quieter under cover.
        RainSoundField.WindSectorGains(_sourceWind, _current[2], RainSoundField.UpwindAngle(_sourceWindVector.X, _sourceWindVector.Y), _scratchSector);
        for (int s = 0; s < RainSoundField.SectorCount; s++)
            Drive(_wind[s], _scratchSector[s] * RainSoundField.GustMultiplier(_gustSeed, s, _audioClock) * p.WindGain * WindLevel * windDuck, windDb, dt, tau, cap);

        // Gust front roar (toward the storm), the hail bed (by sector exposure; the roof owns it under cover) and the leaf skitter (biome and gust).
        RainSoundField.GustRoarSectorGains(_arrivalGust, _stormAngle, _scratchSector);
        float gustLevel = 0f;
        for (int s = 0; s < RainSoundField.SectorCount; s++)
        {
            Drive(_gust[s], _scratchSector[s] * p.WindGain * GustRoarLevel, windDb, dt, tau, cap);
            gustLevel = Math.Max(gustLevel, _gust[s].HasStream ? _gust[s].Gain : 0f);
        }
        _gustLevel = gustLevel;
        for (int s = 0; s < RainSoundField.SectorCount; s++) _scratchHailExposure[s] = _exposure.SectorExposure(s);
        bool roofish = _classifier.Current == RainShelterClass.Roof || _classifier.Current == RainShelterClass.Indoors;
        RainSoundField.HailSectorGains(_arrivalHail, _scratchHailExposure, p.RoofGain, roofish, _scratchSector);
        float hailLevel = 0f;
        float hailOn = _classifier.Current == RainShelterClass.DeepCave ? 0f : underground;
        for (int s = 0; s < RainSoundField.SectorCount; s++)
        {
            Drive(_hail[s], _scratchSector[s] * hailOn * HailLevel, rainDb, dt, tau, cap);
            hailLevel = Math.Max(hailLevel, _hail[s].HasStream ? _hail[s].Gain : 0f);
        }
        _hailLevel = hailLevel;
        float skitter = RainSoundField.LeafSkitterGain(_arrivalGust, _leafShare);
        for (int s = 0; s < RainSoundField.SectorCount; s++)
            Drive(_skitter[s], skitter * (.35f + .65f * RainSoundField.Lobe(RainSoundField.SectorAngle(s), _stormAngle)) * p.WindGain * SkitterLevel, windDb, dt, tau, cap);

        // Distant storm: sector shares move slowly (3 s) so a drifting storm never swings between speakers.
        RainSoundField.DistantSectorGains(_probeAngles, _probeStrengths, 1f, _scratchSector);
        for (int s = 0; s < RainSoundField.SectorCount; s++)
        {
            _distantShare[s] = RainSoundField.SlewGain(_distantShare[s], _scratchSector[s], dt, 3f, 1f);
            Drive(_distant[s], _current[3] * _distantShare[s] * DistantLevel, distantDb, dt, tau, cap);
        }
    }

    private void Drive(Voice voice, float targetGain, float baseDb, double dt, float tau, float cap)
    {
        float before = voice.Gain;
        voice.Gain = RainSoundField.SlewGain(voice.Gain, Math.Clamp(targetGain, 0f, 1f), dt, tau, cap);
        if (dt > 1e-4) _maxGainStep = Math.Max(_maxGainStep, MathF.Abs(voice.Gain - before) / (float)dt);
        if (!voice.HasStream) { voice.Gain = 0; voice.Silence(); return; }
        voice.VolumeDb = voice.Gain <= AudibleGain * .1f ? -80f : baseDb + RainSoundField.GainToDb(voice.Gain);
        if (voice.Gain > AudibleGain)
        {
            voice.SilentSeconds = 0;
            if (!voice.Playing) { voice.Play(StartOffset(voice)); _voiceStartCount++; }
        }
        else
        {
            // A fully faded voice (below -100 dB) stops at once so dry weather is true silence; a quiet one runs on for 3 s in case it swells back.
            voice.SilentSeconds += dt;
            if (voice.Playing && (voice.Gain <= FadedGain || voice.SilentSeconds >= SilentStopSeconds)) voice.Stop();
        }
    }

    // Golden-ratio fractions decorrelate every (sector, layer) loop so the speakers never comb-filter against each other.
    private static float StartOffset(Voice voice)
    {
        double length = voice.StreamLength;
        return (float)(length > 0 ? length * ((voice.Id + 1) * GoldenRatio % 1.0) : 0.0);
    }

    private void PauseVoices(bool paused)
    {
        foreach (Voice[] layer in new[] { _bed, _field, _roof, _wind, _distant, _gust, _hail, _skitter })
            foreach (Voice voice in layer) voice?.Pause(paused);
        foreach (ShotVoice shot in _shots) if (shot != null) shot.Player.StreamPaused = paused;
    }

    private void StopVoices()
    {
        foreach (Voice[] layer in new[] { _bed, _field, _roof, _wind, _distant, _gust, _hail, _skitter })
            foreach (Voice voice in layer) voice?.Silence();
        _gustLevel = _hailLevel = 0f;
        foreach (ShotVoice shot in _shots) shot?.Stop();
        _shotActive = 0;
    }

    private void Suspend()
    {
        if (!_suspended)
        {
            Array.Clear(_target);
            Array.Clear(_current);
            Array.Clear(_distantShare);
            _rainIntensity = 0;
            _wetness = 0;
            _arrival = StormArrivalState.Calm; _arrivalFat = _arrivalHail = _arrivalGust = _leafShare = 0f; _leafSampleClock = double.NegativeInfinity;
            InvalidateProbes();
            _exposure.Invalidate();
            _overhead = default;
            _rainActive = false; _hasFieldListener = false; _sweepRemaining = 0;
            _fieldInvalidations++;
            _classifier.Reset();
            _coverTarget = 0;
            _deepShelter = false;
            _opennessCurrent = 1f;
            _bedCutoffHz = _fieldCutoffHz = _thunderCutoffHz = RainSoundField.OpenCutoffHz;
            _thunderDb = 0;
            if (_bedFilter != null) _bedFilter.CutoffHz = RainSoundField.OpenCutoffHz;
            if (_fieldFilter != null) _fieldFilter.CutoffHz = RainSoundField.OpenCutoffHz;
            if (_windFilter != null) _windFilter.CutoffHz = RainSoundField.OpenCutoffHz;
            ResetThunderShelter();
            StopVoices();
            _lastWeatherTime = double.NaN;
        }
        _suspended = true;
        _sourceRain = 0;
        _sourceWind = 0;
        _sourceSnow = false;
        _explorer?.RemoveMeta(DuckMetadata);
    }

    private void InvalidateProbes()
    {
        Array.Clear(_probes);
        Array.Clear(_probeStrengths);
        _probeIndex = 0;
        _stormProbe = 0;
        _hasProbeAnchor = false;
        _resetCount++;
    }

    // ---- Thunder hook ---------------------------------------------------------------------------------------------

    private void PushThunderShelter(double dt)
    {
        if (_thunder == null || !GodotObject.IsInstanceValid(_thunder)) return;
        _thunder.ShelterMuffleAmount = Mathf.Clamp(-_thunderDb / 16f, 0f, 1f);
        _thunder.ShelterCutoffHz = _thunderCutoffHz;
    }

    private void ResetThunderShelter()
    {
        if (_thunder == null || !GodotObject.IsInstanceValid(_thunder)) return;
        _thunder.ShelterMuffleAmount = 0f;
        _thunder.ShelterCutoffHz = RainSoundField.OpenCutoffHz;
    }

    // ---- Buses ----------------------------------------------------------------------------------------------------

    private static AudioEffectLowPassFilter? AcquireFilter(string busName, out bool owned)
    {
        owned = false;
        int bus = AudioServer.GetBusIndex(busName);
        if (bus < 0) return null;
        for (int i = 0; i < AudioServer.GetBusEffectCount(bus); i++)
            if (AudioServer.GetBusEffect(bus, i) is AudioEffectLowPassFilter existing) { existing.CutoffHz = RainSoundField.OpenCutoffHz; return existing; }
        var filter = new AudioEffectLowPassFilter { CutoffHz = RainSoundField.OpenCutoffHz };
        AudioServer.AddBusEffect(bus, filter);
        owned = true;
        return filter;
    }

    private static void ReleaseFilter(string busName, ref AudioEffectLowPassFilter? filter, bool owned)
    {
        if (filter == null) return;
        if (!owned) { filter.CutoffHz = RainSoundField.OpenCutoffHz; filter = null; return; }
        // Bus indices can shift when another system inserts a bus after _Ready.
        int bus = AudioServer.GetBusIndex(busName);
        if (bus >= 0)
            for (int i = 0; i < AudioServer.GetBusEffectCount(bus); i++)
                if (AudioServer.GetBusEffect(bus, i) == filter) { AudioServer.RemoveBusEffect(bus, i); break; }
        filter = null;
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private RingCanonicalPosition ResolveCameraListener()
    {
        RingCanonicalPosition listener = _explorer!.CanonicalPosition;
        Camera3D? camera = GetViewport().GetCamera3D();
        if (camera == null) return listener;
        if (!_explorer.IsAncestorOf(camera)) return _grid!.WorldToCanonicalRingPosition64(camera.GlobalPosition);
        Transform3D relative = Transform3D.Identity;
        Node3D? node = camera;
        while (node != null && node != _explorer)
        {
            relative = node.Transform * relative;
            node = node.GetParentOrNull<Node3D>();
        }
        if (node == null) return _grid!.WorldToCanonicalRingPosition64(camera.GlobalPosition);
        RingLodGeometrySnapshot geometry = _grid!.CaptureLodGeometrySnapshot();
        Vector3 offset = geometry.GridGlobalTransform.Basis.Inverse() * (_explorer.GlobalBasis * relative.Origin);
        double angle = listener.AroundMeters / geometry.RingRadiusMeters;
        double radial = geometry.RingRadiusMeters - geometry.RingVerticalBaseOffsetMeters - listener.VerticalMeters;
        double radialOffset = offset.X * Math.Cos(angle) + offset.Z * Math.Sin(angle);
        double tangentOffset = -offset.X * Math.Sin(angle) + offset.Z * Math.Cos(angle);
        double cameraRadial = Math.Sqrt((radial + radialOffset) * (radial + radialOffset) + tangentOffset * tangentOffset);
        if (radial <= 0.0 || cameraRadial <= 0.0) return listener;
        double heightOffset = -(2.0 * radial * radialOffset + radialOffset * radialOffset + tangentOffset * tangentOffset)
            / (radial + cameraRadial);
        return new RingCanonicalPosition(
            listener.AroundMeters + geometry.RingRadiusMeters * Math.Atan2(tangentOffset, radial + radialOffset),
            listener.VerticalMeters + heightOffset, listener.AcrossMeters + offset.Y).Wrap(geometry.CircumferenceMeters);
    }

    private static double CanonicalDistance(RingCanonicalPosition a, RingCanonicalPosition b, double circumference)
    {
        double around = (b.AroundMeters - a.AroundMeters) % circumference;
        if (around > circumference * .5) around -= circumference;
        if (around < -circumference * .5) around += circumference;
        double across = b.AcrossMeters - a.AcrossMeters, height = b.VerticalMeters - a.VerticalMeters;
        return Math.Sqrt(around * around + across * across + height * height);
    }

    // ---- Near one-shots -------------------------------------------------------------------------------------------

    private AudioStream[] ShotLibrary(int kind) => (kind switch
    {
        0 => ShotPlips, 1 => ShotLeafDrips, 2 => ShotTicks, 3 => ShotRoofPlinks, 4 => ShotCaveDrips, 5 => ShotEavesDribbles, 6 => ShotPlips, _ => ShotHailHits
    }) ?? Array.Empty<AudioStream>();

    private int LibraryCount(int kind)
    {
        int count = 0;
        foreach (AudioStream? stream in ShotLibrary(kind)) if (stream != null) count++;
        return count;
    }

    private static AudioStream[] LoadShotLibrary(int kind)
    {
        var clips = new System.Collections.Generic.List<AudioStream>();
        for (int index = 1; index <= ShotLibraryMax; index++)
        {
            string path = $"{ShotLibraryRoot}{ShotFolders[kind]}_{index:00}.ogg";
            if (!ResourceLoader.Exists(path)) break;
            AudioStream? stream = ResourceLoader.Load<AudioStream>(path);
            if (ValidStream(stream)) clips.Add(stream!);
        }
        return clips.ToArray();
    }

    // One-shots never loop: an ogg flagged as looping is played from a non-looping duplicate (cached, so playing a shot allocates nothing).
    private static AudioStream? LoadWeatherLoop(string name)
    {
        string path = $"{WeatherAudioRoot}{name}.ogg";
        if (!ResourceLoader.Exists(path)) return null;
        AudioStream? stream = ResourceLoader.Load<AudioStream>(path);
        return ValidStream(stream) ? stream : null;
    }

    // Reads the observer's arrival state (or the fixture override) and smooths what the layers consume. Snow and underground silence the rain-like layers.
    private void ResolveArrivalInputs()
    {
        _arrival = _arrivalOverride ?? StormArrivalMath.Observer;
        bool valid = _arrival.Valid && !_sourceSnow;
        float strength = Math.Min(1f, RingStormArrivalSettings.Current.GustFrontStrength);
        _arrivalGust = valid ? FiniteUnit(_arrival.GustFront) * (_arrivalOverride.HasValue ? 1f : strength) : 0f;
        _arrivalFat = valid ? FiniteUnit(_arrival.FatDrop) : 0f;
        _arrivalHail = valid ? FiniteUnit(_arrival.HailIntensity) : 0f;
        _stormAngle = valid && _arrival.StormBearing.LengthSquared() > 1e-6f ? MathF.Atan2(_arrival.StormBearing.Y, _arrival.StormBearing.X) : 0f;
        // Biome leaf share (forest 1, grassland 0.6) sampled at 0.5 Hz, only while the gust front is up.
        if (_arrivalGust > .02f && _audioClock - _leafSampleClock >= 2.0 && _grid?.TerrainSampler != null)
        {
            _leafSampleClock = _audioClock;
            var biome = _grid.TerrainSampler.GetTerrainSample((long)Math.Floor(_listener.AroundMeters), Mathf.FloorToInt((float)_listener.AcrossMeters)).Biome;
            _leafShare = biome == TerrainBiomeId.Forest ? 1f : biome == TerrainBiomeId.Grassland ? .6f : 0f;
        }
        else if (_arrivalGust <= .02f) _leafShare = 0f;
    }

    private AudioStream PlayableShot(AudioStream stream)
    {
        if (stream is not AudioStreamOggVorbis { Loop: true } ogg) return stream;
        if (_shotStreams.TryGetValue(stream, out AudioStream? cached) && GodotObject.IsInstanceValid(cached)) return cached;
        var copy = (AudioStreamOggVorbis)ogg.Duplicate();
        copy.Loop = false;
        _shotStreams[stream] = copy;
        return copy;
    }

    private void UpdateShots(double dt)
    {
        if (_grid == null || _shots[0] == null) return;
        // Shots stay where they were born: re-place each from its canonical position so a floating-origin shift never drags them along.
        int active = 0;
        foreach (ShotVoice shot in _shots)
        {
            if (!shot.Active) continue;
            if (!shot.Player.Playing) { shot.Active = false; continue; }
            shot.Player.GlobalPosition = _grid.CanonicalRingPositionToWorld(shot.Position);
            active++;
        }
        _shotActive = active;

        Array.Clear(_exposedMaterial);
        for (int s = 0; s < RainSoundField.SectorCount; s++)
        {
            float exposure = _exposure.SectorExposure(s);
            _exposure.SectorMaterials(s, _scratchFractions);
            for (int m = 0; m < RainSoundField.MaterialCount; m++) _exposedMaterial[m] += exposure * _scratchFractions[m] / RainSoundField.SectorCount;
        }
        RainSoundField.ShotRates(_rainIntensity, _wetness, _classifier.Current, _exposedMaterial, _shotRates, _arrivalFat, _arrivalHail);
        int libraryTotal = 0;
        float total = 0f;
        for (int k = 0; k < RainSoundField.ShotKindCount; k++)
        {
            int count = LibraryCount(k);
            _shotLibraryCounts[k] = count;
            if (count == 0) _shotRates[k] = 0f;
            libraryTotal += count;
            total += _shotRates[k];
        }
        _shotRateTotal = total;
        float underground = 1f - Mathf.Clamp(UndergroundBlend, 0f, 1f);
        int started = _shotScheduler.Advance(dt, underground > .01f ? total : 0f, active, libraryTotal);
        if (started != 1) return;
        int kind = _shotScheduler.PickKind(_shotRates);
        if (kind < 0) return;
        AudioStream[] library = ShotLibrary(kind);
        int clip = _shotScheduler.PickIndex(library.Length, ref _lastShot[kind]);
        AudioStream? stream = clip >= 0 ? library[clip] : null;
        if (stream == null) return;
        ShotVoice? voice = null;
        foreach (ShotVoice candidate in _shots) if (!candidate.Active && !candidate.Player.Playing) { voice = candidate; break; }
        if (voice == null) return;
        voice.Position = PlaceShot((RainShotKind)kind);
        AudioStreamPlayer3D player = voice.Player;
        player.Stream = PlayableShot(stream);
        player.PitchScale = _shotScheduler.NextPitchScale() * (kind == (int)RainShotKind.FatPlip ? FatPlipPitch : 1f);
        float nearDb = float.IsFinite(NearDetailVolumeDb) ? Math.Clamp(NearDetailVolumeDb, -24f, 6f) : 0f;
        player.VolumeDb = ClampDb(RainVolumeDb, -10f) + nearDb + KindLevelDb[kind] + _shotScheduler.NextGainDb() + RainSoundField.GainToDb(underground)
            + (kind == (int)RainShotKind.HailHit ? (Mathf.Clamp(_arrival.HailSize, 0f, 1f) - .4f) * 6f : 0f);   // bigger stones hit harder
        player.GlobalPosition = _grid.CanonicalRingPositionToWorld(voice.Position);
        player.Play(0f);
        voice.Active = true;
        _shotStartedTotal++;
        _shotKindTotals[kind]++;
        if (!double.IsNaN(_lastShotClock)) _shotMinSpacing = Math.Min(_shotMinSpacing, _audioClock - _lastShotClock);
        _lastShotClock = _audioClock;
        _shotMaxActive = Math.Max(_shotMaxActive, active + 1);
    }

    // Ring-frame offsets (around, across, vertical) from the listener, converted like the exposure columns are.
    private RingCanonicalPosition PlaceShot(RainShotKind kind)
    {
        float around, across, vertical;
        switch (kind)
        {
            case RainShotKind.RoofPlink:
            case RainShotKind.HailHit when _classifier.Current == RainShelterClass.Roof || _classifier.Current == RainShelterClass.Indoors:
            {
                float angle = _shotScheduler.NextUnit() * MathF.Tau, radius = _shotScheduler.NextUnit() * 2f;
                around = MathF.Cos(angle) * radius; across = MathF.Sin(angle) * radius;
                vertical = 2f + _shotScheduler.NextUnit() * 2f;
                break;
            }
            case RainShotKind.EavesDribble:
            {
                int best = 0;
                float bestExposure = -1f;
                for (int s = 0; s < RainSoundField.SectorCount; s++)
                {
                    float exposure = _exposure.SectorExposure(s);
                    if (exposure > bestExposure) { bestExposure = exposure; best = s; }
                }
                float angle = RainSoundField.SectorAngle(best), distance = 2f + _shotScheduler.NextUnit() * 2f;
                around = MathF.Cos(angle) * distance; across = MathF.Sin(angle) * distance;
                vertical = 1f;
                break;
            }
            case RainShotKind.CaveDrip:
            {
                float angle = _shotScheduler.NextUnit() * MathF.Tau, distance = 2f + _shotScheduler.NextUnit() * 6f;
                around = MathF.Cos(angle) * distance; across = MathF.Sin(angle) * distance;
                vertical = -1f;
                break;
            }
            default:
            {
                float total = 0f;
                for (int s = 0; s < RainSoundField.SectorCount; s++) total += SurfaceShotWeight(kind, s);
                int sector = 0;
                if (total > 1e-6f)
                {
                    float pick = _shotScheduler.NextUnit() * total;
                    for (int s = 0; s < RainSoundField.SectorCount; s++)
                    {
                        float weight = SurfaceShotWeight(kind, s);
                        if (weight <= 0f) continue;
                        sector = s;
                        pick -= weight;
                        if (pick < 0f) break;
                    }
                }
                else sector = Math.Min(RainSoundField.SectorCount - 1, (int)(_shotScheduler.NextUnit() * RainSoundField.SectorCount));
                float angle = RainSoundField.SectorAngle(sector) + (_shotScheduler.NextUnit() * 2f - 1f) * MathF.PI * .25f;
                float distance = 1.5f + _shotScheduler.NextUnit() * 6.5f;
                around = MathF.Cos(angle) * distance; across = MathF.Sin(angle) * distance;
                vertical = kind == RainShotKind.LeafDrip ? 0f : -1.4f;
                break;
            }
        }
        return new RingCanonicalPosition(_listener.AroundMeters + around, _listener.VerticalMeters + vertical, _listener.AcrossMeters + across).Wrap(_lastContext.CircumferenceMeters);
    }

    // Sector weight of a ground-level shot kind: its exposure times the share of the materials the kind belongs to.
    private float SurfaceShotWeight(RainShotKind kind, int sector)
    {
        _exposure.SectorMaterials(sector, _scratchFractions);
        float share = kind switch
        {
            RainShotKind.Plip => _scratchFractions[(int)RainMaterial.Water] + .25f * _scratchFractions[(int)RainMaterial.Soil],
            RainShotKind.LeafDrip => _scratchFractions[(int)RainMaterial.Foliage],
            RainShotKind.FatPlip or RainShotKind.HailHit => 1f,
            _ => _scratchFractions[(int)RainMaterial.Hard] + _scratchFractions[(int)RainMaterial.Metal]
        };
        return _exposure.SectorExposure(sector) * share;
    }

    private bool AnyRainStream()
    {
        if (_bed[0].HasStream || _bed[1].HasStream) return true;
        for (int m = 0; m < RainSoundField.MaterialCount; m++) if (_field[m].HasStream) return true;
        return false;
    }
    private static float ClampDb(float value, float fallback) => float.IsFinite(value) ? Math.Clamp(value, -80, 6) : fallback;
    private static float FiniteUnit(float value) => float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.0f;
    private static int CountPlaying(Voice[] voices)
    {
        int count = 0;
        foreach (Voice voice in voices) if (voice != null && voice.Playing) count++;
        return count;
    }
    private static bool ValidStream(AudioStream? stream) => stream != null && double.IsFinite(stream.GetLength()) && stream.GetLength() > 0.0;

    /// <summary>One near one-shot speaker: a world-fixed player plus the canonical position it was born at.</summary>
    private sealed class ShotVoice
    {
        public ShotVoice(AudioStreamPlayer3D player) => Player = player;
        public AudioStreamPlayer3D Player { get; }
        public RingCanonicalPosition Position;
        public bool Active;
        public void Stop() { Active = false; if (GodotObject.IsInstanceValid(Player) && Player.Playing) Player.Stop(); }
    }

    private readonly record struct Probe(RingCanonicalPosition Position, float Strength, double TimeSeconds, bool Valid);
    private readonly record struct OverheadReading(bool HasCover, bool Natural, float Depth);

    /// <summary>One speaker: a flat or directional player plus its gain state.</summary>
    private sealed class Voice
    {
        private readonly AudioStreamPlayer? _flat;
        private readonly AudioStreamPlayer3D? _spatial;
        public Voice(AudioStreamPlayer? flat, AudioStreamPlayer3D? spatial, int id) { _flat = flat; _spatial = spatial; Id = id; }
        public AudioStreamPlayer3D? Spatial => _spatial;
        public int Id { get; }
        public float Gain;
        public double SilentSeconds;
        public bool HasStream => (_flat?.Stream ?? _spatial?.Stream) != null;
        public bool Playing => _flat?.Playing ?? _spatial!.Playing;
        public double StreamLength => (_flat?.Stream ?? _spatial?.Stream)?.GetLength() ?? 0;
        public float VolumeDb { set { if (_flat != null) _flat.VolumeDb = value; else _spatial!.VolumeDb = value; } }
        public void Play(float from) { if (_flat != null) _flat.Play(from); else _spatial!.Play(from); }
        public void Stop() { if (_flat != null) _flat.Stop(); else _spatial!.Stop(); }
        public void Pause(bool paused) { if (_flat != null) _flat.StreamPaused = paused; else _spatial!.StreamPaused = paused; }
        public void Silence() { Gain = 0; SilentSeconds = 0; VolumeDb = -80f; if (Playing) Stop(); }
    }
}
