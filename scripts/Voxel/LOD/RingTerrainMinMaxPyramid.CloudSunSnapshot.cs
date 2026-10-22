using System;
using System.Collections.Generic;
namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Owned copies of sampled elevation ranges, not exact bounds of arbitrary meshes.</summary>
public sealed record CloudSunHeightLevel(int Width, int Height, float CellSize, ReadOnlyMemory<float> Ranges);
public sealed record CloudSunTerrainSnapshot(double Circumference, double Width, ulong EditVersion,
    IReadOnlyList<CloudSunHeightLevel> Levels, float MinimumHeight, float MaximumHeight, int SourceIdentity = 0);

/// <summary>Flattened per-level maxima and level table handed to the sun-visibility compute; built off-thread where possible.</summary>
public static class CloudSunTerrainPacking
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CloudSunTerrainSnapshot, float[][]> Cache = new();

    public static (float[] Heights, float[] Levels) GetOrPack(CloudSunTerrainSnapshot terrain)
    {
        if (Cache.TryGetValue(terrain, out float[][]? cached)) return (cached[0], cached[1]);
        (float[] Heights, float[] Levels) packed = Pack(terrain);
        Cache.TryAdd(terrain, new[] { packed.Heights, packed.Levels });
        return packed;
    }

    /// <summary>Safe to call from a worker; the main thread then finds the result cached.</summary>
    public static void Prime(CloudSunTerrainSnapshot terrain) => GetOrPack(terrain);

    private static (float[] Heights, float[] Levels) Pack(CloudSunTerrainSnapshot terrain)
    {
        int total = 0;
        foreach (CloudSunHeightLevel level in terrain.Levels) total += level.Ranges.Length / 2;
        var heights = new float[total];
        int cursor = 0;
        foreach (CloudSunHeightLevel level in terrain.Levels)
        {
            ReadOnlySpan<float> ranges = level.Ranges.Span;
            for (int i = 1; i < ranges.Length; i += 2) heights[cursor++] = ranges[i];
        }
        var levels = new float[terrain.Levels.Count * 4];
        int offset = 0;
        for (int n = 0; n < terrain.Levels.Count; n++)
        {
            CloudSunHeightLevel level = terrain.Levels[n];
            levels[n * 4] = level.Width; levels[n * 4 + 1] = level.Height; levels[n * 4 + 2] = offset; levels[n * 4 + 3] = level.CellSize;
            offset += level.Width * level.Height;
        }
        return (heights, levels);
    }
}

public sealed partial class RingTerrainMinMaxPyramid
{
    public CloudSunTerrainSnapshot CaptureCloudSunTerrainSnapshot()
    {
        var levels = new List<CloudSunHeightLevel>(_levels.Count);
        foreach (Level level in _levels)
        {
            var ranges = new float[level.Cells.Length * 2];
            for (int i = 0; i < level.Cells.Length; i++)
            { ranges[i * 2] = level.Cells[i].MinHeight; ranges[i * 2 + 1] = level.Cells[i].MaxHeight; }
            levels.Add(new(level.Width, level.Height, level.CellSize, ranges));
        }
        HeightRange root = _levels[^1].Cells[0];
        return new(_aroundLength, _ringWidth, EditVersion, levels.AsReadOnly(), root.MinHeight, root.MaxHeight, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this));
    }
}
