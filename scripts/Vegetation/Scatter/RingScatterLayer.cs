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
    /// <summary>The explorer's altitude above the terrain under it (colliders only: the reach logic of the trunk pool).</summary>
    public Func<float> Altitude = () => 0f;
    /// <summary>T6-4b: registers a tree-lit material with the decoration layer's lighting, atmosphere and cloud-shadow updates (the understory layer's carrier materials). Rocks do not use it.</summary>
    public Action<ShaderMaterial> RegisterTreeMaterial = _ => { };
    /// <summary>T6-4b: the tree layer's SimplifyDistantTreeMaterials setting, which the understory's L2 carrier follows.</summary>
    public Func<bool> SimplifyDistantMaterials = () => true;
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
internal sealed partial class RingScatterLayer : RingScatterLayerBase
{
    // Group index = LOD budget slot = the shader's rock_group. Slot 3 is the caster budget.
    private const int Boulders = 0, Outcrops = 1, Cobbles = 2, CasterSlot = 3;
    private static readonly RingScatterRenderGroup[] Groups = { RingScatterRenderGroup.Boulders, RingScatterRenderGroup.Outcrops, RingScatterRenderGroup.Cobbles };
    private static readonly int[] LodCounts = { 3, 3, 2 };
    private static readonly uint[] RockBaseCaps = { 1500, 150, 1000, 1650, 0 };
    private static readonly double[] DesignEmbed = { .25, 0.0, .30 }; // by group: the meshes' own base embed (T5-6)

    // B5: (near, far, fade-in start, fade-in end, fade-out start, fade-out end) at distance scale 1; -1 = none. A LOD takes over from the previous one across its fade-in, so the pair is complementary.
    private static readonly (float Near, float Far, float InStart, float InEnd, float OutStart, float OutEnd)[][] BaseBands =
    {
        new[] { (0f, 45f, -1f, -1f, 35f, 45f), (35f, 165f, 35f, 45f, 135f, 165f), (135f, 600f, 135f, 165f, 540f, 600f) },
        new[] { (0f, 165f, -1f, -1f, 135f, 165f), (135f, 650f, 135f, 165f, 550f, 650f), (550f, 2500f, 550f, 650f, 2250f, 2500f) },
        new[] { (0f, 13.5f, -1f, -1f, 10.5f, 13.5f), (10.5f, 50f, 10.5f, 13.5f, 44f, 50f) },
    };

    /// <summary>The rank gate of every batch (T5-9 wires the density setting).</summary>
    internal float RockDensity { get; set; } = 1f;

    protected override string DiagnosticCategory => RingPerformanceGpu.ScatterCulling;
    protected override string HiddenLabel => "Scatter rocks";
    protected override string SetRootName => "ScatterSet";
    protected override uint[] BaseCaps => RockBaseCaps;
    protected override float DensityGate => RockDensity;

    // ------------------------------------------------------------------ diagnostics
    /// <summary>Camera source rows per group (boulders, outcrops, cobbles) of the shown set.</summary>
    public int[] SourceRows => (ActiveSource as SourceBuild)?.Count ?? new int[3];
    /// <summary>Caster source rows per group (boulders, outcrops, 0) of the shown set.</summary>
    public int[] CasterRows => (ActiveSource as SourceBuild)?.CasterCount ?? new int[3];
    /// <summary>One line of the diagnostics above for tools that only reach public methods (GDScript harnesses).</summary>
    public string DiagnosticsText() => $"batches={BatchCount} drawOutputs={DrawOutputs} renderers={RendererCount} rows={string.Join("/", SourceRows)} casterRows={string.Join("/", CasterRows)} buildMs={LastSourceBuildMs:0.0} builds={SourceBuilds} anchorRebuilds={AnchorRebuilds} pending={SetPending} failed={Failed}";

    private RingScatterRockMaterial? _materials;
    private RockCatalog? _catalog;
    private float[] _unitRadius = new float[3];
    private RingTerrain3DSurface? _surface;
    private ulong _nextSurfaceSearchMsec;

    // ------------------------------------------------------------------ build inputs and results

    internal sealed class SourceBuild : SourceBuildBase
    {
        public readonly float[]?[] Rows = new float[3][];
        public readonly Vector4[]?[] Meta = new Vector4[3][];
        public readonly int[] Count = new int[3];
        public readonly float[]?[] CasterRows = new float[3][];
        public readonly Vector4[]?[] CasterMeta = new Vector4[3][];
        public readonly int[] CasterCount = new int[3];
        public readonly Aabb[] Bounds = new Aabb[3];
    }

    // ------------------------------------------------------------------ frame

    protected override void UpdateAlways() => UpdateColliders();

    protected override void LoadAssets()
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

    protected override void UpdateAfterCompute() => UpdateSurface();

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

    protected override Task<SourceBuildBase> StartBuild(ScatterRecord[] records, RingLodGeometrySnapshot snapshot, long anchorAround, long anchorAcross, long epoch, GeometryKey key)
    {
        float[] unitRadius = (float[])_unitRadius.Clone();
        return Task.Run<SourceBuildBase>(() => BuildSources(records, snapshot, anchorAround, anchorAcross, epoch, key, unitRadius));
    }

    /// <summary>Pure (worker): the rows of every group in the frame of the anchor. The geometry snapshot is centred at the anchor, so the large ring coordinates cancel in double precision.</summary>
    internal static SourceBuild BuildSources(IReadOnlyList<ScatterRecord> records, RingLodGeometrySnapshot live, long anchorAround, long anchorAcross, long epoch, GeometryKey key, float[] unitRadius)
    {
        var watch = Stopwatch.StartNew();
        RingLodGeometrySnapshot g = live.CenteredAt(anchorAround, anchorAcross);
        double circumference = g.CircumferenceMeters;
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
            (Basis rotation, Vector3 position) = RecordPose(rec, cls, group, g);
            Basis basis = rotation.Scaled(new Vector3(rec.Size, rec.Size, rec.Size));
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

    /// <summary>
    /// The one place a record becomes a pose: the rotation (up from UpOct, yaw about it; no scale) and the position (surface point plus the embed shift along up), in the frame of <paramref name="g"/>.
    /// The renderer's source rows (<see cref="BuildSources"/>, which scales the rotation by the record's size) and the collider pool (RingScatterLayer.Colliders.cs, which never scales a body) both call it, so they cannot drift apart.
    /// </summary>
    internal static (Basis Rotation, Vector3 Position) RecordPose(in ScatterRecord rec, RingScatterClass cls, int group, in RingLodGeometrySnapshot g)
        => RecordPose(rec, cls, DesignEmbed[group], rec.Size, g);

    /// <summary>
    /// The pose with the mesh's own base embed (<paramref name="designEmbed"/>, a fraction of the instance height) and the instance height in metres per unit of the carrier's normalised space (<paramref name="heightScale"/>): the record size for rocks,
    /// size times the source variant's height for MeshCatalog.Trees (the carrier is 1 unit tall, so the embed fractions apply to the instance height). T6-4b: the understory layer calls it with 0 and that height.
    /// </summary>
    internal static (Basis Rotation, Vector3 Position) RecordPose(in ScatterRecord rec, RingScatterClass cls, double designEmbed, float heightScale, in RingLodGeometrySnapshot g)
    {
        double radius = g.RingRadiusMeters, circumference = g.CircumferenceMeters;
        Basis grid = g.GridGlobalTransform.Basis;
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
        // Embed: the meshes' base sits DesignEmbed (of the size) below the ground contact; this record wants e, so move it by the difference along its up (deeper = down).
        double e = cls.EmbedMin + (cls.EmbedMax - cls.EmbedMin) * EmbedHash(rec.Id);
        position += up * (float)((designEmbed - e) * heightScale);
        return (basis, position);
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

    protected override void CreateOutputs(OutputSet set)
    {
        var build = (SourceBuild)set.Source;
        for (int group = 0; group < 3; group++)
        {
            if (build.Count[group] == 0) continue;
            for (int lod = 0; lod < LodCounts[group]; lod++) CreateOutput(set, group, lod, caster: false);
            if (build.CasterCount[group] > 0) CreateOutput(set, group, -1, caster: true);
        }
    }

    private void CreateOutput(OutputSet set, int group, int lod, bool caster)
    {
        RingScatterRenderGroup g = Groups[group];
        var source = (SourceBuild)set.Source;
        int lodIndex = caster ? RingScatterRockMaterial.CasterLod(g) : lod;
        RockLodMesh lodMesh = _catalog!.Group(g).Lods[lodIndex];
        ShaderMaterial material = _materials!.Get(g, lodIndex);
        string name = caster ? $"ScatterRock_{g}_Shadow" : $"ScatterRock_{g}_L{lod}";
        float[] rows = caster ? source.CasterRows[group]! : source.Rows[group]!;
        Vector4[] meta = caster ? source.CasterMeta[group]! : source.Meta[group]!;
        int count = source.Count[group];
        if (caster) count = source.CasterCount[group];
        AddOutput(set, group, lod, caster, name, lodMesh.Carrier, material, rows, meta, count, source.Bounds[group]);
    }

    protected override void OnOutputCreated(MultiMeshInstance3D full) => full.SetInstanceShaderParameter("rock_frame_origin", Vector3.Zero);

    protected override void ApplyFrameUniforms(MultiMeshInstance3D renderer, OutputSet set, Vector3 origin)
    {
        renderer.SetInstanceShaderParameter("rock_frame_origin", origin);
        renderer.SetInstanceShaderParameter("rock_frame_around", set.Around);
        renderer.SetInstanceShaderParameter("rock_frame_across", set.Across);
        renderer.SetInstanceShaderParameter("rock_macro_uv", set.MacroUv);
    }

    // ------------------------------------------------------------------ frame submission

    protected override FrameTerms ResolveFrame(Output output, float scale, float shadowDistance)
    {
        int pass = output.Caster ? CasterSlot : output.Group;
        float near = 0f, far, inStart = -1f, inEnd = -1f, outStart = -1f, outEnd = -1f;
        if (output.Caster) far = shadowDistance;
        else
        {
            var band = BaseBands[output.Group][output.Lod];
            near = band.Near * scale; far = band.Far * scale;
            if (band.InStart >= 0f) { inStart = band.InStart * scale; inEnd = band.InEnd * scale; }
            if (band.OutStart >= 0f) { outStart = band.OutStart * scale; outEnd = band.OutEnd * scale; }
        }
        return new FrameTerms(pass, near, far, inStart, inEnd, outStart, outEnd);
    }

    protected override void ReleaseAssets() => _materials?.Unregister();
}
