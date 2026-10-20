using System;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Shadows;

/// <summary>
/// Production height source: one independent RingTerrainSampler copy (procedural presentation surface only, no edit snapshot; decision 2).
/// Owns and disposes the sampler. Create on the main thread (CreateIndependentTerrainSampler touches grid state), use from exactly one worker.
/// </summary>
public sealed class RingTerrainHeightShadowSource : IHeightShadowSource
{
    private readonly RingTerrainSampler _sampler;
    public RingTerrainHeightShadowSource(RingTerrainSampler sampler) { _sampler = sampler; }

    public long CircumferenceMeters => _sampler.AroundRingVoxelLength;
    public int WidthMeters => _sampler.RingAcrossWidthMeters;
    public EmitterSiteRegistry? EmitterSites => _sampler.EmitterSites;
    public float GetHeight(long aroundMeters, int acrossMeters) => _sampler.GetPresentationSurfaceHeight(aroundMeters, acrossMeters);
    public void Dispose() => _sampler.Dispose();

    /// <summary>One independent source per fill worker (main thread). Disposes the already created ones and throws when the grid has no sampler.</summary>
    public static IHeightShadowSource[] CreateWorkerSources(VoxelChunkGridManager grid, int workers)
    {
        var sources = new IHeightShadowSource[Math.Max(1, workers)];
        try
        {
            for (int i = 0; i < sources.Length; i++)
                sources[i] = new RingTerrainHeightShadowSource(grid.CreateIndependentTerrainSampler() ?? throw new InvalidOperationException("Height shadow: terrain sampler unavailable."));
            return sources;
        }
        catch
        {
            foreach (IHeightShadowSource? source in sources) source?.Dispose();
            throw;
        }
    }

    public static IHeightShadowSource Wrap(RingTerrainSampler sampler) => new RingTerrainHeightShadowSource(sampler);
}
