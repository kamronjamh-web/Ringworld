using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>
/// The start menu's small grave-key drawer for the live world behind it. It owns the persisted <see cref="MenuLiveWorldSettings"/>:
/// project defaults in development, a local override in exported builds (the PCK is read-only), like the other grave drawers.
/// </summary>
public partial class MenuLiveWorldTuningDrawer : Control
{
    public const string ProjectPath = "res://resources/menu/menu_live_world_defaults.tres";
    public const string UserPath = "user://menu_live_world_defaults.tres";

    /// <summary>Test hook: redirects load and save. Only user:// and res://.godot/ are accepted. Set before the node enters the tree.</summary>
    [Export] public string SavePathOverride { get; set; } = "";

    [Signal] public delegate void SettingsChangedEventHandler();

    private readonly record struct RowSpec(string Property, string Label, double Min, double Max, double Step, string Suffix, string Tip);
    private static readonly RowSpec[] Rows =
    {
        new("CruiseAltitudeKm", "Cruise altitude", 8, MenuLiveWorldSettings.MaxCruiseAltitudeKm, .5, " km", "Camera height above the ground when the menu world places itself above your save spot. Applies the next time the world loads. Never above 18 km."),
        new("CruisePitchDegrees", "Cruise pitch", -60, 0, 1, " deg", "Camera pitch for that default placement (negative looks down). Applies the next time the world loads."),
        new("CrossfadeSeconds", "Crossfade", .5, 6, .1, " s", "Seconds the last-session picture dissolves into the live world."),
        new("SnapshotDriftPercent", "Snapshot drift", 0, 20, .5, " %", "How far the last-session picture starts zoomed and shifted before it settles. Zero holds it still.")
    };

    private readonly Dictionary<string, SpinBox> _controls = new();
    private MenuLiveWorldSettings _settings = new();
    private PanelContainer? _drawer;
    private Label? _status;
    private bool _dirty, _refreshing;
    private double _saveDelay, _retryDelay;
    private string _lastError = "";
    private int _saveAttempts, _saveSuccesses;

    public bool IsDrawerOpen => _drawer?.Visible == true;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        LoadSettings();
        BuildUi();
        RefreshControls();
        SetDrawerOpen(false);
    }

    public override void _Process(double delta)
    {
        if (!_dirty) return;
        _saveDelay -= delta;
        _retryDelay -= delta;
        if (_saveDelay <= 0 && _retryDelay <= 0) FlushPendingSave();
    }

    public override void _Input(InputEvent @event)
    {
        if (TuningMenuUi.HandleNumericWheel(_drawer, @event)) return;
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode == Key.Quoteleft || key.PhysicalKeycode == Key.Quoteleft)
        {
            SetDrawerOpen(!IsDrawerOpen);
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.Escape && IsDrawerOpen)
        {
            SetDrawerOpen(false);
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _ExitTree()
    {
        FlushPendingSave();
    }

    public void SetDrawerOpen(bool open)
    {
        if (_drawer == null) return;
        _drawer.Visible = open;
        if (open) return;
        // Do not leave keyboard focus on a hidden control; the menu buttons take it back.
        Control? focused = IsInsideTree() ? GetViewport().GuiGetFocusOwner() : null;
        if (focused != null && _drawer.IsAncestorOf(focused)) focused.ReleaseFocus();
        FlushPendingSave();
    }

    /// <summary>Current value of a setting by property name (0 for unknown names).</summary>
    public float GetValue(string property)
    {
        Variant value = _settings.Get(property);
        return value.VariantType is Variant.Type.Float or Variant.Type.Int ? (float)value.AsDouble() : 0f;
    }

    /// <summary>Cruise placement for the warm-world loader: {altitude_m, pitch_deg}.</summary>
    public Godot.Collections.Dictionary GetCruise() => new()
    {
        ["altitude_m"] = _settings.CruiseAltitudeKm * 1000f,
        ["pitch_deg"] = _settings.CruisePitchDegrees
    };

    public void SetTuningValue(string property, double value)
    {
        if (_refreshing || !_controls.ContainsKey(property) || !double.IsFinite(value)) return;
        float previous = GetValue(property);
        _settings.Set(property, (float)value);
        _settings.Sanitize();
        if (Mathf.IsEqualApprox(previous, GetValue(property))) { RefreshControls(); return; }
        ApplyAndQueue();
    }

    public bool FlushPendingSave()
    {
        if (!_dirty) return true;
        string path = ResolveSavePath();
        string temporaryAbsolutePath = "";
        _saveAttempts++;
        try
        {
            string absolutePath = ProjectSettings.GlobalizePath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp.tres";
            temporaryAbsolutePath = ProjectSettings.GlobalizePath(temporaryPath);
            Error result = ResourceSaver.Save(_settings, temporaryPath);
            if (result != Error.Ok) throw new IOException(result.ToString());
            // Save beside the target, then replace it: a failed save cannot truncate the last good file.
            File.Move(temporaryAbsolutePath, absolutePath, true);
            temporaryAbsolutePath = "";
            _dirty = false;
            _lastError = "";
            _saveSuccesses++;
            UpdateStatus();
            return true;
        }
        catch (Exception error)
        {
            _lastError = "UNSAVED - " + error.Message;
            _retryDelay = 5.0;
            UpdateStatus();
            return false;
        }
        finally
        {
            if (temporaryAbsolutePath.Length > 0)
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
        foreach (var pair in _controls) values[pair.Key] = pair.Value.Value;
        return new Godot.Collections.Dictionary
        {
            ["open"] = IsDrawerOpen, ["unsaved"] = _dirty, ["save_path"] = ResolveSavePath(), ["last_error"] = _lastError,
            ["control_count"] = _controls.Count, ["values"] = values,
            ["save_attempts"] = _saveAttempts, ["save_successes"] = _saveSuccesses
        };
    }

    private string ResolveSavePath()
    {
        if (SavePathOverride.Length > 0) return SavePathOverride;
        return OS.HasFeature("template") ? UserPath : ProjectPath;
    }

    private void LoadSettings()
    {
        if (SavePathOverride.Length > 0
            && !SavePathOverride.StartsWith("user://", StringComparison.Ordinal)
            && !SavePathOverride.StartsWith("res://.godot/", StringComparison.Ordinal))
            SavePathOverride = "";
        // Exported PCK resources are read-only; without a local override, start from the project defaults bundled into the build.
        string path = ResolveSavePath();
        if (!ResourceLoader.Exists(path) && SavePathOverride.Length == 0) path = ProjectPath;
        if (!ResourceLoader.Exists(path)) return;
        try
        {
            using var loaded = ResourceLoader.Load<MenuLiveWorldSettings>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { _lastError = "Invalid live world settings: " + path; return; }
            _settings.CopyFrom(loaded);
        }
        catch (Exception error) { _lastError = error.Message; }
    }

    private void BuildUi()
    {
        _drawer = new PanelContainer
        {
            Name = "LiveWorldPanel", Theme = RingUiStyle.CreateTheme(),
            AnchorLeft = 1, AnchorRight = 1,
            OffsetLeft = -380, OffsetRight = -12, OffsetTop = 64,
            MouseFilter = MouseFilterEnum.Stop
        };
        StyleBoxFlat panelStyle = RingUiStyle.GlassPanel(.97f, 8);
        panelStyle.ContentMarginLeft = panelStyle.ContentMarginRight = 14;
        panelStyle.ContentMarginTop = panelStyle.ContentMarginBottom = 14;
        _drawer.AddThemeStyleboxOverride("panel", panelStyle);
        AddChild(_drawer);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        _drawer.AddChild(column);
        var header = new HBoxContainer();
        column.AddChild(header);
        header.AddChild(new Label { Text = "LIVE WORLD", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var close = new Button { Name = "LiveWorldClose", Text = "` / Esc", TooltipText = "Close and save pending changes", FocusMode = FocusModeEnum.None };
        close.Pressed += () => SetDrawerOpen(false);
        header.AddChild(close);
        column.AddChild(new Label
        {
            Text = "The world behind this menu. Changes autosave; cruise values apply the next time it loads.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        });
        foreach (RowSpec row in Rows)
        {
            var line = new HBoxContainer();
            column.AddChild(line);
            line.AddChild(new Label { Text = row.Label, TooltipText = row.Tip, ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill });
            var spin = new SpinBox
            {
                Name = row.Property, MinValue = row.Min, MaxValue = row.Max, Step = row.Step, TooltipText = row.Tip,
                Format = "%s" + row.Suffix.Replace("%", "%%"), CustomMinimumSize = new Vector2(150, 32)
            };
            string property = row.Property;
            spin.ValueChanged += value => SetTuningValue(property, value);
            line.AddChild(spin);
            _controls[property] = spin;
        }
        var actions = new HBoxContainer();
        column.AddChild(actions);
        var save = new Button { Name = "LiveWorldSave", Text = "Save now", FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        save.Pressed += () => FlushPendingSave();
        actions.AddChild(save);
        var reset = new Button { Name = "LiveWorldReset", Text = "Reset defaults", FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        reset.Pressed += ResetDefaults;
        actions.AddChild(reset);
        _status = new Label { Name = "LiveWorldStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        _status.AddThemeFontSizeOverride("font_size", 13);
        column.AddChild(_status);
        TuningMenuUi.ConfigureNumericScrolling(_drawer);
    }

    private void RefreshControls()
    {
        _refreshing = true;
        _settings.Sanitize();
        foreach (var pair in _controls) pair.Value.SetValueNoSignal(GetValue(pair.Key));
        _refreshing = false;
        UpdateStatus();
    }

    private void ApplyAndQueue()
    {
        _dirty = true;
        _saveDelay = .5;
        RefreshControls();
        EmitSignal(SignalName.SettingsChanged);
    }

    private void ResetDefaults()
    {
        var factory = new MenuLiveWorldSettings();
        _settings.CopyFrom(factory);
        ApplyAndQueue();
    }

    private void UpdateStatus()
    {
        if (_status == null) return;
        string path = ResolveSavePath();
        string scope = path.StartsWith("user://", StringComparison.Ordinal) ? "Local override" : "Project-wide defaults";
        string state = _lastError.Length > 0 ? _lastError : _dirty ? "Saving..." : _saveSuccesses > 0 ? "Saved" : "Autosave ready";
        _status.Text = $"{scope}\n{path}\n{state}";
    }
}
