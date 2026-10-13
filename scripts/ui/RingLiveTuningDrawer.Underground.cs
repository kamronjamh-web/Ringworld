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
            "Detects when the player is enclosed in a cave. This phase only detects and reports; it does not change rendering or suspend any system.");
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

    private void QueueUndergroundMode()
    {
        if (_undergroundEnabled == null || _undergroundEnterDepth == null || _undergroundExitDepth == null || _undergroundEnterOpenness == null
            || _undergroundExitOpenness == null || _undergroundEnterHold == null || _undergroundExitHold == null || _undergroundBlend == null || _undergroundDefaults == null) return;
        using var candidate = new GameplayUndergroundModeSettings
        {
            Enabled = _undergroundEnabled.ButtonPressed,
            EnterDepthMeters = (float)_undergroundEnterDepth.Value,
            ExitDepthMeters = (float)_undergroundExitDepth.Value,
            EnterOpennessMax = (float)_undergroundEnterOpenness.Value,
            ExitOpennessMin = (float)_undergroundExitOpenness.Value,
            EnterHoldSeconds = (float)_undergroundEnterHold.Value,
            ExitHoldSeconds = (float)_undergroundExitHold.Value,
            BlendSeconds = (float)_undergroundBlend.Value
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
