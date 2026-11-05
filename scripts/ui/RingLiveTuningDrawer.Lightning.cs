using System;
using Godot;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private GameplayLightningDefaults? _lightningDefaults;
    private string? _lightningDefaultsPathOverride;
    private double _lightningSaveDelay, _lightningSaveRetry;
    private Label? _lightningStatus;
    private CheckButton? _lightningReduced;
    private HSlider? _lightningFrequency, _lightningUpperAtmosphere, _lightningIllumination, _lightningIlluminationReach;
    private bool _lightningRefreshing;

    private void InitializeLightningTuning()
    {
        _lightningDefaults = new GameplayLightningDefaults(_lightningDefaultsPathOverride);
        _lightningDefaults.Load();
        _lightningDefaults.Apply();
    }

    private void ProcessLightningTuning(double delta)
    {
        if (_lightningDefaults?.Dirty != true) return;
        _lightningSaveDelay -= delta;
        _lightningSaveRetry -= delta;
        if (_lightningSaveDelay <= 0 && _lightningSaveRetry <= 0 && !FlushLightningDefaults()) _lightningSaveRetry = 5;
    }

    public bool FlushLightningDefaults()
    {
        bool saved = _lightningDefaults?.Save() ?? true;
        RefreshLightningStatus();
        return saved;
    }

    public void InstallLightningDefaults(string isolatedPath)
    {
        if (!isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal) && !isolatedPath.StartsWith("user://generated/", StringComparison.Ordinal)) return;
        _lightningDefaultsPathOverride = isolatedPath;
        if (_lightningDefaults is null) return;
        _lightningDefaults = new GameplayLightningDefaults(isolatedPath);
        _lightningDefaults.Load();
        _lightningDefaults.Apply();
        RefreshLightningControls();
    }

    private void BuildLightningControls(VBoxContainer section)
    {
        GameplayLightningSettings s = _lightningDefaults?.Settings ?? new GameplayLightningSettings();
        _lightningFrequency = AddLightningSlider(section, "LightningFrequency", "Lightning frequency", nameof(GameplayLightningSettings.LightningFrequency), s.LightningFrequency,
            "How often natural storms strike. 0 is silent, 1 is the default, 2 doubles it.");
        _lightningUpperAtmosphere = AddLightningSlider(section, "LightningUpperAtmosphere", "Upper-atmosphere lightning", nameof(GameplayLightningSettings.UpperAtmosphereLightning), s.UpperAtmosphereLightning,
            "How often red sprites and blue jets appear above strong storms, visible across the arch at night. 0 hides them.");
        _lightningIllumination = AddLightningSlider(section, "LightningIllumination", "Lightning illumination", nameof(GameplayLightningSettings.LightningIllumination), s.LightningIllumination,
            "How brightly lightning lights the terrain, vegetation and nearby objects. 0 removes this surface lighting, 1 is the default, 2 doubles its strength.");
        _lightningIlluminationReach = AddLightningSlider(section, "LightningIlluminationReachKm", "Illumination reach (km)", nameof(GameplayLightningSettings.LightningIlluminationReachKm), s.LightningIlluminationReachKm,
            "How far lightning lights surrounding surfaces, in kilometres. The light fades toward this distance. Default: 20 km.");
        _lightningReduced = new CheckButton
        {
            Name = "LightningReducedFlashes", Text = "Reduced flashes", ButtonPressed = s.ReducedFlashes,
            TooltipText = "Accessibility: dims and softens lightning, thins out strikes and sprites, and limits how fast brightness can change between frames."
        };
        _lightningReduced.Toggled += _ => QueueLightningSettings();
        section.AddChild(_lightningReduced);
        var reset = new Button { Name = "LightningReset", Text = "Reset lightning", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Restores all lightning settings to their defaults." };
        reset.Pressed += ResetLightning;
        section.AddChild(reset);
        _lightningStatus = new Label { Name = "LightningStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        section.AddChild(_lightningStatus);
        RefreshLightningStatus();
    }

    private HSlider AddLightningSlider(VBoxContainer parent, string name, string label, string field, double initial, string tooltip)
    {
        var (min, max) = GameplayLightningDefaults.RangeOf(field);
        parent.AddChild(new Label { Text = label, TooltipText = tooltip });
        var row = new HBoxContainer();
        var slider = new HSlider { Name = name, MinValue = min, MaxValue = max, Step = .05, Value = initial, TooltipText = tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var value = new Label { Text = initial.ToString("0.###"), CustomMinimumSize = new Vector2(58, 0) };
        slider.ValueChanged += current => { value.Text = current.ToString("0.###"); QueueLightningSettings(); };
        row.AddChild(slider); row.AddChild(value); parent.AddChild(row);
        return slider;
    }

    private void QueueLightningSettings()
    {
        if (_lightningRefreshing || _lightningDefaults == null || _lightningFrequency == null || _lightningUpperAtmosphere == null || _lightningIllumination == null || _lightningIlluminationReach == null || _lightningReduced == null) return;
        bool changed = _lightningDefaults.SetLightningFrequency((float)_lightningFrequency.Value);
        changed |= _lightningDefaults.SetUpperAtmosphereLightning((float)_lightningUpperAtmosphere.Value);
        changed |= _lightningDefaults.SetLightningIllumination((float)_lightningIllumination.Value);
        changed |= _lightningDefaults.SetLightningIlluminationReachKm((float)_lightningIlluminationReach.Value);
        changed |= _lightningDefaults.SetReducedFlashes(_lightningReduced.ButtonPressed);
        _lightningDefaults.Apply();
        if (!changed) return;
        _lightningSaveDelay = .6;
        RefreshLightningStatus();
    }

    private void ResetLightning()
    {
        _lightningDefaults?.Reset();
        _lightningDefaults?.Apply();
        RefreshLightningControls();
        FlushLightningDefaults();
    }

    private void RefreshLightningControls()
    {
        if (_lightningDefaults == null || _lightningFrequency == null) return;
        GameplayLightningSettings s = _lightningDefaults.Settings;
        _lightningRefreshing = true;
        SetWeatherAudioSlider(_lightningFrequency, s.LightningFrequency);
        SetWeatherAudioSlider(_lightningUpperAtmosphere, s.UpperAtmosphereLightning);
        SetWeatherAudioSlider(_lightningIllumination, s.LightningIllumination);
        SetWeatherAudioSlider(_lightningIlluminationReach, s.LightningIlluminationReachKm);
        _lightningReduced?.SetPressedNoSignal(s.ReducedFlashes);
        _lightningRefreshing = false;
        RefreshLightningStatus();
    }

    private void RefreshLightningStatus()
    {
        if (_lightningStatus == null) return;
        _lightningStatus.Text = !string.IsNullOrEmpty(_lightningDefaults?.LastError)
            ? "Lightning settings could not be saved; retrying automatically."
            : _lightningDefaults?.Dirty == true ? "Lightning settings pending." : "Lightning settings saved.";
    }

    public Godot.Collections.Dictionary GetLightningTuningDiagnostics() => new()
    {
        ["path"] = _lightningDefaults?.GetPath() ?? "", ["dirty"] = _lightningDefaults?.Dirty ?? false, ["last_error"] = _lightningDefaults?.LastError ?? ""
    };
}
