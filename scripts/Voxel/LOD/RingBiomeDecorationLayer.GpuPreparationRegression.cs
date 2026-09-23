using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingBiomeDecorationLayer
{
    // Headless contract for the real sliced iterator: intentionally uses the
    // same plan and Service method as runtime, with no render backend.
    internal string RunGpuPreparationRegression()
    {
        try
        {
            TreeStreamingBudgetMilliseconds = .25f; MaximumGpuTreeRoots = 4096;
            CellSizeMeters = 20; SpatialTileCells = 4;
            MergeFarGpuTreeBatches = true; MergeMidGpuTreeBatches = true; MidTreeBatchTileSpan = 2;
            _gpuTreeGroups.Clear(); _gpuTreeMergedGroups.Clear(); _gpuTreeRootLookup.Clear(); _gpuTreeProtectedIds.Clear();
            RingLodGeometrySnapshot geometry = new(new Transform3D(Basis.Identity, Vector3.Zero), Vector3.Zero, Vector3.Zero,
                5_000_000, 0, 0, 31_415_936, -100, 100, new RingCanonicalPosition(4, 0, 0));
            var first = Tile(0, 0, 1, 300); var second = Tile(1, 0, 10000, 300); var third = Tile(10, 1, 20000, 300);
            CachedTree[] inputOrder = first.Trees.ToArray();
            _vegetationTiles.Clear(); _visibleVegetationTiles.Clear();
            foreach (VegetationTile tile in new[] { first, second, third }) { _vegetationTiles[tile.Key] = tile; _visibleVegetationTiles.Add(tile.Key); }
            PrepareRegressionPlan(geometry); if (!DrainRegressionPlan()) return "FAIL: initial .25ms plan did not converge";
            if (_gpuTreeGroups.Count != 6 || _gpuTreeGroups.Values.Any(g => g.Rows.Length == 0 || !Finite(g.Rows))) return "FAIL: initial source groups/transforms";
            if (_gpuTreeRootLookup.Count != 900 || !inputOrder.SequenceEqual(first.Trees)) return "FAIL: root set/input mutation";
            if (_gpuTreeMergedGroups.Values.SelectMany(map => map.Values).Any(g => !g.CanonicalMin.IsFinite()
                || !g.CanonicalMax.IsFinite() || g.CanonicalMin == g.CanonicalMax)) return "FAIL: merged canonical bounds";
            if (!_gpuTreeMergedGroups.TryGetValue(2, out var mid) || mid.Count >= _gpuTreeGroups.Count
                || mid.Values.Any(g => g.Key.Variant < 0 || g.Members is null || g.Members.Any(m => m.Key.Variant != g.Key.Variant)))
                return "FAIL: variant-preserving mid merge";
            var retained = _gpuTreeGroups[new GpuGroupKey(1, 0, 0)];
            PrepareRegressionPlan(geometry); if (!DrainRegressionPlan() || !ReferenceEquals(retained, _gpuTreeGroups[new GpuGroupKey(1, 0, 0)])) return "FAIL: unchanged identity reuse";
            var distant = _gpuTreeMergedGroups[3].Values.Single(g => g.Members!.Any(m => m.Key.Around == 10) && g.Key.Variant == 0);
            var changed = Tile(0, 0, 1, 300);
            changed.Trees[0] = changed.Trees[0] with { SurfaceHeight = 7 };
            _vegetationTiles[first.Key] = changed;
            PrepareRegressionPlan(geometry); if (!DrainRegressionPlan()) return "FAIL: changed tile convergence";
            if (!ReferenceEquals(retained, _gpuTreeGroups[new GpuGroupKey(1, 0, 0)])
                || !_gpuTreeMergedGroups[3].Values.Contains(distant)
                || _gpuTreeRootLookup[1].Group.Roots[_gpuTreeRootLookup[1].Index].SurfaceHeight != 7) return "FAIL: incremental changed tile";
            _vegetationTiles.Remove(third.Key); _visibleVegetationTiles.Remove(third.Key);
            PrepareRegressionPlan(geometry); if (!DrainRegressionPlan() || _gpuTreeRootLookup.Count != 600
                || _gpuTreeTilePartitions.ContainsKey(third.Key)) return "FAIL: removed tile retention";
            RefreshGpuTreeResidentSources(); PrepareRegressionPlan(geometry);
            if (!_gpuTreePreparation!.MoveNext()) return "FAIL: planner did not yield";
            RefreshGpuTreeResidentSources(); if (!DrainRegressionPlan() || !_gpuTreeSourcesDirty) return "FAIL: late refresh was lost";
            long epoch = GpuTreeSourceEpoch;
            var body = new StaticBody3D(); _importedForestBodies[10299] = body;
            MaximumGpuTreeRoots = 256;
            PrepareRegressionPlan(geometry); if (!DrainRegressionPlan() || _gpuTreeRootLookup.Count != 256
                || !_gpuTreeProtectedIds.Contains(10299) || !_gpuTreeRootLookup[10299].Group.HasProtected) return "FAIL: protected cap priority";
            PrepareRegressionPlan(geometry); _gpuTreePreparation!.MoveNext();
            _importedForestBodies.Clear(); _gpuTreeProtectionRevision++;
            epoch = GpuTreeSourceEpoch;
            if (!DrainRegressionPlan() || GpuTreeSourceEpoch != epoch) return "FAIL: stale protection plan published";
            body.Free();
            PrepareRegressionPlan(geometry); CancelGpuTreeSourcePreparation();
            if (_gpuTreePreparation != null || _gpuTreePreparationPlan != null) return "FAIL: cancellation state";
            GlobalPosition = new Vector3(5000, 0, 0);
            var renderer = new MultiMeshInstance3D(); AddChild(renderer);
            _importedForestCollisionRoot = new Node3D(); AddChild(_importedForestCollisionRoot);
            Vector3 previousCollisionPosition = _importedForestCollisionRoot.GlobalPosition;
            var outputKey = new GpuOutputKey(_gpuTreeGroups.Keys.First(), 0);
            _gpuTreeOutputs[outputKey] = new GpuTreeOutput { Renderer = renderer, BatchId = 0, SourceGroup = _gpuTreeGroups[outputKey.Group] };
            Vector3 previousPosition = renderer.GlobalPosition;
            PrepareRegressionPlan(geometry);
            _gpuTreePreparationPlan!.LayerTransform = new Transform3D(GlobalBasis, Vector3.Zero);
            _gpuTreePreparationPlan.RebuildAll = true;
            if (!DrainRegressionPlan() || GlobalPosition.Length() > .01f || renderer.GlobalPosition.DistanceTo(previousPosition) > .01f
                || _importedForestCollisionRoot.GlobalPosition.DistanceTo(previousCollisionPosition) > .01f)
                return "FAIL: normalization moved retained output";
            _gpuTreeOutputs.Clear(); renderer.Free();
            return "PASS: sliced convergence, incremental reuse/change/removal, caps, stale protection, late refresh, cancellation, normalization";
        }
        catch (Exception error) { return "FAIL: " + error.GetType().Name + " " + error.Message; }
    }

    private void PrepareRegressionPlan(RingLodGeometrySnapshot geometry)
    {
        var tiles = new List<VegetationTile>(_vegetationTiles.Values).ToArray();
        var settings = new GpuSourceSettings(MaximumGpuTreeRoots, MaximumGpuTreeBatches, MaximumVegetationRendererNodes, RootEmbedMeters, MergeFarGpuTreeBatches, CellSizeMeters, SpatialTileCells,
            false, MergeMidGpuTreeBatches, EffectiveMidTreeBatchTileSpan);
        var plan = new GpuPreparationPlan { Geometry = geometry, LayerTransform = GlobalTransform, ActualLayerTransform = GlobalTransform, Settings = settings,
            Focus = default, Circumference = geometry.CircumferenceMeters, Edit = _vegetationEditVersion, RefreshRevision = _gpuTreeSourceRefreshRevision,
            ProtectedSnapshot = new HashSet<ulong>(_importedForestBodies.Keys), Previous = _gpuTreeGroups, PreviousMerged = _gpuTreeMergedGroups, Tiles = tiles, VisibleTiles = tiles,
            Partitions = new Dictionary<VegetationTileKey, GpuTilePartition>(), RebuildAll = false, ProtectionRevision = _gpuTreeProtectionRevision };
        _gpuTreePreparationPlan = plan; _gpuTreePreparation = PrepareGpuTreeSources(plan).GetEnumerator();
    }

    private bool DrainRegressionPlan()
    {
        for (int frame = 0; frame < 10000 && _gpuTreePreparation != null; frame++) ServiceGpuTreeSourcePreparation();
        return _gpuTreePreparation == null && _gpuTreePreparationPlan == null;
    }

    private static VegetationTile Tile(long around, long across, ulong id, int count)
    {
        var trees = new List<CachedTree>(count);
        for (int i = 0; i < count; i++) trees.Add(new CachedTree(id + (ulong)i, around * 80 + i / 5, (int)across * 80 + i % 7, 0, 2, 8, 0, i, (float)i / count, default, i % 2));
        return new VegetationTile { Key = new VegetationTileKey(around, across), Trees = trees };
    }
    private static bool Finite(float[] values) { foreach (float value in values) if (float.IsNaN(value) || float.IsInfinity(value)) return false; return true; }
}
