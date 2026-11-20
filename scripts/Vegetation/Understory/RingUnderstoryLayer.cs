using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Vegetation.Canopy;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Vegetation.Understory;

/// <summary>
/// T6-4b (T6 design A7): the understory layer, the second instance of the scatter layer pattern (<see cref="RingScatterLayerBase"/>). A sibling of the decoration layer (never a descendant: the landscape harness counts everything under it as trees),
/// node name "UnderstoryLayer", created only in worlds whose feature mask has bit 6 (vegetation.forest_structure). It owns its own <see cref="RingGpuVegetationCompute"/> (no haze channel, diagnostic category UnderstoryCulling), so the understory never
/// touches the tree budgets, haze or shadows. Records are the scatter records of the render groups Saplings, Shrubs and DeadWood (rock groups are ignored); saplings, shrubs and dead wood draw.
/// Saplings draw through vertex-fetch carriers (<see cref="UnderstoryCatalog"/>): per LOD one carrier holding every sapling variant, with <c>materials/ring_understory.gdshader</c>, lit like tree meshes.
/// Dead wood (snags, fallen trees, stumps; T6-6) draws through two carriers of <see cref="UnderstoryDeadWood"/> (near 0 to 70 m, far 50 m outwards, cross-fading across 50 to 70 m) and casts the sun shadow through a ShadowsOnly caster batch of the far carrier
/// (snags and logs, within the sun near-map distance). Colliders: RingUnderstoryLayer.Colliders.cs.
/// Shrubs (T6-7) draw through two vertex-fetch carriers of <see cref="UnderstoryShrubs"/> (LOD0 0 to 45 m, LOD1 30 m outwards, cross-fading across 30 to 45 m, fading out across 130 to 150 m), foliage only, lit like the saplings; they cast no shadow. Casters: snags and logs at every preset (slot 3); saplings at Very High and up (slot 4, <see cref="SaplingShadows"/>, the far sapling carrier within the class reach); shrubs never. Stand fillers (T6 SF-3, render group StandFill) draw through the L0 and L2 carriers of <see cref="UnderstoryStandFill"/> (L0 0 to 35 m, L2 25 to 150 m) and cast through the 36-triangle billboard caster carrier at High and up (<see cref="StandFillShadows"/>, to 100 m); their trunks have colliders. Budget slots (seven): 0 saplings, 1 shrubs, 2 dead wood, 3 dead-wood caster, 4 sapling caster, 5 stand filler, 6 stand-filler caster.
/// </summary>
internal sealed partial class RingUnderstoryLayer : RingScatterLayerBase
{
    // Group index = LOD budget slot.
    private const int Saplings = 0, ShrubSlot = 1, DeadWoodSlot = 2, DeadWoodCasterSlot = 3, SaplingCasterSlot = 4, StandFillSlot = 5, StandFillCasterSlot = 6, SlotCount = 7;
    // Dead wood: 400 fallen trees + 200 snags + 200 stumps in one slot; the caster slot holds the snags and logs (200 + 400).
    private static readonly uint[] UnderstoryBaseCaps = { 1500, 3000, 800, 600, 1500, 1500, 1000 };
    // (near, far, fade-in start, fade-in end, fade-out start, fade-out end) at distance scale 1; -1 = none. L0 hands over to L2 across 20 to 30 m; L2 fades out at the class reach (130 to 150 m).
    private static readonly (float Near, float Far, float InStart, float InEnd, float OutStart, float OutEnd)[] SaplingBands =
    {
        (0f, 30f, -1f, -1f, 20f, 30f),
        (20f, 150f, 20f, 30f, 130f, 150f),
    };
    // Shrubs: L0 (0 to 45 m) hands over to L1 across 30 to 45 m; L1 fades out at the class reach (130 to 150 m).
    private static readonly (float Near, float Far, float InStart, float InEnd, float OutStart, float OutEnd)[] ShrubBands =
    {
        (0f, 45f, -1f, -1f, 30f, 45f),
        (30f, 150f, 30f, 45f, 130f, 150f),
    };
    // Stand filler: L0 (0 to 35 m) hands over to L2 across 25 to 35 m; L2 fades out at the class reach (130 to 150 m).
    private static readonly (float Near, float Far, float InStart, float InEnd, float OutStart, float OutEnd)[] StandFillBands =
    {
        (0f, 35f, -1f, -1f, 25f, 35f),
        (25f, 150f, 25f, 35f, 130f, 150f),
    };
    private const float StandFillCasterReachMeters = 100f;
    // Dead wood: the near carrier (0 to 60 m) hands over to the far carrier across 50 to 70 m; snags fade out across 260 to 300 m (logs and stumps end at their reach, 150 and 80 m, where their records stop).
    private static readonly (float Near, float Far, float InStart, float InEnd, float OutStart, float OutEnd)[] DeadWoodBands =
    {
        (0f, 70f, -1f, -1f, 50f, 70f),
        (50f, 300f, 50f, 70f, 260f, 300f),
    };

    /// <summary>The rank gate of the sapling and shrub batches (the "Understory density" setting).</summary>
    internal float UnderstoryDensity { get; set; } = 1f;
    /// <summary>The rank gate of the dead-wood batches and the filter of the dead-wood colliders (the "Dead wood density" setting).</summary>
    internal float DeadWoodDensity { get; set; } = 1f;

    /// <summary>The rank gate of the stand-filler batches and the filter of the stand-filler colliders.</summary>
    internal float StandFillDensity { get; set; } = 1f;

    private bool _standFillShadows;
    /// <summary>Whether stand fillers cast into the sun shadow map (the graphics preset: High and up). Changing it rebuilds the sources so the stand-filler caster slot is filled or emptied.</summary>
    internal bool StandFillShadows
    {
        get => _standFillShadows;
        set { if (_standFillShadows == value) return; _standFillShadows = value; RequestRebuild(); }
    }

    private bool _saplingShadows;
    /// <summary>Whether saplings cast into the sun shadow map (the graphics preset: Very High and up). Changing it rebuilds the sources so the sapling caster slot is filled or emptied.</summary>
    internal bool SaplingShadows
    {
        get => _saplingShadows;
        set { if (_saplingShadows == value) return; _saplingShadows = value; RequestRebuild(); }
    }

    protected override string DiagnosticCategory => RingPerformanceGpu.UnderstoryCulling;
    protected override string HiddenLabel => "Understory";
    protected override string SetRootName => "UnderstorySet";
    protected override uint[] BaseCaps => UnderstoryBaseCaps;
    protected override float DensityGate => UnderstoryDensity;
    protected override float GateOf(Output output) => output.Group == DeadWoodSlot || output.Group == DeadWoodCasterSlot ? DeadWoodDensity : output.Group == StandFillSlot || output.Group == StandFillCasterSlot ? StandFillDensity : UnderstoryDensity;

    /// <summary>Source rows per budget slot (saplings, shrubs, dead wood, dead-wood caster, sapling caster, stand filler, stand-filler caster) of the shown set.</summary>
    public int[] SourceRows => (ActiveSource as SourceBuild)?.Count ?? new int[SlotCount];
    public string DiagnosticsText() => $"batches={BatchCount} drawOutputs={DrawOutputs} renderers={RendererCount} rows={string.Join("/", SourceRows)} buildMs={LastSourceBuildMs:0.0} builds={SourceBuilds} anchorRebuilds={AnchorRebuilds} pending={SetPending} failed={Failed}";

    private UnderstoryCatalog? _catalog;
    private UnderstoryDeadWood? _deadWood;
    private UnderstoryShrubs? _shrubs;
    private UnderstoryStandFill? _standFill;
    private bool? _appliedSimplify;

    internal sealed class SourceBuild : SourceBuildBase
    {
        public readonly float[]?[] Rows = new float[SlotCount][];
        public readonly Vector4[]?[] Meta = new Vector4[SlotCount][];
        public readonly int[] Count = new int[SlotCount];
        public readonly Aabb[] Bounds = new Aabb[SlotCount];
        /// <summary>Stand-filler rows (slot 5) in canonical terms for the forest lighting canopy raster (SF-4): id, around, across, root altitude, instance height, variant row. Immutable once built.</summary>
        public ulong[]? FillId; public double[]? FillAround, FillAcross; public float[]? FillRoot, FillHeight; public byte[]? FillRow;
    }

    /// <summary>
    /// Main thread (SF-4): appends the current source build's stand fillers within <paramref name="radiusMeters"/> (horizontal) of the focus as canopy records (Variant = the base catalog variant, Width = 2 x the base hull's horizontal radius x instance height),
    /// so the forest lighting raster treats them as small trees. Returns the number appended.
    /// </summary>
    internal int CopyStandFillTrees(RingCanonicalPosition focus, double radiusMeters, List<CanopyTreeRecord> into)
    {
        if (ActiveSource is not SourceBuild build || build.FillId == null) return 0;
        TreeCrownHull[] hulls = TreeCrownHullTable.Get();
        int[] sources = UnderstoryCatalog.StandFillSources;
        double circumference = build.Geometry.Circumference, radiusSquared = radiusMeters * radiusMeters;
        int before = into.Count;
        float gate = Mathf.Clamp(StandFillDensity, 0f, 1f);
        for (int i = 0; i < build.FillId.Length; i++)
        {
            if ((float)(build.FillId[i] >> 40) / 16777215f > gate) continue; // the rank gate of the filler batches and colliders: a filler the renderer culls casts no canopy shade either
            double da = Wrap(build.FillAround![i] - focus.AroundMeters, circumference), dz = build.FillAcross![i] - focus.AcrossMeters;
            if (da * da + dz * dz > radiusSquared) continue;
            int source = sources[build.FillRow![i]];
            if (source < 0 || source >= hulls.Length) continue;
            TreeCrownHull hull = hulls[source];
            into.Add(new CanopyTreeRecord(build.FillId[i], build.FillAround[i], build.FillAcross[i], build.FillRoot![i], 2f * hull.RadiusHorizontal * build.FillHeight![i], build.FillHeight[i], hull.Species, source));
        }
        return into.Count - before;
    }

    private static ulong MixHash(ulong h, ulong v)
    {
        h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
        h *= 0xFF51AFD7ED558CCDUL;
        return h ^ (h >> 32);
    }

    /// <summary>Main thread (SF-4): identity of the current stand-filler set (source build epoch, anchor and row count); 0 while there is none.</summary>
    internal long StandFillSignature()
    {
        if (ActiveSource is not SourceBuild build || build.Count[StandFillSlot] == 0) return 0;
        ulong h = 1469598103934665603UL;
        h = MixHash(MixHash(MixHash(MixHash(h, (ulong)build.Epoch), (ulong)build.AnchorAround), (ulong)build.AnchorAcross), (ulong)build.Count[StandFillSlot]);
        h = MixHash(h, (ulong)BitConverter.SingleToInt32Bits(Mathf.Clamp(StandFillDensity, 0f, 1f))); // a density change rebuilds the raster
        return (long)h | 1L;
    }

    protected override void LoadAssets()
    {
        _catalog = UnderstoryCatalog.Load();
        foreach (UnderstoryLodCarrier lod in _catalog.Lods) Host.RegisterTreeMaterial(lod.Material);
        _shrubs = UnderstoryCatalog.LoadShrubs();
        Host.RegisterTreeMaterial(_shrubs.Lod0.Material);
        Host.RegisterTreeMaterial(_shrubs.Lod1.Material);
        _deadWood = UnderstoryCatalog.LoadDeadWood();
        Host.RegisterTreeMaterial(_deadWood.Near.Material);
        Host.RegisterTreeMaterial(_deadWood.Far.Material);
        _standFill = UnderstoryCatalog.LoadStandFill();
        Host.RegisterTreeMaterial(_standFill.Lod0.Material);
        Host.RegisterTreeMaterial(_standFill.Lod2.Material);
        Host.RegisterTreeMaterial(_standFill.Caster.Material);
    }

    protected override void UpdateAfterCompute()
    {
        // The simplified shading of the distant tree LODs applies to the L2 carrier too (the tree layer's SimplifyDistantTreeMaterials policy; L0 is never simplified, as the tree's LOD0). The far dead-wood carrier follows it.
        bool simplify = Host.SimplifyDistantMaterials();
        if (_catalog == null || _appliedSimplify == simplify) return;
        _appliedSimplify = simplify;
        foreach (UnderstoryLodCarrier lod in _catalog.Lods) if (lod.SourceLod >= 2) lod.Material.SetShaderParameter("simplified_tree_shading", simplify);
        _deadWood?.Far.Material.SetShaderParameter("simplified_tree_shading", simplify);
        _standFill?.Lod2.Material.SetShaderParameter("simplified_tree_shading", simplify);
        _standFill?.Caster.Material.SetShaderParameter("simplified_tree_shading", simplify);
    }

    protected override Task<SourceBuildBase> StartBuild(ScatterRecord[] records, RingLodGeometrySnapshot snapshot, long anchorAround, long anchorAcross, long epoch, GeometryKey key)
    {
        float[] heights = (float[])_catalog!.HeightMeters.Clone(), deadHeights = (float[])_deadWood!.HeightMeters.Clone();
        float unitRadius = 0f, deadRadius = Math.Max(_deadWood.Near.UnitRadius, _deadWood.Far.UnitRadius);
        foreach (UnderstoryLodCarrier lod in _catalog.Lods) unitRadius = Math.Max(unitRadius, lod.UnitRadius);
        float[] shrubHeights = (float[])_shrubs!.HeightMeters.Clone();
        float shrubRadius = Math.Max(_shrubs.Lod0.UnitRadius, _shrubs.Lod1.UnitRadius);
        bool saplingCasters = SaplingShadows, standFillCasters = StandFillShadows;
        float[] fillHeights = (float[])_standFill!.HeightMeters.Clone();
        float fillRadius = Math.Max(_standFill.Lod0.UnitRadius, Math.Max(_standFill.Lod2.UnitRadius, _standFill.Caster.UnitRadius));
        return Task.Run<SourceBuildBase>(() => BuildSources(records, snapshot, anchorAround, anchorAcross, epoch, key, heights, unitRadius, deadHeights, deadRadius, shrubHeights, shrubRadius, saplingCasters, fillHeights, fillRadius, standFillCasters));
    }

    /// <summary>The budget slot a record draws in (0 saplings, 1 shrubs, 2 dead wood), or -1: rock groups.</summary>
    private static int GroupOf(RingScatterRenderGroup group) => group switch { RingScatterRenderGroup.Saplings => Saplings, RingScatterRenderGroup.Shrubs => ShrubSlot, RingScatterRenderGroup.DeadWood => DeadWoodSlot, RingScatterRenderGroup.StandFill => StandFillSlot, _ => -1 };

    /// <summary>
    /// Pure (worker): the sapling and dead-wood rows in the frame of the anchor, nearest first (ties by record id), so a binding LOD budget drops the farthest rows first. Snags and fallen trees (rows whose ShadowMinSizeM admits the record) are also copied to the
    /// caster list (slot 3), which draws the far carrier into the sun shadow. With saplingCasters every sapling row is also copied to slot 4.
    /// </summary>
    internal static SourceBuild BuildSources(IReadOnlyList<ScatterRecord> records, RingLodGeometrySnapshot live, long anchorAround, long anchorAcross, long epoch, GeometryKey key, float[] heightMeters, float unitRadius, float[] deadHeightMeters, float deadUnitRadius, float[]? shrubHeightMeters = null, float shrubUnitRadius = 0f, bool saplingCasters = false, float[]? standFillHeightMeters = null, float standFillUnitRadius = 0f, bool standFillCasters = false)
    {
        shrubHeightMeters ??= new float[UnderstoryCatalog.ShrubCount];
        standFillHeightMeters ??= new float[UnderstoryCatalog.StandFillSources.Length];
        var watch = Stopwatch.StartNew();
        RingLodGeometrySnapshot g = live.CenteredAt(anchorAround, anchorAcross);
        double circumference = g.CircumferenceMeters;
        var classes = new Dictionary<ushort, RingScatterClass>();
        foreach (RingScatterClass row in RingScatterClassTable.Production.Rows) classes[row.Id] = row;
        var build = new SourceBuild { Epoch = epoch, AnchorAround = anchorAround, AnchorAcross = anchorAcross, Geometry = key };
        var slotOf = new int[records.Count];
        var variantOf = new int[records.Count];
        var casts = new bool[records.Count];
        for (int i = 0; i < records.Count; i++)
        {
            slotOf[i] = -1; variantOf[i] = -1;
            if (!classes.TryGetValue(records[i].ClassId, out RingScatterClass? row) || row.MeshCatalog != RingScatterMeshCatalog.Trees) continue;
            int slot = GroupOf(row.RenderGroup);
            if (slot < 0) continue;
            int variant = slot == Saplings ? UnderstoryCatalog.VariantRowOf(records[i].Mesh) : slot == ShrubSlot ? UnderstoryCatalog.ShrubRowOf(records[i].Mesh) : slot == StandFillSlot ? UnderstoryCatalog.StandFillRowOf(records[i].Mesh) : UnderstoryCatalog.DeadWoodRowOf(records[i].Mesh);
            if (variant < 0 || variant >= (slot == Saplings ? heightMeters.Length : slot == ShrubSlot ? shrubHeightMeters.Length : slot == StandFillSlot ? standFillHeightMeters.Length : deadHeightMeters.Length)) continue;
            slotOf[i] = slot; variantOf[i] = variant;
            build.Count[slot]++;
            if (slot == Saplings && saplingCasters) build.Count[SaplingCasterSlot]++;
            if (slot == StandFillSlot && standFillCasters) build.Count[StandFillCasterSlot]++;
            if (slot == DeadWoodSlot && records[i].Size >= row.ShadowMinSizeM) { casts[i] = true; build.Count[DeadWoodCasterSlot]++; }
        }
        var order = new int[records.Count];
        var keys = new double[records.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
            double da = Wrap(records[i].AroundCm * .01 - anchorAround, circumference), dz = records[i].AcrossCm * .01 - anchorAcross;
            keys[i] = da * da + dz * dz;
        }
        Array.Sort(order, (a, b) => keys[a] != keys[b] ? keys[a].CompareTo(keys[b]) : records[a].Id.CompareTo(records[b].Id));
        var cursor = new int[SlotCount];
        var minimum = new Vector3[SlotCount]; var maximum = new Vector3[SlotCount]; var maxScale = new float[SlotCount];
        for (int slot = 0; slot < SlotCount; slot++)
        {
            if (build.Count[slot] == 0) continue;
            build.Rows[slot] = new float[build.Count[slot] * RingGpuVegetationCompute.TreeCustomFloats];
            build.Meta[slot] = new Vector4[build.Count[slot]];
            minimum[slot] = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity); maximum[slot] = -minimum[slot];
        }
        int fillCount = build.Count[StandFillSlot], fillAt = 0;
        if (fillCount > 0) { build.FillId = new ulong[fillCount]; build.FillAround = new double[fillCount]; build.FillAcross = new double[fillCount]; build.FillRoot = new float[fillCount]; build.FillHeight = new float[fillCount]; build.FillRow = new byte[fillCount]; }
        var row16 = new float[RingGpuVegetationCompute.TreeCustomFloats];
        foreach (int i in order)
        {
            int slot = slotOf[i], variant = variantOf[i];
            if (slot < 0) continue;
            ScatterRecord rec = records[i];
            RingScatterClass cls = classes[rec.ClassId];
            double around = rec.AroundCm * .01, across = rec.AcrossCm * .01;
            // MeshCatalog.Trees: the instance scale is the record's size times the source variant's height; the carrier is normalised (1 unit tall, a log's length 0.55), so the embed fractions apply to that instance height.
            float scale = rec.Size * (slot == Saplings ? heightMeters[variant] : slot == ShrubSlot ? shrubHeightMeters[variant] : slot == StandFillSlot ? standFillHeightMeters[variant] : deadHeightMeters[variant]);
            (Basis rotation, Vector3 position) = RingScatterLayer.RecordPose(rec, cls, 0.0, scale, g);
            Basis basis = rotation.Scaled(new Vector3(scale, scale, scale));
            Array.Clear(row16);
            row16[0] = basis.Column0.X; row16[1] = basis.Column1.X; row16[2] = basis.Column2.X; row16[3] = position.X;
            row16[4] = basis.Column0.Y; row16[5] = basis.Column1.Y; row16[6] = basis.Column2.Y; row16[7] = position.Y;
            row16[8] = basis.Column0.Z; row16[9] = basis.Column1.Z; row16[10] = basis.Column2.Z; row16[11] = position.Z;
            row16[12] = variant; // INSTANCE_CUSTOM.x: the carrier variant row; .z (the rank) is written by the compute from the metadata
            var meta = new Vector4((float)(rec.Id >> 40) / 16777215f, 0f, (float)Wrap(around - anchorAround, circumference), (float)(across - anchorAcross));
            int at = cursor[slot]++;
            Array.Copy(row16, 0, build.Rows[slot]!, at * RingGpuVegetationCompute.TreeCustomFloats, RingGpuVegetationCompute.TreeCustomFloats);
            build.Meta[slot]![at] = meta;
            minimum[slot] = minimum[slot].Min(position); maximum[slot] = maximum[slot].Max(position); maxScale[slot] = Math.Max(maxScale[slot], scale);
            if (slot == StandFillSlot)
            {
                build.FillId![fillAt] = rec.Id; build.FillAround![fillAt] = around; build.FillAcross![fillAt] = across;
                build.FillRoot![fillAt] = rec.SurfaceHeight - (float)(.5 * (cls.EmbedMin + cls.EmbedMax)) * scale; build.FillHeight![fillAt] = scale; build.FillRow![fillAt] = (byte)variant;
                fillAt++;
            }
            if (slot == Saplings && saplingCasters)
            {
                int sat = cursor[SaplingCasterSlot]++;
                Array.Copy(row16, 0, build.Rows[SaplingCasterSlot]!, sat * RingGpuVegetationCompute.TreeCustomFloats, RingGpuVegetationCompute.TreeCustomFloats);
                build.Meta[SaplingCasterSlot]![sat] = meta;
                minimum[SaplingCasterSlot] = minimum[SaplingCasterSlot].Min(position); maximum[SaplingCasterSlot] = maximum[SaplingCasterSlot].Max(position); maxScale[SaplingCasterSlot] = Math.Max(maxScale[SaplingCasterSlot], scale);
            }
            if (slot == StandFillSlot && standFillCasters)
            {
                int fat = cursor[StandFillCasterSlot]++;
                Array.Copy(row16, 0, build.Rows[StandFillCasterSlot]!, fat * RingGpuVegetationCompute.TreeCustomFloats, RingGpuVegetationCompute.TreeCustomFloats);
                build.Meta[StandFillCasterSlot]![fat] = meta;
                minimum[StandFillCasterSlot] = minimum[StandFillCasterSlot].Min(position); maximum[StandFillCasterSlot] = maximum[StandFillCasterSlot].Max(position); maxScale[StandFillCasterSlot] = Math.Max(maxScale[StandFillCasterSlot], scale);
            }
            if (casts[i])
            {
                int cat = cursor[DeadWoodCasterSlot]++;
                Array.Copy(row16, 0, build.Rows[DeadWoodCasterSlot]!, cat * RingGpuVegetationCompute.TreeCustomFloats, RingGpuVegetationCompute.TreeCustomFloats);
                build.Meta[DeadWoodCasterSlot]![cat] = meta;
                minimum[DeadWoodCasterSlot] = minimum[DeadWoodCasterSlot].Min(position); maximum[DeadWoodCasterSlot] = maximum[DeadWoodCasterSlot].Max(position); maxScale[DeadWoodCasterSlot] = Math.Max(maxScale[DeadWoodCasterSlot], scale);
            }
        }
        for (int slot = 0; slot < SlotCount; slot++)
            if (build.Count[slot] > 0) build.Bounds[slot] = new Aabb(minimum[slot], maximum[slot] - minimum[slot]).Grow(maxScale[slot] * (slot == Saplings || slot == SaplingCasterSlot ? unitRadius : slot == ShrubSlot ? shrubUnitRadius : slot == StandFillSlot || slot == StandFillCasterSlot ? standFillUnitRadius : deadUnitRadius) + 2f);
        build.BuildMs = watch.Elapsed.TotalMilliseconds;
        return build;
    }

    protected override void CreateOutputs(OutputSet set)
    {
        var build = (SourceBuild)set.Source;
        if (build.Count[Saplings] > 0)
            for (int l = 0; l < _catalog!.Lods.Length; l++)
            {
                UnderstoryLodCarrier lod = _catalog.Lods[l];
                AddOutput(set, Saplings, l, false, $"UnderstorySapling_L{lod.SourceLod}", lod.Carrier, lod.Material, build.Rows[Saplings]!, build.Meta[Saplings]!, build.Count[Saplings], build.Bounds[Saplings]);
            }
        if (build.Count[ShrubSlot] > 0)
        {
            UnderstoryLodCarrier[] carriers = { _shrubs!.Lod0, _shrubs.Lod1 };
            for (int l = 0; l < carriers.Length; l++)
                AddOutput(set, ShrubSlot, l, false, $"UnderstoryShrub_L{l}", carriers[l].Carrier, carriers[l].Material, build.Rows[ShrubSlot]!, build.Meta[ShrubSlot]!, build.Count[ShrubSlot], build.Bounds[ShrubSlot]);
        }
        if (build.Count[DeadWoodSlot] > 0)
        {
            UnderstoryLodCarrier[] carriers = { _deadWood!.Near, _deadWood.Far };
            for (int l = 0; l < carriers.Length; l++)
                AddOutput(set, DeadWoodSlot, l, false, l == 0 ? "UnderstoryDeadWood_Near" : "UnderstoryDeadWood_Far", carriers[l].Carrier, carriers[l].Material, build.Rows[DeadWoodSlot]!, build.Meta[DeadWoodSlot]!, build.Count[DeadWoodSlot], build.Bounds[DeadWoodSlot]);
        }
        if (build.Count[DeadWoodCasterSlot] > 0)
            AddOutput(set, DeadWoodCasterSlot, -1, true, "UnderstoryDeadWood_Shadow", _deadWood!.Far.Carrier, _deadWood.Far.Material, build.Rows[DeadWoodCasterSlot]!, build.Meta[DeadWoodCasterSlot]!, build.Count[DeadWoodCasterSlot], build.Bounds[DeadWoodCasterSlot]);
        if (build.Count[StandFillSlot] > 0)
        {
            UnderstoryLodCarrier[] carriers = { _standFill!.Lod0, _standFill.Lod2 };
            for (int l = 0; l < carriers.Length; l++)
                AddOutput(set, StandFillSlot, l, false, l == 0 ? "UnderstoryStandFill_L0" : "UnderstoryStandFill_L2", carriers[l].Carrier, carriers[l].Material, build.Rows[StandFillSlot]!, build.Meta[StandFillSlot]!, build.Count[StandFillSlot], build.Bounds[StandFillSlot]);
        }
        if (build.Count[StandFillCasterSlot] > 0)
            AddOutput(set, StandFillCasterSlot, -1, true, "UnderstoryStandFill_Shadow", _standFill!.Caster.Carrier, _standFill.Caster.Material, build.Rows[StandFillCasterSlot]!, build.Meta[StandFillCasterSlot]!, build.Count[StandFillCasterSlot], build.Bounds[StandFillCasterSlot]);
        if (build.Count[SaplingCasterSlot] > 0)
        {
            UnderstoryLodCarrier far = _catalog!.Lods.First(l => l.SourceLod >= 2);
            AddOutput(set, SaplingCasterSlot, -1, true, "UnderstorySapling_Shadow", far.Carrier, far.Material, build.Rows[SaplingCasterSlot]!, build.Meta[SaplingCasterSlot]!, build.Count[SaplingCasterSlot], build.Bounds[SaplingCasterSlot]);
        }
    }

    protected override void ApplyFrameUniforms(MultiMeshInstance3D renderer, OutputSet set, Vector3 origin) { }

    protected override FrameTerms ResolveFrame(Output output, float scale, float shadowDistance)
    {
        // The caster draws every snag and log inside the sun near-map distance at every preset (the rock layer's ShadowDistance logic).
        // Saplings are not drawn beyond their class reach, so they cast no further.
        if (output.Caster && output.Group == StandFillCasterSlot) return new FrameTerms(StandFillCasterSlot, 0f, Math.Min(shadowDistance, StandFillCasterReachMeters * scale), -1f, -1f, -1f, -1f);
        if (output.Caster && output.Group == SaplingCasterSlot) return new FrameTerms(SaplingCasterSlot, 0f, Math.Min(shadowDistance, SaplingBands[1].Far * scale), -1f, -1f, -1f, -1f);
        if (output.Caster) return new FrameTerms(DeadWoodCasterSlot, 0f, shadowDistance, -1f, -1f, -1f, -1f);
        var band = output.Group == DeadWoodSlot ? DeadWoodBands[output.Lod] : output.Group == ShrubSlot ? ShrubBands[output.Lod] : output.Group == StandFillSlot ? StandFillBands[output.Lod] : SaplingBands[output.Lod];
        float inStart = -1f, inEnd = -1f, outStart = -1f, outEnd = -1f;
        if (band.InStart >= 0f) { inStart = band.InStart * scale; inEnd = band.InEnd * scale; }
        if (band.OutStart >= 0f) { outStart = band.OutStart * scale; outEnd = band.OutEnd * scale; }
        return new FrameTerms(output.Group, band.Near * scale, band.Far * scale, inStart, inEnd, outStart, outEnd);
    }
}
