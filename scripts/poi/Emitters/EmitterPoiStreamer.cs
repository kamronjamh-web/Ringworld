using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.Loading;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Streams the full emitter POI scenes near the player (docs/emitter-poi-streaming-spec.md section A): two regular slots and one
/// main slot, instantiated once at startup and kept in the tree. Assigning a slot only sets a transform, visibility and the
/// stand-in presented flag. A scene that fails to load degrades that kind to stand-ins without blocking world loading.
/// </summary>
public partial class EmitterPoiStreamer : Node3D
{
    public const string RegularScenePathDefault = "res://assets/poi/beam_emitter/BeamEmitterPOI.tscn";
    public const string MainScenePathDefault = "res://assets/poi/main_spire/MainSpirePOI.tscn";
    public const double DetailEndPaddingMeters = 500d, HandoffPaddingMeters = 500d;
    public const double DefaultRegularLoadMeters = 14_000d, DefaultMainLoadMeters = 32_000d, DefaultUnloadMarginMeters = 2_000d;
    public const double DefaultMinDwellSeconds = 1d, DefaultCollisionOnMeters = 1_500d, DefaultCollisionOffMarginMeters = 500d;

    private static readonly (SiteKind Kind, string Name, string Label)[] SlotPlan =
    {
        (SiteKind.Regular, "RegularSlot0", "R0"),
        (SiteKind.Regular, "RegularSlot1", "R1"),
        (SiteKind.Main, "MainSlot0", "M0")
    };

    private enum KindLoad { Pending, Loaded, Failed }

    private sealed class PoiSlot
    {
        public Node3D Root = null!;
        public Node? Collision;
        public MeshInstance3D? FarProxy;
        public SiteKind Kind;
        public string Label = "";
    }

    [Export] public bool Enabled { get => _enabled; set => SetEnabled(value); }
    [Export] public bool AutoBind { get; set; } = true;
    [Export] public bool UseStagedStartup { get; set; }
    [Export] public bool SynchronousLoad { get; set; }
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath StandInsPath { get; set; } = new("../EmitterStandIns");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");

    /// <summary>Scene paths; set before the pool starts loading (tests override these to force a load failure).</summary>
    public string RegularScenePath { get; set; } = RegularScenePathDefault;
    public string MainScenePath { get; set; } = MainScenePathDefault;

    private bool _enabled = true;
    private double _regularLoad = DefaultRegularLoadMeters, _mainLoad = DefaultMainLoadMeters, _unloadMargin = DefaultUnloadMarginMeters;
    private double _minDwell = DefaultMinDwellSeconds, _collisionOn = DefaultCollisionOnMeters, _collisionOffMargin = DefaultCollisionOffMarginMeters;
    private VoxelChunkGridManager? _grid;
    private DirectionalLight3D? _sunLight;
    private EmitterStandInLayer? _standIns;
    private EmitterSitePlacementSource? _placements;
    private object? _lastSamplerSeen;
    private EmitterPoiStreamerCore? _core;
    private RingThreadedResourceClaims? _claims;
    private bool _loadBegun, _farProxyShown = true, _subscribed;
    private readonly KindLoad[] _kindLoad = new KindLoad[2];
    private readonly PackedScene?[] _scenes = new PackedScene?[2];
    private readonly double[] _detailEnd = new double[2];
    private readonly string[] _failReason = { "", "" };
    private readonly List<PoiSlot> _slots = new();
    private readonly bool[] _planBuilt = new bool[SlotPlan.Length];
    private readonly List<EmitterPoiSlotEvent> _events = new();
    private EmitterPoiObservation[] _observations = Array.Empty<EmitterPoiObservation>();
    private Transform3D _lastGridGlobal, _lastSelfGlobal;
    private double _clock, _now;
    private int _nearestSite = -1;
    private double _nearestDistance = double.PositiveInfinity;

    public bool IsBound => _placements != null && _grid != null;
    public bool Unavailable { get; private set; }
    public bool PoolReady { get; private set; }
    public string StartupError { get; private set; } = "";
    public int StepCount { get; private set; }
    public int AnchorRefreshCount { get; private set; }
    public double LastAssignMs { get; private set; }
    public double LastCollisionToggleMs { get; private set; }
    public EmitterPoiStreamerCore? Core => _core;
    public int SlotCount => _slots.Count;
    public IReadOnlyList<SiteKind> FailedKinds
    {
        get
        {
            var failed = new List<SiteKind>();
            if (_kindLoad[(int)SiteKind.Regular] == KindLoad.Failed) failed.Add(SiteKind.Regular);
            if (_kindLoad[(int)SiteKind.Main] == KindLoad.Failed) failed.Add(SiteKind.Main);
            return failed;
        }
    }

    public bool StartupComplete => !_enabled || !UseStagedStartup || Unavailable || (PoolReady && StepCount > 0);

    public override void _Ready()
    {
        ProcessPhysicsPriority = -100;
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _standIns = GetNodeOrNull<EmitterStandInLayer>(StandInsPath);
        if (_grid == null)
        {
            // Missing grid is the only error; it is latched and does not block (Unavailable).
            StartupError = "Emitter POI streamer could not find its chunk grid manager.";
            Unavailable = true;
        }
        else if (AutoBind && _enabled && !SynchronousLoad) BeginLoad();
        if (!AutoBind || _grid == null) SetPhysicsProcess(false);
    }

    public override void _ExitTree()
    {
        if (_grid != null && _subscribed) { _grid.CanonicalFloatingOriginAnchorChanged -= HandleAnchorChanged; _subscribed = false; }
        if (_core != null && _standIns != null && GodotObject.IsInstanceValid(_standIns))
            foreach (EmitterPoiSlotState slot in _core.Slots)
                if (slot.Active) _standIns.SetSitePresented(slot.Site, false);
        _claims?.Dispose();
        _claims = null;
    }

    public override void _Process(double delta)
    {
        if (_grid != null && _enabled && PoolReady) TickPoiHaze(false);
    }

    /// <summary>Publishes the camera ray frame to the POI architecture materials (RingPoiHaze); forced after a floating-origin rebase.</summary>
    private void TickPoiHaze(bool force)
    {
        _sunLight ??= GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        RingPoiHaze.Tick(this, _grid, _sunLight, force);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_grid == null || !_enabled) return;
        _clock += delta;
        if (!IsBound)
        {
            if (!AutoBind) return;
            object? sampler = _grid.TerrainSampler;
            if (sampler == null || ReferenceEquals(sampler, _lastSamplerSeen)) return;
            _lastSamplerSeen = sampler;
            if (_grid.TerrainSampler?.EmitterSites is { } registry) Bind(_grid, new EmitterSitePlacementSource(registry), _standIns);
            else Unavailable = true;
            return;
        }
        if (!PoolReady) { AdvanceStartup(); return; }
        Camera3D? camera = GetViewport()?.GetCamera3D();
        RingCanonicalPosition observer = _grid.WorldToCanonicalRingPosition64(camera?.GlobalPosition ?? Vector3.Zero);
        Step(_clock, observer);
    }

    /// <summary>Attaches the placements and (optionally) the stand-in layer. Pool loading is driven by <see cref="AdvanceStartup"/>.</summary>
    public bool Bind(VoxelChunkGridManager grid, EmitterSitePlacementSource placements, EmitterStandInLayer? standIns = null)
    {
        if (IsBound) return true;
        _grid = grid;
        _placements = placements;
        if (standIns != null) _standIns = standIns;
        if (_observations.Length < placements.Count) _observations = new EmitterPoiObservation[placements.Count];
        Unavailable = false;
        StartupError = "";
        grid.CanonicalFloatingOriginAnchorChanged += HandleAnchorChanged;
        _subscribed = true;
        return true;
    }

    // ---- Startup: threaded load, one slot instantiated per call ---------------------------------------------------------

    private void BeginLoad()
    {
        if (_loadBegun) return;
        _loadBegun = true;
        if (SynchronousLoad)
        {
            LoadSync(SiteKind.Regular, RegularScenePath);
            LoadSync(SiteKind.Main, MainScenePath);
            return;
        }
        _claims = new RingThreadedResourceClaims();
        RequestKind(SiteKind.Regular, RegularScenePath);
        RequestKind(SiteKind.Main, MainScenePath);
    }

    private void LoadSync(SiteKind kind, string path)
    {
        PackedScene? scene = null;
        try { scene = ResourceLoader.Exists(path) ? ResourceLoader.Load<PackedScene>(path) : null; }
        catch (Exception error) { MarkFailed(kind, error.Message); return; }
        if (scene == null) MarkFailed(kind, "could not load " + path);
        else MarkLoaded(kind, scene);
    }

    private void RequestKind(SiteKind kind, string path)
    {
        Error error = _claims!.Request(path);
        if (error != Error.Ok) MarkFailed(kind, "load request for " + path + " returned " + error);
    }

    private void PollKind(SiteKind kind, string path)
    {
        if (_kindLoad[(int)kind] != KindLoad.Pending || _claims == null) return;
        var status = ResourceLoader.LoadThreadedGetStatus(path);
        switch (status)
        {
            case ResourceLoader.ThreadLoadStatus.Loaded:
            case ResourceLoader.ThreadLoadStatus.Failed:
                Resource? resource = null;
                try { resource = _claims.Take(path); } catch (Exception error) { MarkFailed(kind, error.Message); return; }
                if (resource is PackedScene scene) MarkLoaded(kind, scene);
                else MarkFailed(kind, "could not load " + path);
                break;
            case ResourceLoader.ThreadLoadStatus.InvalidResource:
                MarkFailed(kind, "invalid resource " + path);
                break;
        }
    }

    private void MarkLoaded(SiteKind kind, PackedScene scene)
    {
        _scenes[(int)kind] = scene;
        _kindLoad[(int)kind] = KindLoad.Loaded;
    }

    private void MarkFailed(SiteKind kind, string reason)
    {
        _kindLoad[(int)kind] = KindLoad.Failed;
        _failReason[(int)kind] = reason;
        GD.PushWarning($"EmitterPoiStreamer: {kind} POI scene unavailable ({reason}); using stand-ins only for that kind.");
    }

    /// <summary>Polls the loads and instantiates at most one slot. Returns <see cref="PoolReady"/>.</summary>
    public bool AdvanceStartup()
    {
        if (PoolReady || !_enabled || !IsBound) return PoolReady;
        BeginLoad();
        if (!SynchronousLoad)
        {
            PollKind(SiteKind.Regular, RegularScenePath);
            PollKind(SiteKind.Main, MainScenePath);
        }
        for (int plan = 0; plan < SlotPlan.Length; plan++)
        {
            if (_planBuilt[plan]) continue;
            SiteKind kind = SlotPlan[plan].Kind;
            if (_kindLoad[(int)kind] == KindLoad.Failed) { _planBuilt[plan] = true; continue; }
            if (_kindLoad[(int)kind] != KindLoad.Loaded) continue;
            BuildSlot(plan);
            return false;
        }
        foreach (bool built in _planBuilt) if (!built) return false;
        if (_kindLoad[0] == KindLoad.Pending || _kindLoad[1] == KindLoad.Pending) return false;
        FinishPool();
        return true;
    }

    private void BuildSlot(int plan)
    {
        (SiteKind kind, string name, string label) = SlotPlan[plan];
        _planBuilt[plan] = true;
        Node3D? root = _scenes[(int)kind]!.Instantiate() as Node3D;
        if (root == null) { MarkFailed(kind, "scene root is not a Node3D"); return; }
        root.Name = name;
        root.Visible = false;
        Node? collision = root.GetNodeOrNull("Collision");
        if (collision != null) collision.ProcessMode = ProcessModeEnum.Disabled;
        var farProxy = root.GetNodeOrNull<MeshInstance3D>("FarProxy");
        if (farProxy != null) farProxy.Visible = FarProxyShouldShow();
        if (root.GetNodeOrNull<Node3D>("PlaceholderBeam") is { } beam) beam.Visible = false;
        if (_detailEnd[(int)kind] <= 0d) _detailEnd[(int)kind] = ReadDetailVisibleEnd(root);
        AddChild(root);
        if (_grid != null) RingPoiHaze.RegisterTree(root, _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
        _slots.Add(new PoiSlot { Root = root, Collision = collision, FarProxy = farProxy, Kind = kind, Label = label });
    }

    /// <summary>Max of VisibilityRangeEnd + margin over the geometry under Detail; 0 when nothing has an end.</summary>
    private static double ReadDetailVisibleEnd(Node3D root)
    {
        double end = 0d;
        Node? detail = root.GetNodeOrNull("Detail");
        if (detail == null) return 0d;
        foreach (Node node in detail.FindChildren("*", "GeometryInstance3D", true, false))
            if (node is GeometryInstance3D geometry && geometry.VisibilityRangeEnd > 0f)
                end = Math.Max(end, geometry.VisibilityRangeEnd + geometry.VisibilityRangeEndMargin);
        return end;
    }

    private void FinishPool()
    {
        var kinds = new SiteKind[_slots.Count];
        for (int i = 0; i < kinds.Length; i++) kinds[i] = _slots[i].Kind;
        _core = new EmitterPoiStreamerCore(kinds, BuildConfig());
        PoolReady = true;
    }

    // ---- Radii and configuration ---------------------------------------------------------------------------------------

    private double HandoffFor(SiteKind kind) => kind == SiteKind.Main
        ? _standIns?.MainHandoffMeters ?? EmitterStandInLayer.DefaultMainHandoffMeters
        : _standIns?.RegularHandoffMeters ?? EmitterStandInLayer.DefaultRegularHandoffMeters;

    /// <summary>max(setting, detail visible end + 500, stand-in handoff + 500).</summary>
    public double EffectiveLoadRadius(SiteKind kind)
    {
        double setting = kind == SiteKind.Main ? _mainLoad : _regularLoad;
        return Math.Max(setting, Math.Max(_detailEnd[(int)kind] + DetailEndPaddingMeters, HandoffFor(kind) + HandoffPaddingMeters));
    }

    private EmitterPoiStreamerConfig BuildConfig() => new(_enabled, EffectiveLoadRadius(SiteKind.Regular), EffectiveLoadRadius(SiteKind.Main),
        _unloadMargin, _minDwell, _collisionOn, _collisionOffMargin);

    /// <summary>Applies the tuning values (radii are raised to the effective minimum when used). Disabling releases everything.</summary>
    public void Configure(bool enabled, double regularLoadM, double mainLoadM, double unloadMarginM, double minDwellS, double collisionOnM, double collisionOffMarginM)
    {
        static double Clean(double value, double fallback) => double.IsFinite(value) ? Math.Max(0d, value) : fallback;
        _regularLoad = Clean(regularLoadM, DefaultRegularLoadMeters);
        _mainLoad = Clean(mainLoadM, DefaultMainLoadMeters);
        _unloadMargin = Clean(unloadMarginM, DefaultUnloadMarginMeters);
        _minDwell = Clean(minDwellS, DefaultMinDwellSeconds);
        _collisionOn = Clean(collisionOnM, DefaultCollisionOnMeters);
        _collisionOffMargin = Clean(collisionOffMarginM, DefaultCollisionOffMarginMeters);
        if (_core != null) _core.Config = BuildConfig();
        Enabled = enabled;
    }

    private void SetEnabled(bool value)
    {
        if (_enabled == value) return;
        _enabled = value;
        if (_core == null) return;
        _core.Config = BuildConfig();
        if (value) return;
        _events.Clear();
        _core.ReleaseAll(_events);
        ApplyEvents();
    }

    // ---- Per-frame step --------------------------------------------------------------------------------------------------

    /// <summary>Evaluates all sites against <paramref name="observer"/> and applies the resulting slot events.</summary>
    public void Step(double now, in RingCanonicalPosition observer)
    {
        if (_placements == null || _grid == null || _core == null) return;
        _now = now;
        CheckRootShift();
        RefreshFarProxy();
        EmitterPoiStreamerCore core = _core;
        core.Config = BuildConfig();
        EmitterPoiStreamerConfig config = core.Config;
        double circumference = _placements.CircumferenceMeters;
        bool hasRegular = core.HasKind(SiteKind.Regular), hasMain = core.HasKind(SiteKind.Main);
        int count = 0, total = _placements.Count;
        _nearestSite = -1;
        _nearestDistance = double.PositiveInfinity;
        for (int site = 0; site < total; site++)
        {
            EmitterSitePlacement placement = _placements.GetPlacement(site);
            double du = EmitterPoiStreamerCore.SignedWrap(placement.AroundMeters - observer.AroundMeters, circumference);
            double dw = placement.AcrossMeters - observer.AcrossMeters;
            double distance = EmitterPoiStreamerCore.Metric(du, dw, placement.PadHeightMeters, observer.VerticalMeters, placement.Kind);
            if (distance < _nearestDistance) { _nearestDistance = distance; _nearestSite = site; }
            if (placement.Kind == SiteKind.Main ? !hasMain : !hasRegular) continue;
            if (distance >= config.UnloadFor(placement.Kind)) continue;
            _observations[count++] = new EmitterPoiObservation(site, placement.Kind, distance,
                EmitterPoiStreamerCore.GroundMetric(du, dw, placement.PadHeightMeters, observer.VerticalMeters));
        }
        _events.Clear();
        core.Update(now, new ReadOnlySpan<EmitterPoiObservation>(_observations, 0, count), _events);
        ApplyEvents();
        if (_standIns != null && GodotObject.IsInstanceValid(_standIns))
            foreach (EmitterPoiSlotState slot in core.Slots)
                if (slot.Active) _standIns.SetSitePresented(slot.Site, true);
        StepCount++;
    }

    private void ApplyEvents()
    {
        if (_events.Count == 0 || _core == null) return;
        RingLodGeometrySnapshot? snapshot = null;
        Transform3D worldToLayer = IsInsideTree() ? GlobalTransform.AffineInverse() : Transform3D.Identity;
        long assignTicks = 0, collisionTicks = 0;
        bool assigned = false, toggled = false;
        foreach (EmitterPoiSlotEvent slotEvent in _events)
        {
            PoiSlot slot = _slots[slotEvent.Slot];
            long started = Stopwatch.GetTimestamp();
            switch (slotEvent.Kind)
            {
                case EmitterPoiSlotEventKind.Assign:
                    snapshot ??= _grid!.CaptureLodGeometrySnapshot();
                    slot.Root.Transform = worldToLayer * _placements!.GetWorldTransform(snapshot.Value, slotEvent.Site);
                    slot.Root.Visible = true;
                    _standIns?.SetSitePresented(slotEvent.Site, true);
                    assignTicks += Stopwatch.GetTimestamp() - started;
                    assigned = true;
                    break;
                case EmitterPoiSlotEventKind.Release:
                    slot.Root.Visible = false;
                    if (slot.Collision != null) slot.Collision.ProcessMode = ProcessModeEnum.Disabled;
                    if (_standIns != null && GodotObject.IsInstanceValid(_standIns)) _standIns.SetSitePresented(slotEvent.Site, false);
                    break;
                case EmitterPoiSlotEventKind.CollisionOn:
                case EmitterPoiSlotEventKind.CollisionOff:
                    if (slot.Collision != null)
                        slot.Collision.ProcessMode = slotEvent.Kind == EmitterPoiSlotEventKind.CollisionOn ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
                    collisionTicks += Stopwatch.GetTimestamp() - started;
                    toggled = true;
                    break;
            }
        }
        if (assigned) LastAssignMs = Stopwatch.GetElapsedTime(0, assignTicks).TotalMilliseconds;
        if (toggled) LastCollisionToggleMs = Stopwatch.GetElapsedTime(0, collisionTicks).TotalMilliseconds;
        _events.Clear();
    }

    private bool FarProxyShouldShow() => !(_standIns != null && GodotObject.IsInstanceValid(_standIns) && _standIns.IsBound && _standIns.Enabled);

    private void RefreshFarProxy()
    {
        bool show = FarProxyShouldShow();
        if (show == _farProxyShown) return;
        _farProxyShown = show;
        foreach (PoiSlot slot in _slots)
            if (slot.FarProxy != null) slot.FarProxy.Visible = show;
    }

    // ---- Floating origin ---------------------------------------------------------------------------------------------------

    private void HandleAnchorChanged(RingLodGeometrySnapshot previous, RingLodGeometrySnapshot current, RingCanonicalPosition anchor)
    {
        _ = previous; _ = anchor;
        RefreshActiveTransforms(current);
        if (_grid != null && PoolReady) TickPoiHaze(true);
    }

    /// <summary>Recomputes every active slot transform absolutely from <paramref name="snapshot"/>; never applies a delta.</summary>
    private void RefreshActiveTransforms(in RingLodGeometrySnapshot snapshot)
    {
        if (_core == null || _placements == null) return;
        Transform3D worldToLayer = IsInsideTree() ? GlobalTransform.AffineInverse() : Transform3D.Identity;
        foreach (EmitterPoiSlotState slot in _core.Slots)
            if (slot.Active) _slots[slot.Index].Root.Transform = worldToLayer * _placements.GetWorldTransform(snapshot, slot.Site);
        if (_grid != null && _grid.IsInsideTree()) _lastGridGlobal = _grid.GlobalTransform;
        if (IsInsideTree()) _lastSelfGlobal = GlobalTransform;
        AnchorRefreshCount++;
    }

    private void CheckRootShift()
    {
        if (_grid == null || !_grid.IsInsideTree() || !IsInsideTree()) return;
        if (_lastGridGlobal == default && _lastSelfGlobal == default)
        {
            _lastGridGlobal = _grid.GlobalTransform;
            _lastSelfGlobal = GlobalTransform;
            return;
        }
        if (!_grid.GlobalTransform.IsEqualApprox(_lastGridGlobal) || !GlobalTransform.IsEqualApprox(_lastSelfGlobal))
            RefreshActiveTransforms(_grid.CaptureLodGeometrySnapshot());
    }

    // ---- Diagnostics ---------------------------------------------------------------------------------------------------------

    public IReadOnlyList<Node3D> Slots
    {
        get
        {
            var nodes = new List<Node3D>(_slots.Count);
            foreach (PoiSlot slot in _slots) nodes.Add(slot.Root);
            return nodes;
        }
    }

    public Node3D? GetSlotNode(int index) => index >= 0 && index < _slots.Count ? _slots[index].Root : null;

    public Node? GetSlotCollisionNode(int index) => index >= 0 && index < _slots.Count ? _slots[index].Collision : null;

    public MeshInstance3D? GetSlotFarProxy(int index) => index >= 0 && index < _slots.Count ? _slots[index].FarProxy : null;

    public SiteKind GetSlotKind(int index) => _slots[index].Kind;

    public int FindSlotForSite(int site) => _core?.FindSlotForSite(site) ?? -1;

    public bool IsSlotCollisionEnabled(int index)
        => index >= 0 && index < _slots.Count && _slots[index].Collision is { } collision && collision.ProcessMode != ProcessModeEnum.Disabled;

    public double DetailVisibleEnd(SiteKind kind) => _detailEnd[(int)kind];

    public string DescribeStatus()
    {
        if (_grid == null) return "Emitter POI streaming unavailable (no chunk grid).";
        if (!_enabled) return "Emitter POI streaming disabled.";
        if (Unavailable) return "Emitter POI streaming unavailable (needs the v5 sites world).";
        if (!IsBound) return "Emitter POI streaming waiting for the terrain sampler.";
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Pool {_slots.Count}/{SlotPlan.Length} {(PoolReady ? "ready" : "loading")}");
        foreach ((SiteKind kind, _, string label) in SlotPlan)
        {
            PoiSlot? slot = _slots.Find(s => s.Label == label);
            text.Append("; ").Append(label);
            if (_kindLoad[(int)kind] == KindLoad.Failed) { text.Append(" unavailable"); continue; }
            if (slot == null || _core == null) { text.Append(" loading"); continue; }
            EmitterPoiSlotState state = _core.Slots[_slots.IndexOf(slot)];
            if (!state.Active) { text.Append(" idle"); continue; }
            text.Append(CultureInfo.InvariantCulture, $" #{state.Site} {state.Distance / 1000d:0.0} km dwell {Math.Max(0d, _now - state.AssignedAt):0.0} s coll {(state.Collision ? "on" : "off")}");
        }
        if (_nearestSite >= 0) text.Append(CultureInfo.InvariantCulture, $"; nearest #{_nearestSite} {_nearestDistance / 1000d:0} km");
        foreach (SiteKind kind in FailedKinds) text.Append(CultureInfo.InvariantCulture, $"; {kind} POI failed to load ({_failReason[(int)kind]}), stand-ins only");
        return text.ToString();
    }

    public Godot.Collections.Dictionary GetLoadingStatus()
    {
        bool ready = StartupError.Length == 0 && StartupComplete;
        return new() { ["ready"] = ready, ["detail"] = ready ? "Emitter spires ready." : "Preparing the emitter spires.", ["error"] = StartupError };
    }
}
