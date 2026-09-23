using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase1;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.Phase2;

public partial class VoxelChunkGridManager
{
    private readonly record struct ResidentVoxelRebuildResult(
        RingChunkCoordinate Coordinate,
        ulong Epoch,
        ulong Generation,
        MeshBuildData Mesh,
        double WorkerMilliseconds,
        bool Canceled,
        string Error);

    private readonly ConcurrentQueue<ResidentVoxelRebuildResult> _completedResidentVoxelRebuilds = new();
    private readonly Dictionary<RingChunkCoordinate, ulong> _residentVoxelRebuildGenerations = new();
    private readonly Dictionary<RingChunkCoordinate, ulong> _pendingResidentVoxelRebuilds = new();

    private void MarkResidentVoxelRebuildRequested(in RingChunkCoordinate coordinate)
    {
        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        _residentVoxelRebuildGenerations.TryGetValue(canonical, out ulong generation);
        _residentVoxelRebuildGenerations[canonical] = generation + 1;
    }

    private void InvalidateResidentVoxelRebuild(in RingChunkCoordinate coordinate)
    {
        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        MarkResidentVoxelRebuildRequested(canonical);
        _pendingResidentVoxelRebuilds.Remove(canonical);
    }

    private bool TryScheduleResidentVoxelRebuild(in RingChunkCoordinate coordinate)
    {
        if (!EnableAsyncVoxelMeshing
            || _voxelMeshShuttingDown
            || !HasAsyncVoxelWorkerCapacity())
        {
            return false;
        }

        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        if (!_chunks.TryGetValue(canonical, out VoxelChunkNode? chunk)
            || _pendingResidentVoxelRebuilds.ContainsKey(canonical))
        {
            return false;
        }

        if (!_residentVoxelRebuildGenerations.TryGetValue(canonical, out ulong generation))
        {
            generation = 1;
            _residentVoxelRebuildGenerations[canonical] = generation;
        }

        var prepareTimer = Stopwatch.StartNew();
        VoxelChunkData dataSnapshot = chunk.Data.Clone();
        NeighborFaceSnapshot neighborFaces = CaptureLoadedNeighborFaces(canonical);
        Dictionary<RingChunkCoordinate, Dictionary<int, ushort>> overrideSnapshots =
            CaptureWorkerOverrideSnapshots(canonical);
        VoxelBlockPalette palette = VoxelBlockPalette.Capture(BlockColorMap);
        Func<Vector3, Vector3>? vertexWarp = CaptureChunkVertexWarp(canonical);
        bool buildCollisionFaces = chunk.BuildCollisionOnRebuild;
        RingTerrainFineSdfHalo? fineSdfHalo = UseSmoothNaturalTerrainPilot
            && TerrainSettings?.EnableUnified3dDensityTerrain == true
            ? CaptureFineTerrainSdfHalo(canonical)
            : null;
        prepareTimer.Stop();
        LastVoxelMeshPrepareMilliseconds = prepareTimer.Elapsed.TotalMilliseconds;
        MaxVoxelMeshPrepareMilliseconds = Math.Max(
            MaxVoxelMeshPrepareMilliseconds,
            LastVoxelMeshPrepareMilliseconds);

        ulong epoch = _voxelMeshEpoch;
        CancellationToken cancellation = _voxelMeshCancellation.Token;
        _pendingResidentVoxelRebuilds[canonical] = generation;
        _activeVoxelMeshWorkers++;

        _ = Task.Run(() =>
        {
            var timer = Stopwatch.StartNew();
            try
            {
                cancellation.ThrowIfCancellationRequested();
                using RingTerrainSampler? workerSampler = _terrainSampler?.CreateIndependentCopy();
                var surfaceTiles = new Dictionary<RingTerrainColumnCoordinate, int[]>();
                FillMissingNeighborFacesForWorker(
                    neighborFaces,
                    canonical,
                    workerSampler,
                    surfaceTiles,
                    overrideSnapshots);
                if (UseSmoothNaturalTerrainPilot)
                    FillSmoothHaloCornersForWorker(
                        neighborFaces,
                        canonical,
                        workerSampler,
                        surfaceTiles,
                        overrideSnapshots);
                cancellation.ThrowIfCancellationRequested();
                MeshBuildData mesh = VoxelChunkMesher.Build(
                    dataSnapshot,
                    palette,
                    neighborFaces.Sample,
                    vertexWarp,
                    buildCollisionFaces: buildCollisionFaces,
                    smoothNaturalTerrain: UseSmoothNaturalTerrainPilot,
                    smoothTerrainHalo: fineSdfHalo);
                timer.Stop();
                _completedResidentVoxelRebuilds.Enqueue(new ResidentVoxelRebuildResult(
                    canonical,
                    epoch,
                    generation,
                    mesh,
                    timer.Elapsed.TotalMilliseconds,
                    cancellation.IsCancellationRequested,
                    string.Empty));
            }
            catch (OperationCanceledException)
            {
                timer.Stop();
                _completedResidentVoxelRebuilds.Enqueue(new ResidentVoxelRebuildResult(
                    canonical,
                    epoch,
                    generation,
                    default,
                    timer.Elapsed.TotalMilliseconds,
                    true,
                    string.Empty));
            }
            catch (Exception exception)
            {
                timer.Stop();
                _completedResidentVoxelRebuilds.Enqueue(new ResidentVoxelRebuildResult(
                    canonical,
                    epoch,
                    generation,
                    default,
                    timer.Elapsed.TotalMilliseconds,
                    false,
                    exception.ToString()));
            }
        });
        return true;
    }

    private int ServiceCompletedResidentVoxelRebuilds(
        int uploadBudget,
        double timeBudgetUsec,
        ulong sharedStartUsec)
    {
        int uploaded = 0;
        int inspected = 0;

        while (inspected < 64
            && _completedResidentVoxelRebuilds.TryDequeue(
                out ResidentVoxelRebuildResult result))
        {
            inspected++;
            bool currentEpoch = result.Epoch == _voxelMeshEpoch;
            if (currentEpoch)
                _activeVoxelMeshWorkers = Math.Max(0, _activeVoxelMeshWorkers - 1);

            bool ownsPendingSlot = _pendingResidentVoxelRebuilds.TryGetValue(
                    result.Coordinate,
                    out ulong pendingGeneration)
                && pendingGeneration == result.Generation;
            if (ownsPendingSlot)
                _pendingResidentVoxelRebuilds.Remove(result.Coordinate);

            LastVoxelMeshWorkerMilliseconds = result.WorkerMilliseconds;
            MaxVoxelMeshWorkerMilliseconds = Math.Max(
                MaxVoxelMeshWorkerMilliseconds,
                result.WorkerMilliseconds);

            bool currentGeneration = _residentVoxelRebuildGenerations.TryGetValue(
                    result.Coordinate,
                    out ulong requestedGeneration)
                && requestedGeneration == result.Generation;
            bool chunkStillLoaded = _chunks.TryGetValue(
                result.Coordinate,
                out VoxelChunkNode? chunk);
            bool stale = !currentEpoch
                || !ownsPendingSlot
                || !currentGeneration
                || result.Canceled
                || !chunkStillLoaded;
            if (stale)
            {
                DiscardedStaleVoxelMeshCount++;
                continue;
            }

            if (!string.IsNullOrEmpty(result.Error))
            {
                GD.PushError(
                    $"Asynchronous resident voxel rebuild failed for {result.Coordinate}: "
                    + result.Error);
                _queuedRebuildDeadlines[result.Coordinate] = GetNowSeconds();
                continue;
            }

            ulong uploadStartUsec = Time.GetTicksUsec();
            chunk!.InstallRebuild(result.Mesh);
            uploaded++;
            LastVoxelMeshUploadMilliseconds +=
                (Time.GetTicksUsec() - uploadStartUsec) / 1000.0;
            if (uploaded >= uploadBudget
                || Time.GetTicksUsec() - sharedStartUsec >= timeBudgetUsec)
            {
                break;
            }
        }
        return uploaded;
    }

    private void InvalidateAllResidentVoxelRebuilds()
    {
        _residentVoxelRebuildGenerations.Clear();
        _pendingResidentVoxelRebuilds.Clear();
        while (_completedResidentVoxelRebuilds.TryDequeue(out _)) { }
    }
}
