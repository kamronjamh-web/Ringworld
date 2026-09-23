using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Menu-only live controls. Edits persist to the project preset in development.</summary>
public partial class MenuFlyoverTuningDrawer : Control
{
    [Export] public NodePath BackdropPath { get; set; } = new("../SpaceBackdrop");
    [Export] public string SavePathOverride { get; set; } = "";

    private readonly record struct NumericSpec(double Min, double Max, double Step, string Suffix = "");
    private static readonly Dictionary<string, NumericSpec> Ranges = new()
    {
        ["AltitudeKm"] = new(8, 80, .5, "km"),
        ["PitchDegrees"] = new(5, 65, .5, "deg"),
        ["SpeedKmPerSecond"] = new(0, 15, .1, "km/s"),
        ["FieldOfView"] = new(35, 95, 1, "deg"),
        ["SunEnergy"] = new(0, 5, .05),
        ["SunElevationDegrees"] = new(-90, 90, .01, "deg"),
        ["SunAzimuthDegrees"] = new(-180, 180, .01, "deg"),
        ["AmbientEnergy"] = new(0, 2, .05),
        ["HazeAmount"] = new(0, .4, .01),
        ["HazeStartKm"] = new(20, 2000, 10, "km"),
        ["HazeEndKm"] = new(40, 4000, 10, "km"),
        ["GlowIntensity"] = new(0, 1.5, .05),
        ["CloudCoverage"] = new(0, 1, .01),
        ["CloudDensity"] = new(0, 3, .05),
        ["CloudFloorKm"] = new(0, 19, .05, "km"),
        ["CloudCeilingKm"] = new(.5, 20, .05, "km"),
        ["CloudDriftMps"] = new(0, 100, 1, "m/s"),
        ["CloudDitherSpeed"] = new(0, 120, 1),
        ["CloudLightEnergy"] = new(0, 2, .05),
        ["CloudAmbientStrength"] = new(0, 2, .05),
        ["CloudAmbientDay"] = new(0, 1, .01),
        ["CloudAmbientNight"] = new(0, 1, .01),
        ["CloudSharpness"] = new(0, 1, .01),
        ["CloudViewSteps"] = new(64, 256, 8),
        ["CloudLightingSteps"] = new(1, 8, 1),
        ["CloudMinStepMeters"] = new(10, 500, 10, "m"),
        ["CloudMaxStepMeters"] = new(100, 4000, 10, "m"),
        ["CloudLightingTravelKm"] = new(.5, 20, .5, "km"),
        ["CloudLodBias"] = new(.1, 2, .05),
        ["CloudBlurPower"] = new(0, 6, .05),
        ["CloudBlurQuality"] = new(1, 4, 1),
        ["CloudRayStartJitterStrength"] = new(0, 1, .01),
        ["CloudStationaryHistoryWeight"] = new(0, .95, .01),
        ["CloudMovingHistoryWeight"] = new(0, .95, .01),
        ["ShadowVerticalSamples"] = new(1, 16, 1),
        ["ShadowStrength"] = new(0, 1, .05),
        ["ShadowExtinctionPerKm"] = new(0, 10, .01, "/km"),
        ["ShadowRadiusKm"] = new(8, 200, 1, "km"),
        ["ShadowUpdatesPerSecond"] = new(.25, 20, .25, "Hz")
    };

    private readonly Dictionary<string, Control> _controls = new();
    private RingMenuSpaceBackdrop? _backdrop;
    private PanelContainer? _drawer;
    private Label? _status, _diagnostics;
    private TabContainer? _tabs;
    private ConfirmationDialog? _resetConfirmation;
    private MenuFlyoverSettings? _settings;
    private bool _dirty, _refreshing;
    private double _saveDelay, _retryDelay, _diagnosticDelay;
    private string _lastError = "", _savePath = "";
    private int _saveAttempts, _saveSuccesses;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        _backdrop = GetNodeOrNull<RingMenuSpaceBackdrop>(BackdropPath);
        _settings = _backdrop?.GetMenuTuningSettings() ?? new MenuFlyoverSettings();
        _savePath = _backdrop?.GetMenuSettingsPath() ?? "";
        BuildUi();
        Resized += UpdateLayout;
        UpdateLayout();
        RefreshControls();
        SetDrawerOpen(false);
    }

    public override void _Process(double delta)
    {
        if (_dirty)
        {
            _saveDelay -= delta;
            _retryDelay -= delta;
            if (_saveDelay <= 0 && _retryDelay <= 0) FlushPendingSave();
        }
        if (_drawer?.Visible != true) return;
        _diagnosticDelay -= delta;
        if (_diagnosticDelay > 0 || _backdrop is null || _diagnostics is null) return;
        _diagnosticDelay = .25;
        var stats = _backdrop.GetBackdropDiagnostics();
        long totalSamples = Math.Max(1, stats["terrain_cache_rows"].AsInt64() * stats["terrain_cache_columns"].AsInt64());
        string terrain = stats["terrain_rebuild_pending"].AsBool() ? "Terrain rebuild queued" :
            stats["ready"].AsBool() ? "Terrain ready" :
            $"Preparing detail: {100.0 * stats["terrain_sample_count"].AsInt64() / totalSamples:0}%";
        _diagnostics.Text = $"{terrain}  |  UI {Engine.GetFramesPerSecond()} FPS\n60 Hz backdrop / {stats["terrain_spacing_meters"].AsInt32()} m terrain / 4x MSAA";
    }

    public override void _Input(InputEvent input)
    {
        if (_resetConfirmation?.Visible == true) return;
        if (TuningMenuUi.HandleNumericWheel(_drawer, input)) return;
        if (input is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode == Key.Quoteleft || key.PhysicalKeycode == Key.Quoteleft)
        {
            SetDrawerOpen(!(_drawer?.Visible ?? false));
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.Escape && _drawer?.Visible == true)
        {
            SetDrawerOpen(false);
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _ExitTree()
    {
        FlushPendingSave();
        _settings?.Dispose();
        _settings = null;
    }

    public void SetDrawerOpen(bool open)
    {
        if (_drawer is not null) _drawer.Visible = open;
        _diagnosticDelay = 0;
        if (!open) FlushPendingSave();
    }

    public void SetTuningValue(string property, Variant value)
    {
        if (_refreshing || _settings is null || !_controls.TryGetValue(property, out Control? control)) return;
        if (control is CheckBox)
        {
            if (value.VariantType != Variant.Type.Bool) return;
        }
        else if (value.VariantType is not (Variant.Type.Int or Variant.Type.Float) || !double.IsFinite(value.AsDouble()))
        {
            return;
        }
        Variant previous = _settings.Get(property);
        _settings.Set(property, value);
        _settings.Sanitize();
        if (previous.Equals(_settings.Get(property)))
        {
            RefreshControls();
            return;
        }
        ApplyAndQueue();
    }

    public bool FlushPendingSave()
    {
        if (!_dirty || _settings is null) return true;
        string path = ResolveSavePath();
        string temporaryAbsolutePath = "";
        _saveAttempts++;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Expected a writable .tres preset path.");
            string absolutePath = ProjectSettings.GlobalizePath(path);
            if (!Directory.Exists(Path.GetDirectoryName(absolutePath)))
                throw new DirectoryNotFoundException("Preset folder does not exist.");
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp.tres";
            temporaryAbsolutePath = ProjectSettings.GlobalizePath(temporaryPath);
            using MenuFlyoverSettings snapshot = _settings.CopyValidated();
            Error result = ResourceSaver.Save(snapshot, temporaryPath);
            if (result != Error.Ok) throw new IOException(result.ToString());
            // Save alongside the target, then replace it on the same filesystem.
            // A failed serialization cannot truncate the last good preset.
            File.Move(temporaryAbsolutePath, absolutePath, true);
            temporaryAbsolutePath = "";
            _dirty = false;
            _lastError = "";
            _saveSuccesses++;
            UpdateSaveStatus();
            return true;
        }
        catch (Exception error)
        {
            _lastError = "UNSAVED - " + error.Message;
            _retryDelay = 5.0;
            UpdateSaveStatus();
            return false;
        }
        finally
        {
            // This path names only the unique temporary file created above.
            if (!string.IsNullOrEmpty(temporaryAbsolutePath))
            {
                try { File.Delete(temporaryAbsolutePath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public Godot.Collections.Dictionary GetDrawerDiagnostics()
    {
        var values = new Godot.Collections.Dictionary();
        foreach (var pair in _controls)
        {
            values[pair.Key] = pair.Value switch
            {
                CheckBox check => check.ButtonPressed,
                SpinBox spin => spin.Value,
                OptionButton option => option.GetSelectedId(),
                _ => default(Variant)
            };
        }
        return new Godot.Collections.Dictionary
        {
            ["open"] = _drawer?.Visible ?? false, ["unsaved"] = _dirty,
            ["save_path"] = ResolveSavePath(), ["last_error"] = _lastError,
            ["status_text"] = _status?.Text ?? "",
            ["control_count"] = _controls.Count, ["values"] = values,
            ["save_attempts"] = _saveAttempts, ["save_successes"] = _saveSuccesses
        };
    }

    private void BuildUi()
    {
        _drawer = new PanelContainer
        {
            Name = "FlyoverTuningPanel", Theme = RingUiStyle.CreateTheme(),
            AnchorLeft = 1, AnchorRight = 1, AnchorBottom = 1,
            OffsetRight = -12, OffsetTop = 64, OffsetBottom = -18,
            MouseFilter = MouseFilterEnum.Stop
        };
        StyleBoxFlat panelStyle = RingUiStyle.GlassPanel(.97f, 8);
        panelStyle.ContentMarginLeft = panelStyle.ContentMarginRight = 14;
        panelStyle.ContentMarginTop = panelStyle.ContentMarginBottom = 14;
        _drawer.AddThemeStyleboxOverride("panel", panelStyle);
        AddChild(_drawer);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 12);
        _drawer.AddChild(column);
        var header = new HBoxContainer();
        column.AddChild(header);
        header.AddChild(new Label { Text = "MENU LIVE TUNING", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var close = new Button { Text = "` / Esc", TooltipText = "Close and save pending changes" };
        close.Pressed += () => SetDrawerOpen(false);
        header.AddChild(close);
        column.AddChild(new Label
        {
            Text = "Start screen only. Changes apply immediately and autosave after 0.5 seconds.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        });
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.AddThemeFontSizeOverride("font_size", 13);
        column.AddChild(_status);
        _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _tabs.AddThemeFontSizeOverride("font_size", 13);
        _tabs.GetTabBar().AddThemeFontSizeOverride("font_size", 13);
        column.AddChild(_tabs);
        AddTab("Camera", new[] { "AltitudeKm", "PitchDegrees", "SpeedKmPerSecond", "FieldOfView", "Paused" });
        AddTab("Lighting", new[] { "SunEnergy", "SunElevationDegrees", "SunAzimuthDegrees", "AmbientEnergy", "HazeAmount", "HazeStartKm", "HazeEndKm", "GlowEnabled", "GlowIntensity" });
        AddTab("Clouds", new[] { "CloudsEnabled", "CloudCoverage", "CloudDensity", "CloudFloorKm", "CloudCeilingKm", "CloudDriftMps", "CloudDitherSpeed", "CloudLightEnergy", "CloudAmbientStrength", "CloudAmbientDay", "CloudAmbientNight", "CloudSharpness" });
        AddTab("Shadows", new[] { "CloudShadowsEnabled", "ShadowGenerationEnabled", "ShadowReceiversEnabled", "ShadowStrength", "ShadowExtinctionPerKm", "ShadowRadiusKm", "ShadowResolution", "ShadowUpdatesPerSecond", "ShadowVerticalSamples" });
        AddTab("Quality", new[] { "CloudResolution", "CloudViewSteps", "CloudLightingSteps", "CloudMinStepMeters", "CloudMaxStepMeters", "CloudLightingTravelKm", "CloudLodBias", "CloudBlurPower", "CloudBlurQuality", "CloudFastReconstruction", "CloudRayStartJitterStrength", "CloudStationaryHistoryWeight", "CloudMovingHistoryWeight" });
        AddTab("Terrain", new[] { "TerrainSpacingMeters", "FarTerrainDetail" });
        _diagnostics = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _diagnostics.AddThemeFontSizeOverride("font_size", 13);
        column.AddChild(_diagnostics);
        var actions = new HBoxContainer();
        column.AddChild(actions);
        var save = new Button { Text = "Save now", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        save.Pressed += () => FlushPendingSave();
        actions.AddChild(save);
        var reset = new Button { Text = "Reset factory defaults...", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        reset.Pressed += () => _resetConfirmation?.PopupCentered();
        actions.AddChild(reset);
        _resetConfirmation = new ConfirmationDialog
        {
            Name = "ResetConfirmation", Title = "Reset menu flyover tuning?",
            DialogText = "Restore the built-in factory values?\nThis replaces your saved menu defaults."
        };
        _resetConfirmation.Confirmed += ResetDefaults;
        AddChild(_resetConfirmation);
        TuningMenuUi.ConfigureNumericScrolling(_drawer);
    }

    private void UpdateLayout()
    {
        if (_drawer is null) return;
        _drawer.OffsetLeft = -Mathf.Min(460f, Mathf.Max(1f, Size.X - 12f));
    }

    private void AddTab(string title, string[] properties)
    {
        var scroll = new ScrollContainer { Name = title, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _tabs!.AddChild(scroll);
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(column);
        foreach ((string sectionTitle, string[] sectionProperties) in GetSections(title, properties))
        {
            VBoxContainer section = TuningMenuUi.CreateSection(column, sectionTitle);
            foreach (string property in sectionProperties) AddProperty(section, property);
        }
        if (title == "Terrain")
            column.AddChild(new Label
            {
                Text = "Changes rebuild this preview after a short delay. The seed and route stay the same; motion pauses while detail is prepared.\n\n125 m = finest (current default)\n250 m = about 4x fewer fine samples\n500 m = about 16x fewer fine samples\n\nGameplay terrain is unchanged.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            });
        else if (title == "Shadows")
            column.AddChild(new Label
            {
                Text = "Main-menu shadows use one shared GPU map. There is no CPU fallback or local-versus-far terrain-streaming handoff.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            });
        else if (title == "Clouds")
            column.AddChild(new Label
            {
                Text = "Sunshine settings for this flyover only. Gameplay storm simulation, storm sculpting, and near/far cloud handoff are not active here.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            });
    }

    private static IEnumerable<(string Title, string[] Properties)> GetSections(string tab, string[] properties)
    {
        return tab switch
        {
            "Camera" => new[]
            {
                ("View", new[] { "AltitudeKm", "PitchDegrees", "FieldOfView" }),
                ("Motion", new[] { "SpeedKmPerSecond", "Paused" })
            },
            "Lighting" => new[]
            {
                ("Sun", new[] { "SunEnergy", "SunElevationDegrees", "SunAzimuthDegrees" }),
                ("Ambient", new[] { "AmbientEnergy" }),
                ("Fog", new[] { "HazeAmount", "HazeStartKm", "HazeEndKm" }),
                ("Glow", new[] { "GlowEnabled", "GlowIntensity" })
            },
            "Clouds" => new[]
            {
                ("Density", new[] { "CloudsEnabled", "CloudCoverage", "CloudDensity", "CloudSharpness" }),
                ("Altitude", new[] { "CloudFloorKm", "CloudCeilingKm" }),
                ("Motion", new[] { "CloudDriftMps", "CloudDitherSpeed" }),
                ("Lighting", new[] { "CloudLightEnergy", "CloudAmbientStrength", "CloudAmbientDay", "CloudAmbientNight" })
            },
            "Shadows" => new[]
            {
                ("State", new[] { "CloudShadowsEnabled", "ShadowGenerationEnabled", "ShadowReceiversEnabled" }),
                ("Appearance", new[] { "ShadowStrength", "ShadowExtinctionPerKm", "ShadowRadiusKm" }),
                ("Map Updates", new[] { "ShadowResolution", "ShadowUpdatesPerSecond", "ShadowVerticalSamples" })
            },
            "Quality" => new[]
            {
                ("Raymarch", new[] { "CloudResolution", "CloudViewSteps", "CloudLightingSteps", "CloudMinStepMeters", "CloudMaxStepMeters", "CloudLightingTravelKm", "CloudLodBias" }),
                ("Reconstruction", new[] { "CloudBlurPower", "CloudBlurQuality", "CloudFastReconstruction", "CloudRayStartJitterStrength" }),
                ("History", new[] { "CloudStationaryHistoryWeight", "CloudMovingHistoryWeight" })
            },
            "Terrain" => new[] { ("Detail", properties) },
            _ => new[] { ("Settings", properties) }
        };
    }

    private void AddProperty(Container parent, string property)
    {
        var row = new HBoxContainer();
        parent.AddChild(row);
        string label = property switch
        {
            "CloudDitherSpeed" => "Blue-noise motion",
            "CloudRayStartJitterStrength" => "Ray-start jitter",
            "CloudStationaryHistoryWeight" => "Stationary history",
            "CloudMovingHistoryWeight" => "Moving history",
            "CloudAmbientDay" => "Cloud ambient day",
            "CloudAmbientNight" => "Cloud ambient night",
            "ShadowGenerationEnabled" => "Generate cloud shadows",
            "ShadowReceiversEnabled" => "Receive cloud shadows",
            "ShadowExtinctionPerKm" => "Shadow extinction / km",
            "ShadowRadiusKm" => "Shadow radius",
            _ => System.Text.RegularExpressions.Regex.Replace(property, "([a-z])([A-Z])", "$1 $2")
        };
        row.AddChild(new Label { Text = label, TooltipText = label, ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        Control control;
        if (property is "CloudResolution" or "ShadowResolution" or "TerrainSpacingMeters" or "FarTerrainDetail")
        {
            var options = new OptionButton();
            if (property == "CloudResolution")
            {
                options.AddItem("Native", 0);
                options.AddItem("Half", 1);
                options.AddItem("Quarter", 2);
            }
            else if (property == "ShadowResolution")
            {
                options.AddItem("128", 128);
                options.AddItem("256", 256);
                options.AddItem("512", 512);
                options.AddItem("1024", 1024);
                options.AddItem("2048", 2048);
            }
            else if (property == "TerrainSpacingMeters")
            {
                options.AddItem("125 m (Max)", 125);
                options.AddItem("250 m", 250);
                options.AddItem("500 m", 500);
            }
            else
            {
                options.AddItem("Low", 0);
                options.AddItem("Medium", 1);
                options.AddItem("High", 2);
            }
            options.ItemSelected += selected => SetTuningValue(property, options.GetItemId((int)selected));
            control = options;
        }
        else if (_settings!.Get(property).VariantType == Variant.Type.Bool)
        {
            var check = new CheckBox();
            check.Toggled += value => SetTuningValue(property, value);
            control = check;
        }
        else
        {
            NumericSpec spec = Ranges[property];
            var spin = new SpinBox { MinValue = spec.Min, MaxValue = spec.Max, Step = spec.Step, Suffix = spec.Suffix };
            spin.ValueChanged += value => SetTuningValue(property, value);
            control = spin;
        }
        control.Name = property;
        control.TooltipText = property switch
        {
            "CloudMinStepMeters" => "Smallest ray-march step. Larger steps reduce fine cloud detail. Units: meters.",
            "CloudMaxStepMeters" => "Largest step and overall march reach. Larger steps cover more distance but can miss thin clouds.",
            "CloudLightingTravelKm" => "Distance sampled inside clouds for sunlight. Shorter distances can lighten deep clouds.",
            "CloudLodBias" => "Distance over which fine density detail is retained. Lower values fade detail sooner; not a texture-resolution setting.",
            "CloudBlurPower" => "Reconstruction blur radius. Zero disables blur; strong blur costs more and softens cloud edges.",
            "CloudBlurQuality" => "Radial blur samples. Used for larger blur radii, or when fast reconstruction is disabled.",
            "CloudFastReconstruction" => "Use a four-tap reconstruction for tiny blur at reduced resolution. Disable for comparison with the original radial filter.",
            "CloudDitherSpeed" => "Extra blue-noise motion with the flyover animation (noise slices per second). Zero disables extra motion; temporal sampling still advances while paused so history can remove grain.",
            "CloudAmbientDay" => "Daytime cloud ambient brightness, multiplied by Cloud Ambient Strength. Uses the menu sun elevation; the existing cloud tint is preserved.",
            "CloudAmbientNight" => "Nighttime cloud ambient brightness, multiplied by Cloud Ambient Strength. Lower the sun elevation in Lighting to preview it; no gameplay day/night cycle is running.",
            "CloudRayStartJitterStrength" => "Ray Start Jitter Strength. Offsets cloud ray starts to reduce visible marching bands.",
            "CloudStationaryHistoryWeight" => "Previous-frame blend while the camera is still. Higher values smooth ray-start grain over several rendered frames, including while paused. Requires nonzero ray-start jitter to accumulate different samples.",
            "CloudMovingHistoryWeight" => "Previous-frame blend during the flyover, aligned to ring scrolling and cloud drift. Higher values smooth grain but may soften moving edges. Cannot exceed Stationary History Weight.",
            "ShadowGenerationEnabled" => "Generate the shared GPU cloud-shadow map. Disable to freeze the last valid map and diagnose generation cost. Geometry/density edits clear stale shadows until generation resumes.",
            "ShadowReceiversEnabled" => "Apply cloud shadows to menu terrain receivers. Disable to diagnose receiver shading.",
            "ShadowExtinctionPerKm" => "Shadow Extinction Per Km. Cloud density attenuation used while generating the shadow map.",
            "ShadowRadiusKm" => "Shadow Radius Km. Half-span covered by the shared cloud-shadow map.",
            "ShadowVerticalSamples" => "Density samples through the cloud layer per shadow texel. Fewer are cheaper but can miss thin layers.",
            "TerrainSpacingMeters" => "Spacing of the fine menu heightfield. Smaller numbers mean more samples and triangles.",
            "FarTerrainDetail" => "Distant full-ring mesh and atlas: Low 512x17, Medium 1024x33, High 2048x65.",
            _ => label
        };
        // Leave room for negative angles plus units; clipping their sign makes
        // a valid negative azimuth misleading even though the model is correct.
        control.CustomMinimumSize = new Vector2(165, 32);
        row.AddChild(control);
        _controls[property] = control;
    }

    private void RefreshControls()
    {
        if (_settings is null) return;
        _refreshing = true;
        _settings.Sanitize();
        foreach (var pair in _controls)
        {
            Variant value = _settings.Get(pair.Key);
            if (pair.Value is CheckBox check) check.SetPressedNoSignal(value.AsBool());
            else if (pair.Value is SpinBox spin) spin.SetValueNoSignal(value.AsDouble());
            else if (pair.Value is OptionButton option) option.Select(option.GetItemIndex(value.AsInt32()));
        }
        _refreshing = false;
        UpdateSaveStatus();
    }

    private void ApplyAndQueue()
    {
        if (_settings is null) return;
        _settings.Sanitize();
        _backdrop?.ApplyMenuTuningSettings(_settings);
        _dirty = true;
        _saveDelay = .5;
        RefreshControls();
    }

    private void ResetDefaults()
    {
        _settings?.Dispose();
        _settings = new MenuFlyoverSettings();
        ApplyAndQueue();
    }

    private string ResolveSavePath() => string.IsNullOrEmpty(SavePathOverride) ? _savePath : SavePathOverride;

    private void UpdateSaveStatus()
    {
        if (_status is null) return;
        string path = ResolveSavePath();
        string scope = path.StartsWith("user://", StringComparison.Ordinal) ? "Local override (exported build)" : "Project-wide menu defaults";
        string status = !string.IsNullOrEmpty(_lastError) ? _lastError : _dirty ? "Saving..." : _saveSuccesses > 0 ? "Saved" : "Autosave ready";
        _status.Text = $"{scope}\n{path}\n{status}";
        _status.Modulate = string.IsNullOrEmpty(_lastError) ? Colors.White : new Color(1f, .65f, .45f);
    }
}
