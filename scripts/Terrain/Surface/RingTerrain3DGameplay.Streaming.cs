using System;
using System.Collections.Generic;
using Godot;
using Godot.Collections;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Asynchronous gameplay-window request, publication, and collision coverage policy.</summary>
public partial class RingTerrain3DGameplay
{
    private double _lastReadinessMilliseconds;
    private double _maxReadinessMilliseconds;
    private long _coverageWaits;
    private bool _coverageWaiting;
    private bool _budgetStarted;
    private bool _stageReady;
    private ulong _committedEditRevision;
    private ulong _editDebounceUntilUsec;
    private ulong _requestStartedUsec;

    public override void _PhysicsProcess(double delta)
    {
        if (_startupPending)
        {
            TryActivateStagedStartup();
            return;
        }
        if (_synchronousBenchmark) return;
        ServiceUndergroundRecovery(delta);
        if (_saveBlocked || _surface == null || _explorer == null || !_hasPendingFrame || !_stageReady) return;
        RingCanonicalPosition current = _explorer.CanonicalPosition;
        double activeAround = _frame.SignedAroundDeltaMeters(current.AroundMeters);
        double activeAcross = current.AcrossMeters - _frame.AnchorAcrossMeters;
        if (!_pendingEditReplacement && Math.Abs(activeAround) < 128.0 && Math.Abs(activeAcross) < 128.0) return;
        if (_field == null || _pendingEditRevision != _field.Revision || _pendingApertureRevision != _apertureRevision || !FrameContainsCollider(_pendingFrame, current)) return;
        Vector3 velocity = _explorer.CanonicalVelocity;
        var predicted = new RingCanonicalPosition(current.AroundMeters + velocity.X * delta,
            current.VerticalMeters, current.AcrossMeters + velocity.Z * delta);
        if (!FrameContainsCollider(_pendingFrame, predicted)) return;
        if (!_surface.CanActivateStagedWindow(current.AroundMeters, current.AcrossMeters, 0.0f)) return;
        if (!_surface.ActivateStagedWindow(_requestRevision)) return;
        _frame = _pendingFrame;
        _hasPendingFrame = false;
        _stageReady = false;
        _pendingEditReplacement = false;
        _committedRevision = _requestRevision;
        _committedEditRevision = _pendingEditRevision;
        _committedApertureRevision = _pendingApertureRevision;
        _lastWorldFrame = BuildWorldFrame(_frame);
        _surface.SetGameplayWorldFrames(_lastWorldFrame, _lastWorldFrame);
        float height = SampleActivationHeight(current.AroundMeters, current.AcrossMeters);
        if (float.IsFinite(height) && !SkipCapsuleLift(current)) _explorer.LiftSurfaceTerrainCapsule(current.AroundMeters, current.AcrossMeters, height);
        _coverageWaiting = false;
    }

    private void ServiceStreaming(RingCanonicalPosition position)
    {
        if (_synchronousBenchmark) return;
        if (_stream == null || _surface == null || _field == null) return;
        DetectFieldRevision(position);
        _stream.Poll();
        if (_stream.Error.Length > 0)
        {
            _lastError = _stream.Error;
            return;
        }
        if (_stream.TryTakeReady(out PreparedWindow prepared))
        {
            try
            {
                if (_hasPendingFrame && prepared.Revision == _requestRevision && prepared.EditRevision == _field.Revision)
                {
                    RingTerrain3DCollisionGeometry geometry = prepared.TransferGeometryOwnership();
                    if (!_surface.BeginGameplayPreparedWindowWithHoles(prepared.Heights, prepared.HeightImages, prepared.BiomeImages,
                            geometry, prepared.Frame.AnchorAroundMeters, prepared.Frame.AnchorAcrossMeters, prepared.Frame.AnchorHeight,
                            prepared.Frame.AcrossDirection, prepared.Revision, _field.MinimumHeight, _field.MaximumHeight, prepared.ControlImages, prepared.HoleMask))
                    {
                        geometry.Dispose();
                        _lastError = _surface.LastError;
                        CancelPendingWindow();
                    }
                    else
                    {
                        _pendingFrame = prepared.Frame;
                        _hasPendingFrame = true;
                        _budgetStarted = true;
                        _lastError = string.Empty;
                    }
                }
            }
            finally { prepared.Dispose(); }
        }
        if (_budgetStarted)
        {
            Dictionary status = _surface.AdvanceBudgetedPreparation(_generationUploadBudgetMs, _generationCollisionBudgetMs);
            if (!status["ok"].AsBool())
            {
                _lastError = status["error"].AsString();
                CancelPendingWindow();
                return;
            }
            if (status["ready"].AsBool())
            {
                _budgetStarted = false;
                _stageReady = true;
                _lastReadinessMilliseconds = (Time.GetTicksUsec() - _requestStartedUsec) / 1000.0;
                _maxReadinessMilliseconds = Math.Max(_maxReadinessMilliseconds, _lastReadinessMilliseconds);
            }
        }
        RequestCoverage(position, false);
    }

    private void DetectFieldRevision(RingCanonicalPosition position)
    {
        if (_field == null) return;
        if ((ulong)_field.Revision != _pendingEditRevision && Time.GetTicksUsec() >= _editDebounceUntilUsec)
            RequestCoverage(position, true);
    }

    private void RequestCoverage(RingCanonicalPosition position, bool force)
    {
        if (_synchronousBenchmark)
        {
            EnsureSynchronousBenchmarkCoverage(position);
            return;
        }
        if (_field == null || _surface == null || _stream == null || _saveBlocked ||
            !double.IsFinite(position.AroundMeters) || !double.IsFinite(position.AcrossMeters)) return;
        bool editsPending = _field.Revision != _committedEditRevision;
        bool aperturesPending = _apertureRevision != _committedApertureRevision;
        if (editsPending && Time.GetTicksUsec() < _editDebounceUntilUsec) return;
        Vector3 velocity = _explorer?.CanonicalVelocity ?? Vector3.Zero;
        double speed = new Vector2(velocity.X, velocity.Z).Length();
        double seconds = Math.Max(1.5, _maxReadinessMilliseconds / 1000.0);
        double leadAround = Math.Max(_generationPrefetchMeters * Math.Abs(velocity.X) / Math.Max(1, speed), Math.Abs(velocity.X) * seconds + 16);
        double leadAcross = Math.Max(_generationPrefetchMeters * Math.Abs(velocity.Z) / Math.Max(1, speed), Math.Abs(velocity.Z) * seconds + 16);
        bool unsupported = !HasCollisionCoverage(position);
        if (_grid != null && !HasVisualCoverage(position)) _grid.SurfaceTerrainVisualReady = false;
        RingCanonicalPosition target = SelectCoverageTarget(_frame, position, velocity, leadAround, leadAcross, unsupported);
        RingSurfaceFrame next = BuildFrame(target);
        if (_hasPendingFrame && next.AnchorAroundMeters == _pendingFrame.AnchorAroundMeters &&
            next.AnchorAcrossMeters == _pendingFrame.AnchorAcrossMeters && _pendingEditRevision == _field.Revision &&
            _pendingApertureRevision == _apertureRevision) return;
        if (!force && !editsPending && !aperturesPending && next.AnchorAroundMeters == _frame.AnchorAroundMeters && next.AnchorAcrossMeters == _frame.AnchorAcrossMeters)
        {
            // A reversal can make a prepared corridor unnecessary.
            if (_hasPendingFrame) CancelPendingWindow();
            return;
        }
        CancelPendingWindow();
        _requestRevision++;
        _pendingEditRevision = _field.Revision;
        _pendingApertureRevision = _apertureRevision;
        _stream.Request(next, CaptureWindowEdits(next), _pendingEditRevision, _requestRevision, _generationTileCells);
        _pendingFrame = next;
        _hasPendingFrame = true;
        _pendingEditReplacement = editsPending || aperturesPending;
        _budgetStarted = false;
        _requestStartedUsec = Time.GetTicksUsec();
    }

    /// <summary>
    /// Selects the next stream center from the active frame and actual travel
    /// direction. A stationary player retains the active frame; this prevents
    /// a post-handoff offset of -128m from immediately requesting its old
    /// anchor again and churning prepared windows.
    /// </summary>
    public static RingCanonicalPosition SelectCoverageTarget(in RingSurfaceFrame frame,
        RingCanonicalPosition position, Vector3 velocity, double leadAround, double leadAcross, bool unsupported)
    {
        if (unsupported) return position;
        double around = frame.SignedAroundDeltaMeters(position.AroundMeters);
        double across = position.AcrossMeters - frame.AnchorAcrossMeters;
        RingCanonicalPosition target = new(frame.AnchorAroundMeters, position.VerticalMeters, frame.AnchorAcrossMeters);
        // Advance from the active anchor, never from the moving player. Using
        // player+pitch would skip a whole window at the recenter threshold.
        if (velocity.X > .01f && around + leadAround >= 128)
            target = target with { AroundMeters = frame.AnchorAroundMeters + 256.0 };
        else if (velocity.X < -.01f && around - leadAround <= -128)
            target = target with { AroundMeters = frame.AnchorAroundMeters - 256.0 };
        if (velocity.Z > .01f && across + leadAcross >= 128)
            target = target with { AcrossMeters = frame.AnchorAcrossMeters + 256.0 };
        else if (velocity.Z < -.01f && across - leadAcross <= -128)
            target = target with { AcrossMeters = frame.AnchorAcrossMeters - 256.0 };
        return target;
    }

    public bool HasCollisionCoverage(RingCanonicalPosition position)
    {
        if (_surface == null || !_surface.IsReady || _field == null || !FrameContainsCollider(_frame, position))
            return false;
        return true;
    }

    /// <summary>
    /// Terrain3D owns proxy hiding only when the full handoff disk lies inside
    /// the uploaded map, rather than merely when its center has collision.
    /// Both runtime and synchronous windows use the 384-sample, 2m cache:
    /// local map coordinates span [-256, +510] metres from the frame anchor.
    /// </summary>
    public bool HasVisualCoverage(RingCanonicalPosition position)
    {
        if (!HasCollisionCoverage(position)) return false;
        double around = _frame.SignedAroundDeltaMeters(position.AroundMeters);
        double across = (position.AcrossMeters - _frame.AnchorAcrossMeters) * _frame.AcrossDirection;
        return HasUploadedVisualFootprint(around, across, _handoffDistanceMeters);
    }

    /// <summary>
    /// Tests the uploaded 384-sample, 2m height-map footprint only. Callers
    /// must also require committed collision coverage before hiding proxies.
    /// At normal recenter bounds (+/-128m), the certified 128m maximum just
    /// reaches the negative uploaded edge and never expands collision reach.
    /// </summary>
    public static bool HasUploadedVisualFootprint(double localAroundMeters, double localAcrossMeters, float handoffDistanceMeters)
    {
        if (!double.IsFinite(localAroundMeters) || !double.IsFinite(localAcrossMeters) || !float.IsFinite(handoffDistanceMeters)
            || handoffDistanceMeters < GameplaySurfaceTerrainDefaults.MinimumHandoffDistanceMeters
            || handoffDistanceMeters > GameplaySurfaceTerrainDefaults.MaximumHandoffDistanceMeters) return false;
        const double mapMinimumMeters = -256.0;
        const double mapMaximumMeters = 510.0;
        double radius = handoffDistanceMeters;
        return localAroundMeters - radius >= mapMinimumMeters
            && localAroundMeters + radius <= mapMaximumMeters
            && localAcrossMeters - radius >= mapMinimumMeters
            && localAcrossMeters + radius <= mapMaximumMeters;
    }

    public Vector3 ConstrainSurfaceVelocity(RingCanonicalPosition position, Vector3 velocity, double delta)
    {
        if (_synchronousBenchmark) return velocity;
        // Inside the site underground (and the bore column above it) the surface window's coverage and recenter clamp do not apply.
        if (IsInsideSiteUndergroundZone(position.AroundMeters, position.VerticalMeters, position.AcrossMeters)) { _coverageWaiting = false; return velocity; }
        if (_field == null || !HasCollisionCoverage(position)) { CountCoverageWait(); return Vector3.Zero; }
        double around = _frame.SignedAroundDeltaMeters(position.AroundMeters);
        double across = position.AcrossMeters - _frame.AnchorAcrossMeters;
        double safeAround = 176.0;
        double minAcross = _frame.AnchorAcrossMeters <= 192 ? -192.0 : -176.0;
        double maxAcross = _frame.AnchorAcrossMeters >= _field.WidthMeters - 194 ? 192.0 : 176.0;
        float x = velocity.X, z = velocity.Z;
        if (delta > 0)
        {
            if (x > 0 && around + x * delta > safeAround) x = (float)Math.Max(0.0, (safeAround - around) / delta);
            if (x < 0 && around + x * delta < -safeAround) x = (float)Math.Min(0.0, (-safeAround - around) / delta);
            if (z > 0 && across + z * delta > maxAcross) z = (float)Math.Max(0.0, (maxAcross - across) / delta);
            if (z < 0 && across + z * delta < minAcross) z = (float)Math.Min(0.0, (minAcross - across) / delta);
        }
        Vector3 constrained = new(x, velocity.Y, z);
        if (constrained.X != velocity.X || constrained.Z != velocity.Z) CountCoverageWait(); else _coverageWaiting = false;
        return constrained;
    }

    public float SampleCollisionHeight(double around, double across)
    {
        if (_synchronousBenchmark) return SampleHeight(around, across);
        // Open plug (M2-3): a point inside the bore column has the site underground floor below it, not P-200.
        if (TryGetBoreColumnFloor(around, across, out float boreFloor)) return boreFloor;
        RingCanonicalPosition position = new(around, 0.0, across);
        if (_surface != null && HasCollisionCoverage(position))
        {
            float height = _surface.SampleCommittedHeight(around, across);
            if (float.IsFinite(height)) return height;
        }
        return SampleHeight(around, across);
    }

    private Dictionary StreamingDiagnostics()
    {
        Dictionary diagnostics = _stream?.Diagnostics ?? new Dictionary();
        diagnostics["state"] = _lastError.Length > 0 ? "error" : _coverageWaiting ? "waiting_for_coverage" :
            _hasPendingFrame ? (_stageReady ? "ready_for_handoff" : _budgetStarted ? "native_preparing" : "cpu_generating") : "active";
        diagnostics["request_revision"] = _requestRevision;
        diagnostics["committed_revision"] = _committedRevision;
        diagnostics["pending_edit_revision"] = (long)_pendingEditRevision;
        diagnostics["committed_edit_revision"] = (long)_committedEditRevision;
        diagnostics["surface_apertures_enabled"] = _aperturesEnabled;
        diagnostics["surface_aperture_consumers_active"] = SurfaceApertureConsumersActive;
        diagnostics["aperture_replacement_pending"] = ApertureReplacementPending;
        diagnostics["worker_count"] = _generationWorkers;
        diagnostics["active_jobs"] = (diagnostics.TryGetValue("pending_jobs", out Variant jobs) ? jobs.AsInt32() : 0)
            + (diagnostics.TryGetValue("packing", out Variant packing) && packing.AsBool() ? 1 : 0);
        diagnostics["last_readiness_ms"] = _lastReadinessMilliseconds;
        diagnostics["max_readiness_ms"] = _maxReadinessMilliseconds;
        diagnostics["coverage_waits"] = _coverageWaits;
        return diagnostics;
    }

    private bool FrameContainsCollider(in RingSurfaceFrame frame, RingCanonicalPosition position)
    {
        if (!double.IsFinite(position.AroundMeters) || !double.IsFinite(position.AcrossMeters) || _field == null ||
            position.AcrossMeters < 0 || position.AcrossMeters > _field.WidthMeters - _field.SpacingMeters) return false;
        double around = frame.SignedAroundDeltaMeters(position.AroundMeters);
        double across = position.AcrossMeters - frame.AnchorAcrossMeters;
        double limitLow = frame.AnchorAcrossMeters <= 192 ? -192.0 : -176.0;
        double limitHigh = frame.AnchorAcrossMeters >= _field.WidthMeters - 194 ? 192.0 : 176.0;
        return Math.Abs(around) <= 176.001 && across >= limitLow - .001 && across <= limitHigh + .001;
    }

    private RingSurfaceHeightEdit[] CaptureWindowEdits(in RingSurfaceFrame frame)
    {
        if (_field == null) return System.Array.Empty<RingSurfaceHeightEdit>();
        int z0 = frame.AnchorAcrossMeters - frame.AcrossDirection * 256;
        int z1 = frame.AnchorAcrossMeters + frame.AcrossDirection * 510;
        int first = Math.Max(0, Math.Min(z0, z1));
        int last = Math.Min(_field.WidthMeters - (int)_field.SpacingMeters, Math.Max(z0, z1));
        var edits = new List<RingSurfaceHeightEdit>();
        foreach (RingSurfaceHeightEdit edit in _field.GetEditsInWindow(frame.AnchorAroundMeters - 256, frame.AnchorAroundMeters + 510, first, last)) edits.Add(edit);
        return edits.ToArray();
    }

    private void CountCoverageWait()
    {
        if (_coverageWaiting) return;
        _coverageWaiting = true;
        _coverageWaits++;
    }

    private void CancelPendingWindow()
    {
        _stream?.Cancel();
        _surface?.DiscardStagedWindow();
        _hasPendingFrame = _budgetStarted = _stageReady = false;
    }
}
