using System;
using System.Collections.Generic;
using System.Linq;
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
    public int GpuTreeResidentRootCount => _gpuTreeGroups.Values.Sum(group => group.RootCount);
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
        public required float[] Rows;
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
        public required MultiMeshInstance3D Renderer;
        public required int BatchId;
        public required GpuTreeGroup SourceGroup;
        public Transform3D CachedGlobalTransform;
        public long TransformCacheVersion = long.MinValue;
        public bool Ready;
        public bool HasCachedVisibility;
        public bool CachedVisibility;
        public bool TreeDitherData;
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
        _gpuTreeFrameDeadline = Time.GetTicksUsec() + (ulong)(GpuTreeStreamingBudget * 1000);
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
        finally { _gpuTreeFrameDeadline = 0; }
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

    private IEnumerable<GpuTreeGroup> GroupsForGpuLod(int lod)
    {
        if (((lod == 2 && MergeMidGpuTreeBatches) || (lod is 3 or 4 && MergeFarGpuTreeBatches))
            && _gpuTreeMergedGroups.TryGetValue(lod, out Dictionary<GpuGroupKey, GpuTreeGroup>? merged))
            return merged.Values;
        return _gpuTreeGroups.Values;
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
        var changed = new HashSet<GpuTreeGroup>();
        foreach (ulong id in _gpuTreeProtectedIds.ToArray())
            if (!_importedForestBodies.ContainsKey(id))
            {
                if (_gpuTreeRootLookup.TryGetValue(id, out var entry)) { entry.Group.Metadata[entry.Index].Y = 0; changed.Add(entry.Group); _gpuTreeProtectionRemovedScratch = true; }
                _gpuTreeProtectedIds.Remove(id);
            }
        foreach (ulong id in _importedForestBodies.Keys)
        {
            if (_gpuTreeProtectedIds.Contains(id)) continue;
            if (_gpuTreeRootLookup.TryGetValue(id, out var entry))
            { entry.Group.Metadata[entry.Index].Y = 1; changed.Add(entry.Group); _gpuTreeProtectedIds.Add(id); _gpuTreeNewlyProtectedScratch.Add((entry.Group, entry.Index)); }
            else _gpuTreeSourcesDirty = true;
        }
        foreach (GpuTreeGroup group in changed)
        {
            group.HasProtected = Array.Exists(group.Metadata, item => item.Y > .5f);
            foreach (var output in _gpuTreeOutputs)
                if (ReferenceEquals(output.Value.SourceGroup, group)) _gpuTreeCompute?.UpdateMetadata(output.Value.BatchId, group.Metadata);
        }
        if (changed.Count > 0)
        {
            _gpuTreeProtectionRevision++;
            UpdateMergedGpuTreeMetadata(changed);
            if (GpuTreeProtectionAffectsSelection()) _gpuTreeProtectionLayoutRevision++;
            InvalidateGpuTreeFrameCache();
        }
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

    private float[] GpuTreeLodEdges()
    {
        if (EffectiveCompactTreeLodProfile)
        {
            CompactTreeFadeBounds(out _, out float nearEnd, out _, out float billboardEnd);
            float far = Math.Max(Math.Max(VisibilityRadiusMeters, MaximumLod5DistanceMeters), billboardEnd);
            float compactFourth = Mathf.Clamp(MinimumLod5DistanceMeters, billboardEnd, Math.Max(billboardEnd, MaximumLod5DistanceMeters));
            return new[] { 0f, nearEnd, nearEnd, billboardEnd, compactFourth, far };
        }
        float collision = EnableImportedForestCollisions ? Math.Max(1, ImportedForestCollisionRadiusMeters) + 4 : 1;
        float first = Math.Max(collision, Lod1DistanceMeters), second = Math.Max(first, Lod2DistanceMeters), third = Math.Max(second, Lod3DistanceMeters);
        float fourth = Mathf.Clamp(MinimumLod5DistanceMeters, third, Math.Max(third, MaximumLod5DistanceMeters));
        return new[] { 0f, first, second, third, fourth, Math.Max(Math.Max(VisibilityRadiusMeters, MaximumLod5DistanceMeters), collision) };
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
        float[] gates = { VegetationLodRankGate(1), VegetationLodRankGate(2), EffectiveCompactTreeLodProfile ? 1f : VegetationLodRankGate(3), VegetationLodRankGate(4), VegetationLodRankGate(5) };
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
        var pruneCounts = new Dictionary<GpuOutputKey, (int PotentialProtected, int PotentialOrdinary, int GuaranteedProtected, int GuaranteedOrdinary)>();
        bool compactProfile = EffectiveCompactTreeLodProfile;
        float reuseRadius = guard * .25f + .001f;
        for (int lod = 0; lod < 5; lod++)
        {
        if (!IsGpuTreeLodEnabled(lod)) continue;
        foreach (GpuTreeGroup group in GroupsForGpuLod(lod))
        {
            float near, far;
            if (focus is RingCanonicalPosition canonical)
            {
                Vector2 local = CanonicalCamera(group, canonical, circumference);
                near = local.DistanceTo(local.Clamp(group.CanonicalMin, group.CanonicalMax));
                Vector2 delta = (local - group.CanonicalMin).Abs().Max((local - group.CanonicalMax).Abs());
                far = delta.Length();
            }
            else
            {
                Aabb bounds = GlobalTransform * group.Bounds;
                near = camera.DistanceTo(camera.Clamp(bounds.Position, bounds.End));
                far = camera.DistanceTo(bounds.GetCenter()) + bounds.Size.Length() * .5f;
            }
            GpuTreeLodInterval(lod, edges, out float lodNear, out float lodFar, out _, out _, out _, out _);
            var key = new GpuOutputKey(group.Key, lod);
            bool overlaps = (lod == 0 && group.HasProtected && !compactProfile) || (far + guard >= lodNear && near - guard < lodFar);
            if (!overlaps) continue;
            if (PruneEmptyGpuTreeBatches)
            {
                if (!TryGpuTreeGroupPruneCounts(group, lod, lodNear, lodFar, gates[lod], reuseRadius, focus, circumference,
                    compactProfile, out var counts)) continue;
                pruneCounts[key] = counts;
            }
            _gpuTreeOrder.Add((key, near, (compactProfile || lod == 0) && group.HasProtected)); _gpuTreeOutputGroups[key] = group;
        }
        }
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
                || !ReferenceEquals(current.SourceGroup, _gpuTreeOutputGroups[item.Key]) || current.TreeDitherData != UsesTreeDitherData(item.Key.Lod);
            if (!needsReplacement) continue;
            // Guarantee progress even if mandatory selection consumed this
            // frame's soft budget. One Godot allocation cannot be interrupted.
            if (uploaded > 0 && (uploaded >= Math.Max(1, MaximumVegetationUploadsPerFrame)
                || (Time.GetTicksUsec() - start) / 1000.0 >= Math.Max(.1, VegetationUploadBudgetMilliseconds)
                || (_gpuTreeFrameDeadline != 0 && Time.GetTicksUsec() >= _gpuTreeFrameDeadline))) break;
            if (_gpuTreeOutputs.TryGetValue(item.Key, out GpuTreeOutput? existing)
                && _gpuTreeOutputGroups.TryGetValue(item.Key, out GpuTreeGroup? expected)
                && (!ReferenceEquals(existing.SourceGroup, expected) || existing.TreeDitherData != UsesTreeDitherData(item.Key.Lod))) RetireGpuOutput(item.Key);
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
            _gpuTreeFrameScratch.Add(new RingGpuVegetationCompute.BatchFrameParameters(output.BatchId, output.CachedGlobalTransform, false,
                lod, lodNear, lodFar, 0, 1, 1, false, gates[lod], false, null, 0,
                focus is RingCanonicalPosition canonical ? CanonicalCamera(group, canonical, circumference) : null, 0,
                EffectiveCompactTreeLodProfile, fadeInStart, fadeInEnd, fadeOutStart, fadeOutEnd));
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
                _gpuLastCamera = camera; _gpuLastCaps = (uint[])caps.Clone();
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
                    || wantedOutput.TreeDitherData != UsesTreeDitherData(key.Lod)
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

    // Selection is reused while the camera remains inside the guard region. A
    // group is removed only when no root can enter its interval anywhere in that
    // region. Guaranteed counts preserve the compute shader's protected-first
    // budget order without a GPU readback; uncertain candidates stay resident.
    private bool TryGpuTreeGroupPruneCounts(GpuTreeGroup group, int lod, float near, float far, float gate, float reuse,
        RingCanonicalPosition? focus, double circumference, bool compactProfile,
        out (int PotentialProtected, int PotentialOrdinary, int GuaranteedProtected, int GuaranteedOrdinary) counts)
    {
        counts = default;
        if (focus is not RingCanonicalPosition canonical) return true;
        Vector2 camera = CanonicalCamera(group, canonical, circumference);
        bool legacyProtectedLod = !compactProfile && lod == 0;
        float potentialNearSquared = MathF.Max(0, near - reuse); potentialNearSquared *= potentialNearSquared;
        float potentialFar = far + reuse; float potentialFarSquared = potentialFar * potentialFar;
        float guaranteedNear = near + reuse; float guaranteedNearSquared = guaranteedNear * guaranteedNear;
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
            bool guaranteed = legacyProtected || ((near <= 0 || distanceSquared >= guaranteedNearSquared)
                && guaranteedFar > 0 && distanceSquared < guaranteedFarSquared);
            if (protectedRoot) { pp++; if (guaranteed) gp++; }
            else { po++; if (guaranteed) go++; }
        }
        counts = (pp, po, gp, go);
        return pp != 0 || po != 0;
    }

    private void PruneGpuTreeBudgetGuaranteedPrefix(uint[] caps,
        Dictionary<GpuOutputKey, (int PotentialProtected, int PotentialOrdinary, int GuaranteedProtected, int GuaranteedOrdinary)> counts)
    {
        uint[] protectedReservation = new uint[5];
        foreach (var item in _gpuTreeOrder)
        {
            var c = counts[item.Key];
            protectedReservation[item.Key.Lod] = Math.Min(caps[item.Key.Lod], protectedReservation[item.Key.Lod] + (uint)c.GuaranteedProtected);
        }
        var retained = _gpuTreeOrderScratch; retained.Clear();
        uint[] guaranteedOrdinary = (uint[])protectedReservation.Clone();
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
        var sources = new Dictionary<GpuOutputKey, GpuTreeGroup>(_gpuTreeOutputGroups);
        _gpuTreeOrder.Clear(); _gpuTreeOrder.AddRange(retained);
        _gpuTreeOutputGroups.Clear();
        foreach (var item in retained)
            _gpuTreeOutputGroups[item.Key] = sources[item.Key];
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
            mesh = far.Mesh; material = far.Material; castsShadow = false;
            RegisterSharedFarTreeMaterial(far.Material);
        }
        else
        {
            var templates = key.Lod switch { 0 => _lod1AuthoredTemplates, 1 => _lod2AuthoredTemplates, 2 => _lod3AuthoredTemplates, _ => _lod4AuthoredTemplates };
            if (templates == null || key.Group.Variant < 0 || key.Group.Variant >= templates.Length) return false;
            MultiMeshInstance3D template = templates[key.Group.Variant];
            if (template.Multimesh?.Mesh is not Mesh templateMesh) return false;
            mesh = templateMesh; material = template.MaterialOverride; castsShadow = key.Lod < 3;
            if (key.Lod == 2 && UsingAuthoredTreeMeshes) mesh = ImportedTreeRenderMaterials.GetSimplifiedMesh(mesh);
        }
        ConfigureGpuTreeOutputMeshMaterials(key, mesh);
        ConfigureGpuTreeOutputMaterial(key, group, ref material);
        MultiMeshInstance3D renderer = CreateInstances($"GpuTree_{key.Group.Around}_{key.Group.Across}_{key.Group.Variant}_L{key.Lod}", mesh,
            material, castsShadow, castsShadow ? key.Lod : -1);
		renderer.Visible = false; renderer.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off; renderer.ExtraCullMargin = 8;
        int id = _gpuTreeCompute!.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration(renderer.Name, renderer.Multimesh!, mesh,
            group.Rows, group.Metadata, group.RootCount, group.Bounds, false, group.SharedImpostor, UsesTreeDitherData(key.Lod)));
        if (id < 0) { UnregisterTreeSunShadowRenderer(renderer); renderer.QueueFree(); return false; }
        _gpuTreeOutputs[key] = new GpuTreeOutput { Renderer = renderer, BatchId = id, SourceGroup = group, TreeDitherData = UsesTreeDitherData(key.Lod) };
        if (material is ShaderMaterial) renderer.SetInstanceShaderParameter("vegetation_haze_slot", _gpuTreeCompute.GetHazeSlot(id));
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
        output.Renderer.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
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
        uint near = (uint)Math.Max(Mathf.Clamp(MaximumLod1Trees, 32, 2048), EnableImportedForestCollisions ? MaximumImportedForestColliders : 0);
        uint middle = (uint)Mathf.Clamp(MaximumLod2Trees, 64, 4096);
        uint third = (uint)Mathf.Clamp(MaximumLod3Trees, 128, 8192);
        if (EffectiveCompactTreeLodProfile)
            return new[] { near, 0u, near + middle + third, (uint)Mathf.Clamp(MaximumLod4Trees, 256, 16384), (uint)Mathf.Clamp(MaximumTreeBillboards, 256, 65536) };
        return new[] { near, middle, third, (uint)Mathf.Clamp(MaximumLod4Trees, 256, 16384), (uint)Mathf.Clamp(MaximumTreeBillboards, 256, 65536) };
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
}
