using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.Integration;

/// <summary>
/// Verifies the shader-equivalent curved cloud-shadow projection against the
/// production ring dimensions, including a canonical wrap-seam crossing.
/// </summary>
public partial class CertifiedCloudShadowProjectionSmoke : Node
{
    private const double NearCascadeRadiusMeters = 24_000.0;
    private const double FarCascadeRadiusMeters = 96_000.0;
    private const double ArcToleranceMeters = 0.25;
    private const double VerticalToleranceMeters = 0.25;
    private const double AcrossToleranceMeters = 0.05;
    private const float LocalUpTolerance = 0.005f;
    private const double SeriesToleranceMeters = 0.5;

    private readonly record struct ProjectionResult(double Arc, double Across, double Altitude);

    public override void _Ready()
    {
        try
        {
            RunCertifiedProjectionChecks();
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError($"CERTIFIED_CLOUD_SHADOW_PROJECTION_SMOKE=FAIL: {exception.Message}");
            GetTree().Quit(1);
        }
    }

    private static void RunCertifiedProjectionChecks()
    {
        RunTemporalHistoryChecks();
        RunFarBackdropCloudOcclusionChecks();
        if (!CertifiedRingScale.Validate(out string scaleError))
            throw new InvalidOperationException($"certified scale is invalid: {scaleError}");
        if (!RingLodGeometrySnapshot.RunFloatingOriginSelfTest())
            throw new InvalidOperationException("certified floating-origin geometry self-test failed");

        double circumference = CertifiedRingScale.EffectiveCircumferenceMeters;
        double radius = CertifiedRingScale.EffectiveRadiusMeters;
        if (Math.Abs(circumference - 31_415_936.0) > 0.001
            || Math.Abs(radius - circumference / Math.Tau) > 0.000001
            || CertifiedRingScale.RingWidthMeters != 318_000)
        {
            throw new InvalidOperationException("production dimensions no longer match the certified contract");
        }

        double[] centers =
        {
            circumference / 3.0,
            circumference - 40_000.0
        };
        double[] arcOffsets =
        {
            -FarCascadeRadiusMeters,
            -NearCascadeRadiusMeters,
            0.0,
            NearCascadeRadiusMeters,
            FarCascadeRadiusMeters
        };
        double[] acrossOffsets = { -FarCascadeRadiusMeters, 0.0, FarCascadeRadiusMeters };
        double[] altitudes =
        {
            CertifiedRingScale.TerrainFloorMeters,
            0.0,
            CertifiedRingScale.RoutineMountainCeilingMeters,
            CertifiedRingScale.ExceptionalSummitMeters
        };

        double maxArcError = 0.0;
        double maxVerticalError = 0.0;
        double maxAcrossError = 0.0;
        double maxGroundError = 0.0;
        float maxLocalUpError = 0.0f;
        int checkedPositions = 0;
        double maxSeriesArcError = 0.0;
        double maxSeriesTrigError = 0.0;
        double maxFarLinkError = 0.0;
        int farLinkValid = 0;

        foreach (double centerAround in centers)
        {
            double centerAcross = CertifiedRingScale.RingWidthMeters * 0.5;
            var anchor = new RingCanonicalPosition(centerAround, 0.0, centerAcross);
            var geometry = new RingLodGeometrySnapshot(
                Transform3D.Identity,
                Vector3.Zero,
                Vector3.Zero,
                radius,
                0.0,
                0.0,
                circumference,
                CertifiedRingScale.TerrainFloorMeters,
                CertifiedRingScale.StructuralWallTopMeters,
                anchor);

            double centerAngle = Wrap(centerAround, circumference) / radius;
            Vector3 aroundAxis = new(
                (float)-Math.Sin(centerAngle),
                0.0f,
                (float)Math.Cos(centerAngle));
            Vector3 acrossAxis = Vector3.Up;
            Vector3 upAxis = new(
                (float)-Math.Cos(centerAngle),
                0.0f,
                (float)-Math.Sin(centerAngle));

            foreach (double arcOffset in arcOffsets)
            {
                double angleOffset = arcOffset / radius;
                Vector3 expectedLocalUp = (upAxis * (float)Math.Cos(angleOffset)
                    - aroundAxis * (float)Math.Sin(angleOffset)).Normalized();
                Vector3 ground = geometry.CanonicalToWorld(
                    centerAround + arcOffset,
                    0.0,
                    centerAcross);
                Vector3 oneMeterInward = geometry.CanonicalToWorld(
                    centerAround + arcOffset,
                    1.0,
                    centerAcross);
                Vector3 measuredLocalUp = (oneMeterInward - ground).Normalized();
                float localUpError = measuredLocalUp.DistanceTo(expectedLocalUp);
                maxLocalUpError = Math.Max(maxLocalUpError, localUpError);
                if (localUpError > LocalUpTolerance)
                    throw new InvalidOperationException(
                        $"local-up mismatch at arc offset {arcOffset:0}m: "
                        + $"error={localUpError:0.000000}, measured={measuredLocalUp}, expected={expectedLocalUp}");

                Vector3 expectedGround = aroundAxis * (float)(radius * Math.Sin(angleOffset))
                    + upAxis * (float)(radius * (1.0 - Math.Cos(angleOffset)));
                maxGroundError = Math.Max(maxGroundError, ground.DistanceTo(expectedGround));

                foreach (double acrossOffset in acrossOffsets)
                {
                    foreach (double altitude in altitudes)
                    {
                        Vector3 world = geometry.CanonicalToWorld(
                            centerAround + arcOffset,
                            altitude,
                            centerAcross + acrossOffset);
                        ProjectionResult projected = RecoverShaderCoordinates(
                            world,
                            aroundAxis,
                            acrossAxis,
                            upAxis,
                            radius);
                        maxArcError = Math.Max(maxArcError, Math.Abs(projected.Arc - arcOffset));
                        maxVerticalError = Math.Max(
                            maxVerticalError,
                            Math.Abs(projected.Altitude - altitude));
                        maxAcrossError = Math.Max(
                            maxAcrossError,
                            Math.Abs(projected.Across - acrossOffset));
                        checkedPositions++;
                    }
                }

                if (arcOffset == 0.0)
                {
                    RunSeriesChecks(
                        geometry, centerAround, centerAcross, aroundAxis, acrossAxis, upAxis, radius,
                        ref maxSeriesArcError, ref maxSeriesTrigError, ref maxFarLinkError, ref farLinkValid);
                }

                double nearUv = arcOffset / (NearCascadeRadiusMeters * 2.0) + 0.5;
                double farUv = arcOffset / (FarCascadeRadiusMeters * 2.0) + 0.5;
                if (Math.Abs(arcOffset) <= NearCascadeRadiusMeters && (nearUv < 0.0 || nearUv > 1.0))
                    throw new InvalidOperationException("near-cascade UV left its certified footprint");
                if (farUv < 0.0 || farUv > 1.0)
                    throw new InvalidOperationException("far-cascade UV left its certified footprint");
            }
        }

        if (maxArcError > ArcToleranceMeters
            || maxVerticalError > VerticalToleranceMeters
            || maxAcrossError > AcrossToleranceMeters
            || maxGroundError > ArcToleranceMeters)
        {
            throw new InvalidOperationException(
                $"projection tolerance exceeded: arc={maxArcError:0.000}m, "
                + $"vertical={maxVerticalError:0.000}m, across={maxAcrossError:0.000}m, "
                + $"ground={maxGroundError:0.000}m");
        }

        if (maxSeriesArcError > SeriesToleranceMeters
            || maxSeriesTrigError > SeriesToleranceMeters
            || maxFarLinkError > SeriesToleranceMeters
            || farLinkValid == 0)
        {
            throw new InvalidOperationException(
                $"series/far-link tolerance exceeded: seriesArc={maxSeriesArcError:0.0000}m, "
                + $"seriesTrig={maxSeriesTrigError:0.0000}m, farLink={maxFarLinkError:0.0000}m, validLinks={farLinkValid}");
        }

        double farEdgeSag = radius * (1.0 - Math.Cos(FarCascadeRadiusMeters / radius));
        GD.Print(
            "CERTIFIED_CLOUD_SHADOW_PROJECTION_SMOKE=PASS "
            + $"radius={radius:0.000}m circumference={circumference:0}m "
            + $"width={CertifiedRingScale.RingWidthMeters}m positions={checkedPositions} "
            + $"maxErrors={maxArcError:0.000}/{maxVerticalError:0.000}/{maxAcrossError:0.000}m "
            + $"localUp={maxLocalUpError:0.000000} farEdgeSag={farEdgeSag:0.000}m seam=pass "
            + $"series(arc/trig)={maxSeriesArcError:0.0000}/{maxSeriesTrigError:0.0000}m farLink={maxFarLinkError:0.0000}m links={farLinkValid}");
    }

    /// <summary>
    /// Verifies the bridge-side policy that maps live-menu history sliders to
    /// the exact accumulation-decay value sent to Sunshine. This is deliberately
    /// renderer-independent: a headless run can catch a broken UI/bridge path
    /// before GPU capture is needed.
    /// </summary>
    private static void RunTemporalHistoryChecks()
    {
        const float stationary = 0.92f;
        const float moving = 0.18f;
        float atRest = RingSunshineCloudBridge.ResolveTemporalHistoryWeight(stationary, moving, 0.0f);
        float inMotion = RingSunshineCloudBridge.ResolveTemporalHistoryWeight(stationary, moving, 1.0f);
        float midMotion = RingSunshineCloudBridge.ResolveTemporalHistoryWeight(stationary, moving, 0.5f);
        if (!Mathf.IsEqualApprox(atRest, stationary)
            || !Mathf.IsEqualApprox(inMotion, moving)
            || !Mathf.IsEqualApprox(midMotion, 0.55f))
        {
            throw new InvalidOperationException("temporal history slider/motion blend regression");
        }

        // Moving history is never permitted to exceed stationary history, even
        // when inspector or saved values arrive out of order.
        float clamped = RingSunshineCloudBridge.ResolveTemporalHistoryWeight(0.25f, 0.95f, 1.0f);
        if (!Mathf.IsEqualApprox(clamped, 0.25f))
            throw new InvalidOperationException("temporal history moving-weight clamp regression");

        float noSuppression = RingSunshineCloudBridge.ResolveAppliedHistoryWeight(0.70f, 0.0f, 0.0f);
        float lightningSuppressed = RingSunshineCloudBridge.ResolveAppliedHistoryWeight(0.70f, 1.0f, 0.0f);
        float sunSuppressed = RingSunshineCloudBridge.ResolveAppliedHistoryWeight(0.70f, 0.0f, 1.0f);
        if (!Mathf.IsEqualApprox(noSuppression, 0.70f)
            || !Mathf.IsEqualApprox(lightningSuppressed, 0.04f)
            || !Mathf.IsEqualApprox(sunSuppressed, 0.04f))
        {
            throw new InvalidOperationException("temporal history suppression regression");
        }

        // The receiver-side map offset follows Sunshine's large coverage
        // structures (0.55x weather wind), not its faster medium/detail
        // erosion. Using 1.0x caused a visible offset reset at every rebake.
        Vector2 bulkWind = RingSunshineCloudBridge.ResolveGpuShadowBulkWindDisplacement(
            new Vector2(10.0f, -4.0f), 2.0f);
        if (!bulkWind.IsEqualApprox(new Vector2(11.0f, -4.4f)))
            throw new InvalidOperationException("GPU cloud-shadow bulk-wind handoff regression");
    }

    private static void RunFarBackdropCloudOcclusionChecks()
    {
        // Sunshine is the normal renderer. The legacy FogVolume is disabled in
        // that configuration, so conditioning the canvas-backdrop mask on only
        // FogVolume visibility makes the horizon draw through active clouds.
        if (!RingScaledFarViewport.ShouldCompositeCloudBackdrop(true, false, true))
            throw new InvalidOperationException("Sunshine cloud backdrop occlusion regression");
        if (!RingScaledFarViewport.ShouldCompositeCloudBackdrop(true, true, false))
            throw new InvalidOperationException("legacy cloud backdrop occlusion regression");
        if (RingScaledFarViewport.ShouldCompositeCloudBackdrop(false, true, true)
            || RingScaledFarViewport.ShouldCompositeCloudBackdrop(true, false, false))
        {
            throw new InvalidOperationException("far-backdrop cloud mask activation policy regression");
        }
    }

    /// <summary>
    /// Mirrors ring_cloud_shadow_arc_angle / ring_cloud_shadow_sin_cos in single precision and checks
    /// them against the exact double-precision projection at the 40 km and 100 km cascade edges, then
    /// checks that far coordinates derived from near ones (RingCloudShadowProjector.ResolveFarFromNear)
    /// match a direct far projection.
    /// </summary>
    private static void RunSeriesChecks(
        RingLodGeometrySnapshot geometry, double centerAround, double centerAcross,
        Vector3 aroundAxis, Vector3 acrossAxis, Vector3 upAxis, double radius,
        ref double maxSeriesArcError, ref double maxSeriesTrigError, ref double maxFarLinkError, ref int farLinkValid)
    {
        double[] acrossOffsets = { -40_000.0, 0.0, 40_000.0, 100_000.0 };
        double[] altitudes = { 0.0, 1_000.0, 20_000.0 };
        // The far cascade re-centres up to ~4 km away from the near one.
        double farCenterAround = centerAround + 3_000.0;
        double farCenterAcross = centerAcross - 2_000.0;
        double farAngle = (Wrap(farCenterAround, CertifiedRingScale.EffectiveCircumferenceMeters)) / radius;
        Vector3 farAround = new((float)-Math.Sin(farAngle), 0.0f, (float)Math.Cos(farAngle));
        Vector3 farUp = new((float)-Math.Cos(farAngle), 0.0f, (float)-Math.Sin(farAngle));
        Vector3 nearCenter = geometry.CanonicalToWorld(centerAround, 0.0, centerAcross);
        Vector3 farCenter = geometry.CanonicalToWorld(farCenterAround, 0.0, farCenterAcross);
        Vector3 link = RingCloudShadowProjector.ResolveFarFromNear(
            nearCenter, aroundAxis, acrossAxis, upAxis,
            farCenter, farAround, acrossAxis, farUp, radius);
        if (link.Z > 0.5f) farLinkValid++;
        double[] seriesArcOffsets = { -100_000.0, -40_000.0, 40_000.0, 100_000.0 };
        foreach (double arcOffset in seriesArcOffsets)
        foreach (double acrossOffset in acrossOffsets)
        {
            foreach (double altitude in altitudes)
            {
                Vector3 world = geometry.CanonicalToWorld(centerAround + arcOffset, altitude, centerAcross + acrossOffset);
                Vector3 relative = world - nearCenter;
                ProjectionResult exact = RecoverShaderCoordinates(relative, aroundAxis, acrossAxis, upAxis, radius);
                ProjectionResult series = RecoverShaderCoordinatesSeries(relative, aroundAxis, acrossAxis, upAxis, radius);
                maxSeriesArcError = Math.Max(maxSeriesArcError, Math.Abs(series.Arc - exact.Arc));
                if (link.Z > 0.5f)
                {
                    ProjectionResult farDirect = RecoverShaderCoordinates(world - farCenter, farAround, acrossAxis, farUp, radius);
                    maxFarLinkError = Math.Max(maxFarLinkError, Math.Abs(exact.Arc + link.X - farDirect.Arc));
                    maxFarLinkError = Math.Max(maxFarLinkError, Math.Abs(exact.Across + link.Y - farDirect.Across));
                }
            }
        }
        // sin/cos series feed the sun-ray shift (altitude up to the 20 km cloud deck / sun_up >= 0.04).
        for (double angle = -0.05; angle <= 0.0500001; angle += 0.0025)
        {
            (float sine, float cosine) = SeriesSinCos((float)angle);
            maxSeriesTrigError = Math.Max(
                maxSeriesTrigError,
                Math.Max(Math.Abs(sine - Math.Sin(angle)), Math.Abs(cosine - Math.Cos(angle))) * 100_000.0);
        }
    }

    private static (float Sine, float Cosine) SeriesSinCos(float angle)
    {
        float a2 = angle * angle;
        return (
            angle * (1.0f - a2 * (1.0f / 6.0f) * (1.0f - a2 * (1.0f / 20.0f) * (1.0f - a2 * (1.0f / 42.0f)))),
            1.0f - a2 * 0.5f * (1.0f - a2 * (1.0f / 12.0f) * (1.0f - a2 * (1.0f / 30.0f) * (1.0f - a2 * (1.0f / 56.0f)))));
    }

    private static ProjectionResult RecoverShaderCoordinatesSeries(
        Vector3 relative, Vector3 aroundAxis, Vector3 acrossAxis, Vector3 upAxis, double ringRadiusMeters)
    {
        float ringRadius = (float)ringRadiusMeters;
        float tangent = relative.Dot(aroundAxis);
        float across = relative.Dot(acrossAxis);
        float outward = ringRadius - relative.Dot(upAxis);
        float q = tangent / outward;
        float q2 = q * q;
        float angle = Math.Abs(q) <= 0.05f
            ? q * (1.0f - q2 * (1.0f / 3.0f - q2 * (1.0f / 5.0f - q2 * (1.0f / 7.0f))))
            : MathF.Atan2(tangent, outward);
        float altitude = ringRadius - MathF.Sqrt(tangent * tangent + outward * outward);
        return new ProjectionResult(ringRadius * angle, across, altitude);
    }

    private static ProjectionResult RecoverShaderCoordinates(
        Vector3 relative,
        Vector3 aroundAxis,
        Vector3 acrossAxis,
        Vector3 upAxis,
        double ringRadiusMeters)
    {
        double tangent = Dot(relative, aroundAxis);
        double across = Dot(relative, acrossAxis);
        double inward = Dot(relative, upAxis);
        double outwardRadius = ringRadiusMeters - inward;
        double arc = ringRadiusMeters * Math.Atan2(tangent, outwardRadius);
        double altitude = ringRadiusMeters
            - Math.Sqrt(tangent * tangent + outwardRadius * outwardRadius);
        return new ProjectionResult(arc, across, altitude);
    }

    private static double Dot(Vector3 left, Vector3 right)
        => (double)left.X * right.X + (double)left.Y * right.Y + (double)left.Z * right.Z;

    private static double Wrap(double value, double modulus)
    {
        double wrapped = value % modulus;
        return wrapped < 0.0 ? wrapped + modulus : wrapped;
    }
}
