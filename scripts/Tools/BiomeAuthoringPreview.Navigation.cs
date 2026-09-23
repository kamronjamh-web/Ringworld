using Godot;
using RingworldTechDemo.Biomes;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Tools;

/// <summary>Camera navigation for the isolated editor view and F6 preview scene.</summary>
public partial class BiomeAuthoringPreview
{
    private const float OverviewDistance = 7200f;
    private const float DetailX = 8f, DetailZ = 20f;
    private bool _editorRightDrag;
    private readonly System.Collections.Generic.HashSet<Key> _editorKeys = new();

    public Transform3D GetOverviewCameraTransform()
    {
        float height = Profile().MeanElevationMeters;
        return new Transform3D(Basis.Identity, new Vector3(0, height + OverviewDistance, OverviewDistance))
            .LookingAt(new Vector3(0, height, 0), Vector3.Up);
    }

    public Transform3D GetDetailCameraTransform()
    {
        BiomeAuthoringSnapshot profile = Profile();
        float cameraHeight = SurfaceHeight(profile, DetailX, DetailZ) + 2.5f;
        float targetHeight = SurfaceHeight(profile, 0, 0) + 2f;
        return new Transform3D(Basis.Identity, new Vector3(DetailX, cameraHeight, DetailZ))
            .LookingAt(new Vector3(0, targetHeight, 0), Vector3.Up);
    }

    public void FrameOverview()
    {
        if (_camera != null) _camera.GlobalTransform = GetOverviewCameraTransform();
    }

    public void FrameDetail()
    {
        if (_camera != null) _camera.GlobalTransform = GetDetailCameraTransform();
    }

    private BiomeAuthoringSnapshot Profile() => _publishedProfile ?? Preset?.CreateSnapshot()
        ?? throw new System.InvalidOperationException("Biome preview has no preset profile.");

    private static float SurfaceHeight(BiomeAuthoringSnapshot profile, float x, float z)
    {
        using var sampler = new BiomeAuthoringSampler(profile);
        return sampler.SampleSurfaceHeight(x, z);
    }

    private bool NavigationBlocked()
    {
        if (GetTree().Root.GetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, false).AsBool()) return true;
        Control? focus = GetViewport().GuiGetFocusOwner();
        return focus is LineEdit or TextEdit;
    }

    private void RuntimeInput(double delta)
    {
        if (_camera == null || NavigationBlocked()) return;
        float horizontal = (Input.IsKeyPressed(Key.D) ? 1 : 0) - (Input.IsKeyPressed(Key.A) ? 1 : 0);
        float vertical = (Input.IsKeyPressed(Key.S) ? 1 : 0) - (Input.IsKeyPressed(Key.W) ? 1 : 0);
        float up = (Input.IsKeyPressed(Key.E) ? 1 : 0) - (Input.IsKeyPressed(Key.Q) ? 1 : 0);
        Vector3 local = new(horizontal, up, vertical);
        if (local.LengthSquared() <= 0) return;
        float speed = Input.IsKeyPressed(Key.Shift) ? 180f : 45f;
        _camera.GlobalPosition += _camera.GlobalTransform.Basis * local.Normalized() * speed * (float)delta;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Engine.IsEditorHint() || _camera == null || NavigationBlocked()) return;
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right } mouse)
        {
            Input.MouseMode = mouse.Pressed ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode == Key.O) FrameOverview();
            else if (key.Keycode == Key.F) FrameDetail();
            else return;
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _camera.RotateY(-motion.Relative.X * .0025f);
            _camera.RotateObjectLocal(Vector3.Right, -motion.Relative.Y * .0025f);
            GetViewport().SetInputAsHandled();
        }
    }

    public void ReleasePreviewMouse()
    {
        _editorRightDrag = false;
        _editorKeys.Clear();
        if (Input.MouseMode == Input.MouseModeEnum.Captured) Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    /// <summary>Receives input from the plugin-owned editor SubViewport only.</summary>
    public void EditorPreviewGuiInput(InputEvent @event)
    {
        if (!Engine.IsEditorHint() || _camera == null) return;
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right } mouse) { _editorRightDrag = mouse.Pressed; return; }
        if (@event is InputEventMouseMotion motion && _editorRightDrag)
        {
            _camera.RotateY(-motion.Relative.X * .0025f);
            _camera.RotateObjectLocal(Vector3.Right, -motion.Relative.Y * .0025f);
            return;
        }
        if (@event is InputEventKey { Echo: false } key)
        {
            if (key.Pressed) _editorKeys.Add(key.Keycode); else _editorKeys.Remove(key.Keycode);
            if (key.Pressed && key.Keycode == Key.O) FrameOverview();
            if (key.Pressed && key.Keycode == Key.F) FrameDetail();
        }
    }

    public void EditorPreviewTick(double delta)
    {
        if (!Engine.IsEditorHint() || _camera == null) return;
        float horizontal = (_editorKeys.Contains(Key.D) ? 1 : 0) - (_editorKeys.Contains(Key.A) ? 1 : 0);
        float vertical = (_editorKeys.Contains(Key.S) ? 1 : 0) - (_editorKeys.Contains(Key.W) ? 1 : 0);
        float up = (_editorKeys.Contains(Key.E) ? 1 : 0) - (_editorKeys.Contains(Key.Q) ? 1 : 0);
        Vector3 local = new(horizontal, up, vertical);
        if (local.LengthSquared() <= 0) return;
        float speed = _editorKeys.Contains(Key.Shift) ? 180f : 45f;
        _camera.GlobalPosition += _camera.GlobalTransform.Basis * local.Normalized() * speed * (float)delta;
    }

    public Godot.Collections.Dictionary GetEditorNavigationDiagnostics() => new()
    {
        ["right_drag"] = _editorRightDrag,
        ["key_count"] = _editorKeys.Count,
        ["camera_ready"] = _camera != null,
        ["editor_hint"] = Engine.IsEditorHint()
    };
}
