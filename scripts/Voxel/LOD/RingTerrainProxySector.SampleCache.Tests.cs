using System;
using System.Collections.Generic;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Test-only access to <see cref="TerrainBuildSampleCache"/>. Holds a verbatim copy of the pre-WO-C1 three-call
/// <c>Get</c> so the contract test can compare the deduplicated cache against it, field by field, bit for bit.
/// </summary>
public partial class RingTerrainProxySector
{
    /// <summary>Returns "" when every point matches, otherwise a description of the first difference.</summary>
    public static string CompareSampleCacheToLegacy(
        RingLodSurfaceEditSnapshot edits,
        RingTerrainSampler sampler,
        int ringWidth,
        IReadOnlyList<(long Around, int Across)> points,
        out int compared)
    {
        compared = 0;
        using var cache = new TerrainBuildSampleCache(edits, sampler, ringWidth);
        for (int pass = 0; pass < 2; pass++)
        {
            foreach ((long around, int across) in points)
            {
                CachedTerrainPoint expected = LegacySampleCacheGet(edits, sampler, ringWidth, around, across);
                CachedTerrainPoint actual = cache.Get(around, across);
                string difference = DescribePointDifference(expected, actual);
                if (difference.Length != 0) return $"pass {pass} ({around},{across}): {difference}";
                if (pass == 0) compared++;
            }
        }
        return "";
    }

    /// <summary>
    /// WO-C2: a height-only point carries the same height and water fields as the full point; a later full Get upgrades it
    /// (never served weak) and matches the legacy full point bit for bit.
    /// </summary>
    public static string CompareHeightOnlyToFull(
        RingLodSurfaceEditSnapshot edits,
        RingTerrainSampler sampler,
        int ringWidth,
        IReadOnlyList<(long Around, int Across)> points,
        out int compared)
    {
        compared = 0;
        using var cache = new TerrainBuildSampleCache(edits, sampler, ringWidth);
        foreach ((long around, int across) in points)
        {
            CachedTerrainPoint full = LegacySampleCacheGet(edits, sampler, ringWidth, around, across);
            CachedTerrainPoint weak = cache.GetHeightOnly(around, across);
            // Everything except the climate-derived sample fields must match.
            CachedTerrainPoint masked = weak with { Sample = weak.Sample with { Erosion = full.Sample.Erosion, Temperature = full.Sample.Temperature, Moisture = full.Sample.Moisture, Biome = full.Sample.Biome } };
            string difference = DescribePointDifference(full, masked);
            if (difference.Length != 0) return $"height-only ({around},{across}): {difference}";
            if (!cache.GetHeightOnly(around, across).Equals(weak)) return $"height-only repeat ({around},{across}) differs";
            difference = DescribePointDifference(full, cache.Get(around, across));
            if (difference.Length != 0) return $"upgrade ({around},{across}): {difference}";
            difference = DescribePointDifference(full, cache.GetHeightOnly(around, across));
            if (difference.Length != 0) return $"after upgrade ({around},{across}): {difference}";
            compared++;
        }
        return "";
    }

    /// <summary>
    /// T2R-10 (S9): saves the cache points of <paramref name="points"/> to <paramref name="path"/> (which must lie under a folder named lod_cache) with the sampler's record layout, loads them
    /// into a fresh cache, and returns "" when every point survived bit for bit (exact level and flag included). <paramref name="version"/> is the header version, <paramref name="payloadBytes"/> the
    /// inflated payload size (4 bytes of count plus the records), <paramref name="staleLoaded"/> whether a file with the other layout's version was accepted (it must not be).
    /// </summary>
    public static string RoundTripCacheRecords(
        string path,
        RingLodSurfaceEditSnapshot edits,
        RingTerrainSampler sampler,
        int ringWidth,
        IReadOnlyList<(long Around, int Across)> points,
        out int version,
        out long payloadBytes,
        out bool staleLoaded)
    {
        version = 0; payloadBytes = 0; staleLoaded = false;
        var coordinate = new RingTerrainSectorCoordinate(3, 4);
        string root = System.IO.Path.GetDirectoryName(path)!;
        System.IO.Directory.CreateDirectory(root);
        RingGeneratedLodCacheBudget.Schedule(root, 1L << 30, 100, TimeSpan.FromHours(1));
        using var cache = new TerrainBuildSampleCache(edits, sampler, ringWidth);
        cache.BeginCapture();
        foreach ((long around, int across) in points) cache.Get(around, across);
        CachedTerrainSampleRecord[] captured = cache.EndCapture();
        if (!TrySaveCachedTerrainSamples(path, coordinate, captured, sampler.HasInlandWater, default))
            return "the save was refused";
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        version = BitConverter.ToInt32(bytes, 4);
        using (var inflated = new System.IO.MemoryStream())
        {
            using (var source = new System.IO.MemoryStream(bytes, 20, bytes.Length - 20))
            using (var deflate = new System.IO.Compression.DeflateStream(source, System.IO.Compression.CompressionMode.Decompress))
                deflate.CopyTo(inflated);
            payloadBytes = inflated.Length;
        }
        using var loaded = new TerrainBuildSampleCache(edits, sampler, ringWidth);
        if (!TryLoadCachedTerrainSamples(path, coordinate, loaded, out int count) || count != captured.Length)
            return $"the file did not load back (count {count} of {captured.Length})";
        foreach (CachedTerrainSampleRecord record in captured)
        {
            string difference = DescribePointDifference(record.Point, loaded.Get(record.Around, record.Across));
            if (difference.Length != 0) return $"({record.Around},{record.Across}): {difference}";
        }
        // A file written with the other layout's version is stale: it must be refused (and the sector rebuilt).
        byte[] stale = (byte[])bytes.Clone();
        BitConverter.GetBytes(version == 12 ? 11 : 12).CopyTo(stale, 4);
        System.IO.File.WriteAllBytes(path, stale);
        using var staleCache = new TerrainBuildSampleCache(edits, sampler, ringWidth);
        staleLoaded = TryLoadCachedTerrainSamples(path, coordinate, staleCache, out _);
        return "";
    }

    private static CachedTerrainPoint LegacySampleCacheGet(
        RingLodSurfaceEditSnapshot edits, RingTerrainSampler sampler, int ringWidth, long around, int across)
    {
        int clampedAcross = Godot.Mathf.Clamp(across, 0, ringWidth);
        TerrainSample sample = sampler.GetTerrainSample(around, clampedAcross);
        bool usesPresentationSurface = edits.UsesFloatSurface || sampler.UsesRuntimeSurfaceGeneration;
        if (usesPresentationSurface)
            sample = edits.GetPresentationTerrainSample(sampler, around, clampedAcross);
        int terrainHeight = edits.TryGetEditedSurfaceHeight(around, clampedAcross, out int editedHeight)
            ? editedHeight
            : sample.SurfaceHeight;
        float presentationTerrainHeight = usesPresentationSurface
            ? edits.GetPresentationSurfaceHeight(sampler, around, clampedAcross)
            : 0.0f;
        float smoothTerrainHeight = usesPresentationSurface
            ? presentationTerrainHeight
            : edits.TryGetEditedSurfaceHeight(around, clampedAcross, out _)
                ? terrainHeight
                : sampler.GetSmoothSurfaceHeight(around, clampedAcross);
        return new CachedTerrainPoint(
            sample,
            terrainHeight,
            sample.ResolveDisplayedHeight(terrainHeight),
            smoothTerrainHeight,
            presentationTerrainHeight,
            usesPresentationSurface ? sample.ResolveDisplayedHeight(presentationTerrainHeight) : 0.0f,
            usesPresentationSurface,
            sampler.UsesRuntimeSurfaceGeneration);
    }

    private static string DescribePointDifference(CachedTerrainPoint e, CachedTerrainPoint a)
    {
        static bool Same(float x, float y) => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y);
        TerrainSample es = e.Sample, s = a.Sample;
        if (es.SurfaceHeight != s.SurfaceHeight) return $"Sample.SurfaceHeight {es.SurfaceHeight} vs {s.SurfaceHeight}";
        if (es.Region != s.Region) return "Sample.Region";
        if (!Same(es.Continentalness, s.Continentalness)) return "Sample.Continentalness";
        if (!Same(es.Erosion, s.Erosion)) return "Sample.Erosion";
        if (!Same(es.RidgeStrength, s.RidgeStrength)) return "Sample.RidgeStrength";
        if (!Same(es.Temperature, s.Temperature)) return "Sample.Temperature";
        if (!Same(es.Moisture, s.Moisture)) return "Sample.Moisture";
        if (es.Biome != s.Biome) return "Sample.Biome";
        if (es.WaterKind != s.WaterKind) return "Sample.WaterKind";
        if (es.WaterSurfaceHeight != s.WaterSurfaceHeight) return "Sample.WaterSurfaceHeight";
        if (!es.SiteMask.Equals(s.SiteMask)) return "Sample.SiteMask";
        if (es.HasExactWaterLevel != s.HasExactWaterLevel) return "Sample.HasExactWaterLevel";
        if (!Same(es.ExactWaterLevel, s.ExactWaterLevel)) return "Sample.ExactWaterLevel";
        if (e.TerrainHeight != a.TerrainHeight) return "TerrainHeight";
        if (e.DisplayedHeight != a.DisplayedHeight) return "DisplayedHeight";
        if (!Same(e.SmoothTerrainHeight, a.SmoothTerrainHeight)) return $"SmoothTerrainHeight {e.SmoothTerrainHeight:R} vs {a.SmoothTerrainHeight:R}";
        if (!Same(e.PresentationTerrainHeight, a.PresentationTerrainHeight)) return $"PresentationTerrainHeight {e.PresentationTerrainHeight:R} vs {a.PresentationTerrainHeight:R}";
        if (!Same(e.PresentationDisplayedHeight, a.PresentationDisplayedHeight)) return "PresentationDisplayedHeight";
        if (e.UsesPresentationSurface != a.UsesPresentationSurface) return "UsesPresentationSurface";
        if (e.UsesRuntimeWaterPlane != a.UsesRuntimeWaterPlane) return "UsesRuntimeWaterPlane";
        return "";
    }
}
