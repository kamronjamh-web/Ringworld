using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Terrain.Surface;

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
    SiteSurfaceMask SiteMask = default,
    float ExactWaterLevel = 0f,
    bool HasExactWaterLevel = false,
    RiverBedMask RiverBeds = default)
{
    /// <summary>S9: the float surface of inland water. The flag is set only by the sampler, only with Lake or River, a finite level and <c>WaterSurfaceHeight == floor(level)</c>; otherwise this is the whole-metre <see cref="WaterSurfaceHeight"/>.</summary>
    public float WaterLevel => HasExactWaterLevel ? ExactWaterLevel : WaterSurfaceHeight;
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
        => HasSurfaceWater && terrainHeight < WaterLevel
            ? WaterLevel
            : terrainHeight;
}
