using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Phase1;

[GlobalClass]
public partial class VoxelBlockColorMap : Resource
{
    [Export] public Color SoilColor { get; set; } = new Color(0.46f, 0.38f, 0.27f);
    [Export] public Color RockColor { get; set; } = new Color(0.45f, 0.45f, 0.46f);
    [Export] public Color BedrockColor { get; set; } = new Color(0.18f, 0.18f, 0.2f);
    [Export] public Color ConcreteColor { get; set; } = new Color(0.67f, 0.67f, 0.69f);
    [Export] public Color GlassColor { get; set; } = new Color(0.6f, 0.8f, 0.9f, 0.7f);

    public Color GetColor(BlockType blockType)
    {
        return blockType switch
        {
            BlockType.Soil => SoilColor,
            BlockType.Rock => RockColor,
            BlockType.Bedrock => BedrockColor,
            BlockType.Concrete => ConcreteColor,
            BlockType.Glass => GlassColor,
            _ => Colors.White
        };
    }
}
