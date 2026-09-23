using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;
using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingLodPauseMenu : CanvasLayer
{
    public enum UpscalingPreset
    {
        Native = 0,
        Fsr2NativeAntiAliasing = 1,
        Fsr2Quality = 2,
        Fsr2Balanced = 3,
        Fsr2Performance = 4,
        Fsr2UltraPerformance = 5
    }

    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath CoordinatorPath { get; set; } = new("../TerrainLodCoordinator");
    [Export] public NodePath GraphicsQualityPath { get; set; } = new("../GraphicsQuality");
    [Export] public NodePath GlobalLodPath { get; set; } = new("../GlobalTerrainLod3");
    [Export] public NodePath AnalyticGlobalLodPath { get; set; } = new("../AnalyticGlobalRing");
    [Export] public NodePath LegacyFarRingPath { get; set; } = new("../RingWorld/FarRing");
    [Export] public NodePath WorldEnvironmentPath { get; set; } = new("../WorldEnvironment");
    [Export] public bool HazeEnabledByDefault { get; set; } = false;
    [Export(PropertyHint.Range, "2,32,1")] public int DefaultChunkRadius { get; set; } = 7;
    [Export(PropertyHint.Range, "2,32,1")] public int DefaultVerticalChunkRadius { get; set; } = 7;
    [Export] public UpscalingPreset DefaultUpscalingPreset { get; set; } = UpscalingPreset.Native;

    private VoxelChunkGridManager? _grid;
    private RingVoxelExplorerController? _explorer;
    private RingTerrainLodCoordinator? _coordinator;
    private RingGraphicsQualityController? _graphicsQuality;
    private RingTerrainGlobalLod? _globalLod;
    private RingAnalyticGlobalLod? _analyticGlobalLod;
    private Node3D? _legacyFarRing;
    private WorldEnvironment? _worldEnvironment;
    private Control? _root;
    private Label? _radiusValue;
    private HSlider? _radiusSlider;
    private Label? _verticalRadiusValue;
    private HSlider? _verticalRadiusSlider;
    private Label? _workerValue;
    private HSlider? _workerSlider;
    private OptionButton? _aspectRatioOptions;
    private OptionButton? _resolutionOptions;
    private OptionButton? _qualityOptions;
    private OptionButton? _upscalingOptions;
    private CheckButton? _adaptiveRenderResolution;
    private Label? _renderResolutionStatus;
    private GameplayRenderResolutionDefaults _renderResolutionDefaults = new();
    private RingCertificationMonitor? _certificationMonitor;
    private double _renderResolutionSaveElapsed;
    private double _renderResolutionRetryElapsed;
    private double _renderResolutionStatusElapsed;
    private bool _renderResolutionReady;
    private bool _fsr2Available;
    private readonly List<Vector2I> _availableResolutions = new();
    private Label? _applyStatus;
    private bool _displayApplyPending;
    private Vector2I _pendingResolution;
    private CheckButton? _debugColors;
    private CheckButton? _hazeToggle;
    private readonly Dictionary<GeometryInstance3D, Material?> _debugOriginalMaterials = new();
    private readonly Dictionary<CanvasLayer, bool> _suppressedHudVisibility = new();
    private StandardMaterial3D? _voxelDebugMaterial;
    private StandardMaterial3D? _legacyDebugMaterial;
    private bool _menuOpen;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _coordinator = GetNodeOrNull<RingTerrainLodCoordinator>(CoordinatorPath);
        _graphicsQuality = GetNodeOrNull<RingGraphicsQualityController>(GraphicsQualityPath);
        _globalLod = GetNodeOrNull<RingTerrainGlobalLod>(GlobalLodPath);
        _analyticGlobalLod = GetNodeOrNull<RingAnalyticGlobalLod>(AnalyticGlobalLodPath);
        _legacyFarRing = GetNodeOrNull<Node3D>(LegacyFarRingPath);
        _worldEnvironment = GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath);
        _certificationMonitor = GetNodeOrNull<RingCertificationMonitor>("../CertificationMonitor");
        _fsr2Available = RenderingServer.GetCurrentRenderingMethod() == "forward_plus";
        _renderResolutionDefaults.Load((int)DefaultUpscalingPreset);
        BuildMenu();
        ApplySavedRenderResolution(updateStatus: false);
        _renderResolutionReady = true;
        SetAtmosphericHaze(HazeEnabledByDefault);
        ApplyLodProfile(DefaultChunkRadius, DefaultVerticalChunkRadius);
        SetMenuOpen(false);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.Escape)
        {
            SetMenuOpen(_root == null || !_root.Visible);
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (_renderResolutionDefaults.Dirty)
        {
            _renderResolutionSaveElapsed += delta;
            _renderResolutionRetryElapsed += delta;
            if (_renderResolutionSaveElapsed >= 0.5 && (_renderResolutionDefaults.LastError.Length == 0 || _renderResolutionRetryElapsed >= 5.0))
                FlushRenderResolutionSettings();
        }
        if (_menuOpen)
        {
            _renderResolutionStatusElapsed += delta;
            if (_renderResolutionStatusElapsed >= 0.25)
            {
                _renderResolutionStatusElapsed = 0.0;
                UpdateRenderResolutionStatus();
            }
        }
        if (_debugColors?.ButtonPressed == true)
        {
            if (_voxelDebugMaterial != null) ApplyMaterialRecursively(_grid, _voxelDebugMaterial);
            if (_legacyDebugMaterial != null) ApplyMaterialRecursively(_legacyFarRing, _legacyDebugMaterial);
        }
        if (!_displayApplyPending) return;
        _displayApplyPending = false;
        ApplyDisplaySettings(_pendingResolution);
    }

    private void BuildMenu()
    {
        _root = new ColorRect
        {
            Name = "PauseMenuRoot",
            Color = new Color(0.003f, 0.008f, 0.014f, 0.88f),
            MouseFilter = Control.MouseFilterEnum.Stop,
            Theme = RingUiStyle.CreateTheme()
        };
        AddChild(_root);
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var panel = new PanelContainer { Name = "PauseMenuPanel" };
        panel.AnchorLeft = 0.5f;
        panel.AnchorRight = 0.5f;
        panel.AnchorTop = 0.0f;
        panel.AnchorBottom = 1.0f;
        panel.OffsetLeft = -360.0f;
        panel.OffsetRight = 360.0f;
        panel.OffsetTop = 24.0f;
        panel.OffsetBottom = -24.0f;
        panel.AddThemeStyleboxOverride("panel", RingUiStyle.GlassPanel(0.98f, 18));
        _root.AddChild(panel);

        var scroll = new ScrollContainer
        {
            Name = "PauseMenuScroll",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        panel.AddChild(scroll);

        var margin = new MarginContainer();
        margin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        margin.AddThemeConstantOverride("margin_left", 28);
        margin.AddThemeConstantOverride("margin_right", 28);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        scroll.AddChild(margin);
        var column = new VBoxContainer
        {
            Name = "PauseMenuColumn",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        column.AddThemeConstantOverride("separation", 14);
        margin.AddChild(column);

        var kicker = new Label { Text = "RINGWORLD / SYSTEM" };
        kicker.AddThemeFontSizeOverride("font_size", 11);
        kicker.AddThemeColorOverride("font_color", RingUiStyle.Accent);
        column.AddChild(kicker);
        var title = new Label { Text = "PAUSED" };
        title.AddThemeFontSizeOverride("font_size", 34);
        title.AddThemeColorOverride("font_color", RingUiStyle.AccentBright);
        column.AddChild(title);
        var subtitle = new Label { Text = "Terrain, atmosphere, diagnostics, and display" };
        subtitle.Modulate = RingUiStyle.Muted;
        column.AddChild(subtitle);
        column.AddChild(new HSeparator());

        var returnToSurface = new Button
        {
            Text = "Return to Nearest Surface",
            TooltipText = "Stop noclip and safely teleport to the terrain directly beneath or above your current ring location.",
            Disabled = _explorer == null
        };
        returnToSurface.Pressed += ReturnToNearestSurface;
        column.AddChild(returnToSurface);
        var recoveryNote = new Label
        {
            Text = "Recovery: stops flight and returns you to the nearest valid terrain column.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        recoveryNote.Modulate = RingUiStyle.Muted;
        column.AddChild(recoveryNote);

        var teleportToCave = new Button
        {
            Text = "Teleport to Nearest Cave",
            TooltipText = "Find a placement-permitted designed cave chamber in a bounded deterministic search and enter flight mode.",
            Disabled = _explorer == null
        };
        teleportToCave.Pressed += TeleportToNearestCave;
        column.AddChild(teleportToCave);
        var caveTeleportNote = new Label
        {
            Text = "Debug: searches nearby cave provinces using this world's structural bounds, then enters noclip while cave LOD streams.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        caveTeleportNote.Modulate = RingUiStyle.Muted;
        column.AddChild(caveTeleportNote);

        var caveCheckpointRow = new HBoxContainer();
        var setCaveCheckpoint = new Button
        {
            Text = "Set Cave Checkpoint",
            TooltipText = "Save the current underground position after clearance, floor, structural-boundary, and terrain-residency checks.",
            Disabled = _explorer == null,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        setCaveCheckpoint.Pressed += SetCaveCheckpoint;
        caveCheckpointRow.AddChild(setCaveCheckpoint);
        var recoverCaveCheckpoint = new Button
        {
            Text = "Recover to Cave Checkpoint",
            TooltipText = "Return to the last validated underground checkpoint after its collision terrain is resident.",
            Disabled = _explorer == null,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        recoverCaveCheckpoint.Pressed += RecoverToCaveCheckpoint;
        caveCheckpointRow.AddChild(recoverCaveCheckpoint);
        column.AddChild(caveCheckpointRow);

        var qualityRow = new HBoxContainer();
        qualityRow.AddChild(new Label { Text = "Graphics quality", CustomMinimumSize = new Vector2(150, 0) });
        _qualityOptions = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _qualityOptions.AddItem("Low");
        _qualityOptions.AddItem("Medium");
        _qualityOptions.AddItem("High");
        _qualityOptions.AddItem("Very High");
        _qualityOptions.AddItem("Ultra / Video");
        _qualityOptions.AddItem("Cinematic / RTX 5090");
        _qualityOptions.Select((int)(_graphicsQuality?.CurrentPreset
            ?? _coordinator?.CurrentPreset
            ?? RingTerrainLodCoordinator.QualityPreset.Medium));
        _qualityOptions.ItemSelected += OnQualitySelected;
        qualityRow.AddChild(_qualityOptions);
        column.AddChild(qualityRow);
        var qualityNote = new Label
        {
            Text = "Ultra / Video matches the recording pass. Cinematic / RTX 5090 enables a 16K shadow atlas, 24 km cascades, 240 km terrain shadows, and 2048px cloud shadows; it deliberately targets 24 GB+ VRAM and can overwhelm an RTX 4070.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        qualityNote.Modulate = RingUiStyle.Muted;
        column.AddChild(qualityNote);

        var upscalingRow = new HBoxContainer();
        upscalingRow.AddChild(new Label { Text = "3D render resolution", CustomMinimumSize = new Vector2(150, 0) });
        _upscalingOptions = new OptionButton
        {
            Name = "RenderResolutionPreset",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = _fsr2Available
                ? "FSR 2.2 renders only 3D at a lower resolution; the UI remains native-resolution."
                : "FSR 2.2 requires Godot's Forward+ renderer."
        };
        for (int preset = 0; preset <= 5; preset++)
        {
            _upscalingOptions.AddItem(GetRenderResolutionPresetLabel(preset));
            if (!_fsr2Available && preset != 0) _upscalingOptions.SetItemDisabled(preset, true);
        }
        _upscalingOptions.Select(_renderResolutionDefaults.Settings.Preset);
        _upscalingOptions.ItemSelected += OnUpscalingSelected;
        upscalingRow.AddChild(_upscalingOptions);
        column.AddChild(upscalingRow);
        var upscalingNote = new Label
        {
            Text = _fsr2Available
                ? "Native resolution is the default. Enable FSR 2.2 here when you need lower 3D render resolution."
                : "FSR 2.2 is unavailable because this session is not using the Forward+ renderer.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        upscalingNote.Modulate = RingUiStyle.Muted;
        column.AddChild(upscalingNote);
        _adaptiveRenderResolution = new CheckButton
        {
            Name = "AdaptiveRenderResolution",
            Text = "Automatic resolution",
            ButtonPressed = _renderResolutionDefaults.Settings.AdaptiveResolutionEnabled,
            TooltipText = "When enabled, reduce 3D resolution under sustained GPU pressure. Your selected resolution remains the maximum."
        };
        _adaptiveRenderResolution.Toggled += SetAdaptiveRenderResolution;
        column.AddChild(_adaptiveRenderResolution);
        _renderResolutionStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _renderResolutionStatus.Modulate = RingUiStyle.Muted;
        column.AddChild(_renderResolutionStatus);
        column.AddChild(new Label { Text = "Surface voxel radius (chunks in each cardinal direction)" });

        var sliderRow = new HBoxContainer();
        _radiusSlider = new HSlider { MinValue = 2, MaxValue = 32, Step = 1, Value = DefaultChunkRadius, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _radiusValue = new Label { Text = $"{DefaultChunkRadius} chunks", CustomMinimumSize = new Vector2(100, 0) };
        _radiusSlider.ValueChanged += value => _radiusValue.Text = $"{Mathf.RoundToInt(value)} chunks";
        sliderRow.AddChild(_radiusSlider);
        sliderRow.AddChild(_radiusValue);
        column.AddChild(sliderRow);

        column.AddChild(new Label { Text = "Vertical voxel radius (chunks above and below)" });
        var verticalSliderRow = new HBoxContainer();
        _verticalRadiusSlider = new HSlider { MinValue = 2, MaxValue = 32, Step = 1, Value = DefaultVerticalChunkRadius, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _verticalRadiusValue = new Label { Text = $"{DefaultVerticalChunkRadius} chunks", CustomMinimumSize = new Vector2(100, 0) };
        _verticalRadiusSlider.ValueChanged += value => _verticalRadiusValue.Text = $"{Mathf.RoundToInt(value)} chunks";
        verticalSliderRow.AddChild(_verticalRadiusSlider);
        verticalSliderRow.AddChild(_verticalRadiusValue);
        column.AddChild(verticalSliderRow);

        column.AddChild(new Label { Text = "Terrain generation workers (powerful CPUs)" });
        var workerRow = new HBoxContainer();
        int currentWorkers = Mathf.Clamp(_coordinator?.MaxConcurrentWorkerBuilds ?? 2, 1, 32);
        _workerSlider = new HSlider
        {
            MinValue = 1,
            MaxValue = 32,
            Step = 1,
            Value = currentWorkers,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _workerValue = new Label { Text = $"{currentWorkers} workers", CustomMinimumSize = new Vector2(100, 0) };
        _workerSlider.ValueChanged += value =>
        {
            int workers = Mathf.RoundToInt(value);
            _workerValue.Text = workers == 1 ? "1 worker" : $"{workers} workers";
        };
        workerRow.AddChild(_workerSlider);
        workerRow.AddChild(_workerValue);
        column.AddChild(workerRow);
        var workerNote = new Label
        {
            Text = "Each active LOD ring receives one worker first; surplus workers split that ring's pending sectors in parallel.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        workerNote.Modulate = new Color(0.75f, 0.8f, 0.88f);
        column.AddChild(workerNote);

        column.AddChild(new HSeparator());
        column.AddChild(new Label { Text = "Atmosphere" });
        _hazeToggle = new CheckButton
        {
            Text = "Distant atmospheric haze",
            ButtonPressed = HazeEnabledByDefault
        };
        _hazeToggle.Toggled += SetAtmosphericHaze;
        column.AddChild(_hazeToggle);

        column.AddChild(new HSeparator());
        column.AddChild(new Label { Text = "Terrain Layer Debug" });
        _debugColors = new CheckButton { Text = "Diagnostic colors for voxel, all proxy levels, global, and legacy terrain" };
        _debugColors.Toggled += SetDiagnosticColors;
        column.AddChild(_debugColors);
        // A two-column toggle grid made its minimum width larger than the
        // panel and forced the entire menu into a horizontal scroll region.
        var layerGrid = new GridContainer { Columns = 1 };
        column.AddChild(layerGrid);
        AddLayerToggle(layerGrid, "Show voxel chunks — red", _grid, true);
        if (_coordinator != null)
            foreach (RingTerrainProxySector level in _coordinator.Levels)
                AddLayerToggle(layerGrid, $"Show {level.LodLabel}", level, level.StreamingActive);
        AddLayerToggle(layerGrid, "Show global terrain — yellow", _globalLod, true);
        AddLayerToggle(layerGrid, "Show analytic global ring — teal", _analyticGlobalLod, true);
        AddLayerToggle(layerGrid, "Show legacy far ring — magenta", _legacyFarRing, true);

        column.AddChild(new HSeparator());
        column.AddChild(new Label { Text = "Display" });
        var aspectRow = new HBoxContainer();
        aspectRow.AddChild(new Label { Text = "Aspect ratio", CustomMinimumSize = new Vector2(150, 0) });
        _aspectRatioOptions = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _aspectRatioOptions.AddItem("16:9");
        _aspectRatioOptions.AddItem("16:10");
        _aspectRatioOptions.AddItem("21:9 Ultrawide");
        _aspectRatioOptions.AddItem("4:3");
        _aspectRatioOptions.ItemSelected += PopulateResolutionOptions;
        aspectRow.AddChild(_aspectRatioOptions);
        column.AddChild(aspectRow);

        var resolutionRow = new HBoxContainer();
        resolutionRow.AddChild(new Label { Text = "Resolution", CustomMinimumSize = new Vector2(150, 0) });
        _resolutionOptions = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        resolutionRow.AddChild(_resolutionOptions);
        column.AddChild(resolutionRow);
        PopulateResolutionOptions(0);
        var note = new Label
        {
            Text = "Higher voxel ranges greatly increase memory and generation cost.\nVertical radius is especially expensive because it multiplies the full surface window.\nProxy spacing, sector size, fade bands, and shadow policy are editable on each LOD node in the Godot inspector.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        note.Modulate = new Color(0.75f, 0.8f, 0.88f);
        column.AddChild(note);

        _applyStatus = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _applyStatus.Modulate = new Color(0.55f, 0.9f, 0.65f);
        column.AddChild(_applyStatus);
        var apply = new Button { Text = "Apply Settings", CustomMinimumSize = new Vector2(0, 38) };
        apply.Pressed += ApplySettings;
        column.AddChild(apply);
        var resume = new Button { Text = "Resume" };
        resume.Pressed += () => SetMenuOpen(false);
        column.AddChild(resume);
    }

    private static void AddLayerToggle(Container container, string text, Node3D? layer, bool initial)
    {
        var toggle = new CheckButton { Text = text, ButtonPressed = initial };
        toggle.Toggled += visible => { if (layer != null) layer.Visible = visible; };
        container.AddChild(toggle);
    }

    private void ReturnToNearestSurface()
    {
        bool returned = _explorer?.ReturnToNearestSurface() == true;
        if (_applyStatus != null)
            _applyStatus.Text = returned
                ? "Returned to the nearest surface."
                : "Surface recovery is unavailable until terrain is initialized.";
        if (returned) SetMenuOpen(false);
    }

    private void TeleportToNearestCave()
    {
        string feedback = "Cave teleport is unavailable until terrain is initialized.";
        bool teleported = _explorer != null && _explorer.TryTeleportToNearestDesignedCave(out feedback);
        if (_applyStatus != null)
            _applyStatus.Text = feedback;
        if (teleported) SetMenuOpen(false);
    }

    private void SetCaveCheckpoint()
    {
        string feedback = "Cave checkpoint is unavailable until terrain is initialized.";
        _explorer?.TrySetUndergroundCheckpoint(out feedback);
        if (_applyStatus != null) _applyStatus.Text = feedback;
    }

    private void RecoverToCaveCheckpoint()
    {
        string feedback = "Cave checkpoint is unavailable until terrain is initialized.";
        bool recovered = _explorer != null
            && _explorer.TryRecoverToUndergroundCheckpoint(out feedback);
        if (_applyStatus != null) _applyStatus.Text = feedback;
        if (recovered) SetMenuOpen(false);
    }

    private void SetAtmosphericHaze(bool enabled)
    {
        RingAtmosphereModel.Enabled = enabled;
        ProjectSettings.SetSetting("ringworld/runtime/haze_enabled", enabled);
        if (_worldEnvironment?.Environment is { } environment)
            environment.FogEnabled = false;
        ApplyAtmosphereSettingRecursively(GetTree().CurrentScene, enabled);
    }

    private static void ApplyAtmosphereSettingRecursively(Node? node, bool enabled)
    {
        if (node == null) return;
        if (node is GeometryInstance3D geometry)
        {
            SetAtmosphereOnMaterial(geometry.MaterialOverride, enabled);
            SetAtmosphereOnMaterial(geometry.MaterialOverlay, enabled);
            if (geometry is MeshInstance3D meshInstance && meshInstance.Mesh != null)
            {
                for (int surface = 0; surface < meshInstance.Mesh.GetSurfaceCount(); surface++)
                {
                    SetAtmosphereOnMaterial(meshInstance.GetSurfaceOverrideMaterial(surface), enabled);
                    SetAtmosphereOnMaterial(meshInstance.Mesh.SurfaceGetMaterial(surface), enabled);
                }
            }
        }
        foreach (Node child in node.GetChildren())
            ApplyAtmosphereSettingRecursively(child, enabled);
    }

    private static void SetAtmosphereOnMaterial(Material? material, bool enabled)
    {
        if (material is not ShaderMaterial shaderMaterial || shaderMaterial.Shader == null) return;
        if (!shaderMaterial.Shader.Code.Contains("ring_atmosphere_enabled")) return;
        shaderMaterial.SetShaderParameter("ring_atmosphere_enabled", enabled);
    }

    private void SetDiagnosticColors(bool enabled)
    {
        Color[] levelColors = { Colors.Lime, Colors.Cyan, Colors.DodgerBlue, Colors.MediumPurple, Colors.Orange, Colors.HotPink, Colors.Gold, Colors.White };
        if (_coordinator != null)
            for (int i = 0; i < _coordinator.Levels.Count; i++)
                _coordinator.Levels[i].SetDebugColor(enabled, levelColors[i % levelColors.Length]);
        _globalLod?.SetDebugColor(enabled, Colors.Yellow);
        if (enabled)
        {
            _voxelDebugMaterial ??= MakeDebugMaterial(Colors.Red);
            _legacyDebugMaterial ??= MakeDebugMaterial(Colors.Magenta);
            ApplyMaterialRecursively(_grid, _voxelDebugMaterial);
            ApplyMaterialRecursively(_legacyFarRing, _legacyDebugMaterial);
        }
        else
        {
            foreach ((GeometryInstance3D geometry, Material? material) in _debugOriginalMaterials)
                if (GodotObject.IsInstanceValid(geometry)) geometry.MaterialOverride = material;
            _debugOriginalMaterials.Clear();
        }
    }

    private static StandardMaterial3D MakeDebugMaterial(Color color) => new()
    {
        AlbedoColor = color,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Roughness = 1.0f
    };

    private void ApplyMaterialRecursively(Node? node, Material material)
    {
        if (node == null) return;
        if (node is GeometryInstance3D geometry)
        {
            if (!_debugOriginalMaterials.ContainsKey(geometry))
                _debugOriginalMaterials[geometry] = geometry.MaterialOverride;
            geometry.MaterialOverride = material;
        }
        foreach (Node child in node.GetChildren()) ApplyMaterialRecursively(child, material);
    }

    private void ApplySettings()
    {
        int chunks = Mathf.Clamp(Mathf.RoundToInt(_radiusSlider?.Value ?? DefaultChunkRadius), 2, 32);
        int verticalChunks = Mathf.Clamp(Mathf.RoundToInt(_verticalRadiusSlider?.Value ?? DefaultVerticalChunkRadius), 2, 32);
        var quality = (RingTerrainLodCoordinator.QualityPreset)Mathf.Clamp(
            _qualityOptions?.Selected ?? (int)RingTerrainLodCoordinator.QualityPreset.Medium,
            0,
            (int)RingTerrainLodCoordinator.QualityPreset.Cinematic5090);
        if (_graphicsQuality != null) _graphicsQuality.ApplyUserPreset(quality);
        else _coordinator?.ApplyUserQualityPreset(quality);
        int workers = Mathf.Clamp(Mathf.RoundToInt(_workerSlider?.Value ?? 2.0), 1, 32);
        if (_coordinator != null) _coordinator.MaxConcurrentWorkerBuilds = workers;
        ApplyLodProfile(chunks, verticalChunks);

        QueueDisplaySettingsApply();
        if (_applyStatus != null)
        {
            _applyStatus.Text = $"Applied: {_graphicsQuality?.CurrentPresetName ?? _coordinator?.CurrentPresetName ?? quality.ToString()}, surface {chunks}, vertical {verticalChunks}, workers {workers}";
        }
    }

    private void OnUpscalingSelected(long index)
    {
        SetRenderResolutionPreset((int)index);
    }

    public void SetRenderResolutionPreset(int preset)
    {
        preset = GameplayRenderResolutionDefaults.ValidatePreset(preset);
        if (!_renderResolutionReady) return;
        bool changed = _renderResolutionDefaults.Set(preset, _renderResolutionDefaults.Settings.AdaptiveResolutionEnabled);
        ApplyUpscalingPreset((UpscalingPreset)preset, updateStatus: true);
        ConfigureAdaptiveRenderResolution();
        if (changed) { _renderResolutionSaveElapsed = 0.0; _renderResolutionRetryElapsed = 0.0; }
    }

    public void SetAdaptiveRenderResolution(bool enabled)
    {
        if (!_renderResolutionReady) return;
        bool changed = _renderResolutionDefaults.Set(_renderResolutionDefaults.Settings.Preset, enabled);
        if (_adaptiveRenderResolution != null && _adaptiveRenderResolution.ButtonPressed != enabled)
            _adaptiveRenderResolution.SetPressedNoSignal(enabled);
        ConfigureAdaptiveRenderResolution();
        if (!enabled) ApplyUpscalingPreset((UpscalingPreset)_renderResolutionDefaults.Settings.Preset, updateStatus: true);
        if (changed) { _renderResolutionSaveElapsed = 0.0; _renderResolutionRetryElapsed = 0.0; }
    }

    public void ResetRenderResolutionSettings()
    {
        if (!_renderResolutionReady) return;
        _renderResolutionDefaults.Reset();
        if (_upscalingOptions != null) _upscalingOptions.Select(0);
        if (_adaptiveRenderResolution != null) _adaptiveRenderResolution.SetPressedNoSignal(false);
        ApplyUpscalingPreset(UpscalingPreset.Native, updateStatus: true);
        ConfigureAdaptiveRenderResolution();
        _renderResolutionSaveElapsed = 0.0;
        _renderResolutionRetryElapsed = 0.0;
    }

    public bool FlushRenderResolutionSettings()
    {
        _renderResolutionSaveElapsed = 0.0;
        _renderResolutionRetryElapsed = 0.0;
        return _renderResolutionDefaults.Save();
    }

    public void InstallRenderResolutionDefaults(string isolatedPath)
    {
        if (!isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal)
            && !isolatedPath.StartsWith("user://generated/", StringComparison.Ordinal))
            throw new ArgumentException("Render-resolution test settings must use res://.godot/ or user://generated/.", nameof(isolatedPath));
        _renderResolutionDefaults = new GameplayRenderResolutionDefaults(isolatedPath);
        if (!_renderResolutionReady) return;
        _renderResolutionDefaults.Load((int)DefaultUpscalingPreset);
        ApplySavedRenderResolution(updateStatus: false);
    }

    public Godot.Collections.Dictionary GetRenderResolutionDiagnostics()
    {
        Viewport? viewport = _renderResolutionReady ? GetViewport() : null;
        int requested = _renderResolutionDefaults.Settings.Preset;
        int effective = !_fsr2Available && requested != 0 ? 0 : requested;
        return new Godot.Collections.Dictionary
        {
            ["ready"] = _renderResolutionReady,
            ["preset"] = effective,
            ["requested_scale"] = GetRenderResolutionScale(requested),
            ["current_scale"] = viewport?.Scaling3DScale ?? 1.0f,
            ["adaptive_enabled"] = _renderResolutionDefaults.Settings.AdaptiveResolutionEnabled,
            ["memory_protection_enabled"] = _certificationMonitor?.EnableAdaptiveProtection ?? false,
            ["fsr_available"] = _fsr2Available,
            ["path"] = _renderResolutionDefaults.GetPath(),
            ["dirty"] = _renderResolutionDefaults.Dirty,
            ["last_error"] = _renderResolutionDefaults.LastError
        };
    }

    private void ApplySavedRenderResolution(bool updateStatus)
    {
        int preset = GameplayRenderResolutionDefaults.ValidatePreset(_renderResolutionDefaults.Settings.Preset);
        if (_upscalingOptions != null) _upscalingOptions.Select(preset);
        if (_adaptiveRenderResolution != null) _adaptiveRenderResolution.SetPressedNoSignal(_renderResolutionDefaults.Settings.AdaptiveResolutionEnabled);
        ApplyUpscalingPreset((UpscalingPreset)preset, updateStatus);
        ConfigureAdaptiveRenderResolution();
    }

    private void ConfigureAdaptiveRenderResolution()
    {
        if (_certificationMonitor == null) return;
        _certificationMonitor.EnableAdaptiveRenderResolution = _renderResolutionDefaults.Settings.AdaptiveResolutionEnabled;
        int effectivePreset = !_fsr2Available && _renderResolutionDefaults.Settings.Preset != 0
            ? 0
            : _renderResolutionDefaults.Settings.Preset;
        _certificationMonitor.MaximumRenderScale = GetRenderResolutionScale(effectivePreset);
    }

    private void ApplyUpscalingPreset(UpscalingPreset requested, bool updateStatus = true)
    {
        Viewport viewport = GetViewport();
        UpscalingPreset applied = requested;
        if (!_fsr2Available && requested != UpscalingPreset.Native)
        {
            applied = UpscalingPreset.Native;
        }
        if (_upscalingOptions != null && _upscalingOptions.Selected != (int)applied)
            _upscalingOptions.Select((int)applied);

        float scale = GetRenderResolutionScale((int)applied);
        viewport.Scaling3DMode = applied == UpscalingPreset.Native
            ? Viewport.Scaling3DModeEnum.Bilinear
            : Viewport.Scaling3DModeEnum.Fsr2;
        viewport.Scaling3DScale = scale;
        viewport.FsrSharpness = 0.2f;
        GameplayAntiAliasingDefaults.ReapplyRuntimePreference(GetTree().Root);

        if (updateStatus && _applyStatus != null)
        {
            _applyStatus.Text = !_fsr2Available && requested != UpscalingPreset.Native
                ? "FSR 2.2 requires Forward+; requested resolution is unavailable and 100% native remains active."
                : $"3D render resolution: requested {GetRenderResolutionScale((int)requested) * 100f:0}%, active {scale * 100f:0}% ({GetRenderResolutionPresetLabel((int)applied)})";
        }
        UpdateRenderResolutionStatus();
    }

    private void UpdateRenderResolutionStatus()
    {
        if (_renderResolutionStatus == null || !_renderResolutionReady) return;
        int requested = _renderResolutionDefaults.Settings.Preset;
        float requestedScale = GetRenderResolutionScale(requested);
        float currentScale = GetViewport().Scaling3DScale;
        string saveState = _renderResolutionDefaults.LastError.Length > 0
            ? $"Save error: {_renderResolutionDefaults.LastError}"
            : _renderResolutionDefaults.Dirty ? "Saving settings..." : "Settings saved.";
        _renderResolutionStatus.Text = $"Requested {requestedScale * 100f:0}% / current {currentScale * 100f:0}% | Automatic {(_renderResolutionDefaults.Settings.AdaptiveResolutionEnabled ? "on" : "off")} | {saveState}";
    }

    public static float GetRenderResolutionScale(int preset) => GameplayRenderResolutionDefaults.ValidatePreset(preset) switch
    {
        1 => 1.0f, 2 => 0.85f, 3 => 0.75f, 4 => 0.67f, 5 => 0.50f, _ => 1.0f
    };

    public static string GetRenderResolutionPresetLabel(int preset) => GameplayRenderResolutionDefaults.ValidatePreset(preset) switch
    {
        0 => "100% - Native (default)",
        1 => "100% - FSR 2.2 Native AA",
        2 => "85% - FSR 2.2 Quality",
        3 => "75% - FSR 2.2 Balanced",
        4 => "67% - FSR 2.2 Performance",
        5 => "50% - FSR 2.2 Ultra Performance",
        _ => "100% - Native (default)"
    };

    private void OnQualitySelected(long index)
    {
        if (_workerSlider == null) return;
        int workers = (RingTerrainLodCoordinator.QualityPreset)Mathf.Clamp(
            (int)index,
            0,
            (int)RingTerrainLodCoordinator.QualityPreset.Cinematic5090) switch
        {
            RingTerrainLodCoordinator.QualityPreset.Low => 2,
            RingTerrainLodCoordinator.QualityPreset.Medium => 2,
            RingTerrainLodCoordinator.QualityPreset.High => 4,
            RingTerrainLodCoordinator.QualityPreset.VeryHigh => 6,
            RingTerrainLodCoordinator.QualityPreset.UltraVideo => 16,
            RingTerrainLodCoordinator.QualityPreset.Cinematic5090 => 24,
            _ => 2
        };
        _workerSlider.Value = workers;
    }

    private void ApplyLodProfile(int chunks, int verticalChunks)
    {
        _grid?.ApplySurfaceStreamingRadius(chunks);
        _grid?.ApplyVerticalStreamingRadius(verticalChunks);
        float voxelStart = Mathf.Max(32.0f, (chunks - 1) * Voxel.Core.VoxelConstants.ChunkSizeX);
        float voxelEnd = chunks * Voxel.Core.VoxelConstants.ChunkSizeX + 8.0f;
        _coordinator?.ConfigureVoxelTransition(voxelStart, voxelEnd);

        // The coordinator assigns one detailed proxy caster and one coarse
        // caster large enough to cover the sun's local shadow bubble.
        _globalLod?.SetTerrainShadowCasting(false);
    }

    private void PopulateResolutionOptions(long aspectIndex)
    {
        if (_resolutionOptions == null) return;
        _resolutionOptions.Clear();
        _availableResolutions.Clear();
        Vector2I[] choices = aspectIndex switch
        {
            1 => new[] { new Vector2I(1280, 800), new Vector2I(1680, 1050), new Vector2I(1920, 1200), new Vector2I(2560, 1600) },
            2 => new[] { new Vector2I(2560, 1080), new Vector2I(3440, 1440), new Vector2I(3840, 1600), new Vector2I(5120, 2160) },
            3 => new[] { new Vector2I(1024, 768), new Vector2I(1280, 960), new Vector2I(1600, 1200), new Vector2I(2048, 1536) },
            _ => new[] { new Vector2I(1280, 720), new Vector2I(1600, 900), new Vector2I(1920, 1080), new Vector2I(2560, 1440), new Vector2I(3840, 2160) }
        };

        Vector2I current = DisplayServer.WindowGetSize();
        int bestIndex = 0;
        long bestDistance = long.MaxValue;
        foreach (Vector2I resolution in choices)
        {
            _availableResolutions.Add(resolution);
            _resolutionOptions.AddItem($"{resolution.X} × {resolution.Y}");
            long dx = resolution.X - current.X;
            long dy = resolution.Y - current.Y;
            long distance = dx * dx + dy * dy;
            if (distance < bestDistance) { bestDistance = distance; bestIndex = _availableResolutions.Count - 1; }
        }
        _resolutionOptions.Select(bestIndex);
    }

    private void QueueDisplaySettingsApply()
    {
        if (_resolutionOptions == null || _availableResolutions.Count == 0) return;
        int selected = Mathf.Clamp(_resolutionOptions.Selected, 0, _availableResolutions.Count - 1);
        _pendingResolution = _availableResolutions[selected];
        _displayApplyPending = true;
    }

    private void ApplyDisplaySettings(Vector2I requested)
    {
        if (DisplayServer.WindowGetMode() != DisplayServer.WindowMode.Windowed)
        {
            if (_applyStatus != null) _applyStatus.Text += " | Resolution saved; window is not in windowed mode";
            return;
        }

        int screen = DisplayServer.WindowGetCurrentScreen();
        Rect2I usable = DisplayServer.ScreenGetUsableRect(screen);
        Vector2I clamped = new(
            Mathf.Clamp(requested.X, 640, Mathf.Max(640, usable.Size.X)),
            Mathf.Clamp(requested.Y, 360, Mathf.Max(360, usable.Size.Y)));
        if (DisplayServer.WindowGetSize() != clamped)
        {
            DisplayServer.WindowSetSize(clamped);
        }
        Vector2I centered = usable.Position + new Vector2I(
            Mathf.Max(0, (usable.Size.X - clamped.X) / 2),
            Mathf.Max(0, (usable.Size.Y - clamped.Y) / 2));
        DisplayServer.WindowSetPosition(centered);
        if (_applyStatus != null) _applyStatus.Text += $" | {clamped.X}×{clamped.Y}";
    }

    private void SetMenuOpen(bool open)
    {
        if (_root != null) _root.Visible = open;
        if (open != _menuOpen)
        {
            if (open)
            {
                SuppressGameplayHud();
                _renderResolutionStatusElapsed = 0.0;
                UpdateRenderResolutionStatus();
            }
            else
            {
                RestoreGameplayHud();
                FlushRenderResolutionSettings();
            }
            _menuOpen = open;
        }
        GetTree().Paused = open;
        Input.MouseMode = open ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
    }

    public void SetBenchmarkMenuOpen(bool open)
    {
        SetMenuOpen(open);
    }

    public override void _ExitTree()
    {
        FlushRenderResolutionSettings();
        base._ExitTree();
    }

    private void SuppressGameplayHud()
    {
        _suppressedHudVisibility.Clear();
        foreach (string path in new[]
                 {
                     "../PresentationHud",
                     "../TerrainMinimap",
                     "../LodDebugHud",
                     "../CelestialDebugHud"
                 })
        {
            CanvasLayer? layer = GetNodeOrNull<CanvasLayer>(path);
            if (layer == null) continue;
            _suppressedHudVisibility[layer] = layer.Visible;
            layer.Visible = false;
        }
    }

    private void RestoreGameplayHud()
    {
        foreach ((CanvasLayer layer, bool wasVisible) in _suppressedHudVisibility)
        {
            if (GodotObject.IsInstanceValid(layer)) layer.Visible = wasVisible;
        }
        _suppressedHudVisibility.Clear();
    }
}
