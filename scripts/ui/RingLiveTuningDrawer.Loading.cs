using Godot;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private GameplayLoadingPresentationDefaults? _loadingPresentation;
    private HSlider? _loadingAnimationSpeed;
    private HSlider? _loadingHologramBrightness;
    private CheckButton? _loadingReducedMotion;
    private Label? _loadingPresentationStatus;
    private double _loadingSaveDelay, _loadingSaveRetry;
    private string? _loadingPresentationPathOverride;

    private void InitializeLoadingTuning()
    {
        _loadingPresentation = new(_loadingPresentationPathOverride);
        _loadingPresentation.Load();
    }

    public void InstallLoadingPresentationDefaults(string isolatedPath)
    {
        if (!isolatedPath.StartsWith("res://.godot/", System.StringComparison.Ordinal)
            && !isolatedPath.StartsWith("user://generated/", System.StringComparison.Ordinal)) return;
        _loadingPresentationPathOverride = isolatedPath;
        if (_loadingPresentation == null) return;
        InitializeLoadingTuning();
        RefreshLoadingPresentationControls();
    }

    private void ProcessLoadingTuning(double delta)
    {
        if (_loadingPresentation?.Dirty != true) return;
        _loadingSaveDelay -= delta;
        _loadingSaveRetry -= delta;
        if (_loadingSaveDelay <= 0 && _loadingSaveRetry <= 0 && !FlushLoadingPresentationDefaults()) _loadingSaveRetry = 5;
    }

    public bool FlushLoadingPresentationDefaults()
    {
        bool saved = _loadingPresentation?.Save() ?? true;
        RefreshLoadingPresentationStatus();
        return saved;
    }

    private void BuildLoadingTab(TabContainer tabs)
    {
        var tab = CreateTab(tabs, "Loading");
        var section = TuningMenuUi.CreateSection(tab, "Loading screen", "Personalize the ring illustration shown while opening a world. Changes apply the next time you load a world.");
        _loadingReducedMotion = new CheckButton { Name = "LoadingReducedMotion", Text = "Reduced motion", ButtonPressed = !(_loadingPresentation?.Settings.MotionEnabled ?? true) };
        _loadingReducedMotion.Toggled += _ => QueueLoadingPresentation();
        section.AddChild(_loadingReducedMotion);
        _loadingAnimationSpeed = AddLoadingSlider(section, "LoadingAnimationSpeed", "Animation speed", 0, .6, .01, _loadingPresentation?.Settings.AnimationSpeed ?? .18f);
        _loadingHologramBrightness = AddLoadingSlider(section, "LoadingHologramBrightness", "Hologram brightness", .35, 1.5, .05, _loadingPresentation?.Settings.HologramBrightness ?? 1f);
        var row = new HBoxContainer();
        var save = new Button { Name = "LoadingPresentationSave", Text = "Save loading settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        save.Pressed += () => FlushLoadingPresentationDefaults();
        row.AddChild(save);
        var reset = new Button { Name = "LoadingPresentationReset", Text = "Reset loading settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += () => { _loadingPresentation?.Reset(); RefreshLoadingPresentationControls(); FlushLoadingPresentationDefaults(); };
        row.AddChild(reset);
        tab.AddChild(row);
        _loadingPresentationStatus = new Label { Name = "LoadingPresentationStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        tab.AddChild(_loadingPresentationStatus);
        RefreshLoadingPresentationControls();
    }

    private HSlider AddLoadingSlider(VBoxContainer parent, string name, string label, double min, double max, double step, double initial)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(160, 0) });
        var slider = new HSlider { Name = name, MinValue = min, MaxValue = max, Step = step, Value = initial, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var number = new Label { Text = initial.ToString("0.##"), CustomMinimumSize = new Vector2(55, 0) };
        slider.ValueChanged += value => { number.Text = value.ToString("0.##"); QueueLoadingPresentation(); };
        row.AddChild(slider);
        row.AddChild(number);
        parent.AddChild(row);
        return slider;
    }

    private void QueueLoadingPresentation()
    {
        if (_loadingAnimationSpeed == null || _loadingHologramBrightness == null || _loadingReducedMotion == null) return;
        if (_loadingPresentation?.Set((float)_loadingAnimationSpeed.Value, !_loadingReducedMotion.ButtonPressed, (float)_loadingHologramBrightness.Value) != true) return;
        _loadingSaveDelay = .75;
        RefreshLoadingPresentationControls();
    }

    private void RefreshLoadingPresentationControls()
    {
        if (_loadingPresentation == null) return;
        _loadingReducedMotion?.SetPressedNoSignal(!_loadingPresentation.Settings.MotionEnabled);
        if (_loadingAnimationSpeed != null)
        {
            _loadingAnimationSpeed.SetValueNoSignal(_loadingPresentation.Settings.AnimationSpeed);
            _loadingAnimationSpeed.Editable = _loadingPresentation.Settings.MotionEnabled;
            if (_loadingAnimationSpeed.GetParent().GetChild(2) is Label number) number.Text = _loadingAnimationSpeed.Value.ToString("0.##");
        }
        if (_loadingHologramBrightness != null)
        {
            _loadingHologramBrightness.SetValueNoSignal(_loadingPresentation.Settings.HologramBrightness);
            if (_loadingHologramBrightness.GetParent().GetChild(2) is Label number) number.Text = _loadingHologramBrightness.Value.ToString("0.##");
        }
        RefreshLoadingPresentationStatus();
    }

    private void RefreshLoadingPresentationStatus()
    {
        if (_loadingPresentationStatus == null || _loadingPresentation == null) return;
        _loadingPresentationStatus.Text = _loadingPresentation.LastError.Length > 0 ? "Save error: " + _loadingPresentation.LastError
            : _loadingPresentation.Dirty ? "Loading settings pending save." : "Loading settings saved.";
    }
}
