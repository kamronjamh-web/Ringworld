using System;
using System.Collections.Generic;
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
/// Four logical weather layers, independent of delayed thunder. Local rain uses
/// a ring-aligned spatial field; wind/distant rain remain diffuse ambience beds.
/// </summary>
public partial class RingWeatherAmbience : Node
{
    public const string DuckMetadata = "ring_weather_ambience_duck";
    private const int ProbeCount = 16;
    private const ulong ProbeIntervalUsec = 125_000;
    private const double ProbeLifetimeSeconds = 3.0;
    // Surface impacts are deliberately a bounded, independent field.  The eight
    // precipitation-volume voices remain unchanged.
    private const int DetailPoolSize = 24;
    private const int DetailRefreshQueries = 2;
    private const double DetailRefreshIntervalSeconds = .12;
    private const double DetailPredictionSeconds = .20;
    private const double DetailPredictionMaxMeters = 6.0;
    private const double DetailTeleportMeters = 96.0;
    private static readonly double[] RainAroundOffsets = { 12.0, -12.0, 0.0, 0.0 };
    private static readonly double[] RainAcrossOffsets = { 0.0, 0.0, 12.0, -12.0 };
    private static readonly float[] RainPhaseOffsets = { 0.0f, .23f, .51f, .79f };
    // The familiar listener-centred eight are the normal stationary field.  The
    // remaining slots are reserve capacity, not an automatic 24-impact layer.
    private static readonly Vector2[] DetailOffsets = BuildDetailOffsets();

    [Export] public bool Enabled { get; set; } = true;
    [Export(PropertyHint.Range, "-48,6,0.5")] public float RainVolumeDb { get; set; } = -10;
    [Export(PropertyHint.Range, "-48,6,0.5")] public float WindVolumeDb { get; set; } = -13;
    [Export(PropertyHint.Range, "-48,6,0.5")] public float DistantStormVolumeDb { get; set; } = -18;
    [Export] public AudioStream? RainLightStream { get; set; }
    [Export] public AudioStream? RainHeavyStream { get; set; }
    [Export] public AudioStream? RainSoilStream { get; set; }
    [Export] public AudioStream? RainHardStream { get; set; }
    [Export] public AudioStream? RainWaterStream { get; set; }
    [Export] public AudioStream? WindStream { get; set; }
    [Export] public AudioStream? DistantStormStream { get; set; }
    [Export] public NodePath LocalWeatherEffectsPath { get; set; } = new("../LocalWeatherEffects");
    [Export] public NodePath WeatherSourcePath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath SmoothTerrainPath { get; set; } = new("../SmoothTerrainGrid");

    // Wind and distant storm remain non-positional beds. Rain uses two fixed
    // four-emitter fields so it lives around the player rather than in-head.
    private readonly List<AudioStreamPlayer> _players = new(2);
    private readonly List<AudioStreamPlayer3D> _rainLightPlayers = new(4);
    private readonly List<AudioStreamPlayer3D> _rainHeavyPlayers = new(4);
    private readonly bool[] _rainEmitterValid = new bool[4];
    private readonly DetailEmitter[] _detailEmitters = new DetailEmitter[DetailPoolSize];
    private readonly float[] _current = new float[4];
    private readonly float[] _target = new float[4];
    private readonly Probe[] _probes = new Probe[ProbeCount];
    private RingLocalWeatherEffects? _local;
    private RingVolumetricCloudLayer? _weather;
    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private SmoothTerrainGrid? _smoothTerrain;
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
    private bool _sourceSnow;
    private bool _canHear;
    private bool _suspended;
    private bool _warnedMissingStream;
    private RingCanonicalPosition _listener;
    private RingCanonicalPosition _probeAnchor;
    private bool _hasProbeAnchor;
    private ThunderAcousticContext _lastContext;
    private ulong _detailEditVersion;
    private ulong _detailSmoothEditVersion;
    private RingCanonicalPosition _detailAnchor;
    private double _nextDetailRefresh;
    private int _detailCursor;
    private int _surfaceQueriesThisFrame;
    private int _surfaceQueriesTotal;
    private int _smoothDensityQueriesTotal;
    private int _smoothDensityQueriesThisFrame;
    private int _surfaceInvalidations;
    private float _coverTarget;
    private float _coverCurrent;
    private bool _deepShelter;
    /// <summary>Cached precipitation cover; visual rain can share it without new terrain queries.</summary>
    /// <summary>Underground-mode enclosure blend (0..1); fades the exterior beds without stopping their streams.</summary>
    public float UndergroundBlend { get; set; }
    /// <summary>Underground-mode hold: skips detail-emitter placement and surface queries while fully enclosed.</summary>
    public bool UndergroundSuspended { get; set; }
    public bool IsRainSheltered =>_deepShelter || _coverTarget >= .5f;
    private ThunderAcousticContext _lastDetailContext;
    private RingCanonicalPosition _previousDetailListener;
    private bool _hasPreviousDetailListener;
    private int _surfacePredictedPlacements;
    private double _surfaceLeadMeters;
    private int _surfacePlacementCount;
    private int _surfaceRecycled;
    private bool _detailTraveling;
    private double _detailVelocityAround;
    private double _detailVelocityAcross;
    private AudioEffectLowPassFilter? _rainShelterFilter;
    private int _rainBusIndex = -1;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        ProcessPriority = 10; // Consume this frame's already-resolved particle weather.
        _local = GetNodeOrNull<RingLocalWeatherEffects>(LocalWeatherEffectsPath);
        _weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherSourcePath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridPath);
        _smoothTerrain = GetNodeOrNull<SmoothTerrainGrid>(SmoothTerrainPath);
        _lastTicks = Time.GetTicksUsec();
        AddRainGroup("RainLight", RainLightStream, _rainLightPlayers);
        AddRainGroup("RainHeavy", RainHeavyStream, _rainHeavyPlayers);
        AddDetailPool();
        AddPlayer("Wind", WindStream, "Wind");
        AddPlayer("DistantStorm", DistantStormStream, "DistantStorm");
        _rainBusIndex = AudioServer.GetBusIndex("Rain");
        if (_rainBusIndex >= 0) { _rainShelterFilter = new AudioEffectLowPassFilter { CutoffHz = 18000f }; AudioServer.AddBusEffect(_rainBusIndex, _rainShelterFilter); }
    }

    public override void _Process(double delta)
    {
        ulong ticks = Time.GetTicksUsec();
        _probesThisFrame = 0;
        _surfaceQueriesThisFrame = 0;
        _smoothDensityQueriesThisFrame = 0;
        if (GetTree().Paused)
        {
            PausePlayers(true);
            _hasPreviousDetailListener = false; // no stale velocity on resume
            _detailVelocityAround = 0; _detailVelocityAcross = 0;
            _detailTraveling = false; _surfaceLeadMeters = 0;
            _lastTicks = ticks;
            return;
        }
        PausePlayers(false);
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
        _sourceWind = sourceWind.IsFinite() ? sourceWind.Length() : 0.0f;
        _sourceSnow = _local.Snowing;
        UpdateProbes(ticks, context);
        WeatherAmbienceMix.Targets(_sourceRain, _sourceSnow, _sourceWind, _stormProbe,
            out _target[0], out _target[1], out _target[2], out _target[3]);
        UpdateSurfaceDetail(context, realDelta);
        float exteriorMix = (_deepShelter ? 0.0f : Mathf.Lerp(1.0f, .12f, _coverCurrent)) * (1.0f - UndergroundBlend);
        _target[0] *= exteriorMix;
        _target[1] *= exteriorMix;
        UpdateRainEmitterPositions(context);
        Apply(realDelta);
        if (_rainShelterFilter != null) _rainShelterFilter.CutoffHz = Mathf.Lerp(18000f, 1600f, Mathf.Clamp(_coverCurrent / .82f, 0f, 1f));
        // Only audible layers duck the old exterior bed. This ownership is
        // scene-local and leaves the existing water mix completely independent.
        float strongest = Math.Max(Math.Max(_current[0], _current[1]), Math.Max(Math.Max(_current[2], _current[3]), StrongestDetailGain()));
        _explorer.SetMeta(DuckMetadata, 1.0f - strongest * 0.32f);
    }

    public override void _ExitTree()
    {
        if (GodotObject.IsInstanceValid(_explorer)) _explorer!.RemoveMeta(DuckMetadata);
        // Bus indices can shift when another system inserts a bus after _Ready.
        int rainBus = AudioServer.GetBusIndex("Rain");
        if (_rainShelterFilter != null && rainBus >= 0)
            for (int i = 0; i < AudioServer.GetBusEffectCount(rainBus); i++)
                if (AudioServer.GetBusEffect(rainBus, i) == _rainShelterFilter)
                { AudioServer.RemoveBusEffect(rainBus, i); break; }
        StopPlayers();
    }

    public Godot.Collections.Dictionary GetWeatherAmbienceDiagnostics()
    {
        int assets = 0;
        int playing = 0;
        int probes = 0;
        foreach (AudioStreamPlayer player in _players)
        {
            if (ValidStream(player.Stream)) assets++;
            if (player.Playing) playing++;
        }
        foreach (Probe probe in _probes)
            if (probe.Valid && _audioClock - probe.TimeSeconds <= ProbeLifetimeSeconds) probes++;
        int rainPlaying = CountPlaying(_rainLightPlayers) + CountPlaying(_rainHeavyPlayers);
        var detailPositions = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        int detailValid = 0;
        for (int detailIndex = 0; detailIndex < _detailEmitters.Length; detailIndex++)
        {
            DetailEmitter emitter = _detailEmitters[detailIndex];
            if (!emitter.Valid) continue;
            detailValid++;
            detailPositions.Add(new Godot.Collections.Dictionary { ["index"] = detailIndex, ["around"] = emitter.Position.AroundMeters, ["vertical"] = emitter.Position.VerticalMeters, ["across"] = emitter.Position.AcrossMeters, ["material"] = emitter.Material.ToString(), ["gain"] = emitter.CurrentGain, ["playing"] = emitter.Player.Playing });
        }
        int rainValidEmitters = 0;
        foreach (bool valid in _rainEmitterValid) if (valid) rainValidEmitters++;
        int rainAssets = AnyValid(_rainLightPlayers) ? 1 : 0;
        rainAssets += AnyValid(_rainHeavyPlayers) ? 1 : 0;
        rainAssets += ValidStream(RainSoilStream) ? 1 : 0;
        rainAssets += ValidStream(RainHardStream) ? 1 : 0;
        rainAssets += ValidStream(RainWaterStream) ? 1 : 0;
        return new Godot.Collections.Dictionary
        {
            ["source_rain"] = _sourceRain, ["source_wind"] = _sourceWind, ["source_snow"] = _sourceSnow,
            ["target"] = (float[])_target.Clone(), ["current"] = (float[])_current.Clone(),
            ["asset_count"] = assets + rainAssets, ["player_count"] = _players.Count + _rainLightPlayers.Count + _rainHeavyPlayers.Count + DetailPoolSize, ["playing_count"] = playing + rainPlaying + CountDetailPlaying(),
            ["rain_spatial_player_count"] = _rainLightPlayers.Count + _rainHeavyPlayers.Count, ["rain_valid_emitter_count"] = rainValidEmitters,
            ["rain_playing_count"] = rainPlaying, ["nonpositional_player_count"] = _players.Count,
            ["probe_frame"] = _probesThisFrame, ["probe_total"] = _probeTotal, ["probe_slots"] = ProbeCount,
            ["valid_probes"] = probes, ["distant_strength"] = _stormProbe,
            ["can_hear"] = _canHear, ["enabled"] = Enabled, ["clock"] = _audioClock,
            ["reset_count"] = _resetCount, ["duck"] = _explorer?.GetMeta(DuckMetadata, 1.0f) ?? Variant.From(1.0f),
            ["listener_around"] = _listener.AroundMeters, ["listener_vertical"] = _listener.VerticalMeters,
            ["listener_across"] = _listener.AcrossMeters
            , ["surface_detail_pool_count"] = DetailPoolSize, ["surface_detail_valid_count"] = detailValid,
            ["surface_detail_positions"] = detailPositions, ["surface_cover_target"] = _coverTarget,
            ["surface_cover_current"] = _coverCurrent, ["surface_queries_frame"] = _surfaceQueriesThisFrame,
            ["surface_invalidations"] = _surfaceInvalidations, ["surface_query_total"] = _surfaceQueriesTotal,
            ["surface_density_query_frame"] = _smoothDensityQueriesThisFrame, ["surface_density_query_total"] = _smoothDensityQueriesTotal,
            ["detail_playing_count"] = CountDetailPlaying(), ["deep_shelter"] = _deepShelter,
            ["rain_filter_cutoff_hz"] = _rainShelterFilter?.CutoffHz ?? 18000f,
            ["surface_prediction_seconds"] = DetailPredictionSeconds, ["surface_prediction_max_meters"] = DetailPredictionMaxMeters,
            ["surface_retained_audible"] = CountDetailPlaying(), ["surface_predicted_placements"] = _surfacePredictedPlacements
            , ["surface_lead_meters"] = _surfaceLeadMeters, ["surface_allocated_count"] = detailValid,
            ["surface_placement_count"] = _surfacePlacementCount,
            ["surface_recycled_count"] = _surfaceRecycled, ["surface_pool_full"] = detailValid == DetailPoolSize
        };
    }

    private void AddDetailPool()
    {
        var group = new Node3D { Name = "RainSurfaceDetail" };
        AddChild(group);
        for (int index = 0; index < DetailPoolSize; index++)
        {
            var player = new AudioStreamPlayer3D
            {
                Name = "Surface" + (index + 1), Bus = "Rain", VolumeDb = -80, PitchScale = 1,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
                UnitSize = 7, MaxDistance = 42, PanningStrength = 1,
                DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled,
                MaxPolyphony = 1, AttenuationFilterCutoffHz = 18000
            };
            group.AddChild(player);
            _detailEmitters[index] = new DetailEmitter(player);
        }
    }

    private void UpdateSurfaceDetail(in ThunderAcousticContext context, double realDelta)
    {
        if (UndergroundSuspended)
        {
            // Fully enclosed: skip detail-emitter placement and surface queries; beds and streams keep playing.
            _hasPreviousDetailListener = false;
            _detailTraveling = false;
            _surfaceLeadMeters = 0;
            _detailVelocityAround = 0; _detailVelocityAcross = 0;
            _coverTarget = 1; _deepShelter = true;
            _coverCurrent = WeatherAmbienceMix.Smooth(_coverCurrent, 1, realDelta);
            return;
        }
        if (_sourceSnow || _sourceRain <= .0001f)
        {
            _hasPreviousDetailListener = false;
            _detailTraveling = false;
            _surfaceLeadMeters = 0;
            _detailVelocityAround = 0; _detailVelocityAcross = 0;
            _coverTarget = 0; _deepShelter = false;
            _coverCurrent = WeatherAmbienceMix.Smooth(_coverCurrent, 0, realDelta);
            RefreshDetailWorldPositions(context);
            return;
        }
        ulong revision = _grid!.RainSurfaceEditVersion;
        ulong smoothRevision = _smoothTerrain?.EditRevision ?? 0;
        bool terrainChanged = revision != _detailEditVersion || smoothRevision != _detailSmoothEditVersion || context != _lastDetailContext;
        if (terrainChanged)
        {
            _hasPreviousDetailListener = false;
            _detailVelocityAround = 0; _detailVelocityAcross = 0;
            // A terrain edit invalidates placement data, never the audible player
            // itself.  Each slot is revalidated through the normal bounded query
            // budget before it may be reused.
            for (int i = 0; i < _detailEmitters.Length; i++)
            {
                DetailEmitter stale = _detailEmitters[i];
                if (stale.Valid)
                {
                    stale.NeedsRevalidation = true;
                    _detailEmitters[i] = stale;
                }
            }
            _detailEditVersion = revision;
            _detailSmoothEditVersion = smoothRevision;
            _lastDetailContext = context;
        }
        UpdateDetailAnchor(context, realDelta);
        if (_audioClock >= _nextDetailRefresh)
        {
            _coverTarget = ListenerHasCover();
            for (int count = 0; count < DetailRefreshQueries; count++)
            {
                int slot = FindDetailWorkSlot(context);
                if (slot < 0) break;
                RefreshDetailEmitter(slot, context);
            }
            _nextDetailRefresh = _audioClock + DetailRefreshIntervalSeconds;
        }
        _coverCurrent = WeatherAmbienceMix.Smooth(_coverCurrent, _coverTarget, realDelta);
        RefreshDetailWorldPositions(context);
    }

    private float ListenerHasCover()
    {
        long around = (long)Math.Floor(_listener.AroundMeters);
        int across = Mathf.Clamp(Mathf.FloorToInt((float)_listener.AcrossMeters), 0, Mathf.RoundToInt(_grid!.RingAcrossWidthMeters) - 1);
        _deepShelter = false;
        if (!TryResolveRainColumn(around, across, out RainColumn column)) return 0;
        if (column.NaturalTop > _listener.VerticalMeters + .15)
        {
            float depth = column.NaturalTop - (float)_listener.VerticalMeters;
            _deepShelter = depth > 24.0f;
            return Mathf.Clamp(.55f + depth / 24.0f, .55f, 1.0f);
        }
        return column.Top > _listener.VerticalMeters + .15 ? .82f : 0.0f;
    }

    private void RefreshDetailEmitter(int index, in ThunderAcousticContext context)
    {
        DetailEmitter emitter = _detailEmitters[index];
        bool audible = emitter.Valid && ThunderPropagation.TryDistance(emitter.Position, _listener, context, out double currentDistance) && currentDistance < 42.0;
        if (audible && !emitter.NeedsRevalidation)
        {
            // Never steal/reposition an audible loop: it keeps its stream phase
            // and world anchor until natural attenuation makes it expendable.
            return;
        }
        bool revalidating = emitter.NeedsRevalidation && emitter.Valid;
        bool recycled = !emitter.Valid && emitter.Source != null;
        Vector2 offset = DetailOffsets[index];
        RingCanonicalPosition placementAnchor = index < 8 ? _listener : _detailAnchor;
        RingCanonicalPosition column = emitter.NeedsRevalidation && emitter.Valid
            ? emitter.Position
            : new RingCanonicalPosition(placementAnchor.AroundMeters + offset.X, 0, placementAnchor.AcrossMeters + offset.Y);
        column = column.Wrap(context.CircumferenceMeters);
        if (column.AcrossMeters < 0 || column.AcrossMeters >= _grid!.RingAcrossWidthMeters)
        { RetireDetailEmitter(ref emitter); _detailEmitters[index] = emitter; return; }
        int across = Mathf.Clamp(Mathf.FloorToInt((float)column.AcrossMeters), 0, Mathf.RoundToInt(_grid!.RingAcrossWidthMeters) - 1);
        long around = (long)Math.Floor(column.AroundMeters);
        if (!TryResolveRainColumn(around, across, out RainColumn resolved)) { RetireDetailEmitter(ref emitter); _detailEmitters[index] = emitter; return; }
        emitter.Position = new RingCanonicalPosition(around, resolved.Top + .08f, across).Wrap(context.CircumferenceMeters);
        emitter.Material = resolved.Material;
        emitter.Valid = ThunderPropagation.TryDistance(emitter.Position, _listener, context, out double distance) && distance < 42.0;
        emitter.NeedsRevalidation = false;
        if (!emitter.Valid) RetireDetailEmitter(ref emitter);
        else if (!revalidating)
        {
            _surfacePlacementCount++;
            if (index >= 8 && _surfaceLeadMeters > 0) _surfacePredictedPlacements++;
            if (recycled) _surfaceRecycled++;
        }
        _detailEmitters[index] = emitter;
    }

    private void RefreshDetailWorldPositions(in ThunderAcousticContext context)
    {
        for (int detailIndex = 0; detailIndex < _detailEmitters.Length; detailIndex++)
        {
            DetailEmitter emitter = _detailEmitters[detailIndex];
            if (!emitter.Valid) continue;
            if (!ThunderPropagation.TryDistance(emitter.Position, _listener, context, out double distance) || distance >= 42.0)
            {
                RetireDetailEmitter(ref emitter); _detailEmitters[detailIndex] = emitter; continue;
            }
            emitter.Player.GlobalPosition = _grid!.CanonicalRingPositionToWorld(emitter.Position);
            _detailEmitters[detailIndex] = emitter;
        }
    }

    // At most two terrain queries are issued per cadence.  Do not burn either on
    // a retained audible loop: edits come first, then current coverage, then a
    // reserve lead slot only while actually travelling.
    private int FindDetailWorkSlot(in ThunderAcousticContext context)
    {
        for (int pass = 0; pass < 3; pass++)
            for (int step = 0; step < DetailPoolSize; step++)
            {
                int i = (_detailCursor + step) % DetailPoolSize;
                bool permitted = pass == 0 ? _detailEmitters[i].NeedsRevalidation
                    : pass == 1 ? i < 8 && !_detailEmitters[i].Valid
                    : _detailTraveling && i >= 8 && !_detailEmitters[i].Valid;
                if (!permitted || (pass != 0 && IsDetailCandidateDuplicate(i, context))) continue;
                _detailCursor = (i + 1) % DetailPoolSize;
                return i;
            }
        return -1;
    }

    private bool IsDetailCandidateDuplicate(int index, in ThunderAcousticContext context)
    {
        RingCanonicalPosition anchor = index < 8 ? _listener : _detailAnchor;
        Vector2 offset = DetailOffsets[index];
        RingCanonicalPosition candidate = new RingCanonicalPosition(anchor.AroundMeters + offset.X, 0, anchor.AcrossMeters + offset.Y)
            .Wrap(context.CircumferenceMeters);
        foreach (DetailEmitter emitter in _detailEmitters)
        {
            if (!emitter.Valid) continue;
            double around = candidate.AroundMeters - emitter.Position.AroundMeters;
            around %= context.CircumferenceMeters;
            if (around > context.CircumferenceMeters * .5) around -= context.CircumferenceMeters;
            if (around < -context.CircumferenceMeters * .5) around += context.CircumferenceMeters;
            double across = candidate.AcrossMeters - emitter.Position.AcrossMeters;
            if (around * around + across * across < 16.0) return true;
        }
        return false;
    }

    // Integer-column approximation: the same canonical query feeds shelter and
    // emitter placement, so an edited excavation never falls back to legacy air.
    private bool TryResolveRainColumn(long around, int across, out RainColumn result)
    {
        result = default;
        _surfaceQueriesThisFrame++; _surfaceQueriesTotal++;
        if (_grid?.TerrainSampler == null || across < 0 || across >= _grid.RingAcrossWidthMeters) return false;
        float natural;
        if (_smoothTerrain != null)
        {
            natural = Mathf.FloorToInt(_grid.TerrainSampler.GetSmoothSurfaceHeight(around, across) + .5f);
            RingLodSurfaceEditSnapshot edits = _smoothTerrain.CaptureLodSurfaceEditSnapshot();
            if (edits.TryGetEditedSurfaceHeight(around, across, out int editedNatural)) natural = editedNatural;
            // Validate the highest nearby crossing, then refine to 1/32 metre.
            int bracket = Mathf.FloorToInt(natural);
            bool found = false;
            for (int y = bracket + 2; y >= bracket - 2; y--)
            {
                _smoothDensityQueriesThisFrame += 2; _smoothDensityQueriesTotal += 2;
                if (!_smoothTerrain.TryQueryEditableDensityAtCanonicalPosition(around, y, across, out float inside)
                    || !_smoothTerrain.TryQueryEditableDensityAtCanonicalPosition(around, y + 1, across, out float above)) continue;
                if (inside <= 0 && above > 0)
                {
                    float lo = y, hi = y + 1;
                    for (int step = 0; step < 5; step++)
                    {
                        float mid = (lo + hi) * .5f; _smoothDensityQueriesThisFrame++; _smoothDensityQueriesTotal++;
                        if (!_smoothTerrain.TryQueryEditableDensityAtCanonicalPosition(around, mid, across, out float density)) break;
                        if (density <= 0) lo = mid; else hi = mid;
                    }
                    natural = (lo + hi) * .5f; found = true; break;
                }
            }
            if (!found) return false;
        }
		else
		{
			RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
			bool floatSurface = _grid.TerrainSampler.UsesRuntimeSurfaceGeneration || edits.UsesFloatSurface;
			natural = floatSurface
				? edits.GetPresentationSurfaceHeight(_grid.TerrainSampler, around, across)
				: edits.GetSurfaceHeight(_grid.TerrainSampler, around, across) + 1.0f;
		}
        int naturalInteger = Mathf.FloorToInt(natural);
        float top = natural;
        BlockType material = _grid.GetCanonicalBlockType(around, Mathf.FloorToInt(natural - .01f), across);
        if (_smoothTerrain?.CaptureVolumetricDensitySnapshot() is SmoothTerrainVolumetricDensitySnapshot field)
        {
            _smoothDensityQueriesThisFrame++; _smoothDensityQueriesTotal++;
            SmoothTerrainSample naturalSample = field.Sample(around, natural - .5f, across);
            material = naturalSample.Material == (int)SmoothTerrainMaterial.Soil ? BlockType.Soil : BlockType.Rock;
        }
        if (_grid.TryGetHighestRainSurface(around, across, int.MinValue, out int constructedTop, out BlockType constructedMaterial) && constructedTop + 1.0f > top)
        { top = constructedTop + 1.0f; material = constructedMaterial; }
		TerrainSample sample = _grid.CaptureLodSurfaceEditSnapshot().GetPresentationTerrainSample(_grid.TerrainSampler, around, across);
        SurfaceMaterial kind = material == BlockType.Soil ? SurfaceMaterial.Soil : SurfaceMaterial.Hard;
		float waterTop = sample.WaterSurfaceHeight + (_grid.TerrainSampler.UsesRuntimeSurfaceGeneration ? 0.0f : 1.0f);
		if (sample.HasSurfaceWater && waterTop > top) { top = waterTop; kind = SurfaceMaterial.Water; }
        else if (_grid.TryGetDynamicWaterSurfaceAtCanonical(new RingCanonicalPosition(around, Mathf.CeilToInt(top), across), out float dynamicTop) && dynamicTop > top) { top = dynamicTop; kind = SurfaceMaterial.Water; }
        result = new RainColumn(natural, top, kind);
        return true;
    }

    public Godot.Collections.Dictionary GetRainSurfaceDiagnostic(long around, int across)
    {
        int surfaceFrame = _surfaceQueriesThisFrame, surfaceTotal = _surfaceQueriesTotal;
        int densityFrame = _smoothDensityQueriesThisFrame, densityTotal = _smoothDensityQueriesTotal;
        try
        {
            bool found = TryResolveRainColumn(around, across, out RainColumn column);
            return new Godot.Collections.Dictionary { ["found"] = found, ["top"] = found ? column.Top : 0f, ["natural"] = found ? column.NaturalTop : 0f, ["material"] = found ? column.Material.ToString() : "None" };
        }
        finally
        {
            _surfaceQueriesThisFrame = surfaceFrame; _surfaceQueriesTotal = surfaceTotal;
            _smoothDensityQueriesThisFrame = densityFrame; _smoothDensityQueriesTotal = densityTotal;
        }
    }

    private void InvalidateSurfaceDetail()
    {
        _hasPreviousDetailListener = false;
        _detailVelocityAround = 0; _detailVelocityAcross = 0;
        _detailTraveling = false; _surfaceLeadMeters = 0;
        for (int index = 0; index < _detailEmitters.Length; index++)
        {
            DetailEmitter emitter = _detailEmitters[index];
            emitter.Valid = false; emitter.CurrentGain = 0; emitter.Source = null; emitter.Position = default;
            emitter.NeedsRevalidation = false;
            emitter.Player.Stop(); emitter.Player.Stream = null; emitter.Player.VolumeDb = -80;
            _detailEmitters[index] = emitter;
        }
        _surfaceInvalidations++;
    }

    private void AddPlayer(string name, AudioStream? source, string bus)
    {
        var player = new AudioStreamPlayer { Name = name, Bus = bus, VolumeDb = -80, PitchScale = 1 };
        if (ValidStream(source))
        {
            if (source is AudioStreamOggVorbis ogg)
            {
                var copy = (AudioStreamOggVorbis)ogg.Duplicate();
                copy.Loop = true;
                player.Stream = copy;
            }
            else player.Stream = source;
        }
        AddChild(player);
        _players.Add(player);
        if (player.Stream == null && !_warnedMissingStream)
        {
            GD.PushWarning("Weather ambience stream missing or invalid; its layer stays silent.");
            _warnedMissingStream = true;
        }
    }

    private void AddRainGroup(string name, AudioStream? source, List<AudioStreamPlayer3D> destination)
    {
        var group = new Node3D { Name = name };
        AddChild(group);
        AudioStream? loop = DuplicateLoop(source);
        for (int index = 0; index < 4; index++)
        {
            var player = new AudioStreamPlayer3D
            {
                Name = "Emitter" + (index + 1), Bus = "Rain", VolumeDb = -80, PitchScale = 1,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
                UnitSize = 12, MaxDistance = 80, PanningStrength = 1,
                DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled,
                MaxPolyphony = 1, AttenuationFilterCutoffHz = 20500
            };
            player.Stream = loop;
            group.AddChild(player);
            destination.Add(player);
        }
        if (!ValidStream(source) && !_warnedMissingStream) { GD.PushWarning("Weather ambience stream missing or invalid; its layer stays silent."); _warnedMissingStream = true; }
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
        foreach (Probe probe in _probes)
        {
            if (!probe.Valid || _audioClock - probe.TimeSeconds > ProbeLifetimeSeconds) continue;
            // The listener may move within the cache's 1km invalidation radius.
            // Recheck cheap geometry every frame before accepting an old sample.
            if (!ThunderPropagation.TryDistance(probe.Position, _listener, context, out _)) continue;
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

    private void Apply(double dt)
    {
        ApplyRainLayer(_rainLightPlayers, 0, RainVolumeDb, dt);
        ApplyRainLayer(_rainHeavyPlayers, 1, RainVolumeDb, dt);
        ApplySurfaceDetail(dt);
        ApplyBed(_players[0], 2, WindVolumeDb, -13, dt);
        ApplyBed(_players[1], 3, DistantStormVolumeDb, -18, dt);
    }

    private void ApplySurfaceDetail(double dt)
    {
        float rain = _sourceSnow || _deepShelter ? 0.0f : _sourceRain * Mathf.Lerp(1.0f, .2f, _coverCurrent);
        int active = 0;
        foreach (DetailEmitter candidate in _detailEmitters)
            if (candidate.Valid && ValidStream(ResolveDetailStream(candidate.Material))) active++;
        // Equal-power normalization only engages when overlap really exists; the
        // normal eight-source field retains its established individual level.
        float overlap = active > 8 ? Mathf.Sqrt(8.0f / active) : 1.0f;
        for (int detailIndex = 0; detailIndex < _detailEmitters.Length; detailIndex++)
        {
            DetailEmitter emitter = _detailEmitters[detailIndex];
            AudioStream? stream = ResolveDetailStream(emitter.Material);
            if (!emitter.Valid || !ValidStream(stream))
            {
                emitter.CurrentGain = 0; emitter.Player.Stop(); emitter.Player.VolumeDb = -80; _detailEmitters[detailIndex] = emitter; continue;
            }
            if (emitter.Source != stream) { emitter.Player.Stop(); emitter.Player.Stream = DuplicateLoop(stream); emitter.Source = stream; emitter.CurrentGain = 0; _detailEmitters[detailIndex] = emitter; }
            float gain = rain * .35f * overlap;
            emitter.CurrentGain = WeatherAmbienceMix.Smooth(emitter.CurrentGain, gain, dt);
            _detailEmitters[detailIndex] = emitter;
            emitter.Player.VolumeDb = emitter.CurrentGain <= .0001f ? -80 : (float.IsFinite(RainVolumeDb) ? Math.Clamp(RainVolumeDb, -80, 6) : -10) + Mathf.LinearToDb(emitter.CurrentGain);
            if (emitter.CurrentGain > .0001f && !emitter.Player.Playing)
            {
                double length = emitter.Player.Stream?.GetLength() ?? 0;
                emitter.Player.Play((float)(length > 0 ? length * ((detailIndex * .113f) % 1f) : 0));
            }
            if (emitter.CurrentGain <= .0001f && emitter.Player.Playing) emitter.Player.Stop();
        }
    }

    private AudioStream? ResolveDetailStream(SurfaceMaterial material) => material switch
    {
        SurfaceMaterial.Soil => RainSoilStream,
        SurfaceMaterial.Hard => RainHardStream,
        SurfaceMaterial.Water => RainWaterStream,
        _ => null
    };

    private void ApplyRainLayer(List<AudioStreamPlayer3D> players, int layer, float configuredDb, double dt)
    {
        bool anyAudible = false;
        for (int index = 0; index < players.Count; index++)
            anyAudible |= _rainEmitterValid[index] && ValidStream(players[index].Stream);
        if (!anyAudible)
        {
            _target[layer] = 0;
            _current[layer] = 0;
            foreach (AudioStreamPlayer3D player in players) { player.Stop(); player.VolumeDb = -80; }
            return;
        }
        _current[layer] = WeatherAmbienceMix.Smooth(_current[layer], _target[layer], dt);
        float db = float.IsFinite(configuredDb) ? Math.Clamp(configuredDb, -80, 6) : -10;
        for (int index = 0; index < players.Count; index++)
        {
            AudioStreamPlayer3D player = players[index];
            if (!_rainEmitterValid[index] || !ValidStream(player.Stream)) { player.Stop(); player.VolumeDb = -80; continue; }
            float gain = _current[layer] * .18f;
            player.VolumeDb = gain <= .0001f ? -80 : db + Mathf.LinearToDb(gain);
            if (gain > .0001f && !player.Playing) StartRain(player, index);
            if (gain <= .0001f && player.Playing) player.Stop();
        }
    }

    private void ApplyBed(AudioStreamPlayer player, int layer, float configuredDb, float fallbackDb, double dt)
    {
        if (player.Stream == null)
        {
            _target[layer] = 0;
            _current[layer] = 0;
            player.Stop();
            player.VolumeDb = -80;
            return;
        }
        _current[layer] = WeatherAmbienceMix.Smooth(_current[layer], _target[layer], dt);
        float db = float.IsFinite(configuredDb) ? Math.Clamp(configuredDb, -80, 6) : fallbackDb;
        player.VolumeDb = _current[layer] <= .0001f ? -80 : db + Mathf.LinearToDb(_current[layer]);
        if (_current[layer] > .0001f && !player.Playing) player.Play();
        if (_current[layer] <= .0001f && player.Playing) player.Stop();
    }

    private static void StartRain(AudioStreamPlayer3D player, int index)
    {
        double length = player.Stream?.GetLength() ?? 0;
        player.Play((float)(length > 0 ? length * RainPhaseOffsets[index] : 0));
    }

    private void UpdateRainEmitterPositions(in ThunderAcousticContext context)
    {
        // This represents rain in the volume around the character, not yet
        // individual terrain impacts. Translation follows the precipitation
        // volume; camera yaw/pitch must never rotate this ring-aligned field.
        RingCanonicalPosition center = _explorer!.CanonicalPosition;
        for (int index = 0; index < 4; index++)
        {
            RingCanonicalPosition position = new RingCanonicalPosition(
                center.AroundMeters + RainAroundOffsets[index], center.VerticalMeters + 1.5,
                center.AcrossMeters + RainAcrossOffsets[index]).Wrap(context.CircumferenceMeters);
            _rainEmitterValid[index] = ThunderPropagation.TryDistance(position, _listener, context, out double distance)
                && distance < 80;
            // Convert only once per position, after the current floating-origin
            // frame is resolved. Both rain layers share the same physical field.
            Vector3 world = _grid!.CanonicalRingPositionToWorld(position);
            _rainLightPlayers[index].GlobalPosition = world;
            _rainHeavyPlayers[index].GlobalPosition = world;
        }
    }

    private void PausePlayers(bool paused)
    {
        foreach (AudioStreamPlayer player in _players) player.StreamPaused = paused;
        foreach (AudioStreamPlayer3D player in _rainLightPlayers) player.StreamPaused = paused;
        foreach (AudioStreamPlayer3D player in _rainHeavyPlayers) player.StreamPaused = paused;
        foreach (DetailEmitter emitter in _detailEmitters) emitter.Player.StreamPaused = paused;
    }

    private void StopPlayers()
    {
        Array.Clear(_rainEmitterValid);
        foreach (AudioStreamPlayer player in _players)
        {
            player.Stop();
            player.VolumeDb = -80.0f;
        }
        foreach (AudioStreamPlayer3D player in _rainLightPlayers) { player.Stop(); player.VolumeDb = -80; }
        foreach (AudioStreamPlayer3D player in _rainHeavyPlayers) { player.Stop(); player.VolumeDb = -80; }
        foreach (DetailEmitter emitter in _detailEmitters) { emitter.Player.Stop(); emitter.Player.VolumeDb = -80; }
    }

    private void Suspend()
    {
        if (!_suspended)
        {
            Array.Clear(_target);
            Array.Clear(_current);
            InvalidateProbes();
            InvalidateSurfaceDetail();
            _coverTarget = 0;
            _coverCurrent = 0;
            _deepShelter = false;
            if (_rainShelterFilter != null) _rainShelterFilter.CutoffHz = 18000f;
            StopPlayers();
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
        _probeIndex = 0;
        _stormProbe = 0;
        _hasProbeAnchor = false;
        _resetCount++;
    }

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

    private static float FiniteUnit(float value) => float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.0f;
    private void UpdateDetailAnchor(in ThunderAcousticContext context, double dt)
    {
        RingCanonicalPosition lead = _listener;
        _detailTraveling = false;
        _surfaceLeadMeters = 0;
        if (_hasPreviousDetailListener && dt > .0001)
        {
            double around = _listener.AroundMeters - _previousDetailListener.AroundMeters;
            around %= context.CircumferenceMeters;
            if (around > context.CircumferenceMeters * .5) around -= context.CircumferenceMeters;
            if (around < -context.CircumferenceMeters * .5) around += context.CircumferenceMeters;
            double across = _listener.AcrossMeters - _previousDetailListener.AcrossMeters;
            double distance = Math.Sqrt(around * around + across * across);
            if (distance <= DetailTeleportMeters)
            {
                double instantaneousAround = around / dt;
                double instantaneousAcross = across / dt;
                // Exponential low-pass makes render-frame sampling stable when
                // physics movement is quantized; walking/running stays eligible.
                double blend = 1.0 - Math.Exp(-dt * 8.0);
                _detailVelocityAround += (instantaneousAround - _detailVelocityAround) * blend;
                _detailVelocityAcross += (instantaneousAcross - _detailVelocityAcross) * blend;
                double speed = Math.Sqrt(_detailVelocityAround * _detailVelocityAround + _detailVelocityAcross * _detailVelocityAcross);
                _detailTraveling = speed >= .35;
                if (_detailTraveling)
                {
                    double leadScale = Math.Min(DetailPredictionSeconds, DetailPredictionMaxMeters / speed);
                    lead = new RingCanonicalPosition(_listener.AroundMeters + _detailVelocityAround * leadScale, _listener.VerticalMeters,
                        _listener.AcrossMeters + _detailVelocityAcross * leadScale).Wrap(context.CircumferenceMeters);
                    _surfaceLeadMeters = speed * leadScale;
                }
            }
            else { _detailVelocityAround = 0; _detailVelocityAcross = 0; }
        }
        _detailAnchor = lead;
        _previousDetailListener = _listener; _hasPreviousDetailListener = true;
    }
    private static Vector2[] BuildDetailOffsets()
    {
        Vector2[] primary =
        {
            new(0, 0), new(-9, 5), new(5, -10), new(-6, -8),
            new(17, 1), new(-18, -3), new(2, 18), new(-2, -17)
        };
        var offsets = new Vector2[DetailPoolSize];
        Array.Copy(primary, offsets, primary.Length);
        // Reserve locations cover the predicted edge of travel, but their wide
        // deterministic ring is only allocated after motion creates spare work.
        for (int i = 8; i < offsets.Length; i++)
        {
            double angle = ((i - 8) % 8) * Math.PI * 2.0 / 8.0;
            double radius = i < 16 ? 16.0 : 28.0;
            offsets[i] = new Vector2((float)(Math.Cos(angle) * radius), (float)(Math.Sin(angle) * radius));
        }
        return offsets;
    }
    private static void RetireDetailEmitter(ref DetailEmitter emitter)
    {
        emitter.Valid = false; emitter.NeedsRevalidation = false; emitter.CurrentGain = 0;
        emitter.Player.Stop(); emitter.Player.VolumeDb = -80;
    }
    private static int CountPlaying(List<AudioStreamPlayer3D> players)
    {
        int count = 0;
        foreach (AudioStreamPlayer3D player in players) if (player.Playing) count++;
        return count;
    }
    private int CountDetailPlaying()
    {
        int count = 0;
        foreach (DetailEmitter emitter in _detailEmitters) if (emitter.Player.Playing) count++;
        return count;
    }
    private float StrongestDetailGain()
    {
        float strongest = 0;
        foreach (DetailEmitter emitter in _detailEmitters) if (emitter.Valid && emitter.Player.Playing) strongest = Math.Max(strongest, emitter.CurrentGain);
        return strongest;
    }
    private static bool AnyValid(List<AudioStreamPlayer3D> players)
    {
        foreach (AudioStreamPlayer3D player in players) if (ValidStream(player.Stream)) return true;
        return false;
    }
    private static bool ValidStream(AudioStream? stream) => stream != null && double.IsFinite(stream.GetLength()) && stream.GetLength() > 0.0;
    private enum SurfaceMaterial { Soil, Hard, Water }
    private readonly record struct RainColumn(float NaturalTop, float Top, SurfaceMaterial Material);
    private struct DetailEmitter
    {
        public DetailEmitter(AudioStreamPlayer3D player) { Player = player; Position = default; Material = SurfaceMaterial.Soil; Valid = false; }
        public AudioStreamPlayer3D Player; public RingCanonicalPosition Position; public SurfaceMaterial Material; public bool Valid; public bool NeedsRevalidation; public AudioStream? Source; public float CurrentGain;
    }
    private readonly record struct Probe(RingCanonicalPosition Position, float Strength, double TimeSeconds, bool Valid);
}
