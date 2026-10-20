using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Loading;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Poi;

public enum WeatherLungDetailState { None, Loading, Attached, Failed, Attaching }

/// <summary>
/// World streamer of the Weather Lung (integration plan D2 / S5). Binds when the terrain sampler carries a Weather Lung placement (gen-17 v8 worlds;
/// every other world is Unavailable and costs nothing). The small POI scene (controller, FarProxy, Markers, DetailMount) is threaded-loaded at bind and
/// kept in the tree; the Detail scene is threaded-loaded when the observer is inside the Detail load radius and released after the observer has stayed
/// beyond load radius + margin for 2 s. Slot transforms are recomputed ABSOLUTELY from the floating-origin snapshot (never a delta), like the emitter
/// streamer. A load failure degrades to "not presented" and never blocks world loading.
/// </summary>
public partial class WeatherLungStreamer : Node3D, IWeatherLungWeatherSource
{
    public const string PoiScenePathDefault = "res://assets/poi/weather_lung/WeatherLungPOI.tscn";
    public const string DetailScenePathDefault = "res://assets/poi/weather_lung/WeatherLungDetail.tscn";
    /// <summary>Datum clearance above the pad (the datum is asset local Y 0; the plan lifts it 5 cm to avoid z-fighting with the terrain).</summary>
    public const double PadClearanceMeters = 0.05;

    private sealed class CollisionEntry
    {
        public Node3D Body = null!;
        public Vector3 Min, Max;
        public bool On;
    }

    [Export] public bool Enabled { get => _enabled; set => SetEnabled(value); }
    [Export] public bool AutoBind { get; set; } = true;
    [Export] public bool SynchronousLoad { get; set; }
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath StarLightPath { get; set; } = new("../StarLight");
    [Export] public NodePath DaySidePath { get; set; } = new("../DaySideAtmosphere");
    [Export] public NodePath EclipsePath { get; set; } = new("../EclipseEvent");
    [Export] public NodePath CloudsPath { get; set; } = new("../RingVolumetricClouds");

    /// <summary>Scene paths; set before binding (tests override these to force a load failure).</summary>
    public string PoiScenePath { get; set; } = PoiScenePathDefault;
    public string DetailScenePath { get; set; } = DetailScenePathDefault;

    private bool _enabled = true;
    private WeatherLungTuning _tuning = WeatherLungTuning.Defaults;
    private readonly WeatherLungStreamCore _core = new();
    private VoxelChunkGridManager? _grid;
    private WeatherLungPlacement? _placement;
    private object? _lastSamplerSeen;
    private RingThreadedResourceClaims? _claims;
    private bool _subscribed, _poiRequested, _detailRequested;
    private PackedScene? _poiScene, _detailScene;
    private Node3D? _poi, _detail;
    private WeatherLungController? _controller;
    private readonly List<CollisionEntry> _collision = new();
    private Transform3D _lastGridGlobal, _lastSelfGlobal;
    private double _clock;
    private int _revision;
    private int _drawEstimate;
    private readonly List<(PackedScene Scene, Node Parent, string Name)> _slices = new();
    private Node3D? _assembly;
    private int _assemblyStep;
    private System.Collections.Generic.IEnumerator<string>? _finalizer;
    /// <summary>Controller attach stages (bind pivots, bind materials, cache tiles, apply ranges) plus the completion step.</summary>
    private const int FinalizeSteps = 5;

    public bool IsBound => _placement != null && _grid != null;
    public bool Unavailable { get; private set; }
    public bool PoiReady => _poi != null;
    public bool PoiFailed { get; private set; }
    public string FailureReason { get; private set; } = "";
    public WeatherLungDetailState DetailState { get; private set; }
    public WeatherLungPlacement? Placement => _placement;
    public Node3D? PoiRoot => _poi;
    public Node3D? DetailRoot => _detail;
    public WeatherLungController? Controller => _controller;
    public WeatherLungStreamCore Core => _core;
    public WeatherLungTuning Tuning => _tuning;
    public double DistanceMeters { get; private set; } = double.PositiveInfinity;
    public int StepCount { get; private set; }
    public int AnchorRefreshCount { get; private set; }
    public double LastDetailInstantiateMs { get; private set; }
    /// <summary>Injectable millisecond clock for the attach budget (tests); null uses the stopwatch.</summary>
    public Func<double>? AttachClockMs { get; set; }
    public float AttachBudgetMs => _tuning.DetailAttachBudgetMs;

    /// <summary>One line per assembled piece: name, instantiate ms and add-to-tree ms (diagnostics; GDScript cannot read the tuple list).</summary>
    public string DescribeAttachPieces()
    {
        var text = new StringBuilder();
        foreach ((string name, double instantiateMs, double addMs) in AttachPieceMs)
            text.Append(CultureInfo.InvariantCulture, $"{name} instantiate {instantiateMs:0.0} ms add {addMs:0.0} ms; ");
        return text.ToString();
    }
    /// <summary>Per piece: name, instantiate ms, add-to-tree ms (diagnostics).</summary>
    public List<(string Name, double InstantiateMs, double AddMs)> AttachPieceMs { get; } = new();
    public int AttachSliceCount { get; private set; }
    public int AttachSlicesDone { get; private set; }
    public int AttachFrames { get; private set; }
    public double WorstAttachFrameMs { get; private set; }
    public double LastAttachFrameMs { get; private set; }
    public double AttachTotalMs { get; private set; }
    /// <summary>The Detail scene while it is being assembled (hidden); null otherwise.</summary>
    public Node3D? AssemblyRoot => _assembly;
    public int LoadCount { get; private set; }
    public int UnloadCount { get; private set; }
    public int CollisionBodyCount => _collision.Count;
    public int CollisionBodiesOn
    {
        get
        {
            int on = 0;
            foreach (CollisionEntry entry in _collision) if (entry.On) on++;
            return on;
        }
    }

    public override void _Ready()
    {
        ProcessPhysicsPriority = -100;
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _core.Enabled = _enabled;
        ApplyCoreTuning();
        if (_grid == null)
        {
            Unavailable = true;
            FailureReason = "no chunk grid manager";
        }
        if (!AutoBind || _grid == null) SetPhysicsProcess(false);
    }

    public override void _ExitTree()
    {
        if (_grid != null && _subscribed) { _grid.CanonicalFloatingOriginAnchorChanged -= HandleAnchorChanged; _subscribed = false; }
        if (_controller != null) _controller.Changed -= ForwardChanged;
        _claims?.Dispose();
        _claims = null;
    }

    public override void _Process(double delta)
    {
        if (_grid != null && _enabled && _poi != null) TickPoiHaze(false);
    }

    private DirectionalLight3D? _sunLight;

    /// <summary>Registers the POI architecture shader materials under <paramref name="node"/> with RingPoiHaze (deduplicated).</summary>
    private void RegisterPoiHaze(Node node)
    {
        if (_grid != null) RingPoiHaze.RegisterTree(node, _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
    }

    /// <summary>Publishes the camera ray frame to the POI architecture materials; forced after a floating-origin rebase.</summary>
    private void TickPoiHaze(bool force)
    {
        _sunLight ??= GetNodeOrNull<DirectionalLight3D>(StarLightPath);
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
            BindFromSampler(_grid.TerrainSampler);
            return;
        }
        if (!PoiReady)
        {
            AdvanceStartup();
            return;
        }
        Camera3D? camera = GetViewport()?.GetCamera3D();
        RingCanonicalPosition observer = _grid.WorldToCanonicalRingPosition64(camera?.GlobalPosition ?? Vector3.Zero);
        Step(_clock, observer);
    }

    /// <summary>Binds when the sampler carries a Weather Lung placement (v8); any other world is marked Unavailable and costs nothing.</summary>
    public bool BindFromSampler(RingTerrainSampler? sampler)
    {
        if (_grid != null && sampler?.WeatherLung is { } placement) return Bind(_grid, placement);
        Unavailable = true;
        FailureReason = "this world has no Weather Lung";
        return false;
    }

    /// <summary>Attaches the placement; the POI load is driven by <see cref="AdvanceStartup"/>.</summary>
    public bool Bind(VoxelChunkGridManager grid, WeatherLungPlacement placement)
    {
        if (IsBound) return true;
        _grid = grid;
        _placement = placement;
        Unavailable = false;
        FailureReason = "";
        grid.CanonicalFloatingOriginAnchorChanged += HandleAnchorChanged;
        _subscribed = true;
        return true;
    }

    // ---- POI scene (small, always resident once bound) --------------------------------------------------------------------------

    /// <summary>Requests / polls the POI load and instantiates it. Returns <see cref="PoiReady"/>. Never throws; a failure latches <see cref="PoiFailed"/>.</summary>
    public bool AdvanceStartup()
    {
        if (PoiReady || PoiFailed || !_enabled || !IsBound) return PoiReady;
        if (!_poiRequested)
        {
            _poiRequested = true;
            if (SynchronousLoad) _poiScene = LoadSync(PoiScenePath);
            else
            {
                _claims ??= new RingThreadedResourceClaims();
                Error error = _claims.Request(PoiScenePath);
                if (error != Error.Ok) { MarkPoiFailed("load request returned " + error); return false; }
            }
        }
        if (!SynchronousLoad && _poiScene == null)
        {
            var status = ResourceLoader.LoadThreadedGetStatus(PoiScenePath);
            if (status == ResourceLoader.ThreadLoadStatus.InvalidResource) { MarkPoiFailed("invalid resource " + PoiScenePath); return false; }
            if (status is not (ResourceLoader.ThreadLoadStatus.Loaded or ResourceLoader.ThreadLoadStatus.Failed)) return false;
            try { _poiScene = _claims!.Take(PoiScenePath) as PackedScene; }
            catch (Exception error) { MarkPoiFailed(error.Message); return false; }
        }
        if (_poiScene == null) { MarkPoiFailed("could not load " + PoiScenePath); return false; }
        InstantiatePoi();
        return PoiReady;
    }

    private static PackedScene? LoadSync(string path)
    {
        try { return ResourceLoader.Exists(path) ? ResourceLoader.Load<PackedScene>(path) : null; }
        catch (Exception) { return null; }
    }

    private void InstantiatePoi()
    {
        Node3D? root = _poiScene!.Instantiate() as Node3D;
        if (root == null) { MarkPoiFailed("scene root is not a Node3D"); return; }
        root.Name = "WeatherLungPOI";
        root.Visible = false;
        AddChild(root);
        RegisterPoiHaze(root);
        _poi = root;
        _controller = root as WeatherLungController;
        if (_controller == null)
        {
            GD.PushWarning("WeatherLungStreamer: the POI root has no WeatherLungController script; presenting a static station.");
        }
        else
        {
            _controller.Changed += ForwardChanged;
            _controller.BindSunSources(GetNodeOrNull<DirectionalLight3D>(StarLightPath), GetNodeOrNull(DaySidePath),
                GetNodeOrNull<EclipseEventDirector>(EclipsePath), GetNodeOrNull<RingVolumetricCloudLayer>(CloudsPath));
            _controller.ApplyTuning(_tuning);
            WeatherLungSite site = _placement!.Site;
            _controller.ConfigurePlacement(_placement.Identity, site.Around, site.Across);
        }
        AssignTransform(_grid!.CaptureLodGeometrySnapshot());
        root.Visible = _enabled;
        Changed?.Invoke();
    }

    private void MarkPoiFailed(string reason)
    {
        PoiFailed = true;
        FailureReason = reason;
        GD.PushWarning($"WeatherLungStreamer: the Weather Lung scene is unavailable ({reason}); it will not be presented.");
    }

    // ---- Per-frame step ----------------------------------------------------------------------------------------------------------

    /// <summary>Evaluates the observer against the station: detail load / unload, detail instantiation and collision gates.</summary>
    public void Step(double now, in RingCanonicalPosition observer)
    {
        if (_placement == null || _grid == null || !PoiReady) return;
        _clock = Math.Max(_clock, now);
        CheckRootShift();
        WeatherLungSite site = _placement.Site;
        double circumference = _placement.Recipe.CircumferenceM;
        double du = EmitterPoiStreamerCore.SignedWrap(site.Around - observer.AroundMeters, circumference);
        double dw = site.Across - observer.AcrossMeters;
        double dv = site.PadHeight - observer.VerticalMeters;
        DistanceMeters = Math.Sqrt(du * du + dw * dw + dv * dv);
        _core.Enabled = _enabled;
        ApplyCoreTuning();
        switch (_core.Update(now, DistanceMeters))
        {
            case WeatherLungDetailEvent.Load: BeginDetailLoad(); break;
            case WeatherLungDetailEvent.Unload: ReleaseDetail(); break;
        }
        if (DetailState is WeatherLungDetailState.Loading or WeatherLungDetailState.Attaching) PollDetail();
        if (DetailState == WeatherLungDetailState.Attached)
        {
            // Station-local (asset) position of the observer, exact from the canonical offsets (no camera or scene transform needed).
            _placement.ToAssetLocal(-du, -dw, out double lx, out double ly);
            UpdateCollision(new Vector3((float)lx, (float)(observer.VerticalMeters - site.PadHeight - PadClearanceMeters), (float)-ly));
        }
        StepCount++;
    }

    private void ApplyCoreTuning()
    {
        _core.LoadMeters = _tuning.EffectiveLoadRadiusMeters;
        _core.UnloadMarginMeters = _tuning.UnloadMarginMeters;
    }

    private void BeginDetailLoad()
    {
        if (DetailState is WeatherLungDetailState.Attached or WeatherLungDetailState.Loading or WeatherLungDetailState.Failed) return;
        DetailState = WeatherLungDetailState.Loading;
        _detailRequested = true;
        LoadCount++;
        if (SynchronousLoad)
        {
            _detailScene = LoadSync(DetailScenePath);
            if (_detailScene == null) MarkDetailFailed("could not load " + DetailScenePath);
            return;
        }
        _claims ??= new RingThreadedResourceClaims();
        Error error = _claims.Request(DetailScenePath);
        if (error != Error.Ok) MarkDetailFailed("load request returned " + error);
    }

    private void PollDetail()
    {
        if (_detailScene == null && !SynchronousLoad && _claims != null)
        {
            var status = ResourceLoader.LoadThreadedGetStatus(DetailScenePath);
            if (status == ResourceLoader.ThreadLoadStatus.InvalidResource) { MarkDetailFailed("invalid resource " + DetailScenePath); return; }
            if (status is not (ResourceLoader.ThreadLoadStatus.Loaded or ResourceLoader.ThreadLoadStatus.Failed)) return;
            try { _detailScene = _claims.Take(DetailScenePath) as PackedScene; }
            catch (Exception error) { MarkDetailFailed(error.Message); return; }
            _detailRequested = false;
            if (_detailScene == null) { MarkDetailFailed("could not load " + DetailScenePath); return; }
        }
        if (_detailScene == null) return;
        // The Detail scene is assembled piece by piece: the shell, then one instanced sub-scene (a sector or an Arch part) at a time, within a per-frame
        // time budget. It stays hidden (and the FarProxy stays visible at every distance) until every piece is in.
        double frameStart = NowMs();
        if (_assembly == null && !BeginAssembly()) return;
        double budget = Math.Clamp(_tuning.DetailAttachBudgetMs, 0.1d, 100d);
        try
        {
            while (_assembly != null)
            {
                if (_assemblyStep < _slices.Count)
                {
                    (PackedScene scene, Node parent, string name) = _slices[_assemblyStep];
                    double pieceStart = NowMs();
                    Node piece = scene.Instantiate();
                    double instantiated = NowMs();
                    piece.Name = name;
                    parent.AddChild(piece);
                    RegisterPoiHaze(piece);
                    AttachPieceMs.Add((name, instantiated - pieceStart, NowMs() - instantiated));
                    _assemblyStep++;
                    AttachSlicesDone = _assemblyStep;
                }
                else if (FinalizeStep()) break;
                if (NowMs() - frameStart >= budget) break;
            }
        }
        catch (Exception error) { MarkDetailFailed(error.Message); return; }
        double frameMs = NowMs() - frameStart;
        AttachFrames++;
        AttachTotalMs += frameMs;
        LastAttachFrameMs = frameMs;
        WorstAttachFrameMs = Math.Max(WorstAttachFrameMs, frameMs);
        LastDetailInstantiateMs = AttachTotalMs;
    }

    private double NowMs() => AttachClockMs?.Invoke() ?? Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;

    private bool BeginAssembly()
    {
        if (_controller == null) { MarkDetailFailed("no controller to attach the detail scene to"); return false; }
        SceneState state = _detailScene!.GetState();
        int count = state.GetNodeCount();
        var nodes = new Dictionary<string, Node>();
        _slices.Clear();
        Node3D? root = null;
        for (int i = 0; i < count; i++)
        {
            string path = state.GetNodePath(i).ToString();
            PackedScene? instance = state.GetNodeInstance(i);
            if (i == 0)
            {
                root = new Node3D { Name = state.GetNodeName(0) };
                nodes[path] = root;
                for (int p = 0; p < state.GetNodePropertyCount(0); p++) root.Set(state.GetNodePropertyName(0, p), state.GetNodePropertyValue(0, p));
                root.Visible = false;
                continue;
            }
            string parentPath = state.GetNodePath(i, true).ToString();
            if (!nodes.TryGetValue(parentPath, out Node? parent)) { MarkDetailFailed("detail scene state has an orphan node " + path); return false; }
            if (instance != null)
            {
                _slices.Add((instance, parent, state.GetNodeName(i).ToString()));
                continue;
            }
            // Plain grouping nodes (Sectors, Arch, Arch/Dressing) are cheap: created here with their saved properties.
            var group = new Node3D { Name = state.GetNodeName(i) };
            for (int p = 0; p < state.GetNodePropertyCount(i); p++) group.Set(state.GetNodePropertyName(i, p), state.GetNodePropertyValue(i, p));
            parent.AddChild(group);
            nodes[path] = group;
        }
        if (root == null) { MarkDetailFailed("detail scene is empty"); return false; }
        _assembly = root;
        _assemblyStep = 0;
        AttachSliceCount = _slices.Count + FinalizeSteps;
        _finalizer = null;
        AttachPieceMs.Clear();
        AttachSlicesDone = 0;
        AttachFrames = 0;
        AttachTotalMs = 0d;
        WorstAttachFrameMs = 0d;
        LastAttachFrameMs = 0d;
        _controller.HoldProxyVisible = true;
        _controller.DetailMountNode.AddChild(root);
        RegisterPoiHaze(root);
        DetailState = WeatherLungDetailState.Attaching;
        return true;
    }

    /// <summary>Last step: the controller binds the rig and the HLOD parent; only then does Detail become visible and the FarProxy go back to its range.</summary>
    private bool FinalizeStep()
    {
        Node3D root = _assembly!;
        double t0 = NowMs();
        if (_controller == null) { MarkDetailFailed("no controller to attach the detail scene to"); return true; }
        _finalizer ??= _controller.AttachDetailStaged(root).GetEnumerator();
        if (_finalizer.MoveNext())
        {
            AttachPieceMs.Add(("finalize:" + _finalizer.Current, NowMs() - t0, 0d));
            _assemblyStep++;
            AttachSlicesDone = _assemblyStep;
            return false;
        }
        // The staged attach has run to its end (or failed).
        if (_controller.LastAttachError.Length > 0 || !_controller.IsDetailAttached)
        {
            MarkDetailFailed(_controller.LastAttachError.Length > 0 ? "the detail rig could not be bound: " + _controller.LastAttachError : "the detail rig could not be bound");
            return true;
        }
        BuildCollisionEntries(root);
        _drawEstimate = EstimateDraws(root);
        _detail = root;
        _assembly = null;
        _finalizer = null;
        _slices.Clear();
        _assemblyStep = 0;
        root.Visible = true;
        _controller.HoldProxyVisible = false;
        DetailState = WeatherLungDetailState.Attached;
        AttachPieceMs.Add(("finalize:collision entries and reveal", NowMs() - t0, 0d));
        AttachSlicesDone = AttachSliceCount;
        return true;
    }

    /// <summary>Frees a half-built Detail scene (cancel mid-attach, disable, failure) and gives the FarProxy its range back.</summary>
    private void CancelAssembly()
    {
        if (_finalizer != null && _controller != null && GodotObject.IsInstanceValid(_controller)) _controller.DetachDetail(); // a staged attach had already bound part of the rig
        if (_assembly != null && GodotObject.IsInstanceValid(_assembly))
        {
            _assembly.GetParent()?.RemoveChild(_assembly);
            _assembly.Free();
        }
        _finalizer = null;
        _assembly = null;
        _slices.Clear();
        _assemblyStep = 0;
        if (_controller != null && GodotObject.IsInstanceValid(_controller)) _controller.HoldProxyVisible = false;
    }

    private void MarkDetailFailed(string reason)
    {
        CancelAssembly();
        DetailState = WeatherLungDetailState.Failed;
        FailureReason = reason;
        _detailScene = null;
        _core.Reset();
        GD.PushWarning($"WeatherLungStreamer: the Weather Lung detail scene is unavailable ({reason}); only the far proxy is shown.");
    }

    private void ReleaseDetail()
    {
        if (DetailState == WeatherLungDetailState.Failed) { DetailState = WeatherLungDetailState.None; return; }
        CancelAssembly();
        _collision.Clear();
        if (_detail != null && GodotObject.IsInstanceValid(_detail))
        {
            _controller?.DetachDetail();
            _detail.QueueFree();
        }
        UnloadCount++;
        _detail = null;
        _detailScene = null;
        if (_detailRequested && _claims != null)
        {
            // The request may still be in flight; drain it (the claim must be consumed exactly once) and drop the result.
            _claims.DrainCompleted();
        }
        DetailState = WeatherLungDetailState.None;
    }

    // ---- Collision ----------------------------------------------------------------------------------------------------------------

    private void BuildCollisionEntries(Node3D detail)
    {
        _collision.Clear();
        Node? bodies = detail.GetNodeOrNull("Collision");
        if (bodies == null) return;
        foreach (Node child in bodies.GetChildren())
        {
            if (child is not Node3D body) continue;
            Aabb bounds = default;
            bool any = false;
            if (body.Name.ToString().StartsWith("Arch", StringComparison.Ordinal))
            {
                Node3D? arch = detail.GetNodeOrNull<Node3D>("Arch");
                Node? structure = arch?.GetNodeOrNull("Structure");
                if (arch == null || structure == null) continue;
                foreach (Node batch in structure.GetChildren())
                    if (batch is MeshInstance3D { Mesh: { } mesh } mi)
                    {
                        Aabb box = mi.Transform * mesh.GetAabb();
                        bounds = any ? bounds.Merge(box) : box;
                        any = true;
                    }
                if (any) bounds = new Aabb(bounds.Position + arch.Position, bounds.Size);
            }
            else
            {
                string id = body.Name.ToString().Replace("Sector_", "", StringComparison.Ordinal);
                Node3D? sector = detail.GetNodeOrNull<Node3D>("Sectors/WeatherLungSector_" + id);
                if (sector?.GetNodeOrNull<MeshInstance3D>("Structure") is { Mesh: { } mesh })
                {
                    bounds = new Aabb(mesh.GetAabb().Position + sector.Position, mesh.GetAabb().Size);
                    any = true;
                }
            }
            if (!any) continue;
            body.ProcessMode = ProcessModeEnum.Disabled;
            _collision.Add(new CollisionEntry { Body = body, Min = bounds.Position, Max = bounds.End });
        }
    }

    private void UpdateCollision(Vector3 p)
    {
        if (_collision.Count == 0) return;
        double on = _tuning.CollisionRadiusMeters;
        foreach (CollisionEntry entry in _collision)
        {
            if (!GodotObject.IsInstanceValid(entry.Body)) continue;
            double distance = WeatherLungStreamCore.DistanceToBox(p.X, p.Y, p.Z, entry.Min.X, entry.Min.Y, entry.Min.Z, entry.Max.X, entry.Max.Y, entry.Max.Z);
            bool next = WeatherLungStreamCore.CollisionGate(entry.On, distance, on, WeatherLungStreamCore.CollisionOffMarginMeters);
            if (next == entry.On) continue;
            entry.On = next;
            entry.Body.ProcessMode = next ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }
    }

    /// <summary>Station-local position of a POI marker (Datum, DamCrest, IntakeOverlook, ...), or <paramref name="fallback"/> before the POI is loaded.</summary>
    public Vector3 GetMarker(string name, Vector3 fallback)
        => _poi?.GetNodeOrNull<Node3D>("Markers/" + name)?.Position ?? fallback;

    /// <summary>The station world transform for the current floating-origin snapshot (what the POI root must equal).</summary>
    public Transform3D ExpectedWorldTransform() => SiteWorldTransform(_grid!.CaptureLodGeometrySnapshot());

    /// <summary>Collision-on state of the body named <c>Sector_XX</c> or <c>Arch</c> (false when absent or the detail is not attached).</summary>
    public bool IsCollisionOn(string bodyName)
    {
        foreach (CollisionEntry entry in _collision)
            if ((entry.Body.Name == bodyName || (bodyName == "Arch" && entry.Body.Name.ToString().StartsWith("Arch_", StringComparison.Ordinal))) && entry.On) return true;
        return false;
    }

    private static int EstimateDraws(Node3D detail)
    {
        // Structure and Moving surfaces always draw when in view; dressing tiles only within their range (about 5 surfaces per tile, a dozen in range).
        int surfaces = 0;
        var stack = new Stack<Node>();
        stack.Push(detail);
        while (stack.Count > 0)
        {
            Node n = stack.Pop();
            if (n.Name == "Dressing" || n.Name == "Collision") continue;
            foreach (Node c in n.GetChildren()) stack.Push(c);
            if (n is MeshInstance3D { Mesh: { } mesh }) surfaces += mesh.GetSurfaceCount();
        }
        return surfaces + 60;
    }

    // ---- Transform / floating origin ------------------------------------------------------------------------------------------------

    /// <summary>World transform of the station for <paramref name="snapshot"/>: SiteTransform(around, across, pad + 5 cm, cos yaw, sin yaw).</summary>
    public Transform3D SiteWorldTransform(in RingLodGeometrySnapshot snapshot)
    {
        WeatherLungSite site = (_placement ?? throw new InvalidOperationException("Weather Lung streamer is not bound")).Site;
        return new EmitterSiteFrameContext(snapshot).SiteTransform(site.Around, site.Across, site.PadHeight + PadClearanceMeters, site.CosYaw, site.SinYaw);
    }

    private void AssignTransform(in RingLodGeometrySnapshot snapshot)
    {
        if (_poi == null || _placement == null) return;
        Transform3D worldToLayer = IsInsideTree() ? GlobalTransform.AffineInverse() : Transform3D.Identity;
        _poi.Transform = worldToLayer * SiteWorldTransform(snapshot);
        if (_grid != null && _grid.IsInsideTree()) _lastGridGlobal = _grid.GlobalTransform;
        if (IsInsideTree()) _lastSelfGlobal = GlobalTransform;
        _revision++;
    }

    private void HandleAnchorChanged(RingLodGeometrySnapshot previous, RingLodGeometrySnapshot current, RingCanonicalPosition anchor)
    {
        _ = previous; _ = anchor;
        if (_poi == null) return;
        AssignTransform(current);
        AnchorRefreshCount++;
        if (_grid != null) TickPoiHaze(true);
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
        {
            AssignTransform(_grid.CaptureLodGeometrySnapshot());
            AnchorRefreshCount++;
        }
    }

    // ---- Settings ---------------------------------------------------------------------------------------------------------------------

    /// <summary>Applies the tuning values (the saved defaults and the grave-key menu). The Detail load radius is raised to the detail range + 4 km.</summary>
    public void Configure(in WeatherLungTuning tuning)
    {
        _tuning = tuning;
        ApplyCoreTuning();
        _controller?.ApplyTuning(tuning);
        Enabled = tuning.Enabled;
    }

    private void SetEnabled(bool value)
    {
        if (_enabled == value) return;
        _enabled = value;
        _core.Enabled = value;
        if (_poi != null) _poi.Visible = value;
        if (!value && DetailState is WeatherLungDetailState.Attached or WeatherLungDetailState.Loading or WeatherLungDetailState.Attaching)
        {
            ReleaseDetail();
            _core.Reset();
        }
        Changed?.Invoke();
    }

    // ---- IWeatherLungWeatherSource (forwarded to the controller) ---------------------------------------------------------------------

    public event Action? Changed;
    private void ForwardChanged() => Changed?.Invoke();

    public bool IsPresent => _enabled && _controller != null && _controller.IsPresent;
    public ulong Identity => _placement?.Identity ?? 0UL;
    public int GeometryRevision => _revision;
    public double CentreAround => _placement?.Site.Around ?? 0d;
    public double CentreAcross => _placement?.Site.Across ?? 0d;
    public Vector2 BasinRadii => new(14000f, 9000f);
    public double BreathPhase => _controller?.BreathPhase ?? 0d;
    public double InhaleLevel => _controller?.InhaleLevel ?? 0d;
    public double ExhaleEnvelope => _controller?.ExhaleEnvelope ?? 0d;
    public float SunAvailable => _controller?.SunAvailable ?? 1f;

    public bool TryGetPlume(out WeatherLungPlumeState plume)
    {
        if (_controller != null) return _controller.TryGetPlume(out plume);
        plume = default;
        return false;
    }

    // ---- Diagnostics ------------------------------------------------------------------------------------------------------------------

    public string DescribePlacement()
    {
        if (_placement == null) return "no placement";
        WeatherLungSite s = _placement.Site;
        return string.Create(CultureInfo.InvariantCulture,
            $"station {s.Station}, around {s.Around:0} m, across {s.Across} m, datum {s.PadHeight} m, yaw {s.YawDegrees:0} deg, {s.TerrainClass}{(s.IsQuay ? " quay" : "")}, plinth up to {s.PlinthMaxM:0} m");
    }

    public string DescribeStatus()
    {
        if (!_enabled) return "Weather Lung disabled.";
        if (_grid == null) return "Weather Lung unavailable (no chunk grid).";
        if (Unavailable) return "Weather Lung unavailable (this world has no Weather Lung: needs a v8 world).";
        if (!IsBound) return "Weather Lung waiting for the terrain sampler.";
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Placement: {DescribePlacement()}.");
        text.Append(PoiFailed ? $" Scene failed ({FailureReason}); not presented." : PoiReady ? " Scene loaded." : " Scene loading.");
        if (DistanceMeters < double.PositiveInfinity) text.Append(CultureInfo.InvariantCulture, $" Distance {DistanceMeters / 1000d:0.0} km.");
        text.Append(" Detail ").Append(DetailState switch
        {
            WeatherLungDetailState.Attached => string.Create(CultureInfo.InvariantCulture, $"attached ({AttachFrames} frames, worst {WorstAttachFrameMs:0.0} ms, total {AttachTotalMs:0.0} ms, ~{_drawEstimate} draws, collision {CollisionBodiesOn}/{CollisionBodyCount})"),
            WeatherLungDetailState.Attaching => string.Create(CultureInfo.InvariantCulture, $"assembling {AttachSlicesDone}/{AttachSliceCount} (budget {_tuning.DetailAttachBudgetMs:0.#} ms per frame)"),
            WeatherLungDetailState.Loading => "loading",
            WeatherLungDetailState.Failed => "failed (" + FailureReason + ")",
            _ => "released"
        }).Append('.');
        if (_controller != null) text.Append(' ').Append(_controller.DescribeStatus()).Append('.');
        return text.ToString();
    }
}
