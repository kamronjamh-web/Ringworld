using System;
using Godot;
using RingworldTechDemo.Weather;
namespace RingworldTechDemo.UI;

/// <summary>"Megastorm" group of the Clouds tab (docs/megastorm-design.md section 7). Initialize, Process, Flush and Refresh are chained from the Wind partial.</summary>
public partial class RingLiveTuningDrawer
{
    private GameplayMegastormDefaults? _megastormDefaults;
    private string? _megastormDefaultsPathOverride;
    private double _megastormSaveDelay, _megastormRetryDelay;
    private Label? _megastormStatus;
    /// <summary>The megastorm node the debug spawn buttons drive; a sibling of the drawer in the integration scene.</summary>
    [Export] public NodePath MegastormPath { get; set; } = new("../RingMegastorm");
    private void InitializeMegastormTuning()
    {
        _megastormDefaults = _megastormDefaultsPathOverride != null ? new GameplayMegastormDefaults(_megastormDefaultsPathOverride)
            : GameplayMegastormDefaults.StartupDefaults ?? new GameplayMegastormDefaults();
        _megastormDefaults.Initialize();
    }
    private void BuildMegastormControls(VBoxContainer tab)
    {
        var group = TuningMenuUi.CreateSection(tab, "Megastorm", "The ring-wide storm wall once the story triggers it. Speed changes take effect at once and keep the wall where it is. Sizes are fixed in km, so the pass time follows the speed. The buttons at the bottom spawn or remove a debug megastorm ahead of you.");
        AddMegastormNumber(group, "SpeedMetersPerSecond", "Megastorm speed (m/s)", 1, "Wall speed. 350 m/s laps the ring in about 20 in-game days; the 390 km pass over a point then lasts about a quarter day.");
        AddMegastormNumber(group, "TrailingLengthKm", "Megastorm trailing length (km)", 1, "Length of the trailing rain behind the wall. The whole pass is 50 km plus this (390 km by default).");
        AddMegastormNumber(group, "WarningReachKm", "Megastorm warning reach (km)", 1, "How early the world warns: the precursor sky extent and the ground-hum stages (hum from 10x this distance, water and wildlife at 2x).");
        AddMegastormNumber(group, "SoundVolumeDb", "Megastorm sound volume (dB)", 0.5, "Level of the megastorm's own sound: the ground hum, the singing walls, the edge boom and the roar. The weather volumes above it are separate.");
        AddMegastormNumber(group, "SpawnDistanceKm", "Spawn distance ahead (km)", 1, "Debug spawn: how far ahead of you, in the direction you face, \"Spawn megastorm\" places the wall edge. It arrives at the speed above, fully formed.");
        var spawnRow = new HBoxContainer();
        var spawn = new Button { Name = "MegastormSpawn", Text = "Spawn megastorm", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        spawn.Pressed += SpawnMegastormFromMenu; spawnRow.AddChild(spawn);
        var stop = new Button { Name = "MegastormStop", Text = "Stop megastorm", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        stop.Pressed += StopMegastormFromMenu; spawnRow.AddChild(stop); group.AddChild(spawnRow);
        var row = new HBoxContainer();
        var save = new Button { Name = "MegastormSaveNow", Text = "Save megastorm", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        save.Pressed += () => FlushMegastormDefaults(); row.AddChild(save);
        var reset = new Button { Name = "MegastormResetDefaults", Text = "Reset megastorm", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += ResetMegastormSettings; row.AddChild(reset); group.AddChild(row);
        _megastormStatus = new Label { Name = "MegastormDefaultsStatus", Modulate = RingUiStyle.Muted, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        group.AddChild(_megastormStatus); RefreshMegastormControls();
    }
    private void AddMegastormNumber(VBoxContainer group, string key, string label, double step, string tooltip)
    {
        group.AddChild(new Label { Text = label, TooltipText = tooltip }); var limits = GameplayMegastormDefaults.Limits[key];
        double value = RingMegastormSettings.Current.Get(key).AsDouble();
        var row = new HBoxContainer();
        var slider = new HSlider { Name = "Megastorm" + key, TooltipText = tooltip, MinValue = limits.Min, MaxValue = limits.Max, Step = step, Value = value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var entry = new SpinBox { Name = "Megastorm" + key + "Entry", TooltipText = tooltip, MinValue = limits.Min, MaxValue = limits.Max, Step = step, Value = value, CustomMinimumSize = new Vector2(82, 0) };
        slider.ValueChanged += v => { entry.SetValueNoSignal(v); SetMegastormTuningValue(key, v); };
        entry.ValueChanged += v => { slider.SetValueNoSignal(v); SetMegastormTuningValue(key, v); };
        row.AddChild(slider); row.AddChild(entry); group.AddChild(row);
    }
    public void SpawnMegastormFromMenu()
    {
        var node = GetNodeOrNull<RingMegastorm>(MegastormPath);
        float km = RingMegastormSettings.Current.SpawnDistanceKm;
        string text = node == null ? "No megastorm node in this scene."
            : node.SpawnAhead(km * 1000.0) ? $"Megastorm spawned {km:0} km ahead." : "Megastorm not spawned (no observer yet).";
        if (_megastormStatus != null) _megastormStatus.Text = text;
    }
    public void StopMegastormFromMenu()
    {
        var node = GetNodeOrNull<RingMegastorm>(MegastormPath);
        string text = node == null ? "No megastorm node in this scene." : node.RemoveMegastorm() ? "Megastorm removed." : "No megastorm is running.";
        if (_megastormStatus != null) _megastormStatus.Text = text;
    }
    public void SetMegastormTuningValue(string key, Variant value)
    {
        if (_megastormDefaults?.SetOverride(key, value) != true) return;
        _megastormSaveDelay = .5; _megastormRetryDelay = 0; RefreshMegastormControls();
    }
    public void ResetMegastormSettings()
    {
        _megastormDefaults?.Reset(); RefreshMegastormControls();
        if (!FlushMegastormDefaults()) _megastormRetryDelay = 5;
    }
    public bool FlushMegastormDefaults()
    {
        bool saved = _megastormDefaults?.Save() ?? true;
        UpdateMegastormStatus(); return saved;
    }
    private void ProcessMegastormTuning(double delta)
    {
        if (_megastormDefaults?.Dirty != true) return;
        _megastormSaveDelay -= delta; _megastormRetryDelay -= delta;
        if (_megastormSaveDelay <= 0 && _megastormRetryDelay <= 0 && !FlushMegastormDefaults()) _megastormRetryDelay = 5;
    }
    public void InstallMegastormDefaults(string path)
    {
        if (!path.StartsWith("res://.godot/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal)) return;
        _megastormDefaultsPathOverride = path;
        if (_megastormDefaults == null) return;
        _megastormDefaults = new GameplayMegastormDefaults(path); _megastormDefaults.Initialize(); RefreshMegastormControls();
    }
    public Godot.Collections.Dictionary GetMegastormTuningDiagnostics() => new()
    {
        ["path"] = _megastormDefaults?.GetPath() ?? "", ["dirty"] = _megastormDefaults?.Dirty ?? false,
        ["last_error"] = _megastormDefaults?.LastError ?? "", ["override_count"] = _megastormDefaults?.Settings.Overrides.Count ?? 0
    };
    private void UpdateMegastormStatus()
    {
        if (_megastormStatus != null) _megastormStatus.Text = !string.IsNullOrEmpty(_megastormDefaults?.LastError)
            ? "Megastorm settings could not be saved; retrying automatically."
            : _megastormDefaults?.Dirty == true ? "Megastorm settings pending." : "Megastorm settings saved.";
    }
    private void RefreshMegastormControls()
    {
        if (_drawer != null) RefreshMegastormControlsRecursive(_drawer);
        UpdateMegastormStatus();
    }
    private void RefreshMegastormControlsRecursive(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            string name = child.Name.ToString();
            string key = name.StartsWith("Megastorm", StringComparison.Ordinal) ? name["Megastorm".Length..] : "";
            if (key.EndsWith("Entry", StringComparison.Ordinal)) key = key[..^5];
            if (GameplayMegastormDefaults.Limits.ContainsKey(key))
            {
                double value = RingMegastormSettings.Current.Get(key).AsDouble();
                if (child is HSlider slider) slider.SetValueNoSignal(value);
                if (child is SpinBox entry) entry.SetValueNoSignal(value);
            }
            RefreshMegastormControlsRecursive(child);
        }
    }
}
