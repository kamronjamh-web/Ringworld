using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Cached, geometry-derived ocean demand for the FFT water controller.</summary>
public readonly record struct RingWaterFftDemandSnapshot(
    int RelevantSectorCount,
    int RelevantCellCount,
    double WeightedAreaSquareMeters,
    float NearestDistanceMeters)
{
    public bool HasDemand => RelevantCellCount > 0;
}

public partial class RingTerrainProxySector
{
    // 128 m is deliberately common to every proxy level. Each bin retains the
    // union bounds of submitted footprints, rather than summing layers, because
    // overlapping LOD water is a representation handoff of one physical ocean.
    private const double FftDemandBinMeters = 128.0;
    private const int FftDemandPatchResolution = 8;
    private static readonly HashSet<RingTerrainProxySector> FftWaterDemandProviders = new();
    private static readonly Dictionary<DemandBin, DemandBinRect> FftWaterDemandBins = new();
    private static readonly HashSet<SectorDemandKey> FftWaterDemandRelevantSectors = new();
    private readonly Dictionary<RingTerrainSectorCoordinate, FftWaterDemandPatch[]> _fftWaterDemandPatches = new();

    private readonly record struct DemandBin(int Around, int Across);
    private readonly record struct SectorDemandKey(RingTerrainProxySector Provider, RingTerrainSectorCoordinate Coordinate);
    private readonly record struct DemandBinRect(double MinAround, double MaxAround, double MinAcross, double MaxAcross, double SeaY);
    private readonly record struct FftWaterDemandPatch(
        double AroundMin, double AroundMax, double AcrossMin, double AcrossMax);

    private void RegisterFftWaterDemandProvider() => FftWaterDemandProviders.Add(this);

    private void UnregisterFftWaterDemandProvider()
    {
        FftWaterDemandProviders.Remove(this);
        _fftWaterDemandPatches.Clear();
        if (FftWaterDemandProviders.Count == 0)
        {
            FftWaterDemandRelevantSectors.Clear();
            FftWaterDemandBins.Clear();
        }
    }

    /// <summary>
    /// Captures demand from uploaded ocean triangles only.  The result is an
    /// area approximation: submitted ocean triangles are compacted into 8x8
    /// wet patches and all proxy layers merge into common 128 m footprint bins.
    /// No terrain sampling occurs here or in the per-frame query.
    /// </summary>
    public static RingWaterFftDemandSnapshot CaptureFftWaterDemand(
        in RingCanonicalPosition position, float rangeMeters, float waveMarginMeters = 0.0f)
    {
        if (!float.IsFinite(rangeMeters) || !float.IsFinite(waveMarginMeters))
            return new RingWaterFftDemandSnapshot(0, 0, 0.0, float.PositiveInfinity);
        float range = Mathf.Max(0.0f, rangeMeters + Mathf.Max(0.0f, waveMarginMeters));
        if (!float.IsFinite(range))
            return new RingWaterFftDemandSnapshot(0, 0, 0.0, float.PositiveInfinity);
        if (range <= 0.0f || FftWaterDemandProviders.Count == 0)
            return new RingWaterFftDemandSnapshot(0, 0, 0.0, float.PositiveInfinity);

        FftWaterDemandBins.Clear();
        FftWaterDemandRelevantSectors.Clear();
        double nearest = double.PositiveInfinity;
        foreach (RingTerrainProxySector provider in FftWaterDemandProviders)
        {
            if (!provider.StreamingActive || provider._transitHidden || !provider.IsVisibleInTree()
                || provider._grid?.TerrainSampler is not RingTerrainSampler sampler)
                continue;

            float providerRange = provider.EnableFarFade
                ? Mathf.Min(range, Mathf.Max(0.0f, (provider.FadeOutStartMeters
                    + Mathf.Max(provider.FadeOutStartMeters + 1.0f, provider.FadeOutEndMeters)) * 0.5f))
                : range;
            if (providerRange <= 0.0f) continue;
            double seaY = sampler.SeaLevelY + 1.0;
            if (Math.Abs(position.VerticalMeters - seaY) > providerRange) continue;
            double circumference = provider.ResolveAroundCircumferenceMeters();
            foreach ((RingTerrainSectorCoordinate coordinate, FftWaterDemandPatch[] patches) in provider._fftWaterDemandPatches)
            {
                if (!IsSectorNearDemandRange(provider, coordinate, position, range, circumference))
                    continue;
                if (provider._warmUntil.ContainsKey(coordinate)
                    || !provider._active.TryGetValue(coordinate, out MeshInstance3D? node) || !node.IsVisibleInTree())
                    continue;
                foreach (FftWaterDemandPatch patch in patches)
                {
                    double aroundCenter = SignedPeriodicDistance(
                        (patch.AroundMin + patch.AroundMax) * 0.5, position.AroundMeters, circumference);
                    double aroundHalfWidth = (patch.AroundMax - patch.AroundMin) * 0.5;
                    double aroundMin = aroundCenter - aroundHalfWidth;
                    double aroundMax = aroundCenter + aroundHalfWidth;
                    double acrossMin = patch.AcrossMin - position.AcrossMeters;
                    double acrossMax = patch.AcrossMax - position.AcrossMeters;
                    double horizontalDistance = DistanceToRectangle(0.0, 0.0, aroundMin, aroundMax, acrossMin, acrossMax);
                    double distance = Math.Sqrt(horizontalDistance * horizontalDistance + (position.VerticalMeters - seaY) * (position.VerticalMeters - seaY));
                    if (distance > providerRange) continue;
                    FftWaterDemandRelevantSectors.Add(new SectorDemandKey(provider, coordinate));
                    nearest = Math.Min(nearest, distance);
                    RasterizeDemandPatch(aroundMin, aroundMax, acrossMin, acrossMax, seaY, providerRange);
                }
            }
        }

        double weightedArea = 0.0;
        foreach ((DemandBin bin, DemandBinRect rect) in FftWaterDemandBins)
        {
            double centerAround = (rect.MinAround + rect.MaxAround) * 0.5;
            double centerAcross = (rect.MinAcross + rect.MaxAcross) * 0.5;
            double vertical = position.VerticalMeters - rect.SeaY;
            double d = Math.Sqrt(centerAround * centerAround + centerAcross * centerAcross + vertical * vertical);
            float edgeWeight = Mathf.Clamp(1.0f - (float)(d / range), 0.0f, 1.0f);
            weightedArea += (rect.MaxAround - rect.MinAround) * (rect.MaxAcross - rect.MinAcross) * edgeWeight * edgeWeight;
        }
        return new RingWaterFftDemandSnapshot(FftWaterDemandRelevantSectors.Count, FftWaterDemandBins.Count, weightedArea, (float)nearest);
    }

    private static void RasterizeDemandPatch(double minA, double maxA, double minZ, double maxZ, double seaY, float range)
    {
        // Clip before binning. This bounds a huge coarse sector to the query disk
        // envelope instead of scanning bins across the full sector extent.
        minA = Math.Max(minA, -range); maxA = Math.Min(maxA, range);
        minZ = Math.Max(minZ, -range); maxZ = Math.Min(maxZ, range);
        if (maxA <= minA || maxZ <= minZ) return;
        int firstA = (int)Math.Floor(minA / FftDemandBinMeters);
        int lastA = (int)Math.Floor((maxA - 0.001) / FftDemandBinMeters);
        int firstZ = (int)Math.Floor(minZ / FftDemandBinMeters);
        int lastZ = (int)Math.Floor((maxZ - 0.001) / FftDemandBinMeters);
        for (int z = firstZ; z <= lastZ; z++)
        for (int a = firstA; a <= lastA; a++)
        {
            var bin = new DemandBin(a, z);
            double binMinA = a * FftDemandBinMeters, binMaxA = binMinA + FftDemandBinMeters;
            double binMinZ = z * FftDemandBinMeters, binMaxZ = binMinZ + FftDemandBinMeters;
            var clipped = new DemandBinRect(Math.Max(minA, binMinA), Math.Min(maxA, binMaxA),
                Math.Max(minZ, binMinZ), Math.Min(maxZ, binMaxZ), seaY);
            if (!FftWaterDemandBins.TryGetValue(bin, out DemandBinRect existing))
                FftWaterDemandBins[bin] = clipped;
            else
                FftWaterDemandBins[bin] = new DemandBinRect(
                    Math.Min(existing.MinAround, clipped.MinAround), Math.Max(existing.MaxAround, clipped.MaxAround),
                    Math.Min(existing.MinAcross, clipped.MinAcross), Math.Max(existing.MaxAcross, clipped.MaxAcross), existing.SeaY);
        }
    }

    private static double DistanceToRectangle(double x, double z, double minX, double maxX, double minZ, double maxZ)
    {
        double dx = x < minX ? minX - x : x > maxX ? x - maxX : 0.0;
        double dz = z < minZ ? minZ - z : z > maxZ ? z - maxZ : 0.0;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private static bool IsSectorNearDemandRange(RingTerrainProxySector provider,
        RingTerrainSectorCoordinate coordinate, in RingCanonicalPosition position, float range, double circumference)
    {
        double size = provider.SectorSizeMeters;
        double aroundCenter = SignedPeriodicDistance((coordinate.AroundSector + 0.5) * size,
            position.AroundMeters, circumference);
        double aroundHalf = size * 0.5;
        double acrossMinCanonical = Math.Max(0.0, coordinate.AcrossSector * size);
        double acrossMaxCanonical = Math.Min(provider.RingWidthMeters, (coordinate.AcrossSector + 1.0) * size);
        if (acrossMaxCanonical <= acrossMinCanonical) return false;
        double distance = DistanceToRectangle(0.0, 0.0, aroundCenter - aroundHalf, aroundCenter + aroundHalf,
            acrossMinCanonical - position.AcrossMeters, acrossMaxCanonical - position.AcrossMeters);
        return distance <= range;
    }

    /// <summary>Pure collector invariants for command-line smoke coverage.</summary>
    internal static bool RunFftWaterDemandContractTests()
    {
        FftWaterDemandBins.Clear();
        RasterizeDemandPatch(4.0, 12.0, 8.0, 16.0, 1.0, 512.0f);
        bool tinyFootprint = FftWaterDemandBins.Count == 1
            && FftWaterDemandBins.TryGetValue(new DemandBin(0, 0), out DemandBinRect tiny)
            && Math.Abs((tiny.MaxAround - tiny.MinAround) * (tiny.MaxAcross - tiny.MinAcross) - 64.0) < 0.01;
        RasterizeDemandPatch(6.0, 10.0, 10.0, 14.0, 1.0, 512.0f);
        bool duplicateFootprint = FftWaterDemandBins.Count == 1;
        double seamCenter = SignedPeriodicDistance(1.0, 999.0, 1000.0);
        bool seamSafe = Math.Abs(seamCenter - 2.0) < 0.001
            && DistanceToRectangle(0.0, 0.0, 10.0, 20.0, -5.0, 5.0) == 10.0;
        Color ocean = RingWaterModel.EncodeWaterKind(Colors.White, SurfaceWaterKind.Ocean);
        Color lake = RingWaterModel.EncodeWaterKind(Colors.White, SurfaceWaterKind.Lake);
        bool mixedTriangleAccepted = HasOceanVertex(ocean, lake, lake)
            && !HasOceanVertex(lake, lake, lake);
        FftWaterDemandBins.Clear();
        return tinyFootprint && duplicateFootprint && seamSafe && mixedTriangleAccepted;
    }

    private void InstallFftWaterDemandMetadata(SectorData data)
    {
        RemoveFftWaterDemandMetadata(data.Coordinate);
        // The blocky builder intentionally leaves water alpha as the visual
        // color alpha (normally 1, which decodes as River). Without submitted
        // kind metadata it cannot distinguish lakes from ocean, so fail closed.
        if (UseBlockyHeightfield) return;
        if (data.OceanIndices.Length < 3 || data.Uvs.Length == 0 || data.Colors.Length == 0) return;
        if (data.FullyOceanSurface)
        {
            double fullOriginA = data.Coordinate.AroundSector * (double)SectorSizeMeters;
            double fullOriginZ = data.Coordinate.AcrossSector * (double)SectorSizeMeters;
            double acrossMin = Math.Max(0.0, fullOriginZ);
            double acrossMax = Math.Min(RingWidthMeters, fullOriginZ + SectorSizeMeters);
            if (acrossMax > acrossMin)
                _fftWaterDemandPatches[data.Coordinate] = new[]
                {
                    new FftWaterDemandPatch(fullOriginA, fullOriginA + SectorSizeMeters, acrossMin, acrossMax)
                };
            return;
        }
        int patchCount = FftDemandPatchResolution * FftDemandPatchResolution;
        var minU = new float[patchCount];
        var maxU = new float[patchCount];
        var minV = new float[patchCount];
        var maxV = new float[patchCount];
        Array.Fill(minU, float.PositiveInfinity);
        Array.Fill(minV, float.PositiveInfinity);
        Array.Fill(maxU, float.NegativeInfinity);
        Array.Fill(maxV, float.NegativeInfinity);
        for (int i = 0; i + 2 < data.OceanIndices.Length; i += 3)
        {
            int ia = data.OceanIndices[i], ib = data.OceanIndices[i + 1], ic = data.OceanIndices[i + 2];
            if ((uint)ia >= (uint)data.Uvs.Length || (uint)ib >= (uint)data.Uvs.Length || (uint)ic >= (uint)data.Uvs.Length
                || (uint)ia >= (uint)data.Colors.Length || (uint)ib >= (uint)data.Colors.Length || (uint)ic >= (uint)data.Colors.Length)
                continue;
            if (!HasOceanVertex(data.Colors[ia], data.Colors[ib], data.Colors[ic]))
                continue;
            Vector2 a = data.Uvs[ia], b = data.Uvs[ib], c = data.Uvs[ic];
            float triangleMinU = Mathf.Clamp(Mathf.Min(a.X, Mathf.Min(b.X, c.X)), 0.0f, 1.0f);
            float triangleMaxU = Mathf.Clamp(Mathf.Max(a.X, Mathf.Max(b.X, c.X)), 0.0f, 1.0f);
            float triangleMinV = Mathf.Clamp(Mathf.Min(a.Y, Mathf.Min(b.Y, c.Y)), 0.0f, 1.0f);
            float triangleMaxV = Mathf.Clamp(Mathf.Max(a.Y, Mathf.Max(b.Y, c.Y)), 0.0f, 1.0f);
            int minX = Mathf.Clamp(Mathf.FloorToInt(triangleMinU * FftDemandPatchResolution), 0, FftDemandPatchResolution - 1);
            int maxX = Mathf.Clamp(Mathf.FloorToInt(triangleMaxU * FftDemandPatchResolution), 0, FftDemandPatchResolution - 1);
            int minZ = Mathf.Clamp(Mathf.FloorToInt(triangleMinV * FftDemandPatchResolution), 0, FftDemandPatchResolution - 1);
            int maxZ = Mathf.Clamp(Mathf.FloorToInt(triangleMaxV * FftDemandPatchResolution), 0, FftDemandPatchResolution - 1);
            for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
            {
                int cell = z * FftDemandPatchResolution + x;
                float cellMinU = x / (float)FftDemandPatchResolution;
                float cellMaxU = (x + 1) / (float)FftDemandPatchResolution;
                float cellMinV = z / (float)FftDemandPatchResolution;
                float cellMaxV = (z + 1) / (float)FftDemandPatchResolution;
                minU[cell] = Mathf.Min(minU[cell], Mathf.Max(cellMinU, triangleMinU));
                maxU[cell] = Mathf.Max(maxU[cell], Mathf.Min(cellMaxU, triangleMaxU));
                minV[cell] = Mathf.Min(minV[cell], Mathf.Max(cellMinV, triangleMinV));
                maxV[cell] = Mathf.Max(maxV[cell], Mathf.Min(cellMaxV, triangleMaxV));
            }
        }
        var patches = new List<FftWaterDemandPatch>(FftDemandPatchResolution * FftDemandPatchResolution);
        double originA = data.Coordinate.AroundSector * (double)SectorSizeMeters;
        double originZ = data.Coordinate.AcrossSector * (double)SectorSizeMeters;
        for (int z = 0; z < FftDemandPatchResolution; z++)
        for (int x = 0; x < FftDemandPatchResolution; x++)
        {
            int cell = z * FftDemandPatchResolution + x;
            if (!float.IsFinite(minU[cell]) || maxU[cell] <= minU[cell] || maxV[cell] <= minV[cell]) continue;
            double aroundMin = originA + minU[cell] * SectorSizeMeters;
            double aroundMax = originA + maxU[cell] * SectorSizeMeters;
            double acrossMin = originZ + minV[cell] * SectorSizeMeters;
            double acrossMax = originZ + maxV[cell] * SectorSizeMeters;
            patches.Add(new FftWaterDemandPatch(aroundMin, aroundMax, acrossMin, acrossMax));
        }
        if (patches.Count > 0) _fftWaterDemandPatches[data.Coordinate] = patches.ToArray();
    }

    private void RemoveFftWaterDemandMetadata(RingTerrainSectorCoordinate coordinate) => _fftWaterDemandPatches.Remove(coordinate);

    private static bool HasOceanVertex(Color a, Color b, Color c) =>
        RingWaterModel.DecodeWaterKind(a.A) == SurfaceWaterKind.Ocean
        || RingWaterModel.DecodeWaterKind(b.A) == SurfaceWaterKind.Ocean
        || RingWaterModel.DecodeWaterKind(c.A) == SurfaceWaterKind.Ocean;
}
