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
/// </summary>
public partial class EclipseEventDirector : Node
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

    public override void _ExitTree() => Engine.EventRaised -= OnEngineEvent;

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
    }

    private void ResolveNodes()
    {
        if (_cycle == null || !GodotObject.IsInstanceValid(_cycle))
        {
            _cycle = GetNodeOrNull<RingDayNightCycle>(ClockPath);
            _clock = _cycle != null ? new DayNightEclipseClock(_cycle) : null;
        }
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
        _darknessOutput = _darkness.Update(Engine.Settings.Darkness, localCoverage, Engine.State.Level, Engine.Settings.KindleLevel, deltaSeconds);
    }

    /// <summary>Test/tool hook: runs the model without a lighting owner.</summary>
    public void ResetDarkness() { _darkness.Reset(); _darknessOutput = EclipseDarknessOutput.Neutral; }

    public string DescribeStatus() => Engine.DescribeStatus();
}
