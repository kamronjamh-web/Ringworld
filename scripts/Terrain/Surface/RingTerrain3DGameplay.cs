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

    public bool IsReady => _surface?.IsReady == true;
    public string LastError => _lastError.Length > 0 ? _lastError : _surface?.LastError ?? "";
    public float BrushStepMeters => _brushStep;

    public override void _Ready()
    {
        EnsureInitialized();
    }

    public override void _ExitTree()
    {
        SaveSurfaceEdits();
        if (_grid != null)
        {
            if (_subscribed) _grid.CanonicalFloatingOriginAnchorChanged -= OnFloatingOriginChanged;
            _grid.SurfaceTerrainVisualReady = false;
            if (_field != null) _grid.DetachSurfaceHeightField(_field);
        }
        _sampler?.Dispose();
        _sampler = null;
    }

    public override void _Process(double delta)
    {
        if (!EnsureInitialized() || _grid == null || _explorer == null || _surface == null || _field == null) return;
        EnsureCoverage(_explorer.CanonicalPosition);
        _surface.SetWorldFrame(_lastWorldFrame);
        Vector3 cameraPosition = _camera?.GlobalPosition ?? _explorer.GlobalPosition;
        _surface.UpdateCamera(_lastWorldFrame.AffineInverse() * cameraPosition);
        _surface.SetHandoffOriginWorld(_explorer.GlobalPosition);
        _surface.RefreshEditedSurface();
        _grid.SurfaceTerrainVisualReady = _surface.IsReady;
    }

    public bool EnsureInitialized()
    {
        if (_field != null) return IsReady && !_saveBlocked;
        if (_initializationFailed) return false;
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
            _field = new RingSurfaceHeightField(_sampler.TerrainDefinitionHash, circumference, width, CertifiedRingScale.TerrainFloorMeters, CertifiedRingScale.TerrainCeilingMeters, (around, across) => _sampler.GetSmoothSurfaceHeight(around, across) + .5f);
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
            _surface.SetBiomeSampler(_sampler);
            _surface.SetHandoffDistance(HandoffDistance);
            _surface.Initialize(_field, _frame);
            _surface.SetWorldFrame(_lastWorldFrame);
            _surface.SetMeshLods(_meshLods);
            _grid.SurfaceTerrainVisualReady = _surface.IsReady;
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
        if (_field == null || _surface == null) return;
        double aroundDelta = _frame.SignedAroundDeltaMeters(position.AroundMeters);
        double acrossDelta = position.AcrossMeters - _frame.AnchorAcrossMeters;
        if (Math.Abs(aroundDelta) < 128 && Math.Abs(acrossDelta) < 128)
        {
            _lastWorldFrame = BuildWorldFrame(_frame);
            _surface.SetWorldFrame(_lastWorldFrame);
            return;
        }
        RingSurfaceFrame nextFrame = BuildFrame(position);
        if (nextFrame.AnchorAroundMeters == _frame.AnchorAroundMeters && nextFrame.AnchorAcrossMeters == _frame.AnchorAcrossMeters)
        {
            _lastWorldFrame = BuildWorldFrame(_frame);
            _surface.SetWorldFrame(_lastWorldFrame);
            _grid!.SurfaceTerrainVisualReady = _surface.IsReady;
            return;
        }
        _frame = nextFrame;
        _lastWorldFrame = BuildWorldFrame(_frame);
        _surface.RebuildWindow(_frame);
        _surface.SetWorldFrame(_lastWorldFrame);
        _surface.SetBiomeSampler(_sampler);
        _surface.SetHandoffDistance(HandoffDistance);
        _surface.SetMeshLods(_meshLods);
        _grid!.SurfaceTerrainVisualReady = _surface.IsReady;
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
        if (_explorer != null)
        {
            RingCanonicalPosition position = _explorer.CanonicalPosition;
            float playerHeight = SampleHeight(position.AroundMeters, position.AcrossMeters);
            if (float.IsFinite(playerHeight)) _explorer.LiftSurfaceTerrainCapsule(position.AroundMeters, position.AcrossMeters, playerHeight);
        }
        _surface?.RefreshEditedSurface();
        if (_grid != null) _grid.SurfaceTerrainVisualReady = IsReady;
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
        _brushRadius = GameplaySurfaceTerrainDefaults.ClampBrushRadiusMeters(settings.BrushRadiusMeters);
        _brushStep = GameplaySurfaceTerrainDefaults.ClampBrushStepMeters(settings.BrushStepMeters);
        _meshLods = GameplaySurfaceTerrainDefaults.ClampMeshLods(settings.MeshLods);
        _actualWalkSpeed = GameplaySurfaceTerrainDefaults.ClampMoveSpeedMetersPerSecond(settings.MoveSpeedMetersPerSecond);
        if (_explorer != null) _explorer.MoveSpeed = _actualWalkSpeed;
        _surface?.SetMeshLods(_meshLods);
    }
    public Dictionary GetSurfaceDiagnostics()
    {
        RingCanonicalPosition position = _explorer?.CanonicalPosition ?? default;
        return new()
        {
            ["ready"] = IsReady,
            ["edits"] = _field?.EditCount ?? 0,
            ["revision"] = (long)(_field?.Revision ?? 0),
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
            ["save_blocked"] = _saveBlocked
        };
    }
    private void OnFloatingOriginChanged(RingLodGeometrySnapshot previous, RingLodGeometrySnapshot current, RingCanonicalPosition anchor)
    {
        if (_surface == null) return;
        _lastWorldFrame = BuildWorldFrame(_frame);
        _surface.SetWorldFrame(_lastWorldFrame);
    }

    private RingSurfaceFrame BuildFrame(RingCanonicalPosition position)
    {
        long around = (long)Math.Round(position.AroundMeters / 2) * 2;
        int across = Mathf.Clamp((int)Math.Round(position.AcrossMeters / 2) * 2,
            0, Mathf.RoundToInt(_grid!.RingAcrossWidthMeters) - 2);
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
        return Path.Combine(ProjectSettings.GlobalizePath(directory), RingSurfaceEditStore.DefaultPath);
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
