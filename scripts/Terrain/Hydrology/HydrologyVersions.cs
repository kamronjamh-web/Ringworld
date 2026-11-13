namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>Algorithm versions of the river system (docs/t2-river-reset.md). Bump a value when its output changes; the first two feed the runtime terrain definition hash of worlds with <c>water.rivers</c>.</summary>
public static class HydrologyVersions
{
    public const int DrainageAlgorithm = 7;
    public const int CarveOperator = 9;
    public const int GraphFormat = 4;
}
