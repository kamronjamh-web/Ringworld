using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo;

/// <summary>Focused, renderer-free coverage for Certified Ring shadow geometry.</summary>
public partial class RingTerrainShadowGeometryTest : RefCounted
{
    public bool Run()
    {
        try
        {
            VerifyStraightRayCurvature();
            VerifyStructuralWallHits();
            VerifyGrazingWrap();
            VerifyMegaRingScale();
            GD.Print("RING_TERRAIN_SHADOW_GEOMETRY=PASS");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("RING_TERRAIN_SHADOW_GEOMETRY=FAIL " + exception.Message);
            return false;
        }
    }

    private static void VerifyStraightRayCurvature()
    {
        const double radius = 10_000.0;
        RingTerrainShadowGeometry.RingPoint receiver = RingTerrainShadowGeometry.ReceiverPosition(radius, 0.0, 500.0, 0.0);
        Vector3 sun = new Vector3(-0.3f, 0.0f, 0.9539392f).Normalized();
        const double distance = 1_200.0;
        RingTerrainShadowGeometry.RingRaySample actual =
            RingTerrainShadowGeometry.SampleStraightRay(radius, receiver, sun, distance);

        // Independent reference from the straight Cartesian point.
        double x = radius + sun.X * distance;
        double z = sun.Z * distance;
        double expectedHeight = radius - Math.Sqrt(x * x + z * z);
        Require(Math.Abs(actual.HeightMeters - expectedHeight) < 0.01,
            "straight ray did not retain cylindrical curvature");
        Require(Math.Abs(actual.HeightMeters - sun.X * -distance) > 20.0,
            "curved result unexpectedly collapsed to a flat tangent-plane height");
    }

    private static void VerifyStructuralWallHits()
    {
        const double radius = 10_000.0;
        RingTerrainShadowGeometry.RingPoint receiver = RingTerrainShadowGeometry.ReceiverPosition(radius, 0.0, 500.0, 100.0);
        Vector3 towardLeftWall = new Vector3(-0.20f, -0.98f, 0.0f).Normalized();
        Require(RingTerrainShadowGeometry.RayHitsStructuralWall(
                radius, receiver, towardLeftWall, 1_000.0,
                0.0, 500.0, 2_000.0, 2.0),
            "finite left wall should block the ray");

        Vector3 parallelToWalls = new Vector3(-1.0f, 0.0f, 0.0f);
        Require(!RingTerrainShadowGeometry.RayHitsStructuralWall(
                radius, receiver, parallelToWalls, 1_000.0,
                0.0, 500.0, 2_000.0, 2.0),
            "parallel ray reported a wall hit");

        Require(!RingTerrainShadowGeometry.RayHitsStructuralWall(
                radius, receiver, towardLeftWall, 1_000.0,
                501.0, 900.0, 2_000.0, 2.0),
            "wall outside its vertical bounds reported a hit");

        Vector3 towardRightWall = new Vector3(-0.20f, 0.98f, 0.0f).Normalized();
        Require(RingTerrainShadowGeometry.RayHitsStructuralWall(
                radius, receiver, towardRightWall, 1_000.0,
                0.0, 500.0, 2_000.0, 2.0),
            "finite right wall should block the ray");
    }

    private static void VerifyGrazingWrap()
    {
        const double radius = 10_000.0;
        RingTerrainShadowGeometry.RingPoint receiver =
            RingTerrainShadowGeometry.ReceiverPosition(radius, Math.Tau * radius - 2.0, 500.0, 0.0);
        Vector3 grazingSun = new Vector3(-0.001f, 0.0f, 1.0f).Normalized();
        RingTerrainShadowGeometry.RingRaySample ray =
            RingTerrainShadowGeometry.SampleStraightRay(radius, receiver, grazingSun, 20.0);
        Require(ray.AroundMeters >= 0.0 && ray.AroundMeters < Math.Tau * radius,
            "wrapped grazing ray produced an invalid around coordinate");
        Require(ray.AroundMeters < 100.0,
            "wrapped grazing ray did not cross the canonical seam");
    }

    private static void VerifyMegaRingScale()
    {
        Require(MegaRingScale.Validate(out string scaleError),
            "Mega Ring scale is invalid: " + scaleError);
        const double receiverHeight = 125.0;
        const double across = 800_000_000.0;
        const double distance = 75_000.0;
        double circumference = MegaRingScale.EffectiveCircumferenceMeters;
        double radius = MegaRingScale.EffectiveRadiusMeters;
        Require(circumference > int.MaxValue,
            "Mega Ring test no longer exercises 64-bit around coordinates");

        // Both inputs are beyond the legacy signed integer range. The first
        // crosses forward over the seam; the second represents the same near-
        // zero position one circumference later and crosses backward.
        VerifyMegaRay(radius, circumference - 25_000.0, across, receiverHeight,
            TangentDirection(radius, circumference - 25_000.0, forward: true), distance,
            expectedMinimumAround: 0.0, expectedMaximumAround: 100_000.0, "forward seam wrap");
        VerifyMegaRay(radius, circumference + 25_000.0, across, receiverHeight,
            TangentDirection(radius, 25_000.0, forward: false), distance,
            expectedMinimumAround: circumference - 100_000.0, expectedMaximumAround: circumference, "backward seam wrap");

        RingTerrainShadowGeometry.RingPoint nearLeftWall =
            RingTerrainShadowGeometry.ReceiverPosition(radius, circumference - 50_000.0, 50_000.0, receiverHeight);
        Vector3 towardLeftWall = new Vector3(-0.20f, -0.98f, 0.0f).Normalized();
        Require(RingTerrainShadowGeometry.RayHitsStructuralWall(
                radius, nearLeftWall, towardLeftWall, MegaRingScale.RingWidthMeters,
                0.0, 20_000.0, 100_000.0, 1.0),
            "Mega Ring left structural wall should block a nearby ray");
    }

    private static void VerifyMegaRay(
        double radius,
        double around,
        double across,
        double height,
        Vector3 direction,
        double distance,
        double expectedMinimumAround,
        double expectedMaximumAround,
        string label)
    {
        RingTerrainShadowGeometry.RingPoint receiver =
            RingTerrainShadowGeometry.ReceiverPosition(radius, around, across, height);
        RingTerrainShadowGeometry.RingRaySample actual =
            RingTerrainShadowGeometry.SampleStraightRay(radius, receiver, direction, distance);
        double expectedX = receiver.X + direction.X * distance;
        double expectedZ = receiver.Z + direction.Z * distance;
        double expectedHeight = radius - Math.Sqrt(expectedX * expectedX + expectedZ * expectedZ);
        double expectedAround = Math.Atan2(expectedZ, expectedX);
        if (expectedAround < 0.0) expectedAround += Math.Tau;
        expectedAround *= radius;
        Require(Math.Abs(actual.HeightMeters - expectedHeight) < 0.001,
            label + " lost Mega Ring radial-height precision");
        Require(Math.Abs(actual.AroundMeters - expectedAround) < 0.001,
            label + " lost Mega Ring around precision");
        Require(actual.AroundMeters >= 0.0 && actual.AroundMeters < Math.Tau * radius,
            label + " returned an unwrapped around coordinate");
        Require(actual.AroundMeters >= expectedMinimumAround && actual.AroundMeters < expectedMaximumAround,
            label + " did not cross the expected seam direction");
    }

    private static Vector3 TangentDirection(double radius, double around, bool forward)
    {
        double angle = around / radius;
        float sign = forward ? 1.0f : -1.0f;
        return new Vector3((float)(-Math.Sin(angle) * sign), 0.0f, (float)(Math.Cos(angle) * sign));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
