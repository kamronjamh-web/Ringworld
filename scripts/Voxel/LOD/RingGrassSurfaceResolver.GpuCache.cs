using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Editable;

namespace RingworldTechDemo.Voxel.LOD;

internal sealed class RingGrassGpuQueryCache
{
    internal RingGrassGpuQueryCache(float[] queries, float[] cells, int uniqueColumnCount)
    { Queries = queries; Cells = cells; UniqueColumnCount = uniqueColumnCount; }
    internal float[] Queries { get; }
    internal float[] Cells { get; }
    // Dispatch count is the number of candidate queries, not unique columns.
    internal int ColumnCount => Queries.Length / 4;
    internal int UniqueColumnCount { get; }
}

public sealed partial class RingGrassSurfaceResolver
{
    /// <summary>Queries are local x/z plus a shared cell range. Cells are 64 floats:
    /// vertical, triangle count, eight densities, padding, then five 10-float triangles.</summary>
    internal RingGrassGpuQueryCache CaptureGpuQueryCache(
        IReadOnlyList<(double Around, double Across)> queries, int maximumBytes = 16 * 1024 * 1024)
    {
        if (queries is null || queries.Count > 100_000 || maximumBytes <= 0)
            throw new NotSupportedException("Unsupported GPU cache bounds.");
        int queryBytes = checked(queries.Count * 4 * sizeof(float));
        if (queryBytes > maximumBytes) throw new NotSupportedException("Query data exceeds cache bound.");
        var result = new float[checked(queries.Count * 4)];
        var cells = new List<float>();
        var columns = new Dictionary<(long Around, int Across), List<int>>();
        int width = Math.Max(1, _sampler.RingAcrossWidthMeters);
        long circumference = Math.Max(1L, _field.Identity.AroundLength);
        for (int i = 0; i < queries.Count; i++)
        {
            _cancellation.ThrowIfCancellationRequested();
            (double around, double across) = queries[i];
            if (!double.IsFinite(around) || !double.IsFinite(across)
                || around < long.MinValue || around >= 9223372036854775808.0
                || across < int.MinValue || across > int.MaxValue) continue;
            long aroundFloor = (long)Math.Floor(around);
            int acrossFloor = (int)Math.Floor(across);
            if (across < 0 || across >= width - 1.0) continue;
            long canonical = Wrap(aroundFloor, circumference);
            result[i * 4] = (float)(around - aroundFloor);
            result[i * 4 + 1] = (float)(across - acrossFloor);
            if (!columns.TryGetValue((canonical, acrossFloor), out List<int>? indices))
                columns.Add((canonical, acrossFloor), indices = new List<int>());
            indices.Add(i);
        }
        foreach (KeyValuePair<(long Around, int Across), List<int>> pair in columns)
        {
            long around = pair.Key.Around;
            int across = pair.Key.Across;
            List<int> indices = pair.Value;
            (int first, int count) = PackColumn(around, across, indices, result, cells, maximumBytes - queryBytes);
            foreach (int index in indices) { result[index * 4 + 2] = first; result[index * 4 + 3] = count; }
        }
        return new RingGrassGpuQueryCache(result, cells.ToArray(), columns.Count);
    }

    private (int First, int Count) PackColumn(long around, int across, IReadOnlyList<int> queryIndices,
        float[] queries, List<float> cells, int cellBytes)
    {
        int low = int.MaxValue, high = int.MinValue;
        long circumference = Math.Max(1L, _field.Identity.AroundLength);
        for (int z = 0; z <= 1; z++) for (int x = 0; x <= 1; x++)
        { int h = _hints.GetSurfaceHeight(_sampler, Wrap(around + x, circumference), across + z); low = Math.Min(low, h); high = Math.Max(high, h); }
        if (low == int.MaxValue) return (0, 0);
        int top; int bottom;
        try { top = checked(high + SearchAboveHintMeters); bottom = Math.Max(checked(low - SearchBelowHintMeters), checked(top - (MaximumSearchCells - 1))); }
        catch (OverflowException) { return (0, 0); }
        if (top > int.MaxValue - ExteriorAirProbeMeters - 2 || !IsExteriorAir(around, top, across)) return (0, 0);
        int first = cells.Count / 64, count = 0;
        Span<SmoothTerrainSample> samples = stackalloc SmoothTerrainSample[8];
        Span<Vector3> vertices = stackalloc Vector3[12];
        Span<SmoothTerrainMaterial> materials = stackalloc SmoothTerrainMaterial[12];
        Span<float> packed = stackalloc float[64];
        bool[] unresolved = new bool[queryIndices.Count];
        Array.Fill(unresolved, true);
        int unresolvedCount = queryIndices.Count;
        for (int step = 0; step < MaximumSearchCells && top - (long)step >= bottom; step++)
        {
            _cancellation.ThrowIfCancellationRequested();
            int y = (int)(top - (long)step);
            int code = 0;
            for (int i = 0; i < 8; i++)
            {
                var c = Corners[i];
                samples[i] = Sample(around + c.X, y + c.Y, across + c.Z);
                if (!float.IsFinite(samples[i].Density))
                    throw new NotSupportedException("Nonfinite terrain density in GPU cache.");
                if (samples[i].Density < 0) code |= 1 << i;
            }
            if (code is 0 or 255) continue;
            if (Math.Abs((long)y) > 16_777_216)
                throw new NotSupportedException("Vertical cell exceeds exact GPU integer range.");
            if (checked((cells.Count + 64) * sizeof(float)) > cellBytes)
                throw new NotSupportedException("Cell cache exceeds bound.");
            int shape = SmoothTerrainRegularCellTables.Class[code];
            int geometry = SmoothTerrainRegularCellTables.Geometry[shape];
            int vertexCount = geometry >> 4, triangleCount = geometry & 15;
            if (vertexCount > 12 || triangleCount > 5)
                throw new NotSupportedException("Unsupported regular-cell table layout.");
            for (int i = 0; i < vertexCount; i++)
            {
                ushort edge = SmoothTerrainRegularCellTables.Vertex[code * 12 + i];
                int a = (edge & 255) >> 4, b = (edge & 255) & 15;
                float da = samples[a].Density, db = samples[b].Density;
                float t = da / (da - db);
                var pa = Corners[a]; var pb = Corners[b];
                vertices[i] = new Vector3(pa.X + (pb.X - pa.X) * t,
                    pa.Y + (pb.Y - pa.Y) * t, pa.Z + (pb.Z - pa.Z) * t);
                if (!vertices[i].IsFinite()) throw new NotSupportedException("Nonfinite cached triangle.");
                materials[i] = (SmoothTerrainMaterial)(da < 0 ? samples[a].Material : samples[b].Material);
            }
            packed.Clear();
            packed[0] = y; packed[1] = triangleCount;
            for (int i = 0; i < 8; i++) packed[2 + i] = samples[i].Density;
            for (int triangle = 0; triangle < triangleCount; triangle++)
            {
                int indices = shape * 15 + triangle * 3;
                int a = SmoothTerrainRegularCellTables.Indices[indices];
                int b = SmoothTerrainRegularCellTables.Indices[indices + 1];
                int c = SmoothTerrainRegularCellTables.Indices[indices + 2];
                int offset = 12 + triangle * 10;
                StoreVertex(packed, offset, vertices[a]);
                StoreVertex(packed, offset + 3, vertices[b]);
                StoreVertex(packed, offset + 6, vertices[c]);
                packed[offset + 9] = (float)TriangleMaterial(materials, a, b, c);
            }
            for (int i = 0; i < packed.Length; i++) cells.Add(packed[i]);
            count++;
            // The authoritative resolver stops at the first mixed cell whose
            // triangles cover the query projection, including a later-blocked
            // normal/material. Once every query in this 1 m column reaches
            // that point, lower cells cannot affect any GPU or CPU-cache hit.
            for (int query = 0; query < queryIndices.Count; query++)
            {
                if (!unresolved[query]) continue;
                int offset = queryIndices[query] * 4;
                Vector2 point = new(queries[offset], queries[offset + 1]);
                for (int triangle = 0; triangle < triangleCount; triangle++)
                {
                    int indices = shape * 15 + triangle * 3;
                    if (!TryHeightOnTriangle(point, vertices[SmoothTerrainRegularCellTables.Indices[indices]],
                        vertices[SmoothTerrainRegularCellTables.Indices[indices + 1]],
                        vertices[SmoothTerrainRegularCellTables.Indices[indices + 2]], out _)) continue;
                    unresolved[query] = false;
                    unresolvedCount--;
                    break;
                }
            }
            if (unresolvedCount == 0) break;
        }
        return (first, count);
    }

    private static void StoreVertex(Span<float> target, int offset, Vector3 vertex)
    { target[offset] = vertex.X; target[offset + 1] = vertex.Y; target[offset + 2] = vertex.Z; }

    /// <summary>Reference evaluator using the same packed geometry as the GPU.
    /// This separates gains from caching from gains due to GPU execution.</summary>
    internal static RingGrassSurfaceHit?[] ResolveGpuQueryCacheOnCpu(
        RingGrassGpuQueryCache cache, CancellationToken cancellation = default)
    {
        var hits = new RingGrassSurfaceHit?[cache.ColumnCount];
        Span<SmoothTerrainSample> samples = stackalloc SmoothTerrainSample[8];
        float[] cells = cache.Cells, queries = cache.Queries;
        for (int i = 0; i < hits.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            int q = i * 4;
            Vector2 point = new(queries[q], queries[q + 1]);
            int first = (int)queries[q + 2], count = (int)queries[q + 3];
            for (int columnCell = 0; columnCell < count; columnCell++)
            {
                int start = (first + columnCell) * 64;
                int triangles = (int)cells[start + 1];
                bool found = false;
                float best = float.NegativeInfinity;
                Vector3 bestA = default, bestB = default, bestC = default;
                SmoothTerrainMaterial material = default;
                for (int triangle = 0; triangle < triangles; triangle++)
                {
                    int offset = start + 12 + triangle * 10;
                    Vector3 a = ReadVertex(cells, offset), b = ReadVertex(cells, offset + 3), c = ReadVertex(cells, offset + 6);
                    if (!TryHeightOnTriangle(point, a, b, c, out float height) || (found && height <= best)) continue;
                    found = true; best = height; bestA = a; bestB = b; bestC = c;
                    material = (SmoothTerrainMaterial)(int)cells[offset + 9];
                }
                if (!found) continue;
                Vector3 normal = (bestB - bestA).Cross(bestC - bestA);
                if (normal.LengthSquared() <= 1e-12f) break;
                normal = normal.Normalized();
                for (int corner = 0; corner < 8; corner++) samples[corner] = new SmoothTerrainSample(cells[start + 2 + corner], 0, 0);
                Vector3 gradient = Gradient(samples, new Vector3(point.X, best, point.Y));
                if (gradient.LengthSquared() <= 1e-12f) break;
                if (normal.Dot(gradient) < 0) normal = -normal;
                if (normal.Y <= 1e-5f) break;
                hits[i] = new RingGrassSurfaceHit((int)cells[start] + best, normal, material);
                break;
            }
        }
        return hits;
    }

    private static Vector3 ReadVertex(float[] values, int offset)
        => new(values[offset], values[offset + 1], values[offset + 2]);
}
