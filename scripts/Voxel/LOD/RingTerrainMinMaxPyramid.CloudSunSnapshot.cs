using System;
using System.Collections.Generic;
namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Owned copies of sampled elevation ranges, not exact bounds of arbitrary meshes.</summary>
public sealed record CloudSunHeightLevel(int Width, int Height, float CellSize, ReadOnlyMemory<float> Ranges);
public sealed record CloudSunTerrainSnapshot(double Circumference, double Width, ulong EditVersion,
    IReadOnlyList<CloudSunHeightLevel> Levels, float MinimumHeight, float MaximumHeight, int SourceIdentity = 0);

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
