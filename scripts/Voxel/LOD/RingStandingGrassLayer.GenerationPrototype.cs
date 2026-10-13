using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    [Export(PropertyHint.Range, "1,4,1")] public int GpuGenerationPrototypeTileCount { get; set; } = 4;
    public readonly record struct GpuGenerationPrototypeTileResult(long Around, long Across, ulong ResidentPlacementHash, ulong CpuPlacementHash, ulong GpuPlacementHash, bool Supported, bool Parity,
        string Status, int Queries, int CacheBytes, double CpuBaselineMilliseconds, double CpuWarmBaselineMilliseconds,
        double CollectionMilliseconds, double CacheMilliseconds, double CachedCpuMilliseconds, double InitializationMilliseconds, double UploadAndComputeMilliseconds,
        double ReadbackMilliseconds, double ReplayMilliseconds, double CachedCpuReplayMilliseconds, double GpuResolveTotalMilliseconds,
        double HybridMilliseconds, double CachedCpuHybridMilliseconds, int UniqueColumns, int CpuInstances, int ReplayInstances,
        float MaximumPositionError, float MaximumBasisError);
    private sealed record PrototypeWork(RingGrassGpuGenerationPrototype.Input Input, SmoothTerrainVolumetricWorkerContext Context, bool OwnSampler, ulong PlacementHash);
    private readonly object _gpuGenerationPrototypeGate = new();
    private CancellationTokenSource? _gpuGenerationPrototypeCancellation;
    private Task? _gpuGenerationPrototypeTask;
    private GpuGenerationPrototypeTileResult[] _gpuGenerationPrototypeResults = Array.Empty<GpuGenerationPrototypeTileResult>();
    private string _gpuGenerationPrototypeStatus = "Idle";
    private double _gpuGenerationPrototypeRunMilliseconds, _gpuGenerationPrototypeSessionInitializationMilliseconds, _gpuGenerationPrototypeSessionTeardownMilliseconds;
    private double _gpuGenerationPrototypeSnapshotMilliseconds;
    public bool GpuGenerationPrototypeRunning { get { lock (_gpuGenerationPrototypeGate) return _gpuGenerationPrototypeTask is { IsCompleted: false }; } }
    public string GpuGenerationPrototypeStatus { get { lock (_gpuGenerationPrototypeGate) return _gpuGenerationPrototypeStatus; } }
    internal GpuGenerationPrototypeTileResult[] CaptureGpuGenerationPrototypeResults()
    {
        lock (_gpuGenerationPrototypeGate) return (GpuGenerationPrototypeTileResult[])_gpuGenerationPrototypeResults.Clone();
    }

    /// <summary>Builds Godot containers on the caller's main thread from worker-safe result DTOs.</summary>
    public Godot.Collections.Dictionary CaptureGpuGenerationPrototypeDiagnostics()
    {
        var tiles = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (GpuGenerationPrototypeTileResult r in CaptureGpuGenerationPrototypeResults()) tiles.Add(new() {
            ["around"] = r.Around, ["across"] = r.Across, ["resident_placement_hash"] = r.ResidentPlacementHash.ToString("X16"), ["cpu_placement_hash"] = r.CpuPlacementHash.ToString("X16"), ["gpu_placement_hash"] = r.GpuPlacementHash.ToString("X16"), ["supported"] = r.Supported, ["parity"] = r.Parity,
            ["status"] = r.Status, ["queries"] = r.Queries, ["cache_bytes"] = r.CacheBytes,
            ["cpu_baseline_ms"] = r.CpuBaselineMilliseconds, ["cpu_warm_baseline_ms"] = r.CpuWarmBaselineMilliseconds,
            ["collection_ms"] = r.CollectionMilliseconds, ["cache_ms"] = r.CacheMilliseconds, ["cached_cpu_ms"] = r.CachedCpuMilliseconds, ["initialization_ms"] = r.InitializationMilliseconds,
            ["upload_compute_ms"] = r.UploadAndComputeMilliseconds, ["readback_ms"] = r.ReadbackMilliseconds,
            ["replay_ms"] = r.ReplayMilliseconds, ["cached_cpu_replay_ms"] = r.CachedCpuReplayMilliseconds,
            ["gpu_resolve_total_ms"] = r.GpuResolveTotalMilliseconds, ["hybrid_ms"] = r.HybridMilliseconds,
            ["cached_cpu_hybrid_ms"] = r.CachedCpuHybridMilliseconds, ["unique_columns"] = r.UniqueColumns, ["cpu_instances"] = r.CpuInstances,
            ["replay_instances"] = r.ReplayInstances, ["max_position_error"] = r.MaximumPositionError,
            ["max_basis_error"] = r.MaximumBasisError });
        lock (_gpuGenerationPrototypeGate)
            return new() { ["running"] = _gpuGenerationPrototypeTask is { IsCompleted: false }, ["status"] = _gpuGenerationPrototypeStatus,
                ["run_total_ms"] = _gpuGenerationPrototypeRunMilliseconds,
                ["snapshot_preparation_ms"] = _gpuGenerationPrototypeSnapshotMilliseconds,
                ["session_initialization_ms"] = _gpuGenerationPrototypeSessionInitializationMilliseconds,
                ["session_teardown_ms"] = _gpuGenerationPrototypeSessionTeardownMilliseconds, ["tiles"] = tiles };
    }

    /// <summary>Runs a bounded, serial diagnostic over existing residents. It never modifies live placement or render data.</summary>
    public bool StartGpuGenerationPrototype()
    {
        var preparation = System.Diagnostics.Stopwatch.StartNew();
        if (!IsInsideTree() || _shutdown.IsCancellationRequested) return false;
        lock (_gpuGenerationPrototypeGate) if (_gpuGenerationPrototypeTask is { IsCompleted: false }) return false;
        RingTerrainSampler? sampler = _grid?.TerrainSampler; SmoothTerrainVolumetricDensitySnapshot? field = _fieldSnapshot;
        if (sampler == null || field == null) { SetGpuGenerationPrototypeStatus("Unavailable: terrain snapshots are not ready."); return false; }
        RingLodSurfaceEditSnapshot edits = _smooth?.CaptureLodSurfaceEditSnapshot() ?? _grid!.CaptureLodSurfaceEditSnapshot();
        RingLodGeometrySnapshot geometry = _grid!.CaptureLodGeometrySnapshot(); RingGrassExclusionSnapshot? exclusions = CaptureExclusions(sampler);
        RingGrassAppearanceSnapshot appearance = GrassAppearanceProfile?.CapturePlacement() ?? RingGrassAppearanceSnapshot.Default;
        int limit = Mathf.Clamp(GpuGenerationPrototypeTileCount, 1, 4); var selected = new List<(RingGrassTileKey, RingGrassTileBounds)>();
        foreach ((RingGrassTileKey key, _) in _ordered) { if (_tiles.TryGetValue(key, out Resident? resident) && resident.Data.InstanceCount > 0) selected.Add((key, resident.Data.Bounds)); if (selected.Count == limit) break; }
        if (selected.Count == 0) { SetGpuGenerationPrototypeStatus("No nonempty resident grass tiles to sample."); return false; }
        var work = new List<PrototypeWork>(selected.Count);
        try { foreach ((RingGrassTileKey key, RingGrassTileBounds bounds) in selected) { var context = field.CreateWorkerContext(); try { bool ownSampler = context.Sampler == null; RingTerrainSampler workerSampler = context.Sampler ?? sampler.CreateIndependentCopy(); ulong placementHash = _tiles[key].Data.PlacementHash; work.Add(new(new(key, bounds, CellSize, Mathf.Clamp(CandidatesPerCell, 1, 16), Mathf.Clamp(BladesPerSquareMeter, 0, 96), MaximumGrassSourcesPerTile, Mathf.Clamp(MaximumSlopeDegrees, 0, 60), workerSampler, edits, geometry, context.Field, exclusions, appearance, _tiles[key].Data.SourceFraction, _tiles[key].Data.SourceTier, GrassPlacementUnitsPerSource > 1, EnableMeadowTufts && GrassPlacementUnitsPerSource > 1, EffectiveMeadowSpread, GrassMinimumHeightMeters, GrassMaximumHeightMeters, true), context, ownSampler, placementHash)); } catch { context.Dispose(); throw; } } }
        catch { DisposePrototypeWork(work); throw; }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        try
        {
            lock (_gpuGenerationPrototypeGate)
            {
                _gpuGenerationPrototypeCancellation = cancellation;
                _gpuGenerationPrototypeResults = Array.Empty<GpuGenerationPrototypeTileResult>();
                _gpuGenerationPrototypeRunMilliseconds = 0;
                _gpuGenerationPrototypeSessionInitializationMilliseconds = 0;
                _gpuGenerationPrototypeSessionTeardownMilliseconds = 0;
                _gpuGenerationPrototypeSnapshotMilliseconds = preparation.Elapsed.TotalMilliseconds;
                _gpuGenerationPrototypeStatus = $"Running {work.Count} resident tile{(work.Count == 1 ? "" : "s")}…";
                _gpuGenerationPrototypeTask = Task.Run(() => ExecuteGpuGenerationPrototype(work, cancellation));
            }
        }
        catch
        {
            DisposePrototypeWork(work);
            cancellation.Dispose();
            lock (_gpuGenerationPrototypeGate) _gpuGenerationPrototypeCancellation = null;
            throw;
        }
        return true;
    }

    private void ExecuteGpuGenerationPrototype(IReadOnlyList<PrototypeWork> work, CancellationTokenSource cancellation)
    {
        var runTimer = System.Diagnostics.Stopwatch.StartNew();
        GpuGenerationPrototypeTileResult[] results = Array.Empty<GpuGenerationPrototypeTileResult>(); string status;
        double sessionInitializationMilliseconds = 0, sessionTeardownMilliseconds = 0;
        RingGrassGpuGenerationPrototype.Session? session = null;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            session = new RingGrassGpuGenerationPrototype.Session();
            sessionInitializationMilliseconds = session.InitializationMilliseconds;
            results = new GpuGenerationPrototypeTileResult[work.Count];
            for (int i = 0; i < work.Count; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                PrototypeWork item = work[i];
                RingGrassGpuGenerationPrototype.Input input = item.Input;
                RingGrassGpuGenerationPrototype.TileReport r = RingGrassGpuGenerationPrototype.Run(session!, input, cancellation.Token);
                results[i] = new(input.Key.Around, input.Key.Across, item.PlacementHash, r.CpuPlacementHash, r.GpuPlacementHash, r.Supported, r.Parity, r.Status,
                    r.Queries, r.CacheBytes, r.CpuBaselineMilliseconds, r.CpuWarmBaselineMilliseconds, r.CollectionMilliseconds, r.CacheMilliseconds, r.CachedCpuMilliseconds,
                    r.InitializationMilliseconds, r.UploadAndComputeMilliseconds, r.ReadbackMilliseconds,
                    r.ReplayMilliseconds, r.CachedCpuReplayMilliseconds, r.GpuResolveTotalMilliseconds,
                    r.HybridMilliseconds, r.CachedCpuHybridMilliseconds, r.UniqueColumns, r.CpuInstances, r.ReplayInstances,
                    r.MaximumPositionError, r.MaximumBasisError);
            }
            status = $"Completed {results.Length} tile{(results.Length == 1 ? "" : "s")}: {results.Count(r => r.Parity)}/{results.Length} parity.";
        }
        catch (OperationCanceledException) { status = "Cancelled."; }
        catch (Exception error) { status = "Failed: " + error.Message; }
        finally
        {
            if (session != null) { session.Dispose(); sessionTeardownMilliseconds = session.TeardownMilliseconds; }
            DisposePrototypeWork(work);
        }
        runTimer.Stop();
        // Publish before this task returns, so callers never observe stopped with stale results.
        lock (_gpuGenerationPrototypeGate) if (ReferenceEquals(_gpuGenerationPrototypeCancellation, cancellation)) { _gpuGenerationPrototypeResults = results; _gpuGenerationPrototypeStatus = status; _gpuGenerationPrototypeRunMilliseconds = runTimer.Elapsed.TotalMilliseconds; _gpuGenerationPrototypeSessionInitializationMilliseconds = sessionInitializationMilliseconds; _gpuGenerationPrototypeSessionTeardownMilliseconds = sessionTeardownMilliseconds; _gpuGenerationPrototypeCancellation = null; }
        cancellation.Dispose();
    }
    private static void DisposePrototypeWork(IEnumerable<PrototypeWork> work) { foreach (PrototypeWork item in work) { if (item.OwnSampler) item.Input.Sampler.Dispose(); item.Context.Dispose(); } }
    private void SetGpuGenerationPrototypeStatus(string status) { lock (_gpuGenerationPrototypeGate) _gpuGenerationPrototypeStatus = status; }
    private void CancelGpuGenerationPrototype()
    {
        Task? worker;
        lock (_gpuGenerationPrototypeGate)
        {
            worker = _gpuGenerationPrototypeTask;
            if (_gpuGenerationPrototypeCancellation != null)
            {
                _gpuGenerationPrototypeStatus = "Cancelling…";
                _gpuGenerationPrototypeCancellation.Cancel();
            }
        }
        // This worker owns a local RenderingDevice and has no main-thread
        // continuations. Join outside the gate so it can publish and free that
        // device before scene exit allows RenderingServer shutdown to proceed.
        try { worker?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { GD.PushWarning("Grass prototype shutdown: " + error.Message); }
    }
}
