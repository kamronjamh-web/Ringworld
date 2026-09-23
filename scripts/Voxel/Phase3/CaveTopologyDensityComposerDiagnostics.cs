using System;

namespace RingworldTechDemo.Voxel.Phase3;

public static class CaveTopologyDensityComposerDiagnostics
{
    public static bool RunSelfTest(out string summary)
    {
        try
        {
            const long fullProvinceCount = 24;
            long aroundLength = CaveTopologyPartition.ProvinceAroundMeters * fullProvinceCount
                + CaveTopologyPartition.CellAroundMeters * 24;
            CaveGenerationContract contract = CaveGenerationContract.Create(
                7331);
            CaveTopologyDensityComposer composer = CreateComposer(contract, aroundLength);
            CaveTopologyDensityComposer repeated = CreateComposer(contract, aroundLength);
            CaveTopologyDensityComposer changedSeed = CreateComposer(
                CaveGenerationContract.Create(7332),
                aroundLength);
            CaveTopologyDensityComposer changedRing = CreateComposer(contract, aroundLength + 1);
            if (composer.Identity != repeated.Identity
                || composer.Identity == changedSeed.Identity
                || composer.Identity == changedRing.Identity)
            {
                throw new InvalidOperationException("topology density composer identity isolation failed");
            }
            Throws<ArgumentException>(() => new CaveTopologyDensityComposer(
                composer.Features,
                changedSeed.WallNoise));

            CaveProvinceDistribution distribution = composer.Features.Maps.Graphs.Anchors.Distribution;
            CaveProvincePlan active = FindActivePlan(distribution, partialOnly: false);
            CaveCellBoundaryConnectionMap map = composer.Features.Maps.Resolve(active);
            CaveChamberAnchor anchor = map.Graph.Anchors[0];
            CaveTopologyDensityRegion anchorRegion = CaptureAround(composer, anchor, 64);
            CaveTopologyDensityResult anchorDensity = anchorRegion.Sample(
                anchor.Around,
                anchor.Vertical,
                anchor.Across);
            if (!anchorDensity.HasContribution
                || anchorDensity.TopologyDensityMeters < CaveTopologyDensityComposer.ChamberConnectivityRadiusMeters
                || anchorDensity.FinalDensityMeters != anchorDensity.TopologyDensityMeters
                || anchorDensity.AppliedWallDisplacementMeters != 0.0
                || anchorDensity.DominantFeatureKey == 0)
            {
                throw new InvalidOperationException("chamber scaffold density or protected core failed");
            }
            AssertTerrainContributionPath(anchorRegion);
            AssertSameRegion(anchorRegion, CaptureAround(repeated, anchor, 64), "repeated capture");
            if (anchorRegion.Identity == CaptureAround(changedSeed, anchor, 64).Identity)
                throw new InvalidOperationException("shared terrain seed did not change region density identity");

            long sharedAround=anchor.Around+55;
            CaveTopologyDensityRegion boundaryLeft=composer.CaptureRegion(
                anchor.Around-1,anchor.Vertical-1,anchor.Across-1,56,2,2);
            CaveTopologyDensityRegion boundaryRight=composer.CaptureRegion(
                sharedAround,anchor.Vertical-1,anchor.Across-1,56,2,2);
            CaveTopologyDensityResult leftBoundary=boundaryLeft.Sample(
                sharedAround,anchor.Vertical,anchor.Across);
            CaveTopologyDensityResult rightBoundary=boundaryRight.Sample(
                sharedAround,anchor.Vertical,anchor.Across);
            if(leftBoundary!=rightBoundary||!leftBoundary.HasContribution)
                throw new InvalidOperationException("neighboring density regions disagreed inside the wall-influence halo");

            CaveCellBoundaryConnection connection = map[0];
            CaveTopologyCell owner = distribution.Partition.ResolveNormalizedCell(
                connection.Face.Owner.Around,
                connection.Face.Owner.Vertical,
                connection.Face.Owner.Across);
            long corridorAround = owner.AroundOrigin + owner.AroundExtent / 2;
            long corridorVertical = owner.VerticalOrigin + owner.VerticalExtent / 2;
            long corridorAcross = owner.AcrossOrigin + owner.AcrossExtent / 2;
            CaveTopologyDensityRegion corridorRegion = composer.CaptureRegion(
                corridorAround - 32,
                corridorVertical - 32,
                corridorAcross - 32,
                64,
                64,
                64);
            CaveTopologyDensityResult corridorDensity = corridorRegion.Sample(
                corridorAround,
                corridorVertical,
                corridorAcross);
            ValidateResult(corridorDensity);
            AssertTerrainContributionPath(corridorRegion);

            CaveProvincePlan partial = FindActivePlan(distribution, partialOnly: true);
            CaveChamberAnchor partialAnchor = composer.Features.Maps.Resolve(partial).Graph.Anchors[0];
            CaveTopologyDensityRegion partialRegion = CaptureAround(composer, partialAnchor, 32);
            CaveTopologyDensityResult partialDensity = partialRegion.Sample(
                partialAnchor.Around,
                partialAnchor.Vertical,
                partialAnchor.Across);
            if (!partialDensity.HasContribution
                || partialRegion.FeatureCount <= 0
                || partialRegion.Features.CandidateProvinceCount > CaveRegionFeatureSnapshotGenerator.MaximumCandidateProvinces)
            {
                throw new InvalidOperationException("partial seam province density capture failed");
            }
            AssertTerrainContributionPath(partialRegion);
            AssertCompiledTerrainLatticePlan(composer, aroundLength);

            CaveTopologyDensityResult wrapped = anchorRegion.Sample(
                anchor.Around + aroundLength,
                anchor.Vertical,
                anchor.Across);
            if (wrapped != anchorDensity)
                throw new InvalidOperationException("topology density changed at the around-ring wrap");
            Throws<ArgumentOutOfRangeException>(() => anchorRegion.Sample(
                anchor.Around,
                anchor.Vertical + 65,
                anchor.Across));
            Throws<ArgumentOutOfRangeException>(() => composer.CaptureRegion(
                new CaveRegionFeatureRequest(anchor.Around, anchor.Vertical, anchor.Across, 1, 1, 1, 0)));

            var requests = new[] { anchorRegion.Request, corridorRegion.Request, partialRegion.Request };
            var expected = new ulong[requests.Length];
            for (int index = 0; index < requests.Length; index++)
                expected[index] = composer.CaptureRegion(requests[index]).Identity;
            Array.Reverse(requests);
            foreach (CaveRegionFeatureRequest request in requests)
            {
                CaveTopologyDensityRegion captured = composer.CaptureRegion(request);
                bool found = false;
                for (int index = 0; index < expected.Length; index++)
                    found |= captured.Identity == expected[index];
                if (!found)
                    throw new InvalidOperationException("reverse capture order changed a topology density region");
            }

            summary = $"identity={composer.Identity:X16} region={anchorRegion.Identity:X16} features={anchorRegion.FeatureCount} partial={partialRegion.FeatureCount}";
            return true;
        }
        catch (Exception exception)
        {
            summary = exception.Message;
            return false;
        }
    }

    private static CaveTopologyDensityRegion CaptureAround(
        CaveTopologyDensityComposer composer,
        CaveChamberAnchor anchor,
        long halfExtent)
    {
        return composer.CaptureRegion(
            anchor.Around - halfExtent,
            anchor.Vertical - halfExtent,
            anchor.Across - halfExtent,
            halfExtent * 2,
            halfExtent * 2,
            halfExtent * 2);
    }

    private static CaveProvincePlan FindActivePlan(
        CaveProvinceDistribution distribution,
        bool partialOnly)
    {
        long start = partialOnly ? distribution.Partition.AroundProvinceCount - 1 : 0;
        long end = partialOnly
            ? distribution.Partition.AroundProvinceCount
            : distribution.Partition.AroundProvinceCount - 1;
        for (long vertical = -64; vertical < 0; vertical++)
        for (long around = start; around < end; around++)
        for (long across = -16; across <= 16; across++)
        {
            CaveProvincePlan plan = distribution.ResolvePlan(
                new CaveTopologyProvinceAddress(around, vertical, across));
            if (plan.Active) return plan;
        }
        throw new InvalidOperationException("could not find an active cave density province");
    }

    private static void ValidateResult(CaveTopologyDensityResult result)
    {
        if (!result.HasContribution
            || !double.IsFinite(result.TopologyDensityMeters)
            || !double.IsFinite(result.FinalDensityMeters)
            || result.NormalizedWallNoise is < -1.0 or > 1.0
            || result.WallInfluenceMask is < 0.0 or > 1.0
            || Math.Abs(result.AppliedWallDisplacementMeters)
                > CaveTopologyDensityComposer.WallProfile.MaximumDisplacementMeters)
        {
            throw new InvalidOperationException("topology density result exceeded its bounds");
        }
    }

    private static void AssertTerrainContributionPath(CaveTopologyDensityRegion region)
    {
        CaveRegionFeatureRequest request = region.Request;
        for (int z = 0; z <= 4; z++)
        for (int y = 0; y <= 4; y++)
        for (int x = 0; x <= 4; x++)
        {
            long around = checked(request.Around + request.AroundExtent * x / 4);
            long vertical = checked(request.Vertical + request.VerticalExtent * y / 4);
            long across = checked(request.Across + request.AcrossExtent * z / 4);
            CaveTopologyDensityResult exact = region.Sample(around, vertical, across);
            CaveTopologyDensityResult terrain = region.SampleTerrainContribution(
                around,
                vertical,
                across);
            if (exact.HasContribution ? terrain != exact : terrain.HasContribution)
            {
                throw new InvalidOperationException(
                    $"terrain contribution path diverged at {around}/{vertical}/{across}: exact={exact}, terrain={terrain}");
            }
        }
    }

    private static void AssertCompiledTerrainLatticePlan(
        CaveTopologyDensityComposer composer,
        long aroundLength)
    {
        const long around = 1_000;
        var request = new CaveRegionFeatureRequest(around, -100, -100, 100, 200, 200,
            CaveTopologyDensityComposer.MaximumInfluenceHaloMeters);
        CaveRegionFeature crossingCorridor = new(
            CaveRegionFeatureKind.CorridorSegment, 11, 11, default, 0, 0,
            900, 0, 0, 1_200, 0, 0);
        CaveRegionFeature distantCorridor = new(
            CaveRegionFeatureKind.CorridorSegment, 12, 12, default, 0, 0,
            1_600, 0, 0, 1_700, 0, 0);
        CaveFeatureShape edgeGiant = Shape(21, 1_170, 0, 0, 80.0);
        CaveFeatureShape distantGiant = Shape(22, 1_500, 0, 0, 80.0);
        CaveTopologyDensityRegion region = CreateRegion(
            composer, request,
            new[] { crossingCorridor, distantCorridor },
            new[] { edgeGiant, distantGiant });

        if (region.TerrainConnectivityCount != 1
            || region.TerrainFamilyShapeCount != 1
            || region.FamilyShapeCount != 2
            || region.FamilyShapes.AsSpan()[0] != edgeGiant
            || region.FamilyShapes.AsSpan()[1] != distantGiant)
        {
            throw new InvalidOperationException("compiled terrain lattice changed discovery descriptors or failed to cull nonintersecting cave descriptors");
        }
        AssertTerrainContributionPath(region);

        var wrappedRequest = request with { Around = aroundLength - 20, AroundExtent = 40 };
        CaveTopologyDensityRegion wrapped = CreateRegion(
            composer, wrappedRequest, Array.Empty<CaveRegionFeature>(),
            new[] { Shape(31, 5, 0, 0, 80.0) });
        if (wrapped.TerrainFamilyShapeCount != 1)
            throw new InvalidOperationException("compiled terrain lattice missed a wrapped family bound");
        AssertTerrainContributionPath(wrapped);
    }

    private static CaveTopologyDensityRegion CreateRegion(
        CaveTopologyDensityComposer composer,
        CaveRegionFeatureRequest request,
        CaveRegionFeature[] features,
        CaveFeatureShape[] shapes) => new(
            composer,
            1,
            request,
            new CaveRegionFeatureSnapshot(1, request, 0, 0, features),
            new CaveFeatureFamilySnapshot(1, shapes));

    private static CaveFeatureShape Shape(
        ulong key,
        long around,
        long vertical,
        long across,
        double radius) => new(
            CaveFeatureFamily.Megacavern,
            CaveFeatureVariant.None,
            key,
            key,
            default,
            around,
            vertical,
            across,
            radius,
            radius,
            radius,
            0.0,
            1.0,
            default);

    private static void AssertSameRegion(
        CaveTopologyDensityRegion expected,
        CaveTopologyDensityRegion actual,
        string context)
    {
        if (expected.Identity != actual.Identity
            || expected.Request != actual.Request
            || expected.FeatureCount != actual.FeatureCount)
        {
            throw new InvalidOperationException($"{context} changed topology density capture");
        }
    }

    private static CaveTopologyDensityComposer CreateComposer(
        CaveGenerationContract contract,
        long aroundLength)
    {
        var features = new CaveRegionFeatureSnapshotGenerator(
            new CaveCellBoundaryConnectionMapGenerator(
                new CaveChamberGraphGenerator(
                    new CaveChamberAnchorGenerator(
                        new CaveProvinceDistribution(
                            new CaveTopologyPartition(contract, aroundLength))))));
        return new CaveTopologyDensityComposer(
            features,
            new CaveWallNoiseApplicator(contract, aroundLength));
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"expected {typeof(TException).Name}");
    }
}
