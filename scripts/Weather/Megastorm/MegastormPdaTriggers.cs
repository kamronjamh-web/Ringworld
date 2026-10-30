using System;
using System.Collections.Generic;
using System.Globalization;
using RingworldTechDemo.Pda;

namespace RingworldTechDemo;

/// <summary>
/// Hazard inputs for the suit-warning lines (docs/megastorm-design.md 5.5). <see cref="FromSample"/> builds them from a <see cref="MegastormSample"/> and the observer's
/// shelter class; a caller that has no hazards passes null and the shelter, bullet-rain and scythe lines stay silent (the overpressure figures then fall back to the draft values).
/// </summary>
public readonly record struct MegastormPdaHazards(bool ShelterOpen = false, float RainImpactKilowattsPerSquareMeter = 0.0f, float Exposure = 0.0f,
    bool ScytheWithin5Km = false, float WallOverpressureKpa = 60.0f, float WakeUnderpressureKpa = 8.0f, float RainSpeedMetersPerSecond = 140.0f)
{
    /// <summary>No hazards: the optional-parameter defaults do not apply to <c>new()</c> on a struct.</summary>
    public static MegastormPdaHazards None => new(false, 0.0f, 0.0f, false, 60.0f, 8.0f, 140.0f);

    /// <summary>
    /// The hazard lines' inputs at the observer: the shelter-inadequate line needs an Open shelter class and a boom to come; the wall-arrival figure is the open-air peak
    /// attenuated by the shelter class; the scythe line reads "within 5 km" from the scythe stream, or from the attachment zone (a sweep rate above 0) until the stream (WO12) lands.
    /// </summary>
    public static MegastormPdaHazards FromSample(in MegastormSample s, MegastormShelter shelter)
    {
        MegastormHazards h = s.Hazards;
        bool scythe = (!float.IsNaN(h.NearestScytheDistanceMeters) && h.NearestScytheDistanceMeters <= 5000.0f) || (float.IsNaN(h.NearestScytheDistanceMeters) && h.ScytheSweepRatePerSecond > 0.0f);
        return new MegastormPdaHazards(shelter == MegastormShelter.Open && h.BoomPeakKPa > 0.0f, h.RainImpactPowerWm2 / 1000.0f, h.RainExposure, scythe,
            h.BoomPeakKPa * MegastormHazardMath.ShelterFactor(shelter), MegastormHazardMath.WakeTroughKPa, h.RainDropSpeedMs);
    }
}

/// <summary>
/// Trigger and episode rules for the megastorm PDA lines (docs/megastorm-design.md 5.4). Pure of nodes: feed it the observer's
/// <see cref="MegastormSample"/> each frame and it submits requests to a <see cref="PdaAnnouncer"/>.
/// <list type="bullet">
/// <item>An episode is <see cref="MegastormSample.EpisodeId"/> (the passage key); each id speaks at most once per episode, except the resonance
/// rising lines (at most 2 in total, at least 10 min apart).</item>
/// <item>At most one non-critical line per 5 min; critical lines are never limited.</item>
/// <item>Entering an episode already under way (a load, or a position jump over 20 km) speaks only the resync line and marks every earlier stage as done.</item>
/// </list>
/// </summary>
public sealed class MegastormPdaTriggers
{
    public const double NonCriticalSpacingSeconds = 300.0, RisingSpacingSeconds = 600.0, JumpMeters = 20000.0, FarEtaMinutes = 180.0;
    public const float DetectedHum = 0.05f, ResyncHum = 0.06f, RisingFirstHum = 0.25f, RisingSecondHum = 0.5f, FaunaQuiet = 0.8f;
    public const double AirborneAudibleMeters = 80000.0, WallArrivalSeconds = 10.0, ShelterSeconds = 60.0, WakeXiMeters = 20000.0, PassageXiMeters = 50000.0;

    private readonly PdaLineTable _table;
    private readonly PdaAnnouncer _announcer;
    private readonly Func<double> _clock;
    private readonly Action? _unlockEntry;
    private readonly HashSet<string> _fired = new();
    private long _episode;
    private double _lastXi;
    private bool _haveLast;
    private int _risingFired;
    private double _lastRising = double.NegativeInfinity, _lastNonCritical = double.NegativeInfinity;

    /// <param name="unlockEntry">Called when the first resonance line fires (or a resync finds the hum already audible): unlocks the database entry.</param>
    public MegastormPdaTriggers(PdaLineTable table, PdaAnnouncer announcer, Func<double> clock, Action? unlockEntry = null)
    {
        _table = table;
        _announcer = announcer;
        _clock = clock;
        _unlockEntry = unlockEntry;
    }

    public long Episode => _episode;
    public bool HasFired(string id) => _fired.Contains(id);

    public void Update(in MegastormSample s, MegastormPdaHazards? hazards = null)
    {
        if (!s.Active || s.EpisodeId == 0) { _episode = 0; _haveLast = false; return; }
        bool newEpisode = s.EpisodeId != _episode;
        bool jumped = !newEpisode && _haveLast && Math.Abs(s.XiMeters - _lastXi) > JumpMeters;
        if (newEpisode) { _episode = s.EpisodeId; _fired.Clear(); _risingFired = 0; _lastRising = double.NegativeInfinity; }
        _lastXi = s.XiMeters;
        bool first = !_haveLast || newEpisode;
        _haveLast = true;
        if (jumped || (first && UnderWay(s))) { Resync(s); return; }
        Evaluate(s, hazards);
    }

    // ---- Resync ---------------------------------------------------------------------------------------------------------
    private static bool UnderWay(in MegastormSample s) => s.HumLevel > ResyncHum || (s.XiMeters >= 0.0 && s.Phase != MegastormPhase.Clear);

    private void Resync(in MegastormSample s)
    {
        MarkPast(s);
        if (!UnderWay(s)) return;
        if (s.HumLevel > DetectedHum || s.XiMeters >= 0.0) _unlockEntry?.Invoke();
        if (_table.TryCreate("megastorm_event_resync", Facts(("phase", PhaseWords(s.Phase, s.XiMeters))), out PdaRequest r))
        {
            _announcer.SubmitResync(r);
            _lastNonCritical = _clock();
        }
    }

    /// <summary>Marks every stage the sample is already at or past as done, so the backlog is not replayed.</summary>
    private void MarkPast(in MegastormSample s)
    {
        bool behind = s.XiMeters >= 0.0;
        if (s.HumLevel > DetectedHum || behind) _fired.Add("megastorm_resonance_detected");
        int stage = behind || s.HumLevel >= RisingSecondHum ? 2 : s.HumLevel >= RisingFirstHum ? 1 : 0;
        _risingFired = Math.Max(_risingFired, stage);
        if (stage > 0) _lastRising = _clock();
        if (behind || s.WildlifeQuiet > FaunaQuiet) _fired.Add("megastorm_fauna_silent");
        if (behind || s.Phase == MegastormPhase.Approach || s.Phase == MegastormPhase.GustFront) { _fired.Add("megastorm_acoustic_dead_zone"); _fired.Add("megastorm_airborne_approach"); }
        if (behind || s.Phase == MegastormPhase.GustFront) _fired.Add("megastorm_gust_warning");
        if (behind || s.TimeToArrivalSeconds <= ShelterSeconds) _fired.Add("megastorm_shelter_inadequate");
        if (behind || s.TimeToArrivalSeconds <= WallArrivalSeconds) _fired.Add("megastorm_wall_arrival");
        if (behind) { _fired.Add("megastorm_bullet_rain"); _fired.Add("megastorm_scythe_warning"); }
        if (s.XiMeters >= WakeXiMeters) _fired.Add("megastorm_wake_warning");
        if (s.XiMeters >= PassageXiMeters) _fired.Add("megastorm_passage");
        if (behind && s.TimeToClearSeconds <= 0.0) _fired.Add("megastorm_all_clear");
        else if (s.Phase == MegastormPhase.Aftermath) _fired.Add("megastorm_all_clear");
    }

    // ---- Triggers -------------------------------------------------------------------------------------------------------
    private void Evaluate(in MegastormSample s, MegastormPdaHazards? hazardInput)
    {
        MegastormPdaHazards h = hazardInput ?? MegastormPdaHazards.None;
        double tta = s.TimeToArrivalSeconds;
        bool ahead = s.XiMeters < 0.0;
        string distanceKm = Num(Math.Round(s.DistanceToEdgeMeters / 1000.0));

        // Critical suit warnings: never rate-limited, once per episode.
        if (ahead && tta <= ShelterSeconds && h.ShelterOpen)
            Critical("megastorm_shelter_inadequate", ("kpa", Num(h.WallOverpressureKpa)));
        if (ahead && s.Phase == MegastormPhase.GustFront)
            Critical("megastorm_gust_warning", ("wind_ms", Num(Math.Round(MegastormSound.GustOutflowMetersPerSecond))));
        if (ahead && tta <= WallArrivalSeconds)
            Critical("megastorm_wall_arrival", ("seconds", Num(Math.Ceiling(tta))), ("kpa", Num(h.WallOverpressureKpa)));
        if (!ahead && h.RainImpactKilowattsPerSquareMeter > 2.0f && h.Exposure > 0.3f)
            Critical("megastorm_bullet_rain", ("speed_ms", Num(Math.Round(h.RainSpeedMetersPerSecond))));
        if (s.XiMeters >= WakeXiMeters && s.XiMeters < PassageXiMeters)
            Critical("megastorm_wake_warning", ("kpa", Num(h.WakeUnderpressureKpa)));
        if (!ahead && h.ScytheWithin5Km && h.Exposure > 0.3f)
            Critical("megastorm_scythe_warning");

        // Non-critical, in table order.
        if (s.HumLevel > DetectedHum && !_fired.Contains("megastorm_resonance_detected")
            && NonCritical("megastorm_resonance_detected", ("distance_km", distanceKm), ("bearing", Bearing(s))))
            _unlockEntry?.Invoke();
        if (_risingFired < 2 && ahead && s.HumLevel >= (_risingFired == 0 ? RisingFirstHum : RisingSecondHum)
            && _clock() - _lastRising >= RisingSpacingSeconds && _fired.Contains("megastorm_resonance_detected"))
        {
            double etaMinutes = tta / 60.0;
            bool spoke = etaMinutes > FarEtaMinutes
                ? NonCritical("megastorm_resonance_rising_far", ("distance_km", distanceKm), ("eta_hours", Num(Math.Round(tta / 3600.0))))
                : NonCritical("megastorm_resonance_rising", ("distance_km", distanceKm), ("eta_minutes", Num(Math.Round(etaMinutes))));
            if (spoke) { _risingFired++; _lastRising = _clock(); }
        }
        if (ahead && s.WildlifeQuiet > FaunaQuiet)
            NonCritical("megastorm_fauna_silent", ("eta_minutes", Num(Math.Round(tta / 60.0))));
        if (ahead && s.AirSound == AirSoundState.Leading && s.DistanceToEdgeMeters <= AirborneAudibleMeters)
            NonCritical("megastorm_airborne_approach", ("distance_km", distanceKm));
        if (s.Phase == MegastormPhase.Approach && s.AirSound == AirSoundState.Silent)
            NonCritical("megastorm_acoustic_dead_zone", ("seconds", Num(Math.Round(tta))));
        if (s.Phase == MegastormPhase.Trailing)
            NonCritical("megastorm_passage", ("minutes", Num(Math.Round(s.TimeToClearSeconds / 60.0))));
        if (s.Phase == MegastormPhase.Aftermath)
            NonCritical("megastorm_all_clear", ("distance_km", Num(Math.Round(s.XiMeters / 1000.0))));
    }

    private void Critical(string id, params (string, string)[] facts)
    {
        if (!_fired.Add(id)) return;
        if (_table.TryCreate(id, Facts(facts), out PdaRequest r)) _announcer.Submit(r);
    }

    /// <summary>
    /// True when the line was submitted. The first speaking marks the id done for the episode (the rising lines are counted by the caller instead).
    /// A line held back by the 5 min spacing is retried while its condition still holds.
    /// </summary>
    private bool NonCritical(string id, params (string, string)[] facts)
    {
        bool rising = id.StartsWith("megastorm_resonance_rising", StringComparison.Ordinal);
        if (!rising && _fired.Contains(id)) return false;
        double now = _clock();
        if (now - _lastNonCritical < NonCriticalSpacingSeconds) return false;
        if (!_table.TryCreate(id, Facts(facts), out PdaRequest r)) return false;
        _fired.Add(id);
        _lastNonCritical = now;
        _announcer.Submit(r);
        return true;
    }

    // ---- Facts ----------------------------------------------------------------------------------------------------------
    private static IReadOnlyDictionary<string, string> Facts(params (string Key, string Value)[] pairs)
    {
        var d = new Dictionary<string, string>();
        foreach ((string k, string val) in pairs) if (!string.IsNullOrEmpty(k)) d[k] = val;
        return d;
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The storm lies on the side it comes from (against its direction of travel). Increasing around is taken as spinward.</summary>
    public static string Bearing(in MegastormSample s) => s.Direction > 0 ? "antispinward" : "spinward";

    public static string PhaseWords(MegastormPhase phase, double xi) => phase switch
    {
        MegastormPhase.Precursor => "precursor veil",
        MegastormPhase.Anvil => "anvil overhead",
        MegastormPhase.Approach => "calm before the front",
        MegastormPhase.GustFront => "gust front",
        MegastormPhase.Wall => "storm wall",
        MegastormPhase.Transition => "wall transition",
        MegastormPhase.Trailing => "trailing rain",
        MegastormPhase.Aftermath => "clearing",
        _ => xi < 0.0 ? "approach" : "aftermath"
    };
}
