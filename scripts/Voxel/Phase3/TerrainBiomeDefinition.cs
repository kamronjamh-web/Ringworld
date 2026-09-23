using Godot;

namespace RingworldTechDemo.Voxel.Phase3;

[GlobalClass]
public partial class TerrainBiomeDefinition : Resource
{
    [Export] public TerrainBiomeId Id { get; set; } = TerrainBiomeId.Grassland;
    [Export] public int Priority { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")] public float MinimumTemperature { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")] public float MaximumTemperature { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float MinimumMoisture { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")] public float MaximumMoisture { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float MinimumRidgeStrength { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")] public float MaximumRidgeStrength { get; set; } = 1.0f;
    // Distance, in normalized climate-field units, over which this biome fades
    // in/out. A non-zero value prevents categorical colour and relief seams;
    // decoration systems may still use the retained dominant biome ID.
    [Export(PropertyHint.Range, "0.01,0.50,0.01")] public float BlendWidth { get; set; } = 0.12f;
    // Scales this profile's fuzzy selection score. Broad fallback profiles can
    // use a lower value without weakening specialized biome interiors.
    [Export(PropertyHint.Range, "0.01,2.0,0.01")] public float SelectionWeightMultiplier { get; set; } = 1.0f;
    // Small, local-only surface variation.  Macro elevation remains governed
    // by continentalness and mountain belts so biome borders cannot create a
    // step in the terrain.
    [Export(PropertyHint.Range, "0,160,1")] public float LocalReliefMeters { get; set; }
    [Export] public Color SurfaceColor { get; set; } = new(0.22f, 0.34f, 0.14f, 1.0f);
}
