using System;
using Godot;
using RingworldTechDemo.Rendering;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Weather;

/// <summary>
/// Precipitation occlusion map producer (docs/storm-revamp-s1-design.md section 1, WO2b). A 512x512 orthographic SubViewport shares the main world and looks
/// down the ring "up" axis from a plane at the cloud base (clamped 40..1000 m above the observer); its own empty Compositor keeps SunshineClouds and the other
/// world compositor effects off it, and only RingPrecipOcclusionCapture.gd runs (POST_OPAQUE, depth -> R32F metres below the plane, ping-pong outputs).
/// Layers (RingRenderLayers): every Light3D moves to the Lights bit and grass/trees/player/weather meshes to NoPrecipitationOcclusion, both outside the map camera's cull mask.
///
/// Cadence: render on first activation, on a recentre (more than 16 m), a plane/height change, a terrain or construction edit revision, and every second.
/// A floating-origin rebase only republishes the world-space frame (the map content is canonical-relative), it never re-renders.
/// Active only in the Below phase (PrecipitationAltitude); otherwise the globals say "not ready" and every shader treats the world as exposed.
/// The 10 Hz cover probe (Probe partial) implements <see cref="IPrecipitationCoverProbe"/> and is reachable through <see cref="PrecipitationCover.Probe"/>.
/// </summary>
public partial class RingPrecipitationOcclusion : Node, IPrecipitationCoverProbe
{
    public static RingPrecipitationOcclusion? Instance { get; private set; }

    [Export] public NodePath GridPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath WeatherEffectsPath { get; set; } = new("../LocalWeatherEffects");
    [Export] public NodePath SmoothTerrainPath { get; set; } = new("../SmoothTerrainGrid");
    [Export] public bool Enabled { get; set; } = true;
    /// <summary>Seconds between refresh renders while nothing else triggers one.</summary>
    [Export] public double RefreshSeconds { get; set; } = 1.0;

    public static readonly StringName GlobalTexture = "ring_precip_occlusion", GlobalOrigin = "ring_precip_occlusion_origin", GlobalAround = "ring_precip_occlusion_around",
        GlobalAcross = "ring_precip_occlusion_across", GlobalState = "ring_precip_occlusion_state";
    private const string CaptureScript = "res://scripts/render/RingPrecipOcclusionCapture.gd";
    private const double PendingTimeoutSeconds = 3.0;

    /// <summary>Manual frame (tests, tools): the world-space map transform is supplied through SubmitManualFrame; grid, weather and phase logic are bypassed.</summary>
    public bool ManualFrame { get; set; }
    /// <summary>Observer/listener camera override (default: the main viewport's current camera).</summary>
    public Camera3D? CameraSource { get; set; }
    public bool Supported { get; private set; }
    /// <summary>True while a map is published to the shader globals.</summary>
    public new bool Ready { get; private set; }
    public int FrameSerial { get; private set; }
    /// <summary>Changes only when the canonical frame (centre/plane) changes or a manual frame is submitted, not on refresh renders of the same frame; the probe ties its freshness to it.</summary>
    public int FrameGeneration { get; private set; }
    public long RenderCount { get; private set; }
    public long RenderRequests { get; private set; }
    public string LastRenderReason { get; private set; } = "";
    public Transform3D PublishedTransform { get; private set; }
    public float PublishedFarMeters { get; private set; }
    public SubViewport? MapViewport => _viewport;
    public Texture2D? PublishedTexture { get; private set; }
    public int PublishedSlot => _publishedSlot;

    private SubViewport? _viewport;
    private Camera3D? _camera;
    private CompositorEffect? _capture;
    private VoxelChunkGridManager? _grid;
    private RingLocalWeatherEffects? _weather;
    private SmoothTerrainGrid? _smooth;
    private PrecipitationOcclusionFrame? _frame;
    private PrecipitationOcclusionFrame? _pendingFrame, _publishedFrame;
    private Transform3D _pendingTransform;
    private int _serialIssued, _pendingSerial, _pendingSlot = -1, _publishedSlot = -1;
    private bool _renderPending, _anchorSubscribed, _treeHooked, _forceRender;
    private double _lastRenderSeconds = double.NegativeInfinity, _pendingSinceSeconds;
    private ulong _editVersion = ulong.MaxValue, _smoothEditVersion = ulong.MaxValue;
    private static readonly StringName CompletedSerialName = "get_completed_serial", CompletedSlotName = "get_completed_slot", GetTextureName = "get_texture", RequestCaptureName = "request_capture";

    public override void _Ready()
    {
        Instance?.ReleaseOnExit(); Instance = this;
        PrecipitationCover.Probe = this;
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridPath);
        _weather = GetNodeOrNull<RingLocalWeatherEffects>(WeatherEffectsPath);
        _smooth = GetNodeOrNull<SmoothTerrainGrid>(SmoothTerrainPath);
        Supported = RenderingServer.GetRenderingDevice() != null;
        HookLayerPolicy();
        if (_grid != null) { _grid.CanonicalFloatingOriginAnchorChanged += OnFloatingOriginAnchorChanged; _anchorSubscribed = true; }
    }

    public override void _ExitTree()
    {
        if (_anchorSubscribed && _grid != null && IsInstanceValid(_grid)) _grid.CanonicalFloatingOriginAnchorChanged -= OnFloatingOriginAnchorChanged;
        _anchorSubscribed = false;
        UnhookLayerPolicy();
        if (!ReferenceEquals(Instance, this)) return;
        ReleaseOnExit();
        Instance = null;
    }

    private void ReleaseOnExit()
    {
        if (ReferenceEquals(PrecipitationCover.Probe, this)) PrecipitationCover.Probe = null;
        Deactivate();
        ReleaseProbe();
        SetDebugOverlay(false);
        if (_viewport != null && IsInstanceValid(_viewport)) _viewport.QueueFree();
        _viewport = null; _camera = null; _capture = null;
    }

    // ------------------------------------------------------------------ layer policy

    private void HookLayerPolicy()
    {
        if (_treeHooked) return;
        SceneTree tree = GetTree();
        // Lights and excluded geometry already in the tree, then everything added later (runtime lights such as LocalStormFlash, pooled grass/tree renderers).
        ApplyLayerPolicy(tree.Root);
        tree.NodeAdded += OnNodeAdded;
        _treeHooked = true;
    }

    private void UnhookLayerPolicy()
    {
        if (!_treeHooked) return;
        SceneTree? tree = GetTree();
        if (tree != null) tree.NodeAdded -= OnNodeAdded;
        _treeHooked = false;
    }

    private void OnNodeAdded(Node node)
    {
        if (node is Light3D or GeometryInstance3D) RingRenderLayers.Apply(node);
    }

    private static void ApplyLayerPolicy(Node node)
    {
        RingRenderLayers.Apply(node);
        foreach (Node child in node.GetChildren()) ApplyLayerPolicy(child);
    }

    // ------------------------------------------------------------------ frame

    private void OnFloatingOriginAnchorChanged(RingLodGeometrySnapshot previous, RingLodGeometrySnapshot current, RingCanonicalPosition anchor)
    {
        if (!ReferenceEquals(Instance, this) || ManualFrame) return;
        RefreshWorldFrame(current);
    }

    /// <summary>Rebase: the canonical frame is unchanged, only its world-space transform moves. Republish the globals and the pending camera, never render.</summary>
    private void RefreshWorldFrame(in RingLodGeometrySnapshot geometry)
    {
        if (_pendingFrame is { } pending && _camera != null) { _pendingTransform = pending.WorldTransform(geometry); _camera.GlobalTransform = _pendingTransform; }
        if (_publishedFrame is { } published)
        {
            Transform3D transform = published.WorldTransform(geometry);
            if (transform.Origin != PublishedTransform.Origin || !transform.Basis.IsEqualApprox(PublishedTransform.Basis)) { PublishedTransform = transform; PushGlobals(true); }
        }
    }

    public override void _Process(double delta)
    {
        if (!ReferenceEquals(Instance, this) || !Supported) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (!Enabled || (!ManualFrame && !GridPhaseActive())) { Deactivate(); UpdateDebugOverlay(); return; }
        EnsureViewport();
        if (_viewport == null || _capture == null) return;
        PollCompletion(now);
        if (!ManualFrame && _grid != null)
        {
            RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
            RefreshWorldFrame(geometry);
            if (!_renderPending) Schedule(geometry, now);
        }
        else if (ManualFrame && _forceRender && !_renderPending) IssueRender(null, _pendingTransform, "manual", now);
        TickProbe(now);
        UpdateDebugOverlay();
    }

    private bool GridPhaseActive() =>
        _grid != null && _grid.TerrainSampler != null && (_weather?.PrecipitationPhase ?? PrecipitationPhase.Below) == PrecipitationPhase.Below;

    private Vector3? ObserverWorld()
    {
        Camera3D? camera = CameraSource != null && IsInstanceValid(CameraSource) ? CameraSource : GetViewport()?.GetCamera3D();
        return camera != null && IsInstanceValid(camera) ? camera.GlobalPosition : null;
    }

    private void Schedule(in RingLodGeometrySnapshot geometry, double now)
    {
        if (_grid == null) return;
        Vector3? world = ObserverWorld();
        if (world == null) return;
        RingCanonicalPosition observer = _grid.WorldToCanonicalRingPosition64(world.Value);
        double circumference = geometry.CircumferenceMeters;
        double cloudBase = _weather?.PrecipitationCloudBaseMeters ?? double.NaN;
        string reason = "";
        bool rebuild = _frame is not { } current || current.NeedsRebuild(observer, cloudBase, circumference);
        if (rebuild) reason = _frame == null ? "first" : "recentre";
        ulong version = _grid.RainSurfaceEditVersion, smooth = _smooth?.EditRevision ?? 0;
        if (reason == "" && (version != _editVersion || smooth != _smoothEditVersion)) reason = "edit";
        if (reason == "" && now - _lastRenderSeconds >= RefreshSeconds) reason = "refresh";
        if (reason == "" && _forceRender) reason = "forced";
        if (reason == "") return;
        _editVersion = version; _smoothEditVersion = smooth;
        PrecipitationOcclusionFrame frame = rebuild ? PrecipitationOcclusionFrame.Create(observer, cloudBase, circumference) : _frame!.Value;
        _frame = frame;
        IssueRender(frame, frame.WorldTransform(geometry), reason, now);
    }

    private void IssueRender(PrecipitationOcclusionFrame? frame, Transform3D transform, string reason, double now)
    {
        if (_viewport == null || _camera == null || _capture == null) return;
        _forceRender = false;
        _pendingFrame = frame;
        _pendingTransform = transform;
        _pendingSlot = _publishedSlot == 0 ? 1 : 0;
        _pendingSerial = ++_serialIssued;
        _renderPending = true; _pendingSinceSeconds = now; _lastRenderSeconds = now; LastRenderReason = reason; RenderRequests++;
        _camera.GlobalTransform = transform;
        _camera.Far = frame?.FarMeters ?? PublishedFarMeters;
        _capture.Call(RequestCaptureName, _pendingSlot, _pendingSerial);
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    private void PollCompletion(double now)
    {
        if (!_renderPending || _capture == null) return;
        using Variant serialValue = _capture.Call(CompletedSerialName);
        if (serialValue.AsInt32() >= _pendingSerial)
        {
            using Variant slotValue = _capture.Call(CompletedSlotName);
            int slot = slotValue.AsInt32();
            using Variant textureValue = _capture.Call(GetTextureName, slot);
            if (textureValue.AsGodotObject() is Texture2D texture)
            {
                if (_pendingFrame == null || _pendingFrame != _publishedFrame) FrameGeneration++;
                _publishedSlot = slot; PublishedTexture = texture;
                _publishedFrame = _pendingFrame; PublishedTransform = _pendingTransform; PublishedFarMeters = _camera?.Far ?? PublishedFarMeters;
                FrameSerial = _pendingSerial; RenderCount++;
                _renderPending = false;
                PushGlobals(false);
            }
            return;
        }
        if (now - _pendingSinceSeconds > PendingTimeoutSeconds) { _renderPending = false; _forceRender = true; }
    }

    // ------------------------------------------------------------------ globals

    private void PushGlobals(bool transformOnly)
    {
        if (PublishedTexture == null) return;
        if (!transformOnly) RenderingServer.GlobalShaderParameterSet(GlobalTexture, PublishedTexture);
        Basis b = PublishedTransform.Basis;
        Vector3 o = PublishedTransform.Origin;
        RenderingServer.GlobalShaderParameterSet(GlobalOrigin, new Vector4(o.X, o.Y, o.Z, PublishedFarMeters));
        RenderingServer.GlobalShaderParameterSet(GlobalAround, new Vector4(b.Y.X, b.Y.Y, b.Y.Z, PrecipitationOcclusionFrame.HalfExtentMeters));
        RenderingServer.GlobalShaderParameterSet(GlobalAcross, new Vector4(b.X.X, b.X.Y, b.X.Z, PrecipitationOcclusionFrame.TexelMeters));
        RenderingServer.GlobalShaderParameterSet(GlobalState, new Vector4(1, PrecipitationOcclusionFrame.Resolution, FrameSerial, 0));
        Ready = true;
    }

    /// <summary>Leaves the globals neutral: every consumer sees an exposed world until the next publish.</summary>
    private void Deactivate()
    {
        if (!Ready && !_renderPending && _frame == null && _publishedFrame == null) return;
        if (Ready) RenderingServer.GlobalShaderParameterSet(GlobalState, new Vector4(0, PrecipitationOcclusionFrame.Resolution, FrameSerial, 0));
        Ready = false; _renderPending = false; _frame = null; _publishedFrame = null; _pendingFrame = null; _forceRender = false;
        InvalidateProbe();
    }

    /// <summary>Test/tool hook: world-space map transform (Z up, Y image-up) and plane height, bypassing the grid and weather. Renders on the next frame.</summary>
    public void SubmitManualFrame(Transform3D mapTransform, float planeHeightMeters)
    {
        ManualFrame = true;
        PublishedFarMeters = planeHeightMeters + (float)PrecipitationOcclusionFrame.FarMarginMeters;
        _pendingTransform = mapTransform;
        _frame = null; _forceRender = true;
    }

    /// <summary>Test/tool hook: a floating-origin rebase in manual mode. The world-space transform moves by <paramref name="worldDelta"/>; nothing is re-rendered.</summary>
    public void ShiftManualFrame(Vector3 worldDelta)
    {
        if (!ManualFrame) return;
        _pendingTransform = new Transform3D(_pendingTransform.Basis, _pendingTransform.Origin + worldDelta);
        if (Ready) { PublishedTransform = new Transform3D(PublishedTransform.Basis, PublishedTransform.Origin + worldDelta); PushGlobals(true); }
        if (_renderPending && _camera != null) _camera.GlobalTransform = _pendingTransform;
    }

    public void SetProbeListener(Vector3 world) { ProbeListenerWorldOverride = world; InvalidateProbe(); }
    public void ClearProbeListener() { ProbeListenerWorldOverride = null; InvalidateProbe(); }

    /// <summary>GDScript-friendly render statistics of the map viewport (see GetRenderInfo).</summary>
    public Godot.Collections.Dictionary GetRenderInfoDictionary()
    {
        var info = GetRenderInfo();
        return new Godot.Collections.Dictionary { ["objects"] = info.Objects, ["primitives"] = info.Primitives, ["draw_calls"] = info.DrawCalls, ["shadow_objects"] = info.ShadowObjects, ["shadow_draw_calls"] = info.ShadowDrawCalls };
    }

    /// <summary>Forces one refresh render on the next frame (manual mode, or to retest after changing fixtures).</summary>
    public void RequestRender() => _forceRender = true;

    // ------------------------------------------------------------------ viewport

    private void EnsureViewport()
    {
        if (_viewport != null && IsInstanceValid(_viewport)) return;
        GDScript? script = GD.Load<GDScript>(CaptureScript);
        _capture = script?.New().AsGodotObject() as CompositorEffect;
        if (_capture == null) { GD.PushWarning("Precipitation occlusion capture script unavailable; the map stays off."); Supported = false; return; }
        _viewport = new SubViewport
        {
            Name = "PrecipitationOcclusionViewport",
            Size = new Vector2I(PrecipitationOcclusionFrame.Resolution, PrecipitationOcclusionFrame.Resolution),
            OwnWorld3D = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            Msaa3D = Viewport.Msaa.Disabled,
            ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
            UseTaa = false,
            Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear,
            Scaling3DScale = 1.0f,
            MeshLodThreshold = 0.0f,
            DebugDraw = Viewport.DebugDrawEnum.Unshaded,
            PositionalShadowAtlasSize = 0,
            AudioListenerEnable3D = false,
            GuiDisableInput = true,
            HandleInputLocally = false,
            TransparentBg = false,
        };
        var environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = Colors.Black, AmbientLightSource = Godot.Environment.AmbientSource.Disabled };
        _camera = new Camera3D
        {
            Name = "PrecipitationOcclusionCamera",
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Size = PrecipitationOcclusionFrame.ExtentMeters,
            Near = PrecipitationOcclusionFrame.NearMeters,
            Far = 600.0f,
            CullMask = RingRenderLayers.PrecipitationMapCullMask,
            Environment = environment,
            // An EMPTY compositor: with none, SunshineClouds and RingFarBackdropComposite run for this camera (+57 ms per update in the spike).
            Compositor = new Compositor(),
        };
        var effects = new Godot.Collections.Array<CompositorEffect> { _capture };
        using Godot.Collections.Array lifetime = (Godot.Collections.Array)effects;
        _camera.Compositor.CompositorEffects = effects;
        // The map camera jumps (recentre, plane change) and renders once. With physics interpolation (on by default in gameplay) the first render of a new frame
        // came from part of the way between the old and the new transform, so after a cloud-base jump the published map put every surface hundreds of metres
        // too high for one refresh: the cover probe read "sheltered" and the wet world dried the ground for a second (S3 time-lapse, 2026-10-29). Never interpolate it.
        _viewport.PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off;
        _camera.PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off;
        _viewport.AddChild(_camera);
        _camera.Current = true;
        AddChild(_viewport);
    }


    /// <summary>Render statistics of the map viewport's last render: shadow draw calls must stay 0 (lights are masked out); primitives show what the map camera still draws.</summary>
    public (long Objects, long Primitives, long DrawCalls, long ShadowObjects, long ShadowDrawCalls) GetRenderInfo()
    {
        if (_viewport == null) return default;
        Rid rid = _viewport.GetViewportRid();
        long Info(RenderingServer.ViewportRenderInfoType type, RenderingServer.ViewportRenderInfo info) => RenderingServer.ViewportGetRenderInfo(rid, type, info);
        return (Info(RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.ObjectsInFrame),
            Info(RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.PrimitivesInFrame),
            Info(RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.DrawCallsInFrame),
            Info(RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.ObjectsInFrame),
            Info(RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.DrawCallsInFrame));
    }

    /// <summary>Raw map texture RID of the published slot (render-thread consumers: the probe, tests that read it back).</summary>
    public Rid PublishedTextureRid => PublishedTexture is Texture2Drd rd ? rd.TextureRdRid : default;
}
