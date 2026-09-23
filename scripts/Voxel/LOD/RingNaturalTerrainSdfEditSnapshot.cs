using System.Collections.Generic;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Immutable sparse signed-density deltas for natural terrain. Construction
/// remains in the exact block override layer.
/// </summary>
public sealed class RingNaturalTerrainSdfEditSnapshot
{
    private readonly IReadOnlyDictionary<RingTerrainSampleCoordinate, float> _densityDeltas;
    private readonly long _aroundVoxelLength;

    public ulong Version { get; }
    public int EditedSampleCount => _densityDeltas.Count;

    public RingNaturalTerrainSdfEditSnapshot(
        ulong version,
        long aroundVoxelLength,
        IReadOnlyDictionary<RingTerrainSampleCoordinate, float> densityDeltas)
    {
        Version = version;
        _aroundVoxelLength = System.Math.Max(1L, aroundVoxelLength);
        _densityDeltas = new Dictionary<RingTerrainSampleCoordinate, float>(densityDeltas);
    }

    public float SampleDelta(long globalAroundVoxel, int globalY, int globalAcrossVoxel)
    {
        var sample = new RingTerrainSampleCoordinate(
            CanonicalizeAround(globalAroundVoxel),
            globalY,
            globalAcrossVoxel);
        return _densityDeltas.TryGetValue(sample, out float delta) ? delta : 0.0f;
    }

    private long CanonicalizeAround(long around)
    {
        long canonical = around % _aroundVoxelLength;
        return canonical < 0 ? canonical + _aroundVoxelLength : canonical;
    }
}