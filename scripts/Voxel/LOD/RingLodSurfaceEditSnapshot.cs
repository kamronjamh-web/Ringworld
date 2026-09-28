using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Terrain.Surface;
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
    private readonly IReadOnlyDictionary<RingTerrainColumnCoordinate, float> _canonicalFloatSurfaceHeights;
    private readonly IReadOnlyCollection<RingTerrainColumnCoordinate> _canonicalDirtyColumns;
    private readonly long _aroundVoxelLength;
    private readonly int _editedColumnCount;

    public ulong Version { get; }
    public int EditedColumnCount => _editedColumnCount;
    public bool HasEdits => EditedColumnCount > 0;
    /// <summary>True when this snapshot originates from a float Terrain3D surface field.</summary>
    public bool UsesFloatSurface { get; }
    public IReadOnlyDictionary<Vector2I, int> EditedColumns => _surfaceHeights;
    public IReadOnlyCollection<Vector2I> DirtyColumns => _dirtyColumns;
    public IReadOnlyDictionary<RingTerrainColumnCoordinate, int> CanonicalEditedColumns => _canonicalSurfaceHeights;
    /// <summary>Exact float surface overrides for presentation consumers.</summary>
    public IReadOnlyDictionary<RingTerrainColumnCoordinate, float> CanonicalFloatColumns => _canonicalFloatSurfaceHeights;
    public IReadOnlyCollection<RingTerrainColumnCoordinate> CanonicalDirtyColumns => _canonicalDirtyColumns;

    public RingLodSurfaceEditSnapshot(
        ulong version,
        int aroundVoxelLength,
        IReadOnlyDictionary<Vector2I, int> surfaceHeights,
        IReadOnlyCollection<Vector2I>? dirtyColumns = null)
    {
        Version = version;
        _aroundVoxelLength = Mathf.Max(1, aroundVoxelLength);
        _surfaceHeights = new Dictionary<Vector2I, int>(surfaceHeights);
        _dirtyColumns = dirtyColumns == null ? new List<Vector2I>(surfaceHeights.Keys) : new List<Vector2I>(dirtyColumns);
        var canonicalHeights = new Dictionary<RingTerrainColumnCoordinate, int>(surfaceHeights.Count);
        foreach ((Vector2I column, int height) in surfaceHeights)
            canonicalHeights[new RingTerrainColumnCoordinate(column.X, column.Y)] = height;
        _canonicalSurfaceHeights = canonicalHeights;
        _canonicalFloatSurfaceHeights = new Dictionary<RingTerrainColumnCoordinate, float>();
        var canonicalDirtyColumns = new List<RingTerrainColumnCoordinate>(_dirtyColumns.Count);
        foreach (Vector2I column in _dirtyColumns)
            canonicalDirtyColumns.Add(new RingTerrainColumnCoordinate(column.X, column.Y));
        _canonicalDirtyColumns = canonicalDirtyColumns;
        _editedColumnCount = _canonicalSurfaceHeights.Count;
        UsesFloatSurface = false;
    }

    public RingLodSurfaceEditSnapshot(
        ulong version,
        long aroundVoxelLength,
        IReadOnlyDictionary<RingTerrainColumnCoordinate, int> surfaceHeights,
        IReadOnlyCollection<RingTerrainColumnCoordinate>? dirtyColumns = null)
        : this(version, aroundVoxelLength, surfaceHeights,
            new Dictionary<RingTerrainColumnCoordinate, float>(), false, dirtyColumns)
    {
    }

    /// <summary>
    /// Creates a snapshot that preserves legacy integer edits and the exact Terrain3D
    /// presentation heights independently. Float columns may exist without a legacy
    /// integer override when an edit is fractional-only.
    /// </summary>
    public RingLodSurfaceEditSnapshot(
        ulong version,
        long aroundVoxelLength,
        IReadOnlyDictionary<RingTerrainColumnCoordinate, int> surfaceHeights,
        IReadOnlyDictionary<RingTerrainColumnCoordinate, float> floatSurfaceHeights,
        bool usesFloatSurface,
        IReadOnlyCollection<RingTerrainColumnCoordinate>? dirtyColumns = null)
    {
        Version = version;
        _aroundVoxelLength = Math.Max(1, aroundVoxelLength);
        _canonicalSurfaceHeights = new Dictionary<RingTerrainColumnCoordinate, int>(surfaceHeights);
        _canonicalFloatSurfaceHeights = new Dictionary<RingTerrainColumnCoordinate, float>(floatSurfaceHeights);
        UsesFloatSurface = usesFloatSurface;
        if (dirtyColumns != null && floatSurfaceHeights.Count == 0)
        {
            _canonicalDirtyColumns = new List<RingTerrainColumnCoordinate>(dirtyColumns);
        }
        else if (dirtyColumns != null)
        {
            var allDirtyColumns = new HashSet<RingTerrainColumnCoordinate>(dirtyColumns);
            allDirtyColumns.UnionWith(floatSurfaceHeights.Keys);
            _canonicalDirtyColumns = new List<RingTerrainColumnCoordinate>(allDirtyColumns);
        }
        else if (floatSurfaceHeights.Count == 0)
        {
            // Preserve the established integer-only constructor behavior and ordering.
            _canonicalDirtyColumns = new List<RingTerrainColumnCoordinate>(surfaceHeights.Keys);
        }
        else
        {
            var allColumns = new HashSet<RingTerrainColumnCoordinate>(surfaceHeights.Keys);
            allColumns.UnionWith(floatSurfaceHeights.Keys);
            _canonicalDirtyColumns = new List<RingTerrainColumnCoordinate>(allColumns);
        }
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
        _editedColumnCount = _canonicalSurfaceHeights.Count;
        foreach (RingTerrainColumnCoordinate column in _canonicalFloatSurfaceHeights.Keys)
            if (!_canonicalSurfaceHeights.ContainsKey(column)) _editedColumnCount++;
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

    public bool TryGetEditedSurfaceHeightFloat(long globalAroundVoxel, int globalAcrossVoxel, out float height)
    {
        long canonicalAround = CanonicalizeAround(globalAroundVoxel);
        return _canonicalFloatSurfaceHeights.TryGetValue(
            new RingTerrainColumnCoordinate(canonicalAround, globalAcrossVoxel), out height);
    }

    /// <summary>
    /// Returns the exact Terrain3D edit where one exists. Legacy integer edits retain
    /// their existing meaning by using the sampler's presentation surface offset.
    /// </summary>
    public float GetPresentationSurfaceHeight(RingTerrainSampler sampler, long globalAroundVoxel, int globalAcrossVoxel)
    {
        if (sampler.UsesRuntimeSurfaceGeneration && UsesFloatSurface)
            return GetPresentationSurfaceHeight(sampler, (double)globalAroundVoxel, globalAcrossVoxel);
        if (TryGetEditedSurfaceHeightFloat(globalAroundVoxel, globalAcrossVoxel, out float floatHeight))
            return floatHeight;
        if (TryGetEditedSurfaceHeight(globalAroundVoxel, globalAcrossVoxel, out int editedHeight))
            return editedHeight + sampler.SurfaceHeightOffset;
        return sampler.GetPresentationSurfaceHeight(globalAroundVoxel, globalAcrossVoxel);
    }

    /// <summary>
    /// Samples immutable runtime field overrides on the same canonical 2m,
    /// positive-diagonal lattice as the gameplay Terrain3D field. Generation 16
    /// continues to interpolate its one-metre integer presentation surface.
    /// </summary>
    public float GetPresentationSurfaceHeight(RingTerrainSampler sampler, double globalAroundVoxel, double globalAcrossVoxel)
    {
        if (!double.IsFinite(globalAroundVoxel) || !double.IsFinite(globalAcrossVoxel))
            throw new ArgumentOutOfRangeException("Presentation surface coordinates must be finite.");
        if (!sampler.UsesRuntimeSurfaceGeneration || !UsesFloatSurface)
            return SampleLegacyPresentationHeight(sampler, globalAroundVoxel, globalAcrossVoxel);
        if (!HasEdits)
            return sampler.GetPresentationSurfaceHeight(globalAroundVoxel, globalAcrossVoxel);

        const int spacing = 2;
        double wrappedAround = globalAroundVoxel % _aroundVoxelLength;
        if (wrappedAround < 0.0) wrappedAround += _aroundVoxelLength;
        double aroundSample = wrappedAround / spacing;
        long aroundIndex = checked((long)Math.Floor(aroundSample));
        double tx = aroundSample - Math.Floor(aroundSample);

        int finalAcross = Math.Max(0, sampler.RingAcrossWidthMeters - spacing);
        double across = Math.Clamp(globalAcrossVoxel, 0.0, finalAcross);
        double acrossSample = across / spacing;
        int acrossIndex = (int)Math.Floor(acrossSample);
        double tz = acrossSample - acrossIndex;
        long around0 = CanonicalizeAround(aroundIndex * spacing);
        long around1 = CanonicalizeAround((aroundIndex + 1L) * spacing);
        int across0 = acrossIndex * spacing;
        int across1 = Math.Min(across0 + spacing, finalAcross);

        float h00 = GetRuntimeLatticeHeight(sampler, around0, across0);
        if (tx == 0.0 && tz == 0.0) return h00;
        float h10 = GetRuntimeLatticeHeight(sampler, around1, across0);
        float h01 = GetRuntimeLatticeHeight(sampler, around0, across1);
        float h11 = GetRuntimeLatticeHeight(sampler, around1, across1);
        return RingSurfaceLattice.TriangleInterpolate(h00, h10, h01, h11, tx, tz);
    }

    private float GetRuntimeLatticeHeight(RingTerrainSampler sampler, long around, int across)
    {
        if (TryGetEditedSurfaceHeightFloat(around, across, out float floatHeight))
            return floatHeight;
        if (TryGetEditedSurfaceHeight(around, across, out int editedHeight))
            return editedHeight + sampler.SurfaceHeightOffset;
        return sampler.GetPresentationSurfaceHeight(around, across);
    }

    private float SampleLegacyPresentationHeight(RingTerrainSampler sampler, double globalAroundVoxel, double globalAcrossVoxel)
    {
        double across = Math.Clamp(globalAcrossVoxel, 0.0, sampler.RingAcrossWidthMeters);
        long around0 = checked((long)Math.Floor(globalAroundVoxel));
        int across0 = (int)Math.Floor(across);
        long around1 = around0 + 1;
        int across1 = Math.Min(across0 + 1, sampler.RingAcrossWidthMeters);
        double tx = globalAroundVoxel - Math.Floor(globalAroundVoxel);
        double tz = across - across0;
        float h00 = GetPresentationSurfaceHeight(sampler, around0, across0);
        float h10 = GetPresentationSurfaceHeight(sampler, around1, across0);
        float h01 = GetPresentationSurfaceHeight(sampler, around0, across1);
        float h11 = GetPresentationSurfaceHeight(sampler, around1, across1);
        double low = h00 + (h10 - h00) * tx;
        double high = h01 + (h11 - h01) * tx;
        return (float)(low + (high - low) * tz);
    }

    /// <summary>Returns a surface sample whose shoreline follows an exact runtime edit.</summary>
    public TerrainSample GetPresentationTerrainSample(RingTerrainSampler sampler, long globalAroundVoxel, int globalAcrossVoxel)
    {
        TerrainSample sample = sampler.GetTerrainSample(globalAroundVoxel, globalAcrossVoxel);
        if (!sampler.UsesRuntimeSurfaceGeneration || !TryGetEditedSurfaceHeightFloat(globalAroundVoxel, globalAcrossVoxel, out float height))
            return sample;

        int surfaceHeight = Mathf.FloorToInt(height);
        SurfaceWaterKind water = height < 0.0f ? SurfaceWaterKind.Ocean : SurfaceWaterKind.None;
        return sample with
        {
            SurfaceHeight = surfaceHeight,
            WaterKind = water,
            WaterSurfaceHeight = water == SurfaceWaterKind.Ocean ? 0 : surfaceHeight
        };
    }

    /// <summary>
    /// Projects sparse one-meter edits onto a coarser proxy lattice. The strongest
    /// local height delta wins when multiple edited columns share one sample.
    /// </summary>
    public RingLodSurfaceEditSnapshot ForSampleSpacing(RingTerrainSampler sampler, int sampleSpacing, int maxAcrossVoxel)
    {
        int spacing = Mathf.Max(1, sampleSpacing);
        if (spacing == 1 || (_canonicalSurfaceHeights.Count == 0 && _canonicalFloatSurfaceHeights.Count == 0)) return this;

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

        var sampledFloatHeights = new Dictionary<RingTerrainColumnCoordinate, float>();
        foreach ((RingTerrainColumnCoordinate editedColumn, float editedHeight) in _canonicalFloatSurfaceHeights)
        {
            long sampleAround = (long)Math.Round(editedColumn.AroundVoxel / (double)spacing) * spacing;
            int sampleAcross = Mathf.RoundToInt(editedColumn.AcrossVoxel / (float)spacing) * spacing;
            sampleAround = CanonicalizeAround(sampleAround);
            sampleAcross = Mathf.Clamp(sampleAcross, 0, Mathf.Max(0, maxAcrossVoxel));
            var sampleColumn = new RingTerrainColumnCoordinate(sampleAround, sampleAcross);

            float sourceBaseline = sampler.GetPresentationSurfaceHeight(editedColumn.AroundVoxel, editedColumn.AcrossVoxel);
            float sampleBaseline = sampler.GetPresentationSurfaceHeight(sampleAround, sampleAcross);
            float candidateHeight = sampleBaseline + editedHeight - sourceBaseline;
            if (!sampledFloatHeights.TryGetValue(sampleColumn, out float existingHeight)
                || Mathf.Abs(candidateHeight - sampleBaseline) > Mathf.Abs(existingHeight - sampleBaseline))
            {
                sampledFloatHeights[sampleColumn] = candidateHeight;
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
            sampledFloatHeights,
            UsesFloatSurface,
            sampledDirtyColumns);
    }

    private long CanonicalizeAround(long around)
    {
        long canonical = around % _aroundVoxelLength;
        return canonical < 0 ? canonical + _aroundVoxelLength : canonical;
    }
}
