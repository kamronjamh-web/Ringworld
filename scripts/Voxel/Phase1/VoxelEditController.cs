using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Phase1;

public partial class VoxelEditController : Node3D
{
    [Export] public NodePath CameraPath { get; set; } = new NodePath();
    [Export] public NodePath ChunkPath { get; set; } = new NodePath();
    [Export] public float MaxEditDistance { get; set; } = 12.0f;
    [Export] public BlockType PlaceBlockType { get; set; } = BlockType.Concrete;

    private Camera3D? _camera;
    private VoxelChunkNode? _chunk;

    public override void _Ready()
    {
        ResolveReferences();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mouseButton)
        {
            return;
        }

        if (!mouseButton.Pressed || mouseButton.IsEcho())
        {
            return;
        }

        if (mouseButton.ButtonIndex == MouseButton.Left)
        {
            TryEdit(place: false);
        }
        else if (mouseButton.ButtonIndex == MouseButton.Right)
        {
            TryEdit(place: true);
        }
    }

    private bool TryEdit(bool place)
    {
        ResolveReferences();

        if (_camera == null || _chunk == null)
        {
            return false;
        }

        Vector2 mousePos = GetViewport().GetMousePosition();
        Vector3 origin = _camera.ProjectRayOrigin(mousePos);
        Vector3 direction = _camera.ProjectRayNormal(mousePos).Normalized();
        Vector3 target = origin + direction * MaxEditDistance;

        var query = PhysicsRayQueryParameters3D.Create(origin, target);
        query.CollideWithAreas = false;

        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0)
        {
            return false;
        }

        if (!TryResolveHitChunk(hit, out Vector3 hitPosition, out Vector3 hitNormal))
        {
            return false;
        }

        Vector3I hitVoxel = _chunk!.WorldToLocalVoxel(hitPosition - hitNormal * 0.01f);
        Vector3I adjacentVoxel = _chunk.WorldToLocalVoxel(hitPosition + hitNormal * 0.01f);
        Vector3I targetVoxel = place ? adjacentVoxel : hitVoxel;

        if (!_chunk.Data.IsInBounds(targetVoxel.X, targetVoxel.Y, targetVoxel.Z))
        {
            return false;
        }

        if (!place)
        {
            return _chunk.TrySetBlock(targetVoxel, BlockType.Air, rebuild: true);
        }

        if (_chunk.Data.GetBlock(targetVoxel.X, targetVoxel.Y, targetVoxel.Z) != BlockType.Air)
        {
            return false;
        }

        return _chunk.TrySetBlock(targetVoxel, PlaceBlockType, rebuild: true);
    }

    private bool TryResolveHitChunk(Godot.Collections.Dictionary hit, out Vector3 hitPosition, out Vector3 hitNormal)
    {
        hitPosition = Vector3.Zero;
        hitNormal = Vector3.Up;

        if (!hit.ContainsKey("position") || !hit.ContainsKey("normal") || !hit.ContainsKey("collider"))
        {
            return false;
        }

        hitPosition = (Vector3)hit["position"];
        hitNormal = ((Vector3)hit["normal"]).Normalized();

        GodotObject? hitCollider = hit["collider"].AsGodotObject();
        if (hitCollider is not Node collider)
        {
            return false;
        }

        if (_chunk == null)
        {
            return false;
        }

        if (collider == _chunk)
        {
            return true;
        }

        if (_chunk.IsAncestorOf(collider))
        {
            return true;
        }

        return false;
    }

    private void ResolveReferences()
    {
        if (_camera == null && !CameraPath.IsEmpty)
        {
            _camera = GetNodeOrNull<Camera3D>(CameraPath);
        }

        if (_chunk == null && !ChunkPath.IsEmpty)
        {
            _chunk = GetNodeOrNull<VoxelChunkNode>(ChunkPath);
        }
    }
}
