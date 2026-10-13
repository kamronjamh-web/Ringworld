using Godot;
using RingworldTechDemo.Underground;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private RingUndergroundModeController? _undergroundController;
    private GameplayUndergroundModeDefaults? _undergroundDefaults;
    private CheckButton? _undergroundEnabled;
    private OptionButton? _undergroundOverride;
    private HSlider? _undergroundEnterDepth, _undergroundExitDepth, _undergroundEnterOpenness, _undergroundExitOpenness;
    private HSlider? _undergroundEnterHold, _undergroundExitHold, _undergroundBlend;
    private HSlider? _undergroundDeepHide, _undergroundLightingWeight, _undergroundCaveEnergy;
    private CheckButton? _undergroundAudioWeather, _undergroundVegetation, _undergroundClouds, _undergroundWater, _undergroundFarTerrain, _undergroundShadows;
    private ColorPickerButton? _undergroundCaveAmbientColor, _undergroundCaveFogColor;
    private Label? _undergroundStatus, _undergroundLive;
    private double _undergroundSaveDelay, _undergroundSaveRetry, _undergroundLiveDelay;

    private void InitializeUndergroundModeTuning()
    {
        _undergroundController = GetNodeOrNull<RingUndergroundModeController>("../UndergroundMode");
        _undergroundDefaults = new GameplayUndergroundModeDefaults();
        _undergroundDefaults.Load();
        // Without a saved choice the controller keeps its own defaults.
        if (_undergroundController != null && _undergroundDefaults.HasSavedFile) _undergroundDefaults.Apply(_undergroundController);
    }

    private void ProcessUndergroundModeTuning(double delta)
    {
        if (_open && _undergroundLive != null && (_undergroundLiveDelay -= delta) <= 0)
        {
            _undergroundLiveDelay = .25;
            _undergroundLive.Text = _undergroundController?.DescribeStatus() ?? "Underground mode is unavailable in this scene.";
        }
        if (_undergroundDefaults?.Dirty != true) return;
        _undergroundSaveDelay -= delta;
        _undergroundSaveRetry -= delta;
        if (_undergroundSaveDelay <= 0 && _undergroundSaveRetry <= 0 && !FlushUndergroundModeDefaults()) _undergroundSaveRetry = 5;
    }

    public bool FlushUndergroundModeDefaults()
    {
        bool saved = _undergroundDefaults?.Save() ?? true;
        RefreshUndergroundModeStatus();
        return saved;
    }

    private void BuildUndergroundModeSection(VBoxContainer tabRoot)
    {
        var section = TuningMenuUi.CreateSection(tabRoot, "Underground mode",
            "Detects when the player is enclosed in a cave and suspends surface systems the player cannot see, darkening sun and sky ambient first. Every group can be turned off independently; nothing is freed while suspended.");
        _undergroundLive = new Label { Name = "UndergroundModeLive", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        section.AddChild(_undergroundLive);
        _undergroundEnabled = new CheckButton { Name = "UndergroundModeEnabled", Text = "Underground mode enabled", ButtonPressed = _undergroundDefaults?.Settings.Enabled ?? true, Disabled = _undergroundController == null };
        _undergroundEnabled.Toggled += _ => QueueUndergroundMode();
        section.AddChild(_undergroundEnabled);
        var overrideRow = new HBoxContainer { TooltipText = "Debug override, not saved. Auto detects from depth and sky openness." };
        overrideRow.AddChild(new Label { Text = "Mode override", CustomMinimumSize = new Vector2(160, 0) });
        _undergroundOverride = new OptionButton { Name = "UndergroundModeOverride", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Disabled = _undergroundController == null };
        _undergroundOverride.AddItem("Auto");
        _undergroundOverride.AddItem("Force surface");
        _undergroundOverride.AddItem("Force underground");
        _undergroundOverride.Selected = (int)(_undergroundController?.ModeOverride ?? UndergroundModeOverride.Auto);
        _undergroundOverride.ItemSelected += index => { if (_undergroundController != null) _undergroundController.ModeOverride = (UndergroundModeOverride)(int)index; };
        overrideRow.AddChild(_undergroundOverride);
        section.AddChild(overrideRow);
        var current = _undergroundController?.Parameters ?? new UndergroundEnclosureParameters();
        const float maxDepth = GameplayUndergroundModeDefaults.MaximumDepth;
        _undergroundEnterDepth = AddUndergroundSlider(section, "UndergroundEnterDepth", "Enter depth (m)", GameplayUndergroundModeDefaults.MinimumDepth, maxDepth, 1, current.EnterDepthMeters,
            "Depth below the local surface needed to count as enclosed.");
        _undergroundExitDepth = AddUndergroundSlider(section, "UndergroundExitDepth", "Exit depth (m)", GameplayUndergroundModeDefaults.MinimumDepth, maxDepth, 1, current.ExitDepthMeters,
            "Rising above this depth leaves underground mode. Kept at or below the enter depth.");
        _undergroundEnterOpenness = AddUndergroundSlider(section, "UndergroundEnterOpenness", "Enter openness max", 0, .5, .01, current.EnterOpennessMax,
            "Fraction of the 17 sky rays that may escape while still counting as enclosed.");
        _undergroundExitOpenness = AddUndergroundSlider(section, "UndergroundExitOpenness", "Exit openness min", 0, 1, .01, current.ExitOpennessMin,
            "Sky openness at or above this leaves underground mode. Kept at or above the enter openness.");
        _undergroundEnterHold = AddUndergroundSlider(section, "UndergroundEnterHold", "Enter hold (s)", 0, GameplayUndergroundModeDefaults.MaximumEnterHold, .05, current.EnterHoldSeconds,
            "Enclosure must hold continuously this long before entering.");
        _undergroundExitHold = AddUndergroundSlider(section, "UndergroundExitHold", "Exit hold (s)", 0, GameplayUndergroundModeDefaults.MaximumExitHold, .05, current.ExitHoldSeconds,
            "Exit conditions must hold this long before leaving.");
        _undergroundBlend = AddUndergroundSlider(section, "UndergroundBlend", "Blend time (s)", GameplayUndergroundModeDefaults.MinimumBlend, GameplayUndergroundModeDefaults.MaximumBlend, .1, current.BlendSeconds,
            "Seconds for the enclosure blend to ramp between 0 and 1.");
        _undergroundDeepHide = AddUndergroundSlider(section, "UndergroundDeepHide", "Deep hide depth (m)", GameplayUndergroundModeDefaults.MinimumDeepHideDepth, GameplayUndergroundModeDefaults.MaximumDeepHideDepth, 1, _undergroundController?.DeepHideDepthMeters ?? 128,
            "Far terrain is hidden, not just frozen, only this deep with the sky fully closed. Shallower caves keep it drawn so distant cave mouths never show a hole.");
        section.AddChild(new Label { Text = "Suspended systems while fully enclosed", Modulate = RingUiStyle.Muted });
        _undergroundAudioWeather = AddUndergroundGroupToggle(section, "UndergroundGroupAudioWeather", "Audio and weather effects", "Fades exterior ambience, rain and thunder; hides rain and storm-flash lighting.", UndergroundGroups.AudioWeather);
        _undergroundVegetation = AddUndergroundGroupToggle(section, "UndergroundGroupVegetation", "Grass and trees", "Holds grass and tree streaming and hides them; nothing is released.", UndergroundGroups.Vegetation);
        _undergroundClouds = AddUndergroundGroupToggle(section, "UndergroundGroupClouds", "Clouds", "Stops the cloud compositor, local volumetric clouds and terrain cloud-shadow tracing. The weather clock keeps running.", UndergroundGroups.Clouds);
        _undergroundWater = AddUndergroundGroupToggle(section, "UndergroundGroupWater", "Water", "Makes the FFT water dormant. Waves keep their wind clock and resume instantly.", UndergroundGroups.Water);
        _undergroundFarTerrain = AddUndergroundGroupToggle(section, "UndergroundGroupFarTerrain", "Far terrain", "Freezes surface LOD proxies when enclosed; hides proxies, global terrain and the far viewport at the deep hide depth.", UndergroundGroups.FarTerrain);
        _undergroundShadows = AddUndergroundGroupToggle(section, "UndergroundGroupShadows", "Sun and planet shadows", "Skips directional shadow maps once sun and sky are fully darkened.", UndergroundGroups.Shadows);
        section.AddChild(new Label { Text = "Cave lighting", Modulate = RingUiStyle.Muted });
        _undergroundLightingWeight = AddUndergroundSlider(section, "UndergroundLightingWeight", "Darken sky light", 0, 1, .01, _undergroundController?.EnclosureLightingWeight ?? 1,
            "How far enclosure fades direct sun and sky ambient in designed caves. 1 is truly dark; below 1 far terrain is never hidden because sunlight could leak.");
        _undergroundCaveEnergy = AddUndergroundSlider(section, "UndergroundCaveAmbientEnergy", "Cave ambient energy", 0, GameplayUndergroundModeDefaults.MaximumCaveAmbientEnergy, .005, _undergroundController?.CaveAmbientEnergy ?? .035,
            "Faint ambient light in designed caves so large cavern shapes read as silhouettes. The structural cavity stays near black.");
        _undergroundCaveAmbientColor = AddUndergroundColor(section, "UndergroundCaveAmbientColor", "Cave ambient color", _undergroundController?.CaveAmbientColor ?? new Color(.16f, .20f, .30f), "Color of the faint cave ambient light.");
        _undergroundCaveFogColor = AddUndergroundColor(section, "UndergroundCaveFogColor", "Cave fog color", _undergroundController?.CaveFogColor ?? new Color(.05f, .07f, .11f), "Fog color blended in by the enclosure so the distance fades into the cave color.");
        var row = new HBoxContainer();
        var save = new Button { Name = "UndergroundModeSave", Text = "Save underground settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        save.Pressed += () => { QueueUndergroundMode(); FlushUndergroundModeDefaults(); };
        row.AddChild(save);
        var reset = new Button { Name = "UndergroundModeReset", Text = "Reset underground settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += () =>
        {
            _undergroundDefaults?.Reset();
            if (_undergroundController != null && _undergroundDefaults != null) _undergroundDefaults.Apply(_undergroundController);
            RefreshUndergroundModeControls();
            FlushUndergroundModeDefaults();
        };
        row.AddChild(reset);
        section.AddChild(row);
        _undergroundStatus = new Label { Name = "UndergroundModeStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        section.AddChild(_undergroundStatus);
        RefreshUndergroundModeStatus();
    }

    private HSlider AddUndergroundSlider(VBoxContainer parent, string name, string label, double min, double max, double step, double initial, string tooltip)
    {
        var row = new HBoxContainer { TooltipText = tooltip };
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(160, 0), TooltipText = tooltip });
        var slider = new HSlider { Name = name, MinValue = min, MaxValue = max, Step = step, Value = initial, Editable = _undergroundController != null, TooltipText = tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var number = new Label { Text = slider.Value.ToString("0.##"), CustomMinimumSize = new Vector2(55, 0) };
        slider.ValueChanged += value => { number.Text = value.ToString("0.##"); QueueUndergroundMode(); };
        row.AddChild(slider);
        row.AddChild(number);
        parent.AddChild(row);
        return slider;
    }

    private CheckButton AddUndergroundGroupToggle(VBoxContainer parent, string name, string label, string tooltip, UndergroundGroups group)
    {
        var toggle = new CheckButton
        {
            Name = name, Text = label, TooltipText = tooltip, Disabled = _undergroundController == null,
            ButtonPressed = (_undergroundController?.EnabledGroups ?? UndergroundGroups.All).HasFlag(group)
        };
        toggle.Toggled += _ => QueueUndergroundMode();
        parent.AddChild(toggle);
        return toggle;
    }

    private ColorPickerButton AddUndergroundColor(VBoxContainer parent, string name, string label, Color initial, string tooltip)
    {
        var row = new HBoxContainer { TooltipText = tooltip };
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(160, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = tooltip });
        var picker = new ColorPickerButton { Name = name, Color = initial, EditAlpha = false, CustomMinimumSize = new Vector2(96, 0), Disabled = _undergroundController == null, TooltipText = tooltip };
        picker.ColorChanged += _ => QueueUndergroundMode();
        row.AddChild(picker);
        parent.AddChild(row);
        return picker;
    }

    private void QueueUndergroundMode()
    {
        if (_undergroundEnabled == null || _undergroundEnterDepth == null || _undergroundExitDepth == null || _undergroundEnterOpenness == null
            || _undergroundExitOpenness == null || _undergroundEnterHold == null || _undergroundExitHold == null || _undergroundBlend == null || _undergroundDefaults == null
            || _undergroundDeepHide == null || _undergroundAudioWeather == null || _undergroundVegetation == null || _undergroundClouds == null || _undergroundWater == null
            || _undergroundFarTerrain == null || _undergroundShadows == null || _undergroundLightingWeight == null || _undergroundCaveEnergy == null
            || _undergroundCaveAmbientColor == null || _undergroundCaveFogColor == null) return;
        using var candidate = new GameplayUndergroundModeSettings
        {
            Enabled = _undergroundEnabled.ButtonPressed,
            EnterDepthMeters = (float)_undergroundEnterDepth.Value,
            ExitDepthMeters = (float)_undergroundExitDepth.Value,
            EnterOpennessMax = (float)_undergroundEnterOpenness.Value,
            ExitOpennessMin = (float)_undergroundExitOpenness.Value,
            EnterHoldSeconds = (float)_undergroundEnterHold.Value,
            ExitHoldSeconds = (float)_undergroundExitHold.Value,
            BlendSeconds = (float)_undergroundBlend.Value,
            DeepHideDepthMeters = (float)_undergroundDeepHide.Value,
            AudioWeatherEnabled = _undergroundAudioWeather.ButtonPressed,
            VegetationEnabled = _undergroundVegetation.ButtonPressed,
            CloudsEnabled = _undergroundClouds.ButtonPressed,
            WaterEnabled = _undergroundWater.ButtonPressed,
            FarTerrainEnabled = _undergroundFarTerrain.ButtonPressed,
            ShadowsEnabled = _undergroundShadows.ButtonPressed,
            EnclosureLightingWeight = (float)_undergroundLightingWeight.Value,
            CaveAmbientEnergy = (float)_undergroundCaveEnergy.Value,
            CaveAmbientColor = _undergroundCaveAmbientColor.Color,
            CaveFogColor = _undergroundCaveFogColor.Color
        };
        bool changed = _undergroundDefaults.Set(candidate);
        if (_undergroundController != null) _undergroundDefaults.Apply(_undergroundController);
        if (!changed) return;
        _undergroundSaveDelay = .75;
        // Sanitizing may have moved a linked threshold; reflect the stored value.
        RefreshUndergroundModeControls();
    }

    private void RefreshUndergroundModeControls()
    {
        if (_undergroundDefaults == null) return;
        var s = _undergroundDefaults.Settings;
        _undergroundEnabled?.SetPressedNoSignal(s.Enabled);
        SetUndergroundSliderNoSignal(_undergroundEnterDepth, s.EnterDepthMeters);
        SetUndergroundSliderNoSignal(_undergroundExitDepth, s.ExitDepthMeters);
        SetUndergroundSliderNoSignal(_undergroundEnterOpenness, s.EnterOpennessMax);
        SetUndergroundSliderNoSignal(_undergroundExitOpenness, s.ExitOpennessMin);
        SetUndergroundSliderNoSignal(_undergroundEnterHold, s.EnterHoldSeconds);
        SetUndergroundSliderNoSignal(_undergroundExitHold, s.ExitHoldSeconds);
        SetUndergroundSliderNoSignal(_undergroundBlend, s.BlendSeconds);
        SetUndergroundSliderNoSignal(_undergroundDeepHide, s.DeepHideDepthMeters);
        _undergroundAudioWeather?.SetPressedNoSignal(s.AudioWeatherEnabled);
        _undergroundVegetation?.SetPressedNoSignal(s.VegetationEnabled);
        _undergroundClouds?.SetPressedNoSignal(s.CloudsEnabled);
        _undergroundWater?.SetPressedNoSignal(s.WaterEnabled);
        _undergroundFarTerrain?.SetPressedNoSignal(s.FarTerrainEnabled);
        _undergroundShadows?.SetPressedNoSignal(s.ShadowsEnabled);
        SetUndergroundSliderNoSignal(_undergroundLightingWeight, s.EnclosureLightingWeight);
        SetUndergroundSliderNoSignal(_undergroundCaveEnergy, s.CaveAmbientEnergy);
        if (_undergroundCaveAmbientColor != null && _undergroundCaveAmbientColor.Color != s.CaveAmbientColor) _undergroundCaveAmbientColor.Color = s.CaveAmbientColor;
        if (_undergroundCaveFogColor != null && _undergroundCaveFogColor.Color != s.CaveFogColor) _undergroundCaveFogColor.Color = s.CaveFogColor;
        RefreshUndergroundModeStatus();
    }

    private static void SetUndergroundSliderNoSignal(HSlider? slider, double value)
    {
        if (slider == null) return;
        slider.SetValueNoSignal(value);
        if (slider.GetParent().GetChild(2) is Label number) number.Text = slider.Value.ToString("0.##");
    }

    private void RefreshUndergroundModeStatus()
    {
        if (_undergroundStatus == null || _undergroundDefaults == null) return;
        _undergroundStatus.Text = _undergroundController == null ? "Underground mode is unavailable in this scene."
            : _undergroundDefaults.LastError.Length > 0 ? "Save error: " + _undergroundDefaults.LastError
            : _undergroundDefaults.Dirty ? "Underground settings pending save." : "Underground settings saved.";
    }
}
