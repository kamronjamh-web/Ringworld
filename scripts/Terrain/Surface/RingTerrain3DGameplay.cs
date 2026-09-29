using System;
using System.IO;
using Godot;
using Godot.Collections;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Production-scene bridge for the bounded editable Terrain3D surface.</summary>
public partial class RingTerrain3DGameplay : Node3D
{
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath CoordinatorPath { get; set; } = new("../TerrainLodCoordinator");
    [Export] public NodePath CameraPath { get; set; } = new("../Explorer/Head/Camera3D");
    // Midpoint of the 64–104m proxy transition. The current bounded collision
    // window guarantees coverage for this near-surface radius during rebasing.
    private const float HandoffDistance = 84f;
    private const string RuntimeSurfaceEditFileName = "surface_runtime_edits_v1.json";

    private VoxelChunkGridManager? _grid;
    private RingVoxelExplorerController? _explorer;
    private RingTerrainLodCoordinator? _coordinator;
    private Camera3D? _camera;
    private RingTerrainSampler? _sampler;
    private RingSurfaceHeightField? _field;
    private RingTerrain3DSurface? _surface;
    private RingSurfaceFrame _frame;
    private Transform3D _lastWorldFrame;
    private string _savePath = "";
    private string _lastError = "";
    private bool _saveBlocked;
    private bool _initializationFailed;
    private bool _subscribed;
    private float _actualWalkSpeed = 8;
    private float _brushRadius = 8;
    private float _brushStep = 2;
    private int _meshLods = 4;
    private RingTerrain3DGameplayStream? _stream;
    private RingSurfaceFrame _pendingFrame;
    private bool _hasPendingFrame;
    private long _requestRevision = 1;
    private long _committedRevision = 1;
    private ulong _pendingEditRevision;
    private float _generationPrefetchMeters = 64.0f;
    private float _generationUploadBudgetMs = 1.0f;
    private float _generationCollisionBudgetMs = .75f;
    private int _generationTileCells = 32;
    private int _generationWorkers = 2;
    private int _generationCacheMiB = 8;
    private bool _pendingEditReplacement;
    private bool _synchronousBenchmark;

    public bool IsReady => _surface?.IsReady == true;
    public string LastError => _lastError.Length > 0 ? _lastError : _surface?.LastError ?? "";
    public float BrushStepMeters => _brushStep;
    /// <summary>Managed teardown result retained after the Godot renderer exits.</summary>
    public bool SurfaceEditsSavedOnExit { get; private set; }
    public string SurfaceEditsExitSaveError { get; private set; } = string.Empty;

    public override void _Ready()
    {
        EnsureInitialized();
    }

    public override void _ExitTree()
    {
        SurfaceEditsSavedOnExit = SaveSurfaceEdits();
        SurfaceEditsExitSaveError = SurfaceEditsSavedOnExit ? string.Empty : LastError;
        if (_grid != null)
        {
            if (_subscribed) _grid.CanonicalFloatingOriginAnchorChanged -= OnFloatingOriginChanged;
            _grid.SurfaceTerrainVisualReady = false;
            if (_field != null) _grid.DetachSurfaceHeightField(_field);
        }
        _stream?.Dispose();
        _stream = null;
        _sampler?.Dispose();
        _sampler = null;
    }

    public override void _Process(double delta)
    {
        if (!EnsureInitialized() || _grid == null || _explorer == null || _surface == null || _field == null) return;
        if (_synchronousBenchmark)
        {
            ProcessSynchronousBenchmark(_explorer.CanonicalPosition);
        }
        else
        {
            ServiceStreaming(_explorer.CanonicalPosition);
            _surface.SetGameplayWorldFrames(_lastWorldFrame, _hasPendingFrame ? BuildWorldFrame(_pendingFrame) : _lastWorldFrame);
        }
        Vector3 cameraPosition = _camera?.GlobalPosition ?? _explorer.GlobalPosition;
        _surface.UpdateCamera(_lastWorldFrame.AffineInverse() * cameraPosition);
        _surface.SetHandoffOriginWorld(_explorer.GlobalPosition);
        UpdateSurfaceAppearance();
        _grid.SurfaceTerrainVisualReady = HasVisualCoverage(_explorer.CanonicalPosition);
    }

    public bool EnsureInitialized()
    {
        if (_initializationFailed) return false;
        if (_field != null) return IsReady && !_saveBlocked;
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _coordinator = GetNodeOrNull<RingTerrainLodCoordinator>(CoordinatorPath);
        _camera = GetNodeOrNull<Camera3D>(CameraPath);
        if (_grid == null) { _lastError = "Grid manager unavailable."; return false; }
        try
        {
            _sampler = _grid.CreateIndependentTerrainSampler() ?? throw new InvalidOperationException("Terrain sampler unavailable.");
            long circumference = checked((long)Math.Round(_grid.GetCanonicalRingCircumferenceMeters64()));
            int width = Mathf.RoundToInt(_grid.RingAcrossWidthMeters);
            if (!_grid.UseTerrain3DSurface || circumference != CertifiedRingScale.AroundVoxelLength)
                throw new InvalidOperationException("Terrain3D gameplay requires certified surface scale.");
            bool runtimeSurfaceGeneration = _sampler.UsesRuntimeSurfaceGeneration;
            float minimumHeight = runtimeSurfaceGeneration ? -8000f : CertifiedRingScale.TerrainFloorMeters;
            float maximumHeight = runtimeSurfaceGeneration ? 8000f : CertifiedRingScale.TerrainCeilingMeters;
            Func<long, int, float> baseline = runtimeSurfaceGeneration
                ? _sampler.GetPresentationSurfaceHeight
                : (around, across) => _sampler.GetSmoothSurfaceHeight(around, across) + .5f;
            _field = new RingSurfaceHeightField(
                _sampler.TerrainDefinitionHash,
                circumference,
                width,
                minimumHeight,
                maximumHeight,
                baseline,
                useTriangleInterpolation: runtimeSurfaceGeneration);
            _savePath = ResolveSavePath();
            LoadEdits();
            if (_saveBlocked) throw new InvalidDataException(_lastError);
            _grid.AttachSurfaceHeightField(_field);
            _coordinator?.ConfigureVoxelTransition(64, 104);
            RingCanonicalPosition position = _explorer?.CanonicalPosition ?? new RingCanonicalPosition(0, 0, width * .5);
            _frame = BuildFrame(position);
            _lastWorldFrame = BuildWorldFrame(_frame);
            _surface = new RingTerrain3DSurface { Name = "Terrain3DSurface" };
            AddChild(_surface);
            _surface.SetHandoffDistance(HandoffDistance);
            _surface.SetMeshLods(_meshLods);
            if (_synchronousBenchmark)
            {
                _surface.SetBiomeSampler(_sampler);
                _surface.Initialize(_field, _frame);
                _surface.SetWorldFrame(_lastWorldFrame);
                _surface.SetMeshLods(_meshLods);
            }
            else
            {
                _surface.InitializeGameplayWindow(_field, _frame, _sampler);
                _surface.SetGameplayWorldFrames(_lastWorldFrame, _lastWorldFrame);
                _stream = new RingTerrain3DGameplayStream(_sampler, minimumHeight, maximumHeight, width, circumference);
                _stream.Configure(_generationWorkers, _generationCacheMiB);
                if (_surface.TryGetInitialGameplayCache(out RingSurfaceFrame cacheFrame, out float[] cacheHeights, out byte[] cacheBiomes))
                    _stream.PrimeBaselineWindow(cacheFrame, cacheHeights, cacheBiomes);
            }
            _pendingEditRevision = _committedEditRevision = _field.Revision;
            _grid.SurfaceTerrainVisualReady = HasVisualCoverage(position);
            if (!_subscribed) { _grid.CanonicalFloatingOriginAnchorChanged += OnFloatingOriginChanged; _subscribed = true; }
            if (_explorer != null) _explorer.MoveSpeed = _actualWalkSpeed;
            return IsReady;
        }
        catch (Exception error)
        {
            _initializationFailed = true;
            _grid.SurfaceTerrainVisualReady = false;
            _lastError = error.Message;
            GD.PushError("Terrain3D gameplay: " + error.Message);
            return false;
        }
    }

    public void EnsureCoverage(RingCanonicalPosition position)
    {
        if (_synchronousBenchmark)
        {
            EnsureSynchronousBenchmarkCoverage(position);
            return;
        }
        RequestCoverage(position, false);
    }

    public float SampleHeight(double around, double across)
    {
        if (_field == null || _sampler == null || !double.IsFinite(around) || !double.IsFinite(across) || across < 0 || across > _field.WidthMeters - _field.SpacingMeters)
            return float.NaN;
        return _field.SampleHeight(around, across);
    }

    public void BlockSavingAndEditing(string reason)
    {
        _saveBlocked = true;
        _lastError = string.IsNullOrWhiteSpace(reason) ? "Surface editing was blocked." : reason;
    }
    public bool ApplySurfaceBrushAtCanonical(double around, double across, float delta)
    {
        if (_field == null || _saveBlocked || !_field.ApplyBrush(around, across, _brushRadius, delta)) return false;
        if (_synchronousBenchmark)
        {
            _surface?.RefreshEditedSurface();
            if (_grid != null) _grid.SurfaceTerrainVisualReady = _explorer != null && HasVisualCoverage(_explorer.CanonicalPosition);
            return true;
        }
        // A held brush can produce many revisions. Publish the latest snapshot
        // once the input settles, while the existing collider remains active.
        _editDebounceUntilUsec = Time.GetTicksUsec() + 100_000;
        CancelPendingWindow();
        if (_grid != null) _grid.SurfaceTerrainVisualReady = _explorer != null && HasVisualCoverage(_explorer.CanonicalPosition);
        return true;
    }
    public bool SaveSurfaceEdits()
    {
        if (_field == null) return !_saveBlocked;
        if (_saveBlocked) return false;
        RingSurfaceEditStoreResult result = _field.Save(_savePath);
        if (!result.Success)
        {
            _lastError = "Surface save failed: " + result.Error;
            return false;
        }
        return true;
    }
    public void ApplySurfaceSettings(GameplaySurfaceTerrainSettings settings)
    {
        ApplySurfaceAppearanceSettings(settings);
        _brushRadius = GameplaySurfaceTerrainDefaults.ClampBrushRadiusMeters(settings.BrushRadiusMeters);
        _brushStep = GameplaySurfaceTerrainDefaults.ClampBrushStepMeters(settings.BrushStepMeters);
        _meshLods = GameplaySurfaceTerrainDefaults.ClampMeshLods(settings.MeshLods);
        _actualWalkSpeed = GameplaySurfaceTerrainDefaults.ClampMoveSpeedMetersPerSecond(settings.MoveSpeedMetersPerSecond);
        if (_explorer != null) _explorer.MoveSpeed = _actualWalkSpeed;
        _surface?.SetMeshLods(_meshLods);
        _generationPrefetchMeters = GameplaySurfaceTerrainDefaults.ClampGenerationPrefetchMeters(settings.GenerationPrefetchMeters);
        _generationUploadBudgetMs = GameplaySurfaceTerrainDefaults.ClampGenerationUploadBudgetMs(settings.GenerationUploadBudgetMs);
        _generationCollisionBudgetMs = GameplaySurfaceTerrainDefaults.ClampGenerationCollisionBudgetMs(settings.GenerationCollisionBudgetMs);
        _generationTileCells = GameplaySurfaceTerrainDefaults.ClampGenerationColliderTileCells(settings.GenerationColliderTileCells);
        _generationWorkers = GameplaySurfaceTerrainDefaults.ClampGenerationWorkerCount(settings.GenerationWorkerCount);
        _generationCacheMiB = GameplaySurfaceTerrainDefaults.ClampGenerationCacheMiB(settings.GenerationCacheMiB);
        _stream?.Configure(_generationWorkers, _generationCacheMiB);
    }
    public Dictionary GetSurfaceDiagnostics()
    {
        RingCanonicalPosition position = _explorer?.CanonicalPosition ?? default;
        return new()
        {
            ["ready"] = IsReady,
            ["synchronous_benchmark"] = _synchronousBenchmark,
            ["rebuild"] = _surface?.GetRebuildDiagnostics() ?? new Dictionary(),
            ["native"] = _surface?.GetPreparedDiagnostics() ?? new Dictionary(),
            ["streaming"] = StreamingDiagnostics(),
            ["edits"] = _field?.EditCount ?? 0,
            ["revision"] = (long)(_field?.Revision ?? 0),
            ["request_revision"] = _requestRevision,
            ["committed_revision"] = _committedRevision,
            ["pending_edit_revision"] = (long)_pendingEditRevision,
            ["last_generation_ms"] = _stream?.LastGenerationMilliseconds ?? 0.0,
            ["last_readiness_ms"] = _lastReadinessMilliseconds,
            ["max_readiness_ms"] = _maxReadinessMilliseconds,
            ["coverage_waits"] = _coverageWaits,
            ["visual_coverage"] = HasVisualCoverage(position),
            ["regions"] = _surface?.RegionCount ?? 0,
            ["native_origin_height"] = _surface?.TerrainNativeData?.Call("get_height", Vector3.Zero).AsSingle() ?? float.NaN,
            ["frame_around"] = _frame.AnchorAroundMeters,
            ["frame_across"] = _frame.AnchorAcrossMeters,
            ["player_around"] = position.AroundMeters,
            ["player_across"] = position.AcrossMeters,
            ["player_height"] = position.VerticalMeters,
            ["surface_height"] = SampleHeight(position.AroundMeters, position.AcrossMeters),
            ["grounded"] = _explorer?.IsOnFloor() ?? false,
            ["swimming"] = _explorer?.IsSwimming ?? false,
            ["velocity"] = _explorer?.Velocity ?? Vector3.Zero,
            ["save_path"] = _savePath,
            ["error"] = LastError,
            ["save_blocked"] = _saveBlocked,
            ["runtime_surface_generation"] = _sampler?.UsesRuntimeSurfaceGeneration ?? false,
            ["terrain_generator_version"] = _sampler?.TerrainGeneratorVersion ?? 0,
            ["terrain_definition_hash"] = _sampler is { } sampler ? sampler.TerrainDefinitionHash.ToString("X16") : string.Empty,
            ["surface_generation_recipe_json"] = _sampler?.SurfaceGenerationRecipeJson ?? string.Empty,
            ["surface_generation_contract_id"] = _sampler?.SurfaceGenerationContractId ?? string.Empty,
            ["surface_definition_version"] = _sampler?.UsesRuntimeSurfaceGeneration == true ? 1 : 0,
            ["minimum_height"] = _field?.MinimumHeight ?? float.NaN,
            ["maximum_height"] = _field?.MaximumHeight ?? float.NaN,
            ["sea_level"] = _sampler?.SeaLevelY ?? 0,
            ["sea_level_y"] = _sampler?.SeaLevelY ?? 0,
            ["bedrock_y"] = _sampler?.BedrockY ?? 0,
            ["build_ceiling"] = _grid?.MaxBuildGlobalY ?? 0,
            ["policy"] = _sampler?.UsesRuntimeSurfaceGeneration == true
                ? "surface-only; terrain -8000..8000 m; reserved build ceiling 8400 m; sea level 0 m; caves disabled; recipe immutable"
                : "Terrain3D surface-only; caves disabled"
        };
    }
    private void OnFloatingOriginChanged(RingLodGeometrySnapshot previous, RingLodGeometrySnapshot current, RingCanonicalPosition anchor)
    {
        if (_surface == null) return;
        _lastWorldFrame = BuildWorldFrame(_frame);
        if (_synchronousBenchmark)
            _surface.SetWorldFrame(_lastWorldFrame);
        else
            _surface.SetGameplayWorldFrames(_lastWorldFrame, _hasPendingFrame ? BuildWorldFrame(_pendingFrame) : _lastWorldFrame);
    }

    private RingSurfaceFrame BuildFrame(RingCanonicalPosition position)
    {
        long around = (long)Math.Round(position.AroundMeters / 2) * 2;
        int across = Mathf.Clamp((int)Math.Round(position.AcrossMeters / 2) * 2,
            192, Mathf.RoundToInt(_grid!.RingAcrossWidthMeters) - 194);
        return new RingSurfaceFrame(around, across, _field!.SampleHeight(around, across),
            _field.CircumferenceMeters, _grid.CaptureLodGeometrySnapshot().RingRadiusMeters,
            _field.SpacingMeters, -1);
    }

    private Transform3D BuildWorldFrame(RingSurfaceFrame frame)
    {
        RingLodGeometrySnapshot geometry = _grid!.CaptureLodGeometrySnapshot();
        double phi = frame.AnchorAroundMeters / geometry.RingRadiusMeters;
        Basis gridBasis = geometry.GridGlobalTransform.Basis;
        Vector3 tangent = (gridBasis * new Vector3((float)-Math.Sin(phi), 0, (float)Math.Cos(phi))).Normalized();
        Vector3 inward = (gridBasis * new Vector3((float)-Math.Cos(phi), 0, (float)-Math.Sin(phi))).Normalized();
        // The game's positive across axis completes a left-handed frame with
        // tangent and inward. Negate it here; AcrossDirection=-1 restores the
        // canonical sign while keeping the collider's transform right-handed.
        return new Transform3D(new Basis(tangent, inward, (gridBasis * Vector3.Down).Normalized()),
            _grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(
                frame.AnchorAroundMeters, frame.AnchorHeight, frame.AnchorAcrossMeters)));
    }

    private string ResolveSavePath()
    {
        string world = ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString();
        string directory = string.IsNullOrWhiteSpace(world)
            ? $"user://terrain3d_surface_gameplay/{_field!.TerrainDefinitionHash:X16}" : world;
        string fileName = _sampler?.UsesRuntimeSurfaceGeneration == true
            ? RuntimeSurfaceEditFileName
            : RingSurfaceEditStore.DefaultPath;
        return Path.Combine(ProjectSettings.GlobalizePath(directory), fileName);
    }

    private void LoadEdits()
    {
        if (_field == null || !File.Exists(_savePath)) return;
        RingSurfaceEditStoreResult result = _field.Load(_savePath);
        if (!result.Success)
        {
            _saveBlocked = true;
            _lastError = "Surface save rejected and protected: " + result.Error;
        }
    }
}
