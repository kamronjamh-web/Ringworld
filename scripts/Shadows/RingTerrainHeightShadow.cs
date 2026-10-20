using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Shadows;

public enum HeightShadowPhase { Disabled, WaitingForWorld, Idle, Held, Filling, Uploading, Building, Publishing, Failed }

/// <summary>
/// S4c scheduler of the terrain height-shadow producer (docs/shadow-revamp-plan.md S4; nothing reads the result until S5). Owns the CPU clipmap, the worker
/// fills, the render-thread GPU object, the three-layer Texture2DArray publish and the ringworld_height_shadow_* globals.
///
/// Job pipeline (strictly serial, one job at a time, the GPU params/height buffers are never touched mid-build):
///   Idle -> [Filling: worker strip/full fill (low priority) -> Uploading: scatter chunks + max-mips over a few frames] -> Building: row slices into the build
///   layer inside RingPerformanceGpu.BeginScope(HeightShadow) -> Publishing: the finished layer becomes the front layer in one main-thread globals update, the
///   old front crossfades out over CrossfadeSeconds (snap for the first publish, altitude band, light source or texel size change).
/// Triggers (checked only in Idle): no publish yet, window or band change (after the CPU strips), settings or world change, the light moved SunStepDegrees,
/// the light source switched (sun / planet / none). Holds (eclipse hub, underground) start nothing and keep the last result; on release the remembered light
/// direction is forgotten so the first frame after the hold always rebuilds from the real light.
/// </summary>
public partial class RingTerrainHeightShadow : Node
{
    public static RingTerrainHeightShadow? Instance { get; private set; }
    /// <summary>Underground-mode hold: start no new job, keep the published result.</summary>
    public bool UndergroundHold { get; set; }
    /// <summary>Transient (never persisted) switch for the receiver side: false writes light = 0 into ringworld_height_shadow_state so every receiver falls back to its previous path while the producer keeps running (A/B harness, perf labels).</summary>
    public bool ReceiversEnabled { get; set; } = true;
    [Export] public NodePath GridPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath GlobalLodPath { get; set; } = new("../GlobalTerrainLod3");
    [Export] public NodePath FocusPath { get; set; } = new("../Explorer");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export] public NodePath PlanetLightPath { get; set; } = new("../PlanetLight");
    [Export] public NodePath GraphicsPath { get; set; } = new("../GraphicsQuality");

    public const int UploadChunksPerFrame = 8;
    public const double OutsideReachMeters = 24000.0;
    public const float DefaultOutsideMaxMeters = 8000f;
    public const float OutsideMaxMarginMeters = 200f;
    public const float ReceiverBiasMeters = 0.5f;

    public RingHeightShadowSettings Settings { get; private set; } = new();
    public HeightShadowPhase Phase => _phase;
    public string Status => _status;
    public HeightShadowPublished Published => _roles.Current;
    public int AltitudeBand => _band;

    private VoxelChunkGridManager? _grid;
    private RingTerrainGlobalLod? _globalLod;
    private RingVoxelExplorerController? _focus;
    private DirectionalLight3D? _sun, _planet;
    private RingGraphicsQualityController? _graphics;

    private HeightShadowPhase _phase = HeightShadowPhase.Disabled;
    private string _status = "disabled";
    private readonly HeightShadowLayerRoles _roles = new();
    private RingHeightShadowClipmap? _clipmap;
    private IHeightShadowSource[]? _sources;
    private int _sourceWorkers, _sourceIdentity;
    private int _band;
    private bool _needsReset, _snapNext = true, _shutdownRequested;
    private readonly float[] _levelMax = new float[2];
    private HeightShadowLight _lastBuiltLight = HeightShadowLight.None;
    private Vector3 _lastBuiltDirection;
    private bool _haveLastBuiltDirection;

    private CancellationTokenSource? _fillCts;
    private Task<FillOutcome>? _fillTask;
    private readonly Queue<(int Level, HeightShadowSlotRect Rect)> _uploadQueue = new();
    private readonly bool[] _mipsDirty = new bool[2];
    private int _uploadCallbacksPosted;
    private bool _uploadFinalPosted;
    private BuildJob? _job;
    private int _nextJobId;
    private int _settingsRevision = -1;
    private float _outsideMax = DefaultOutsideMaxMeters;
    private long _outsideKeyI = long.MinValue, _outsideKeyJ = long.MinValue;
    private int _outsideKeyTexel;
    private bool _outsideUsedPyramid;

    // Published description (what the globals describe)
    private readonly HeightShadowIndexRect?[] _frontRect = new HeightShadowIndexRect?[2];
    private readonly HeightShadowIndexRect?[] _prevRect = new HeightShadowIndexRect?[2];
    private readonly int[] _frontTexel = new int[2];
    private double _frontRadius;
    private double _lastCameraAround;

    // Diagnostics
    public long FillsCompleted { get; private set; }
    public long BuildsCompleted { get; private set; }
    public long PublishCount { get; private set; }
    public double LastFillSeconds { get; private set; }
    public long LastFillTexels { get; private set; }
    public long LastFillRects { get; private set; }
    public double LastBuildWallSeconds { get; private set; }
    public int LastBuildFrames { get; private set; }
    public double LastMainThreadMs { get; private set; }
    public double MainThreadMsTotal { get; private set; }
    public long MainThreadFrames { get; private set; }
    public int LastRowsPerFrame { get; private set; }

    private sealed class FillOutcome
    {
        public readonly List<HeightShadowIndexRect>[] Filled = { new(), new() };
        public readonly bool[] Moved = new bool[2];
        public readonly float[] LevelMax = new float[2];
        public double Seconds;
        public long Texels, Rects;
        public Exception? Error;
        public bool Cancelled;
    }

    private sealed class BuildJob
    {
        public int Id;
        public HeightShadowBuildInputs Inputs = new();
        public readonly HeightShadowIndexRect?[] Rects = new HeightShadowIndexRect?[2];
        public readonly int[] Texel = new int[2];
        public HeightShadowLight Light;
        public Vector3 LightDirection;
        public Vector3 LightWorld;
        public bool Snap;
        public int Layer;
        public HeightShadowBuildCursor Cursor = new(2);
        public bool InputsSent;
        public int Slices;
        public Stopwatch Wall = Stopwatch.StartNew();
        public double ElevationRadians;
    }

    public void Configure(RingHeightShadowSettings settings)
    {
        RingHeightShadowSettings next = settings.Normalized();
        if (next == Settings) return;
        RingHeightShadowSettings old = Settings;
        Settings = next;
        if (old.L0TexelMeters != next.L0TexelMeters || old.Levels != next.Levels) _needsReset = true;
        if (old.FillWorkers != next.FillWorkers) _sourceWorkers = -1;
        if (old.Enabled && !next.Enabled) _shutdownRequested = true;
    }

    public override void _Ready()
    {
        Instance?.ReleaseOnExit(); Instance = this;
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridPath);
        _globalLod = GetNodeOrNull<RingTerrainGlobalLod>(GlobalLodPath);
        _focus = GetNodeOrNull<RingVoxelExplorerController>(FocusPath);
        _sun = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        _planet = GetNodeOrNull<DirectionalLight3D>(PlanetLightPath);
        _graphics = GetNodeOrNull<RingGraphicsQualityController>(GraphicsPath);
        if (_grid != null) { _grid.CanonicalFloatingOriginAnchorChanged += OnFloatingOriginAnchorChanged; _anchorSubscribed = true; }
    }

    private bool _anchorSubscribed;

    /// <summary>The floating origin moved: the frame matrix must follow in the same frame (PushGlobals otherwise runs a frame late, in _Process, while the chunks already shifted).</summary>
    private void OnFloatingOriginAnchorChanged(RingLodGeometrySnapshot previous, RingLodGeometrySnapshot current, RingCanonicalPosition anchor)
    {
        if (!ReferenceEquals(Instance, this) || !Settings.Enabled) return;
        try { PushGlobals(current, 0f); }
        catch (Exception error) { GD.PushError("height shadow frame refresh failed: " + error.Message); }
    }

    public override void _ExitTree()
    {
        if (_anchorSubscribed && _grid != null && GodotObject.IsInstanceValid(_grid)) _grid.CanonicalFloatingOriginAnchorChanged -= OnFloatingOriginAnchorChanged;
        _anchorSubscribed = false;
        if (!ReferenceEquals(Instance, this)) return;
        ReleaseOnExit();
        Instance = null;
    }

    private void ReleaseOnExit()
    {
        ShutdownPipeline(true);
        InvalidateGlobals();
    }

    public override void _Process(double delta)
    {
        if (!ReferenceEquals(Instance, this)) return;
        var watch = Stopwatch.StartNew();
        try { Step((float)delta); }
        catch (Exception error)
        {
            Fail("height shadow scheduler failed: " + error.Message);
            GD.PushError(error.ToString());
        }
        watch.Stop();
        LastMainThreadMs = watch.Elapsed.TotalMilliseconds;
        MainThreadMsTotal += LastMainThreadMs; MainThreadFrames++;
    }

    private void Fail(string message)
    {
        _phase = HeightShadowPhase.Failed;
        _status = message;
        ShutdownPipeline(false);
        InvalidateGlobals();
    }

    private void Step(float delta)
    {
        if (_graphics != null && GodotObject.IsInstanceValid(_graphics) && _graphics.HeightShadowRevision != _settingsRevision)
        {
            _settingsRevision = _graphics.HeightShadowRevision;
            Configure(_graphics.ResolveHeightShadowSettings());
        }
        if (_phase == HeightShadowPhase.Failed)
        {
            if (!Settings.Enabled) { _phase = HeightShadowPhase.Disabled; _status = "disabled"; }
            return;
        }
        if (!Settings.Enabled)
        {
            if (_phase != HeightShadowPhase.Disabled || _shutdownRequested) { ShutdownPipeline(false); InvalidateGlobals(); }
            _shutdownRequested = false; _phase = HeightShadowPhase.Disabled; _status = "disabled";
            return;
        }
        if (_grid == null || _focus == null || _sun == null || !GodotObject.IsInstanceValid(_grid) || !GodotObject.IsInstanceValid(_focus) || !GodotObject.IsInstanceValid(_sun))
        {
            _phase = HeightShadowPhase.WaitingForWorld; _status = "waiting for world"; return;
        }
        if (_grid.TerrainSampler == null) { _phase = HeightShadowPhase.WaitingForWorld; _status = "waiting for terrain sampler"; return; }
        if (_phase == HeightShadowPhase.Disabled) _phase = HeightShadowPhase.WaitingForWorld;

        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        int identity = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_grid.TerrainSampler);
        if (_sourceIdentity != 0 && identity != _sourceIdentity) _needsReset = true;

        if (!HandleReset(identity)) { PushGlobals(geometry, delta); return; }
        _roles.Advance(delta, Settings.CrossfadeSeconds);
        PollGpuLink();

        switch (_phase)
        {
            case HeightShadowPhase.Filling: StepFilling(geometry); break;
            case HeightShadowPhase.Uploading: StepUploading(geometry); break;
            case HeightShadowPhase.Building: StepBuilding(geometry); break;
            case HeightShadowPhase.Publishing: StepPublishing(geometry); break;
            default: StepIdle(geometry); break;
        }
        PushGlobals(geometry, delta);
    }

    // ------------------------------------------------------------------ reset / shutdown

    /// <summary>Applies a pending reset once no fill is running. Returns false while waiting for a cancelled fill to end.</summary>
    private bool HandleReset(int identity)
    {
        if (_sourceWorkers == -1 && _fillTask == null && _phase is HeightShadowPhase.Idle or HeightShadowPhase.WaitingForWorld or HeightShadowPhase.Held)
        {
            DisposeSources();
            _sourceWorkers = 0;
        }
        if (!_needsReset) return true;
        if (_fillTask != null) { _fillCts?.Cancel(); if (!_fillTask.IsCompleted) return false; DrainFill(); }
        _clipmap = null;
        DisposeSources();
        _job = null; _uploadQueue.Clear(); _uploadFinalPosted = false;
        _roles.DiscardBuild();
        _roles.Invalidate();
        _haveLastBuiltDirection = false; _lastBuiltLight = HeightShadowLight.None;
        _snapNext = true; _band = 0;
        _frontRect[0] = _frontRect[1] = null; _prevRect[0] = _prevRect[1] = null;
        _outsideKeyI = long.MinValue;
        _needsReset = false;
        _phase = HeightShadowPhase.Idle; _status = "reset";
        _sourceIdentity = identity;
        return true;
    }

    private void ShutdownPipeline(bool exiting)
    {
        try { _fillCts?.Cancel(); } catch (ObjectDisposedException) { }
        if (_fillTask != null && !exiting) { /* the worker ends on its own; its outcome is dropped */ }
        _fillTask = null;
        _clipmap = null;
        DisposeSources();
        _job = null; _uploadQueue.Clear(); _uploadFinalPosted = false;
        _roles.DiscardBuild(); _roles.Invalidate();
        _haveLastBuiltDirection = false; _lastBuiltLight = HeightShadowLight.None;
        _snapNext = true; _band = 0; _sourceIdentity = 0;
        _frontRect[0] = _frontRect[1] = null; _prevRect[0] = _prevRect[1] = null;
        ReleaseGpu(exiting);
    }

    private void DisposeSources()
    {
        if (_sources == null) return;
        foreach (IHeightShadowSource source in _sources) source.Dispose();
        _sources = null;
    }

    // ------------------------------------------------------------------ idle: triggers

    private bool Held => UndergroundHold || EclipseHubLighting.HoldMacroShadows;

    private void StepIdle(RingLodGeometrySnapshot geometry)
    {
        if (Held)
        {
            _haveLastBuiltDirection = false; // forget: the first frame after the release rebuilds from the real light
            _phase = HeightShadowPhase.Held; _status = UndergroundHold ? "held (underground)" : "held (eclipse hub)";
            return;
        }
        _phase = HeightShadowPhase.Idle;
        HeightShadowLight light = ResolveLight(geometry, out Vector3 localDirection);
        if (light == HeightShadowLight.None)
        {
            if (_roles.Front >= 0 || _roles.Light != HeightShadowLight.None) { _roles.Invalidate(); _frontRect[0] = _frontRect[1] = null; _prevRect[0] = _prevRect[1] = null; }
            _haveLastBuiltDirection = false; _lastBuiltLight = HeightShadowLight.None; _snapNext = true;
            _status = "idle (no light)";
            return;
        }
        if (!EnsureSources(out string sourceError)) { _status = sourceError; return; }
        if (!EnsureClipmap(geometry, out string clipError)) { Fail(clipError); return; }
        if (!EnsureGpu()) { _status = "starting GPU"; return; }

        bool fill = PlanFill(geometry, out long[] wantIc, out long[] wantJc);
        if (fill)
        {
            StartFill(wantIc, wantJc);
            return;
        }
        if (!_roles.CanBeginBuild) return;
        bool trigger = _roles.Front < 0 || light != _lastBuiltLight || !_haveLastBuiltDirection
            || AngleDegrees(_lastBuiltDirection, localDirection) >= Settings.SunStepDegrees;
        _status = _roles.Front < 0 ? "waiting for first build" : "ready";
        if (trigger) StartBuild(geometry, light, localDirection);
    }

    private HeightShadowLight ResolveLight(RingLodGeometrySnapshot geometry, out Vector3 localDirection)
    {
        localDirection = default;
        Vector3 world;
        HeightShadowLight light;
        bool sunSuspended = _graphics != null && GodotObject.IsInstanceValid(_graphics) && (_graphics.LocalSunShadowSuspended || _graphics.UndergroundShadowSuspended);
        if (_sun != null && _sun.LightEnergy > RingGraphicsQualityController.SunShadowMinimumEnergy && !sunSuspended)
        {
            world = _sun.GlobalBasis.Z.Normalized(); light = HeightShadowLight.Sun;
        }
        else if (_planet != null && GodotObject.IsInstanceValid(_planet) && _planet.LightEnergy > RingGraphicsQualityController.SunShadowMinimumEnergy
            && (_graphics == null || !GodotObject.IsInstanceValid(_graphics) || _graphics.EffectivePlanetShadowsEnabled))
        {
            world = _planet.GlobalBasis.Z.Normalized(); light = HeightShadowLight.Planet;
        }
        else return HeightShadowLight.None;
        localDirection = (geometry.GridGlobalTransform.Basis.Inverse() * world).Normalized();
        return light;
    }

    /// <summary>Angle between two unit vectors in degrees (atan2 form: exact for the 0.02 degree sun steps where acos(dot) is not).</summary>
    public static double AngleDegrees(Vector3 a, Vector3 b)
    {
        double ax = a.X, ay = a.Y, az = a.Z, bx = b.X, by = b.Y, bz = b.Z;
        double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
        return Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), ax * bx + ay * by + az * bz) * 180.0 / Math.PI;
    }

    private bool EnsureSources(out string error)
    {
        error = "";
        int workers = Settings.FillWorkers;
        if (_sources != null && _sourceWorkers == workers) return true;
        DisposeSources();
        try { _sources = RingTerrainHeightShadowSource.CreateWorkerSources(_grid!, workers); _sourceWorkers = workers; return true; }
        catch (Exception e) { error = "height shadow: terrain sampler unavailable (" + e.Message + ")"; return false; }
    }

    private bool EnsureClipmap(RingLodGeometrySnapshot geometry, out string error)
    {
        error = "";
        IHeightShadowSource source = _sources![0];
        int t0 = RingHeightShadowLattice.TexelForBand(Settings.L0TexelMeters, _band);
        t0 = FitTexelToWidth(t0, source.WidthMeters);
        if (_clipmap != null && _clipmap.Circumference == source.CircumferenceMeters && _clipmap.Width == source.WidthMeters)
        {
            if (_clipmap.Level0TexelMeters == t0) return true;
            _clipmap.Reset(t0); // band change: both windows lose their data, full refill + snap publish
            _snapNext = true;
            return true;
        }
        try
        {
            RingHeightShadowLattice.AssertConfiguration(source.CircumferenceMeters, source.WidthMeters, t0);
            _clipmap = new RingHeightShadowClipmap(source.CircumferenceMeters, source.WidthMeters, t0) { WorkerPriority = ThreadPriority.BelowNormal };
            _snapNext = true;
            return true;
        }
        catch (ArgumentException e) { error = "height shadow unsupported on this ring: " + e.Message; return false; }
    }

    /// <summary>Largest power-of-two texel (&lt;= wanted) whose level-1 window (1024 x 4 t) still fits between the walls.</summary>
    public static int FitTexelToWidth(int wantedTexel, int widthMeters)
    {
        int t = wantedTexel;
        while (t > 1 && widthMeters - 2 < (long)RingHeightShadowLattice.TextureSize * RingHeightShadowLattice.Level1Texel(t)) t >>= 1;
        return t;
    }

    // ------------------------------------------------------------------ fill

    private bool PlanFill(RingLodGeometrySnapshot geometry, out long[] wantIc, out long[] wantJc)
    {
        wantIc = new long[2]; wantJc = new long[2];
        RingCanonicalPositionView camera = Camera();
        _lastCameraAround = camera.Around;
        UpdateBand(camera);
        RingHeightShadowClipmap clipmap = _clipmap!;
        if (clipmap.Level0TexelMeters != FitTexelToWidth(RingHeightShadowLattice.TexelForBand(Settings.L0TexelMeters, _band), clipmap.Width))
        {
            clipmap.Reset(FitTexelToWidth(RingHeightShadowLattice.TexelForBand(Settings.L0TexelMeters, _band), clipmap.Width));
            _snapNext = true;
        }
        bool need = false;
        for (int l = 0; l < Settings.Levels; l++)
        {
            RingHeightShadowLevel level = clipmap.Levels[l];
            int t = level.TexelMeters;
            wantIc[l] = level.Window is { } current ? RingHeightShadowLattice.UnwrapNear(camera.Around, current.Ic, t, clipmap.Circumference) : RingHeightShadowLattice.NearestIndex(camera.Around, t);
            wantJc[l] = RingHeightShadowLattice.NearestIndex(camera.Across, t);
            if (level.Window is not { } window) { need = true; continue; }
            RingHeightShadowLattice.NextWindow(window, wantIc[l], wantJc[l], t, clipmap.Width, out bool moved);
            need |= moved;
        }
        return need;
    }

    private readonly record struct RingCanonicalPositionView(double Around, double Across, double Vertical);

    private RingCanonicalPositionView Camera()
    {
        var p = _focus!.CanonicalPosition;
        return new RingCanonicalPositionView(p.AroundMeters, p.AcrossMeters, p.VerticalMeters);
    }

    private void UpdateBand(RingCanonicalPositionView camera)
    {
        RingHeightShadowClipmap clipmap = _clipmap!;
        RingHeightShadowLevel l0 = clipmap.Levels[0];
        if (l0.Window is not { } window) return;
        int t = l0.TexelMeters;
        long ic = RingHeightShadowLattice.UnwrapNear(camera.Around, window.Ic, t, clipmap.Circumference);
        long jc = RingHeightShadowLattice.NearestIndex(camera.Across, t);
        if (!window.Rect.Contains(ic, jc)) return;
        float surface = l0.Heights[RingHeightShadowLattice.SlotOf(jc) * RingHeightShadowLattice.TextureSize + RingHeightShadowLattice.SlotOf(ic)];
        _band = RingHeightShadowLattice.UpdateBand(_band, camera.Vertical - surface);
    }

    private void StartFill(long[] wantIc, long[] wantJc)
    {
        RingHeightShadowClipmap clipmap = _clipmap!;
        IHeightShadowSource[] sources = _sources!;
        int levels = Settings.Levels;
        _fillCts?.Dispose();
        _fillCts = new CancellationTokenSource();
        CancellationToken ct = _fillCts.Token;
        long[] ic = (long[])wantIc.Clone(), jc = (long[])wantJc.Clone();
        _fillTask = Task.Run(() =>
        {
            var outcome = new FillOutcome();
            var watch = Stopwatch.StartNew();
            try
            {
                for (int l = 0; l < levels; l++)
                {
                    HeightShadowFillReport report = clipmap.Recentre(l, ic[l], jc[l], sources, ct, out bool moved, outcome.Filled[l]);
                    outcome.Moved[l] = moved;
                    outcome.Texels += report.Texels; outcome.Rects += report.Rects;
                    if (moved) outcome.LevelMax[l] = RingTerrainHeightShadowGpu.LevelMaxHeight(clipmap, l);
                }
            }
            catch (OperationCanceledException) { outcome.Cancelled = true; }
            catch (Exception e) { outcome.Error = e; }
            outcome.Seconds = watch.Elapsed.TotalSeconds;
            return outcome;
        }, CancellationToken.None);
        _phase = HeightShadowPhase.Filling;
        _status = "filling heights";
    }

    private FillOutcome? DrainFill()
    {
        Task<FillOutcome>? task = _fillTask;
        _fillTask = null;
        if (task == null || !task.IsCompleted) return null;
        FillOutcome outcome = task.Result;
        if (outcome.Cancelled || outcome.Error != null)
        {
            // A cancelled or failed fill leaves the master partially written: forget its windows so the next fill refills completely.
            if (_clipmap != null) _clipmap.Reset(_clipmap.Level0TexelMeters);
            _snapNext = true;
            if (outcome.Error != null) Fail("height shadow fill failed: " + outcome.Error.Message);
            return null;
        }
        return outcome;
    }

    private void StepFilling(RingLodGeometrySnapshot geometry)
    {
        if (_fillTask == null || !_fillTask.IsCompleted) return;
        FillOutcome? outcome = DrainFill();
        if (outcome == null) { if (_phase != HeightShadowPhase.Failed) _phase = HeightShadowPhase.Idle; return; }
        FillsCompleted++;
        LastFillSeconds = outcome.Seconds; LastFillTexels = outcome.Texels; LastFillRects = outcome.Rects;
        _uploadQueue.Clear(); _mipsDirty[0] = _mipsDirty[1] = false;
        for (int l = 0; l < Settings.Levels; l++)
        {
            if (!outcome.Moved[l]) continue;
            _levelMax[l] = outcome.LevelMax[l];
            _mipsDirty[l] = true;
            foreach (HeightShadowIndexRect rect in outcome.Filled[l]) EnqueueUpload(l, rect);
        }
        _uploadFinalPosted = false; _uploadCallbacksPosted = 0;
        ResetUploadCounters();
        _phase = HeightShadowPhase.Uploading;
        _status = "uploading heights";
    }

    private void EnqueueUpload(int level, HeightShadowIndexRect rect)
    {
        Span<HeightShadowSlotRect> slots = stackalloc HeightShadowSlotRect[4];
        int count = RingHeightShadowLattice.SplitToSlots(rect, slots);
        for (int i = 0; i < count; i++)
        {
            HeightShadowSlotRect slot = slots[i];
            int rowsPerChunk = Math.Max(1, RingHeightShadowLattice.ScatterChunkFloats / slot.Width);
            for (int row = 0; row < slot.Height; row += rowsPerChunk)
                _uploadQueue.Enqueue((level, new HeightShadowSlotRect(slot.X, slot.Y + row, slot.Width, Math.Min(rowsPerChunk, slot.Height - row))));
        }
    }

    private void StepUploading(RingLodGeometrySnapshot geometry)
    {
        if (!_uploadFinalPosted)
        {
            if (!GpuReady) { if (GpuFailed) Fail("height shadow GPU failed: " + GpuError); return; }
            var items = new List<(int Level, HeightShadowSlotRect Rect)>(UploadChunksPerFrame);
            while (items.Count < UploadChunksPerFrame && _uploadQueue.Count > 0) items.Add(_uploadQueue.Dequeue());
            bool final = _uploadQueue.Count == 0;
            QueueUpload(items, final ? (bool[])_mipsDirty.Clone() : null, _clipmap!);
            _uploadFinalPosted = final;
            if (!final) return;
        }
        if (!UploadCallbacksFinished(_uploadCallbacksPosted)) return;
        // Heights are on the GPU: start the build from the freshest light.
        _phase = HeightShadowPhase.Idle;
        HeightShadowLight light = ResolveLight(geometry, out Vector3 localDirection);
        if (light == HeightShadowLight.None || Held) { _status = "idle (no light)"; return; }
        StartBuild(geometry, light, localDirection);
    }

    // ------------------------------------------------------------------ build

    private void StartBuild(RingLodGeometrySnapshot geometry, HeightShadowLight light, Vector3 localDirection)
    {
        RingHeightShadowClipmap clipmap = _clipmap!;
        for (int l = 0; l < Settings.Levels; l++) if (clipmap.Levels[l].Window == null) return;
        double radius = geometry.RingRadiusMeters - geometry.RingVerticalBaseOffsetMeters;
        var job = new BuildJob { Id = ++_nextJobId, Light = light, LightDirection = localDirection, LightWorld = (geometry.GridGlobalTransform.Basis * localDirection).Normalized() };
        HeightShadowBuildInputs inputs = job.Inputs;
        int t1 = clipmap.Levels[1].TexelMeters;
        HeightShadowWindow w0 = clipmap.Levels[0].Window!.Value;
        long anchor = RingHeightShadowLattice.AnchorMeters(w0.Ic * (double)clipmap.Levels[0].TexelMeters, t1);
        inputs.RingRadiusMeters = radius;
        inputs.WidthMeters = clipmap.Width;
        inputs.AnchorMeters = anchor;
        double theta = RingHeightShadowLattice.PosMod(anchor, clipmap.Circumference) / radius;
        inputs.AnchorSun = RingHeightShadowLattice.ProjectToTangentFrame(localDirection.X, localDirection.Y, localDirection.Z, theta);
        inputs.Walls = _globalLod != null && GodotObject.IsInstanceValid(_globalLod) && _globalLod.BuildStructuralEdgeWalls && _globalLod.EnableMacroTerrainWallShadows;
        inputs.WallTopMeters = _grid!.GetStructuralWallTopGlobalY();
        for (int l = 0; l < 2; l++)
        {
            job.Texel[l] = clipmap.Levels[l].TexelMeters;
            inputs.TexelMeters[l] = clipmap.Levels[l].TexelMeters;
            if (l < Settings.Levels)
            {
                inputs.Windows[l] = clipmap.Levels[l].Window;
                job.Rects[l] = clipmap.Levels[l].Window!.Value.Rect;
                inputs.LevelMax[l] = _levelMax[l];
            }
        }
        inputs.OutsideMaxMeters = ResolveOutsideMax(clipmap);
        job.Snap = _snapNext || _roles.Front < 0 || light != _roles.Light || light != _lastBuiltLight || TexelChanged(job.Texel);
        job.Cursor = new HeightShadowBuildCursor(Settings.Levels);
        job.Layer = _roles.BeginBuild();
        double upSin = SunUpSine(localDirection, _lastCameraAround, radius);
        job.ElevationRadians = Math.Asin(Math.Clamp(upSin, -1, 1));
        _job = job;
        _lastBuiltLight = light; _lastBuiltDirection = localDirection; _haveLastBuiltDirection = true; _snapNext = false;
        _phase = HeightShadowPhase.Building;
        _status = "building";
    }

    private bool TexelChanged(int[] texel) => _frontTexel[0] != texel[0] || _frontTexel[1] != texel[1];

    private static double SunUpSine(Vector3 localDirection, double aroundMeters, double radius)
    {
        double angle = aroundMeters / radius;
        return -(localDirection.X * Math.Cos(angle) + localDirection.Z * Math.Sin(angle));
    }

    private float ResolveOutsideMax(RingHeightShadowClipmap clipmap)
    {
        if (_globalLod == null || !GodotObject.IsInstanceValid(_globalLod) || Settings.Levels < 2) return DefaultOutsideMaxMeters;
        RingHeightShadowLevel l1 = clipmap.Levels[1];
        HeightShadowWindow w = l1.Window!.Value;
        if (_outsideKeyI == w.Ic && _outsideKeyJ == w.Jc && _outsideKeyTexel == l1.TexelMeters && (_outsideUsedPyramid || _outsideRetry-- > 0)) return _outsideMax;
        int t = l1.TexelMeters;
        double a0 = w.I0 * (double)t - OutsideReachMeters, a1 = (w.I1 + 1) * (double)t + OutsideReachMeters;
        double c0 = w.J0 * (double)t - OutsideReachMeters, c1 = (w.J1 + 1) * (double)t + OutsideReachMeters;
        _outsideKeyI = w.Ic; _outsideKeyJ = w.Jc; _outsideKeyTexel = t; _outsideRetry = 120;
        if (_globalLod.TryGetTerrainHeightRange(a0, a1, c0, c1, out RingTerrainMinMaxPyramid.HeightRange range))
        {
            _outsideMax = Math.Max(range.MaxHeight, 0) + OutsideMaxMarginMeters;
            _outsideUsedPyramid = true;
        }
        else { _outsideMax = DefaultOutsideMaxMeters; _outsideUsedPyramid = false; }
        return _outsideMax;
    }
    private int _outsideRetry;

    private void StepBuilding(RingLodGeometrySnapshot geometry)
    {
        BuildJob job = _job!;
        if (!job.Cursor.Done)
        {
            if (!GpuReady) { if (GpuFailed) Fail("height shadow GPU failed: " + GpuError); return; }
            int level = job.Cursor.Level;
            double msPerRow = GpuMsPerRow(level);
            if (!double.IsFinite(msPerRow) || msPerRow <= 0) msPerRow = HeightShadowSlicing.ModelMsPerRow(level, job.ElevationRadians);
            int rows = HeightShadowSlicing.Rows(Settings.RowsPerFrame, Settings.BudgetMs, msPerRow);
            LastRowsPerFrame = rows;
            (int sliceLevel, int row, int count) = job.Cursor.Take(rows);
            bool last = job.Cursor.Done;
            QueueBuildSlice(job, sliceLevel, row, count, last);
            job.InputsSent = true;
            job.Slices++;
            return;
        }
        if (!BuildRecorded(job.Id)) return;
        _roles.CompleteBuild();
        BuildsCompleted++;
        LastBuildFrames = job.Slices;
        LastBuildWallSeconds = job.Wall.Elapsed.TotalSeconds;
        _phase = HeightShadowPhase.Publishing;
        _status = "publishing";
        StepPublishing(geometry);
    }

    private void StepPublishing(RingLodGeometrySnapshot geometry)
    {
        BuildJob job = _job!;
        if (!_roles.CanPublish(job.Snap)) { _status = "waiting for crossfade"; return; }
        PublishBuild(job);
        _job = null;
        _phase = HeightShadowPhase.Idle;
        _status = "published";
    }

    private void PublishBuild(BuildJob job)
    {
        HeightShadowIndexRect?[] oldFront = { _frontRect[0], _frontRect[1] };
        int[] oldTexel = { _frontTexel[0], _frontTexel[1] };
        bool snap = job.Snap || _roles.Front < 0;
        _roles.Publish(snap, job.Light);
        for (int l = 0; l < 2; l++)
        {
            HeightShadowIndexRect? newRect = job.Rects[l];
            _prevRect[l] = !snap && oldFront[l] is { } old && newRect is { } now && oldTexel[l] == job.Texel[l] ? HeightShadowFrameMath.Intersect(old, now) : null;
            _frontRect[l] = newRect;
            _frontTexel[l] = job.Texel[l];
        }
        _frontRadius = job.Inputs.RingRadiusMeters;
        _frontLightWorld = job.LightWorld;
        PublishCount++;
        _globalsDirty = true;
    }

    // ------------------------------------------------------------------ status / diagnostics

    /// <summary>Texel size of the published level (0 when nothing is published).</summary>
    public int FrontTexelMeters(int level) => _frontRect[level] != null ? _frontTexel[level] : 0;

    public string GetStatusText()
    {
        if (!Settings.Enabled) return "Height shadows: off.";
        string window = _frontRect[0] is { } w ? $"L0 {w.Width * _frontTexel[0] / 1000.0:0.#} km @ {_frontTexel[0]} m" + (_frontRect[1] != null ? $", L1 {_frontRect[1]!.Value.Width * _frontTexel[1] / 1000.0:0.#} km @ {_frontTexel[1]} m" : "") : "no window";
        string unresolved = UnresolvedPercent(0) is { } u0 ? $"{u0:0.#}%" + (UnresolvedPercent(1) is { } u1 ? $" / {u1:0.#}%" : "") : "n/a";
        if (UnresolvedPercent(0) is not null)
            unresolved += $" (shadowed so far {(UnresolvedShadowedPercent(0) is { } s0 ? $"{s0:0.#}%" : "n/a")}" + (UnresolvedShadowedPercent(1) is { } s1 ? $" / {s1:0.#}%" : "") + ")";
        var gpuMs = GpuMsPerRow(0);
        return $"Height shadows: {_phase} ({_status}); generation {_roles.Generation}; light {_roles.Light}; {window}; band {_band}; unresolved {unresolved}"
            + (double.IsFinite(gpuMs) ? $"; build {LastBuildFrames} frames" : "") + ".";
    }

    public Godot.Collections.Dictionary GetDiagnostics()
    {
        var published = _roles.Current;
        var result = new Godot.Collections.Dictionary
        {
            ["enabled"] = Settings.Enabled, ["phase"] = _phase.ToString(), ["status"] = _status, ["generation"] = (long)published.Generation,
            ["front"] = published.Front, ["prev"] = published.Prev, ["crossfade"] = published.Crossfade, ["light"] = (int)published.Light,
            ["build_layer"] = _roles.Build, ["build_in_flight"] = _roles.BuildInFlight, ["build_complete_pending"] = _roles.BuildComplete,
            ["band"] = _band, ["l0_texel"] = _frontTexel[0], ["l1_texel"] = _frontTexel[1],
            ["fills"] = FillsCompleted, ["builds"] = BuildsCompleted, ["publishes"] = PublishCount,
            ["fill_seconds"] = LastFillSeconds, ["fill_texels"] = LastFillTexels, ["fill_rects"] = LastFillRects,
            ["build_frames"] = LastBuildFrames, ["build_wall_seconds"] = LastBuildWallSeconds, ["rows_per_frame"] = LastRowsPerFrame,
            ["main_thread_ms_last"] = LastMainThreadMs, ["main_thread_ms_avg"] = MainThreadFrames > 0 ? MainThreadMsTotal / MainThreadFrames : 0.0,
            ["outside_max"] = _outsideMax, ["outside_from_pyramid"] = _outsideUsedPyramid, ["held"] = Held, ["gpu_ready"] = GpuReady, ["gpu_error"] = GpuError,
        };
        AddGpuDiagnostics(result);
        for (int l = 0; l < 2; l++)
        {
            if (_frontRect[l] is { } r) { result[$"l{l}_window"] = new Godot.Collections.Array<long> { r.I0, r.J0, r.Width, r.Height }; }
            if (_prevRect[l] is { } p) { result[$"l{l}_prev_window"] = new Godot.Collections.Array<long> { p.I0, p.J0, p.Width, p.Height }; }
        }
        return result;
    }

    /// <summary>The ground-plane view of the debug overlay: a world point under the camera and two unit world directions (around, across).</summary>
    public bool TryGetOverlayView(out Vector3 origin, out Vector3 axisU, out Vector3 axisV)
    {
        origin = axisU = axisV = default;
        if (_frameRows == null) return false;
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera == null) return false;
        origin = camera.GlobalPosition;
        axisU = new Vector3((float)_frameRows[0], (float)_frameRows[1], (float)_frameRows[2]).Normalized();
        axisV = new Vector3((float)_frameRows[4], (float)_frameRows[5], (float)_frameRows[6]).Normalized();
        return true;
    }
}
