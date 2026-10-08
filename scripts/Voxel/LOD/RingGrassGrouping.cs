using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

internal readonly record struct RingGrassGroupKey(long Around, long Across, int Level)
{
    internal int Factor => 1 << Level;
    internal static RingGrassGroupKey FromLeaf(RingGrassTileKey leaf, int level) =>
        new(FloorDivide(leaf.Around, 1 << level), FloorDivide(leaf.Across, 1 << level), level);
    private static long FloorDivide(long value, long divisor) => value >= 0 ? value / divisor : (value - divisor + 1) / divisor;
    internal RingGrassTileKey Leaf(int around, int across) => new(Around * Factor + around, Across * Factor + across);
}

/// <summary>Groups immutable leaf sources without changing their placement or leaf-local LOD order.</summary>
internal static class RingGrassGrouping
{
    internal static RingGrassTileData Describe(IReadOnlyList<RingGrassTileData> sources, in RingLodGeometrySnapshot geometry)
    {
        RingGrassTileData first = sources[0], last = sources[^1];
        var bounds = new RingGrassTileBounds(first.Bounds.AroundMin, last.Bounds.AroundMax, first.Bounds.AcrossMin, last.Bounds.AcrossMax);
        RingLodGeometrySnapshot centered = geometry.CenteredAt(bounds.AroundCenter, bounds.AcrossCenter);
        Transform3D inverse = RingGrassCoordinates.CreateTileWorldTransform(bounds, first.AnchorHeight, centered).AffineInverse();
        Aabb union = default; bool initialized = false;
        foreach (var leaf in sources)
        {
            Aabb transformed = (inverse * RingGrassCoordinates.CreateTileWorldTransform(leaf.Bounds, leaf.AnchorHeight, centered)) * leaf.LocalAabb;
            union = initialized ? union.Merge(transformed) : transformed; initialized = true;
        }
        return new RingGrassTileData(first.Key, bounds, first.AnchorHeight, Array.Empty<float>(), union.Grow(.01f), first.PlacementHash,
            first.SourceFraction, first.SourceTier, first.BladesPerSource);
    }
    internal static bool Compatible(IReadOnlyList<RingGrassTileData> sources, int factor, int maximumRoots)
    {
        if (sources.Count != factor * factor || sources.Count == 0) return false;
        RingGrassTileData first = sources[0];
        long count = 0;
        double aroundSpan = first.Bounds.AroundMax - first.Bounds.AroundMin;
        double acrossSpan = first.Bounds.AcrossMax - first.Bounds.AcrossMin;
        foreach (RingGrassTileData leaf in sources)
        {
            // Partial seam/edge cells and independently sampled tint maps retain their fine outputs.
            if (leaf.SourceTier != first.SourceTier || leaf.SourceFraction != first.SourceFraction
                || leaf.BladesPerSource != first.BladesPerSource || leaf.ModifierTintBytes != null
                || Math.Abs(leaf.Bounds.AroundMax - leaf.Bounds.AroundMin - aroundSpan) > .001
                || Math.Abs(leaf.Bounds.AcrossMax - leaf.Bounds.AcrossMin - acrossSpan) > .001) return false;
            count += leaf.InstanceCount;
        }
        return count > 0 && count <= maximumRoots;
    }

    internal static RingGrassTileData Build(IReadOnlyList<RingGrassTileData> sources, in RingLodGeometrySnapshot geometry,
        out Vector4[] metadata)
    {
        RingGrassTileData first = sources[0];
        double aroundMin = first.Bounds.AroundMin, aroundMax = first.Bounds.AroundMax;
        double acrossMin = first.Bounds.AcrossMin, acrossMax = first.Bounds.AcrossMax;
        int count = 0;
        foreach (RingGrassTileData leaf in sources)
        {
            aroundMin = Math.Min(aroundMin, leaf.Bounds.AroundMin); aroundMax = Math.Max(aroundMax, leaf.Bounds.AroundMax);
            acrossMin = Math.Min(acrossMin, leaf.Bounds.AcrossMin); acrossMax = Math.Max(acrossMax, leaf.Bounds.AcrossMax);
            count = checked(count + leaf.InstanceCount);
        }
        var bounds = new RingGrassTileBounds(aroundMin, aroundMax, acrossMin, acrossMax);
        // Center before the float conversions. A world-frame subtraction at large ring radii loses root precision.
        RingLodGeometrySnapshot localGeometry = geometry.CenteredAt(bounds.AroundCenter, bounds.AcrossCenter);
        Transform3D group = RingGrassCoordinates.CreateTileWorldTransform(bounds, first.AnchorHeight, localGeometry);
        Transform3D inverse = group.AffineInverse();
        float[] rows = new float[count * RingGrassTileData.FloatsPerInstance];
        metadata = new Vector4[count];
        Aabb union = default;
        bool initialized = false;
        int destination = 0;
        foreach (RingGrassTileData leaf in sources)
        {
            Transform3D rebase = inverse * RingGrassCoordinates.CreateTileWorldTransform(leaf.Bounds, leaf.AnchorHeight, localGeometry);
            Aabb leafBounds = rebase * leaf.LocalAabb;
            union = initialized ? union.Merge(leafBounds) : leafBounds; initialized = true;
            for (int id = 0; id < leaf.InstanceCount; id++, destination++)
            {
                int input = id * 20, output = destination * 20;
                Array.Copy(leaf.Buffer, input, rows, output, 20);
                Transform3D transform = ReadTransform(leaf.Buffer, input);
                WriteTransform(rows, output, rebase * transform);
                // The shader hashes the original ID rather than the concatenated ID. custom.xy and wind anchors stay untouched.
                metadata[destination] = new Vector4((id + .5f) / leaf.InstanceCount, id, 1, 0);
            }
        }
        return new RingGrassTileData(first.Key, bounds, first.AnchorHeight, rows, union.Grow(.01f),
            first.PlacementHash, first.SourceFraction, first.SourceTier, first.BladesPerSource);
    }

    internal static Transform3D ReadTransform(float[] rows, int offset) => new(new Basis(
        new Vector3(rows[offset], rows[offset + 4], rows[offset + 8]),
        new Vector3(rows[offset + 1], rows[offset + 5], rows[offset + 9]),
        new Vector3(rows[offset + 2], rows[offset + 6], rows[offset + 10])),
        new Vector3(rows[offset + 3], rows[offset + 7], rows[offset + 11]));

    internal static void WriteTransform(float[] rows, int offset, Transform3D transform)
    {
        rows[offset] = transform.Basis.X.X; rows[offset + 1] = transform.Basis.Y.X; rows[offset + 2] = transform.Basis.Z.X; rows[offset + 3] = transform.Origin.X;
        rows[offset + 4] = transform.Basis.X.Y; rows[offset + 5] = transform.Basis.Y.Y; rows[offset + 6] = transform.Basis.Z.Y; rows[offset + 7] = transform.Origin.Y;
        rows[offset + 8] = transform.Basis.X.Z; rows[offset + 9] = transform.Basis.Y.Z; rows[offset + 10] = transform.Basis.Z.Z; rows[offset + 11] = transform.Origin.Z;
    }
}
