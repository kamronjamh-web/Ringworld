using System;
using System.Collections.Generic;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Voxel.Phase2;

/// <summary>Optional Terrain3D surface-only bridge. It snapshots sparse heights before LOD workers consume them.</summary>
public partial class VoxelChunkGridManager
{
    private RingSurfaceHeightField? _surfaceHeightField;
    private ulong _observedSurfaceHeightRevision = ulong.MaxValue;
    private readonly HashSet<RingTerrainColumnCoordinate> _previousSurfaceTerrainColumns = new();

    public RingSurfaceHeightField? SurfaceHeightField => _surfaceHeightField;
    /// <summary>Set by the Terrain3D gameplay owner only after its near surface is ready to replace proxy pixels.</summary>
    public bool SurfaceTerrainVisualReady { get; set; }

    public void AttachSurfaceHeightField(RingSurfaceHeightField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (ReferenceEquals(_surfaceHeightField, field)) return;
        _surfaceHeightField = field;
        _observedSurfaceHeightRevision = ulong.MaxValue;
        InvalidateLodSurfaceEditSnapshot();
        _rainSurfaceEditVersion++;
    }

    public void DetachSurfaceHeightField(RingSurfaceHeightField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (!ReferenceEquals(_surfaceHeightField, field)) return;
        _surfaceHeightField = null;
        _observedSurfaceHeightRevision = ulong.MaxValue;
        InvalidateLodSurfaceEditSnapshot();
        _rainSurfaceEditVersion++;
    }

    private void RefreshSurfaceTerrainRevision()
    {
        RingSurfaceHeightField? field = _surfaceHeightField;
        if (field == null || _observedSurfaceHeightRevision == field.Revision) return;
        _observedSurfaceHeightRevision = field.Revision;
        InvalidateLodSurfaceEditSnapshot();
        _rainSurfaceEditVersion++;
    }

    private RingLodSurfaceEditSnapshot CaptureSurfaceTerrainLodSnapshot()
    {
        RefreshSurfaceTerrainRevision();
        if (_cachedLodSurfaceEditSnapshot != null && _cachedLodSurfaceEditSnapshot.Version == _lodSurfaceEditVersion)
            return _cachedLodSurfaceEditSnapshot;
        EnsureTerrainSampler();
        RingSurfaceHeightField? field = _surfaceHeightField;
        long aroundLength = GetAroundRingVoxelLength();
        var heights = new Dictionary<RingTerrainColumnCoordinate, int>();
        if (field == null || _terrainSampler == null)
            return _cachedLodSurfaceEditSnapshot = new RingLodSurfaceEditSnapshot(_lodSurfaceEditVersion, aroundLength, heights);

        // A 2m height lattice can affect the adjacent 1m proxy columns. Copy the
        // resulting absolute values now; worker snapshots never call the mutable field.
        foreach (RingSurfaceHeightEdit edit in field.GetEditsInWindow(0, field.CircumferenceMeters - (long)field.SpacingMeters, 0, field.WidthMeters - (int)field.SpacingMeters))
        {
            for (long around = edit.AroundMeters - 1; around <= edit.AroundMeters + 1; around++)
            for (int across = Math.Max(0, edit.AcrossMeters - 1); across <= Math.Min(field.WidthMeters - (int)field.SpacingMeters, edit.AcrossMeters + 1); across++)
            {
                float height = field.SampleHeight(around, across);
                int surfaceHeight = checked((int)Math.Round(height - .5f));
                long canonicalAround = CanonicalizeSurfaceAround(around, field.CircumferenceMeters);
                int baseline = _terrainSampler.GetSurfaceHeight(canonicalAround, across);
                if (surfaceHeight != baseline) heights[new RingTerrainColumnCoordinate(canonicalAround, across)] = surfaceHeight;
            }
        }
        var dirtyColumns = new HashSet<RingTerrainColumnCoordinate>(_previousSurfaceTerrainColumns);
        foreach (RingTerrainColumnCoordinate column in heights.Keys) dirtyColumns.Add(column);
        _previousSurfaceTerrainColumns.Clear();
        foreach (RingTerrainColumnCoordinate column in heights.Keys) _previousSurfaceTerrainColumns.Add(column);
        return _cachedLodSurfaceEditSnapshot = new RingLodSurfaceEditSnapshot(_lodSurfaceEditVersion, aroundLength, heights, dirtyColumns);
    }

    private static long CanonicalizeSurfaceAround(long around, long circumference)
    {
        long canonical = around % circumference;
        return canonical < 0 ? canonical + circumference : canonical;
    }
}
