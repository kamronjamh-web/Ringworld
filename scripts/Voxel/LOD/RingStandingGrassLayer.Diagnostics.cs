using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Opt-in, bounded timestamps for diagnosing grass generation latency.
/// All collection occurs on the main thread after worker completion. GPU readiness
/// is an observed compute-dispatch/draw-handoff state, never a hardware completion timestamp.</summary>
public partial class RingStandingGrassLayer
{
    private const int DefaultMaximumDiagnosticRecords = 4096;
    private sealed class GenerationTrace
    {
        public required RingGrassTileKey Key;
        public long Session;
        public string Phase = "";
        public string Outcome = "requested";
        public long RequestTick, PreparationStartTick, PreparationEndTick, TaskQueuedTick, WorkerStartTick, WorkerFinishTick;
        public long DrainTick, ReadyTick, InstallStartTick, InstallFinishTick, GpuRegistrationStartTick, GpuRegistrationTick, GpuReadyTick;
        public double WorkerWallMilliseconds;
        public int InstanceCount;
        public ulong PlacementHash;
        public long GpuCompletedUpdatesAtReady;
    }

    private readonly Dictionary<RingGrassTileKey, GenerationTrace> _generationTraces = new();
    private readonly List<GenerationTrace> _generationTraceRecords = new();
    private readonly Queue<GenerationTrace> _generationTraceOrder = new();
    private bool _generationDiagnosticsEnabled;
    private string _generationDiagnosticPhase = "";
    private int _generationDiagnosticLimit = DefaultMaximumDiagnosticRecords;
    private int _generationDiagnosticEvictions;
    private long _generationDiagnosticSession;
    private int _generationRootBudgetBlocked;
    private readonly HashSet<RingGrassTileKey> _generationRootBudgetBlockedKeys = new();

    public void BeginGenerationDiagnostics(string phase = "")
    {
        _generationDiagnosticsEnabled = true;
        _generationDiagnosticPhase = phase ?? "";
        _generationTraces.Clear();
        _generationTraceRecords.Clear();
        _generationTraceOrder.Clear();
        _generationDiagnosticEvictions = 0;
        _generationRootBudgetBlocked = 0;
        _generationRootBudgetBlockedKeys.Clear();
        _generationDiagnosticSession++;
    }

    public void EndGenerationDiagnostics() => _generationDiagnosticsEnabled = false;

    public void BeginGrassStreamingDiagnostics(bool resetTiles = true, int maxRecords = DefaultMaximumDiagnosticRecords)
    {
        BeginGenerationDiagnostics("grass_streaming");
        _generationDiagnosticLimit = Math.Clamp(maxRecords, 1, DefaultMaximumDiagnosticRecords);
        if (resetTiles) Reset();
    }
    public void EndGrassStreamingDiagnostics() => EndGenerationDiagnostics();
    public bool IsGrassStreamingDiagnosticsSettled
    {
        get
        {
            if (_wanted.Count == 0 || _requests.Count != 0 || _jobs.Count != 0 || _uploads.Count != 0) return false;
            if (UseGpuCulling && (_gpuCompute == null || !_gpuCompute.Ready || _gpuCompute.Failed)) return false;
            bool budgetFull = ResidentGrassInstanceCount >= MaximumGrassSourceInstances;
            foreach (RingGrassTileKey key in _wanted)
            {
                if (!_tiles.TryGetValue(key, out Resident? resident)) { if (budgetFull) continue; return false; }
                if (resident.Renderer == null) return false;
                // Zero-root tiles are intentionally not registered for an indirect draw.
                if (resident.Data.InstanceCount == 0) continue;
                if (!UseGpuCulling || _gpuCompute == null) continue;
                if (resident.NearWanted && (resident.GpuNearBatch < 0 || !_gpuCompute.IsBatchReady(resident.GpuNearBatch))) return false;
                if (resident.IntermediateWanted && (resident.GpuIntermediateBatch < 0 || !_gpuCompute.IsBatchReady(resident.GpuIntermediateBatch))) return false;
            }
            return true;
        }
    }

    /// <summary>Clears resident presentation and queues the normal scheduler to rebuild on its next step.</summary>
    public void ResetForDiagnostics()
    {
        if (!_generationDiagnosticsEnabled) return;
        Reset();
    }

    public Godot.Collections.Dictionary CaptureGenerationDiagnostics()
    {
        Godot.Collections.Dictionary surfaceCache = SurfaceCacheDiagnostics;
        var records = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (GenerationTrace trace in _generationTraceRecords)
        {
            records.Add(new Godot.Collections.Dictionary
            {
                ["around"] = trace.Key.Around, ["across"] = trace.Key.Across, ["phase"] = trace.Phase,
                ["outcome"] = trace.Outcome, ["request_tick"] = trace.RequestTick,
                ["preparation_start_tick"] = trace.PreparationStartTick, ["preparation_end_tick"] = trace.PreparationEndTick,
                ["task_queued_tick"] = trace.TaskQueuedTick, ["worker_start_tick"] = trace.WorkerStartTick,
                ["worker_finish_tick"] = trace.WorkerFinishTick, ["drain_tick"] = trace.DrainTick,
                ["ready_tick"] = trace.ReadyTick, ["install_start_tick"] = trace.InstallStartTick,
                ["install_finish_tick"] = trace.InstallFinishTick, ["gpu_registration_start_tick"] = trace.GpuRegistrationStartTick, ["gpu_registration_tick"] = trace.GpuRegistrationTick,
                ["gpu_ready_tick"] = trace.GpuReadyTick, ["gpu_dispatch_ready_observed_tick"] = trace.GpuReadyTick, ["worker_wall_ms"] = trace.WorkerWallMilliseconds,
                ["instance_count"] = trace.InstanceCount, ["placement_hash"] = trace.PlacementHash.ToString(),
                ["gpu_completed_updates_at_ready"] = trace.GpuCompletedUpdatesAtReady
            });
        }
        return new Godot.Collections.Dictionary
        {
            ["enabled"] = _generationDiagnosticsEnabled, ["phase"] = _generationDiagnosticPhase,
            ["timestamp_frequency"] = Stopwatch.Frequency, ["record_count"] = records.Count,
            ["evicted_records"] = _generationDiagnosticEvictions,
            ["pending_requests"] = _requests.Count, ["active_jobs"] = _jobs.Count, ["pending_uploads"] = _uploads.Count,
            ["resident_tiles"] = _tiles.Count, ["gpu_batches"] = GpuCullingBatchCount,
            ["gpu_requested_batches"] = GpuCullingRequestedBatchCount,
            ["gpu_dispatched_batches"] = GpuCullingDispatchedBatchCount,
            ["gpu_deferred_batches"] = GpuCullingDeferredBatchCount,
            ["gpu_buffer_bytes"] = GpuCullingBytes, ["gpu_shared_source_bytes"] = GpuCullingSharedSourceBytes,
            ["gpu_output_bytes"] = GpuCullingOutputBytes, ["gpu_shared_source_count"] = GpuCullingSharedSourceCount,
            ["gpu_completed_updates"] = GpuCullingCompletedUpdates, ["root_budget_blocked"] = _generationRootBudgetBlocked, ["root_budget_blocked_current"] = _generationRootBudgetBlockedKeys.Count,
            ["surface_cache_byte_cap"] = surfaceCache["byte_cap"], ["surface_cache_estimated_bytes"] = surfaceCache["estimated_bytes"],
            ["surface_cache_entries"] = surfaceCache["entries"], ["surface_cache_hits"] = surfaceCache["hits"],
            ["surface_cache_misses"] = surfaceCache["misses"], ["surface_cache_evictions"] = surfaceCache["evictions"], ["records"] = records
        };
    }
    public Godot.Collections.Dictionary CaptureGrassStreamingDiagnostics()
    {
        Godot.Collections.Dictionary result = CaptureGenerationDiagnostics();
        result["settled"] = IsGrassStreamingDiagnosticsSettled;
        result["settings"] = new Godot.Collections.Dictionary
        {
            ["enabled"] = Enabled, ["density"] = BladesPerSquareMeter, ["visibility_radius"] = VisibilityRadiusMeters, ["spatial_tile_meters"] = SpatialTileMeters, ["effective_tile_meters"] = TileSize,
            ["near_distance"] = LodNearDistanceMeters, ["mid_distance"] = LodMidDistanceMeters, ["far_distance"] = LodFarDistanceMeters,
            ["hierarchical_coverage"] = EnableHierarchicalCoverage,
            ["mid_source_density"] = LodMidSourceDensity, ["far_source_density"] = LodFarSourceDensity,
            ["transition_width"] = LodTransitionWidthMeters, ["near_density"] = LodNearDensity, ["far_density"] = LodFarDensity, ["distance_lod"] = EnableDistanceLod,
            ["maximum_instances"] = MaximumInstances, ["maximum_instances_per_tile"] = MaximumInstancesPerTile,
            ["grass_blades_per_source"] = GrassBladesPerSource,
            ["maximum_grass_source_instances"] = MaximumGrassSourceInstances,
            ["maximum_grass_sources_per_tile"] = MaximumGrassSourcesPerTile,
            ["use_clumps"] = UseGrassClumps,
            ["cull_tiles"] = CullGrassTiles,
            ["group_compute_phases"] = GroupGrassComputePhases,
            ["maximum_resident_tiles"] = MaximumResidentTiles, ["maximum_builds_in_flight"] = MaximumTileBuildsInFlight, ["maximum_uploads_per_frame"] = MaximumTileUploadsPerFrame,
            ["maximum_upload_ms"] = MaximumTileUploadMilliseconds, ["maximum_gpu_batches_per_frame"] = MaximumGpuCullingBatchesPerFrame,
            ["surface_cache_megabytes"] = SurfaceCacheMegabytes, ["gpu_culling"] = UseGpuCulling,
            ["grassland_biome_density"] = GrasslandBiomeDensity, ["forest_biome_density"] = ForestBiomeDensity, ["sparse_biome_density"] = SparseBiomeDensity
        };
        result["wanted_tiles"] = _wanted.Count;
        result["planning_tiles_in_radius"] = _ordered.Count;
        result["unbuilt_wanted_tiles"] = _wanted.Count - _wanted.Count(key => _tiles.ContainsKey(key));
        result["resident_roots"] = ResidentGrassInstanceCount;
        result["resident_near_roots"] = ResidentGrassNearRootCount;
        result["resident_mid_roots"] = ResidentGrassMidRootCount;
        result["resident_far_roots"] = ResidentGrassFarRootCount;
        result["tier_rebuilds"] = GrassTierRebuildCount;
        result["idle_at_root_budget"] = _requests.Count == 0 && _jobs.Count == 0 && _uploads.Count == 0 && ResidentGrassInstanceCount >= MaximumGrassSourceInstances;
        return result;
    }
    public Godot.Collections.Array<Godot.Collections.Dictionary> CaptureGrassStreamingDiagnosticResidents()
    {
        var residents = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var pair in _tiles.OrderBy(pair => pair.Key.Around).ThenBy(pair => pair.Key.Across))
            residents.Add(new Godot.Collections.Dictionary { ["around"] = pair.Key.Around, ["across"] = pair.Key.Across,
                ["instance_count"] = pair.Value.Data.InstanceCount, ["placement_hash"] = pair.Value.Data.PlacementHash.ToString() });
        return residents;
    }

    private long DiagnosticTimestamp() => _generationDiagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
    private void DiagnosticRequested(RingGrassTileKey key)
    {
        if (!_generationDiagnosticsEnabled) return;
        if (_generationTraces.TryGetValue(key, out GenerationTrace? current) && current.Outcome is "requested" or "ready" or "root_budget_reached") return;
        while (_generationTraceRecords.Count >= _generationDiagnosticLimit && _generationTraceOrder.Count > 0)
        {
            GenerationTrace evicted = _generationTraceOrder.Dequeue(); _generationTraceRecords.Remove(evicted);
            if (_generationTraces.TryGetValue(evicted.Key, out GenerationTrace? active) && ReferenceEquals(active, evicted)) _generationTraces.Remove(evicted.Key);
            _generationDiagnosticEvictions++;
        }
        var trace = new GenerationTrace { Key = key, Session = _generationDiagnosticSession, Phase = _generationDiagnosticPhase, RequestTick = Stopwatch.GetTimestamp() };
        _generationTraces[key] = trace; _generationTraceRecords.Add(trace); _generationTraceOrder.Enqueue(trace);
    }
    private GenerationTrace? DiagnosticTrace(RingGrassTileKey key)
        => _generationDiagnosticsEnabled && _generationTraces.TryGetValue(key, out GenerationTrace? trace) ? trace : null;
    private void DiagnosticOutcome(RingGrassTileKey key, string outcome) { GenerationTrace? trace = DiagnosticTrace(key); if (trace != null) trace.Outcome = outcome; }
    private void DiagnosticRootBudgetBlocked(RingGrassTileKey key, IEnumerable<RingGrassTileKey> remaining)
    {
        if (!_generationDiagnosticsEnabled) return;
        if (_generationRootBudgetBlockedKeys.Add(key)) _generationRootBudgetBlocked++;
        foreach (RingGrassTileKey pending in remaining) if (_generationRootBudgetBlockedKeys.Add(pending)) _generationRootBudgetBlocked++;
    }
    private void DiagnosticStarted(RingGrassTileKey key) => _generationRootBudgetBlockedKeys.Remove(key);
    private void DiagnosticOutcome(RingGrassTileKey key, Job job, string outcome)
    {
        GenerationTrace? trace = job.DiagnosticTrace;
        if (trace != null && trace.Session == _generationDiagnosticSession && _generationTraces.TryGetValue(key, out GenerationTrace? active) && ReferenceEquals(active, trace)) trace.Outcome = outcome;
    }
    private void DiagnosticDrained(RingGrassTileKey key, Job job)
    {
        GenerationTrace? trace = job.DiagnosticTrace;
        if (trace == null || trace.Session != _generationDiagnosticSession || !_generationTraces.TryGetValue(key, out GenerationTrace? active) || !ReferenceEquals(active, trace)) return;
        trace.PreparationStartTick = job.PreparationStartTick; trace.PreparationEndTick = job.PreparationEndTick;
        trace.TaskQueuedTick = job.TaskQueuedTick; trace.WorkerStartTick = job.WorkerStartTick;
        trace.WorkerFinishTick = job.WorkerFinishTick; trace.WorkerWallMilliseconds = job.WorkerMilliseconds;
        trace.DrainTick = Stopwatch.GetTimestamp();
    }
    private void DiagnosticReady(RingGrassTileKey key, Job job, RingGrassTileData data)
    {
        GenerationTrace? trace = job.DiagnosticTrace;
        if (trace == null || trace.Session != _generationDiagnosticSession || !_generationTraces.TryGetValue(key, out GenerationTrace? active) || !ReferenceEquals(active, trace)) return;
        trace.ReadyTick = Stopwatch.GetTimestamp(); trace.Outcome = "ready";
        trace.InstanceCount = data.InstanceCount; trace.PlacementHash = data.PlacementHash;
    }
    private void DiagnosticInstalled(RingGrassTileKey key, RingGrassTileData data, long start, long finish)
    {
        GenerationTrace? trace = DiagnosticTrace(key);
        if (trace == null) return;
        trace.InstallStartTick = start; trace.InstallFinishTick = finish; trace.InstanceCount = data.InstanceCount;
        trace.PlacementHash = data.PlacementHash; trace.Outcome = "installed";
    }
    private void DiagnosticGpuRegistered(RingGrassTileKey key, Resident resident, long start)
    {
        GenerationTrace? trace = DiagnosticTrace(key);
        if (trace != null && (resident.GpuNearBatch >= 0 || resident.GpuIntermediateBatch >= 0)) { trace.GpuRegistrationStartTick = start; trace.GpuRegistrationTick = Stopwatch.GetTimestamp(); }
    }
    private void DiagnosticGpuReadiness()
    {
        if (!_generationDiagnosticsEnabled || _gpuCompute == null) return;
        foreach ((RingGrassTileKey key, Resident resident) in _tiles)
        {
            GenerationTrace? trace = DiagnosticTrace(key);
            if (trace == null || trace.GpuRegistrationTick == 0 || trace.GpuReadyTick != 0) continue;
            bool nearReady = !resident.NearWanted || (resident.GpuNearBatch >= 0 && _gpuCompute.IsBatchReady(resident.GpuNearBatch));
            bool intermediateReady = !resident.IntermediateWanted || (resident.GpuIntermediateBatch >= 0 && _gpuCompute.IsBatchReady(resident.GpuIntermediateBatch));
            if (nearReady && intermediateReady)
            { trace.GpuReadyTick = Stopwatch.GetTimestamp(); trace.GpuCompletedUpdatesAtReady = _gpuCompute.CompletedUpdates; trace.Outcome = "wanted_gpu_passes_ready"; }
        }
    }
}
