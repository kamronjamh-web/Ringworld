using System;
using System.Collections.Generic;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>
/// Opt-in gen-17 canonical 2 m lattice height cache for the smooth voxel baseline (spike B1).
/// Smooth chunks sample integer columns at 1 m, and every off-lattice column costs four model
/// evaluations. This evaluates each lattice vertex once and reuses it across the chunk, its halo and
/// neighbouring requests, then interpolates exactly like <c>GetRuntimePresentationSurfaceHeightLocked</c>,
/// so results are bit-identical to the uncached path. The cache belongs to one sampler instance and is
/// guarded by that sampler's noise lock; worker threads use <see cref="CreateIndependentCopy"/>, so a
/// model (mutable gully cache, native noise) is never shared across threads.
/// </summary>
public sealed partial class RingTerrainSampler
{
    private const int LatticeBlockShift = 4; // 16 x 16 vertices = 32 m blocks
    private const int LatticeBlockVertices = 1 << LatticeBlockShift;
    private const int LatticeBlockCapacity = 96;
    private Dictionary<(long Around, int Across), float[]>? _latticeBlocks;
    private long _latticeVertexEvaluations;
    private long _latticeVertexHits;

    /// <summary>Model evaluations performed by the lattice cache since the last reset (vertex misses).</summary>
    internal long LatticeVertexEvaluations { get { lock (_noiseLock) return _latticeVertexEvaluations; } }
    internal long LatticeVertexHits { get { lock (_noiseLock) return _latticeVertexHits; } }

    internal void ResetLatticeCache()
    {
        lock (_noiseLock)
        {
            _latticeBlocks?.Clear();
            _latticeVertexEvaluations = 0;
            _latticeVertexHits = 0;
        }
    }

    /// <summary>
    /// Runtime-generation presentation height for an integer column using the per-sampler lattice cache.
    /// Bit-identical to <see cref="GetPresentationSurfaceHeight(long, int)"/>.
    /// </summary>
    internal float GetPresentationSurfaceHeightLatticeCached(long globalAroundVoxel, int globalAcrossVoxel)
    {
        if (!UsesRuntimeSurfaceGeneration) return GetPresentationSurfaceHeight(globalAroundVoxel, globalAcrossVoxel);
        lock (_noiseLock)
        {
            const int spacing = 2;
            double wrappedAround = (double)globalAroundVoxel % _aroundRingVoxelLength;
            if (wrappedAround < 0.0) wrappedAround += _aroundRingVoxelLength;
            double aroundSample = wrappedAround / spacing;
            long aroundIndex = checked((long)Math.Floor(aroundSample));
            double tx = aroundSample - Math.Floor(aroundSample);
            double across = Math.Clamp((double)globalAcrossVoxel, 0.0, _ringAcrossWidthMeters - spacing);
            double acrossSample = across / spacing;
            int acrossIndex = (int)Math.Floor(acrossSample);
            double tz = acrossSample - acrossIndex;

            long around0 = RingCoordMath.WrapAround(aroundIndex * spacing, _aroundRingVoxelLength);
            long around1 = RingCoordMath.WrapAround((aroundIndex + 1L) * spacing, _aroundRingVoxelLength);
            int across0 = acrossIndex * spacing;
            int across1 = Math.Min(across0 + spacing, _ringAcrossWidthMeters - spacing);
            float h00 = GetLatticeVertexLocked(around0, across0);
            if (tx == 0.0 && tz == 0.0) return h00;
            float h10 = GetLatticeVertexLocked(around1, across0);
            float h01 = GetLatticeVertexLocked(around0, across1);
            float h11 = GetLatticeVertexLocked(around1, across1);
            return RingSurfaceLattice.TriangleInterpolate(h00, h10, h01, h11, tx, tz);
        }
    }

    private float GetLatticeVertexLocked(long around, int across)
    {
        // around is canonical and even (the ring length is even); across is even.
        long a = around >> 1;
        int b = across >> 1;
        var blocks = _latticeBlocks ??= new Dictionary<(long, int), float[]>(LatticeBlockCapacity + 1);
        var key = (a >> LatticeBlockShift, b >> LatticeBlockShift);
        if (!blocks.TryGetValue(key, out float[]? block))
        {
            if (blocks.Count >= LatticeBlockCapacity) blocks.Clear();
            block = new float[LatticeBlockVertices * LatticeBlockVertices];
            Array.Fill(block, float.NaN);
            blocks[key] = block;
        }
        int index = (int)(b & (LatticeBlockVertices - 1)) * LatticeBlockVertices + (int)(a & (LatticeBlockVertices - 1));
        float value = block[index];
        if (!float.IsNaN(value))
        {
            _latticeVertexHits++;
            return value;
        }
        value = (float)_runtimeSurfaceModel!.CalculateHeight(around, across);
        block[index] = value;
        _latticeVertexEvaluations++;
        return value;
    }
}
