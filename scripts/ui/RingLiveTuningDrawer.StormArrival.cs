using System;
using Godot;
using RingworldTechDemo.Weather;
namespace RingworldTechDemo.UI;

/// <summary>"Storm arrival" group of the Wind tab (docs/storm-revamp-s3-storm-arrival-design.md section 10). Entry points are chained from the Wind partial.</summary>
public partial class RingLiveTuningDrawer
{
    private GameplayStormArrivalDefaults? _stormArrivalDefaults;
    private string? _stormArrivalDefaultsPathOverride;
    private double _stormArrivalSaveDelay, _stormArrivalRetryDelay;
    private Label? _stormArrivalStatus;
    private void InitializeStormArrivalTuning()
    {
        _stormArrivalDefaults = _stormArrivalDefaultsPathOverride != null ? new GameplayStormArrivalDefaults(_stormArrivalDefaultsPathOverride)
            : GameplayStormArrivalDefaults.StartupDefaults ?? new GameplayStormArrivalDefaults();
        _stormArrivalDefaults.Initialize();
    }
    private void BuildStormArrivalControls(VBoxContainer tab)
    {
        var group = TuningMenuUi.CreateSection(tab, "Storm arrival", "How a storm announces itself: the gust front, hail, the mist after rain and rainbows. All follow the weather and sun; 0 turns the effect off.");
        AddStormArrivalNumber(group, "GustFrontStrength", "Gust front strength", .01, "Scales the wind shift and speed jump at the leading edge of a storm, and the debris it lifts. 0 = off.");
        AddStormArrivalNumber(group, "GustFrontLeadSeconds", "Gust front lead (s)", 1, "How long a fast storm edge takes to cross the gust front band.");
        AddStormArrivalNumber(group, "Hail", "Hail", .01, "Hail swath count. 0 = no hail.");
        AddStormArrivalNumber(group, "PostRainMist", "Post-rain mist", .01, "Mist that gathers in the haze after rain on calm, cool air. 0 = haze unchanged.");
        AddStormArrivalNumber(group, "Rainbows", "Rainbows", .01, "Rainbow strength when the low sun shines on rain ahead. 0 = none.");
        var row = new HBoxContainer();
        var save = new Button { Name = "StormArrivalSaveNow", Text = "Save storm arrival", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        save.Pressed += () => FlushStormArrivalDefaults(); row.AddChild(save);
        var reset = new Button { Name = "StormArrivalResetDefaults", Text = "Reset storm arrival", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += ResetStormArrivalSettings; row.AddChild(reset); group.AddChild(row);
        _stormArrivalStatus = new Label { Name = "StormArrivalDefaultsStatus", Modulate = RingUiStyle.Muted, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        group.AddChild(_stormArrivalStatus); RefreshStormArrivalControls();
    }
    private void AddStormArrivalNumber(VBoxContainer group, string key, string label, double step, string tooltip)
    {
        group.AddChild(new Label { Text = label, TooltipText = tooltip }); var limits = GameplayStormArrivalDefaults.Limits[key];
        double value = RingStormArrivalSettings.Current.Get(key).AsDouble();
        var row = new HBoxContainer();
        var slider = new HSlider { Name = "StormArrival" + key, TooltipText = tooltip, MinValue = limits.Min, MaxValue = limits.Max, Step = step, Value = value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var entry = new SpinBox { Name = "StormArrival" + key + "Entry", TooltipText = tooltip, MinValue = limits.Min, MaxValue = limits.Max, Step = step, Value = value, CustomMinimumSize = new Vector2(82, 0) };
        slider.ValueChanged += v => { entry.SetValueNoSignal(v); SetStormArrivalTuningValue(key, v); };
        entry.ValueChanged += v => { slider.SetValueNoSignal(v); SetStormArrivalTuningValue(key, v); };
        row.AddChild(slider); row.AddChild(entry); group.AddChild(row);
    }
    public void SetStormArrivalTuningValue(string key, Variant value)
    {
        if (_stormArrivalDefaults?.SetOverride(key, value) != true) return;
        _stormArrivalSaveDelay = .5; _stormArrivalRetryDelay = 0; RefreshStormArrivalControls();
    }
    public void ResetStormArrivalSettings()
    {
        _stormArrivalDefaults?.Reset(); RefreshStormArrivalControls();
        if (!FlushStormArrivalDefaults()) _stormArrivalRetryDelay = 5;
    }
    public bool FlushStormArrivalDefaults()
    {
        bool saved = _stormArrivalDefaults?.Save() ?? true;
        UpdateStormArrivalStatus(); return saved;
    }
    private void ProcessStormArrivalTuning(double delta)
    {
        if (_stormArrivalDefaults?.Dirty != true) return;
        _stormArrivalSaveDelay -= delta; _stormArrivalRetryDelay -= delta;
        if (_stormArrivalSaveDelay <= 0 && _stormArrivalRetryDelay <= 0 && !FlushStormArrivalDefaults()) _stormArrivalRetryDelay = 5;
    }
    public void InstallStormArrivalDefaults(string path)
    {
        if (!path.StartsWith("res://.godot/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal)) return;
        _stormArrivalDefaultsPathOverride = path;
        if (_stormArrivalDefaults == null) return;
        _stormArrivalDefaults = new GameplayStormArrivalDefaults(path); _stormArrivalDefaults.Initialize(); RefreshStormArrivalControls();
    }
    public Godot.Collections.Dictionary GetStormArrivalTuningDiagnostics() => new()
    {
        ["path"] = _stormArrivalDefaults?.GetPath() ?? "", ["dirty"] = _stormArrivalDefaults?.Dirty ?? false,
        ["last_error"] = _stormArrivalDefaults?.LastError ?? "", ["override_count"] = _stormArrivalDefaults?.Settings.Overrides.Count ?? 0
    };
    private void UpdateStormArrivalStatus()
    {
        if (_stormArrivalStatus != null) _stormArrivalStatus.Text = !string.IsNullOrEmpty(_stormArrivalDefaults?.LastError)
            ? "Storm arrival settings could not be saved; retrying automatically."
            : _stormArrivalDefaults?.Dirty == true ? "Storm arrival settings pending." : "Storm arrival settings saved.";
    }
    private void RefreshStormArrivalControls()
    {
        if (_drawer != null) RefreshStormArrivalControlsRecursive(_drawer);
        UpdateStormArrivalStatus();
    }
    private void RefreshStormArrivalControlsRecursive(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            string name = child.Name.ToString();
            string key = name.StartsWith("StormArrival", StringComparison.Ordinal) ? name[12..] : "";
            if (key.EndsWith("Entry", StringComparison.Ordinal)) key = key[..^5];
            if (GameplayStormArrivalDefaults.Limits.ContainsKey(key))
            {
                double value = RingStormArrivalSettings.Current.Get(key).AsDouble();
                if (child is HSlider slider) slider.SetValueNoSignal(value);
                if (child is SpinBox entry) entry.SetValueNoSignal(value);
            }
            RefreshStormArrivalControlsRecursive(child);
        }
    }
}
