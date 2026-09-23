using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Immutable, worker-safe surface-height overrides derived from sparse voxel edits.
/// Only columns whose visible top differs from the procedural baseline are stored.
/// </summary>
public sealed class RingLodSurfaceEditSnapshot
{
    private readonly IReadOnlyDictionary<Vector2I, int> _surfaceHeights;
    private readonly IReadOnlyCollection<Vector2I> _dirtyColumns;
    private readonly IReadOnlyDictionary<RingTerrainColumnCoordinate, int> _canonicalSurfaceHeights;
    private readonly IReadOnlyCollection<RingTerrainColumnCoordinate> _canonicalDirtyColumns;
    private readonly long _aroundVoxelLength;

    public ulong Version { get; }
    public int EditedColumnCount => _canonicalSurfaceHeights.Count;
    public IReadOnlyDictionary<Vector2I, int> EditedColumns => _surfaceHeights;
    public IReadOnlyCollection<Vector2I> DirtyColumns => _dirtyColumns;
    public IReadOnlyDictionary<RingTerrainColumnCoordinate, int> CanonicalEditedColumns => _canonicalSurfaceHeights;
    public IReadOnlyCollection<RingTerrainColumnCoordinate> CanonicalDirtyColumns => _canonicalDirtyColumns;

    public RingLodSurfaceEditSnapshot(
        ulong version,
        int aroundVoxelLength,
        IReadOnlyDictionary<Vector2I, int> surfaceHeights,
        IReadOnlyCollection<Vector2I>? dirtyColumns = null)
    {
        Version = version;
        _aroundVoxelLength = Mathf.Max(1, aroundVoxelLength);
        _surfaceHeights = surfaceHeights;
        _dirtyColumns = dirtyColumns ?? new List<Vector2I>(surfaceHeights.Keys);
        var canonicalHeights = new Dictionary<RingTerrainColumnCoordinate, int>(surfaceHeights.Count);
        foreach ((Vector2I column, int height) in surfaceHeights)
            canonicalHeights[new RingTerrainColumnCoordinate(column.X, column.Y)] = height;
        _canonicalSurfaceHeights = canonicalHeights;
        var canonicalDirtyColumns = new List<RingTerrainColumnCoordinate>(_dirtyColumns.Count);
        foreach (Vector2I column in _dirtyColumns)
            canonicalDirtyColumns.Add(new RingTerrainColumnCoordinate(column.X, column.Y));
        _canonicalDirtyColumns = canonicalDirtyColumns;
    }

    public RingLodSurfaceEditSnapshot(
        ulong version,
        long aroundVoxelLength,
        IReadOnlyDictionary<RingTerrainColumnCoordinate, int> surfaceHeights,
        IReadOnlyCollection<RingTerrainColumnCoordinate>? dirtyColumns = null)
    {
        Version = version;
        _aroundVoxelLength = Math.Max(1, aroundVoxelLength);
        _canonicalSurfaceHeights = surfaceHeights;
        _canonicalDirtyColumns = dirtyColumns ?? new List<RingTerrainColumnCoordinate>(surfaceHeights.Keys);
        var legacyHeights = new Dictionary<Vector2I, int>();
        var legacyDirtyColumns = new List<Vector2I>();
        if (_aroundVoxelLength <= int.MaxValue)
        {
            foreach ((RingTerrainColumnCoordinate column, int height) in surfaceHeights)
                legacyHeights[new Vector2I((int)column.AroundVoxel, column.AcrossVoxel)] = height;
            foreach (RingTerrainColumnCoordinate column in _canonicalDirtyColumns)
                legacyDirtyColumns.Add(new Vector2I((int)column.AroundVoxel, column.AcrossVoxel));
        }
        _surfaceHeights = legacyHeights;
        _dirtyColumns = legacyDirtyColumns;
    }

    public int GetSurfaceHeight(RingTerrainSampler sampler, int globalAroundVoxel, int globalAcrossVoxel)
    {
        long canonicalAround = CanonicalizeAround(globalAroundVoxel);
        var key = new RingTerrainColumnCoordinate(canonicalAround, globalAcrossVoxel);
        return _canonicalSurfaceHeights.TryGetValue(key, out int editedHeight)
            ? editedHeight
            : sampler.GetSurfaceHeight(canonicalAround, globalAcrossVoxel);
    }

    public bool TryGetEditedSurfaceHeight(int globalAroundVoxel, int globalAcrossVoxel, out int height)
    {
        return TryGetEditedSurfaceHeight((long)globalAroundVoxel, globalAcrossVoxel, out height);
    }

    /// <summary>
    /// Compatibility bridge while the proxy/HLOD column stores migrate from
    /// Vector2I to RingTerrainColumnCoordinate. Mega-scale construction edits
    /// are not projected into legacy heightfield LODs.
    /// </summary>
    public bool TryGetEditedSurfaceHeight(long globalAroundVoxel, int globalAcrossVoxel, out int height)
    {
        long canonicalAround = CanonicalizeAround(globalAroundVoxel);
        return _canonicalSurfaceHeights.TryGetValue(
            new RingTerrainColumnCoordinate(canonicalAround, globalAcrossVoxel),
            out height);
    }

    public int GetSurfaceHeight(RingTerrainSampler sampler, long globalAroundVoxel, int globalAcrossVoxel)
    {
        return TryGetEditedSurfaceHeight(globalAroundVoxel, globalAcrossVoxel, out int editedHeight)
            ? editedHeight
            : sampler.GetSurfaceHeight(globalAroundVoxel, globalAcrossVoxel);
    }

    /// <summary>
    /// Projects sparse one-meter edits onto a coarser proxy lattice. The strongest
    /// local height delta wins when multiple edited columns share one sample.
    /// </summary>
    public RingLodSurfaceEditSnapshot ForSampleSpacing(RingTerrainSampler sampler, int sampleSpacing, int maxAcrossVoxel)
    {
        int spacing = Mathf.Max(1, sampleSpacing);
        if (spacing == 1 || _canonicalSurfaceHeights.Count == 0) return this;

        var sampledHeights = new Dictionary<RingTerrainColumnCoordinate, int>();
        foreach ((RingTerrainColumnCoordinate editedColumn, int editedHeight) in _canonicalSurfaceHeights)
        {
            long sampleAround = (long)Math.Round(editedColumn.AroundVoxel / (double)spacing) * spacing;
            int sampleAcross = Mathf.RoundToInt(editedColumn.AcrossVoxel / (float)spacing) * spacing;
            sampleAround = CanonicalizeAround(sampleAround);
            sampleAcross = Mathf.Clamp(sampleAcross, 0, Mathf.Max(0, maxAcrossVoxel));
            var sampleColumn = new RingTerrainColumnCoordinate(sampleAround, sampleAcross);

            int sourceBaseline = sampler.GetSurfaceHeight(editedColumn.AroundVoxel, editedColumn.AcrossVoxel);
            int sampleBaseline = sampler.GetSurfaceHeight(sampleAround, sampleAcross);
            int candidateHeight = sampleBaseline + editedHeight - sourceBaseline;
            if (!sampledHeights.TryGetValue(sampleColumn, out int existingHeight)
                || Mathf.Abs(candidateHeight - sampleBaseline) > Mathf.Abs(existingHeight - sampleBaseline))
            {
                sampledHeights[sampleColumn] = candidateHeight;
            }
        }

        var sampledDirtyColumns = new HashSet<RingTerrainColumnCoordinate>();
        foreach (RingTerrainColumnCoordinate dirtyColumn in _canonicalDirtyColumns)
        {
            long sampleAround = (long)Math.Round(dirtyColumn.AroundVoxel / (double)spacing) * spacing;
            int sampleAcross = Mathf.RoundToInt(dirtyColumn.AcrossVoxel / (float)spacing) * spacing;
            sampledDirtyColumns.Add(new RingTerrainColumnCoordinate(
                CanonicalizeAround(sampleAround),
                Mathf.Clamp(sampleAcross, 0, Mathf.Max(0, maxAcrossVoxel))));
        }

        return new RingLodSurfaceEditSnapshot(
            Version,
            _aroundVoxelLength,
            sampledHeights,
            sampledDirtyColumns);
    }

    private long CanonicalizeAround(long around)
    {
        long canonical = around % _aroundVoxelLength;
        return canonical < 0 ? canonical + _aroundVoxelLength : canonical;
    }
}
