using Godot;

namespace RingworldTechDemo.Voxel.Phase3;

[GlobalClass]
public partial class TerrainBiomeCatalog : Resource
{
    [Export] public Godot.Collections.Array<TerrainBiomeDefinition> Biomes { get; set; } = new();
}
