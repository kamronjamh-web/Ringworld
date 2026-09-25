using System;
using Godot;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Integration;

public partial class RingVoxelExplorerController
{
    private RingTerrain3DGameplay? _terrain3DSurfaceGameplay;

    private bool TryGetSurfaceTerrainHeight(double aroundMeters, double acrossMeters, out float height)
    {
        height = float.NaN;
        if (!UseTerrain3DSurfacePhysics || _grid == null || acrossMeters < 0 || acrossMeters > _grid.RingAcrossWidthMeters - 2)
            return false;
        _terrain3DSurfaceGameplay ??= GetNodeOrNull<RingTerrain3DGameplay>(Terrain3DGameplayPath);
        if (_terrain3DSurfaceGameplay == null || !_terrain3DSurfaceGameplay.IsNodeReady()) return false;
        if (IsInsideTree() && !_terrain3DSurfaceGameplay.EnsureInitialized()) return false;
        height = _terrain3DSurfaceGameplay.SampleHeight(aroundMeters, acrossMeters);
        return float.IsFinite(height);
    }

    private bool MoveWithTerrain3DSurfacePhysics(bool swimming)
    {
        _terrain3DSurfaceGameplay ??= GetNodeOrNull<RingTerrain3DGameplay>(Terrain3DGameplayPath);
        if (_terrain3DSurfaceGameplay?.IsReady != true || _grid == null) return false;
        _terrain3DSurfaceGameplay.EnsureCoverage(_canonicalPosition);
        double delta = Math.Max(.0001, GetPhysicsProcessDeltaTime());
        _terrain3DSurfaceGameplay.EnsureCoverage(new RingCanonicalPosition(_canonicalPosition.AroundMeters + _canonicalVelocity.X * delta, _canonicalPosition.VerticalMeters + _canonicalVelocity.Y * delta, _canonicalPosition.AcrossMeters + _canonicalVelocity.Z * delta));
        if (!_terrain3DSurfaceGameplay.IsReady) return false;
        RecenterCollisionOrigin(force: false); ApplyWorldTransform();
        double angle = _canonicalPosition.AroundMeters / _grid.GetCanonicalRingCircumferenceMeters64() * Math.Tau;
        Vector3 outward = new((float)Math.Cos(angle), 0, (float)Math.Sin(angle));
        Vector3 up = -outward, tangent = new(-outward.Z, 0, outward.X), across = Vector3.Up;
        UpDirection = up; FloorSnapLength = swimming ? 0 : Mathf.Max(.1f, StepHeight + SafeMargin);
        Velocity = tangent * _canonicalVelocity.X + up * _canonicalVelocity.Y + across * _canonicalVelocity.Z;
        MoveAndSlide();
        if (!swimming && _grounded && !IsOnFloor() && _canonicalVelocity.Y <= 0) ApplyFloorSnap();
        Vector3 actual = Velocity;
        _canonicalVelocity = new Vector3(actual.Dot(tangent), actual.Dot(up), actual.Dot(across));
        _canonicalPosition = _grid.WorldToCanonicalRingPosition64(GlobalPosition).Wrap(_grid.GetCanonicalRingCircumferenceMeters64());
        _grounded = !swimming && IsOnFloor();
        if (_grounded) { _lastSmoothGroundedPosition = _canonicalPosition; _hasLastSmoothGroundedPosition = true; }
        _terrain3DSurfaceGameplay.EnsureCoverage(_canonicalPosition);
        if (!_terrain3DSurfaceGameplay.IsReady) return false;
        RecenterCollisionOrigin(force: false); ApplyWorldTransform();
        return true;
    }

    public void LiftSurfaceTerrainCapsule(double aroundMeters, double acrossMeters, float surfaceHeight)
    {
        if (_grid == null) return;
        double horizontal = new Vector2((float)(_canonicalPosition.AroundMeters - aroundMeters), (float)(_canonicalPosition.AcrossMeters - acrossMeters)).Length();
        if (horizontal > BodyWidth || _canonicalPosition.VerticalMeters >= surfaceHeight + .03f) return;
        _canonicalPosition = _canonicalPosition with { VerticalMeters = surfaceHeight + .03f };
        _canonicalVelocity.Y = Math.Max(0, _canonicalVelocity.Y);
        GlobalPosition = _grid.CanonicalRingPositionToWorld(_canonicalPosition);
    }
}
