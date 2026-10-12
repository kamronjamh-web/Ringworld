using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingTerrainProxySector
{
    private bool _loadingShutdownRequested;

    /// <summary>Stops new loading work without waiting on the scene thread.</summary>
    public void BeginLoadingShutdown()
    {
        if (_loadingShutdownRequested) return;
        _loadingShutdownRequested = true;
        _coordinatorBuildRequested = false;
        _coordinatorForceBuildRequested = false;
        _refreshQueued = false;
        _forceRefreshQueued = false;
        _queuedStructuralWall = null;
        _buildCancellation?.Cancel();
        // Release staging backpressure so cancelled publishers can finish.
        ClearPendingUploads();
        SetProcess(false);
    }

    /// <summary>Poll on the scene thread before freeing the loading world.</summary>
    public bool LoadingShutdownReady
    {
        get
        {
            if (!_loadingShutdownRequested) return false;
            if (_pending != null && !_pending.IsCompleted) return false;
            if (_pendingStructuralWall != null && !_pendingStructuralWall.IsCompleted) return false;
            // Observing faults prevents detached task exceptions; no results
            // from this abandoned world are installed into renderer instances.
            if (_pending?.IsFaulted == true) _ = _pending.Exception;
            if (_pendingStructuralWall?.IsFaulted == true) _ = _pendingStructuralWall.Exception;
            _pending = null;
            _pendingStructuralWall = null;
            _activeWorkerSlots = 0;
            DisposePendingWorkerSampler();
            _buildCancellation?.Dispose();
            _buildCancellation = null;
            ClearPendingUploads();
            return true;
        }
    }

    public Godot.Collections.Dictionary GetLoadingShutdownStatus() => new()
    {
        ["requested"] = _loadingShutdownRequested,
        ["ready"] = LoadingShutdownReady,
        ["terrain_pending"] = _pending?.IsCompleted == false,
        ["structure_pending"] = _pendingStructuralWall?.IsCompleted == false
    };
}
