using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Underground;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.Pda;

/// <summary>Prepared debug information, refreshed on the main thread without running a language model or changing simulation state.</summary>
public partial class PdaContextFeed : Node
{
    public const string KnowledgePath = "res://resources/pda/debug_knowledge.json";
    public const double RefreshSeconds = 1d;
    [Export] public bool AutoBind { get; set; } = true;
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath ClockPath { get; set; } = new("../RingDayNightCycle");
    [Export] public NodePath EclipsePath { get; set; } = new("../EclipseEvent");
    [Export] public NodePath WeatherPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath WeatherEffectsPath { get; set; } = new("../LocalWeatherEffects");
    [Export] public NodePath MegastormPath { get; set; } = new("../RingMegastorm");
    [Export] public NodePath UndergroundPath { get; set; } = new("../UndergroundMode");

    private readonly int _ownerThread = System.Environment.CurrentManagedThreadId;
    private readonly PdaContextChangeHistory _history = new();
    private readonly Dictionary<string, ulong> _providerIds = new(StringComparer.Ordinal);
    private IReadOnlyList<PdaKnowledgeEntry> _knowledge = Array.Empty<PdaKnowledgeEntry>();
    private string _knowledgeStatus = "not loaded";
    private bool _knowledgeLoaded;
    private long _version;
    private double _refresh;
    private PdaStateEvidence _stateEvidence = new();
    public PdaContextSnapshot? CurrentSnapshot { get; private set; }

    public override void _Ready()
    {
        if (!OS.IsDebugBuild()) { SetProcess(false); return; }
        ProcessMode = ProcessModeEnum.Always;
        ProcessPriority = 100;
        Capture();
    }

    public override void _Process(double delta)
    {
        _refresh += Math.Max(0d, delta);
        if (_refresh < RefreshSeconds) return;
        _refresh = 0d;
        Capture();
    }

    public PdaQuestionContext PrepareQuestion(string question, string? previousQuestion = null)
        => PdaContextComposer.Compose(Capture(), question, previousQuestion);

    public PdaContextSnapshot Capture()
    {
        if (System.Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("PDA context must be captured on its owning game thread.");
        LoadKnowledge();
        _stateEvidence = new PdaStateEvidence();
        var facts = new List<PdaContextFact>();
        double observationSeconds = Time.GetTicksMsec() / 1000d;
        _history.Prune(observationSeconds);
        Add(facts, "support.readings", "support", "Only the supplied readings are available. Player health, oxygen, inventory, damage attribution, scanned targets and undiscovered lore are not exposed. Missing readings mean unknown, not zero or safe.", "PdaContextFeed",
            "All readings are local unless stated. Missing means unknown, not safe. Health, oxygen, inventory, injury, scanned targets and undiscovered lore are unavailable. Weather potential is not observed rain; effects are not wetness/injury; modeled light is not visible brightness.");
        Add(facts, "clocks.observation", "clocks", $"Current observation session clock is {N(observationSeconds)} seconds (monotonic real time, separate from celestial and weather simulation clocks). Observed changes expire after 300 observation seconds.", "Time.GetTicksMsec",
            $"Observation clock {N(observationSeconds)} real seconds; history expires at 300 seconds. Celestial and weather clocks are separate simulations.");
        if (_knowledgeStatus != "loaded") Add(facts, "support.knowledge", "support", "Authored explanation catalog is unavailable: " + _knowledgeStatus + ".", KnowledgePath);

        RingVoxelExplorerController? explorer = Resolve<RingVoxelExplorerController>(ExplorerPath, "explorer");
        Read("location", facts, () => CaptureExplorer(explorer, facts));
        Read("celestial", facts, () => CaptureCelestial(Resolve<RingDayNightCycle>(ClockPath, "daynight"), facts));
        Read("celestial", facts, () => CaptureEclipse(Resolve<EclipseEventDirector>(EclipsePath, "eclipse"), facts));
        Read("weather", facts, () => CaptureWeather(Resolve<RingVolumetricCloudLayer>(WeatherPath, "weather"), explorer, facts));
        Read("weather", facts, () => CaptureWeatherEffects(Resolve<global::RingworldTechDemo.RingLocalWeatherEffects>(WeatherEffectsPath, "effects"), facts));
        Read("weather", facts, () => AddCloudLayer(facts));
        Read("megastorm", facts, () => CaptureMegastorm(Resolve<RingMegastorm>(MegastormPath, "megastorm"), explorer, facts));
        Read("underground", facts, () => CaptureUnderground(Resolve<RingUndergroundModeController>(UndergroundPath, "underground"), facts));
        CurrentSnapshot = new PdaContextSnapshot(++_version, DateTimeOffset.UtcNow, Array.AsReadOnly(facts.ToArray()), _knowledge, _history.Events, _stateEvidence);
        return CurrentSnapshot;
    }

    private void LoadKnowledge()
    {
        if (_knowledgeLoaded) return;
        _knowledgeLoaded = true;
        try
        {
            if (!FileAccess.FileExists(KnowledgePath)) { _knowledgeStatus = "resource missing"; return; }
            _knowledge = PdaContextComposer.ParseKnowledge(FileAccess.GetFileAsString(KnowledgePath));
            _knowledgeStatus = "loaded";
        }
        catch (Exception e) { _knowledgeStatus = e.GetType().Name; }
    }

    private T? Resolve<T>(NodePath path, string provider) where T : Node
    {
        T? node = AutoBind && OS.IsDebugBuild() && IsInsideTree() ? GetNodeOrNull<T>(path) : null;
        ulong id = node?.GetInstanceId() ?? 0;
        if (!_providerIds.TryGetValue(provider, out ulong previous) || previous != id)
        {
            _history.ResetPrefix(provider + ".");
            _providerIds[provider] = id;
        }
        return node;
    }

    private static void Read(string topic, List<PdaContextFact> facts, Action capture)
    {
        try { capture(); }
        catch (Exception e) { Add(facts, topic + ".read_error", topic, "Provider reading unavailable (" + e.GetType().Name + "); do not infer current conditions from this provider.", "PdaContextFeed"); }
    }

    private static void Add(List<PdaContextFact> facts, string id, string topic, string text, string source, string? modelText = null)
        => facts.Add(new PdaContextFact(id, topic, text, source, modelText));
    private void State(List<PdaContextFact> facts, string id, string topic, string text, string source, string key, string signature, string? modelText = null)
    {
        Add(facts, id, topic, text, source, modelText);
        _history.Observe(key, signature, topic + ": " + text, source, Time.GetTicksMsec() / 1000d);
    }
    private static string N(double value) => PdaContextComposer.Number(value);

    private void CaptureExplorer(RingVoxelExplorerController? e, List<PdaContextFact> facts)
    {
        if (e == null) { State(facts, "location.state", "location", "Player location and movement readings are unavailable.", "Explorer", "explorer.mode", "unavailable"); return; }
        var p = e.CanonicalPosition;
        Add(facts, "location.position", "location", $"Player canonical position: around {N(p.AroundMeters)} m; across {N(p.AcrossMeters)} m; vertical {N(p.VerticalMeters)} m. These are ring coordinates, not latitude/longitude.", "Explorer.CanonicalPosition",
            $"Ring coordinates: around {N(p.AroundMeters)} m, across {N(p.AcrossMeters)} m, vertical {N(p.VerticalMeters)} m.");
        string mode = e.NoclipEnabled ? "debug flight/noclip" : e.IsSwimming ? "swimming" : "normal physics movement";
        State(facts, "location.mode", "location", $"Player movement mode is {mode}; camera is {(e.IsCameraUnderwater ? "underwater" : "not underwater")}.", "Explorer", "explorer.mode", mode + ":" + e.IsCameraUnderwater,
            $"Movement: {mode}; camera {(e.IsCameraUnderwater ? "underwater" : "not underwater")}.");
        Add(facts, "location.motion", "location", $"Player speed is {N(e.CanonicalVelocity.Length())} m/s; altitude above local terrain {N(e.CurrentAltitudeAboveTerrainMeters)} m; gravity factor {N(e.CurrentGravityFactor)}.", "Explorer.CanonicalVelocity/CurrentAltitudeAboveTerrainMeters/CurrentGravityFactor",
            $"Speed {N(e.CanonicalVelocity.Length())} m/s; terrain-relative altitude {N(e.CurrentAltitudeAboveTerrainMeters)} m; gravity factor {N(e.CurrentGravityFactor)}.");
        if (e.IsSwimming || e.IsCameraUnderwater) Add(facts, "location.water", "location", $"Local player water depth is {N(e.CurrentWaterDepthMeters)} m. Oxygen is not measured by this feed.", "Explorer.CurrentWaterDepthMeters",
            $"Water depth {N(e.CurrentWaterDepthMeters)} m.");
    }

    private static string Minutes(double seconds) => PdaReadingText.Minutes(seconds);
    private static string Zero(double value, string format = "0.##") => PdaReadingText.Zero(value, format);

    private void CaptureCelestial(RingDayNightCycle? clock, List<PdaContextFact> facts)
    {
        if (clock == null || clock.CurrentFrame.ToSun.LengthSquared() < .01f)
        { State(facts, "celestial.state", "celestial", "Local celestial readings are unavailable or not initialized.", "RingDayNightCycle", "daynight.state", "unavailable"); return; }
        CelestialFrame f = clock.CurrentFrame;
        if (float.IsFinite(f.RawEclipseCoverage) && f.RawEclipseCoverage is >= 0f and <= 1f)
            _stateEvidence = _stateEvidence with { LocalEclipseCoverageFraction = f.RawEclipseCoverage };
        _stateEvidence = _stateEvidence with { Time = new PdaTimeEvidence(f.LightingPhase.ToString(), f.SunAboveHorizon, f.SecondsUntilSunrise, f.SecondsUntilSunset) };
        string next = f.SunAboveHorizon ? $"sunset in {Minutes(f.SecondsUntilSunset)}" : $"sunrise in {Minutes(f.SecondsUntilSunrise)}";
        Add(facts, "clocks.celestial", "clocks", $"Celestial game clock has run {Minutes(f.AbsoluteSeconds)} since the world began. This elapsed total is not the time of day and is separate from the weather clock.", "RingDayNightCycle.CurrentFrame.AbsoluteSeconds",
            $"Game clock {Minutes(f.AbsoluteSeconds)} elapsed total (not time of day).");
        State(facts, "celestial.state", "celestial", $"Time of day: {f.LightingPhase}. The real star is {(f.SunAboveHorizon ? "above" : "below")} the local horizon; {next}. Game minutes are simulation-clock minutes.", "RingDayNightCycle.CurrentFrame", "daynight.state", f.LightingPhase + ":" + f.SunAboveHorizon,
            $"Time of day {f.LightingPhase}; real star {(f.SunAboveHorizon ? "above" : "below")} horizon; {next}.");
        State(facts, "celestial.eclipse", "celestial", $"Local gas-giant eclipse is {f.EclipseType}; the giant covers {N(f.RawEclipseCoverage * 100d)} percent of the real star's disc at this observer. This reading does not describe every location on the ring.", "RingDayNightCycle.CurrentFrame", "daynight.eclipse", f.EclipseType.ToString(),
            $"Local giant eclipse {f.EclipseType}; real star covered {N(f.RawEclipseCoverage * 100d)} percent. Other ring locations may differ.");
        Add(facts, "celestial.horizon", "celestial", $"Real star elevation {N(f.SunElevationDegrees)} degrees; gas giant {(f.GiantAboveHorizon ? "above" : "below")} the local horizon.", "RingDayNightCycle.CurrentFrame",
            $"Real star elevation {N(f.SunElevationDegrees)} degrees; giant {(f.GiantAboveHorizon ? "above" : "below")} local horizon.");
        Add(facts, "celestial.contacts", "celestial", $"Predicted local sunrise in {Minutes(f.SecondsUntilSunrise)}; sunset in {Minutes(f.SecondsUntilSunset)}.", "RingDayNightCycle.CurrentFrame",
            $"Local sunrise in {Minutes(f.SecondsUntilSunrise)}; sunset in {Minutes(f.SecondsUntilSunset)}.");
    }

    private void CaptureEclipse(EclipseEventDirector? eclipse, List<PdaContextFact> facts)
    {
        if (eclipse == null)
        { State(facts, "eclipse.state", "celestial", "The scripted eclipse event and artificial hub-light readings are unavailable.", "EclipseEvent", "eclipse.state", "unavailable", "Eclipse event and hub-light readings unavailable."); return; }
        State(facts, "eclipse.state", "celestial", $"Scripted eclipse event phase is {eclipse.Phase}; event binding is {(eclipse.Bound ? "ready" : "not ready")}. This is distinct from local natural eclipse coverage.", "EclipseEventDirector.Phase/Bound", "eclipse.state", eclipse.Bound ? eclipse.Phase.ToString() : "unavailable",
            $"Scripted eclipse event {eclipse.Phase}; binding {(eclipse.Bound ? "ready" : "not ready")}. Natural eclipse is a separate reading.");
        var hub = eclipse.HubLight;
        bool active = hub.Active;
        bool overridden = eclipse.HubLevelOverrideActive || hub.Overridden;
        _stateEvidence = _stateEvidence with { HubRawActive = active, HubProviderBound = eclipse.Bound };
        if (eclipse.Bound || overridden) _stateEvidence = _stateEvidence with { HubActive = active };
        string cause = overridden ? "A debug override controls it independently of the natural eclipse." : active ? "It is driven by the configured eclipse event timeline." : "No artificial hub illumination is currently reported.";
        string compactCause = overridden ? "Cause: debug override, independent of natural eclipse." : active ? "Cause: eclipse event timeline." : "No hub illumination reported.";
        string label = active ? "ACTIVE (on)" : "INACTIVE (off)";
        State(facts, "eclipse.hub", "celestial", $"Artificial hub light state: {label}. {cause}", "EclipseEventDirector.HubLight/HubLevelOverrideActive", "eclipse.hub", active + ":" + overridden,
            $"Artificial hub light: {label}. {compactCause}");
        if (active) Add(facts, "eclipse.level", "celestial", $"Artificial hub-light output level {N(hub.Level)}, daylight contribution {N(hub.Day)}. These are lighting model outputs; they do not prove a source is visible through terrain or clouds.", "EclipseEventDirector.HubLight",
            $"Modeled hub output level {N(hub.Level)}; daylight contribution {N(hub.Day)}.");
    }

    internal static string WindDescription(double speed, double around, double across) => PdaReadingText.Wind(speed, around, across);

    private (double Base, double Top, double Coverage, double Vertical)? _cloudModel;
    private double _effectsCloudBase = double.NaN;

    private void CaptureWeather(RingVolumetricCloudLayer? weather, RingVoxelExplorerController? explorer, List<PdaContextFact> facts)
    {
        _cloudModel = null;
        if (weather == null || explorer == null)
        { State(facts, "weather.state", "weather", "Local weather-model readings are unavailable because the weather provider or player position is missing. Cached precipitation effects are a separate provider.", "RingVolumetricClouds", "weather.state", "unavailable", "Local weather model unavailable; precipitation effects are separate."); return; }
        Add(facts, "clocks.weather", "clocks", $"Weather simulation clock has run {N(weather.CloudSimulationTimeSeconds)} seconds, {(weather.CloudSimulationPaused ? "paused" : "running")}; it is separate from celestial time.", "RingVolumetricCloudLayer.CloudSimulationTimeSeconds",
            $"Weather clock {N(weather.CloudSimulationTimeSeconds)} simulation seconds, {(weather.CloudSimulationPaused ? "paused" : "running")}.");
        var w = weather.QueryLocalWeather(explorer.CanonicalPosition);
        _cloudModel = (w.CloudBaseAltitudeMeters, w.CloudTopAltitudeMeters, w.CloudCoverage * 100d, explorer.CanonicalPosition.VerticalMeters);
        _stateEvidence = _stateEvidence with
        {
            Wind = new PdaWindEvidence(w.WindMetersPerSecond.Length(), w.WindMetersPerSecond.X, w.WindMetersPerSecond.Y),
            PrecipitationPotential = w.PrecipitationPotential,
            Storm = (_stateEvidence.Storm ?? new PdaStormEvidence()) with { WeatherKind = w.SystemKind.ToString(), WeatherInfluence = w.SystemInfluence }
        };
        State(facts, "weather.state", "weather", $"At the player's position the local weather model reports {w.SystemKind}, influence {N(w.SystemInfluence)}. Cloud presentation switch is {(weather.Enabled ? "enabled" : "disabled")}; that switch alone does not determine whether simulated weather or rainfall is active.", "RingVolumetricCloudLayer.QueryLocalWeather/Enabled", "weather.state", w.SystemKind + ":" + weather.Enabled,
            $"Local weather {w.SystemKind}; influence {N(w.SystemInfluence)}. Cloud presentation {(weather.Enabled ? "enabled" : "disabled")}, not a rain/simulation switch.");
        string wind = WindDescription(w.WindMetersPerSecond.Length(), w.WindMetersPerSecond.X, w.WindMetersPerSecond.Y);
        Add(facts, "weather.air", "weather", $"Local wind is {wind}. Spinward is the direction of increasing around-ring distance; directions are relative to the ring, not compass points.", "RingVolumetricCloudLayer.QueryLocalWeather",
            $"Local wind {wind}. Directions are ring-relative (spinward = increasing around), not compass.");
        Add(facts, "weather.precipitation", "weather", $"Local precipitation potential is {Zero(w.PrecipitationPotential)} on the weather model scale. This is not an observed rain/hail reading; local precipitation-effect drive, when available, is reported separately.", "RingVolumetricCloudLayer.QueryLocalWeather.PrecipitationPotential",
            $"Local precipitation potential {Zero(w.PrecipitationPotential)}; not observed rain/hail.");
        try
        {
            var arrival = weather.ObserverStormArrival;
            string phase = arrival.Valid ? arrival.Phase.ToString() : "Clear";
            double toRain = arrival.Valid && phase != "Clear" && float.IsFinite(arrival.TimeToRainSeconds) ? arrival.TimeToRainSeconds : double.NaN;
            _stateEvidence = _stateEvidence with { Storm = (_stateEvidence.Storm ?? new PdaStormEvidence()) with { NaturalArrivalPhase = phase, NaturalSecondsToRain = double.IsFinite(toRain) ? toRain : null } };
            string detail = phase == "Clear" ? "No natural storm front is arriving at your position." : $"A natural storm front is at arrival phase {phase}{(double.IsFinite(toRain) ? "; rain expected in " + Minutes(toRain) : "")}.";
            Add(facts, "weather.arrival", "weather", detail + " This covers natural weather fronts, separate from the scripted megastorm.", "RingVolumetricCloudLayer.ObserverStormArrival", detail);
        }
        catch (Exception) { /* Arrival is optional evidence; the other weather readings stand without it. */ }
    }

    private void AddCloudLayer(List<PdaContextFact> facts)
    {
        if (_cloudModel is not { } c) return;
        double baseAltitude = double.IsFinite(_effectsCloudBase) ? _effectsCloudBase : c.Base;
        double? above = double.IsFinite(baseAltitude) && double.IsFinite(c.Vertical) ? baseAltitude - c.Vertical : null;
        _stateEvidence = _stateEvidence with { Cloud = new PdaCloudEvidence(baseAltitude, c.Top, above, c.Coverage) };
        string relation = above is { } a ? $", which is {N(Math.Abs(a))} m {(a >= 0 ? "above" : "below")} your position" : "";
        string text = $"Cloud layer: coverage {N(c.Coverage)} percent; cloud base at {N(baseAltitude)} m altitude{relation}; cloud top at {N(c.Top)} m altitude. Altitudes share the vertical scale of your position (vertical {N(c.Vertical)} m).";
        Add(facts, "weather.clouds", "weather", text, "RingVolumetricCloudLayer.QueryLocalWeather/LocalWeatherEffects.PrecipitationCloudBaseMeters",
            $"Clouds: coverage {N(c.Coverage)} percent; base {N(baseAltitude)} m altitude{relation}; top {N(c.Top)} m altitude.");
    }

    internal static bool HasInitializedPrecipitationEffectReading(bool processing, bool hasCurrentWindSnapshot, double cloudBaseMeters, float intensity)
        => processing && hasCurrentWindSnapshot && double.IsFinite(cloudBaseMeters) && float.IsFinite(intensity) && intensity >= 0f;

    private void CaptureWeatherEffects(global::RingworldTechDemo.RingLocalWeatherEffects? effects, List<PdaContextFact> facts)
    {
        _effectsCloudBase = double.NaN;
        if (effects == null || !HasInitializedPrecipitationEffectReading(effects.IsProcessing(), effects.HasCurrentWindSnapshot,
            effects.PrecipitationCloudBaseMeters, effects.PrecipitationIntensity))
        {
            if (effects != null) _stateEvidence = _stateEvidence with { PrecipitationInitialized = false };
            State(facts, "weather.effects.state", "weather", "Local precipitation-effect readings are unavailable or not initialized; zero-valued defaults cannot establish dry conditions. Rain/snow effect drive is unknown.",
                "LocalWeatherEffects.IsProcessing/HasCurrentWindSnapshot/PrecipitationCloudBaseMeters", "effects.mode", "unavailable",
                "Precipitation-effect drive unavailable/uninitialized, not dry.");
            return;
        }
        float intensity = effects.PrecipitationIntensity;
        _effectsCloudBase = effects.PrecipitationCloudBaseMeters;
        bool driven = intensity > .01f;
        _stateEvidence = _stateEvidence with
        {
            PrecipitationInitialized = true, PrecipitationDrive = intensity,
            PrecipitationMode = !driven ? PdaPrecipitationMode.Negligible : effects.Snowing ? PdaPrecipitationMode.Snow : PdaPrecipitationMode.Rain,
            PrecipitationSuspended = effects.UndergroundSuspended
        };
        string mode = !driven ? "negligible or zero local precipitation drive" : effects.Snowing ? "snow" : "rain";
        string shortMode = !driven ? "negligible (no rain or snow)" : effects.Snowing ? "snow" : "rain";
        string presentation = effects.UndergroundSuspended ? "suspended for underground mode; cached drive can remain nonzero" : "not suspended by underground mode";
        string drive = Zero(intensity, "0.###");
        State(facts, "weather.effects.state", "weather", $"Cached local precipitation-effect mode is {shortMode}. The measured effect drive is {drive}. Separately, 0.01 is only the classification threshold: drive must exceed 0.01 to count as rain or snow; the threshold is not the drive. Its presentation is {presentation}. This reports effect drive, not player wetness or damage.",
            "LocalWeatherEffects.PrecipitationIntensity/Snowing/UndergroundSuspended", "effects.mode", mode + ":" + effects.PrecipitationPhase + ":" + effects.UndergroundSuspended,
            $"Local precipitation effect: {shortMode}. Measured drive {drive}. (0.01 is only the classification threshold, not the drive.) Underground presentation {(effects.UndergroundSuspended ? "suspended; cached drive may remain" : "not suspended")}.");
        string phase = effects.PrecipitationPhase.ToString();
        Add(facts, "weather.effects.precipitation", "weather", $"Cached local smoothed precipitation-effect intensity is {drive}; observer cloud-relative phase {phase} (your position relative to the cloud layer). This is not a rain gauge, whole-ring rainfall or hail classification.",
            "LocalWeatherEffects.PrecipitationIntensity/PrecipitationPhase",
            $"Cached local effect intensity {drive}; cloud-relative phase {phase}. No hail classification.");
        Add(facts, "weather.effects.streaks", "weather", $"Nearby rain-streak presentation is {(effects.RainStreaksActive ? "active" : "inactive")}. It can include neighboring precipitation columns even when the observer's local effect drive is zero; it does not establish rain striking the player.",
            "LocalWeatherEffects.RainStreaksActive",
            $"Nearby rain streaks {(effects.RainStreaksActive ? "active" : "inactive")}; neighboring columns can show streaks with zero local drive. Not proof of rain striking player.");
    }

    private void CaptureMegastorm(RingMegastorm? storm, RingVoxelExplorerController? explorer, List<PdaContextFact> facts)
    {
        PdaStormEvidence Current() => _stateEvidence.Storm ?? new PdaStormEvidence();
        if (storm == null)
        { State(facts, "megastorm.state", "megastorm", "Megastorm readings are unavailable; activation and local hazards are unknown.", "RingMegastorm", "megastorm.state", "unavailable", "Megastorm unavailable; activation/hazards unknown."); return; }
        if (!storm.Triggered)
        {
            _stateEvidence = _stateEvidence with { Storm = Current() with { MegastormTriggered = false } };
            State(facts, "megastorm.state", "megastorm", "No scripted megastorm is currently triggered. Its local hazard and arrival readings do not apply.", "RingMegastorm.Triggered", "megastorm.state", "inactive", "Megastorm inactive: not triggered; hazard/arrival readings do not apply."); return;
        }
        if (explorer == null)
        { State(facts, "megastorm.state", "megastorm", "A scripted megastorm is triggered, but its local phase and hazards are unknown because player position is unavailable.", "RingMegastorm.Triggered", "megastorm.state", "triggered_no_position", "Megastorm triggered; player position missing, so local phase/hazards unknown."); return; }
        MegastormSample s = storm.QueryNow(explorer.CanonicalPosition);
        _stateEvidence = _stateEvidence with { Storm = Current() with { MegastormTriggered = true, MegastormActiveHere = s.Active, MegastormPhase = s.Phase.ToString(),
            MegastormSecondsToArrival = s.Active ? s.TimeToArrivalSeconds : null, MegastormSecondsToClear = s.Active ? s.TimeToClearSeconds : null, MegastormDistanceToEdgeMeters = s.Active ? s.DistanceToEdgeMeters : null } };
        State(facts, "megastorm.state", "megastorm", $"Scripted megastorm is triggered; local sample is {(s.Active ? "active" : "inactive")}, phase {s.Phase}.", "RingMegastorm.QueryNow", "megastorm.state", s.Active + ":" + s.Phase,
            $"Megastorm triggered; locally {(s.Active ? "active" : "inactive")}, phase {s.Phase}.");
        if (!s.Active) return;
        Add(facts, "megastorm.arrival", "megastorm", $"At the player, front-edge distance {N(s.DistanceToEdgeMeters)} m; arrival in {Minutes(s.TimeToArrivalSeconds)} (weather clock); clearing in {Minutes(s.TimeToClearSeconds)}. Negative arrival time means the edge has passed.", "RingMegastorm.QueryNow",
            $"Local front distance {N(s.DistanceToEdgeMeters)} m; arrival {Minutes(s.TimeToArrivalSeconds)}; clearing {Minutes(s.TimeToClearSeconds)}. Negative arrival means edge passed.");
        Add(facts, "megastorm.signals", "megastorm", $"Local storm danger index {N(s.Danger)}, structural hum {N(s.HumLevel)}, tremor {N(s.TremorLevel)}; airborne sound state {s.AirSound}.", "RingMegastorm.QueryNow",
            $"Local storm danger {N(s.Danger)}, structural hum {N(s.HumLevel)}, tremor {N(s.TremorLevel)}; airborne sound {s.AirSound}.");
        Add(facts, "megastorm.darkness", "megastorm", $"Local megastorm lighting-model extra-darkness weight is {N(s.ExtraDarkness)}; its darkness multiplier is {N(s.DarknessMultiplier)} (1 means neutral). These are this storm's modeled lighting contributions, not proof that the whole rendered scene or direct light has that brightness.", "RingMegastorm.QueryNow.ExtraDarkness/DarknessMultiplier",
            $"Local modeled storm extra-darkness {N(s.ExtraDarkness)}; darkness multiplier {N(s.DarknessMultiplier)} (1 neutral). Other light/darkness causes remain independent.");
        MegastormObserver o = storm.ObserverContext;
        Add(facts, "megastorm.shelter", "megastorm", $"Observer shelter class {o.Shelter}; wind-aligned rain exposure {N(o.RainExposure)}. These describe exposure, not player health or damage.", "RingMegastorm.ObserverContext",
            $"Local shelter {o.Shelter}; wind-aligned rain exposure {N(o.RainExposure)}. Exposure is not safety or injury.");
        MegastormHazards h = s.Hazards;
        string scythe = float.IsPositiveInfinity(h.NearestScytheDistanceMeters) ? "no live scythe is reported" : "nearest live scythe distance " + N(h.NearestScytheDistanceMeters) + " m";
        Add(facts, "megastorm.hazards", "megastorm", $"Local storm-model overpressure {N(h.OverpressureKPa)} kPa; pressure deficit {N(h.PressureDeficitKPa)} kPa; rain-impact power {N(h.RainImpactPowerWm2)} W/m2; {scythe}. These are hazard outputs, not evidence of injury.", "RingMegastorm.QueryNow.Hazards",
            $"Local modeled overpressure {N(h.OverpressureKPa)} kPa; pressure deficit {N(h.PressureDeficitKPa)} kPa; rain-impact power {N(h.RainImpactPowerWm2)} W/m2; {scythe}.");
    }

    private void CaptureUnderground(RingUndergroundModeController? underground, List<PdaContextFact> facts)
    {
        if (underground == null || !underground.IsAvailable)
        { State(facts, "underground.state", "underground", "Underground enclosure readings are unavailable; the feed cannot establish that the player is in a cave.", "UndergroundMode.IsAvailable", "underground.state", "unavailable", "Enclosure unavailable; cave location unknown."); return; }
        string where = underground.Mode switch
        {
            UndergroundMode.Surface => "on the surface (not underground)",
            UndergroundMode.Underground => "UNDERGROUND (enclosed)",
            UndergroundMode.Entering => "entering underground (in transition)",
            _ => "exiting to the surface (in transition)"
        };
        _stateEvidence = _stateEvidence with { Enclosure = new PdaEnclosureEvidence(underground.Mode.ToString(), underground.DepthMeters, underground.Openness, underground.LightingDarkened) };
        State(facts, "underground.state", "underground", $"Enclosure: you are {where}. Enclosure mode {underground.Mode}; underground lighting is {(underground.LightingDarkened ? "applied" : "not applied")}. Mode override is {underground.ModeOverride}.", "UndergroundMode.Mode/LightingDarkened/ModeOverride", "underground.state", underground.Mode + ":" + underground.LightingDarkened + ":" + underground.ModeOverride,
            $"Enclosure: {where}; underground darkening {(underground.LightingDarkened ? "applied" : "not applied")}; override {underground.ModeOverride}.");
        string depth = PdaReadingText.Depth(underground.DepthMeters);
        Add(facts, "underground.enclosure", "underground", $"Position relative to terrain: {depth}; openness {N(underground.Openness)} (1 is open sky); enclosure blend {N(underground.EnclosureBlend)}; cave fog weight {N(underground.CaveFogWeight)}.", "UndergroundMode.DepthMeters/Openness/EnclosureBlend/CaveFogWeight",
            $"Terrain-relative position: {depth}; openness {N(underground.Openness)} (1 open sky); enclosure blend {N(underground.EnclosureBlend)}; cave fog {N(underground.CaveFogWeight)}.");
    }
}
