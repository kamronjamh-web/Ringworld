using System;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Shadows;

/// <summary>
/// Height provider of the S4 terrain height-shadow clipmap. NOT thread-safe: each fill worker owns its own instance (production wraps an independent
/// RingTerrainSampler copy, tests inject synthetic sources). Coordinates are canonical metres: around already wrapped into [0, C), across clamped to [0, W-2].
/// </summary>
public interface IHeightShadowSource : IDisposable
{
    long CircumferenceMeters { get; }
    int WidthMeters { get; }
    /// <summary>Presentation surface height in metres at the integer-metre lattice point.</summary>
    float GetHeight(long aroundMeters, int acrossMeters);
    /// <summary>Emitter site registry for bore aperture carving, or null (synthetic sources, v4 worlds).</summary>
    EmitterSiteRegistry? EmitterSites { get; }
}

/// <summary>Analytic source for headless tests: heights from a delegate, optional registry.</summary>
public sealed class SyntheticHeightShadowSource : IHeightShadowSource
{
    private readonly Func<long, int, float> _height;
    public SyntheticHeightShadowSource(long circumference, int width, Func<long, int, float> height, EmitterSiteRegistry? sites = null)
    {
        CircumferenceMeters = circumference; WidthMeters = width; _height = height; EmitterSites = sites;
    }
    public long CircumferenceMeters { get; }
    public int WidthMeters { get; }
    public EmitterSiteRegistry? EmitterSites { get; }
    public float GetHeight(long aroundMeters, int acrossMeters) => _height(aroundMeters, acrossMeters);
    public void Dispose() { }
}
