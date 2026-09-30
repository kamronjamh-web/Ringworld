using System;
using Godot;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Immutable, canonical description of the terrain triangles currently
/// published by <see cref="RingTerrainGlobalLod"/>. It is deliberately free of
/// Godot nodes so proxy workers can use it without touching the scene tree.
/// </summary>
public sealed class RingTerrainGlobalMorphSnapshot
{
    public readonly record struct Identity(
        ulong Generation,
        ulong EditVersion,
        ulong TerrainDefinitionHash,
        int AroundSegments,
        int AcrossSegments,
        int GlobalSegmentCount,
        int RingWidthMeters,
        float UnderlayMeters,
        ulong TopologyAndHeightHash);

    private readonly Segment[] _segments;
    private readonly double _circumferenceMeters;

    public Identity SnapshotIdentity { get; }
    public int SegmentCount => _segments.Length;
    internal Segment[] CopySegments() => (Segment[])_segments.Clone();

    internal RingTerrainGlobalMorphSnapshot(
        Identity identity,
        double circumferenceMeters,
        Segment[] segments)
    {
        SnapshotIdentity = identity;
        _circumferenceMeters = circumferenceMeters;
        _segments = segments;
    }

    /// <summary>
    /// Samples the exact triangle plane used by the receiver mesh. The method
    /// bends each stored vertex first, then barycentrically interpolates the
    /// receiver's 01-to-10 diagonal; it never interpolates height and bends
    /// afterwards.
    /// </summary>
    public bool TrySampleTerrainWorld(
        RingLodGeometrySnapshot workerGeometry,
        double aroundMeters,
        double acrossMeters,
        out Vector3 worldPosition)
    {
        worldPosition = default;
        if (_segments.Length == 0 || acrossMeters < 0.0 || acrossMeters > SnapshotIdentity.RingWidthMeters)
            return false;

        double wrappedAround = WrapAround(aroundMeters, _circumferenceMeters);
        Segment? segment = FindSegment(wrappedAround);
        if (segment == null) return false;
        return segment.TrySample(workerGeometry, wrappedAround, acrossMeters, out worldPosition);
    }

    private Segment? FindSegment(double aroundMeters)
    {
        // Segment count is small and this runs only while a proxy worker is
        // building. Linear selection keeps the snapshot compact and avoids a
        // second mutable lookup structure.
        foreach (Segment segment in _segments)
            if (aroundMeters >= segment.AroundStartMeters && aroundMeters <= segment.AroundEndMeters)
                return segment;
        return _segments[^1]; // circumference endpoint belongs to the last segment.
    }

    private static double WrapAround(double value, double circumference)
    {
        if (circumference <= 0.0) return value;
        double wrapped = value % circumference;
        return wrapped < 0.0 ? wrapped + circumference : wrapped;
    }

    internal sealed class Segment
    {
        private readonly float[] _canonicalHeights;
        private readonly int _localAroundCount;
        private readonly int _acrossCount;
        private readonly double _aroundSpacing;
        private readonly double _acrossSpacing;

        public double AroundStartMeters { get; }
        public double AroundEndMeters => AroundStartMeters + _localAroundCount * _aroundSpacing;
        internal int LocalAroundCount => _localAroundCount;
        internal int AcrossCount => _acrossCount;
        internal double AroundSpacing => _aroundSpacing;
        internal double AcrossSpacing => _acrossSpacing;
        internal float[] CopyCanonicalHeights() => (float[])_canonicalHeights.Clone();

        internal Segment(
            int aroundStartIndex,
            int localAroundCount,
            int acrossCount,
            double aroundSpacing,
            double acrossSpacing,
            float[] canonicalHeights)
        {
            _localAroundCount = localAroundCount;
            _acrossCount = acrossCount;
            _aroundSpacing = aroundSpacing;
            _acrossSpacing = acrossSpacing;
            _canonicalHeights = canonicalHeights;
            AroundStartMeters = aroundStartIndex * aroundSpacing;
        }

        internal bool TrySample(RingLodGeometrySnapshot workerGeometry, double aroundMeters, double acrossMeters, out Vector3 worldPosition)
        {
            worldPosition = default;
            if (_canonicalHeights.Length != (_localAroundCount + 1) * (_acrossCount + 1)
                || _localAroundCount <= 0 || _acrossCount <= 0)
                return false;

            double localAround = Math.Clamp(aroundMeters - AroundStartMeters, 0.0, _localAroundCount * _aroundSpacing);
            double localAcross = Math.Clamp(acrossMeters, 0.0, _acrossCount * _acrossSpacing);
            int x = Math.Min(_localAroundCount - 1, Math.Max(0, (int)Math.Floor(localAround / _aroundSpacing)));
            int z = Math.Min(_acrossCount - 1, Math.Max(0, (int)Math.Floor(localAcross / _acrossSpacing)));
            float tx = (float)((localAround - x * _aroundSpacing) / _aroundSpacing);
            float tz = (float)((localAcross - z * _acrossSpacing) / _acrossSpacing);
            if (x == _localAroundCount - 1 && Mathf.IsEqualApprox(tx, 1.0f)) tx = 1.0f;
            if (z == _acrossCount - 1 && Mathf.IsEqualApprox(tz, 1.0f)) tz = 1.0f;

            int row = _localAroundCount + 1;
            int i00 = z * row + x;
            Vector3 p00 = Vertex(workerGeometry, x, z, i00);
            Vector3 p10 = Vertex(workerGeometry, x + 1, z, i00 + 1);
            Vector3 p01 = Vertex(workerGeometry, x, z + 1, i00 + row);
            Vector3 p11 = Vertex(workerGeometry, x + 1, z + 1, i00 + row + 1);
            worldPosition = tx + tz <= 1.0f
                ? p00 * (1.0f - tx - tz) + p10 * tx + p01 * tz
                : p11 * (tx + tz - 1.0f) + p01 * (1.0f - tx) + p10 * (1.0f - tz);
            return true;
        }

        private Vector3 Vertex(RingLodGeometrySnapshot workerGeometry, int x, int z, int index)
        {
            double around = AroundStartMeters + x * _aroundSpacing;
            double across = z * _acrossSpacing;
            return workerGeometry.CanonicalToWorld(around, _canonicalHeights[index], across);
        }
    }
}
