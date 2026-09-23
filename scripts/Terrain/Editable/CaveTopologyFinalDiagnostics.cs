using System;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Terrain.Editable;

/// <summary>
/// End-to-end invariants across the complete designed-topology chain and the
/// authoritative smooth-terrain chunk capture used by production meshing.
/// </summary>
public static class CaveTopologyFinalDiagnostics
{
    private const long RingLength = CaveTopologyPartition.ProvinceAroundMeters * 12
        + CaveTopologyPartition.CellAroundMeters * 24;
    private const int ChunkCells = SmoothTerrainChunkSnapshot.Cells;

    public static bool RunSelfTest(out string summary)
    {
        try
        {
            using var forwardSampler = CreateSampler();
            using var reverseSampler = CreateSampler();
            if (forwardSampler.CaveTopologyDensityComposer.Identity
                    != reverseSampler.CaveTopologyDensityComposer.Identity
                || forwardSampler.CaveRegionFeatureSnapshotGenerator.Identity
                    != reverseSampler.CaveRegionFeatureSnapshotGenerator.Identity)
            {
                throw new InvalidOperationException(
                    "independent same-contract topology chains changed identity");
            }

            var forwardBaseline = new RingTerrainSmoothBaseline(
                forwardSampler,
                enableDesignedCaves: true);
            var reverseBaseline = new RingTerrainSmoothBaseline(
                reverseSampler,
                enableDesignedCaves: true);
            var disabledBaseline = new RingTerrainSmoothBaseline(
                forwardSampler,
                enableDesignedCaves: false);

            CaveChamberAnchor negativeAnchor = FindNegativeAnchor(forwardSampler);
            CaveProvincePlan partialPlan = FindActivePartialPlan(forwardSampler);
            CaveChamberAnchor partialAnchor = forwardSampler.CaveChamberGraphGenerator
                .Resolve(partialPlan).Anchors[0];
            AssertNegativeOwnership(forwardSampler, negativeAnchor);
            AssertContributingPeriodicAnchor(
                forwardSampler,
                forwardBaseline,
                disabledBaseline,
                negativeAnchor,
                "negative-depth anchor");
            AssertContributingPeriodicAnchor(
                forwardSampler,
                forwardBaseline,
                disabledBaseline,
                partialAnchor,
                "partial-final anchor");

            CaveTopologyDensityRegion[] forwardRegions = CaptureRegionsInOrder(
                forwardSampler,
                negativeAnchor,
                partialAnchor,
                reverse: false);
            // The bounded shared province cache must never participate in the
            // region identity. Repeating an identical capture proves both
            // deterministic equality and that the expensive province graph
            // work is reused rather than regenerated.
            CaveRegionFeatureSnapshotGenerator features = forwardSampler.CaveRegionFeatureSnapshotGenerator;
            CaveRegionFeatureSnapshotGenerator.ProvinceCacheDiagnostics cacheBefore =
                features.CaptureProvinceCacheDiagnostics();
            CaveTopologyDensityRegion repeated = forwardSampler.CaveTopologyDensityComposer.CaptureRegion(
                forwardRegions[0].Request);
            CompareRegions(forwardRegions[0], repeated);
            CaveRegionFeatureSnapshotGenerator.ProvinceCacheDiagnostics cacheAfter =
                features.CaptureProvinceCacheDiagnostics();
            if (cacheAfter.Count > cacheAfter.Capacity
                || cacheAfter.Hits <= cacheBefore.Hits
                || cacheAfter.Misses < cacheBefore.Misses)
            {
                throw new InvalidOperationException($"province cache telemetry invalid: before={cacheBefore}, after={cacheAfter}");
            }
            AssertBoundedProvinceCache(forwardSampler.CaveGenerationContract);
            CaveTopologyDensityRegion[] reverseRegions = CaptureRegionsInOrder(
                reverseSampler,
                negativeAnchor,
                partialAnchor,
                reverse: true);
            for (int index = 0; index < forwardRegions.Length; index++)
                CompareRegions(forwardRegions[index], reverseRegions[index]);

            var edits = new SmoothTerrainEditSnapshot(
                Array.Empty<SmoothTerrainEditCommand>(),
                revision: 0,
                RingLength);
            var forwardField = new SmoothTerrainVolumetricDensitySnapshot(
                forwardBaseline,
                edits);
            var reverseField = new SmoothTerrainVolumetricDensitySnapshot(
                reverseBaseline,
                edits);
            var disabledField = new SmoothTerrainVolumetricDensitySnapshot(
                disabledBaseline,
                edits);

            SmoothTerrainChunkCoordinate center = ChunkContaining(negativeAnchor);
            SmoothTerrainChunkCoordinate partialCenter = ChunkContaining(partialAnchor);
            var requests = new[]
            {
                center,
                new SmoothTerrainChunkCoordinate(
                    center.AroundChunk - 1,
                    center.VerticalChunk,
                    center.AcrossChunk),
                new SmoothTerrainChunkCoordinate(
                    center.AroundChunk,
                    center.VerticalChunk - 1,
                    center.AcrossChunk),
                partialCenter
            };
            SmoothTerrainChunkSnapshot[] forwardChunks = CaptureChunksInOrder(
                forwardField,
                requests,
                reverse: false);
            SmoothTerrainChunkSnapshot[] reverseChunks = CaptureChunksInOrder(
                reverseField,
                requests,
                reverse: true);
            for (int index = 0; index < requests.Length; index++)
                CompareChunks(forwardChunks[index], reverseChunks[index]);

            SmoothTerrainChunkSnapshot centerChunk = forwardField.CaptureChunk(center);
            SmoothTerrainChunkSnapshot disabledCenter = disabledField.CaptureChunk(center);
            int contributingLocalFaces = 0;
            foreach (var axis in new[]
            {
                (Around: 1L, Vertical: 0, Across: 0),
                (Around: 0L, Vertical: 1, Across: 0),
                (Around: 0L, Vertical: 0, Across: 1)
            })
            {
                var neighborCoordinate = new SmoothTerrainChunkCoordinate(
                    center.AroundChunk + axis.Around,
                    center.VerticalChunk + axis.Vertical,
                    center.AcrossChunk + axis.Across);
                SmoothTerrainChunkSnapshot neighbor = forwardField.CaptureChunk(
                    neighborCoordinate);
                if (CompareSharedFace(centerChunk, neighbor, disabledCenter, axis))
                    contributingLocalFaces++;
            }
            if (contributingLocalFaces != 3)
            {
                throw new InvalidOperationException(
                    $"chunk seams did not exercise cave density on all axes: {contributingLocalFaces}/3");
            }

            if (RingLength % ChunkCells != 0)
                throw new InvalidOperationException("test ring is not aligned to production chunks");
            long aroundChunkCount = RingLength / ChunkCells;
            var lastChunkCoordinate = new SmoothTerrainChunkCoordinate(
                aroundChunkCount - 1,
                center.VerticalChunk,
                center.AcrossChunk);
            var firstChunkCoordinate = new SmoothTerrainChunkCoordinate(
                0,
                center.VerticalChunk,
                center.AcrossChunk);
            CompareSharedFace(
                forwardField.CaptureChunk(lastChunkCoordinate),
                forwardField.CaptureChunk(firstChunkCoordinate),
                disabledLeft: null,
                (Around: 1L, Vertical: 0, Across: 0));

            summary =
                $"composer={forwardSampler.CaveTopologyDensityComposer.Identity:X16} "
                + $"negative={negativeAnchor.Key:X16} partial={partialAnchor.Key:X16} "
                + $"regions={forwardRegions.Length} chunks={requests.Length} "
                + $"caveFaces={contributingLocalFaces} wrapChunks={aroundChunkCount}";
            return true;
        }
        catch (Exception exception)
        {
            summary = exception.Message;
            return false;
        }
    }

    private static VoxelTerrainSettings CreateSettings() => new()
    {
        // This fixture historically used terrain seed 17 plus cave seed 7331.
        // With one world seed, retain the topology fixture by making 7331 the
        // shared terrain/cave seed.
        Seed = 7331,
        BaseSurfaceHeightVoxels = 2048,
        HeightAmplitudeVoxels = 0,
        DetailAmplitudeVoxels = 0,
        BedrockY = -20_000,
        DesignedCaveGenerationContractSettings = new CaveGenerationSettings(),
        EnableDesignedCaveDensityComposition = true
    };

    private static RingTerrainSampler CreateSampler() => new(
        CreateSettings(),
        periodicAroundRing: true,
        RingLength,
        ringRadiusMeters: 64_000,
        ringAcrossWidthMeters: 32_768,
        maxBuildGlobalY: 4096);

    private static void AssertBoundedProvinceCache(CaveGenerationContract contract)
    {
        const int capacity = 4;
        var partition = new CaveTopologyPartition(contract, RingLength);
        var distribution = new CaveProvinceDistribution(partition);
        var anchors = new CaveChamberAnchorGenerator(distribution);
        var graphs = new CaveChamberGraphGenerator(anchors);
        var maps = new CaveCellBoundaryConnectionMapGenerator(graphs);
        var features = new CaveRegionFeatureSnapshotGenerator(maps, capacity);
        CaveRegionFeatureRequest lastRequest = default;
        CaveRegionFeatureSnapshot? lastSnapshot = null;
        for (int index = 0; index < capacity + 2; index++)
        {
            lastRequest = new CaveRegionFeatureRequest(
                Around: 1,
                Vertical: checked(-(index + 1L) * CaveTopologyPartition.ProvinceVerticalMeters + 1),
                Across: 1,
                AroundExtent: 1,
                VerticalExtent: 1,
                AcrossExtent: 1,
                Halo: 0);
            lastSnapshot = features.Capture(lastRequest);
        }

        CaveRegionFeatureSnapshotGenerator.ProvinceCacheDiagnostics filled =
            features.CaptureProvinceCacheDiagnostics();
        if (filled.Capacity != capacity
            || filled.Count != capacity
            || filled.Misses != capacity + 2
            || filled.Evictions != 2)
        {
            throw new InvalidOperationException($"province cache hard bound failed: {filled}");
        }

        CaveRegionFeatureSnapshot repeated = features.Capture(lastRequest);
        CaveRegionFeatureSnapshotGenerator.ProvinceCacheDiagnostics hit =
            features.CaptureProvinceCacheDiagnostics();
        if (lastSnapshot == null
            || repeated.Identity != lastSnapshot.Identity
            || repeated.Count != lastSnapshot.Count
            || hit.Hits != filled.Hits + 1
            || hit.Count != capacity)
        {
            throw new InvalidOperationException($"bounded province cache changed repeat output: {hit}");
        }
    }

    private static CaveChamberAnchor FindNegativeAnchor(RingTerrainSampler sampler)
    {
        for (long vertical = -1; vertical >= -32; vertical--)
        for (long around = 0;
             around < sampler.CaveTopologyPartition.AroundProvinceCount - 1;
             around++)
        for (long across = 0; across <= 2; across++)
        {
            CaveProvincePlan plan = sampler.CaveProvinceDistribution.ResolvePlan(
                new CaveTopologyProvinceAddress(around, vertical, across));
            if (plan.Active)
                return sampler.CaveChamberGraphGenerator.Resolve(plan).Anchors[0];
        }
        throw new InvalidOperationException("no active negative-depth cave anchor found");
    }

    private static CaveProvincePlan FindActivePartialPlan(RingTerrainSampler sampler)
    {
        long last = sampler.CaveTopologyPartition.AroundProvinceCount - 1;
        for (long vertical = -1; vertical >= -64; vertical--)
        for (long across = 0; across <= 1; across++)
        {
            CaveProvincePlan plan = sampler.CaveProvinceDistribution.ResolvePlan(
                new CaveTopologyProvinceAddress(last, vertical, across));
            if (plan.Active)
            {
                if (plan.Province.AroundExtent >= CaveTopologyPartition.ProvinceAroundMeters)
                    throw new InvalidOperationException("final province was not partial");
                return plan;
            }
        }
        throw new InvalidOperationException("capable partial-final cave province never activated");
    }

    private static void AssertNegativeOwnership(
        RingTerrainSampler sampler,
        CaveChamberAnchor anchor)
    {
        CaveTopologyCell owner = sampler.CaveTopologyPartition.Resolve(
            anchor.Around,
            anchor.Vertical,
            anchor.Across);
        long expectedVerticalCell = FloorDiv(
            anchor.Vertical,
            CaveTopologyPartition.CellVerticalMeters);
        if (anchor.Vertical >= 0
            || expectedVerticalCell >= 0
            || owner.Address != anchor.OwningCellAddress
            || owner.Address.Vertical != expectedVerticalCell
            || anchor.Vertical < owner.VerticalOrigin
            || anchor.Vertical >= owner.VerticalOrigin + owner.VerticalExtent)
        {
            throw new InvalidOperationException(
                "negative-Y cave anchor escaped floor-divided cell ownership");
        }
    }

    private static void AssertContributingPeriodicAnchor(
        RingTerrainSampler sampler,
        RingTerrainSmoothBaseline enabled,
        RingTerrainSmoothBaseline disabled,
        CaveChamberAnchor anchor,
        string context)
    {
        CaveTopologyDensityRegion region = CaptureAround(
            sampler.CaveTopologyDensityComposer,
            anchor);
        CaveTopologyDensityResult canonical = region.Sample(
            anchor.Around,
            anchor.Vertical,
            anchor.Across);
        CaveTopologyDensityResult forwardWrap = region.Sample(
            checked(anchor.Around + RingLength),
            anchor.Vertical,
            anchor.Across);
        CaveTopologyDensityResult backwardWrap = region.Sample(
            checked(anchor.Around - RingLength),
            anchor.Vertical,
            anchor.Across);
        SmoothTerrainSample enabledSample = enabled.Sample(
            anchor.Around,
            checked((int)anchor.Vertical),
            checked((int)anchor.Across));
        SmoothTerrainSample disabledSample = disabled.Sample(
            anchor.Around,
            checked((int)anchor.Vertical),
            checked((int)anchor.Across));
        if (!canonical.HasContribution
            || canonical.FinalDensityMeters <= 0
            || canonical != forwardWrap
            || canonical != backwardWrap
            || enabledSample.Density <= disabledSample.Density)
        {
            throw new InvalidOperationException(
                $"{context} did not prove non-vacuous periodic cave density");
        }
    }

    private static CaveTopologyDensityRegion CaptureAround(
        CaveTopologyDensityComposer composer,
        CaveChamberAnchor anchor) => composer.CaptureRegion(
            anchor.Around - 64,
            anchor.Vertical - 64,
            anchor.Across - 64,
            128,
            128,
            128);

    private static CaveTopologyDensityRegion[] CaptureRegionsInOrder(
        RingTerrainSampler sampler,
        CaveChamberAnchor negativeAnchor,
        CaveChamberAnchor partialAnchor,
        bool reverse)
    {
        var requests = new[]
        {
            new CaveRegionFeatureRequest(
                negativeAnchor.Around - 64,
                negativeAnchor.Vertical - 64,
                negativeAnchor.Across - 64,
                128,
                128,
                128,
                CaveTopologyDensityComposer.MaximumInfluenceHaloMeters),
            new CaveRegionFeatureRequest(
                partialAnchor.Around - 64,
                partialAnchor.Vertical - 64,
                partialAnchor.Across - 64,
                128,
                128,
                128,
                CaveTopologyDensityComposer.MaximumInfluenceHaloMeters),
            new CaveRegionFeatureRequest(
                RingLength - 64,
                negativeAnchor.Vertical - 64,
                negativeAnchor.Across - 64,
                128,
                128,
                128,
                CaveTopologyDensityComposer.MaximumInfluenceHaloMeters)
        };
        var result = new CaveTopologyDensityRegion[requests.Length];
        if (!reverse)
        {
            for (int index = 0; index < requests.Length; index++)
                result[index] = sampler.CaveTopologyDensityComposer.CaptureRegion(requests[index]);
        }
        else
        {
            for (int index = requests.Length - 1; index >= 0; index--)
                result[index] = sampler.CaveTopologyDensityComposer.CaptureRegion(requests[index]);
        }
        return result;
    }

    private static void CompareRegions(
        CaveTopologyDensityRegion left,
        CaveTopologyDensityRegion right)
    {
        CaveRegionFeatureSnapshot a = left.Features;
        CaveRegionFeatureSnapshot b = right.Features;
        if (left.Identity != right.Identity
            || a.Identity != b.Identity
            || a.Request != b.Request
            || a.CandidateProvinceCount != b.CandidateProvinceCount
            || a.ActiveProvinceCount != b.ActiveProvinceCount
            || a.Count != b.Count)
        {
            throw new InvalidOperationException(
                "forward/reverse region capture changed topology metadata");
        }
        for (int index = 0; index < a.Count; index++)
        {
            if (a[index] != b[index])
                throw new InvalidOperationException(
                    $"forward/reverse region capture changed feature {index}");
        }
    }

    private static SmoothTerrainChunkSnapshot[] CaptureChunksInOrder(
        SmoothTerrainVolumetricDensitySnapshot field,
        SmoothTerrainChunkCoordinate[] requests,
        bool reverse)
    {
        var result = new SmoothTerrainChunkSnapshot[requests.Length];
        if (!reverse)
        {
            for (int index = 0; index < requests.Length; index++)
                result[index] = field.CaptureChunk(requests[index]);
        }
        else
        {
            for (int index = requests.Length - 1; index >= 0; index--)
                result[index] = field.CaptureChunk(requests[index]);
        }
        return result;
    }

    private static SmoothTerrainChunkCoordinate ChunkContaining(CaveChamberAnchor anchor) => new(
        FloorDiv(anchor.Around, ChunkCells),
        checked((int)FloorDiv(anchor.Vertical, ChunkCells)),
        checked((int)FloorDiv(anchor.Across, ChunkCells)));

    private static void CompareChunks(
        SmoothTerrainChunkSnapshot left,
        SmoothTerrainChunkSnapshot right)
    {
        if (left.Coordinate != right.Coordinate || left.Revision != right.Revision)
            throw new InvalidOperationException("deterministic chunk metadata changed");
        for (int z = -SmoothTerrainChunkSnapshot.Halo;
             z <= ChunkCells + SmoothTerrainChunkSnapshot.Halo;
             z++)
        for (int y = -SmoothTerrainChunkSnapshot.Halo;
             y <= ChunkCells + SmoothTerrainChunkSnapshot.Halo;
             y++)
        for (int x = -SmoothTerrainChunkSnapshot.Halo;
             x <= ChunkCells + SmoothTerrainChunkSnapshot.Halo;
             x++)
        {
            if (left.Density(x, y, z) != right.Density(x, y, z)
                || left.Material(x, y, z) != right.Material(x, y, z))
            {
                throw new InvalidOperationException(
                    $"independent chains changed chunk sample {x}/{y}/{z}");
            }
        }
    }

    /// <returns>True when the enabled cave baseline changed at least one face sample.</returns>
    private static bool CompareSharedFace(
        SmoothTerrainChunkSnapshot left,
        SmoothTerrainChunkSnapshot right,
        SmoothTerrainChunkSnapshot? disabledLeft,
        (long Around, int Vertical, int Across) axis)
    {
        bool caveContributed = false;
        for (int b = 0; b <= ChunkCells; b++)
        for (int c = 0; c <= ChunkCells; c++)
        {
            (float Density, int Material) l = FaceSample(left, axis, ChunkCells, b, c);
            (float Density, int Material) r = FaceSample(right, axis, 0, b, c);
            if (l != r)
                throw new InvalidOperationException("neighboring production chunk face disagreed");
            if (disabledLeft != null
                && l != FaceSample(disabledLeft, axis, ChunkCells, b, c))
            {
                caveContributed = true;
            }
        }
        return caveContributed;
    }

    private static (float Density, int Material) FaceSample(
        SmoothTerrainChunkSnapshot snapshot,
        (long Around, int Vertical, int Across) axis,
        int face,
        int b,
        int c) => axis.Around != 0
            ? (snapshot.Density(face, b, c), snapshot.Material(face, b, c))
            : axis.Vertical != 0
                ? (snapshot.Density(b, face, c), snapshot.Material(b, face, c))
                : (snapshot.Density(b, c, face), snapshot.Material(b, c, face));

    private static long FloorDiv(long value, long divisor) =>
        value / divisor - (value < 0 && value % divisor != 0 ? 1 : 0);
}
