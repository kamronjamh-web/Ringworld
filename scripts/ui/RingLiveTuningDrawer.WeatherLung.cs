using System;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private static readonly string[] WeatherLungSunModeNames = { "Auto (from the sun)", "Day", "Night", "Manual" };
    private WeatherLungStreamer? _weatherLungStreamer;
    private GameplayWeatherLungDefaults? _weatherLungDefaults;
    private string? _weatherLungDefaultsPathOverride;
    private readonly WeatherLungTeleport _weatherLungTeleport = new();
    private CheckButton? _weatherLungEnabled, _weatherLungAnimation, _weatherLungFreeze;
    private OptionButton? _weatherLungSunMode;
    private HSlider? _weatherLungCycle, _weatherLungVane, _weatherLungGill, _weatherLungRpm, _weatherLungLag, _weatherLungNozzle, _weatherLungHeat, _weatherLungSunManual,
        _weatherLungFullRate, _weatherLungReducedRate, _weatherLungReducedHz, _weatherLungDetailRange, _weatherLungDressingRange, _weatherLungDressingFade,
        _weatherLungFarProxy, _weatherLungLoad, _weatherLungUnload, _weatherLungCollision, _weatherLungAttachBudget, _weatherLungPhase;
    private Label? _weatherLungLive, _weatherLungResult, _weatherLungSaveStatus;
    private double _weatherLungSaveDelay, _weatherLungSaveRetry, _weatherLungLiveDelay;
    private WeatherLungTeleportPhase _weatherLungLastTeleportState = WeatherLungTeleportPhase.Idle;

    /// <summary>State of the last Weather Lung teleport: Idle, Holding, Arrived, TimedOut or Failed.</summary>
    public string WeatherLungTeleportState => _weatherLungTeleport.State.ToString();
    public string WeatherLungTeleportMessage => _weatherLungTeleport.Message;

    private void InitializeWeatherLungTuning()
    {
        _weatherLungStreamer = GetNodeOrNull<WeatherLungStreamer>("../WeatherLung");
        _weatherLungDefaults = new GameplayWeatherLungDefaults(_weatherLungDefaultsPathOverride);
        _weatherLungDefaults.Load();
        _weatherLungDefaults.Apply(_weatherLungStreamer);
    }

    private void ProcessWeatherLungTuning(double delta)
    {
        WeatherLungTeleportPhase state = _weatherLungTeleport.Poll(delta);
        if (state != _weatherLungLastTeleportState || state == WeatherLungTeleportPhase.Holding)
        {
            _weatherLungLastTeleportState = state;
            if (_weatherLungResult != null) _weatherLungResult.Text = _weatherLungTeleport.Message;
        }
        if (_open && _weatherLungLive != null && (_weatherLungLiveDelay -= delta) <= 0)
        {
            _weatherLungLiveDelay = .25;
            _weatherLungLive.Text = _weatherLungStreamer?.DescribeStatus() ?? "The Weather Lung is unavailable in this scene.";
            bool present = _weatherLungStreamer != null && !_weatherLungStreamer.Unavailable;
            foreach (HSlider? slider in WeatherLungSliders())
                if (slider != null && slider.Editable != present) slider.Editable = present;
            foreach (BaseButton? button in new BaseButton?[] { _weatherLungEnabled, _weatherLungAnimation, _weatherLungFreeze })
                if (button != null && button.Disabled == present) button.Disabled = !present;
            if (_weatherLungSunMode != null && _weatherLungSunMode.Disabled == present) _weatherLungSunMode.Disabled = !present;
            if (_weatherLungPhase != null && _weatherLungStreamer?.Controller is { } controller && controller.FreezePhase == false)
                _weatherLungPhase.SetValueNoSignal(controller.GetPhase());
        }
        if (_weatherLungDefaults?.Dirty != true) return;
        _weatherLungSaveDelay -= delta;
        _weatherLungSaveRetry -= delta;
        if (_weatherLungSaveDelay <= 0 && _weatherLungSaveRetry <= 0 && !FlushWeatherLungDefaults()) _weatherLungSaveRetry = 5;
    }

    public bool FlushWeatherLungDefaults()
    {
        bool saved = _weatherLungDefaults?.Save() ?? true;
        RefreshWeatherLungSaveStatus();
        return saved;
    }

    public void InstallWeatherLungDefaults(string isolatedPath)
    {
        if (!isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal) && !isolatedPath.StartsWith("user://generated/", StringComparison.Ordinal)) return;
        _weatherLungDefaultsPathOverride = isolatedPath;
        if (_weatherLungDefaults is null) return;
        _weatherLungDefaults = new GameplayWeatherLungDefaults(isolatedPath);
        _weatherLungDefaults.Load();
        _weatherLungDefaults.Apply(_weatherLungStreamer);
        RefreshWeatherLungControls();
    }

    private System.Collections.Generic.IEnumerable<HSlider?> WeatherLungSliders() => new[]
    {
        _weatherLungCycle, _weatherLungVane, _weatherLungGill, _weatherLungRpm, _weatherLungLag, _weatherLungNozzle, _weatherLungHeat, _weatherLungSunManual,
        _weatherLungFullRate, _weatherLungReducedRate, _weatherLungReducedHz, _weatherLungDetailRange, _weatherLungDressingRange, _weatherLungDressingFade,
        _weatherLungFarProxy, _weatherLungLoad, _weatherLungUnload, _weatherLungCollision, _weatherLungAttachBudget, _weatherLungPhase
    };

    private void BuildWeatherLungSection(VBoxContainer tabRoot)
    {
        var section = TuningMenuUi.CreateSection(tabRoot, "Weather Lung",
            "The breathing weather station (28 x 18 km basin with a hood, intake vanes, gills, turbine throat and heliostats) at its deterministic site. Exists only in worlds created with the Weather Lung (v8) recipe. The Detail load radius is raised to the Detail range + 4 km.");
        _weatherLungLive = new Label { Name = "WeatherLungLive", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        section.AddChild(_weatherLungLive);
        var s = _weatherLungDefaults?.Settings ?? new GameplayWeatherLungSettings();
        bool present = _weatherLungStreamer != null;
        _weatherLungEnabled = AddWeatherLungCheck(section, "WeatherLungEnabled", "Weather Lung enabled", s.Enabled, present, "Shows the station and lets it animate. Off hides it and releases the detail model.");
        _weatherLungAnimation = AddWeatherLungCheck(section, "WeatherLungAnimation", "Animation enabled", s.AnimationEnabled, present, "Off freezes the moving parts in their current pose (the breath clock keeps running).");
        _weatherLungCycle = AddWeatherLungSlider(section, "WeatherLungCycle", "Breath cycle (s)", GameplayWeatherLungDefaults.MinimumCycleSeconds, GameplayWeatherLungDefaults.MaximumCycleSeconds, 5, s.CycleSeconds,
            "Seconds per breath (inhale then exhale). Retiming keeps the phase continuous. Also sets the turbine revolutions per cycle.");
        _weatherLungVane = AddWeatherLungSlider(section, "WeatherLungVane", "Vane amplitude (deg)", GameplayWeatherLungDefaults.MinimumAmplitudeDeg, GameplayWeatherLungDefaults.MaximumAmplitudeDeg, .5, s.VaneAmplitudeDeg,
            "Opening angle of the five intake vanes at the top of the inhale.");
        _weatherLungGill = AddWeatherLungSlider(section, "WeatherLungGill", "Gill amplitude (deg)", GameplayWeatherLungDefaults.MinimumAmplitudeDeg, GameplayWeatherLungDefaults.MaximumAmplitudeDeg, .5, s.GillAmplitudeDeg,
            "Flare angle of the 16 hood gills during the exhale.");
        _weatherLungRpm = AddWeatherLungSlider(section, "WeatherLungRpm", "Turbine max rpm", GameplayWeatherLungDefaults.MinimumTurbineRpm, GameplayWeatherLungDefaults.MaximumTurbineRpm, .05, s.TurbineMaxRpm,
            "Whole turbine revolutions per cycle = max(1, round(rpm / 1.5 x cycle / 60)); the status line shows the effective value.");
        _weatherLungLag = AddWeatherLungSlider(section, "WeatherLungLag", "Ripple lag", GameplayWeatherLungDefaults.MinimumRippleLag, GameplayWeatherLungDefaults.MaximumRippleLag, .001, s.RippleLag,
            "Per-vane / per-gill delay in cycles that makes the motion ripple along the arch.");
        _weatherLungNozzle = AddWeatherLungSlider(section, "WeatherLungNozzle", "Nozzle glow scale", GameplayWeatherLungDefaults.MinimumEmissionScale, GameplayWeatherLungDefaults.MaximumEmissionScale, .01, s.NozzleEmissionScale,
            "Multiplier on the twelve nozzle glows (bright on the exhale). Initial value is a guess, tune it against the HDR glow threshold.");
        _weatherLungHeat = AddWeatherLungSlider(section, "WeatherLungHeat", "Heat grid glow scale", GameplayWeatherLungDefaults.MinimumEmissionScale, GameplayWeatherLungDefaults.MaximumEmissionScale, .01, s.HeatGridEmissionScale,
            "Multiplier on the basin floor heat-exchanger glow (night only: it is off in daylight).");
        var sunRow = new HBoxContainer { TooltipText = "Where the day / night state of the station (heliostats, heat grid) comes from." };
        sunRow.AddChild(new Label { Text = "Sun", CustomMinimumSize = new Vector2(160, 0) });
        _weatherLungSunMode = new OptionButton { Name = "WeatherLungSunMode", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Disabled = !present };
        foreach (string name in WeatherLungSunModeNames) _weatherLungSunMode.AddItem(name);
        _weatherLungSunMode.Selected = s.SunMode;
        _weatherLungSunMode.ItemSelected += _ => QueueWeatherLungSettings();
        sunRow.AddChild(_weatherLungSunMode);
        section.AddChild(sunRow);
        _weatherLungSunManual = AddWeatherLungSlider(section, "WeatherLungSunManual", "Manual sun (0 night, 1 day)", 0, 1, .01, s.SunManual, "Sun availability used when the sun mode is Manual.");
        _weatherLungFullRate = AddWeatherLungSlider(section, "WeatherLungFullRate", "Full-rate animation within (m)", GameplayWeatherLungDefaults.MinimumFullRateMeters, GameplayWeatherLungDefaults.MaximumFullRateMeters, 100, s.FullRateMeters,
            "Inside this distance the moving parts update every frame.");
        _weatherLungReducedRate = AddWeatherLungSlider(section, "WeatherLungReducedRate", "Reduced-rate animation within (m)", GameplayWeatherLungDefaults.MinimumReducedRateMeters, GameplayWeatherLungDefaults.MaximumReducedRateMeters, 100, s.ReducedRateMeters,
            "Between the full-rate distance and this one the moving parts update at the reduced rate; beyond it they do not update.");
        _weatherLungReducedHz = AddWeatherLungSlider(section, "WeatherLungReducedHz", "Reduced rate (Hz)", GameplayWeatherLungDefaults.MinimumReducedRateHz, GameplayWeatherLungDefaults.MaximumReducedRateHz, 1, s.ReducedRateHz,
            "Updates per second in the reduced-rate band.");
        _weatherLungDetailRange = AddWeatherLungSlider(section, "WeatherLungDetailRange", "Detail range (m)", GameplayWeatherLungDefaults.MinimumDetailRangeMeters, GameplayWeatherLungDefaults.MaximumDetailRangeMeters, 500, s.DetailRangeMeters,
            "Inside this distance the detailed model is shown; beyond it the far proxy takes over (sets the far proxy's begin distance).");
        _weatherLungDressingRange = AddWeatherLungSlider(section, "WeatherLungDressingRange", "Dressing range (m)", GameplayWeatherLungDefaults.MinimumDressingRangeMeters, GameplayWeatherLungDefaults.MaximumDressingRangeMeters, 100, s.DressingRangeMeters,
            "Fine detail (galleries, railings, equipment) is drawn only within this distance of each 1 km tile centre.");
        _weatherLungDressingFade = AddWeatherLungSlider(section, "WeatherLungDressingFade", "Dressing fade (m)", GameplayWeatherLungDefaults.MinimumDressingFadeMeters, GameplayWeatherLungDefaults.MaximumDressingFadeMeters, 25, s.DressingFadeMeters,
            "Length of the dithered fade-out at the dressing range.");
        _weatherLungFarProxy = AddWeatherLungSlider(section, "WeatherLungFarProxy", "Far proxy max distance (km)", GameplayWeatherLungDefaults.MinimumFarProxyMaxKm, GameplayWeatherLungDefaults.MaximumFarProxyMaxKm, 10, s.FarProxyMaxKm,
            "Farthest distance at which the low-detail station silhouette is drawn.");
        _weatherLungLoad = AddWeatherLungSlider(section, "WeatherLungLoad", "Detail load radius (m)", GameplayWeatherLungDefaults.MinimumDetailLoadMeters, GameplayWeatherLungDefaults.MaximumDetailLoadMeters, 500, s.DetailLoadRadiusMeters,
            "Distance at which the detailed model loads. Never below the detail range + 4 km.");
        _weatherLungUnload = AddWeatherLungSlider(section, "WeatherLungUnload", "Unload margin (m)", GameplayWeatherLungDefaults.MinimumUnloadMarginMeters, GameplayWeatherLungDefaults.MaximumUnloadMarginMeters, 500, s.UnloadMarginMeters,
            "The detailed model unloads after you stay this far beyond the load radius for 2 s.");
        _weatherLungCollision = AddWeatherLungSlider(section, "WeatherLungCollision", "Collision radius (m)", GameplayWeatherLungDefaults.MinimumCollisionRadiusMeters, GameplayWeatherLungDefaults.MaximumCollisionRadiusMeters, 100, s.CollisionRadiusMeters,
            "Structure collision of each station sector switches on within this distance of its bounds (off again 500 m farther).");
        _weatherLungAttachBudget = AddWeatherLungSlider(section, "WeatherLungAttachBudget", "Detail attach budget (ms/frame)", GameplayWeatherLungDefaults.MinimumAttachBudgetMs, GameplayWeatherLungDefaults.MaximumAttachBudgetMs, .5, s.DetailAttachBudgetMs,
            "The detailed model is assembled one piece at a time; this is the main-thread time per frame spent on it. A smaller budget means smaller hitches but a longer wait before the handover from the far proxy.");
        var reset = new Button { Name = "WeatherLungReset", Text = "Reset Weather Lung settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += ResetWeatherLung;
        section.AddChild(reset);
        _weatherLungSaveStatus = new Label { Name = "WeatherLungSaveStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        section.AddChild(_weatherLungSaveStatus);

        section.AddChild(new Label { Text = "Session only (not saved)", Modulate = RingUiStyle.Muted });
        _weatherLungFreeze = AddWeatherLungCheck(section, "WeatherLungFreeze", "Freeze breath phase", false, present, "Holds the current phase; the scrub slider then sets it.");
        _weatherLungFreeze.Toggled += pressed => { if (_weatherLungStreamer?.Controller is { } controller) controller.FreezePhase = pressed; };
        _weatherLungPhase = new HSlider { Name = "WeatherLungPhase", MinValue = 0, MaxValue = 1, Step = .005, Editable = present, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "Scrubs the breath phase (inhale 0 to 0.5, exhale 0.5 to 1) and freezes it." };
        _weatherLungPhase.ValueChanged += value =>
        {
            if (_weatherLungStreamer?.Controller is not { } controller) return;
            controller.ScrubTo(value);
            _weatherLungFreeze?.SetPressedNoSignal(true);
        };
        var phaseRow = new HBoxContainer();
        phaseRow.AddChild(new Label { Text = "Phase scrub", CustomMinimumSize = new Vector2(160, 0) });
        phaseRow.AddChild(_weatherLungPhase);
        section.AddChild(phaseRow);
        var teleports = new HBoxContainer();
        foreach ((string text, WeatherLungViewpoint viewpoint) in new[] { ("Dam crest", WeatherLungViewpoint.DamCrest), ("Intake overlook", WeatherLungViewpoint.IntakeOverlook), ("Far 40 km", WeatherLungViewpoint.Far40Km) })
        {
            var button = new Button { Name = "WeatherLungTeleport" + text.Replace(" ", ""), Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Hover at this viewpoint of the actual Weather Lung site." };
            button.Pressed += () => RequestWeatherLungTeleport((int)viewpoint);
            teleports.AddChild(button);
        }
        section.AddChild(teleports);
        _weatherLungResult = new Label { Name = "WeatherLungResult", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        section.AddChild(_weatherLungResult);
        RefreshWeatherLungSaveStatus();
    }

    private CheckButton AddWeatherLungCheck(VBoxContainer parent, string name, string text, bool pressed, bool present, string tooltip)
    {
        var check = new CheckButton { Name = name, Text = text, ButtonPressed = pressed, Disabled = !present, TooltipText = tooltip };
        if (name != "WeatherLungFreeze") check.Toggled += _ => QueueWeatherLungSettings();
        parent.AddChild(check);
        return check;
    }

    private HSlider AddWeatherLungSlider(VBoxContainer parent, string name, string label, double min, double max, double step, double initial, string tooltip)
    {
        var row = new HBoxContainer { TooltipText = tooltip };
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(160, 0), TooltipText = tooltip });
        var slider = new HSlider { Name = name, MinValue = min, MaxValue = max, Step = step, Value = initial, Editable = _weatherLungStreamer != null, TooltipText = tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var number = new Label { Text = slider.Value.ToString("0.###"), CustomMinimumSize = new Vector2(55, 0) };
        slider.ValueChanged += value => { number.Text = value.ToString("0.###"); QueueWeatherLungSettings(); };
        row.AddChild(slider);
        row.AddChild(number);
        parent.AddChild(row);
        return slider;
    }

    private void QueueWeatherLungSettings()
    {
        if (_weatherLungDefaults == null || _weatherLungEnabled == null || _weatherLungAnimation == null || _weatherLungSunMode == null || _weatherLungCycle == null
            || _weatherLungVane == null || _weatherLungGill == null || _weatherLungRpm == null || _weatherLungLag == null || _weatherLungNozzle == null || _weatherLungHeat == null
            || _weatherLungSunManual == null || _weatherLungFullRate == null || _weatherLungReducedRate == null || _weatherLungReducedHz == null || _weatherLungDetailRange == null
            || _weatherLungDressingRange == null || _weatherLungDressingFade == null || _weatherLungFarProxy == null || _weatherLungLoad == null || _weatherLungUnload == null
            || _weatherLungCollision == null || _weatherLungAttachBudget == null) return;
        GameplayWeatherLungDefaults d = _weatherLungDefaults;
        bool changed = d.SetEnabled(_weatherLungEnabled.ButtonPressed);
        changed |= d.SetAnimationEnabled(_weatherLungAnimation.ButtonPressed);
        changed |= d.SetCycleSeconds((float)_weatherLungCycle.Value);
        changed |= d.SetVaneAmplitudeDeg((float)_weatherLungVane.Value);
        changed |= d.SetGillAmplitudeDeg((float)_weatherLungGill.Value);
        changed |= d.SetTurbineMaxRpm((float)_weatherLungRpm.Value);
        changed |= d.SetRippleLag((float)_weatherLungLag.Value);
        changed |= d.SetNozzleEmissionScale((float)_weatherLungNozzle.Value);
        changed |= d.SetHeatGridEmissionScale((float)_weatherLungHeat.Value);
        changed |= d.SetSunMode(_weatherLungSunMode.Selected);
        changed |= d.SetSunManual((float)_weatherLungSunManual.Value);
        changed |= d.SetFullRateMeters((float)_weatherLungFullRate.Value);
        changed |= d.SetReducedRateMeters((float)_weatherLungReducedRate.Value);
        changed |= d.SetReducedRateHz((float)_weatherLungReducedHz.Value);
        changed |= d.SetDetailRangeMeters((float)_weatherLungDetailRange.Value);
        changed |= d.SetDressingRangeMeters((float)_weatherLungDressingRange.Value);
        changed |= d.SetDressingFadeMeters((float)_weatherLungDressingFade.Value);
        changed |= d.SetFarProxyMaxKm((float)_weatherLungFarProxy.Value);
        changed |= d.SetDetailLoadRadiusMeters((float)_weatherLungLoad.Value);
        changed |= d.SetUnloadMarginMeters((float)_weatherLungUnload.Value);
        changed |= d.SetCollisionRadiusMeters((float)_weatherLungCollision.Value);
        changed |= d.SetDetailAttachBudgetMs((float)_weatherLungAttachBudget.Value);
        d.Apply(_weatherLungStreamer);
        SetWeatherLungSliderNoSignal(_weatherLungLoad, d.Settings.DetailLoadRadiusMeters); // may have been raised to range + 4 km
        if (!changed) return;
        _weatherLungSaveDelay = .75;
        RefreshWeatherLungSaveStatus();
    }

    private void ResetWeatherLung()
    {
        _weatherLungDefaults?.Reset();
        _weatherLungDefaults?.Apply(_weatherLungStreamer);
        RefreshWeatherLungControls();
        FlushWeatherLungDefaults();
    }

    private void RefreshWeatherLungControls()
    {
        if (_weatherLungDefaults == null) return;
        GameplayWeatherLungSettings s = _weatherLungDefaults.Settings;
        _weatherLungEnabled?.SetPressedNoSignal(s.Enabled);
        _weatherLungAnimation?.SetPressedNoSignal(s.AnimationEnabled);
        _weatherLungSunMode?.Select(s.SunMode);
        SetWeatherLungSliderNoSignal(_weatherLungCycle, s.CycleSeconds);
        SetWeatherLungSliderNoSignal(_weatherLungVane, s.VaneAmplitudeDeg);
        SetWeatherLungSliderNoSignal(_weatherLungGill, s.GillAmplitudeDeg);
        SetWeatherLungSliderNoSignal(_weatherLungRpm, s.TurbineMaxRpm);
        SetWeatherLungSliderNoSignal(_weatherLungLag, s.RippleLag);
        SetWeatherLungSliderNoSignal(_weatherLungNozzle, s.NozzleEmissionScale);
        SetWeatherLungSliderNoSignal(_weatherLungHeat, s.HeatGridEmissionScale);
        SetWeatherLungSliderNoSignal(_weatherLungSunManual, s.SunManual);
        SetWeatherLungSliderNoSignal(_weatherLungFullRate, s.FullRateMeters);
        SetWeatherLungSliderNoSignal(_weatherLungReducedRate, s.ReducedRateMeters);
        SetWeatherLungSliderNoSignal(_weatherLungReducedHz, s.ReducedRateHz);
        SetWeatherLungSliderNoSignal(_weatherLungDetailRange, s.DetailRangeMeters);
        SetWeatherLungSliderNoSignal(_weatherLungDressingRange, s.DressingRangeMeters);
        SetWeatherLungSliderNoSignal(_weatherLungDressingFade, s.DressingFadeMeters);
        SetWeatherLungSliderNoSignal(_weatherLungFarProxy, s.FarProxyMaxKm);
        SetWeatherLungSliderNoSignal(_weatherLungLoad, s.DetailLoadRadiusMeters);
        SetWeatherLungSliderNoSignal(_weatherLungUnload, s.UnloadMarginMeters);
        SetWeatherLungSliderNoSignal(_weatherLungCollision, s.CollisionRadiusMeters);
        SetWeatherLungSliderNoSignal(_weatherLungAttachBudget, s.DetailAttachBudgetMs);
        RefreshWeatherLungSaveStatus();
    }

    private static void SetWeatherLungSliderNoSignal(HSlider? slider, double value)
    {
        if (slider == null) return;
        slider.SetValueNoSignal(value);
        if (slider.GetParent().GetChild(2) is Label number) number.Text = slider.Value.ToString("0.###");
    }

    private void RefreshWeatherLungSaveStatus()
    {
        if (_weatherLungSaveStatus == null || _weatherLungDefaults == null) return;
        _weatherLungSaveStatus.Text = _weatherLungDefaults.LastError.Length > 0 ? "Save error: " + _weatherLungDefaults.LastError
            : _weatherLungDefaults.Dirty ? "Weather Lung settings pending save." : "Weather Lung settings saved.";
    }

    /// <summary>Test hook (works while the drawer is unbuilt): teleport to viewpoint 0 dam crest, 1 intake overlook, 2 far 40 km of the Weather Lung site.</summary>
    public bool RequestWeatherLungTeleport(int viewpoint)
    {
        bool started = _weatherLungTeleport.Begin(GetNodeOrNull<RingVoxelExplorerController>("../Explorer"), _weatherLungStreamer, _grid,
            (WeatherLungViewpoint)Math.Clamp(viewpoint, 0, 2));
        _weatherLungLastTeleportState = _weatherLungTeleport.State;
        if (_weatherLungResult != null) _weatherLungResult.Text = _weatherLungTeleport.Message;
        return started;
    }

    /// <summary>Test hook: the streamer node (null in scenes without it).</summary>
    public WeatherLungStreamer? WeatherLungStreamerNode => _weatherLungStreamer;
}
