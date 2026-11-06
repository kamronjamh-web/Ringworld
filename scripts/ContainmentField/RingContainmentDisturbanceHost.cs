using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.ContainmentField;

/// <summary>
/// The scene node that runs the containment field's data side (docs/containment-field-design.md 4.3): it installs the real field provider
/// (<see cref="ContainmentFieldProvider"/>: <c>MegastormFieldCoupling.FieldUndersideProvider</c> and <c>CeilingFromUnderside</c>, replacing the megastorm stub), owns the
/// <see cref="ContainmentDisturbanceBus"/> and feeds it every frame, on the weather clock, from the real layers: the shared lightning list (lightning, field strikes,
/// sprites and jets), the render weather systems (storm contact) and the megastorm model (contact band, bursts, far flashes). The ripple patches (WO3/WO4) read
/// <see cref="Bus"/>. Contact arcs (<c>ActiveContactArcs</c>) are never read here: they publish nothing.
/// <para>It re-installs the provider when the world's width, wall top or circumference change (test worlds configure them after the scene loads) and removes it on exit.</para>
/// </summary>
public partial class RingContainmentDisturbanceHost : Node
{
    [Export] public bool Enabled { get; set; } = true;
    [Export] public NodePath WeatherPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath MegastormPath { get; set; } = new("../RingMegastorm");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath EmitterFieldShotsPath { get; set; } = new("../EmitterFieldShots");

    public static RingContainmentDisturbanceHost? Instance { get; private set; }
    /// <summary>The live bus of the running game (null before the world is configured). The ripple patches of WO3/WO4 read it.</summary>
    public static ContainmentDisturbanceBus? SharedBus => Instance?.Bus;

    public ContainmentDisturbanceBus? Bus { get; private set; }
    public ContainmentFieldProfile? Profile { get; private set; }
    /// <summary>Test seam: replaces the observer (default: the explorer).</summary>
    public Func<RingCanonicalPosition?>? ObserverProvider { get; set; }

    private RingVolumetricCloudLayer? _weather;
    private RingMegastorm? _megastorm;
    private VoxelChunkGridManager? _grid;
    private RingVoxelExplorerController? _explorer;
    private LightningDisturbanceAdapter? _lightning;
    private UpperAtmosphereDisturbanceAdapter? _upper;
    private StormContactAdapter? _contact;
    private MegastormDisturbanceAdapter? _megastormAdapter;
    private EmitterShotDisturbanceAdapter? _emitterAdapter;
    private BodyContactDisturbanceAdapter? _bodyAdapter;
    private RingContainmentFieldAudio? _fieldAudio;
    private EmitterFieldShotLayer? _emitterShots;
    private readonly List<RingVolumetricCloudLayer.RenderWeatherSystem> _stormScratch = new();
    private (double Width, double WallTop, double Circumference) _built;

    public LightningDisturbanceAdapter? LightningAdapter => _lightning;
    public UpperAtmosphereDisturbanceAdapter? UpperAdapter => _upper;
    public StormContactAdapter? ContactAdapter => _contact;
    public MegastormDisturbanceAdapter? MegastormAdapter => _megastormAdapter;
    public EmitterShotDisturbanceAdapter? EmitterShotAdapter => _emitterAdapter;
    /// <summary>Contact ripples (decision 4): register fast bodies here (<see cref="BodyContactDisturbanceAdapter.Register"/>); the player is always tracked.</summary>
    public BodyContactDisturbanceAdapter? BodyContactAdapter => _bodyAdapter;
    public RingContainmentFieldAudio? FieldAudio => _fieldAudio;

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        ContainmentFieldProvider.Uninstall();
        Bus = null; Profile = null;
    }

    public override void _Ready()
    {
        ProcessPriority = 100;   // after the weather layer has updated its lightning list for this frame
        _weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherPath);
        _megastorm = GetNodeOrNull<RingMegastorm>(MegastormPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _emitterShots = GetNodeOrNull<EmitterFieldShotLayer>(EmitterFieldShotsPath);
        _fieldAudio = new RingContainmentFieldAudio { Name = "FieldAudio" };
        AddChild(_fieldAudio);
        EnsureInstalled();
    }

    public override void _Process(double delta)
    {
        if (!Enabled) return;
        EnsureInstalled();
        if (Bus == null || _weather == null) return;
        RingCanonicalPosition? observer = ObserverProvider != null ? ObserverProvider() : _explorer?.CanonicalPosition;
        if (observer == null) return;
        double now = _weather.CloudSimulationTimeSeconds;
        Bus.Observer = observer;
        Bus.Advance(now);
        if (_weather.EnableSharedLightning)
        {
            _lightning!.Update(_weather.ActiveLightningEvents);
            _upper!.Update(_weather.ActiveLightningEvents);
        }
        _contact!.Update(now, observer.Value);
        _megastormAdapter!.Update(now, observer.Value);
        _emitterAdapter!.Update(now, observer.Value);
        UpdateBodyContact(now, delta);
    }

    /// <summary>The camera's per-frame segment against the field (contact ripple and whump) and its side of the field (vacuum audio).</summary>
    private void UpdateBodyContact(double now, double delta)
    {
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (_grid == null || camera == null || Profile is not { } profile) return;
        RingCanonicalPosition cam = _grid.WorldToCanonicalRingPosition64(camera.GlobalPosition);
        RingLodGeometrySnapshot snapshot = _grid.CaptureLodGeometrySnapshot();
        _bodyAdapter!.Update(now, delta, snapshot, cam);
        _fieldAudio?.UpdateVacuum(profile, cam, delta);
        _fieldAudio?.UpdateHum(profile, snapshot, cam, delta);
    }

    /// <summary>Builds (or rebuilds after a world change) the profile, provider, bus and adapters; a no-op while the world is unconfigured.</summary>
    private void EnsureInstalled()
    {
        if (_grid == null || _weather == null) return;
        double width = _grid.RingAcrossWidthMeters, wallTop = _grid.GetStructuralWallTopGlobalY(), circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        if (Bus != null && _built == (width, wallTop, circumference)) return;
        RimWallDescription wall = RimWallDescription.Current;
        var profile = new ContainmentFieldProfile(width, wallTop, ContainmentFieldProfile.DefaultApexMeters, wall.RootAcross(0, width), wall.RootAcross(1, width),
            ContainmentFieldProfile.DefaultSkirtHeightMeters);
        if (!profile.IsValid || circumference <= 1.0) return;
        _built = (width, wallTop, circumference);
        Profile = profile;
        ContainmentFieldProvider.Install(profile);
        if (Bus == null)
        {
            Bus = new ContainmentDisturbanceBus(profile, circumference);
            _lightning = new LightningDisturbanceAdapter(Bus, profile, ResolveStorm);
            _upper = new UpperAtmosphereDisturbanceAdapter(Bus, profile, () => _weather!.CloudTopMeters);
            _contact = new StormContactAdapter(Bus, profile, CollectSystems, () => _weather!.WeatherWindMetersPerSecond, circumference);
            _megastormAdapter = new MegastormDisturbanceAdapter(() => _megastorm?.Model, Bus, profile);
            _bodyAdapter = new BodyContactDisturbanceAdapter(Bus, profile, circumference);
            _bodyAdapter.Crossed += c => { if (c.IsPlayer) _fieldAudio?.PlayWhump(c.Energy, _bodyAdapter!.Crossings); };
            _emitterAdapter = new EmitterShotDisturbanceAdapter(Bus, () => _emitterShots != null && IsInstanceValid(_emitterShots) ? _emitterShots.Schedule : null, circumference);
        }
        else
        {
            Bus.Clear();
            Bus.Profile = profile; Bus.CircumferenceMeters = circumference;
            _lightning!.Profile = profile; _upper!.Profile = profile; _contact!.Profile = profile; _megastormAdapter!.Profile = profile;
            _emitterAdapter!.CircumferenceMeters = circumference;
            _bodyAdapter!.Profile = profile; _bodyAdapter.CircumferenceMeters = circumference; _bodyAdapter.Reset();
        }
    }

    private int CollectSystems(RingCanonicalPosition center, double radiusMeters, double time, List<RingVolumetricCloudLayer.RenderWeatherSystem> output)
        => _weather!.CollectRenderWeatherSystems(center, radiusMeters, time, output, 0.0f, 500.0f, includeMegastorm: false);

    /// <summary>
    /// The parent storm of an event: the megastorm's current top for kind 6, else the top (StormCeilingMeters) of the render system with the event's SystemId at the
    /// event's position, falling back to the global cloud top (docs 4.2). Called only for events that could couple (near a rim), so the lookup is rare.
    /// </summary>
    private LightningStormInfo? ResolveStorm(RingLightningEvent e)
    {
        if (e.SystemKind == MegastormModel.KindMegastorm)
        {
            MegastormModel? model = _megastorm?.Model;
            if (model == null || !model.Triggered) return null;
            return new LightningStormInfo(model.Evaluate(e.StartTimeSeconds).TopMeters, (float)MegastormLightning.FlashRadiusMeters);
        }
        _stormScratch.Clear();
        _weather!.CollectRenderWeatherSystems(e.GroundPosition, 0.0, e.StartTimeSeconds, _stormScratch, 0.0f, 500.0f, includeMegastorm: false);
        foreach (RingVolumetricCloudLayer.RenderWeatherSystem s in _stormScratch)
        {
            if (s.SystemId != e.SystemId) continue;
            float top = s.StormCeilingMeters > s.StormFloorMeters + 1.0f ? s.StormCeilingMeters : _weather.CloudTopMeters;
            return new LightningStormInfo(top, s.MajorRadiusMeters);
        }
        return new LightningStormInfo(_weather.CloudTopMeters, 0.0f);
    }
}
