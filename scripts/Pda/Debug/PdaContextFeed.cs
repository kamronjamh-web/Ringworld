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
        Read("megastorm", facts, () => CaptureMegastorm(Resolve<RingMegastorm>(MegastormPath, "megastorm"), explorer, facts));
        Read("underground", facts, () => CaptureUnderground(Resolve<RingUndergroundModeController>(UndergroundPath, "underground"), facts));
        CurrentSnapshot = new PdaContextSnapshot(++_version, DateTimeOffset.UtcNow, Array.AsReadOnly(facts.ToArray()), _knowledge, _history.Events);
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

    private void CaptureCelestial(RingDayNightCycle? clock, List<PdaContextFact> facts)
    {
        if (clock == null || clock.CurrentFrame.ToSun.LengthSquared() < .01f)
        { State(facts, "celestial.state", "celestial", "Local celestial readings are unavailable or not initialized.", "RingDayNightCycle", "daynight.state", "unavailable"); return; }
        CelestialFrame f = clock.CurrentFrame;
        Add(facts, "clocks.celestial", "clocks", $"Celestial simulation clock is {N(f.AbsoluteSeconds)} seconds. This is separate from the weather simulation clock.", "RingDayNightCycle.CurrentFrame.AbsoluteSeconds",
            $"Celestial clock {N(f.AbsoluteSeconds)} simulation seconds.");
        State(facts, "celestial.state", "celestial", $"Local natural lighting phase is {f.LightingPhase}; the real star is {(f.SunAboveHorizon ? "above" : "below")} the local horizon.", "RingDayNightCycle.CurrentFrame", "daynight.state", f.LightingPhase + ":" + f.SunAboveHorizon,
            $"Natural light phase {f.LightingPhase}; real star {(f.SunAboveHorizon ? "above" : "below")} local horizon.");
        State(facts, "celestial.eclipse", "celestial", $"Local gas-giant eclipse is {f.EclipseType}; the giant covers {N(f.RawEclipseCoverage * 100d)} percent of the real star's disc at this observer. This reading does not describe every location on the ring.", "RingDayNightCycle.CurrentFrame", "daynight.eclipse", f.EclipseType.ToString(),
            $"Local giant eclipse {f.EclipseType}; real star covered {N(f.RawEclipseCoverage * 100d)} percent. Other ring locations may differ.");
        Add(facts, "celestial.horizon", "celestial", $"Real star elevation {N(f.SunElevationDegrees)} degrees; gas giant {(f.GiantAboveHorizon ? "above" : "below")} the local horizon.", "RingDayNightCycle.CurrentFrame",
            $"Real star elevation {N(f.SunElevationDegrees)} degrees; giant {(f.GiantAboveHorizon ? "above" : "below")} local horizon.");
        Add(facts, "celestial.contacts", "celestial", $"Predicted local sunrise in {N(f.SecondsUntilSunrise)} celestial seconds; sunset in {N(f.SecondsUntilSunset)} celestial seconds. These durations are simulation time.", "RingDayNightCycle.CurrentFrame",
            $"Local sunrise in {N(f.SecondsUntilSunrise)} celestial seconds; sunset in {N(f.SecondsUntilSunset)} celestial seconds.");
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
        string cause = overridden ? "A debug override controls it independently of the natural eclipse." : active ? "It is driven by the configured eclipse event timeline." : "No artificial hub illumination is currently reported.";
        string compactCause = overridden ? "Cause: debug override, independent of natural eclipse." : active ? "Cause: eclipse event timeline." : "No hub illumination reported.";
        State(facts, "eclipse.hub", "celestial", $"Artificial hub-light output is {(active ? "active" : "inactive")}. {cause}", "EclipseEventDirector.HubLight/HubLevelOverrideActive", "eclipse.hub", active + ":" + overridden,
            $"Artificial hub light {(active ? "active" : "inactive")}. {compactCause}");
        if (active) Add(facts, "eclipse.level", "celestial", $"Artificial hub-light output level {N(hub.Level)}, daylight contribution {N(hub.Day)}. These are lighting model outputs; they do not prove a source is visible through terrain or clouds.", "EclipseEventDirector.HubLight",
            $"Modeled hub output level {N(hub.Level)}; daylight contribution {N(hub.Day)}.");
    }

    private void CaptureWeather(RingVolumetricCloudLayer? weather, RingVoxelExplorerController? explorer, List<PdaContextFact> facts)
    {
        if (weather == null || explorer == null)
        { State(facts, "weather.state", "weather", "Local weather-model readings are unavailable because the weather provider or player position is missing. Cached precipitation effects are a separate provider.", "RingVolumetricClouds", "weather.state", "unavailable", "Local weather model unavailable; precipitation effects are separate."); return; }
        Add(facts, "clocks.weather", "clocks", $"Weather simulation clock is {N(weather.CloudSimulationTimeSeconds)} seconds, {(weather.CloudSimulationPaused ? "paused" : "running")}; it is separate from celestial time.", "RingVolumetricCloudLayer.CloudSimulationTimeSeconds",
            $"Weather clock {N(weather.CloudSimulationTimeSeconds)} simulation seconds, {(weather.CloudSimulationPaused ? "paused" : "running")}.");
        var w = weather.QueryLocalWeather(explorer.CanonicalPosition);
        State(facts, "weather.state", "weather", $"At the player's position the local weather model reports {w.SystemKind}, influence {N(w.SystemInfluence)}. Cloud presentation switch is {(weather.Enabled ? "enabled" : "disabled")}; that switch alone does not determine whether simulated weather or rainfall is active.", "RingVolumetricCloudLayer.QueryLocalWeather/Enabled", "weather.state", w.SystemKind + ":" + weather.Enabled,
            $"Local weather {w.SystemKind}; influence {N(w.SystemInfluence)}. Cloud presentation {(weather.Enabled ? "enabled" : "disabled")}, not a rain/simulation switch.");
        Add(facts, "weather.air", "weather", $"Local weather wind speed {N(w.WindMetersPerSecond.Length())} m/s; around component {N(w.WindMetersPerSecond.X)} m/s, across component {N(w.WindMetersPerSecond.Y)} m/s.", "RingVolumetricCloudLayer.QueryLocalWeather",
            $"Local wind {N(w.WindMetersPerSecond.Length())} m/s; around {N(w.WindMetersPerSecond.X)} m/s, across {N(w.WindMetersPerSecond.Y)} m/s.");
        Add(facts, "weather.clouds", "weather", $"Local model cloud coverage {N(w.CloudCoverage * 100d)} percent; cloud base {N(w.CloudBaseAltitudeMeters)} m and top {N(w.CloudTopAltitudeMeters)} m.", "RingVolumetricCloudLayer.QueryLocalWeather",
            $"Local cloud coverage {N(w.CloudCoverage * 100d)} percent; base {N(w.CloudBaseAltitudeMeters)} m, top {N(w.CloudTopAltitudeMeters)} m.");
        Add(facts, "weather.precipitation", "weather", $"Local precipitation potential is {N(w.PrecipitationPotential)} on the weather model scale. This is not an observed rain/hail reading; local precipitation-effect drive, when available, is reported separately.", "RingVolumetricCloudLayer.QueryLocalWeather.PrecipitationPotential",
            $"Local precipitation potential {N(w.PrecipitationPotential)}; not observed rain/hail.");
    }

    internal static bool HasInitializedPrecipitationEffectReading(bool processing, bool hasCurrentWindSnapshot, double cloudBaseMeters, float intensity)
        => processing && hasCurrentWindSnapshot && double.IsFinite(cloudBaseMeters) && float.IsFinite(intensity) && intensity >= 0f;

    private void CaptureWeatherEffects(global::RingworldTechDemo.RingLocalWeatherEffects? effects, List<PdaContextFact> facts)
    {
        if (effects == null || !HasInitializedPrecipitationEffectReading(effects.IsProcessing(), effects.HasCurrentWindSnapshot,
            effects.PrecipitationCloudBaseMeters, effects.PrecipitationIntensity))
        {
            State(facts, "weather.effects.state", "weather", "Local precipitation-effect readings are unavailable or not initialized; zero-valued defaults cannot establish dry conditions. Rain/snow effect drive is unknown.",
                "LocalWeatherEffects.IsProcessing/HasCurrentWindSnapshot/PrecipitationCloudBaseMeters", "effects.mode", "unavailable",
                "Precipitation-effect drive unavailable/uninitialized, not dry.");
            return;
        }
        float intensity = effects.PrecipitationIntensity;
        bool driven = intensity > .01f;
        string mode = !driven ? "negligible or zero local precipitation drive" : effects.Snowing ? "snow" : "rain";
        string presentation = effects.UndergroundSuspended ? "suspended for underground mode; cached drive can remain nonzero" : "not suspended by underground mode";
        State(facts, "weather.effects.state", "weather", $"Cached local precipitation-effect mode is {mode} (drive classification threshold 0.01). Its presentation is {presentation}. This reports effect drive, not player wetness or damage.",
            "LocalWeatherEffects.PrecipitationIntensity/Snowing/UndergroundSuspended", "effects.mode", mode + ":" + effects.PrecipitationPhase + ":" + effects.UndergroundSuspended,
            $"Local effect mode {mode}; drive threshold 0.01. Underground presentation {(effects.UndergroundSuspended ? "suspended; cached drive may remain" : "not suspended")}.");
        Add(facts, "weather.effects.precipitation", "weather", $"Cached local smoothed precipitation-effect intensity is {PdaContextComposer.Number(intensity, "0.###")}; observer cloud-relative phase {effects.PrecipitationPhase}; local cloud base {N(effects.PrecipitationCloudBaseMeters)} canonical vertical metres. This is not a rain gauge, whole-ring rainfall or hail classification.",
            "LocalWeatherEffects.PrecipitationIntensity/PrecipitationPhase/PrecipitationCloudBaseMeters",
            $"Cached local effect intensity {PdaContextComposer.Number(intensity, "0.###")}; cloud-relative phase {effects.PrecipitationPhase}; cloud base {N(effects.PrecipitationCloudBaseMeters)} canonical vertical metres. No hail classification.");
        Add(facts, "weather.effects.streaks", "weather", $"Nearby rain-streak presentation is {(effects.RainStreaksActive ? "active" : "inactive")}. It can include neighboring precipitation columns even when the observer's local effect drive is zero; it does not establish rain striking the player.",
            "LocalWeatherEffects.RainStreaksActive",
            $"Nearby rain streaks {(effects.RainStreaksActive ? "active" : "inactive")}; neighboring columns can show streaks with zero local drive. Not proof of rain striking player.");
    }

    private void CaptureMegastorm(RingMegastorm? storm, RingVoxelExplorerController? explorer, List<PdaContextFact> facts)
    {
        if (storm == null)
        { State(facts, "megastorm.state", "megastorm", "Megastorm readings are unavailable; activation and local hazards are unknown.", "RingMegastorm", "megastorm.state", "unavailable", "Megastorm unavailable; activation/hazards unknown."); return; }
        if (!storm.Triggered)
        { State(facts, "megastorm.state", "megastorm", "No scripted megastorm is currently triggered. Its local hazard and arrival readings do not apply.", "RingMegastorm.Triggered", "megastorm.state", "inactive", "Megastorm inactive: not triggered; hazard/arrival readings do not apply."); return; }
        if (explorer == null)
        { State(facts, "megastorm.state", "megastorm", "A scripted megastorm is triggered, but its local phase and hazards are unknown because player position is unavailable.", "RingMegastorm.Triggered", "megastorm.state", "triggered_no_position", "Megastorm triggered; player position missing, so local phase/hazards unknown."); return; }
        MegastormSample s = storm.QueryNow(explorer.CanonicalPosition);
        State(facts, "megastorm.state", "megastorm", $"Scripted megastorm is triggered; local sample is {(s.Active ? "active" : "inactive")}, phase {s.Phase}.", "RingMegastorm.QueryNow", "megastorm.state", s.Active + ":" + s.Phase,
            $"Megastorm triggered; locally {(s.Active ? "active" : "inactive")}, phase {s.Phase}.");
        if (!s.Active) return;
        Add(facts, "megastorm.arrival", "megastorm", $"At the player, front-edge distance {N(s.DistanceToEdgeMeters)} m; arrival in {N(s.TimeToArrivalSeconds)} weather seconds; clearing in {N(s.TimeToClearSeconds)} weather seconds. Negative arrival time means the edge has passed.", "RingMegastorm.QueryNow",
            $"Local front distance {N(s.DistanceToEdgeMeters)} m; arrival {N(s.TimeToArrivalSeconds)} weather seconds; clearing {N(s.TimeToClearSeconds)} weather seconds. Negative arrival means edge passed.");
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
        State(facts, "underground.state", "underground", $"Local enclosure mode is {underground.Mode}; underground lighting is {(underground.LightingDarkened ? "applied" : "not applied")}. Mode override is {underground.ModeOverride}.", "UndergroundMode.Mode/LightingDarkened/ModeOverride", "underground.state", underground.Mode + ":" + underground.LightingDarkened + ":" + underground.ModeOverride,
            $"Local enclosure {underground.Mode}; underground darkening {(underground.LightingDarkened ? "applied" : "not applied")}; override {underground.ModeOverride}.");
        Add(facts, "underground.enclosure", "underground", $"Local depth {N(underground.DepthMeters)} m; openness {N(underground.Openness)}; enclosure blend {N(underground.EnclosureBlend)}; cave fog weight {N(underground.CaveFogWeight)}.", "UndergroundMode.DepthMeters/Openness/EnclosureBlend/CaveFogWeight",
            $"Local depth {N(underground.DepthMeters)} m; openness {N(underground.Openness)}; enclosure blend {N(underground.EnclosureBlend)}; cave fog weight {N(underground.CaveFogWeight)}.");
    }
}
