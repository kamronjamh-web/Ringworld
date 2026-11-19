using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Process-wide cache of the ground-cover atlas (T6 design B2), keyed by <see cref="RingGroundCoverAtlasParameters.CacheKey"/>, built once on the thread pool like <see cref="RingGrassCardAssets"/>.
/// In memory only: generation is deterministic and takes well under a second.
/// </summary>
public static class RingGroundCoverAssets
{
    static readonly ConcurrentDictionary<ulong, Lazy<Task<RingGroundCoverAtlasData>>> Cache = new();
    static int _buildCount;

    /// <summary>Number of atlases actually generated in this process (cache hits do not count).</summary>
    public static int BuildCount => Volatile.Read(ref _buildCount);

    /// <summary>Starts (or joins) the off-thread build. A faulted build is reported once and evicted, so the next request retries.</summary>
    public static Task<RingGroundCoverAtlasData> RequestAsync(RingGroundCoverAtlasParameters parameters)
    {
        ulong key = parameters.CacheKey();
        if (Cache.TryGetValue(key, out var existing) && existing.IsValueCreated && existing.Value.IsFaulted)
        {
            GD.PushError("Ground cover atlas build failed: " + (existing.Value.Exception?.GetBaseException().Message ?? "unknown error"));
            Cache.TryRemove(new KeyValuePair<ulong, Lazy<Task<RingGroundCoverAtlasData>>>(key, existing));
        }
        return Cache.GetOrAdd(key, _ => new Lazy<Task<RingGroundCoverAtlasData>>(() => Task.Run(() => BuildCounted(parameters)), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>Non-blocking poll for the layer: true once the atlas for these parameters is finished.</summary>
    public static bool TryGet(RingGroundCoverAtlasParameters parameters, out RingGroundCoverAtlasData? atlas)
    {
        atlas = null;
        if (!Cache.TryGetValue(parameters.CacheKey(), out var lazy) || !lazy.IsValueCreated) return false;
        Task<RingGroundCoverAtlasData> task = lazy.Value;
        if (!task.IsCompletedSuccessfully) return false;
        atlas = task.Result;
        return true;
    }

    /// <summary>Synchronous build-or-fetch (tests and tools). Prefer <see cref="RequestAsync"/> in the game.</summary>
    public static RingGroundCoverAtlasData GetBlocking(RingGroundCoverAtlasParameters parameters) => RequestAsync(parameters).GetAwaiter().GetResult();

    /// <summary>Drops every cached atlas (tests only).</summary>
    public static void ClearForTests() => Cache.Clear();

    private static RingGroundCoverAtlasData BuildCounted(RingGroundCoverAtlasParameters parameters)
    {
        RingGroundCoverAtlasData atlas = RingGroundCoverAtlasGenerator.Build(parameters);
        Interlocked.Increment(ref _buildCount);
        return atlas;
    }
}
