using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase1;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.Phase2;

public partial class VoxelChunkGridManager
{
    [ExportGroup("Asynchronous Voxel Meshing")]
    [Export] public bool EnableAsyncVoxelMeshing { get; set; } = true;
    [Export(PropertyHint.Range, "1,8,1")] public int MaxConcurrentVoxelMeshWorkers { get; set; } = 2;
    [ExportGroup("Voxel mesh memory pressure")]
    [Export] public bool EnableVoxelWorkerMemoryThrottle { get; set; } = true;
    [Export(PropertyHint.Range, "1,8,1")] public int MemoryPressureVoxelWorkerLimit { get; set; } = 1;
    [Export] public bool PrebuildAsyncCollisionFaces { get; set; } = true;
    [Export(PropertyHint.Range, "0,4,1")] public int AsyncCollisionPrebuildMarginChunks { get; set; } = 2;
    [Export(PropertyHint.Range, "1,8,1")] public int MaxVoxelMeshUploadsPerFrame { get; set; } = 1;
    [Export(PropertyHint.Range, "0.25,8.0,0.05")] public float MaxVoxelMeshUploadMillisecondsPerFrame { get; set; } = 1.5f;
    [Export(PropertyHint.Range, "0,8,1")] public int PrewarmedVoxelChunkNodes { get; set; } = 1;

    public int ActiveVoxelMeshWorkerCount => _activeVoxelMeshWorkers;
    public int CompletedVoxelMeshCount => _completedVoxelBuilds.Count;
    public int PendingVoxelMeshCount => _pendingVoxelBuildEpochs.Count;
    public int DeferredVoxelMeshCount => _deferredVoxelBuildSet.Count;
    public double LastVoxelMeshWorkerMilliseconds { get; private set; }
    public double MaxVoxelMeshWorkerMilliseconds { get; private set; }
    public double LastVoxelMeshPrepareMilliseconds { get; private set; }
    public double MaxVoxelMeshPrepareMilliseconds { get; private set; }
    public double LastVoxelNeighborCaptureMilliseconds { get; private set; }
    public double MaxVoxelNeighborCaptureMilliseconds { get; private set; }
    public double LastVoxelMeshUploadMilliseconds { get; private set; }
    public double MaxVoxelMeshUploadMilliseconds { get; private set; }
    public double LastVoxelMeshSurfaceUploadMilliseconds { get; private set; }
    public double MaxVoxelMeshSurfaceUploadMilliseconds { get; private set; }
    public double LastVoxelCollisionInstallMilliseconds { get; private set; }
    public double MaxVoxelCollisionInstallMilliseconds { get; private set; }
    public double LastVoxelChunkAcquireMilliseconds { get; private set; }
    public double MaxVoxelChunkAcquireMilliseconds { get; private set; }
    public double LastVoxelPostLoadMilliseconds { get; private set; }
    public double MaxVoxelPostLoadMilliseconds { get; private set; }
    public double LastVoxelNodeTreeRegistrationMilliseconds { get; private set; }
    public double LastVoxelMaterialPreparationMilliseconds { get; private set; }
    public bool VoxelUploadBackpressureRequestedThisFrame { get; private set; }
    public bool VoxelMeshUploadedThisFrame { get; private set; }
    public long DiscardedStaleVoxelMeshCount { get; private set; }

    private RingTerrainLodCoordinator? _voxelUploadLodCoordinator;
    private bool _yieldNextReadyVoxelUploadToProxyFrame;

    private sealed class NeighborFaceSnapshot
    {
        private const int HaloSizeX = VoxelConstants.ChunkSizeX + 3;
        private const int HaloSizeY = VoxelConstants.ChunkSizeY + 3;
        private const int HaloSizeZ = VoxelConstants.ChunkSizeZ + 3;

        private readonly BlockType[] _negativeX = new BlockType[VoxelConstants.ChunkSizeY * VoxelConstants.ChunkSizeZ];
        private readonly BlockType[] _positiveX = new BlockType[VoxelConstants.ChunkSizeY * VoxelConstants.ChunkSizeZ];
        private readonly BlockType[] _negativeY = new BlockType[VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeZ];
        private readonly BlockType[] _positiveY = new BlockType[VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeZ];
        private readonly BlockType[] _negativeZ = new BlockType[VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeY];
        private readonly BlockType[] _positiveZ = new BlockType[VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeY];
        private readonly BlockType[] _halo = new BlockType[HaloSizeX * HaloSizeY * HaloSizeZ];
        private readonly bool[] _capturedFaces = new bool[6];

        public void SetX(bool positive, int y, int z, BlockType block)
        {
            (positive ? _positiveX : _negativeX)[y + VoxelConstants.ChunkSizeY * z] = block;
            SetHalo(positive ? VoxelConstants.ChunkSizeX : -1, y, z, block);
        }

        public void SetY(bool positive, int x, int z, BlockType block)
        {
            (positive ? _positiveY : _negativeY)[x + VoxelConstants.ChunkSizeX * z] = block;
            SetHalo(x, positive ? VoxelConstants.ChunkSizeY : -1, z, block);
        }

        public void SetZ(bool positive, int x, int y, BlockType block)
        {
            (positive ? _positiveZ : _negativeZ)[x + VoxelConstants.ChunkSizeX * y] = block;
            SetHalo(x, y, positive ? VoxelConstants.ChunkSizeZ : -1, block);
        }

        public void CopyX(
            bool positive,
            VoxelChunkData source,
            bool sourcePositiveBoundary)
        {
            source.CopyBoundaryX(
                sourcePositiveBoundary,
                positive ? _positiveX : _negativeX);
            for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
            for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
                SetHalo(
                    positive ? VoxelConstants.ChunkSizeX : -1,
                    y,
                    z,
                    (positive ? _positiveX : _negativeX)[y + VoxelConstants.ChunkSizeY * z]);
            MarkX(positive);
        }

        public void CopyY(
            bool positive,
            VoxelChunkData source,
            bool sourcePositiveBoundary)
        {
            source.CopyBoundaryY(
                sourcePositiveBoundary,
                positive ? _positiveY : _negativeY);
            for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
            for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
                SetHalo(
                    x,
                    positive ? VoxelConstants.ChunkSizeY : -1,
                    z,
                    (positive ? _positiveY : _negativeY)[x + VoxelConstants.ChunkSizeX * z]);
            MarkY(positive);
        }

        public void CopyZ(
            bool positive,
            VoxelChunkData source,
            bool sourcePositiveBoundary)
        {
            source.CopyBoundaryZ(
                sourcePositiveBoundary,
                positive ? _positiveZ : _negativeZ);
            for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
            for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
                SetHalo(
                    x,
                    y,
                    positive ? VoxelConstants.ChunkSizeZ : -1,
                    (positive ? _positiveZ : _negativeZ)[x + VoxelConstants.ChunkSizeX * y]);
            MarkZ(positive);
        }

        public bool HasX(bool positive) => _capturedFaces[positive ? 1 : 0];
        public bool HasY(bool positive) => _capturedFaces[positive ? 3 : 2];
        public bool HasZ(bool positive) => _capturedFaces[positive ? 5 : 4];
        public void MarkX(bool positive) => _capturedFaces[positive ? 1 : 0] = true;
        public void MarkY(bool positive) => _capturedFaces[positive ? 3 : 2] = true;
        public void MarkZ(bool positive) => _capturedFaces[positive ? 5 : 4] = true;

        public void SetHalo(int x, int y, int z, BlockType block)
        {
            if (x < -1 || x > VoxelConstants.ChunkSizeX + 1
                || y < -1 || y > VoxelConstants.ChunkSizeY + 1
                || z < -1 || z > VoxelConstants.ChunkSizeZ + 1)
                return;
            _halo[(x + 1) + HaloSizeX * ((y + 1) + HaloSizeY * (z + 1))] = block;
        }

        public BlockType Sample(int x, int y, int z)
        {
            if (x >= -1 && x <= VoxelConstants.ChunkSizeX + 1
                && y >= -1 && y <= VoxelConstants.ChunkSizeY + 1
                && z >= -1 && z <= VoxelConstants.ChunkSizeZ + 1)
                return _halo[(x + 1) + HaloSizeX * ((y + 1) + HaloSizeY * (z + 1))];
            return BlockType.Air;
        }
    }

    private enum AsyncVoxelClassification : byte
    {
        Renderable,
        VirtualAir,
        VirtualSolid
    }

    private readonly record struct AsyncVoxelBuildResult(
        RingChunkCoordinate Coordinate,
        ulong Epoch,
        ulong EditVersion,
        VoxelChunkData? Data,
        MeshBuildData Mesh,
        AsyncVoxelClassification Classification,
        double WorkerMilliseconds,
        bool Canceled,
        string Error);

    private readonly ConcurrentQueue<AsyncVoxelBuildResult> _completedVoxelBuilds = new();
    private readonly Dictionary<RingChunkCoordinate, ulong> _pendingVoxelBuildEpochs = new();
    private readonly Queue<RingChunkCoordinate> _deferredVoxelBuilds = new();
    private readonly HashSet<RingChunkCoordinate> _deferredVoxelBuildSet = new();
    private CancellationTokenSource _voxelMeshCancellation = new();
    private ulong _voxelMeshEpoch = 1;
    private int _activeVoxelMeshWorkers;
    private bool _voxelMeshShuttingDown;

    private void ApplyAsyncVoxelMeshingCommandLineOverrides()
    {
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.Equals("--disable-async-voxel-meshing", StringComparison.OrdinalIgnoreCase))
            {
                EnableAsyncVoxelMeshing = false;
            }
            else if (TryReadPositiveIntegerArgument(argument, "--voxel-mesh-workers=", out int workers))
            {
                MaxConcurrentVoxelMeshWorkers = Math.Clamp(workers, 1, 8);
            }
        }
    }

    private static bool TryReadPositiveIntegerArgument(string argument, string prefix, out int value)
    {
        value = 0;
        return argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(argument[prefix.Length..], out value)
            && value > 0;
    }

    private bool HasAsyncVoxelWorkerCapacity() =>
        !EnableAsyncVoxelMeshing
        || _activeVoxelMeshWorkers < ResolveVoxelMeshWorkerLimit();

    private int ResolveVoxelMeshWorkerLimit()
    {
        int configured = Mathf.Max(1, MaxConcurrentVoxelMeshWorkers);
        if (!EnableVoxelWorkerMemoryThrottle || !VoxelMeshMemoryPressureActive)
            return configured;
        return Mathf.Min(configured, Mathf.Max(1, MemoryPressureVoxelWorkerLimit));
    }

    private bool IsAsyncVoxelBuildPending(in RingChunkCoordinate coordinate) =>
        _pendingVoxelBuildEpochs.ContainsKey(CanonicalizeChunkCoordinate(coordinate))
        || _deferredVoxelBuildSet.Contains(CanonicalizeChunkCoordinate(coordinate));

    private void DeferAsyncVoxelBuild(in RingChunkCoordinate coordinate)
    {
        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        if (_chunks.ContainsKey(canonical)
            || _pendingVoxelBuildEpochs.ContainsKey(canonical)
            || !_deferredVoxelBuildSet.Add(canonical))
        {
            return;
        }
        _deferredVoxelBuilds.Enqueue(canonical);
    }

    private int ScheduleDeferredAsyncVoxelBuilds(int scheduleBudget)
    {
        if (!EnableAsyncVoxelMeshing || scheduleBudget <= 0) return 0;
        int scheduled = 0;
        int inspected = 0;
        while (scheduled < scheduleBudget
            && inspected < 256
            && HasAsyncVoxelWorkerCapacity()
            && _deferredVoxelBuilds.Count > 0)
        {
            inspected++;
            RingChunkCoordinate coordinate = _deferredVoxelBuilds.Dequeue();
            if (!_deferredVoxelBuildSet.Remove(coordinate)
                || !_streamingDesiredVisual.Contains(coordinate)
                || _chunks.ContainsKey(coordinate)
                || _pendingVoxelBuildEpochs.ContainsKey(coordinate))
            {
                continue;
            }
            if (ScheduleAsyncVoxelBuild(coordinate))
                scheduled++;
        }
        return scheduled;
    }

    private bool ScheduleAsyncVoxelBuild(in RingChunkCoordinate coordinate)
    {
        if (!EnableAsyncVoxelMeshing || _voxelMeshShuttingDown || !HasAsyncVoxelWorkerCapacity())
            return false;

        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        if (_chunks.ContainsKey(canonical) || _pendingVoxelBuildEpochs.ContainsKey(canonical))
            return false;

        var prepareTimer = Stopwatch.StartNew();
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Voxel mesh prepare");
        NeighborFaceSnapshot neighborFaces =
            CaptureLoadedNeighborFaces(canonical);
        Dictionary<RingChunkCoordinate, Dictionary<int, ushort>> overrideSnapshots =
            CaptureWorkerOverrideSnapshots(canonical);
        prepareTimer.Stop();
        LastVoxelMeshPrepareMilliseconds = prepareTimer.Elapsed.TotalMilliseconds;
        MaxVoxelMeshPrepareMilliseconds = Math.Max(
            MaxVoxelMeshPrepareMilliseconds,
            LastVoxelMeshPrepareMilliseconds);
        LastVoxelNeighborCaptureMilliseconds = prepareTimer.Elapsed.TotalMilliseconds;
        MaxVoxelNeighborCaptureMilliseconds = Math.Max(
            MaxVoxelNeighborCaptureMilliseconds,
            LastVoxelNeighborCaptureMilliseconds);
        VoxelBlockPalette palette = VoxelBlockPalette.Capture(BlockColorMap);
        Func<Vector3, Vector3>? vertexWarp = CaptureChunkVertexWarp(canonical);
        bool useFineSdf = UseSmoothNaturalTerrainPilot
            && TerrainSettings?.EnableUnified3dDensityTerrain == true
            && VoxelChunkMesherNative.IsSmoothRegularFineSdfAvailable
            && _terrainSampler != null
            && TerrainSettings != null;
        RingLodSurfaceEditSnapshot? smoothEdits = useFineSdf
            ? CaptureLodSurfaceEditSnapshot()
            : null;
        RingNaturalTerrainSdfEditSnapshot? smoothNaturalEdits = useFineSdf
            ? CaptureNaturalTerrainSdfEditSnapshot()
            : null;
        int smoothRingWidth = Mathf.RoundToInt(RingAcrossWidthMeters);
        int smoothBedrockY = TerrainSettings?.BedrockY ?? 0;
        int smoothSoilDepth = TerrainSettings?.SoilDepthVoxels ?? 1;
        // Keep expanded triangles for a small halo around the collision
        // corridor. That prevents ArrayMesh.GetFaces() stalls when a nearby
        // visual chunk enters the physics bubble without paying the memory and
        // worker cost for every physical chunk in the seven-chunk view radius.
        bool buildCollisionFaces = _streamingDesiredCollision.Contains(canonical)
            || (PrebuildAsyncCollisionFaces && IsNearDesiredCollision(canonical));
        ulong epoch = _voxelMeshEpoch;
        ulong editVersion = _lodSurfaceEditVersion;
        CancellationToken cancellation = _voxelMeshCancellation.Token;
        _pendingVoxelBuildEpochs[canonical] = epoch;
        _activeVoxelMeshWorkers++;

        _ = Task.Run(() =>
        {
            using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Voxel mesh build", worker: true);
            var timer = Stopwatch.StartNew();
            try
            {
                if (cancellation.IsCancellationRequested)
                {
                    _completedVoxelBuilds.Enqueue(new AsyncVoxelBuildResult(
                        canonical,
                        epoch,
                        editVersion,
                        null,
                        default,
                        AsyncVoxelClassification.Renderable,
                        0.0,
                        true,
                        string.Empty));
                    return;
                }

                using RingTerrainSampler? workerSampler =
                    _terrainSampler?.CreateIndependentCopy();
                var surfaceTiles = new Dictionary<RingTerrainColumnCoordinate, int[]>();
                AsyncVoxelClassification classification = ClassifyVoxelChunkForWorker(
                    canonical,
                    buildCollisionFaces,
                    workerSampler,
                    surfaceTiles,
                    overrideSnapshots);
                if (classification != AsyncVoxelClassification.Renderable)
                {
                    timer.Stop();
                    _completedVoxelBuilds.Enqueue(new AsyncVoxelBuildResult(
                        canonical,
                        epoch,
                        editVersion,
                        null,
                        default,
                        classification,
                        timer.Elapsed.TotalMilliseconds,
                        cancellation.IsCancellationRequested,
                        string.Empty));
                    return;
                }

                RingTerrainFineSdfHalo? fineSdfHalo = null;
                if (useFineSdf
                    && workerSampler != null
                    && smoothEdits != null
                    && smoothNaturalEdits != null)
                {
                    using var smoothSnapshot = RingTerrainSdfSnapshot.Create(
                        workerSampler,
                        smoothEdits,
                        smoothNaturalEdits,
                        smoothRingWidth,
                        smoothBedrockY,
                        smoothSoilDepth);
                    var fineOrigin = new RingTerrainSampleCoordinate(
                        canonical.AroundRingChunk * VoxelConstants.ChunkSizeX,
                        canonical.VerticalChunk * VoxelConstants.ChunkSizeY,
                        canonical.AcrossRingChunk * VoxelConstants.ChunkSizeZ);
                    fineSdfHalo = RingTerrainFineSdfHalo.Build(smoothSnapshot, fineOrigin);
                }

                var data = new VoxelChunkData();
                PopulateChunkBaselineForWorker(
                    data,
                    canonical,
                    workerSampler,
                    surfaceTiles);
                ApplyWorkerOverrides(canonical, data, overrideSnapshots);
                FillMissingNeighborFacesForWorker(
                    neighborFaces,
                    canonical,
                    workerSampler,
                    surfaceTiles,
                    overrideSnapshots);
                if (UseSmoothNaturalTerrainPilot && !useFineSdf)
                    FillSmoothHaloCornersForWorker(
                        neighborFaces,
                        canonical,
                        workerSampler,
                        surfaceTiles,
                        overrideSnapshots);
                cancellation.ThrowIfCancellationRequested();
                MeshBuildData mesh = VoxelChunkMesher.Build(
                    data,
                    palette,
                    neighborFaces.Sample,
                    vertexWarp,
                    buildCollisionFaces: buildCollisionFaces,
                    smoothNaturalTerrain: UseSmoothNaturalTerrainPilot,
                    smoothTerrainHalo: fineSdfHalo);
                timer.Stop();
                bool canceled = cancellation.IsCancellationRequested;
                _completedVoxelBuilds.Enqueue(new AsyncVoxelBuildResult(
                    canonical,
                    epoch,
                    editVersion,
                    canceled ? null : data,
                    mesh,
                    AsyncVoxelClassification.Renderable,
                    timer.Elapsed.TotalMilliseconds,
                    canceled,
                    string.Empty));
            }
            catch (OperationCanceledException)
            {
                timer.Stop();
                _completedVoxelBuilds.Enqueue(new AsyncVoxelBuildResult(
                    canonical,
                    epoch,
                    editVersion,
                    null,
                    default,
                    AsyncVoxelClassification.Renderable,
                    timer.Elapsed.TotalMilliseconds,
                    true,
                    string.Empty));
            }
            catch (Exception exception)
            {
                timer.Stop();
                _completedVoxelBuilds.Enqueue(new AsyncVoxelBuildResult(
                    canonical,
                    epoch,
                    editVersion,
                    null,
                    default,
                    AsyncVoxelClassification.Renderable,
                    timer.Elapsed.TotalMilliseconds,
                    false,
                    exception.ToString()));
            }
        });
        return true;
    }

    private bool IsNearDesiredCollision(in RingChunkCoordinate coordinate)
    {
        int margin = Mathf.Max(0, AsyncCollisionPrebuildMarginChunks);
        for (int across = -margin; across <= margin; across++)
        for (int around = -margin; around <= margin; around++)
        {
            if (_streamingDesiredCollision.Contains(
                OffsetChunkCoordinate(coordinate, around, across, 0)))
            {
                return true;
            }
        }
        return false;
    }

    private NeighborFaceSnapshot CaptureLoadedNeighborFaces(
        in RingChunkCoordinate coordinate)
    {
        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        var snapshot = new NeighborFaceSnapshot();

        if (_chunks.TryGetValue(
                OffsetChunkCoordinate(canonical, -1, 0, 0),
                out VoxelChunkNode? negativeX)
            && negativeX.HasRetainedVoxelData)
            snapshot.CopyX(
                positive: false,
                negativeX.Data,
                sourcePositiveBoundary: true);
        if (_chunks.TryGetValue(
                OffsetChunkCoordinate(canonical, +1, 0, 0),
                out VoxelChunkNode? positiveX)
            && positiveX.HasRetainedVoxelData)
            snapshot.CopyX(
                positive: true,
                positiveX.Data,
                sourcePositiveBoundary: false);

        // Ring coordinates are stored as around/across/vertical. The voxel
        // chunk's local Y axis maps to vertical and local Z maps to across.
        if (_chunks.TryGetValue(
                OffsetChunkCoordinate(canonical, 0, 0, -1),
                out VoxelChunkNode? negativeY)
            && negativeY.HasRetainedVoxelData)
            snapshot.CopyY(
                positive: false,
                negativeY.Data,
                sourcePositiveBoundary: true);
        if (_chunks.TryGetValue(
                OffsetChunkCoordinate(canonical, 0, 0, +1),
                out VoxelChunkNode? positiveY)
            && positiveY.HasRetainedVoxelData)
            snapshot.CopyY(
                positive: true,
                positiveY.Data,
                sourcePositiveBoundary: false);

        if (_chunks.TryGetValue(
                OffsetChunkCoordinate(canonical, 0, -1, 0),
                out VoxelChunkNode? negativeZ)
            && negativeZ.HasRetainedVoxelData)
            snapshot.CopyZ(
                positive: false,
                negativeZ.Data,
                sourcePositiveBoundary: true);
        if (_chunks.TryGetValue(
                OffsetChunkCoordinate(canonical, 0, +1, 0),
                out VoxelChunkNode? positiveZ)
            && positiveZ.HasRetainedVoxelData)
            snapshot.CopyZ(
                positive: true,
                positiveZ.Data,
                sourcePositiveBoundary: false);

        return snapshot;
    }

    private Dictionary<RingChunkCoordinate, Dictionary<int, ushort>> CaptureWorkerOverrideSnapshots(
        in RingChunkCoordinate coordinate)
    {
        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        var snapshots = new Dictionary<RingChunkCoordinate, Dictionary<int, ushort>>();
        for (int vertical = -1; vertical <= 1; vertical++)
        for (int across = -1; across <= 1; across++)
        for (int around = -1; around <= 1; around++)
        {
            RingChunkCoordinate relevantCoordinate = OffsetChunkCoordinate(
                canonical,
                around,
                across,
                vertical);
            if (_chunkOverrides.TryGetValue(
                    relevantCoordinate,
                    out Dictionary<int, ushort>? overrides)
                && overrides.Count > 0)
            {
                snapshots[relevantCoordinate] = new Dictionary<int, ushort>(overrides);
            }
        }
        return snapshots;
    }

    private void FillMissingNeighborFacesForWorker(
        NeighborFaceSnapshot snapshot,
        in RingChunkCoordinate coordinate,
        RingTerrainSampler? sampler,
        Dictionary<RingTerrainColumnCoordinate, int[]> surfaceTiles,
        IReadOnlyDictionary<RingChunkCoordinate, Dictionary<int, ushort>> overrides)
    {
        FillMissingXNeighborFaceForWorker(
            snapshot, coordinate, positive: false, sampler, surfaceTiles, overrides);
        FillMissingXNeighborFaceForWorker(
            snapshot, coordinate, positive: true, sampler, surfaceTiles, overrides);
        FillMissingYNeighborFaceForWorker(
            snapshot, coordinate, positive: false, sampler, surfaceTiles, overrides);
        FillMissingYNeighborFaceForWorker(
            snapshot, coordinate, positive: true, sampler, surfaceTiles, overrides);
        FillMissingZNeighborFaceForWorker(
            snapshot, coordinate, positive: false, sampler, surfaceTiles, overrides);
        FillMissingZNeighborFaceForWorker(
            snapshot, coordinate, positive: true, sampler, surfaceTiles, overrides);
    }

    private void FillSmoothHaloCornersForWorker(
        NeighborFaceSnapshot snapshot,
        in RingChunkCoordinate coordinate,
        RingTerrainSampler? sampler,
        Dictionary<RingTerrainColumnCoordinate, int[]> surfaceTiles,
        IReadOnlyDictionary<RingChunkCoordinate, Dictionary<int, ushort>> overrides)
    {
        for (int z = -1; z <= VoxelConstants.ChunkSizeZ + 1; z++)
        for (int y = -1; y <= VoxelConstants.ChunkSizeY + 1; y++)
        for (int x = -1; x <= VoxelConstants.ChunkSizeX + 1; x++)
        {
            int outsideAxes = (x is < 0 or >= VoxelConstants.ChunkSizeX ? 1 : 0)
                + (y is < 0 or >= VoxelConstants.ChunkSizeY ? 1 : 0)
                + (z is < 0 or >= VoxelConstants.ChunkSizeZ ? 1 : 0);
            bool requiresPositivePadding = x == VoxelConstants.ChunkSizeX + 1
                || y == VoxelConstants.ChunkSizeY + 1
                || z == VoxelConstants.ChunkSizeZ + 1;
            if (outsideAxes < 2 && !requiresPositivePadding) continue;

            int aroundOffset = x < 0 ? -1 : x >= VoxelConstants.ChunkSizeX ? 1 : 0;
            int acrossOffset = z < 0 ? -1 : z >= VoxelConstants.ChunkSizeZ ? 1 : 0;
            int verticalOffset = y < 0 ? -1 : y >= VoxelConstants.ChunkSizeY ? 1 : 0;
            RingChunkCoordinate target = OffsetChunkCoordinate(
                coordinate,
                aroundOffset,
                acrossOffset,
                verticalOffset);
            int localX = x < 0
                ? VoxelConstants.ChunkSizeX - 1
                : x >= VoxelConstants.ChunkSizeX ? x - VoxelConstants.ChunkSizeX : x;
            int localY = y < 0
                ? VoxelConstants.ChunkSizeY - 1
                : y >= VoxelConstants.ChunkSizeY ? y - VoxelConstants.ChunkSizeY : y;
            int localZ = z < 0
                ? VoxelConstants.ChunkSizeZ - 1
                : z >= VoxelConstants.ChunkSizeZ ? z - VoxelConstants.ChunkSizeZ : z;
            snapshot.SetHalo(
                x,
                y,
                z,
                GetWorkerBaselineBlock(
                    target,
                    localX,
                    localY,
                    localZ,
                    sampler,
                    surfaceTiles,
                    overrides));
        }
    }

    private void FillMissingXNeighborFaceForWorker(
        NeighborFaceSnapshot snapshot,
        in RingChunkCoordinate coordinate,
        bool positive,
        RingTerrainSampler? sampler,
        Dictionary<RingTerrainColumnCoordinate, int[]> surfaceTiles,
        IReadOnlyDictionary<RingChunkCoordinate, Dictionary<int, ushort>> overrides)
    {
        if (snapshot.HasX(positive)) return;
        RingChunkCoordinate target = OffsetChunkCoordinate(
            coordinate, positive ? 1 : -1, 0, 0);
        int localX = positive ? 0 : VoxelConstants.ChunkSizeX - 1;
        for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
        for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
        {
            snapshot.SetX(
                positive,
                y,
                z,
                GetWorkerBaselineBlock(
                    target,
                    localX,
                    y,
                    z,
                    sampler,
                    surfaceTiles,
                    overrides));
        }
        snapshot.MarkX(positive);
    }

    private void FillMissingYNeighborFaceForWorker(
        NeighborFaceSnapshot snapshot,
        in RingChunkCoordinate coordinate,
        bool positive,
        RingTerrainSampler? sampler,
        Dictionary<RingTerrainColumnCoordinate, int[]> surfaceTiles,
        IReadOnlyDictionary<RingChunkCoordinate, Dictionary<int, ushort>> overrides)
    {
        if (snapshot.HasY(positive)) return;
        RingChunkCoordinate target = OffsetChunkCoordinate(
            coordinate, 0, 0, positive ? 1 : -1);
        int localY = positive ? 0 : VoxelConstants.ChunkSizeY - 1;
        for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
        for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
        {
            snapshot.SetY(
                positive,
                x,
                z,
                GetWorkerBaselineBlock(
                    target,
                    x,
                    localY,
                    z,
                    sampler,
                    surfaceTiles,
                    overrides));
        }
        snapshot.MarkY(positive);
    }

    private void FillMissingZNeighborFaceForWorker(
        NeighborFaceSnapshot snapshot,
        in RingChunkCoordinate coordinate,
        bool positive,
        RingTerrainSampler? sampler,
        Dictionary<RingTerrainColumnCoordinate, int[]> surfaceTiles,
        IReadOnlyDictionary<RingChunkCoordinate, Dictionary<int, ushort>> overrides)
    {
        if (snapshot.HasZ(positive)) return;
        RingChunkCoordinate target = OffsetChunkCoordinate(
            coordinate, 0, positive ? 1 : -1, 0);
        int localZ = positive ? 0 : VoxelConstants.ChunkSizeZ - 1;
        for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
        for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
        {
            snapshot.SetZ(
                positive,
                x,
                y,
                GetWorkerBaselineBlock(
                    target,
                    x,
                    y,
                    localZ,
                    sampler,
                    surfaceTiles,
                    overrides));
        }
        snapshot.MarkZ(positive);
    }

    private BlockType GetWorkerBaselineBlock(
        in RingChunkCoordinate coordinate,
        int localX,
        int localY,
        int localZ,
        RingTerrainSampler? sampler,
        Dictionary<RingTerrainColumnCoordinate, int[]> surfaceTiles,
        IReadOnlyDictionary<RingChunkCoordinate, Dictionary<int, ushort>> overrides)
    {
        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        int localIndex = VoxelChunkData.ToLinearIndex(localX, localY, localZ);
        if (overrides.TryGetValue(canonical, out Dictionary<int, ushort>? chunkOverrides)
            && chunkOverrides.TryGetValue(localIndex, out ushort blockId))
        {
            return (BlockType)blockId;
        }

        if (!UseDeterministicTerrainGeneration || TerrainSettings == null)
            return VoxelChunkData.GetFlatTestTerrainBlock(localY, InitialSolidHeight);
        if (!IsChunkWithinVerticalBounds(canonical))
            return BlockType.Air;

        int globalY = canonical.VerticalChunk * VoxelConstants.ChunkSizeY + localY;
        int globalZ = canonical.AcrossRingChunk * VoxelConstants.ChunkSizeZ + localZ;
        if (IsStructuralBedrockVoxel(globalY, globalZ))
            return BlockType.Bedrock;
        if (globalY < MinExcavationGlobalY
            || globalY > MaxBuildGlobalY
            || !IsAcrossVoxelWithinRingBounds(globalZ))
        {
            return BlockType.Air;
        }
        if (IsAcrossVoxelInStructuralHull(globalZ))
            return IsVerticalVoxelWithinStructuralHull(globalY)
                ? BlockType.Bedrock
                : BlockType.Air;

        if (sampler == null)
            return BlockType.Air;

        int[] surfaceTile = GetOrBuildWorkerSurfaceTile(
            canonical, sampler, surfaceTiles);
        int surfaceY = surfaceTile[localZ * VoxelConstants.ChunkSizeX + localX];
        int soilDepth = Mathf.Max(1, TerrainSettings.SoilDepthVoxels);
        return globalY <= TerrainSettings.BedrockY
            ? BlockType.Bedrock
            : globalY > surfaceY
                ? BlockType.Air
                : surfaceY - globalY < soilDepth ? BlockType.Soil : BlockType.Rock;
    }

    private AsyncVoxelClassification ClassifyVoxelChunkForWorker(
        in RingChunkCoordinate coordinate,
        bool buildCollision,
        RingTerrainSampler? sampler,
        Dictionary<RingTerrainColumnCoordinate, int[]> surfaceTiles,
        IReadOnlyDictionary<RingChunkCoordinate, Dictionary<int, ushort>> overrides)
    {
        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        if (!UseDeterministicTerrainGeneration || TerrainSettings == null)
            return AsyncVoxelClassification.Renderable;
        if (overrides.TryGetValue(canonical, out Dictionary<int, ushort>? localOverrides)
            && localOverrides.Count > 0)
        {
            return AsyncVoxelClassification.Renderable;
        }

        int globalBaseY = canonical.VerticalChunk * VoxelConstants.ChunkSizeY;
        int globalEndYExclusive = globalBaseY + VoxelConstants.ChunkSizeY;
        int globalAcrossStart = canonical.AcrossRingChunk * VoxelConstants.ChunkSizeZ;
        int globalAcrossEnd = globalAcrossStart + VoxelConstants.ChunkSizeZ - 1;
        if (ChunkOverlapsStructuralUnderside(globalBaseY, globalEndYExclusive)
            && ChunkOverlapsStructuralShellAcross(globalAcrossStart, globalAcrossEnd))
        {
            return AsyncVoxelClassification.Renderable;
        }
        if (IsAcrossVoxelInStructuralHull(globalAcrossStart)
            || IsAcrossVoxelInStructuralHull(globalAcrossEnd))
        {
            bool entirelyAir = globalEndYExclusive <= GetStructuralBottomGlobalY()
                || globalBaseY > GetStructuralWallTopGlobalY();
            return entirelyAir && SkipKnownEmptyStreamingChunks
                ? AsyncVoxelClassification.VirtualAir
                : AsyncVoxelClassification.Renderable;
        }

        if (!IsAcrossVoxelWithinRingBounds(globalAcrossStart))
            return SkipKnownEmptyStreamingChunks
                ? AsyncVoxelClassification.VirtualAir
                : AsyncVoxelClassification.Renderable;
        if (globalEndYExclusive <= MinExcavationGlobalY)
            return SkipKnownEmptyStreamingChunks
                ? AsyncVoxelClassification.VirtualAir
                : AsyncVoxelClassification.Renderable;
        if (sampler == null)
            return AsyncVoxelClassification.Renderable;
        if (globalBaseY > sampler.MaximumPossibleSurfaceHeight)
        {
            return SkipKnownEmptyStreamingChunks
                ? AsyncVoxelClassification.VirtualAir
                : AsyncVoxelClassification.Renderable;
        }

        int[] surfaceTile = GetOrBuildWorkerSurfaceTile(
            canonical, sampler, surfaceTiles);
        bool hasSolidVoxel = false;
        foreach (int surfaceHeight in surfaceTile)
        {
            if (surfaceHeight >= globalBaseY)
            {
                hasSolidVoxel = true;
                break;
            }
        }
        if (!hasSolidVoxel)
        {
            return SkipKnownEmptyStreamingChunks
                ? AsyncVoxelClassification.VirtualAir
                : AsyncVoxelClassification.Renderable;
        }
        if (buildCollision
            || !SkipKnownSolidStreamingChunks
            || !IsChunkWithinRingAcrossBounds(canonical)
            || !IsChunkWithinVerticalBounds(canonical)
            || HasWorkerOverridesInChunkOrNeighbors(canonical, overrides)
            || globalBaseY <= MinExcavationGlobalY
            || globalEndYExclusive > MaxBuildGlobalY
            || !IsAcrossVoxelWithinRingBounds(globalAcrossEnd))
        {
            return AsyncVoxelClassification.Renderable;
        }

        foreach (int surfaceHeight in surfaceTile)
        {
            if (surfaceHeight < globalEndYExclusive)
                return AsyncVoxelClassification.Renderable;
        }

        int globalTopY = globalEndYExclusive - 1;
        for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
        {
            if (GetWorkerBaselineSurfaceHeight(
                    canonical, -1, z, sampler, surfaceTiles) < globalTopY
                || GetWorkerBaselineSurfaceHeight(
                    canonical,
                    VoxelConstants.ChunkSizeX,
                    z,
                    sampler,
                    surfaceTiles) < globalTopY)
            {
                return AsyncVoxelClassification.Renderable;
            }
        }
        for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
        {
            if (GetWorkerBaselineSurfaceHeight(
                    canonical, x, -1, sampler, surfaceTiles) < globalTopY
                || GetWorkerBaselineSurfaceHeight(
                    canonical,
                    x,
                    VoxelConstants.ChunkSizeZ,
                    sampler,
                    surfaceTiles) < globalTopY)
            {
                return AsyncVoxelClassification.Renderable;
            }
        }

        return AsyncVoxelClassification.VirtualSolid;
    }

    private bool HasWorkerOverridesInChunkOrNeighbors(
        in RingChunkCoordinate coordinate,
        IReadOnlyDictionary<RingChunkCoordinate, Dictionary<int, ushort>> overrides)
    {
        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        return overrides.ContainsKey(canonical)
            || overrides.ContainsKey(OffsetChunkCoordinate(canonical, -1, 0, 0))
            || overrides.ContainsKey(OffsetChunkCoordinate(canonical, +1, 0, 0))
            || overrides.ContainsKey(OffsetChunkCoordinate(canonical, 0, -1, 0))
            || overrides.ContainsKey(OffsetChunkCoordinate(canonical, 0, +1, 0))
            || overrides.ContainsKey(OffsetChunkCoordinate(canonical, 0, 0, -1))
            || overrides.ContainsKey(OffsetChunkCoordinate(canonical, 0, 0, +1));
    }

    private int GetWorkerBaselineSurfaceHeight(
        in RingChunkCoordinate baseCoordinate,
        int localX,
        int localZ,
        RingTerrainSampler sampler,
        Dictionary<RingTerrainColumnCoordinate, int[]> surfaceTiles)
    {
        RingChunkCoordinate target = CanonicalizeChunkCoordinate(baseCoordinate);
        while (localX < 0)
        {
            target = OffsetChunkCoordinate(target, -1, 0, 0);
            localX += VoxelConstants.ChunkSizeX;
        }
        while (localX >= VoxelConstants.ChunkSizeX)
        {
            target = OffsetChunkCoordinate(target, +1, 0, 0);
            localX -= VoxelConstants.ChunkSizeX;
        }
        while (localZ < 0)
        {
            target = OffsetChunkCoordinate(target, 0, -1, 0);
            localZ += VoxelConstants.ChunkSizeZ;
        }
        while (localZ >= VoxelConstants.ChunkSizeZ)
        {
            target = OffsetChunkCoordinate(target, 0, +1, 0);
            localZ -= VoxelConstants.ChunkSizeZ;
        }

        int globalZ = target.AcrossRingChunk * VoxelConstants.ChunkSizeZ + localZ;
        if (!IsAcrossVoxelWithinRingBounds(globalZ)
            || IsAcrossVoxelInStructuralHull(globalZ))
        {
            return int.MinValue;
        }

        int[] tile = GetOrBuildWorkerSurfaceTile(target, sampler, surfaceTiles);
        return tile[localZ * VoxelConstants.ChunkSizeX + localX];
    }

    private int[] GetOrBuildWorkerSurfaceTile(
        in RingChunkCoordinate coordinate,
        RingTerrainSampler sampler,
        Dictionary<RingTerrainColumnCoordinate, int[]> surfaceTiles)
    {
        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        var key = new RingTerrainColumnCoordinate(
            canonical.AroundRingChunk,
            canonical.AcrossRingChunk);
        if (surfaceTiles.TryGetValue(key, out int[]? cached))
            return cached;

        var generated = new int[
            VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeZ];
        long globalBaseX = canonical.AroundRingChunk * VoxelConstants.ChunkSizeX;
        int globalBaseZ = canonical.AcrossRingChunk * VoxelConstants.ChunkSizeZ;
        for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
        for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
        {
            generated[z * VoxelConstants.ChunkSizeX + x] =
                sampler.GetSurfaceHeight(globalBaseX + x, globalBaseZ + z);
        }
        surfaceTiles[key] = generated;
        return generated;
    }

    private void PopulateChunkBaselineForWorker(
        VoxelChunkData data,
        in RingChunkCoordinate coordinate,
        RingTerrainSampler? sampler,
        Dictionary<RingTerrainColumnCoordinate, int[]> surfaceTiles)
    {
        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        if (!UseDeterministicTerrainGeneration || TerrainSettings == null)
        {
            data.FillFromSampler((_, y, _) =>
                VoxelChunkData.GetFlatTestTerrainBlock(y, InitialSolidHeight));
            return;
        }
        if (sampler == null)
        {
            data.Fill(BlockType.Air);
            return;
        }

        int globalBaseY = canonical.VerticalChunk * VoxelConstants.ChunkSizeY;
        int globalBaseZ = canonical.AcrossRingChunk * VoxelConstants.ChunkSizeZ;
        int soilDepth = Mathf.Max(1, TerrainSettings.SoilDepthVoxels);
        int bedrockY = TerrainSettings.BedrockY;
        int[] surfaceTile = GetOrBuildWorkerSurfaceTile(
            canonical, sampler, surfaceTiles);
        for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
        for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
        {
            int globalZ = globalBaseZ + z;
            if (IsAcrossVoxelInStructuralHull(globalZ))
            {
                for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
                {
                    int globalY = globalBaseY + y;
                    data.SetBlock(
                        x,
                        y,
                        z,
                        IsVerticalVoxelWithinStructuralHull(globalY)
                            ? BlockType.Bedrock
                            : BlockType.Air);
                }
                continue;
            }
            if (!IsAcrossVoxelWithinRingBounds(globalZ))
            {
                for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
                    data.SetBlock(x, y, z, BlockType.Air);
                continue;
            }

            int surfaceY = surfaceTile[
                z * VoxelConstants.ChunkSizeX + x];
            for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
            {
                int globalY = globalBaseY + y;
                BlockType block = IsStructuralBedrockVoxel(globalY, globalZ)
                    ? BlockType.Bedrock
                    : globalY < MinExcavationGlobalY
                        ? BlockType.Air
                        : globalY <= bedrockY
                            ? BlockType.Bedrock
                            : globalY > surfaceY
                                ? BlockType.Air
                                : surfaceY - globalY < soilDepth
                                    ? BlockType.Soil
                                    : BlockType.Rock;
                data.SetBlock(x, y, z, block);
            }
        }
    }

    private static void ApplyWorkerOverrides(
        in RingChunkCoordinate coordinate,
        VoxelChunkData data,
        IReadOnlyDictionary<RingChunkCoordinate, Dictionary<int, ushort>> overrides)
    {
        if (!overrides.TryGetValue(
                coordinate,
                out Dictionary<int, ushort>? chunkOverrides))
        {
            return;
        }
        foreach ((int localIndex, ushort blockId) in chunkOverrides)
        {
            if (!VoxelChunkData.TryFromLinearIndex(
                    localIndex,
                    out Vector3I localVoxel))
            {
                continue;
            }
            data.SetBlock(
                localVoxel.X,
                localVoxel.Y,
                localVoxel.Z,
                (BlockType)blockId);
        }
    }

    private Func<Vector3, Vector3>? CaptureChunkVertexWarp(in RingChunkCoordinate coordinate)
    {
        bool enabled = UseRingCurvedPlacement
            && EnableCurvatureVertexWarp
            && CurvatureVertexWarpStrength > 0.0001f;
        if (!enabled) return null;

        float angularRadiusMeters = GetEffectiveRingRadiusMeters();
        float radialBaseMeters = UseHabitableRingAxisLayout
            ? angularRadiusMeters - (RingVerticalBaseOffsetMeters
                + coordinate.VerticalChunk * VoxelConstants.ChunkSizeY * VoxelConstants.VoxelSizeMeters)
            : angularRadiusMeters + RingAcrossBaseOffsetMeters
                + coordinate.AcrossRingChunk * VoxelConstants.ChunkSizeZ * VoxelConstants.VoxelSizeMeters;
        return VoxelChunkNode.CreateRingVertexWarp(
            angularRadiusMeters,
            radialBaseMeters,
            CurvatureVertexWarpStrength,
            UseHabitableRingAxisLayout);
    }

    private void ServiceCompletedVoxelBuilds()
    {
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Voxel mesh install");
        LastVoxelMeshUploadMilliseconds = 0.0;
        VoxelUploadBackpressureRequestedThisFrame = false;
        VoxelMeshUploadedThisFrame = false;
        bool hasReadyUpload = !_completedVoxelBuilds.IsEmpty
            || !_completedResidentVoxelRebuilds.IsEmpty;
        if (!hasReadyUpload)
        {
            _yieldNextReadyVoxelUploadToProxyFrame = false;
        }
        else
        {
            _voxelUploadLodCoordinator ??=
                GetParent()?.GetNodeOrNull<RingTerrainLodCoordinator>("TerrainLodCoordinator");
            if (_yieldNextReadyVoxelUploadToProxyFrame)
            {
                // Let the proxy uploader make progress between consecutive voxel
                // submissions. The following frame will reserve a quiet drain frame.
                _yieldNextReadyVoxelUploadToProxyFrame = false;
                return;
            }
            if (_voxelUploadLodCoordinator?.LastFrameUploadCount > 0)
            {
                // A render-server surface upload can block for hundreds of
                // milliseconds when submitted behind the proxy mesh queue. Ask
                // the coordinator for one upload-free drain frame, then install
                // the voxel on the next frame while the proxy mesh remains visible.
                VoxelUploadBackpressureRequestedThisFrame = true;
                return;
            }
        }
        int totalUploadBudget = Mathf.Max(1, MaxVoxelMeshUploadsPerFrame);
        double timeBudgetUsec = Math.Max(0.25, MaxVoxelMeshUploadMillisecondsPerFrame) * 1000.0;
        ulong startUsec = Time.GetTicksUsec();
        int residentUploads = ServiceCompletedResidentVoxelRebuilds(
            totalUploadBudget,
            timeBudgetUsec,
            startUsec);
        int uploadBudget = totalUploadBudget - residentUploads;
        if (uploadBudget <= 0 || Time.GetTicksUsec() - startUsec >= timeBudgetUsec)
        {
            MaxVoxelMeshUploadMilliseconds = Math.Max(
                MaxVoxelMeshUploadMilliseconds,
                LastVoxelMeshUploadMilliseconds);
            VoxelMeshUploadedThisFrame = residentUploads > 0;
            if (VoxelMeshUploadedThisFrame)
                _yieldNextReadyVoxelUploadToProxyFrame = true;
            return;
        }
        int uploaded = 0;
        int inspected = 0;

        while (inspected < 64 && _completedVoxelBuilds.TryDequeue(out AsyncVoxelBuildResult result))
        {
            inspected++;
            bool currentEpoch = result.Epoch == _voxelMeshEpoch;
            if (currentEpoch)
                _activeVoxelMeshWorkers = Math.Max(0, _activeVoxelMeshWorkers - 1);

            bool ownsPendingSlot = _pendingVoxelBuildEpochs.TryGetValue(result.Coordinate, out ulong pendingEpoch)
                && pendingEpoch == result.Epoch;
            if (ownsPendingSlot)
                _pendingVoxelBuildEpochs.Remove(result.Coordinate);

            LastVoxelMeshWorkerMilliseconds = result.WorkerMilliseconds;
            MaxVoxelMeshWorkerMilliseconds = Math.Max(MaxVoxelMeshWorkerMilliseconds, result.WorkerMilliseconds);

            if (currentEpoch && ownsPendingSlot && !string.IsNullOrEmpty(result.Error))
            {
                GD.PushError($"Asynchronous voxel mesh failed for {result.Coordinate}: {result.Error}");
                _hasLastStreamingCenter = false;
                continue;
            }

            bool baseStale = !currentEpoch
                || !ownsPendingSlot
                || result.Canceled
                || result.EditVersion != _lodSurfaceEditVersion
                || !_streamingDesiredVisual.Contains(result.Coordinate)
                || _chunks.ContainsKey(result.Coordinate);
            if (result.Classification != AsyncVoxelClassification.Renderable)
            {
                if (baseStale)
                {
                    DiscardedStaleVoxelMeshCount++;
                    continue;
                }
                if (result.Classification == AsyncVoxelClassification.VirtualAir)
                {
                    _knownEmptyStreamingChunks.Add(result.Coordinate);
                    _knownSolidStreamingChunks.Remove(result.Coordinate);
                }
                else
                {
                    _knownSolidStreamingChunks.Add(result.Coordinate);
                    _knownEmptyStreamingChunks.Remove(result.Coordinate);
                }
                continue;
            }

            bool stale = baseStale || result.Data == null;
            if (stale)
            {
                DiscardedStaleVoxelMeshCount++;
                if (currentEpoch
                    && _streamingDesiredVisual.Contains(result.Coordinate)
                    && !_chunks.ContainsKey(result.Coordinate))
                {
                    _hasLastStreamingCenter = false;
                }
                continue;
            }

            bool buildCollision = _streamingDesiredCollision.Contains(result.Coordinate);
            ulong uploadStartUsec = Time.GetTicksUsec();
            VoxelChunkNode chunk = CreateChunkNodeFromBuild(
                result.Coordinate,
                buildCollision,
                result.Data!,
                result.Mesh);
            double createMilliseconds = (Time.GetTicksUsec() - uploadStartUsec) / 1000.0;
            LastVoxelChunkAcquireMilliseconds = Math.Max(
                0.0,
                createMilliseconds - chunk.LastMeshInstallMilliseconds);
            MaxVoxelChunkAcquireMilliseconds = Math.Max(
                MaxVoxelChunkAcquireMilliseconds,
                LastVoxelChunkAcquireMilliseconds);
            LastVoxelMeshSurfaceUploadMilliseconds = chunk.LastMeshSurfaceUploadMilliseconds;
            MaxVoxelMeshSurfaceUploadMilliseconds = Math.Max(
                MaxVoxelMeshSurfaceUploadMilliseconds,
                LastVoxelMeshSurfaceUploadMilliseconds);
            LastVoxelCollisionInstallMilliseconds = chunk.LastCollisionInstallMilliseconds;
            MaxVoxelCollisionInstallMilliseconds = Math.Max(
                MaxVoxelCollisionInstallMilliseconds,
                LastVoxelCollisionInstallMilliseconds);
            _chunks[result.Coordinate] = chunk;
            _voxelVisualCoverageRevision++;
            if (buildCollision && !chunk.IsCollisionReady)
                QueueRebuild(result.Coordinate);
            ulong postLoadStartUsec = Time.GetTicksUsec();
            OnChunkLoadedPhase5(result.Coordinate);
			OnFluidChunkLoaded(result.Coordinate);
			TryReleaseUneditedVisualChunkData(result.Coordinate, chunk);
            LastVoxelPostLoadMilliseconds = (Time.GetTicksUsec() - postLoadStartUsec) / 1000.0;
            MaxVoxelPostLoadMilliseconds = Math.Max(
                MaxVoxelPostLoadMilliseconds,
                LastVoxelPostLoadMilliseconds);
            uploaded++;
            LastVoxelMeshUploadMilliseconds += (Time.GetTicksUsec() - uploadStartUsec) / 1000.0;
            if ((Time.GetTicksUsec() - uploadStartUsec) / 1000.0 >= 4.0)
            {
                GD.Print(
                    $"Voxel chunk install profile {result.Coordinate}: total={(Time.GetTicksUsec() - uploadStartUsec) / 1000.0:0.00}ms, "
                    + $"acquire={LastVoxelChunkAcquireMilliseconds:0.00}ms, install={chunk.LastMeshInstallMilliseconds:0.00}ms, "
                    + $"tree={LastVoxelNodeTreeRegistrationMilliseconds:0.00}ms, material={LastVoxelMaterialPreparationMilliseconds:0.00}ms, "
                    + $"arrays={chunk.LastMeshArrayPreparationMilliseconds:0.00}ms, "
                    + $"surface={chunk.LastMeshSurfaceUploadMilliseconds:0.00}ms, "
                    + $"assign={chunk.LastMeshAssignmentMilliseconds:0.00}ms, "
                    + $"collision={chunk.LastCollisionInstallMilliseconds:0.00}ms, "
                    + $"postLoad={LastVoxelPostLoadMilliseconds:0.00}ms, "
                    + $"vertices={result.Mesh.Vertices.Length}, triangles={result.Mesh.Indices.Length / 3}, "
                    + $"collisionFaces={result.Mesh.CollisionFaces.Length / 3}");
            }
            if (uploaded >= uploadBudget
                || Time.GetTicksUsec() - startUsec >= timeBudgetUsec)
            {
                break;
            }
        }

        MaxVoxelMeshUploadMilliseconds = Math.Max(
            MaxVoxelMeshUploadMilliseconds,
            LastVoxelMeshUploadMilliseconds);
        VoxelMeshUploadedThisFrame = residentUploads + uploaded > 0;
        if (VoxelMeshUploadedThisFrame)
            _yieldNextReadyVoxelUploadToProxyFrame = true;
    }

    private void CancelPendingAsyncVoxelBuild(in RingChunkCoordinate coordinate)
    {
        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        _pendingVoxelBuildEpochs.Remove(canonical);
        _deferredVoxelBuildSet.Remove(canonical);
    }

    private void InvalidateAllAsyncVoxelBuilds()
    {
        _voxelMeshEpoch++;
        _voxelMeshCancellation.Cancel();
        _voxelMeshCancellation.Dispose();
        _voxelMeshCancellation = new CancellationTokenSource();
        _pendingVoxelBuildEpochs.Clear();
        _deferredVoxelBuilds.Clear();
        _deferredVoxelBuildSet.Clear();
        _activeVoxelMeshWorkers = 0;
        InvalidateAllResidentVoxelRebuilds();
        _hasLastStreamingCenter = false;
        while (_completedVoxelBuilds.TryDequeue(out _)) { }
    }

    private void ShutdownAsyncVoxelMeshing()
    {
        _voxelMeshShuttingDown = true;
        _voxelMeshCancellation.Cancel();
        _pendingVoxelBuildEpochs.Clear();
        _deferredVoxelBuilds.Clear();
        _deferredVoxelBuildSet.Clear();
        _activeVoxelMeshWorkers = 0;
        InvalidateAllResidentVoxelRebuilds();
        while (_completedVoxelBuilds.TryDequeue(out _)) { }
    }
}
