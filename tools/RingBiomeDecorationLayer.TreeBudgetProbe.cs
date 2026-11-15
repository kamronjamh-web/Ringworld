using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// TEST ONLY (work order TR-1, docs/work-orders/terrain-vegetation/TR.md). Read-only probe of the GPU tree budget chain at the current camera:
/// tree records in the streamed tiles -> resident GPU roots (MaximumGpuTreeRoots) -> per-slot distance band -> per-slot rank gate -> per-slot cap -> drawn.
/// The CPU part uses the production edges, caps and gates (GpuTreeLodEdges, GpuTreeLodCaps, GpuTreeLodInterval, VegetationLodRankGate), so it cannot drift from them.
/// The drawn counts come from a readback of each output's indirect command and instance buffer. Nothing here changes selection, buffers or settings.
/// Called by tools/landscape_baseline_common.gd: TreeBudgetProbeBegin(), then poll TreeBudgetProbeTake() until it is not empty.
/// </summary>
public partial class RingBiomeDecorationLayer
{
    private static readonly double[] TreeBudgetProbeBinEdges = { 0, 25, 50, 100, 150, 200, 300, 400, 550, 700, 900, 1200, 1500, 1920, 2500, 3000, 3500, 4000, 4500, 5000, 5500 };
    private Godot.Collections.Dictionary? _treeBudgetProbeResult;
    private int _treeBudgetProbePending;
    private long _treeBudgetProbeSerial;

    private sealed class TreeBudgetProbeSlot
    {
        public int Outputs, VisibleOutputs, ShadowProxies, SelfShadowOutputs;
        public long Drawn, SubmittedTriangles, ShadowTriangles, Capacity, PartialCoverage;
        public long MeshTrianglesMin = long.MaxValue, MeshTrianglesMax;
        public readonly long[] DrawnHistogram = new long[TreeBudgetProbeBinEdges.Length];
    }

    private static int TreeBudgetProbeBin(double distance)
    {
        for (int i = TreeBudgetProbeBinEdges.Length - 1; i >= 0; i--) if (distance >= TreeBudgetProbeBinEdges[i]) return i;
        return 0;
    }

    private static Godot.Collections.Array TreeBudgetProbeArray(long[] values)
    {
        var array = new Godot.Collections.Array();
        foreach (long value in values) array.Add(value);
        return array;
    }

    private static long TreeBudgetProbeMeshTriangles(Mesh? mesh, Dictionary<ulong, long> cache)
    {
        if (mesh == null) return 0;
        ulong id = mesh.GetInstanceId();
        if (cache.TryGetValue(id, out long known)) return known;
        long triangles = 0;
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            if (mesh is ArrayMesh arrayMesh)
            {
                int indices = arrayMesh.SurfaceGetArrayIndexLen(surface);
                triangles += (indices > 0 ? indices : arrayMesh.SurfaceGetArrayLen(surface)) / 3;
            }
            else
            {
                Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surface);
                Variant index = arrays[(int)Mesh.ArrayType.Index];
                triangles += (index.VariantType == Variant.Type.Nil ? arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length : index.AsInt32Array().Length) / 3;
            }
        }
        cache[id] = triangles;
        return triangles;
    }

    /// <summary>Starts a probe. False when the GPU tree path is not running. A probe in flight is replaced.</summary>
    public bool TreeBudgetProbeBegin()
    {
        _treeBudgetProbeResult = null;
        if (_explorer == null || _grid == null || _gpuTreeCompute == null || !_gpuTreeCompute.Ready) return false;
        long serial = ++_treeBudgetProbeSerial;
        RingCanonicalPosition focus = _explorer.CanonicalPosition;
        Vector3 cameraWorld = _explorer.GlobalPosition;
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        float[] edges = GpuTreeLodEdges();
        uint[] caps = GpuTreeLodCaps();
        float[] gates = { VegetationLodRankGate(1), VegetationLodRankGate(2), EffectiveCompactTreeLodProfile ? 1f : VegetationLodRankGate(3), VegetationLodRankGate(4), VegetationLodRankGate(5) };
        double farEdge = edges[^1];
        int bins = TreeBudgetProbeBinEdges.Length;
        var near = new float[5]; var far = new float[5]; var fadeIn = new Vector2[5]; var fadeOut = new Vector2[5];
        for (int lod = 0; lod < 5; lod++)
        {
            GpuTreeLodInterval(lod, edges, out near[lod], out far[lod], out float inStart, out float inEnd, out float outStart, out float outEnd);
            fadeIn[lod] = new Vector2(inStart, inEnd); fadeOut[lod] = new Vector2(outStart, outEnd);
        }

        // 1. Resident GPU roots (after MaximumGpuTreeRoots), with the shader's own distance: metadata zw against the group's canonical camera.
        var rootIds = new HashSet<ulong>();
        long[] rootHistogram = new long[bins];
        long[] rootsInRange = new long[5], gatedInRange = new long[5], protectedInRange = new long[5];
        long[][] gatedHistogram = new long[5][];
        for (int lod = 0; lod < 5; lod++) gatedHistogram[lod] = new long[bins];
        long rootsWithinFarEdge = 0, rootCount = 0;
        double farthestRoot = 0;
        foreach (GpuTreeGroup group in _gpuTreeGroups.Values)
        {
            Vector2 camera = CanonicalCamera(group, focus, circumference);
            for (int i = 0; i < group.Metadata.Length; i++)
            {
                Vector4 meta = group.Metadata[i];
                double dx = meta.Z - camera.X, dz = meta.W - camera.Y, distance = Math.Sqrt(dx * dx + dz * dz);
                rootCount++;
                if (i < group.Roots.Length) rootIds.Add(group.Roots[i].Id);
                rootHistogram[TreeBudgetProbeBin(distance)]++;
                if (distance < farEdge) rootsWithinFarEdge++;
                farthestRoot = Math.Max(farthestRoot, distance);
                bool isProtected = meta.Y > .5f;
                for (int lod = 0; lod < 5; lod++)
                {
                    if (!IsGpuTreeLodEnabled(lod) || distance < near[lod] || distance >= far[lod]) continue;
                    rootsInRange[lod]++;
                    if (isProtected) protectedInRange[lod]++;
                    if (isProtected || meta.X <= gates[lod]) { gatedInRange[lod]++; gatedHistogram[lod][TreeBudgetProbeBin(distance)]++; }
                }
            }
        }

        // 2. Records in the streamed tiles: everything cached, and the visible (frustum plus behind-camera) set that source preparation selects from.
        long cacheRecords = 0, visibleRecords = 0, visibleWithinFarEdge = 0, visibleNotRoot = 0, visibleNotRootWithinFarEdge = 0;
        long[] visibleHistogram = new long[bins], droppedHistogram = new long[bins], visibleInRange = new long[5];
        double nearestDropped = double.PositiveInfinity;
        int visibleTiles = 0;
        foreach (VegetationTile tile in _vegetationTiles.Values)
        {
            bool visible = _visibleVegetationTiles.Contains(tile.Key);
            if (visible) visibleTiles++;
            foreach (CachedTree tree in tile.Trees)
            {
                if (tree.Variant < 0 || tree.Variant >= ImportedTreeCatalog.VariantCount) continue;
                cacheRecords++;
                if (!visible) continue;
                double da = WrappedVegetationDelta(tree.Around - focus.AroundMeters, circumference), dz = tree.Across - focus.AcrossMeters;
                double distance = Math.Sqrt(da * da + dz * dz);
                visibleRecords++;
                visibleHistogram[TreeBudgetProbeBin(distance)]++;
                if (distance < farEdge) visibleWithinFarEdge++;
                for (int lod = 0; lod < 5; lod++)
                    if (IsGpuTreeLodEnabled(lod) && distance >= near[lod] && distance < far[lod]) visibleInRange[lod]++;
                if (rootIds.Contains(tree.Id)) continue;
                visibleNotRoot++;
                droppedHistogram[TreeBudgetProbeBin(distance)]++;
                if (distance < farEdge) visibleNotRootWithinFarEdge++;
                nearestDropped = Math.Min(nearestDropped, distance);
            }
        }
        long rootsBeyondNearestDropped = 0;
        if (!double.IsInfinity(nearestDropped))
            foreach (GpuTreeGroup group in _gpuTreeGroups.Values)
            {
                Vector2 camera = CanonicalCamera(group, focus, circumference);
                for (int i = 0; i < group.Metadata.Length; i++)
                {
                    double dx = group.Metadata[i].Z - camera.X, dz = group.Metadata[i].W - camera.Y;
                    if (Math.Sqrt(dx * dx + dz * dz) > nearestDropped) rootsBeyondNearestDropped++;
                }
            }

        var binEdges = new Godot.Collections.Array(); foreach (double edge in TreeBudgetProbeBinEdges) binEdges.Add(edge);
        var edgeArray = new Godot.Collections.Array(); foreach (float edge in edges) edgeArray.Add(edge);
        var capArray = new Godot.Collections.Array(); foreach (uint cap in caps) capArray.Add((long)cap);
        var gateArray = new Godot.Collections.Array(); foreach (float gate in gates) gateArray.Add(gate);
        var work = new Godot.Collections.Dictionary
        {
            ["ok"] = true, ["focus_around_m"] = focus.AroundMeters, ["focus_across_m"] = focus.AcrossMeters,
            ["compact_profile"] = EffectiveCompactTreeLodProfile, ["shared_far_impostors"] = EffectiveSharedFarTreeImpostors,
            ["merge_far"] = MergeFarGpuTreeBatches, ["merge_mid"] = MergeMidGpuTreeBatches, ["mid_span"] = EffectiveMidTreeBatchTileSpan,
            ["maximum_gpu_tree_roots"] = MaximumGpuTreeRoots, ["maximum_gpu_tree_batches"] = MaximumGpuTreeBatches,
            ["tree_sun_shadow_distance_m"] = TreeSunShadowDistanceMeters, ["maximum_tree_sun_shadow_batches"] = MaximumTreeSunShadowBatches,
            ["edges_m"] = edgeArray, ["slot_caps"] = capArray, ["slot_rank_gates"] = gateArray, ["far_edge_m"] = farEdge, ["bin_edges_m"] = binEdges,
            ["cache_records"] = cacheRecords, ["cache_tiles"] = _vegetationTiles.Count, ["visible_tiles"] = visibleTiles,
            ["visible_records"] = visibleRecords, ["visible_records_within_far_edge"] = visibleWithinFarEdge,
            ["gpu_roots"] = rootCount, ["gpu_roots_within_far_edge"] = rootsWithinFarEdge, ["gpu_groups"] = _gpuTreeGroups.Count,
            ["root_cap"] = new Godot.Collections.Dictionary
            {
                ["visible_not_root"] = visibleNotRoot, ["visible_not_root_within_far_edge"] = visibleNotRootWithinFarEdge,
                ["nearest_dropped_m"] = double.IsInfinity(nearestDropped) ? -1.0 : nearestDropped, ["farthest_root_m"] = farthestRoot,
                ["roots_beyond_nearest_dropped"] = rootsBeyondNearestDropped
            },
            ["histogram_visible"] = TreeBudgetProbeArray(visibleHistogram), ["histogram_roots"] = TreeBudgetProbeArray(rootHistogram),
            ["histogram_visible_not_root"] = TreeBudgetProbeArray(droppedHistogram),
            ["gpu_buffer_bytes"] = GpuTreeResidentBytes, ["gpu_batches"] = GpuTreeBatchCount, ["gpu_fallback_batches"] = GpuTreeFallbackBatchCount,
            ["culling_settled"] = GpuTreeCullingSettled, ["preparation_pending"] = TreePreparationPending,
            ["selection_truncated"] = _gpuTreeSelectionTruncated, ["wanted_outputs"] = _gpuTreeWanted.Count
        };

        // 3. Drawn instances per slot: the indirect command count and the instance rows of every installed output.
        var slots = new TreeBudgetProbeSlot[5];
        for (int lod = 0; lod < 5; lod++) slots[lod] = new TreeBudgetProbeSlot();
        var meshTriangles = new Dictionary<ulong, long>();
        void Finish()
        {
            if (serial != _treeBudgetProbeSerial) return;
            var slotArray = new Godot.Collections.Array();
            for (int lod = 0; lod < 5; lod++)
            {
                TreeBudgetProbeSlot slot = slots[lod];
                slotArray.Add(new Godot.Collections.Dictionary
                {
                    ["slot"] = lod, ["enabled"] = IsGpuTreeLodEnabled(lod), ["near_m"] = near[lod], ["far_m"] = far[lod],
                    ["fade_in_m"] = fadeIn[lod], ["fade_out_m"] = fadeOut[lod], ["rank_gate"] = gates[lod], ["cap"] = (long)caps[lod],
                    ["visible_in_range"] = visibleInRange[lod], ["roots_in_range"] = rootsInRange[lod], ["protected_in_range"] = protectedInRange[lod],
                    ["gated_in_range"] = gatedInRange[lod], ["outputs"] = slot.Outputs, ["visible_outputs"] = slot.VisibleOutputs, ["output_capacity"] = slot.Capacity,
                    ["drawn"] = slot.Drawn, ["drawn_partial_coverage"] = slot.PartialCoverage, ["submitted_triangles"] = slot.SubmittedTriangles,
                    ["mesh_triangles_min"] = slot.MeshTrianglesMin == long.MaxValue ? 0 : slot.MeshTrianglesMin, ["mesh_triangles_max"] = slot.MeshTrianglesMax,
                    ["shadow_proxies"] = slot.ShadowProxies, ["self_shadow_outputs"] = slot.SelfShadowOutputs, ["shadow_triangles"] = slot.ShadowTriangles,
                    ["histogram_gated"] = TreeBudgetProbeArray(gatedHistogram[lod]), ["histogram_drawn"] = TreeBudgetProbeArray(slot.DrawnHistogram)
                });
            }
            work["slots"] = slotArray;
            _treeBudgetProbeResult = work;
        }
        _treeBudgetProbePending = _gpuTreeOutputs.Count;
        if (_treeBudgetProbePending == 0) { Finish(); return true; }
        foreach ((GpuOutputKey key, GpuTreeOutput output) in _gpuTreeOutputs)
        {
            int lod = Mathf.Clamp(key.Lod, 0, 4);
            TreeBudgetProbeSlot slot = slots[lod];
            Transform3D transform = output.Renderer.GlobalTransform;
            int stride = output.TreeDitherData ? RingGpuVegetationCompute.TreeCustomFloats : output.SourceGroup.RowStride;
            bool dither = output.TreeDitherData;
            int capacity = output.SourceGroup.RootCount;
            long triangles = TreeBudgetProbeMeshTriangles(output.Renderer.Multimesh?.Mesh, meshTriangles);
            bool rendererVisible = output.Renderer.Visible;
            bool selfShadow = output.Renderer.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off;
            long shadowMeshTriangles = 0;
            if (_gpuTreeShadowProxies.TryGetValue(output.BatchId, out GpuTreeShadowProxy? proxy) && GodotObject.IsInstanceValid(proxy.Renderer) && proxy.Ready && proxy.Renderer.Visible)
                shadowMeshTriangles = TreeBudgetProbeMeshTriangles(proxy.Renderer.Multimesh?.Mesh, meshTriangles);
            bool hasProxy = shadowMeshTriangles > 0;
            slot.Outputs++; slot.Capacity += capacity;
            if (rendererVisible) slot.VisibleOutputs++;
            if (hasProxy) slot.ShadowProxies++;
            if (selfShadow) slot.SelfShadowOutputs++;
            slot.MeshTrianglesMin = Math.Min(slot.MeshTrianglesMin, triangles); slot.MeshTrianglesMax = Math.Max(slot.MeshTrianglesMax, triangles);
            _gpuTreeCompute.RequestReadbackForTests(output.BatchId, (commands, rows) =>
            {
                if (serial != _treeBudgetProbeSerial) return;
                long count = commands.Length >= 2 ? Math.Min(commands[1], (uint)capacity) : 0;
                if (stride > 0) count = Math.Min(count, rows.Length / stride);
                if (rendererVisible)
                {
                    slot.Drawn += count; slot.SubmittedTriangles += count * triangles;
                    if (hasProxy) slot.ShadowTriangles += count * shadowMeshTriangles;
                    else if (selfShadow) slot.ShadowTriangles += count * triangles;
                    for (int i = 0; i < count; i++)
                    {
                        int b = i * stride;
                        Vector3 world = transform * new Vector3(rows[b + 3], rows[b + 7], rows[b + 11]);
                        slot.DrawnHistogram[TreeBudgetProbeBin(world.DistanceTo(cameraWorld))]++;
                        if (dither && stride >= 16 && MathF.Abs(rows[b + 13]) < 1f) slot.PartialCoverage++;
                    }
                }
                if (--_treeBudgetProbePending <= 0) Finish();
            });
        }
        return true;
    }

    /// <summary>
    /// Renderer instance id -> slot for every installed GPU tree output (a shadow proxy maps to slot + 16, a retained fallback output to slot + 32). The hide cases
    /// use this, not node names: an output recreated while its predecessor is still in the tree is renamed by Godot to "@MultiMeshInstance3D@N".
    /// </summary>
    public Godot.Collections.Dictionary TreeBudgetProbeOutputSlots()
    {
        var result = new Godot.Collections.Dictionary();
        foreach ((GpuOutputKey key, GpuTreeOutput output) in _gpuTreeOutputs)
        {
            if (!GodotObject.IsInstanceValid(output.Renderer)) continue;
            result[(long)output.Renderer.GetInstanceId()] = key.Lod;
            if (_gpuTreeShadowProxies.TryGetValue(output.BatchId, out GpuTreeShadowProxy? proxy) && GodotObject.IsInstanceValid(proxy.Renderer))
                result[(long)proxy.Renderer.GetInstanceId()] = key.Lod + 16;
        }
        foreach ((GpuOutputKey key, GpuTreeOutput output) in _gpuTreeFallbackOutputs)
            if (GodotObject.IsInstanceValid(output.Renderer)) result[(long)output.Renderer.GetInstanceId()] = key.Lod + 32;
        return result;
    }

    /// <summary>The finished probe, or an empty dictionary while readbacks are in flight. Reading it clears it.</summary>
    public Godot.Collections.Dictionary TreeBudgetProbeTake()
    {
        if (_treeBudgetProbeResult == null) return new Godot.Collections.Dictionary();
        Godot.Collections.Dictionary result = _treeBudgetProbeResult;
        _treeBudgetProbeResult = null;
        return result;
    }
}
