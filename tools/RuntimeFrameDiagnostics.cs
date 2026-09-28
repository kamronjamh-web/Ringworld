using System;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo;

/// <summary>
/// Test-only per-frame managed-runtime counters for travel benchmark reports.
/// The timestamp deliberately uses the same Stopwatch clock as performance
/// telemetry events so GC pauses and allocations can be correlated directly.
/// </summary>
public partial class RuntimeFrameDiagnostics : RefCounted
{
    public Godot.Collections.Dictionary CaptureGlobalTerrainCounters(Node node)
    {
        if (node is not RingTerrainGlobalLod terrain)
            throw new ArgumentException("Expected the global terrain node.", nameof(node));
        return new()
        {
            ["active_segments"] = terrain.ActiveSegmentCount,
            ["pending_initial_segments"] = terrain.PendingInitialSegmentCount,
            ["pending_edited_segments"] = terrain.PendingSegmentRebuildCount,
            ["pending_vertex_samples"] = terrain.PendingSegmentSampleCount,
            ["sampling_worker_pending"] = terrain.GlobalSegmentSamplingWorkerPending,
            ["last_initial_build_ms"] = terrain.LastInitialSegmentBuildMilliseconds,
            ["max_initial_build_ms"] = terrain.MaxInitialSegmentBuildMilliseconds,
            ["around_segments"] = terrain.AroundSegments,
            ["across_segments"] = terrain.AcrossSegments,
            ["preparation_budget_ms"] = terrain.MaxSegmentRebuildMillisecondsPerFrame,
            ["estimated_mesh_bytes"] = terrain.EstimatedGlobalMeshBytes
        };
    }

    public Godot.Collections.Dictionary CaptureCounters() => new()
    {
        ["stopwatch_seconds"] = RingPerformanceTelemetry.NowSeconds(),
        ["allocated_bytes"] = GC.GetTotalAllocatedBytes(false),
        ["total_gc_pause_ms"] = GC.GetTotalPauseDuration().TotalMilliseconds,
        ["gen0_collections"] = GC.CollectionCount(0),
        ["gen1_collections"] = GC.CollectionCount(1),
        ["gen2_collections"] = GC.CollectionCount(2),
        ["proxy_main_thread_allocated_bytes_since_startup"] = RingTerrainProxySector.MainThreadAllocatedBytesSinceStartupAllLevels,
        ["proxy_worker_allocated_bytes_since_startup"] = RingTerrainProxySector.WorkerAllocatedBytesSinceStartup,
        ["structural_worker_allocated_bytes_since_startup"] = RingTerrainProxySector.StructuralWorkerAllocatedBytesSinceStartup,
        ["maximum_structural_wall_requested_around_cells_since_startup"] = RingTerrainProxySector.MaximumStructuralWallRequestedAroundCellsSinceStartup
    };
}
