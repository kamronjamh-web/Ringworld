using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tests;

/// <summary>Focused contract checks for mesh-authoritative grass roots.</summary>
public partial class GrassSurfaceValidation : RefCounted
{
    private sealed class PlaneBaseline : ISmoothTerrainBaseline
    {
        public long AroundLength { get; }
        public int AcrossWidth { get; }
        public ulong Revision => 731;
        private readonly bool _rockStripe;
        public PlaneBaseline(long length, int width, bool rockStripe = false) { AroundLength = length; AcrossWidth = width; _rockStripe = rockStripe; }
        public SmoothTerrainSample Sample(long around, int vertical, int across)
        {
            float surface = 42.25f + (float)around * .17f - across * .11f;
            SmoothTerrainMaterial material = vertical < surface - 2.0f || (_rockStripe && around >= 11) ? SmoothTerrainMaterial.Rock : SmoothTerrainMaterial.Soil;
            return new SmoothTerrainSample(vertical - surface, (int)material, Revision);
        }
    }

    public bool Run()
    {
        const long circumference = 128;
        const int width = 64;
        var settings = new VoxelTerrainSettings();
        using var sampler = new RingTerrainSampler(settings, true, circumference, 5_000_000, width, 8000);
        var baseline = new PlaneBaseline(circumference, width);
        var noEdits = new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), 1, circumference);
        var hintColumns = new Dictionary<RingTerrainColumnCoordinate, int>();
        for (long around = 0; around < circumference; around++)
        for (int across = 0; across < width; across++)
            hintColumns[new RingTerrainColumnCoordinate(around, across)] = (int)MathF.Round(42.25f + around * .17f - across * .11f);
        var hints = new RingLodSurfaceEditSnapshot(1, circumference, hintColumns);
        var field = new SmoothTerrainVolumetricDensitySnapshot(baseline, noEdits);
        VerifyMeshParity(field, hints, sampler);
        VerifySeamAndFarCoordinates(field, hints, sampler);
        VerifyRockAndExcavation(baseline, hints, sampler, circumference);
        VerifyCaveRoofStaysExterior(baseline, hints, sampler, circumference);
        VerifyInteriorHintsFailClosed(baseline, hints, sampler, circumference, width);
        VerifyMixedMaterialRejectsSoil(hints, sampler, circumference, width);
        VerifySearchExhaustion(field, hints, sampler, circumference);
        VerifyGpuQueryCache(field, hints, sampler);
        VerifyBoundedSurfaceCache(field, hints, sampler);
        VerifyCancellation(field, hints, sampler);
        GD.Print("GRASS_SURFACE_CONTRACT=PASS mesh parity, edits, cave exterior, seam, cancellation");
        return true;
    }

    private static void VerifyMeshParity(SmoothTerrainVolumetricDensitySnapshot field, RingLodSurfaceEditSnapshot hints, RingTerrainSampler sampler)
    {
        var resolver = new RingGrassSurfaceResolver(field, hints, sampler);
        Require(resolver.TryResolve(10.37, 12.61, out RingGrassSurfaceHit hit), "tilted plane did not resolve");
        SmoothTerrainChunkSnapshot chunk = field.CaptureChunk(new SmoothTerrainChunkCoordinate(0, 1, 0));
        SmoothTerrainMeshData mesh = SmoothTerrainMesher.Build(chunk);
        Require(TryMeshHeight(mesh, new Vector2(10.37f, 12.61f), out float expected), "production mesh had no matching triangle");
        Require(MathF.Abs((float)hit.Height - (32 + expected)) < 1e-4f, "resolver height differs from regular mesh");
        Vector3 expectedNormal = new Vector3(-.17f, 1, .11f).Normalized();
        Require(hit.CanonicalNormal.DistanceTo(expectedNormal) < 1e-4f && hit.Material == SmoothTerrainMaterial.Soil, "tilted plane normal/material mismatch");
    }

    private static void VerifySeamAndFarCoordinates(SmoothTerrainVolumetricDensitySnapshot field, RingLodSurfaceEditSnapshot hints, RingTerrainSampler sampler)
    {
        var resolver = new RingGrassSurfaceResolver(field, hints, sampler);
        Require(resolver.TryResolve(-.25, 12.61, out RingGrassSurfaceHit left), "negative seam coordinate failed");
        Require(resolver.TryResolve(127.75, 12.61, out RingGrassSurfaceHit right), "positive seam coordinate failed");
        Require(Math.Abs(left.Height - right.Height) < .001, "canonical seam height changed");
        Require(resolver.TryResolve(128.37 + 128 * 1000000.0, 12.61, out _), "far canonical coordinate failed");
    }

    private static void VerifyRockAndExcavation(PlaneBaseline baseline, RingLodSurfaceEditSnapshot hints, RingTerrainSampler sampler, long length)
    {
        var rockAdd = new SmoothTerrainEditSnapshot(new[] { new SmoothTerrainEditCommand(1, 2, 2, true, 20, 48, 20, 6, (int)SmoothTerrainMaterial.Rock) }, 2, length);
        RingLodSurfaceEditSnapshot raisedHints = WithHint(hints, 20, 20, 55, length);
        var rockField = new SmoothTerrainVolumetricDensitySnapshot(baseline, rockAdd);
        var rockResolver = new RingGrassSurfaceResolver(rockField, raisedHints, sampler);
        Require(rockResolver.TryResolve(20.2, 20.2, out RingGrassSurfaceHit rock) && rock.Material == SmoothTerrainMaterial.Rock, "exposed rock was not reported");
        VerifyMeshHeight(rockField, rock, 20.2f, 20.2f, "rock fill");
        var soilAdd = new SmoothTerrainEditSnapshot(new[] { new SmoothTerrainEditCommand(1, 21, 21, true, 20, 48, 20, 6, (int)SmoothTerrainMaterial.Soil) }, 21, length);
        var soilField = new SmoothTerrainVolumetricDensitySnapshot(baseline, soilAdd);
        Require(new RingGrassSurfaceResolver(soilField, raisedHints, sampler).TryResolve(20.2, 20.2, out RingGrassSurfaceHit soil) && soil.Material == SmoothTerrainMaterial.Soil, "soil fill was not reported");
        VerifyMeshHeight(soilField, soil, 20.2f, 20.2f, "soil fill");
        var excavation = new SmoothTerrainEditSnapshot(new[] { new SmoothTerrainEditCommand(1, 3, 3, false, 30, 42, 20, 5, (int)SmoothTerrainMaterial.Soil) }, 3, length);
        var excavationField = new SmoothTerrainVolumetricDensitySnapshot(baseline, excavation);
        var excavationResolver = new RingGrassSurfaceResolver(excavationField, hints, sampler);
        Require(excavationResolver.TryResolve(30.2, 20.2, out RingGrassSurfaceHit floor) && floor.Material == SmoothTerrainMaterial.Rock, "excavation exposed soil or invented a baseline root");
        VerifyMeshHeight(excavationField, floor, 30.2f, 20.2f, "excavation");
    }

    private static void VerifyCaveRoofStaysExterior(PlaneBaseline baseline, RingLodSurfaceEditSnapshot hints, RingTerrainSampler sampler, long length)
    {
        var cave = new SmoothTerrainEditSnapshot(new[] { new SmoothTerrainEditCommand(1, 4, 4, false, 45, 37, 20, 3, (int)SmoothTerrainMaterial.Rock) }, 4, length);
        var resolver = new RingGrassSurfaceResolver(new SmoothTerrainVolumetricDensitySnapshot(baseline, cave), hints, sampler);
        Require(resolver.TryResolve(45.2, 20.2, out RingGrassSurfaceHit hit), "cave column did not resolve exterior");
        Require(hit.Height > 41 && hit.Material == SmoothTerrainMaterial.Soil, "resolver descended into a cave surface");
    }

    private static void VerifyCancellation(SmoothTerrainVolumetricDensitySnapshot field, RingLodSurfaceEditSnapshot hints, RingTerrainSampler sampler)
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        try { new RingGrassSurfaceResolver(field, hints, sampler, source.Token).TryResolve(10.2, 10.2, out _); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("cancelled grass query did not cancel");
    }

    private static void VerifyGpuQueryCache(SmoothTerrainVolumetricDensitySnapshot field, RingLodSurfaceEditSnapshot hints, RingTerrainSampler sampler)
    {
        var queries = new (double Around, double Across)[] {
            (10.2, 12.2), (10.8, 12.8), (10.2 + 128 * 1000000.0, 12.2),
            (-.25, 12.61), (127.75, 12.61), (double.NaN, 0), (0, -1), (0, 64)
        };
        var resolver = new RingGrassSurfaceResolver(field, hints, sampler);
        RingGrassGpuQueryCache cache = resolver.CaptureGpuQueryCache(queries);
        Require(cache.ColumnCount == queries.Length && cache.UniqueColumnCount == 2,
            "GPU cache did not share repeated and wrapped columns");
        Require(cache.Queries[2] == cache.Queries[6] && cache.Queries[6] == cache.Queries[10],
            "GPU cache duplicated a column's triangle range");
        RingGrassSurfaceHit?[] hits = RingGrassSurfaceResolver.ResolveGpuQueryCacheOnCpu(cache);
        for (int i = 0; i < queries.Length; i++)
        {
            bool resolved = resolver.TryResolve(queries[i].Around, queries[i].Across, out RingGrassSurfaceHit expected);
            Require(resolved == hits[i].HasValue, "GPU cache changed hit eligibility");
            if (resolved) Require(hits[i]!.Value == expected, "GPU cache changed authoritative height, normal or material");
        }
        Require(RingGrassSurfaceResolver.ResolveGpuQueryCacheOnCpu(resolver.CaptureGpuQueryCache(Array.Empty<(double, double)>())).Length == 0,
            "empty GPU cache was not empty");
        bool limited = false;
        try { resolver.CaptureGpuQueryCache(queries, queries.Length * 16); }
        catch (NotSupportedException) { limited = true; }
        Require(limited, "GPU cache omitted query storage from its byte bound");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool cancelled = false;
        try { new RingGrassSurfaceResolver(field, hints, sampler, cancellation.Token).CaptureGpuQueryCache(queries); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, "GPU cache did not honor cancellation");
        GD.Print("GRASS_GPU_CACHE_CONTRACT=PASS authoritative parity, shared columns, far coordinates, bounds, cancellation");
    }

    private static void VerifyBoundedSurfaceCache(SmoothTerrainVolumetricDensitySnapshot field, RingLodSurfaceEditSnapshot hints, RingTerrainSampler sampler)
    {
        var cache = new RingGrassSurfaceCache(RingGrassSurfaceCache.EstimatedBytesPerEntry * 2L);
        var cached = new RingGrassSurfaceResolver(field, hints, sampler, CancellationToken.None, cache);
        var direct = new RingGrassSurfaceResolver(field, hints, sampler);
        for (int i = 0; i < 8; i++)
        {
            double around = 10.15 + (i % 2) * .5, across = 12.12 + (i % 2) * .5;
            Require(direct.TryResolve(around, across, out RingGrassSurfaceHit expected), "direct cache parity query failed");
            Require(cached.TryResolve(around, across, out RingGrassSurfaceHit actual) && actual == expected,
                "surface cache changed exact regular-cell interpolation");
        }
        RingGrassSurfaceCache.Diagnostics warm = cache.CaptureDiagnostics();
        Require(warm.Hits > 0 && warm.Entries <= 2 && warm.EstimatedBytes <= warm.ByteCap, "surface cache warm/cap contract failed");
        for (int i = 0; i < 8; i++) Require(cached.TryResolve(20 + i + .2, 12.2, out _), "surface cache eviction query failed");
        RingGrassSurfaceCache.Diagnostics evicted = cache.CaptureDiagnostics();
        Require(evicted.Entries <= 2 && evicted.EstimatedBytes <= evicted.ByteCap && evicted.Evictions > 0, "surface cache exceeded byte cap");
        RingLodSurfaceEditSnapshot changedHints = WithHint(hints, 10, 12, 70, 128);
        var changed = new RingGrassSurfaceResolver(field, changedHints, sampler, CancellationToken.None, cache);
        long missesBeforeRevisionChange = cache.CaptureDiagnostics().Misses;
        Require(changed.TryResolve(10.2, 12.2, out _)
            && cache.CaptureDiagnostics().Misses > missesBeforeRevisionChange,
            "surface cache reused an entry across edit revision");
        GD.Print("GRASS_SURFACE_LRU_CACHE=PASS exact parity, byte cap, LRU eviction, revision invalidation");
    }

    private static void VerifyInteriorHintsFailClosed(PlaneBaseline baseline, RingLodSurfaceEditSnapshot hints, RingTerrainSampler sampler, long length, int width)
    {
        Require(!new RingGrassSurfaceResolver(new SmoothTerrainVolumetricDensitySnapshot(baseline, new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), 8, length)), WithHint(hints, 10, 10, 30, length), sampler).TryResolve(10.2, 10.2, out _), "starting in solid did not fail closed");
        var cave = new SmoothTerrainEditSnapshot(new[] { new SmoothTerrainEditCommand(1, 88, 88, false, 10, 37, 10, 3, (int)SmoothTerrainMaterial.Rock) }, 9, length);
        RingLodSurfaceEditSnapshot insideCave = WithHint(hints, 10, 10, 36, length);
        Require(!new RingGrassSurfaceResolver(new SmoothTerrainVolumetricDensitySnapshot(baseline, cave), insideCave, sampler).TryResolve(10.2, 10.2, out _), "cave ceiling with misleading low hints planted grass");
    }

    private static void VerifyMixedMaterialRejectsSoil(RingLodSurfaceEditSnapshot hints, RingTerrainSampler sampler, long length, int width)
    {
        var striped = new SmoothTerrainVolumetricDensitySnapshot(new PlaneBaseline(length, width, rockStripe: true), new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), 10, length));
        Require(new RingGrassSurfaceResolver(striped, hints, sampler).TryResolve(10.8, 10.2, out RingGrassSurfaceHit hit) && hit.Material == SmoothTerrainMaterial.Rock, "mixed soil/rock triangle remained grass-eligible");
    }

    private static void VerifySearchExhaustion(SmoothTerrainVolumetricDensitySnapshot field, RingLodSurfaceEditSnapshot hints, RingTerrainSampler sampler, long length)
    {
        RingLodSurfaceEditSnapshot distant = WithHint(hints, 10, 10, 1000, length);
        Require(!new RingGrassSurfaceResolver(field, distant, sampler).TryResolve(10.2, 10.2, out _), "bounded search invented support outside its maximum range");
    }

    private static RingLodSurfaceEditSnapshot WithHint(RingLodSurfaceEditSnapshot source, long around, int across, int height, long length)
    {
        var values = new Dictionary<RingTerrainColumnCoordinate, int>(source.CanonicalEditedColumns);
        for (long x = around - 1; x <= around + 1; x++) for (int z = across - 1; z <= across + 1; z++)
            values[new RingTerrainColumnCoordinate((x % length + length) % length, z)] = height;
        return new RingLodSurfaceEditSnapshot(source.Version + 1, length, values);
    }

    private static void VerifyMeshHeight(SmoothTerrainVolumetricDensitySnapshot field, RingGrassSurfaceHit hit, float around, float across, string label)
    {
        int verticalChunk = (int)MathF.Floor((float)hit.Height / SmoothTerrainChunkSnapshot.Cells);
        SmoothTerrainMeshData mesh = SmoothTerrainMesher.Build(field.CaptureChunk(new SmoothTerrainChunkCoordinate(0, verticalChunk, 0)));
        Require(TryMeshHeight(mesh, new Vector2(around, across), out float expected), label + " mesh had no matching triangle");
        Require(MathF.Abs((float)hit.Height - (verticalChunk * SmoothTerrainChunkSnapshot.Cells + expected)) < 1e-4f, label + " height differs from mesh");
    }

    private static bool TryMeshHeight(SmoothTerrainMeshData mesh, Vector2 point, out float height)
    {
        bool found = false; height = float.NegativeInfinity;
        for (int index = 0; index < mesh.Indices.Length; index += 3)
        {
            Vector3 a = mesh.Vertices[mesh.Indices[index]], b = mesh.Vertices[mesh.Indices[index + 1]], c = mesh.Vertices[mesh.Indices[index + 2]];
            float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (MathF.Abs(d) < 1e-7f) continue;
            float u = ((b.Z - c.Z) * (point.X - c.X) + (c.X - b.X) * (point.Y - c.Z)) / d;
            float v = ((c.Z - a.Z) * (point.X - c.X) + (a.X - c.X) * (point.Y - c.Z)) / d;
            float w = 1 - u - v;
            if (u < -1e-5f || v < -1e-5f || w < -1e-5f) continue;
            float candidate = a.Y * u + b.Y * v + c.Y * w;
            if (!found || candidate > height) { found = true; height = candidate; }
        }
        if (found) return true;
        height = 0; return false;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
