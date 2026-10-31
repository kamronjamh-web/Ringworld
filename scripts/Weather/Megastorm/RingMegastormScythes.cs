using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>
/// Megastorm lightning scythes near the observer (docs/megastorm-design.md 5.5.4, WO12). The stream, paths and pulses are the pure
/// <see cref="MegastormScythes"/>; this node only draws and plays them, re-deriving everything from the weather clock, so a rewind or reload clears and
/// rebuilds the same scythes. Per scythe: the sheet (a box ribbon 50-200 m wide, 2-8 m thick, 0.5-4 m above the 100 m smoothed ground) with a bright
/// centre-line core, the branch filaments (the 4 nearest only) that re-light per return stroke, a scorch strip that cools and fades over 30 s and a
/// gust-debris dust burst. The nearest scythe is also lit by 3 shadowless omni lights riding its tip, middle and attachment, and its flash centroid
/// drives the Sunshine flash intensity. Crackle one-shots (one per path segment, each at its own d / 343 delay) play on the megastorm air bus, gated
/// like thunder. Untriggered it costs one bool check per frame.
/// </summary>
public partial class RingMegastormScythes : Node
{
    [Export] public bool Enabled { get; set; } = true;
    [Export] public NodePath MegastormPath { get; set; } = new("../RingMegastorm");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath CloudPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath BridgePath { get; set; } = new("../SunshineCloudBridge");

    public const int MaxActive = 10, MaxDetailed = 4, MaxScorch = 8, MaxCreatesPerFrame = 2, VoicePool = 16, MaxPendingCrackles = 400;
    public const double ReachMeters = 25000.0, DetailReachMeters = 8000.0, AudioReachMeters = 15000.0, ScorchSeconds = 30.0, LightReachMeters = 12000.0;
    private const float LightRangeMeters = 1400.0f, LightEnergy = 16.0f, BranchWidthMeters = 2.5f, CoreWidthMeters = 3.5f;
    private const string AudioRoot = "res://resources/audio/weather/";
    private static readonly Color BoltColor = new(0.72f, 0.86f, 1.0f, 1.0f);

    /// <summary>Test seam: replaces the observer and the terrain height.</summary>
    public Func<RingCanonicalPosition?>? ObserverProvider { get; set; }
    public Func<double, double, double>? SurfaceProvider { get; set; }
    /// <summary>Test seam: replaces the airborne gate (default: the megastorm audio mix at the observer).</summary>
    public Func<float>? AirGateProvider { get; set; }

    private sealed class ActiveScythe
    {
        public MegastormScythe S;
        public ScythePoint[] Points = new ScythePoint[MegastormScythes.MaxPoints];
        public int N;
        public bool Detailed;
        public Node3D Root = null!;
        public MeshInstance3D Sheet = null!;
        public MeshInstance3D? Branches;
        public ShaderMaterial SheetMat = null!;
        public ShaderMaterial? BranchMat;
        public RingCanonicalPosition Origin;
        public Vector3 LastOrigin = new(float.NaN, 0, 0);
        public float Distance;
        public ScytheState State;
        public Vector3[]? CoreWorld;   // channel overlay: world positions of the path points at the core height
        public Vector3 CoreOrigin = new(float.NaN, 0, 0);
    }

    /// <summary>One scythe core line for the post-cloud lightning channel overlay (RingLocalWeatherEffects.LightningChannels.cs). Points is a reused buffer, valid for Count entries until the next call.</summary>
    public readonly record struct ChannelCore(Vector3[] Points, int Count, float TipFraction, float Intensity, float LengthMeters, long Id);

    public static RingMegastormScythes? Instance { get; private set; }

    /// <summary>Clears <paramref name="output"/> and adds one core per visible, pulsing scythe. World points refresh only when the floating origin moved.</summary>
    public int CopyChannelCores(List<ChannelCore> output)
    {
        output.Clear();
        if (_grid == null) return 0;
        RingLodGeometrySnapshot geometry = default;
        bool haveGeometry = false;
        foreach (ActiveScythe a in _active)
        {
            float intensity = a.State.Pulse * a.S.Energy;
            // Kept while visible even between strokes (intensity 0 is culled on the GPU), so a core does not leave and re-enter the set, which would
            // force a segment rebuild on every stroke gap.
            if (!a.State.Visible || !(intensity >= 0.0f) || a.N < 2 || a.S.LengthMeters <= 0.0f) continue;
            a.CoreWorld ??= new Vector3[MegastormScythes.MaxPoints];
            if (!a.CoreOrigin.IsEqualApprox(a.LastOrigin) || float.IsNaN(a.CoreOrigin.X))
            {
                if (!haveGeometry) { geometry = _grid.CaptureLodGeometrySnapshot(); haveGeometry = true; }
                for (int i = 0; i < a.N; i++)
                    a.CoreWorld[i] = geometry.CanonicalToWorld(a.Points[i].AroundMeters, a.Points[i].LowerEdgeMeters + 0.5 * a.S.ThicknessMeters, a.Points[i].AcrossMeters);
                a.CoreOrigin = a.LastOrigin;
            }
            output.Add(new ChannelCore(a.CoreWorld, a.N, a.State.TipDistanceMeters / a.S.LengthMeters, intensity, a.S.LengthMeters, a.S.Id));
        }
        return output.Count;
    }

    private sealed class ScorchStrip
    {
        public MegastormScythe S;
        public MeshInstance3D Mesh = null!;
        public ShaderMaterial Mat = null!;
        public RingCanonicalPosition Origin;
        public Vector3 LastOrigin = new(float.NaN, 0, 0);
    }

    private readonly List<ActiveScythe> _active = new();
    private readonly List<ScorchStrip> _scorch = new();
    private readonly List<MegastormScythe> _scratch = new();
    private readonly List<ScytheCrackle> _crackleScratch = new();
    private readonly List<(ScytheCrackle Crackle, RingCanonicalPosition Position)> _pending = new();
    private readonly OmniLight3D[] _lights = new OmniLight3D[3];
    private readonly AudioStreamPlayer3D[] _voices = new AudioStreamPlayer3D[VoicePool];
    private readonly GpuParticles3D[] _dust = new GpuParticles3D[4];
    private readonly double[] _dustUntil = new double[4];
    private AudioStream[] _crackleStreams = Array.Empty<AudioStream>();
    private Shader? _sheetShader, _scorchShader;
    private StandardMaterial3D? _dustMaterial;
    private RingMegastorm? _megastorm;
    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private RingVolumetricCloudLayer? _clouds;
    private RingSunshineCloudBridge? _bridge;
    private double _lastNow = double.NegativeInfinity;
    private bool _flashPublished;

    // ---- Diagnostics (tests and captures) ----
    public int ActiveCount => _active.Count;
    public int DetailedCount { get { int n = 0; foreach (ActiveScythe a in _active) if (a.Detailed) n++; return n; } }
    public int ScorchCount => _scorch.Count;
    public int PendingCrackles => _pending.Count;
    public int CreatedCount { get; private set; }
    public int CrackleStarts { get; private set; }
    public int CrackleDropped { get; private set; }
    public float LastCrackleGateDb { get; private set; }
    public double LastCrackleArrival { get; private set; } = double.NaN;
    public int LastCracklePointIndex { get; private set; } = -1;
    /// <summary>Flash centroid of the nearest scythe in world space (valid while <see cref="FlashLevel"/> is above 0) and its level (pulse x energy x distance weight).</summary>
    public Vector3 FlashCentroidWorld { get; private set; }
    public float FlashLevel { get; private set; }
    public int LightsLit { get { int n = 0; foreach (OmniLight3D l in _lights) if (l != null && l.LightEnergy > 0.0f) n++; return n; } }
    public float MaxLightEnergy { get { float m = 0; foreach (OmniLight3D l in _lights) if (l != null) m = Math.Max(m, l.LightEnergy); return m; } }
    /// <summary>The distance of the nearest live scythe's chord midpoint (ground plane), +Infinity when none.</summary>
    public float NearestDistanceMeters { get; private set; } = float.PositiveInfinity;
    /// <summary>Capture debugging: the active scythes' ids, root world positions, state and the camera-relative offset of the root.</summary>
    public string DescribeActive(Vector3 camera)
    {
        var sb = new System.Text.StringBuilder();
        foreach (ActiveScythe a in _active)
            sb.Append($"[id={a.S.Id} detailed={a.Detailed} root={a.Root.GlobalPosition} rel={a.Root.GlobalPosition - camera} d={a.Distance:F0} pulse={a.State.Pulse:F2} tip={a.State.TipDistanceMeters:F0} len={a.S.LengthMeters:F0} skim0={a.Points[0].SkimMeters:F1} gnd0={a.Points[0].SmoothedGroundMeters:F1} lower0={a.Points[0].LowerEdgeMeters:F1} aabb={a.Sheet.Mesh?.GetAabb()} s0={SurfaceAabbs((ArrayMesh?)a.Sheet.Mesh)} p0=({a.Points[0].AroundMeters:F0},{a.Points[0].AcrossMeters:F0}) pn=({a.Points[a.N - 1].AroundMeters:F0},{a.Points[a.N - 1].AcrossMeters:F0}) dir={a.S.Direction} surfaces={((ArrayMesh?)a.Sheet.Mesh)?.GetSurfaceCount()} visible={a.Sheet.IsVisibleInTree()} shader={(a.SheetMat.Shader != null)} intensity={a.SheetMat.GetShaderParameter("intensity")} tip={a.SheetMat.GetShaderParameter("tip_fraction")}] ");
        return sb.ToString();
    }
    private static string SurfaceAabbs(ArrayMesh? mesh)
    {
        if (mesh == null) return "-";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < mesh.GetSurfaceCount(); i++)
        {
            Godot.Collections.Array arrays = mesh.SurfaceGetArrays(i);
            Vector3[] v = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
            Vector3 lo = v[0], hi = v[0];
            foreach (Vector3 p in v) { lo = lo.Min(p); hi = hi.Max(p); }
            sb.Append($"surf{i}:n={v.Length} {lo}..{hi};");
        }
        return sb.ToString();
    }
    public Godot.Collections.Dictionary GetScytheDiagnostics() => new()
    {
        ["active"] = ActiveCount, ["detailed"] = DetailedCount, ["scorch"] = ScorchCount, ["created"] = CreatedCount, ["pending_crackles"] = PendingCrackles,
        ["crackle_starts"] = CrackleStarts, ["crackle_dropped"] = CrackleDropped, ["lights_lit"] = LightsLit, ["flash_level"] = FlashLevel,
        ["nearest_distance"] = NearestDistanceMeters, ["process_us"] = ProcessMicrosAverage, ["crackle_streams"] = _crackleStreams.Length
    };

    public override void _Ready()
    {
        _megastorm = GetNodeOrNull<RingMegastorm>(MegastormPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridPath);
        _clouds = GetNodeOrNull<RingVolumetricCloudLayer>(CloudPath);
        _bridge = GetNodeOrNull<RingSunshineCloudBridge>(BridgePath);
        Instance = this;
    }

    /// <summary>Exponential average (about 60 frames) of this node's own _Process time in microseconds, for the perf captures.</summary>
    public double ProcessMicrosAverage { get; private set; }

    public override void _Process(double delta)
    {
        ulong start = Time.GetTicksUsec();
        ProcessInner();
        ProcessMicrosAverage += (Time.GetTicksUsec() - start - ProcessMicrosAverage) / 60.0;
    }

    private void ProcessInner()
    {
        if (_megastorm == null || !Enabled || !_megastorm.Triggered)
        {
            if (_active.Count > 0 || _scorch.Count > 0 || _pending.Count > 0 || _flashPublished) ClearAll();
            return;
        }
        RingCanonicalPosition? observer = ObserverProvider != null ? ObserverProvider() : _explorer?.CanonicalPosition;
        if (observer == null || _grid == null) return;
        Step(_megastorm.Now, observer.Value);
    }

    public override void _ExitTree() { if (Instance == this) Instance = null; ClearAll(); }

    /// <summary>One update at weather time <paramref name="now"/>; public so a test or capture can drive it.</summary>
    public void Step(double now, RingCanonicalPosition observer)
    {
        if (_megastorm == null || _grid == null) return;
        MegastormModel model = _megastorm.Model;
        EnsureResources();
        if (now < _lastNow - 1e-9) ClearAll();   // rewind or reload: rebuild the present from the pure stream
        _lastNow = now;
        double from = Math.Max(0.0, now - MegastormScythes.MaxLifetimeSeconds);
        float scale = LightningVisualPolicy.NaturalStrikeChanceScale;
        bool reduced = LightningVisualPolicy.ReducedFlashes;
        if (scale > 0.0f)
        {
            // The pure window is re-scanned each frame (about 90 cell rolls); a scythe already active is skipped, one past its life is not created.
            MegastormScythes.ScythesInWindow(model, from, Math.BitIncrement(now), observer.AroundMeters, observer.AcrossMeters, ReachMeters, scale, _scratch);
            int created = 0;
            foreach (MegastormScythe s in _scratch)
            {
                if (now - s.StartTimeSeconds > s.LifetimeSeconds || _active.Exists(x => x.S.Id == s.Id)) continue;
                if (_active.Count >= MaxActive || created >= MaxCreatesPerFrame) break;   // picked up next frame while still inside its life
                Create(model, s, observer, now);
                created++;
            }
        }
        UpdateActive(model, now, observer, reduced);
        UpdateScorch(now);
        FirePendingCrackles(model, now, observer);
    }

    private void EnsureResources()
    {
        if (_sheetShader != null) return;
        _sheetShader = GD.Load<Shader>("res://materials/ring_megastorm_scythe.gdshader");
        _scorchShader = GD.Load<Shader>("res://materials/ring_megastorm_scorch.gdshader");
        for (int i = 0; i < _lights.Length; i++)
        {
            _lights[i] = new OmniLight3D
            {
                Name = "ScytheLight" + i, TopLevel = true, ShadowEnabled = false, LightColor = BoltColor, LightEnergy = 0.0f, OmniRange = LightRangeMeters,
                LightVolumetricFogEnergy = 2.4f, Visible = false
            };
            AddChild(_lights[i]);
        }
        var streams = new List<AudioStream>();
        for (int i = 1; i <= 3; i++)
        {
            string path = $"{AudioRoot}megastorm_scythe_crackle_{i}.ogg";
            if (ResourceLoader.Exists(path) && ResourceLoader.Load<AudioStream>(path) is { } stream) streams.Add(stream);
        }
        _crackleStreams = streams.ToArray();
        int bus = AudioServer.GetBusIndex(RingMegastormAudio.AirBus);
        string busName = bus >= 0 ? RingMegastormAudio.AirBus : "Master";
        for (int i = 0; i < _voices.Length; i++)
        {
            _voices[i] = new AudioStreamPlayer3D
            {
                Name = "ScytheCrackle" + i, TopLevel = true, Bus = busName, VolumeDb = -80f, MaxPolyphony = 1, MaxDistance = 20000f, UnitSize = 250f,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
                DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled
            };
            AddChild(_voices[i]);
        }
        _dustMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, VertexColorUseAsAlbedo = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = new Color(0.34f, 0.30f, 0.26f, 0.32f)
        };
        for (int i = 0; i < _dust.Length; i++)
        {
            var process = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(1750f, 1.5f, 60f),
                Direction = new Vector3(0, 1, 0), Spread = 35f, InitialVelocityMin = 2f, InitialVelocityMax = 9f, Gravity = new Vector3(0, -0.8f, 0),
                ScaleMin = 14f, ScaleMax = 34f, DampingMin = 0.2f, DampingMax = 0.6f
            };
            _dust[i] = new GpuParticles3D
            {
                Name = "ScytheDust" + i, TopLevel = true, Amount = 90, Lifetime = 4.0, OneShot = true, Emitting = false, Explosiveness = 0.0f, LocalCoords = false,
                ProcessMaterial = process, DrawPass1 = new QuadMesh { Size = new Vector2(1, 1), Material = _dustMaterial },
                VisibilityAabb = new Aabb(new Vector3(-4000, -200, -4000), new Vector3(8000, 600, 8000)), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(_dust[i]);
        }
    }

    // ---- Creation ----
    private void Create(MegastormModel model, in MegastormScythe s, RingCanonicalPosition observer, double now)
    {
        Func<double, double, double>? surface = SurfaceProvider ?? _clouds?.CreateSurfaceHeightSampler();
        Func<double, double, double>? smoothed = surface != null ? (a, z) => MegastormScythes.SmoothedGround(surface, a, z) : null;
        var active = new ActiveScythe { S = s };
        active.N = MegastormScythes.PathPoints(model, s, smoothed, active.Points);
        if (active.N < 2) return;
        double c = model.Settings.CircumferenceMeters;
        (double midAround, double midAcross) = MegastormScythes.PositionAt(active.Points, active.N, s, s.LengthMeters * 0.5f, c);
        double dx = MegastormModel.WrapSigned(midAround - observer.AroundMeters, c), dz = midAcross - observer.AcrossMeters;
        active.Distance = (float)Math.Sqrt(dx * dx + dz * dz);
        active.Detailed = active.Distance <= DetailReachMeters && DetailedCount < MaxDetailed;
        active.Origin = new RingCanonicalPosition(active.Points[0].AroundMeters, active.Points[0].LowerEdgeMeters, active.Points[0].AcrossMeters);
        RingLodGeometrySnapshot geometry = _grid!.CaptureLodGeometrySnapshot();
        Vector3 origin = geometry.CanonicalToWorld(active.Origin.AroundMeters, active.Origin.VerticalMeters, active.Origin.AcrossMeters);
        active.Root = new Node3D { Name = "Scythe" + (s.Id & 0xFFFFFF), TopLevel = true };
        AddChild(active.Root);
        active.Root.GlobalPosition = origin;
        active.LastOrigin = origin;
        active.SheetMat = NewSheetMaterial(0.0f);
        active.Sheet = new MeshInstance3D
        {
            Name = "Sheet", Mesh = ScytheMeshBuilder.BuildSheet(geometry, model, active.S, active.Points, active.N, origin, CoreWidthMeters),
            MaterialOverride = active.SheetMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, IgnoreOcclusionCulling = true
        };
        active.Root.AddChild(active.Sheet);
        if (active.Detailed)
        {
            active.BranchMat = NewSheetMaterial(1.0f);
            active.Branches = new MeshInstance3D
            {
                Name = "Branches", Mesh = ScytheMeshBuilder.BuildBranches(geometry, model, active.S, active.Points, active.N, origin, BranchWidthMeters),
                MaterialOverride = active.BranchMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, IgnoreOcclusionCulling = true
            };
            active.Root.AddChild(active.Branches);
            CreateScorch(geometry, model, active, surface);
            Dust(model, active, geometry, now);
        }
        if (active.Distance <= AudioReachMeters && _crackleStreams.Length > 0)
            QueueCrackles(model, active, observer);
        _active.Add(active);
        CreatedCount++;
    }

    private ShaderMaterial NewSheetMaterial(float isBranch)
    {
        var m = new ShaderMaterial { Shader = _sheetShader };
        m.SetShaderParameter("bolt_color", BoltColor);
        m.SetShaderParameter("intensity", 0.0f);
        m.SetShaderParameter("tip_fraction", 0.0f);
        m.SetShaderParameter("emission_energy", 8.0f);
        m.SetShaderParameter("is_branch", isBranch);
        return m;
    }

    private void CreateScorch(RingLodGeometrySnapshot geometry, MegastormModel model, ActiveScythe a, Func<double, double, double>? surface)
    {
        while (_scorch.Count >= MaxScorch) RemoveScorch(0);
        Vector3 origin = geometry.CanonicalToWorld(a.Origin.AroundMeters, a.Origin.VerticalMeters, a.Origin.AcrossMeters);
        var mat = new ShaderMaterial { Shader = _scorchShader };
        mat.SetShaderParameter("age_seconds", 0.0f);
        mat.SetShaderParameter("tip_fraction", 0.0f);
        mat.SetShaderParameter("fade_seconds", (float)ScorchSeconds);
        var strip = new ScorchStrip
        {
            S = a.S, Mat = mat, Origin = a.Origin, LastOrigin = origin,
            Mesh = new MeshInstance3D
            {
                Name = "Scorch" + (a.S.Id & 0xFFFFFF), TopLevel = true, Mesh = ScytheMeshBuilder.BuildScorch(geometry, model, a.S, a.Points, a.N, origin, surface),
                MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, IgnoreOcclusionCulling = true
            }
        };
        AddChild(strip.Mesh);
        strip.Mesh.GlobalPosition = origin;
        _scorch.Add(strip);
    }

    private void RemoveScorch(int index)
    {
        ScorchStrip strip = _scorch[index];
        _scorch.RemoveAt(index);
        if (GodotObject.IsInstanceValid(strip.Mesh)) strip.Mesh.QueueFree();
    }

    private void Dust(MegastormModel model, ActiveScythe a, RingLodGeometrySnapshot geometry, double now)
    {
        int slot = -1;
        for (int i = 0; i < _dust.Length; i++) if (!_dust[i].Emitting && now >= _dustUntil[i]) { slot = i; break; }
        if (slot < 0) return;
        double c = model.Settings.CircumferenceMeters;
        (double midAround, double midAcross) = MegastormScythes.PositionAt(a.Points, a.N, a.S, a.S.LengthMeters * 0.5f, c);
        float ground = a.Points[a.N / 2].SmoothedGroundMeters;
        Vector3 mid = geometry.CanonicalToWorld(midAround, ground + 1.0, midAcross);
        Vector3 along = (geometry.CanonicalToWorld(midAround + a.S.Direction * 100.0, ground + 1.0, midAcross) - mid).Normalized();
        Vector3 up = (geometry.CanonicalToWorld(midAround, ground + 11.0, midAcross) - mid).Normalized();
        Vector3 side = along.Cross(up).Normalized();
        along = up.Cross(side).Normalized();
        ((ParticleProcessMaterial)_dust[slot].ProcessMaterial).EmissionBoxExtents = new Vector3(a.S.LengthMeters * 0.5f, 1.5f, a.S.WidthMeters * 0.5f);
        _dust[slot].GlobalTransform = new Transform3D(new Basis(along, up, side), mid);
        _dust[slot].Amount = 90;
        _dust[slot].Restart();
        _dust[slot].Emitting = true;
        _dustUntil[slot] = now + _dust[slot].Lifetime;
    }

    // ---- Per frame ----
    private void UpdateActive(MegastormModel model, double now, RingCanonicalPosition observer, bool reduced)
    {
        double c = model.Settings.CircumferenceMeters;
        ActiveScythe? nearest = null;
        float nearestDistance = float.PositiveInfinity;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            ActiveScythe a = _active[i];
            a.State = MegastormScythes.StateAt(model, a.S, now, reduced);
            if (!a.State.Visible)
            {
                a.Root.QueueFree(); _active.RemoveAt(i);
                continue;
            }
            Vector3 origin = _grid!.CanonicalRingPositionToWorld(a.Origin);
            if (!origin.IsEqualApprox(a.LastOrigin)) { a.Root.GlobalPosition = origin; a.LastOrigin = origin; }   // floating-origin shift
            float intensity = a.State.Pulse * a.S.Energy;
            float tip = a.State.TipDistanceMeters / a.S.LengthMeters;
            a.SheetMat.SetShaderParameter("intensity", intensity);
            a.SheetMat.SetShaderParameter("tip_fraction", tip);
            if (a.BranchMat != null)
            {
                a.BranchMat.SetShaderParameter("intensity", intensity);
                a.BranchMat.SetShaderParameter("tip_fraction", tip);
                a.BranchMat.SetShaderParameter("stroke_seed", reduced ? 0.0f : (float)a.State.StrokesFired);
            }
            (double midAround, double midAcross) = MegastormScythes.PositionAt(a.Points, a.N, a.S, a.State.TipDistanceMeters * 0.5f, c);
            double dx = MegastormModel.WrapSigned(midAround - observer.AroundMeters, c), dz = midAcross - observer.AcrossMeters;
            a.Distance = (float)Math.Sqrt(dx * dx + dz * dz);
            if (a.Distance < nearestDistance) { nearestDistance = a.Distance; nearest = a; }
        }
        NearestDistanceMeters = nearestDistance;
        UpdateLightsAndFlash(model, nearest, c);
    }

    private void UpdateLightsAndFlash(MegastormModel model, ActiveScythe? nearest, double circumference)
    {
        float level = 0.0f;
        if (nearest != null && nearest.Distance <= LightReachMeters && nearest.State.Pulse > 0.0f)
        {
            ActiveScythe a = nearest;
            float energy = a.State.Pulse * a.S.Energy;
            RingLodGeometrySnapshot geometry = _grid!.CaptureLodGeometrySnapshot();
            float[] fractions = { 1.0f, 0.5f, 0.0f };   // tip, middle, attachment
            Vector3 sum = Vector3.Zero;
            for (int i = 0; i < 3; i++)
            {
                (double around, double across) = MegastormScythes.PositionAt(a.Points, a.N, a.S, a.State.TipDistanceMeters * fractions[i], circumference);
                int index = Math.Clamp((int)Math.Round((a.State.TipDistanceMeters * fractions[i]) / a.S.LengthMeters * (a.N - 1)), 0, a.N - 1);
                Vector3 position = geometry.CanonicalToWorld(around, a.Points[index].LowerEdgeMeters + 0.5f * a.S.ThicknessMeters + 4.0, across);
                _lights[i].Visible = true;
                _lights[i].GlobalPosition = position;
                _lights[i].LightEnergy = LightEnergy * energy * (i == 0 ? 1.0f : 0.7f);
                sum += position;
            }
            FlashCentroidWorld = sum / 3.0f;
            level = energy * (1.0f - (float)Mathf.SmoothStep(LightReachMeters * 0.5f, LightReachMeters, a.Distance));
        }
        else foreach (OmniLight3D light in _lights) { light.LightEnergy = 0.0f; light.Visible = false; }
        FlashLevel = level;
        if (level > 0.0f) { _bridge?.SetLightningFlashIntensity(level); _flashPublished = true; }
        else if (_flashPublished) { _bridge?.SetLightningFlashIntensity(0.0f); _flashPublished = false; }
    }

    private void UpdateScorch(double now)
    {
        for (int i = _scorch.Count - 1; i >= 0; i--)
        {
            ScorchStrip strip = _scorch[i];
            double age = now - strip.S.StartTimeSeconds;
            if (age < 0.0 || age > ScorchSeconds) { RemoveScorch(i); continue; }
            Vector3 origin = _grid!.CanonicalRingPositionToWorld(strip.Origin);
            if (!origin.IsEqualApprox(strip.LastOrigin)) { strip.Mesh.GlobalPosition = origin; strip.LastOrigin = origin; }
            strip.Mat.SetShaderParameter("age_seconds", (float)age);
            strip.Mat.SetShaderParameter("tip_fraction", (float)Math.Min(1.0, strip.S.TipSpeedMetersPerSecond * age / strip.S.LengthMeters));
        }
    }

    // ---- Crackle audio ----
    private void QueueCrackles(MegastormModel model, ActiveScythe a, RingCanonicalPosition observer)
    {
        MegastormScythes.CrackleSchedule(model, a.S, a.Points, a.N, observer.AroundMeters, observer.AcrossMeters, _crackleScratch);
        foreach (ScytheCrackle crackle in _crackleScratch)
        {
            ScythePoint p = a.Points[crackle.PointIndex];
            _pending.Add((crackle, new RingCanonicalPosition(p.AroundMeters, p.LowerEdgeMeters + 0.5 * a.S.ThicknessMeters, p.AcrossMeters)));
        }
        _pending.Sort((x, y) => x.Crackle.ArrivalTimeSeconds.CompareTo(y.Crackle.ArrivalTimeSeconds));
        while (_pending.Count > MaxPendingCrackles)
        {
            // Keep the nearest: drop the most distant of the queue.
            int far = 0;
            for (int i = 1; i < _pending.Count; i++) if (_pending[i].Crackle.DistanceMeters > _pending[far].Crackle.DistanceMeters) far = i;
            _pending.RemoveAt(far); CrackleDropped++;
        }
    }

    private void FirePendingCrackles(MegastormModel model, double now, RingCanonicalPosition observer)
    {
        if (_pending.Count == 0 || _crackleStreams.Length == 0) return;
        float gate = -1.0f;
        int fired = 0;
        while (_pending.Count > 0 && _pending[0].Crackle.ArrivalTimeSeconds <= now)
        {
            (ScytheCrackle crackle, RingCanonicalPosition position) = _pending[0];
            _pending.RemoveAt(0);
            if (now - crackle.ArrivalTimeSeconds > 0.6) { CrackleDropped++; continue; }   // a big step or a stall: the moment has passed
            if (gate < 0.0f) gate = AirGateProvider != null ? AirGateProvider() : (_megastorm != null ? MegastormAudioMix.AirborneGate(_megastorm.QueryNow(observer)) : 1.0f);
            if (!(gate > 0.001f)) { CrackleDropped++; continue; }
            AudioStreamPlayer3D? voice = Array.Find(_voices, v => !v.Playing);
            if (voice == null || fired >= 8) { CrackleDropped++; continue; }
            int variant = (int)((crackle.PointIndex * 7 + crackle.Stroke * 3 + (position.AroundMeters > 0 ? (long)position.AroundMeters % 3 : 0)) % _crackleStreams.Length);
            voice.Stream = _crackleStreams[variant];
            voice.GlobalPosition = _grid!.CanonicalRingPositionToWorld(position);
            float volumeDb = -4.0f + Mathf.LinearToDb(Mathf.Clamp(crackle.Amplitude, 0.05f, 1.0f)) + Mathf.LinearToDb(gate);
            voice.VolumeDb = volumeDb;
            voice.PitchScale = 0.9f + 0.2f * (float)((crackle.PointIndex * 37 + crackle.Stroke * 11) % 10) / 10.0f;
            voice.Play((float)Math.Max(0.0, now - crackle.ArrivalTimeSeconds));
            CrackleStarts++; fired++;
            LastCrackleGateDb = Mathf.LinearToDb(gate);
            LastCrackleArrival = crackle.ArrivalTimeSeconds;
            LastCracklePointIndex = crackle.PointIndex;
        }
    }

    // ---- Lifetime ----
    private void ClearAll()
    {
        foreach (ActiveScythe a in _active) if (GodotObject.IsInstanceValid(a.Root)) a.Root.QueueFree();
        _active.Clear();
        for (int i = _scorch.Count - 1; i >= 0; i--) RemoveScorch(i);
        _pending.Clear();
        _lastNow = double.NegativeInfinity;
        foreach (OmniLight3D light in _lights) if (light != null) { light.LightEnergy = 0.0f; light.Visible = false; }
        foreach (AudioStreamPlayer3D voice in _voices) voice?.Stop();
        foreach (GpuParticles3D dust in _dust) if (dust != null) dust.Emitting = false;
        Array.Clear(_dustUntil);
        FlashLevel = 0.0f; NearestDistanceMeters = float.PositiveInfinity;
        if (_flashPublished) { _bridge?.SetLightningFlashIntensity(0.0f); _flashPublished = false; }
    }
}

/// <summary>Mesh builders of a scythe (sheet box, core ribbon, branch filaments, scorch strip): positions in a local frame at <c>origin</c> (the attachment's world point).</summary>
internal static class ScytheMeshBuilder
{
    private static Vector3 W(RingLodGeometrySnapshot g, double around, double vertical, double across, Vector3 origin)
        => g.CanonicalToWorld(around, vertical, across) - origin;

    /// <summary>Half width of the sheet at path fraction f: 70% at the attachment, full from 4% of the way, thinning to 70% over the last 6% (about 98.5% of the nominal area, which the hazard footprint assumes).</summary>
    public static float HalfWidth(in MegastormScythe s, float f)
        => 0.5f * s.WidthMeters * (0.7f + 0.3f * (float)MegastormModel.Smooth(f * 25.0)) * (1.0f - 0.3f * (float)MegastormModel.Smooth((f - 0.94f) / 0.06f));

    private static (double X, double Z) Normal(MegastormModel model, ScythePoint[] p, int n, int i)
    {
        double c = model.Settings.CircumferenceMeters;
        int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
        double tx = MegastormModel.WrapSigned(p[b].AroundMeters - p[a].AroundMeters, c), tz = p[b].AcrossMeters - p[a].AcrossMeters;
        double len = Math.Sqrt(tx * tx + tz * tz);
        if (len < 1e-6) return (0.0, 1.0);
        return (-tz / len, tx / len);
    }

    public static ArrayMesh BuildSheet(RingLodGeometrySnapshot g, MegastormModel model, in MegastormScythe s, ScythePoint[] p, int n, Vector3 origin, float coreWidth)
    {
        var mesh = new ArrayMesh();
        // Surface 0: the sheet box; surface 1: a soft halo (wider, taller, dim) so the sheet reads as a glowing slab at a distance.
        AddBox(mesh, g, model, s, p, n, origin, 1.0f, 1.0f, 1.0f);
        AddBox(mesh, g, model, s, p, n, origin, 2.0f, 5.0f, 0.3f);
        // The centre-line core: two crossed ribbons at the sheet's mid height, brighter and narrow.
        var cv = new List<Vector3>(); var cc = new List<Color>(); var ci = new List<int>();
        for (int i = 0; i < n; i++)
        {
            float f = (float)i / (n - 1);
            float thick = s.ThicknessMeters * (0.6f + 0.4f * (float)MegastormModel.Smooth(f * 4.0));
            double mid = p[i].LowerEdgeMeters + 0.5 * thick;
            Vector3 center = W(g, p[i].AroundMeters, mid, p[i].AcrossMeters, origin);
            Vector3 up = (W(g, p[i].AroundMeters, mid + 10.0, p[i].AcrossMeters, origin) - center).Normalized();
            (double nx, double nz) = Normal(model, p, n, i);
            Vector3 side = (W(g, p[i].AroundMeters + nx * 10.0, mid, p[i].AcrossMeters + nz * 10.0, origin) - center).Normalized();
            int baseIndex = cv.Count;
            foreach (Vector3 axis in new[] { side, up })
            {
                cv.Add(center - axis * coreWidth * 0.5f); cc.Add(new Color(f, 0.2f, 1, 1));
                cv.Add(center); cc.Add(new Color(f, 1.0f, 1, 1));
                cv.Add(center + axis * coreWidth * 0.5f); cc.Add(new Color(f, 0.2f, 1, 1));
            }
            if (i == 0) continue;
            int prev = baseIndex - 6;
            for (int k = 0; k < 2; k++)
                for (int e = 0; e < 2; e++)
                {
                    int a = prev + k * 3 + e, b = prev + k * 3 + e + 1, c2 = baseIndex + k * 3 + e + 1, d = baseIndex + k * 3 + e;
                    ci.AddRange(new[] { a, b, c2, a, c2, d });
                }
        }
        AddSurface(mesh, cv.ToArray(), cc.ToArray(), ci);
        return mesh;
    }

    private static void AddBox(ArrayMesh mesh, RingLodGeometrySnapshot g, MegastormModel model, in MegastormScythe s, ScythePoint[] p, int n, Vector3 origin,
        float widthScale, float thickScale, float glowScale)
    {
        const int Ring = 8;
        var vertices = new Vector3[n * Ring];
        var colors = new Color[n * Ring];
        var indices = new List<int>();
        float[] offsets = { -1, 0, 1, 1, 1, 0, -1, -1 }, levels = { 0, 0, 0, 0.5f, 1, 1, 1, 0.5f }, glows = { 0.25f, 1, 0.25f, 0.35f, 0.25f, 1, 0.25f, 0.35f };
        for (int i = 0; i < n; i++)
        {
            float f = (float)i / (n - 1);
            (double nx, double nz) = Normal(model, p, n, i);
            float half = HalfWidth(s, f) * widthScale, thick = s.ThicknessMeters * (0.6f + 0.4f * (float)MegastormModel.Smooth(f * 4.0)) * thickScale;
            double mid = p[i].LowerEdgeMeters + 0.5 * s.ThicknessMeters * (0.6f + 0.4f * (float)MegastormModel.Smooth(f * 4.0));
            for (int j = 0; j < Ring; j++)
            {
                double around = p[i].AroundMeters + nx * half * offsets[j], across = Math.Max(0.0, p[i].AcrossMeters + nz * half * offsets[j]);
                double vertical = thickScale > 1.0f ? mid + thick * (levels[j] - 0.5f) : p[i].LowerEdgeMeters + thick * levels[j];
                vertices[i * Ring + j] = W(g, around, vertical, across, origin);
                colors[i * Ring + j] = new Color(f, glows[j] * glowScale, 1.0f, 1.0f);
            }
        }
        for (int i = 0; i < n - 1; i++)
            for (int j = 0; j < Ring; j++)
            {
                int a = i * Ring + j, b = i * Ring + (j + 1) % Ring, c = (i + 1) * Ring + (j + 1) % Ring, d = (i + 1) * Ring + j;
                indices.AddRange(new[] { a, b, c, a, c, d });
            }
        AddSurface(mesh, vertices, colors, indices);
    }

    public static ArrayMesh BuildBranches(RingLodGeometrySnapshot g, MegastormModel model, in MegastormScythe s, ScythePoint[] p, int n, Vector3 origin, float width)
    {
        var rng = new Random(unchecked((int)(s.Id * 2654435761L ^ 0x5bd1e995)));
        var v = new List<Vector3>(); var col = new List<Color>(); var idx = new List<int>();
        int count = 14 + rng.Next(8);
        for (int b = 0; b < count; b++)
        {
            int i = 1 + rng.Next(n - 2);
            float f = (float)i / (n - 1);
            double side = rng.Next(2) == 0 ? -1.0 : 1.0;
            (double nx, double nz) = Normal(model, p, n, i);
            float half = HalfWidth(s, f);
            double thick = s.ThicknessMeters * (0.6 + 0.4 * MegastormModel.Smooth(f * 4.0));
            double height = p[i].LowerEdgeMeters + thick * (0.2 + 0.6 * rng.NextDouble());
            double around = p[i].AroundMeters + nx * half * side, across = p[i].AcrossMeters + nz * half * side;
            double angle = Math.Atan2(nz * side, nx * side) + (rng.NextDouble() - 0.5) * 1.2;
            float branchRandom = (float)rng.NextDouble();
            Grow(g, v, col, idx, origin, around, across, height, angle, 3 + rng.Next(3), f, branchRandom, width, rng, 1, p[i].LowerEdgeMeters, model);
        }
        var mesh = new ArrayMesh();
        if (v.Count > 0) AddSurface(mesh, v.ToArray(), col.ToArray(), idx);
        return mesh;
    }

    private static void Grow(RingLodGeometrySnapshot g, List<Vector3> v, List<Color> col, List<int> idx, Vector3 origin, double around, double across, double height,
        double angle, int steps, float f, float branchRandom, float width, Random rng, int depth, double floorHeight, MegastormModel model)
    {
        for (int k = 0; k < steps; k++)
        {
            double len = 18.0 + rng.NextDouble() * 45.0;
            angle += (rng.NextDouble() - 0.5) * 0.9;
            double na = around + Math.Cos(angle) * len, nz = Math.Max(0.0, across + Math.Sin(angle) * len);
            double nh = Math.Max(floorHeight - 0.2, height + (rng.NextDouble() - 0.5) * 2.0);
            Segment(g, v, col, idx, origin, around, height, across, na, nh, nz, f, branchRandom, width * (1.0f - 0.18f * k), 1.0f - 0.55f * k / steps);
            if (depth < 2 && k == 1 && rng.NextDouble() < 0.5)
                Grow(g, v, col, idx, origin, na, nz, nh, angle + (rng.NextDouble() < 0.5 ? -0.7 : 0.7), 2 + rng.Next(2), f, branchRandom, width * 0.7f, rng, depth + 1, floorHeight, model);
            around = na; across = nz; height = nh;
        }
    }

    private static void Segment(RingLodGeometrySnapshot g, List<Vector3> v, List<Color> col, List<int> idx, Vector3 origin, double a0, double h0, double z0, double a1, double h1,
        double z1, float f, float branchRandom, float width, float glow)
    {
        Vector3 p0 = W(g, a0, h0, z0, origin), p1 = W(g, a1, h1, z1, origin);
        Vector3 dir = p1 - p0;
        if (dir.LengthSquared() < 1e-4f) return;
        dir = dir.Normalized();
        Vector3 up = (W(g, a0, h0 + 10.0, z0, origin) - p0).Normalized();
        Vector3 side = dir.Cross(up).Normalized();
        foreach (Vector3 axis in new[] { side, up })
        {
            int b = v.Count;
            v.Add(p0 - axis * width * 0.5f); v.Add(p0 + axis * width * 0.5f); v.Add(p1 + axis * width * 0.5f * 0.8f); v.Add(p1 - axis * width * 0.5f * 0.8f);
            Color c = new(f, glow, branchRandom, 1.0f);
            col.Add(c); col.Add(c); col.Add(c); col.Add(c);
            idx.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }
    }

    public static ArrayMesh BuildScorch(RingLodGeometrySnapshot g, MegastormModel model, in MegastormScythe s, ScythePoint[] p, int n, Vector3 origin, Func<double, double, double>? surface)
    {
        double c = model.Settings.CircumferenceMeters;
        int samples = Math.Max(8, (int)(s.LengthMeters / 60.0));
        var v = new List<Vector3>(); var col = new List<Color>(); var idx = new List<int>();
        for (int i = 0; i < samples; i++)
        {
            float f = (float)i / (samples - 1);
            (double around, double across) = MegastormScythes.PositionAt(p, n, s, s.LengthMeters * f, c);
            int ni = Math.Min(n - 1, (int)Math.Round(f * (n - 1)));
            (double nx, double nz) = Normal(model, p, n, ni);
            float half = HalfWidth(s, f);
            for (int j = -1; j <= 1; j++)
            {
                double a = around + nx * half * j, z = Math.Max(0.0, across + nz * half * j);
                double ground = surface != null ? surface(a, z) : p[ni].SmoothedGroundMeters;
                v.Add(W(g, a, ground + 0.35, z, origin));
                col.Add(new Color(f, j == 0 ? 1.0f : 0.0f, 0, 1));
            }
            if (i == 0) continue;
            int b = (i - 1) * 3, e = i * 3;
            for (int k = 0; k < 2; k++) idx.AddRange(new[] { b + k, b + k + 1, e + k + 1, b + k, e + k + 1, e + k });
        }
        var mesh = new ArrayMesh();
        AddSurface(mesh, v.ToArray(), col.ToArray(), idx);
        return mesh;
    }

    private static void AddSurface(ArrayMesh mesh, Vector3[] vertices, Color[] colors, List<int> indices)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }
}
