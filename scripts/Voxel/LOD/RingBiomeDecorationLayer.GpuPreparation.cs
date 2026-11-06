using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingBiomeDecorationLayer
{
    private sealed class GpuPreparationPlan
    {
        public required RingLodGeometrySnapshot Geometry;
        public required Transform3D ActualLayerTransform;
        public required Transform3D LayerTransform;
        public required GpuSourceSettings Settings;
        public required RingCanonicalPosition Focus;
        public required double Circumference;
        public required ulong Edit;
        public required long RefreshRevision;
        public required HashSet<ulong> ProtectedSnapshot;
        public required Dictionary<GpuGroupKey, GpuTreeGroup> Previous;
        public required Dictionary<int, Dictionary<GpuGroupKey, GpuTreeGroup>> PreviousMerged;
        public required VegetationTile[] Tiles;
        public required VegetationTile[] VisibleTiles;
        public required Dictionary<VegetationTileKey, GpuTilePartition> Partitions;
        public bool RebuildAll;
        public long ProtectionRevision;
        public readonly Dictionary<GpuGroupKey, List<CachedTree>> Selected = new();
        public readonly Dictionary<GpuGroupKey, GpuTreeGroup> Built = new();
        public Dictionary<ulong, (GpuTreeGroup Group, int Index)> Lookup = new();
        public readonly HashSet<ulong> ProtectedIds = new();
        public readonly Dictionary<int, Dictionary<GpuGroupKey, GpuTreeGroup>> Merged = new();
    }
    private GpuPreparationPlan? _gpuTreePreparationPlan;
    private long _gpuTreeProtectionRevision;
    private ulong _gpuTreeFrameDeadline;

    private double GpuTreeStreamingBudget => float.IsFinite(TreeStreamingBudgetMilliseconds)
        ? Math.Clamp(TreeStreamingBudgetMilliseconds, .25f, 8f) : 2.0;

    private GpuSourceSettings CaptureGpuTreeSourceSettings() => new(MaximumGpuTreeRoots, MaximumGpuTreeBatches,
        MaximumVegetationRendererNodes, RootEmbedMeters, MergeFarGpuTreeBatches, CellSizeMeters, SpatialTileCells, EffectiveSharedFarTreeImpostors,
        MergeMidGpuTreeBatches, EffectiveMidTreeBatchTileSpan);

    private bool GpuTreePreparationMatches(RingLodGeometrySnapshot geometry, GpuSourceSettings settings)
        => _gpuTreePreparationPlan is { } plan && plan.Geometry == geometry && plan.Edit == _vegetationEditVersion
           && plan.ActualLayerTransform == GlobalTransform && plan.Settings == settings
           && plan.ProtectionRevision == _gpuTreeProtectionRevision;

    private void ReconcileGpuTreeSources(RingLodGeometrySnapshot geometry)
    {
        if (_gpuTreePreparation == null) StartGpuTreeSourcePreparation(geometry);
        ServiceGpuTreeSourcePreparation();
    }

    private void StartGpuTreeSourcePreparation(RingLodGeometrySnapshot geometry)
    {
        FlushDeferredGpuTreeProtectedRefresh();
        using var scope = RingPerformanceTelemetry.MeasureCpu("Trees source snapshot");
        double circumference = _grid!.GetCanonicalRingCircumferenceMeters64();
        VegetationTile[] tiles = _vegetationTiles.Values.ToArray();
        var visible = new List<VegetationTile>();
        foreach (VegetationTile tile in tiles) if (_visibleVegetationTiles.Contains(tile.Key)) visible.Add(tile);
        Transform3D actualTransform = GlobalTransform;
        // Keep newly built rows close to the current floating origin even after
        // a long trip. Old renderers retain their world poses at publication.
        Transform3D targetTransform = actualTransform;
        if (targetTransform.Origin.LengthSquared() > 4096f * 4096f) targetTransform.Origin = Vector3.Zero;
        var plan = new GpuPreparationPlan {
            Geometry = geometry, ActualLayerTransform = actualTransform, LayerTransform = targetTransform,
            Settings = new GpuSourceSettings(MaximumGpuTreeRoots, MaximumGpuTreeBatches, MaximumVegetationRendererNodes, RootEmbedMeters, MergeFarGpuTreeBatches, CellSizeMeters, SpatialTileCells, EffectiveSharedFarTreeImpostors,
                MergeMidGpuTreeBatches, EffectiveMidTreeBatchTileSpan),
            Focus = _explorer!.CanonicalPosition, Circumference = circumference, Edit = _vegetationEditVersion,
            RefreshRevision = _gpuTreeSourceRefreshRevision, ProtectedSnapshot = new HashSet<ulong>(_importedForestBodies.Keys),
            Previous = _gpuTreeGroups, PreviousMerged = _gpuTreeMergedGroups, Tiles = tiles, VisibleTiles = visible.ToArray(),
            Partitions = new Dictionary<VegetationTileKey, GpuTilePartition>(tiles.Length),
            RebuildAll = _gpuTreeGeometry != geometry || _gpuTreeLayerTransform != targetTransform || _gpuTreeRootEmbed != RootEmbedMeters,
            ProtectionRevision = _gpuTreeProtectionRevision
        };
        _gpuTreePreparationPlan = plan;
        _gpuTreePreparation = PrepareGpuTreeSources(plan).GetEnumerator();
        // Count only arrivals after this snapshot; a later completion must not
        // erase them or bypass the eight-tile coalescing threshold.
        _vegetationTilesChangedSinceRender = 0;
    }

    private void ServiceGpuTreeSourcePreparation()
    {
        if (_gpuTreePreparation == null) return;
        using var scope = RingPerformanceTelemetry.MeasureCpu("Trees source prepare");
        Stopwatch watch = Stopwatch.StartNew();
        double budget = GpuTreeStreamingBudget * .60;
        bool stepped = false;
        while ((!stepped || (watch.Elapsed.TotalMilliseconds < budget
            && (_gpuTreeFrameDeadline == 0 || Time.GetTicksUsec() < _gpuTreeFrameDeadline))) && _gpuTreePreparation != null)
        {
            stepped = true;
            if (!_gpuTreePreparation.MoveNext())
            {
                _gpuTreePreparation.Dispose(); _gpuTreePreparation = null; _gpuTreePreparationPlan = null;
                // Reconcile colliders that changed while protection was frozen.
                FlushDeferredGpuTreeProtectedRefresh();
                break;
            }
        }
        LastTreePreparationMilliseconds = watch.Elapsed.TotalMilliseconds;
    }

    private IEnumerable<bool> PrepareGpuTreeSources(GpuPreparationPlan plan)
    {
        foreach (bool _ in PrepareGpuTilePartitions(plan.Tiles, plan.Partitions)) yield return false;

        int maximum = Math.Max(Mathf.Clamp(plan.Settings.MaximumRoots, 256, 1_048_576), plan.ProtectedSnapshot.Count);
        var protectedSeen = new HashSet<ulong>();
        var reservedPerTile = new Dictionary<VegetationTileKey, int>();
        var fullSelections = new Dictionary<GpuGroupKey, CachedTree[]>();
        int selectedCount = 0;
        void Add(VegetationTile tile, CachedTree tree)
        {
            GpuGroupKey key = new(tile.Key.Around, tile.Key.Across, tree.Variant);
            if (!plan.Selected.TryGetValue(key, out List<CachedTree>? roots)) plan.Selected[key] = roots = new();
            roots.Add(tree);
            selectedCount++;
        }
        // Bodies may live outside the visible set. Scan captured partitions, never a live dictionary.
        int scanned = 0;
        foreach (VegetationTile tile in plan.Tiles)
        {
            GpuTilePartition partition = plan.Partitions[tile.Key];
            foreach (ulong id in plan.ProtectedSnapshot)
            {
                if (selectedCount < maximum && partition.RootsById.TryGetValue(id, out CachedTree tree) && protectedSeen.Add(id))
                {
                    Add(tile, tree);
                    reservedPerTile[tile.Key] = reservedPerTile.GetValueOrDefault(tile.Key) + 1;
                }
                if (++scanned % 128 == 0) yield return false;
            }
            if (++scanned % 128 == 0) yield return false;
        }
        VegetationLayout layout = CaptureVegetationLayout(plan.Circumference);
        Array.Sort(plan.VisibleTiles, (a, b) => {
            var ac = VegetationTileCenter(a.Key, layout); var bc = VegetationTileCenter(b.Key, layout);
            double ad = Math.Pow(WrappedVegetationDelta(ac.Around - plan.Focus.AroundMeters, plan.Circumference), 2) + Math.Pow(ac.Across - plan.Focus.AcrossMeters, 2);
            double bd = Math.Pow(WrappedVegetationDelta(bc.Around - plan.Focus.AroundMeters, plan.Circumference), 2) + Math.Pow(bc.Across - plan.Focus.AcrossMeters, 2);
            int d = ad.CompareTo(bd); return d != 0 ? d : a.Key.Around != b.Key.Around ? a.Key.Around.CompareTo(b.Key.Around) : a.Key.Across.CompareTo(b.Key.Across);
        });
        foreach (VegetationTile tile in plan.VisibleTiles)
        {
            if (selectedCount >= maximum) break;
            int extra = tile.Trees.Count - reservedPerTile.GetValueOrDefault(tile.Key);
            if (extra <= maximum - selectedCount)
            {
                // IDs belong to one canonical tile. Complete tiles can reuse
                // their sorted arrays without visiting or sorting every root.
                foreach ((int variant, CachedTree[] roots) in plan.Partitions[tile.Key].Groups)
                {
                    var key = new GpuGroupKey(tile.Key.Around, tile.Key.Across, variant);
                    fullSelections[key] = roots;
                    plan.Selected.Remove(key); // Replaces its reserved subset.
                }
                selectedCount += extra;
            }
            else
            {
                // Preserve authored candidate order at the hard-cap boundary.
                foreach (CachedTree tree in tile.Trees)
                {
                    if (selectedCount >= maximum) break;
                    if (!protectedSeen.Contains(tree.Id)) Add(tile, tree);
                    if (++scanned % 128 == 0) yield return false;
                }
            }
            yield return false;
        }
        foreach ((GpuGroupKey key, List<CachedTree> subset) in plan.Selected)
        {
            subset.Sort(static (a, b) => { int rank = a.Rank.CompareTo(b.Rank); return rank != 0 ? rank : a.Id.CompareTo(b.Id); });
            fullSelections[key] = subset.ToArray();
            yield return false;
        }
        foreach ((GpuGroupKey key, CachedTree[] roots) in fullSelections)
        {
            if (!plan.RebuildAll && plan.Previous.TryGetValue(key, out GpuTreeGroup? old)
                && (ReferenceEquals(old.Roots, roots) || old.Roots.AsSpan().SequenceEqual(roots)))
            { plan.Built[key] = old; TreePreparationGroupsReused++; }
            else { foreach (bool _ in BuildGpuTreeGroupSliced(plan, key, roots)) yield return false; TreePreparationGroupsRebuilt++; }
            yield return false;
        }
        // Reused groups keep their lookup entries (same objects, same indices): start from the committed lookup and only drop retired
        // groups' roots and add new groups' roots. The committed lookup must still describe exactly the previous groups, else rebuild it all.
        int previousRoots = 0;
        foreach (GpuTreeGroup old in plan.Previous.Values) previousRoots += old.Roots.Length;
        bool incremental = !plan.RebuildAll && previousRoots == _gpuTreeRootLookup.Count;
        if (incremental)
        {
            plan.Lookup = new Dictionary<ulong, (GpuTreeGroup Group, int Index)>(_gpuTreeRootLookup);
            foreach ((GpuGroupKey key, GpuTreeGroup old) in plan.Previous)
            {
                if (plan.Built.TryGetValue(key, out GpuTreeGroup? kept) && ReferenceEquals(kept, old)) continue;
                for (int i = 0; i < old.Roots.Length; i++) { plan.Lookup.Remove(old.Roots[i].Id); if (++scanned % 128 == 0) yield return false; }
            }
        }
        foreach ((GpuGroupKey key, GpuTreeGroup group) in plan.Built)
        {
            if (group.HasProtected)
                for (int i = 0; i < group.Roots.Length; i++) if (group.Metadata[i].Y > .5f) plan.ProtectedIds.Add(group.Roots[i].Id);
            if (incremental && plan.Previous.TryGetValue(key, out GpuTreeGroup? was) && ReferenceEquals(was, group)) continue;
            for (int i = 0; i < group.Roots.Length; i++)
            {
                plan.Lookup[group.Roots[i].Id] = (group, i);
                if (++scanned % 128 == 0) yield return false;
            }
        }
        foreach (bool _ in BuildMergedGpuTreeGroupsSliced(plan)) yield return false;
        // The extra partition cache follows visible/protected residency, not the
        // entire raw-tile LRU. Evicted records cannot accumulate here.
        var retainedTiles = new HashSet<VegetationTileKey>(plan.VisibleTiles.Select(tile => tile.Key));
        foreach (GpuGroupKey key in plan.Built.Keys) retainedTiles.Add(new VegetationTileKey(key.Around, key.Across));
        foreach (VegetationTileKey key in plan.Partitions.Keys.ToArray())
        {
            if (!retainedTiles.Contains(key)) plan.Partitions.Remove(key);
            if (++scanned % 128 == 0) yield return false;
        }
        if (!GpuTreePreparationStillValid(plan)) { _gpuTreeSourcesDirty = true; _gpuTreePreparationPlan = null; yield break; }
        using (RingPerformanceTelemetry.MeasureCpu("Trees source commit"))
        {
            if (plan.LayerTransform != plan.ActualLayerTransform)
            {
                // Resource creation remains staged. Compensate retained outputs
                // once so changing the layer origin cannot move old geometry.
                Transform3D compensation = plan.LayerTransform.AffineInverse() * plan.ActualLayerTransform;
                GlobalTransform = plan.LayerTransform;
                foreach (GpuTreeOutput output in _gpuTreeOutputs.Values) output.Renderer.Transform = compensation * output.Renderer.Transform;
                foreach (GpuTreeOutput output in _gpuTreeFallbackOutputs.Values) output.Renderer.Transform = compensation * output.Renderer.Transform;
                if (_importedForestCollisionRoot != null)
                    _importedForestCollisionRoot.Transform = compensation * _importedForestCollisionRoot.Transform;
            }
            _gpuTreeGroups = plan.Built; _gpuTreeMergedGroups = plan.Merged; _gpuTreeRootLookup = plan.Lookup; _gpuTreeProtectedIds = plan.ProtectedIds;
            _gpuTreeTilePartitions = plan.Partitions; GpuTreeSourceEpoch++; InvalidateGpuTreeFrameCache();
            _gpuTreeGeometry = plan.Geometry; _gpuTreeLayerTransform = plan.LayerTransform; _gpuTreeSourceEdit = plan.Edit; _gpuTreeRootEmbed = plan.Settings.RootEmbed;
            _gpuTreeSourceSettings = plan.Settings; _gpuTreeSourcesDirty = _gpuTreeSourceRefreshRevision != plan.RefreshRevision;
            if (!_gpuTreeSourcesDirty) _vegetationTilesChangedSinceRender = 0;
        }
        _gpuTreePreparationPlan = null;
    }

    private bool GpuTreePreparationStillValid(GpuPreparationPlan plan)
        => plan.Edit == _vegetationEditVersion && plan.Geometry == (_grid?.CaptureLodGeometrySnapshot() ?? plan.Geometry)
           && plan.ActualLayerTransform == GlobalTransform && plan.Settings == CaptureGpuTreeSourceSettings()
           && plan.ProtectionRevision == _gpuTreeProtectionRevision;

    // Per root this keeps only cheap arithmetic: the compact record (altitude, yaw, height, variant), the canonical offset and the extent
    // min/max. The transform itself is expanded on the GPU (ring_gpu_vegetation_indirect.glsl phase 5) from a per-group anchor frame.
    private IEnumerable<bool> BuildGpuTreeGroupSliced(GpuPreparationPlan plan, GpuGroupKey key, CachedTree[] roots)
    {
        var rootParams = new Vector4[roots.Length]; Vector4[] metadata = new Vector4[roots.Length];
        Vector2 minimum = new(float.PositiveInfinity, float.PositiveInfinity), maximum = -minimum;
        float minimumAltitude = float.PositiveInfinity, maximumAltitude = float.NegativeInfinity, maximumHeight = 0;
        for (int i = 0; i < roots.Length; i++)
        {
            CachedTree tree = roots[i];
            float altitude = RootAltitude(tree.SurfaceHeight, plan.Settings.RootEmbed);
            rootParams[i] = new Vector4(altitude, tree.Yaw, tree.Height, key.Variant);
            minimumAltitude = Math.Min(minimumAltitude, altitude); maximumAltitude = Math.Max(maximumAltitude, altitude); maximumHeight = Math.Max(maximumHeight, tree.Height);
            Vector2 canonical = new((float)WrappedVegetationDelta(tree.Around - roots[0].Around, plan.Circumference), tree.Across - roots[0].Across); minimum = minimum.Min(canonical); maximum = maximum.Max(canonical);
            metadata[i] = new Vector4(tree.Rank, plan.ProtectedSnapshot.Contains(tree.Id) ? 1 : 0, canonical.X, canonical.Y);
            if (i % 128 == 127) yield return false;
        }
        Aabb bounds = ConservativeGpuTreeBounds(plan.Geometry, plan.LayerTransform, roots[0].Around, roots[0].Across, minimum, maximum, minimumAltitude, maximumAltitude, maximumHeight, GpuTreePrototypeBounds(key.Variant));
        plan.Built[key] = new GpuTreeGroup { Key = key, Roots = roots, Rows = Array.Empty<float>(), Metadata = metadata, Bounds = bounds.Grow(8), AroundOrigin = roots[0].Around, AcrossOrigin = roots[0].Across, CanonicalMin = minimum, CanonicalMax = maximum, HasProtected = Array.Exists(metadata, value => value.Y > .5f),
            Expansion = new RingGpuVegetationCompute.TreeExpansion { Roots = rootParams, Frame = BuildGpuTreeExpansionFrame(plan.Geometry, plan.LayerTransform, roots[0].Around, roots[0].Across) } };
    }

    // Bounds come from the group's canonical extents, never from a per-root transform merge. Every root lies inside the extent box
    // (altitude range included); the prototype scaled by the tallest root, plus the shader's .6-height tip wind, covers its geometry
    // whatever its yaw or lean.
    internal static Aabb ConservativeGpuTreeBounds(in RingLodGeometrySnapshot geometry, Transform3D layerTransform, long aroundOrigin, long acrossOrigin,
        Vector2 minimum, Vector2 maximum, float minimumAltitude, float maximumAltitude, float maximumHeight, Aabb prototype)
    {
        Transform3D inverse = layerTransform.AffineInverse(); Aabb bounds = default; bool first = true;
        foreach (float around in new[] { minimum.X, maximum.X }) foreach (float across in new[] { minimum.Y, maximum.Y }) foreach (float altitude in new[] { minimumAltitude, maximumAltitude })
        {
            Vector3 point = inverse * geometry.CanonicalToWorld(aroundOrigin + (double)around, altitude, acrossOrigin + (double)across);
            bounds = first ? new Aabb(point, Vector3.Zero) : bounds.Expand(point); first = false;
        }
        float prototypeRadius = 0;
        for (int corner = 0; corner < 8; corner++) prototypeRadius = Math.Max(prototypeRadius, prototype.GetEndpoint(corner).Length());
        float scale = Math.Max(inverse.Basis.Y.Length(), Math.Max(inverse.Basis.X.Length(), inverse.Basis.Z.Length()));
        return bounds.Grow(maximumHeight * scale * (prototypeRadius + .6f));
    }

    /// <summary>Frame words for shader phase 5 (see <see cref="RingGpuVegetationCompute.TreeExpansion"/>), anchored at canonical (around, 0, across).</summary>
    internal static float[] BuildGpuTreeExpansionFrame(in RingLodGeometrySnapshot geometry, Transform3D layerTransform, long aroundOrigin, long acrossOrigin)
    {
        Transform3D inverse = layerTransform.AffineInverse();
        Vector3 anchor = inverse * geometry.CanonicalToWorld(aroundOrigin, 0, acrossOrigin);
        double around = aroundOrigin % geometry.CircumferenceMeters; if (around < 0) around += geometry.CircumferenceMeters;
        double angle = around / geometry.RingRadiusMeters;
        Basis grid = geometry.GridGlobalTransform.Basis, layerInverse = inverse.Basis, combined = layerInverse * grid;
        var frame = new float[RingGpuVegetationCompute.TreeExpansion.FrameWords];
        frame[0] = anchor.X; frame[1] = anchor.Y; frame[2] = anchor.Z;
        frame[3] = (float)Math.Sin(angle); frame[4] = (float)Math.Cos(angle);
        frame[5] = (float)geometry.RingRadiusMeters; frame[6] = (float)(geometry.RingRadiusMeters - geometry.RingVerticalBaseOffsetMeters);
        void Columns(Basis basis, int at)
        {
            frame[at] = basis.X.X; frame[at + 1] = basis.X.Y; frame[at + 2] = basis.X.Z;
            frame[at + 3] = basis.Y.X; frame[at + 4] = basis.Y.Y; frame[at + 5] = basis.Y.Z;
            frame[at + 6] = basis.Z.X; frame[at + 7] = basis.Z.Y; frame[at + 8] = basis.Z.Z;
        }
        Columns(grid, 8); Columns(layerInverse, 17); Columns(combined, 26);
        return frame;
    }

    internal static Aabb TreeWindDeformationBounds(Transform3D transform, Aabb prototype)
        => (transform * prototype).Grow(transform.Basis.Y.Length() * .6f);

    private IEnumerable<bool> BuildMergedGpuTreeGroupsSliced(GpuPreparationPlan plan)
    {
        if (!plan.Settings.MergeFar && !plan.Settings.MergeMid) yield break;
        float close = Mathf.Max(20, plan.Settings.CellSize) * Mathf.Clamp(plan.Settings.TileCells, 4, 24); VegetationLayout layout = CaptureVegetationLayout(plan.Circumference);
        foreach (int lod in new[] { 2, 3, 4 })
        {
            if (lod == 2 && !plan.Settings.MergeMid) continue;
            if (lod >= 3 && !plan.Settings.MergeFar) continue;
            bool shared = lod >= 3 && plan.Settings.SharedFar;
            float span = close * (lod == 2 ? plan.Settings.MidSpan : lod == 3 ? 2 : 4); var chunks = new Dictionary<GpuGroupKey, List<GpuTreeGroup>>(); int n = 0;
            foreach (GpuTreeGroup source in plan.Built.Values) { var c = VegetationTileCenter(new VegetationTileKey(source.Key.Around, source.Key.Across), layout); var key = new GpuGroupKey((long)Math.Floor(c.Around / span), (int)Math.Floor(c.Across / span), shared ? -1 : source.Key.Variant); if (!chunks.TryGetValue(key, out var list)) chunks[key] = list = new(); list.Add(source); if (++n % 128 == 0) yield return false; }
            var result = new Dictionary<GpuGroupKey, GpuTreeGroup>();
            foreach ((GpuGroupKey key, List<GpuTreeGroup> members) in chunks)
            {
                members.Sort(static (a,b) => a.Key.Around != b.Key.Around ? a.Key.Around.CompareTo(b.Key.Around) : a.Key.Across != b.Key.Across ? a.Key.Across.CompareTo(b.Key.Across) : a.Key.Variant.CompareTo(b.Key.Variant)); long oa = (long)Math.Floor(key.Around * (double)span); int oz = (int)Math.Floor(key.Across * (double)span);
                if (plan.PreviousMerged.TryGetValue(lod, out var oldMap) && oldMap.TryGetValue(key, out var retained) && retained.AroundOrigin == oa && retained.AcrossOrigin == oz && retained.SharedImpostor == shared && retained.Members is not null && SameMemberReferences(retained.Members, members)) { result[key] = retained; continue; }
                int count = members.Sum(x => x.RootCount), dst = 0;
                // Members are always compact here: their records (variant included) concatenate; the GPU expands them against this chunk's anchor.
                var rootParams = new Vector4[count];
                Vector4[] meta = new Vector4[count];
                int[] offsets = new int[members.Count];
                Aabb bounds = default;
                bool first = true, hasProtected = false;
                Vector2 minimum = new(float.PositiveInfinity, float.PositiveInfinity), maximum = -minimum;
                for (int member = 0; member < members.Count; member++)
                {
                    GpuTreeGroup source = members[member];
                    offsets[member] = dst;
                    // Root offsets are whole metres inside their tile, so shifting by the member's origin delta (taken once, in double) equals the
                    // per-root double subtraction exactly, and the member's canonical extent shifts the same way.
                    float shiftAround = (float)WrappedVegetationDelta((double)source.AroundOrigin - oa, plan.Circumference), shiftAcross = (float)((double)source.AcrossOrigin - oz);
                    var shift = new Vector2(shiftAround, shiftAcross);
                    minimum = minimum.Min(source.CanonicalMin + shift); maximum = maximum.Max(source.CanonicalMax + shift);
                    for (int start = 0; start < source.Metadata.Length; start += 1024)
                    {
                        int length = Math.Min(1024, source.Metadata.Length - start);
                        Array.Copy(source.Expansion!.Roots, start, rootParams, dst + start, length);
                        for (int i = start; i < start + length; i++)
                        {
                            Vector4 m = source.Metadata[i];
                            meta[dst + i] = new Vector4(m.X, m.Y, m.Z + shiftAround, m.W + shiftAcross);
                            hasProtected |= m.Y > .5f;
                        }
                        yield return false;
                    }
                    bounds = first ? source.Bounds : bounds.Merge(source.Bounds);
                    first = false; dst += source.Metadata.Length;
                }
                result[key] = new GpuTreeGroup { Key = key, Roots = Array.Empty<CachedTree>(), Rows = Array.Empty<float>(), Metadata = meta,
                    Bounds = bounds.Grow(8), AroundOrigin = oa, AcrossOrigin = oz, CanonicalMin = minimum, CanonicalMax = maximum,
                    HasProtected = hasProtected, SharedImpostor = shared, Members = members.ToArray(), MemberOffsets = offsets,
                    Expansion = new RingGpuVegetationCompute.TreeExpansion { Roots = rootParams, Frame = BuildGpuTreeExpansionFrame(plan.Geometry, plan.LayerTransform, oa, oz) } };
                yield return false;
            }
            plan.Merged[lod] = result;
        }
    }

    private void CancelGpuTreeSourcePreparation()
    {
        _gpuTreePreparation?.Dispose(); _gpuTreePreparation = null; _gpuTreePreparationPlan = null;
    }
}
