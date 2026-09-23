using Godot;

namespace RingworldTechDemo.Voxel.Core;

/// <summary>
/// Canonical terrain coordinates. Only the around-ring axis exceeds 32-bit
/// range for current Mega Ring targets; local chunk voxels remain Vector3I.
/// </summary>
public readonly record struct RingTerrainColumnCoordinate(
    long AroundVoxel,
    int AcrossVoxel);

public readonly record struct RingTerrainSampleCoordinate(
    long AroundVoxel,
    int VerticalVoxel,
    int AcrossVoxel)
{
    public long X => AroundVoxel;
    public int Y => VerticalVoxel;
    public int Z => AcrossVoxel;

    public static implicit operator RingTerrainSampleCoordinate(Vector3I value) =>
        new(value.X, value.Y, value.Z);
}