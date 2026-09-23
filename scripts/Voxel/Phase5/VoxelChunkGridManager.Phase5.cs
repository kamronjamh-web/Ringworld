using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase1;
using RingworldTechDemo.Voxel.Phase5;

namespace RingworldTechDemo.Voxel.Phase2;

public partial class VoxelChunkGridManager
{
    [Export] public bool EnableChunkLocalNavInvalidation { get; set; } = false;
    [Export(PropertyHint.Range, "0.0,2.0,0.01")] public float NavInvalidationDelaySeconds { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "1,32,1")] public int MaxQueuedNavInvalidationsPerFrame { get; set; } = 4;
    [Export] public bool LogNavInvalidationDebug { get; set; } = false;
    [Export] public bool EnableNavigationRegionBridge { get; set; } = false;
    [Export] public NodePath NavigationRegionBridgeRootPath { get; set; } = new NodePath("../ChunkNavRegions");
    [Export] public bool AutoCreateNavigationRegionsForLoadedChunks { get; set; } = false;
    [Export] public NavigationMesh? AutoCreatedNavigationMeshTemplate { get; set; }
    [Export] public bool LogNavigationRegionBridgeDebug { get; set; } = false;

    [Export] public bool EnableServerAuthoritativeEditPath { get; set; } = false;
    [Export(PropertyHint.Range, "1,4096,1")] public int LocalAuthorityId { get; set; } = 1;
    [Export] public bool SimulateLocalAuthorityMismatchForDebug { get; set; } = false;

    [Export] public bool EnableAnchoredBuildObjects { get; set; } = false;
    [Export] public NodePath AnchoredObjectsRootPath { get; set; } = new NodePath("AnchoredObjects");
    [Export] public bool LogAnchoredObjectOperations { get; set; } = false;

    [Export(PropertyHint.Range, "16,4096,1")] public int SoftVoxelMemoryBudgetMb { get; set; } = 256;
    [Export] public bool LogMemoryBudgetWarnings { get; set; } = false;
    [Export(PropertyHint.Range, "0.1,10.0,0.1")] public float MemoryBudgetLogIntervalSeconds { get; set; } = 1.0f;

    private readonly Dictionary<RingChunkCoordinate, uint> _chunkRevisions = new();
    private readonly Dictionary<RingChunkCoordinate, double> _queuedNavInvalidationDeadlines = new();
    private readonly Dictionary<RingChunkCoordinate, NavigationRegion3D> _chunkNavigationRegions = new();
    private readonly Dictionary<long, VoxelAnchoredObjectRecord> _anchoredObjects = new();
    private readonly Dictionary<long, Node3D> _anchoredObjectNodes = new();
    private Node3D? _anchoredObjectsRoot;
    private Node? _navigationRegionBridgeRoot;
    private long _nextAnchoredObjectId = 1;
    private uint _globalRevision;
    private ulong _navigationRegionBridgeUpdateCount;
    private double _memoryBudgetLogAccumulator;

    public event Action<RingChunkCoordinate>? ChunkNavigationInvalidated;
    public event Action<RingChunkCoordinate, uint>? ChunkRevisionChanged;
    public event Action<VoxelBlockEditResult>? AuthoritativeEditApplied;
    public event Action<VoxelBlockEditResult>? AuthoritativeEditProcessed;

    public ulong NavigationRegionBridgeUpdateCount => _navigationRegionBridgeUpdateCount;

    private void ProcessPhase5Runtime(double delta)
    {
        ProcessNavInvalidationQueue();
        ProcessMemoryBudgetWarnings(delta);
    }

    private void OnRebuildGridPhase5()
    {
        _queuedNavInvalidationDeadlines.Clear();
        _chunkNavigationRegions.Clear();
        _navigationRegionBridgeRoot = null;
        EnsureRevisionEntriesForLoadedChunks();

        foreach (RingChunkCoordinate coordinate in _chunks.Keys)
        {
            EnsureNavigationRegionRegistration(coordinate);
        }

        if (!EnableChunkLocalNavInvalidation)
        {
            return;
        }

        foreach (RingChunkCoordinate coordinate in _chunks.Keys)
        {
            QueueNavInvalidation(coordinate);
        }
    }

    private void OnClearExistingChunksPhase5()
    {
        _queuedNavInvalidationDeadlines.Clear();
        _chunkRevisions.Clear();
        _chunkNavigationRegions.Clear();
        _navigationRegionBridgeRoot = null;
        _globalRevision = 0;
        _navigationRegionBridgeUpdateCount = 0;
        _memoryBudgetLogAccumulator = 0.0;
    }

    private void OnChunkLoadedPhase5(in RingChunkCoordinate coordinate)
    {
        EnsureChunkRevisionEntry(coordinate);
        EnsureNavigationRegionRegistration(coordinate);

        if (EnableChunkLocalNavInvalidation)
        {
            QueueNavInvalidation(coordinate);
        }
    }

    private void OnChunkUnloadedPhase5(in RingChunkCoordinate coordinate)
    {
        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        _queuedNavInvalidationDeadlines.Remove(canonical);
        _chunkNavigationRegions.Remove(canonical);
    }

    private void OnSparseSnapshotAppliedPhase5()
    {
        EnsureRevisionEntriesForLoadedChunks();

        foreach (RingChunkCoordinate coordinate in _chunkOverrides.Keys)
        {
            EnsureChunkRevisionEntry(coordinate);
        }
    }

    private void OnVoxelEditedPhase5(in RingChunkCoordinate chunkCoord, in Vector3I localVoxel, BlockType newBlockType)
    {
        _ = newBlockType;

        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(chunkCoord);
        MarkChunkRevisionChanged(canonical);
        QueueNavInvalidationForEditedVoxel(canonical, localVoxel);
    }

    private void EnsureRevisionEntriesForLoadedChunks()
    {
        foreach (RingChunkCoordinate coordinate in _chunks.Keys)
        {
            EnsureChunkRevisionEntry(coordinate);
        }
    }

    private void EnsureChunkRevisionEntry(in RingChunkCoordinate coordinate)
    {
        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        if (!_chunkRevisions.ContainsKey(canonical))
        {
            _chunkRevisions[canonical] = 0;
        }
    }

    private uint GetChunkRevision(in RingChunkCoordinate coordinate)
    {
        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        return _chunkRevisions.TryGetValue(canonical, out uint revision) ? revision : 0;
    }

    private uint MarkChunkRevisionChanged(in RingChunkCoordinate coordinate)
    {
        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        uint nextRevision = GetChunkRevision(canonical) + 1;
        _chunkRevisions[canonical] = nextRevision;
        _globalRevision++;
        ChunkRevisionChanged?.Invoke(canonical, nextRevision);
        return nextRevision;
    }

    public IReadOnlyDictionary<RingChunkCoordinate, uint> BuildChunkRevisionSnapshot()
    {
        return new Dictionary<RingChunkCoordinate, uint>(_chunkRevisions);
    }

    private void QueueNavInvalidationForEditedVoxel(in RingChunkCoordinate centerCoord, in Vector3I localVoxel)
    {
        if (!EnableChunkLocalNavInvalidation)
        {
            return;
        }

        QueueNavInvalidation(centerCoord);

        if (localVoxel.X == 0)
        {
            QueueNavInvalidation(OffsetChunkCoordinate(centerCoord, -1, 0, 0));
        }
        else if (localVoxel.X == VoxelConstants.ChunkSizeX - 1)
        {
            QueueNavInvalidation(OffsetChunkCoordinate(centerCoord, +1, 0, 0));
        }

        if (localVoxel.Z == 0)
        {
            QueueNavInvalidation(OffsetChunkCoordinate(centerCoord, 0, -1, 0));
        }
        else if (localVoxel.Z == VoxelConstants.ChunkSizeZ - 1)
        {
            QueueNavInvalidation(OffsetChunkCoordinate(centerCoord, 0, +1, 0));
        }

        if (localVoxel.Y == 0)
        {
            QueueNavInvalidation(OffsetChunkCoordinate(centerCoord, 0, 0, -1));
        }
        else if (localVoxel.Y == VoxelConstants.ChunkSizeY - 1)
        {
            QueueNavInvalidation(OffsetChunkCoordinate(centerCoord, 0, 0, +1));
        }
    }

    private void QueueNavInvalidation(in RingChunkCoordinate coordinate)
    {
        if (!EnableChunkLocalNavInvalidation)
        {
            return;
        }

        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        if (!_chunks.ContainsKey(canonical))
        {
            return;
        }

        double dueTime = GetNowSeconds() + Mathf.Max(0.0f, NavInvalidationDelaySeconds);
        _queuedNavInvalidationDeadlines[canonical] = dueTime;
    }

    private void ProcessNavInvalidationQueue()
    {
        if (!EnableChunkLocalNavInvalidation)
        {
            if (_queuedNavInvalidationDeadlines.Count > 0)
            {
                _queuedNavInvalidationDeadlines.Clear();
            }

            return;
        }

        if (_queuedNavInvalidationDeadlines.Count == 0)
        {
            return;
        }

        double now = GetNowSeconds();
        int frameBudget = Mathf.Max(1, MaxQueuedNavInvalidationsPerFrame);
        var due = new List<KeyValuePair<RingChunkCoordinate, double>>();

        foreach (KeyValuePair<RingChunkCoordinate, double> pair in _queuedNavInvalidationDeadlines)
        {
            if (pair.Value <= now)
            {
                due.Add(pair);
            }
        }

        if (due.Count == 0)
        {
            return;
        }

        due.Sort((a, b) => a.Value.CompareTo(b.Value));

        int processed = 0;
        foreach (KeyValuePair<RingChunkCoordinate, double> pair in due)
        {
            if (processed >= frameBudget)
            {
                break;
            }

            _queuedNavInvalidationDeadlines.Remove(pair.Key);
            RefreshNavigationRegionBridge(pair.Key);
            ChunkNavigationInvalidated?.Invoke(pair.Key);

            if (LogNavInvalidationDebug)
            {
                GD.Print($"Voxel nav invalidated chunk {pair.Key.AroundRingChunk},{pair.Key.AcrossRingChunk},{pair.Key.VerticalChunk}");
            }

            processed++;
        }
    }

    public int GetRegisteredNavigationRegionCount()
    {
        PruneInvalidNavigationRegionCache();
        return _chunkNavigationRegions.Count;
    }

    private void PruneInvalidNavigationRegionCache()
    {
        if (_chunkNavigationRegions.Count == 0)
        {
            return;
        }

        var remove = new List<RingChunkCoordinate>();
        foreach ((RingChunkCoordinate coord, NavigationRegion3D region) in _chunkNavigationRegions)
        {
            if (region == null || !IsInstanceValid(region))
            {
                remove.Add(coord);
            }
        }

        foreach (RingChunkCoordinate coord in remove)
        {
            _chunkNavigationRegions.Remove(coord);
        }
    }

    private void EnsureNavigationRegionRegistration(in RingChunkCoordinate coordinate)
    {
        if (!EnableNavigationRegionBridge)
        {
            return;
        }

        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);

        if (_chunkNavigationRegions.TryGetValue(canonical, out NavigationRegion3D? cachedRegion)
            && cachedRegion != null
            && IsInstanceValid(cachedRegion))
        {
            return;
        }

        _chunkNavigationRegions.Remove(canonical);

        if (TryResolveNavigationRegionForChunk(canonical, out _))
        {
            return;
        }

        if (AutoCreateNavigationRegionsForLoadedChunks)
        {
            TryCreateNavigationRegionForChunk(canonical);
        }
    }

    private void RefreshNavigationRegionBridge(in RingChunkCoordinate coordinate)
    {
        if (!EnableNavigationRegionBridge)
        {
            return;
        }

        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);

        if (!TryResolveNavigationRegionForChunk(canonical, out NavigationRegion3D region))
        {
            if (AutoCreateNavigationRegionsForLoadedChunks)
            {
                TryCreateNavigationRegionForChunk(canonical);
            }

            if (!TryResolveNavigationRegionForChunk(canonical, out region))
            {
                return;
            }
        }

        if (region == null || !IsInstanceValid(region) || !region.IsInsideTree())
        {
            _chunkNavigationRegions.Remove(canonical);
            return;
        }

        if (_chunks.TryGetValue(canonical, out VoxelChunkNode? chunk))
        {
            region.GlobalTransform = chunk.GlobalTransform;
        }

        Rid regionRid = region.GetRid();
        if (regionRid.IsValid)
        {
            NavigationServer3D.RegionSetTransform(regionRid, region.GlobalTransform);

            if (region.NavigationMesh != null)
            {
                NavigationServer3D.RegionSetNavigationMesh(regionRid, region.NavigationMesh);
            }

            bool enabled = region.Enabled;
            NavigationServer3D.RegionSetEnabled(regionRid, false);
            NavigationServer3D.RegionSetEnabled(regionRid, enabled);
            _navigationRegionBridgeUpdateCount++;
        }

        if (LogNavigationRegionBridgeDebug)
        {
            GD.Print($"Navigation region bridge refreshed chunk {canonical.AroundRingChunk},{canonical.AcrossRingChunk},{canonical.VerticalChunk}");
        }
    }

    private bool TryResolveNavigationRegionForChunk(in RingChunkCoordinate coordinate, out NavigationRegion3D region)
    {
        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);

        if (_chunkNavigationRegions.TryGetValue(canonical, out NavigationRegion3D? cached)
            && cached != null
            && IsInstanceValid(cached))
        {
            region = cached;
            return true;
        }

        _chunkNavigationRegions.Remove(canonical);

        if (!TryResolveNavigationRegionBridgeRoot(out Node bridgeRoot))
        {
            region = null!;
            return false;
        }

        string regionName = BuildNavigationRegionNodeName(canonical);
        NavigationRegion3D? resolved = bridgeRoot.GetNodeOrNull<NavigationRegion3D>(regionName);
        if (resolved == null)
        {
            resolved = bridgeRoot.FindChild(regionName, recursive: true, owned: false) as NavigationRegion3D;
        }

        if (resolved == null)
        {
            region = null!;
            return false;
        }

        _chunkNavigationRegions[canonical] = resolved;
        region = resolved;
        return true;
    }

    private bool TryResolveNavigationRegionBridgeRoot(out Node bridgeRoot)
    {
        if (_navigationRegionBridgeRoot != null && IsInstanceValid(_navigationRegionBridgeRoot))
        {
            bridgeRoot = _navigationRegionBridgeRoot;
            return true;
        }

        _navigationRegionBridgeRoot = null;
        bridgeRoot = null!;

        if (NavigationRegionBridgeRootPath.IsEmpty)
        {
            return false;
        }

        _navigationRegionBridgeRoot = GetNodeOrNull<Node>(NavigationRegionBridgeRootPath);
        if (_navigationRegionBridgeRoot == null)
        {
            return false;
        }

        bridgeRoot = _navigationRegionBridgeRoot;
        return true;
    }

    private void TryCreateNavigationRegionForChunk(in RingChunkCoordinate coordinate)
    {
        if (!AutoCreateNavigationRegionsForLoadedChunks || !TryResolveNavigationRegionBridgeRoot(out Node bridgeRoot))
        {
            return;
        }

        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(coordinate);
        string regionName = BuildNavigationRegionNodeName(canonical);

        NavigationRegion3D? existing = bridgeRoot.GetNodeOrNull<NavigationRegion3D>(regionName);
        if (existing != null)
        {
            _chunkNavigationRegions[canonical] = existing;
            return;
        }

        var created = new NavigationRegion3D
        {
            Name = regionName,
            Enabled = true,
            NavigationMesh = AutoCreatedNavigationMeshTemplate
        };

        bridgeRoot.AddChild(created);

        if (_chunks.TryGetValue(canonical, out VoxelChunkNode? chunk))
        {
            created.GlobalTransform = chunk.GlobalTransform;
        }

        _chunkNavigationRegions[canonical] = created;

        if (LogNavigationRegionBridgeDebug)
        {
            GD.Print($"Navigation region bridge created region node for chunk {canonical.AroundRingChunk},{canonical.AcrossRingChunk},{canonical.VerticalChunk}");
        }
    }

    private static string BuildNavigationRegionNodeName(in RingChunkCoordinate coordinate)
    {
        return $"NavRegion_{coordinate.AroundRingChunk}_{coordinate.AcrossRingChunk}_{coordinate.VerticalChunk}";
    }

    private int ResolveLocalCommandAuthorityId()
    {
        int localAuthority = Math.Max(1, LocalAuthorityId);
        if (!EnableServerAuthoritativeEditPath || !SimulateLocalAuthorityMismatchForDebug)
        {
            return localAuthority;
        }

        int mismatchedAuthority = localAuthority + 1;
        if (mismatchedAuthority > 4096)
        {
            mismatchedAuthority = 1;
        }

        if (mismatchedAuthority == localAuthority)
        {
            mismatchedAuthority = localAuthority == 1 ? 2 : 1;
        }

        return mismatchedAuthority;
    }

    public bool TryApplyAuthoritativeEditCommand(in VoxelBlockEditCommand command, out VoxelBlockEditResult result)
    {
        EnsurePhase7ReplayBaselineCaptured();

        if (EnableServerAuthoritativeEditPath && command.AuthorityId != LocalAuthorityId)
        {
            result = new VoxelBlockEditResult(
                Applied: false,
                Reason: "authority_mismatch",
                ChunkCoordinate: default,
                LocalVoxel: default,
                PreviousBlockType: BlockType.Air,
                NewBlockType: command.BlockType,
                ChunkRevision: 0,
                GlobalRevision: _globalRevision);
            OnAuthoritativeEditProcessedPhase7(command, result);
            AuthoritativeEditProcessed?.Invoke(result);
            return false;
        }

        if (!TryResolveChunkAndLocalFromVoxel(command.TargetVoxel, out RingChunkCoordinate chunkCoord, out Vector3I localVoxel))
        {
            result = new VoxelBlockEditResult(
                Applied: false,
                Reason: "target_out_of_bounds",
                ChunkCoordinate: default,
                LocalVoxel: default,
                PreviousBlockType: BlockType.Air,
                NewBlockType: command.BlockType,
                ChunkRevision: 0,
                GlobalRevision: _globalRevision);
            OnAuthoritativeEditProcessedPhase7(command, result);
            AuthoritativeEditProcessed?.Invoke(result);
            return false;
        }

        RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(chunkCoord);
        VoxelChunkNode chunk = EnsureChunkLoaded(canonical);

        if (!chunk.Data.IsInBounds(localVoxel.X, localVoxel.Y, localVoxel.Z))
        {
            result = new VoxelBlockEditResult(
                Applied: false,
                Reason: "local_voxel_out_of_bounds",
                ChunkCoordinate: canonical,
                LocalVoxel: localVoxel,
                PreviousBlockType: BlockType.Air,
                NewBlockType: command.BlockType,
                ChunkRevision: GetChunkRevision(canonical),
                GlobalRevision: _globalRevision);
            OnAuthoritativeEditProcessedPhase7(command, result);
            AuthoritativeEditProcessed?.Invoke(result);
            return false;
        }

        BlockType previousBlock = chunk.Data.GetBlock(localVoxel.X, localVoxel.Y, localVoxel.Z);
        if (command.RequireTargetAir && previousBlock != BlockType.Air)
        {
            result = new VoxelBlockEditResult(
                Applied: false,
                Reason: "target_not_air",
                ChunkCoordinate: canonical,
                LocalVoxel: localVoxel,
                PreviousBlockType: previousBlock,
                NewBlockType: command.BlockType,
                ChunkRevision: GetChunkRevision(canonical),
                GlobalRevision: _globalRevision);
            OnAuthoritativeEditProcessedPhase7(command, result);
            AuthoritativeEditProcessed?.Invoke(result);
            return false;
        }

        RingVoxelCoordinate globalVoxel = LocalToGlobalVoxel(canonical, localVoxel);
        if (!IsVoxelEditAllowed(previousBlock, command.BlockType, globalVoxel))
        {
            result = new VoxelBlockEditResult(
                Applied: false,
                Reason: "edit_not_allowed",
                ChunkCoordinate: canonical,
                LocalVoxel: localVoxel,
                PreviousBlockType: previousBlock,
                NewBlockType: command.BlockType,
                ChunkRevision: GetChunkRevision(canonical),
                GlobalRevision: _globalRevision);
            OnAuthoritativeEditProcessedPhase7(command, result);
            AuthoritativeEditProcessed?.Invoke(result);
            return false;
        }

        bool didSet = chunk.TrySetBlock(localVoxel, command.BlockType, rebuild: false);
        if (!didSet)
        {
            result = new VoxelBlockEditResult(
                Applied: false,
                Reason: "set_block_failed",
                ChunkCoordinate: canonical,
                LocalVoxel: localVoxel,
                PreviousBlockType: previousBlock,
                NewBlockType: command.BlockType,
                ChunkRevision: GetChunkRevision(canonical),
                GlobalRevision: _globalRevision);
            OnAuthoritativeEditProcessedPhase7(command, result);
            AuthoritativeEditProcessed?.Invoke(result);
            return false;
        }

        UpdateOverrideForVoxel(canonical, localVoxel, chunk);
        RebuildChunkAndNeighbors(canonical, localVoxel);
        OnVoxelEditedPhase5(canonical, localVoxel, command.BlockType);

        result = new VoxelBlockEditResult(
            Applied: true,
            Reason: "applied",
            ChunkCoordinate: canonical,
            LocalVoxel: localVoxel,
            PreviousBlockType: previousBlock,
            NewBlockType: command.BlockType,
            ChunkRevision: GetChunkRevision(canonical),
            GlobalRevision: _globalRevision);

        OnAuthoritativeEditProcessedPhase7(command, result);
        AuthoritativeEditProcessed?.Invoke(result);
        AuthoritativeEditApplied?.Invoke(result);
        return true;
    }

    private bool TryResolveChunkAndLocalFromVoxel(in RingVoxelCoordinate voxelCoordinate, out RingChunkCoordinate chunkCoord, out Vector3I localVoxel)
    {
        bool localOk = RingCoordMath.TryToLocalVoxel(
            voxelCoordinate,
            GetAroundRingVoxelLength(),
            out chunkCoord,
            out localVoxel);

        if (!localOk)
        {
            chunkCoord = default;
            localVoxel = default;
            return false;
        }

        chunkCoord = CanonicalizeChunkCoordinate(chunkCoord);
        return true;
    }

    public VoxelReplicationSnapshot BuildReplicationSnapshot(uint minChunkRevisionExclusive = 0, bool includeChunkOverrides = true)
    {
        EnsureRevisionEntriesForLoadedChunks();

        var snapshot = new VoxelReplicationSnapshot
        {
            AuthorityId = LocalAuthorityId,
            GlobalRevision = _globalRevision
        };

        foreach ((RingChunkCoordinate coord, uint revision) in _chunkRevisions)
        {
            if (revision <= minChunkRevisionExclusive)
            {
                continue;
            }

            string key = ChunkCoordToKey(coord);
            snapshot.ChunkRevisions[key] = revision;

            if (!includeChunkOverrides)
            {
                continue;
            }

            // An empty payload is an explicit tombstone: this revision restores
            // the chunk to its procedural baseline and clears remote overrides.
            snapshot.ChunkOverrides[key] = _chunkOverrides.TryGetValue(coord, out Dictionary<int, ushort>? overrides)
                ? new Dictionary<int, ushort>(overrides)
                : new Dictionary<int, ushort>();
        }

        snapshot.WaterSources = CaptureDynamicWaterSources();

        return snapshot;
    }

    public void ApplyReplicationSnapshot(VoxelReplicationSnapshot snapshot, bool flushRebuildQueueAfterApply = true)
    {
		ApplyDynamicWaterSources(snapshot.WaterSources);
        var changedChunks = new HashSet<RingChunkCoordinate>();
        foreach ((string chunkKey, uint revision) in snapshot.ChunkRevisions)
        {
            if (!TryParseChunkCoordKey(chunkKey, out RingChunkCoordinate parsed))
            {
                continue;
            }

            RingChunkCoordinate canonical = CanonicalizeChunkCoordinate(parsed);
            uint existing = GetChunkRevision(canonical);
            if (revision < existing)
            {
                continue;
            }

            if (revision > existing) _chunkRevisions[canonical] = revision;
            if (!snapshot.ChunkOverrides.TryGetValue(chunkKey, out Dictionary<int, ushort>? overrides))
            {
                continue;
            }

            if (overrides.Count == 0)
            {
                _chunkOverrides.Remove(canonical);
            }
            else
            {
                _chunkOverrides[canonical] = new Dictionary<int, ushort>(overrides);
            }
            _knownEmptyStreamingChunks.Remove(canonical);
            _knownSolidStreamingChunks.Remove(canonical);
            changedChunks.Add(canonical);
        }

        _globalRevision = Math.Max(_globalRevision, snapshot.GlobalRevision);
        if (changedChunks.Count == 0) return;

        // An edit can expose a face owned by an adjacent virtual solid chunk.
        // Reclassify the desired window on the next streaming update.
        _knownSolidStreamingChunks.Clear();
        _hasLastStreamingCenter = false;
        InvalidateLodSurfaceEditSnapshot();

        foreach ((RingChunkCoordinate coord, VoxelChunkNode chunk) in _chunks)
        {
            if (!changedChunks.Contains(coord)) continue;
            PopulateChunkBaseline(chunk, coord);
            ApplyOverridesToChunkData(coord, chunk);
            QueueRebuild(coord);
            QueueRebuild(OffsetChunkCoordinate(coord, -1, 0, 0));
            QueueRebuild(OffsetChunkCoordinate(coord, +1, 0, 0));
            QueueRebuild(OffsetChunkCoordinate(coord, 0, -1, 0));
            QueueRebuild(OffsetChunkCoordinate(coord, 0, +1, 0));
            QueueRebuild(OffsetChunkCoordinate(coord, 0, 0, -1));
            QueueRebuild(OffsetChunkCoordinate(coord, 0, 0, +1));
        }

        if (flushRebuildQueueAfterApply)
        {
            FlushRebuildQueue();
        }
    }

    public bool TryPlaceAnchoredObjectByWorldPosition(
        Vector3 worldPosition,
        PackedScene anchoredScene,
        string archetypeId,
        out long anchorId)
    {
        anchorId = 0;

        if (!EnableAnchoredBuildObjects)
        {
            return false;
        }

        if (anchoredScene == null)
        {
            return false;
        }

        if (!TryGetChunkAtWorldPosition(worldPosition, out VoxelChunkNode chunk, out RingChunkCoordinate chunkCoord))
        {
            return false;
        }

        Vector3I localVoxel = chunk.WorldToLocalVoxel(worldPosition);
        if (!chunk.Data.IsInBounds(localVoxel.X, localVoxel.Y, localVoxel.Z))
        {
            return false;
        }

        BlockType supportBlock = chunk.Data.GetBlock(localVoxel.X, localVoxel.Y, localVoxel.Z);
        if (supportBlock == BlockType.Air)
        {
            return false;
        }

        if (anchoredScene.Instantiate() is not Node3D instance)
        {
            return false;
        }

        ResolveAnchoredObjectsRoot();
        _anchoredObjectsRoot!.AddChild(instance);

        RingChunkCoordinate canonicalChunk = CanonicalizeChunkCoordinate(chunkCoord);
        RingVoxelCoordinate anchorVoxel = RingCoordMath.ToVoxelCoordinate(
            canonicalChunk,
            localVoxel,
            GetAroundRingVoxelLength());

        Vector3 anchorTopCenterLinear = new Vector3(localVoxel.X + 0.5f, localVoxel.Y + 1.0f, localVoxel.Z + 0.5f);
        instance.GlobalPosition = chunk.LinearLocalToWorldPoint(anchorTopCenterLinear);

        anchorId = _nextAnchoredObjectId++;
        string scenePath = string.IsNullOrWhiteSpace(anchoredScene.ResourcePath)
            ? string.Empty
            : anchoredScene.ResourcePath;

        var record = new VoxelAnchoredObjectRecord
        {
            AnchorId = anchorId,
            ArchetypeId = archetypeId,
            AnchorVoxel = anchorVoxel,
            ScenePath = scenePath,
            Rotation = instance.Quaternion,
            LocalOffset = Vector3.Zero
        };

        _anchoredObjects[anchorId] = record;
        _anchoredObjectNodes[anchorId] = instance;

        if (LogAnchoredObjectOperations)
        {
            GD.Print($"Anchored object placed id={anchorId} archetype={archetypeId}");
        }

        return true;
    }

    public bool TryRemoveAnchoredObject(long anchorId)
    {
        bool removed = false;

        if (_anchoredObjectNodes.TryGetValue(anchorId, out Node3D? node))
        {
            _anchoredObjectNodes.Remove(anchorId);
            if (node != null && IsInstanceValid(node))
            {
                node.QueueFree();
            }

            removed = true;
        }

        if (_anchoredObjects.Remove(anchorId))
        {
            removed = true;
        }

        if (removed && LogAnchoredObjectOperations)
        {
            GD.Print($"Anchored object removed id={anchorId}");
        }

        return removed;
    }

    public IReadOnlyDictionary<long, VoxelAnchoredObjectRecord> BuildAnchoredObjectSnapshot()
    {
        return new Dictionary<long, VoxelAnchoredObjectRecord>(_anchoredObjects);
    }

    private void ResolveAnchoredObjectsRoot()
    {
        if (_anchoredObjectsRoot != null && IsInstanceValid(_anchoredObjectsRoot))
        {
            return;
        }

        if (!AnchoredObjectsRootPath.IsEmpty)
        {
            _anchoredObjectsRoot = GetNodeOrNull<Node3D>(AnchoredObjectsRootPath);
        }

        if (_anchoredObjectsRoot != null)
        {
            return;
        }

        _anchoredObjectsRoot = new Node3D { Name = "AnchoredObjects" };
        AddChild(_anchoredObjectsRoot);
    }

    public VoxelRuntimeMetricsSnapshot BuildRuntimeMetricsSnapshot()
    {
        int overrideVoxelCount = 0;
        foreach (Dictionary<int, ushort> overrides in _chunkOverrides.Values)
        {
            overrideVoxelCount += overrides.Count;
        }

        const int estimatedAnchoredBytesPerRecord = 192;

        long estimatedChunkDataBytes = (long)_chunks.Count * VoxelConstants.ChunkVolume * sizeof(byte);
        long estimatedOverrideBytes = (long)overrideVoxelCount * (sizeof(int) + sizeof(ushort));
        long estimatedAnchoredObjectBytes = (long)_anchoredObjects.Count * estimatedAnchoredBytesPerRecord;
        long estimatedTotalBytes = estimatedChunkDataBytes + estimatedOverrideBytes + estimatedAnchoredObjectBytes;

        long softBudgetBytes = (long)Math.Max(1, SoftVoxelMemoryBudgetMb) * 1024 * 1024;
        bool exceedsBudget = estimatedTotalBytes > softBudgetBytes;

        return new VoxelRuntimeMetricsSnapshot(
            LoadedChunkCount: _chunks.Count,
            QueuedRebuildCount: _queuedRebuildDeadlines.Count,
            QueuedNavInvalidationCount: _queuedNavInvalidationDeadlines.Count,
            OverrideChunkCount: _chunkOverrides.Count,
            OverrideVoxelCount: overrideVoxelCount,
            AnchoredObjectCount: _anchoredObjects.Count,
            EstimatedChunkDataBytes: estimatedChunkDataBytes,
            EstimatedOverrideBytes: estimatedOverrideBytes,
            EstimatedAnchoredObjectBytes: estimatedAnchoredObjectBytes,
            EstimatedTotalBytes: estimatedTotalBytes,
            ExceedsSoftMemoryBudget: exceedsBudget,
            SoftMemoryBudgetMb: Math.Max(1, SoftVoxelMemoryBudgetMb));
    }

    public string BuildRuntimeMetricsReport()
    {
        VoxelRuntimeMetricsSnapshot metrics = BuildRuntimeMetricsSnapshot();

        const double bytesToMb = 1.0 / (1024.0 * 1024.0);
        var builder = new StringBuilder();
        builder.AppendLine("Voxel Runtime Metrics");
        builder.AppendLine($"Loaded chunks: {metrics.LoadedChunkCount}");
        builder.AppendLine($"Queued rebuilds: {metrics.QueuedRebuildCount}");
        builder.AppendLine($"Queued nav invalidations: {metrics.QueuedNavInvalidationCount}");
        builder.AppendLine($"Override chunks: {metrics.OverrideChunkCount}");
        builder.AppendLine($"Override voxels: {metrics.OverrideVoxelCount}");
        builder.AppendLine($"Anchored objects: {metrics.AnchoredObjectCount}");
        builder.AppendLine($"Chunk data est: {metrics.EstimatedChunkDataBytes * bytesToMb:0.00} MB");
        builder.AppendLine($"Override data est: {metrics.EstimatedOverrideBytes * bytesToMb:0.00} MB");
        builder.AppendLine($"Anchored data est: {metrics.EstimatedAnchoredObjectBytes * bytesToMb:0.00} MB");
        builder.AppendLine($"Total est: {metrics.EstimatedTotalBytes * bytesToMb:0.00} MB");
        builder.AppendLine($"Soft budget: {metrics.SoftMemoryBudgetMb} MB");
        builder.AppendLine($"Budget exceeded: {metrics.ExceedsSoftMemoryBudget}");
        return builder.ToString();
    }

    private void ProcessMemoryBudgetWarnings(double delta)
    {
        _memoryBudgetLogAccumulator += delta;
        double interval = Math.Max(0.1, MemoryBudgetLogIntervalSeconds);
        if (_memoryBudgetLogAccumulator < interval)
        {
            return;
        }

        _memoryBudgetLogAccumulator = 0.0;

        VoxelRuntimeMetricsSnapshot metrics = BuildRuntimeMetricsSnapshot();
        SetVoxelDataMemoryPressure(metrics.ExceedsSoftMemoryBudget);
        if (!LogMemoryBudgetWarnings)
        {
            return;
        }
        if (!metrics.ExceedsSoftMemoryBudget)
        {
            return;
        }

        double estimatedMb = metrics.EstimatedTotalBytes / (1024.0 * 1024.0);
        GD.PushWarning(
            $"Voxel memory budget exceeded: est={estimatedMb:0.00}MB budget={metrics.SoftMemoryBudgetMb}MB "
            + $"chunks={metrics.LoadedChunkCount} overrides={metrics.OverrideVoxelCount} anchors={metrics.AnchoredObjectCount}");
    }
}
