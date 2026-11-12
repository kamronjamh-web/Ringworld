using System;
using System.Text;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// The pre-graph input identity of a river bake (S12, D14): everything the drainage grid, routing and graph read, hashed before any graph exists. It finds and validates the graph file
/// and keys <see cref="RiverGraphStore"/>. It mixes the tag <c>hydrology-input-v1</c>, <see cref="TerrainGenerationParameters.NaturalSiteIdentity"/> (which excludes extension fields, so a later
/// generation's relief fields stay out), the 20 world climate values, the six river fields, the site registries' identities, the circumference and width bits, the grid size and
/// <see cref="HydrologyVersions.DrainageAlgorithm"/>.
/// </summary>
public static class HydrologyBakeIdentity
{
    public const string Tag = "hydrology-input-v1";

    /// <summary>The identity of a recipe, building (or reusing) its site registries.</summary>
    public static ulong Compute(TerrainGenerationParameters p)
        => Compute(p, p.HasEmitterSites ? EmitterSiteTerrainShaper.GetOrCreate(p) : null);

    public static ulong Compute(TerrainGenerationParameters p, EmitterSiteTerrainShaper? sites)
    {
        ulong h = 14695981039346656037UL;
        void Mix(ulong v) { unchecked { for (int i = 0; i < 8; i++) { h ^= (v >> (8 * i)) & 0xFF; h *= 1099511628211UL; } } }
        void Double(double v) => Mix((ulong)BitConverter.DoubleToInt64Bits(v));
        foreach (byte b in Encoding.ASCII.GetBytes(Tag)) { h ^= b; unchecked { h *= 1099511628211UL; } }
        Mix(p.NaturalSiteIdentity);
        Double(p.ContinentalFrequency); Double(p.ErosionFrequency); Double(p.RidgeFrequency); Double(p.TemperatureFrequency); Double(p.MoistureFrequency);
        Double(p.MountainWarpFrequency); Double(p.ValleyFrequency); Double(p.ContinentalSecondaryStrength); Double(p.ContinentalContrast); Double(p.ContinentalOceanBias);
        Double(p.MountainWarpDistanceMeters); Double(p.PeakRarityExponent); Double(p.MountainBeltBroadness); Double(p.ValleyNarrowness); Double(p.OceanMoistureInfluence);
        Double(p.ClimateLatitudeInfluence); Double(p.CoastalBeachStrength); Double(p.AltitudeTemperatureDropPerKilometre); Double(p.MountainRainShadowStrength);
        Mix((ulong)p.MacroRegionSizeMeters);
        foreach (string field in RiverFields) Double(p.HasRivers ? p.GetExtensionField(field) : 0d);
        if (sites != null)
        {
            Mix(sites.Registry.Identity);
            Mix(sites.Climate?.Identity ?? 0UL);
            Mix(sites.WeatherLung?.Identity ?? 0UL);
        }
        Double(p.CircumferenceM); Double(p.WidthM);
        Mix((ulong)Math.Round(p.CircumferenceM / 1000d)); Mix((ulong)Math.Round(p.WidthM / 1000d));
        Mix((ulong)HydrologyVersions.DrainageAlgorithm);
        return h == 0 ? 1UL : h;
    }

    private static readonly string[] RiverFields = { "river_density", "lake_abundance", "river_valley_width", "channel_depth", "meander_strength", "river_ferocity" };
}
