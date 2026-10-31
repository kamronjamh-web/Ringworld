using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo;

/// <summary>
/// Point query result (docs/megastorm-design.md 6.1). This is the part the pure model supports today; the audio, hazard, darkness and wind fields
/// (WO3/WO6/WO10) are added to the end of this record when those work orders land.
/// </summary>
public readonly record struct MegastormSample(bool Active, MegastormPhase Phase, double XiMeters, double DistanceToEdgeMeters /* > 0 ahead */,
    double TimeToArrivalSeconds, double TimeToClearSeconds, double TimeSincePassageSeconds, float Danger /* 0..1 */, float Suppression,
    float SpeedMetersPerSecond, int Direction, long EpisodeId /* passage key for the PDA once-per-approach rules; 0 when inactive */,
    float HumLevel = 0.0f, float WallSingLevel = 0.0f, AirSoundState AirSound = AirSoundState.Silent, float AirGain = 0.0f, float BoomEnvelope = 0.0f,
    float GustSwell = 0.0f, float TremorLevel = 0.0f, float WildlifeQuiet = 0.0f /* audio fields: MegastormSound, docs/megastorm-design.md 5.2; all 0 when inactive */,
    MegastormHazards Hazards = default /* docs/megastorm-design.md 5.5 (WO10); MegastormHazards.None when inactive */,
    float ExtraDarkness = 0.0f /* E 0..1, 5.6 */, float DarknessMultiplier = 1.0f /* K = 1 - (1 - k_min_eff) E, 5.6 */);

/// <summary>
/// Megastorm darkness inputs the bridge writes each frame (docs/megastorm-design.md 5.6) for <see cref="RingMegastorm.Query"/>:
/// the "Storm darkness" strength and the effective darkness floor k_min_eff (day_side_atmosphere.gd, night-dependent).
/// </summary>
public static class MegastormDarknessInputs
{
    public static float StormDarkness { get; set; } = 0.9f;
    public static float DarknessFloor { get; set; } = 0.06f;
}

/// <summary>
/// Runtime owner of the megastorm (docs/megastorm-design.md 6.1, WO2): holds the <see cref="MegastormModel"/>, follows the weather clock
/// (<see cref="RingVolumetricCloudLayer.CloudSimulationTimeSeconds"/>, which also freezes with <c>CloudSimulationPaused</c>), applies the three
/// menu settings live, and persists through <see cref="MegastormDocument"/>. Untriggered it costs one bool check per frame.
/// </summary>
public partial class RingMegastorm : Node, IMegastormWeatherSource
{
    [Export] public NodePath CloudPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath AmbiencePath { get; set; } = new("../WeatherAmbience");
    [Export] public string MegastormPreferencesPath { get; set; } = GameplayMegastormDefaults.UserPath;

    /// <summary>Test seam: replaces the weather clock.</summary>
    public Func<double>? ClockProvider { get; set; }
    /// <summary>Observer for <see cref="ObserverPhaseChanged"/>; defaults to the explorer.</summary>
    public Func<RingCanonicalPosition?>? ObserverPositionProvider { get; set; }
    /// <summary>(old phase, new phase) at the observer.</summary>
    public event Action<MegastormPhase, MegastormPhase>? ObserverPhaseChanged;

    // ---- Hazard events and seams (5.5, WO10) ----
    /// <summary>The observer crossed xi = 0 (edge-triggered; open-air peak and impulse at the observer).</summary>
    public event Action<MegastormBoomEvent>? BoomPassed;
    /// <summary>The observer entered / left the vacuum wake (xi 25-60 km); the argument is the episode id.</summary>
    public event Action<long>? WakeEntered;
    public event Action<long>? WakeExited;
    /// <summary>A registered structure's position crossed xi = 0.</summary>
    public event Action<MegastormStructure, MegastormBoom>? StructureBoomPassed;
    /// <summary>Registered by a future building system; null today, so nothing is polled.</summary>
    public IMegastormStructureSource? StructureSource { get; set; }
    /// <summary>The observer's shelter class and wind-aligned rain exposure as of the last 4 Hz probe (open and 1 when there is no probe).</summary>
    public MegastormObserver ObserverContext => _observer;
    /// <summary>Test seam: replaces the rain-exposure provider's solid test used by the wind-aligned cover probe.</summary>
    public Func<double, double, double, bool>? SolidProbe { get; set; }
    /// <summary>Test seam: forces the shelter class instead of reading the ambience classifier.</summary>
    public Func<RainShelterClass>? ShelterClassProvider { get; set; }

    /// <summary>PDA database hook (5.4): persisted unlock of the "structural resonance" entry.</summary>
    public bool ResonanceEntryUnlocked { get; set; }

    // Debug only (section 7); read by the far and near render paths when they land.
    public static bool DebugFarOff { get; } = System.Environment.GetEnvironmentVariable("RING_MEGASTORM_FAR_OFF") == "1";
    public static bool DebugReprojectOff { get; } = System.Environment.GetEnvironmentVariable("RING_MEGASTORM_REPROJECT_OFF") == "1";

    private RingVolumetricCloudLayer? _clouds;
    private RingVoxelExplorerController? _explorer;
    private MegastormModel _model;
    private (float Speed, float Trailing, float Reach) _applied;
    private MegastormPhase _observerPhase = MegastormPhase.Inactive;
    private (double Around, int Direction, double Age, float Speed)? _debugTrigger;
    private double? _debugXiMeters;
    // The debug request as read at startup, so a save restore without a megastorm (which resets the model and may jump the clock) re-arms it.
    private (double Around, int Direction, double Age, float Speed)? _debugTriggerRequest;
    private double? _debugXiRequest;
    private RingWeatherAmbience? _ambience;
    private MegastormObserver _observer = MegastormObserver.Open;
    private double _probeTimer;
    private readonly MegastormHazardEventTracker _hazardEvents = new();
    private readonly System.Collections.Generic.Dictionary<long, MegastormHazardEventTracker> _structureEvents = new();
    private readonly MegastormStructure[] _structureBuffer = new MegastormStructure[64];
    private const double StructureRadiusMeters = 20000.0, ProbeIntervalSeconds = 0.25;

    public RingMegastorm() { _model = new MegastormModel(SettingsFromMenu()); }

    public MegastormModel Model => _model;
    public bool Triggered => _model.Triggered;
    /// <summary>The weather clock, in seconds.</summary>
    public double Now => ClockProvider?.Invoke() ?? _clouds?.CloudSimulationTimeSeconds ?? 0.0;

    public override void _EnterTree()
    {
        if (GameplayMegastormDefaults.StartupDefaults?.GetPath() != MegastormPreferencesPath)
        {
            RingMegastormSettings.Current = new RingMegastormSettings();
            GameplayMegastormDefaults.ApplyStartup(MegastormPreferencesPath);
        }
        if (!Triggered) _model = new MegastormModel(SettingsFromMenu());
        _applied = MenuValues();
    }

    public override void _Ready()
    {
        _clouds = GetNodeOrNull<RingVolumetricCloudLayer>(CloudPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _ambience = GetNodeOrNull<RingWeatherAmbience>(AmbiencePath);
        ReadDebugEnvironment();
    }

    public override void _Process(double delta)
    {
        ApplySettings();
        if (_debugTrigger != null || _debugXiMeters != null) ApplyDebugTrigger();
        SyncWeatherSource();
        if (!_model.Triggered) { _observerPhase = MegastormPhase.Inactive; ResetHazards(); return; }
        RingCanonicalPosition? observer = ObserverPosition();
        if (observer == null) return;
        UpdateHazards(delta, observer.Value);
        MegastormPhase phase = Query(observer.Value, Now).Phase;
        if (phase == _observerPhase) return;
        MegastormPhase old = _observerPhase; _observerPhase = phase;
        ObserverPhaseChanged?.Invoke(old, phase);
    }

    // ---- Hazards (5.5, WO10): observer context, edge events, registered structures ----
    private void ResetHazards()
    {
        _hazardEvents.Reset();
        if (_structureEvents.Count > 0) _structureEvents.Clear();
        _observer = MegastormObserver.Open;
    }

    private void UpdateHazards(double delta, RingCanonicalPosition observer)
    {
        double t = Now;
        MegastormPointSample ps = _model.Query(observer.AroundMeters, observer.AcrossMeters, t);
        if (!ps.Active) { ResetHazards(); return; }
        _probeTimer -= delta;
        bool tick = _probeTimer <= 0.0;
        if (tick) { _probeTimer = ProbeIntervalSeconds; _observer = ProbeObserver(observer, ps.XiMeters, t); }
        MegastormHazardEdges edges = _hazardEvents.Update(ps.XiMeters);
        if (edges != MegastormHazardEdges.None)
        {
            long episode = EpisodeId(observer.AroundMeters, t);
            if ((edges & MegastormHazardEdges.BoomPassed) != 0)
            {
                MegastormBoom boom = MegastormHazardMath.BoomAt(_model, observer.AroundMeters, observer.AcrossMeters, t);
                BoomPassed?.Invoke(new MegastormBoomEvent(episode, observer, boom.PeakKPa, boom.ImpulseKPaS, boom.TimeSeconds));
            }
            if ((edges & MegastormHazardEdges.WakeEntered) != 0) WakeEntered?.Invoke(episode);
            if ((edges & MegastormHazardEdges.WakeExited) != 0) WakeExited?.Invoke(episode);
        }
        if (tick && StructureSource != null) UpdateStructures(observer, t);
    }

    /// <summary>Shelter class from the ambience classifier; the lee and the rain exposure from the two wind-aligned probes (travel direction ahead of the edge, the wind behind it).</summary>
    private MegastormObserver ProbeObserver(RingCanonicalPosition observer, double xi, double t)
    {
        RainShelterClass cls = ShelterClassProvider?.Invoke() ?? _ambience?.ShelterClass ?? RainShelterClass.Open;
        bool enclosed = cls == RainShelterClass.Indoors || cls == RainShelterClass.Cave || cls == RainShelterClass.DeepCave;
        if (enclosed) return new MegastormObserver(MegastormShelter.Enclosed, 0.0f);
        if (xi < -30000.0 || xi >= MegastormHazardMath.WakeEndXi) return MegastormObserver.Open;
        Func<double, double, double, bool>? solid = SolidProbe ?? SolidFromExposureProvider();
        if (solid == null) return MegastormObserver.Open;
        float level = _model.Evaluate(t).Level;
        int dir = _model.State.Direction;
        float exposure = 1.0f;
        if (xi >= 0.0)
            exposure = WindAlignedCoverProbe.Exposure(observer.AroundMeters, observer.VerticalMeters, observer.AcrossMeters,
                new Vector2(dir * MegastormHazardMath.WindAlong(xi) * level, 0.0f), solid);
        // The blast comes from the wall: the lee is a body upwind of the observer along the travel direction.
        float blast = xi <= 0.0
            ? WindAlignedCoverProbe.Exposure(observer.AroundMeters, observer.VerticalMeters, observer.AcrossMeters, new Vector2(dir * 100.0f, 0.0f), solid) : 1.0f;
        return new MegastormObserver(blast < 0.5f ? MegastormShelter.Lee : MegastormShelter.Open, exposure);
    }

    private Func<double, double, double, bool>? SolidFromExposureProvider()
    {
        IRainExposureProvider? provider = _ambience?.ExposureProvider;
        if (provider == null) return null;
        return (around, vertical, across) => provider.IsSolidAt((long)Math.Floor(around), vertical, (int)Math.Floor(across));
    }

    private void UpdateStructures(RingCanonicalPosition observer, double t)
    {
        int n = StructureSource!.CollectNear(observer, StructureRadiusMeters, _structureBuffer);
        if (_structureEvents.Count > 512) _structureEvents.Clear();
        for (int i = 0; i < n && i < _structureBuffer.Length; i++)
        {
            MegastormStructure st = _structureBuffer[i];
            if (!_structureEvents.TryGetValue(st.Id, out MegastormHazardEventTracker? tracker)) _structureEvents[st.Id] = tracker = new MegastormHazardEventTracker();
            double xi = _model.Xi(st.Base.AroundMeters, st.Base.AcrossMeters, t);
            if ((tracker.Update(xi) & MegastormHazardEdges.BoomPassed) != 0)
                StructureBoomPassed?.Invoke(st, MegastormHazardMath.BoomAt(_model, st.Base.AroundMeters, st.Base.AcrossMeters, t));
        }
    }

    // ---- Weather seam (WO3) -----------------------------------------------------------------------------------------
    // The layer is handed this node (not the model, which is replaced on restore and settings changes) only while a storm exists, so an untriggered
    // world keeps the layer's MegastormSource == null path and stays bit-identical.
    private void SyncWeatherSource()
    {
        if (_clouds == null) return;
        if (_model.Triggered) { if (_clouds.MegastormSource == null) _clouds.MegastormSource = this; }
        else if (ReferenceEquals(_clouds.MegastormSource, this)) _clouds.MegastormSource = null;
    }

    public int BuildBundle(double t, double queryAcross, List<MegastormRecord> list) => _model.BuildBundle(t, queryAcross, list);
    public float SuppressionAt(double around, double across, double t) => _model.SuppressionAt(around, across, t);

    private RingCanonicalPosition? ObserverPosition() => ObserverPositionProvider != null ? ObserverPositionProvider() : _explorer?.CanonicalPosition;

    // ---- Settings ---------------------------------------------------------------------------------------------------
    private static (float Speed, float Trailing, float Reach) MenuValues()
        => (RingMegastormSettings.Current.SpeedMetersPerSecond, RingMegastormSettings.Current.TrailingLengthKm, RingMegastormSettings.Current.WarningReachKm);

    private static MegastormSettings SettingsFromMenu() => WithMenu(new MegastormSettings(), MenuValues());

    private static MegastormSettings WithMenu(MegastormSettings s, (float Speed, float Trailing, float Reach) m) => new()
    {
        SpeedMetersPerSecond = Math.Clamp(m.Speed, MegastormSettings.MinimumSpeed, MegastormSettings.MaximumSpeed),
        TrailingLengthMeters = m.Trailing * 1000.0, WarningReachMeters = m.Reach * 1000.0,
        GustFrontLeadSeconds = RingStormArrivalSettings.Current.GustFrontLeadSeconds,
        CircumferenceMeters = s.CircumferenceMeters, WidthMeters = s.WidthMeters, BowMeters = s.BowMeters,
        MeanderAmplitudeMeters = s.MeanderAmplitudeMeters, MeanderWavelengthMeters = s.MeanderWavelengthMeters, MeanderPhaseRadiansPerHour = s.MeanderPhaseRadiansPerHour
    };

    /// <summary>Pushes changed menu values into the model: a speed change re-anchors at now (position continuous), lengths replace the snapshot.</summary>
    public void ApplySettings()
    {
        var menu = MenuValues();
        if (menu == _applied) return;
        var previous = _applied;
        _applied = menu;
        MegastormSettings next = WithMenu(_model.Settings, menu);
        if (!_model.Triggered) { _model = new MegastormModel(next); return; }
        _model.State.Settings = next;
        if (menu.Speed != previous.Speed) _model.SetSpeed(Now, menu.Speed);
    }

    // ---- Trigger API (6.1) ------------------------------------------------------------------------------------------
    /// <summary>Idempotent per TriggerId; false if a megastorm is already triggered.</summary>
    public bool Trigger(in MegastormTrigger t) => _model.Trigger(t);

    /// <summary>Triggers so the edge reaches <paramref name="target"/> after <paramref name="arrivalSeconds"/> of weather time; refuses (unless forced) when that is too soon.</summary>
    public bool TriggerToReach(RingCanonicalPosition target, double arrivalSeconds, int direction, string triggerId, bool force = false)
        => _model.TriggerToReach(target.AroundMeters, target.AcrossMeters, arrivalSeconds, direction, triggerId, Now, float.NaN, 0, force);

    /// <summary>Re-anchors at now with a new speed (the menu value is not changed).</summary>
    public void SetSpeed(float metersPerSecond) => _model.SetSpeed(Now, metersPerSecond);
    public void Stop(double atTimeSeconds, float fadeSeconds) => _model.Stop(atTimeSeconds, fadeSeconds);

    /// <summary>The boom passage at any point (open air, pure): peak, impulse and the peak time; None when subsonic.</summary>
    public MegastormBoom BoomAt(RingCanonicalPosition p, double t) => MegastormHazardMath.BoomAt(_model, p.AroundMeters, p.AcrossMeters, t);
    public MegastormBoom BoomAt(RingCanonicalPosition p) => BoomAt(p, Now);

    /// <summary>
    /// Ground height provider (around, across) for the scythe skim surface; the scene wiring sets the terrain sampler. With one, the cut chance takes the
    /// ground depth below the 100 m smoothed ground from it; without, a flat ground.
    /// </summary>
    public Func<double, double, double>? SurfaceHeightProvider { get; set; }

    /// <summary>
    /// Per-passage scythe cut chance for a body of this height (5.5.4): sweep rate x dt x F(h_eff) x exposure, the rate scaled like the stream (Lightning
    /// frequency, Reduced flashes). <paramref name="groundBelowSkimMeters"/> is the depth of the local ground below the smoothed skim surface (a trench is positive, which lowers the effective height);
    /// NaN (the default) reads it from <see cref="SurfaceHeightProvider"/>, else the cloud layer's terrain sampler (smoothed 100 m minus the local height), 0 without one.
    /// </summary>
    public float ScytheCutProbability(RingCanonicalPosition p, float heightAboveGroundMeters, float exposure, double t, double dt, float groundBelowSkimMeters = float.NaN)
    {
        MegastormFrame f = _model.Evaluate(t);
        if (!f.Active) return 0.0f;
        if (float.IsNaN(groundBelowSkimMeters))
            {
            Func<double, double, double>? ground = SurfaceHeightProvider ?? _clouds?.CreateSurfaceHeightSampler();
            groundBelowSkimMeters = ground != null ? MegastormScythes.GroundBelowSkimMeters(ground, p.AroundMeters, p.AcrossMeters) : 0.0f;
        }
        float rate = MegastormHazardMath.ScytheSweepRatePerSecond(_model.Xi(p.AroundMeters, p.AcrossMeters, t), _model.Settings.WidthMeters) * f.Level
            * Lightning.LightningVisualPolicy.NaturalStrikeChanceScale;
        return MegastormHazardMath.ScytheCutProbability(rate, heightAboveGroundMeters, groundBelowSkimMeters, exposure, dt);
    }

    /// <summary>The query at the weather clock; a point within 5 m of the observer gets the observer's shelter class and cover-probe exposure.</summary>
    public MegastormSample QueryNow(RingCanonicalPosition p)
    {
        RingCanonicalPosition? o = ObserverPosition();
        bool atObserver = o != null && Math.Abs(MegastormModel.WrapSigned(o.Value.AroundMeters - p.AroundMeters, _model.Settings.CircumferenceMeters)) < 5.0
            && Math.Abs(o.Value.AcrossMeters - p.AcrossMeters) < 5.0;
        return Query(p, Now, atObserver ? _observer : null);
    }

    /// <summary>
    /// Pure of (state, p, t); allocation-free. <paramref name="observer"/> carries the observer-only inputs (shelter class, wind-aligned rain exposure);
    /// null means open ground and full exposure.
    /// </summary>
    public MegastormSample Query(RingCanonicalPosition p, double t, MegastormObserver? observer = null)
    {
        MegastormPointSample s = _model.Query(p.AroundMeters, p.AcrossMeters, t);
        if (!s.Active) return new MegastormSample(false, MegastormPhase.Inactive, 0.0, 0.0, double.PositiveInfinity, double.PositiveInfinity, 0.0, 0.0f, 0.0f, 0.0f, _model.State.Direction, 0, Hazards: MegastormHazards.None);
        float speed = _model.SpeedAt(t);
        MegastormSoundSample a = MegastormSound.Evaluate(_model, s.XiMeters, p.AcrossMeters, speed);
        float darkness = _model.DarknessAt(p.AroundMeters, p.AcrossMeters, t, MegastormDarknessInputs.StormDarkness);
        return new MegastormSample(true, s.Phase, s.XiMeters, s.DistanceToEdgeMeters, s.TimeToArrivalSeconds, s.TimeToClearSeconds, s.TimeSincePassageSeconds,
            s.Danger, s.Suppression, speed, _model.State.Direction, EpisodeId(p.AroundMeters, t),
            a.HumLevel, a.WallSingLevel, a.AirSound, a.AirGain, a.BoomEnvelope, a.GustSwell, a.TremorLevel, a.WildlifeQuiet,
            MegastormHazardMath.Evaluate(_model, p.AroundMeters, p.AcrossMeters, t, s.XiMeters, _model.Evaluate(t), observer ?? MegastormObserver.Open),
            darkness, MegastormModel.DarknessMultiplier(darkness, MegastormDarknessInputs.DarknessFloor));
    }

    /// <summary>
    /// Passage key: trigger-id hash in the high bits, lap index of the edge past this around coordinate in the low bits. Constant through one whole
    /// approach, passage and recession (the lap rolls over at the antipode). The unwrapped line position is summed over the retained anchors, so the
    /// lap index is stable until more than 16 speed changes have trimmed the anchor history.
    /// </summary>
    public long EpisodeId(double around, double t)
    {
        MegastormState st = _model.State;
        if (st.Anchors.Count == 0) return 0;
        double unwrapped = st.Anchors[0].AroundMeters;
        int k = 0;
        for (int i = st.Anchors.Count - 1; i >= 0; i--) if (st.Anchors[i].TimeSeconds <= t) { k = i; break; }
        for (int i = 0; i < k; i++)
            unwrapped += st.Direction * (double)st.Anchors[i].Speed * (st.Anchors[i + 1].TimeSeconds - st.Anchors[i].TimeSeconds);
        unwrapped += st.Direction * (double)st.Anchors[k].Speed * (t - st.Anchors[k].TimeSeconds);
        double c = _model.Settings.CircumferenceMeters;
        long lap = (long)Math.Floor((st.Direction * (unwrapped - around) + 0.5 * c) / c);
        uint idHash = 2166136261u;
        foreach (char ch in st.TriggerId ?? string.Empty) idHash = (idHash ^ ch) * 16777619u;
        return ((long)(idHash & 0x7FFFFFu) << 32) | (uint)(lap + 1);
    }

    // ---- Save / load (6.2) ------------------------------------------------------------------------------------------
    /// <summary>Null when untriggered, so an untriggered world writes nothing.</summary>
    public MegastormDocument? ToDocument() => _model.Triggered ? MegastormDocument.FromState(_model.State, ResonanceEntryUnlocked) : null;

    /// <summary>Null (an old save) or an invalid document restores the untriggered state.</summary>
    public void RestoreFrom(MegastormDocument? document)
    {
        MegastormState? state = document?.ToState();
        _model = state != null ? new MegastormModel(state) : new MegastormModel(SettingsFromMenu());
        if (_debugTriggerRequest != null || _debugXiRequest != null)
        {
            // A debug request was given at startup: re-place the debug storm against the restored clock and position, whatever the save holds.
            _debugTrigger = _debugTriggerRequest; _debugXiMeters = _debugXiRequest;
        }
        ResonanceEntryUnlocked = document?.ResonanceEntryUnlocked ?? false;
        _applied = MenuValues();
        _observerPhase = MegastormPhase.Inactive;
        ResetHazards();
    }

    // ---- GDScript wrappers (snake_case, like register_manual_weather_at) ---------------------------------------------
    /// <summary>start_time: weather-clock start, negative = now. speed: m/s, zero or negative = the menu speed.</summary>
    public bool trigger_megastorm(string trigger_id, double leading_around, int direction = 1, double start_time = -1.0, double speed = 0.0, double formation_seconds = 1800.0, int seed = 0)
        => Trigger(new MegastormTrigger(trigger_id, leading_around, direction, start_time < 0.0 ? Now : start_time, speed > 0.0 ? (float)speed : float.NaN, (float)formation_seconds, seed));
    public bool trigger_megastorm_to_reach(string trigger_id, double around, double across, double arrival_seconds, int direction = 1, bool force = false)
        => TriggerToReach(new RingCanonicalPosition(around, 0.0, across), arrival_seconds, direction, trigger_id, force);
    public void set_megastorm_speed(double meters_per_second) => SetSpeed((float)meters_per_second);
    public void stop_megastorm(double at_time = -1.0, double fade_seconds = 60.0) => Stop(at_time < 0.0 ? Now : at_time, (float)fade_seconds);
    public bool is_megastorm_triggered() => Triggered;
    public bool spawn_megastorm_ahead(double distance_km) => SpawnAhead(distance_km * 1000.0);
    public bool remove_megastorm() => RemoveMegastorm();
    public Godot.Collections.Dictionary query_megastorm(double around, double across, double time_seconds = -1.0)
        => ToDictionary(Query(new RingCanonicalPosition(around, 0.0, across), time_seconds < 0.0 ? Now : time_seconds));

    private static Godot.Collections.Dictionary ToDictionary(in MegastormSample s) => new()
    {
        ["active"] = s.Active, ["phase"] = (int)s.Phase, ["phase_name"] = s.Phase.ToString(), ["xi_meters"] = s.XiMeters,
        ["distance_to_edge_meters"] = s.DistanceToEdgeMeters, ["time_to_arrival_seconds"] = s.TimeToArrivalSeconds,
        ["time_to_clear_seconds"] = s.TimeToClearSeconds, ["time_since_passage_seconds"] = s.TimeSincePassageSeconds,
        ["danger"] = s.Danger, ["suppression"] = s.Suppression, ["speed"] = s.SpeedMetersPerSecond, ["direction"] = s.Direction, ["episode_id"] = s.EpisodeId,
        ["hum_level"] = s.HumLevel, ["wall_sing_level"] = s.WallSingLevel, ["air_sound"] = s.AirSound.ToString(), ["air_gain"] = s.AirGain,
        ["boom_envelope"] = s.BoomEnvelope, ["gust_swell"] = s.GustSwell, ["tremor_level"] = s.TremorLevel, ["wildlife_quiet"] = s.WildlifeQuiet,
        ["overpressure_kpa"] = s.Hazards.OverpressureKPa, ["boom_peak_kpa"] = s.Hazards.BoomPeakKPa, ["boom_impulse_kpa_s"] = s.Hazards.BoomImpulseKPaS,
        ["boom_time_seconds"] = s.Hazards.BoomTimeSeconds, ["rain_impact_power_wm2"] = s.Hazards.RainImpactPowerWm2, ["rain_drop_speed_ms"] = s.Hazards.RainDropSpeedMs,
        ["rain_exposure"] = s.Hazards.RainExposure, ["pressure_deficit_kpa"] = s.Hazards.PressureDeficitKPa, ["pressure_rate_kpa_s"] = s.Hazards.PressureRateKPaPerS,
        ["wake_fog"] = s.Hazards.WakeFog, ["scythe_sweep_rate"] = s.Hazards.ScytheSweepRatePerSecond
    };

    /// <summary>Weather-HUD / debug dictionary: triggered state, line position, speed and the observer's phase.</summary>
    public Godot.Collections.Dictionary GetMegastormDiagnostic()
    {
        double t = Now;
        MegastormFrame f = _model.Evaluate(t);
        return new Godot.Collections.Dictionary
        {
            ["triggered"] = Triggered, ["active"] = f.Active, ["trigger_id"] = _model.State.TriggerId ?? "", ["time_seconds"] = t,
            ["around_meters"] = f.AroundMeters, ["speed"] = f.SpeedMetersPerSecond, ["direction"] = f.Direction, ["formation"] = f.Formation,
            ["level"] = f.Level, ["anchor_count"] = _model.State.Anchors.Count, ["observer_phase"] = _observerPhase.ToString()
        };
    }
    public Godot.Collections.Dictionary get_megastorm_diagnostic() => GetMegastormDiagnostic();

    // ---- Debug environment (section 7; debug only, not in the menu) --------------------------------------------------
    /// <summary>
    /// RING_MEGASTORM=around_km,dir,age_s[,speed]: a storm whose reference line is at around_km now, already age_s seconds old.
    /// RING_MEGASTORM_XI=km: places it so the observer is that many km behind the edge (negative = ahead); with no RING_MEGASTORM it uses dir +1 and a fully formed storm.
    /// </summary>
    public static bool TryParseDebugTrigger(string? text, out (double Around, int Direction, double Age, float Speed) result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] p = text.Split(',', StringSplitOptions.TrimEntries);
        if (p.Length < 3 || !double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double km) || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dir)
            || !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double age) || !double.IsFinite(km) || !double.IsFinite(age)) return false;
        float speed = float.NaN;
        if (p.Length > 3 && (!float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out speed) || !float.IsFinite(speed))) return false;
        result = (km * 1000.0, dir < 0 ? -1 : 1, Math.Max(0.0, age), speed);
        return true;
    }

    private void ReadDebugEnvironment()
    {
        string? spec = System.Environment.GetEnvironmentVariable("RING_MEGASTORM");
        string? xi = System.Environment.GetEnvironmentVariable("RING_MEGASTORM_XI");
        // Command-line forms: --megastorm=around_km,dir,age_s[,speed] and --megastorm-xi=km. They work after "--", after "++" or with no separator
        // (a PowerShell wrapper swallows a bare "--", leaving them as ordinary engine args); user args win over engine args, which win over env vars.
        foreach (string[] args in new[] { OS.GetCmdlineArgs(), OS.GetCmdlineUserArgs() })
            foreach (string arg in args)
            {
                if (arg.StartsWith("--megastorm-xi=", StringComparison.Ordinal)) xi = arg["--megastorm-xi=".Length..];
                else if (arg.StartsWith("--megastorm=", StringComparison.Ordinal)) spec = arg["--megastorm=".Length..];
            }
        if (TryParseDebugTrigger(spec, out var trigger)) _debugTrigger = trigger;
        if (!string.IsNullOrWhiteSpace(xi) && double.TryParse(xi, NumberStyles.Float, CultureInfo.InvariantCulture, out double km) && double.IsFinite(km)) _debugXiMeters = km * 1000.0;
        _debugTriggerRequest = _debugTrigger; _debugXiRequest = _debugXiMeters;
        if (_debugTrigger != null || _debugXiMeters != null)
            GD.Print($"Megastorm debug: requested (xi_km={(_debugXiMeters / 1000.0)?.ToString(CultureInfo.InvariantCulture) ?? "-"}, spec={spec ?? "-"}); waiting for the explorer.");
    }

    private void ApplyDebugTrigger()
    {
        RingCanonicalPosition? observer = ObserverPosition();
        if (_debugXiMeters != null && observer == null) return;   // wait for the explorer
        if (_model.Triggered)
        {
            GD.Print($"Megastorm debug: replacing the saved megastorm (id={_model.State.TriggerId})");
            _model = new MegastormModel(SettingsFromMenu());
        }
        double now = Now;
        var t = _debugTrigger ?? (Around: 0.0, Direction: 1, Age: 1.0e6, Speed: float.NaN);
        _debugTrigger = null;
        double? xi = _debugXiMeters; _debugXiMeters = null;
        (double around, float speed) = PlaceStorm("debug_env", t.Around, t.Direction, t.Age, t.Speed, now, xi != null ? observer : null, xi ?? 0.0);
        GD.Print($"Megastorm debug: triggered at weather t={now:0.0} s, line around={around / 1000.0:0.0} km, dir={t.Direction}, speed={speed:0} m/s"
            + (observer != null ? $", observer around={observer.Value.AroundMeters / 1000.0:0.0} km across={observer.Value.AcrossMeters / 1000.0:0.0} km, xi={(Query(observer.Value, now).XiMeters / 1000.0):0.0} km." : "."));
    }

    /// <summary>
    /// Triggers the model with its reference line at <paramref name="around"/> now, or, when <paramref name="observer"/> is given, placed so the observer's xi is
    /// <paramref name="xiMeters"/> (negative = ahead of the edge). The line was age seconds earlier at around - dir * v * age; the old trigger time skips the formation ramp.
    /// </summary>
    private (double Around, float Speed) PlaceStorm(string id, double around, int direction, double age, float speedOrNaN, double now, RingCanonicalPosition? observer, double xiMeters)
    {
        float speed = Math.Clamp(float.IsNaN(speedOrNaN) ? RingMegastormSettings.Current.SpeedMetersPerSecond : speedOrNaN, MegastormSettings.MinimumSpeed, MegastormSettings.MaximumSpeed);
        if (observer != null)
            // The edge (xi = 0) is the reference line shifted by the lead offset at the observer's across: put the reference line xi + lead ahead of the observer.
            around = observer.Value.AroundMeters + direction * (xiMeters + _model.LeadOffset(observer.Value.AcrossMeters, now));
        _model.Trigger(new MegastormTrigger(id, MegastormModel.WrapPositive(around - direction * speed * age, _model.Settings.CircumferenceMeters), direction, now - age, speed, 1800f, 0));
        return (around, speed);
    }

    private void ResetToFreshModel()
    {
        _model = new MegastormModel(SettingsFromMenu());
        _applied = MenuValues();
        _observerPhase = MegastormPhase.Inactive;
        _debugTrigger = null; _debugXiMeters = null;
        ResetHazards();
    }

    /// <summary>
    /// Menu / debug spawn: replaces any storm with a fully formed one whose edge is <paramref name="distanceMeters"/> ahead of the observer in the facing direction
    /// (facing 0 = from the explorer's yaw, +1 without one), approaching at the menu speed.
    /// </summary>
    public bool SpawnAhead(double distanceMeters, int facing = 0)
    {
        RingCanonicalPosition? observer = ObserverPosition();
        if (observer == null) { GD.Print("Megastorm: spawn refused (no observer)"); return false; }
        if (facing == 0) facing = _explorer != null && Math.Cos(_explorer.SavedYaw) < 0.0 ? -1 : 1;
        facing = facing < 0 ? -1 : 1;
        ResetToFreshModel();
        double now = Now;
        (_, float speed) = PlaceStorm("debug_spawn", 0.0, -facing, 1.0e6, float.NaN, now, observer, -distanceMeters);
        GD.Print($"Megastorm: spawned {distanceMeters / 1000.0:0.0} km ahead (dir={-facing}, speed={speed:0} m/s, observer around={observer.Value.AroundMeters / 1000.0:0.0} km across={observer.Value.AcrossMeters / 1000.0:0.0} km, xi={Query(observer.Value, now).XiMeters / 1000.0:0.0} km, t={now:0.0} s).");
        return true;
    }

    /// <summary>Instant removal (no fade), unlike <see cref="Stop"/>.</summary>
    public bool RemoveMegastorm()
    {
        _debugTrigger = null; _debugXiMeters = null;
        if (!_model.Triggered) return false;
        string? id = _model.State.TriggerId;
        ResetToFreshModel();
        GD.Print($"Megastorm: stopped (removed id={id})");
        return true;
    }
}
