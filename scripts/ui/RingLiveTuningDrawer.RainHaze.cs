using System;
using Godot;
using RingworldTechDemo.Weather;
namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private GameplayRainHazeDefaults? _rainHazeDefaults;
    private string? _rainHazeDefaultsPathOverride;
    private double _rainHazeSaveDelay, _rainHazeRetryDelay;
    private Label? _rainHazeStatus;
    private void InitializeRainHazeTuning()
    {
        _rainHazeDefaults = _rainHazeDefaultsPathOverride != null ? new GameplayRainHazeDefaults(_rainHazeDefaultsPathOverride)
            : GameplayRainHazeDefaults.StartupDefaults ?? new GameplayRainHazeDefaults();
        _rainHazeDefaults.Initialize();
    }
    private void BuildRainHazeControls(VBoxContainer tab)
    {
        var group = TuningMenuUi.CreateSection(tab, "Rain haze", "Soft gray haze thickens with rainfall. The near-clear distance keeps nearby surfaces visible.");
        var enabled = new CheckButton { Name = "RainHazeEnabled", Text = "Enable rain haze", ButtonPressed = RingRainHazeSettings.Current.Enabled };
        enabled.Toggled += value => SetRainHazeTuningValue("Enabled", value); group.AddChild(enabled);
        AddRainHazeNumber(group, "Density", "Rain haze density", .0001);
        AddRainHazeNumber(group, "NearClearDistanceMeters", "Near-clear distance (m)", 1);
        AddRainHazeNumber(group, "MaximumOpacity", "Maximum haze opacity", .01);
        AddRainHazeNumber(group, "VariationStrength", "Haze variation", .01);
        var row = new HBoxContainer(); row.AddChild(new Label { Text = "Haze tint", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var tint = new ColorPickerButton { Name = "RainHazeTint", Color = RingRainHazeSettings.Current.Tint, EditAlpha = false };
        tint.ColorChanged += value => SetRainHazeTuningValue("Tint", value); row.AddChild(tint); group.AddChild(row);
        row = new HBoxContainer();
        var save = new Button { Name = "RainHazeSaveNow", Text = "Save rain haze", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        save.Pressed += () => FlushRainHazeDefaults(); row.AddChild(save);
        var reset = new Button { Name = "RainHazeResetDefaults", Text = "Reset rain haze", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += ResetRainHazeSettings; row.AddChild(reset); group.AddChild(row);
        _rainHazeStatus = new Label { Name = "RainHazeDefaultsStatus", Modulate = RingUiStyle.Muted, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        group.AddChild(_rainHazeStatus); RefreshRainHazeControls();
    }
    private void AddRainHazeNumber(VBoxContainer group, string key, string label, double step)
    {
        group.AddChild(new Label { Text = label }); var limits = GameplayRainHazeDefaults.Limits[key];
        double value = RingRainHazeSettings.Current.Get(key).AsDouble();
        var row = new HBoxContainer();
        var slider = new HSlider { Name = "RainHaze" + key, MinValue = limits.Min, MaxValue = limits.Max, Step = step, Value = value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var entry = new SpinBox { Name = "RainHaze" + key + "Entry", MinValue = limits.Min, MaxValue = limits.Max, Step = step, Value = value, CustomMinimumSize = new Vector2(82, 0) };
        slider.ValueChanged += v => { entry.SetValueNoSignal(v); SetRainHazeTuningValue(key, v); };
        entry.ValueChanged += v => { slider.SetValueNoSignal(v); SetRainHazeTuningValue(key, v); };
        row.AddChild(slider); row.AddChild(entry); group.AddChild(row);
    }
    public void SetRainHazeTuningValue(string key, Variant value)
    {
        if (_rainHazeDefaults?.SetOverride(key, value) != true) return;
        _rainHazeSaveDelay = .5; _rainHazeRetryDelay = 0; RefreshRainHazeControls();
    }
    public void ResetRainHazeSettings()
    {
        _rainHazeDefaults?.Reset(); RefreshRainHazeControls();
        if (!FlushRainHazeDefaults()) _rainHazeRetryDelay = 5;
    }
    public bool FlushRainHazeDefaults()
    {
        bool saved = _rainHazeDefaults?.Save() ?? true;
        if (_rainHazeStatus != null) _rainHazeStatus.Text = !string.IsNullOrEmpty(_rainHazeDefaults?.LastError)
            ? "Rain haze settings could not be saved; retrying automatically."
            : _rainHazeDefaults?.Dirty == true ? "Rain haze settings pending." : "Rain haze settings saved.";
        return saved;
    }
    private void ProcessRainHazeTuning(double delta)
    {
        if (_rainHazeDefaults?.Dirty != true) return;
        _rainHazeSaveDelay -= delta; _rainHazeRetryDelay -= delta;
        if (_rainHazeSaveDelay <= 0 && _rainHazeRetryDelay <= 0 && !FlushRainHazeDefaults()) _rainHazeRetryDelay = 5;
    }
    public void InstallRainHazeDefaults(string path)
    {
        if (!path.StartsWith("res://.godot/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal)) return;
        _rainHazeDefaultsPathOverride = path;
        var effects = GetNodeOrNull<global::RingworldTechDemo.RingLocalWeatherEffects>(WeatherEffectsPath);
        if (effects != null) effects.RainHazePreferencesPath = path;
        if (_rainHazeDefaults == null) return;
        _rainHazeDefaults = new GameplayRainHazeDefaults(path); _rainHazeDefaults.Initialize(); RefreshRainHazeControls();
    }
    public Godot.Collections.Dictionary GetRainHazeTuningDiagnostics() => new()
    {
        ["path"] = _rainHazeDefaults?.GetPath() ?? "", ["dirty"] = _rainHazeDefaults?.Dirty ?? false,
        ["last_error"] = _rainHazeDefaults?.LastError ?? "", ["override_count"] = _rainHazeDefaults?.Settings.Overrides.Count ?? 0
    };
    private void RefreshRainHazeControls()
    {
        if (_drawer == null) return;
        RefreshRainHazeControlsRecursive(_drawer);
        if (_rainHazeStatus != null) _rainHazeStatus.Text = !string.IsNullOrEmpty(_rainHazeDefaults?.LastError)
            ? "Rain haze settings could not be saved; retrying automatically."
            : _rainHazeDefaults?.Dirty == true ? "Rain haze settings pending." : "Rain haze settings saved.";
    }
    private void RefreshRainHazeControlsRecursive(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            string name = child.Name.ToString();
            string key = name.StartsWith("RainHaze", StringComparison.Ordinal) ? name[8..] : "";
            if (key.EndsWith("Entry", StringComparison.Ordinal)) key = key[..^5];
            if (GameplayRainHazeDefaults.Limits.ContainsKey(key))
            {
                double value = RingRainHazeSettings.Current.Get(key).AsDouble();
                if (child is HSlider slider) slider.SetValueNoSignal(value);
                if (child is SpinBox entry) entry.SetValueNoSignal(value);
            }
            else if (key == "Enabled" && child is CheckButton toggle) toggle.SetPressedNoSignal(RingRainHazeSettings.Current.Enabled);
            else if (key == "Tint" && child is ColorPickerButton picker && picker.Color != RingRainHazeSettings.Current.Tint) picker.Color = RingRainHazeSettings.Current.Tint;
            RefreshRainHazeControlsRecursive(child);
        }
    }
}
