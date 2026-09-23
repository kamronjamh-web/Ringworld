using System;

namespace RingworldTechDemo.Voxel.Phase2;

[Flags]
public enum VoxelFluidFlags : byte
{
    None = 0,
    Source = 1,
    Falling = 2,
    Waterlogged = 4
}

/// <summary>
/// Sparse Minecraft-style fluid data. Level eight is a full source cell;
/// lower levels are flowing water. It deliberately lives outside BlockType so
/// an ocean never requires a voxel allocation for every metre of depth.
/// </summary>
public readonly record struct VoxelFluidState(byte Level, VoxelFluidFlags Flags)
{
    public bool IsSource => (Flags & VoxelFluidFlags.Source) != 0;
    public bool IsFalling => (Flags & VoxelFluidFlags.Falling) != 0;
    public bool IsWaterlogged => (Flags & VoxelFluidFlags.Waterlogged) != 0;
    public bool IsEmpty => Level == 0;

    public static VoxelFluidState Flowing(int level, bool falling = false, bool waterlogged = false) =>
        new((byte)Math.Clamp(level, 0, 8),
            (falling ? VoxelFluidFlags.Falling : VoxelFluidFlags.None)
            | (waterlogged ? VoxelFluidFlags.Waterlogged : VoxelFluidFlags.None));

    public static VoxelFluidState Source(bool waterlogged = false) =>
        new(8, VoxelFluidFlags.Source | (waterlogged ? VoxelFluidFlags.Waterlogged : VoxelFluidFlags.None));
}
