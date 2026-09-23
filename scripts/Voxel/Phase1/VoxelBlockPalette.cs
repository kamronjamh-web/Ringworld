using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Phase1;

/// <summary>
/// Immutable, worker-safe copy of the voxel color resource.
/// </summary>
public readonly record struct VoxelBlockPalette(
    Color Soil,
    Color Rock,
    Color Bedrock,
    Color Concrete,
    Color Glass)
{
    public static VoxelBlockPalette Capture(VoxelBlockColorMap? source) => source == null
        ? new VoxelBlockPalette(
            new Color(0.46f, 0.38f, 0.27f),
            new Color(0.45f, 0.45f, 0.46f),
            new Color(0.18f, 0.18f, 0.20f),
            new Color(0.67f, 0.67f, 0.69f),
            new Color(0.60f, 0.80f, 0.90f, 0.70f))
        : new VoxelBlockPalette(
            source.SoilColor,
            source.RockColor,
            source.BedrockColor,
            source.ConcreteColor,
            source.GlassColor);

    public Color GetColor(BlockType blockType) => blockType switch
    {
        BlockType.Soil => Soil,
        BlockType.Rock => Rock,
        BlockType.Bedrock => Bedrock,
        BlockType.Concrete => Concrete,
        BlockType.Glass => Glass,
        _ => Colors.White
    };
}
