using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Diagnostics;

namespace RingworldTechDemo.UI;

/// <summary>Non-pausing live visual tuning drawer for the integration scene.</summary>
public partial class RingLiveTuningDrawer : CanvasLayer
{
    public const string MouseOwnershipMeta = "ringworld_live_tuning_mouse_open";
    private static VoxelTerrainSettings? _pendingSurfaceTerrainSettings;
    private static double _pendingSurfaceStartAroundMeters;
    private static double _pendingSurfaceStartAcrossMeters;

    /// <summary>Consumes the one-time terrain and position handoff for the isolated surface prototype.</summary>
    public static bool TryConsumeSurfaceTerrainPrototypeLaunch(out VoxelTerrainSettings? terrainSettings, out double startAroundMeters, out double startAcrossMeters)
    {
        terrainSettings = _pendingSurfaceTerrainSettings;
        startAroundMeters = _pendingSurfaceStartAroundMeters;
        startAcrossMeters = _pendingSurfaceStartAcrossMeters;
        _pendingSurfaceTerrainSettings = null;
        _pendingSurfaceStartAroundMeters = 0;
        _pendingSurfaceStartAcrossMeters = 0;
        return terrainSettings != null;
    }

    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath CoordinatorPath { get; set; } = new("../TerrainLodCoordinator");
    [Export] public NodePath LodPauseMenuPath { get; set; } = new("../LodPauseMenu");
    [Export] public NodePath Lod1Path { get; set; } = new("../Lod1Terrain2m");
    [Export] public NodePath WorldEnvironmentPath { get; set; } = new("../WorldEnvironment");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export] public NodePath CloudBridgePath { get; set; } = new("../SunshineCloudBridge");
    [Export] public NodePath CloudProviderPath { get; set; } = new("../CloudProviderController");
    [Export] public NodePath WeatherEffectsPath { get; set; } = new("../LocalWeatherEffects");
    [Export] public NodePath DayNightCyclePath { get; set; } = new("../RingDayNightCycle");
    [Export] public NodePath WeatherAudioPath { get; set; } = new("../WeatherAudio");
    [Export] public NodePath WeatherAmbiencePath { get; set; } = new("../WeatherAmbience");
    [Export] public NodePath CoarsePilotPath { get; set; } = new("../CoarseSdfLod1Pilot");
    [Export] public NodePath GraphicsQualityPath { get; set; } = new("../GraphicsQuality");
    /// <summary>Apply gameplay preferences at startup; create tuning controls on first open.</summary>
    [Export] public bool DeferDrawerBuildUntilOpened { get; set; }
    [Export] public NodePath WaterFftPath { get; set; } = new("../WaterFftController");
    [Export] public NodePath PerformanceMonitorPath { get; set; } = new("../RingPerformanceMonitor");
    /// <summary>Set only by the isolated sunblocker scene.  It keeps the production drawer untouched.</summary>
    [Export] public NodePath SunblockerDemoPath { get; set; } = new NodePath();
    /// <summary>Optional authoring-preview root. When set, F6 exposes only the biome preset controls.</summary>
    [Export] public NodePath BiomeAuthoringPreviewPath { get; set; } = new NodePath();
    /// <summary>Optional isolated reference-generation preview. It has its own versioned recipe save.</summary>
    [Export] public NodePath GenerationPreviewPath { get; set; } = new NodePath();
    /// <summary>Optional isolated Certified Terrain3D surface prototype root.</summary>
    [Export] public NodePath SurfaceTerrainPilotPath { get; set; } = new NodePath();
    [Export] public string SurfaceTerrainDefaultsPathOverride { get; set; } = "";
    [Export(PropertyHint.Range, "280,620,10")] public float DrawerWidth { get; set; } = 420.0f;

    private VoxelChunkGridManager? _grid;
    private RingTerrainLodCoordinator? _coordinator;
    private RingLodPauseMenu? _lodPauseMenu;
    private RingTerrainProxySector? _lod1;
    private WorldEnvironment? _environment;
    private DirectionalLight3D? _sun;
    private global::RingworldTechDemo.RingSunshineCloudBridge? _cloudBridge;
    private global::RingworldTechDemo.RingCloudProviderController? _cloudProvider;
    private global::RingworldTechDemo.RingLocalWeatherEffects? _weatherEffects;
    private RingTerrainCoarseSdfPilot? _coarsePilot;
    private global::RingworldTechDemo.RingDayNightCycle? _dayNightCycle;
    private global::RingworldTechDemo.RingWeatherAudio? _weatherAudio;
    private global::RingworldTechDemo.RingWeatherAmbience? _weatherAmbience;
    private RingPerformanceMonitor? _performanceMonitor;
    private RingLodDebugHud? _performanceHud;
    private PanelContainer? _drawer;
    private TuningPopupIsolation? _popupIsolation;
    private Tween? _tween;
    private bool _open;
    private Label? _cloudStatus;
    private Label? _effectiveCloudHandoffStatus;
    private CheckButton? _partialCoverageMeshCullingToggle;
    private Label? _partialCoverageMeshCullingStatus;
    private CheckButton? _intermediateLodTiersToggle;
    private Label? _intermediateLodTiersStatus;
    private GameplayCloudDefaults _cloudDefaults = null!;
    private Label? _cloudSaveStatus;
    private double _cloudSaveDelay, _cloudSaveRetryDelay;
    private double _cloudRefreshDelay;
    private ConfirmationDialog? _cloudResetConfirmation;
    private readonly System.Collections.Generic.HashSet<string> _cloudControlKeys = new();
    private global::RingworldTechDemo.SunblockerDemoController? _sunblockerDemo;
    private Node? _biomeAuthoringPreview;
    private Node? _generationPreview;
    private Node? _surfaceTerrainPilot;
    private Node? _surfaceTerrainGameplay;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _popupIsolation = new TuningPopupIsolation(this);
        _biomeAuthoringPreview = GetNodeOrNull<Node>(BiomeAuthoringPreviewPath);
        _generationPreview = GetNodeOrNull<Node>(GenerationPreviewPath);
        if (_generationPreview != null)
        {
            BuildDrawer();
            SetOpen(false, immediate: true);
            return;
        }
        if (_biomeAuthoringPreview != null)
        {
            BuildDrawer();
            SetOpen(false, immediate: true);
            return;
        }
        _surfaceTerrainPilot = GetNodeOrNull<Node>(SurfaceTerrainPilotPath);
        if (_surfaceTerrainPilot != null)
        {
            if (!string.IsNullOrWhiteSpace(SurfaceTerrainDefaultsPathOverride))
                _surfaceTerrainDefaultsPathOverride = SurfaceTerrainDefaultsPathOverride;
            InitializeSurfaceTerrainTuning();
            if (_surfaceTerrainPilot.HasMethod("get_recipe_snapshot")) _generationPreview = _surfaceTerrainPilot;
            BuildDrawer();
            SetOpen(false, immediate: true);
            return;
        }
        _sunblockerDemo = GetNodeOrNull<global::RingworldTechDemo.SunblockerDemoController>(SunblockerDemoPath);
        if (_sunblockerDemo != null || _biomeAuthoringPreview != null || _generationPreview != null || _surfaceTerrainPilot != null)
        {
            BuildDrawer();
            SetOpen(false, immediate: true);
            return;
        }
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _coordinator = GetNodeOrNull<RingTerrainLodCoordinator>(CoordinatorPath);
        _lodPauseMenu = GetNodeOrNull<RingLodPauseMenu>(LodPauseMenuPath);
        _lod1 = GetNodeOrNull<RingTerrainProxySector>(Lod1Path);
        _environment = GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath);
        _sun = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        _cloudBridge = GetNodeOrNull<global::RingworldTechDemo.RingSunshineCloudBridge>(CloudBridgePath);
        _cloudProvider = GetNodeOrNull<global::RingworldTechDemo.RingCloudProviderController>(CloudProviderPath);
        _weatherEffects = GetNodeOrNull<global::RingworldTechDemo.RingLocalWeatherEffects>(WeatherEffectsPath);
        _coarsePilot = GetNodeOrNull<RingTerrainCoarseSdfPilot>(CoarsePilotPath);
        _dayNightCycle = GetNodeOrNull<global::RingworldTechDemo.RingDayNightCycle>(DayNightCyclePath);
        _weatherAudio = GetNodeOrNull<global::RingworldTechDemo.RingWeatherAudio>(WeatherAudioPath);
        _weatherAmbience = GetNodeOrNull<global::RingworldTechDemo.RingWeatherAmbience>(WeatherAmbiencePath);
        _performanceMonitor = GetNodeOrNull<RingPerformanceMonitor>(PerformanceMonitorPath);
        _performanceHud = GetNodeOrNull<RingLodDebugHud>("../LodDebugHud");
        _surfaceTerrainGameplay = GetNodeOrNull<Node>("../Terrain3DGameplay");
        InitializeAntiAliasingTuning();
        InitializeMotionTuning();
        InitializeWaterTuning();
        InitializeWindTuning();
        InitializeRainHazeTuning();
        InitializeGrassTuning();
        InitializeVegetationTuning();
        InitializeShadowTuning();
        InitializeAtmosphereTuning();
        InitializeFarRenderingTuning();
        InitializeLoadingTuning();
        if (_surfaceTerrainGameplay != null)
            InitializeSurfaceTerrainTuning();
        // The bridge owns the shared, startup-initialized sparse overrides.
        _cloudDefaults = _cloudBridge?.GameplayCloudDefaults ?? new GameplayCloudDefaults();
        if (!DeferDrawerBuildUntilOpened)
        {
            BuildDrawer();
            SetOpen(false, immediate: true);
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (_cloudResetConfirmation?.Visible == true || BiomeModalActive) return;
        if (TuningMenuUi.HandleNumericWheel(_drawer, @event)) return;
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode == Key.Quoteleft || key.PhysicalKeycode == Key.Quoteleft)
        {
            SetOpen(!_open, immediate: false);
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.Escape && _open)
        {
            SetOpen(false, immediate: false);
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _ExitTree()
    {
        _popupIsolation?.Dispose();
        _popupIsolation = null;
        if (_sunblockerDemo != null || _biomeAuthoringPreview != null || _generationPreview != null || _surfaceTerrainPilot != null)
        {
            FlushSurfaceTerrainDefaults();
            GetTree().Root.SetMeta(MouseOwnershipMeta, false);
            return;
        }
        FlushCloudDefaults();
        FlushAntiAliasingDefaults();
        FlushMotionDefaults();
        FlushWaterDefaults();
        FlushWindDefaults();
        FlushRainHazeDefaults();
        FlushGrassDefaults();
        _vegetationDefaults?.Save();
        FlushShadowDefaults();
        FlushAtmosphereDefaults();
        FlushFarRenderingDefaults();
        FlushLoadingPresentationDefaults();
        FlushSurfaceTerrainDefaults();
        GetTree().Root.SetMeta(MouseOwnershipMeta, false);
        if (_open)
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _Process(double delta)
    {
        if (_surfaceTerrainPilot != null)
        {
            ProcessSurfaceTerrainTuning(delta);
            if (_generationPreview != null) ProcessGenerationPreviewTuning(delta);
            return;
        }
        if (_generationPreview != null) { ProcessGenerationPreviewTuning(delta); return; }
        if (_sunblockerDemo != null || _biomeAuthoringPreview != null) return;
        ProcessSurfaceTerrainPrototypeLaunch(delta);
        if (_open)
        {
            _cloudRefreshDelay -= delta;
            if (_cloudRefreshDelay <= 0)
            {
                _cloudRefreshDelay = .25;
                RefreshCloudControls();
                RefreshPartialCoverageMeshCullingStatus();
                RefreshIntermediateLodTiersStatus();
            }
        }
        ProcessWaterTuning(delta);
        ProcessWindTuning(delta);
        ProcessRainHazeTuning(delta);
        ProcessShadowTuning(delta);
        ProcessAtmosphereTuning(delta);
        ProcessFarRenderingTuning(delta);
        ProcessLoadingTuning(delta);
        ProcessAntiAliasingTuning(delta);
        ProcessMotionTuning(delta);
        if (_surfaceTerrainGameplay != null)
            ProcessSurfaceTerrainTuning(delta);
        _grassApplyDelay -= delta;
        CommitQueuedGrassValues();
        ProcessGrassTuning(delta);
        ProcessVegetationTuning(delta);
        ProcessDiagnosticsTuning(delta);
        if (_cloudDefaults.Dirty)
        {
            _cloudSaveDelay -= delta; _cloudSaveRetryDelay -= delta;
            if (_cloudSaveDelay <= 0 && _cloudSaveRetryDelay <= 0 && !FlushCloudDefaults()) _cloudSaveRetryDelay = 5.0;
        }
    }

    /// <summary>Opens the live drawer without animation for rendered UI smoke tests.</summary>
    public void SetBenchmarkDrawerOpen(bool open) => SetOpen(open, immediate: true);

    private void BuildDrawer()
    {
        _drawer = new PanelContainer
        {
            Name = "LiveTuningDrawer",
            Theme = RingUiStyle.CreateTheme(),
            MouseFilter = Control.MouseFilterEnum.Stop,
            // The panel is right-anchored. If content raises its minimum width,
            // grow left into the viewport instead of beyond the right edge.
            GrowHorizontal = Control.GrowDirection.Begin,
            Visible = false
        };
        _drawer.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        _drawer.OffsetTop = 18;
        _drawer.OffsetBottom = -18;
        _drawer.OffsetRight = 0;
        _drawer.OffsetLeft = 0;
        _drawer.CustomMinimumSize = new Vector2(DrawerWidth, 0);
        _drawer.AddThemeStyleboxOverride("panel", RingUiStyle.GlassPanel(0.97f, 8));
        AddChild(_drawer);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 18);
        margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        _drawer.AddChild(margin);
        var column = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        margin.AddChild(column);
        var header = new HBoxContainer();
        header.AddChild(new Label { Text = "LIVE TUNING", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var close = new Button { Text = "X", TooltipText = "Close tuning drawer" };
        close.Pressed += () => SetOpen(false, immediate: false);
        header.AddChild(close);
        column.AddChild(header);
        var tabs = new TabContainer { Name = "LiveTuningTabs", SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        tabs.AddThemeFontSizeOverride("font_size", 13);
        tabs.GetTabBar().AddThemeFontSizeOverride("font_size", 13);
        column.AddChild(tabs);
        if (_sunblockerDemo != null)
        {
            BuildSunblockerDemoTab(tabs, _sunblockerDemo);
            DisableKeyboardFocus(_drawer);
            TuningMenuUi.ConfigureNumericScrolling(_drawer);
            return;
        }
        if (_biomeAuthoringPreview != null)
        {
            BuildBiomeAuthoringTab(tabs);
            DisableKeyboardFocus(_drawer);
            TuningMenuUi.ConfigureNumericScrolling(_drawer);
            return;
        }
        if (_generationPreview != null)
        {
            BuildGenerationPreviewTab(tabs);
            if (_surfaceTerrainPilot == null)
            {
                DisableKeyboardFocus(_drawer);
                TuningMenuUi.ConfigureNumericScrolling(_drawer);
                return;
            }
        }
        if (_surfaceTerrainPilot != null)
        {
            BuildSurfaceTerrainTab(tabs);
            DisableKeyboardFocus(_drawer);
            TuningMenuUi.ConfigureNumericScrolling(_drawer);
            return;
        }
        BuildLightingTab(tabs);
        BuildAtmosphereTab(tabs);
        BuildShadowsTab(tabs);
        BuildCloudsTab(tabs);
        BuildCloudQualityTab(tabs);
        BuildAntiAliasingTab(tabs);
        BuildMotionTab(tabs);
        BuildLodTab(tabs);
        BuildFarRenderingTab(tabs);
        BuildLoadingTab(tabs);
        BuildTerrainTab(tabs);
        if (_surfaceTerrainGameplay != null)
            BuildSurfaceTerrainGameplayTab(tabs);
        else
            BuildSurfaceTerrainPrototypeLaunch(tabs);
        BuildWaterTab(tabs);
        BuildWindTab(tabs);
        BuildGrassTab(tabs);
        BuildVegetationTab(tabs);
        BuildDiagnosticsTab(tabs);
        _cloudSaveStatus = new Label { Name = "CloudDefaultsStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        column.AddChild(_cloudSaveStatus);
        CreateCloudResetConfirmation();
        UpdateCloudSaveStatus();
        DisableKeyboardFocus(_drawer);
        TuningMenuUi.ConfigureNumericScrolling(_drawer);
    }

    private void BuildLightingTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Lighting");
        VBoxContainer weather = TuningMenuUi.CreateSection(tab, "Weather audio");
        var lightningButton = new Button
        {
            Text = "SPAWN LIGHTNING STRIKE",
            TooltipText = "Close the drawer and spawn a procedural shared-lightning strike near the player.",
            Disabled = _weatherEffects == null
        };
        lightningButton.Pressed += SpawnLightningStrike;
        weather.AddChild(lightningButton);
        AddToggle(weather, "Thunder", _weatherAudio?.Enabled ?? false, value => { if (_weatherAudio != null) _weatherAudio.Enabled = value; });
        AddSlider(weather, "Thunder volume", -48, 12, 0.5, _weatherAudio?.ThunderVolumeDb ?? -8, value => { if (_weatherAudio != null) _weatherAudio.ThunderVolumeDb = (float)value; });
        AddToggle(weather, "Weather ambience", _weatherAmbience?.Enabled ?? false, value => { if (_weatherAmbience != null) _weatherAmbience.Enabled = value; });
        AddSlider(weather, "Rain volume", -48, 6, 0.5, _weatherAmbience?.RainVolumeDb ?? -10, value => { if (_weatherAmbience != null) _weatherAmbience.RainVolumeDb = (float)value; });
        AddSlider(weather, "Wind volume", -48, 6, 0.5, _weatherAmbience?.WindVolumeDb ?? -13, value => { if (_weatherAmbience != null) _weatherAmbience.WindVolumeDb = (float)value; });
        AddSlider(weather, "Distant storm volume", -48, 6, 0.5, _weatherAmbience?.DistantStormVolumeDb ?? -18, value => { if (_weatherAmbience != null) _weatherAmbience.DistantStormVolumeDb = (float)value; });
        VBoxContainer sun = TuningMenuUi.CreateSection(tab, "Sun / ambient");
        AddSlider(sun, "Sun energy", 0, 8, 0.05, _sun?.LightEnergy ?? 3, value => { if (_sun != null) _sun.LightEnergy = (float)value; });
        AddDayNightSpeedControl(sun);
        Godot.Environment? env = _environment?.Environment;
        AddSlider(sun, "Ambient energy", 0, 2, 0.02, env?.AmbientLightEnergy ?? 0.4, value => { if (env != null) env.AmbientLightEnergy = (float)value; });
        VBoxContainer fog = TuningMenuUi.CreateSection(tab, "Fog");
        AddToggle(fog, "Atmospheric fog", env?.FogEnabled ?? false, value => { if (env != null) env.FogEnabled = value; });
        AddSlider(fog, "Fog density", 0, 0.005, 0.00002, env?.FogDensity ?? 0.00014, value => { if (env != null) env.FogDensity = (float)value; });
        AddSlider(fog, "Fog light energy", 0, 2, 0.02, env?.FogLightEnergy ?? 0.9, value => { if (env != null) env.FogLightEnergy = (float)value; });
        AddSlider(fog, "Fog sky influence", 0, 1, 0.01, env?.FogSkyAffect ?? 0.6, value => { if (env != null) env.FogSkyAffect = (float)value; });
    }

    private void AddDayNightSpeedControl(VBoxContainer parent)
    {
        string cycleDuration = GetRingDayDurationLabel();
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = "Day/night speed", CustomMinimumSize = new Vector2(150, 0) });
        var options = new OptionButton
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = $"One full ring-spin day lasts {cycleDuration} at 1×. This changes only the time rate."
        };
        int[] speeds = { 1, 2, 5, 10, 25, 50, 100 };
        float activeSpeed = GetDayNightSpeed();
        int selected = 0;
        for (int i = 0; i < speeds.Length; i++)
        {
            options.AddItem($"{speeds[i]}×");
            if (Mathf.IsEqualApprox(activeSpeed, speeds[i])) selected = i;
        }
        options.Select(selected);
        options.ItemSelected += index => SetDayNightSpeed(speeds[Mathf.Clamp((int)index, 0, speeds.Length - 1)]);
        row.AddChild(options);
        parent.AddChild(row);

        var note = new Label
        {
            Text = $"Ring-spin cycle: {cycleDuration} at 1×; up to 100× for testing.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        note.Modulate = RingUiStyle.Muted;
        parent.AddChild(note);
    }

    private string GetRingDayDurationLabel()
    {
        const float fallbackSeconds = (float)CelestialConfiguration.DefaultRingDaySeconds;
        float seconds = fallbackSeconds;
        if (_dayNightCycle != null)
        {
            Variant value = _dayNightCycle.Get("FullCycleSeconds");
            if (value.VariantType is Variant.Type.Float or Variant.Type.Int && value.AsSingle() > 0f)
                seconds = value.AsSingle();
        }
        return $"{seconds / 60f:0.##} minutes";
    }

    private float GetDayNightSpeed()
    {
        if (_dayNightCycle == null) return 1.0f;
        Variant value = _dayNightCycle.Get("TimeScale");
        return value.VariantType is Variant.Type.Float or Variant.Type.Int
            ? Mathf.Clamp(value.AsSingle(), 1.0f, 100.0f)
            : 1.0f;
    }

    private void SetDayNightSpeed(float speed)
    {
        if (_dayNightCycle == null) return;
        _dayNightCycle.Call("set_speed_multiplier", Mathf.Clamp(speed, 1.0f, 100.0f));
    }

    private void BuildShadowsTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Shadows");
        BuildPersistentShadowControls(tab);
        AddTerrainCloudShadowControls(tab);
        VBoxContainer cloudShadows = TuningMenuUi.CreateSection(tab, "Cloud shadow state");
        AddPersistentToggle(cloudShadows, "CastCloudShadows", "Cloud shadows", _cloudBridge?.CastCloudShadows ?? true);
        AddPersistentSlider(cloudShadows, "ShadowStrength", "Cloud shadow strength", 0, 1, .01, _cloudBridge?.ShadowStrength ?? .98);
        AddPersistentToggle(cloudShadows, "PreferGpuCloudShadows", "GPU cascaded cloud shadows", _cloudBridge?.PreferGpuCloudShadows ?? true);
        AddToggle(cloudShadows, "Cloud shadow generation (profile)", _cloudBridge?.GpuShadowGenerationEnabled ?? true, value => { if (_cloudBridge != null) _cloudBridge.GpuShadowGenerationEnabled = value; });
        AddToggle(cloudShadows, "Cloud shadow receivers (profile)", _cloudBridge?.CloudShadowReceiversEnabled ?? true, value => { if (_cloudBridge != null) _cloudBridge.CloudShadowReceiversEnabled = value; });
        AddPersistentSlider(cloudShadows, "GpuShadowVerticalSamples", "GPU vertical samples", 1, 64, 1, _cloudBridge?.GpuShadowVerticalSamples ?? 4);
        AddToggle(cloudShadows, "LOD1 terrain shadows", _lod1?.CastTerrainShadows ?? true, value => { if (_lod1 != null) _lod1.CastTerrainShadows = value; });
        VBoxContainer gpuNear = TuningMenuUi.CreateSection(tab, "GPU near map");
        AddPersistentSlider(gpuNear, "GpuNearShadowMapResolution", "GPU near map", 64, 2048, 64, _cloudBridge?.GpuNearShadowMapResolution ?? 1024); AddPersistentSlider(gpuNear, "GpuNearShadowRadiusMeters", "GPU near radius", 8000, 80000, 1000, _cloudBridge?.GpuNearShadowRadiusMeters ?? 24000); AddPersistentSlider(gpuNear, "GpuNearShadowUpdatesPerSecond", "GPU near updates/sec", 1, 20, .5, _cloudBridge?.GpuNearShadowUpdatesPerSecond ?? 10);
        VBoxContainer gpuFar = TuningMenuUi.CreateSection(tab, "GPU far map");
        AddPersistentSlider(gpuFar, "GpuFarShadowMapResolution", "GPU far map", 64, 1024, 32, _cloudBridge?.GpuFarShadowMapResolution ?? 256); AddPersistentSlider(gpuFar, "GpuFarShadowRadiusMeters", "GPU far radius", 16000, 200000, 1000, _cloudBridge?.GpuFarShadowRadiusMeters ?? 96000); AddPersistentSlider(gpuFar, "GpuFarShadowUpdatesPerSecond", "GPU far updates/sec", .25, 5, .25, _cloudBridge?.GpuFarShadowUpdatesPerSecond ?? .5);
        VBoxContainer cpuFallback = TuningMenuUi.CreateSection(tab, "CPU fallback");
        AddPersistentSlider(cpuFallback, "ShadowRadiusMeters", "CPU fallback radius", 8000, 150000, 500, _cloudBridge?.ShadowRadiusMeters ?? 24000); AddPersistentSlider(cpuFallback, "ShadowMapResolution", "CPU fallback map", 16, 128, 8, _cloudBridge?.ShadowMapResolution ?? 64); AddPersistentSlider(cpuFallback, "ShadowRefreshSeconds", "CPU fallback refresh", .1, 15, .1, _cloudBridge?.ShadowRefreshSeconds ?? .5);
    }

    private void SpawnLightningStrike()
    {
        if (_weatherEffects == null)
        {
            GD.PushWarning("Lightning preview unavailable: LocalWeatherEffects was not found.");
            return;
        }

        SetOpen(false, immediate: false);
        GetTree().CreateTimer(0.35).Timeout += _weatherEffects.TriggerLightningPreview;
    }

    private void BuildCloudsTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Clouds");
        VBoxContainer appearance = TuningMenuUi.CreateSection(tab, "Appearance"); AddPersistentCloudControls(appearance);
        VBoxContainer handoff = TuningMenuUi.CreateSection(tab, "Handoff & altitudes");
        AddPersistentToggle(handoff, "CastCloudShadows", "Cloud shadows", _cloudBridge?.CastCloudShadows ?? true);
        AddPersistentToggle(handoff, "AutomaticCloudHandoff", "Automatic cloud handoff", _cloudBridge?.AutomaticCloudHandoff ?? true);
        AddPersistentSlider(handoff, "CloudHandoffOverlapMeters", "Minimum cloud overlap (m)", 2000, RingSunshineCloudBridge.MaximumCloudHandoffOverlapMeters, 500, _cloudBridge?.CloudHandoffOverlapMeters ?? 16000);
        AddPersistentSlider(handoff, "NearCloudRangeExtensionMeters", "Near range extension (m)", 0, RingSunshineCloudBridge.MaximumNearCloudRangeExtensionMeters, 500, _cloudBridge?.NearCloudRangeExtensionMeters ?? 8000);
        AddPersistentSlider(handoff, "HandoffStartMeters", "Near-to-far blend start", 5000, RingSunshineCloudBridge.MaximumHandoffStartMeters, 500, _cloudBridge?.HandoffStartMeters ?? 36000);
        AddPersistentSlider(handoff, "HandoffEndMeters", "Near-to-far blend end", 6000, RingSunshineCloudBridge.MaximumHandoffEndMeters, 500, _cloudBridge?.HandoffEndMeters ?? 48000);
        _effectiveCloudHandoffStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        handoff.AddChild(_effectiveCloudHandoffStatus);
        AddPersistentSlider(handoff, "NaturalCloudBaseAltitudeMeters", "Cloud base altitude", 0, 19000, 50, _cloudBridge?.NaturalCloudBaseAltitudeMeters ?? 2000); AddPersistentSlider(handoff, "NaturalCloudTopAltitudeMeters", "Cloud top altitude", 500, 20000, 50, _cloudBridge?.NaturalCloudTopAltitudeMeters ?? 6000); AddPersistentSlider(handoff, "StormFrontCloudBaseAltitudeMeters", "Storm-front base altitude", 0, 5000, 50, _cloudBridge?.StormFrontCloudBaseAltitudeMeters ?? 500);
        VBoxContainer distant = TuningMenuUi.CreateSection(tab, "Distant clouds");
        AddPersistentToggle(distant, "FarCloudPatchCullingEnabled", "Cull off-screen distant cloud patches", _cloudBridge?.FarCloudPatchCullingEnabled ?? true);
        AddPersistentToggle(distant, "FarCloudDetailEnabled", "Reuse Sunshine density", _cloudBridge?.FarCloudDetailEnabled ?? true); AddPersistentSlider(distant, "FarCloudDetailStrength", "Shared detail strength", 0, 1, .01, _cloudBridge?.FarCloudDetailStrength ?? 1); AddPersistentSlider(distant, "FarCloudOpacityMultiplier", "Distant opacity", 0, 2, .01, _cloudBridge?.FarCloudOpacityMultiplier ?? 1); AddPersistentSlider(distant, "FarCloudRadiusMeters", "Distant cloud radius", 30000, RingSunshineCloudBridge.MaximumFarCloudRadiusMeters, 5000, _cloudBridge?.FarCloudRadiusMeters ?? 180000); AddPersistentSlider(distant, "CloudPatchiness", "Coverage variation", 0, 1, .01, _cloudBridge?.CloudPatchiness ?? 1); AddPersistentSlider(distant, "CloudPatchScaleMeters", "Weather patch size", 16000, 256000, 1000, _cloudBridge?.CloudPatchScaleMeters ?? 64000); AddPersistentSlider(distant, "ClearSkyThreshold", "Clear-sky threshold", 0, .6, .01, _cloudBridge?.ClearSkyThreshold ?? .12);
        VBoxContainer storm = TuningMenuUi.CreateSection(tab, "Storm shaping"); AddPersistentToggle(storm, "SculptLocalizedStorms", "Sculpt local storms", _cloudBridge?.SculptLocalizedStorms ?? true); AddPersistentSlider(storm, "StormSculptStrength", "Storm sculpt strength", 0, 1, .01, _cloudBridge?.StormSculptStrength ?? .92); AddPersistentSlider(storm, "StormPillarRadiusMeters", "Storm pillar radius", 1000, 8000, 250, _cloudBridge?.StormPillarRadiusMeters ?? 4200); AddPersistentSlider(storm, "AnvilRadiusMultiplier", "Storm anvil spread", 1.2, 4, .1, _cloudBridge?.AnvilRadiusMultiplier ?? 2.6);
        VBoxContainer lighting = TuningMenuUi.CreateSection(tab, "Lighting & history"); AddPersistentColorSlider(lighting, "DayCloudAmbientColor", "Cloud ambient day", _cloudBridge?.DayCloudAmbientColor ?? Colors.White); AddPersistentColorSlider(lighting, "NightCloudAmbientColor", "Cloud ambient night", _cloudBridge?.NightCloudAmbientColor ?? Colors.White); AddPersistentSlider(lighting, "DitherSpeed", "Blue-noise motion", 0, 60, 1, _cloudBridge?.DitherSpeed ?? 0); AddPersistentSlider(lighting, "RayStartJitterStrength", "Ray-start jitter", 0, 1, .01, _cloudBridge?.RayStartJitterStrength ?? 1); AddPersistentSlider(lighting, "StationaryHistoryWeight", "Stationary history", 0, .95, .01, _cloudBridge?.StationaryHistoryWeight ?? .95); AddPersistentSlider(lighting, "MovingHistoryWeight", "Moving history", 0, .95, .01, _cloudBridge?.MovingHistoryWeight ?? .13); AddPersistentSlider(lighting, "ShadowExtinctionPerKilometer", "Shadow extinction / km", .05, 2, .05, _cloudBridge?.ShadowExtinctionPerKilometer ?? 1.4); AddPersistentSlider(lighting, "StormAmbientFloor", "Storm ambient floor", .02, .5, .01, _cloudBridge?.StormAmbientFloor ?? .14); AddPersistentSlider(lighting, "StormAmbientOcclusionStrength", "Storm ambient occlusion", 0, 1, .01, _cloudBridge?.StormAmbientOcclusionStrength ?? .86);
        AddCloudHistoryPolicyControls(lighting);
        VBoxContainer showcase = TuningMenuUi.CreateSection(tab, "Showcase"); AddCloudShowcaseControls(showcase);
        BuildRainHazeControls(tab);
        AddCloudDefaultsFooter(tab);
        AddCloudProviderControls(TuningMenuUi.CreateSection(tab, "Provider"));
    }

    private void BuildCloudQualityTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Quality");
        VBoxContainer raymarch = TuningMenuUi.CreateSection(tab, "Raymarch");
        AddPersistentToggle(raymarch, "SkipEmptyCloudMaskSamples", "Skip empty cloud mask samples", _cloudBridge?.SkipEmptyCloudMaskSamples ?? true);
        AddPersistentOption(raymarch, "CloudResolutionScale", "Resolution", new[] { "Native", "Half", "Quarter" }, _cloudBridge?.CloudResolutionScale ?? 0); AddPersistentSlider(raymarch, "RaymarchSteps", "View / raymarch steps", 64, RingSunshineCloudBridge.MaximumRaymarchSteps, 8, _cloudBridge?.RaymarchSteps ?? 128); AddPersistentSlider(raymarch, "LightingSteps", "Lighting steps", 1, RingSunshineCloudBridge.MaximumLightingSteps, 1, _cloudBridge?.LightingSteps ?? 8); AddPersistentSlider(raymarch, "MinStepDistanceMeters", "Minimum step", 10, RingSunshineCloudBridge.MaximumMinimumStepDistanceMeters, 1, _cloudBridge?.MinStepDistanceMeters ?? 90); AddPersistentSlider(raymarch, "MaxStepDistanceMeters", "Maximum step", 100, RingSunshineCloudBridge.MaximumStepDistanceMeters, 1, _cloudBridge?.MaxStepDistanceMeters ?? 560); AddPersistentSlider(raymarch, "LightingTravelKm", "Lighting travel", .5, RingSunshineCloudBridge.MaximumLightingTravelKm, .1, _cloudBridge?.LightingTravelKm ?? 7); AddPersistentSlider(raymarch, "CloudLodBias", "Cloud LOD bias", .1, RingSunshineCloudBridge.MaximumCloudLodBias, .01, _cloudBridge?.CloudLodBias ?? .9);
        VBoxContainer reconstruction = TuningMenuUi.CreateSection(tab, "Reconstruction");
        AddPersistentSlider(reconstruction, "BlurPower", "Upsample blur", 0, 6, .1, _cloudBridge?.BlurPower ?? .1); AddPersistentSlider(reconstruction, "BlurQuality", "Blur quality", 1, 4, 1, _cloudBridge?.BlurQuality ?? 1); AddPersistentToggle(reconstruction, "FastReconstruction", "Fast reconstruction", _cloudBridge?.FastReconstruction ?? false);
        BuildRainHazeControls(tab);
        AddCloudDefaultsFooter(tab);
    }

    private void AddPersistentCloudControls(VBoxContainer tab)
    {
        tab.AddChild(new Label { Text = "Gameplay appearance", Modulate = RingUiStyle.Muted });
        AddPersistentToggle(tab, "CloudRenderingEnabled", "Sunshine local rendering", _cloudBridge?.CloudRenderingEnabled ?? true);
        AddPersistentSlider(tab, "CloudCoverageBias", "Coverage bias", -1, 1, .01, _cloudBridge?.CloudCoverageBias ?? 0);
        AddPersistentSlider(tab, "CloudDensityMultiplier", "Density multiplier", 0, 3, .01, _cloudBridge?.CloudDensityMultiplier ?? 1);
        AddPersistentSlider(tab, "CloudWindMultiplier", "Cloud travel multiplier", 0, 3, .01, _cloudBridge?.CloudWindMultiplier ?? 1);
        AddPersistentSlider(tab, "CloudDirectLightMultiplier", "Direct light multiplier", 0, 2, .01, _cloudBridge?.CloudDirectLightMultiplier ?? 1);
        AddPersistentSlider(tab, "CloudAmbientMultiplier", "Ambient multiplier", 0, 2, .01, _cloudBridge?.CloudAmbientMultiplier ?? 1);
        AddPersistentSlider(tab, "CloudSharpness", "Cloud sharpness", 0, 1, .01, _cloudBridge?.CloudSharpness ?? .62);
    }

    private void AddPersistentSlider(VBoxContainer parent, string key, string label, double min, double max, double step, double initial)
    {
        parent.AddChild(new Label { Text = label }); var row = new HBoxContainer();
        _cloudControlKeys.Add(key);
        var slider = new HSlider { Name = key, MinValue = min, MaxValue = max, Step = step, Value = initial, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = CloudTooltip(key) };
        var value = new Label { Text = initial.ToString("0.###"), CustomMinimumSize = new Vector2(58, 0) };
        slider.ValueChanged += current => { value.Text = current.ToString("0.###"); SetCloudTuningValue(key, current); };
        row.AddChild(slider); row.AddChild(value); parent.AddChild(row);
    }
    private void AddPersistentToggle(VBoxContainer parent, string key, string label, bool initial)
    {
        _cloudControlKeys.Add(key);
        var toggle = new CheckButton { Name = key, Text = label, ButtonPressed = initial, TooltipText = CloudTooltip(key) };
        toggle.Toggled += value => SetCloudTuningValue(key, value); parent.AddChild(toggle);
    }
    private void AddPersistentColorSlider(VBoxContainer parent, string key, string label, Color initial)
    {
        _cloudControlKeys.Add(key); parent.AddChild(new Label { Text = label }); var row = new HBoxContainer();
        var slider = new HSlider { Name = key, MinValue = 0, MaxValue = 1, Step = .01, Value = initial.R, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Gameplay cloud ambient color override." };
        var value = new Label { Text = initial.R.ToString("0.###"), CustomMinimumSize = new Vector2(58, 0) };
        slider.ValueChanged += current => { value.Text = current.ToString("0.###"); SetCloudTuningValue(key, new Color((float)current, (float)current, (float)current, initial.A)); };
        row.AddChild(slider); row.AddChild(value); parent.AddChild(row);
    }
    private void AddPersistentOption(VBoxContainer parent, string key, string label, string[] labels, int selected)
    {
        var row = new HBoxContainer(); row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(150, 0) });
        _cloudControlKeys.Add(key);
        var option = new OptionButton { Name = key, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = CloudTooltip(key) };
        for (int i = 0; i < labels.Length; i++) option.AddItem(labels[i], i);
        option.Select(Mathf.Clamp(selected, 0, labels.Length - 1)); option.ItemSelected += index => SetCloudTuningValue(key, (int)index);
        row.AddChild(option); parent.AddChild(row);
    }
    private void AddCloudDefaultsFooter(VBoxContainer parent)
    {
        var row = new HBoxContainer();
        var save = new Button { Name = "CloudSaveNow", Text = "Save cloud defaults", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        save.Pressed += () => FlushCloudDefaults(); row.AddChild(save);
        var reset = new Button { Name = "CloudResetDefaults", Text = "Reset cloud overrides", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += () => _cloudResetConfirmation?.PopupCentered(); row.AddChild(reset); parent.AddChild(row);
        UpdateCloudSaveStatus();
    }

    /// <summary>Fixture-friendly cloud-only write entry point; non-whitelisted keys are rejected.</summary>
    public void SetCloudTuningValue(string property, Variant value)
    {
        if (!_cloudDefaults.SetOverride(_cloudBridge, property, value)) return;
        _cloudSaveDelay = .5; _cloudSaveRetryDelay = 0; RefreshCloudControls(); UpdateCloudSaveStatus();
    }
    public bool FlushCloudDefaults()
    {
        bool saved = _cloudDefaults.Save(); UpdateCloudSaveStatus(); return saved;
    }
    public Godot.Collections.Dictionary GetCloudTuningDiagnostics() => new()
    {
        ["path"] = _cloudDefaults.GetPath(), ["dirty"] = _cloudDefaults.Dirty,
        ["last_error"] = _cloudDefaults.LastError, ["override_count"] = _cloudDefaults.Settings.Overrides.Count
    };
    private void UpdateCloudSaveStatus()
    {
        if (_cloudSaveStatus != null) _cloudSaveStatus.Text = !string.IsNullOrEmpty(_cloudDefaults.LastError)
            ? "Cloud defaults unsaved (retrying): " + _cloudDefaults.LastError
            : _cloudDefaults.Dirty ? $"Gameplay cloud overrides pending: {_cloudDefaults.Settings.Overrides.Count}"
            : $"Gameplay cloud defaults saved: {_cloudDefaults.GetPath()}";
    }
    private void CreateCloudResetConfirmation()
    {
        _cloudResetConfirmation = new ConfirmationDialog { Name = "CloudResetConfirmation", Title = "Reset gameplay cloud overrides?", DialogText = "This removes the project-wide cloud overrides and restores the startup baseline." };
        _cloudResetConfirmation.Confirmed += () => { _cloudDefaults.Reset(_cloudBridge); _cloudSaveDelay = 0; RefreshCloudControls(); FlushCloudDefaults(); UpdateCloudSaveStatus(); };
        AddChild(_cloudResetConfirmation);
    }
    private static string CloudTooltip(string key) => key switch
    {
        "SkipEmptyCloudMaskSamples" => "Skips weather-texture reads outside the cloud layer while preserving ray positions, fog, and cloud history.",
        "TerrainCloudShadowEnabled" => "Uses terrain sunlight visibility to shade clouds behind ring terrain. Does not control cloud shadows cast onto terrain.",
        "TerrainCloudShadowStrength" => "Strength of terrain sunlight occlusion in clouds. Zero preserves unoccluded cloud lighting.",
        "TerrainCloudShadowGlobalAroundResolution" or "TerrainCloudShadowGlobalAcrossResolution" or "TerrainCloudShadowRegionalResolution" => "Sunlight visibility map resolution. Rounded up to a power of two; larger maps improve detail and increase work and memory.",
        "TerrainCloudShadowAltitudeSlices" => "Vertical sunlight visibility layers. More slices distinguish cloud heights at greater cost.",
        "TerrainCloudShadowRegionalExtentMeters" => "Span of the higher-detail terrain visibility map around the explorer.",
        "TerrainCloudShadowRefreshSeconds" => "Minimum interval between terrain visibility refreshes. Larger intervals reduce work but react more slowly.",
        "TerrainCloudShadowTraceVisitBudget" => "Maximum terrain hierarchy visits per texel. More visits improve difficult terrain traces at higher cost.",
        "TerrainCloudShadowTexelsPerFrame" => "Maximum visibility texels processed per frame. Larger budgets publish refreshed maps sooner with more frame work.",
        "TerrainCloudShadowSunValidityDegrees" => "Maximum sun-angle change before cached terrain visibility becomes stale. Smaller values demand fresher maps.",
        "DepthHistoryRejectionEnabled" => "Rejects cloud history whose opaque depth no longer matches the current view.",
        "HistoryDepthToleranceMultiplier" => "Scales depth-history acceptance tolerance. Lower values reject more aggressively; higher values retain more history.",
        "CloudHistoryCompatibilityEnabled" => "Rejects cloud history when cloud density or lighting is incompatible with the current sample.",
        "LightningHistoryIsolationEnabled" => "Keeps transient lightning illumination from persisting in normal cloud history.",
        "FarCloudPatchCullingEnabled" => "Conservatively hides distant cloud mesh patches outside their viewport's frustum. Visible patches keep the same density and shading.",
        "CloudResolutionScale" => "Native is sharpest and most expensive; Half and Quarter reduce cloud render cost.",
        "RaymarchSteps" => "More view steps improve detail but increase GPU raymarch cost.",
        "LightingSteps" => "More lighting samples improve scattering at additional GPU cost.",
        "MinStepDistanceMeters" or "MaxStepDistanceMeters" => "Raymarch step distance in metres. Larger steps cost less but can miss fine cloud detail.",
        "LightingTravelKm" => "Maximum lighting travel in kilometres. Longer travel improves deep scattering at higher cost.",
        "AutomaticCloudHandoff" => "Preserves the requested start and end range, then adapts it to the reachable near-cloud edge and the selected minimum overlap.",
        "CloudHandoffOverlapMeters" => "Minimum overlap in metres between near and distant clouds when automatic handoff is enabled.",
        "NearCloudRangeExtensionMeters" => "Additional near-cloud reach in metres used by automatic handoff, within the raymarch step budget.",
        "HandoffStartMeters" or "HandoffEndMeters" => "Requested near-to-far range. Automatic mode preserves this range but caps it by the near step budget and widens the minimum overlap; manual mode uses these exact distances.",
        "CloudPatchiness" => "Controls seeded, world-coherent coverage variation.",
        "CloudPatchScaleMeters" => "Size of each weather patch in metres.",
        "ClearSkyThreshold" => "Higher values clear more low-coverage background while storms retain clouds.",
        "FastReconstruction" => "Reduces reconstruction cost, with less stable fine detail.",
        "BlurPower" or "BlurQuality" => "Higher filtering can hide noise but costs more and softens detail.",
        "FarCloudDetailEnabled" => "Uses Sunshine's existing noise textures for distant curved cloud layers.",
        "FarCloudDetailStrength" => "Blends the lightweight distant shape toward Sunshine's background density.",
        "FarCloudOpacityMultiplier" => "Scales distant cloud alpha without changing near Sunshine clouds.",
        "FarCloudRadiusMeters" => "Outer radius of the curved distant canopy; larger values add visible geometry coverage.",
        _ => "Gameplay cloud override. Changes are saved separately from graphics presets."
    };
    private void RefreshCloudControls()
    {
        if (_drawer == null || _cloudBridge == null) return;
        RefreshCloudControlsRecursive(_drawer);
        RefreshEffectiveCloudHandoffStatus();
        UpdateCloudStatus();
        UpdateCloudSaveStatus();
    }

    private void RefreshEffectiveCloudHandoffStatus()
    {
        if (_effectiveCloudHandoffStatus == null || _cloudBridge == null) return;
        float effectiveStart = _cloudBridge.EffectiveHandoffStartMeters;
        float effectiveEnd = _cloudBridge.EffectiveHandoffEndMeters;
        float nearReach = _cloudBridge.NearCloudReachMeters;
        string mode = _cloudBridge.AutomaticCloudHandoff ? "Automatic" : "Manual";
        string providerState = _cloudBridge.RuntimeVisible ? "active" : "suspended";
        _effectiveCloudHandoffStatus.Text = $"Sunshine {mode.ToLowerInvariant()} blend: {effectiveStart / 1000.0f:0.0}-{effectiveEnd / 1000.0f:0.0} km | ray budget {nearReach / 1000.0f:0.0} km | {providerState}";
    }
    private void RefreshCloudControlsRecursive(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is not HSlider and not CheckButton and not OptionButton) { RefreshCloudControlsRecursive(child); continue; }
            string key = child.Name.ToString();
            if (!_cloudControlKeys.Contains(key)) { RefreshCloudControlsRecursive(child); continue; }
            Variant live = _cloudBridge!.Get(key);
            if (child is HSlider slider && live.VariantType is Variant.Type.Float or Variant.Type.Int)
            {
                slider.SetValueNoSignal(live.AsDouble());
                if (slider.GetParent() is HBoxContainer row && row.GetChildCount() > 1 && row.GetChild(1) is Label value)
                    value.Text = slider.Value.ToString("0.###");
            }
            else if (child is HSlider colorSlider && live.VariantType == Variant.Type.Color)
            {
                colorSlider.SetValueNoSignal(live.AsColor().R);
                if (colorSlider.GetParent() is HBoxContainer colorRow && colorRow.GetChildCount() > 1 && colorRow.GetChild(1) is Label colorValue)
                    colorValue.Text = colorSlider.Value.ToString("0.###");
            }
            else if (child is CheckButton toggle && live.VariantType == Variant.Type.Bool)
                toggle.SetPressedNoSignal(live.AsBool());
            else if (child is OptionButton option && key == "CloudResolutionScale" && live.VariantType is Variant.Type.Float or Variant.Type.Int)
                option.Select(Mathf.Clamp(live.AsInt32(), 0, option.ItemCount - 1));
            RefreshCloudControlsRecursive(child);
        }
    }

    private void AddCloudProviderControls(VBoxContainer tab)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = "Cloud provider", CustomMinimumSize = new Vector2(150, 0) });
        var options = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Disabled = _cloudProvider == null };
        options.AddItem("Sunshine Clouds 2");
        options.AddItem("Clouds for Little Planets");
        options.Select((int)(_cloudProvider?.SelectedProvider ?? global::RingworldTechDemo.RingCloudProviderController.CloudProvider.Sunshine));
        options.ItemSelected += index => _cloudProvider?.SetProvider((int)index);
        row.AddChild(options);
        tab.AddChild(row);

        var note = new Label
        {
            Text = "CLP is a cloud-only, flat-world prototype. Selecting it unloads Sunshine's compositor effect; switching back frees CLP's GPU resources.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = RingUiStyle.Muted
        };
        tab.AddChild(note);

        bool clpAvailable = _cloudProvider?.ClpAvailable == true;
        AddSlider(tab, "CLP resolution (0=native, 5=1/32)", 0, 5, 1, _cloudProvider?.ClpScaleDownPower ?? 2, value =>
        {
            if (_cloudProvider == null) return;
            _cloudProvider.ClpScaleDownPower = Mathf.RoundToInt(value); _cloudProvider.ApplyClpTuning();
        }, clpAvailable);
        AddSlider(tab, "CLP quality (0=low, 3=very high)", 0, 3, 1, _cloudProvider?.ClpQuality ?? 1, value =>
        {
            if (_cloudProvider == null) return;
            _cloudProvider.ClpQuality = Mathf.RoundToInt(value); _cloudProvider.ApplyClpTuning();
        }, clpAvailable);
        AddSlider(tab, "CLP density", 0.1, 4, 0.05, _cloudProvider?.ClpDensity ?? 0.9, value =>
        {
            if (_cloudProvider == null) return;
            _cloudProvider.ClpDensity = (float)value; _cloudProvider.ApplyClpTuning();
        }, clpAvailable);
        AddSlider(tab, "CLP coverage", 0.05, 1, 0.05, _cloudProvider?.ClpCoverage ?? 0.5, value =>
        {
            if (_cloudProvider == null) return;
            _cloudProvider.ClpCoverage = (float)value; _cloudProvider.ApplyClpTuning();
        }, clpAvailable);
        AddSlider(tab, "CLP provider travel", 0.05, 4, 0.05, _cloudProvider?.ClpWind ?? 1, value =>
        {
            if (_cloudProvider == null) return;
            _cloudProvider.ClpWind = (float)value; _cloudProvider.ApplyClpTuning();
        }, clpAvailable);
        AddSlider(tab, "CLP cloud base", 500, 12000, 100, _cloudProvider?.ClpCloudBaseMeters ?? 800, value =>
        {
            if (_cloudProvider == null) return;
            _cloudProvider.ClpCloudBaseMeters = (float)value; _cloudProvider.ApplyClpTuning();
        }, clpAvailable);
        AddSlider(tab, "CLP cloud top", 1000, 25000, 100, _cloudProvider?.ClpCloudTopMeters ?? 3200, value =>
        {
            if (_cloudProvider == null) return;
            _cloudProvider.ClpCloudTopMeters = (float)value; _cloudProvider.ApplyClpTuning();
        }, clpAvailable);
        AddSlider(tab, "CLP storm/anvil top", 1000, 25000, 100, _cloudProvider?.ClpStormCloudTopMeters ?? 14000, value =>
        {
            if (_cloudProvider == null) return;
            _cloudProvider.ClpStormCloudTopMeters = (float)value;
        }, clpAvailable);
        AddSlider(tab, "CLP storm intensity", 0, 1, 0.01, _cloudProvider?.ClpStormIntensity ?? 1, value =>
        {
            if (_cloudProvider != null) _cloudProvider.ClpStormIntensity = (float)value;
        }, clpAvailable);
        AddSlider(tab, "CLP storm coverage", 0.05, 1, 0.01, _cloudProvider?.ClpStormCoverage ?? 0.94, value =>
        {
            if (_cloudProvider != null) _cloudProvider.ClpStormCoverage = (float)value;
        }, clpAvailable);
        AddSlider(tab, "CLP storm density", 0.1, 4, 0.05, _cloudProvider?.ClpStormDensity ?? 2, value =>
        {
            if (_cloudProvider != null) _cloudProvider.ClpStormDensity = (float)value;
        }, clpAvailable);
        AddSlider(tab, "CLP shadow strength", 0, 1, 0.01, _cloudProvider?.ClpShadowStrength ?? 0.62, value =>
        {
            if (_cloudProvider == null) return;
            _cloudProvider.ClpShadowStrength = (float)value;
        }, clpAvailable);
        AddToggle(tab, "CLP terrain shadows", _cloudProvider?.ClpShadowsEnabled ?? true, value =>
        {
            if (_cloudProvider != null) _cloudProvider.ClpShadowsEnabled = value;
        });
        AddSlider(tab, "CLP shadow radius", 5000, 80000, 1000, _cloudProvider?.ClpShadowRadiusMeters ?? 30000, value =>
        {
            if (_cloudProvider == null) return;
            _cloudProvider.ClpShadowRadiusMeters = (float)value;
        }, clpAvailable);
        AddSlider(tab, "CLP front start distance", 1000, 80000, 1000, _cloudProvider?.ClpStormFrontDistanceMeters ?? 16000, value =>
        {
            if (_cloudProvider != null) _cloudProvider.ClpStormFrontDistanceMeters = (float)value;
        }, clpAvailable);
        AddSlider(tab, "CLP front half-width", 1000, 30000, 500, _cloudProvider?.ClpStormFrontHalfWidthMeters ?? 8000, value =>
        {
            if (_cloudProvider != null) _cloudProvider.ClpStormFrontHalfWidthMeters = (float)value;
        }, clpAvailable);
        AddSlider(tab, "CLP preview front speed", 1, 1000, 1, _cloudProvider?.ClpStormFrontSpeedMetersPerSecond ?? 250, value =>
        {
            if (_cloudProvider == null) return;
            _cloudProvider.ClpStormFrontSpeedMetersPerSecond = (float)value;
        }, clpAvailable);
        var storm = new Button { Text = "Start / clear CLP storm front", Disabled = !clpAvailable };
        storm.Pressed += () => _cloudProvider?.ToggleClpStormFront();
        tab.AddChild(storm);
        var stormNote = new Label
        {
            Text = "The front ramps CLP height/density at the viewer, drives rain and lightning, and moves a matching low-cost macro shadow across terrain. Set preview speed near 10-30 m/s for natural motion.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = RingUiStyle.Muted
        };
        tab.AddChild(stormNote);
    }

    private void BuildLodTab(TabContainer tabs)
    {
        VBoxContainer tabRoot = CreateTab(tabs, "LODs");
        VBoxContainer tab = TuningMenuUi.CreateSection(tabRoot, "Voxel streaming");
        bool legacyPresentationDisabled = _grid?.DormantEditableBackend ?? false;
        if (legacyPresentationDisabled)
            tab.AddChild(new Label { Text = "Legacy voxel streaming is disabled while active surface terrain owns presentation.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        AddToggle(tab, "Voxel streaming", _grid?.EnableChunkStreaming ?? false, value => { if (_grid != null) _grid.EnableChunkStreaming = value; }, !legacyPresentationDisabled);
        AddSlider(tab, "Streaming update", 0.01, 1, 0.01, _grid?.StreamingUpdateIntervalSeconds ?? 0.1, value => { if (_grid != null) _grid.StreamingUpdateIntervalSeconds = (float)value; }, !legacyPresentationDisabled);
        AddSlider(tab, "Voxel radius", 2, 16, 1, _grid?.StreamingVisualRadiusAround ?? 7, value =>
        {
            if (_grid == null) return;
            int radius = Mathf.RoundToInt(value); _grid.StreamingVisualRadiusAround = radius; _grid.StreamingVisualRadiusAcross = radius;
        }, !legacyPresentationDisabled);
        AddSlider(tab, "Voxel vertical radius", 1, 12, 1, _grid?.StreamingVisualRadiusVertical ?? 7, value => { if (_grid != null) _grid.StreamingVisualRadiusVertical = Mathf.RoundToInt(value); }, !legacyPresentationDisabled);
        AddSlider(tab, "Collision radius", 0, 16, 1, _grid?.StreamingCollisionRadiusAround ?? 1, value =>
        {
            if (_grid == null) return;
            int radius = Mathf.RoundToInt(value); _grid.StreamingCollisionRadiusAround = radius; _grid.StreamingCollisionRadiusAcross = radius;
        }, !legacyPresentationDisabled);
        AddToggle(tab, "Predictive streaming", _grid?.EnablePredictiveStreaming ?? true, value => { if (_grid != null) _grid.EnablePredictiveStreaming = value; }, !legacyPresentationDisabled);
        AddSlider(tab, "Prediction look-ahead", 0, 3, 0.05, _grid?.PredictionLookAheadSeconds ?? 0.5, value => { if (_grid != null) _grid.PredictionLookAheadSeconds = (float)value; }, !legacyPresentationDisabled);
        AddSlider(tab, "Mesh workers", 1, 8, 1, _grid?.MaxConcurrentVoxelMeshWorkers ?? 2, value => { if (_grid != null) _grid.MaxConcurrentVoxelMeshWorkers = Mathf.RoundToInt(value); }, !legacyPresentationDisabled);
        AddSlider(tab, "Voxel upload budget", 0.25, 8, 0.05, _grid?.MaxVoxelMeshUploadMillisecondsPerFrame ?? 1.5, value => { if (_grid != null) _grid.MaxVoxelMeshUploadMillisecondsPerFrame = (float)value; }, !legacyPresentationDisabled);
        VBoxContainer scheduling = TuningMenuUi.CreateSection(tabRoot, "Scheduling");
        AddSlider(scheduling, "LOD workers", 1, 8, 1, _coordinator?.MaxConcurrentWorkerBuilds ?? 2, value => { if (_coordinator != null) _coordinator.MaxConcurrentWorkerBuilds = Mathf.RoundToInt(value); });
        AddSlider(scheduling, "LOD uploads/frame", 1, 16, 1, _coordinator?.TotalSectorUploadsPerFrame ?? 3, value => { if (_coordinator != null) _coordinator.TotalSectorUploadsPerFrame = Mathf.RoundToInt(value); }); AddSlider(scheduling, "LOD upload budget", 0.25, 8, 0.05, _coordinator?.MaxUploadMillisecondsPerFrame ?? 2, value => { if (_coordinator != null) _coordinator.MaxUploadMillisecondsPerFrame = (float)value; });
        VBoxContainer handoff = TuningMenuUi.CreateSection(tabRoot, "LOD handoff"); AddSlider(handoff, "Terrain shadow coverage", 100, 50000, 100, _coordinator?.TerrainShadowCoverageMeters ?? 4000, value => { if (_coordinator != null) _coordinator.TerrainShadowCoverageMeters = (float)value; }); AddSlider(handoff, "LOD1 fade start", 32, 600, 8, _lod1?.FadeOutStartMeters ?? 240, value => { if (_lod1 != null) _lod1.FadeOutStartMeters = (float)value; }); AddSlider(handoff, "LOD1 fade end", 64, 800, 8, _lod1?.FadeOutEndMeters ?? 320, value => { if (_lod1 != null) _lod1.FadeOutEndMeters = (float)value; });
        VBoxContainer coverage = TuningMenuUi.CreateSection(tabRoot, "Proxy coverage culling");
        _partialCoverageMeshCullingToggle = new CheckButton
        {
            Name = "PartialCoverageMeshCulling",
            Text = "Cull triangles covered by finer terrain",
            ButtonPressed = _lodPauseMenu?.PartialCoverageMeshCullingEnabled ?? true,
            Disabled = _lodPauseMenu == null
        };
        _partialCoverageMeshCullingToggle.Toggled += value => _lodPauseMenu?.SetPartialCoverageMeshCulling(value);
        coverage.AddChild(_partialCoverageMeshCullingToggle);
        _partialCoverageMeshCullingStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        coverage.AddChild(_partialCoverageMeshCullingStatus);
        RefreshPartialCoverageMeshCullingStatus();

        VBoxContainer transitions = TuningMenuUi.CreateSection(tabRoot, "Terrain LOD transitions");
        _intermediateLodTiersToggle = new CheckButton
        {
            Name = "IntermediateLodTiers",
            Text = "Use intermediate terrain LOD tiers",
            ButtonPressed = _lodPauseMenu?.IntermediateLodTiersEnabled ?? true,
            TooltipText = "Adds bridge tiers between existing proxy levels and in the scaled far-terrain view. Existing tiers remain active.",
            Disabled = _lodPauseMenu == null
        };
        _intermediateLodTiersToggle.Toggled += value => _lodPauseMenu?.SetIntermediateLodTiers(value);
        transitions.AddChild(_intermediateLodTiersToggle);
        _intermediateLodTiersStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        transitions.AddChild(_intermediateLodTiersStatus);
        RefreshIntermediateLodTiersStatus();
    }

    private void RefreshPartialCoverageMeshCullingStatus()
    {
        if (_partialCoverageMeshCullingStatus == null) return;
        if (_lodPauseMenu == null)
        {
            _lodPauseMenu = GetNodeOrNull<RingLodPauseMenu>(LodPauseMenuPath);
            if (_lodPauseMenu != null && _partialCoverageMeshCullingToggle != null)
                _partialCoverageMeshCullingToggle.Disabled = false;
        }
        if (_lodPauseMenu == null)
        {
            _partialCoverageMeshCullingStatus.Text = "Proxy coverage culling is unavailable until the LOD pause menu initializes.";
            return;
        }
        if (_partialCoverageMeshCullingToggle != null)
            _partialCoverageMeshCullingToggle.SetPressedNoSignal(_lodPauseMenu.PartialCoverageMeshCullingEnabled);
        string saveError = _lodPauseMenu.PartialCoverageMeshCullingLastError;
        _partialCoverageMeshCullingStatus.Text = _lodPauseMenu.GetPartialCoverageMeshCullingStatus()
            + (saveError.Length == 0 ? "" : $"\nSave error: {saveError}");
    }

    private void RefreshIntermediateLodTiersStatus()
    {
        if (_intermediateLodTiersStatus == null) return;
        if (_lodPauseMenu == null)
        {
            _lodPauseMenu = GetNodeOrNull<RingLodPauseMenu>(LodPauseMenuPath);
        }
        if (_lodPauseMenu == null)
        {
            _intermediateLodTiersStatus.Text = "Intermediate terrain LOD tiers are unavailable until the LOD menu initializes.";
            return;
        }
        if (_intermediateLodTiersToggle != null)
        {
            _intermediateLodTiersToggle.Disabled = false;
            _intermediateLodTiersToggle.SetPressedNoSignal(_lodPauseMenu.IntermediateLodTiersEnabled);
        }
        string saveError = _lodPauseMenu.IntermediateLodTiersLastError;
        _intermediateLodTiersStatus.Text = $"Intermediate tiers {(_lodPauseMenu.IntermediateLodTiersEnabled ? "on" : "off")}."
            + (saveError.Length == 0 ? "" : $"\nSave error: {saveError}");
    }

    private void BuildTerrainTab(TabContainer tabs)
    {
        VBoxContainer tabRoot = CreateTab(tabs, "Terrain");
        VBoxContainer tab = TuningMenuUi.CreateSection(tabRoot, "Appearance");
        AddToggle(tab, "Smooth natural terrain", _grid?.UseSmoothNaturalTerrainPilot ?? false, value => { _grid?.SetSmoothNaturalTerrainEnabled(value); });
        AddToggle(tab, "Pilot terrain textures", _grid?.UseSmoothTerrainPilotTextures ?? true, value => { if (_grid != null) _grid.UseSmoothTerrainPilotTextures = value; });
        AddToggle(tab, "Fine terrain near shadows", _grid?.LimitSmoothTerrainShadowsToCollisionRange ?? true, value => { _grid?.SetSmoothTerrainNearShadowLimit(value); });
        AddSlider(tab, "Pilot texture scale", 0.01, 1, 0.01, _grid?.SmoothTerrainPilotTextureScale ?? 0.2, value => { if (_grid != null) _grid.SmoothTerrainPilotTextureScale = (float)value; });
        VBoxContainer editing = TuningMenuUi.CreateSection(tabRoot, "Editing / debug");
        AddSlider(editing, "Natural terrain brush radius", 0.5, 8, 0.25, _grid?.NaturalTerrainSdfBrushRadiusMeters ?? 1.5, value => { if (_grid != null) _grid.NaturalTerrainSdfBrushRadiusMeters = (float)value; });
        AddToggle(editing, "Excavation limits", _grid?.EnforceExcavationLimits ?? true, value => { if (_grid != null) _grid.EnforceExcavationLimits = value; }); AddToggle(editing, "Build height limit", _grid?.EnforceBuildHeightLimit ?? true, value => { if (_grid != null) _grid.EnforceBuildHeightLimit = value; }); AddToggle(editing, "Seam gap visualization", _grid?.EnableSeamGapDebugVisualization ?? false, value => { if (_grid != null) _grid.EnableSeamGapDebugVisualization = value; }); AddSlider(editing, "Seam debug interval", 0.02, 1, 0.01, _grid?.SeamGapDebugUpdateIntervalSeconds ?? 0.2, value => { if (_grid != null) _grid.SeamGapDebugUpdateIntervalSeconds = (float)value; });
        bool legacyPresentationDisabled = _grid?.DormantEditableBackend ?? false;
        VBoxContainer coarsePilot = TuningMenuUi.CreateSection(tabRoot, "Coarse pilot");
        if (legacyPresentationDisabled)
            coarsePilot.AddChild(new Label { Text = "Coarse SDF pilot is unavailable while active surface terrain is in use.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        AddToggle(coarsePilot, "Coarse SDF LOD1 pilot", _coarsePilot?.Enabled ?? false, value => { if (_coarsePilot != null) _coarsePilot.Enabled = value; }, !legacyPresentationDisabled); AddSlider(coarsePilot, "Coarse pilot radius", 0, 2, 1, _coarsePilot?.RadiusInCoarseChunks ?? 1, value => { if (_coarsePilot != null) _coarsePilot.RadiusInCoarseChunks = Mathf.RoundToInt(value); }, !legacyPresentationDisabled); AddToggle(coarsePilot, "Hide legacy LOD1", _coarsePilot?.HideLod1ProxyWhileEnabled ?? true, value => { if (_coarsePilot != null) _coarsePilot.HideLod1ProxyWhileEnabled = value; }, !legacyPresentationDisabled);
    }

    private static VBoxContainer CreateTab(TabContainer tabs, string name)
    {
        var scroll = new ScrollContainer { Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        tabs.AddChild(scroll);
        var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(column);
        return column;
    }

    private static void AddSlider(VBoxContainer parent, string label, double min, double max, double step, double initial, Action<double> apply, bool enabled = true)
    {
        parent.AddChild(new Label { Text = label });
        var row = new HBoxContainer();
        var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = initial, Editable = enabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var value = new Label { Text = initial.ToString("0.###"), CustomMinimumSize = new Vector2(58, 0) };
        slider.ValueChanged += current => { value.Text = current.ToString("0.###"); apply(current); };
        row.AddChild(slider); row.AddChild(value); parent.AddChild(row);
    }

    private static void AddToggle(VBoxContainer parent, string label, bool initial, Action<bool> apply, bool enabled = true)
    {
        var toggle = new CheckButton { Text = label, ButtonPressed = initial, Disabled = !enabled };
        toggle.Toggled += value => apply(value);
        parent.AddChild(toggle);
    }

    private void AddCloudShowcaseControls(VBoxContainer parent)
    {
        var cloudTypes = new OptionButton { TooltipText = "Cloud type for the physical-size preview" };
        foreach (global::RingworldTechDemo.CloudPhysicalDefinition cloud in global::RingworldTechDemo.CloudPhysicalCatalog.All)
        {
            float representativeWidth = MathF.Sqrt(Math.Max(1.0f, cloud.MinimumWidthMeters) * Math.Max(1.0f, cloud.MaximumWidthMeters));
            cloudTypes.AddItem($"{cloud.Abbreviation} - {cloud.Name} ({representativeWidth / 1000.0f:0.0} km)");
        }
        parent.AddChild(cloudTypes);

        var actions = new HBoxContainer();
        var spawnSelected = new Button { Text = "Spawn selected", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        spawnSelected.Pressed += () => SpawnCatalogCloud(cloudTypes.Selected);
        actions.AddChild(spawnSelected);
        var spawnNext = new Button { Text = "Spawn next", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        spawnNext.Pressed += () =>
        {
            SpawnCatalogCloud(cloudTypes.Selected);
            cloudTypes.Select((cloudTypes.Selected + 1) % cloudTypes.ItemCount);
        };
        actions.AddChild(spawnNext);
        parent.AddChild(actions);

        var spawnWeather = new HBoxContainer();
        var stormFront = new Button { Text = "Storm front", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        stormFront.Pressed += () => { _cloudBridge?.SpawnStormFrontAhead(); UpdateCloudStatus(); };
        spawnWeather.AddChild(stormFront);
        var anvilCluster = new Button { Text = "Anvil cluster", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        anvilCluster.Pressed += () => { _cloudBridge?.SpawnAnvilClusterAhead(); UpdateCloudStatus(); };
        spawnWeather.AddChild(anvilCluster);
        parent.AddChild(spawnWeather);

        var restore = new Button { Text = "Restore natural weather" };
        restore.Pressed += () => { _cloudBridge?.ClearSpawnedWeather(); _cloudBridge?.ClearCatalogCloudShowcase(); UpdateCloudStatus(); };
        parent.AddChild(restore);
        _cloudStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _cloudStatus.Modulate = RingUiStyle.Muted;
        parent.AddChild(_cloudStatus);
        UpdateCloudStatus();
    }

    private void SpawnCatalogCloud(int typeIndex)
    {
        _cloudBridge?.SpawnCatalogCloudAhead(typeIndex);
        UpdateCloudStatus();
    }

    private void UpdateCloudStatus()
    {
        if (_cloudStatus != null)
            _cloudStatus.Text = _cloudBridge == null
                ? "Cloud controls unavailable"
                : $"Live: {_cloudBridge.SpawnedWeatherLabel} | Catalog: {_cloudBridge.CloudShowcaseLabel}\n"
                    + $"History: requested {_cloudBridge.StationaryHistoryWeight:0.00}/{_cloudBridge.MovingHistoryWeight:0.00} "
                    + $"| motion {_cloudBridge.CameraHistoryMotion:0.00} "
                    + $"| effective {_cloudBridge.EffectiveHistoryWeight:0.00} "
                    + $"| applied {_cloudBridge.AppliedHistoryWeight:0.00}";
    }

    private static void DisableKeyboardFocus(Control control)
    {
        // Numeric water entries must accept typed values while the drawer owns
        // the mouse. Other tuning widgets retain the existing no-focus behavior.
        control.FocusMode = control is LineEdit && control.GetParent() is SpinBox
            ? Control.FocusModeEnum.All
            : Control.FocusModeEnum.None;
        foreach (Node child in control.GetChildren())
        {
            if (child is Control childControl)
                DisableKeyboardFocus(childControl);
        }
    }

    private void SetOpen(bool open, bool immediate)
    {
        if (_drawer == null && open) BuildDrawer();
        if (_drawer == null) return;
        if (_sunblockerDemo != null || _biomeAuthoringPreview != null || _generationPreview != null || _surfaceTerrainPilot != null)
        {
            _open = open;
            GetTree().Root.SetMeta(MouseOwnershipMeta, open);
            Input.MouseMode = _surfaceTerrainPilot != null && !open
                ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            float authoringWidth = Mathf.Max(DrawerWidth, _drawer.GetCombinedMinimumSize().X);
            _drawer.OffsetLeft = open ? -authoringWidth : 0;
            _drawer.OffsetRight = open ? 0 : authoringWidth;
            _drawer.Visible = open;
            return;
        }
        if (open) { _cloudRefreshDelay = 0; RefreshCloudControls(); RefreshWaterControls(); RefreshWindControls(); RefreshRainHazeControls(); RefreshGrassControls(); RebindAtmosphereDefaults(); RefreshAtmosphereControls(); RefreshSurfaceTerrainStatus(); RefreshPartialCoverageMeshCullingStatus(); RefreshIntermediateLodTiersStatus(); }
        else { FlushCloudDefaults(); FlushWaterDefaults(); FlushWindDefaults(); FlushRainHazeDefaults(); CommitQueuedGrassValues(); FlushGrassDefaults(); FlushAtmosphereDefaults(); FlushFarRenderingDefaults(); FlushLoadingPresentationDefaults(); }
        _open = open;
        GetTree().Root.SetMeta(MouseOwnershipMeta, open);
        Input.MouseMode = open ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
        // Theme margins and dynamic status text can make the real minimum a
        // little wider than DrawerWidth. Anchor from that resolved width so
        // the right edge stays on-screen instead of spilling past the viewport.
        float width = Mathf.Max(DrawerWidth, _drawer.GetCombinedMinimumSize().X);
        float targetLeft = open ? -width : 0;
        float targetRight = open ? 0 : width;
        _tween?.Kill();
        if (open) _drawer.Visible = true;
        if (immediate)
        {
            _drawer.OffsetLeft = targetLeft;
            _drawer.OffsetRight = targetRight;
            _drawer.Visible = open;
            return;
        }
        _tween = CreateTween();
        _tween.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _tween.TweenProperty(_drawer, "offset_left", targetLeft, 0.24);
        _tween.Parallel().TweenProperty(_drawer, "offset_right", targetRight, 0.24);
        if (!open)
            _tween.TweenCallback(Callable.From(() =>
            {
                if (!_open && _drawer != null)
                    _drawer.Visible = false;
            }));
    }
}
