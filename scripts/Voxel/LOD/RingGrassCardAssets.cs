using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>One immutable asset bundle for a parameter set: atlas, card mesh, hero mesh. CPU data is final once the bundle exists;
/// Godot resources are created on first main-thread access and then reused.</summary>
public sealed class RingGrassCardAssetSet
{
    public RingGrassCardAtlasParameters Parameters { get; init; }
    public ulong CacheKey { get; init; }
    public RingGrassCardAtlasData Atlas { get; init; } = null!;
    public RingGrassMeshData CardMesh { get; init; } = null!;
    public RingGrassMeshData HeroMesh { get; init; } = null!;
    public double BuildMilliseconds { get; init; }

    /// <summary>Main thread only.</summary>
    public ImageTexture AtlasTexture => Atlas.GetTexture();
    /// <summary>Main thread only.</summary>
    public ArrayMesh CardArrayMesh => CardMesh.GetMesh();
    /// <summary>Main thread only.</summary>
    public ArrayMesh HeroArrayMesh => HeroMesh.GetMesh();
}

/// <summary>
/// Process-wide cache of the grass-card assets, keyed by hash(parameters, GeneratorVersion). Generation runs once on the
/// thread pool; concurrent requests for the same key share one task. In-memory only (user:// persistence is not needed:
/// generation is a one-off cost of a second or two and is fully deterministic).
/// </summary>
public static class RingGrassCardAssets
{
    static readonly ConcurrentDictionary<ulong, Lazy<Task<RingGrassCardAssetSet>>> Cache = new();
    static int _buildCount;

    /// <summary>Number of asset sets actually generated in this process (cache hits do not count).</summary>
    public static int BuildCount => Volatile.Read(ref _buildCount);

    /// <summary>Starts (or joins) the off-thread build. Safe to call every frame; returns the shared task.</summary>
    public static Task<RingGrassCardAssetSet> RequestAsync(RingGrassCardAtlasParameters parameters)
    {
        RingGrassCardAtlasParameters p = parameters.Normalized();
        ulong key = p.CacheKey();
        return Cache.GetOrAdd(key, _ => new Lazy<Task<RingGrassCardAssetSet>>(() => Task.Run(() => BuildSet(p, key)), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>Non-blocking poll for the layer: true once the set for these parameters is finished.</summary>
    public static bool TryGet(RingGrassCardAtlasParameters parameters, out RingGrassCardAssetSet? set)
    {
        set = null;
        if (!Cache.TryGetValue(parameters.Normalized().CacheKey(), out var lazy) || !lazy.IsValueCreated) return false;
        Task<RingGrassCardAssetSet> task = lazy.Value;
        if (!task.IsCompletedSuccessfully) return false;
        set = task.Result;
        return true;
    }

    /// <summary>Synchronous build-or-fetch (tests / tools). Prefer <see cref="RequestAsync"/> in the game.</summary>
    public static RingGrassCardAssetSet GetBlocking(RingGrassCardAtlasParameters parameters) => RequestAsync(parameters).GetAwaiter().GetResult();

    /// <summary>Drops every cached set (tests only).</summary>
    public static void ClearForTests() => Cache.Clear();

    /// <summary>Pure generation, no caching: deterministic for equal parameters on any thread.</summary>
    public static RingGrassCardAssetSet BuildSet(RingGrassCardAtlasParameters parameters, ulong key = 0)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        RingGrassCardAtlasParameters p = parameters.Normalized();
        var atlas = RingGrassCardAtlasGenerator.Build(p);
        var cards = RingGrassCardMeshFactory.Build();
        var hero = RingGrassHeroTuftMeshFactory.BuildDefault();
        Interlocked.Increment(ref _buildCount);
        return new RingGrassCardAssetSet
        {
            Parameters = p, CacheKey = key != 0 ? key : p.CacheKey(), Atlas = atlas, CardMesh = cards, HeroMesh = hero,
            BuildMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
        };
    }
}
