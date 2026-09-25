using System;
using System.IO;
using Godot;
using Godot.Collections;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Isolated, surface-only Terrain3D play scene. It deliberately does not open voxel or smooth-terrain journals.</summary>
public partial class RingTerrain3DPilot : Node3D
{
    [Export] public VoxelTerrainSettings? TerrainSettings { get; set; }
    [Export] public double StartAroundMeters { get; set; }
    [Export] public double StartAcrossMeters { get; set; } = CertifiedRingScale.RingWidthMeters * .5;
    [Export] public string SurfaceSavePathOverride { get; set; } = "";
    [Export] public NodePath SurfacePath { get; set; } = new("Surface");
    [Export] public NodePath PlayerPath { get; set; } = new("Player");
    [Export] public NodePath CameraPath { get; set; } = new("Player/Head/Camera3D");
    [Export] public NodePath HeadPath { get; set; } = new("Player/Head");
    [Export] public NodePath StatusPath { get; set; } = new("Hud/Status");

    private RingSurfaceHeightField? _field;
    private RingTerrain3DSurface? _surface;
    private CharacterBody3D? _player;
    private Camera3D? _camera;
    private Node3D? _head;
    private Label? _status;
    private RingTerrainSampler? _sampler;
    private RingSurfaceFrame _frame;
    private string _savePath = "";
    private string _lastError = "";
    private bool _saveBlocked;
    private ulong _savedRevision;
    private bool _fly;
    private float _brushRadius = 8, _brushStep = 2, _moveSpeed = 12;
    private int _meshLods = 4;
    private double _autosave = 20;
    private float _pitch;

    public override void _Ready()
    {
        GetTree().AutoAcceptQuit = false;
        _surface = GetNodeOrNull<RingTerrain3DSurface>(SurfacePath);
        _player = GetNodeOrNull<CharacterBody3D>(PlayerPath);
        _camera = GetNodeOrNull<Camera3D>(CameraPath);
        _head = GetNodeOrNull<Node3D>(HeadPath);
        _status = GetNodeOrNull<Label>(StatusPath);
        try
        {
            if (RingLiveTuningDrawer.TryConsumeSurfaceTerrainPrototypeLaunch(out VoxelTerrainSettings? handoffSettings, out double handoffAround, out double handoffAcross))
            {
                TerrainSettings = handoffSettings;
                StartAroundMeters = handoffAround;
                StartAcrossMeters = handoffAcross;
            }
            bool usedFallback = TerrainSettings == null;
            VoxelTerrainSettings settings = (TerrainSettings?.Duplicate(true) as VoxelTerrainSettings) ?? GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true) as VoxelTerrainSettings ?? throw new InvalidOperationException("Certified terrain settings are unavailable.");
            if (usedFallback)
            {
                settings.BaseSurfaceHeightVoxels = 16; settings.HeightAmplitudeVoxels = 180; settings.DetailAmplitudeVoxels = 30;
                settings.BedrockY = -2000; settings.SeaLevelY = 0; settings.RarePeakY = 5200;
            }
            settings.EnableUnified3dDensityTerrain = false;
            settings.EnableDesignedCaveDensityComposition = false;
            settings.EnableProceduralCaves = false;
            _sampler = new RingTerrainSampler(settings, true, CertifiedRingScale.AroundVoxelLength, CertifiedRingScale.EffectiveRadiusMeters, CertifiedRingScale.RingWidthMeters, CertifiedRingScale.TerrainCeilingMeters);
            _field = new RingSurfaceHeightField(_sampler.TerrainDefinitionHash, CertifiedRingScale.AroundVoxelLength, CertifiedRingScale.RingWidthMeters, CertifiedRingScale.TerrainFloorMeters, CertifiedRingScale.TerrainCeilingMeters, (around, across) => _sampler.GetSmoothSurfaceHeight(around, across) + .5f);
            long around = Lattice(StartAroundMeters); int across = Mathf.Clamp((int)Lattice(StartAcrossMeters), 0, CertifiedRingScale.RingWidthMeters - 2);
            _frame = NewFrame(around, across);
            _savePath = ResolveSavePath();
            LoadEdits();
            _surface?.Initialize(_field, _frame);
            _surface?.SetMeshLods(_meshLods);
            PlacePlayer(around, across, 2.0f);
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
        catch (Exception error) { SetError(error.Message); }
        UpdateStatus();
    }

    public override void _ExitTree() { SaveSurfaceEdits(); GetTree().AutoAcceptQuit = true; _sampler?.Dispose(); }

    public override void _Notification(int what)
    {
        if (what != 1006) return; // NOTIFICATION_WM_CLOSE_REQUEST
        if (SaveSurfaceEdits()) GetTree().Quit();
        else SetError("Close cancelled: surface edits could not be saved.");
    }

    public override void _Process(double delta)
    {
        if (_field == null || _surface == null || _player == null || !_surface.IsReady) return;
        _surface.UpdateCamera(_camera?.GlobalPosition ?? _player.GlobalPosition);
        _surface.RefreshEditedSurface();
        RecenterIfNeeded();
        _autosave -= delta;
        if (_autosave <= 0) { SaveSurfaceEdits(); _autosave = 20; }
        UpdateStatus();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_player == null || _camera == null || _surface?.IsReady != true) return;
        bool canControl = Input.MouseMode == Input.MouseModeEnum.Captured && !DrawerOwnsMouse();
        float dt = (float)delta;
        if (_fly)
        {
            if (!canControl) { _player.Velocity = Vector3.Zero; return; }
            Vector2 flyInput = MovementInput();
            Vector3 move = flyInput.X * _camera.GlobalTransform.Basis.X + flyInput.Y * -_camera.GlobalTransform.Basis.Z;
            if (Input.IsPhysicalKeyPressed(Key.Q)) move.Y -= 1; if (Input.IsPhysicalKeyPressed(Key.E)) move.Y += 1;
            _player.GlobalPosition += move.Normalized() * _moveSpeed * dt; _player.Velocity = Vector3.Zero; return;
        }
        Vector2 input = canControl ? MovementInput() : Vector2.Zero;
        Vector3 forward = -_player.GlobalTransform.Basis.Z; forward.Y = 0; forward = forward.Normalized();
        Vector3 right = _player.GlobalTransform.Basis.X; right.Y = 0; right = right.Normalized();
        float speed = _moveSpeed * (Input.IsPhysicalKeyPressed(Key.Shift) ? 1.8f : 1f);
        Vector3 horizontal = (right * input.X + forward * input.Y) * speed;
        Vector3 velocity = _player.Velocity; velocity.X = horizontal.X; velocity.Z = horizontal.Z;
        if (_player.IsOnFloor() && canControl && Input.IsPhysicalKeyPressed(Key.Space)) velocity.Y = 8.5f; else velocity.Y -= 24 * dt;
        _player.Velocity = velocity; _player.MoveAndSlide();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (DrawerOwnsMouse()) return;
        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _player?.RotateY(Mathf.DegToRad(-motion.Relative.X * .12f));
            if (_head != null)
            {
                _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * .12f, -85, 85);
                _head.RotationDegrees = new Vector3(_pitch, 0, 0);
            }
            return;
        }
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode == Key.F) _fly = !_fly;
            if (key.Keycode == Key.Escape) Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        if (@event is InputEventMouseButton mouse && mouse.Pressed)
        {
            if (Input.MouseMode != Input.MouseModeEnum.Captured) { Input.MouseMode = Input.MouseModeEnum.Captured; return; }
            if (mouse.ButtonIndex is MouseButton.Left or MouseButton.Right) EditAtCrosshair(mouse.ButtonIndex == MouseButton.Right ? _brushStep : -_brushStep);
        }
    }

    public void ApplySurfaceSettings(GameplaySurfaceTerrainSettings settings)
    {
        if (settings == null) return;
        _brushRadius = GameplaySurfaceTerrainDefaults.ClampBrushRadiusMeters(settings.BrushRadiusMeters);
        _brushStep = GameplaySurfaceTerrainDefaults.ClampBrushStepMeters(settings.BrushStepMeters);
        _moveSpeed = GameplaySurfaceTerrainDefaults.ClampMoveSpeedMetersPerSecond(settings.MoveSpeedMetersPerSecond);
        _meshLods = GameplaySurfaceTerrainDefaults.ClampMeshLods(settings.MeshLods);
        _surface?.SetMeshLods(_meshLods);
    }

    public bool ApplySurfaceBrushAtCanonical(double aroundMeters, double acrossMeters, float delta)
    {
        if (_field == null || _saveBlocked || !_field.ApplyBrush(aroundMeters, acrossMeters, _brushRadius, delta)) return false;
        // Raising the terrain into the capsule would leave the player embedded until physics resolves it.
        // Lowering deliberately does not reposition the player, so ordinary gravity takes over.
        if (_player != null)
        {
            RingSurfaceCoordinates player = _frame.Inverse(_player.GlobalPosition);
            float raisedSurface = _field.SampleHeight(player.AroundMeters, player.AcrossMeters);
            const float capsuleFootClearance = 1.0f;
            if (raisedSurface + capsuleFootClearance > player.Height)
                _player.GlobalPosition = _frame.MapPosition(player.AroundMeters, player.AcrossMeters, raisedSurface + capsuleFootClearance);
        }
        _surface?.RefreshEditedSurface(); return true;
    }

    public void TeleportCanonicalSurface(double aroundMeters, double acrossMeters)
    {
        if (_field == null) return;
        PlacePlayer(Lattice(aroundMeters), (int)Lattice(acrossMeters), 2); RecenterIfNeeded(force: true);
    }

    public void RecenterSurfaceNow() => RecenterIfNeeded(force: true);

    public float GetNativeHeightAtOrigin()
    {
        if (_surface?.TerrainNativeData == null) return float.NaN;
        Variant value = _surface.TerrainNativeData.Call("get_height", Vector3.Zero);
        return value.VariantType is Variant.Type.Float or Variant.Type.Int ? value.AsSingle() : float.NaN;
    }

    public Dictionary GetSurfaceDiagnostics()
    {
        RingSurfaceCoordinates player = _player == null || _field == null || _frame.RadiusMeters <= 0 ? default : _frame.Inverse(_player.GlobalPosition);
        return new() { ["addon_ready"] = _surface?.IsReady ?? false, ["regions"] = _surface?.RegionCount ?? 0, ["field_revision"] = (long)(_field?.Revision ?? 0), ["edits"] = _field?.EditCount ?? 0, ["collision"] = _surface?.IsReady ?? false, ["frame_around"] = _frame.AnchorAroundMeters, ["frame_across"] = _frame.AnchorAcrossMeters, ["player_around"] = player.AroundMeters, ["player_across"] = player.AcrossMeters, ["player_height"] = player.Height, ["save_path"] = _savePath, ["save_blocked"] = _saveBlocked, ["error"] = _lastError.Length > 0 ? _lastError : _surface?.LastError ?? "" };
    }

    public bool SaveSurfaceEdits()
    {
        if (_field == null) return true;
        if (_saveBlocked) return _field.Revision == _savedRevision;
        RingSurfaceEditStoreResult result = _field.Save(_savePath);
        if (!result.Success) { SetError("Surface save failed: " + result.Error); return false; }
        _savedRevision = _field.Revision;
        if (_lastError.StartsWith("Surface save failed:", StringComparison.Ordinal)) _lastError = "";
        return true;
    }

    private void EditAtCrosshair(float delta)
    {
        if (_camera == null || _field == null) return;
        Vector2 center = GetViewport().GetVisibleRect().Size * .5f;
        Vector3 from = _camera.ProjectRayOrigin(center), to = from + _camera.ProjectRayNormal(center) * 600;
        var query = PhysicsRayQueryParameters3D.Create(from, to, 1);
        if (_player != null) query.Exclude = new Godot.Collections.Array<Rid> { _player.GetRid() };
        Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0 || !hit.ContainsKey("position")) return;
        RingSurfaceCoordinates canonical = _frame.Inverse((Vector3)hit["position"]);
        if (canonical.AcrossMeters < 0 || canonical.AcrossMeters >= CertifiedRingScale.RingWidthMeters) return;
        ApplySurfaceBrushAtCanonical(canonical.AroundMeters, canonical.AcrossMeters, delta);
    }

    private void RecenterIfNeeded(bool force = false)
    {
        if (_player == null || _field == null || _surface == null) return;
        RingSurfaceCoordinates current = _frame.Inverse(_player.GlobalPosition);
        if (!force && Math.Abs(current.AroundOffsetMeters) < 128 && Math.Abs(current.AcrossMeters - _frame.AnchorAcrossMeters) < 128) return;
        long anchorAround = Lattice(current.AroundMeters);
        int anchorAcross = Mathf.Clamp((int)Lattice(current.AcrossMeters), 0, CertifiedRingScale.RingWidthMeters - 2);
        float anchorHeight = _field.SampleHeight(anchorAround, anchorAcross);
        RingSurfaceFrame oldFrame = _frame;
        float rotation = (float)(-oldFrame.SignedAroundDeltaMeters(anchorAround) / CertifiedRingScale.EffectiveRadiusMeters);
        Basis rebaseRotation = new(Vector3.Back, rotation);
        Vector3 velocity = rebaseRotation * _player.Velocity;
        Basis orientation = rebaseRotation * _player.GlobalTransform.Basis;
        _frame = new RingSurfaceFrame(anchorAround, anchorAcross, anchorHeight, CertifiedRingScale.AroundVoxelLength, CertifiedRingScale.EffectiveRadiusMeters);
        _surface.RebuildWindow(_frame);
        _surface.SetMeshLods(_meshLods);
        _player.GlobalTransform = new Transform3D(orientation, _frame.MapPosition(current.AroundMeters, current.AcrossMeters, current.Height));
        _player.Velocity = velocity;
    }

    private RingSurfaceFrame NewFrame(long around, int across) => new(around, across, _field!.SampleHeight(around, across), CertifiedRingScale.AroundVoxelLength, CertifiedRingScale.EffectiveRadiusMeters);
    private void PlacePlayer(long around, int across, float clearance) { if (_player == null || _field == null) return; _player.GlobalPosition = _frame.MapSample(around, across, _field.SampleHeight(around, across) + clearance); }
    private long Lattice(double value) => checked((long)(Math.Round(value / 2.0) * 2));
    private string ResolveSavePath()
    {
        if (!string.IsNullOrWhiteSpace(SurfaceSavePathOverride)) return ProjectSettings.GlobalizePath(SurfaceSavePathOverride);
        string world = ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString();
        if (!string.IsNullOrWhiteSpace(world)) return Path.Combine(ProjectSettings.GlobalizePath(world), RingSurfaceEditStore.DefaultPath);
        return Path.Combine(ProjectSettings.GlobalizePath($"user://terrain3d_surface_pilot/{_field!.TerrainDefinitionHash:X16}"), RingSurfaceEditStore.DefaultPath);
    }
    private void LoadEdits()
    {
        if (!File.Exists(_savePath) || _field == null) return;
        RingSurfaceEditStoreResult result = _field.Load(_savePath);
        if (!result.Success) { _saveBlocked = true; SetError("Surface save was rejected and will not be overwritten: " + result.Error); }
        _savedRevision = _field.Revision;
    }
    private void SetError(string error) { _lastError = error; GD.PushError("Terrain3D surface pilot: " + error); }
    private void UpdateStatus() { if (_status != null) _status.Text = $"CERTIFIED TERRAIN3D SURFACE  |  ` tune  |  WASD walk  Shift sprint  Space jump  F fly  Q/E rise/lower\nLeft lower, Right raise  |  radius {_brushRadius:0.##}m step {_brushStep:0.##}m  |  edits {_field?.EditCount ?? 0}  |  {(_lastError.Length > 0 ? _lastError : _surface?.IsReady == true ? "Terrain3D ready" : "Terrain3D unavailable")}"; }
    private static Vector2 MovementInput() => new((Input.IsPhysicalKeyPressed(Key.D) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.A) ? 1 : 0), (Input.IsPhysicalKeyPressed(Key.W) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.S) ? 1 : 0));
    private bool DrawerOwnsMouse() => GetTree().Root.GetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, false).AsBool();
}
