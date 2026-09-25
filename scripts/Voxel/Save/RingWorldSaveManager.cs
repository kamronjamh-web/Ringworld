using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Terrain.Surface;

namespace RingworldTechDemo.Voxel.Save;

/// <summary>Slot-scoped autosave for mutable world and precision-safe player state.</summary>
public partial class RingWorldSaveManager : Node
{
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath CoordinatorPath { get; set; } = new("../TerrainLodCoordinator");
    [Export] public NodePath CloudPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath SmoothTerrainPath { get; set; } = new("../SmoothTerrainGrid");
    [Export] public NodePath ConstructionOverlayPath { get; set; } = new("../SmoothConstructionOverlay");
    [Export] public NodePath SurfaceTerrainPath { get; set; } = new("../Terrain3DGameplay");
    [Export] public NodePath CelestialClockPath { get; set; } = new("../RingDayNightCycle");
    [Export(PropertyHint.Range, "5,300,1")] public float AutosaveSeconds { get; set; } = 30.0f;
    [Export(PropertyHint.Range, "0,4,1")] public int ActiveRegionRadius { get; set; } = 1;
    [Export(PropertyHint.Range, "1,8,1")] public int RetainedRegionRadius { get; set; } = 2;

    // v3 records that caves derive from the terrain/world seed. v2 and v1 remain
    // readable only through the fail-closed policy below.
    private const int SaveFormatVersion = 3;
    private const int RegionChunkSpan = 4096;
    private VoxelChunkGridManager? _grid;
    private RingVoxelExplorerController? _explorer;
    private RingTerrainLodCoordinator? _coordinator;
    private RingworldTechDemo.RingVolumetricCloudLayer? _clouds;
    private SmoothTerrainGrid? _smoothTerrain;
    private SmoothConstructionOverlay? _constructionOverlay;
    private RingTerrain3DGameplay? _surfaceTerrain;
    private Node? _celestialClock;
    private bool _smoothBackendActive;
    private bool _surfaceBackendActive;
    private bool _loadRejected;
    private string _worldDirectory = string.Empty;
    private string _statePath = string.Empty;
    private string _deltaPath = string.Empty;
    private string _deltaJournalPath = string.Empty;
    private string _smoothJournalPath = string.Empty;
    private string _constructionPath = string.Empty;
    private double _elapsed;
    private bool _loaded;
    private bool _shutdownSaved;
    private Task<SaveResult>? _saveTask;
    private VoxelChunkDeltaSnapshot? _inFlightDelta;
    private readonly VoxelChunkDeltaSnapshot _legacyDelta = new();
    private readonly VoxelChunkDeltaSnapshot _activeDelta = new();
    private readonly Dictionary<RegionKey, string> _regionJournals = new();
    private readonly Dictionary<RegionKey, VoxelChunkDeltaSnapshot> _loadedRegionDeltas = new();
    private RegionKey? _lastActivationCenter;
    private RegionKey? _pendingActivationCenter;

    private sealed record SaveResult(bool Success, string ErrorMessage);
    private readonly record struct RegionKey(long AroundRegion, int AcrossRegion);

    private sealed class SaveDocument
    {
        public int FormatVersion { get; set; } = SaveFormatVersion;
        public string Backend { get; set; } = string.Empty;
        public int TerrainGeneratorVersion { get; set; }
        public ulong TerrainDefinitionHash { get; set; }
        public double CircumferenceMeters { get; set; }
        public RingWorldDesignedCaveContractSnapshot? DesignedCaveContract { get; set; }
        public long SavedUnix { get; set; }
        public PlayerDocument Player { get; set; } = new();
        public RuntimeDocument Runtime { get; set; } = new();
    }

    private sealed class PlayerDocument
    {
        public double AroundMeters { get; set; }
        public double VerticalMeters { get; set; }
        public double AcrossMeters { get; set; }
        public float VelocityAround { get; set; }
        public float VelocityVertical { get; set; }
        public float VelocityAcross { get; set; }
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public bool Noclip { get; set; }
        public int NoclipSpeedTier { get; set; }
        public float NoclipSpeed { get; set; }
        public bool HasUndergroundCheckpoint { get; set; }
        public double CheckpointAroundMeters { get; set; }
        public double CheckpointVerticalMeters { get; set; }
        public double CheckpointAcrossMeters { get; set; }
    }

    private sealed class RuntimeDocument
    {
        public string HardwareTier { get; set; } = string.Empty;
        public int TerrainQuality { get; set; } = (int)RingTerrainLodCoordinator.QualityPreset.High;
        public int TerrainWorkers { get; set; } = 2;
        public int SurfaceChunkRadius { get; set; } = 7;
        public int VerticalChunkRadius { get; set; } = 7;
        public bool AtmosphericHaze { get; set; }
        public int CloudQuality { get; set; } = 1;
        // Optional for backwards-compatible save documents. Old saves restore
        // at zero and then advance normally.
        public double CloudSimulationTimeSeconds { get; set; }
        // Optional for v3 saves: absolute celestial time restores the same
        // eclipse phase without coupling it to cloud temporal history.
        public double CelestialSimulationTimeSeconds { get; set; }
        public bool CelestialPaused { get; set; }
        public float CelestialTimeScale { get; set; } = 1.0f;
    }

    public override void _Ready()
    {
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _coordinator = GetNodeOrNull<RingTerrainLodCoordinator>(CoordinatorPath);
        _clouds = GetNodeOrNull<RingworldTechDemo.RingVolumetricCloudLayer>(CloudPath);
        _smoothTerrain = GetNodeOrNull<SmoothTerrainGrid>(SmoothTerrainPath);
        _constructionOverlay = GetNodeOrNull<SmoothConstructionOverlay>(ConstructionOverlayPath);
        _surfaceTerrain = GetNodeOrNull<RingTerrain3DGameplay>(SurfaceTerrainPath);
        _celestialClock = GetNodeOrNull<Node>(CelestialClockPath);
        _surfaceBackendActive = _grid?.UseTerrain3DSurface == true;
        _smoothBackendActive = _grid?.IsSmoothTerrainReplacementActive == true;
        if (_surfaceBackendActive)
        {
            _smoothBackendActive = false;
            _smoothTerrain = null;
            _constructionOverlay = null;
        }
        if (!_smoothBackendActive)
        {
            _smoothTerrain = null;
            _constructionOverlay = null;
        }
        string configured = ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString();
        if (string.IsNullOrWhiteSpace(configured) || _grid?.TerrainSampler == null || _explorer == null)
        {
            GD.PushWarning("World save manager disabled: no active save slot or required gameplay nodes.");
            SetProcess(false);
            return;
        }

        _worldDirectory = ProjectSettings.GlobalizePath(configured);
        Directory.CreateDirectory(_worldDirectory);
        Directory.CreateDirectory(Path.Combine(_worldDirectory, "regions"));
        Directory.CreateDirectory(Path.Combine(_worldDirectory, "lod_cache"));
        _statePath = Path.Combine(_worldDirectory, _surfaceBackendActive ? "world_state_terrain3d_v1.json" : "world_state.json");
        _deltaPath = configured.TrimEnd('/', '\\') + "/voxel_deltas.json";
        _deltaJournalPath = Path.Combine(_worldDirectory, "voxel_delta_journal.ndjson");
        _smoothJournalPath = Path.Combine(_worldDirectory, "smooth_terrain_edits_v1.json");
        _constructionPath = Path.Combine(_worldDirectory, "smooth_construction_v1.json");
        Callable.From(LoadWorld).CallDeferred();
    }

    public override void _Process(double delta)
    {
        if (!_loaded) return;
        CompleteBackgroundSaveIfReady();
        if (!_surfaceBackendActive) QueueRegionActivation();
        _elapsed += delta;
        if (_elapsed < Math.Max(5.0f, AutosaveSeconds)) return;
        _elapsed = 0.0;
        SaveWorld();
    }

    public override void _ExitTree()
    {
        if (_loaded && !_shutdownSaved)
        {
            SaveWorld();
            WaitForBackgroundSave();
            // Edits made while the previous async write was in flight remain dirty.
            SaveWorld();
            WaitForBackgroundSave();
            _shutdownSaved = true;
        }
    }

    private void WaitForBackgroundSave()
    {
        if (_saveTask == null) return;
        try { _saveTask.Wait(); }
        catch (Exception exception) { GD.PushWarning($"Final save did not finish cleanly: {exception.Message}"); }
        CompleteBackgroundSaveIfReady();
    }

    public Error SaveWorld()
    {
        if (!IsSavePermittedForLoadState(_loaded, _loadRejected)) return _loadRejected ? Error.Failed : Error.Busy;
        if (_grid?.TerrainSampler == null || _explorer == null || string.IsNullOrEmpty(_statePath))
            return Error.Unconfigured;
        if (_saveTask != null)
            return Error.Busy;

        if (_surfaceBackendActive)
        {
            if (_surfaceTerrain == null || !_surfaceTerrain.IsReady || !_surfaceTerrain.SaveSurfaceEdits())
            {
                GD.PushError($"Surface terrain save failed: {_surfaceTerrain?.LastError ?? "Terrain3D gameplay node is unavailable."}");
                return Error.Failed;
            }
        }
        else if (_smoothBackendActive && (_smoothTerrain == null || _constructionOverlay == null))
            return Error.Unconfigured;
        VoxelChunkDeltaSnapshot delta = _smoothBackendActive
            || _surfaceBackendActive ? new VoxelChunkDeltaSnapshot()
            : _grid.CaptureDirtyChunkDeltaSnapshot();
        RingCanonicalPosition position = _explorer.CanonicalPosition;
        RingCanonicalPosition? checkpoint = _explorer.UndergroundCheckpoint;
        Vector3 velocity = _explorer.CanonicalVelocity;
        var document = new SaveDocument
        {
            Backend = GetBackendName(),
            TerrainGeneratorVersion = _grid.TerrainSampler.TerrainGeneratorVersion,
            TerrainDefinitionHash = _grid.TerrainSampler.TerrainDefinitionHash,
            CircumferenceMeters = _grid.GetCanonicalRingCircumferenceMeters64(),
            DesignedCaveContract = CaptureDesignedCaveContract(),
            SavedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Player = new PlayerDocument
            {
                AroundMeters = position.AroundMeters,
                VerticalMeters = position.VerticalMeters,
                AcrossMeters = position.AcrossMeters,
                VelocityAround = velocity.X,
                VelocityVertical = velocity.Y,
                VelocityAcross = velocity.Z,
                Yaw = _explorer.SavedYaw,
                Pitch = _explorer.SavedPitch,
                Noclip = _explorer.NoclipEnabled,
                NoclipSpeedTier = _explorer.NoclipSpeedTier,
                NoclipSpeed = _explorer.NoclipTargetSpeedMetersPerSecond,
                HasUndergroundCheckpoint = checkpoint.HasValue,
                CheckpointAroundMeters = checkpoint?.AroundMeters ?? 0.0,
                CheckpointVerticalMeters = checkpoint?.VerticalMeters ?? 0.0,
                CheckpointAcrossMeters = checkpoint?.AcrossMeters ?? 0.0
            },
            Runtime = new RuntimeDocument
            {
                HardwareTier = GetActiveHardwareTier(),
                TerrainQuality = (int)(_coordinator?.CurrentPreset ?? RingTerrainLodCoordinator.QualityPreset.High),
                TerrainWorkers = _coordinator?.MaxConcurrentWorkerBuilds ?? 2,
                SurfaceChunkRadius = _smoothBackendActive ? _smoothTerrain!.RadiusAround : _grid.StreamingVisualRadiusAround,
                VerticalChunkRadius = _smoothBackendActive ? _smoothTerrain!.RadiusVertical : _grid.StreamingVisualRadiusVertical,
                AtmosphericHaze = RingAtmosphereModel.Enabled,
                CloudQuality = (int)(_clouds?.Quality ?? RingworldTechDemo.RingVolumetricCloudLayer.CloudQuality.High),
                CloudSimulationTimeSeconds = _clouds?.CloudSimulationTimeSeconds ?? 0.0,
                CelestialSimulationTimeSeconds = GetCelestialSimulationTimeSeconds(),
                CelestialPaused = GetCelestialPaused(),
                CelestialTimeScale = GetCelestialTimeScale()
            }
        };

        _inFlightDelta = _smoothBackendActive || _surfaceBackendActive ? null : delta;
        SmoothTerrainJournalEnvelope? smooth = _smoothBackendActive ? _smoothTerrain!.CaptureJournalEnvelope() : null;
        IReadOnlyList<SmoothConstructionCommand>? construction = _smoothBackendActive ? _constructionOverlay!.CaptureCommands() : null;
        bool smoothBackend = _smoothBackendActive;
        bool surfaceBackend = _surfaceBackendActive;
        _saveTask = Task.Run(() => WriteSaveFiles(document, delta, smooth, construction, smoothBackend, surfaceBackend));
        return Error.Ok;
    }

    /// <summary>Completes a durable save before a scene transition destroys this world.</summary>
    public Error SaveForSceneTransition()
    {
        if (_saveTask != null)
        {
            SaveResult pending;
            try { pending = _saveTask.GetAwaiter().GetResult(); }
            catch (Exception exception) { pending = new SaveResult(false, exception.Message); }
            CompleteBackgroundSaveIfReady();
            if (!pending.Success) return Error.Failed;
        }

        Error queued = SaveWorld();
        if (queued != Error.Ok) return queued;
        SaveResult completed;
        try { completed = _saveTask!.GetAwaiter().GetResult(); }
        catch (Exception exception) { completed = new SaveResult(false, exception.Message); }
        CompleteBackgroundSaveIfReady();
        return completed.Success ? Error.Ok : Error.Failed;
    }

    private SaveResult WriteSaveFiles(SaveDocument document, VoxelChunkDeltaSnapshot delta, SmoothTerrainJournalEnvelope? smooth, IReadOnlyList<SmoothConstructionCommand>? construction, bool smoothBackend, bool surfaceBackend)
    {
        string temporary = _statePath + ".tmp";
        string constructionTemporary = _constructionPath + ".tmp";
        try
        {
            if (surfaceBackend)
            {
                // The Terrain3D gameplay node durably committed its isolated
                // surface_terrain3d_edits_v1.json before this shared state task.
            }
            else if (smoothBackend)
            {
                if (!smooth.HasValue || construction == null)
                    throw new InvalidOperationException("Smooth editable save snapshot is incomplete.");
                SmoothTerrainJournalLoadResult journal = SmoothTerrainEditJournalStore.SaveSnapshotAsync(_smoothJournalPath, smooth.Value).GetAwaiter().GetResult();
                if (!journal.Success) throw new IOException($"Smooth terrain journal save failed: {journal.Error}");
                File.WriteAllText(constructionTemporary, JsonSerializer.Serialize(construction));
                File.Move(constructionTemporary, _constructionPath, true);
            }
            else
            {
                WriteRegionDeltaJournals(delta);
            }

            // Commit the shared player/runtime document last. If an editable
            // journal write fails, the prior coherent state remains current.
            string stateJson = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporary, stateJson);
            File.Move(temporary, _statePath, overwrite: true);
            return new SaveResult(true, string.Empty);
        }
        catch (Exception exception)
        {
            return new SaveResult(false, exception.Message);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(constructionTemporary)) File.Delete(constructionTemporary);
        }
    }

    private void WriteRegionDeltaJournals(VoxelChunkDeltaSnapshot delta)
    {
        if (delta.ChunkOverrides.Count == 0 && delta.NaturalTerrainSdfDeltas.Count == 0)
            return;

        var regions = new Dictionary<RegionKey, VoxelChunkDeltaSnapshot>();
        foreach ((string chunkKey, Dictionary<int, ushort> overrides) in delta.ChunkOverrides)
        {
            if (!TryParseChunkKey(chunkKey, out long aroundChunk, out int acrossChunk))
                throw new InvalidDataException($"Invalid sparse chunk key '{chunkKey}'.");
            RegionKey region = new(FloorDiv(aroundChunk, RegionChunkSpan), FloorDiv(acrossChunk, RegionChunkSpan));
            if (!regions.TryGetValue(region, out VoxelChunkDeltaSnapshot? patch))
            {
                patch = new VoxelChunkDeltaSnapshot();
                regions[region] = patch;
            }
            patch.ChunkOverrides[chunkKey] = overrides;
        }

        foreach (NaturalTerrainSdfDeltaRecord record in delta.NaturalTerrainSdfDeltas)
        {
            long aroundChunk = FloorDiv(record.AroundVoxel, VoxelConstants.ChunkSizeX);
            int acrossChunk = FloorDiv(record.AcrossVoxel, VoxelConstants.ChunkSizeZ);
            RegionKey region = new(FloorDiv(aroundChunk, RegionChunkSpan), FloorDiv(acrossChunk, RegionChunkSpan));
            if (!regions.TryGetValue(region, out VoxelChunkDeltaSnapshot? patch))
            {
                patch = new VoxelChunkDeltaSnapshot();
                regions[region] = patch;
            }
            patch.NaturalTerrainSdfDeltas.Add(record);
        }

        string regionsDirectory = Path.Combine(_worldDirectory, "regions");
        foreach ((RegionKey region, VoxelChunkDeltaSnapshot patch) in regions)
        {
            string path = Path.Combine(regionsDirectory, $"a_{region.AroundRegion}_x_{region.AcrossRegion}.ndjson");
            File.AppendAllText(path, JsonSerializer.Serialize(patch) + System.Environment.NewLine);
        }
    }

    private void CompleteBackgroundSaveIfReady()
    {
        if (_saveTask == null || !_saveTask.IsCompleted) return;
        SaveResult result;
        try { result = _saveTask.GetAwaiter().GetResult(); }
        catch (Exception exception) { result = new SaveResult(false, exception.Message); }

        if (!result.Success)
        {
            if (_inFlightDelta != null) _grid?.RestoreDirtyChunkDeltaSnapshot(_inFlightDelta);
            GD.PushError($"World save failed: {result.ErrorMessage}");
        }
        else if (_inFlightDelta != null)
        {
            MergeSavedDeltaIntoRegions(_inFlightDelta);
        }
        _saveTask = null;
        _inFlightDelta = null;
    }

    private void LoadWorld()
    {
        if (_grid?.TerrainSampler == null || _explorer == null) return;
        SaveDocument? document = null;
        RingWorldCaveContractCompatibilityResult? compatibility = null;
        if (File.Exists(_statePath))
        {
            try
            {
                document = JsonSerializer.Deserialize<SaveDocument>(File.ReadAllText(_statePath));
                if (document == null)
                    throw new InvalidDataException("World state document is empty.");
                if (!IsBackendCompatible(document, out string backendReason))
                    throw new InvalidDataException(backendReason);
                compatibility = EvaluateCompatibility(document);
                if (!compatibility.Value.CanLoad)
                {
                    GD.PushWarning(
                        $"World preserved read-only before edit restoration: {compatibility.Value.Reason}");
                    RejectLoad(compatibility.Value.Reason);
                    return;
                }
            }
            catch (Exception exception)
            {
                GD.PushWarning(
                    $"World state could not be validated and was preserved read-only: {exception.Message}");
                RejectLoad(exception.Message);
                return;
            }
        }

        // Smooth mode owns a separate journal and must never hydrate the dormant
        // legacy grid from old voxel files.  A conversion is deliberately
        // fail-closed until every legacy record can be mapped without loss.
        Error deltaError = Error.Unavailable;
        if (_surfaceBackendActive)
        {
            if (_surfaceTerrain == null || !_surfaceTerrain.EnsureInitialized() || !_surfaceTerrain.IsReady)
            {
                GD.PushWarning($"Surface terrain save could not be loaded and was preserved read-only: {_surfaceTerrain?.LastError ?? "Terrain3D gameplay node is unavailable."}");
                RejectLoad(_surfaceTerrain?.LastError ?? "Terrain3D gameplay node is unavailable.");
                return;
            }
        }
        else if (_smoothBackendActive)
        {
            if (LegacyEditableSaveExists())
                GD.PushWarning("Legacy editable save detected and left read-only; no smooth importer is certified for this slot.");
            LoadSmoothTerrainState();
        }
        else
        {
            deltaError = LoadLegacyVoxelDeltas();
        }

        if (document != null)
        {
            ApplyDocument(document);
            if (compatibility!.Value.RequiresRewrite)
                GD.Print($"World save compatibility migration accepted: {compatibility.Value.Reason}");
        }
        int activatedRegions = 0;
        if (!_smoothBackendActive && !_surfaceBackendActive)
        {
            DiscoverRegionJournals();
            activatedRegions = ActivateRegionsAroundExplorer(force: true);
            if (activatedRegions == 0)
            {
                RebuildActiveDelta();
                ApplyActiveDeltaSnapshot();
            }
        }
        _loaded = true;
        GD.Print($"World save ready: {_worldDirectory} (terrainSeed={_grid!.TerrainSampler!.Seed}, legacy edits={deltaError}, active regions={activatedRegions}).");
    }

    private void LoadSmoothTerrainState()
    {
        if (_smoothTerrain != null && File.Exists(_smoothJournalPath))
        {
            SmoothTerrainJournalLoadResult result = SmoothTerrainEditJournalStore.LoadSnapshot(_smoothJournalPath, _smoothTerrain.TerrainDefinitionHash, _smoothTerrain.CircumferenceMeters, _smoothTerrain.AcceptedLegacyTerrainDefinitionHashes);
            if (!result.Success || !_smoothTerrain.RestoreJournalEnvelope(result.Envelope)) GD.PushWarning($"Smooth terrain journal ignored (preserved read-only): {result.Error}");
        }
        if (_constructionOverlay != null && File.Exists(_constructionPath))
        {
            try { var commands = JsonSerializer.Deserialize<SmoothConstructionCommand[]>(File.ReadAllText(_constructionPath)); if (commands == null || !_constructionOverlay.ReplaceCommands(commands)) GD.PushWarning("Smooth construction journal ignored (preserved read-only). "); }
            catch (Exception exception) { GD.PushWarning($"Smooth construction journal ignored (preserved read-only): {exception.Message}"); }
        }
    }

    private string GetBackendName()
    {
        if (_surfaceBackendActive) return "surface_terrain3d";
        if (_smoothBackendActive) return "smooth_editable";
        return "legacy_voxel";
    }

    private bool IsBackendCompatible(SaveDocument document, out string reason)
    {
        string expected = GetBackendName();
        if (string.IsNullOrWhiteSpace(document.Backend))
        {
            // Historical world-state documents predate the marker. They are
            // accepted only by the existing backends; Terrain3D is isolated.
            if (!_surfaceBackendActive) { reason = string.Empty; return true; }
            reason = "World state has no terrain backend marker and cannot be loaded by Terrain3D.";
            return false;
        }
        if (string.Equals(document.Backend, expected, StringComparison.Ordinal)) { reason = string.Empty; return true; }
        reason = $"World state terrain backend '{document.Backend}' is incompatible with active backend '{expected}'.";
        return false;
    }

    private void RejectLoad(string reason)
    {
        _loadRejected = true;
        if (_surfaceBackendActive)
            _surfaceTerrain?.BlockSavingAndEditing("World state rejected: " + reason);
        SetProcess(false);
    }

    internal static bool IsSavePermittedForLoadState(bool loaded, bool loadRejected) => loaded && !loadRejected;

    private bool LegacyEditableSaveExists()
    {
        if (File.Exists(ProjectSettings.GlobalizePath(_deltaPath)) || File.Exists(_deltaJournalPath))
            return true;
        string regionsDirectory = Path.Combine(_worldDirectory, "regions");
        return Directory.Exists(regionsDirectory)
            && Directory.EnumerateFiles(regionsDirectory, "*.ndjson", SearchOption.TopDirectoryOnly).Any();
    }

    private Error LoadLegacyVoxelDeltas()
    {
        var combined = new VoxelChunkDeltaSnapshot();
        var naturalDensityDeltas = new Dictionary<string, NaturalTerrainSdfDeltaRecord>();
        string legacyPath = ProjectSettings.GlobalizePath(_deltaPath);
        try
        {
            if (File.Exists(legacyPath))
            {
                VoxelChunkDeltaSnapshot? baseline =
                    JsonSerializer.Deserialize<VoxelChunkDeltaSnapshot>(File.ReadAllText(legacyPath));
                if (baseline != null)
                {
                    foreach (var pair in baseline.ChunkOverrides)
                        combined.ChunkOverrides[pair.Key] = pair.Value;
                    MergeNaturalTerrainSdfDeltas(naturalDensityDeltas, baseline.NaturalTerrainSdfDeltas);
                }
            }

            foreach (string journalPath in EnumerateLegacyDeltaJournals())
            {
                foreach (string line in File.ReadLines(journalPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    VoxelChunkDeltaSnapshot? patch = JsonSerializer.Deserialize<VoxelChunkDeltaSnapshot>(line);
                    if (patch == null) continue;
                    foreach (var pair in patch.ChunkOverrides)
                    {
                        if (pair.Value.Count == 0) combined.ChunkOverrides.Remove(pair.Key);
                        else combined.ChunkOverrides[pair.Key] = pair.Value;
                    }
                    MergeNaturalTerrainSdfDeltas(naturalDensityDeltas, patch.NaturalTerrainSdfDeltas);
                }
            }

            combined.NaturalTerrainSdfDeltas.AddRange(naturalDensityDeltas.Values);
            MergeDeltaPatch(_legacyDelta, combined);
            return Error.Ok;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Voxel deltas could not be loaded: {exception.Message}");
            return Error.ParseError;
        }
    }

    private IEnumerable<string> EnumerateLegacyDeltaJournals()
    {
        if (File.Exists(_deltaJournalPath)) yield return _deltaJournalPath;
    }

    private void DiscoverRegionJournals()
    {
        _regionJournals.Clear();
        string regionsDirectory = Path.Combine(_worldDirectory, "regions");
        if (!Directory.Exists(regionsDirectory)) return;
        foreach (string path in Directory.EnumerateFiles(regionsDirectory, "*.ndjson", SearchOption.TopDirectoryOnly))
        {
            if (TryParseRegionJournalPath(path, out RegionKey key))
                _regionJournals[key] = path;
        }
    }

    private void QueueRegionActivation()
    {
        if (_smoothBackendActive || _surfaceBackendActive || _grid == null || _explorer == null || _regionJournals.Count == 0) return;
        RegionKey center = GetRegionKey(_explorer.CanonicalPosition);
        if (_pendingActivationCenter == center)
        {
            if (_saveTask == null) ActivateRegionsAroundExplorer(force: true);
            return;
        }
        if (_lastActivationCenter == center) return;
        _pendingActivationCenter = center;
        if (_saveTask == null) SaveWorld();
        if (_saveTask == null)
        {
            ActivateRegionsAroundExplorer(force: true);
            _pendingActivationCenter = null;
        }
    }

    private int ActivateRegionsAroundExplorer(bool force = false)
    {
        if (_grid == null || _explorer == null || _regionJournals.Count == 0) return 0;
        RegionKey center = GetRegionKey(_explorer.CanonicalPosition);
        if (_pendingActivationCenter != null && _pendingActivationCenter != center) return 0;
        if (_saveTask != null) return 0;
        if (!force && _lastActivationCenter == center) return 0;
        _lastActivationCenter = center;
        _pendingActivationCenter = null;
        int activated = 0;
        int radius = Math.Max(0, ActiveRegionRadius);
        long aroundRegionCount = Math.Max(1L, (long)Math.Ceiling(
            _grid.GetCanonicalRingCircumferenceMeters64() / (VoxelConstants.ChunkSizeX * (double)RegionChunkSpan)));
        for (int acrossOffset = -radius; acrossOffset <= radius; acrossOffset++)
        for (long aroundOffset = -radius; aroundOffset <= radius; aroundOffset++)
        {
            long around = (center.AroundRegion + aroundOffset) % aroundRegionCount;
            if (around < 0) around += aroundRegionCount;
            var key = new RegionKey(around, center.AcrossRegion + acrossOffset);
            if (_loadedRegionDeltas.ContainsKey(key) || !_regionJournals.TryGetValue(key, out string? path)) continue;
            _loadedRegionDeltas[key] = LoadRegionJournal(path);
            activated++;
        }
        int retainedRadius = Math.Max(radius, RetainedRegionRadius);
        foreach (RegionKey key in new List<RegionKey>(_loadedRegionDeltas.Keys))
        {
            if (!IsWithinRegionWindow(key, center, retainedRadius, aroundRegionCount))
                _loadedRegionDeltas.Remove(key);
        }
        if (activated > 0 || force)
        {
            RebuildActiveDelta();
            ApplyActiveDeltaSnapshot();
        }
        return activated;
    }

    private RegionKey GetRegionKey(in RingCanonicalPosition position)
    {
        long aroundChunk = FloorDiv((long)Math.Floor(position.AroundMeters), VoxelConstants.ChunkSizeX);
        int acrossChunk = FloorDiv((int)Math.Floor(position.AcrossMeters), VoxelConstants.ChunkSizeZ);
        return new RegionKey(FloorDiv(aroundChunk, RegionChunkSpan), FloorDiv(acrossChunk, RegionChunkSpan));
    }

    private static VoxelChunkDeltaSnapshot LoadRegionJournal(string path)
    {
        var snapshot = new VoxelChunkDeltaSnapshot();
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            VoxelChunkDeltaSnapshot? patch = JsonSerializer.Deserialize<VoxelChunkDeltaSnapshot>(line);
            if (patch != null) MergeDeltaPatch(snapshot, patch);
        }
        return snapshot;
    }

    private void MergeSavedDeltaIntoRegions(VoxelChunkDeltaSnapshot delta)
    {
        foreach ((RegionKey key, VoxelChunkDeltaSnapshot patch) in PartitionDeltaByRegion(delta))
        {
            if (!_loadedRegionDeltas.TryGetValue(key, out VoxelChunkDeltaSnapshot? activeRegion))
            {
                activeRegion = new VoxelChunkDeltaSnapshot();
                _loadedRegionDeltas[key] = activeRegion;
            }
            MergeDeltaPatch(activeRegion, patch);
        }
        RebuildActiveDelta();
    }

    private void RebuildActiveDelta()
    {
        _activeDelta.ChunkOverrides.Clear();
        _activeDelta.NaturalTerrainSdfDeltas.Clear();
        MergeDeltaPatch(_activeDelta, _legacyDelta);
        foreach (VoxelChunkDeltaSnapshot region in _loadedRegionDeltas.Values)
            MergeDeltaPatch(_activeDelta, region);
    }

    private Dictionary<RegionKey, VoxelChunkDeltaSnapshot> PartitionDeltaByRegion(VoxelChunkDeltaSnapshot delta)
    {
        var regions = new Dictionary<RegionKey, VoxelChunkDeltaSnapshot>();
        foreach ((string chunkKey, Dictionary<int, ushort> overrides) in delta.ChunkOverrides)
        {
            if (!TryParseChunkKey(chunkKey, out long aroundChunk, out int acrossChunk)) continue;
            RegionKey key = new(FloorDiv(aroundChunk, RegionChunkSpan), FloorDiv(acrossChunk, RegionChunkSpan));
            if (!regions.TryGetValue(key, out VoxelChunkDeltaSnapshot? patch)) regions[key] = patch = new();
            patch.ChunkOverrides[chunkKey] = overrides;
        }
        foreach (NaturalTerrainSdfDeltaRecord record in delta.NaturalTerrainSdfDeltas)
        {
            RegionKey key = new(
                FloorDiv(FloorDiv(record.AroundVoxel, VoxelConstants.ChunkSizeX), RegionChunkSpan),
                FloorDiv(FloorDiv(record.AcrossVoxel, VoxelConstants.ChunkSizeZ), RegionChunkSpan));
            if (!regions.TryGetValue(key, out VoxelChunkDeltaSnapshot? patch)) regions[key] = patch = new();
            patch.NaturalTerrainSdfDeltas.Add(record);
        }
        return regions;
    }

    private static bool IsWithinRegionWindow(RegionKey key, RegionKey center, int radius, long aroundRegionCount)
    {
        long aroundDistance = Math.Abs(key.AroundRegion - center.AroundRegion) % aroundRegionCount;
        aroundDistance = Math.Min(aroundDistance, aroundRegionCount - aroundDistance);
        return aroundDistance <= radius && Math.Abs((long)key.AcrossRegion - center.AcrossRegion) <= radius;
    }

    private void ApplyActiveDeltaSnapshot()
    {
        if (_activeDelta.ChunkOverrides.Count > 0 || _activeDelta.NaturalTerrainSdfDeltas.Count > 0)
            _grid?.ApplySparseChunkDeltaSnapshot(_activeDelta, flushRebuildQueueAfterApply: true);
    }

    private static void MergeDeltaPatch(VoxelChunkDeltaSnapshot destination, VoxelChunkDeltaSnapshot patch)
    {
        foreach ((string key, Dictionary<int, ushort> overrides) in patch.ChunkOverrides)
        {
            if (overrides.Count == 0) destination.ChunkOverrides.Remove(key);
            else destination.ChunkOverrides[key] = new Dictionary<int, ushort>(overrides);
        }
        var natural = new Dictionary<string, NaturalTerrainSdfDeltaRecord>();
        MergeNaturalTerrainSdfDeltas(natural, destination.NaturalTerrainSdfDeltas);
        MergeNaturalTerrainSdfDeltas(natural, patch.NaturalTerrainSdfDeltas);
        destination.NaturalTerrainSdfDeltas.Clear();
        destination.NaturalTerrainSdfDeltas.AddRange(natural.Values);
    }

    private static bool TryParseRegionJournalPath(string path, out RegionKey key)
    {
        key = default;
        string name = Path.GetFileNameWithoutExtension(path);
        string[] parts = name.Split('_');
        if (parts.Length != 4
            || parts[0] != "a"
            || parts[2] != "x"
            || !long.TryParse(parts[1], out long around)
            || !int.TryParse(parts[3], out int across))
        {
            return false;
        }
        key = new RegionKey(around, across);
        return true;
    }

    private static bool TryParseChunkKey(string key, out long aroundChunk, out int acrossChunk)
    {
        aroundChunk = default;
        acrossChunk = default;
        string[] parts = key.Split(',');
        return parts.Length == 3
            && long.TryParse(parts[0], out aroundChunk)
            && int.TryParse(parts[1], out acrossChunk)
            && int.TryParse(parts[2], out _);
    }

    private static long FloorDiv(long value, long divisor)
    {
        long quotient = value / divisor;
        long remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static void MergeNaturalTerrainSdfDeltas(
        Dictionary<string, NaturalTerrainSdfDeltaRecord> destination,
        IEnumerable<NaturalTerrainSdfDeltaRecord> source)
    {
        foreach (NaturalTerrainSdfDeltaRecord record in source)
        {
            string key = FormattableString.Invariant(
                $"{record.AroundVoxel}:{record.VerticalVoxel}:{record.AcrossVoxel}");
            if (!float.IsFinite(record.DensityDelta) || Mathf.IsZeroApprox(record.DensityDelta))
                destination.Remove(key);
            else
                destination[key] = record;
        }
    }

    private RingWorldDesignedCaveContractSnapshot CaptureDesignedCaveContract()
    {
        RingTerrainSampler sampler = _grid!.TerrainSampler!;
        sampler.CaveGenerationContract.ValidateTerrainSeed(sampler.Seed);
        return RingWorldDesignedCaveContractSnapshot.Capture(
            sampler.CaveGenerationContract,
            sampler.CavePlacementPolicy,
            _grid.TerrainSettings?.EnableDesignedCaveDensityComposition == true,
            _grid.GetVerticalDomainConfiguration());
    }

    private RingWorldCaveContractCompatibilityResult EvaluateCompatibility(SaveDocument document)
    {
        RingTerrainSampler sampler = _grid!.TerrainSampler!;
        ulong[] acceptedLegacyHashes = _smoothBackendActive && _smoothTerrain != null
            ? _smoothTerrain.AcceptedLegacyTerrainDefinitionHashes
            : Array.Empty<ulong>();
        return RingWorldCaveContractCompatibilityPolicy.Evaluate(
            document.FormatVersion,
            SaveFormatVersion,
            document.TerrainGeneratorVersion,
            sampler.TerrainGeneratorVersion,
            document.TerrainDefinitionHash,
            sampler.TerrainDefinitionHash,
            acceptedLegacyHashes,
            document.CircumferenceMeters,
            _grid.GetCanonicalRingCircumferenceMeters64(),
            document.DesignedCaveContract,
            CaptureDesignedCaveContract());
    }

    private void ApplyDocument(SaveDocument document)
    {
        PlayerDocument player = document.Player;
        _explorer!.RestoreSavedState(
            new RingCanonicalPosition(player.AroundMeters, player.VerticalMeters, player.AcrossMeters),
            new Vector3(player.VelocityAround, player.VelocityVertical, player.VelocityAcross),
            player.Yaw,
            player.Pitch,
            player.Noclip,
            player.NoclipSpeedTier,
            player.NoclipSpeed,
            player.HasUndergroundCheckpoint
                ? new RingCanonicalPosition(player.CheckpointAroundMeters, player.CheckpointVerticalMeters, player.CheckpointAcrossMeters)
                : null);

        RuntimeDocument runtime = document.Runtime;
        string activeHardwareTier = GetActiveHardwareTier();
        bool hardwareTierChanged = !string.Equals(
            runtime.HardwareTier,
            activeHardwareTier,
            StringComparison.Ordinal);
        if (hardwareTierChanged)
        {
            ApplyHardwareBaseline(runtime, activeHardwareTier);
            GD.Print($"World performance settings adapted for hardware tier: {activeHardwareTier}.");
        }
        if (_coordinator != null)
        {
            var preset = (RingTerrainLodCoordinator.QualityPreset)Math.Clamp(
                runtime.TerrainQuality,
                (int)RingTerrainLodCoordinator.QualityPreset.Low,
                (int)RingTerrainLodCoordinator.QualityPreset.VeryHigh);
            _coordinator.ApplyUserQualityPreset(preset);
            _coordinator.MaxConcurrentWorkerBuilds = Math.Clamp(runtime.TerrainWorkers, 1, 32);
        }
        if (_smoothBackendActive && _smoothTerrain != null)
        {
            int surfaceRadius = Math.Clamp(runtime.SurfaceChunkRadius, 1, 32);
            _smoothTerrain.RadiusAround = surfaceRadius;
            _smoothTerrain.RadiusAcross = surfaceRadius;
            _smoothTerrain.RadiusVertical = Math.Clamp(runtime.VerticalChunkRadius, 0, 32);
            _smoothTerrain.RefreshDesired();
        }
        else
        {
            _grid!.ApplySurfaceStreamingRadius(Math.Clamp(runtime.SurfaceChunkRadius, 2, 32));
            _grid.ApplyVerticalStreamingRadius(Math.Clamp(runtime.VerticalChunkRadius, 2, 32));
        }
        RingAtmosphereModel.Enabled = runtime.AtmosphericHaze;
        ProjectSettings.SetSetting("ringworld/runtime/haze_enabled", runtime.AtmosphericHaze);
        if (_clouds != null)
        {
            var quality = (RingworldTechDemo.RingVolumetricCloudLayer.CloudQuality)Math.Clamp(
                runtime.CloudQuality,
                (int)RingworldTechDemo.RingVolumetricCloudLayer.CloudQuality.Off,
                (int)RingworldTechDemo.RingVolumetricCloudLayer.CloudQuality.Ultra);
            _clouds.SetQuality(quality);
            _clouds.CloudSimulationTimeSeconds = Math.Max(0.0, runtime.CloudSimulationTimeSeconds);
        }
        if (_celestialClock != null && _celestialClock.HasMethod("set_simulation_seconds"))
            _celestialClock.Call("set_simulation_seconds", Math.Max(0.0, runtime.CelestialSimulationTimeSeconds));
        if (_celestialClock != null)
        {
            _celestialClock.Set("Paused", runtime.CelestialPaused);
            _celestialClock.Set("TimeScale", Math.Clamp(runtime.CelestialTimeScale, 1.0f, 100.0f));
        }
    }

    private double GetCelestialSimulationTimeSeconds()
    {
        if (_celestialClock == null || !_celestialClock.HasMethod("get_simulation_seconds")) return 0.0;
        Variant value = _celestialClock.Call("get_simulation_seconds");
        return Math.Max(0.0, value.AsDouble());
    }

    private bool GetCelestialPaused() => _celestialClock?.Get("Paused").AsBool() ?? false;
    private float GetCelestialTimeScale() => Math.Clamp(_celestialClock?.Get("TimeScale").AsSingle() ?? 1.0f, 1.0f, 100.0f);

    private static string GetActiveHardwareTier() =>
        ProjectSettings.GetSetting(
            "ringworld/runtime/hardware_tier",
            "laptop_140v_baseline").AsString();

    private static void ApplyHardwareBaseline(RuntimeDocument runtime, string tier)
    {
        bool desktop = string.Equals(tier, "desktop_mid_high", StringComparison.Ordinal);
        runtime.HardwareTier = tier;
        runtime.TerrainQuality = (int)(desktop
            ? RingTerrainLodCoordinator.QualityPreset.VeryHigh
            : RingTerrainLodCoordinator.QualityPreset.High);
        runtime.TerrainWorkers = desktop ? 8 : 3;
        runtime.SurfaceChunkRadius = 7;
        runtime.VerticalChunkRadius = 7;
        runtime.CloudQuality = (int)(desktop
            ? RingworldTechDemo.RingVolumetricCloudLayer.CloudQuality.VeryHigh
            : RingworldTechDemo.RingVolumetricCloudLayer.CloudQuality.High);
    }
}
