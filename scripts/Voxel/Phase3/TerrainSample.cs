using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Voxel.Phase3;

public readonly record struct TerrainSample(
    int SurfaceHeight,
    TerrainRegion Region,
    float Continentalness,
    float Erosion,
    float RidgeStrength,
    float Temperature,
    float Moisture,
    TerrainBiomeId Biome,
    SurfaceWaterKind WaterKind,
    int WaterSurfaceHeight,
    SiteSurfaceMask SiteMask = default)
{
    public bool HasSurfaceWater => WaterKind != SurfaceWaterKind.None;
    public bool IsOcean => WaterKind == SurfaceWaterKind.Ocean;
    public int WaterDepth => HasSurfaceWater
        ? System.Math.Max(0, WaterSurfaceHeight - SurfaceHeight)
        : 0;

    public int ResolveDisplayedHeight(int terrainHeight)
        => HasSurfaceWater && terrainHeight < WaterSurfaceHeight
            ? WaterSurfaceHeight
            : terrainHeight;

    public float ResolveDisplayedHeight(float terrainHeight)
        => HasSurfaceWater && terrainHeight < WaterSurfaceHeight
            ? WaterSurfaceHeight
            : terrainHeight;
}
