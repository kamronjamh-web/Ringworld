using System;
using Godot;
using Godot.Collections;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Main-thread two-resident prepared-window staging facade.</summary>
public partial class RingTerrain3DSurface
{
    private RingTerrain3DSurface? _activePreparedSurface;
    private RingTerrain3DSurface? _stagedPreparedSurface;
    private bool _stagingCandidate;
    private long _stagedRevision;
    private string _stagingError = string.Empty;
    private int _preparedMeshLods = 4;
    private int _preparedMeshSize = 48;
    private RingTerrain3DSurface? _idlePreparedSurface;
    private bool _isBudgetedPreparation;

    /// <summary>
    /// Keeps synchronous field startup for save/load compatibility, then makes
    /// this node a prepared-window facade for all later gameplay replacements.
    /// </summary>
    public void InitializeGameplayWindow(RingSurfaceHeightField field, RingSurfaceFrame frame, RingTerrainSampler sampler)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(sampler);
        if (_field != null || _activePreparedSurface != null || _stagedPreparedSurface != null)
            throw new InvalidOperationException("Gameplay window initialization requires an unused surface facade.");

        var active = new RingTerrain3DSurface { Name = "RingTerrain3DGameplayActive" };
        AddChild(active);
        active.SetSmoothTerrainShading(_smoothTerrainShading);
        active.SetWorldFrame(_worldFrame);
        active.SetHandoffDistance(_handoffDistanceMeters);
        active.SetMeshLods(_preparedMeshLods);
        active.SetMeshSize(_preparedMeshSize);
        active.SetBiomeSampler(sampler);
        active.Initialize(field, frame);
        if (!active.IsReady) throw new InvalidOperationException(active.LastError);
        active.SetMeshLods(_preparedMeshLods);
        active.SetMeshSize(_preparedMeshSize);
        active._stagingCandidate = true;
        active._isBudgetedPreparation = true;
        if (active._collisionBody != null) { active._collisionBody.CollisionLayer = 1; active._collisionBody.CollisionMask = 1; }
        active._committedPreparedRevision = active._renderPreparedRevision = active._queryPreparedRevision = active._collisionPreparedRevision = 1;
        active._budgetArraysReady = true;
        active._field = null;
        _activePreparedSurface = active;
        _field = null;

        // Prewarm a second, hidden native owner. It intentionally has no maps or
        // collision shape yet, so startup does not resample a second window.
        var idle = new RingTerrain3DSurface { Name = "RingTerrain3DGameplayIdle", Visible = false, _stagingCandidate = true, _isBudgetedPreparation = true };
        AddChild(idle);
        idle.SetSmoothTerrainShading(_smoothTerrainShading);
        idle.SetWorldFrame(_worldFrame);
        idle.SetHandoffDistance(_handoffDistanceMeters);
        idle.SetMeshLods(_preparedMeshLods);
        idle.SetMeshSize(_preparedMeshSize);
        idle.SetBiomeSampler(sampler);
        idle.EnsurePreparedNative(frame);
        idle.Visible = false;
        _idlePreparedSurface = idle;
        _isReady = true;
        _lastError = _stagingError = string.Empty;
        ReplayFollowersFromActive();
    }

    /// <summary>Applies independently rebased frames while a gameplay stage is pending.</summary>
    public void SetGameplayWorldFrames(Transform3D activeWorld, Transform3D stagedWorld)
    {
        _worldFrame = activeWorld;
        if (_activePreparedSurface != null) _activePreparedSurface.SetWorldFrame(activeWorld);
        else SetWorldFrame(activeWorld);
        _stagedPreparedSurface?.SetWorldFrame(stagedWorld);
    }

    public RingSurfaceFrame? StagedFrame => _stagedPreparedSurface?._frame;

    public bool StagePreparedWindow(float[] absoluteHeights, Array<Image> heightImages, long anchorAround,
        int anchorAcross, float frameHeight, int acrossDirection, long revision, bool directRegions = true)
    {
        if (_field != null || _stagingCandidate)
        {
            _stagingError = "This surface cannot own prepared replacements.";
            return false;
        }
        long live = _activePreparedSurface?._committedPreparedRevision ?? _committedPreparedRevision;
        if (revision <= live || (_stagedPreparedSurface != null && revision <= _stagedRevision))
        {
            _stagingError = "Prepared stage revision is stale.";
            return false;
        }
        // A bad request must not destroy a valid replacement waiting for coverage.
        if (!ValidatePreparedWindow(absoluteHeights, heightImages, anchorAround, anchorAcross,
                frameHeight, acrossDirection, revision, out string error, out _))
        {
            _stagingError = error;
            return false;
        }
        DiscardStagedWindow();
        // The synchronous compatibility path does not retain an inactive pool.
        _idlePreparedSurface?.Free();
        _idlePreparedSurface = null;
        var candidate = new RingTerrain3DSurface
        {
            Name = "RingTerrain3DStaged", Visible = false, _stagingCandidate = true
        };
        AddChild(candidate);
        candidate.SetSmoothTerrainShading(_smoothTerrainShading);
        candidate.SetWorldFrame(_worldFrame);
        candidate.SetHandoffDistance(_handoffDistanceMeters);
        candidate.SetMeshLods(_preparedMeshLods);
        candidate.SetMeshSize(_preparedMeshSize);
        if (!candidate.CommitPreparedWindow(absoluteHeights, heightImages, anchorAround, anchorAcross,
                frameHeight, acrossDirection, revision, directRegions))
        {
            _stagingError = candidate.LastError;
            candidate.Free();
            return false;
        }
        _stagedPreparedSurface = candidate;
        _stagedRevision = revision;
        _stagingError = string.Empty;
        _lastError = string.Empty;
        if (_hasLocalPhysicalCameraPosition) UpdateCamera(_lastLocalPhysicalCameraPosition);
        return true;
    }

    public bool CanActivateStagedWindow(double around, double across, float marginMeters = 16)
    {
        if (_stagedPreparedSurface == null || !_stagedPreparedSurface.IsReady || !double.IsFinite(around) || !double.IsFinite(across) || !float.IsFinite(marginMeters) || marginMeters < 0 || marginMeters >= 192) return false;
        double a = _stagedPreparedSurface._frame.SignedAroundDeltaMeters(around);
        double z = (across - _stagedPreparedSurface._frame.AnchorAcrossMeters) * _stagedPreparedSurface._frame.AcrossDirection;
        // Canonical coordinates can come back through float physical transforms.
        // A sub-millimetre round trip must not strand a ready replacement at the margin.
        double limit = Math.Min(192, 192 - marginMeters + 0.001);
        return Math.Abs(a) <= limit && Math.Abs(z) <= limit;
    }

    public bool ActivateStagedWindow(long revision)
    {
        long live = _activePreparedSurface?._committedPreparedRevision ?? _committedPreparedRevision;
        if (_stagedPreparedSurface == null || !_stagedPreparedSurface.IsReady ||
            _stagedPreparedSurface._collisionBody == null || revision != _stagedRevision || revision <= live)
            return false;

        // Called at the controller's physics boundary. The new shape already exists;
        // no sampling, upload, or shape construction is deferred until this handoff.
        RingTerrain3DSurface? previous = _activePreparedSurface;
        StaticBody3D? previousBody = previous?._collisionBody ?? _collisionBody;
        if (previousBody != null) { previousBody.CollisionLayer = 0; previousBody.CollisionMask = 0; }
        if (previous != null) previous.Visible = false;
        _activePreparedSurface = _stagedPreparedSurface;
        _stagedPreparedSurface = null;
        _stagedRevision = 0;
        _activePreparedSurface.Visible = true;
        _activePreparedSurface._collisionBody.CollisionLayer = 1;
        _activePreparedSurface._collisionBody.CollisionMask = 1;
        if (previous != null)
        {
            if (_activePreparedSurface._isBudgetedPreparation)
            {
                _idlePreparedSurface?.Free();
                _idlePreparedSurface = previous;
            }
            else previous.Free();
        }
        else DestroyNativeNodes();
        ReplayFollowersFromActive();
        _stagingError = string.Empty;
        _lastError = string.Empty;
        return true;
    }

    public float SampleStagedHeight(double around, double across) => _stagedPreparedSurface?.SampleCommittedHeight(around, across) ?? float.NaN;
    public void DiscardStagedWindow()
    {
        if (_stagedPreparedSurface?._isBudgetedPreparation == true)
        {
            _stagedPreparedSurface.CancelBudgetPreparation();
            _stagedPreparedSurface.Visible = false;
            if (_stagedPreparedSurface._collisionBody != null) { _stagedPreparedSurface._collisionBody.CollisionLayer = 0; _stagedPreparedSurface._collisionBody.CollisionMask = 0; }
            _idlePreparedSurface?.Free();
            _idlePreparedSurface = _stagedPreparedSurface;
        }
        else _stagedPreparedSurface?.Free();
        _stagedPreparedSurface = null;
        _stagedRevision = 0;
        _stagingError = string.Empty;
    }
}
