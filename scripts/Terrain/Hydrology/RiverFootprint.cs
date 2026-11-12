namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// S10 footprints: what a sample's spacing means to the river operator. Spacing 2 (the finest proxy level, Terrain3D, the controller and vegetation) is a point, footprint 0;
/// 4 to 256 m pass their spacing; coarser levels and the far views are <see cref="Far"/> (valley only, no river owner). The proxy cache key and record store the footprint as an
/// integer, with <see cref="FarCode"/> standing for +infinity.
/// </summary>
public static class RiverFootprint
{
    /// <summary>The far views: valley only and no river owner (S10).</summary>
    public const double Far = double.PositiveInfinity;

    /// <summary>The cache key and record value for <see cref="Far"/>; the record stores footprints as a ushort.</summary>
    public const int FarCode = ushort.MaxValue;

    private const int LargestFinitePointSpacing = 256;

    /// <summary>The cache footprint code of a proxy level with the given sample spacing (0 for a point).</summary>
    public static int ForProxySpacing(int spacingMeters)
        => spacingMeters <= 2 ? 0 : spacingMeters <= LargestFinitePointSpacing ? spacingMeters : FarCode;

    /// <summary>The footprint in metres for a cache footprint code.</summary>
    public static double ToMeters(int footprintCode) => footprintCode >= FarCode ? Far : footprintCode;
}
