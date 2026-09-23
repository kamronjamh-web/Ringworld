using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Immutable terrain-density view for worker meshing. It keeps voxel chunks and
/// regional LODs on one edited terrain definition while allowing either side to
/// choose its own lattice spacing.
/// </summary>
public sealed class RingTerrainSdfSnapshot : IDisposable
{
    private readonly RingTerrainSampler _sampler;
    private readonly RingLodSurfaceEditSnapshot _edits;
    private readonly RingNaturalTerrainSdfEditSnapshot _naturalSdfEdits;
    private readonly int _ringWidthMeters;
    private readonly int _bedrockY;
    private readonly int _soilDepth;
    private bool _disposed;

    private RingTerrainSdfSnapshot(
        RingTerrainSampler sampler,
        RingLodSurfaceEditSnapshot edits,
        RingNaturalTerrainSdfEditSnapshot naturalSdfEdits,
        int ringWidthMeters,
        int bedrockY,
        int soilDepth)
    {
        _sampler = sampler;
        _edits = edits;
        _naturalSdfEdits = naturalSdfEdits;
        _ringWidthMeters = Math.Max(1, ringWidthMeters);
        _bedrockY = bedrockY;
        _soilDepth = Math.Max(1, soilDepth);
    }

    public static RingTerrainSdfSnapshot Create(
        RingTerrainSampler source,
        RingLodSurfaceEditSnapshot edits,
        RingNaturalTerrainSdfEditSnapshot naturalSdfEdits,
        int ringWidthMeters,
        int bedrockY,
        int soilDepth) => new(
            source.CreateIndependentCopy(),
            edits,
            naturalSdfEdits,
            ringWidthMeters,
            bedrockY,
            soilDepth);

    /// <summary>
    /// Returns negative values inside natural terrain and positive values in air.
    /// The zero-crossing sits halfway above the authoritative integer surface.
    /// </summary>
    public float SampleDensity(long globalAround, int globalY, int globalAcross)
    {
        ThrowIfDisposed();
        if (globalAcross < 0 || globalAcross > _ringWidthMeters)
            return 1.0f;

        if (_edits.TryGetEditedSurfaceHeight(
            globalAround,
            globalAcross,
            out int editedSurface))
        {
            return globalY - (editedSurface + 0.5f)
                + _naturalSdfEdits.SampleDelta(globalAround, globalY, globalAcross);
        }

        return _sampler.SampleNaturalTerrainDensity(globalAround, globalY, globalAcross)
            + _naturalSdfEdits.SampleDelta(globalAround, globalY, globalAcross);
    }

    public BlockType SampleNaturalMaterial(long globalAround, int globalY, int globalAcross)
    {
        ThrowIfDisposed();
        if (SampleDensity(globalAround, globalY, globalAcross) > 0.0f)
            return BlockType.Air;
        if (globalY <= _bedrockY)
            return BlockType.Bedrock;

        int surface = _edits.GetSurfaceHeight(_sampler, globalAround, globalAcross);
        return surface - globalY < _soilDepth ? BlockType.Soil : BlockType.Rock;
    }

    public float[] CaptureDensityLattice(
        Vector3I globalOrigin,
        Vector3I sampleCount,
        int spacingMeters)
    {
        ThrowIfDisposed();
        int spacing = Math.Max(1, spacingMeters);
        int countX = Math.Max(0, sampleCount.X);
        int countY = Math.Max(0, sampleCount.Y);
        int countZ = Math.Max(0, sampleCount.Z);
        var densities = new float[countX * countY * countZ];
        int index = 0;
        for (int z = 0; z < countZ; z++)
        for (int y = 0; y < countY; y++)
        for (int x = 0; x < countX; x++)
        {
            densities[index++] = SampleDensity(
                globalOrigin.X + x * spacing,
                globalOrigin.Y + y * spacing,
                globalOrigin.Z + z * spacing);
        }
        return densities;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _sampler.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RingTerrainSdfSnapshot));
    }
}
