using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Phase2;

public partial class VoxelGridEditController : Node3D
{
    [Export] public NodePath CameraPath { get; set; } = new NodePath();
    [Export] public NodePath GridManagerPath { get; set; } = new NodePath();
    [Export] public NodePath SmoothTerrainPath { get; set; } = new NodePath();
    [Export] public NodePath ConstructionOverlayPath { get; set; } = new NodePath();
    [Export] public bool UseSmoothTerrainBackend { get; set; }
    [Export] public float MaxEditDistance { get; set; } = 64.0f;
    [Export] public BlockType PlaceBlockType { get; set; } = BlockType.Concrete;
    [Export] public bool EnableAnchoredPlacementDebug { get; set; } = false;
    [Export] public PackedScene? AnchoredObjectScene { get; set; }
    [Export] public string AnchoredArchetypeId { get; set; } = "debug_marker";
    [Export] public MouseButton AnchoredPlacementMouseButton { get; set; } = MouseButton.Middle;

    private Camera3D? _camera;
    private VoxelChunkGridManager? _gridManager;
    private SmoothTerrainGrid? _smoothTerrain;
    private SmoothConstructionOverlay? _constructionOverlay;

    public override void _Ready()
    {
        ResolveReferences();
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mouseButton)
        {
            return;
        }

        // Gameplay edits are accepted only while the camera owns the mouse.
        // Using _Input here prevents decorative HUD Controls from swallowing
        // clicks before the terrain editor can see them.
        if (Input.MouseMode != Input.MouseModeEnum.Captured
            || !mouseButton.Pressed
            || mouseButton.IsEcho())
        {
            return;
        }

        if (mouseButton.ButtonIndex == MouseButton.Left)
        {
            if (TryEdit(place: false)) GetViewport().SetInputAsHandled();
        }
        else if (mouseButton.ButtonIndex == MouseButton.Right)
        {
            if (TryEdit(place: true)) GetViewport().SetInputAsHandled();
        }
        else if (EnableAnchoredPlacementDebug && mouseButton.ButtonIndex == AnchoredPlacementMouseButton)
        {
            if (TryPlaceAnchoredObjectAtCursor()) GetViewport().SetInputAsHandled();
        }
    }

    private bool TryEdit(bool place)
    {
        ResolveReferences();

        if (_camera == null || _gridManager == null)
        {
            return false;
        }

        if (UseSmoothTerrainBackend && _smoothTerrain != null)
        {
            if (!TryGetCursorRay(out Vector3 smoothOrigin, out Vector3 smoothDirection))
                return false;

            // Physics is consulted only for the separate construction overlay.
            // Trees, props, and stale concave terrain bodies must never choose
            // the center of a natural-density brush.
            if (TryGetCursorPhysicsHit(out Vector3 constructionHit, out Vector3 constructionNormal))
            {
                bool changedConstruction = place
                    ? _constructionOverlay?.TryPlaceBlockAtWorldPosition(constructionHit, constructionNormal, SmoothConstructionMaterial.Concrete, out _) == true
                    : _constructionOverlay?.TryRemoveBlockAtWorldPosition(constructionHit, constructionNormal, out _) == true;
                if (changedConstruction) return true;
            }

            if (!_smoothTerrain.TryRaycastEditableSurface(
                    smoothOrigin,
                    smoothDirection,
                    MaxEditDistance,
                    out Vector3 smoothHit,
                    out Vector3 smoothNormal))
                return false;

            return place
                ? _constructionOverlay?.TryPlaceBlockAtWorldPosition(smoothHit, smoothNormal, SmoothConstructionMaterial.Concrete, out _) == true
                : _smoothTerrain.TryApplyNaturalBrushAtWorldPosition(
                    smoothHit - smoothNormal * 0.05f,
                    false,
                    2.0f,
                    SmoothTerrainMaterial.Soil,
                    out _);
        }

        if (!TryGetCursorHit(out Vector3 hitPosition, out Vector3 hitNormal))
        {
            return false;
        }

        if (TryApplyEditAtHit(place, hitPosition, hitNormal))
        {
            return true;
        }

        // A streamed concave shape may contain the preceding frame's mesh.
        // Retry against the authoritative voxel field before rejecting a click.
        if (!TryGetCursorRay(out Vector3 origin, out Vector3 direction)
            || !_gridManager.TryRaycastEditableVoxelSurface(
                origin,
                direction,
                MaxEditDistance,
                out hitPosition,
                out hitNormal))
        {
            return false;
        }

        return TryApplyEditAtHit(place, hitPosition, hitNormal);
    }

    private bool TryApplyEditAtHit(bool place, Vector3 hitPosition, Vector3 hitNormal)
    {
        if (_gridManager == null) return false;
        float faceOffset = Mathf.Max(0.05f, VoxelConstants.VoxelSizeMeters * 0.1f);
        Vector3 insidePoint = hitPosition - hitNormal * faceOffset;
        Vector3 outsidePoint = hitPosition + hitNormal * faceOffset;
        return _gridManager.TrySetBlockByWorldPosition(
            place ? outsidePoint : insidePoint,
            place ? PlaceBlockType : BlockType.Air,
            out _,
            out _,
            requireTargetAir: place);
    }

    private bool TryPlaceAnchoredObjectAtCursor()
    {
        ResolveReferences();

        if (_camera == null || _gridManager == null || AnchoredObjectScene == null)
        {
            return false;
        }

        if (!TryGetCursorHit(out Vector3 hitPosition, out Vector3 hitNormal))
        {
            return false;
        }

        Vector3 supportPoint = hitPosition - hitNormal * 0.01f;
        return _gridManager.TryPlaceAnchoredObjectByWorldPosition(
            supportPoint,
            AnchoredObjectScene,
            AnchoredArchetypeId,
            out _);
    }

    private bool TryGetCursorHit(out Vector3 hitPosition, out Vector3 hitNormal)
    {
        hitPosition = Vector3.Zero;
        hitNormal = Vector3.Up;

        if (_camera == null)
        {
            return false;
        }

        if (!TryGetCursorRay(out Vector3 origin, out Vector3 direction))
        {
            return false;
        }
        if (TryGetCursorPhysicsHit(out hitPosition, out hitNormal))
        {
            return true;
        }

        // Streamed visual chunks can briefly exist before their concave physics
        // shape is promoted. Query the deterministic voxel field as a fallback
        // so placement and breaking never depend on that collision timing.
        if (UseSmoothTerrainBackend && _smoothTerrain != null)
            return _smoothTerrain.TryRaycastEditableSurface(origin, direction, MaxEditDistance, out hitPosition, out hitNormal);
        return _gridManager != null
            && _gridManager.TryRaycastEditableVoxelSurface(
                origin,
                direction,
                MaxEditDistance,
                out hitPosition,
                out hitNormal);
    }

    private bool TryGetCursorPhysicsHit(out Vector3 hitPosition, out Vector3 hitNormal)
    {
        hitPosition = Vector3.Zero;
        hitNormal = Vector3.Up;
        if (!TryGetCursorRay(out Vector3 origin, out Vector3 direction)) return false;
        Vector3 target = origin + direction * MaxEditDistance;
        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, target);
        query.CollideWithAreas = false;
        Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        return hit.Count > 0 && TryResolveHit(hit, out hitPosition, out hitNormal);
    }

    private bool TryGetCursorRay(out Vector3 origin, out Vector3 direction)
    {
        origin = Vector3.Zero;
        direction = Vector3.Forward;
        if (_camera == null) return false;

        Vector2 mousePos = GetViewport().GetMousePosition();
        origin = _camera.ProjectRayOrigin(mousePos);
        direction = _camera.ProjectRayNormal(mousePos).Normalized();
        return !direction.IsZeroApprox();
    }

    private static bool TryResolveHit(
        Godot.Collections.Dictionary hit,
        out Vector3 hitPosition,
        out Vector3 hitNormal)
    {
        hitPosition = Vector3.Zero;
        hitNormal = Vector3.Up;

        if (!hit.ContainsKey("position") || !hit.ContainsKey("normal") || !hit.ContainsKey("collider"))
        {
            return false;
        }

        hitPosition = (Vector3)hit["position"];
        hitNormal = ((Vector3)hit["normal"]).Normalized();

        GodotObject? hitColliderObject = hit["collider"].AsGodotObject();
        if (hitColliderObject is not Node)
        {
            return false;
        }

        return true;
    }

    private void ResolveReferences()
    {
        if (_camera == null && !CameraPath.IsEmpty)
        {
            _camera = GetNodeOrNull<Camera3D>(CameraPath);
        }

        if (_gridManager == null && !GridManagerPath.IsEmpty)
        {
            _gridManager = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        }
        if (_smoothTerrain == null && !SmoothTerrainPath.IsEmpty)
            _smoothTerrain = GetNodeOrNull<SmoothTerrainGrid>(SmoothTerrainPath);
        if (_constructionOverlay == null && !ConstructionOverlayPath.IsEmpty)
            _constructionOverlay = GetNodeOrNull<SmoothConstructionOverlay>(ConstructionOverlayPath);
    }
}
