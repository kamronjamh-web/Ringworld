using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;

namespace RingworldTechDemo.UI;

/// <summary>Grave-key "Eclipse event" sections (docs/eclipse-false-sun-spec.md G.2, E1: timeline and debug; E2: eclipse darkness; E3+ add lighting/beams/audio here).</summary>
public partial class RingLiveTuningDrawer
{
    private sealed record EclipseSliderSpec(string Field, string Label, string Tooltip, double? Step = null);

    private static readonly EclipseSliderSpec[] EclipseDarknessSliders =
    {
        new("EclipseAmbientScale", "Ambient scale", "Environment ambient (and fog light) energy at totality, as a fraction of the night value. 0.15 turns 0.30 into 0.045."),
        new("EclipseAtmosphereNightScale", "Atmosphere night scale", "Multiplier on the atmosphere night colour at totality (haze emission, far-arch glow)."),
        new("EclipseSkyNightScale", "Sky night scale", "Multiplier on the night sky palette at totality."),
        new("EclipseCloudAmbientScale", "Cloud ambient scale", "Multiplier on the cloud night ambient at totality. Never below 0.3: darker clouds turn see-through."),
        new("EclipseStarVisibility", "Star visibility", "Star visibility reached at totality (never dims stars the night sky already shows)."),
    };

    private static readonly EclipseSliderSpec[] EclipseTimelineSliders =
    {
        new("ParallaxSafetySeconds", "Window margin (s)", "Shrinks the whole-ring totality window at both ends so the event never overlaps the real sun at any longitude."),
        new("PreRollSeconds", "Pre-roll (s)", "Warning period before the event window opens. Used by Force eclipse now and the scrub range."),
        new("DarknessBeatSeconds", "Darkness beat (s)", "Seconds of near pitch black after the window opens, before the main spire fires."),
        new("MainClimbSeconds", "Main beam climb (s)", "Time the main spire beam takes to reach the hub."),
        new("MainClimbExponent", "Main climb exponent", "Beam tip fraction is u^exponent: higher values accelerate the climb."),
        new("KindleSeconds", "Kindle (s)", "Seconds for the hub to ramp to the kindle level after the main beam arrives."),
        new("KindleLevel", "Kindle level", "Hub brightness fraction reached at the end of the kindle."),
        new("IgnitionDelaySeconds", "Ignition delay (s)", "Pause between the kindle and the bidirectional ignition pulse."),
        new("IgnitionSeconds", "Ignition half-ring (s)", "Seconds for a front to reach the antipode. Used only when the ignition speed mode is Manual."),
        new("RegularClimbSeconds", "Regular beam climb (s)", "Time each regular spire beam takes to reach the hub."),
        new("RampGamma", "Ramp gamma", "Exponent of the perceptual brightness ramp. Used only in Perceptual ramp mode."),
        new("RampStepSmoothSeconds", "Ramp step smoothing (s)", "Smooths each beam's brightness step as it lands at the hub."),
        new("IgnitionPlateauLevel", "Chain plateau level", "How brightly the already-ignited chain links stay lit behind a front."),
        new("MeetingFlareSeconds", "Meeting flare (s)", "Length of the flare where the two fronts meet at the antipode."),
        new("CrescendoSeconds", "Crescendo (s)", "Time all beams stay lit at full brightness before the hand-off."),
        new("HandoffFadeSeconds", "Hand-off fade (s)", "Time the regular beams take to fade when the main spire takes over."),
    };

    private static readonly EclipseSliderSpec[] EclipseSustainSliders =
    {
        new("OrbTravelSeconds", "Orb travel (s)", "Time a refuel orb takes from the spire base to the hub. The hub heartbeat lands on its arrival, so the hub pulses this long after a row launches. Changing it rebuilds the timeline."),
        new("FlashClimbSeconds", "Orb launch fade-in (s)", "Time a refuel orb takes to fade in at the spire base (also the bore light kick at launch)."),
        new("FlashHoldSeconds", "Orb hold at hub (s)", "Time a refuel orb stays at full strength at the hub."),
        new("FlashFadeSeconds", "Orb fade at hub (s)", "Time a refuel orb takes to fade out at the hub."),
        new("HeartbeatKick", "Heartbeat kick", "Hub brightness kick each time a refuel orb arrives."),
        new("HeartbeatTauSeconds", "Heartbeat decay (s)", "Decay time of the heartbeat kick."),
        new("RelaunchKick", "Relaunch kick", "Hub brightness kick at the main spire relaunch flare."),
        new("BreathAmplitude", "Breath amplitude", "Slow brightness breathing during Sustain."),
        new("BreathPeriodSeconds", "Breath period (s)", "Period of the breathing."),
        new("ReturnTailSeconds", "Return tail (s)", "Return-phase length when the player's own third contact is unknown."),
    };

    private static readonly EclipseSliderSpec[] EclipseCollapseSliders =
    {
        new("CollapseLeadSeconds", "Collapse lead (s)", "How long before the window closes the instability begins. The four stages are scaled to fit this lead."),
        new("InstabilitySeconds", "Instability (s)", "Length of the growing flicker."),
        new("FlickerMax", "Flicker depth", "Maximum brightness dip while unstable."),
        new("FlickerHzStart", "Flicker Hz start", "Flicker frequency at the start of the instability."),
        new("FlickerHzEnd", "Flicker Hz end", "Flicker frequency at the end of the instability."),
        new("OverloadSeconds", "Overload (s)", "Length of the overload flare."),
        new("OverloadGain", "Overload gain", "Peak brightness multiple of the overload flare."),
        new("ImplosionSeconds", "Implosion (s)", "Length of the implosion."),
        new("BeamRetractSeconds", "Beam retract (s)", "Time the beams take to retract when cut."),
        new("ShockwaveSeconds", "Shockwave (s)", "Length of the expanding light shell."),
        new("ShockwaveArrivalFraction", "Shock arrival fraction", "Fraction of the shockwave at which it reaches the ground."),
        new("ShockFlashLevel", "Ground flash level", "Extra brightness of the ground flash on shockwave arrival."),
        new("ShockFlashSeconds", "Ground flash (s)", "Length of the ground flash."),
    };

    private static readonly EclipseSliderSpec[] EclipseLevelSliders =
    {
        new("RealSunEnergy", "Real sun energy", "StarLight energy at real noon, the reference for the hub energy fraction."),
        new("HubDirectFraction", "Hub energy fraction", "Direct light energy at full hub level, as a fraction of the real sun."),
        new("HubDayAtFull", "Hub day factor", "Day factor at full hub level (ambient, sky, haze, cloud ambient)."),
        new("HubLightRed", "Hub light red", "StarLight colour at full hub weight. The default is cool white (0.92, 0.97, 1.0)."),
        new("HubLightGreen", "Hub light green", "StarLight colour at full hub weight."),
        new("HubLightBlue", "Hub light blue", "StarLight colour at full hub weight."),
        new("HubSkyTintStrength", "Hub sky tint", "How far the sky palette leans toward the cool hub tint while the hub, not the real sun, lights it."),
        new("HubZenithHaloIntensity", "Zenith halo", "Brightness of the sky glow around the zenith at full hub level."),
    };

    private static readonly EclipseSliderSpec[] EclipseBeamSliders =
    {
        new("BeamCoreWidthMeters", "Core width (m)", "Bore diameter of the regular spire beams (Gaussian core)."),
        new("BeamHaloWidthMeters", "Halo width (m)", "Exponential halo width of the regular spire beams."),
        new("BeamMainCoreWidthMeters", "Main core width (m)", "Bore diameter of the main spire beam."),
        new("BeamMainHaloWidthMeters", "Main halo width (m)", "Exponential halo width of the main spire beam."),
        new("BeamMinPixelWidth", "Minimum pixel width", "A distant beam is never drawn thinner than this; the lost energy is partly given back by the far compensation."),
        new("BeamFarCompensation", "Far compensation", "How much of the energy lost by widening a distant beam to the minimum pixel width is given back. Lower than the relay grid's 0.6 so the far starburst reads."),
        new("BeamBaseEmission", "Base emission", "HDR brightness of a regular beam. The beams ignore the day factor by design."),
        new("BeamMainEmissionScale", "Main emission scale", "Brightness of the main beam relative to a regular beam."),
        new("BeamHaloAmplitude", "Halo amplitude", "Strength of the halo around the core."),
        new("BeamBaseFadeMeters", "Halo base fade (m)", "The halo fades in over this many metres above the beam origin so it does not start with a hard horizontal edge."),
        new("BeamCloseSoftenMeters", "Close-range softening (m)", "The beam light fades in over this many metres of view distance, so standing at the bore rim does not white out the screen. 0 turns it off."),
        new("BeamDayVisibility", "Day visibility", "Brightness factor applied only while the hub day factor is above 0.5, so the beams do not flatten the hub-lit sky."),
        new("BeamCoreDepthThreshold", "Depth core threshold", "Core coverage times intensity above which the depth core writes depth (clouds behind the beam stop dimming it)."),
        new("BeamCoreDepthMinPixels", "Depth core width (px)", "Minimum full width of the depth core footprint, so it survives the cloud pass's 2x2 depth reduction."),
        new("BeamSoftTipFraction", "Soft tip", "Length of the soft end of a climbing beam, as a fraction of the beam."),
        new("BeamGridEventPeak", "Ignition front peak", "Brightness multiplier of the relay grid pulse while the ignition fronts climb the arch both ways. The grid also ignores the hub day then, so the fronts stay bright."),
        new("BeamCloudWidening", "Cloud widening", "A near beam crossing the cloud deck widens its halo by 1 + this x the cloud coverage at the spire, over the deck span."),
        new("BeamCloudHaloBoost", "Cloud halo boost", "Extra halo brightness inside the cloud deck: 1 + this x the cloud coverage."),
        new("BeamCloudCoreCut", "Cloud core cut", "Where coverage times the deck mask exceeds this the depth-writing core is switched off, so the cloud pass blends that segment of the beam. The default 1.5 never cuts: a cut under a storm leaves only the halo, which Sunshine dims."),
        new("BeamCrownGlowEmission", "Crown glow emission", "HDR emission of the far stand-in's crown while its beam fires (written on fire, hand-off and cut only)."),
        new("BeamBoreLightBoost", "Bore light boost", "Factor on the near spire bore plug light while its beam fires, and the short kick when a refuel orb launches (1 = no boost)."),
        new("BeamOrbEaseExponent", "Orb ease exponent", "Orb position = (time / travel) to this power. 1 is constant speed; 3 lifts off over about 3 s, then rushes to the hub."),
        new("BeamOrbRadiusMeters", "Orb core radius (m)", "Gaussian radius of the white orb core."),
        new("BeamOrbHaloMeters", "Orb halo radius (m)", "E-fold radius of the teal glow around an orb."),
        new("BeamOrbMinPixelRadius", "Orb minimum radius (px)", "A distant orb is never drawn smaller than this; the lost energy is partly given back."),
        new("BeamOrbEmission", "Orb emission", "HDR brightness of a refuel orb."),
        new("BeamCloudLightIntensity", "Cloud light intensity", "How strongly the beams, orbs and grid pulses light the clouds they pass through (colour times this). 0 lights nothing."),
        new("BeamCloudLightRadiusMeters", "Cloud light radius (m)", "How far from its line a beam lights the cloud. Also the length scale that sets how soft the glow is."),
        new("BeamCloudLightMaxSegments", "Cloud light segments", "At most this many lights nearest the camera are fed to the cloud pass (1-8). Each costs a little per cloud sample near it.", 1d),
    };

    private static readonly EclipseSliderSpec[] EclipseHubBodySliders =
    {
        new("HubBodyCoreEmission", "Hub core emission", "HDR emission of the hub disc at hub level 1 (scaled by the level). 40 saturates to white at every level from the kindle up."),
        new("HubBodyRadiusKm", "Hub core radius (km)", "Core radius of the hub sun at full level, real km (30 km is 0.34 degrees from the floor). It grows from about 0.66 of this at the kindle."),
        new("HubBodyMinRadiusPixels", "Hub minimum radius (px)", "The hub disc is never drawn smaller than this, so the kindle still reads as a white point."),
        new("HubBodyHaloAmplitude", "Hub halo amplitude", "Peak emission of the teal glow around the hub disc at level 1; it rides the overload swell."),
        new("HubBodyHaloRadiusKm", "Hub halo radius (km)", "E-fold radius of the hub glow, real km (400 km is about 4.6 degrees from the floor)."),
        new("HubSkyInScatter", "Sky in-scatter", "Strength of the sky's blue forward scatter toward the hub, so the hub-lit sky reads blue and not grey. 0 leaves the sky shader exactly as before."),
    };

    private EclipseHubBeamLayer? _eclipseBeams;
    private EclipseHubBody? _eclipseBody;
    private Label? _eclipseBeamLive, _eclipseBodyLive;
    private EclipseEventDirector? _eclipseDirector;
    private GameplayEclipseEventDefaults? _eclipseDefaults;
    private string? _eclipseDefaultsPathOverride;
    private readonly Dictionary<string, HSlider> _eclipseSliders = new();
    private readonly Dictionary<string, CheckButton> _eclipseToggles = new();
    private readonly Dictionary<string, OptionButton> _eclipseOptions = new();
    private Label? _eclipseLive, _eclipseStatus, _eclipseRing, _eclipseDarkLive, _eclipseHubLive;
    private HSlider? _eclipseHubOverride;
    private CheckButton? _eclipseHubOverrideToggle, _eclipseGizmo;
    private OptionButton? _eclipsePhasePick;
    private HSlider? _eclipseScrub;
    private CheckButton? _eclipseFreeze, _eclipseLog;
    private Button? _eclipseForce, _eclipseJump;
    private double _eclipseSaveDelay, _eclipseSaveRetry, _eclipseLiveDelay;

    private void InitializeEclipseTuning()
    {
        _eclipseDirector = GetNodeOrNull<EclipseEventDirector>("../EclipseEvent");
        _eclipseDefaults = new GameplayEclipseEventDefaults(_eclipseDefaultsPathOverride);
        _eclipseDefaults.Load();
        _eclipseDefaults.Apply(_eclipseDirector);
    }

    private void ProcessEclipseTuning(double delta)
    {
        if (_open && _eclipseLive != null && (_eclipseLiveDelay -= delta) <= 0)
        {
            _eclipseLiveDelay = .25;
            _eclipseDirector ??= GetNodeOrNull<EclipseEventDirector>("../EclipseEvent");
            _eclipseLive.Text = _eclipseDirector?.DescribeStatus() ?? "Eclipse event director is unavailable in this scene.";
            if (_eclipseRing != null) _eclipseRing.Text = _eclipseDirector?.Engine.BuildRingStrip() ?? "";
            if (_eclipseDarkLive != null)
            {
                EclipseDarknessOutput d = _eclipseDirector?.DarknessOutput ?? EclipseDarknessOutput.Neutral;
                _eclipseDarkLive.Text = _eclipseDirector == null ? "Eclipse director is unavailable in this scene."
                    : $"Darkness weight {d.Weight:0.00}: ambient x{d.AmbientMultiplier:0.000}, atmosphere night x{d.AtmosphereNightMultiplier:0.000}, sky night x{d.SkyNightMultiplier:0.000}, cloud ambient x{d.CloudAmbientMultiplier:0.000}, star floor {d.StarFloor:0.00}";
            }
            if (_eclipseBeamLive != null)
            {
                _eclipseBeams ??= GetNodeOrNull<EclipseHubBeamLayer>("../EclipseBeams");
                _eclipseBeamLive.Text = _eclipseBeams?.DescribeStatus() ?? "Eclipse hub beams are unavailable in this scene.";
            }
            if (_eclipseBodyLive != null)
            {
                _eclipseBody ??= GetNodeOrNull<EclipseHubBody>("../EclipseHubBody");
                _eclipseBodyLive.Text = _eclipseBody?.DescribeStatus() ?? "The eclipse hub body is unavailable in this scene.";
            }
            if (_eclipseHubLive != null)
            {
                EclipseHubLightOutput h = _eclipseDirector?.HubLight ?? EclipseHubLightOutput.Neutral;
                _eclipseHubLive.Text = _eclipseDirector == null ? "Eclipse director is unavailable in this scene."
                    : $"Hub level {h.Level:0.00}{(h.Overridden ? " (OVERRIDE)" : "")}: light weight {_eclipseDirector.HubLightWeight:0.00}, energy {h.DirectEnergy:0.00}, day {h.Day:0.00}, colour ({h.ColorR:0.00}, {h.ColorG:0.00}, {h.ColorB:0.00})";
            }
            RefreshEclipseEditable();
        }
        if (_eclipseDefaults?.Dirty != true) return;
        _eclipseSaveDelay -= delta;
        _eclipseSaveRetry -= delta;
        if (_eclipseSaveDelay <= 0 && _eclipseSaveRetry <= 0 && !FlushEclipseDefaults()) _eclipseSaveRetry = 5;
    }

    public bool FlushEclipseDefaults()
    {
        bool saved = _eclipseDefaults?.Save() ?? true;
        RefreshEclipseStatus();
        return saved;
    }

    public void InstallEclipseDefaults(string isolatedPath)
    {
        if (!isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal) && !isolatedPath.StartsWith("user://generated/", StringComparison.Ordinal)) return;
        _eclipseDefaultsPathOverride = isolatedPath;
        if (_eclipseDefaults is null) return;
        _eclipseDefaults = new GameplayEclipseEventDefaults(isolatedPath);
        _eclipseDefaults.Load();
        _eclipseDefaults.Apply(_eclipseDirector);
        RefreshEclipseControls();
    }

    private void BuildEclipseSections(VBoxContainer tab)
    {
        var s = _eclipseDefaults?.Settings ?? new GameplayEclipseEventSettings();
        VBoxContainer darkness = TuningMenuUi.CreateSection(tab, "Eclipse darkness",
            "How black a gas-giant total eclipse gets. Applies to every eclipse on every world, with or without the false-sun event. Normal nights and days are not affected. The scales are the totality values and blend in with the eclipse coverage.");
        _eclipseDarkLive = new Label { Name = "EclipseDarknessLive", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        darkness.AddChild(_eclipseDarkLive);
        AddEclipseToggle(darkness, "EclipseDarknessEnabled", "Pitch-black eclipse", s.EclipseDarknessEnabled,
            "Off restores the old eclipse lighting: totality keeps the full night ambient.");
        foreach (EclipseSliderSpec spec in EclipseDarknessSliders) AddEclipseSlider(darkness, spec, s);

        VBoxContainer timeline =TuningMenuUi.CreateSection(tab, "Eclipse event: timeline",
            "The false-sun event runs inside the whole-ring totality window of each gas-giant eclipse. It follows the celestial time scale: at 10x the whole show is ten times faster. Needs a world with emitter sites.");
        _eclipseLive = new Label { Name = "EclipseLive", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        timeline.AddChild(_eclipseLive);
        AddEclipseToggle(timeline, "EventEnabled", "Event enabled", s.EventEnabled, "Off leaves the natural eclipse only.");
        AddEclipseOption(timeline, "IgnitionSpeedMode", "Ignition speed", new[] { "Match grid pulse", "Manual" }, s.IgnitionSpeedMode,
            "Match grid uses the relay pulse speed (150 s per half ring at the defaults). Manual uses the half-ring seconds below.");
        AddEclipseOption(timeline, "RampMode", "Brightness ramp", new[] { "Per beam (linear)", "Perceptual" }, s.RampMode,
            "Per beam adds an equal share of hub brightness for each beam that lands.");
        foreach (EclipseSliderSpec spec in EclipseTimelineSliders) AddEclipseSlider(timeline, spec, s);

        VBoxContainer sustain = TuningMenuUi.CreateSection(tab, "Eclipse event: sustain and refuel",
            "Hand-off to the main spire, then the normal grid pulse refuels the hub. By default the refuel flashes run on the celestial clock so they compress with the time scale.");
        AddEclipseToggle(sustain, "SuppressGridPulseDuringIgnition", "Suppress grid pulse during ignition", s.SuppressGridPulseDuringIgnition,
            "Hides the normal one-way grid pulse from the main fire until the hand-off so the bidirectional ignition reads cleanly.");
        AddEclipseOption(sustain, "RefuelClock", "Refuel clock", new[] { "Celestial (follows time scale)", "Weather (real time)" }, s.RefuelClock,
            "Which clock drives the sustain refuel flashes. Celestial keeps them in sync with the compressed timeline.");
        foreach (EclipseSliderSpec spec in EclipseSustainSliders) AddEclipseSlider(sustain, spec, s);

        VBoxContainer collapse = TuningMenuUi.CreateSection(tab, "Eclipse event: collapse",
            "Instability, overload flare, implosion and shockwave before totality ends. Cosmetic only.");
        foreach (EclipseSliderSpec spec in EclipseCollapseSliders) AddEclipseSlider(collapse, spec, s);

        VBoxContainer levels = TuningMenuUi.CreateSection(tab, "Eclipse event: lighting",
            "The hub sun lights every point from its own zenith (\"noon everywhere\"). Full hub level is 80 % of the real sun by default. The hub light is exactly off outside the event.");
        _eclipseHubLive = new Label { Name = "EclipseHubLive", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        levels.AddChild(_eclipseHubLive);
        foreach (EclipseSliderSpec spec in EclipseLevelSliders) AddEclipseSlider(levels, spec, s);

        VBoxContainer beams = TuningMenuUi.CreateSection(tab, "Eclipse event: beams and hub",
            "The 595 spire-to-hub beams: the nearest pillars in the main view and a starburst of hair-lines converging on the zenith hub in the far view. They exist only from just before the main fire until the collapse cuts them. The beams ignore the day factor and the background brightness by design.");
        _eclipseBeamLive = new Label { Name = "EclipseBeamLive", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        beams.AddChild(_eclipseBeamLive);
        AddEclipseToggle(beams, "BeamsEnabled", "Hub beams", s.BeamsEnabled, "Off removes both beam layers; the hub light and the timeline are unaffected.");
        AddEclipseOption(beams, "BeamOrigin", "Beam origin", new[] { "Crown (spire top)", "Base (bore iris)", "Reactor (preview)" }, s.BeamOrigin,
            "Where the beams rise from. Base is the bore iris, 200 m under the pad; Crown is the spire's crown focus; Reactor is the reactor exhaust top (2,961 m down, 1,922 m for the main spire), a preview until the reactors are placed. Changing it rebuilds only the beam transforms.");
        AddEclipseToggle(beams, "BeamFarLayerEnabled", "Far beam layer", s.BeamFarLayerEnabled, "The starburst of distant beams in the far viewport. Off leaves only the near pillars.");
        AddEclipseToggle(beams, "BeamDepthWriteCore", "Depth-writing core", s.BeamDepthWriteCore, "Clouds behind a beam no longer dim it. Off shows the cost-free halo only (clouds dim the beam).");
        AddEclipseToggle(beams, "BeamCloudDiffusionEnabled", "In-cloud diffusion", s.BeamCloudDiffusionEnabled,
            "A near beam widens and brightens its halo where it crosses the cloud deck (one weather query per near spire, 30 at most per eclipse), and its depth core is cut inside dense cloud.");
        AddEclipseToggle(beams, "BeamCrownGlowEnabled", "Stand-in crown glow", s.BeamCrownGlowEnabled, "The far stand-in's crown glows while its beam fires.");
        AddEclipseToggle(beams, "BeamCloudLightEnabled", "Beams light clouds", s.BeamCloudLightEnabled,
            "The beams and the refuel orbs light the Sunshine clouds they pass through from inside (segment lights in the cloud pass). Off, or outside the event, the clouds are exactly as before.");
        AddEclipseToggle(beams, "GridCloudLightEnabled", "Grid links light clouds", s.GridCloudLightEnabled,
            "The relay grid pulses also light the clouds they pass through (after the beams and orbs, in the remaining segments). Needs 'Beams light clouds'.");
        foreach (EclipseSliderSpec spec in EclipseBeamSliders) AddEclipseSlider(beams, spec, s);
        _eclipseBodyLive = new Label { Name = "EclipseHubBodyLive", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        beams.AddChild(_eclipseBodyLive);
        AddEclipseToggle(beams, "HubBodyEnabled", "Hub sun body", s.HubBodyEnabled,
            "The white disc and glow where the beams meet. A main-view proxy along the hub direction: clouds and overhead geometry still hide it, and it is not drawn in the far view.");
        foreach (EclipseSliderSpec spec in EclipseHubBodySliders) AddEclipseSlider(beams, spec, s);

        VBoxContainer debug = TuningMenuUi.CreateSection(tab, "Eclipse event: debug",
            "Not saved. Force, jump and scrub move the celestial clock (a real time jump, like the F9 time buttons) and emit one Resync.");
        _eclipseForce = new Button { Name = "EclipseForce", Text = "Force eclipse now", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "Jumps the celestial clock to the start of the pre-roll of the current or next eclipse. This changes world time." };
        _eclipseForce.Pressed += () => { _eclipseDirector?.ForceEclipseNow(); };
        debug.AddChild(_eclipseForce);
        var jumpRow = new HBoxContainer();
        _eclipsePhasePick = new OptionButton { Name = "EclipsePhasePick", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Phase to jump to." };
        foreach (EclipseEventPhase phase in Enum.GetValues<EclipseEventPhase>()) _eclipsePhasePick.AddItem(phase.ToString(), (int)phase);
        _eclipsePhasePick.Select(_eclipsePhasePick.GetItemIndex((int)EclipseEventPhase.MainFire));
        jumpRow.AddChild(_eclipsePhasePick);
        _eclipseJump = new Button { Name = "EclipseJump", Text = "Jump to phase", TooltipText = "Jumps the celestial clock to the start of the chosen phase. This changes world time." };
        _eclipseJump.Pressed += () => { if (_eclipsePhasePick != null) _eclipseDirector?.JumpToPhase((EclipseEventPhase)_eclipsePhasePick.GetSelectedId()); };
        jumpRow.AddChild(_eclipseJump);
        debug.AddChild(jumpRow);
        var scrubRow = new HBoxContainer { TooltipText = "Scrub the event timeline. Applies when released." };
        scrubRow.AddChild(new Label { Text = "Scrub (tau s)", CustomMinimumSize = new Vector2(160, 0) });
        _eclipseScrub = new HSlider { Name = "EclipseScrub", MinValue = -300, MaxValue = 4200, Step = 1, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Event time in seconds from the window start. Applies when released." };
        var scrubNumber = new Label { Text = "0", CustomMinimumSize = new Vector2(55, 0) };
        _eclipseScrub.ValueChanged += value => scrubNumber.Text = value.ToString("0");
        _eclipseScrub.DragEnded += changed => { if (changed && _eclipseScrub != null) _eclipseDirector?.ScrubToTau(_eclipseScrub.Value); };
        scrubRow.AddChild(_eclipseScrub);
        scrubRow.AddChild(scrubNumber);
        debug.AddChild(scrubRow);
        _eclipseFreeze = new CheckButton { Name = "EclipseFreeze", Text = "Freeze event", TooltipText = "Pauses the celestial clock. The weather clock and grid pulse keep running." };
        _eclipseFreeze.Toggled += on => _eclipseDirector?.SetFrozen(on);
        debug.AddChild(_eclipseFreeze);
        _eclipseLog = new CheckButton { Name = "EclipseLogEvents", Text = "Log events", TooltipText = "Prints every event to the console." };
        _eclipseLog.Toggled += on => { if (_eclipseDirector != null) _eclipseDirector.LogEvents = on; };
        debug.AddChild(_eclipseLog);
        _eclipseHubOverrideToggle = new CheckButton { Name = "EclipseHubOverride", Text = "Hub level override", TooltipText = "Forces the hub light on at the level below, even outside an eclipse (it then shows alongside the real sun). Visual only; not saved." };
        _eclipseHubOverrideToggle.Toggled += on => _eclipseDirector?.SetHubLevelOverride(on && _eclipseHubOverride != null ? (float)_eclipseHubOverride.Value : -1f);
        debug.AddChild(_eclipseHubOverrideToggle);
        var overrideRow = new HBoxContainer { TooltipText = "Hub level used while the override is on (0 to 2.5; 1 is the full sun at 80 %)." };
        overrideRow.AddChild(new Label { Text = "Override level", CustomMinimumSize = new Vector2(160, 0) });
        _eclipseHubOverride = new HSlider { Name = "EclipseHubOverrideLevel", MinValue = 0, MaxValue = 2.5, Step = .01, Value = 1, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Hub level used while the override is on." };
        var overrideNumber = new Label { Text = "1", CustomMinimumSize = new Vector2(55, 0) };
        _eclipseHubOverride.ValueChanged += value =>
        {
            overrideNumber.Text = value.ToString("0.##");
            if (_eclipseHubOverrideToggle?.ButtonPressed == true) _eclipseDirector?.SetHubLevelOverride((float)value);
        };
        overrideRow.AddChild(_eclipseHubOverride);
        overrideRow.AddChild(overrideNumber);
        debug.AddChild(overrideRow);
        _eclipseGizmo = new CheckButton { Name = "EclipseZenithGizmo", Text = "Show local zenith gizmo", TooltipText = "Draws your local zenith (green) and the StarLight direction (yellow) in front of the camera. Not saved." };
        _eclipseGizmo.Toggled += on => { if (_eclipseDirector != null) _eclipseDirector.ShowZenithGizmo = on; };
        debug.AddChild(_eclipseGizmo);
        _eclipseRing = new Label { Name = "EclipseRingStrip", TooltipText = "Fired sites along the loop; the main spire is at the left." };
        _eclipseRing.AddThemeFontSizeOverride("font_size", 11);
        debug.AddChild(_eclipseRing);

        var reset = new Button { Name = "EclipseReset", Text = "Reset eclipse event", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "Restores the eclipse event settings to their defaults." };
        reset.Pressed += ResetEclipse;
        tab.AddChild(reset);
        _eclipseStatus = new Label { Name = "EclipseStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        tab.AddChild(_eclipseStatus);
        RefreshEclipseEditable();
        RefreshEclipseStatus();
    }

    private static double EclipseStep(float min, float max)
    {
        float range = max - min;
        return range <= 1.05f ? .005 : range <= 10.5f ? .05 : range <= 120f ? .5 : 1;
    }

    private CheckButton AddEclipseToggle(VBoxContainer parent, string field, string text, bool pressed, string tooltip)
    {
        var toggle = new CheckButton { Name = "Eclipse" + field, Text = text, ButtonPressed = pressed, TooltipText = tooltip, Disabled = _eclipseDirector == null };
        toggle.Toggled += on => { if (_eclipseDefaults != null && _eclipseDefaults.SetBool(field, on)) QueueEclipseSettings(); };
        parent.AddChild(toggle);
        _eclipseToggles[field] = toggle;
        return toggle;
    }

    private OptionButton AddEclipseOption(VBoxContainer parent, string field, string label, string[] items, int selected, string tooltip)
    {
        var row = new HBoxContainer { TooltipText = tooltip };
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(160, 0), TooltipText = tooltip });
        var option = new OptionButton { Name = "Eclipse" + field, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = tooltip, Disabled = _eclipseDirector == null };
        foreach (string item in items) option.AddItem(item);
        option.Select(Mathf.Clamp(selected, 0, items.Length - 1));
        option.ItemSelected += index => { if (_eclipseDefaults != null && _eclipseDefaults.SetInt(field, (int)index)) QueueEclipseSettings(); };
        row.AddChild(option);
        parent.AddChild(row);
        _eclipseOptions[field] = option;
        return option;
    }

    private HSlider AddEclipseSlider(VBoxContainer parent, EclipseSliderSpec spec, GameplayEclipseEventSettings s)
    {
        var (min, max) = GameplayEclipseEventDefaults.RangeOf(spec.Field);
        var row = new HBoxContainer { TooltipText = spec.Tooltip };
        row.AddChild(new Label { Text = spec.Label, CustomMinimumSize = new Vector2(160, 0), TooltipText = spec.Tooltip });
        var slider = new HSlider
        {
            Name = "Eclipse" + spec.Field, MinValue = min, MaxValue = max, Step = spec.Step ?? EclipseStep(min, max),
            Value = (float)s.Get(spec.Field).AsDouble(), Editable = _eclipseDirector != null, TooltipText = spec.Tooltip,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var number = new Label { Text = slider.Value.ToString("0.###"), CustomMinimumSize = new Vector2(55, 0) };
        slider.ValueChanged += value =>
        {
            number.Text = value.ToString("0.###");
            if (_eclipseDefaults != null && _eclipseDefaults.SetFloat(spec.Field, (float)value)) QueueEclipseSettings();
        };
        row.AddChild(slider);
        row.AddChild(number);
        parent.AddChild(row);
        _eclipseSliders[spec.Field] = slider;
        return slider;
    }

    private void QueueEclipseSettings()
    {
        if (_eclipseDefaults == null) return;
        _eclipseDefaults.Apply(_eclipseDirector);
        _eclipseSaveDelay = .75;
        RefreshEclipseStatus();
    }

    private void ResetEclipse()
    {
        _eclipseDefaults?.Reset();
        _eclipseDefaults?.Apply(_eclipseDirector);
        RefreshEclipseControls();
        FlushEclipseDefaults();
    }

    private void RefreshEclipseControls()
    {
        if (_eclipseDefaults == null) return;
        var s = _eclipseDefaults.Settings;
        foreach (var (field, slider) in _eclipseSliders) SetEmitterSliderNoSignal(slider, (float)s.Get(field).AsDouble());
        foreach (var (field, toggle) in _eclipseToggles) toggle.SetPressedNoSignal(s.Get(field).AsBool());
        foreach (var (field, option) in _eclipseOptions) option.Select(s.Get(field).AsInt32());
        RefreshEclipseEditable();
        RefreshEclipseStatus();
    }

    private void RefreshEclipseEditable()
    {
        bool present = _eclipseDirector != null && GodotObject.IsInstanceValid(_eclipseDirector);
        bool bound = present && _eclipseDirector!.Bound;
        // Settings stay editable whenever the director exists; the debug actions need a bound event (a world with emitter sites).
        foreach (HSlider slider in _eclipseSliders.Values) if (slider.Editable != present) slider.Editable = present;
        foreach (CheckButton toggle in _eclipseToggles.Values) if (toggle.Disabled == present) toggle.Disabled = !present;
        foreach (OptionButton option in _eclipseOptions.Values) if (option.Disabled == present) option.Disabled = !present;
        foreach (BaseButton? button in new BaseButton?[] { _eclipseForce, _eclipseJump, _eclipseFreeze, _eclipseLog })
            if (button != null && button.Disabled == bound) button.Disabled = !bound;
        // The hub override and the zenith gizmo only need the director, not a bound event: they work on any world for lighting checks.
        foreach (BaseButton? button in new BaseButton?[] { _eclipseHubOverrideToggle, _eclipseGizmo })
            if (button != null && button.Disabled == present) button.Disabled = !present;
        if (_eclipseHubOverride != null && _eclipseHubOverride.Editable != present) _eclipseHubOverride.Editable = present;
        if (_eclipsePhasePick != null && _eclipsePhasePick.Disabled == bound) _eclipsePhasePick.Disabled = !bound;
        if (_eclipseScrub != null)
        {
            if (_eclipseScrub.Editable != bound) _eclipseScrub.Editable = bound;
            if (bound && _eclipseDirector!.Engine.TryGetScrubRange(out double lo, out double hi))
            {
                if (_eclipseScrub.MinValue != lo) _eclipseScrub.MinValue = lo;
                if (_eclipseScrub.MaxValue != hi) _eclipseScrub.MaxValue = hi;
                if (!_eclipseScrub.HasFocus()) _eclipseScrub.SetValueNoSignal(Math.Clamp(_eclipseDirector.State.Tau, lo, hi));
            }
        }
    }

    private void RefreshEclipseStatus()
    {
        if (_eclipseStatus == null || _eclipseDefaults == null) return;
        _eclipseStatus.Text = _eclipseDefaults.LastError.Length > 0 ? "Save error: " + _eclipseDefaults.LastError
            : _eclipseDefaults.Dirty ? "Eclipse event settings pending save." : "Eclipse event settings saved.";
    }
}
