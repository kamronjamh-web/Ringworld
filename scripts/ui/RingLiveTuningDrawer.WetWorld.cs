using System;
using Godot;
using RingworldTechDemo.Weather;
namespace RingworldTechDemo.UI;

/// <summary>"Wet world" group of the Clouds tab, under the Rain group (docs/storm-revamp-s2-wet-world-design.md section 4). Persisted sparse overrides in user://gameplay_wet_world_defaults.tres.</summary>
public partial class RingLiveTuningDrawer
{
    private GameplayWetWorldDefaults? _wetWorldDefaults;
    private string? _wetWorldDefaultsPathOverride, _wetWorldLegacyPathOverride;
    private double _wetWorldSaveDelay, _wetWorldRetryDelay;
    private Label? _wetWorldStatus;
    private void InitializeWetWorldTuning()
    {
        _wetWorldDefaults = _wetWorldDefaultsPathOverride != null ? new GameplayWetWorldDefaults(_wetWorldDefaultsPathOverride)
            : GameplayWetWorldDefaults.StartupDefaults ?? new GameplayWetWorldDefaults();
        if (_wetWorldLegacyPathOverride != null) _wetWorldDefaults.LegacySurfaceTerrainPath = _wetWorldLegacyPathOverride;
        _wetWorldDefaults.Initialize();
    }
    private void BuildWetWorldControls(VBoxContainer tab)
    {
        var group = TuningMenuUi.CreateSection(tab, "Wet world", "How the ground, buildings and trees look after and during rain: darkening, gloss, puddles, ripples and drops on the lens. The rain history itself comes from the storm arrival; ripple layers follow the graphics preset.");
        AddWetWorldNumber(group, "Wetness", "Wetness", .01, "Darkening and gloss amount of wet surfaces. 0 = nothing gets wet visibly.");
        AddWetWorldNumber(group, "WetGloss", "Wet gloss", .01, "Wet roughness of glossy wet ground (lower = shinier).");
        AddWetWorldNumber(group, "Puddles", "Puddles", .01, "Puddle amount. 0 = no puddles.");
        AddWetWorldNumber(group, "DryingSpeed", "Drying speed", .01, "How fast wet ground dries after the rain (sun and wind speed it up).");
        AddWetWorldNumber(group, "LensDroplets", "Lens droplets", .01, "Rain drops on the camera lens. 0 = off.");
        var row = new HBoxContainer();
        var save = new Button { Name = "WetWorldSaveNow", Text = "Save wet world", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        save.Pressed += () => FlushWetWorldDefaults(); row.AddChild(save);
        var reset = new Button { Name = "WetWorldResetDefaults", Text = "Reset wet world", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += ResetWetWorldSettings; row.AddChild(reset); group.AddChild(row);
        _wetWorldStatus = new Label { Name = "WetWorldDefaultsStatus", Modulate = RingUiStyle.Muted, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        group.AddChild(_wetWorldStatus); RefreshWetWorldControls();
    }
    private void AddWetWorldNumber(VBoxContainer group, string key, string label, double step, string tooltip)
    {
        group.AddChild(new Label { Text = label, TooltipText = tooltip }); var limits = GameplayWetWorldDefaults.Limits[key];
        double value = RingWetWorldSettings.Current.Get(key).AsDouble();
        var row = new HBoxContainer();
        var slider = new HSlider { Name = "WetWorld" + key, TooltipText = tooltip, MinValue = limits.Min, MaxValue = limits.Max, Step = step, Value = value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var entry = new SpinBox { Name = "WetWorld" + key + "Entry", TooltipText = tooltip, MinValue = limits.Min, MaxValue = limits.Max, Step = step, Value = value, CustomMinimumSize = new Vector2(82, 0) };
        slider.ValueChanged += v => { entry.SetValueNoSignal(v); SetWetWorldTuningValue(key, v); };
        entry.ValueChanged += v => { slider.SetValueNoSignal(v); SetWetWorldTuningValue(key, v); };
        row.AddChild(slider); row.AddChild(entry); group.AddChild(row);
    }
    public void SetWetWorldTuningValue(string key, Variant value)
    {
        if (_wetWorldDefaults?.SetOverride(key, value) != true) return;
        _wetWorldSaveDelay = .5; _wetWorldRetryDelay = 0; RefreshWetWorldControls();
    }
    public void ResetWetWorldSettings()
    {
        _wetWorldDefaults?.Reset(); RefreshWetWorldControls();
        if (!FlushWetWorldDefaults()) _wetWorldRetryDelay = 5;
    }
    public bool FlushWetWorldDefaults()
    {
        bool saved = _wetWorldDefaults?.Save() ?? true;
        UpdateWetWorldStatus(); return saved;
    }
    private void ProcessWetWorldTuning(double delta)
    {
        if (_wetWorldDefaults?.Dirty != true) return;
        _wetWorldSaveDelay -= delta; _wetWorldRetryDelay -= delta;
        if (_wetWorldSaveDelay <= 0 && _wetWorldRetryDelay <= 0 && !FlushWetWorldDefaults()) _wetWorldRetryDelay = 5;
    }
    public void InstallWetWorldDefaults(string path, string? legacySurfaceTerrainPath = null)
    {
        if (!path.StartsWith("res://.godot/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal)) return;
        _wetWorldDefaultsPathOverride = path;
        _wetWorldLegacyPathOverride = legacySurfaceTerrainPath;
        if (_wetWorldDefaults == null) return;
        _wetWorldDefaults = new GameplayWetWorldDefaults(path);
        if (legacySurfaceTerrainPath != null) _wetWorldDefaults.LegacySurfaceTerrainPath = legacySurfaceTerrainPath;
        _wetWorldDefaults.Initialize(); RefreshWetWorldControls();
    }
    public Godot.Collections.Dictionary GetWetWorldTuningDiagnostics() => new()
    {
        ["path"] = _wetWorldDefaults?.GetPath() ?? "", ["dirty"] = _wetWorldDefaults?.Dirty ?? false,
        ["last_error"] = _wetWorldDefaults?.LastError ?? "", ["override_count"] = _wetWorldDefaults?.Settings.Overrides.Count ?? 0
    };
    private void UpdateWetWorldStatus()
    {
        if (_wetWorldStatus != null) _wetWorldStatus.Text = !string.IsNullOrEmpty(_wetWorldDefaults?.LastError)
            ? "Wet world settings could not be saved; retrying automatically."
            : _wetWorldDefaults?.Dirty == true ? "Wet world settings pending." : "Wet world settings saved.";
    }
    private void RefreshWetWorldControls()
    {
        if (_drawer != null) RefreshWetWorldControlsRecursive(_drawer);
        UpdateWetWorldStatus();
    }
    private void RefreshWetWorldControlsRecursive(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            string name = child.Name.ToString();
            string key = name.StartsWith("WetWorld", StringComparison.Ordinal) ? name[8..] : "";
            if (key.EndsWith("Entry", StringComparison.Ordinal)) key = key[..^5];
            if (GameplayWetWorldDefaults.Limits.ContainsKey(key))
            {
                double value = RingWetWorldSettings.Current.Get(key).AsDouble();
                if (child is HSlider slider) slider.SetValueNoSignal(value);
                if (child is SpinBox entry) entry.SetValueNoSignal(value);
            }
            RefreshWetWorldControlsRecursive(child);
        }
    }
}
