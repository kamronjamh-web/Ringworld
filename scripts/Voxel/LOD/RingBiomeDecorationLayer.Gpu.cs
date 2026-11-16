using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingBiomeDecorationLayer
{
    [ExportGroup("GPU tree culling")]
    [Export] public bool UseGpuCulling { get; set; } = true;
    [Export] public bool SkipUnchangedGpuCulling { get; set; } = true;
    [Export] public bool MergeFarGpuTreeBatches { get; set; } = true;
    [Export] public bool KeepTreeLodFallbacksUntilReady { get; set; } = true;
    [Export(PropertyHint.Range, "256,1048576,256")] public int MaximumGpuTreeRoots { get; set; } = 65536;
    [Export(PropertyHint.Range, "16,2048,16")] public int MaximumGpuTreeBatches { get; set; } = 1024;
    [Export(PropertyHint.Range, "0.25,8,0.25")] public float TreeStreamingBudgetMilliseconds { get; set; } = 2.0f;
    public bool TreePreparationPending => _gpuTreePreparation != null;
    public long TreePreparationGroupsRebuilt { get; private set; }
    public long TreePreparationGroupsReused { get; private set; }
    public double LastTreePreparationMilliseconds { get; private set; }
    public bool GpuTreeCullingActive => _gpuTreePresentation && _gpuTreeCompute is { Ready: true, CompletedUpdates: > 0 };
    public string GpuTreeCullingStatus { get; private set; } = "CPU fallback";
    public long GpuTreeResidentBytes => _gpuTreeCompute?.EstimatedBytes ?? 0;
    // Cached sum of resident group roots, read every frame. Valid while the group
    // dictionary instance, its count and GpuTreeSourceEpoch are unchanged (every
    // same-count group replacement bumps the epoch; group Metadata length is fixed at construction).
    public int GpuTreeResidentRootCount
    {
        get
        {
            if (!ReferenceEquals(_gpuTreeResidentRootCountSource, _gpuTreeGroups)
                || _gpuTreeResidentRootCountGroups != _gpuTreeGroups.Count
                || _gpuTreeResidentRootCountEpoch != GpuTreeSourceEpoch)
            {
                _gpuTreeResidentRootCount = _gpuTreeGroups.Values.Sum(group => group.RootCount);
                _gpuTreeResidentRootCountSource = _gpuTreeGroups;
                _gpuTreeResidentRootCountGroups = _gpuTreeGroups.Count;
                _gpuTreeResidentRootCountEpoch = GpuTreeSourceEpoch;
            }
            return _gpuTreeResidentRootCount;
        }
    }
    private Dictionary<GpuGroupKey, GpuTreeGroup>? _gpuTreeResidentRootCountSource;
    private int _gpuTreeResidentRootCount, _gpuTreeResidentRootCountGroups;
    private long _gpuTreeResidentRootCountEpoch = long.MinValue;
    public int GpuTreeBatchCount => _gpuTreeOutputs.Count;
    public int GpuTreeFallbackBatchCount => _gpuTreeFallbackOutputs.Count;
    public long GpuTreeSourceEpoch { get; private set; }
    public long GpuTreeCompletedUpdates => _gpuTreeCompute?.CompletedUpdates ?? 0;
    public long GpuTreeSkippedUnchangedUpdates => _gpuTreeCompute?.SkippedUnchangedUpdates ?? 0;
    public bool GpuTreeCullingSettled => _gpuFrameCacheComplete;

    private readonly record struct GpuGroupKey(long Around, long Across, int Variant);
    private readonly record struct GpuOutputKey(GpuGroupKey Group, int Lod);
    private readonly record struct GpuSourceSettings(int MaximumRoots, int MaximumBatches, int MaximumNodes, float RootEmbed, bool MergeFar, float CellSize, int TileCells, bool SharedFar = false,
        bool MergeMid = false, int MidSpan = 2);
    private sealed class GpuTreeGroup
    {
        public required GpuGroupKey Key;
        public required CachedTree[] Roots;
        // CPU rows exist only for fixtures. Production groups keep compact roots instead (see Expansion) and the GPU builds the rows.
        public required float[] Rows;
        public RingGpuVegetationCompute.TreeExpansion? Expansion;
        public required Vector4[] Metadata;
        public required Aabb Bounds;
        public long AroundOrigin;
        public int AcrossOrigin;
        public Vector2 CanonicalMin, CanonicalMax;
        public bool HasProtected;
        public bool SharedImpostor;
        public int RowStride => SharedImpostor ? RingGpuVegetationCompute.TreeCustomFloats : RingGpuVegetationCompute.TreeFloats;
        public int RootCount => Metadata.Length;
        public GpuTreeGroup[]? Members;
        public int[]? MemberOffsets;
    }
    private sealed class GpuTreeOutput
    {
        // Renderer draws the full list (the sun shadows' casters while no shadow proxy is ready; never the camera). CameraRenderer draws the camera list and
        // VerifyRenderer (TreeCullVerify only) the rows the frustum filter dropped, in magenta. Both are children of Renderer.
        public MultiMeshInstance3D? CameraRenderer, VerifyRenderer;
        public required MultiMeshInstance3D Renderer;
        public required int BatchId;
        public required GpuTreeGroup SourceGroup;
        public Transform3D CachedGlobalTransform;
        public long TransformCacheVersion = long.MinValue;
        public bool Ready;
        public bool HasCachedVisibility;
        public bool CachedVisibility;
        public bool TreeDitherData;
        public bool FarQuad;
        public bool RenderLodMesh;
    }
    private Dictionary<GpuGroupKey, GpuTreeGroup> _gpuTreeGroups = new();
    private Dictionary<int, Dictionary<GpuGroupKey, GpuTreeGroup>> _gpuTreeMergedGroups = new();
    private readonly Dictionary<GpuOutputKey, GpuTreeGroup> _gpuTreeOutputGroups = new();
    private readonly Dictionary<GpuOutputKey, GpuTreeOutput> _gpuTreeOutputs = new();
    // These outputs are deliberately absent from the next compute frame. Their
    // last indirect contents remain visible only while current outputs upload.
    private readonly Dictionary<GpuOutputKey, GpuTreeOutput> _gpuTreeFallbackOutputs = new();
    private readonly Dictionary<GpuOutputKey, long> _gpuTreeFallbackOrder = new();
    private long _gpuTreeFallbackSequence;
    private Dictionary<ulong, (GpuTreeGroup Group, int Index)> _gpuTreeRootLookup = new();
    private HashSet<ulong> _gpuTreeProtectedIds = new();
    private readonly List<ulong> _gpuTreeProtectedIdScratch = new();
    private readonly HashSet<GpuTreeGroup> _gpuTreeProtectedChangedScratch = new();
    private readonly Dictionary<GpuTreeGroup, List<GpuTreeOutput>> _gpuTreeProtectedOutputsByGroupScratch = new();
    // Per selection rebuild: a group's (near, far) depends only on the group and the camera.
    private readonly Dictionary<GpuTreeGroup, (float Near, float Far)> _gpuTreeGroupDistanceScratch = new();
    private RingGpuVegetationCompute? _gpuTreeCompute;
    private RingLodGeometrySnapshot? _gpuTreeGeometry;
    private Transform3D _gpuTreeLayerTransform;
    private float _gpuTreeRootEmbed;
    private bool _gpuTreeSourcesDirty = true;
    private bool _gpuTreePresentation, _gpuTreeFailed, _gpuTreeFixture;
    private bool _gpuFixtureLastMerge, _gpuFixtureLastSharedFar, _gpuFixtureLastMergeMid;
    private ulong _gpuTreeSourceEdit = ulong.MaxValue;
    private GpuSourceSettings? _gpuTreeSourceSettings;
    private readonly List<(GpuOutputKey Key, float Distance, bool Priority)> _gpuTreeOrder = new();
    private readonly List<(GpuOutputKey Key, float Distance, bool Priority)> _gpuTreeOrderScratch = new();
    private readonly List<GpuOutputKey> _gpuTreeKeyScratch = new();
    private static readonly Comparison<(GpuOutputKey Key, float Distance, bool Priority)> GpuTreeOrderComparison = CompareGpuTreeOrder;
    private static readonly Comparison<(GpuOutputKey Key, float Distance, bool Priority)> GpuTreeNearestComparison = CompareGpuTreeNearest;
    private List<RingGpuVegetationCompute.BatchFrameParameters> _gpuTreeLastFrameList = new();
    private bool _gpuLastFramesValid;
    private int _gpuTreeStatusInstalled = -1, _gpuTreeStatusWanted = -1, _gpuTreeStatusFallback = -1;
    private string _gpuTreeStatusText = "";
    private readonly HashSet<GpuOutputKey> _gpuTreeWanted = new();
    private bool _gpuTreeSelectionValid;
    private long _gpuTreeSelectionEpoch, _gpuTreeSelectionProtectionRevision;
    private RingCanonicalPosition? _gpuTreeSelectionFocus;
    private Vector3 _gpuTreeSelectionCamera;
    private double _gpuTreeSelectionCircumference;
    private Transform3D _gpuTreeSelectionLayerTransform;
    private float[]? _gpuTreeSelectionEdges, _gpuTreeSelectionGates;
    private uint[]? _gpuTreeSelectionCaps;
    private float _gpuTreeSelectionGuard;
    private int _gpuTreeSelectionMaximumBatches, _gpuTreeSelectionMaximumNodes;
    private bool _gpuTreeSelectionMergeFar, _gpuTreeSelectionSharedFar, _gpuTreeSelectionCompactProfile, _gpuTreeSelectionMergeMid, _gpuTreeSelectionPrune;
    private int _gpuTreeSelectionMidSpan, _gpuTreeSelectionPolicySignature, _gpuLastPolicySignature;
    private Vector3 _gpuLastCamera = new(float.NaN, float.NaN, float.NaN);
    private RingCanonicalPosition? _gpuLastFocus;
    private double _gpuLastCircumference = double.NaN;
    private bool _gpuFrameCacheComplete;
    private uint[]? _gpuLastCaps;
    private float[]? _gpuLastEdges, _gpuLastRankGates;
    private float _gpuLastGuard = float.NaN;
    private Transform3D _gpuLastFrameLayerTransform;
    private IEnumerator<bool>? _gpuTreePreparation;
    // Collision churn must not cancel an in-flight source plan. Protection is
    // frozen while a plan runs and reconciled by one diff after it ends.
    private bool _gpuTreeProtectedRefreshDeferred;
    // Bumped only when a protection change can alter the wanted output set, so a
    // collider-only change keeps the settled selection and its frame cache.
    private long _gpuTreeProtectionLayoutRevision;
    private bool _gpuTreeSelectionTruncated, _gpuTreeProtectionRemovedScratch;
    private readonly List<(GpuTreeGroup Group, int Index)> _gpuTreeNewlyProtectedScratch = new();
    private readonly Dictionary<(GpuTreeGroup Source, int Lod), (GpuTreeGroup Aggregate, int Offset)> _gpuTreeProtectedOwnerScratch = new();

    // Streaming events only invalidate inputs here. One service call coalesces
    // all completed tiles; unchanged groups retain their transforms and RD buffers.
    private bool _gpuTreeSourceRefreshForced;
    private long _gpuTreeSourceRefreshRevision;
    private void RefreshGpuTreeResidentSources(bool force = false)
    {
        _gpuTreeSourcesDirty = true;
        _gpuTreeSourceRefreshRevision++;
        _gpuTreeSourceRefreshForced |= force;
    }

    private bool ServiceGpuTreeRenderUpdates()
    {
        if (!UseGpuCulling || !UseRegionalImportedForest)
        {
            StopGpuTreePresentation("CPU fallback");
            _gpuTreeFailed = false;
            return false;
        }
        if (_explorer == null || _grid == null) return false;
        if (!EnsureGpuTreeBackend()) return false;
        // The completion pass (CompleteTileStreaming) arms the same deadline before its own bookkeeping; only arm here when nobody has.
        bool armedDeadline = LegacyTreeStreamingBudget || _gpuTreeFrameDeadline == 0;
        if (armedDeadline) _gpuTreeFrameDeadline = Time.GetTicksUsec() + (ulong)(GpuTreeStreamingBudget * 1000);
        try
        {
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        TryRebaseGpuTreeSources(geometry);
        geometry = _grid.CaptureLodGeometrySnapshot();
        var settings = new GpuSourceSettings(MaximumGpuTreeRoots, MaximumGpuTreeBatches, MaximumVegetationRendererNodes,
            RootEmbedMeters, MergeFarGpuTreeBatches, CellSizeMeters, SpatialTileCells, EffectiveSharedFarTreeImpostors, MergeMidGpuTreeBatches, EffectiveMidTreeBatchTileSpan);
        bool structuralChange = _gpuTreeSourceEdit != _vegetationEditVersion
            || _gpuTreeGeometry != geometry || _gpuTreeLayerTransform != GlobalTransform || settings != _gpuTreeSourceSettings;
        if (structuralChange) { _gpuTreeSourceRefreshForced = true; _gpuTreeSourcesDirty = true; }
        if (_gpuTreePreparation != null && !GpuTreePreparationMatches(geometry, settings))
            CancelGpuTreeSourcePreparation();
        bool deferCompletedTiles = _gpuTreeSourcesDirty && !_gpuTreeSourceRefreshForced
            && _vegetationTilesChangedSinceRender > 0 && VegetationPendingTiles > 0
            && _vegetationTilesChangedSinceRender < Mathf.Max(1, VegetationTilesPerRenderRefresh)
            && _gpuTreeOutputs.Count > 0;
        if (TreePreparationPending || (_gpuTreeSourcesDirty && !deferCompletedTiles))
        {
            ReconcileGpuTreeSources(geometry);
            _gpuTreeSourceSettings = settings;
            _gpuTreeSourceRefreshForced = false;
        }
        return ServiceGpuTreeOutputs(_explorer.GlobalPosition, _explorer.CanonicalPosition,
            _grid.GetCanonicalRingCircumferenceMeters64());
        }
        finally { if (armedDeadline) _gpuTreeFrameDeadline = 0; }
    }

    private bool EnsureGpuTreeBackend()
    {
        if (_gpuTreeFailed) return false;
        _gpuTreeCompute ??= new RingGpuVegetationCompute { HazeChannel = RingVegetationHaze.Channel.Trees };
        _gpuTreeCompute.Initialize();
        if (_gpuTreeCompute.Failed)
        {
            string error = _gpuTreeCompute.Status;
            StopGpuTreePresentation(error);
            _gpuTreeFailed = true;
            return false;
        }
        GpuTreeCullingStatus = _gpuTreeCompute.Status;
        return _gpuTreeCompute.Ready;
    }

    // Cooperative selection/build/merge implementation lives in .GpuPreparation.cs.

    private IEnumerable<GpuTreeGroup> GroupsForGpuLod(int lod) => GroupContainerForGpuLod(lod).Values;

    private Dictionary<GpuGroupKey, GpuTreeGroup> GroupContainerForGpuLod(int lod)
    {
        if (((lod == 2 && MergeMidGpuTreeBatches) || (lod is 3 or 4 && MergeFarGpuTreeBatches))
            && _gpuTreeMergedGroups.TryGetValue(lod, out Dictionary<GpuGroupKey, GpuTreeGroup>? merged))
            return merged;
        return _gpuTreeGroups;
    }

    // Far outputs deliberately use the same growing bin sizes as the retired
    // CPU renderer: LOD4=2x close tile span and LOD5=4x. Rows are copied only
    // when resident sources change, never while a camera moves.
    private void RebuildGpuTreeMergedGroups(double circumference)
    {
        var previous = new Dictionary<int, Dictionary<GpuGroupKey, GpuTreeGroup>>(_gpuTreeMergedGroups);
        _gpuTreeMergedGroups.Clear();
        if (!MergeFarGpuTreeBatches && !MergeMidGpuTreeBatches) return;
        float close = Mathf.Max(20, CellSizeMeters) * Mathf.Clamp(SpatialTileCells, 4, 24);
        VegetationLayout layout = CaptureVegetationLayout(circumference);
        foreach (int lod in new[] { 2, 3, 4 })
        {
            if (lod == 2 && !MergeMidGpuTreeBatches) continue;
            if (lod >= 3 && !MergeFarGpuTreeBatches) continue;
            float span = close * (lod == 2 ? EffectiveMidTreeBatchTileSpan : lod == 3 ? 2 : 4);
            var chunks = new Dictionary<GpuGroupKey, List<GpuTreeGroup>>();
            foreach (GpuTreeGroup source in _gpuTreeGroups.Values)
            {
                // Group keys use the stable source tile, never whichever root
                // currently sorts first by rank. This remains stable as ranks
                // and protected metadata change.
                (double tileAround, double tileAcross) = VegetationTileCenter(new VegetationTileKey(source.Key.Around, source.Key.Across), layout);
                long aroundBin = (long)Math.Floor(tileAround / span);
                int acrossBin = (int)Math.Floor(tileAcross / span);
                var key = new GpuGroupKey(aroundBin, acrossBin, lod >= 3 && EffectiveSharedFarTreeImpostors ? -1 : source.Key.Variant);
                if (!chunks.TryGetValue(key, out List<GpuTreeGroup>? list)) chunks[key] = list = new();
                list.Add(source);
            }
            var built = new Dictionary<GpuGroupKey, GpuTreeGroup>();
            foreach (var entry in chunks)
            {
                entry.Value.Sort(static (a, b) => a.Key.Around != b.Key.Around ? a.Key.Around.CompareTo(b.Key.Around)
                    : a.Key.Across != b.Key.Across ? a.Key.Across.CompareTo(b.Key.Across) : a.Key.Variant.CompareTo(b.Key.Variant));
                int count = entry.Value.Sum(value => value.RootCount);
                long originAround = (long)Math.Floor((double)entry.Key.Around * (double)span); int originAcross = (int)Math.Floor((double)entry.Key.Across * (double)span);
                if (previous.TryGetValue(lod, out Dictionary<GpuGroupKey, GpuTreeGroup>? oldMap)
                    && oldMap.TryGetValue(entry.Key, out GpuTreeGroup? retained)
                    && retained.AroundOrigin == originAround && retained.AcrossOrigin == originAcross
                    && retained.SharedImpostor == (lod >= 3 && EffectiveSharedFarTreeImpostors)
                    && retained.Members is not null && SameMemberReferences(retained.Members, entry.Value))
                {
                    built[entry.Key] = retained;
                    continue;
                }
                bool shared = lod >= 3 && EffectiveSharedFarTreeImpostors;
                int stride = shared ? RingGpuVegetationCompute.TreeCustomFloats : RingGpuVegetationCompute.TreeFloats;
                var rows = new float[count * stride]; var metadata = new Vector4[count]; Aabb bounds = default; bool first = true; int dst = 0;
                Vector2 minimum = new(float.PositiveInfinity, float.PositiveInfinity), maximum = -minimum;
                var offsets = new int[entry.Value.Count];
                for (int member = 0; member < entry.Value.Count; member++)
                {
                    GpuTreeGroup source = entry.Value[member]; offsets[member] = dst;
                    int roots = source.RootCount;
                    if (shared)
                        for (int i = 0; i < roots; i++)
                        {
                            Array.Copy(source.Rows, i * source.RowStride, rows, (dst + i) * stride, RingGpuVegetationCompute.TreeFloats);
                            rows[(dst + i) * stride + RingGpuVegetationCompute.TreeFloats] = source.Key.Variant;
                        }
                    else Array.Copy(source.Rows, 0, rows, dst * stride, source.Rows.Length);
                    for (int i = 0; i < roots; i++)
                    {
                        Vector4 sourceMeta = source.Metadata[i];
                        // Keep the mega-ring canonical subtraction in double.
                        // Converting the ~31M-metre origin to float first loses
                        // the two-metre-scale source offset at the seam.
                        float around = (float)WrappedVegetationDelta((double)source.AroundOrigin + (double)sourceMeta.Z - (double)originAround, circumference);
                        float across = (float)((double)source.AcrossOrigin + (double)sourceMeta.W - (double)originAcross);
                        metadata[dst + i] = new Vector4(sourceMeta.X, sourceMeta.Y, around, across);
                        minimum = minimum.Min(new Vector2(around, across)); maximum = maximum.Max(new Vector2(around, across));
                    }
                    bounds = first ? source.Bounds : bounds.Merge(source.Bounds); first = false; dst += roots;
                }
                var aggregate = new GpuTreeGroup { Key = entry.Key, Roots = Array.Empty<CachedTree>(), Rows = rows, Metadata = metadata,
                    Bounds = bounds.Grow(8), AroundOrigin = originAround, AcrossOrigin = originAcross, CanonicalMin = minimum, CanonicalMax = maximum,
                    HasProtected = Array.Exists(metadata, value => value.Y > .5f), SharedImpostor = shared, Members = entry.Value.ToArray(), MemberOffsets = offsets };
                RetireGpuOutput(new GpuOutputKey(entry.Key, lod));
                built[entry.Key] = aggregate;
            }
            if (previous.TryGetValue(lod, out Dictionary<GpuGroupKey, GpuTreeGroup>? oldGroups))
                foreach (GpuGroupKey obsolete in oldGroups.Keys) if (!built.ContainsKey(obsolete)) RetireGpuOutput(new GpuOutputKey(obsolete, lod));
            _gpuTreeMergedGroups[lod] = built;
        }
    }

    private Aabb GpuTreePrototypeBounds(int variant)
    {
        Aabb bounds = default; bool first = true;
        foreach (var templates in new[] { _lod1AuthoredTemplates, _lod2AuthoredTemplates, _lod3AuthoredTemplates, _lod4AuthoredTemplates })
            if (templates != null && variant >= 0 && variant < templates.Length && templates[variant].Multimesh?.Mesh is Mesh mesh)
            { bounds = first ? mesh.GetAabb() : bounds.Merge(mesh.GetAabb()); first = false; }
        return first ? new Aabb(new Vector3(-1, 0, -1), new Vector3(2, 2, 2)) : bounds;
    }

    private void FlushDeferredGpuTreeProtectedRefresh()
    {
        if (_gpuTreeProtectedRefreshDeferred && _gpuTreePreparation == null) RefreshGpuTreeProtectedFlags();
    }

    private void RefreshGpuTreeProtectedFlags()
    {
        if (_gpuTreeFixture) return;
        if (_gpuTreePreparation != null) { _gpuTreeProtectedRefreshDeferred = true; return; }
        _gpuTreeProtectedRefreshDeferred = false;
        _gpuTreeNewlyProtectedScratch.Clear(); _gpuTreeProtectedOwnerScratch.Clear(); _gpuTreeProtectionRemovedScratch = false;
        HashSet<GpuTreeGroup> changed = _gpuTreeProtectedChangedScratch; changed.Clear();
        _gpuTreeProtectedIdScratch.Clear(); _gpuTreeProtectedIdScratch.AddRange(_gpuTreeProtectedIds);
        for (int idIndex = 0; idIndex < _gpuTreeProtectedIdScratch.Count; idIndex++)
        {
            ulong id = _gpuTreeProtectedIdScratch[idIndex];
            if (!_importedForestBodies.ContainsKey(id))
            {
                if (_gpuTreeRootLookup.TryGetValue(id, out var entry)) { entry.Group.Metadata[entry.Index].Y = 0; changed.Add(entry.Group); _gpuTreeProtectionRemovedScratch = true; }
                _gpuTreeProtectedIds.Remove(id);
            }
        }
        foreach (ulong id in _importedForestBodies.Keys)
        {
            if (_gpuTreeProtectedIds.Contains(id)) continue;
            if (_gpuTreeRootLookup.TryGetValue(id, out var entry))
            { entry.Group.Metadata[entry.Index].Y = 1; changed.Add(entry.Group); _gpuTreeProtectedIds.Add(id); _gpuTreeNewlyProtectedScratch.Add((entry.Group, entry.Index)); }
            else _gpuTreeSourcesDirty = true;
        }
        if (changed.Count > 0)
        {
            // One pass over the outputs; each group's list keeps _gpuTreeOutputs order.
            _gpuTreeProtectedOutputsByGroupScratch.Clear();
            foreach (GpuTreeOutput output in _gpuTreeOutputs.Values)
            {
                if (output.SourceGroup is not GpuTreeGroup source || !changed.Contains(source)) continue;
                if (!_gpuTreeProtectedOutputsByGroupScratch.TryGetValue(source, out List<GpuTreeOutput>? list))
                    _gpuTreeProtectedOutputsByGroupScratch[source] = list = new List<GpuTreeOutput>();
                list.Add(output);
            }
        }
        foreach (GpuTreeGroup group in changed)
        {
            group.HasProtected = Array.Exists(group.Metadata, item => item.Y > .5f);
            if (_gpuTreeProtectedOutputsByGroupScratch.TryGetValue(group, out List<GpuTreeOutput>? groupOutputs))
                foreach (GpuTreeOutput output in groupOutputs) _gpuTreeCompute?.UpdateMetadata(output.BatchId, group.Metadata);
        }
        if (changed.Count > 0)
        {
            _gpuTreeProtectionRevision++;
            UpdateMergedGpuTreeMetadata(changed);
            if (GpuTreeProtectionAffectsSelection()) _gpuTreeProtectionLayoutRevision++;
            InvalidateGpuTreeFrameCache();
        }
        _gpuTreeProtectedOutputsByGroupScratch.Clear();
    }

    // True when the protection change just applied could change which outputs the
    // selection wants. The GPU already sees the new metadata through UpdateMetadata;
    // this only gates the CPU selection rebuild. An addition can only add wanted
    // outputs (or tighten budget pruning, which keeps a superset), so a stale
    // selection that already holds every output a newly protected root could need
    // stays valid. A removal can relax budget pruning, so it is only safe when
    // pruning is off. Install order (protected first) may stay stale.
    private bool GpuTreeProtectionAffectsSelection()
    {
        if (!EffectiveCompactTreeLodProfile || !_gpuTreeSelectionValid || _gpuTreeSelectionTruncated
            || _gpuTreeSelectionEdges is null || _gpuTreeSelectionFocus is not RingCanonicalPosition focus) return true;
        if (_gpuTreeProtectionRemovedScratch && PruneEmptyGpuTreeBatches) return true;
        foreach ((GpuTreeGroup source, int index) in _gpuTreeNewlyProtectedScratch)
            for (int lod = 0; lod < 5; lod++)
            {
                if (!IsGpuTreeLodEnabled(lod)) continue;
                GpuTreeGroup owner = source; int slot = index;
                // Mirrors GroupsForGpuLod: merged outputs are keyed by the aggregate.
                if (((lod == 2 && MergeMidGpuTreeBatches) || (lod is 3 or 4 && MergeFarGpuTreeBatches)) && _gpuTreeMergedGroups.ContainsKey(lod))
                {
                    if (!_gpuTreeProtectedOwnerScratch.TryGetValue((source, lod), out var aggregate)) return true;
                    owner = aggregate.Aggregate; slot = aggregate.Offset + index;
                }
                if (_gpuTreeWanted.Contains(new GpuOutputKey(owner.Key, lod))) continue;
                Vector2 camera = CanonicalCamera(owner, focus, _gpuTreeSelectionCircumference);
                Vector4 meta = owner.Metadata[slot];
                float distance = (new Vector2(meta.Z, meta.W) - camera).Length();
                GpuTreeLodInterval(lod, _gpuTreeSelectionEdges, out float near, out float far, out _, out _, out _, out _);
                if (distance + _gpuTreeSelectionGuard >= near && distance - _gpuTreeSelectionGuard < far) return true;
            }
        return false;
    }

    private void UpdateMergedGpuTreeMetadata(HashSet<GpuTreeGroup> changed)
    {
        foreach ((int lod, Dictionary<GpuGroupKey, GpuTreeGroup> groups) in _gpuTreeMergedGroups)
        foreach (GpuTreeGroup aggregate in groups.Values)
        {
            if (aggregate.Members is null || aggregate.MemberOffsets is null) continue;
            bool touched = false;
            for (int member = 0; member < aggregate.Members.Length; member++)
            {
                GpuTreeGroup source = aggregate.Members[member];
                if (!changed.Contains(source)) continue;
                int offset = aggregate.MemberOffsets[member];
                for (int i = 0; i < source.Metadata.Length; i++) aggregate.Metadata[offset + i].Y = source.Metadata[i].Y;
                _gpuTreeProtectedOwnerScratch[(source, lod)] = (aggregate, offset);
                touched = true;
            }
            if (!touched) continue;
            aggregate.HasProtected = Array.Exists(aggregate.Metadata, value => value.Y > .5f);
            foreach (GpuTreeOutput output in _gpuTreeOutputs.Values)
                if (ReferenceEquals(output.SourceGroup, aggregate)) _gpuTreeCompute?.UpdateMetadata(output.BatchId, aggregate.Metadata);
        }
    }

    // Cap fall-through (TR-9a). In the compact profile edges[1..4] are the hand-off ends of slots 0..3 (edges[2] is the LOD2 to LOD2b end, equal to the near
    // fade end when the render LODs are off, where slot 1 is unused). GpuTreeConfiguredEdges holds the configured values; GpuTreeLodEdges substitutes the
    // stored effective ends (moved inward until each slot's cap holds its band) so every consumer, the selection signature included, sees them.
    private float[]? _gpuTreeEffectiveBase, _gpuTreeEffectiveEnds;
    /// <summary>Test switch: the configured hand-off ends are used as before TR-9a. No menu row.</summary>
    internal bool DisableGpuTreeEffectiveEdges { get; set; }

    // The per-frame edge, cap and gate arrays are interned: the previously returned array comes back while the values are unchanged and a new array
    // otherwise. Selection and frame submission keep these references and compare them by value, so they must never be mutated in place.
    private float[]? _gpuTreeConfiguredEdgesInterned, _gpuTreeLodEdgesInterned, _gpuTreeGatesInterned;
    private uint[]? _gpuTreeCapsInterned;
    private static T[] InternArray<T>(ref T[]? slot, ReadOnlySpan<T> values) where T : unmanaged, IEquatable<T>
    {
        if (slot is null || !slot.AsSpan().SequenceEqual(values)) slot = values.ToArray();
        return slot;
    }
    /// <summary>Test switch: the tree streaming deadline is armed only inside the render update and the source lookup copy is unchunked, as before PERF-3 T1. No menu row.</summary>
    public bool LegacyTreeStreamingBudget { get; set; }

    private float[] GpuTreeLodEdges()
    {
        Span<float> edges = stackalloc float[6];
        FillGpuTreeConfiguredEdges(edges);
        if (EffectiveCompactTreeLodProfile && !DisableGpuTreeEffectiveEdges && _gpuTreeEffectiveEnds is { } ends
            && _gpuTreeEffectiveBase is { } baseEdges && baseEdges.AsSpan().SequenceEqual(edges))
            for (int i = 0; i < 4; i++) edges[1 + i] = ends[i];
        return InternArray(ref _gpuTreeLodEdgesInterned, (ReadOnlySpan<float>)edges);
    }

    /// <summary>Test hook: the stored effective hand-off ends of slots 0..3, or null when none are in force.</summary>
    internal float[]? GpuTreeEffectiveEndsForTests()
    {
        float[] edges = GpuTreeLodEdges();
        return EffectiveCompactTreeLodProfile && !DisableGpuTreeEffectiveEdges && _gpuTreeEffectiveEnds is not null ? new[] { edges[1], edges[2], edges[3], edges[4] } : null;
    }

    private float[] GpuTreeConfiguredEdges()
    {
        Span<float> edges = stackalloc float[6];
        FillGpuTreeConfiguredEdges(edges);
        return InternArray(ref _gpuTreeConfiguredEdgesInterned, (ReadOnlySpan<float>)edges);
    }

    private void FillGpuTreeConfiguredEdges(Span<float> edges)
    {
        if (EffectiveCompactTreeLodProfile)
        {
            CompactTreeFadeBounds(out _, out float nearEnd, out _, out float billboardEnd);
            float far = Math.Max(Math.Max(VisibilityRadiusMeters, MaximumLod5DistanceMeters), billboardEnd);
            float compactFourth = EffectiveFarTreeQuads
                ? Math.Min(CompactTreeQuadCardFarEdge(), far)
                : Mathf.Clamp(MinimumLod5DistanceMeters, billboardEnd, Math.Max(billboardEnd, MaximumLod5DistanceMeters));
            float midEnd = nearEnd;
            if (EffectiveTreeRenderLods) CompactTreeMidFadeBounds(out _, out midEnd);
            edges[0] = 0f; edges[1] = nearEnd; edges[2] = midEnd; edges[3] = billboardEnd; edges[4] = compactFourth; edges[5] = far;
            return;
        }
        float collision = EnableImportedForestCollisions ? Math.Max(1, ImportedForestCollisionRadiusMeters) + 4 : 1;
        float first = Math.Max(collision, Lod1DistanceMeters), second = Math.Max(first, Lod2DistanceMeters), third = Math.Max(second, Lod3DistanceMeters);
        float fourth = Mathf.Clamp(MinimumLod5DistanceMeters, third, Math.Max(third, MaximumLod5DistanceMeters));
        edges[0] = 0f; edges[1] = first; edges[2] = second; edges[3] = third; edges[4] = fourth;
        edges[5] = Math.Max(Math.Max(VisibilityRadiusMeters, MaximumLod5DistanceMeters), collision);
    }

    private bool GpuTreeSelectionCanReuse(Vector3 camera, RingCanonicalPosition? focus, double circumference,
        float[] edges, uint[] caps, float[] gates, float guard)
    {
        if (!_gpuTreeSelectionValid || !SkipUnchangedGpuCulling || _gpuTreeSelectionEpoch != GpuTreeSourceEpoch
            || _gpuTreeSelectionProtectionRevision != _gpuTreeProtectionLayoutRevision
            || _gpuTreeSelectionCircumference != circumference || _gpuTreeSelectionLayerTransform != GlobalTransform
            || _gpuTreeSelectionGuard != guard || _gpuTreeSelectionMaximumBatches != MaximumGpuTreeBatches
            || _gpuTreeSelectionMaximumNodes != MaximumVegetationRendererNodes || _gpuTreeSelectionMergeFar != MergeFarGpuTreeBatches
            || _gpuTreeSelectionSharedFar != EffectiveSharedFarTreeImpostors
            || _gpuTreeSelectionCompactProfile != EffectiveCompactTreeLodProfile || _gpuTreeSelectionMergeMid != MergeMidGpuTreeBatches
            || _gpuTreeSelectionMidSpan != EffectiveMidTreeBatchTileSpan || _gpuTreeSelectionPrune != PruneEmptyGpuTreeBatches
            || _gpuTreeSelectionPolicySignature != GpuTreeRenderPolicySignature()
            || _gpuTreeSelectionEdges is null || !_gpuTreeSelectionEdges.AsSpan().SequenceEqual(edges)
            || _gpuTreeSelectionCaps is null || !_gpuTreeSelectionCaps.AsSpan().SequenceEqual(caps)
            || _gpuTreeSelectionGates is null || !_gpuTreeSelectionGates.AsSpan().SequenceEqual(gates)) return false;
        if (focus is not RingCanonicalPosition current || _gpuTreeSelectionFocus is not RingCanonicalPosition previous)
            return focus is null && _gpuTreeSelectionFocus is null && _gpuTreeSelectionCamera == camera;
        double around = WrappedVegetationDelta(current.AroundMeters - previous.AroundMeters, circumference);
        double across = current.AcrossMeters - previous.AcrossMeters;
        return around * around + across * across <= guard * guard * .0625;
    }

    private bool ServiceGpuTreeOutputs(Vector3 camera, RingCanonicalPosition? focus, double circumference)
    {
        if (_gpuTreeCompute == null || !_gpuTreeCompute.Ready) return false;
        _gpuTreeCompute.RequestHaze(RingVegetationHaze.CreateFrame(_treeHazeRayFrame, _grid != null ? _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau : 0));
        _gpuTreeCompute.RequestShadow(global::RingworldTechDemo.Shadows.RingVegetationShadow.CreateFrame(camera));
        UpdateTreeCameraCulling();
        if (_gpuLastPolicySignature != GpuTreeRenderPolicySignature()) MarkTreeSunShadowPolicyDirty();
        // A parent/layer transform can change without a streaming invalidation
        // (including a floating-origin move in a fixture).
        if (_gpuLastFrameLayerTransform != GlobalTransform) _gpuTreeFrameCacheVersion++;
        // A live tuning change must take effect even when the settled-frame
        // fast path would otherwise return before doing any output work.
        if (!KeepTreeLodFallbacksUntilReady && _gpuTreeFallbackOutputs.Count > 0)
            ClearGpuTreeFallbackOutputs();
        else if (_gpuFrameCacheComplete && _gpuTreeFallbackOutputs.Count > 0)
            ClearGpuTreeFallbackOutputs(budgeted: true);
        float[] edges = GpuTreeLodEdges();
        uint[] caps = GpuTreeLodCaps();
        Span<float> gateValues = stackalloc float[5] { VegetationLodRankGate(1), VegetationLodRankGate(2), EffectiveCompactTreeLodProfile ? 1f : VegetationLodRankGate(3), VegetationLodRankGate(4), VegetationLodRankGate(5) };
        float[] gates = InternArray(ref _gpuTreeGatesInterned, (ReadOnlySpan<float>)gateValues);
        float guard = Math.Max(32, CellSizeMeters * 2);
        _gpuTreeCompute.OptimizeTreeDispatches = OptimizeGpuTreeDispatches;
        if (CanDeferGpuTreeFrame(camera, focus, circumference, edges, caps, gates, guard)) return true;
        // A static camera with unchanged resident inputs must not rebuild the
        // candidate order or submit another identical compute frame. Keep the
        // gradual-install path alive until every wanted output exists.
        if (SkipUnchangedGpuCulling && _gpuFrameCacheComplete && _gpuLastCamera == camera
            && _gpuLastFocus == focus && _gpuLastCircumference == circumference
            && _gpuLastEdges is not null && _gpuLastEdges.AsSpan().SequenceEqual(edges)
            && _gpuLastCaps is not null && _gpuLastCaps.AsSpan().SequenceEqual(caps)
            && _gpuLastRankGates is not null && _gpuLastRankGates.AsSpan().SequenceEqual(gates)
            && _gpuLastGuard == guard && _gpuLastFrameLayerTransform == GlobalTransform && _gpuLastPolicySignature == GpuTreeRenderPolicySignature()) return true;
        bool rebuildSelection = !GpuTreeSelectionCanReuse(camera, focus, circumference, edges, caps, gates, guard);
        if (rebuildSelection)
        {
        // Group bounds are cheap to test and retain offscreen shadow casters.
        // A one-cell guard keeps LOD outputs resident across small camera motion.
        using var selectionScope = RingPerformanceTelemetry.MeasureCpu("Trees output selection");
        _gpuTreeOrder.Clear(); _gpuTreeWanted.Clear();
        _gpuTreeOutputGroups.Clear();
        var pruneCounts = _gpuTreePruneCountsScratch; pruneCounts.Clear();
        bool compactProfile = EffectiveCompactTreeLodProfile;
        float reuseRadius = guard * .25f + .001f;
        _gpuTreeGroupDistanceScratch.Clear();
        if ((!SerialTreeSelection || VerifyTreeSelection) && focus is RingCanonicalPosition boundsFocus)
            using (RingPerformanceTelemetry.MeasureCpu("Trees selection bounds")) PrepareGpuTreeBoundsSets(boundsFocus, circumference);
        // TR-9a: derive the hand-off ends from the caps before any band is used. Pruning and a canonical focus are required; otherwise the configured edges apply.
        using (RingPerformanceTelemetry.MeasureCpu("Trees selection effective edges"))
        {
        if (compactProfile && PruneEmptyGpuTreeBatches && !DisableGpuTreeEffectiveEdges && focus is RingCanonicalPosition effectiveFocus)
            edges = UpdateGpuTreeEffectiveEdges(camera, effectiveFocus, circumference, caps, gates, guard);
        else if (_gpuTreeEffectiveEnds is not null) { _gpuTreeEffectiveEnds = null; _gpuTreeEffectiveBase = null; edges = GpuTreeLodEdges(); }
        }
        using (RingPerformanceTelemetry.MeasureCpu("Trees selection prune counts"))
        {
        if (VerifyTreeSelection)
        {
            // Serial result is committed; the parallel one is computed alongside and compared.
            BuildGpuTreeSelectionCandidates(false, false, edges, gates, guard, reuseRadius, camera, focus, circumference, compactProfile, _gpuTreeOrder, _gpuTreeOutputGroups, pruneCounts);
            VerifyGpuTreeSelectionCandidates(edges, gates, guard, reuseRadius, camera, focus, circumference, compactProfile, _gpuTreeOrder, _gpuTreeOutputGroups, pruneCounts);
        }
        else BuildGpuTreeSelectionCandidates(!SerialTreeSelection, false, edges, gates, guard, reuseRadius, camera, focus, circumference, compactProfile, _gpuTreeOrder, _gpuTreeOutputGroups, pruneCounts);
        }
        ReleaseGpuTreeBoundsSets();
        using (RingPerformanceTelemetry.MeasureCpu("Trees selection order"))
        {
        _gpuTreeOrder.Sort(GpuTreeOrderComparison);
        if (PruneEmptyGpuTreeBatches && focus is RingCanonicalPosition pruningFocus)
            PruneGpuTreeBudgetGuaranteedPrefix(caps, pruneCounts);
        int protectedGroups = 0;
        foreach (GpuTreeGroup outputGroup in _gpuTreeOutputGroups.Values) if (outputGroup.HasProtected) protectedGroups++;
        int budget = Math.Max(protectedGroups, Math.Min(Mathf.Clamp(MaximumGpuTreeBatches, 16, 2048), Math.Max(128, MaximumVegetationRendererNodes)));
        _gpuTreeSelectionTruncated = _gpuTreeOrder.Count > budget;
        if (_gpuTreeSelectionTruncated) _gpuTreeOrder.RemoveRange(budget, _gpuTreeOrder.Count - budget);
		// Collision-protected groups lead the installation order, but shadow priority
		// is strictly nearest-first. Hash that nearest order so crossing the protected
		// partition still refreshes the bounded caster set.
		_gpuTreeOrderScratch.Clear(); _gpuTreeOrderScratch.AddRange(_gpuTreeOrder);
		_gpuTreeOrderScratch.Sort(GpuTreeNearestComparison);
		long shadowOrderSignature = 17L;
		foreach (var item in _gpuTreeOrderScratch)
			shadowOrderSignature = unchecked(shadowOrderSignature * 31 + item.Key.Group.Around * 17
				+ item.Key.Group.Across * 13 + item.Key.Group.Variant * 7 + item.Key.Lod);
		UpdateGpuTreeSunShadowOrderSignature(shadowOrderSignature);
        foreach (var item in _gpuTreeOrder) _gpuTreeWanted.Add(item.Key);
        }
        _gpuTreeSelectionValid = true; _gpuTreeSelectionEpoch = GpuTreeSourceEpoch; _gpuTreeSelectionProtectionRevision = _gpuTreeProtectionLayoutRevision;
        _gpuTreeSelectionFocus = focus; _gpuTreeSelectionCamera = camera; _gpuTreeSelectionCircumference = circumference; _gpuTreeSelectionLayerTransform = GlobalTransform;
        _gpuTreeSelectionEdges = edges; _gpuTreeSelectionCaps = caps; _gpuTreeSelectionGates = gates; _gpuTreeSelectionGuard = guard;
        _gpuTreeSelectionMaximumBatches = MaximumGpuTreeBatches; _gpuTreeSelectionMaximumNodes = MaximumVegetationRendererNodes; _gpuTreeSelectionMergeFar = MergeFarGpuTreeBatches; _gpuTreeSelectionSharedFar = EffectiveSharedFarTreeImpostors;
        _gpuTreeSelectionCompactProfile = EffectiveCompactTreeLodProfile; _gpuTreeSelectionMergeMid = MergeMidGpuTreeBatches; _gpuTreeSelectionMidSpan = EffectiveMidTreeBatchTileSpan; _gpuTreeSelectionPrune = PruneEmptyGpuTreeBatches; _gpuTreeSelectionPolicySignature = GpuTreeRenderPolicySignature();
        }
        HashSet<GpuOutputKey> wanted = _gpuTreeWanted;
        using (RingPerformanceTelemetry.MeasureCpu("Trees output retire"))
        {
            int retired = 0;
            _gpuTreeKeyScratch.Clear(); _gpuTreeKeyScratch.AddRange(_gpuTreeOutputs.Keys);
            foreach (GpuOutputKey key in _gpuTreeKeyScratch)
            {
                if (wanted.Contains(key)) continue;
                if (retired > 0 && (retired >= Math.Max(1, MaximumVegetationUploadsPerFrame)
                    || (_gpuTreeFrameDeadline != 0 && Time.GetTicksUsec() >= _gpuTreeFrameDeadline))) break;
                RetireGpuOutput(key); retired++;
            }
        }
        int uploaded = 0;
        ulong start = Time.GetTicksUsec();
        using (RingPerformanceTelemetry.MeasureCpu("Trees output upload"))
        {
        foreach (var item in _gpuTreeOrder)
        {
            bool needsReplacement = !_gpuTreeOutputs.TryGetValue(item.Key, out GpuTreeOutput? current)
                || !ReferenceEquals(current.SourceGroup, _gpuTreeOutputGroups[item.Key]) || current.TreeDitherData != UsesTreeDitherData(item.Key.Lod) || current.FarQuad != UsesFarQuadMesh(item.Key.Lod) || current.RenderLodMesh != UsesRenderLodMesh(item.Key.Lod);
            if (!needsReplacement) continue;
            // Guarantee progress even if mandatory selection consumed this
            // frame's soft budget. One Godot allocation cannot be interrupted.
            if (uploaded > 0 && (uploaded >= Math.Max(1, MaximumVegetationUploadsPerFrame)
                || (Time.GetTicksUsec() - start) / 1000.0 >= Math.Max(.1, VegetationUploadBudgetMilliseconds)
                || (_gpuTreeFrameDeadline != 0 && Time.GetTicksUsec() >= _gpuTreeFrameDeadline))) break;
            if (_gpuTreeOutputs.TryGetValue(item.Key, out GpuTreeOutput? existing)
                && _gpuTreeOutputGroups.TryGetValue(item.Key, out GpuTreeGroup? expected)
                && (!ReferenceEquals(existing.SourceGroup, expected) || existing.TreeDitherData != UsesTreeDitherData(item.Key.Lod) || existing.FarQuad != UsesFarQuadMesh(item.Key.Lod) || existing.RenderLodMesh != UsesRenderLodMesh(item.Key.Lod))) RetireGpuOutput(item.Key);
            if (_gpuTreeOutputs.ContainsKey(item.Key)) continue;
            if (CreateGpuTreeOutput(item.Key)) uploaded++;
        }
        }
		if (_treeSunShadowPolicyDirty) ApplyTreeSunShadowState();
		using var frameScope = RingPerformanceTelemetry.MeasureCpu("Trees frame submission");
        _gpuTreeFrameScratch.Clear();
        int matchingFallbacksRetired = 0;
        foreach (var item in _gpuTreeOrder)
        {
            if (!_gpuTreeOutputs.TryGetValue(item.Key, out GpuTreeOutput? output)) continue;
            // A staged source swap leaves old render resources alive until their
            // replacement is installed; frame parameters must match that source.
            GpuTreeGroup group = output.SourceGroup;
            int lod = item.Key.Lod;
            if (output.TransformCacheVersion != _gpuTreeFrameCacheVersion)
            {
                output.CachedGlobalTransform = output.Renderer.GlobalTransform;
                output.TransformCacheVersion = _gpuTreeFrameCacheVersion;
            }
            GpuTreeLodInterval(lod, edges, out float lodNear, out float lodFar, out float fadeInStart, out float fadeInEnd, out float fadeOutStart, out float fadeOutEnd);
            GpuTreeSteepRule(lod, edges, gates, out int steepRole, out float steepSinLow, out float steepSinHigh, out float steepFadeEnd, out float steepRankGate);
            _gpuTreeFrameScratch.Add(new RingGpuVegetationCompute.BatchFrameParameters(output.BatchId, output.CachedGlobalTransform, false,
                lod, lodNear, lodFar, 0, 1, 1, false, gates[lod], false, null, 0,
                focus is RingCanonicalPosition canonical ? CanonicalCamera(group, canonical, circumference) : null, 0,
                EffectiveCompactTreeLodProfile, fadeInStart, fadeInEnd, fadeOutStart, fadeOutEnd, null,
                steepRole, steepSinLow, steepSinHigh, steepFadeEnd, steepRankGate));
            if (!output.Ready) output.Ready = _gpuTreeCompute.IsBatchReady(output.BatchId);
            if (!output.HasCachedVisibility || output.CachedVisibility != output.Ready)
            {
                output.Renderer.Visible = output.Ready;
                output.CachedVisibility = output.Ready;
                output.HasCachedVisibility = true;
            }
            // A ready replacement for the same logical partition need not wait
            // for unrelated tiles. Cross-LOD/merged-to-unmerged transitions still
            // retain coverage until the complete desired set is ready.
            if (output.Ready && output.CachedVisibility && ReferenceEquals(group, _gpuTreeOutputGroups[item.Key])
                && _gpuTreeFallbackOutputs.TryGetValue(item.Key, out GpuTreeOutput? fallback)
                && (fallback.SourceGroup.Members == null) == (group.Members == null)
                && (matchingFallbacksRetired == 0 || (matchingFallbacksRetired < Math.Max(1, MaximumVegetationUploadsPerFrame)
                    && (_gpuTreeFrameDeadline == 0 || Time.GetTicksUsec() < _gpuTreeFrameDeadline))))
            {
                using var retireScope = RingPerformanceTelemetry.MeasureCpu("Trees output retire");
                DisposeGpuTreeOutput(fallback);
                _gpuTreeFallbackOutputs.Remove(item.Key); _gpuTreeFallbackOrder.Remove(item.Key);
                matchingFallbacksRetired++;
            }
        }
        bool unchanged = SkipUnchangedGpuCulling && _gpuLastCamera == camera
            && _gpuLastCaps is not null && _gpuLastCaps.AsSpan().SequenceEqual(caps)
            && GpuTreeFramesMatch(_gpuTreeFrameScratch, _gpuLastFramesValid ? _gpuTreeLastFrameList : null);
        if (!unchanged)
        {
            // RequestFrame snapshots the batch list before returning, so the scratch
            // list can be handed over directly and then recycled as the "last" list.
            bool requested;
            using (RingPerformanceTelemetry.MeasureCpu("Trees frame request"))
                requested = _gpuTreeCompute.RequestFrame(new RingGpuVegetationCompute.FrameRequest(camera, caps, _gpuTreeFrameScratch, SkipUnchangedGpuCulling));
            if (requested)
            {
                RecordGpuTreeFrameSubmission(camera, focus, circumference);
                _gpuLastCamera = camera; _gpuLastCaps = caps;
                (_gpuTreeFrameScratch, _gpuTreeLastFrameList) = (_gpuTreeLastFrameList, _gpuTreeFrameScratch);
                _gpuLastFramesValid = true;
            }
        }
        _gpuLastFocus = focus; _gpuLastCircumference = circumference;
        _gpuLastEdges = edges; _gpuLastRankGates = gates;
        _gpuLastGuard = guard; _gpuLastFrameLayerTransform = GlobalTransform; _gpuLastPolicySignature = GpuTreeRenderPolicySignature();
        bool allWantedReady = _gpuTreeOutputs.Count == wanted.Count;
        if (allWantedReady)
            foreach (GpuOutputKey key in wanted)
                if (!_gpuTreeOutputs.TryGetValue(key, out GpuTreeOutput? wantedOutput)
                    || !_gpuTreeOutputGroups.TryGetValue(key, out GpuTreeGroup? wantedSource)
                    || !ReferenceEquals(wantedOutput.SourceGroup, wantedSource)
                    || wantedOutput.TreeDitherData != UsesTreeDitherData(key.Lod) || wantedOutput.FarQuad != UsesFarQuadMesh(key.Lod) || wantedOutput.RenderLodMesh != UsesRenderLodMesh(key.Lod)
                    || !wantedOutput.Ready || !wantedOutput.CachedVisibility) { allWantedReady = false; break; }
        _gpuFrameCacheComplete = allWantedReady;
        if (_gpuFrameCacheComplete) ClearGpuTreeFallbackOutputs(budgeted: true);
        // Detach the CPU presentation once; leaving it visible would double trees.
        if (!_gpuTreePresentation) { HideCpuTreePools(); _gpuTreePresentation = true; }
        _pendingVegetationRenderPlan = null; _vegetationRenderDirty = false; _vegetationRenderMovementDirty = false;
        LastInstalledTransformCount = GpuTreeResidentRootCount; // Capacity, not a GPU visibility readback.
        VegetationRendererNodeCount = CountRendererPoolNodes() + GpuTreeBatchCount + GpuTreeFallbackBatchCount;
        int visibleOutputs = 0;
        foreach (GpuTreeOutput visibleOutput in _gpuTreeOutputs.Values) if (visibleOutput.CachedVisibility) visibleOutputs++;
        foreach (GpuTreeOutput visibleFallback in _gpuTreeFallbackOutputs.Values) if (visibleFallback.CachedVisibility) visibleOutputs++;
        VegetationActiveRendererNodes = visibleOutputs;
        VegetationRendererEstimatedBytes = GpuTreeResidentBytes;
        LastVegetationUploadMilliseconds = (Time.GetTicksUsec() - start) / 1000.0;
        // EnsureGpuTreeBackend overwrites the status every frame, so always assign;
        // only the formatted text is cached.
        if (_gpuTreeStatusInstalled != _gpuTreeOutputs.Count || _gpuTreeStatusWanted != wanted.Count || _gpuTreeStatusFallback != GpuTreeFallbackBatchCount)
        {
            _gpuTreeStatusInstalled = _gpuTreeOutputs.Count; _gpuTreeStatusWanted = wanted.Count; _gpuTreeStatusFallback = GpuTreeFallbackBatchCount;
            _gpuTreeStatusText = $"GPU compute + indirect draws ({_gpuTreeStatusInstalled}/{_gpuTreeStatusWanted} batches installed, {_gpuTreeStatusFallback} ready fallback)";
        }
        GpuTreeCullingStatus = _gpuTreeStatusText;
        return true;
    }

    // Nearest and farthest distance of a group to the camera, cached per selection rebuild.
    // The canonical-focus bounds of a group: a pure function of the group, the focus and the circumference (the parallel pass computes these off the main thread).
    private static (float Near, float Far) GpuTreeCanonicalBounds(GpuTreeGroup group, RingCanonicalPosition canonical, double circumference)
    {
        Vector2 local = CanonicalCamera(group, canonical, circumference);
        float near = local.DistanceTo(local.Clamp(group.CanonicalMin, group.CanonicalMax));
        Vector2 delta = (local - group.CanonicalMin).Abs().Max((local - group.CanonicalMax).Abs());
        return (near, delta.Length());
    }

    private (float Near, float Far) GpuTreeGroupDistanceBounds(GpuTreeGroup group, Vector3 camera, RingCanonicalPosition? focus, double circumference)
    {
        if (_gpuTreeGroupDistanceScratch.TryGetValue(group, out (float Near, float Far) cached)) return cached;
        float near, far;
        if (focus is RingCanonicalPosition canonical)
            (near, far) = GpuTreeCanonicalBounds(group, canonical, circumference);
        else
        {
            Aabb bounds = GlobalTransform * group.Bounds;
            near = camera.DistanceTo(camera.Clamp(bounds.Position, bounds.End));
            far = camera.DistanceTo(bounds.GetCenter()) + bounds.Size.Length() * .5f;
        }
        _gpuTreeGroupDistanceScratch[group] = (near, far);
        return (near, far);
    }

    private const int GpuTreeEffectiveBins = 64;
    private readonly int[][] _gpuTreeEffectiveHistograms = { new int[GpuTreeEffectiveBins], new int[GpuTreeEffectiveBins], new int[GpuTreeEffectiveBins], new int[GpuTreeEffectiveBins] };
    private const float GpuTreeEffectiveCapShare = .95f;

    /// <summary>
    /// Effective hand-off end of one slot (TR-9a). <paramref name="histogram"/> counts the slot's ordinary candidates in GpuTreeEffectiveBins equal bins over
    /// [lo, hi); the slot owns the roots from <paramref name="start"/> (the previous hand-off's fade-in start) up to its end. The end is the upper edge of the
    /// last bin whose running count is at most 95 % of <paramref name="available"/>; hi (the configured end) when the whole band fits; the minimum when not
    /// even the first bin fits. It moves away from <paramref name="stored"/> only when the new value differs by more than one bin.
    /// </summary>
    internal static float ResolveGpuTreeEffectiveEnd(int[] histogram, float lo, float hi, float start, float available, float stored, float minimum)
    {
        float binWidth = (hi - lo) / histogram.Length;
        if (!(binWidth > 0)) return hi;
        int first = Math.Clamp((int)MathF.Floor((start - lo) / binWidth), 0, histogram.Length - 1);
        float threshold = available * GpuTreeEffectiveCapShare;
        long total = 0;
        for (int b = first; b < histogram.Length; b++) total += histogram[b];
        float raw;
        if (total <= threshold) raw = hi;
        else
        {
            long running = 0; int last = -1;
            for (int b = first; b < histogram.Length; b++) { running += histogram[b]; if (running <= threshold) last = b; else break; }
            raw = last >= 0 ? lo + (last + 1) * binWidth : minimum;
        }
        raw = Math.Clamp(raw, Math.Min(minimum, hi), hi);
        if (float.IsNaN(stored) || MathF.Abs(raw - stored) <= binWidth) raw = float.IsNaN(stored) ? raw : Math.Clamp(stored, Math.Min(minimum, hi), hi);
        return raw;
    }

    /// <summary>
    /// Cap fall-through (TR-9a, design B2). One pass over the roots of the groups that can reach slots 0..3 fills a 64-bin distance histogram per slot (ordinary
    /// roots that pass the slot's gate); protected roots are counted off the slot's cap (they keep first claim) and slot 0's end never goes inside the
    /// farthest protected root. The ends are then resolved nearest slot first, each slot starting where the previous one now ends, so a tree that a cap
    /// pushes out of one slot lands in the next. The ends are stored with a one-bin hysteresis and returned as the new edge array. Called only from a
    /// selection rebuild, so nothing changes while the camera stands still.
    /// </summary>
    private float[] UpdateGpuTreeEffectiveEdges(Vector3 camera, RingCanonicalPosition focus, double circumference, uint[] caps, float[] gates, float guard)
    {
        float[] configured = GpuTreeConfiguredEdges();
        bool renderLods = EffectiveTreeRenderLods;
        CompactTreeFadeBounds(out float nearStart, out float nearEnd, out float billboardStart, out float billboardEnd);
        int count = renderLods ? 4 : 3;
        Span<int> slotOf = stackalloc int[4];
        Span<float> widthOf = stackalloc float[4];
        if (renderLods) { slotOf[0] = 0; slotOf[1] = 1; slotOf[2] = 2; slotOf[3] = 3; }
        else { slotOf[0] = 0; slotOf[1] = 2; slotOf[2] = 3; }
        for (int j = 0; j < count; j++)
        {
            float width = slotOf[j] switch
            {
                0 => nearEnd - nearStart,
                1 => 0,
                2 => billboardEnd - billboardStart,
                _ => EffectiveFarTreeQuads ? TreeQuadCrossFadeMeters : 0f
            };
            if (slotOf[j] == 1) { CompactTreeMidFadeBounds(out float midStart, out float midEnd); width = midEnd - midStart; }
            widthOf[j] = MathF.Max(0, width);
        }
        Span<float> lo = stackalloc float[4], hi = stackalloc float[4], minimum = stackalloc float[4];
        float previousMinimum = 0, previousWidth = 0;
        for (int j = 0; j < count; j++)
        {
            int edge = 1 + slotOf[j];
            hi[j] = configured[edge];
            lo[j] = j == 0 ? 0 : previousMinimum - previousWidth;
            minimum[j] = Math.Min(previousMinimum + widthOf[j], hi[j]);
            previousMinimum = minimum[j]; previousWidth = widthOf[j];
        }
        var histograms = _gpuTreeEffectiveHistograms;
        Span<int> protectedCount = stackalloc int[4];
        float protectedFarthest = 0;
        for (int j = 0; j < count; j++)
        {
            Array.Clear(histograms[j]);
            int slot = slotOf[j];
            if (!(hi[j] > lo[j])) continue;
            CountGpuTreeEffectiveSlot(!SerialTreeSelection && !VerifyTreeSelection, false, slot, lo[j], hi[j], gates[slot], guard, camera, focus, circumference,
                histograms[j], out int slotProtected, out float slotFarthest);
            if (VerifyTreeSelection)
            {
                Array.Clear(_gpuTreeEffectiveVerifyHistogram);
                CountGpuTreeEffectiveSlot(true, true, slot, lo[j], hi[j], gates[slot], guard, camera, focus, circumference,
                    _gpuTreeEffectiveVerifyHistogram, out int verifyProtected, out float verifyFarthest);
                if (!histograms[j].AsSpan().SequenceEqual(_gpuTreeEffectiveVerifyHistogram) || verifyProtected != slotProtected
                    || BitConverter.SingleToInt32Bits(verifyFarthest) != BitConverter.SingleToInt32Bits(slotFarthest)) CountGpuTreeSelectionMismatch($"effective histogram slot {slot}");
            }
            protectedCount[j] = slotProtected;
            if (slotFarthest > protectedFarthest) protectedFarthest = slotFarthest;
        }
        bool sameBase = _gpuTreeEffectiveBase is { } previousBase && previousBase.AsSpan().SequenceEqual(configured) && _gpuTreeEffectiveEnds is not null;
        var ends = new float[4] { configured[1], configured[2], configured[3], configured[4] };
        float previousEnd = 0; previousWidth = 0;
        for (int j = 0; j < count; j++)
        {
            int slot = slotOf[j], index = slot;
            float floor = minimum[j];
            if (j > 0) floor = Math.Min(hi[j], Math.Max(floor, previousEnd + widthOf[j]));
            if (slot == 0) floor = Math.Min(hi[j], Math.Max(floor, protectedFarthest + (hi[j] - lo[j]) / GpuTreeEffectiveBins));
            float start = j == 0 ? 0 : previousEnd - previousWidth;
            float available = MathF.Max(0, caps[slot] - protectedCount[j]);
            float stored = sameBase ? _gpuTreeEffectiveEnds![index] : float.NaN;
            float end = ResolveGpuTreeEffectiveEnd(histograms[j], lo[j], hi[j], start, available, stored, floor);
            // The hysteresis may keep a stored value that the moved previous end no longer allows.
            end = Math.Clamp(end, floor, hi[j]);
            ends[index] = end;
            previousEnd = end; previousWidth = widthOf[j];
        }
        _gpuTreeEffectiveBase = configured; _gpuTreeEffectiveEnds = ends;
        return GpuTreeLodEdges();
    }

    // Selection is reused while the camera remains inside the guard region. A
    // group is removed only when no root can enter its interval anywhere in that
    // region. Guaranteed counts preserve the compute shader's protected-first
    // budget order without a GPU readback; uncertain candidates stay resident.
    private bool TryGpuTreeGroupPruneCounts(GpuTreeGroup group, int lod, float near, float far, float gate, float reuse,
        RingCanonicalPosition? focus, double circumference, bool compactProfile,
        out (int PotentialProtected, int PotentialOrdinary, int GuaranteedProtected, int GuaranteedOrdinary) counts,
        float guaranteedGate = float.NaN, float guaranteedNearOverride = float.NaN)
    {
        if (float.IsNaN(guaranteedGate)) guaranteedGate = gate;
        if (float.IsNaN(guaranteedNearOverride)) guaranteedNearOverride = near;
        counts = default;
        if (focus is not RingCanonicalPosition canonical) return true;
        Vector2 camera = CanonicalCamera(group, canonical, circumference);
        bool legacyProtectedLod = !compactProfile && lod == 0;
        float potentialNearSquared = MathF.Max(0, near - reuse); potentialNearSquared *= potentialNearSquared;
        float potentialFar = far + reuse; float potentialFarSquared = potentialFar * potentialFar;
        float guaranteedNear = guaranteedNearOverride + reuse; float guaranteedNearSquared = guaranteedNear * guaranteedNear;
        float guaranteedFar = far - reuse; float guaranteedFarSquared = guaranteedFar > 0 ? guaranteedFar * guaranteedFar : 0;
        int pp = 0, po = 0, gp = 0, go = 0;
        for (int i = 0; i < group.Metadata.Length; i++)
        {
            Vector4 meta = group.Metadata[i];
            bool protectedRoot = meta.Y > .5f;
            bool legacyProtected = protectedRoot && legacyProtectedLod;
            if (protectedRoot && !compactProfile && !legacyProtected) continue;
            if (meta.Y <= .5f && meta.X > gate) continue;
            float dx = meta.Z - camera.X, dz = meta.W - camera.Y, distanceSquared = dx * dx + dz * dz;
            bool potential = legacyProtected || (distanceSquared >= potentialNearSquared && distanceSquared < potentialFarSquared);
            if (!potential) continue;
            bool guaranteed = legacyProtected || ((guaranteedNearOverride <= 0 || distanceSquared >= guaranteedNearSquared)
                && guaranteedFar > 0 && distanceSquared < guaranteedFarSquared && (protectedRoot || meta.X <= guaranteedGate));
            if (protectedRoot) { pp++; if (guaranteed) gp++; }
            else { po++; if (guaranteed) go++; }
        }
        counts = (pp, po, gp, go);
        return pp != 0 || po != 0;
    }

    // ---- PERF-3 T2: the selection passes computed in parallel with identical results ----
    /// <summary>Test switch: the tree selection runs entirely on the calling thread, as before PERF-3 T2. No menu row.</summary>
    public bool SerialTreeSelection { get; set; }
    /// <summary>Debug switch: every selection rebuild also computes the parallel result and counts differences in <see cref="TreeSelectionMismatches"/>. The serial result is the one committed. No menu row.</summary>
    public bool VerifyTreeSelection { get; set; }
    /// <summary>Differences found between the serial and parallel selection while <see cref="VerifyTreeSelection"/> is on (order, groups, prune counts, histograms).</summary>
    public long TreeSelectionMismatches { get; private set; }
    /// <summary>Parallel passes run only when at least this many roots (effective edges) or groups x 16 (prune counts) are involved; smaller work stays serial.</summary>
    internal int ParallelSelectionMinimumRoots = 4096;
    private const int ParallelSelectionGroupsPerChunk = 32;
    private const int ParallelSelectionMaximumDegree = 4;

    private sealed class GpuTreeEffectiveChunk
    {
        public readonly int[] Histogram = new int[GpuTreeEffectiveBins];
        public int Protected;
        public float Farthest;
    }
    private GpuTreeEffectiveChunk[] _gpuTreeEffectiveChunks = Array.Empty<GpuTreeEffectiveChunk>();
    private readonly List<(Vector4[] Metadata, Vector2 Local)> _gpuTreeEffectiveEligible = new();
    private readonly int[] _gpuTreeEffectiveVerifyHistogram = new int[GpuTreeEffectiveBins];

    // The per-root body of the effective-edge histogram, shared by the serial and parallel passes so both evaluate the same expressions.
    private static void AccumulateGpuTreeEffectiveRoots(Vector4[] metadata, Vector2 local, float gate, float low, float invBin, float lowSquared, float highSquared,
        bool slotZero, int[] histogram, ref int protectedCount, ref float farthest)
    {
        for (int i = 0; i < metadata.Length; i++)
        {
            Vector4 meta = metadata[i];
            bool protectedRoot = meta.Y > .5f;
            if (!protectedRoot && meta.X > gate) continue;
            float dx = meta.Z - local.X, dz = meta.W - local.Y, distanceSquared = dx * dx + dz * dz;
            if (distanceSquared < lowSquared || distanceSquared >= highSquared) continue;
            float distance = MathF.Sqrt(distanceSquared);
            if (protectedRoot) { protectedCount++; if (slotZero && distance > farthest) farthest = distance; }
            else histogram[Math.Min(GpuTreeEffectiveBins - 1, (int)((distance - low) * invBin))]++;
        }
    }

    // One slot's histogram, protected count and farthest protected root (slot 0 only). The parallel variant gathers the eligible groups serially (the distance
    // cache is not thread-safe), counts chunks of groups into private histograms and sums them in chunk order; integer sums and max are order independent.
    private void CountGpuTreeEffectiveSlot(bool parallel, bool force, int slot, float low, float high, float gate, float guard, Vector3 camera,
        RingCanonicalPosition focus, double circumference, int[] histogram, out int protectedCount, out float farthest)
    {
        float invBin = GpuTreeEffectiveBins / (high - low), lowSquared = low * low, highSquared = high * high;
        bool slotZero = slot == 0;
        int protectedTotal = 0; float farthestTotal = 0;
        if (!parallel)
        {
            foreach (GpuTreeGroup group in GroupsForGpuLod(slot))
            {
                (float near, float far) = GpuTreeGroupDistanceBounds(group, camera, focus, circumference);
                if (far + guard < low || near - guard >= high) continue;
                AccumulateGpuTreeEffectiveRoots(group.Metadata, CanonicalCamera(group, focus, circumference), gate, low, invBin, lowSquared, highSquared, slotZero, histogram, ref protectedTotal, ref farthestTotal);
            }
            protectedCount = protectedTotal; farthest = farthestTotal;
            return;
        }
        var eligible = _gpuTreeEffectiveEligible; eligible.Clear();
        long roots = 0;
        if (FindGpuTreeBoundsSet(slot) is { } set)
        {
            for (int i = 0; i < set.Count; i++)
            {
                if (set.Far[i] + guard < low || set.Near[i] - guard >= high) continue;
                GpuTreeGroup group = set.Groups[i];
                eligible.Add((group.Metadata, CanonicalCamera(group, focus, circumference)));
                roots += group.Metadata.Length;
            }
        }
        else
        foreach (GpuTreeGroup group in GroupsForGpuLod(slot))
        {
            (float near, float far) = GpuTreeGroupDistanceBounds(group, camera, focus, circumference);
            if (far + guard < low || near - guard >= high) continue;
            eligible.Add((group.Metadata, CanonicalCamera(group, focus, circumference)));
            roots += group.Metadata.Length;
        }
        if (!force && roots < ParallelSelectionMinimumRoots)
        {
            foreach ((Vector4[] metadata, Vector2 local) in eligible)
                AccumulateGpuTreeEffectiveRoots(metadata, local, gate, low, invBin, lowSquared, highSquared, slotZero, histogram, ref protectedTotal, ref farthestTotal);
            eligible.Clear();
            protectedCount = protectedTotal; farthest = farthestTotal;
            return;
        }
        int chunkCount = (eligible.Count + ParallelSelectionGroupsPerChunk - 1) / ParallelSelectionGroupsPerChunk;
        if (_gpuTreeEffectiveChunks.Length < chunkCount)
        {
            int previous = _gpuTreeEffectiveChunks.Length;
            Array.Resize(ref _gpuTreeEffectiveChunks, Math.Max(chunkCount, previous * 2));
            for (int c = previous; c < _gpuTreeEffectiveChunks.Length; c++) _gpuTreeEffectiveChunks[c] = new GpuTreeEffectiveChunk();
        }
        GpuTreeEffectiveChunk[] chunks = _gpuTreeEffectiveChunks;
        Parallel.For(0, chunkCount, new ParallelOptions { MaxDegreeOfParallelism = ParallelSelectionMaximumDegree }, c =>
        {
            GpuTreeEffectiveChunk chunk = chunks[c];
            Array.Clear(chunk.Histogram);
            int chunkProtected = 0; float chunkFarthest = 0;
            int end = Math.Min(eligible.Count, (c + 1) * ParallelSelectionGroupsPerChunk);
            for (int g = c * ParallelSelectionGroupsPerChunk; g < end; g++)
                AccumulateGpuTreeEffectiveRoots(eligible[g].Metadata, eligible[g].Local, gate, low, invBin, lowSquared, highSquared, slotZero, chunk.Histogram, ref chunkProtected, ref chunkFarthest);
            chunk.Protected = chunkProtected; chunk.Farthest = chunkFarthest;
        });
        for (int c = 0; c < chunkCount; c++)
        {
            GpuTreeEffectiveChunk chunk = chunks[c];
            for (int b = 0; b < GpuTreeEffectiveBins; b++) histogram[b] += chunk.Histogram[b];
            protectedTotal += chunk.Protected;
            if (chunk.Farthest > farthestTotal) farthestTotal = chunk.Farthest;
        }
        eligible.Clear();
        protectedCount = protectedTotal; farthest = farthestTotal;
    }

    // Per selection rebuild, the groups of every distinct container (the raw groups and the merged mid and far sets) copied into flat arrays in enumeration order,
    // with their canonical bounds computed in parallel chunks. The per-slot and per-LOD passes then read arrays instead of hashing every group through the cache.
    private sealed class GpuTreeBoundsSet
    {
        public Dictionary<GpuGroupKey, GpuTreeGroup>? Container;
        public GpuTreeGroup[] Groups = Array.Empty<GpuTreeGroup>();
        public float[] Near = Array.Empty<float>(), Far = Array.Empty<float>();
        public int Count;
    }
    private readonly List<GpuTreeBoundsSet> _gpuTreeBoundsSets = new();
    private int _gpuTreeBoundsSetCount;
    private bool _gpuTreeBoundsReady;
    private const int ParallelBoundsGroupsPerChunk = 512;

    private void PrepareGpuTreeBoundsSets(RingCanonicalPosition focus, double circumference)
    {
        _gpuTreeBoundsSetCount = 0; _gpuTreeBoundsReady = false;
        for (int lod = 0; lod < 5; lod++)
        {
            if (!IsGpuTreeLodEnabled(lod)) continue;
            Dictionary<GpuGroupKey, GpuTreeGroup> container = GroupContainerForGpuLod(lod);
            bool known = false;
            for (int i = 0; i < _gpuTreeBoundsSetCount; i++) if (ReferenceEquals(_gpuTreeBoundsSets[i].Container, container)) { known = true; break; }
            if (known) continue;
            if (_gpuTreeBoundsSetCount == _gpuTreeBoundsSets.Count) _gpuTreeBoundsSets.Add(new GpuTreeBoundsSet());
            GpuTreeBoundsSet set = _gpuTreeBoundsSets[_gpuTreeBoundsSetCount++];
            set.Container = container;
            int count = container.Count;
            if (set.Groups.Length < count)
            {
                int size = Math.Max(count, set.Groups.Length * 2);
                set.Groups = new GpuTreeGroup[size]; set.Near = new float[size]; set.Far = new float[size];
            }
            int n = 0;
            foreach (GpuTreeGroup group in container.Values) set.Groups[n++] = group;
            set.Count = n;
            GpuTreeGroup[] groups = set.Groups; float[] near = set.Near, far = set.Far;
            if (n < ParallelBoundsGroupsPerChunk * 2)
                for (int i = 0; i < n; i++) (near[i], far[i]) = GpuTreeCanonicalBounds(groups[i], focus, circumference);
            else
                Parallel.For(0, (n + ParallelBoundsGroupsPerChunk - 1) / ParallelBoundsGroupsPerChunk, new ParallelOptions { MaxDegreeOfParallelism = ParallelSelectionMaximumDegree }, c =>
                {
                    int end = Math.Min(n, (c + 1) * ParallelBoundsGroupsPerChunk);
                    for (int i = c * ParallelBoundsGroupsPerChunk; i < end; i++) (near[i], far[i]) = GpuTreeCanonicalBounds(groups[i], focus, circumference);
                });
        }
        _gpuTreeBoundsReady = true;
    }

    private GpuTreeBoundsSet? FindGpuTreeBoundsSet(int lod)
    {
        if (!_gpuTreeBoundsReady) return null;
        Dictionary<GpuGroupKey, GpuTreeGroup> container = GroupContainerForGpuLod(lod);
        for (int i = 0; i < _gpuTreeBoundsSetCount; i++) if (ReferenceEquals(_gpuTreeBoundsSets[i].Container, container)) return _gpuTreeBoundsSets[i];
        return null;
    }

    private void ReleaseGpuTreeBoundsSets()
    {
        _gpuTreeBoundsReady = false;
        for (int i = 0; i < _gpuTreeBoundsSetCount; i++) { Array.Clear(_gpuTreeBoundsSets[i].Groups, 0, _gpuTreeBoundsSets[i].Count); _gpuTreeBoundsSets[i].Container = null; }
        _gpuTreeBoundsSetCount = 0;
    }

    private struct GpuTreePruneCandidate
    {
        public GpuTreeGroup Group; public GpuOutputKey Key; public float Near, LodNear, LodFar, Gate, GuaranteedGate, GuaranteedNear;
        public int Lod; public bool Priority, Ok;
        public (int PotentialProtected, int PotentialOrdinary, int GuaranteedProtected, int GuaranteedOrdinary) Counts;
    }
    private GpuTreePruneCandidate[] _gpuTreePruneCandidates = new GpuTreePruneCandidate[256];

    /// <summary>
    /// Candidate groups of every enabled LOD in the original loop order, with the per-group prune counts. The parallel variant lists the overlapping groups
    /// serially, computes the (pure) prune counts of those in parallel chunks and then appends to the outputs serially in the listed order, so the order list,
    /// group map and counts are exactly what the serial loop produces.
    /// </summary>
    private void BuildGpuTreeSelectionCandidates(bool parallel, bool force, float[] edges, float[] gates, float guard, float reuseRadius, Vector3 camera,
        RingCanonicalPosition? focus, double circumference, bool compactProfile,
        List<(GpuOutputKey Key, float Distance, bool Priority)> order, Dictionary<GpuOutputKey, GpuTreeGroup> outputGroups,
        Dictionary<GpuOutputKey, (int PotentialProtected, int PotentialOrdinary, int GuaranteedProtected, int GuaranteedOrdinary)> counts)
    {
        order.Clear(); outputGroups.Clear(); counts.Clear();
        bool prune = PruneEmptyGpuTreeBatches;
        GpuTreeLodInterval(4, edges, out _, out _, out float quadInStart, out _, out _, out _);
        int listed = 0;
        for (int lod = 0; lod < 5; lod++)
        {
            if (!IsGpuTreeLodEnabled(lod)) continue;
            // Per-LOD invariants: the interval depends only on (lod, edges).
            GpuTreeLodInterval(lod, edges, out float lodNear, out float lodFar, out _, out _, out _, out _);
            // Quad mode: the quad slot also accepts steep trees of the card gate from the card near edge, so its candidate count uses the larger gate and
            // only counts roots past its own start as guaranteed; the card slot loses steep trees, so none of its roots is guaranteed.
            bool quadSlot = lod == 4 && EffectiveFarTreeQuads, cardSlot = lod == 3 && EffectiveFarTreeQuads;
            float gate = quadSlot ? Math.Max(gates[3], gates[4]) : gates[lod];
            float guaranteedGate = cardSlot ? -1f : quadSlot ? gates[4] : float.NaN, guaranteedNear = quadSlot ? quadInStart : float.NaN;
            void Consider(GpuTreeGroup group, float near, float far)
            {
                var key = new GpuOutputKey(group.Key, lod);
                bool overlaps = (lod == 0 && group.HasProtected && !compactProfile) || (far + guard >= lodNear && near - guard < lodFar);
                if (!overlaps) return;
                bool priority = (compactProfile || lod == 0) && group.HasProtected;
                if (!prune) { order.Add((key, near, priority)); outputGroups[key] = group; return; }
                if (!parallel)
                {
                    if (!TryGpuTreeGroupPruneCounts(group, lod, lodNear, lodFar, gate, reuseRadius, focus, circumference, compactProfile, out var groupCounts, guaranteedGate, guaranteedNear)) return;
                    counts[key] = groupCounts;
                    order.Add((key, near, priority)); outputGroups[key] = group;
                    return;
                }
                if (listed == _gpuTreePruneCandidates.Length) Array.Resize(ref _gpuTreePruneCandidates, listed * 2);
                _gpuTreePruneCandidates[listed++] = new GpuTreePruneCandidate { Group = group, Key = key, Near = near, LodNear = lodNear, LodFar = lodFar, Gate = gate,
                    GuaranteedGate = guaranteedGate, GuaranteedNear = guaranteedNear, Lod = lod, Priority = priority };
            }
            if (parallel && FindGpuTreeBoundsSet(lod) is { } set)
                for (int i = 0; i < set.Count; i++) Consider(set.Groups[i], set.Near[i], set.Far[i]);
            else
                foreach (GpuTreeGroup group in GroupsForGpuLod(lod))
                {
                    (float near, float far) = GpuTreeGroupDistanceBounds(group, camera, focus, circumference);
                    Consider(group, near, far);
                }
        }
        if (listed == 0) return;
        GpuTreePruneCandidate[] candidates = _gpuTreePruneCandidates;
        long work = 0;
        for (int i = 0; i < listed; i++) work += candidates[i].Group.Metadata.Length;
        if (!force && work < ParallelSelectionMinimumRoots)
            for (int i = 0; i < listed; i++) ComputeGpuTreePruneCandidate(ref candidates[i], reuseRadius, focus, circumference, compactProfile);
        else
        {
            int chunkCount = (listed + ParallelSelectionGroupsPerChunk - 1) / ParallelSelectionGroupsPerChunk;
            Parallel.For(0, chunkCount, new ParallelOptions { MaxDegreeOfParallelism = ParallelSelectionMaximumDegree }, c =>
            {
                int end = Math.Min(listed, (c + 1) * ParallelSelectionGroupsPerChunk);
                for (int i = c * ParallelSelectionGroupsPerChunk; i < end; i++) ComputeGpuTreePruneCandidate(ref candidates[i], reuseRadius, focus, circumference, compactProfile);
            });
        }
        for (int i = 0; i < listed; i++)
        {
            ref GpuTreePruneCandidate candidate = ref candidates[i];
            if (candidate.Ok)
            {
                counts[candidate.Key] = candidate.Counts;
                order.Add((candidate.Key, candidate.Near, candidate.Priority)); outputGroups[candidate.Key] = candidate.Group;
            }
            candidate.Group = null!; // Do not keep groups alive from the reused buffer.
        }
    }

    private void ComputeGpuTreePruneCandidate(ref GpuTreePruneCandidate candidate, float reuseRadius, RingCanonicalPosition? focus, double circumference, bool compactProfile)
        => candidate.Ok = TryGpuTreeGroupPruneCounts(candidate.Group, candidate.Lod, candidate.LodNear, candidate.LodFar, candidate.Gate, reuseRadius, focus, circumference,
            compactProfile, out candidate.Counts, candidate.GuaranteedGate, candidate.GuaranteedNear);

    private readonly List<(GpuOutputKey Key, float Distance, bool Priority)> _gpuTreeVerifyOrder = new();
    private readonly Dictionary<GpuOutputKey, GpuTreeGroup> _gpuTreeVerifyGroups = new();
    private readonly Dictionary<GpuOutputKey, (int PotentialProtected, int PotentialOrdinary, int GuaranteedProtected, int GuaranteedOrdinary)> _gpuTreeVerifyCounts = new();

    private void CountGpuTreeSelectionMismatch(string what)
    {
        if (TreeSelectionMismatches++ < 5) GD.PrintErr($"Tree selection mismatch: {what}");
    }

    // Computes the parallel selection (forced through the parallel code whatever its size) and compares it with the serial one that was just committed.
    private void VerifyGpuTreeSelectionCandidates(float[] edges, float[] gates, float guard, float reuseRadius, Vector3 camera, RingCanonicalPosition? focus, double circumference,
        bool compactProfile, List<(GpuOutputKey Key, float Distance, bool Priority)> order, Dictionary<GpuOutputKey, GpuTreeGroup> outputGroups,
        Dictionary<GpuOutputKey, (int PotentialProtected, int PotentialOrdinary, int GuaranteedProtected, int GuaranteedOrdinary)> counts)
    {
        BuildGpuTreeSelectionCandidates(true, true, edges, gates, guard, reuseRadius, camera, focus, circumference, compactProfile, _gpuTreeVerifyOrder, _gpuTreeVerifyGroups, _gpuTreeVerifyCounts);
        if (order.Count != _gpuTreeVerifyOrder.Count) CountGpuTreeSelectionMismatch($"order length {order.Count} vs {_gpuTreeVerifyOrder.Count}");
        else
            for (int i = 0; i < order.Count; i++)
                if (order[i].Key != _gpuTreeVerifyOrder[i].Key || BitConverter.SingleToInt32Bits(order[i].Distance) != BitConverter.SingleToInt32Bits(_gpuTreeVerifyOrder[i].Distance)
                    || order[i].Priority != _gpuTreeVerifyOrder[i].Priority) { CountGpuTreeSelectionMismatch($"order entry {i}"); break; }
        if (outputGroups.Count != _gpuTreeVerifyGroups.Count) CountGpuTreeSelectionMismatch("group map size");
        else
            foreach ((GpuOutputKey key, GpuTreeGroup group) in outputGroups)
                if (!_gpuTreeVerifyGroups.TryGetValue(key, out GpuTreeGroup? other) || !ReferenceEquals(group, other)) { CountGpuTreeSelectionMismatch("group reference"); break; }
        if (counts.Count != _gpuTreeVerifyCounts.Count) CountGpuTreeSelectionMismatch("prune count size");
        else
            foreach ((GpuOutputKey key, var value) in counts)
                if (!_gpuTreeVerifyCounts.TryGetValue(key, out var other) || other != value) { CountGpuTreeSelectionMismatch("prune counts"); break; }
        _gpuTreeVerifyOrder.Clear(); _gpuTreeVerifyGroups.Clear(); _gpuTreeVerifyCounts.Clear();
    }

    private readonly Dictionary<GpuOutputKey, (int PotentialProtected, int PotentialOrdinary, int GuaranteedProtected, int GuaranteedOrdinary)> _gpuTreePruneCountsScratch = new();
    private readonly Dictionary<GpuOutputKey, GpuTreeGroup> _gpuTreePruneSourcesScratch = new();

    private void PruneGpuTreeBudgetGuaranteedPrefix(uint[] caps,
        Dictionary<GpuOutputKey, (int PotentialProtected, int PotentialOrdinary, int GuaranteedProtected, int GuaranteedOrdinary)> counts)
    {
        Span<uint> protectedReservation = stackalloc uint[5];
        foreach (var item in _gpuTreeOrder)
        {
            var c = counts[item.Key];
            protectedReservation[item.Key.Lod] = Math.Min(caps[item.Key.Lod], protectedReservation[item.Key.Lod] + (uint)c.GuaranteedProtected);
        }
        var retained = _gpuTreeOrderScratch; retained.Clear();
        Span<uint> guaranteedOrdinary = stackalloc uint[5];
        protectedReservation.CopyTo(guaranteedOrdinary);
        foreach (var item in _gpuTreeOrder)
        {
            int lod = item.Key.Lod;
            var c = counts[item.Key];
            if (c.PotentialProtected == 0 && c.PotentialOrdinary == 0) continue;
            // A merely possible protected root is always retained. Protected
            // reservations run before ordinary selection in the GPU backend.
            if (c.PotentialProtected == 0 && guaranteedOrdinary[lod] >= caps[lod]) continue;
            retained.Add(item);
            guaranteedOrdinary[lod] = Math.Min(caps[lod], guaranteedOrdinary[lod] + (uint)c.GuaranteedOrdinary);
        }
        if (retained.Count == _gpuTreeOrder.Count) return;
        var sources = _gpuTreePruneSourcesScratch; sources.Clear();
        foreach ((GpuOutputKey key, GpuTreeGroup group) in _gpuTreeOutputGroups) sources[key] = group;
        _gpuTreeOrder.Clear(); _gpuTreeOrder.AddRange(retained);
        _gpuTreeOutputGroups.Clear();
        foreach (var item in retained)
            _gpuTreeOutputGroups[item.Key] = sources[item.Key];
        sources.Clear();
    }

    private bool UsesFarQuadMesh(int lod) => lod == 4 && EffectiveFarTreeQuads;
    private bool _farQuadHandoffReported;

    /// <summary>
    /// Steep-view parameters of the two far slots (shader words p[78..82]). The card slot (role 1) and the quad slot (role 2) must share one set of numbers or the
    /// three tiers stop partitioning the dither interval: the quad's steep ramp is the card's fade-in (start and end), and the card's fade-out is the quad's fade-in.
    /// A mismatch is logged once and the rule is switched off.
    /// </summary>
    private void GpuTreeSteepRule(int lod, float[] edges, float[] gates, out int role, out float sinLow, out float sinHigh, out float fadeEnd, out float rankGate)
    {
        role = 0; sinLow = sinHigh = fadeEnd = 0; rankGate = 1;
        if (lod is not (3 or 4) || !EffectiveFarTreeQuads) return;
        GpuTreeLodInterval(3, edges, out float cardNear, out _, out float cardInStart, out float cardInEnd, out float cardOutStart, out float cardOutEnd);
        GpuTreeLodInterval(4, edges, out float quadNear, out _, out float quadInStart, out float quadInEnd, out _, out _);
        if (cardNear != quadNear || cardNear != cardInStart || cardOutStart != quadInStart || cardOutEnd != quadInEnd || !(cardInEnd > cardInStart))
        {
            if (!_farQuadHandoffReported)
            {
                _farQuadHandoffReported = true;
                GD.PushError($"Far tree quad hand-off ranges disagree (card {cardNear}/{cardInStart}-{cardInEnd}/{cardOutStart}-{cardOutEnd}, quad {quadNear}/{quadInStart}-{quadInEnd}); the steep-view rule is off.");
            }
            return;
        }
        role = lod == 3 ? 1 : 2;
        sinLow = MathF.Sin(Mathf.DegToRad(TreeSteepElevationLowDegrees)); sinHigh = MathF.Sin(Mathf.DegToRad(TreeSteepElevationHighDegrees));
        fadeEnd = cardInEnd; rankGate = gates[3];
    }

    private bool CreateGpuTreeOutput(GpuOutputKey key)
    {
        if (!_gpuTreeOutputGroups.TryGetValue(key, out GpuTreeGroup? group)) return false;
        Mesh mesh;
        Material? material;
        bool castsShadow;
        if (group.SharedImpostor)
        {
            ImportedTreeImpostorCatalog far = ImportedTreeImpostorCatalog.Load();
            bool quad = UsesFarQuadMesh(key.Lod);
            mesh = quad ? far.FarQuadMesh : far.Mesh; material = quad ? far.FarQuadMaterial : far.Material; castsShadow = false;
            RegisterSharedFarTreeMaterial(far.FarQuadMaterial);
            RegisterSharedFarTreeMaterial(far.Material);
        }
        else
        {
            // Render LODs (TR-5): slot 1 draws the authored LOD2 (template 2) and slot 2 the decimated LOD2b; otherwise slot N draws template N.
            bool renderLods = EffectiveTreeRenderLods;
            var templates = key.Lod switch { 0 => _lod1AuthoredTemplates, 1 => renderLods ? _lod3AuthoredTemplates : _lod2AuthoredTemplates, 2 => _lod3AuthoredTemplates, _ => _lod4AuthoredTemplates };
            if (templates == null || key.Group.Variant < 0 || key.Group.Variant >= templates.Length) return false;
            MultiMeshInstance3D template = templates[key.Group.Variant];
            if (template.Multimesh?.Mesh is not Mesh templateMesh) return false;
            mesh = templateMesh; material = template.MaterialOverride; castsShadow = key.Lod < 3;
            if (UsesRenderLodMesh(key.Lod) && UsingAuthoredTreeMeshes && templates.Length == ImportedTreeCatalog.VariantCount)
                mesh = ImportedTreeRenderLodCatalog.GetLod2b(ImportedTreeCatalog.Load()[key.Group.Variant]);
            if ((key.Lod == 2 || (key.Lod == 1 && renderLods)) && UsingAuthoredTreeMeshes) mesh = ImportedTreeRenderMaterials.GetSimplifiedMesh(mesh);
        }
        ConfigureGpuTreeOutputMeshMaterials(key, mesh);
        ConfigureGpuTreeOutputMaterial(key, group, ref material);
        MultiMeshInstance3D renderer = CreateInstances($"GpuTree_{key.Group.Around}_{key.Group.Across}_{key.Group.Variant}_L{key.Lod}", mesh,
            material, castsShadow, castsShadow ? key.Lod : -1);
        renderer.Visible = false; renderer.ExtraCullMargin = 8;
        SetGpuTreeFullListCast(renderer, GeometryInstance3D.ShadowCastingSetting.Off);
        // The camera draws its own list (frustum-filtered by phase 6 of the compute); this renderer keeps the full list for the sun shadows.
        var cameraMultiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh };
        var cameraRenderer = new MultiMeshInstance3D { Name = renderer.Name + "_Camera", Multimesh = cameraMultiMesh, MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = renderer.ExtraCullMargin };
        renderer.AddChild(cameraRenderer);
        MultiMeshInstance3D? verifyRenderer = null;
        MultiMesh? verifyMultiMesh = null;
        if (TreeCullVerify && CreateTreeCullVerifyMaterial(material, mesh) is { } verifyMaterial)
        {
            verifyMultiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh };
            verifyRenderer = new MultiMeshInstance3D { Name = renderer.Name + "_CullVerify", Multimesh = verifyMultiMesh, MaterialOverride = verifyMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = renderer.ExtraCullMargin };
            renderer.AddChild(verifyRenderer);
        }
        int id = _gpuTreeCompute!.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration(renderer.Name, renderer.Multimesh!, mesh,
            group.Rows, group.Metadata, group.RootCount, group.Bounds, false, group.SharedImpostor, UsesTreeDitherData(key.Lod), group.Expansion, cameraMultiMesh, verifyMultiMesh));
        if (id < 0) { UnregisterTreeSunShadowRenderer(renderer); renderer.QueueFree(); return false; }
        _gpuTreeOutputs[key] = new GpuTreeOutput { Renderer = renderer, CameraRenderer = cameraRenderer, VerifyRenderer = verifyRenderer, BatchId = id, SourceGroup = group, TreeDitherData = UsesTreeDitherData(key.Lod), FarQuad = UsesFarQuadMesh(key.Lod), RenderLodMesh = UsesRenderLodMesh(key.Lod) };
        if (material is ShaderMaterial) renderer.SetInstanceShaderParameter("vegetation_haze_slot", _gpuTreeCompute.GetHazeSlot(id));
        if (material is ShaderMaterial) cameraRenderer.SetInstanceShaderParameter("vegetation_haze_slot", _gpuTreeCompute.GetCameraHazeSlot(id));
		MarkTreeSunShadowPolicyDirty();
        return true;
    }

    private void RemoveGpuOutput(GpuOutputKey key)
    {
        if (!_gpuTreeOutputs.Remove(key, out GpuTreeOutput? output)) return;
        output.Renderer.Visible = false;
        _gpuTreeCompute?.RemoveBatch(output.BatchId);
		UnregisterTreeSunShadowRenderer(output.Renderer);
        ReleaseGpuTreeOutputMaterial(output);
        output.Renderer.QueueFree();
        InvalidateGpuTreeFrameCache();
    }
    private void RetireGpuOutput(GpuOutputKey key)
    {
        if (!_gpuTreeOutputs.Remove(key, out GpuTreeOutput? output)) return;
        // An output still waiting for its first compute result has no visual
        // coverage to preserve. Do not let it replace an older usable fallback.
        if (!KeepTreeLodFallbacksUntilReady || _gpuTreeCompute == null
            || !_gpuTreeCompute.IsBatchReady(output.BatchId) || !output.Renderer.Visible)
        {
            DisposeGpuTreeOutput(output);
            InvalidateGpuTreeFrameCache();
            return;
        }
        // Retained output is visual continuity only: it must not remain in the
        // current sun-shadow selection while a replacement is being prepared.
        UnregisterTreeSunShadowRenderer(output.Renderer);
        ReleaseGpuTreeOutputMaterial(output);
        SetGpuTreeFullListCast(output.Renderer, GeometryInstance3D.ShadowCastingSetting.Off);
        // There can only be one retained generation for a logical output key.
        if (_gpuTreeFallbackOutputs.Remove(key, out GpuTreeOutput? older))
        {
            _gpuTreeFallbackOrder.Remove(key);
            DisposeGpuTreeOutput(older);
        }
        _gpuTreeFallbackOutputs[key] = output;
        _gpuTreeFallbackOrder[key] = ++_gpuTreeFallbackSequence;
        TrimGpuTreeFallbackOutputs();
        InvalidateGpuTreeFrameCache();
    }
    private void DisposeGpuTreeOutput(GpuTreeOutput output)
    {
        output.Renderer.Visible = false;
        _gpuTreeCompute?.RemoveBatch(output.BatchId);
        UnregisterTreeSunShadowRenderer(output.Renderer);
        ReleaseGpuTreeOutputMaterial(output);
        output.Renderer.QueueFree();
    }
    private int GpuTreeFallbackLimit()
        => Math.Max(1, Math.Min(Mathf.Clamp(MaximumGpuTreeBatches, 16, 2048), Math.Max(128, MaximumVegetationRendererNodes)));
    private void TrimGpuTreeFallbackOutputs()
    {
        while (_gpuTreeFallbackOutputs.Count > GpuTreeFallbackLimit())
        {
            GpuOutputKey oldest = _gpuTreeFallbackOrder.OrderBy(pair => pair.Value).First().Key;
            GpuTreeOutput output = _gpuTreeFallbackOutputs[oldest];
            _gpuTreeFallbackOutputs.Remove(oldest); _gpuTreeFallbackOrder.Remove(oldest);
            DisposeGpuTreeOutput(output);
        }
    }
    private void ClearGpuTreeFallbackOutputs(bool budgeted = false)
    {
        using var scope = RingPerformanceTelemetry.MeasureCpu("Trees output retire");
        int retired = 0;
        foreach (GpuOutputKey key in _gpuTreeFallbackOutputs.Keys.ToArray())
        {
            if (budgeted && retired > 0 && (retired >= Math.Max(1, MaximumVegetationUploadsPerFrame)
                || (_gpuTreeFrameDeadline != 0 && Time.GetTicksUsec() >= _gpuTreeFrameDeadline))) break;
            DisposeGpuTreeOutput(_gpuTreeFallbackOutputs[key]);
            _gpuTreeFallbackOutputs.Remove(key); _gpuTreeFallbackOrder.Remove(key); retired++;
        }
    }
    private void RetireGpuGroupOutputs(GpuGroupKey group)
    {
        if (!_gpuTreeGroups.TryGetValue(group, out GpuTreeGroup? source)) return;
        foreach (var pair in _gpuTreeOutputs.ToArray())
            if (ReferenceEquals(pair.Value.SourceGroup, source)
                || pair.Value.SourceGroup.Members?.Any(member => ReferenceEquals(member, source)) == true)
                RetireGpuOutput(pair.Key);
    }

    private static bool SameGpuGroupRows(GpuTreeGroup a, GpuTreeGroup b)
        => a.AroundOrigin == b.AroundOrigin && a.AcrossOrigin == b.AcrossOrigin && a.Key == b.Key
            && a.Rows.AsSpan().SequenceEqual(b.Rows);
    private static bool SameMemberReferences(GpuTreeGroup[] left, List<GpuTreeGroup> right)
    {
        if (left.Length != right.Count) return false;
        for (int i = 0; i < left.Length; i++) if (!ReferenceEquals(left[i], right[i])) return false;
        return true;
    }
    private void HideCpuTreePools()
    {
        foreach (var pool in new[] { _lod1TrunkTiles, _lod1CanopyTiles, _lod2TrunkTiles, _lod2CanopyTiles,
            _lod3TrunkTiles, _lod3CanopyTiles, _vegetationLod4Tiles, _vegetationLod5Tiles })
            foreach (MultiMeshInstance3D node in pool.Values)
            {
                node.Visible = false;
                // CPU fallback rebuilds these outputs on demand. Keeping the
                // node is cheap; keeping both copies of instance data is not.
                if (node.Multimesh != null) node.Multimesh.InstanceCount = 0;
            }
    }
    private void StopGpuTreePresentation(string status)
    {
        if (_gpuTreeCompute == null && _gpuTreeOutputs.Count == 0 && !_gpuTreePresentation)
        {
            GpuTreeCullingStatus = status;
            return;
        }
        bool wasGpu = _gpuTreePresentation;
        ClearGpuTreeResidentSources(status);
        if (wasGpu) { _vegetationRenderDirty = true; _vegetationRenderMovementDirty = true; LastInstalledTransformCount = 0; }
    }
    private void ClearGpuTreeResidentSources(string status)
    {
        CancelGpuTreeSourcePreparation();
        _gpuTreeSelectionValid = false; _gpuTreeWanted.Clear();
        _gpuTreeTilePartitions.Clear();
        foreach (GpuOutputKey key in _gpuTreeOutputs.Keys.ToArray()) RemoveGpuOutput(key);
        ClearGpuTreeFallbackOutputs();
        _gpuTreeCompute?.Dispose(); _gpuTreeCompute = null;
        _gpuTreeMergedGroups.Clear(); _gpuTreeOutputGroups.Clear();
        if (!_gpuTreeFixture) { _gpuTreeGroups.Clear(); _gpuTreeRootLookup.Clear(); _gpuTreeProtectedIds.Clear(); }
        _gpuTreePresentation = false; _gpuTreeSourcesDirty = true;
        InvalidateGpuTreeFrameCache();
        _gpuTreeGeometry = null; GpuTreeCullingStatus = status;
        VegetationRendererNodeCount = CountRendererPoolNodes();
        VegetationActiveRendererNodes = 0;
        VegetationRendererEstimatedBytes = 0;
    }
    private static Vector2 CanonicalCamera(GpuTreeGroup group, RingCanonicalPosition focus, double circumference)
        => new((float)WrappedVegetationDelta(focus.AroundMeters - group.AroundOrigin, circumference), (float)(focus.AcrossMeters - group.AcrossOrigin));

    // Protected groups lead the installation order, then nearest-first with a
    // total tie-break so the result never depends on enumeration order.
    private static int CompareGpuTreeOrder((GpuOutputKey Key, float Distance, bool Priority) a, (GpuOutputKey Key, float Distance, bool Priority) b)
    {
        if (a.Priority != b.Priority) return a.Priority ? -1 : 1;
        return CompareGpuTreeNearest(a, b);
    }
    private static int CompareGpuTreeNearest((GpuOutputKey Key, float Distance, bool Priority) a, (GpuOutputKey Key, float Distance, bool Priority) b)
    {
        int d = a.Distance.CompareTo(b.Distance); if (d != 0) return d;
        d = a.Key.Lod.CompareTo(b.Key.Lod); if (d != 0) return d;
        d = a.Key.Group.Around.CompareTo(b.Key.Group.Around); if (d != 0) return d;
        d = a.Key.Group.Across.CompareTo(b.Key.Group.Across); return d != 0 ? d : a.Key.Group.Variant.CompareTo(b.Key.Group.Variant);
    }

    private void InvalidateGpuTreeFrameCache()
    {
        _gpuTreeFrameCacheVersion++;
        _gpuLastCamera = new(float.NaN, float.NaN, float.NaN);
        _gpuLastFocus = null; _gpuLastCircumference = double.NaN;
        _gpuLastCaps = null; _gpuLastEdges = _gpuLastRankGates = null; _gpuLastGuard = float.NaN; _gpuLastFramesValid = false; _gpuFrameCacheComplete = false;
        _gpuTreeLastSubmittedCamera = new(float.NaN, float.NaN, float.NaN);
        _gpuTreeLastSubmittedFocus = null; _gpuTreeLastSubmittedCircumference = double.NaN; _gpuTreeLastSubmitUsec = 0;
    }

    private uint[] GpuTreeLodCaps()
    {
        Span<uint> caps = stackalloc uint[5];
        FillGpuTreeLodCaps(caps);
        return InternArray(ref _gpuTreeCapsInterned, (ReadOnlySpan<uint>)caps);
    }

    private void FillGpuTreeLodCaps(Span<uint> caps)
    {
        uint near = (uint)Math.Max(Mathf.Clamp(MaximumLod1Trees, 32, 2048), EnableImportedForestCollisions ? MaximumImportedForestColliders : 0);
        uint middle = (uint)Mathf.Clamp(MaximumLod2Trees, 64, 4096);
        uint third = (uint)Mathf.Clamp(MaximumLod3Trees, 128, 8192);
        if (EffectiveCompactTreeLodProfile)
        {
            caps[0] = near; caps[1] = EffectiveTreeRenderLods ? middle : 0u; caps[2] = near + middle + third;
        }
        else { caps[0] = near; caps[1] = middle; caps[2] = third; }
        caps[3] = (uint)Mathf.Clamp(MaximumLod4Trees, 256, 16384); caps[4] = (uint)Mathf.Clamp(MaximumTreeBillboards, 256, 65536);
    }
    private static void WriteGpuTreeTransform(float[] rows, int i, Transform3D t)
    {
        rows[i] = t.Basis.X.X; rows[i + 1] = t.Basis.Y.X; rows[i + 2] = t.Basis.Z.X; rows[i + 3] = t.Origin.X;
        rows[i + 4] = t.Basis.X.Y; rows[i + 5] = t.Basis.Y.Y; rows[i + 6] = t.Basis.Z.Y; rows[i + 7] = t.Origin.Y;
        rows[i + 8] = t.Basis.X.Z; rows[i + 9] = t.Basis.Y.Z; rows[i + 10] = t.Basis.Z.Z; rows[i + 11] = t.Origin.Z;
    }

    internal void ConfigureGpuTreeFixture(Mesh[] meshes, Transform3D[] transforms, float[] ranks, bool[] protectedRoots)
    {
        ClearGpuTreeResidentSources("Fixture reset"); _gpuTreeFixture = true; _gpuTreeGroups.Clear();
        MultiMeshInstance3D[] Make(int lod)
        {
            var template = new MultiMeshInstance3D { Multimesh = new MultiMesh { Mesh = meshes[lod] }, Visible = false,
                CastShadow = lod < 3 ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(template); RegisterTreeSunShadowRenderer(template, lod < 3 ? lod : -1);
            return new[] { template };
        }
        _lod1AuthoredTemplates = Make(0); _lod2AuthoredTemplates = Make(1); _lod3AuthoredTemplates = Make(2); _lod4AuthoredTemplates = Make(3);
        var rows = new float[transforms.Length * 12]; var meta = new Vector4[transforms.Length]; Aabb bounds = default;
        for (int i = 0; i < transforms.Length; i++)
        { WriteGpuTreeTransform(rows, i * 12, transforms[i]); meta[i] = new Vector4(ranks[i], protectedRoots[i] ? 1 : 0, 0, 0);
            Aabb next = TreeWindDeformationBounds(transforms[i], meshes[0].GetAabb()); bounds = i == 0 ? next : bounds.Merge(next); }
        var key = new GpuGroupKey(0, 0, 0);
        _gpuTreeGroups[key] = new GpuTreeGroup { Key = key, Roots = Array.Empty<CachedTree>(), Rows = rows, Metadata = meta, Bounds = bounds.Grow(8), HasProtected = Array.Exists(meta, item => item.Y > .5f) };
        GpuTreeSourceEpoch++;
    }
    internal void StepGpuTreeFixture(Vector3 cameraWorld, Transform3D layerTransform)
    {
        ServiceGpuTreeShadowMirrors();
        GlobalTransform = layerTransform;
        if (_gpuTreeFixture && (_gpuFixtureLastMerge != MergeFarGpuTreeBatches || _gpuFixtureLastSharedFar != UseSharedFarTreeImpostors || _gpuFixtureLastMergeMid != MergeMidGpuTreeBatches)) { RebuildGpuTreeMergedGroups(1_000_000); InvalidateGpuTreeFrameCache(); _gpuFixtureLastMerge = MergeFarGpuTreeBatches; _gpuFixtureLastSharedFar = UseSharedFarTreeImpostors; _gpuFixtureLastMergeMid = MergeMidGpuTreeBatches; }
        if (!UseGpuCulling) { StopGpuTreePresentation("CPU fallback"); _gpuTreeFailed = false; return; }
        if (EnsureGpuTreeBackend()) ServiceGpuTreeOutputs(cameraWorld, null, 1_000_000);
    }
    internal void StepGpuTreeFixture(Vector3 cameraWorld, Transform3D layerTransform, RingCanonicalPosition canonicalFocus, double circumference)
    {
        ServiceGpuTreeShadowMirrors();
        GlobalTransform = layerTransform;
        if (_gpuTreeFixture && (_gpuFixtureLastMerge != MergeFarGpuTreeBatches || _gpuFixtureLastSharedFar != UseSharedFarTreeImpostors || _gpuFixtureLastMergeMid != MergeMidGpuTreeBatches)) { RebuildGpuTreeMergedGroups(Math.Max(1, circumference)); InvalidateGpuTreeFrameCache(); _gpuFixtureLastMerge = MergeFarGpuTreeBatches; _gpuFixtureLastSharedFar = UseSharedFarTreeImpostors; _gpuFixtureLastMergeMid = MergeMidGpuTreeBatches; }
        if (!UseGpuCulling) { StopGpuTreePresentation("CPU fallback"); _gpuTreeFailed = false; return; }
        if (EnsureGpuTreeBackend()) ServiceGpuTreeOutputs(cameraWorld, canonicalFocus, Math.Max(1, circumference));
    }
    internal void GpuTreeFixtureReadback(int lod, Action<uint[], float[]> callback)
    {
        var key = new GpuOutputKey(new GpuGroupKey(0, 0, 0), lod);
        if (_gpuTreeOutputs.TryGetValue(key, out GpuTreeOutput? output)) _gpuTreeCompute!.RequestReadbackForTests(output.BatchId, callback);
        else callback(Array.Empty<uint>(), Array.Empty<float>());
    }
    internal void GpuTreeFixtureReadbackAll(int lod, Action<uint[], float[]> callback)
    {
        var outputs = _gpuTreeOutputs.Where(pair => pair.Key.Lod == lod).Select(pair => pair.Value).ToArray();
        if (outputs.Length == 0) { callback(Array.Empty<uint>(), Array.Empty<float>()); return; }
        int pending = outputs.Length; var commands = new List<uint>(); var rows = new List<float>();
        foreach (GpuTreeOutput output in outputs) _gpuTreeCompute!.RequestReadbackForTests(output.BatchId, (c, r) =>
        {
            uint count = c.Length >= 2 ? c[1] : 0;
            int stride = output.TreeDitherData ? RingGpuVegetationCompute.TreeCustomFloats : output.SourceGroup.RowStride;
            commands.AddRange(c); rows.AddRange(r.Take(Math.Min(r.Length, checked((int)count * stride))));
            if (--pending == 0) callback(commands.ToArray(), rows.ToArray());
        });
    }
    internal int[] GpuTreeFixtureOutputIds(int lod)
        => _gpuTreeOutputs.Where(pair => pair.Key.Lod == lod).OrderBy(pair => pair.Key.Group.Around).ThenBy(pair => pair.Key.Group.Across).Select(pair => pair.Value.BatchId).ToArray();
    internal void SetGpuTreeFixtureProtected(long tileAround, int tileAcross, int rootIndex, bool value)
    {
        var key = new GpuGroupKey(tileAround, tileAcross, 0);
        if (!_gpuTreeGroups.TryGetValue(key, out GpuTreeGroup? group) || rootIndex < 0 || rootIndex >= group.Metadata.Length) return;
        group.Metadata[rootIndex].Y = value ? 1 : 0; group.HasProtected = Array.Exists(group.Metadata, item => item.Y > .5f); _gpuTreeProtectionRevision++; _gpuTreeProtectionLayoutRevision++;
        foreach (GpuTreeOutput output in _gpuTreeOutputs.Values)
            if (ReferenceEquals(output.SourceGroup, group)) _gpuTreeCompute?.UpdateMetadata(output.BatchId, group.Metadata);
        UpdateMergedGpuTreeMetadata(new HashSet<GpuTreeGroup> { group }); InvalidateGpuTreeFrameCache();
    }

    internal void ReplaceGpuTreeFixtureSource(long tileAround, int tileAcross, int rootIndex, float localX)
    {
        var key = new GpuGroupKey(tileAround, tileAcross, 0);
        if (!_gpuTreeGroups.TryGetValue(key, out GpuTreeGroup? source) || rootIndex < 0 || rootIndex >= source.RootCount) return;
        RetireGpuGroupOutputs(key);
        float[] rows = (float[])source.Rows.Clone(); rows[rootIndex * 12 + 3] = localX;
        _gpuTreeGroups[key] = new GpuTreeGroup
        {
            Key = source.Key, Roots = source.Roots, Rows = rows, Metadata = (Vector4[])source.Metadata.Clone(), Bounds = source.Bounds,
            AroundOrigin = source.AroundOrigin, AcrossOrigin = source.AcrossOrigin, CanonicalMin = source.CanonicalMin,
            CanonicalMax = source.CanonicalMax, HasProtected = source.HasProtected
        };
        RebuildGpuTreeMergedGroups(31_415_936); InvalidateGpuTreeFrameCache(); GpuTreeSourceEpoch++;
    }

    internal void ConfigureGpuTreeFixtureGroups(Mesh[] meshes, Transform3D[][] transforms, float[][] ranks, bool[][] protectedRoots, long[] tileAround, int[] tileAcross, Vector2I[][]? canonicalRoots = null)
    {
        ClearGpuTreeResidentSources("Fixture grouped reset"); _gpuTreeFixture = true; _gpuTreeGroups.Clear();
        MultiMeshInstance3D[] Make(int lod) { var template = new MultiMeshInstance3D { Multimesh = new MultiMesh { Mesh = meshes[lod] }, Visible = false, CastShadow = lod < 3 ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off }; AddChild(template); RegisterTreeSunShadowRenderer(template, lod < 3 ? lod : -1); return new[] { template }; }
        _lod1AuthoredTemplates = Make(0); _lod2AuthoredTemplates = Make(1); _lod3AuthoredTemplates = Make(2); _lod4AuthoredTemplates = Make(3);
        const double fixtureCircumference = 31_415_936;
        VegetationLayout layout = CaptureVegetationLayout(fixtureCircumference);
        for (int g = 0; g < transforms.Length; g++)
        {
            int count = transforms[g].Length; var rows = new float[count * 12]; var meta = new Vector4[count]; Aabb bounds = default;
            (double aroundOrigin, double acrossOrigin) = VegetationTileCenter(new VegetationTileKey(tileAround[g], tileAcross[g]), layout);
            long stableAroundOrigin = (long)aroundOrigin; int stableAcrossOrigin = (int)acrossOrigin;
            Vector2 minimum = new(float.PositiveInfinity, float.PositiveInfinity), maximum = -minimum;
            for (int i = 0; i < count; i++) { WriteGpuTreeTransform(rows, i * 12, transforms[g][i]); double rootAround = canonicalRoots is not null ? (double)canonicalRoots[g][i].X : transforms[g][i].Origin.X; double rootAcross = canonicalRoots is not null ? (double)canonicalRoots[g][i].Y : transforms[g][i].Origin.Z; Vector2 canonical = new((float)WrappedVegetationDelta(rootAround - stableAroundOrigin, fixtureCircumference), (float)(rootAcross - stableAcrossOrigin)); meta[i] = new Vector4(ranks[g][i], protectedRoots[g][i] ? 1 : 0, canonical.X, canonical.Y); minimum = minimum.Min(canonical); maximum = maximum.Max(canonical); Aabb next = TreeWindDeformationBounds(transforms[g][i], meshes[0].GetAabb()); bounds = i == 0 ? next : bounds.Merge(next); }
            var key = new GpuGroupKey(tileAround[g], tileAcross[g], 0);
            _gpuTreeGroups[key] = new GpuTreeGroup { Key = key, Roots = Array.Empty<CachedTree>(), Rows = rows, Metadata = meta, Bounds = bounds.Grow(8), AroundOrigin = stableAroundOrigin, AcrossOrigin = stableAcrossOrigin, CanonicalMin = minimum, CanonicalMax = maximum, HasProtected = Array.Exists(meta, item => item.Y > .5f) };
        }
        RebuildGpuTreeMergedGroups(fixtureCircumference); _gpuFixtureLastMerge = MergeFarGpuTreeBatches; _gpuFixtureLastSharedFar = UseSharedFarTreeImpostors; _gpuFixtureLastMergeMid = MergeMidGpuTreeBatches; InvalidateGpuTreeFrameCache(); GpuTreeSourceEpoch++;
    }

    // ---- Per-instance frustum culling of the camera tree lists (OC-2) ----------------------------------------------------------------------------------
    // Each GPU tree output keeps its full list (budget, LOD bands, dither words; the sun shadows' casters) and the compute cuts a second, camera-only list from it
    // (phase 6): the rows whose bounding sphere (mesh sphere x instance scale + the wind bound of 0.6 instance heights + 0.25 m) touches a latched, padded camera
    // frustum. The latch follows RingStandingGrassLayer's rules (GrassCullingFrustum.Pad): it is rebuilt after a turn of TreeCullRotationDegrees or a move of
    // TreeCullMovementMeters, widened by TreeCullPaddingDegrees and by the movement threshold plus a margin for the camera's own speed (the render camera can be
    // a frame or two ahead of the one this thread sees). Culling is off while a megastorm blast bends the trees and for the frame the layer is rebased.
    /// <summary>A/B switch (debug, not a tuning row): false keeps every row in the camera list. The picture is identical either way; only the draw cost differs.</summary>
    public bool TreeFrustumCulling { get; set; } = true;
    /// <summary>Debug: draws the rows the frustum filter drops in magenta (unshaded). Must be set before the outputs are created. A magenta pixel in a frame means a visible row was culled.</summary>
    public bool TreeCullVerify { get; set; }
    public long TreeCullRelatches { get; private set; }
    public string TreeCullLastRelatchReason { get; private set; } = "none";
    public bool TreeCullActive => _treeCullSentEnabled;
    /// <summary>Counters for measuring the filter (cumulative): compute-list dispatches of phase 6, render-thread ms spent recording them, and the worst single Work.</summary>
    public long TreeCullDispatches => _gpuTreeCompute?.CameraCullDispatchCount ?? 0;
    /// <summary>Debug / fixtures: <see cref="StartTreeCameraAudit"/> reads every camera list back (render thread, stalls) and checks it against its full list: rows in order and bit-identical,
    /// haze and shadow terms, the filter off = identical lists. <see cref="TreeCullAuditResult"/> holds the text once it finishes ("" while pending, "idle" before the first call).</summary>
    public string TreeCullAuditResult { get; private set; } = "idle";
    public void StartTreeCameraAudit()
    {
        if (_gpuTreeCompute == null) { TreeCullAuditResult = "no compute"; return; }
        TreeCullAuditResult = "";
        _gpuTreeCompute.RequestCameraListAudit(a => TreeCullAuditResult = $"batches={a.Batches} notIdentical={a.NotIdentical} rowsFull={a.RowsFull} rowsCamera={a.RowsCamera} dropped={a.RowsDropped} altered={a.OutOfOrderOrAltered} termsChecked={a.TermsChecked} termMismatches={a.TermMismatches} first=\"{a.First}\"");
    }
    public double TreeCullRecordMilliseconds => _gpuTreeCompute?.CameraCullRecordMilliseconds ?? 0;
    public double TreeCullWorstWorkMilliseconds => _gpuTreeCompute?.CameraCullWorstWorkMilliseconds ?? 0;
    private const float TreeCullMovementMeters = 4f, TreeCullRotationDegrees = 3f, TreeCullPaddingDegrees = 5f, TreeCullSpeedSeconds = .1f;
    private const float TreeCullWindFraction = .6f, TreeCullGuardMeters = .25f;
    private readonly record struct TreeCullCamera(ulong Id, Camera3D.ProjectionType Projection, Camera3D.KeepAspectEnum KeepAspect, float Fov, float Size, Vector2 FrustumOffset,
        float Near, float Far, float HOffset, float VOffset, Vector2 Viewport);
    private bool _treeCullValid, _treeCullSentEnabled, _treeCullSentVerify, _treeCullHavePrevious;
    private Transform3D _treeCullTransform, _treeCullLayer;
    private TreeCullCamera _treeCullCamera;
    private float _treeCullSpeedMargin;
    private Vector3 _treeCullPreviousOrigin;
    private ulong _treeCullHoldUntilUsec;
    private ulong _treeCullPreviousUsec;
    private Vector4[]? _treeCullPlanes;
    private Plane[]? _treeCullPlaneScratch;


    private bool TreeCullTransformsCurrent()
    {
        foreach (GpuTreeOutput output in _gpuTreeOutputs.Values)
            if (output.TransformCacheVersion != _gpuTreeFrameCacheVersion || output.CachedGlobalTransform != output.Renderer.GlobalTransform) return false;
        return true;
    }

    private void UpdateTreeCameraCulling()
    {
        if (_gpuTreeCompute is not { Ready: true }) return;
        using var scope = RingPerformanceTelemetry.MeasureCpu("Trees camera cull");
        Camera3D? camera = GetViewport()?.GetCamera3D();
        bool haveCamera = camera != null && camera.IsInsideTree();
        ulong now = Time.GetTicksUsec();
        // Speed is measured in layer space: a floating-origin rebase moves the layer and the camera together and must not read as a jump or as standing still.
        Vector3 origin = haveCamera ? GlobalTransform.AffineInverse() * camera!.GlobalTransform.Origin : default;
        float speed = 0f;
        if (haveCamera && _treeCullHavePrevious && now > _treeCullPreviousUsec)
            speed = origin.DistanceTo(_treeCullPreviousOrigin) / ((now - _treeCullPreviousUsec) * 1e-6f);
        _treeCullPreviousOrigin = origin; _treeCullPreviousUsec = now; _treeCullHavePrevious = haveCamera;
        bool layerChanged = _treeCullValid && GlobalTransform != _treeCullLayer;
        // The layer moving (a floating-origin rebase) switches the filter off for half a second; a flight that rebases often then stays off instead of toggling.
        if (layerChanged) _treeCullHoldUntilUsec = now + 500_000;
        bool held = now < _treeCullHoldUntilUsec;
        // Fast flight re-latches every frame for little gain (the padding is about as wide as the view), so the filter steps aside above ~100 m/s (hysteresis 90 / 120).
        bool fast = speed > (_treeCullSentEnabled ? 120f : 90f);
        bool enabled = TreeFrustumCulling && haveCamera && !held && !fast && !RingMegastormHazardFx.BlastActive;
        // Switching on needs the transforms the compute last culled with to be the renderers' current ones (the filter tests world-space planes against them).
        if (enabled && !_treeCullSentEnabled && !TreeCullTransformsCurrent()) enabled = false;
        bool verify = enabled && TreeCullVerify;
        TreeCullCamera live = default;
        float neededMargin = Math.Min(speed * TreeCullSpeedSeconds, 1e6f);
        string? reason = null;
        if (!_treeCullValid) reason = "initial";
        else if (enabled != _treeCullSentEnabled) reason = held ? "layer" : "enabled";
        else if (verify != _treeCullSentVerify) reason = "verify";
        else if (enabled)
        {
            live = new TreeCullCamera(camera!.GetInstanceId(), camera.Projection, camera.KeepAspect, camera.Fov, camera.Size, camera.FrustumOffset,
                camera.Near, camera.Far, camera.HOffset, camera.VOffset, GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero);
            Transform3D transform = camera.GlobalTransform;
            if (live != _treeCullCamera) reason = "projection";
            else if (transform.Origin.DistanceTo(_treeCullTransform.Origin) > TreeCullMovementMeters) reason = "translation";
            else if (live.Projection == Camera3D.ProjectionType.Perspective
                ? GrassCullingFrustum.BasisAngleDegrees(_treeCullTransform.Basis, transform.Basis) > TreeCullRotationDegrees
                : transform.Basis != _treeCullTransform.Basis) reason = "rotation";
            else if (neededMargin > _treeCullSpeedMargin) reason = "speed";
            else if (_treeCullSpeedMargin > neededMargin * 4f + 4f) reason = "settled";
        }
        _treeCullLayer = GlobalTransform;
        if (reason == null) return;
        _treeCullValid = true; _treeCullSentEnabled = enabled; _treeCullSentVerify = verify;
        TreeCullRelatches++; TreeCullLastRelatchReason = reason;
        if (!enabled)
        {
            _gpuTreeCompute.RequestCameraCull(new RingGpuVegetationCompute.CameraCullState(false, Array.Empty<Vector4>(), TreeCullWindFraction, TreeCullGuardMeters, false));
            return;
        }
        if (live.Id == 0) live = new TreeCullCamera(camera!.GetInstanceId(), camera.Projection, camera.KeepAspect, camera.Fov, camera.Size, camera.FrustumOffset,
            camera.Near, camera.Far, camera.HOffset, camera.VOffset, GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero);
        _treeCullCamera = live;
        _treeCullTransform = camera!.GlobalTransform;
        // Hysteresis: the margin is twice what the speed needs now, so ordinary speed changes do not re-latch.
        _treeCullSpeedMargin = neededMargin * 2f + .5f;
        bool perspective = live.Projection == Camera3D.ProjectionType.Perspective;
        Godot.Collections.Array<Plane> exact = camera.GetFrustum();
        int count = exact.Count;
        if (_treeCullPlanes is null || _treeCullPlanes.Length != count) { _treeCullPlanes = new Vector4[count]; _treeCullPlaneScratch = new Plane[count]; }
        float movement = TreeCullMovementMeters + _treeCullSpeedMargin + Math.Abs(camera.HOffset) + Math.Abs(camera.VOffset)
            + Math.Abs(camera.FrustumOffset.X) + Math.Abs(camera.FrustumOffset.Y);
        GrassCullingFrustum.Pad(exact, _treeCullTransform, camera.Near, camera.Far, movement, perspective ? TreeCullPaddingDegrees : 0f, perspective, _treeCullPlanes, _treeCullPlaneScratch!);
        _gpuTreeCompute.RequestCameraCull(new RingGpuVegetationCompute.CameraCullState(true, _treeCullPlanes, TreeCullWindFraction, TreeCullGuardMeters, verify));
    }

    /// <summary>Debug: an unshaded magenta copy of the tree material (same vertex shader, so wind and the camera-facing impostors move the vertices as drawn), or null when
    /// there is no spatial shader to copy. Meshes with one material per surface use their first shader material.</summary>
    private static ShaderMaterial? CreateTreeCullVerifyMaterial(Material? material, Mesh mesh)
    {
        ShaderMaterial? source = material as ShaderMaterial;
        if (source == null) foreach (ShaderMaterial candidate in ImportedTreeRenderMaterials.ShaderMaterials(mesh)) { source = candidate; break; }
        if (source?.Shader is not { } shader) return null;
        string code = shader.Code;
        int fragment = code.IndexOf("void fragment()", StringComparison.Ordinal), render = code.IndexOf("render_mode ", StringComparison.Ordinal);
        if (fragment < 0 || render < 0 || render > fragment) return null;
        int open = code.IndexOf('{', fragment), depth = 0, close = -1;
        for (int i = open; i < code.Length && close < 0; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) close = i;
        }
        if (open < 0 || close < 0) return null;
        code = code.Insert(close, "\n\tALBEDO = vec3(4.0, 0.0, 4.0);\n\tEMISSION = vec3(0.0);\n").Insert(render + "render_mode ".Length, "unshaded, ");
        var material2 = (ShaderMaterial)source.Duplicate();
        material2.Shader = new Shader { Code = code };
        return material2;
    }
}
