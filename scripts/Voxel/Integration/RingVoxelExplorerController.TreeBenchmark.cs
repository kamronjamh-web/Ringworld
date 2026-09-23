using System;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Integration;

public partial class RingVoxelExplorerController
{
    /// <summary>Opt-in profiling motion which keeps normal floating-origin thresholds.</summary>
    public void AdvanceTreeCpuBenchmarkCanonicalPosition(in RingCanonicalPosition position, float simulatedSpeedMetersPerSecond)
    {
        if (_grid == null || !double.IsFinite(position.AroundMeters)
            || !double.IsFinite(position.VerticalMeters) || !double.IsFinite(position.AcrossMeters)) return;
        _benchmarkOverrideActive = true;
        _canonicalPosition = position.Wrap(_grid.GetCanonicalRingCircumferenceMeters64());
        _canonicalVelocity = float.IsFinite(simulatedSpeedMetersPerSecond)
            ? Vector3.Back * Mathf.Max(0.0f, simulatedSpeedMetersPerSecond) : Vector3.Zero;
        _noclip = false;
        CurrentGravityFactor = ResolveGravityFactor();
        RecenterCollisionOrigin(force: false);
        ApplyWorldTransform();
    }
}
