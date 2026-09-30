using System;
using Godot;
using RingworldTechDemo.Terrain.Surface;
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
    private Label? _runtimeSurfaceDefinitionStatus;
    private Label? _generationStreamingDiagnostics;
    private double _surfaceTerrainSaveDelay;
    private double _surfaceTerrainSaveRetryDelay;
    private double _surfaceTerrainDiagnosticsRefreshDelay;
    private string? _surfaceTerrainDefaultsPathOverride;
    private readonly System.Collections.Generic.List<(HSlider Slider, Label Value)> _surfaceTerrainControls = new();
    private CheckButton? _surfaceTerrainSmoothShadingToggle;
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
        if (_runtimeSurfaceDefinitionStatus != null)
        {
            _surfaceTerrainDiagnosticsRefreshDelay -= delta;
            if (_surfaceTerrainDiagnosticsRefreshDelay <= 0)
            {
                _surfaceTerrainDiagnosticsRefreshDelay = .25;
                RefreshSurfaceTerrainControls();
                RefreshSurfaceTerrainStatus();
            }
        }
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
        if (_generationPreview != null)
        {
            VBoxContainer playable = CreateTab(tabs, "Playable");
            playable.AddChild(new Label { Text = "STREAMING TERRAIN PATCH", ThemeTypeVariation = "HeaderSmall" });
            playable.AddChild(new Label { Text = "Terrain streams ahead while you travel. A handoff keeps the current patch active until the next patch is ready.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
            VBoxContainer movement = TuningMenuUi.CreateSection(playable, "Movement and mesh");
            AddSurfaceTerrainSlider(movement, "Mesh detail levels", GameplaySurfaceTerrainDefaults.MinimumMeshLods, GameplaySurfaceTerrainDefaults.MaximumMeshLods, 1, _surfaceTerrainDefaults.Settings.MeshLods, value => _surfaceTerrainDefaults.SetMeshLods(Mathf.RoundToInt(value)));
            AddSurfaceTerrainSlider(movement, "Movement speed (m/s)", GameplaySurfaceTerrainDefaults.MinimumMoveSpeedMetersPerSecond, GameplaySurfaceTerrainDefaults.MaximumMoveSpeedMetersPerSecond, 1, _surfaceTerrainDefaults.Settings.MoveSpeedMetersPerSecond, value => _surfaceTerrainDefaults.SetMoveSpeedMetersPerSecond((float)value));
            AddSurfaceTerrainSmoothShadingToggle(movement);
            VBoxContainer streaming = TuningMenuUi.CreateSection(playable, "Terrain streaming");
            AddSurfaceTerrainStreamingControls(streaming);
            _generationStreamingDiagnostics = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
            streaming.AddChild(_generationStreamingDiagnostics);
            VBoxContainer travel = TuningMenuUi.CreateSection(playable, "Travel to coordinates");
            var around = new SpinBox { MinValue = 0, MaxValue = 31415936, Step = 2, AllowGreater = true, Name = "TravelAroundMeters" };
            var across = new SpinBox { MinValue = 80, MaxValue = 317664, Step = 2, Name = "TravelAcrossMeters" };
            if (_surfaceTerrainPilot != null) { around.Value = _surfaceTerrainPilot.Get("_canonical_around").AsDouble(); across.Value = _surfaceTerrainPilot.Get("_canonical_across").AsDouble(); }
            travel.AddChild(new Label { Text = "Around (m)" }); travel.AddChild(around); travel.AddChild(new Label { Text = "Across (m)" }); travel.AddChild(across);
            var travelButton = new Button { Text = "Travel to coordinates" };
            travelButton.Pressed += () => _surfaceTerrainPilot?.Call("request_teleport", around.Value, across.Value);
            travel.AddChild(travelButton);
            var returnOverview = new Button { Text = "Return to overview" };
            returnOverview.Pressed += () => _surfaceTerrainPilot?.Call("return_to_overview");
            playable.AddChild(returnOverview);
            _surfaceTerrainStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
            playable.AddChild(_surfaceTerrainStatus);
            RefreshSurfaceTerrainStatus();
            return;
        }
        VBoxContainer tab = CreateTab(tabs, "Terrain3D");
        tab.AddChild(new Label { Text = "CERTIFIED SURFACE PROTOTYPE", ThemeTypeVariation = "HeaderSmall" });
        tab.AddChild(new Label { Text = "Edit the isolated surface and adjust brush, mesh, and camera controls. Surface edits save separately from the main world.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        VBoxContainer controls = TuningMenuUi.CreateSection(tab, "Surface editing and camera");
        AddSurfaceTerrainSlider(controls, "Brush radius (m)", GameplaySurfaceTerrainDefaults.MinimumBrushRadiusMeters, GameplaySurfaceTerrainDefaults.MaximumBrushRadiusMeters, .25, _surfaceTerrainDefaults.Settings.BrushRadiusMeters, value => _surfaceTerrainDefaults.SetBrushRadiusMeters((float)value));
        AddSurfaceTerrainSlider(controls, "Brush step (m)", GameplaySurfaceTerrainDefaults.MinimumBrushStepMeters, GameplaySurfaceTerrainDefaults.MaximumBrushStepMeters, .25, _surfaceTerrainDefaults.Settings.BrushStepMeters, value => _surfaceTerrainDefaults.SetBrushStepMeters((float)value));
        AddSurfaceTerrainSlider(controls, "Mesh detail levels", GameplaySurfaceTerrainDefaults.MinimumMeshLods, GameplaySurfaceTerrainDefaults.MaximumMeshLods, 1, _surfaceTerrainDefaults.Settings.MeshLods, value => _surfaceTerrainDefaults.SetMeshLods(Mathf.RoundToInt(value)));
        AddSurfaceTerrainSlider(controls, "Native mesh size", GameplaySurfaceTerrainDefaults.MinimumMeshSize, GameplaySurfaceTerrainDefaults.MaximumMeshSize, 8, _surfaceTerrainDefaults.Settings.MeshSize, value => _surfaceTerrainDefaults.SetMeshSize(Mathf.RoundToInt(value)));
        AddSurfaceTerrainSlider(controls, "Terrain3D handoff radius (m)", GameplaySurfaceTerrainDefaults.MinimumHandoffDistanceMeters, GameplaySurfaceTerrainDefaults.MaximumHandoffDistanceMeters, 1, _surfaceTerrainDefaults.Settings.HandoffDistanceMeters, value => _surfaceTerrainDefaults.SetHandoffDistanceMeters((float)value));
        AddSurfaceTerrainSlider(controls, "Movement speed (m/s)", GameplaySurfaceTerrainDefaults.MinimumMoveSpeedMetersPerSecond, GameplaySurfaceTerrainDefaults.MaximumMoveSpeedMetersPerSecond, 1, _surfaceTerrainDefaults.Settings.MoveSpeedMetersPerSecond, value => _surfaceTerrainDefaults.SetMoveSpeedMetersPerSecond((float)value));
        AddSurfaceTerrainSmoothShadingToggle(controls);
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
		_runtimeSurfaceDefinitionStatus = new Label { Name = "RuntimeSurfaceDefinition", Text = "Loading runtime surface definition…", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
		tab.AddChild(_runtimeSurfaceDefinitionStatus);
        VBoxContainer controls = TuningMenuUi.CreateSection(tab, "Surface editing and camera");
        AddSurfaceTerrainSlider(controls, "Brush radius (m)", GameplaySurfaceTerrainDefaults.MinimumBrushRadiusMeters, GameplaySurfaceTerrainDefaults.MaximumBrushRadiusMeters, .25, _surfaceTerrainDefaults.Settings.BrushRadiusMeters, value => _surfaceTerrainDefaults.SetBrushRadiusMeters((float)value));
        AddSurfaceTerrainSlider(controls, "Brush step (m)", GameplaySurfaceTerrainDefaults.MinimumBrushStepMeters, GameplaySurfaceTerrainDefaults.MaximumBrushStepMeters, .25, _surfaceTerrainDefaults.Settings.BrushStepMeters, value => _surfaceTerrainDefaults.SetBrushStepMeters((float)value));
        AddSurfaceTerrainSlider(controls, "Mesh detail levels", GameplaySurfaceTerrainDefaults.MinimumMeshLods, GameplaySurfaceTerrainDefaults.MaximumMeshLods, 1, _surfaceTerrainDefaults.Settings.MeshLods, value => _surfaceTerrainDefaults.SetMeshLods(Mathf.RoundToInt(value)));
        AddSurfaceTerrainSlider(controls, "Movement speed (m/s)", GameplaySurfaceTerrainDefaults.MinimumMoveSpeedMetersPerSecond, GameplaySurfaceTerrainDefaults.MaximumMoveSpeedMetersPerSecond, 1, _surfaceTerrainDefaults.Settings.MoveSpeedMetersPerSecond, value => _surfaceTerrainDefaults.SetMoveSpeedMetersPerSecond((float)value));
        AddSurfaceTerrainAppearanceControls(controls);
        VBoxContainer streaming = TuningMenuUi.CreateSection(tab, "Terrain streaming");
        AddSurfaceTerrainStreamingControls(streaming);
        _generationStreamingDiagnostics = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        streaming.AddChild(_generationStreamingDiagnostics);
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

    private void AddSurfaceTerrainStreamingControls(VBoxContainer parent)
    {
        AddSurfaceTerrainSlider(parent, "Generation workers", GameplaySurfaceTerrainDefaults.MinimumGenerationWorkerCount, GameplaySurfaceTerrainDefaults.MaximumGenerationWorkerCount, 1, _surfaceTerrainDefaults.Settings.GenerationWorkerCount, value => _surfaceTerrainDefaults.SetGenerationWorkerCount(Mathf.RoundToInt(value)));
        AddSurfaceTerrainSlider(parent, "Prefetch distance (m)", GameplaySurfaceTerrainDefaults.MinimumGenerationPrefetchMeters, GameplaySurfaceTerrainDefaults.MaximumGenerationPrefetchMeters, 1, _surfaceTerrainDefaults.Settings.GenerationPrefetchMeters, value => _surfaceTerrainDefaults.SetGenerationPrefetchMeters((float)value));
        AddSurfaceTerrainSlider(parent, "Generation cache (MiB)", GameplaySurfaceTerrainDefaults.MinimumGenerationCacheMiB, GameplaySurfaceTerrainDefaults.MaximumGenerationCacheMiB, 1, _surfaceTerrainDefaults.Settings.GenerationCacheMiB, value => _surfaceTerrainDefaults.SetGenerationCacheMiB(Mathf.RoundToInt(value)));
        AddSurfaceTerrainSlider(parent, "Upload budget (ms)", GameplaySurfaceTerrainDefaults.MinimumGenerationUploadBudgetMs, GameplaySurfaceTerrainDefaults.MaximumGenerationUploadBudgetMs, .1, _surfaceTerrainDefaults.Settings.GenerationUploadBudgetMs, value => _surfaceTerrainDefaults.SetGenerationUploadBudgetMs((float)value));
        AddSurfaceTerrainSlider(parent, "Collision budget (ms)", GameplaySurfaceTerrainDefaults.MinimumGenerationCollisionBudgetMs, GameplaySurfaceTerrainDefaults.MaximumGenerationCollisionBudgetMs, .05, _surfaceTerrainDefaults.Settings.GenerationCollisionBudgetMs, value => _surfaceTerrainDefaults.SetGenerationCollisionBudgetMs((float)value));
        AddSurfaceTerrainSlider(parent, "Collider tile cells", 16, 32, 16, _surfaceTerrainDefaults.Settings.GenerationColliderTileCells, value => _surfaceTerrainDefaults.SetGenerationColliderTileCells(Mathf.RoundToInt(value)));
    }

    private void AddSurfaceTerrainAppearanceControls(VBoxContainer parent)
    {
        AddSurfaceTerrainSmoothShadingToggle(parent);
        AddSurfaceTerrainSlider(parent, "Dry roughness", GameplaySurfaceTerrainDefaults.MinimumDryRoughness, GameplaySurfaceTerrainDefaults.MaximumDryRoughness, .01, _surfaceTerrainDefaults.Settings.DryRoughness, value => _surfaceTerrainDefaults.SetDryRoughness((float)value));
        AddSurfaceTerrainSlider(parent, "Wet roughness", GameplaySurfaceTerrainDefaults.MinimumWetRoughness, GameplaySurfaceTerrainDefaults.MaximumWetRoughness, .01, _surfaceTerrainDefaults.Settings.WetRoughness, value => _surfaceTerrainDefaults.SetWetRoughness((float)value));
        AddSurfaceTerrainSlider(parent, "Rain wetness strength", GameplaySurfaceTerrainDefaults.MinimumRainWetnessStrength, GameplaySurfaceTerrainDefaults.MaximumRainWetnessStrength, .01, _surfaceTerrainDefaults.Settings.RainWetnessStrength, value => _surfaceTerrainDefaults.SetRainWetnessStrength((float)value));
    }

    private void AddSurfaceTerrainSmoothShadingToggle(VBoxContainer parent)
    {
        _surfaceTerrainSmoothShadingToggle = new CheckButton { Name = "SmoothTerrainShading", Text = "Smooth terrain shading", ButtonPressed = _surfaceTerrainDefaults.Settings.SmoothTerrainShading };
        _surfaceTerrainSmoothShadingToggle.Toggled += value => { _surfaceTerrainDefaults.SetSmoothTerrainShading(value); ApplySurfaceTerrainSettings(); _surfaceTerrainSaveDelay = .5; _surfaceTerrainSaveRetryDelay = 0; RefreshSurfaceTerrainStatus(); };
        parent.AddChild(_surfaceTerrainSmoothShadingToggle);
    }

    private Node? SurfaceTerrainTarget => _surfaceTerrainPilot ?? _surfaceTerrainGameplay;
    private void ApplySurfaceTerrainSettings()
    {
        SurfaceTerrainTarget?.Call("ApplySurfaceSettings", _surfaceTerrainDefaults.Settings);
    }

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
        // The gameplay node is installed after the drawer in runtime-surface worlds.
        // Resolve it lazily so opening the grave menu always exposes the frozen policy.
        _surfaceTerrainGameplay ??= GetTree().CurrentScene?.GetNodeOrNull<RingTerrain3DGameplay>("Terrain3DGameplay");
        if (_surfaceTerrainGameplay != null)
        {
            Godot.Collections.Dictionary diagnostics = _surfaceTerrainGameplay.Call("GetSurfaceDiagnostics").AsGodotDictionary();
            RefreshRuntimeSurfaceDefinition(diagnostics);
            RefreshGameplayStreamingDiagnostics(diagnostics);
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

	private void RefreshRuntimeSurfaceDefinition(Godot.Collections.Dictionary diagnostics)
	{
		if (_runtimeSurfaceDefinitionStatus == null) return;
		if (!diagnostics.TryGetValue("runtime_surface_generation", out Variant runtimeValue) || !runtimeValue.AsBool())
		{
			_runtimeSurfaceDefinitionStatus.Text = "Terrain3D surface definition active.";
			return;
		}
		int generator = diagnostics.TryGetValue("terrain_generator_version", out Variant generatorValue) ? generatorValue.AsInt32() : 0;
		string hash = diagnostics.TryGetValue("terrain_definition_hash", out Variant hashValue) ? hashValue.AsString() : "";
		string recipeId = "";
		if (diagnostics.TryGetValue("surface_generation_recipe_json", out Variant recipeJsonValue))
		{
			Variant parsed = Json.ParseString(recipeJsonValue.AsString());
			if (parsed.VariantType == Variant.Type.Dictionary)
				recipeId = parsed.AsGodotDictionary().TryGetValue("generator_id", out Variant idValue) ? idValue.AsString() : "";
		}
		string recipeStyle = recipeId == "ringworld_surface_detail_v3" ? "detail v3"
			: recipeId == "ringworld_surface_coastal_v2" ? "coastal v2"
			: recipeId == "ringworld_surface_reference_v1" ? "reference v1" : "unknown recipe";
		_runtimeSurfaceDefinitionStatus.Text = $"Runtime surface generator v{generator} | hash {hash} | recipe {recipeStyle}\nTerrain -8000..+8000 m | sea level 0 m | reserved build ceiling +8400 m.\nThis world's recipe is frozen. Save a recipe in the terrain preview before creating a new runtime surface world to change its geography.";
	}

    private void RefreshGameplayStreamingDiagnostics(Godot.Collections.Dictionary diagnostics)
    {
        if (_generationStreamingDiagnostics == null) return;
        Godot.Collections.Dictionary stream = diagnostics.TryGetValue("streaming", out Variant streamingValue)
            ? streamingValue.AsGodotDictionary() : new();
        if (stream.Count == 0)
        {
            _generationStreamingDiagnostics.Text = "Streaming diagnostics are unavailable.";
            return;
        }

        string state = stream.TryGetValue("state", out Variant stateValue) ? stateValue.AsString() : "Unknown";
        long requestRevision = stream.TryGetValue("request_revision", out Variant requestValue) ? requestValue.AsInt64() : 0;
        long committedRevision = stream.TryGetValue("committed_revision", out Variant committedValue) ? committedValue.AsInt64() : 0;
        long pendingEditRevision = stream.TryGetValue("pending_edit_revision", out Variant pendingEditValue) ? pendingEditValue.AsInt64() : 0;
        long committedEditRevision = stream.TryGetValue("committed_edit_revision", out Variant committedEditValue) ? committedEditValue.AsInt64() : 0;
        long workers = stream.TryGetValue("worker_count", out Variant workersValue) ? workersValue.AsInt64() : 0;
        long activeJobs = stream.TryGetValue("active_jobs", out Variant jobsValue) ? jobsValue.AsInt64() : 0;
        long cacheBytes = stream.TryGetValue("cache_bytes", out Variant cacheValue) ? cacheValue.AsInt64() : 0;
        long cacheHits = stream.TryGetValue("cache_hits", out Variant hitsValue) ? hitsValue.AsInt64() : 0;
        long cacheMisses = stream.TryGetValue("cache_misses", out Variant missesValue) ? missesValue.AsInt64() : 0;
        double generationMs = stream.TryGetValue("last_generation_ms", out Variant generationValue) ? generationValue.AsDouble() : 0;
        double readinessMs = stream.TryGetValue("last_readiness_ms", out Variant readinessValue) ? readinessValue.AsDouble() : 0;
        double maxReadinessMs = stream.TryGetValue("max_readiness_ms", out Variant maxReadinessValue) ? maxReadinessValue.AsDouble() : 0;
        long coverageWaits = stream.TryGetValue("coverage_waits", out Variant waitsValue) ? waitsValue.AsInt64() : 0;
        Godot.Collections.Dictionary native = diagnostics.TryGetValue("native", out Variant nativeValue)
            && nativeValue.VariantType == Variant.Type.Dictionary ? nativeValue.AsGodotDictionary() : new();
        Godot.Collections.Dictionary preparation = native.TryGetValue("preparation", out Variant preparationValue)
            ? preparationValue.AsGodotDictionary() : new();
        string phase = preparation.TryGetValue("phase", out Variant phaseValue) ? phaseValue.AsString() : "Idle";
        long regions = native.TryGetValue("regions", out Variant regionsValue) ? regionsValue.AsInt64() : 0;
        long tilesDone = preparation.TryGetValue("tiles_done", out Variant tilesDoneValue) ? tilesDoneValue.AsInt64() : 0;
        long tilesTotal = preparation.TryGetValue("tiles_total", out Variant tilesTotalValue) ? tilesTotalValue.AsInt64() : 0;
        double maxStep = preparation.TryGetValue("step_max_ms", out Variant maxStepValue) ? maxStepValue.AsDouble() : 0;

        _generationStreamingDiagnostics.Text = $"State: {state} | request {requestRevision}, committed {committedRevision} | edits pending {pendingEditRevision}, committed {committedEditRevision}\n"
            + $"Workers {workers} | active jobs {activeJobs} | cache {cacheBytes / 1048576.0:0.0} MiB | hits {cacheHits}, misses {cacheMisses}\n"
            + $"Generation {generationMs:0.##} ms | readiness {readinessMs:0.##}/{maxReadinessMs:0.##} ms | coverage waits {coverageWaits}\n"
            + $"Native preparation: {phase} | regions {regions}/9 | tiles {tilesDone}/{tilesTotal} | max step {maxStep:0.##} ms";
    }

    private void RefreshSurfaceTerrainControls()
    {
        if (_surfaceTerrainDefaults is null || _surfaceTerrainControls.Count == 0) return;
        _surfaceTerrainSmoothShadingToggle?.SetPressedNoSignal(_surfaceTerrainDefaults.Settings.SmoothTerrainShading);
        double[] values = _generationPreview != null
            ? new[] { (double)_surfaceTerrainDefaults.Settings.MeshLods, _surfaceTerrainDefaults.Settings.MoveSpeedMetersPerSecond, _surfaceTerrainDefaults.Settings.GenerationWorkerCount, _surfaceTerrainDefaults.Settings.GenerationPrefetchMeters, _surfaceTerrainDefaults.Settings.GenerationCacheMiB, _surfaceTerrainDefaults.Settings.GenerationUploadBudgetMs, _surfaceTerrainDefaults.Settings.GenerationCollisionBudgetMs, (double)_surfaceTerrainDefaults.Settings.GenerationColliderTileCells }
            : _runtimeSurfaceDefinitionStatus != null
                ? new[] { (double)_surfaceTerrainDefaults.Settings.BrushRadiusMeters, _surfaceTerrainDefaults.Settings.BrushStepMeters, _surfaceTerrainDefaults.Settings.MeshLods, (double)_surfaceTerrainDefaults.Settings.MeshSize, _surfaceTerrainDefaults.Settings.HandoffDistanceMeters, _surfaceTerrainDefaults.Settings.MoveSpeedMetersPerSecond, _surfaceTerrainDefaults.Settings.DryRoughness, _surfaceTerrainDefaults.Settings.WetRoughness, _surfaceTerrainDefaults.Settings.RainWetnessStrength, _surfaceTerrainDefaults.Settings.GenerationWorkerCount, _surfaceTerrainDefaults.Settings.GenerationPrefetchMeters, _surfaceTerrainDefaults.Settings.GenerationCacheMiB, _surfaceTerrainDefaults.Settings.GenerationUploadBudgetMs, _surfaceTerrainDefaults.Settings.GenerationCollisionBudgetMs, (double)_surfaceTerrainDefaults.Settings.GenerationColliderTileCells }
                : new[] { (double)_surfaceTerrainDefaults.Settings.BrushRadiusMeters, _surfaceTerrainDefaults.Settings.BrushStepMeters, _surfaceTerrainDefaults.Settings.MeshLods, _surfaceTerrainDefaults.Settings.MoveSpeedMetersPerSecond };
        if (_surfaceTerrainControls.Count != values.Length) return;
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
