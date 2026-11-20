using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Vegetation.Scatter;

/// <summary>
/// The group-independent machinery of a scatter layer (T5-7b, design B2/B3/B5; generalised by T6-4b): the compute, the anchor frame and the worker source build loop, the output sets with their gap-free promotion,
/// the camera cull latch (the tree layer's rules, without the shadow state) and the frame submission. A concrete layer (<see cref="RingScatterLayer"/> for rocks, RingUnderstoryLayer for saplings and later shrubs and dead wood)
/// supplies its assets, its worker build, its outputs (one <see cref="Output"/> per group and LOD, optionally a caster) and the per-output band. Everything here is moved verbatim from the rock layer, so the rocks behave as before.
/// </summary>
internal abstract partial class RingScatterLayerBase : Node3D
{
    public const float AnchorMeters = 512f;
    private const double EpochRebuildSeconds = .25;

    // Same padding the tree layer uses for its camera lists (RingBiomeDecorationLayer.Gpu.cs, "Per-instance frustum culling of the camera tree lists").
    private const float CullMovementMeters = 4f, CullRotationDegrees = 3f, CullPaddingDegrees = 5f, CullSpeedSeconds = .1f, CullWindFraction = .6f, CullGuardMeters = .25f;
    private static readonly StringName SunNearDistanceMetaName = RingGraphicsQualityController.SunNearDistanceMetaKey;

    internal ScatterLayerHost Host { get; set; } = new();
    /// <summary>Multiplies the per-slot LOD budgets (T5-9 wires the presets).</summary>
    internal float CapScale { get; set; } = 1f;
    /// <summary>Debug / test: false keeps every row in the camera lists.</summary>
    internal bool FrustumCulling { get; set; } = true;

    // ------------------------------------------------------------------ what a concrete layer supplies

    /// <summary>The compute's diagnostic category (RingPerformanceGpu).</summary>
    protected abstract string DiagnosticCategory { get; }
    /// <summary>Used in the one-time warning when the layer hides itself, e.g. "Scatter rocks".</summary>
    protected abstract string HiddenLabel { get; }
    /// <summary>Name of the node that parents one source set's renderers.</summary>
    protected abstract string SetRootName { get; }
    /// <summary>The per-slot LOD budgets at cap scale 1 (five slots).</summary>
    protected abstract uint[] BaseCaps { get; }
    /// <summary>The rank gate of every batch.</summary>
    protected abstract float DensityGate { get; }
    /// <summary>The rank gate of one output (T6-6: the understory layer gates saplings and dead wood separately). Defaults to <see cref="DensityGate"/>.</summary>
    protected virtual float GateOf(Output output) => DensityGate;
    /// <summary>Loads catalogs and materials once, after the compute exists. Throws (message reported) on failure.</summary>
    protected abstract void LoadAssets();
    /// <summary>Starts the worker build of the rows in the frame of the anchor.</summary>
    protected abstract Task<SourceBuildBase> StartBuild(ScatterRecord[] records, RingLodGeometrySnapshot snapshot, long anchorAround, long anchorAcross, long epoch, GeometryKey key);
    /// <summary>Creates the outputs of a new set (AddOutput per group, LOD and caster).</summary>
    protected abstract void CreateOutputs(OutputSet set);
    /// <summary>Writes the per-renderer instance uniforms that follow the set's anchor frame.</summary>
    protected abstract void ApplyFrameUniforms(MultiMeshInstance3D renderer, OutputSet set, Vector3 origin);
    /// <summary>Budget slot and distance band of one output for this frame (the distance scale is already applied by the caller via <paramref name="scale"/>).</summary>
    protected abstract FrameTerms ResolveFrame(Output output, float scale, float shadowDistance);
    /// <summary>Runs every frame, before the compute is required: colliders.</summary>
    protected virtual void UpdateAlways() { }
    /// <summary>Runs every frame once the compute is ready: surface registration.</summary>
    protected virtual void UpdateAfterCompute() { }
    /// <summary>A renderer's instance uniforms right after its creation.</summary>
    protected virtual void OnOutputCreated(MultiMeshInstance3D full) { }
    protected virtual void ReleaseAssets() { }

    protected readonly record struct FrameTerms(int Pass, float Near, float Far, float InStart, float InEnd, float OutStart, float OutEnd);

    // ------------------------------------------------------------------ diagnostics
    internal RingGpuVegetationCompute? Compute => _compute;
    /// <summary>Batches registered on the scatter compute (the shown set plus a set waiting to take over).</summary>
    public int BatchCount => _compute?.BatchCount ?? 0;
    /// <summary>Renderers drawing right now: ready camera outputs plus ready caster outputs of the shown set.</summary>
    public int DrawOutputs { get { int n = 0; if (_active != null) foreach (Output o in _active.Outputs) if (o.Ready && o.Full.Visible) n++; return n; } }
    /// <summary>Every MultiMeshInstance3D the layer owns (full-list and camera renderers, both sets).</summary>
    public int RendererCount { get { int n = 0; foreach (OutputSet? set in new[] { _active, _pending }) if (set != null) foreach (Output o in set.Outputs) n += o.Camera != null ? 2 : 1; return n; } }
    /// <summary>Test hook: the shown set's outputs (name, batch id, group slot, LOD or -1 for a caster batch).</summary>
    internal List<(string Name, int BatchId, int Group, int Lod, bool Caster)> OutputInfo()
    {
        var list = new List<(string, int, int, int, bool)>();
        if (_active != null) foreach (Output o in _active.Outputs) list.Add((o.Name, o.BatchId, o.Group, o.Lod, o.Caster));
        return list;
    }
    public double LastSourceBuildMs { get; private set; }
    /// <summary>Rebuilds started because the camera left the anchor's 512 m.</summary>
    public int AnchorRebuilds { get; private set; }
    /// <summary>Source builds in all (the first one, anchor moves, record epoch changes).</summary>
    public int SourceBuilds { get; private set; }
    /// <summary>Canonical anchor (metres around, across) of the latest source build.</summary>
    internal (long Around, long Across) Anchor => (_anchorAround, _anchorAcross);
    public bool Failed => _failed;
    internal bool SetPending => _pending != null || _build != null;
    /// <summary>The shown set's source build (null before the first one).</summary>
    protected SourceBuildBase? ActiveSource => _active?.Source;

    protected RingGpuVegetationCompute? _compute;
    private bool _failed, _failureLogged;

    private Task<SourceBuildBase>? _build;
    private OutputSet? _active, _pending;
    private bool _haveAnchor;
    private long _anchorAround, _anchorAcross;
    private long _builtEpoch = -1;
    private GeometryKey _builtGeometry;
    private ulong _lastBuildMsec;
    private readonly List<ScatterRecord> _recordScratch = new();
    private readonly List<RingGpuVegetationCompute.BatchFrameParameters> _frames = new();
    private uint[] _caps = Array.Empty<uint>(); // one budget per slot, sized from BaseCaps (rocks 5, understory 7 with the stand filler)

    // ------------------------------------------------------------------ build inputs and results

    internal readonly record struct GeometryKey(Basis Basis, double Radius, double Circumference, double VerticalBase);

    private static GeometryKey KeyOf(in RingLodGeometrySnapshot g) => new(g.GridGlobalTransform.Basis, g.RingRadiusMeters, g.CircumferenceMeters, g.RingVerticalBaseOffsetMeters);

    internal abstract class SourceBuildBase
    {
        public long Epoch, AnchorAround, AnchorAcross;
        public GeometryKey Geometry;
        public double BuildMs;
    }

    protected sealed class Output
    {
        public int Group, Lod;           // Lod -1: the group's caster batch
        public bool Caster;
        public string Name = "";
        public MultiMeshInstance3D Full = null!;
        public MultiMeshInstance3D? Camera;
        public int BatchId = -1;
        public bool Ready;
    }

    protected sealed class OutputSet
    {
        public Node3D Root = null!;
        public SourceBuildBase Source = null!;
        public readonly List<Output> Outputs = new();
        public Transform3D LastTransform;
        public Vector3 UniformOrigin = new(float.NaN, float.NaN, float.NaN);
        public Vector3 Around, Across;
        public Vector2 MacroUv;
    }

    // ------------------------------------------------------------------ frame

    /// <summary>Test hook: false runs the collider pool only (no GPU compute, no renderers), for headless tests.</summary>
    internal bool RenderingEnabled { get; set; } = true;

    public override void _Process(double delta)
    {
        if (Host == null) return;
        UpdateAlways(); // gameplay: independent of the compute and the camera
        if (_failed || !RenderingEnabled) return;
        if (!EnsureCompute()) return;
        UpdateAfterCompute();
        RingLodGeometrySnapshot? geometry = Host.Geometry();
        RingCanonicalPosition? focus = Host.Focus();
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (geometry is not { } geo || focus is not { } canonical || camera == null || !camera.IsInsideTree()) return;
        ServiceSources(geo, canonical);
        bool moved = UpdateSetTransforms(geo);
        PromotePending();
        UpdateCulling(camera, moved);
        SubmitFrame(camera);
    }

    private bool EnsureCompute()
    {
        if (_compute == null)
        {
            _compute = new RingGpuVegetationCompute { HazeChannel = RingVegetationHaze.Channel.None, DiagnosticCategory = DiagnosticCategory };
            try { LoadAssets(); }
            catch (Exception error) { Fail(HiddenLabel + " assets: " + error.Message); return false; }
        }
        _compute.Initialize();
        if (_compute.Failed) { Fail(_compute.Status); return false; }
        return _compute.Ready;
    }

    protected void Fail(string status)
    {
        _failed = true;
        Visible = false;
        if (!_failureLogged) { _failureLogged = true; GD.PushWarning(HiddenLabel + " are hidden: the GPU vegetation compute is unavailable (" + status + ")."); }
    }

    // ------------------------------------------------------------------ sources

    protected static double Wrap(double delta, double circumference) => delta - circumference * Math.Round(delta / circumference);

    protected static long Snap(double meters) => (long)Math.Round(meters / AnchorMeters) * (long)AnchorMeters;

    /// <summary>The next ServiceSources starts a new source build, subject to the epoch throttle; used when a layer's output set changes shape.</summary>
    protected void RequestRebuild() => _builtEpoch = -1;

    private void ServiceSources(in RingLodGeometrySnapshot geometry, in RingCanonicalPosition focus)
    {
        if (_build is { IsCompleted: true })
        {
            Task<SourceBuildBase> done = _build;
            _build = null;
            if (done.IsCompletedSuccessfully) Install(done.Result, geometry, focus);
            else { GD.PushWarning(HiddenLabel + " source build failed: " + done.Exception?.GetBaseException().Message); }
        }
        if (_build != null) return;
        GeometryKey key = KeyOf(geometry);
        long epoch = Host.Epoch();
        bool anchorMoved = false;
        if (!_haveAnchor) { /* first build */ }
        else if (Math.Sqrt(Math.Pow(Wrap(focus.AroundMeters - _anchorAround, geometry.CircumferenceMeters), 2) + Math.Pow(focus.AcrossMeters - _anchorAcross, 2)) > AnchorMeters) anchorMoved = true;
        else if (!key.Equals(_builtGeometry)) { }
        else if (epoch != _builtEpoch && (Time.GetTicksMsec() - _lastBuildMsec) * .001 >= EpochRebuildSeconds) { }
        else return;
        long around = _haveAnchor && !anchorMoved ? _anchorAround : Snap(focus.AroundMeters), across = _haveAnchor && !anchorMoved ? _anchorAcross : Snap(focus.AcrossMeters);
        if (anchorMoved) AnchorRebuilds++;
        SourceBuilds++;
        _haveAnchor = true; _anchorAround = around; _anchorAcross = across; _builtEpoch = epoch; _builtGeometry = key; _lastBuildMsec = Time.GetTicksMsec();
        _recordScratch.Clear();
        Host.CopyRecords(_recordScratch);
        ScatterRecord[] records = _recordScratch.ToArray();
        RingLodGeometrySnapshot snapshot = geometry;
        _build = StartBuild(records, snapshot, around, across, epoch, key);
    }

    // ------------------------------------------------------------------ sets

    private void Install(SourceBuildBase build, in RingLodGeometrySnapshot geometry, in RingCanonicalPosition focus)
    {
        LastSourceBuildMs = build.BuildMs;
        if (_pending != null) { DisposeSet(_pending); _pending = null; }
        var set = new OutputSet { Source = build };
        set.Root = new Node3D { Name = SetRootName };
        AddChild(set.Root);
        set.Root.GlobalTransform = AnchorTransform(geometry, build);
        set.LastTransform = set.Root.GlobalTransform;
        double radius = geometry.RingRadiusMeters, circumference = geometry.CircumferenceMeters;
        RingStandingGrassLayer.CalculateGrassGroundCovectors(geometry, 0.0, build.AnchorAround, out set.Around, out set.Across);
        double aroundWrapped = build.AnchorAround % circumference; if (aroundWrapped < 0) aroundWrapped += circumference;
        set.MacroUv = new Vector2((float)(aroundWrapped / (Math.Tau * Math.Max(1.0, radius))), (float)(build.AnchorAcross / Math.Max(1.0, Host.RingAcrossWidthMeters())));
        CreateOutputs(set);
        _pending = set;
        PromotePending();
    }

    private static Transform3D AnchorTransform(in RingLodGeometrySnapshot geometry, SourceBuildBase build) => AnchorTransform(geometry, build.AnchorAround, build.AnchorAcross);

    protected static Transform3D AnchorTransform(in RingLodGeometrySnapshot geometry, long anchorAround, long anchorAcross)
        => new(Basis.Identity, geometry.CanonicalToWorld(anchorAround, 0.0, anchorAcross));

    /// <summary>Registers one batch on the compute and adds its renderers (the full list and, for camera batches, the camera list) under the set root.</summary>
    protected void AddOutput(OutputSet set, int group, int lod, bool caster, string name, Mesh carrier, ShaderMaterial material, float[] rows, Vector4[] meta, int count, Aabb bounds)
    {
        var full = new MultiMeshInstance3D
        {
            Name = name, Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = carrier }, MaterialOverride = material,
            ExtraCullMargin = 8, Visible = false,
            // Camera batches: the full list is never drawn (the camera draws the phase 6 list). Caster batches: the full list casts the sun shadow.
            CastShadow = caster ? GeometryInstance3D.ShadowCastingSetting.ShadowsOnly : GeometryInstance3D.ShadowCastingSetting.Off,
        };
        if (!caster) full.Layers = 0;
        MultiMesh? cameraMultiMesh = null;
        MultiMeshInstance3D? camera = null;
        if (!caster)
        {
            cameraMultiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = carrier };
            camera = new MultiMeshInstance3D { Name = name + "_Camera", Multimesh = cameraMultiMesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 8 };
            full.AddChild(camera);
        }
        set.Root.AddChild(full);
        int id = _compute!.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration(name, full.Multimesh!, carrier, rows, meta, count, bounds, false,
            TreeCustomData: true, TreeDitherData: !caster, Expansion: null, CameraTarget: cameraMultiMesh, VerifyTarget: null));
        if (id < 0) { full.QueueFree(); GD.PushWarning(HiddenLabel + " batch registration failed for " + name); return; }
        OnOutputCreated(full);
        set.Outputs.Add(new Output { Group = group, Lod = caster ? -1 : lod, Caster = caster, Name = name, Full = full, Camera = camera, BatchId = id });
    }

    private void DisposeSet(OutputSet set)
    {
        foreach (Output output in set.Outputs) _compute?.RemoveBatch(output.BatchId);
        set.Root.QueueFree();
    }

    /// <summary>Anchor frames follow the floating origin: the node sits at the anchor's world position from the live geometry (a rebase only moves it). True when a node moved.</summary>
    private bool UpdateSetTransforms(in RingLodGeometrySnapshot geometry)
    {
        bool moved = false;
        foreach (OutputSet? set in new[] { _active, _pending })
        {
            if (set == null) continue;
            Transform3D transform = AnchorTransform(geometry, set.Source);
            if (transform != set.Root.GlobalTransform) set.Root.GlobalTransform = transform;
            if (transform != set.LastTransform) { moved = true; set.LastTransform = transform; }
            if (transform.Origin != set.UniformOrigin)
            {
                set.UniformOrigin = transform.Origin;
                foreach (Output output in set.Outputs)
                {
                    ApplyFrameUniforms(output.Full, set, transform.Origin);
                    if (output.Camera != null) ApplyFrameUniforms(output.Camera, set, transform.Origin);
                }
            }
        }
        return moved;
    }

    /// <summary>A new set shows (and the old one goes) in the same frame, once every batch of it has produced its first result.</summary>
    private void PromotePending()
    {
        foreach (OutputSet? set in new[] { _active, _pending })
            if (set != null) foreach (Output output in set.Outputs) output.Ready = output.Ready || _compute!.IsBatchReady(output.BatchId);
        if (_pending != null)
        {
            bool all = true;
            foreach (Output output in _pending.Outputs) all &= output.Ready;
            if (all)
            {
                if (_active != null) DisposeSet(_active);
                _active = _pending; _pending = null;
            }
        }
        if (_active != null) foreach (Output output in _active.Outputs) if (output.Full.Visible != output.Ready) output.Full.Visible = output.Ready;
    }

    // ------------------------------------------------------------------ camera cull latch (the tree layer's rules, without the shadow state)

    private readonly record struct CullCamera(ulong Id, Camera3D.ProjectionType Projection, Camera3D.KeepAspectEnum KeepAspect, float Fov, float Size, Vector2 FrustumOffset,
        float Near, float Far, float HOffset, float VOffset, Vector2 Viewport);
    private bool _cullValid, _cullSentEnabled, _cullHavePrevious;
    private Transform3D _cullTransform;
    private CullCamera _cullCamera;
    private float _cullSpeedMargin;
    private Vector3 _cullPreviousOrigin;
    private ulong _cullHoldUntilUsec, _cullPreviousUsec;
    private Vector4[]? _cullPlanes;
    private Plane[]? _cullPlaneScratch;

    private void UpdateCulling(Camera3D camera, bool setsMoved)
    {
        ulong now = Time.GetTicksUsec();
        Transform3D frame = _active?.Root.GlobalTransform ?? Transform3D.Identity;
        Vector3 origin = frame.AffineInverse() * camera.GlobalTransform.Origin;
        float speed = 0f;
        if (_cullHavePrevious && now > _cullPreviousUsec) speed = origin.DistanceTo(_cullPreviousOrigin) / ((now - _cullPreviousUsec) * 1e-6f);
        _cullPreviousOrigin = origin; _cullPreviousUsec = now; _cullHavePrevious = true;
        // An anchor node moving (a floating-origin rebase) switches the filter off for half a second.
        if (setsMoved) _cullHoldUntilUsec = now + 500_000;
        bool held = now < _cullHoldUntilUsec;
        bool fast = speed > (_cullSentEnabled ? 120f : 90f);
        bool enabled = FrustumCulling && !held && !fast;
        CullCamera live = default;
        float neededMargin = Math.Min(speed * CullSpeedSeconds, 1e6f);
        string? reason = null;
        if (!_cullValid) reason = "initial";
        else if (enabled != _cullSentEnabled) reason = held ? "layer" : "enabled";
        else if (enabled)
        {
            live = new CullCamera(camera.GetInstanceId(), camera.Projection, camera.KeepAspect, camera.Fov, camera.Size, camera.FrustumOffset, camera.Near, camera.Far, camera.HOffset, camera.VOffset, GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero);
            Transform3D transform = camera.GlobalTransform;
            if (live != _cullCamera) reason = "projection";
            else if (transform.Origin.DistanceTo(_cullTransform.Origin) > CullMovementMeters) reason = "translation";
            else if (live.Projection == Camera3D.ProjectionType.Perspective ? GrassCullingFrustum.BasisAngleDegrees(_cullTransform.Basis, transform.Basis) > CullRotationDegrees : transform.Basis != _cullTransform.Basis) reason = "rotation";
            else if (neededMargin > _cullSpeedMargin) reason = "speed";
            else if (_cullSpeedMargin > neededMargin * 4f + 4f) reason = "settled";
        }
        if (reason == null) return;
        _cullValid = true; _cullSentEnabled = enabled;
        if (!enabled)
        {
            _compute!.RequestCameraCull(new RingGpuVegetationCompute.CameraCullState(false, Array.Empty<Vector4>(), CullWindFraction, CullGuardMeters, false));
            return;
        }
        if (live.Id == 0) live = new CullCamera(camera.GetInstanceId(), camera.Projection, camera.KeepAspect, camera.Fov, camera.Size, camera.FrustumOffset, camera.Near, camera.Far, camera.HOffset, camera.VOffset, GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero);
        _cullCamera = live;
        _cullTransform = camera.GlobalTransform;
        _cullSpeedMargin = neededMargin * 2f + .5f;
        bool perspective = live.Projection == Camera3D.ProjectionType.Perspective;
        Godot.Collections.Array<Plane> exact = camera.GetFrustum();
        int count = exact.Count;
        if (_cullPlanes is null || _cullPlanes.Length != count) { _cullPlanes = new Vector4[count]; _cullPlaneScratch = new Plane[count]; }
        float movement = CullMovementMeters + _cullSpeedMargin + Math.Abs(camera.HOffset) + Math.Abs(camera.VOffset) + Math.Abs(camera.FrustumOffset.X) + Math.Abs(camera.FrustumOffset.Y);
        GrassCullingFrustum.Pad(exact, _cullTransform, camera.Near, camera.Far, movement, perspective ? CullPaddingDegrees : 0f, perspective, _cullPlanes, _cullPlaneScratch!);
        _compute!.RequestCameraCull(new RingGpuVegetationCompute.CameraCullState(true, _cullPlanes, CullWindFraction, CullGuardMeters, false));
    }

    // ------------------------------------------------------------------ frame submission

    private float ShadowDistance()
    {
        DirectionalLight3D? sun = Host.Sun();
        if (sun == null || !GodotObject.IsInstanceValid(sun)) return 0f;
        float distance = sun.DirectionalShadowMaxDistance;
        // A POI sun-map extension raises the light's range; scatter keeps the near-map distance the controller records on the light.
        if (sun.HasMeta(SunNearDistanceMetaName)) distance = Mathf.Min(distance, (float)sun.GetMeta(SunNearDistanceMetaName));
        return float.IsFinite(distance) && distance > 0f ? distance : 0f;
    }

    private void SubmitFrame(Camera3D camera)
    {
        float scale = Mathf.Clamp(Host.DistanceScale(), .05f, 4f), shadow = ShadowDistance(), capScale = Math.Max(0f, CapScale);
        uint[] baseCaps = BaseCaps;
        if (_caps.Length != baseCaps.Length) _caps = new uint[baseCaps.Length];
        for (int i = 0; i < baseCaps.Length; i++) _caps[i] = (uint)Math.Round(baseCaps[i] * capScale);
        _frames.Clear();
        // Near LOD first inside a group (the batches of a group share one budget slot), so a binding cap drops far rows first. The shown set leads.
        foreach (OutputSet? set in new[] { _active, _pending })
        {
            if (set == null) continue;
            Transform3D world = set.Root.GlobalTransform;
            foreach (Output output in set.Outputs)
            {
                FrameTerms terms = ResolveFrame(output, scale, shadow);
                float gate = Mathf.Clamp(GateOf(output), 0f, 1f);
                _frames.Add(new RingGpuVegetationCompute.BatchFrameParameters(output.BatchId, world, false, terms.Pass, terms.Near, terms.Far, 0f, 1f, 1f, false, gate, false, null, 0f,
                    TreeFadeInStart: terms.InStart, TreeFadeInEnd: terms.InEnd, TreeFadeOutStart: terms.OutStart, TreeFadeOutEnd: terms.OutEnd));
            }
        }
        if (_frames.Count > 0) _compute!.RequestFrame(new RingGpuVegetationCompute.FrameRequest(camera.GlobalTransform.Origin, _caps, _frames, true));
    }

    public override void _ExitTree()
    {
        foreach (OutputSet? set in new[] { _active, _pending }) if (set != null) foreach (Output output in set.Outputs) _compute?.RemoveBatch(output.BatchId);
        _active = null; _pending = null;
        ReleaseAssets();
        _compute?.Dispose();
        _compute = null;
    }
}
