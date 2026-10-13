using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>
/// One immutable designed-cave density evaluation. Positive density is open
/// space; negative density is solid, matching the smooth terrain field.
/// </summary>
public readonly record struct CaveTopologyDensityResult(
    double TopologyDensityMeters,
    double NormalizedWallNoise,
    double WallInfluenceMask,
    double AppliedWallDisplacementMeters,
    double FinalDensityMeters,
    ulong DominantFeatureKey,
    bool HasContribution);

/// <summary>
/// Composes deterministic cave-family SDFs over the connected topology graph.
/// Corridors remain max-unioned so retained solids cannot sever every route.
/// </summary>
public sealed class CaveTopologyDensityComposer
{
    public const int Version = 5;
    public const int PrimitiveDistanceVersion = 1;
    public const double ChamberConnectivityRadiusMeters = 48.0;
    public const double CorridorConnectivityRadiusMeters = 16.0;
    public const long ConnectivityInfluenceHaloMeters = 56;
    public const long MaximumInfluenceHaloMeters = CaveFeatureFamilyGenerator.MaximumInfluenceMeters;

    private const ulong IdentitySalt = 0x434F4D504F534531UL;
    private const ulong RegionIdentitySalt = 0x434F4D5052454731UL;
    public static readonly CaveWallNoiseProfile WallProfile = new(
        MaximumDisplacementMeters: 4.0,
        ProtectedOpenCoreMeters: 8.0,
        WallInfluenceMeters: 8.0);

    public CaveRegionFeatureSnapshotGenerator Features { get; }
    public CaveFeaturePlacementPolicy Placement { get; }
    public CaveFeatureFamilyGenerator Families { get; }
    public CaveWallNoiseApplicator WallNoise { get; }
    public ulong Identity { get; }

    public CaveTopologyDensityComposer(
        CaveRegionFeatureSnapshotGenerator features,
        CaveWallNoiseApplicator wallNoise,
        CaveFeaturePlacementPolicy? placement = null)
    {
        Features = features ?? throw new ArgumentNullException(nameof(features));
        Placement = placement ?? CaveFeaturePlacementPolicy.UnboundedForDiagnostics;
        Families = new CaveFeatureFamilyGenerator(Features, Placement);
        WallNoise = wallNoise ?? throw new ArgumentNullException(nameof(wallNoise));
        CaveTopologyPartition partition = features.Maps.Graphs.Anchors.Distribution.Partition;
        if (partition.Contract != wallNoise.Contract
            || partition.AroundLengthMeters != wallNoise.AroundLengthMeters)
        {
            throw new ArgumentException(
                "Cave topology and wall noise must belong to the same cave world.",
                nameof(wallNoise));
        }

        ulong identity = MixIdentity(features.Identity, IdentitySalt);
        identity = MixIdentity(identity, wallNoise.Identity);
        identity = MixIdentity(identity, Families.Identity);
        identity = MixIdentity(identity, Placement.Identity);
        identity = MixIdentity(identity, Version);
        identity = MixIdentity(identity, PrimitiveDistanceVersion);
        identity = MixIdentity(
            identity,
            BitConverter.DoubleToInt64Bits(ChamberConnectivityRadiusMeters));
        identity = MixIdentity(
            identity,
            BitConverter.DoubleToInt64Bits(CorridorConnectivityRadiusMeters));
        identity = MixIdentity(identity, ConnectivityInfluenceHaloMeters);
        identity = MixIdentity(identity, MaximumInfluenceHaloMeters);
        identity = MixIdentity(
            identity,
            BitConverter.DoubleToInt64Bits(WallProfile.MaximumDisplacementMeters));
        identity = MixIdentity(
            identity,
            BitConverter.DoubleToInt64Bits(WallProfile.ProtectedOpenCoreMeters));
        identity = MixIdentity(
            identity,
            BitConverter.DoubleToInt64Bits(WallProfile.WallInfluenceMeters));
        Identity = Avalanche(identity);
    }

    /// <summary>
    /// Captures topology once for a closed sampling region. The supplied feature
    /// request must carry the exact influence halo declared by this composer.
    /// </summary>
    public CaveTopologyDensityRegion CaptureRegion(CaveRegionFeatureRequest request)
    {
        long timingStart = Stopwatch.GetTimestamp();
        try
        {
        if (request.Halo != MaximumInfluenceHaloMeters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Topology density regions require a {MaximumInfluenceHaloMeters} m feature halo.");
        }
        // Keep per-voxel route evaluation local. The expanded capture exists
        // only long enough to resolve anchor-owned massive-family descriptors.
        CaveRegionFeatureSnapshot localSnapshot = Features.Capture(
            request with { Halo = ConnectivityInfluenceHaloMeters });
        CaveRegionFeatureSnapshot familySourceSnapshot = Features.Capture(request);
        CaveFeatureFamilySnapshot familySnapshot = Families.Resolve(familySourceSnapshot);
        ulong regionIdentity = MixIdentity(Identity, RegionIdentitySalt);
        regionIdentity = MixIdentity(regionIdentity, localSnapshot.Identity);
        regionIdentity = MixIdentity(regionIdentity, localSnapshot.Count);
        regionIdentity = MixIdentity(regionIdentity, familySourceSnapshot.Identity);
        regionIdentity = MixIdentity(regionIdentity, familySourceSnapshot.Count);
        regionIdentity = MixIdentity(regionIdentity, familySnapshot.Identity);
        return new CaveTopologyDensityRegion(
            this,
            Avalanche(regionIdentity),
            request,
            localSnapshot,
            familySnapshot);
        }
        finally
        {
            CaveGenerationPerformanceTelemetry.RecordRegionCapture(
                Identity,
                Stopwatch.GetTimestamp() - timingStart);
        }
    }

    public CaveTopologyDensityRegion CaptureRegion(
        long around,
        long vertical,
        long across,
        long aroundExtent,
        long verticalExtent,
        long acrossExtent)
    {
        return CaptureRegion(new CaveRegionFeatureRequest(
            around,
            vertical,
            across,
            aroundExtent,
            verticalExtent,
            acrossExtent,
            MaximumInfluenceHaloMeters));
    }

    internal CaveTopologyDensityResult Sample(
        CaveTopologyDensityRegion region,
        long around,
        long vertical,
        long across)
    {
        if (!ReferenceEquals(region.Composer, this))
            throw new ArgumentException("Cave density region belongs to another composer.", nameof(region));
        if (!region.ContainsSample(around, vertical, across))
            throw new ArgumentOutOfRangeException(nameof(around), "Point lies outside the captured cave sampling region.");

        double topologyDensity = double.NegativeInfinity;
        ulong dominantFeatureKey = 0;
        foreach (CaveRegionFeature feature in region.Features.AsSpan())
        {
            double radius = feature.Kind == CaveRegionFeatureKind.ChamberAnchor
                ? ChamberConnectivityRadiusMeters : CorridorConnectivityRadiusMeters;
            if (!Placement.PermitsConnectivity(feature, radius))
                continue;
            double candidate = feature.Kind switch
            {
                CaveRegionFeatureKind.ChamberAnchor => ChamberDensity(
                    around,
                    vertical,
                    across,
                    feature),
                CaveRegionFeatureKind.CorridorSegment => CorridorDensity(
                    around,
                    vertical,
                    across,
                    feature),
                _ => throw new InvalidOperationException("Unknown cave region feature kind.")
            };
            if (candidate > topologyDensity
                || (candidate == topologyDensity && feature.Key < dominantFeatureKey))
            {
                topologyDensity = candidate;
                dominantFeatureKey = feature.Key;
            }
        }

        // The small anchor cores and corridors remain part of the max-union so
        // retained solids can shape each large volume without isolating it from
        // the deterministic graph. Family structures remain visible beyond the
        // 48 m anchor core.
        foreach (CaveFeatureShape shape in region.FamilyShapes.AsSpan())
        {
            if (!Placement.Permits(shape))
                continue;
            double candidate = FamilyDensity(around, vertical, across, shape);
            if (candidate > topologyDensity
                || (candidate == topologyDensity && shape.Key < dominantFeatureKey))
            {
                topologyDensity = candidate;
                dominantFeatureKey = shape.Key;
            }
        }

        if (double.IsNegativeInfinity(topologyDensity)
            || topologyDensity <= -WallProfile.WallInfluenceMeters)
        {
            return new CaveTopologyDensityResult(
                topologyDensity,
                0.0,
                0.0,
                0.0,
                topologyDensity,
                dominantFeatureKey,
                false);
        }

        CaveWallNoiseResult wall = WallNoise.Apply(
            around,
            vertical,
            across,
            topologyDensity,
            WallProfile);
        return new CaveTopologyDensityResult(
            topologyDensity,
            wall.NormalizedNoise,
            wall.InfluenceMask,
            wall.AppliedDisplacementMeters,
            wall.FinalDensityMeters,
            dominantFeatureKey,
            true);
    }

    /// <summary>
    /// Exact contributing-density path used by terrain lattice builders. It may
    /// discard candidates proven to be at or beyond the wall-influence band,
    /// because those candidates cannot alter the composed terrain density.
    /// The public diagnostic sample above deliberately retains its full
    /// out-of-range topology result.
    /// </summary>
    internal CaveTopologyDensityResult SampleTerrainContribution(
        CaveTopologyDensityRegion region,
        long around,
        long vertical,
        long across,
        bool regionValidated = false)
    {
        if (!ReferenceEquals(region.Composer, this))
            throw new ArgumentException("Cave density region belongs to another composer.", nameof(region));
        // Lattice builders prove containment once per lattice (see
        // CaveTopologyDensityRegion.RequireContainsLattice) and skip the
        // per-sample check; every other caller keeps the strict check.
        if (!regionValidated && !region.ContainsSample(around, vertical, across))
            throw new ArgumentOutOfRangeException(nameof(around), "Point lies outside the captured cave sampling region.");

        double topologyDensity = double.NegativeInfinity;
        ulong dominantFeatureKey = 0;
        ReadOnlySpan<CaveTerrainConnectivityEvaluation> connectivity = region.TerrainConnectivityEvaluations;
        for (int index = 0; index < connectivity.Length; index++)
        {
            ref readonly CaveTerrainConnectivityEvaluation evaluation = ref connectivity[index];
            if (!ConnectivityCanContribute(in evaluation, around, vertical, across))
                continue;
            double candidate = evaluation.Feature.Kind switch
            {
                CaveRegionFeatureKind.ChamberAnchor => ChamberDensity(
                    around, vertical, across, evaluation.Feature),
                CaveRegionFeatureKind.CorridorSegment => CorridorDensity(
                    around, vertical, across, in evaluation),
                _ => throw new InvalidOperationException("Unknown cave region feature kind.")
            };
            if (candidate > topologyDensity
                || (candidate == topologyDensity && evaluation.Feature.Key < dominantFeatureKey))
            {
                topologyDensity = candidate;
                dominantFeatureKey = evaluation.Feature.Key;
            }
        }

        ReadOnlySpan<CaveFamilyDensityEvaluation> families = region.TerrainFamilyEvaluations;
        for (int index = 0; index < families.Length; index++)
        {
            ref readonly CaveFamilyDensityEvaluation evaluation = ref families[index];
            double aroundOffset = AroundDelta(around, evaluation.Shape.Around);
            double verticalOffset = Difference(vertical, evaluation.Shape.Vertical);
            double acrossOffset = Difference(across, evaluation.Shape.Across);
            if (Math.Abs(aroundOffset) >= evaluation.AroundReach
                || Math.Abs(verticalOffset) >= evaluation.VerticalReach
                || Math.Abs(acrossOffset) >= evaluation.AcrossReach)
                continue;
            double candidate = EvaluateFamilyDensityAtOffset(
                in evaluation,
                aroundOffset,
                verticalOffset,
                acrossOffset);
            if (candidate > topologyDensity
                || (candidate == topologyDensity && evaluation.Shape.Key < dominantFeatureKey))
            {
                topologyDensity = candidate;
                dominantFeatureKey = evaluation.Shape.Key;
            }
        }

        if (double.IsNegativeInfinity(topologyDensity)
            || topologyDensity <= -WallProfile.WallInfluenceMeters)
        {
            return new CaveTopologyDensityResult(
                topologyDensity, 0.0, 0.0, 0.0, topologyDensity,
                dominantFeatureKey, false);
        }

        CaveWallNoiseResult wall = WallNoise.Apply(
            around,
            vertical,
            across,
            topologyDensity,
            WallProfile);
        return new CaveTopologyDensityResult(
            topologyDensity,
            wall.NormalizedNoise,
            wall.InfluenceMask,
            wall.AppliedDisplacementMeters,
            wall.FinalDensityMeters,
            dominantFeatureKey,
            true);
    }

    /// <summary>
    /// Conservative interval bound of the terrain-contribution topology density
    /// (the max-union <see cref="SampleTerrainContribution"/> feeds into wall
    /// noise) over the closed integer box [start, start + extent] on every
    /// axis. <paramref name="upper"/> bounds every candidate from above, so
    /// <c>upper &lt;= -WallInfluenceMeters</c> proves no sample contributes.
    /// <paramref name="lower"/> bounds the density from below using only
    /// contributors that no sample in the box can skip, and is -infinity when
    /// there are none. It is only a classification proof: callers must never use
    /// it to fill densities. Returns false when the around interval cannot be
    /// expressed as one unwrapped interval relative to a contributor.
    /// </summary>
    internal bool TryBoundTerrainTopology(
        CaveTopologyDensityRegion region,
        long aroundStart,
        long verticalStart,
        long acrossStart,
        long extent,
        out double lower,
        out double upper)
    {
        const double margin = 1e-3;
        lower = double.NegativeInfinity;
        upper = double.NegativeInfinity;
        if (!ReferenceEquals(region.Composer, this))
            throw new ArgumentException("Cave density region belongs to another composer.", nameof(region));
        double half = extent * 0.5;
        double halfDiagonal = half * Math.Sqrt(3.0);
        long aroundEnd = unchecked(aroundStart + extent);

        ReadOnlySpan<CaveTerrainConnectivityEvaluation> connectivity = region.TerrainConnectivityEvaluations;
        for (int index = 0; index < connectivity.Length; index++)
        {
            ref readonly CaveTerrainConnectivityEvaluation evaluation = ref connectivity[index];
            CaveRegionFeature feature = evaluation.Feature;
            double a0 = AroundDelta(aroundStart, feature.AroundA);
            if (AroundDelta(aroundEnd, feature.AroundA) != a0 + extent)
                return false;
            double y0 = Difference(verticalStart, feature.VerticalA);
            double x0 = Difference(acrossStart, feature.AcrossA);
            double a1 = a0 + extent, y1 = y0 + extent, x1 = x0 + extent;

            double candidateUpper;
            double candidateLower;
            if (feature.Kind == CaveRegionFeatureKind.ChamberAnchor)
            {
                // Exact: density is a radius minus the distance to the anchor.
                double nearest = Math.Sqrt(
                    NearestAxisSquared(a0, a1) + NearestAxisSquared(y0, y1) + NearestAxisSquared(x0, x1));
                double farthest = Math.Sqrt(
                    FarthestAxisSquared(a0, a1) + FarthestAxisSquared(y0, y1) + FarthestAxisSquared(x0, x1));
                candidateUpper = ChamberConnectivityRadiusMeters - nearest;
                candidateLower = ChamberConnectivityRadiusMeters - farthest;
            }
            else
            {
                // Distance to a segment is convex, so its maximum over the box is
                // at a corner; its minimum is bounded by the 1-Lipschitz centre.
                double farthest = 0.0;
                for (int corner = 0; corner < 8; corner++)
                {
                    farthest = Math.Max(farthest, SegmentDistance(
                        (corner & 1) == 0 ? a0 : a1,
                        (corner & 2) == 0 ? y0 : y1,
                        (corner & 4) == 0 ? x0 : x1,
                        in evaluation));
                }
                double centre = SegmentDistance(a0 + half, y0 + half, x0 + half, in evaluation);
                candidateUpper = CorridorConnectivityRadiusMeters - Math.Max(0.0, centre - halfDiagonal);
                candidateLower = CorridorConnectivityRadiusMeters - farthest;
            }
            upper = Math.Max(upper, candidateUpper);

            double reach = evaluation.Reach;
            bool neverSkipped = a0 > Math.Min(0.0, evaluation.SegmentAround) - reach
                && a1 < Math.Max(0.0, evaluation.SegmentAround) + reach
                && y0 > Math.Min(0.0, evaluation.SegmentVertical) - reach
                && y1 < Math.Max(0.0, evaluation.SegmentVertical) + reach
                && x0 > Math.Min(0.0, evaluation.SegmentAcross) - reach
                && x1 < Math.Max(0.0, evaluation.SegmentAcross) + reach;
            if (neverSkipped)
                lower = Math.Max(lower, candidateLower);
        }

        ReadOnlySpan<CaveFamilyDensityEvaluation> families = region.TerrainFamilyEvaluations;
        for (int index = 0; index < families.Length; index++)
        {
            ref readonly CaveFamilyDensityEvaluation evaluation = ref families[index];
            CaveFeatureShape shape = evaluation.Shape;
            double a0 = AroundDelta(aroundStart, shape.Around);
            if (AroundDelta(aroundEnd, shape.Around) != a0 + extent)
                return false;
            double y0 = Difference(verticalStart, shape.Vertical);
            double x0 = Difference(acrossStart, shape.Across);
            double centreDensity = EvaluateFamilyDensityAtOffset(
                in evaluation, a0 + half, y0 + half, x0 + half);
            double slack = FamilyLipschitzBound(shape) * halfDiagonal;
            upper = Math.Max(upper, centreDensity + slack);

            bool neverSkipped =
                Math.Max(Math.Abs(a0), Math.Abs(a0 + extent)) < evaluation.AroundReach
                && Math.Max(Math.Abs(y0), Math.Abs(y0 + extent)) < evaluation.VerticalReach
                && Math.Max(Math.Abs(x0), Math.Abs(x0 + extent)) < evaluation.AcrossReach;
            if (neverSkipped)
                lower = Math.Max(lower, centreDensity - slack);
        }

        if (!double.IsNegativeInfinity(upper)) upper += margin;
        if (!double.IsNegativeInfinity(lower)) lower -= margin;
        return true;
    }

    private static double NearestAxisSquared(double low, double high)
    {
        double distance = low > 0.0 ? low : high < 0.0 ? -high : 0.0;
        return distance * distance;
    }

    private static double FarthestAxisSquared(double low, double high)
    {
        double distance = Math.Max(Math.Abs(low), Math.Abs(high));
        return distance * distance;
    }

    private static double SegmentDistance(
        double pointAround,
        double pointVertical,
        double pointAcross,
        in CaveTerrainConnectivityEvaluation evaluation)
    {
        double parameter = evaluation.LengthSquared <= 0.0
            ? 0.0
            : Math.Clamp(
                (pointAround * evaluation.SegmentAround
                    + pointVertical * evaluation.SegmentVertical
                    + pointAcross * evaluation.SegmentAcross) / evaluation.LengthSquared,
                0.0,
                1.0);
        double aroundDistance = pointAround - evaluation.SegmentAround * parameter;
        double verticalDistance = pointVertical - evaluation.SegmentVertical * parameter;
        double acrossDistance = pointAcross - evaluation.SegmentAcross * parameter;
        return Math.Sqrt(
            aroundDistance * aroundDistance
            + verticalDistance * verticalDistance
            + acrossDistance * acrossDistance);
    }

    /// <summary>
    /// Lipschitz constant (w.r.t. Euclidean distance in around/vertical/across)
    /// of <see cref="EvaluateFamilyDensityAtOffset(CaveFeatureShape,double,double,double)"/>.
    /// Tilt is a rotation of the around/vertical plane (an isometry), and
    /// min/max/abs never increase a Lipschitz constant, so only the sloped
    /// terms need a constant above 1: the collapsed-region rubble plane and the
    /// Basin, Buttress and Ruin landmark terms. Mirrors
    /// <see cref="EvaluateFamilyBaseDensity"/> and <see cref="ApplyLandmarkDensity"/>.
    /// </summary>
    internal static double FamilyLipschitzBound(CaveFeatureShape shape)
    {
        double bound = shape.Family == CaveFeatureFamily.CollapsedRegion
            ? Math.Sqrt(1.0 + CaveFeatureFamilyGenerator.CollapsedSlope * CaveFeatureFamilyGenerator.CollapsedSlope)
            : 1.0;
        CaveLandmarkTemplate landmark = shape.Landmark;
        if (!landmark.IsDefined) return bound;
        switch (landmark.Operation)
        {
            case CaveLandmarkOperation.Basin:
                // floor = vertical + 0.08 * horizontal
                return Math.Max(bound, Math.Sqrt(1.0 + 0.08 * 0.08));
            case CaveLandmarkOperation.Ruin:
                // block shear: vertical + 0.12 * around
                return Math.Max(bound, Math.Sqrt(1.0 + 0.12 * 0.12));
            case CaveLandmarkOperation.Buttress:
            {
                // diagonalWall: across - around * (radiusAcross / radiusAround) * 0.30
                double radiusAround = Math.Max(4.0, shape.RadiusAround * landmark.BoundAround);
                double radiusAcross = Math.Max(4.0, shape.RadiusAcross * landmark.BoundAcross);
                double slope = 0.30 * radiusAcross / radiusAround;
                return Math.Max(bound, Math.Sqrt(1.0 + slope * slope));
            }
            default:
                return bound;
        }
    }

    private double ChamberDensity(
        long around,
        long vertical,
        long across,
        in CaveRegionFeature feature)
    {
        double aroundDelta = AroundDelta(around, feature.AroundA);
        double verticalDelta = Difference(vertical, feature.VerticalA);
        double acrossDelta = Difference(across, feature.AcrossA);
        return ChamberConnectivityRadiusMeters - Math.Sqrt(
            aroundDelta * aroundDelta
            + verticalDelta * verticalDelta
            + acrossDelta * acrossDelta);
    }

    private double CorridorDensity(
        long around,
        long vertical,
        long across,
        in CaveTerrainConnectivityEvaluation evaluation)
    {
        CaveRegionFeature feature = evaluation.Feature;
        double pointAround = AroundDelta(around, feature.AroundA);
        double pointVertical = Difference(vertical, feature.VerticalA);
        double pointAcross = Difference(across, feature.AcrossA);
        double parameter = evaluation.LengthSquared <= 0.0
            ? 0.0
            : Math.Clamp(
                (pointAround * evaluation.SegmentAround
                    + pointVertical * evaluation.SegmentVertical
                    + pointAcross * evaluation.SegmentAcross) / evaluation.LengthSquared,
                0.0,
                1.0);
        double aroundDistance = pointAround - evaluation.SegmentAround * parameter;
        double verticalDistance = pointVertical - evaluation.SegmentVertical * parameter;
        double acrossDistance = pointAcross - evaluation.SegmentAcross * parameter;
        return CorridorConnectivityRadiusMeters - Math.Sqrt(
            aroundDistance * aroundDistance
            + verticalDistance * verticalDistance
            + acrossDistance * acrossDistance);
    }

    private double CorridorDensity(
        long around,
        long vertical,
        long across,
        in CaveRegionFeature feature)
    {
        double pointAround = AroundDelta(around, feature.AroundA);
        double pointVertical = Difference(vertical, feature.VerticalA);
        double pointAcross = Difference(across, feature.AcrossA);
        double segmentAround = AroundDelta(feature.AroundB, feature.AroundA);
        double segmentVertical = Difference(feature.VerticalB, feature.VerticalA);
        double segmentAcross = Difference(feature.AcrossB, feature.AcrossA);
        double lengthSquared = segmentAround * segmentAround
            + segmentVertical * segmentVertical
            + segmentAcross * segmentAcross;
        double parameter = lengthSquared <= 0.0
            ? 0.0
            : Math.Clamp(
                (pointAround * segmentAround
                    + pointVertical * segmentVertical
                    + pointAcross * segmentAcross) / lengthSquared,
                0.0,
                1.0);
        double aroundDistance = pointAround - segmentAround * parameter;
        double verticalDistance = pointVertical - segmentVertical * parameter;
        double acrossDistance = pointAcross - segmentAcross * parameter;
        return CorridorConnectivityRadiusMeters - Math.Sqrt(
            aroundDistance * aroundDistance
            + verticalDistance * verticalDistance
            + acrossDistance * acrossDistance);
    }

    private bool ConnectivityCanContribute(
        in CaveTerrainConnectivityEvaluation evaluation,
        long around,
        long vertical,
        long across)
    {
        CaveRegionFeature feature = evaluation.Feature;
        double pointAround = AroundDelta(around, feature.AroundA);
        double pointVertical = Difference(vertical, feature.VerticalA);
        double pointAcross = Difference(across, feature.AcrossA);
        double reach = evaluation.Reach;
        return pointAround > Math.Min(0.0, evaluation.SegmentAround) - reach
            && pointAround < Math.Max(0.0, evaluation.SegmentAround) + reach
            && pointVertical > Math.Min(0.0, evaluation.SegmentVertical) - reach
            && pointVertical < Math.Max(0.0, evaluation.SegmentVertical) + reach
            && pointAcross > Math.Min(0.0, evaluation.SegmentAcross) - reach
            && pointAcross < Math.Max(0.0, evaluation.SegmentAcross) + reach;
    }

    private double FamilyDensity(long around, long vertical, long across, CaveFeatureShape shape)
    {
        double a = AroundDelta(around, shape.Around);
        double y = Difference(vertical, shape.Vertical);
        double x = Difference(across, shape.Across);
        return EvaluateFamilyDensityAtOffset(shape, a, y, x);
    }

    internal static double EvaluateFamilyDensityAtOffset(
        CaveFeatureShape shape,
        double aroundOffset,
        double verticalOffset,
        double acrossOffset)
    {
        double c = Math.Cos(shape.Tilt), s = Math.Sin(shape.Tilt);
        return EvaluateFamilyDensityAtOffset(
            new CaveFamilyDensityEvaluation(shape, c, s, 0.0, 0.0, 0.0),
            aroundOffset,
            verticalOffset,
            acrossOffset);
    }

    private static double EvaluateFamilyDensityAtOffset(
        in CaveFamilyDensityEvaluation evaluation,
        double aroundOffset,
        double verticalOffset,
        double acrossOffset)
    {
        CaveFeatureShape shape = evaluation.Shape;
        double c = evaluation.Cosine, s = evaluation.Sine;
        double rotatedAround = aroundOffset * c - verticalOffset * s;
        double rotatedVertical = aroundOffset * s + verticalOffset * c;
        double open = EllipsoidDensity(
            rotatedAround,
            rotatedVertical,
            acrossOffset,
            shape.RadiusAround,
            shape.RadiusVertical,
            shape.RadiusAcross);
        double familyDensity = EvaluateFamilyBaseDensity(
            shape,
            rotatedAround,
            rotatedVertical,
            acrossOffset,
            open);
        return ApplyLandmarkDensity(
            familyDensity,
            shape,
            rotatedAround,
            rotatedVertical,
            acrossOffset);
    }

    private static double EvaluateFamilyBaseDensity(
        CaveFeatureShape shape,
        double rotatedAround,
        double rotatedVertical,
        double acrossOffset,
        double open)
    {
        switch (shape.Family)
        {
            case CaveFeatureFamily.GrandChamber:
            {
                if (shape.Variant == CaveFeatureVariant.None)
                    return open;
                if (shape.Variant != CaveFeatureVariant.GrandLayered)
                    throw new InvalidOperationException("Grand chamber has an invalid family variant.");

                double center = shape.RadiusVertical
                    * CaveFeatureFamilyGenerator.GrandLayerCenterFraction;
                double layerRadius = shape.RadiusVertical
                    * CaveFeatureFamilyGenerator.GrandLayerRadiusFraction;
                double lower = EllipsoidDensity(
                    rotatedAround,
                    rotatedVertical + center,
                    acrossOffset,
                    shape.RadiusAround,
                    layerRadius,
                    shape.RadiusAcross);
                double upper = EllipsoidDensity(
                    rotatedAround,
                    rotatedVertical - center,
                    acrossOffset,
                    shape.RadiusAround,
                    layerRadius,
                    shape.RadiusAcross);
                double layeredOpen = Math.Max(lower, upper);
                double floorBand = Math.Abs(rotatedVertical)
                    - shape.RadiusVertical
                        * CaveFeatureFamilyGenerator.GrandFloorHalfThicknessFraction;
                double horizontalDistance = Math.Sqrt(
                    rotatedAround * rotatedAround + acrossOffset * acrossOffset);
                double outsideShaft = shape.RadiusAround
                        * CaveFeatureFamilyGenerator.GrandShaftRadiusFraction
                    - horizontalDistance;
                double retainedFloor = Math.Max(floorBand, outsideShaft);
                return Math.Min(layeredOpen, retainedFloor);
            }
            case CaveFeatureFamily.FaultCavern:
                // Flattened tilted ellipsoid deliberately reads as a fracture.
                return open;
            case CaveFeatureFamily.PillarHall:
            {
                double halfThickness = shape.Detail
                    * CaveFeatureFamilyGenerator.PillarHalfThicknessFraction;
                double gridAround = NearestRepeatDistance(
                    rotatedAround,
                    shape.Detail,
                    0.0) - halfThickness;
                double gridAcross = NearestRepeatDistance(
                    acrossOffset,
                    shape.Detail,
                    0.0) - halfThickness;
                double column = Math.Max(gridAround, gridAcross);
                return Math.Min(open, column);
            }
            case CaveFeatureFamily.ChasmSystem:
            {
                double halfThickness = shape.Detail
                    * CaveFeatureFamilyGenerator.ChasmShelfHalfThicknessFraction;
                double shelfBand = NearestRepeatDistance(
                    rotatedVertical,
                    shape.Detail,
                    0.0) - halfThickness;
                double sideZone = shape.RadiusAcross
                        * CaveFeatureFamilyGenerator.ChasmShelfInnerRadiusFraction
                    - Math.Abs(acrossOffset);
                double sideShelf = Math.Max(shelfBand, sideZone);

                double bridgeBand = NearestRepeatDistance(
                    rotatedVertical,
                    shape.Detail,
                    CaveFeatureFamilyGenerator.ChasmBridgePhase) - halfThickness;
                double bridgeWidth = Math.Abs(acrossOffset)
                    - shape.RadiusAcross
                        * CaveFeatureFamilyGenerator.ChasmBridgeHalfWidthFraction;
                double bridge = Math.Max(bridgeBand, bridgeWidth);
                double retainedWalkways = Math.Min(sideShelf, bridge);
                return Math.Min(open, retainedWalkways);
            }
            case CaveFeatureFamily.UndergroundBasin:
                // A flattened bottom creates a future-water-friendly floor.
                return Math.Min(
                    open,
                    rotatedVertical
                        + shape.RadiusVertical * CaveFeatureFamilyGenerator.BasinFloorFraction);
            case CaveFeatureFamily.CathedralCavern:
                return Math.Min(
                    open,
                    rotatedVertical
                        + shape.RadiusVertical * CaveFeatureFamilyGenerator.CathedralFloorFraction);
            case CaveFeatureFamily.CollapsedRegion:
            {
                double slopeAxis = (shape.Key & 1UL) == 0
                    ? acrossOffset
                    : -acrossOffset;
                double rubble = rotatedVertical
                    + shape.RadiusVertical * CaveFeatureFamilyGenerator.CollapsedFloorFraction
                    - slopeAxis * CaveFeatureFamilyGenerator.CollapsedSlope;
                return Math.Min(open, rubble);
            }
            case CaveFeatureFamily.EngineeredVoid:
                return EngineeredDensity(
                    shape,
                    rotatedAround,
                    rotatedVertical,
                    acrossOffset);
            default:
                return open;
        }
    }

    /// <summary>
    /// Applies the shape's preselected authored landmark. Retained landmarks
    /// use a min intersection and carved landmarks use a max union, while an
    /// ellipsoid mask keeps every operation inside the descriptor envelope.
    /// The graph's anchor/corridor max-union is applied later, so landmark
    /// solids can shape a room without severing its authored route.
    /// </summary>
    private static double ApplyLandmarkDensity(
        double familyDensity,
        CaveFeatureShape shape,
        double around,
        double vertical,
        double across)
    {
        CaveLandmarkTemplate landmark = shape.Landmark;
        if (!landmark.IsDefined) return familyDensity;

        double radiusAround = Math.Max(4.0, shape.RadiusAround * landmark.BoundAround);
        double radiusVertical = Math.Max(4.0, shape.RadiusVertical * landmark.BoundVertical);
        double radiusAcross = Math.Max(4.0, shape.RadiusAcross * landmark.BoundAcross);
        double envelope = EllipsoidDensity(
            around,
            vertical,
            across,
            radiusAround,
            radiusVertical,
            radiusAcross);
        double horizontal = Math.Sqrt(around * around + across * across);
        double minimumRadius = Math.Min(radiusAround, Math.Min(radiusVertical, radiusAcross));

        switch (landmark.Operation)
        {
            case CaveLandmarkOperation.Dome:
            {
                // A bounded apse shell above centre gives cathedral and mega
                // rooms a recognizable nested-dome silhouette.
                double dome = EllipsoidDensity(
                    around,
                    vertical - radiusVertical * 0.12,
                    across,
                    radiusAround * 0.62,
                    radiusVertical * 0.62,
                    radiusAcross * 0.62);
                double shell = Math.Abs(dome) - Math.Max(2.0, minimumRadius * 0.055);
                double clearCentralVolume = radiusAround * 0.24 - horizontal;
                double retained = Math.Max(Math.Max(shell, clearCentralVolume), -envelope);
                return Math.Min(familyDensity, retained);
            }
            case CaveLandmarkOperation.Terrace:
            {
                double slab = Math.Abs(vertical + radiusVertical * 0.18)
                    - Math.Max(2.0, radiusVertical * 0.055);
                double outsideRotunda = radiusAround * 0.38 - horizontal;
                double retained = Math.Max(Math.Max(slab, outsideRotunda), -envelope);
                return Math.Min(familyDensity, retained);
            }
            case CaveLandmarkOperation.RibbedGallery:
            {
                double spacing = Math.Max(12.0, shape.Detail);
                double rib = NearestRepeatDistance(around, spacing, 0.0)
                    - Math.Max(1.5, spacing * 0.075);
                double clearCore = Math.Min(
                    radiusVertical * 0.58 - Math.Abs(vertical),
                    radiusAcross * 0.58 - Math.Abs(across));
                double retained = Math.Max(Math.Max(rib, clearCore), -envelope);
                return Math.Min(familyDensity, retained);
            }
            case CaveLandmarkOperation.Basin:
            {
                double floor = vertical + radiusVertical * 0.48
                    + horizontal * 0.08;
                double retained = Math.Max(floor, -envelope);
                return Math.Min(familyDensity, retained);
            }
            case CaveLandmarkOperation.Buttress:
            {
                double thickness = Math.Max(2.0, minimumRadius * 0.09);
                double sidePair = Math.Abs(Math.Abs(around) - radiusAround * 0.48) - thickness;
                double centralBand = Math.Abs(across) - thickness;
                double pairedButtress = Math.Max(sidePair, centralBand);
                double diagonalWall = Math.Abs(
                    across - around * radiusAcross / radiusAround * 0.30) - thickness;
                double clearAnchorCore = Math.Min(radiusAround, radiusAcross) * 0.14
                    - horizontal;
                double retained = Math.Max(
                    Math.Max(Math.Min(pairedButtress, diagonalWall), clearAnchorCore),
                    -envelope);
                return Math.Min(familyDensity, retained);
            }
            case CaveLandmarkOperation.Ruin:
            {
                double block = BoxDensity(
                    around + radiusAround * 0.18,
                    vertical + radiusVertical * 0.34 + around * 0.12,
                    across - radiusAcross * 0.11,
                    radiusAround * 0.22,
                    radiusVertical * 0.18,
                    radiusAcross * 0.26);
                double retained = Math.Max(-block, -envelope);
                return Math.Min(familyDensity, retained);
            }
            default:
                throw new InvalidOperationException("Unknown authored cave landmark operation.");
        }
    }

    private static double EngineeredDensity(
        CaveFeatureShape shape,
        double around,
        double vertical,
        double across)
    {
        double box = BoxDensity(
            around,
            vertical,
            across,
            shape.RadiusAround,
            shape.RadiusVertical,
            shape.RadiusAcross);
        double halfRib = shape.Detail
            * CaveFeatureFamilyGenerator.EngineeredRibHalfThicknessFraction;
        switch (shape.Variant)
        {
            case CaveFeatureVariant.MaintenanceTrunk:
            {
                double ribBand = NearestRepeatDistance(across, shape.Detail, 0.0) - halfRib;
                double wallZone = Math.Min(
                    shape.RadiusVertical * CaveFeatureFamilyGenerator.EngineeredClearanceFraction
                        - Math.Abs(vertical),
                    shape.RadiusAround * CaveFeatureFamilyGenerator.EngineeredClearanceFraction
                        - Math.Abs(around));
                return Math.Min(box, Math.Max(ribBand, wallZone));
            }
            case CaveFeatureVariant.Reservoir:
            {
                double horizontal = 1.0 - Math.Sqrt(
                    around * around / (shape.RadiusAround * shape.RadiusAround)
                    + across * across / (shape.RadiusAcross * shape.RadiusAcross));
                double cylinder = Math.Min(
                    horizontal * Math.Min(shape.RadiusAround, shape.RadiusAcross),
                    shape.RadiusVertical - Math.Abs(vertical));
                double floor = vertical
                    + shape.RadiusVertical * CaveFeatureFamilyGenerator.ReservoirFloorFraction;
                return Math.Min(cylinder, floor);
            }
            case CaveFeatureVariant.Habitat:
            {
                double supportHalf = shape.Detail
                    * CaveFeatureFamilyGenerator.EngineeredSupportHalfThicknessFraction;
                double supportAround = NearestRepeatDistance(
                    around,
                    shape.Detail,
                    0.5) - supportHalf;
                double supportAcross = NearestRepeatDistance(
                    across,
                    shape.Detail,
                    0.5) - supportHalf;
                return Math.Min(box, Math.Max(supportAround, supportAcross));
            }
            case CaveFeatureVariant.TransitGallery:
            {
                double ribBand = NearestRepeatDistance(around, shape.Detail, 0.0) - halfRib;
                double wallZone = Math.Min(
                    shape.RadiusVertical * CaveFeatureFamilyGenerator.EngineeredClearanceFraction
                        - Math.Abs(vertical),
                    shape.RadiusAcross * CaveFeatureFamilyGenerator.EngineeredClearanceFraction
                        - Math.Abs(across));
                return Math.Min(box, Math.Max(ribBand, wallZone));
            }
            default:
                throw new InvalidOperationException("Engineered void has no explicit purpose variant.");
        }
    }

    private static double EllipsoidDensity(
        double around,
        double vertical,
        double across,
        double radiusAround,
        double radiusVertical,
        double radiusAcross)
    {
        double normalized = 1.0 - Math.Sqrt(
            around * around / (radiusAround * radiusAround)
            + vertical * vertical / (radiusVertical * radiusVertical)
            + across * across / (radiusAcross * radiusAcross));
        return normalized * Math.Min(radiusAround, Math.Min(radiusVertical, radiusAcross));
    }

    private static double BoxDensity(
        double around,
        double vertical,
        double across,
        double radiusAround,
        double radiusVertical,
        double radiusAcross) =>
        Math.Min(
            radiusAround - Math.Abs(around),
            Math.Min(
                radiusVertical - Math.Abs(vertical),
                radiusAcross - Math.Abs(across)));

    private static double NearestRepeatDistance(double value, double spacing, double phase)
    {
        double shifted = value / spacing - phase;
        return Math.Abs(shifted - Math.Round(shifted)) * spacing;
    }

    internal double ForwardAroundDistance(long value, long origin)
    {
        long canonicalValue = Mod(value, WallNoise.AroundLengthMeters);
        long canonicalOrigin = Mod(origin, WallNoise.AroundLengthMeters);
        return canonicalValue >= canonicalOrigin
            ? canonicalValue - canonicalOrigin
            : (double)canonicalValue + WallNoise.AroundLengthMeters - canonicalOrigin;
    }

    private double AroundDelta(long value, long origin)
    {
        // Both operands are wrapped into [0, length), so the difference lies in
        // (-length, length) and cannot overflow a long. "2 * delta > length" is
        // exactly "delta > length / 2" (floor) for integers, which avoids the
        // overflow a doubled delta could cause. The corrected delta is within
        // [-length / 2 - 1, length / 2 + 1], also overflow-free.
        long length = WallNoise.AroundLengthMeters;
        long delta = Mod(value, length) - Mod(origin, length);
        long half = length >> 1;
        if (delta > half) delta -= length;
        if (delta < -half) delta += length;
        return (double)delta;
    }

    private static double Difference(long value, long origin)
    {
        long delta = unchecked(value - origin);
        // Signed-overflow check; fall back to the exact wide subtraction.
        if (((value ^ origin) & (value ^ delta)) < 0)
            return (double)((Int128)value - origin);
        return (double)delta;
    }

    private static long Mod(long value, long divisor)
    {
        long remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    internal CaveTerrainLatticePlan CompileTerrainLatticePlan(
        CaveRegionFeatureRequest request,
        CaveRegionFeatureSnapshot features,
        ReadOnlySpan<CaveFamilyDensityEvaluation> familyEvaluations)
    {
        var connectivity = new List<CaveTerrainConnectivityEvaluation>();
        foreach (CaveRegionFeature feature in features.AsSpan())
        {
            double radius = feature.Kind == CaveRegionFeatureKind.ChamberAnchor
                ? ChamberConnectivityRadiusMeters : CorridorConnectivityRadiusMeters;
            if (!Placement.PermitsConnectivity(feature, radius))
                continue;

            double reach = radius + WallProfile.WallInfluenceMeters;
            double segmentAround = feature.Kind == CaveRegionFeatureKind.CorridorSegment
                ? AroundDelta(feature.AroundB, feature.AroundA) : 0.0;
            double segmentVertical = feature.Kind == CaveRegionFeatureKind.CorridorSegment
                ? Difference(feature.VerticalB, feature.VerticalA) : 0.0;
            double segmentAcross = feature.Kind == CaveRegionFeatureKind.CorridorSegment
                ? Difference(feature.AcrossB, feature.AcrossA) : 0.0;
            if (!IntersectsTerrainRequest(
                    request, feature.AroundA,
                    Math.Min(0.0, segmentAround) - reach,
                    Math.Max(0.0, segmentAround) + reach,
                    feature.VerticalA,
                    Math.Min(0.0, segmentVertical) - reach,
                    Math.Max(0.0, segmentVertical) + reach,
                    feature.AcrossA,
                    Math.Min(0.0, segmentAcross) - reach,
                    Math.Max(0.0, segmentAcross) + reach))
            {
                continue;
            }

            double lengthSquared = segmentAround * segmentAround
                + segmentVertical * segmentVertical
                + segmentAcross * segmentAcross;
            connectivity.Add(new CaveTerrainConnectivityEvaluation(
                feature, segmentAround, segmentVertical, segmentAcross,
                lengthSquared,
                reach));
        }

        var families = new List<CaveFamilyDensityEvaluation>();
        foreach (CaveFamilyDensityEvaluation evaluation in familyEvaluations)
        {
            CaveFeatureShape shape = evaluation.Shape;
            if (Placement.Permits(shape)
                && IntersectsTerrainRequest(
                    request, shape.Around,
                    -evaluation.AroundReach, evaluation.AroundReach,
                    shape.Vertical,
                    -evaluation.VerticalReach, evaluation.VerticalReach,
                    shape.Across,
                    -evaluation.AcrossReach, evaluation.AcrossReach))
            {
                families.Add(evaluation);
            }
        }
        return new CaveTerrainLatticePlan(connectivity.ToArray(), families.ToArray());
    }

    // Bounds are intentionally expanded to the wall influence band. They are
    // a conservative rejection test only: every candidate that can affect a
    // terrain lattice point remains in the compiled plan.
    private bool IntersectsTerrainRequest(
        CaveRegionFeatureRequest request,
        long aroundOrigin,
        double aroundLow,
        double aroundHigh,
        long verticalOrigin,
        double verticalLow,
        double verticalHigh,
        long acrossOrigin,
        double acrossLow,
        double acrossHigh)
    {
        return CircularRangeIntersects(request.Around, request.AroundExtent,
                   aroundOrigin, aroundLow, aroundHigh)
            && LinearRangeIntersects(request.Vertical, request.VerticalExtent,
                verticalOrigin, verticalLow, verticalHigh)
            && LinearRangeIntersects(request.Across, request.AcrossExtent,
                acrossOrigin, acrossLow, acrossHigh);
    }

    private bool CircularRangeIntersects(
        long requestStart, long requestExtent,
        long origin, double low, double high)
    {
        long length = WallNoise.AroundLengthMeters;
        if (requestExtent >= length || high - low >= length)
            return true;

        double start = ForwardAroundDistance(requestStart, origin);
        double end = start + requestExtent;
        return RangesIntersect(start, end, low, high)
            || RangesIntersect(start, end, low + length, high + length);
    }

    private static bool LinearRangeIntersects(
        long requestStart, long requestExtent,
        long origin, double low, double high)
    {
        double start = Difference(requestStart, origin);
        double end = start + requestExtent;
        return RangesIntersect(start, end, low, high);
    }

    private static bool RangesIntersect(double firstLow, double firstHigh, double secondLow, double secondHigh) =>
        firstLow <= secondHigh && secondLow <= firstHigh;

    private static ulong MixIdentity(ulong hash, long value) =>
        MixIdentity(hash, unchecked((ulong)value));

    private static ulong MixIdentity(ulong hash, int value) =>
        MixIdentity(hash, unchecked((ulong)(long)value));

    private static ulong MixIdentity(ulong hash, ulong value)
    {
        for (int index = 0; index < 8; index++)
        {
            hash ^= (byte)(value >> (index * 8));
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static ulong Avalanche(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58476d1ce4e5b9UL;
        value ^= value >> 27;
        value *= 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }
}

/// <summary>Immutable cave topology captured once for one meshing/lattice region.</summary>
internal readonly record struct CaveFamilyDensityEvaluation(
    CaveFeatureShape Shape,
    double Cosine,
    double Sine,
    double AroundReach,
    double VerticalReach,
    double AcrossReach)
{
    public static CaveFamilyDensityEvaluation Create(
        CaveFeatureShape shape,
        double wallInfluenceMeters)
    {
        double cosine = Math.Cos(shape.Tilt);
        double sine = Math.Sin(shape.Tilt);
        double minimumRadius = Math.Min(
            shape.RadiusAround,
            Math.Min(shape.RadiusVertical, shape.RadiusAcross));
        double influenceScale = 1.0 + wallInfluenceMeters / minimumRadius;
        double aroundRadius = shape.RadiusAround * influenceScale;
        double verticalRadius = shape.RadiusVertical * influenceScale;
        double acrossRadius = shape.RadiusAcross * influenceScale;
        double absoluteCosine = Math.Abs(cosine);
        double absoluteSine = Math.Abs(sine);
        return new CaveFamilyDensityEvaluation(
            shape,
            cosine,
            sine,
            aroundRadius * absoluteCosine + verticalRadius * absoluteSine,
            aroundRadius * absoluteSine + verticalRadius * absoluteCosine,
            acrossRadius);
    }
}

/// <summary>Precompiled, request-local candidates for terrain lattice sampling.</summary>
internal readonly record struct CaveTerrainConnectivityEvaluation(
    CaveRegionFeature Feature,
    double SegmentAround,
    double SegmentVertical,
    double SegmentAcross,
    double LengthSquared,
    double Reach);

internal sealed class CaveTerrainLatticePlan
{
    public CaveTerrainLatticePlan(
        CaveTerrainConnectivityEvaluation[] connectivity,
        CaveFamilyDensityEvaluation[] families)
    {
        Connectivity = connectivity;
        Families = families;
    }

    public CaveTerrainConnectivityEvaluation[] Connectivity { get; }
    public CaveFamilyDensityEvaluation[] Families { get; }
}

public sealed class CaveTopologyDensityRegion
{
    private readonly CaveFamilyDensityEvaluation[] _familyEvaluations;
    private readonly CaveTerrainLatticePlan _terrainLatticePlan;
    internal CaveTopologyDensityRegion(
        CaveTopologyDensityComposer composer,
        ulong identity,
        CaveRegionFeatureRequest request,
        CaveRegionFeatureSnapshot features,
        CaveFeatureFamilySnapshot familyShapes)
    {
        Composer = composer;
        Identity = identity;
        Request = request;
        Features = features;
        FamilyShapes = familyShapes;
        ReadOnlySpan<CaveFeatureShape> shapes = familyShapes.AsSpan();
        _familyEvaluations = new CaveFamilyDensityEvaluation[shapes.Length];
        for (int index = 0; index < shapes.Length; index++)
            _familyEvaluations[index] = CaveFamilyDensityEvaluation.Create(
                shapes[index],
                CaveTopologyDensityComposer.WallProfile.WallInfluenceMeters);
        _terrainLatticePlan = composer.CompileTerrainLatticePlan(
            request, features, _familyEvaluations);
    }

    internal CaveTopologyDensityComposer Composer { get; }
    public ulong Identity { get; }
    public CaveRegionFeatureSnapshot Features { get; }
    public CaveFeatureFamilySnapshot FamilyShapes { get; }

    public CaveRegionFeatureRequest Request { get; }
    public int FeatureCount => Features.Count;
    public int FamilyShapeCount => FamilyShapes.Count;
    internal ReadOnlySpan<CaveFamilyDensityEvaluation> FamilyEvaluations => _familyEvaluations;
    internal ReadOnlySpan<CaveTerrainConnectivityEvaluation> TerrainConnectivityEvaluations => _terrainLatticePlan.Connectivity;
    internal ReadOnlySpan<CaveFamilyDensityEvaluation> TerrainFamilyEvaluations => _terrainLatticePlan.Families;
    internal int TerrainConnectivityCount => _terrainLatticePlan.Connectivity.Length;
    internal int TerrainFamilyShapeCount => _terrainLatticePlan.Families.Length;
    internal bool HasTerrainContributors =>
        _terrainLatticePlan.Connectivity.Length != 0 || _terrainLatticePlan.Families.Length != 0;

    public CaveTopologyDensityResult Sample(long around, long vertical, long across) =>
        Composer.Sample(this, around, vertical, across);
    internal CaveTopologyDensityResult SampleTerrainContribution(
        long around,
        long vertical,
        long across) => Composer.SampleTerrainContribution(this, around, vertical, across);

    /// <summary>Interval bound over the cube [start, start + extent]; see
    /// <see cref="CaveTopologyDensityComposer.TryBoundTerrainTopology"/>.</summary>
    internal bool TryBoundTerrainTopology(
        long aroundStart,
        long verticalStart,
        long acrossStart,
        long extent,
        out double lower,
        out double upper) => Composer.TryBoundTerrainTopology(
            this, aroundStart, verticalStart, acrossStart, extent, out lower, out upper);

    /// <summary>
    /// Sample path for lattice builders that have already proven containment
    /// with <see cref="RequireContainsLattice"/>; skips the per-sample check.
    /// </summary>
    internal CaveTopologyDensityResult SampleTerrainContributionValidated(
        long around,
        long vertical,
        long across) => Composer.SampleTerrainContribution(this, around, vertical, across, regionValidated: true);

    /// <summary>
    /// Proves once that an axis-aligned lattice (first/last sample coordinates
    /// per axis, around measured along the ring) lies inside this region. The
    /// region is axis-aligned and convex per axis, so the corners suffice.
    /// </summary>
    internal void RequireContainsLattice(
        long firstAround, long lastAround,
        long firstVertical, long lastVertical,
        long firstAcross, long lastAcross)
    {
        for (int corner = 0; corner < 8; corner++)
        {
            long around = (corner & 1) == 0 ? firstAround : lastAround;
            long vertical = (corner & 2) == 0 ? firstVertical : lastVertical;
            long across = (corner & 4) == 0 ? firstAcross : lastAcross;
            if (!ContainsSample(around, vertical, across))
                throw new ArgumentOutOfRangeException(
                    nameof(firstAround),
                    "Lattice lies outside the captured cave sampling region.");
        }
    }

    internal bool ContainsSample(long around, long vertical, long across)
    {
        CaveRegionFeatureRequest request = Request;
        if ((Int128)vertical < request.Vertical
            || (Int128)vertical > (Int128)request.Vertical + request.VerticalExtent
            || (Int128)across < request.Across
            || (Int128)across > (Int128)request.Across + request.AcrossExtent)
        {
            return false;
        }

        long ringLength = Composer.WallNoise.AroundLengthMeters;
        if (request.AroundExtent >= ringLength)
            return true;
        double forward = Composer.ForwardAroundDistance(around, request.Around);
        return forward <= request.AroundExtent;
    }
}
