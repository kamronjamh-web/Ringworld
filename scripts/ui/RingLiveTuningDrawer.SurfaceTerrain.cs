using System;
using Godot;
using RingworldTechDemo.Voxel.Save;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.UI;

/// <summary>Live controls for the isolated Certified Terrain3D surface prototype.</summary>
public partial class RingLiveTuningDrawer
{
    private GameplaySurfaceTerrainDefaults _surfaceTerrainDefaults = null!;
    private Label? _surfaceTerrainStatus;
    private double _surfaceTerrainSaveDelay;
    private double _surfaceTerrainSaveRetryDelay;
    private string? _surfaceTerrainDefaultsPathOverride;
    private readonly System.Collections.Generic.List<(HSlider Slider, Label Value)> _surfaceTerrainControls = new();
    private bool _surfacePrototypeOpenPending;
    private double _surfacePrototypeOpenRetryDelay;

    private void InitializeSurfaceTerrainTuning()
    {
        _surfaceTerrainDefaults = new GameplaySurfaceTerrainDefaults(_surfaceTerrainDefaultsPathOverride);
        _surfaceTerrainDefaults.Initialize();
        ApplySurfaceTerrainSettings();
    }

    private void ProcessSurfaceTerrainTuning(double delta)
    {
        if (!_surfaceTerrainDefaults.Dirty) return;
        _surfaceTerrainSaveDelay -= delta;
        _surfaceTerrainSaveRetryDelay -= delta;
        if (_surfaceTerrainSaveDelay <= 0 && _surfaceTerrainSaveRetryDelay <= 0 && !FlushSurfaceTerrainDefaults())
            _surfaceTerrainSaveRetryDelay = 5.0;
    }

    private void ProcessSurfaceTerrainPrototypeLaunch(double delta)
    {
        if (!_surfacePrototypeOpenPending) return;
        _surfacePrototypeOpenRetryDelay -= delta;
        if (_surfacePrototypeOpenRetryDelay <= 0) TryOpenSurfaceTerrainPrototype();
    }

    private void BuildSurfaceTerrainTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Terrain3D");
        tab.AddChild(new Label { Text = "CERTIFIED SURFACE PROTOTYPE", ThemeTypeVariation = "HeaderSmall" });
        tab.AddChild(new Label { Text = "Edit the isolated surface and adjust brush, mesh, and camera controls. Surface edits save separately from the main world.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        VBoxContainer controls = TuningMenuUi.CreateSection(tab, "Surface editing and camera");
        AddSurfaceTerrainSlider(controls, "Brush radius (m)", GameplaySurfaceTerrainDefaults.MinimumBrushRadiusMeters, GameplaySurfaceTerrainDefaults.MaximumBrushRadiusMeters, .25, _surfaceTerrainDefaults.Settings.BrushRadiusMeters, value => _surfaceTerrainDefaults.SetBrushRadiusMeters((float)value));
        AddSurfaceTerrainSlider(controls, "Brush step (m)", GameplaySurfaceTerrainDefaults.MinimumBrushStepMeters, GameplaySurfaceTerrainDefaults.MaximumBrushStepMeters, .25, _surfaceTerrainDefaults.Settings.BrushStepMeters, value => _surfaceTerrainDefaults.SetBrushStepMeters((float)value));
        AddSurfaceTerrainSlider(controls, "Mesh detail levels", GameplaySurfaceTerrainDefaults.MinimumMeshLods, GameplaySurfaceTerrainDefaults.MaximumMeshLods, 1, _surfaceTerrainDefaults.Settings.MeshLods, value => _surfaceTerrainDefaults.SetMeshLods(Mathf.RoundToInt(value)));
        AddSurfaceTerrainSlider(controls, "Movement speed (m/s)", GameplaySurfaceTerrainDefaults.MinimumMoveSpeedMetersPerSecond, GameplaySurfaceTerrainDefaults.MaximumMoveSpeedMetersPerSecond, 1, _surfaceTerrainDefaults.Settings.MoveSpeedMetersPerSecond, value => _surfaceTerrainDefaults.SetMoveSpeedMetersPerSecond((float)value));
        var save = new Button { Text = "Save surface edits" };
        save.Pressed += SaveSurfaceTerrainEdits;
        tab.AddChild(save);
        var reset = new Button { Text = "Reset surface controls" };
        reset.Pressed += () => { _surfaceTerrainDefaults.Reset(); ApplySurfaceTerrainSettings(); _surfaceTerrainSaveDelay = 0; FlushSurfaceTerrainDefaults(); RefreshSurfaceTerrainControls(); RefreshSurfaceTerrainStatus(); };
        tab.AddChild(reset);
        var exit = new Button { Text = "Return to start menu" };
        exit.Pressed += ReturnSurfaceTerrainToStartMenu;
        tab.AddChild(exit);
        _surfaceTerrainStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        tab.AddChild(_surfaceTerrainStatus);
        RefreshSurfaceTerrainStatus();
    }

    private void BuildSurfaceTerrainGameplayTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Terrain3D");
        tab.AddChild(new Label { Text = "CERTIFIED SURFACE EDITING", ThemeTypeVariation = "HeaderSmall" });
        tab.AddChild(new Label { Text = "This Certified world uses the Terrain3D surface editor. Caves are unavailable in this surface-only mode.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        VBoxContainer controls = TuningMenuUi.CreateSection(tab, "Surface editing and camera");
        AddSurfaceTerrainSlider(controls, "Brush radius (m)", GameplaySurfaceTerrainDefaults.MinimumBrushRadiusMeters, GameplaySurfaceTerrainDefaults.MaximumBrushRadiusMeters, .25, _surfaceTerrainDefaults.Settings.BrushRadiusMeters, value => _surfaceTerrainDefaults.SetBrushRadiusMeters((float)value));
        AddSurfaceTerrainSlider(controls, "Brush step (m)", GameplaySurfaceTerrainDefaults.MinimumBrushStepMeters, GameplaySurfaceTerrainDefaults.MaximumBrushStepMeters, .25, _surfaceTerrainDefaults.Settings.BrushStepMeters, value => _surfaceTerrainDefaults.SetBrushStepMeters((float)value));
        AddSurfaceTerrainSlider(controls, "Mesh detail levels", GameplaySurfaceTerrainDefaults.MinimumMeshLods, GameplaySurfaceTerrainDefaults.MaximumMeshLods, 1, _surfaceTerrainDefaults.Settings.MeshLods, value => _surfaceTerrainDefaults.SetMeshLods(Mathf.RoundToInt(value)));
        AddSurfaceTerrainSlider(controls, "Movement speed (m/s)", GameplaySurfaceTerrainDefaults.MinimumMoveSpeedMetersPerSecond, GameplaySurfaceTerrainDefaults.MaximumMoveSpeedMetersPerSecond, 1, _surfaceTerrainDefaults.Settings.MoveSpeedMetersPerSecond, value => _surfaceTerrainDefaults.SetMoveSpeedMetersPerSecond((float)value));
        var save = new Button { Text = "Save surface edits" };
        save.Pressed += SaveSurfaceTerrainGameplay;
        tab.AddChild(save);
        var reset = new Button { Text = "Reset surface controls" };
        reset.Pressed += () => { _surfaceTerrainDefaults.Reset(); ApplySurfaceTerrainSettings(); FlushSurfaceTerrainDefaults(); RefreshSurfaceTerrainControls(); RefreshSurfaceTerrainStatus(); };
        tab.AddChild(reset);
        _surfaceTerrainStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        tab.AddChild(_surfaceTerrainStatus);
        RefreshSurfaceTerrainStatus();
    }

    private void BuildSurfaceTerrainPrototypeLaunch(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Terrain3D");
        tab.AddChild(new Label { Text = "CERTIFIED SURFACE PROTOTYPE", ThemeTypeVariation = "HeaderSmall" });
        tab.AddChild(new Label { Text = "Open the isolated surface editing prototype. It uses separate surface-edit saves and leaves the main terrain backend unchanged.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        bool supportedWorld = IsCertifiedSurfacePrototypeWorld();
        var open = new Button { Text = "Open Certified surface prototype", Disabled = !supportedWorld, TooltipText = supportedWorld ? "Save the current game and open the isolated surface editor." : "Available for the Certified ring world." };
        open.Pressed += OpenSurfaceTerrainPrototype;
        tab.AddChild(open);
        if (!supportedWorld) tab.AddChild(new Label { Text = "The surface prototype currently supports the Certified ring world.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        _surfaceTerrainStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        tab.AddChild(_surfaceTerrainStatus);
    }

    private void AddSurfaceTerrainSlider(VBoxContainer parent, string label, double min, double max, double step, double initial, System.Action<double> set)
    {
        parent.AddChild(new Label { Text = label });
        var row = new HBoxContainer();
        var slider = new HSlider { Name = label.Replace(" ", ""), MinValue = min, MaxValue = max, Step = step, Value = initial, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var value = new Label { Text = initial.ToString("0.##"), CustomMinimumSize = new Vector2(58, 0) };
        slider.ValueChanged += current => { value.Text = current.ToString("0.##"); set(current); ApplySurfaceTerrainSettings(); _surfaceTerrainSaveDelay = .5; _surfaceTerrainSaveRetryDelay = 0; RefreshSurfaceTerrainStatus(); };
        row.AddChild(slider); row.AddChild(value); parent.AddChild(row);
        _surfaceTerrainControls.Add((slider, value));
    }

    private Node? SurfaceTerrainTarget => _surfaceTerrainPilot ?? _surfaceTerrainGameplay;
    private void ApplySurfaceTerrainSettings() => SurfaceTerrainTarget?.Call("ApplySurfaceSettings", _surfaceTerrainDefaults.Settings);

    private bool FlushSurfaceTerrainDefaults()
    {
        if (_surfaceTerrainDefaults is null) return true;
        bool saved = _surfaceTerrainDefaults.Save();
        RefreshSurfaceTerrainStatus();
        return saved;
    }

    private void SaveSurfaceTerrainEdits()
    {
        if (_surfaceTerrainPilot == null) return;
        if (!_surfaceTerrainPilot.Call("SaveSurfaceEdits").AsBool())
        {
            if (_surfaceTerrainStatus != null) _surfaceTerrainStatus.Text = "Surface edits could not be saved. Stay in the prototype and try again.";
            return;
        }
        FlushSurfaceTerrainDefaults();
        RefreshSurfaceTerrainStatus("Surface edits saved.");
    }

    private void SaveSurfaceTerrainGameplay()
    {
        if (_surfaceTerrainGameplay == null) return;
        if (!_surfaceTerrainGameplay.Call("SaveSurfaceEdits").AsBool())
        {
            if (_surfaceTerrainStatus != null) _surfaceTerrainStatus.Text = "Surface edits could not be saved. Try again.";
            return;
        }
        if (!FlushSurfaceTerrainDefaults()) return;
        RingWorldSaveManager? saves = GetTree().CurrentScene?.GetNodeOrNull<RingWorldSaveManager>("WorldSaveManager");
        Error result = saves?.SaveForSceneTransition() ?? Error.Unconfigured;
        if (result != Error.Ok && result != Error.Unconfigured)
        {
            if (_surfaceTerrainStatus != null) _surfaceTerrainStatus.Text = "Surface edits saved, but world state could not be saved.";
            return;
        }
        RefreshSurfaceTerrainStatus("Surface edits and controls saved.");
    }

    private void ReturnSurfaceTerrainToStartMenu()
    {
        if (_surfaceTerrainPilot == null) return;
        if (!_surfaceTerrainPilot.Call("SaveSurfaceEdits").AsBool())
        {
            if (_surfaceTerrainStatus != null) _surfaceTerrainStatus.Text = "Surface edits could not be saved. Return cancelled.";
            return;
        }
        if (!FlushSurfaceTerrainDefaults()) return;
        CallDeferred(nameof(ChangeToStartMenu));
    }

    private void OpenSurfaceTerrainPrototype()
    {
        if (!IsCertifiedSurfacePrototypeWorld()) return;
        _surfacePrototypeOpenPending = true;
        _surfacePrototypeOpenRetryDelay = 0;
        TryOpenSurfaceTerrainPrototype();
    }

    private void TryOpenSurfaceTerrainPrototype()
    {
        RingWorldSaveManager? saves = GetTree().CurrentScene?.GetNodeOrNull<RingWorldSaveManager>("WorldSaveManager");
        Error result = saves?.SaveForSceneTransition() ?? Error.Unconfigured;
        if (result == Error.Busy)
        {
            _surfacePrototypeOpenRetryDelay = .25;
            if (_surfaceTerrainStatus != null) _surfaceTerrainStatus.Text = "Waiting for the current game save before opening the surface prototype.";
            return;
        }
        bool noActiveSaveDirectory = string.IsNullOrWhiteSpace(ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString());
        if (result != Error.Ok && (result != Error.Unconfigured || !noActiveSaveDirectory))
        {
            _surfacePrototypeOpenPending = false;
            if (_surfaceTerrainStatus != null) _surfaceTerrainStatus.Text = "The current game could not be saved. Surface prototype opening was cancelled.";
            return;
        }
        _surfacePrototypeOpenPending = false;
        if (_grid?.TerrainSettings != null)
            _pendingSurfaceTerrainSettings = (VoxelTerrainSettings)_grid.TerrainSettings.Duplicate(true);
        RingVoxelExplorerController? explorer = GetTree().CurrentScene?.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        if (explorer != null)
        {
            _pendingSurfaceStartAroundMeters = explorer.CanonicalPosition.AroundMeters;
            _pendingSurfaceStartAcrossMeters = explorer.CanonicalPosition.AcrossMeters;
        }
        CallDeferred(nameof(ChangeToSurfaceTerrainPrototype));
    }

    private bool IsCertifiedSurfacePrototypeWorld() => _grid != null && IsCertifiedSurfacePrototypeWorld(
        _grid.RingRadiusMeters, _grid.RingAcrossWidthMeters, _grid.AroundRingChunkLength);

    /// <summary>Limits the prototype to the supported Certified world scale.</summary>
    public static bool IsCertifiedSurfacePrototypeWorld(double radiusMeters, double widthMeters, long aroundChunkCount) =>
        Math.Abs(radiusMeters - CertifiedRingScale.EffectiveRadiusMeters) <= 1.0
        && Math.Abs(widthMeters - CertifiedRingScale.RingWidthMeters) <= 1.0
        && aroundChunkCount == CertifiedRingScale.AroundChunkCount;

    private void ChangeToSurfaceTerrainPrototype()
    {
        if (GetTree().ChangeSceneToFile("res://scenes/Terrain3DSurfacePilot.tscn") != Error.Ok && _surfaceTerrainStatus != null)
            _surfaceTerrainStatus.Text = "The surface prototype could not be opened.";
    }

    private void ChangeToStartMenu()
    {
        if (GetTree().ChangeSceneToFile("res://scenes/StartMenu.tscn") != Error.Ok && _surfaceTerrainStatus != null)
            _surfaceTerrainStatus.Text = "The start menu could not be opened.";
    }

    private void RefreshSurfaceTerrainStatus(string? success = null)
    {
        if (_surfaceTerrainStatus == null) return;
        if (_surfaceTerrainGameplay != null)
        {
            Godot.Collections.Dictionary diagnostics = _surfaceTerrainGameplay.Call("GetSurfaceDiagnostics").AsGodotDictionary();
            string error = diagnostics.TryGetValue("error", out Variant errorValue) ? errorValue.AsString() : "";
            bool saveBlocked = diagnostics.TryGetValue("save_blocked", out Variant blockedValue) && blockedValue.AsBool();
            bool ready = diagnostics.TryGetValue("ready", out Variant readyValue) && readyValue.AsBool();
            if (saveBlocked || error.Length > 0)
            {
                _surfaceTerrainStatus.Text = "Surface editing issue: " + (error.Length > 0 ? error : "The saved surface is protected and cannot be replaced.");
                return;
            }
            if (!ready)
            {
                _surfaceTerrainStatus.Text = "Surface editing is unavailable.";
                return;
            }
        }
        _surfaceTerrainStatus.Text = success ?? (_surfaceTerrainDefaults is null ? "" : _surfaceTerrainDefaults.Dirty ? "Surface controls pending save." : _surfaceTerrainDefaults.LastError.Length > 0 ? "Surface control save error: " + _surfaceTerrainDefaults.LastError : "Surface controls saved.");
    }

    private void RefreshSurfaceTerrainControls()
    {
        if (_surfaceTerrainDefaults is null || _surfaceTerrainControls.Count != 4) return;
        double[] values = { _surfaceTerrainDefaults.Settings.BrushRadiusMeters, _surfaceTerrainDefaults.Settings.BrushStepMeters, _surfaceTerrainDefaults.Settings.MeshLods, _surfaceTerrainDefaults.Settings.MoveSpeedMetersPerSecond };
        for (int index = 0; index < values.Length; index++)
        {
            _surfaceTerrainControls[index].Slider.SetValueNoSignal(values[index]);
            _surfaceTerrainControls[index].Value.Text = values[index].ToString("0.##");
        }
    }

    /// <summary>Test fixture hook that keeps surface preferences outside the player's user:// defaults.</summary>
    public void InstallSurfaceTerrainDefaults(string isolatedPath)
    {
        if (!isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal)
            && !isolatedPath.StartsWith("user://generated/", StringComparison.Ordinal)) return;
        _surfaceTerrainDefaultsPathOverride = isolatedPath;
        _surfaceTerrainDefaults = new GameplaySurfaceTerrainDefaults(isolatedPath);
        _surfaceTerrainDefaults.Initialize();
        ApplySurfaceTerrainSettings();
        RefreshSurfaceTerrainControls();
        RefreshSurfaceTerrainStatus();
    }
}
