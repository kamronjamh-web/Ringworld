using System;
using Godot;
using RingworldTechDemo.Weather;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private GameplayWindDefaults? _windDefaults;
    private string? _windDefaultsPathOverride;
    private double _windSaveDelay, _windSaveRetryDelay;
    private Label? _windSaveStatus;
    private Label? _windRuntimeStatus;
    private double _windRuntimeRefreshDelay;
    private void InitializeWindTuning()
    {
        _windDefaults = _windDefaultsPathOverride != null ? new GameplayWindDefaults(_windDefaultsPathOverride)
            : GameplayWindDefaults.StartupDefaults ?? new GameplayWindDefaults();
        _windDefaults.Initialize();
    }
    private void BuildWindTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Wind");
        tab.AddChild(new Label { Text = "Shared travelling gusts and vegetation response are tuned here. Manual ocean wind remains in Water; cloud travel remains in Clouds.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        var preset = new Button { Name = "WindDramaticStormPreset", Text = "Dramatic storm preset" };
        preset.Pressed += ApplyDramaticStormPreset; tab.AddChild(preset);
        VBoxContainer group = TuningMenuUi.CreateSection(tab, "Storm wind", "Smooth storm buildup and decay use simulation time.");
        AddWindToggle(group, "Enabled", "Enable storm wind and shared gusts");
        AddWindNumber(group, "BaseWindMultiplier", "Mean breeze multiplier", .05);
        AddWindNumber(group, "StormWindSpeed", "Storm wind speed (m/s)", .5);
        AddWindNumber(group, "StormDirectionDegrees", "Storm direction variation (degrees)", 1);
        AddWindNumber(group, "StormRiseSeconds", "Storm rise (seconds)", .1);
        AddWindNumber(group, "StormDecaySeconds", "Storm decay (seconds)", .1);
        group = TuningMenuUi.CreateSection(tab, "Travelling gusts");
        AddWindNumber(group, "GustStrength", "Gust strength", .01);
        AddWindNumber(group, "GustScaleMeters", "Gust pattern size (m)", 10, "The repeating field span. Larger patterns produce larger gust patches; individual patches occupy part of this span.");
        AddWindNumber(group, "GustTravelMultiplier", "Gust travel multiplier", .05);
        AddWindNumber(group, "GustBaseBreeze", "Base breeze response", .01);
        group = TuningMenuUi.CreateSection(tab, "Grass response", "Adjust grass bending and its response to the shared gust patches.");
        AddGrassSlider(group, "GrassWindStrengthMultiplier", "Grass bend strength", 0, 2, .01);
        AddGrassToggle(group, "EnableGrassGustTexture", "Enable grass gust response");
        AddGrassSlider(group, "GrassGustStrength", "Grass gust response", 0, 2, .01);
        group = TuningMenuUi.CreateSection(tab, "Tree response", "Roots stay fixed. Wood stiffness controls trunk and branch bending; leaf flex controls canopy movement independently. Lower trunk strength keeps wood stiff while leaves react to gusts.");
        AddWindNumber(group, "TreeBendStrength", "Tree bend strength", .05);
        AddWindNumber(group, "TreeTrunkBendStrength", "Trunk and branch bend strength", .01);
        AddWindNumber(group, "TreeCanopyFlexStrength", "Leaf and canopy flex strength", .05);
        AddWindNumber(group, "TreeMaximumBendRatio", "Maximum bend / tree height", .01);
        AddWindNumber(group, "TreeTrunkResponseSeconds", "Trunk response and recovery (seconds)", .05);
        AddWindNumber(group, "TreeCanopyResponseSeconds", "Canopy response and recovery (seconds)", .05);
        group = TuningMenuUi.CreateSection(tab, "Water response");
        AddWindNumber(group, "WaterResponseSeconds", "Ocean mean wind response (seconds)", .1);
        AddWindNumber(group, "WaterGustResponseStrength", "Gust detail and foam response", .05);
        AddWindNumber(group, "AnalyticWaveResponseStrength", "Analytic wave response", .05);
        group = TuningMenuUi.CreateSection(tab, "Radar wind");
        AddWindToggle(group, "RadarWindArrowEnabled", "Show wind compass relative to player facing");
        AddWindNumber(group, "RadarWindCalmThreshold", "Calm arrow threshold (m/s)", .05);
        _windRuntimeStatus = new Label { Name = "WindRuntimeStatus", Modulate = RingUiStyle.Muted, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        tab.AddChild(_windRuntimeStatus);
        UpdateWindRuntimeStatus();
        var row = new HBoxContainer();
        var save = new Button { Name = "WindSaveNow", Text = "Save wind settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        save.Pressed += () => FlushWindDefaults(); row.AddChild(save);
        var reset = new Button { Name = "WindResetDefaults", Text = "Reset wind settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += ResetWindSettings; row.AddChild(reset); tab.AddChild(row);
        _windSaveStatus = new Label { Name = "WindDefaultsStatus", Modulate = RingUiStyle.Muted, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        tab.AddChild(_windSaveStatus); RefreshWindControls();
    }
    private void AddWindNumber(VBoxContainer parent, string key, string label, double step, string tooltip = "")
    {
        var limits = GameplayWindDefaults.Limits[key];
        parent.AddChild(new Label { Text = label, TooltipText = tooltip });
        var row = new HBoxContainer();
        double initial = RingWindSettings.Current.Get(key).AsDouble();
        var slider = new HSlider { Name = "Wind" + key, TooltipText = tooltip, MinValue = limits.Min, MaxValue = limits.Max, Step = step, Value = initial, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var entry = new SpinBox { Name = "Wind" + key + "Entry", TooltipText = tooltip, MinValue = limits.Min, MaxValue = limits.Max, Step = step, Value = initial, CustomMinimumSize = new Vector2(82, 0) };
        slider.ValueChanged += value => { entry.SetValueNoSignal(value); SetWindTuningValue(key, value); };
        entry.ValueChanged += value => { slider.SetValueNoSignal(value); SetWindTuningValue(key, value); };
        row.AddChild(slider); row.AddChild(entry); parent.AddChild(row);
    }
    private void AddWindToggle(VBoxContainer parent, string key, string label)
    {
        var toggle = new CheckButton { Name = "Wind" + key, Text = label, ButtonPressed = RingWindSettings.Current.Get(key).AsBool() };
        toggle.Toggled += value => SetWindTuningValue(key, value); parent.AddChild(toggle);
    }
    public void SetWindTuningValue(string key, Variant value)
    {
        if (_windDefaults?.SetOverride(key, value) != true) return;
        _windSaveDelay = .5; _windSaveRetryDelay = 0; RefreshWindControls();
    }
    public void ApplyDramaticStormPreset()
    {
        _windDefaults?.ApplyDramaticStormPreset(); _windSaveDelay = .5; _windSaveRetryDelay = 0; RefreshWindControls();
    }
    public void ResetWindSettings()
    {
        foreach (string key in GameplayGrassDefaults.WindResponseKeys) _pendingGrassValues.Remove(key);
        _grassDefaults?.ResetWindResponse(_grassGraphics);
        _windDefaults?.Reset(); RefreshWindControls(); RefreshGrassControls();
        if (!FlushWindDefaults()) _windSaveRetryDelay = 5;
    }
    public bool FlushWindDefaults()
    {
        bool saved = _windDefaults?.Save() ?? true;
        if (_grassDefaults != null) saved = FlushGrassDefaults() && saved;
        UpdateWindSaveStatus(); return saved;
    }
    private void ProcessWindTuning(double delta)
    {
        if (_open)
        {
            _windRuntimeRefreshDelay -= delta;
            if (_windRuntimeRefreshDelay <= 0)
            {
                _windRuntimeRefreshDelay = .2;
                UpdateWindRuntimeStatus();
                UpdateWindSaveStatus();
            }
        }
        if (_windDefaults?.Dirty != true) return;
        _windSaveDelay -= delta; _windSaveRetryDelay -= delta;
        if (_windSaveDelay <= 0 && _windSaveRetryDelay <= 0 && !FlushWindDefaults()) _windSaveRetryDelay = 5;
    }
    /// <summary>Use isolated generated preferences before Ready in smoke tests.</summary>
    public void InstallWindDefaults(string path)
    {
        if (!path.StartsWith("res://.godot/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal)) return;
        _windDefaultsPathOverride = path;
        if (_windDefaults == null) return;
        _windDefaults = new GameplayWindDefaults(path); _windDefaults.Initialize(); RefreshWindControls();
    }
    public Godot.Collections.Dictionary GetWindTuningDiagnostics() => new()
    {
        ["path"] = _windDefaults?.GetPath() ?? "", ["dirty"] = _windDefaults?.Dirty ?? false,
        ["last_error"] = _windDefaults?.LastError ?? "", ["override_count"] = _windDefaults?.Settings.Overrides.Count ?? 0
    };
    private void RefreshWindControls()
    {
        if (_drawer != null) RefreshWindControlsRecursive(_drawer);
        UpdateWindSaveStatus();
        UpdateWindRuntimeStatus();
    }
    private void RefreshWindControlsRecursive(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            string name = child.Name.ToString();
            string key = name.StartsWith("Wind", StringComparison.Ordinal) ? name[4..] : "";
            if (key.EndsWith("Entry", StringComparison.Ordinal)) key = key[..^5];
            if (GameplayWindDefaults.Limits.ContainsKey(key))
            {
                double value = RingWindSettings.Current.Get(key).AsDouble();
                if (child is HSlider slider) slider.SetValueNoSignal(value);
                if (child is SpinBox entry) entry.SetValueNoSignal(value);
            }
            else if (key is "Enabled" or "RadarWindArrowEnabled" && child is CheckButton toggle)
                toggle.SetPressedNoSignal(RingWindSettings.Current.Get(key).AsBool());
            RefreshWindControlsRecursive(child);
        }
    }
    private void UpdateWindSaveStatus()
    {
        if (_windSaveStatus != null) _windSaveStatus.Text = !string.IsNullOrEmpty(_windDefaults?.LastError) || !string.IsNullOrEmpty(_grassDefaults?.LastError)
            ? "Wind settings could not be saved; retrying automatically."
            : _windDefaults?.Dirty == true || _grassDefaults?.Dirty == true || _pendingGrassValues.Count > 0 ? "Wind settings pending." : "Wind settings saved.";
    }
    private void UpdateWindRuntimeStatus()
    {
        if (_windRuntimeStatus == null) return;
        string wind;
        if (_weatherEffects == null)
            wind = "Local wind unavailable.";
        else
        {
            var snapshot = _weatherEffects.CurrentWindSnapshot;
            wind = $"Wind: {(snapshot.Enabled ? "enabled" : "calm / disabled")}; mean {snapshot.MeanWind.Length():0.0} m/s ({snapshot.MeanWind.X:0.0}, {snapshot.MeanWind.Y:0.0}), instantaneous {snapshot.InstantWind.Length():0.0} m/s."
                + $"\nStorm influence {snapshot.StormFactor:0.00}; gust multiplier {snapshot.GustMultiplier:0.00}; simulation time {snapshot.SimulationTime:0.0} s.";
        }
        string water = _waterFft is RingWaterFftController fft
            ? $"\nWater: {(fft.FollowWeatherWind ? "following mean wind" : "manual wind")}; smoothed {fft.ActualWindSpeedMetersPerSecond:0.0} m/s toward {fft.ActualWindDirectionDegrees:0} degrees; response {fft.StormResponseSeconds:0.0} s; spectrum configurations {fft.SpectrumConfigurationCount}. {fft.Status}"
            : "\nWater: analytic fallback; no FFT controller.";
        _windRuntimeStatus.Text = wind + water;
    }
}
