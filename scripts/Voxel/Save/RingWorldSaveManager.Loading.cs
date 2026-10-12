using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Save;

public partial class RingWorldSaveManager
{
    private sealed record WorldDocumentReadResult(SaveDocument? Document, string Error);
    private Task<WorldDocumentReadResult>? _worldDocumentRead;
    private sealed record SmoothDocumentReadResult(SmoothTerrainJournalEnvelope? Journal,
        SmoothConstructionCommand[]? Construction, string Error);
    private Task<SmoothDocumentReadResult>? _smoothDocumentRead;
    private readonly CancellationTokenSource _worldLoadCancellation = new();
    private bool _stagedDocumentValidated;
    private SaveDocument? _stagedDocument;
    private string _loadingStage = "pending";
    private string _loadingDetail = "Waiting to read world state.";
    private float _loadingProgress;

    public bool IsWorldLoadComplete => _loaded && !_loadRejected;
    public bool IsWorldLoadFailed => _loadRejected;
    public string WorldLoadError { get; private set; } = string.Empty;

    public Godot.Collections.Dictionary GetLoadingStatus() => new()
    {
        ["stage"] = IsWorldLoadComplete ? "complete" : _loadingStage,
        ["detail"] = IsWorldLoadComplete ? "World state restored." : _loadingDetail,
        ["progress"] = IsWorldLoadComplete ? 1.0f : _loadingProgress,
        ["complete"] = IsWorldLoadComplete,
        ["failed"] = IsWorldLoadFailed,
        ["error"] = WorldLoadError
    };

    /// <summary>Stops restoration and prohibits writes before the loading scene is removed.</summary>
    public void CancelWorldLoad()
    {
        _worldLoadCancellation.Cancel();
        RejectLoad("World loading cancelled.");
        _loadingStage = "cancelled";
    }

    private void BeginStagedWorldLoad()
    {
        if (_loadRejected || _isExiting || _worldDocumentRead != null) return;
        _loadingStage = "reading_save";
        _loadingDetail = "Reading saved player and world state.";
        _loadingProgress = 0.1f;
        string statePath = _statePath;
        CancellationToken cancellation = _worldLoadCancellation.Token;
        // Only managed file data and private JSON DTOs cross the worker boundary.
        // Backend/cave validation and every scene mutation remain on the main thread.
        _worldDocumentRead = Task.Run(() => ReadWorldDocument(statePath, cancellation));
        if (_smoothBackendActive)
        {
            string journalPath = _smoothJournalPath;
            string constructionPath = _constructionPath;
            _smoothDocumentRead = Task.Run(() => ReadSmoothDocuments(journalPath, constructionPath, cancellation));
        }
    }

    private static SmoothDocumentReadResult ReadSmoothDocuments(string journalPath, string constructionPath,
        CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            SmoothTerrainJournalEnvelope? journal = File.Exists(journalPath)
                ? JsonSerializer.Deserialize<SmoothTerrainJournalEnvelope>(File.ReadAllText(journalPath)) : null;
            cancellation.ThrowIfCancellationRequested();
            SmoothConstructionCommand[]? construction = File.Exists(constructionPath)
                ? JsonSerializer.Deserialize<SmoothConstructionCommand[]>(File.ReadAllText(constructionPath))
                    ?? throw new InvalidDataException("Smooth construction journal is empty.") : null;
            cancellation.ThrowIfCancellationRequested();
            return new(journal, construction, string.Empty);
        }
        catch (Exception exception) { return new(null, null, exception.Message); }
    }

    private static WorldDocumentReadResult ReadWorldDocument(string statePath, CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (!File.Exists(statePath)) return new(null, string.Empty);
            string json = File.ReadAllText(statePath);
            cancellation.ThrowIfCancellationRequested();
            SaveDocument? document = JsonSerializer.Deserialize<SaveDocument>(json);
            cancellation.ThrowIfCancellationRequested();
            return document == null
                ? new(null, "World state document is empty.")
                : new(document, string.Empty);
        }
        catch (Exception exception)
        {
            return new(null, exception.Message);
        }
    }

    private void AdvanceStagedWorldLoad()
    {
        if (_worldLoadCancellation.IsCancellationRequested || _isExiting) return;
        if (_worldDocumentRead == null || !_worldDocumentRead.IsCompleted) return;
        try
        {
            if (!_stagedDocumentValidated)
            {
                WorldDocumentReadResult read = _worldDocumentRead.GetAwaiter().GetResult();
                if (!string.IsNullOrEmpty(read.Error)) throw new InvalidDataException(read.Error);
                SaveDocument? document = read.Document;
                _loadingStage = "validating_save";
                _loadingDetail = "Checking saved world contracts.";
                _loadingProgress = 0.35f;
                if (document != null)
                {
                    if (document.Player == null || document.Runtime == null)
                        throw new InvalidDataException("World state player or runtime data is missing.");
                    if (!IsBackendCompatible(document, out string reason)) throw new InvalidDataException(reason);
                    RingWorldCaveContractCompatibilityResult compatibility = EvaluateCompatibility(document);
                    if (!compatibility.CanLoad) throw new InvalidDataException(compatibility.Reason);
                    // Terrain preparation needs the saved region, but full player
                    // recovery must see its restored editable terrain first.
                    PlayerDocument player = document.Player;
                    _explorer!.RestoreWorldLoadingFocus(new RingCanonicalPosition(
                        player.AroundMeters, player.VerticalMeters, player.AcrossMeters));
                    _stagedDocument = document;
                    if (compatibility.RequiresRewrite)
                        GD.Print($"World save compatibility migration accepted: {compatibility.Reason}");
                }
                else if (_surfaceRuntimeGenerationActive && RuntimeSurfaceLegacySlotIsOccupied())
                    throw new InvalidDataException("Legacy editable world data occupies this runtime-surface slot; no implicit migration is available.");
                _stagedDocumentValidated = true;
                _loadingStage = "restoring_terrain";
                _loadingDetail = "Restoring saved terrain edits.";
                _loadingProgress = 0.6f;
            }

            if (_surfaceBackendActive)
            {
                if (_surfaceTerrain == null) throw new InvalidOperationException("Terrain3D gameplay node is unavailable.");
                _surfaceTerrain.ReleaseSavedStateHold();
                if (!_surfaceTerrain.EnsureInitialized() || !_surfaceTerrain.IsReady)
                {
                    if (_surfaceTerrain.InitializationFailed) throw new InvalidDataException(_surfaceTerrain.LastError);
                    return;
                }
            }
            else
            {
                // Existing legacy and smooth migration policies remain authoritative.
                // Their journal hydration is isolated from the Terrain3D startup path.
                if (_smoothBackendActive)
                {
                    if (_smoothDocumentRead == null || !_smoothDocumentRead.IsCompleted) return;
                    SmoothDocumentReadResult smooth = _smoothDocumentRead.GetAwaiter().GetResult();
                    if (!string.IsNullOrEmpty(smooth.Error)) throw new InvalidDataException(smooth.Error);
                    if (smooth.Journal.HasValue && (_smoothTerrain == null
                        || !_smoothTerrain.RestoreJournalEnvelope(smooth.Journal.Value)))
                        throw new InvalidDataException("Smooth terrain journal could not be restored.");
                    if (smooth.Construction != null && (_constructionOverlay == null
                        || !_constructionOverlay.ReplaceCommands(smooth.Construction)))
                        throw new InvalidDataException("Smooth construction journal could not be restored.");
                }
                else
                {
                    if (LoadLegacyVoxelDeltas() == Error.ParseError)
                        throw new InvalidDataException("Voxel delta journals could not be restored.");
                    DiscoverRegionJournals();
                    if (ActivateRegionsAroundExplorer(force: true) == 0)
                    {
                        RebuildActiveDelta();
                        ApplyActiveDeltaSnapshot();
                    }
                }
            }
            // Restore velocity/checkpoints/runtime and perform below-terrain
            // recovery only after the authoritative saved edit fields exist.
            if (_stagedDocument != null) ApplyDocument(_stagedDocument);
            _loaded = true;
            _stagedDocument = null;
            _worldDocumentRead = null;
            _smoothDocumentRead = null;
            _loadingStage = "complete";
            _loadingDetail = "World state restored.";
            _loadingProgress = 1.0f;
            GD.Print($"World save ready: {_worldDirectory} (staged startup).");
        }
        catch (Exception exception)
        {
            GD.PushWarning($"World load was preserved read-only: {exception.Message}");
            RejectLoad(exception.Message);
        }
    }
}
