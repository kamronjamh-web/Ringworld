using System;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>Read-only accessors for the emitter-site registry build. No generation behaviour lives here.</summary>
public sealed partial class RingTerrainSampler
{
    /// <summary>Lowest natural surface this sampler can produce (generation-16 clamp floor; runtime recipes use -8000).</summary>
    public int MinimumNaturalSurfaceHeight => UsesRuntimeSurfaceGeneration
        ? -8000
        : Math.Max(_settings.BedrockY + 1, _settings.DeepOceanFloorY);

    public float ExceptionalSummitRadiusMeters => _settings.ExceptionalSummitRadiusMeters;

    /// <summary>
    /// Generation-16 dry presentation height before hydrology (continental plus biome relief plus the
    /// 0.5 presentation offset). This is the value any future site shaping operator receives as h0.
    /// </summary>
    public float GetUnshapedDryPresentationHeight(long globalAroundVoxel, int globalAcrossVoxel)
    {
        if (UsesRuntimeSurfaceGeneration)
            throw new InvalidOperationException("Runtime surface generation exposes its unshaped height through the height model.");
        lock (_noiseLock)
        {
            long around = _periodicAroundRing
                ? RingCoordMath.WrapAround(globalAroundVoxel, _aroundRingVoxelLength)
                : globalAroundVoxel;
            RegionalCoordinate coordinate = ResolveRegionalCoordinate(around, globalAcrossVoxel);
            TerrainFields fields = SampleTerrainFieldsLocked(around, globalAcrossVoxel, coordinate);
            float preliminaryHeight = GetContinentalSurfaceHeightContinuousLocked(
                around, globalAcrossVoxel, coordinate, fields.Continentalness, fields.Erosion, fields.Mountain);
            return GetBiomeTerrainHeightLocked(coordinate, fields, preliminaryHeight).Height + SurfaceHeightOffset;
        }
    }
}
