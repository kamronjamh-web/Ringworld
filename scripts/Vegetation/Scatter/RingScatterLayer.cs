using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Vegetation.Scatter;

/// <summary>What the scatter layer reads from its world. The decoration layer fills it from the live grid, explorer and streamer (RingBiomeDecorationLayer.Scatter.cs); a test fills it with a fixed list.</summary>
internal sealed class ScatterLayerHost
{
    public Func<RingLodGeometrySnapshot?> Geometry = () => null;
    public Func<RingCanonicalPosition?> Focus = () => null;
    public Func<long> Epoch = () => 0;
    public Action<List<ScatterRecord>> CopyRecords = _ => { };
    public Func<double> RingAcrossWidthMeters = () => 0.0;
    public Func<float> DistanceScale = () => 1f;
    public Func<DirectionalLight3D?> Sun = () => null;
    /// <summary>Null: the Terrain3D surface is looked up the way the grass layer does it ("../Terrain3DGameplay").</summary>
    public Func<RingTerrain3DSurface?>? Surface { get; set; }
}

/// <summary>
/// T5-7b (design B2, B3, B5, B6): the scatter rock layer. A sibling of the decoration layer (never a descendant: the landscape harness counts everything under it as trees), created only in worlds whose feature mask has bit 4.
/// It owns its own <see cref="RingGpuVegetationCompute"/> (no haze channel, diagnostic category ScatterCulling), so rocks never touch the tree budgets, haze or shadows.
/// Records come from the streamer (CopyResidentScatterRecords) and are turned on a worker into 16-float rows in the frame of an anchor (the camera's canonical position snapped to 512 m), one camera source per render group and one
/// caster source per casting group. Each source set gets its own anchor node ("ScatterSet"); a new set is registered next to the old one and takes over once all its batches are ready, so a rebuild never shows a gap.
/// Per (group, LOD) a camera batch (hidden full list plus the phase 6 camera list), per casting group a ShadowsOnly caster batch. Rocks draw with <see cref="RingScatterRockMaterial"/> (terrain-lit); no CPU fallback.
/// </summary>
internal sealed partial class RingScatterLayer : Node3D
{
    // Group index = LOD budget slot = the shader's rock_group. Slot 3 is the caster budget.
    private const int Boulders = 0, Outcrops = 1, Cobbles = 2, CasterSlot = 3;
    private static readonly RingScatterRenderGroup[] Groups = { RingScatterRenderGroup.Boulders, RingScatterRenderGroup.Outcrops, RingScatterRenderGroup.Cobbles };
    private static readonly int[] LodCounts = { 3, 3, 2 };
    private static readonly uint[] BaseCaps = { 1500, 150, 1000, 1650, 0 };
    public const float AnchorMeters = 512f;
    private const double EpochRebuildSeconds = .25;
    private static readonly double[] DesignEmbed = { .25, 0.0, .30 }; // by group: the meshes' own base embed (T5-6)

    // B5: (near, far, fade-in start, fade-in end, fade-out start, fade-out end) at distance scale 1; -1 = none. A LOD takes over from the previous one across its fade-in, so the pair is complementary.
    private static readonly (float Near, float Far, float InStart, float InEnd, float OutStart, float OutEnd)[][] BaseBands =
    {
        new[] { (0f, 45f, -1f, -1f, 35f, 45f), (35f, 165f, 35f, 45f, 135f, 165f), (135f, 600f, 135f, 165f, 540f, 600f) },
        new[] { (0f, 165f, -1f, -1f, 135f, 165f), (135f, 650f, 135f, 165f, 550f, 650f), (550f, 2500f, 550f, 650f, 2250f, 2500f) },
        new[] { (0f, 13.5f, -1f, -1f, 10.5f, 13.5f), (10.5f, 50f, 10.5f, 13.5f, 44f, 50f) },
    };

    // Same padding the tree layer uses for its camera lists (RingBiomeDecorationLayer.Gpu.cs, "Per-instance frustum culling of the camera tree lists").
    private const float CullMovementMeters = 4f, CullRotationDegrees = 3f, CullPaddingDegrees = 5f, CullSpeedSeconds = .1f, CullWindFraction = .6f, CullGuardMeters = .25f;
    private static readonly StringName SunNearDistanceMetaName = RingGraphicsQualityController.SunNearDistanceMetaKey;

    internal ScatterLayerHost Host { get; set; } = new();
    /// <summary>Multiplies the per-slot LOD budgets (T5-9 wires the presets).</summary>
    internal float CapScale { get; set; } = 1f;
    /// <summary>The rank gate of every batch (T5-9 wires the density setting).</summary>
    internal float RockDensity { get; set; } = 1f;
    /// <summary>Debug / test: false keeps every row in the camera lists.</summary>
    internal bool FrustumCulling { get; set; } = true;

    // ------------------------------------------------------------------ diagnostics
    internal RingGpuVegetationCompute? Compute => _compute;
    /// <summary>Batches registered on the scatter compute (the shown set plus a set waiting to take over).</summary>
    public int BatchCount => _compute?.BatchCount ?? 0;
    /// <summary>Renderers drawing right now: ready camera outputs plus ready caster outputs of the shown set.</summary>
    public int DrawOutputs { get { int n = 0; if (_active != null) foreach (Output o in _active.Outputs) if (o.Ready && o.Full.Visible) n++; return n; } }
    /// <summary>Every MultiMeshInstance3D the layer owns (full-list and camera renderers, both sets).</summary>
    public int RendererCount { get { int n = 0; foreach (OutputSet? set in new[] { _active, _pending }) if (set != null) foreach (Output o in set.Outputs) n += o.Camera != null ? 2 : 1; return n; } }
    /// <summary>Camera source rows per group (boulders, outcrops, cobbles) of the shown set.</summary>
    public int[] SourceRows => _active?.Source.Count ?? new int[3];
    /// <summary>Caster source rows per group (boulders, outcrops, 0) of the shown set.</summary>
    public int[] CasterRows => _active?.Source.CasterCount ?? new int[3];
    /// <summary>Test hook: the shown set's outputs (name, batch id, group slot, LOD or -1 for a caster batch).</summary>
    internal List<(string Name, int BatchId, int Group, int Lod, bool Caster)> OutputInfo()
    {
        var list = new List<(string, int, int, int, bool)>();
        if (_active != null) foreach (Output o in _active.Outputs) list.Add((o.Name, o.BatchId, o.Group, o.Lod, o.Caster));
        return list;
    }
    /// <summary>One line of the diagnostics above for tools that only reach public methods (GDScript harnesses).</summary>
    public string DiagnosticsText() => $"batches={BatchCount} drawOutputs={DrawOutputs} renderers={RendererCount} rows={string.Join("/", SourceRows)} casterRows={string.Join("/", CasterRows)} buildMs={LastSourceBuildMs:0.0} builds={SourceBuilds} anchorRebuilds={AnchorRebuilds} pending={SetPending} failed={Failed}";
    public double LastSourceBuildMs { get; private set; }
    /// <summary>Rebuilds started because the camera left the anchor's 512 m.</summary>
    public int AnchorRebuilds { get; private set; }
    /// <summary>Source builds in all (the first one, anchor moves, record epoch changes).</summary>
    public int SourceBuilds { get; private set; }
    /// <summary>Canonical anchor (metres around, across) of the latest source build.</summary>
    internal (long Around, long Across) Anchor => (_anchorAround, _anchorAcross);
    public bool Failed => _failed;
    internal bool SetPending => _pending != null || _build != null;

    private RingGpuVegetationCompute? _compute;
    private RingScatterRockMaterial? _materials;
    private RockCatalog? _catalog;
    private float[] _unitRadius = new float[3];
    private bool _failed, _failureLogged;
    private RingTerrain3DSurface? _surface;
    private ulong _nextSurfaceSearchMsec;

    private Task<SourceBuild>? _build;
    private OutputSet? _active, _pending;
    private bool _haveAnchor;
    private long _anchorAround, _anchorAcross;
    private long _builtEpoch = -1;
    private GeometryKey _builtGeometry;
    private ulong _lastBuildMsec;
    private readonly List<ScatterRecord> _recordScratch = new();
    private readonly List<RingGpuVegetationCompute.BatchFrameParameters> _frames = new();
    private readonly uint[] _caps = new uint[5];

    // ------------------------------------------------------------------ build inputs and results

    internal readonly record struct GeometryKey(Basis Basis, double Radius, double Circumference, double VerticalBase);

    private static GeometryKey KeyOf(in RingLodGeometrySnapshot g) => new(g.GridGlobalTransform.Basis, g.RingRadiusMeters, g.CircumferenceMeters, g.RingVerticalBaseOffsetMeters);

    internal sealed class SourceBuild
    {
        public long Epoch, AnchorAround, AnchorAcross;
        public GeometryKey Geometry;
        public readonly float[]?[] Rows = new float[3][];
        public readonly Vector4[]?[] Meta = new Vector4[3][];
        public readonly int[] Count = new int[3];
        public readonly float[]?[] CasterRows = new float[3][];
        public readonly Vector4[]?[] CasterMeta = new Vector4[3][];
        public readonly int[] CasterCount = new int[3];
        public readonly Aabb[] Bounds = new Aabb[3];
        public double BuildMs;
    }

    private sealed class Output
    {
        public int Group, Lod;           // Lod -1: the group's caster batch
        public bool Caster;
        public string Name = "";
        public MultiMeshInstance3D Full = null!;
        public MultiMeshInstance3D? Camera;
        public int BatchId = -1;
        public bool Ready;
    }

    private sealed class OutputSet
    {
        public Node3D Root = null!;
        public SourceBuild Source = null!;
        public readonly List<Output> Outputs = new();
        public Transform3D LastTransform;
        public Vector3 UniformOrigin = new(float.NaN, float.NaN, float.NaN);
        public Vector3 Around, Across;
        public Vector2 MacroUv;
    }

    // ------------------------------------------------------------------ frame

    public override void _Process(double delta)
    {
        if (_failed || Host == null) return;
        if (!EnsureCompute()) return;
        UpdateSurface();
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
            _compute = new RingGpuVegetationCompute { HazeChannel = RingVegetationHaze.Channel.None, DiagnosticCategory = RingPerformanceGpu.ScatterCulling };
            try
            {
                _catalog = RockCatalog.Load();
                _materials = new RingScatterRockMaterial(_catalog);
                for (int g = 0; g < 3; g++)
                {
                    float radius = 0f;
                    foreach (RockLodMesh lod in _catalog.Group(Groups[g]).Lods) for (int c = 0; c < 8; c++) radius = Math.Max(radius, lod.UnitAabb.GetEndpoint(c).Length());
                    _unitRadius[g] = radius;
                }
            }
            catch (Exception error) { Fail("rock assets: " + error.Message); return false; }
        }
        _compute.Initialize();
        if (_compute.Failed) { Fail(_compute.Status); return false; }
        return _compute.Ready;
    }

    private void Fail(string status)
    {
        _failed = true;
        Visible = false;
        if (!_failureLogged) { _failureLogged = true; GD.PushWarning("Scatter rocks are hidden: the GPU vegetation compute is unavailable (" + status + ")."); }
    }

    private void UpdateSurface()
    {
        if (_materials == null) return;
        if (_surface != null && GodotObject.IsInstanceValid(_surface)) return;
        _surface = null;
        ulong now = Time.GetTicksMsec();
        if (now < _nextSurfaceSearchMsec) return;
        _nextSurfaceSearchMsec = now + 500;
        RingTerrain3DSurface? surface = Host.Surface != null ? Host.Surface() : GetNodeOrNull<RingTerrain3DGameplay>("../Terrain3DGameplay")?.SurfaceForFollowers;
        if (surface == null || !GodotObject.IsInstanceValid(surface)) return;
        _materials.Register(surface);
        _surface = surface;
    }

    // ------------------------------------------------------------------ sources

    private static double Wrap(double delta, double circumference) => delta - circumference * Math.Round(delta / circumference);

    private static long Snap(double meters) => (long)Math.Round(meters / AnchorMeters) * (long)AnchorMeters;

    private void ServiceSources(in RingLodGeometrySnapshot geometry, in RingCanonicalPosition focus)
    {
        if (_build is { IsCompleted: true })
        {
            Task<SourceBuild> done = _build;
            _build = null;
            if (done.IsCompletedSuccessfully) Install(done.Result, geometry, focus);
            else { GD.PushWarning("Scatter source build failed: " + done.Exception?.GetBaseException().Message); }
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
        float[] unitRadius = (float[])_unitRadius.Clone();
        _build = Task.Run(() => BuildSources(records, snapshot, around, across, epoch, key, unitRadius));
    }

    /// <summary>Pure (worker): the rows of every group in the frame of the anchor. The geometry snapshot is centred at the anchor, so the large ring coordinates cancel in double precision.</summary>
    internal static SourceBuild BuildSources(IReadOnlyList<ScatterRecord> records, RingLodGeometrySnapshot live, long anchorAround, long anchorAcross, long epoch, GeometryKey key, float[] unitRadius)
    {
        var watch = Stopwatch.StartNew();
        RingLodGeometrySnapshot g = live.CenteredAt(anchorAround, anchorAcross);
        double radius = g.RingRadiusMeters, circumference = g.CircumferenceMeters;
        Basis grid = g.GridGlobalTransform.Basis;
        RingScatterClassTable table = RingScatterClassTable.Production;
        var classes = new Dictionary<ushort, RingScatterClass>();
        foreach (RingScatterClass row in table.Rows) classes[row.Id] = row;
        var build = new SourceBuild { Epoch = epoch, AnchorAround = anchorAround, AnchorAcross = anchorAcross, Geometry = key };
        var groupOf = new int[records.Count];
        var casts = new bool[records.Count];
        for (int i = 0; i < records.Count; i++)
        {
            groupOf[i] = -1;
            if (!classes.TryGetValue(records[i].ClassId, out RingScatterClass? row)) continue;
            int group = GroupIndex(row.RenderGroup);
            if (group < 0) continue;
            groupOf[i] = group;
            build.Count[group]++;
            if (group != Cobbles && records[i].Size >= row.ShadowMinSizeM) { casts[i] = true; build.CasterCount[group]++; }
        }
        // Rows go in by horizontal distance from the anchor centre (ties by record id), so a binding LOD budget drops the farthest rows first.
        var order = new int[records.Count];
        var keys = new double[records.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
            double da = Wrap(records[i].AroundCm * .01 - anchorAround, circumference), dz = records[i].AcrossCm * .01 - anchorAcross;
            keys[i] = da * da + dz * dz;
        }
        Array.Sort(order, (a, b) => keys[a] != keys[b] ? keys[a].CompareTo(keys[b]) : records[a].Id.CompareTo(records[b].Id));
        var cursor = new int[3];
        var casterCursor = new int[3];
        var minimum = new Vector3[3]; var maximum = new Vector3[3]; var maxSize = new float[3];
        for (int group = 0; group < 3; group++)
        {
            if (build.Count[group] > 0)
            {
                build.Rows[group] = new float[build.Count[group] * RingGpuVegetationCompute.TreeCustomFloats];
                build.Meta[group] = new Vector4[build.Count[group]];
                minimum[group] = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity); maximum[group] = -minimum[group];
            }
            if (build.CasterCount[group] > 0)
            {
                build.CasterRows[group] = new float[build.CasterCount[group] * RingGpuVegetationCompute.TreeCustomFloats];
                build.CasterMeta[group] = new Vector4[build.CasterCount[group]];
            }
        }
        var row16 = new float[RingGpuVegetationCompute.TreeCustomFloats];
        foreach (int i in order)
        {
            int group = groupOf[i];
            if (group < 0) continue;
            ScatterRecord rec = records[i];
            RingScatterClass cls = classes[rec.ClassId];
            double around = rec.AroundCm * .01, across = rec.AcrossCm * .01;
            Vector3 position = g.CanonicalToWorld(around, rec.SurfaceHeight, across);
            double wrapped = around % circumference; if (wrapped < 0) wrapped += circumference;
            double angle = wrapped / radius, sa = Math.Sin(angle), ca = Math.Cos(angle);
            (double ux, double uy, double uz) = ScatterRecord.DecodeUpOct(rec.UpOct);
            // Local ring frame at the record: tangent (-sin, 0, cos), up (-cos, 0, -sin), across (0, 1, 0); up_oct is (around, up, across) in it.
            Vector3 up = (grid * new Vector3((float)(-ux * sa - uy * ca), (float)uz, (float)(ux * ca - uy * sa))).Normalized();
            Vector3 tangent = grid * new Vector3((float)-sa, 0f, (float)ca);
            Vector3 x = tangent - up * tangent.Dot(up);
            x = x.LengthSquared() > 1e-8f ? x.Normalized() : up.Cross(Vector3.Up).Normalized();
            var basis = new Basis(up, rec.Yaw) * new Basis(x, up, x.Cross(up));
            basis = basis.Scaled(new Vector3(rec.Size, rec.Size, rec.Size));
            // Embed: the meshes' base sits DesignEmbed (of the size) below the ground contact; this record wants e, so move it by the difference along its up (deeper = down).
            double e = cls.EmbedMin + (cls.EmbedMax - cls.EmbedMin) * EmbedHash(rec.Id);
            position += up * (float)((DesignEmbed[group] - e) * rec.Size);
            (float r, float gr, float b) = ScatterRecord.UnpackBaseTint(rec.BaseTint);
            int tint = ((int)MathF.Round(r * 63f) << 12) | ((int)MathF.Round(gr * 63f) << 6) | (int)MathF.Round(b * 63f);
            int variant = Math.Clamp(rec.Mesh - cls.MeshFirst, 0, 31);
            Array.Clear(row16);
            row16[0] = basis.Column0.X; row16[1] = basis.Column1.X; row16[2] = basis.Column2.X; row16[3] = position.X;
            row16[4] = basis.Column0.Y; row16[5] = basis.Column1.Y; row16[6] = basis.Column2.Y; row16[7] = position.Y;
            row16[8] = basis.Column0.Z; row16[9] = basis.Column1.Z; row16[10] = basis.Column2.Z; row16[11] = position.Z;
            row16[12] = (float)(variant + 32L * tint); row16[14] = rec.SurfaceHeight;
            var meta = new Vector4((float)(rec.Id >> 40) / 16777215f, 0f, (float)Wrap(around - anchorAround, circumference), (float)(across - anchorAcross));
            int at = cursor[group]++;
            Array.Copy(row16, 0, build.Rows[group]!, at * RingGpuVegetationCompute.TreeCustomFloats, RingGpuVegetationCompute.TreeCustomFloats);
            build.Meta[group]![at] = meta;
            if (casts[i])
            {
                int cat = casterCursor[group]++;
                Array.Copy(row16, 0, build.CasterRows[group]!, cat * RingGpuVegetationCompute.TreeCustomFloats, RingGpuVegetationCompute.TreeCustomFloats);
                build.CasterMeta[group]![cat] = meta;
            }
            minimum[group] = minimum[group].Min(position); maximum[group] = maximum[group].Max(position); maxSize[group] = Math.Max(maxSize[group], rec.Size);
        }
        for (int group = 0; group < 3; group++)
            if (build.Count[group] > 0)
                build.Bounds[group] = new Aabb(minimum[group], maximum[group] - minimum[group]).Grow(maxSize[group] * unitRadius[group] + 2f);
        build.BuildMs = watch.Elapsed.TotalMilliseconds;
        return build;
    }

    private static int GroupIndex(RingScatterRenderGroup group) => group switch
    {
        RingScatterRenderGroup.Boulders => Boulders,
        RingScatterRenderGroup.Outcrops => Outcrops,
        RingScatterRenderGroup.Cobbles => Cobbles,
        _ => -1,
    };

    /// <summary>[0, 1): a hash of the record id, decorrelated from the rank (which is the id's top 24 bits).</summary>
    private static double EmbedHash(ulong id) => (double)((id * 0x9E3779B97F4A7C15UL) >> 40) / 16777216.0;

    // ------------------------------------------------------------------ sets

    private void Install(SourceBuild build, in RingLodGeometrySnapshot geometry, in RingCanonicalPosition focus)
    {
        LastSourceBuildMs = build.BuildMs;
        if (_pending != null) { DisposeSet(_pending); _pending = null; }
        var set = new OutputSet { Source = build };
        set.Root = new Node3D { Name = "ScatterSet" };
        AddChild(set.Root);
        set.Root.GlobalTransform = AnchorTransform(geometry, build);
        set.LastTransform = set.Root.GlobalTransform;
        double radius = geometry.RingRadiusMeters, circumference = geometry.CircumferenceMeters;
        RingStandingGrassLayer.CalculateGrassGroundCovectors(geometry, 0.0, build.AnchorAround, out set.Around, out set.Across);
        double aroundWrapped = build.AnchorAround % circumference; if (aroundWrapped < 0) aroundWrapped += circumference;
        set.MacroUv = new Vector2((float)(aroundWrapped / (Math.Tau * Math.Max(1.0, radius))), (float)(build.AnchorAcross / Math.Max(1.0, Host.RingAcrossWidthMeters())));
        for (int group = 0; group < 3; group++)
        {
            if (build.Count[group] == 0) continue;
            for (int lod = 0; lod < LodCounts[group]; lod++) CreateOutput(set, group, lod, caster: false);
            if (build.CasterCount[group] > 0) CreateOutput(set, group, -1, caster: true);
        }
        _pending = set;
        PromotePending();
    }

    private static Transform3D AnchorTransform(in RingLodGeometrySnapshot geometry, SourceBuild build)
        => new(Basis.Identity, geometry.CanonicalToWorld(build.AnchorAround, 0.0, build.AnchorAcross));

    private void CreateOutput(OutputSet set, int group, int lod, bool caster)
    {
        RingScatterRenderGroup g = Groups[group];
        SourceBuild source = set.Source;
        int lodIndex = caster ? RingScatterRockMaterial.CasterLod(g) : lod;
        RockLodMesh lodMesh = _catalog!.Group(g).Lods[lodIndex];
        ShaderMaterial material = _materials!.Get(g, lodIndex);
        string name = caster ? $"ScatterRock_{g}_Shadow" : $"ScatterRock_{g}_L{lod}";
        float[] rows = caster ? source.CasterRows[group]! : source.Rows[group]!;
        Vector4[] meta = caster ? source.CasterMeta[group]! : source.Meta[group]!;
        var full = new MultiMeshInstance3D
        {
            Name = name, Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = lodMesh.Carrier }, MaterialOverride = material,
            ExtraCullMargin = 8, Visible = false,
            // Camera batches: the full list is never drawn (the camera draws the phase 6 list). Caster batches: the full list casts the sun shadow.
            CastShadow = caster ? GeometryInstance3D.ShadowCastingSetting.ShadowsOnly : GeometryInstance3D.ShadowCastingSetting.Off,
        };
        if (!caster) full.Layers = 0;
        MultiMesh? cameraMultiMesh = null;
        MultiMeshInstance3D? camera = null;
        if (!caster)
        {
            cameraMultiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = lodMesh.Carrier };
            camera = new MultiMeshInstance3D { Name = name + "_Camera", Multimesh = cameraMultiMesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 8 };
            full.AddChild(camera);
        }
        set.Root.AddChild(full);
        int count = source.Count[group];
        if (caster) count = source.CasterCount[group];
        int id = _compute!.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration(name, full.Multimesh!, lodMesh.Carrier, rows, meta, count, source.Bounds[group], false,
            TreeCustomData: true, TreeDitherData: !caster, Expansion: null, CameraTarget: cameraMultiMesh, VerifyTarget: null));
        if (id < 0) { full.QueueFree(); GD.PushWarning("Scatter batch registration failed for " + name); return; }
        full.SetInstanceShaderParameter("rock_frame_origin", Vector3.Zero);
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
                    Apply(output.Full, set, transform.Origin);
                    if (output.Camera != null) Apply(output.Camera, set, transform.Origin);
                }
            }
        }
        return moved;
    }

    private static void Apply(MultiMeshInstance3D renderer, OutputSet set, Vector3 origin)
    {
        renderer.SetInstanceShaderParameter("rock_frame_origin", origin);
        renderer.SetInstanceShaderParameter("rock_frame_around", set.Around);
        renderer.SetInstanceShaderParameter("rock_frame_across", set.Across);
        renderer.SetInstanceShaderParameter("rock_macro_uv", set.MacroUv);
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
        // A POI sun-map extension raises the light's range; rocks keep the near-map distance the controller records on the light.
        if (sun.HasMeta(SunNearDistanceMetaName)) distance = Mathf.Min(distance, (float)sun.GetMeta(SunNearDistanceMetaName));
        return float.IsFinite(distance) && distance > 0f ? distance : 0f;
    }

    private void SubmitFrame(Camera3D camera)
    {
        float scale = Mathf.Clamp(Host.DistanceScale(), .05f, 4f), gate = Mathf.Clamp(RockDensity, 0f, 1f), shadow = ShadowDistance(), capScale = Math.Max(0f, CapScale);
        for (int i = 0; i < 5; i++) _caps[i] = (uint)Math.Round(BaseCaps[i] * capScale);
        _frames.Clear();
        // Near LOD first inside a group (the batches of a group share one budget slot), so a binding cap drops far rows first. The shown set leads.
        foreach (OutputSet? set in new[] { _active, _pending })
        {
            if (set == null) continue;
            Transform3D world = set.Root.GlobalTransform;
            foreach (Output output in set.Outputs)
            {
                int pass = output.Caster ? CasterSlot : output.Group;
                float near = 0f, far, inStart = -1f, inEnd = -1f, outStart = -1f, outEnd = -1f;
                if (output.Caster) far = shadow;
                else
                {
                    var band = BaseBands[output.Group][output.Lod];
                    near = band.Near * scale; far = band.Far * scale;
                    if (band.InStart >= 0f) { inStart = band.InStart * scale; inEnd = band.InEnd * scale; }
                    if (band.OutStart >= 0f) { outStart = band.OutStart * scale; outEnd = band.OutEnd * scale; }
                }
                _frames.Add(new RingGpuVegetationCompute.BatchFrameParameters(output.BatchId, world, false, pass, near, far, 0f, 1f, 1f, false, gate, false, null, 0f,
                    TreeFadeInStart: inStart, TreeFadeInEnd: inEnd, TreeFadeOutStart: outStart, TreeFadeOutEnd: outEnd));
            }
        }
        if (_frames.Count > 0) _compute!.RequestFrame(new RingGpuVegetationCompute.FrameRequest(camera.GlobalTransform.Origin, _caps, _frames, true));
    }

    public override void _ExitTree()
    {
        foreach (OutputSet? set in new[] { _active, _pending }) if (set != null) foreach (Output output in set.Outputs) _compute?.RemoveBatch(output.BatchId);
        _active = null; _pending = null;
        _materials?.Unregister();
        _compute?.Dispose();
        _compute = null;
    }
}
