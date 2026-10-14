using System;

namespace RingworldTechDemo.Terrain.Sites;

/// <summary>
/// Unshaped (pre-site) dry presentation height authority used only to build the emitter-site
/// registry. Instances are worker-confined; use <see cref="CreateWorkerCopy"/> per thread.
/// </summary>
public interface INaturalHeightSource : IDisposable
{
    /// <summary>Natural presentation height in metres at a canonical lattice position.</summary>
    double NaturalHeight(long around, int across);
    double SeaLevel { get; }
    /// <summary>Conservative lower bound of any height this source can return.</summary>
    double MinPossibleHeight { get; }
    /// <summary>Terrain definition hash of the world this source belongs to (registry cache key).</summary>
    ulong Identity { get; }
    /// <summary>Independent instance that can be used from one other thread. The caller disposes it.</summary>
    INaturalHeightSource CreateWorkerCopy();
    /// <summary>Gen-16 exceptional summit; sites must stay away from it.</summary>
    bool TryGetExclusionZone(out long around, out int across, out double radiusMeters);
}
