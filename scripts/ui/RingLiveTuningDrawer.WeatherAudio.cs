using System;
using Godot;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private GameplayWeatherAudioDefaults? _weatherAudioDefaults;
    private string? _weatherAudioDefaultsPathOverride;
    private double _weatherAudioSaveDelay, _weatherAudioSaveRetry;
    private Label? _weatherAudioStatus;
    private CheckButton? _weatherAudioThunder, _weatherAudioAmbience;
    private HSlider? _weatherAudioThunderVolume, _weatherAudioRain, _weatherAudioWind, _weatherAudioDistant, _weatherAudioWidth, _weatherAudioNear;
    private bool _weatherAudioRefreshing;

    private void InitializeWeatherAudioTuning()
    {
        _weatherAudioDefaults = new GameplayWeatherAudioDefaults(_weatherAudioDefaultsPathOverride);
        _weatherAudioDefaults.Load();
        _weatherAudioDefaults.Apply(_weatherAudio, _weatherAmbience);
    }

    private void ProcessWeatherAudioTuning(double delta)
    {
        if (_weatherAudioDefaults?.Dirty != true) return;
        _weatherAudioSaveDelay -= delta;
        _weatherAudioSaveRetry -= delta;
        if (_weatherAudioSaveDelay <= 0 && _weatherAudioSaveRetry <= 0 && !FlushWeatherAudioDefaults()) _weatherAudioSaveRetry = 5;
    }

    public bool FlushWeatherAudioDefaults()
    {
        bool saved = _weatherAudioDefaults?.Save() ?? true;
        RefreshWeatherAudioStatus();
        return saved;
    }

    public void InstallWeatherAudioDefaults(string isolatedPath)
    {
        if (!isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal) && !isolatedPath.StartsWith("user://generated/", StringComparison.Ordinal)) return;
        _weatherAudioDefaultsPathOverride = isolatedPath;
        if (_weatherAudioDefaults is null) return;
        _weatherAudioDefaults = new GameplayWeatherAudioDefaults(isolatedPath);
        _weatherAudioDefaults.Load();
        _weatherAudioDefaults.Apply(_weatherAudio, _weatherAmbience);
        RefreshWeatherAudioControls();
    }

    /// <summary>Same labels and control layout as the rows this replaced (tests and players find them by text); controls also carry stable names.</summary>
    private void BuildWeatherAudioControls(VBoxContainer section)
    {
        GameplayWeatherAudioSettings s = _weatherAudioDefaults?.Settings ?? new GameplayWeatherAudioSettings();
        _weatherAudioThunder = AddWeatherAudioToggle(section, "WeatherAudioThunder", "Thunder", s.ThunderEnabled, _weatherAudio != null);
        _weatherAudioThunderVolume = AddWeatherAudioSlider(section, "WeatherAudioThunderVolume", "Thunder volume", nameof(GameplayWeatherAudioSettings.ThunderVolumeDb), s.ThunderVolumeDb, _weatherAudio != null);
        _weatherAudioAmbience = AddWeatherAudioToggle(section, "WeatherAudioAmbience", "Weather ambience", s.AmbienceEnabled, _weatherAmbience != null);
        _weatherAudioRain = AddWeatherAudioSlider(section, "WeatherAudioRainVolume", "Rain volume", nameof(GameplayWeatherAudioSettings.RainVolumeDb), s.RainVolumeDb, _weatherAmbience != null);
        _weatherAudioWind = AddWeatherAudioSlider(section, "WeatherAudioWindVolume", "Wind volume", nameof(GameplayWeatherAudioSettings.WindVolumeDb), s.WindVolumeDb, _weatherAmbience != null);
        _weatherAudioDistant = AddWeatherAudioSlider(section, "WeatherAudioDistantStormVolume", "Distant storm volume", nameof(GameplayWeatherAudioSettings.DistantStormVolumeDb), s.DistantStormVolumeDb, _weatherAmbience != null);
        _weatherAudioWidth = AddWeatherAudioSlider(section, "WeatherAudioSpatialWidth", "Spatial width", nameof(GameplayWeatherAudioSettings.SpatialWidth), s.SpatialWidth, _weatherAmbience != null,
            "How strongly the four directional rain, wind and storm speakers pan against the camera. Low is a calm, wide wash; high puts each direction clearly to one side.", .05);
        _weatherAudioNear = AddWeatherAudioSlider(section, "WeatherAudioNearDetailVolume", "Near detail volume", nameof(GameplayWeatherAudioSettings.NearDetailVolumeDb), s.NearDetailVolumeDb, _weatherAmbience != null,
            "Level of individual drops and drips close to you. Takes effect once near-detail sounds are available.");
        var reset = new Button { Name = "WeatherAudioReset", Text = "Reset weather audio", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Restores the thunder, rain, wind and distant storm settings to their defaults." };
        reset.Pressed += ResetWeatherAudio;
        section.AddChild(reset);
        _weatherAudioStatus = new Label { Name = "WeatherAudioStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        section.AddChild(_weatherAudioStatus);
        RefreshWeatherAudioStatus();
    }

    private CheckButton AddWeatherAudioToggle(VBoxContainer parent, string name, string text, bool pressed, bool enabled)
    {
        var toggle = new CheckButton { Name = name, Text = text, ButtonPressed = pressed, Disabled = !enabled };
        toggle.Toggled += _ => QueueWeatherAudioSettings();
        parent.AddChild(toggle);
        return toggle;
    }

    private HSlider AddWeatherAudioSlider(VBoxContainer parent, string name, string label, string field, double initial, bool enabled, string tooltip = "", double step = .5)
    {
        var (min, max) = GameplayWeatherAudioDefaults.RangeOf(field);
        parent.AddChild(new Label { Text = label, TooltipText = tooltip });
        var row = new HBoxContainer();
        var slider = new HSlider { Name = name, MinValue = min, MaxValue = max, Step = step, Value = initial, Editable = enabled, TooltipText = tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var value = new Label { Text = initial.ToString("0.###"), CustomMinimumSize = new Vector2(58, 0) };
        slider.ValueChanged += current => { value.Text = current.ToString("0.###"); QueueWeatherAudioSettings(); };
        row.AddChild(slider); row.AddChild(value); parent.AddChild(row);
        return slider;
    }

    private void QueueWeatherAudioSettings()
    {
        if (_weatherAudioRefreshing || _weatherAudioDefaults == null || _weatherAudioThunder == null || _weatherAudioThunderVolume == null || _weatherAudioAmbience == null
            || _weatherAudioRain == null || _weatherAudioWind == null || _weatherAudioDistant == null || _weatherAudioWidth == null || _weatherAudioNear == null) return;
        bool changed = _weatherAudioDefaults.SetThunderEnabled(_weatherAudioThunder.ButtonPressed);
        changed |= _weatherAudioDefaults.SetThunderVolumeDb((float)_weatherAudioThunderVolume.Value);
        changed |= _weatherAudioDefaults.SetAmbienceEnabled(_weatherAudioAmbience.ButtonPressed);
        changed |= _weatherAudioDefaults.SetRainVolumeDb((float)_weatherAudioRain.Value);
        changed |= _weatherAudioDefaults.SetWindVolumeDb((float)_weatherAudioWind.Value);
        changed |= _weatherAudioDefaults.SetDistantStormVolumeDb((float)_weatherAudioDistant.Value);
        changed |= _weatherAudioDefaults.SetSpatialWidth((float)_weatherAudioWidth.Value);
        changed |= _weatherAudioDefaults.SetNearDetailVolumeDb((float)_weatherAudioNear.Value);
        _weatherAudioDefaults.Apply(_weatherAudio, _weatherAmbience);
        if (!changed) return;
        _weatherAudioSaveDelay = .6;
        RefreshWeatherAudioStatus();
    }

    private void ResetWeatherAudio()
    {
        _weatherAudioDefaults?.Reset();
        _weatherAudioDefaults?.Apply(_weatherAudio, _weatherAmbience);
        RefreshWeatherAudioControls();
        FlushWeatherAudioDefaults();
    }

    private void RefreshWeatherAudioControls()
    {
        if (_weatherAudioDefaults == null || _weatherAudioThunder == null) return;
        GameplayWeatherAudioSettings s = _weatherAudioDefaults.Settings;
        _weatherAudioRefreshing = true;
        _weatherAudioThunder.SetPressedNoSignal(s.ThunderEnabled);
        _weatherAudioAmbience?.SetPressedNoSignal(s.AmbienceEnabled);
        SetWeatherAudioSlider(_weatherAudioThunderVolume, s.ThunderVolumeDb);
        SetWeatherAudioSlider(_weatherAudioRain, s.RainVolumeDb);
        SetWeatherAudioSlider(_weatherAudioWind, s.WindVolumeDb);
        SetWeatherAudioSlider(_weatherAudioDistant, s.DistantStormVolumeDb);
        SetWeatherAudioSlider(_weatherAudioWidth, s.SpatialWidth);
        SetWeatherAudioSlider(_weatherAudioNear, s.NearDetailVolumeDb);
        _weatherAudioRefreshing = false;
        RefreshWeatherAudioStatus();
    }

    private static void SetWeatherAudioSlider(HSlider? slider, double value)
    {
        if (slider == null) return;
        slider.SetValueNoSignal(value);
        // The value label sits next to the slider in its row.
        if (slider.GetParent() is HBoxContainer row && row.GetChildCount() > 1 && row.GetChild(1) is Label label) label.Text = value.ToString("0.###");
    }

    private void RefreshWeatherAudioStatus()
    {
        if (_weatherAudioStatus == null) return;
        _weatherAudioStatus.Text = !string.IsNullOrEmpty(_weatherAudioDefaults?.LastError)
            ? "Weather audio settings could not be saved; retrying automatically."
            : _weatherAudioDefaults?.Dirty == true ? "Weather audio settings pending." : "Weather audio settings saved.";
    }

    public Godot.Collections.Dictionary GetWeatherAudioTuningDiagnostics() => new()
    {
        ["path"] = _weatherAudioDefaults?.GetPath() ?? "", ["dirty"] = _weatherAudioDefaults?.Dirty ?? false, ["last_error"] = _weatherAudioDefaults?.LastError ?? ""
    };
}
