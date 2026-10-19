using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private AtmosphereVisualDefaults? _atmosphereDefaults;
    private readonly System.Collections.Generic.Dictionary<string, Range> _atmosphereRanges = new();
    private readonly System.Collections.Generic.Dictionary<string, Label> _atmosphereLabels = new();
    private readonly System.Collections.Generic.List<Control> _atmosphereControls = new();
    private OptionButton? _atmospherePreset;
    private OptionButton? _atmosphereHazeModel;
    private OptionButton? _atmosphereHazeQuality;
    private VBoxContainer? _atmospherePhysicalGroup;
    private CheckButton? _atmosphereEnabled;
    private CheckButton? _atmosphereSkyHazeLut;
    private Label? _atmosphereStatus;
    private double _atmosphereSaveDelay;
    private double _atmosphereRefreshDelay;

    private void InitializeAtmosphereTuning() => RebindAtmosphereDefaults();

    private RingGraphicsQualityController? GetAtmosphereController() =>
        GetNodeOrNull<RingGraphicsQualityController>(GraphicsQualityPath);

    private bool RebindAtmosphereDefaults()
    {
        RingGraphicsQualityController? controller = GetAtmosphereController();
        AtmosphereVisualDefaults? defaults = controller?.AtmosphereDefaults;
        bool changed = !ReferenceEquals(_atmosphereDefaults, defaults);
        _atmosphereDefaults = defaults;
        if (changed) RefreshAtmosphereControls();
        return defaults != null;
    }

    public void InstallAtmosphereDefaults(string isolatedPath)
    {
        GetAtmosphereController()?.InstallAtmosphereDefaults(isolatedPath);
        RebindAtmosphereDefaults();
    }

    public void SetAtmospherePreset(int preset)
    {
        if (!RebindAtmosphereDefaults()) return;
        _atmosphereDefaults!.SetPreset(preset);
        _atmosphereSaveDelay = .5;
        RefreshAtmosphereControls();
    }

    public void SetAtmosphereHazeModel(int model)
    {
        if (!RebindAtmosphereDefaults()) return;
        _atmosphereDefaults!.SetHazeModel(model);
        _atmosphereSaveDelay = .5;
        RefreshAtmosphereControls();
    }

    public void SetAtmosphereHazeQuality(int quality)
    {
        if (!RebindAtmosphereDefaults()) return;
        _atmosphereDefaults!.SetHazeQuality(quality);
        _atmosphereSaveDelay = .5;
        RefreshAtmosphereControls();
    }

    public void SetAtmosphereSkyHazeLut(bool enabled)
    {
        if (!RebindAtmosphereDefaults()) return;
        _atmosphereDefaults!.SetSkyHazeLut(enabled);
        _atmosphereSaveDelay = .5;
        RefreshAtmosphereControls();
    }

    public void SetAtmosphereEnabled(bool enabled)
    {
        GetAtmosphereController()?.SetAtmosphereEnabled(enabled);
        RefreshAtmosphereControls();
    }

    public void SetAtmosphereTuningValue(string key, float value)
    {
        if (!RebindAtmosphereDefaults()) return;
        _atmosphereDefaults!.Set(key, value);
        _atmosphereSaveDelay = .5;
        RefreshAtmosphereControls();
    }

    private void ProcessAtmosphereTuning(double delta)
    {
        if (_open)
        {
            _atmosphereRefreshDelay -= delta;
            if (_atmosphereRefreshDelay <= 0.0)
            {
                _atmosphereRefreshDelay = .25;
                RebindAtmosphereDefaults();
                RefreshAtmosphereControls();
            }
        }

        if (_atmosphereDefaults?.Dirty == true)
        {
            _atmosphereSaveDelay -= delta;
            if (_atmosphereSaveDelay <= 0.0)
                _atmosphereSaveDelay = _atmosphereDefaults.Save() ? 0.0 : 5.0;
            UpdateAtmosphereStatus();
        }
    }

    private void FlushAtmosphereDefaults()
    {
        _atmosphereSaveDelay = 0.0;
        _atmosphereDefaults?.Save();
        UpdateAtmosphereStatus();
    }

    private void BuildAtmosphereTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Atmosphere");
        AtmosphereVisualSettings settings = _atmosphereDefaults?.Settings ?? new AtmosphereVisualSettings();

        _atmosphereEnabled = new CheckButton
        {
            Name = "AtmosphereEnabled",
            Text = "Atmospheric haze",
            ButtonPressed = RingAtmosphereModel.Enabled
        };
        _atmosphereEnabled.Toggled += SetAtmosphereEnabled;
        _atmosphereControls.Add(_atmosphereEnabled);
        tab.AddChild(_atmosphereEnabled);

        _atmospherePreset = new OptionButton { Name = "AtmospherePreset" };
        _atmospherePreset.AddItem("Natural", 0);
        _atmospherePreset.AddItem("Cinematic", 1);
        _atmospherePreset.Select(Mathf.Clamp(settings.Preset, 0, 1));
        _atmospherePreset.ItemSelected += index => SetAtmospherePreset((int)index);
        _atmosphereControls.Add(_atmospherePreset);
        tab.AddChild(_atmospherePreset);
        var hazeModelRow = new HBoxContainer();
        hazeModelRow.AddChild(new Label { Text = "Haze model", CustomMinimumSize = new Vector2(150, 0) });
        _atmosphereHazeModel = new OptionButton { Name = "AtmosphereHazeModel", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _atmosphereHazeModel.AddItem("Legacy", 0);
        _atmosphereHazeModel.AddItem("Physical", 1);
        _atmosphereHazeModel.Select(Mathf.Clamp(settings.HazeModel, 0, 1));
        _atmosphereHazeModel.ItemSelected += index => SetAtmosphereHazeModel((int)index);
        _atmosphereControls.Add(_atmosphereHazeModel);
        hazeModelRow.AddChild(_atmosphereHazeModel);
        tab.AddChild(hazeModelRow);
        AddAtmosphereSlider(tab, "Visibility", "Visibility (km)", 10, 400, 1, settings.VisibilityFullKm);
        AddAtmosphereSlider(tab, "Density", "Density", 0, 2, .01, settings.Density);
        AddAtmosphereSlider(tab, "Altitude", "Altitude falloff", 0, 1, .01, settings.AltitudeFalloff);
        AddAtmosphereSlider(tab, "Aerosol", "Aerosol", 0, 1, .01, settings.Aerosol);
        AddAtmosphereSlider(tab, "Forward", "Forward scattering", 0, .85, .01, settings.ForwardScattering);
        AddAtmosphereSlider(tab, "Warmth", "Sun warmth", 0, 1, .01, settings.SunWarmth);
        AddAtmosphereSlider(tab, "HazeBrightness", "Haze brightness", 0, 2, .01, settings.HazeBrightness);
        AddAtmosphereSlider(tab, "EnclosedCloudFog", "Cloud fog (caves/water)", 0, 1, .01, settings.EnclosedCloudFog);
        // Physical-only controls (hidden while the Legacy model is selected; see RefreshAtmosphereControls).
        _atmospherePhysicalGroup = new VBoxContainer { Name = "AtmospherePhysicalGroup" };
        _atmospherePhysicalGroup.AddChild(new Label { Text = "Physical haze", Modulate = RingUiStyle.Muted });
        AddAtmosphereSlider(_atmospherePhysicalGroup, "CloudShadowing", "Cloud shadowing of haze", 0, 1, .01, settings.CloudShadowing);
        AddAtmosphereSlider(_atmospherePhysicalGroup, "StormHaze", "Storm haze", 0, 1, .01, settings.StormHaze);
        AddAtmosphereSlider(_atmospherePhysicalGroup, "MistDensity", "Ground mist density", 0, 5, .01, settings.MistDensity);
        AddAtmosphereSlider(_atmospherePhysicalGroup, "MistHeight", "Mist height (m)", 50, 2000, 10, settings.MistHeight);
        AddAtmosphereSlider(_atmospherePhysicalGroup, "AerosolHeight", "Aerosol height (m)", 200, 5000, 50, settings.AerosolHeight);
        AddAtmosphereSlider(_atmospherePhysicalGroup, "RayleighStrength", "Rayleigh strength", 0, 3, .01, settings.RayleighStrength);
        AddAtmosphereSlider(_atmospherePhysicalGroup, "GroundAlbedo", "Ground bounce albedo", 0, .5, .01, settings.GroundAlbedo);
        AddAtmosphereSlider(_atmospherePhysicalGroup, "MultiScatter", "Multi-scatter gain", 0, 3, .01, settings.MultiScatter);
        AddAtmosphereSlider(_atmospherePhysicalGroup, "SkyTintR", "Sky tint R", 0, 2, .01, settings.SkyTintR);
        AddAtmosphereSlider(_atmospherePhysicalGroup, "SkyTintG", "Sky tint G", 0, 2, .01, settings.SkyTintG);
        AddAtmosphereSlider(_atmospherePhysicalGroup, "SkyTintB", "Sky tint B", 0, 2, .01, settings.SkyTintB);
        _atmosphereSkyHazeLut = new CheckButton { Name = "AtmosphereSkyHazeLut", Text = "Sky haze LUT", ButtonPressed = settings.SkyHazeLut };
        _atmosphereSkyHazeLut.Toggled += SetAtmosphereSkyHazeLut;
        _atmosphereControls.Add(_atmosphereSkyHazeLut);
        _atmospherePhysicalGroup.AddChild(_atmosphereSkyHazeLut);
        var qualityRow = new HBoxContainer();
        qualityRow.AddChild(new Label { Text = "Quality (points)", CustomMinimumSize = new Vector2(150, 0) });
        _atmosphereHazeQuality = new OptionButton { Name = "AtmosphereHazeQuality", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _atmosphereHazeQuality.AddItem("4", 4);
        _atmosphereHazeQuality.AddItem("8", 8);
        _atmosphereHazeQuality.AddItem("16", 16);
        _atmosphereHazeQuality.ItemSelected += index => SetAtmosphereHazeQuality(_atmosphereHazeQuality.GetItemId((int)index));
        _atmosphereControls.Add(_atmosphereHazeQuality);
        qualityRow.AddChild(_atmosphereHazeQuality);
        _atmospherePhysicalGroup.AddChild(qualityRow);
        tab.AddChild(_atmospherePhysicalGroup);
        var save = new Button { Text = "Save atmosphere", Name = "AtmosphereSaveNow" };
        save.Pressed += () => { if (RebindAtmosphereDefaults()) _atmosphereDefaults!.Save(); UpdateAtmosphereStatus(); };
        _atmosphereControls.Add(save);
        tab.AddChild(save);
        var reset = new Button { Text = "Reset atmosphere", Name = "AtmosphereReset" };
        reset.Pressed += () => { if (RebindAtmosphereDefaults()) _atmosphereDefaults!.Reset(); _atmosphereSaveDelay = 0; RefreshAtmosphereControls(); };
        _atmosphereControls.Add(reset);
        tab.AddChild(reset);
        _atmosphereStatus = new Label { Name = "AtmosphereStatus", Modulate = RingUiStyle.Muted, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        tab.AddChild(_atmosphereStatus);
        RefreshAtmosphereControls();
    }

    private void AddAtmosphereSlider(VBoxContainer parent, string key, string label, double min, double max, double step, double initial)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(150, 0) });
        var slider = new HSlider { Name = "Atmosphere" + key, MinValue = min, MaxValue = max, Step = step, Value = initial, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var value = new Label { Text = initial.ToString("0.###"), CustomMinimumSize = new Vector2(55, 0) };
        slider.ValueChanged += changed => { value.Text = changed.ToString("0.###"); SetAtmosphereTuningValue(key, (float)changed); };
        _atmosphereControls.Add(slider);
        _atmosphereRanges[key] = slider;
        _atmosphereLabels[key] = value;
        row.AddChild(slider);
        row.AddChild(value);
        parent.AddChild(row);
    }

    private void RefreshAtmosphereControls()
    {
        bool available = _atmosphereDefaults != null && GetAtmosphereController() != null;
        foreach (Control control in _atmosphereControls)
        {
            if (control is BaseButton button) button.Disabled = !available;
            if (control is HSlider slider) slider.Editable = available;
        }
        _atmosphereEnabled?.SetPressedNoSignal(RingAtmosphereModel.Enabled);
        if (!available) { UpdateAtmosphereStatus(); return; }

        AtmosphereVisualSettings s = _atmosphereDefaults!.Settings;
        _atmospherePreset?.Select(s.Preset);
        _atmosphereHazeModel?.Select(Mathf.Clamp(s.HazeModel, 0, 1));
        SetAtmosphereRange("Visibility", s.VisibilityFullKm);
        SetAtmosphereRange("Density", s.Density);
        SetAtmosphereRange("Altitude", s.AltitudeFalloff);
        SetAtmosphereRange("Aerosol", s.Aerosol);
        SetAtmosphereRange("Forward", s.ForwardScattering);
        SetAtmosphereRange("Warmth", s.SunWarmth);
        SetAtmosphereRange("HazeBrightness", s.HazeBrightness);
        SetAtmosphereRange("CloudShadowing", s.CloudShadowing);
        SetAtmosphereRange("StormHaze", s.StormHaze);
        SetAtmosphereRange("EnclosedCloudFog", s.EnclosedCloudFog);
        SetAtmosphereRange("MistDensity", s.MistDensity);
        SetAtmosphereRange("MistHeight", s.MistHeight);
        SetAtmosphereRange("AerosolHeight", s.AerosolHeight);
        SetAtmosphereRange("RayleighStrength", s.RayleighStrength);
        SetAtmosphereRange("GroundAlbedo", s.GroundAlbedo);
        SetAtmosphereRange("MultiScatter", s.MultiScatter);
        SetAtmosphereRange("SkyTintR", s.SkyTintR);
        SetAtmosphereRange("SkyTintG", s.SkyTintG);
        SetAtmosphereRange("SkyTintB", s.SkyTintB);
        _atmosphereHazeQuality?.Select(_atmosphereHazeQuality.GetItemIndex(s.HazeQuality));
        _atmosphereSkyHazeLut?.SetPressedNoSignal(s.SkyHazeLut);
        if (_atmospherePhysicalGroup != null) _atmospherePhysicalGroup.Visible = s.HazeModel == 1;
        UpdateAtmosphereStatus();
    }

    private void SetAtmosphereRange(string key, float value)
    {
        if (_atmosphereRanges.TryGetValue(key, out Range? range)) range.SetValueNoSignal(value);
        if (_atmosphereLabels.TryGetValue(key, out Label? label)) label.Text = value.ToString("0.###");
    }

    private void UpdateAtmosphereStatus()
    {
        if (_atmosphereStatus == null) return;
        if (_atmosphereDefaults == null || GetAtmosphereController() == null)
        {
            _atmosphereStatus.Text = "Atmosphere controls unavailable.";
            return;
        }
        string hazeState = !RingAtmosphereModel.Enabled ? "Atmospheric haze: Off"
            : RingAtmosphereModel.Density <= 0.0f ? "Atmospheric haze: enabled (density is zero)"
            : "Atmospheric haze: enabled (" + RingAtmosphereModel.HazeModel + ")";
        string profileState = !string.IsNullOrEmpty(_atmosphereDefaults.LastError) ? "Atmosphere save error: " + _atmosphereDefaults.LastError
            : _atmosphereDefaults.Dirty ? "Saving atmosphere changes..." : "Atmosphere saved";
        _atmosphereStatus.Text = hazeState + " | " + profileState;
    }
}
