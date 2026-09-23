using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>A mesh-authoritative root query for standing grass.  It samples only
/// the small, one metre regular cells that can contain the requested column.</summary>
public readonly record struct RingGrassSurfaceHit(
    double Height, Vector3 CanonicalNormal, SmoothTerrainMaterial Material);

public sealed partial class RingGrassSurfaceResolver
{
    private const int SearchAboveHintMeters = 3;
    private const int SearchBelowHintMeters = 160;
    private const int MaximumSearchCells = 256;
    private const int ExteriorAirProbeMeters = 3;
    private const int MaximumCachedSamples = 4096;
    private static readonly (int X, int Y, int Z)[] Corners =
    {
        (0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0),
        (0, 0, 1), (1, 0, 1), (0, 1, 1), (1, 1, 1)
    };

    private readonly SmoothTerrainVolumetricDensitySnapshot _field;
    private readonly RingLodSurfaceEditSnapshot _hints;
    private readonly RingTerrainSampler _sampler;
    private readonly CancellationToken _cancellation;
    private readonly RingGrassSurfaceCache? _surfaceCache;
    private readonly Dictionary<SampleKey, SmoothTerrainSample> _samples = new();

    private readonly record struct SampleKey(long Around, int Vertical, int Across);
    private enum CellResult { None, Hit, Blocked }

    public RingGrassSurfaceResolver(SmoothTerrainVolumetricDensitySnapshot field,
        RingLodSurfaceEditSnapshot hints, RingTerrainSampler sampler,
        CancellationToken cancellation = default)
        : this(field, hints, sampler, cancellation, null)
    {
    }

    internal RingGrassSurfaceResolver(SmoothTerrainVolumetricDensitySnapshot field,
        RingLodSurfaceEditSnapshot hints, RingTerrainSampler sampler,
        CancellationToken cancellation, RingGrassSurfaceCache? surfaceCache)
    {
        _field = field ?? throw new ArgumentNullException(nameof(field));
        _hints = hints ?? throw new ArgumentNullException(nameof(hints));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _cancellation = cancellation;
        _surfaceCache = surfaceCache;
    }

    /// <summary>Finds the first regular-cell triangle encountered from the
    /// exterior.  A non-soil hit is deliberately returned so the caller can
    /// reject it without searching down to a cave roof or buried soil.</summary>
    public bool TryResolve(double around, double across, out RingGrassSurfaceHit hit)
    {
        _cancellation.ThrowIfCancellationRequested();
        hit = default;
        if (!double.IsFinite(around) || !double.IsFinite(across)) return false;
        int width = Math.Max(1, _sampler.RingAcrossWidthMeters);
        if (across < 0.0 || across >= width - 1.0) return false;

        if (around < long.MinValue || around > long.MaxValue || across < int.MinValue || across > int.MaxValue) return false;
        long aroundFloor = (long)Math.Floor(around);
        int acrossFloor = (int)Math.Floor(across);
        long circumference = Math.Max(1L, _field.Identity.AroundLength);
        long canonicalAround = Wrap(aroundFloor, circumference);
        // A cache hit is only a known regular-cell vertical. Reconstructing the
        // cell at the exact requested fractional point retains mesh parity; a
        // failed direct probe deliberately falls back to the full authority.
        if (_surfaceCache != null && _surfaceCache.TryGet(_field.Identity, _hints.Version, canonicalAround, acrossFloor, out int cachedVertical))
        {
            CellResult cached = TryResolveCell(canonicalAround, cachedVertical, acrossFloor,
                around - aroundFloor, across - acrossFloor, out hit);
            if (cached == CellResult.Hit) return true;
        }
        int hintLow = int.MaxValue, hintHigh = int.MinValue;
        for (int z = 0; z <= 1; z++)
        for (int x = 0; x <= 1; x++)
        {
            int height = _hints.GetSurfaceHeight(_sampler, Wrap(canonicalAround + x, circumference), acrossFloor + z);
            hintLow = Math.Min(hintLow, height);
            hintHigh = Math.Max(hintHigh, height);
        }
        if (hintLow == int.MaxValue) return false;

        // These are only bounds.  The field and regular-cell tables below are
        // the authority for both height and normal.
        int top, bottom;
        try
        {
            top = checked(hintHigh + SearchAboveHintMeters);
            bottom = Math.Max(checked(hintLow - SearchBelowHintMeters), checked(top - (MaximumSearchCells - 1)));
        }
        catch (OverflowException) { return false; }
        if (top > int.MaxValue - ExteriorAirProbeMeters - 2) return false;
        // The bound must begin in exterior air. If stale/misleading hints place
        // it in solid or in an unknown interior, fail closed rather than walk
        // down to a cave floor.
        if (!IsExteriorAir(canonicalAround, top, acrossFloor)) return false;
        for (int step = 0; step < MaximumSearchCells && top - (long)step >= bottom; step++)
        {
            _cancellation.ThrowIfCancellationRequested();
            int y = (int)(top - (long)step);
            CellResult result = TryResolveCell(canonicalAround, y, acrossFloor,
                around - aroundFloor, across - acrossFloor, out hit);
            if (result == CellResult.Blocked) return false;
            if (result == CellResult.Hit)
            {
                _surfaceCache?.Store(_field.Identity, _hints.Version, canonicalAround, acrossFloor, y);
                return true;
            }
        }
        return false;
    }

    private CellResult TryResolveCell(long around, int vertical, int across, double localAround, double localAcross,
        out RingGrassSurfaceHit hit)
    {
        hit = default;
        Span<SmoothTerrainSample> cornerSamples = stackalloc SmoothTerrainSample[8];
        int code = 0;
        for (int index = 0; index < 8; index++)
        {
            var corner = Corners[index];
            SmoothTerrainSample sample = Sample(around + corner.X, vertical + corner.Y, across + corner.Z);
            cornerSamples[index] = sample;
            if (sample.Density < 0.0f) code |= 1 << index;
        }
        if (code is 0 or 255) return CellResult.None;
        int shape = SmoothTerrainRegularCellTables.Class[code];
        int geometry = SmoothTerrainRegularCellTables.Geometry[shape];
        int vertexCount = geometry >> 4, triangleCount = geometry & 15;
        Span<Vector3> vertices = stackalloc Vector3[12];
        Span<SmoothTerrainMaterial> materials = stackalloc SmoothTerrainMaterial[12];
        for (int index = 0; index < vertexCount; index++)
        {
            ushort edge = SmoothTerrainRegularCellTables.Vertex[code * 12 + index];
            int a = (edge & 255) >> 4, b = (edge & 255) & 15;
            float da = cornerSamples[a].Density, db = cornerSamples[b].Density;
            float t = da / (da - db);
            var pa = Corners[a]; var pb = Corners[b];
            vertices[index] = new Vector3(pa.X + (pb.X - pa.X) * t, pa.Y + (pb.Y - pa.Y) * t, pa.Z + (pb.Z - pa.Z) * t);
            materials[index] = (SmoothTerrainMaterial)(da < 0.0f ? cornerSamples[a].Material : cornerSamples[b].Material);
        }
        Vector2 point = new((float)localAround, (float)localAcross);
        bool found = false;
        float bestHeight = float.NegativeInfinity;
        Vector3 bestNormal = default;
        SmoothTerrainMaterial bestMaterial = default;
        Vector3 bestA = default, bestB = default, bestC = default;
        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            int offset = shape * 15 + triangle * 3;
            Vector3 a = vertices[SmoothTerrainRegularCellTables.Indices[offset]];
            Vector3 b = vertices[SmoothTerrainRegularCellTables.Indices[offset + 1]];
            Vector3 c = vertices[SmoothTerrainRegularCellTables.Indices[offset + 2]];
            if (!TryHeightOnTriangle(point, a, b, c, out float height)) continue;
            if (found && height <= bestHeight) continue;
            found = true;
            bestHeight = height;
            bestMaterial = TriangleMaterial(materials, SmoothTerrainRegularCellTables.Indices[offset], SmoothTerrainRegularCellTables.Indices[offset + 1], SmoothTerrainRegularCellTables.Indices[offset + 2]);
            bestA = a; bestB = b; bestC = c;
        }
        if (!found) return CellResult.None;
        bestNormal = (bestB - bestA).Cross(bestC - bestA);
        if (bestNormal.LengthSquared() <= 1e-12f) return CellResult.Blocked;
        bestNormal = bestNormal.Normalized();
        Vector3 fieldGradient = Gradient(cornerSamples, new Vector3(point.X, bestHeight, point.Y));
        if (fieldGradient.LengthSquared() <= 1e-12f) return CellResult.Blocked;
        if (bestNormal.Dot(fieldGradient) < 0.0f) bestNormal = -bestNormal;
        // This is the signed-field outward direction. A negative vertical
        // component is a cave ceiling/underside, not exterior ground.
        if (bestNormal.Y <= 1e-5f) return CellResult.Blocked;
        hit = new RingGrassSurfaceHit(vertical + bestHeight, bestNormal, bestMaterial);
        return CellResult.Hit;
    }

    private bool IsExteriorAir(long around, int top, int across)
    {
        for (int y = 0; y <= ExteriorAirProbeMeters; y++)
        for (int z = 0; z <= 1; z++) for (int x = 0; x <= 1; x++)
            if (Sample(around + x, checked(top + y), across + z).Density < 0.0f) return false;
        return true;
    }

    private static Vector3 Gradient(Span<SmoothTerrainSample> samples, Vector3 p)
    {
        float tx = p.X, ty = p.Y, tz = p.Z;
        Vector3 gradient = new(
            Difference(samples, 1, 0, 0, 0, 0, 0) * (1 - ty) * (1 - tz) + Difference(samples, 1, 1, 0, 0, 1, 0) * ty * (1 - tz) + Difference(samples, 1, 0, 1, 0, 0, 1) * (1 - ty) * tz + Difference(samples, 1, 1, 1, 0, 1, 1) * ty * tz,
            Difference(samples, 0, 1, 0, 0, 0, 0) * (1 - tx) * (1 - tz) + Difference(samples, 1, 1, 0, 1, 0, 0) * tx * (1 - tz) + Difference(samples, 0, 1, 1, 0, 0, 1) * (1 - tx) * tz + Difference(samples, 1, 1, 1, 1, 0, 1) * tx * tz,
            Difference(samples, 0, 0, 1, 0, 0, 0) * (1 - tx) * (1 - ty) + Difference(samples, 1, 0, 1, 1, 0, 0) * tx * (1 - ty) + Difference(samples, 0, 1, 1, 0, 1, 0) * (1 - tx) * ty + Difference(samples, 1, 1, 1, 1, 1, 0) * tx * ty);
        return gradient.Normalized();
    }

    private static float Difference(ReadOnlySpan<SmoothTerrainSample> samples, int ax, int ay, int az, int bx, int by, int bz)
        => samples[ax + ay * 2 + az * 4].Density - samples[bx + by * 2 + bz * 4].Density;

    private static SmoothTerrainMaterial TriangleMaterial(Span<SmoothTerrainMaterial> materials, int a, int b, int c)
    {
        SmoothTerrainMaterial ma = materials[a], mb = materials[b], mc = materials[c];
        if (ma == SmoothTerrainMaterial.Soil && mb == SmoothTerrainMaterial.Soil && mc == SmoothTerrainMaterial.Soil) return SmoothTerrainMaterial.Soil;
        if (ma == SmoothTerrainMaterial.Bedrock || mb == SmoothTerrainMaterial.Bedrock || mc == SmoothTerrainMaterial.Bedrock) return SmoothTerrainMaterial.Bedrock;
        return SmoothTerrainMaterial.Rock;
    }

    private SmoothTerrainSample Sample(long around, int vertical, int across)
    {
        long canonicalAround = Wrap(around, _field.Identity.AroundLength);
        var key = new SampleKey(canonicalAround, vertical, across);
        if (_samples.TryGetValue(key, out SmoothTerrainSample sample)) return sample;
        if (_samples.Count >= MaximumCachedSamples) _samples.Clear();
        sample = _field.Sample(canonicalAround, vertical, across);
        _samples.Add(key, sample);
        return sample;
    }

    private static bool TryHeightOnTriangle(Vector2 point, Vector3 a, Vector3 b, Vector3 c, out float height)
    {
        float denominator = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(denominator) <= 1e-7f) { height = 0; return false; }
        float u = ((b.Z - c.Z) * (point.X - c.X) + (c.X - b.X) * (point.Y - c.Z)) / denominator;
        float v = ((c.Z - a.Z) * (point.X - c.X) + (a.X - c.X) * (point.Y - c.Z)) / denominator;
        float w = 1.0f - u - v;
        const float epsilon = 1e-5f;
        if (u < -epsilon || v < -epsilon || w < -epsilon) { height = 0; return false; }
        height = a.Y * u + b.Y * v + c.Y * w;
        return true;
    }

    private static long Wrap(long value, long length)
    {
        long result = value % length;
        return result < 0 ? result + length : result;
    }
}
