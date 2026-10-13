namespace RingworldTechDemo.Underground;

/// <summary>
/// Backend seam for the enclosure probe. Coordinates are canonical ring metres.
/// Density convention matches the terrain fields: greater than zero is air, zero or less is solid.
/// </summary>
public interface IUndergroundProbeSource
{
    bool IsAvailable { get; }

    /// <summary>Changes whenever the density field or its vertical domain changes; keys the probe cache.</summary>
    ulong Revision { get; }

    bool TryGetSurfaceHeight(double around, double across, out float height);

    bool TryGetDensity(double around, double vertical, double across, out float density);
}
