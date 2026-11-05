using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Loading;
using RingworldTechDemo.Shadows;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Streams the full atmosphere-shield projector tower scene near the player (docs/containment-field-design.md 6.1, 6.2; the emitter streamer pattern,
/// <see cref="EmitterPoiStreamer"/>): two slots, instantiated once at startup and kept in the tree. Assigning a slot only sets a transform, visibility and the
/// stand-in presented flag. A scene that fails to load latches <see cref="Unavailable"/> and leaves the stand-ins drawing; world loading is never gated.
/// </summary>
public partial class AtmosphereProjectorStreamer : Node3D
{
    public const string ScenePathDefault = "res://assets/poi/atmosphere_projector/AtmosphereProjectorPOI.tscn";
    public const int SlotCount = 2;
    public const double DetailEndPaddingMeters = 500d, HandoffPaddingMeters = 500d;
    public const double DefaultLoadMeters = 16_000d, DefaultUnloadMarginMeters = 2_000d, DefaultMinDwellSeconds = 1d;

    private sealed class PoiSlot
    {
        public Node3D Root = null!;
        public MeshInstance3D? FarProxy;
        public string Label = "";
        public Aabb LocalBounds;
        public bool HasBounds;
        public RingLargePoiShadowExtents.BoundsProvider? ShadowExtent;
        public bool ShadowExtentRegistered;
    }

    [Export] public bool Enabled { get => _enabled; set => SetEnabled(value); }
    [Export] public bool AutoBind { get; set; } = true;
    [Export] public bool SynchronousLoad { get; set; }
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath StandInsPath { get; set; } = new("../AtmosphereProjectorStandIns");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");

    /// <summary>Scene path; set before the pool starts loading (tests override it to force a load failure).</summary>
    public string ScenePath { get; set; } = ScenePathDefault;

    private bool _enabled = true;
    private double _load = DefaultLoadMeters, _unloadMargin = DefaultUnloadMarginMeters, _minDwell = DefaultMinDwellSeconds;
    private VoxelChunkGridManager? _grid;
    private DirectionalLight3D? _sunLight;
    private AtmosphereProjectorStandInLayer? _standIns;
    private AtmosphereProjectorSites? _sites;
    private EmitterPoiStreamerCore? _core;
    private RingThreadedResourceClaims? _claims;
    private PackedScene? _scene;
    private bool _loadBegun, _loadPending, _farProxyShown = true, _subscribed;
    private string _failReason = "";
    private double _detailEnd;
    private readonly List<PoiSlot> _slots = new();
    private readonly List<EmitterPoiSlotEvent> _events = new();
    private EmitterPoiObservation[] _observations = Array.Empty<EmitterPoiObservation>();
    private Transform3D _lastGridGlobal, _lastSelfGlobal;
    private double _clock, _now;
    private int _nearestSite = -1;
    private double _nearestDistance = double.PositiveInfinity;

    public bool IsBound => _sites != null && _grid != null;
    public bool Unavailable { get; private set; }
    public bool PoolReady { get; private set; }
    public string StartupError { get; private set; } = "";
    public int StepCount { get; private set; }
    public int AnchorRefreshCount { get; private set; }
    public double LastAssignMs { get; private set; }
    public EmitterPoiStreamerCore? Core => _core;
    public AtmosphereProjectorSites? Sites => _sites;
    public double DetailVisibleEnd => _detailEnd;

    public override void _Ready()
    {
        ProcessPhysicsPriority = -100;
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _standIns = GetNodeOrNull<AtmosphereProjectorStandInLayer>(StandInsPath);
        if (_grid == null)
        {
            StartupError = "Projector streamer could not find its chunk grid manager.";
            Unavailable = true;
        }
        else if (AutoBind && _enabled && !SynchronousLoad) BeginLoad();
        if (!AutoBind || _grid == null) SetPhysicsProcess(false);
    }

    public override void _ExitTree()
    {
        if (_grid != null && _subscribed) { _grid.CanonicalFloatingOriginAnchorChanged -= HandleAnchorChanged; _subscribed = false; }
        foreach (PoiSlot slot in _slots) SetShadowExtent(slot, false);
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
            if (AutoBind && GridReady(_grid)) Bind(_grid, _standIns);
            return;
        }
        if (!PoolReady) { AdvanceStartup(); return; }
        Camera3D? camera = GetViewport()?.GetCamera3D();
        RingCanonicalPosition observer = _grid.WorldToCanonicalRingPosition64(camera?.GlobalPosition ?? Vector3.Zero);
        Step(_clock, observer);
    }

    private static bool GridReady(VoxelChunkGridManager grid) => grid.GetCanonicalRingCircumferenceMeters64() > 0d && grid.RingAcrossWidthMeters > 0f;

    /// <summary>Builds the sites for the grid's geometry and (optionally) attaches the stand-in layer. Pool loading is driven by <see cref="AdvanceStartup"/>.</summary>
    public bool Bind(VoxelChunkGridManager grid, AtmosphereProjectorStandInLayer? standIns = null)
    {
        if (IsBound) return true;
        if (!GridReady(grid)) return false;
        _grid = grid;
        if (standIns != null) _standIns = standIns;
        _sites = AtmosphereProjectorSites.Build(grid.CaptureLodGeometrySnapshot(), grid.RingAcrossWidthMeters, RimWallDescription.Current);
        if (_observations.Length < _sites.Count) _observations = new EmitterPoiObservation[_sites.Count];
        if (!_subscribed) { grid.CanonicalFloatingOriginAnchorChanged += HandleAnchorChanged; _subscribed = true; }
        return true;
    }

    // ---- Startup: threaded load, one slot instantiated per call ---------------------------------------------------------

    private void BeginLoad()
    {
        if (_loadBegun) return;
        _loadBegun = true;
        if (SynchronousLoad)
        {
            try { _scene = ResourceLoader.Exists(ScenePath) ? ResourceLoader.Load<PackedScene>(ScenePath) : null; }
            catch (Exception error) { MarkFailed(error.Message); return; }
            if (_scene == null) MarkFailed("could not load " + ScenePath);
            return;
        }
        _claims = new RingThreadedResourceClaims();
        Error error2 = _claims.Request(ScenePath);
        if (error2 != Error.Ok) MarkFailed("load request for " + ScenePath + " returned " + error2);
        else _loadPending = true;
    }

    private void PollLoad()
    {
        if (!_loadPending || _claims == null) return;
        switch (ResourceLoader.LoadThreadedGetStatus(ScenePath))
        {
            case ResourceLoader.ThreadLoadStatus.Loaded:
            case ResourceLoader.ThreadLoadStatus.Failed:
                _loadPending = false;
                Resource? resource = null;
                try { resource = _claims.Take(ScenePath); } catch (Exception error) { MarkFailed(error.Message); return; }
                if (resource is PackedScene scene) _scene = scene;
                else MarkFailed("could not load " + ScenePath);
                break;
            case ResourceLoader.ThreadLoadStatus.InvalidResource:
                _loadPending = false;
                MarkFailed("invalid resource " + ScenePath);
                break;
        }
    }

    private void MarkFailed(string reason)
    {
        _loadPending = false;
        _failReason = reason;
        Unavailable = true;
        GD.PushWarning($"AtmosphereProjectorStreamer: projector POI scene unavailable ({reason}); using stand-ins only.");
    }

    /// <summary>Polls the load and instantiates at most one slot. Returns <see cref="PoolReady"/>.</summary>
    public bool AdvanceStartup()
    {
        if (PoolReady || !_enabled || !IsBound || Unavailable) return PoolReady;
        BeginLoad();
        if (!SynchronousLoad) PollLoad();
        if (Unavailable || _scene == null) return false;
        if (_slots.Count < SlotCount) { BuildSlot(_slots.Count); return false; }
        FinishPool();
        return true;
    }

    private void BuildSlot(int index)
    {
        Node3D? root = _scene!.Instantiate() as Node3D;
        if (root == null) { MarkFailed("scene root is not a Node3D"); return; }
        root.Name = "ProjectorSlot" + index;
        root.Visible = false;
        if (root.GetNodeOrNull("Collision") is { } collision) collision.ProcessMode = ProcessModeEnum.Disabled;
        var farProxy = root.GetNodeOrNull<MeshInstance3D>("FarProxy");
        if (farProxy != null) farProxy.Visible = FarProxyShouldShow();
        if (_detailEnd <= 0d) _detailEnd = ReadDetailVisibleEnd(root);
        AddChild(root);
        if (_grid != null) RingPoiHaze.RegisterTree(root, _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
        PoiBaseAmbientOcclusion.Apply(root, root, PoiBaseAmbientOcclusion.MeasureHeight(root));
        var poiSlot = new PoiSlot { Root = root, FarProxy = farProxy, Label = "P" + index };
        poiSlot.HasBounds = TryMeasureDetailBounds(root, out poiSlot.LocalBounds);
        poiSlot.ShadowExtent = (out Aabb world) => TryGetShadowExtentBounds(poiSlot, out world);
        _slots.Add(poiSlot);
    }

    // ---- Sun shadow map extension (RingLargePoiShadowExtents, same pattern as the emitter streamer) -------------------------

    private static bool TryMeasureDetailBounds(Node3D root, out Aabb local)
    {
        local = default;
        Node? detail = root.GetNodeOrNull("Detail");
        if (detail == null || !root.IsInsideTree()) return false;
        Transform3D toRoot = root.GlobalTransform.AffineInverse();
        bool any = false;
        foreach (Node node in detail.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (node is not MeshInstance3D { Mesh: not null } mesh) continue;
            Aabb box = (toRoot * mesh.GlobalTransform) * mesh.GetAabb();
            local = any ? local.Merge(box) : box;
            any = true;
        }
        return any;
    }

    private static bool TryGetShadowExtentBounds(PoiSlot slot, out Aabb world)
    {
        world = default;
        if (!slot.HasBounds || !GodotObject.IsInstanceValid(slot.Root) || !slot.Root.IsInsideTree() || !slot.Root.Visible) return false;
        world = slot.Root.GlobalTransform * slot.LocalBounds;
        return true;
    }

    private static void SetShadowExtent(PoiSlot slot, bool mounted)
    {
        if (slot.ShadowExtent == null || slot.ShadowExtentRegistered == mounted) return;
        if (mounted) { if (!slot.HasBounds) return; RingLargePoiShadowExtents.Register(slot.ShadowExtent); }
        else RingLargePoiShadowExtents.Unregister(slot.ShadowExtent);
        slot.ShadowExtentRegistered = mounted;
    }

    /// <summary>Max of VisibilityRangeEnd + margin over the geometry under Detail; 0 when nothing has an end.</summary>
    public static double ReadDetailVisibleEnd(Node3D root)
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
        for (int i = 0; i < kinds.Length; i++) kinds[i] = SiteKind.Regular;
        _core = new EmitterPoiStreamerCore(kinds, BuildConfig());
        PoolReady = true;
    }

    // ---- Radii and configuration ---------------------------------------------------------------------------------------

    /// <summary>max(setting, detail visible end + 500, stand-in handoff + 500).</summary>
    public double EffectiveLoadRadius
        => ComputeEffectiveLoadRadius(_load, _detailEnd, _standIns?.HandoffMeters ?? AtmosphereProjectorStandInLayer.DefaultHandoffMeters);

    public static double ComputeEffectiveLoadRadius(double load, double detailVisibleEnd, double handoff)
        => Math.Max(load, Math.Max(detailVisibleEnd + DetailEndPaddingMeters, handoff + HandoffPaddingMeters));

    private EmitterPoiStreamerConfig BuildConfig()
        => new(_enabled, EffectiveLoadRadius, EffectiveLoadRadius, _unloadMargin, _minDwell, 0d, 0d);

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

    /// <summary>
    /// Observations for every site closer than <paramref name="unloadRadius"/> (the mid-height metric, <see cref="AtmosphereProjectorSites.Distance"/>, and the
    /// wall-top ground distance). Returns the count written to <paramref name="destination"/> (at least <c>sites.Count</c> long).
    /// </summary>
    public static int BuildObservations(AtmosphereProjectorSites sites, in RingCanonicalPosition observer, double unloadRadius, Span<EmitterPoiObservation> destination)
        => BuildObservations(sites, observer, unloadRadius, destination, out _, out _);

    public static int BuildObservations(AtmosphereProjectorSites sites, in RingCanonicalPosition observer, double unloadRadius, Span<EmitterPoiObservation> destination,
        out int nearestSite, out double nearestDistance)
    {
        int count = 0;
        nearestSite = -1;
        nearestDistance = double.PositiveInfinity;
        for (int site = 0; site < sites.Count; site++)
        {
            double distance = sites.Distance(site, observer);
            if (distance < nearestDistance) { nearestDistance = distance; nearestSite = site; }
            if (distance >= unloadRadius) continue;
            destination[count++] = new EmitterPoiObservation(site, SiteKind.Regular, distance, sites.GroundDistance(site, observer));
        }
        return count;
    }

    /// <summary>Evaluates all sites against <paramref name="observer"/> and applies the resulting slot events.</summary>
    public void Step(double now, in RingCanonicalPosition observer)
    {
        if (_sites == null || _grid == null || _core == null) return;
        _now = now;
        EnsureSites();
        CheckRootShift();
        RefreshFarProxy();
        EmitterPoiStreamerCore core = _core;
        core.Config = BuildConfig();
        int count = BuildObservations(_sites, observer, core.Config.UnloadFor(SiteKind.Regular), _observations, out _nearestSite, out _nearestDistance);
        _events.Clear();
        core.Update(now, new ReadOnlySpan<EmitterPoiObservation>(_observations, 0, count), _events);
        ApplyEvents();
        if (_standIns != null && GodotObject.IsInstanceValid(_standIns))
            foreach (EmitterPoiSlotState slot in core.Slots)
                if (slot.Active) _standIns.SetSitePresented(slot.Site, true);
        StepCount++;
    }

    /// <summary>Rebuilds the sites when the grid's circumference, width or wall top changed (all slots are released first).</summary>
    private void EnsureSites()
    {
        if (_grid == null || _sites == null) return;
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64(), width = _grid.RingAcrossWidthMeters, wallTop = _grid.GetStructuralWallTopGlobalY();
        if (_sites.Matches(circumference, width, wallTop, RimWallDescription.Current)) return;
        if (_core != null)
        {
            _events.Clear();
            _core.ReleaseAll(_events);
            ApplyEvents();
        }
        _sites = AtmosphereProjectorSites.Build(_grid.CaptureLodGeometrySnapshot(), width, RimWallDescription.Current);
        if (_observations.Length < _sites.Count) _observations = new EmitterPoiObservation[_sites.Count];
    }

    private void ApplyEvents()
    {
        if (_events.Count == 0 || _core == null) return;
        RingLodGeometrySnapshot? snapshot = null;
        Transform3D worldToLayer = IsInsideTree() ? GlobalTransform.AffineInverse() : Transform3D.Identity;
        long assignTicks = 0;
        bool assigned = false;
        foreach (EmitterPoiSlotEvent slotEvent in _events)
        {
            PoiSlot slot = _slots[slotEvent.Slot];
            long started = Stopwatch.GetTimestamp();
            switch (slotEvent.Kind)
            {
                case EmitterPoiSlotEventKind.Assign:
                    snapshot ??= _grid!.CaptureLodGeometrySnapshot();
                    slot.Root.Transform = worldToLayer * _sites!.GetWorldTransform(snapshot.Value, slotEvent.Site);
                    slot.Root.Visible = true;
                    SetShadowExtent(slot, true);
                    _standIns?.SetSitePresented(slotEvent.Site, true);
                    assignTicks += Stopwatch.GetTimestamp() - started;
                    assigned = true;
                    break;
                case EmitterPoiSlotEventKind.Release:
                    slot.Root.Visible = false;
                    SetShadowExtent(slot, false);
                    if (_standIns != null && GodotObject.IsInstanceValid(_standIns)) _standIns.SetSitePresented(slotEvent.Site, false);
                    break;
                // CollisionOn / CollisionOff: the projector scene has no Collision node (the wall-top walk owns the footprint).
            }
        }
        if (assigned) LastAssignMs = Stopwatch.GetElapsedTime(0, assignTicks).TotalMilliseconds;
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
        if (_core == null || _sites == null) return;
        Transform3D worldToLayer = IsInsideTree() ? GlobalTransform.AffineInverse() : Transform3D.Identity;
        foreach (EmitterPoiSlotState slot in _core.Slots)
            if (slot.Active) _slots[slot.Index].Root.Transform = worldToLayer * _sites.GetWorldTransform(snapshot, slot.Site);
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

    public Node3D? GetSlotNode(int index) => index >= 0 && index < _slots.Count ? _slots[index].Root : null;

    public int FindSlotForSite(int site) => _core?.FindSlotForSite(site) ?? -1;

    public string DescribeStatus()
    {
        if (_grid == null) return "Projector streaming unavailable (no chunk grid).";
        if (!_enabled) return "Projector streaming disabled.";
        if (Unavailable) return "Projector streaming unavailable (" + (_failReason.Length > 0 ? _failReason : StartupError) + "), stand-ins only.";
        if (!IsBound) return "Projector streaming waiting for the ring geometry.";
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Pool {_slots.Count}/{SlotCount} {(PoolReady ? "ready" : "loading")}");
        for (int i = 0; i < _slots.Count; i++)
        {
            text.Append("; ").Append(_slots[i].Label);
            if (_core == null) { text.Append(" loading"); continue; }
            EmitterPoiSlotState state = _core.Slots[i];
            if (!state.Active) { text.Append(" idle"); continue; }
            text.Append(CultureInfo.InvariantCulture, $" #{state.Site} side {state.Site / _sites!.PerWall} {state.Distance / 1000d:0.0} km dwell {Math.Max(0d, _now - state.AssignedAt):0.0} s");
        }
        if (_nearestSite >= 0) text.Append(CultureInfo.InvariantCulture, $"; nearest #{_nearestSite} {_nearestDistance / 1000d:0} km");
        return text.ToString();
    }
}
