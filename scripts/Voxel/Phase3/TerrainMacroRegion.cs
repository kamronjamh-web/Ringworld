namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>
/// Cached, deterministic ownership for one large terrain cell.  It is not a
/// render tile: fine terrain remains continuously sampled.  The record gives
/// hydrology, biome, and decoration generation a stable regional context.
/// </summary>
public readonly record struct TerrainMacroRegion(
    long AroundRegion,
    int AcrossRegion,
    long CenterAroundVoxel,
    int CenterAcrossVoxel,
    TerrainRegion DominantTerrain,
    float Continentalness,
    float Erosion,
    float Temperature,
    float Moisture,
    float RidgeStrength,
    float FlowAround,
    float FlowAcross)
{
    public bool SupportsInlandWater => DominantTerrain is TerrainRegion.Lowlands or TerrainRegion.Highlands
        && Moisture >= 0.34f
        && Continentalness > -0.04f;
}
