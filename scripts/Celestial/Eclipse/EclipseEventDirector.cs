using System;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Celestial.Eclipse;

/// <summary>Adapter that presents <see cref="RingDayNightCycle"/> as an <see cref="IEclipseClock"/> (no changes to the clock itself).</summary>
public sealed class DayNightEclipseClock : IEclipseClock
{
    private readonly RingDayNightCycle _cycle;
    public DayNightEclipseClock(RingDayNightCycle cycle) => _cycle = cycle;
    public double SimulationSeconds => _cycle.GetSimulationSeconds();
    public float TimeScale => _cycle.TimeScale;
    public bool Paused { get => _cycle.Paused; set => _cycle.Paused = value; }
    public void SeekSimulationSeconds(double seconds) => _cycle.SetSimulationSeconds(seconds);
}

/// <summary>
/// Eclipse false-sun event director (docs/eclipse-false-sun-spec.md phase E1). Scene node "EclipseEvent", process priority -99: it runs after
/// <see cref="RingDayNightCycle"/> (-100) and before the lighting and layer consumers. It reads the celestial clock, the emitter grid layer's
/// topology and pulse clock, and the player pose, drives an <see cref="EclipseEventEngine"/>, and publishes <see cref="State"/>,
/// <see cref="EventRaised"/> and <see cref="DescribeStatus"/>. E1 changed no lighting or visuals. Phase E2 adds only the pitch-black eclipse darkness outputs (<see cref="DarknessOutput"/>, pulled each frame by day_side_atmosphere.gd through <see cref="UpdateDarkness"/>).
/// Phase E3 adds the "noon everywhere" hub lighting outputs (<see cref="HubLight"/>, read by day_side_atmosphere.gd and the cloud bridge) and
/// implements <see cref="IPrimaryLightOverride"/> so <see cref="RingDayNightCycle"/> aims StarLight at the local zenith while the hub sun is up.
/// </summary>
public partial class EclipseEventDirector : Node, IPrimaryLightOverride
{
    [Export] public NodePath ClockPath { get; set; } = new("../RingDayNightCycle");
    [Export] public NodePath GridLayerPath { get; set; } = new("../EmitterGrid");
    [Export] public NodePath ChunkGridPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath WeatherPath { get; set; } = new("../RingVolumetricClouds");
    /// <summary>Loads user://gameplay_eclipse_event_defaults.tres on ready when it exists (the drawer applies the same file later).</summary>
    [Export] public bool LoadSavedDefaults { get; set; } = true;
    /// <summary>When false the director never binds itself (tests drive <see cref="Engine"/> directly).</summary>
    [Export] public bool AutoBind { get; set; } = true;
    /// <summary>Debug, not saved: print every event to the console.</summary>
    public bool LogEvents { get; set; }

    private RingDayNightCycle? _cycle;
    private EmitterGridBeamLayer? _gridLayer;
    private VoxelChunkGridManager? _grid;
    private RingVoxelExplorerController? _explorer;
    private RingVolumetricCloudLayer? _weather;
    private DayNightEclipseClock? _clock;
    private CelestialConfiguration? _solverConfig;
    private EmitterGridTopology? _boundTopology;
    private EmitterGridPulseClock? _boundClock;
    private double _sessionWeather;

    public EclipseEventEngine Engine { get; } = new();
    public EclipseEventState State => Engine.State;
    public EclipseEventPhase Phase => Engine.Phase;
    public bool Bound => Engine.Bound;
    /// <summary>E2-E6 consumers subscribe here (spec F.1).</summary>
    public event Action<EclipseEvent>? EventRaised;
    /// <summary>Clock the debug actions use; null until the day/night node resolves.</summary>
    public IEclipseClock? Clock => _clock;
    public bool GridClockOverrideActive => Engine.GridClockOverrideActive;
    public double GridTimeSeconds => Engine.GridTimeSeconds;

    public override void _Ready()
    {
        ProcessPriority = -99;
        Engine.EventRaised += OnEngineEvent;
        if (LoadSavedDefaults)
        {
            var defaults = new GameplayEclipseEventDefaults();
            defaults.Load();
            if (defaults.HasSavedFile) Configure(defaults.Settings);
        }
        if (!AutoBind) SetProcess(false);
    }

    public override void _ExitTree()
    {
        Engine.EventRaised -= OnEngineEvent;
        if (_cycle != null && GodotObject.IsInstanceValid(_cycle) && ReferenceEquals(_cycle.PrimaryLightOverride, this)) _cycle.PrimaryLightOverride = null;
        EclipseHubLighting.Reset();
        // A re-added director (same instance) must re-register and start from the unprimed state.
        _hubWeight = 0d;
        _hubPrimed = false;
    }

    /// <summary>Applies saved choices (grave drawer, ready).</summary>
    public void Configure(GameplayEclipseEventSettings settings) => Engine.SetSettings(GameplayEclipseEventDefaults.ToCore(settings));

    private void OnEngineEvent(EclipseEvent e)
    {
        if (LogEvents) GD.Print($"[Eclipse] {e.Kind} phase={e.Phase} t={e.CelestialTime:0.0} site={e.Site} dist={e.DistanceKm:0.0}km value={e.Value:0.##}");
        EventRaised?.Invoke(e);
    }

    public override void _Process(double delta)
    {
        ResolveNodes();
        if (_cycle == null || _clock == null) return;
        _sessionWeather += delta;
        EnsureSolver();
        EnsureBound();
        double weather = _weather != null && GodotObject.IsInstanceValid(_weather) ? _weather.CloudSimulationTimeSeconds : _sessionWeather;
        Engine.Update(_clock, weather, ResolvePose());
        RefreshHubLight(delta);
        EclipseHubLighting.Tick();
        UpdateZenithGizmo();
    }

    private void ResolveNodes()
    {
        if (_cycle == null || !GodotObject.IsInstanceValid(_cycle))
        {
            _cycle = GetNodeOrNull<RingDayNightCycle>(ClockPath);
            _clock = _cycle != null ? new DayNightEclipseClock(_cycle) : null;
        }
        if (_cycle != null && !ReferenceEquals(_cycle.PrimaryLightOverride, this)) _cycle.PrimaryLightOverride = this;
        if (_gridLayer == null || !GodotObject.IsInstanceValid(_gridLayer)) _gridLayer = GetNodeOrNull<EmitterGridBeamLayer>(GridLayerPath);
        if (_grid == null || !GodotObject.IsInstanceValid(_grid)) _grid = GetNodeOrNull<VoxelChunkGridManager>(ChunkGridPath);
        if (_explorer == null || !GodotObject.IsInstanceValid(_explorer)) _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        if (_weather == null || !GodotObject.IsInstanceValid(_weather)) _weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherPath);
    }

    /// <summary>Builds the window solver from the clock's effective configuration, the authored epoch sun and the ring axis (spec A.3).</summary>
    private void EnsureSolver()
    {
        CelestialConfiguration? config = _cycle?.Configuration;
        if (_cycle == null || config == null || _grid == null) return;
        if (ReferenceEquals(config, _solverConfig) && Engine.Solver != null) return;
        double radiusKm = _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau / 1000d;
        if (!(radiusKm > 1d)) return;
        Vector3 axis = _grid.GlobalBasis.Y;
        _solverConfig = config;
        Engine.SetSolver(new EclipseWindowSolver(config, _cycle.AuthoredEpochSun, axis, radiusKm));
    }

    /// <summary>Binds the engine to the grid layer's topology and pulse clock; rebinds when the layer rebuilds its clock (settings change).</summary>
    private void EnsureBound()
    {
        if (_gridLayer == null || !_gridLayer.IsBound || _gridLayer.Topology == null || _gridLayer.Clock == null)
        {
            if (_boundTopology != null) { _boundTopology = null; _boundClock = null; Engine.Bind(null, null, 0L); }
            return;
        }
        if (ReferenceEquals(_boundTopology, _gridLayer.Topology) && ReferenceEquals(_boundClock, _gridLayer.Clock)) return;
        EmitterGridTopology topology = _gridLayer.Topology;
        if (!ReferenceEquals(_boundTopology, topology)) _siteTable = EclipseSiteTable.FromTopology(topology);
        _boundTopology = topology; _boundClock = _gridLayer.Clock;
        Engine.Bind(_siteTable, _boundClock, topology.Registry.Recipe.Seed);
    }

    private EclipseSiteTable? _siteTable;

    private EclipseObserverPose ResolvePose()
    {
        if (_explorer == null || _grid == null) return EclipseObserverPose.None;
        var p = _explorer.CanonicalPosition;
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        double radius = circumference / Math.Tau;
        if (!(radius > 1d)) return EclipseObserverPose.None;
        double radialKm = (radius - (_grid.RingVerticalBaseOffsetMeters + p.VerticalMeters)) / 1000d;
        double acrossKm = (_grid.RingAcrossBaseOffsetMeters + p.AcrossMeters) / 1000d;
        return new EclipseObserverPose(p.AroundMeters, p.AcrossMeters, p.AroundMeters / radius, acrossKm, radialKm, true);
    }

    // ---------------------------------------------------------------- debug actions (spec G.3); each is a real clock jump

    public bool ForceEclipseNow() => _clock != null && Engine.ForceEclipseNow(_clock);
    public bool JumpToPhase(EclipseEventPhase phase) => _clock != null && Engine.JumpToPhase(_clock, phase);
    public bool ScrubToTau(double tau) => _clock != null && Engine.ScrubToTau(_clock, tau);
    public void SetFrozen(bool frozen) { if (_clock != null) EclipseEventEngine.SetFrozen(_clock, frozen); }
    public bool Frozen => _clock?.Paused == true;

    // ---------------------------------------------------------------- pitch-black eclipse (spec D, phase E2)

    private readonly EclipseDarknessModel _darkness = new();
    private EclipseDarknessOutput _darknessOutput = EclipseDarknessOutput.Neutral;

    /// <summary>Latest darkness multipliers (pure model, spec D.1). Neutral (all 1, star floor 0) outside an eclipse.</summary>
    public EclipseDarknessOutput DarknessOutput => _darknessOutput;
    public float DarknessWeight => (float)_darknessOutput.Weight;
    public float DarknessAmbientMultiplier => (float)_darknessOutput.AmbientMultiplier;
    public float DarknessAtmosphereNightMultiplier => (float)_darknessOutput.AtmosphereNightMultiplier;
    public float DarknessSkyNightMultiplier => (float)_darknessOutput.SkyNightMultiplier;
    public float DarknessCloudAmbientMultiplier => (float)_darknessOutput.CloudAmbientMultiplier;
    public float DarknessStarFloor => (float)_darknessOutput.StarFloor;

    /// <summary>
    /// Called once per frame by the lighting owner (<c>day_side_atmosphere.gd</c>) with the raw local eclipse coverage it already uses for the
    /// published solar visibility (so the two agree), and the real frame delta. The hub level comes from <see cref="State"/>.
    /// </summary>
    public void UpdateDarkness(double localCoverage, double deltaSeconds)
    {
        // The hub light relieves the darkness from the kindle (D.1); the level includes the debug override.
        _darknessOutput = _darkness.Update(Engine.Settings.Darkness, localCoverage, _hubLight.Level, Engine.Settings.KindleLevel, deltaSeconds, EclipseDarknessModel.HubReliefAvailable);
    }

    /// <summary>Test/tool hook: runs the model without a lighting owner.</summary>
    public void ResetDarkness() { _darkness.Reset(); _darknessOutput = EclipseDarknessOutput.Neutral; }

    // ---------------------------------------------------------------- hub "noon everywhere" lighting (spec C, phase E3)

    /// <summary>The hub weight reaches about 95 % of a step in this many seconds (scrubs and loads into the event).</summary>
    public const double HubWeightSmoothSeconds = 1d;

    private EclipseHubLightOutput _hubLight = EclipseHubLightOutput.Neutral;
    private double _hubLevelOverride = -1d;
    private double _hubWeight;
    private bool _hubPrimed;
    private bool _hubOverrideChanged;

    /// <summary>Latest hub lighting outputs (pure model). Exactly neutral (all 0) outside the event.</summary>
    public EclipseHubLightOutput HubLight => _hubLight;
    /// <summary>Smoothed share of the primary light's direction that follows the local zenith (shader global <c>ringworld_hub_light_weight</c>).</summary>
    public float HubLightWeight => (float)_hubWeight;
    public float HubLightDay => (float)_hubLight.Day;
    public float HubLightDirect => (float)_hubLight.DirectEnergy;
    public float HubLightLevel => (float)_hubLight.Level;
    public float HubSkyTintStrength => (float)_hubLight.SkyTintStrength;
    public float HubZenithHaloIntensity => (float)_hubLight.ZenithHaloIntensity;
    public Color HubLightColor => new((float)_hubLight.ColorR, (float)_hubLight.ColorG, (float)_hubLight.ColorB);
    /// <summary>Debug hub level override, -1 when off (not saved).</summary>
    public float HubLevelOverride => (float)_hubLevelOverride;
    public bool HubLevelOverrideActive => _hubLevelOverride >= 0d;
    float IPrimaryLightOverride.PrimaryLightWeight => HubLightWeight;

    /// <summary>
    /// Debug (spec G.3, not saved): forces hub weight 1 and the given level (0..2.5) for lighting captures, outside an eclipse too. A negative
    /// value clears it. Visual only: the event timeline and the clock are untouched. Outside an eclipse the hub then shows alongside the real sun.
    /// </summary>
    public void SetHubLevelOverride(float level)
    {
        double next = float.IsFinite(level) && level >= 0f ? Math.Min(level, EclipseHubLightModel.MaxOverrideLevel) : -1d;
        if (next == _hubLevelOverride) return;
        _hubLevelOverride = next;
        _hubOverrideChanged = true;
        RefreshHubLight(0d);
    }

    /// <summary>Test/tool hook: re-evaluates the hub lighting from the current state without advancing time.</summary>
    public void RefreshHubLight(double deltaSeconds)
    {
        _hubLight = EclipseHubLightModel.Evaluate(Engine.Settings, Engine.State, _hubLevelOverride);
        double target = _hubLight.Weight;
        // The weight eases over about 1 s so a scrub or a load into the event does not snap the light direction. It snaps on the first
        // frame and for the debug override (captures want the final state at once).
        if (!_hubPrimed || _hubOverrideChanged || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0d) { _hubWeight = target; _hubPrimed = true; }
        else
        {
            // Ease in celestial time: at a high TimeScale the natural S/E steps must not lag the compressed timeline.
            double timeScale = _cycle != null && GodotObject.IsInstanceValid(_cycle) ? Math.Max(1d, _cycle.TimeScale) : 1d;
            _hubWeight += (target - _hubWeight) * (1d - Math.Exp(-deltaSeconds * timeScale / (HubWeightSmoothSeconds / 3d)));
            if (Math.Abs(_hubWeight - target) < 1e-4) _hubWeight = target;
        }
        _hubOverrideChanged = false;
        EclipseHubLighting.Weight = (float)_hubWeight;
    }

    // ---------------------------------------------------------------- local zenith gizmo (debug, spec G.3)

    private MeshInstance3D? _gizmo;
    private ImmediateMesh? _gizmoMesh;
    /// <summary>Debug, not saved: draws the player's local zenith (green) and the StarLight direction (yellow) in front of the camera.</summary>
    public bool ShowZenithGizmo { get; set; }

    private void UpdateZenithGizmo()
    {
        if (!ShowZenithGizmo)
        {
            if (_gizmo != null && GodotObject.IsInstanceValid(_gizmo)) _gizmo.Visible = false;
            return;
        }
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera == null || _explorer == null || _grid == null) return;
        if (_gizmo == null || !GodotObject.IsInstanceValid(_gizmo))
        {
            _gizmoMesh = new ImmediateMesh();
            _gizmo = new MeshInstance3D
            {
                Name = "EclipseZenithGizmo", Mesh = _gizmoMesh, TopLevel = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, NoDepthTest = true, DisableFog = true
                }
            };
            AddChild(_gizmo);
        }
        _gizmo.Visible = true;
        Vector3 zenith = (_grid.GlobalBasis.Orthonormalized() * _explorer.GetCanonicalSurfaceUp()).Normalized();
        Vector3 origin = camera.GlobalPosition + camera.GlobalBasis * new Vector3(0f, -0.4f, -2f);
        var green = new Color(0.2f, 1f, 0.3f);
        var yellow = new Color(1f, 0.9f, 0.2f);
        _gizmoMesh!.ClearSurfaces();
        _gizmoMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        _gizmoMesh.SurfaceSetColor(green); _gizmoMesh.SurfaceAddVertex(origin);
        _gizmoMesh.SurfaceSetColor(green); _gizmoMesh.SurfaceAddVertex(origin + zenith * 3f);
        DirectionalLight3D? star = GetNodeOrNull<DirectionalLight3D>("../StarLight");
        if (star != null)
        {
            Vector3 toLight = star.GlobalBasis.Z.Normalized();
            _gizmoMesh.SurfaceSetColor(yellow); _gizmoMesh.SurfaceAddVertex(origin);
            _gizmoMesh.SurfaceSetColor(yellow); _gizmoMesh.SurfaceAddVertex(origin + toLight * 2.5f);
        }
        _gizmoMesh.SurfaceEnd();
    }

    public string DescribeStatus() => Engine.DescribeStatus();
}
