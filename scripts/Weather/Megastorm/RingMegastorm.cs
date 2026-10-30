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
    float SpeedMetersPerSecond, int Direction, long EpisodeId /* passage key for the PDA once-per-approach rules; 0 when inactive */);

/// <summary>
/// Runtime owner of the megastorm (docs/megastorm-design.md 6.1, WO2): holds the <see cref="MegastormModel"/>, follows the weather clock
/// (<see cref="RingVolumetricCloudLayer.CloudSimulationTimeSeconds"/>, which also freezes with <c>CloudSimulationPaused</c>), applies the three
/// menu settings live, and persists through <see cref="MegastormDocument"/>. Untriggered it costs one bool check per frame.
/// </summary>
public partial class RingMegastorm : Node, IMegastormWeatherSource
{
    [Export] public NodePath CloudPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public string MegastormPreferencesPath { get; set; } = GameplayMegastormDefaults.UserPath;

    /// <summary>Test seam: replaces the weather clock.</summary>
    public Func<double>? ClockProvider { get; set; }
    /// <summary>Observer for <see cref="ObserverPhaseChanged"/>; defaults to the explorer.</summary>
    public Func<RingCanonicalPosition?>? ObserverPositionProvider { get; set; }
    /// <summary>(old phase, new phase) at the observer.</summary>
    public event Action<MegastormPhase, MegastormPhase>? ObserverPhaseChanged;

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
        ReadDebugEnvironment();
    }

    public override void _Process(double delta)
    {
        ApplySettings();
        if (_debugTrigger != null || _debugXiMeters != null) ApplyDebugTrigger();
        SyncWeatherSource();
        if (!_model.Triggered) { _observerPhase = MegastormPhase.Inactive; return; }
        RingCanonicalPosition? observer = ObserverPosition();
        if (observer == null) return;
        MegastormPhase phase = Query(observer.Value, Now).Phase;
        if (phase == _observerPhase) return;
        MegastormPhase old = _observerPhase; _observerPhase = phase;
        ObserverPhaseChanged?.Invoke(old, phase);
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

    public MegastormSample QueryNow(RingCanonicalPosition p) => Query(p, Now);

    /// <summary>Pure of (state, p, t); allocation-free.</summary>
    public MegastormSample Query(RingCanonicalPosition p, double t)
    {
        MegastormPointSample s = _model.Query(p.AroundMeters, p.AcrossMeters, t);
        if (!s.Active) return new MegastormSample(false, MegastormPhase.Inactive, 0.0, 0.0, double.PositiveInfinity, double.PositiveInfinity, 0.0, 0.0f, 0.0f, 0.0f, _model.State.Direction, 0);
        return new MegastormSample(true, s.Phase, s.XiMeters, s.DistanceToEdgeMeters, s.TimeToArrivalSeconds, s.TimeToClearSeconds, s.TimeSincePassageSeconds,
            s.Danger, s.Suppression, _model.SpeedAt(t), _model.State.Direction, EpisodeId(p.AroundMeters, t));
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
        ResonanceEntryUnlocked = document?.ResonanceEntryUnlocked ?? false;
        _applied = MenuValues();
        _observerPhase = MegastormPhase.Inactive;
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
    public Godot.Collections.Dictionary query_megastorm(double around, double across, double time_seconds = -1.0)
        => ToDictionary(Query(new RingCanonicalPosition(around, 0.0, across), time_seconds < 0.0 ? Now : time_seconds));

    private static Godot.Collections.Dictionary ToDictionary(in MegastormSample s) => new()
    {
        ["active"] = s.Active, ["phase"] = (int)s.Phase, ["phase_name"] = s.Phase.ToString(), ["xi_meters"] = s.XiMeters,
        ["distance_to_edge_meters"] = s.DistanceToEdgeMeters, ["time_to_arrival_seconds"] = s.TimeToArrivalSeconds,
        ["time_to_clear_seconds"] = s.TimeToClearSeconds, ["time_since_passage_seconds"] = s.TimeSincePassageSeconds,
        ["danger"] = s.Danger, ["suppression"] = s.Suppression, ["speed"] = s.SpeedMetersPerSecond, ["direction"] = s.Direction, ["episode_id"] = s.EpisodeId
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
        if (TryParseDebugTrigger(System.Environment.GetEnvironmentVariable("RING_MEGASTORM"), out var trigger)) _debugTrigger = trigger;
        string? xi = System.Environment.GetEnvironmentVariable("RING_MEGASTORM_XI");
        if (!string.IsNullOrWhiteSpace(xi) && double.TryParse(xi, NumberStyles.Float, CultureInfo.InvariantCulture, out double km) && double.IsFinite(km)) _debugXiMeters = km * 1000.0;
    }

    private void ApplyDebugTrigger()
    {
        RingCanonicalPosition? observer = ObserverPosition();
        if (_debugXiMeters != null && observer == null) return;   // wait for the explorer
        double now = Now;
        var t = _debugTrigger ?? (Around: 0.0, Direction: 1, Age: 1.0e6, Speed: float.NaN);
        float speed = float.IsNaN(t.Speed) ? Math.Clamp(RingMegastormSettings.Current.SpeedMetersPerSecond, MegastormSettings.MinimumSpeed, MegastormSettings.MaximumSpeed)
            : Math.Clamp(t.Speed, MegastormSettings.MinimumSpeed, MegastormSettings.MaximumSpeed);
        double around = t.Around;
        if (_debugXiMeters != null && observer != null)
            // The edge (xi = 0) is the reference line shifted by the lead offset at the observer's across: put the reference line xi + lead ahead of the observer.
            around = observer.Value.AroundMeters + t.Direction * (_debugXiMeters.Value + _model.LeadOffset(observer.Value.AcrossMeters, now));
        _debugTrigger = null; _debugXiMeters = null;
        // The line is at `around` now and was age seconds earlier at around - dir * v * age; the old trigger time skips the formation ramp.
        _model.Trigger(new MegastormTrigger("debug_env", MegastormModel.WrapPositive(around - t.Direction * speed * t.Age, _model.Settings.CircumferenceMeters), t.Direction, now - t.Age, speed, 1800f, 0));
    }
}
