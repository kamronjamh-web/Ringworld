using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Godot.Collections;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Opt-in initial window preparation using the existing worker and native budgets.</summary>
public partial class RingTerrain3DGameplay
{
    private bool _startupPending;
    private bool _startupExited;
    private ulong _startupAdvanceFrame = ulong.MaxValue;
    private long _startupCompletedBaseline;
    private long _startupCacheHitBaseline;
    private string _startupStage = "initializing";
    private string _startupDetail = "Getting your world ready.";
    private double _startupProgress;
    private Dictionary _startupNativeStatus = new();
    private Task<(RingSurfaceEditStoreResult Result, RingSurfaceHeightField? Field)>? _startupEditLoad;
    private CancellationTokenSource? _startupEditCancellation;
    private bool _startupCleanupRequested;

    public bool InitializationPending => UseStagedStartup && !_startupExited && !_initializationFailed
        && (WaitForSavedState || _field == null || _startupPending);
    public bool InitializationFailed => _initializationFailed;
    public bool StartupCancellationReady => PollStartupCancellation();

    /// <summary>Call only after applying the validated saved player pose.</summary>
    public void ReleaseSavedStateHold() => WaitForSavedState = false;

    /// <summary>Cancel loading without permitting partial or invalid state to overwrite a save.</summary>
    public void CancelStartup(string reason)
    {
        if (_startupExited) return;
        FailStagedStartup(string.IsNullOrWhiteSpace(reason) ? "Terrain startup cancelled." : reason);
    }

    public Dictionary GetLoadingStatus()
    {
        RingCanonicalPosition position = _explorer?.CanonicalPosition ?? default;
        bool collision = !_initializationFailed && !_startupPending && HasCollisionCoverage(position);
        bool visual = IsReady;
        string stage = _initializationFailed ? "failed" : WaitForSavedState && UseStagedStartup ? "waiting_for_saved_state"
            : IsReady ? "ready" : _startupStage;
        string detail = _initializationFailed ? LastError : WaitForSavedState && UseStagedStartup
            ? "Waiting for your saved world." : IsReady ? "Your location is ready." : _startupDetail;
        return new Dictionary
        {
            ["stage"] = stage, ["detail"] = detail, ["progress"] = IsReady ? 1.0 : _startupProgress,
            ["ready"] = IsReady, ["pending"] = InitializationPending, ["failed"] = InitializationFailed,
            ["visual_ready"] = visual, ["collision_ready"] = collision, ["visual_coverage"] = collision && HasVisualCoverage(position), ["error"] = LastError,
            ["request_revision"] = _requestRevision, ["committed_revision"] = _committedRevision,
            ["streaming"] = _stream?.Diagnostics ?? new Dictionary(), ["preparation"] = _startupNativeStatus
        };
    }

    private void BeginStagedStartup(RingCanonicalPosition position)
    {
        _startupPending = true;
        _committedRevision = 0;
        RequestStagedStartupWindow(position);
    }

    private void BeginStagedSavedEdits(Func<long, int, float> baseline)
    {
        if (_field == null) throw new InvalidOperationException("Terrain definition is missing.");
        RingSurfaceEditSnapshot definition = _field.CreateSnapshot();
        bool triangles = _field.UsesTriangleInterpolation;
        string path = _savePath;
        _startupPending = true;
        _committedRevision = 0;
        _startupStage = "reading_edits";
        _startupDetail = "Reading saved terrain changes.";
        _startupProgress = .02;
        _startupEditCancellation = new CancellationTokenSource();
        CancellationToken cancellation = _startupEditCancellation.Token;
        // File/JSON validation and sparse dictionary construction touch managed
        // data only. The sampler delegate is retained but never invoked here.
        _startupEditLoad = Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return (RingSurfaceEditStoreResult.Ok, (RingSurfaceHeightField?)null);
            RingSurfaceEditStoreResult result = RingSurfaceEditStore.Load(path, out RingSurfaceEditSnapshot saved);
            cancellation.ThrowIfCancellationRequested();
            if (!result.Success) return (result, (RingSurfaceHeightField?)null);
            var restored = new RingSurfaceHeightField(definition.TerrainDefinitionHash, definition.CircumferenceMeters,
                definition.WidthMeters, definition.MinimumHeight, definition.MaximumHeight, baseline,
                definition.SpacingMeters, triangles);
            if (!restored.Restore(saved)) return (new RingSurfaceEditStoreResult(false, "Surface edit save does not match this terrain definition."), (RingSurfaceHeightField?)null);
            cancellation.ThrowIfCancellationRequested();
            return (RingSurfaceEditStoreResult.Ok, (RingSurfaceHeightField?)restored);
        });
    }

    private void RequestStagedStartupWindow(RingCanonicalPosition position)
    {
        if (_field == null || _stream == null || _surface == null) throw new InvalidOperationException("Terrain startup dependencies are missing.");
        if (!double.IsFinite(position.AroundMeters) || !double.IsFinite(position.AcrossMeters))
            throw new InvalidOperationException("Terrain startup player coordinates must be finite.");
        CancelPendingWindow();
        _pendingFrame = BuildFrame(position);
        _frame = _pendingFrame;
        _lastWorldFrame = BuildWorldFrame(_pendingFrame);
        _surface.SetGameplayWorldFrames(_lastWorldFrame, _lastWorldFrame);
        _requestRevision++;
        _pendingEditRevision = _field.Revision;
        _requestStartedUsec = Time.GetTicksUsec();
        Dictionary counters = _stream.Diagnostics;
        _startupCompletedBaseline = counters["completed"].AsInt64();
        _startupCacheHitBaseline = counters["cache_hits"].AsInt64();
        _stream.Request(_pendingFrame, CaptureWindowEdits(_pendingFrame), _pendingEditRevision, _requestRevision, _generationTileCells);
        if (_stream.Error.Length > 0) throw new InvalidOperationException(_stream.Error);
        _hasPendingFrame = true;
        _startupNativeStatus = new Dictionary();
        _startupProgress = .05;
        _startupStage = "generating";
        _startupDetail = "Building nearby terrain.";
        _grid!.SurfaceTerrainVisualReady = false;
    }

    private void AdvanceStagedStartup()
    {
        if (!_startupPending || _initializationFailed || _startupExited || _field == null) return;
        ulong frame = Engine.GetProcessFrames();
        if (_startupAdvanceFrame == frame) return;
        _startupAdvanceFrame = frame;
        try
        {
            if (_saveBlocked) throw new InvalidOperationException(LastError);
            if (_startupEditLoad != null)
            {
                if (!_startupEditLoad.IsCompleted) return;
                var loaded = _startupEditLoad.GetAwaiter().GetResult();
                _startupEditLoad = null;
                _startupEditCancellation?.Dispose();
                _startupEditCancellation = null;
                if (!loaded.Result.Success) throw new InvalidDataException("Surface save rejected and protected: " + loaded.Result.Error);
                if (loaded.Field != null) _field = loaded.Field;
                CompleteSurfaceInitialization();
                return;
            }
            if (_stream == null || _surface == null) throw new InvalidOperationException("Terrain startup preparation is missing.");
            RingCanonicalPosition position = _explorer?.CanonicalPosition ?? new RingCanonicalPosition(_pendingFrame.AnchorAroundMeters, _pendingFrame.AnchorHeight, _pendingFrame.AnchorAcrossMeters);
            // Saved poses can arrive after the first request. Never publish an
            // initial map or collider around an obsolete location or edit revision.
            if (!StartupFrameCovers(_pendingFrame, position) || _pendingEditRevision != _field.Revision)
                RequestStagedStartupWindow(position);
            _stream.Poll();
            if (_stream.Error.Length > 0) throw new InvalidOperationException(_stream.Error);
            if (_stream.TryTakeReady(out PreparedWindow prepared))
            {
                try
                {
                    if (prepared.Revision == _requestRevision && prepared.EditRevision == _field.Revision)
                    {
                        RingTerrain3DCollisionGeometry geometry = prepared.TransferGeometryOwnership();
                        if (!_surface.BeginGameplayPreparedWindow(prepared.Heights, prepared.HeightImages, prepared.BiomeImages,
                            geometry, prepared.Frame.AnchorAroundMeters, prepared.Frame.AnchorAcrossMeters,
                            prepared.Frame.AnchorHeight, prepared.Frame.AcrossDirection, prepared.Revision,
                            _field.MinimumHeight, _field.MaximumHeight))
                        {
                            geometry.Dispose();
                            throw new InvalidOperationException(_surface.LastError);
                        }
                        _pendingFrame = prepared.Frame;
                        _surface.SetGameplayWorldFrames(_lastWorldFrame, BuildWorldFrame(_pendingFrame));
                        _budgetStarted = true;
                    }
                }
                finally { prepared.Dispose(); }
            }
            if (!_budgetStarted && !_stageReady)
            {
                Dictionary counters = _stream.Diagnostics;
                long completed = counters["completed"].AsInt64() - _startupCompletedBaseline;
                long hits = counters["cache_hits"].AsInt64() - _startupCacheHitBaseline;
                _startupProgress = .05 + .40 * Math.Clamp((completed + hits) / 9.0, 0.0, 1.0);
                return;
            }
            if (_budgetStarted)
            {
                _startupNativeStatus = _surface.AdvanceBudgetedPreparation(_generationUploadBudgetMs, _generationCollisionBudgetMs);
                if (!_startupNativeStatus["ok"].AsBool()) throw new InvalidOperationException(_startupNativeStatus["error"].AsString());
                UpdateStartupPreparationStatus(_startupNativeStatus);
                if (_startupNativeStatus["ready"].AsBool())
                {
                    _budgetStarted = false;
                    _stageReady = true;
                    _startupStage = "activating";
                    _startupDetail = "Finishing your location.";
                    _startupProgress = .98;
                }
            }
        }
        catch (Exception error) { FailStagedStartup(error.Message); }
    }

    private bool StartupFrameCovers(in RingSurfaceFrame frame, RingCanonicalPosition position)
        // Full handoff coverage can be unavailable near a physical across edge;
        // the existing proxy fallback stays visible there. That must not strand
        // a fully uploaded startup window whose collider safely contains the pose.
        => FrameContainsCollider(frame, position);

    private void UpdateStartupPreparationStatus(Dictionary status)
    {
        string phase = status["phase"].AsString();
        if (phase is "setup" or "regions" or "maps")
        {
            _startupStage = "uploading";
            _startupDetail = "Adding terrain detail.";
            _startupProgress = .45 + .25 * Math.Clamp(status["regions_done"].AsInt32() / 9.0, 0.0, 1.0);
        }
        else
        {
            _startupStage = "collision";
            _startupDetail = "Preparing solid ground.";
            _startupProgress = .70 + .27 * Math.Clamp(status["tiles_done"].AsInt32() / (double)Math.Max(1, status["tiles_total"].AsInt32()), 0.0, 1.0);
        }
    }

    private void TryActivateStagedStartup()
    {
        if (!_startupPending || !_stageReady || _initializationFailed || _surface == null || _field == null) return;
        try
        {
            RingCanonicalPosition position = _explorer?.CanonicalPosition ?? new RingCanonicalPosition(_pendingFrame.AnchorAroundMeters, _pendingFrame.AnchorHeight, _pendingFrame.AnchorAcrossMeters);
            if (_pendingEditRevision != _field.Revision || !StartupFrameCovers(_pendingFrame, position))
            {
                RequestStagedStartupWindow(position);
                return;
            }
            if (!_surface.CanActivateStagedWindow(position.AroundMeters, position.AcrossMeters, 0.0f)
                || !_surface.ActivateStagedWindow(_requestRevision)) throw new InvalidOperationException("Prepared startup terrain could not activate matching collision.");
            _frame = _pendingFrame;
            _lastWorldFrame = BuildWorldFrame(_frame);
            _surface.SetGameplayWorldFrames(_lastWorldFrame, _lastWorldFrame);
            _committedRevision = _requestRevision;
            _committedEditRevision = _pendingEditRevision;
            _hasPendingFrame = _budgetStarted = _stageReady = false;
            _startupPending = false;
            _startupProgress = 1.0;
            _startupStage = "ready";
            _startupDetail = "Your location is ready.";
            _lastReadinessMilliseconds = (Time.GetTicksUsec() - _requestStartedUsec) / 1000.0;
            _maxReadinessMilliseconds = Math.Max(_maxReadinessMilliseconds, _lastReadinessMilliseconds);
            _grid!.SurfaceTerrainVisualReady = HasVisualCoverage(position);
            float height = _surface.SampleCommittedHeight(position.AroundMeters, position.AcrossMeters);
            if (float.IsFinite(height)) _explorer?.LiftSurfaceTerrainCapsule(position.AroundMeters, position.AcrossMeters, height);
        }
        catch (Exception error) { FailStagedStartup(error.Message); }
    }

    private void FailStagedStartup(string error)
    {
        _startupPending = false;
        _initializationFailed = true;
        _saveBlocked = true;
        _lastError = string.IsNullOrWhiteSpace(error) ? "Terrain startup failed." : error;
        _startupStage = "failed";
        _startupDetail = _lastError;
        _startupCleanupRequested = true;
        _startupEditCancellation?.Cancel();
        CancelPendingWindow();
        if (_grid != null)
        {
            _grid.SurfaceTerrainVisualReady = false;
            if (_field != null) _grid.DetachSurfaceHeightField(_field);
        }
    }

    private bool PollStartupCancellation()
    {
        if (!_startupCleanupRequested) return !InitializationPending;
        if (_startupEditLoad != null)
        {
            if (!_startupEditLoad.IsCompleted) return false;
            if (_startupEditLoad.IsFaulted) _ = _startupEditLoad.Exception;
            _startupEditLoad = null;
            _startupEditCancellation?.Dispose();
            _startupEditCancellation = null;
        }
        if (_stream == null) return true;
        _stream.Poll();
        Dictionary counters = _stream.Diagnostics;
        if (counters["pending_jobs"].AsInt32() > 0 || counters["packing"].AsBool()) return false;
        _stream.Dispose();
        _stream = null;
        return true;
    }
}
