using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tests;

/// <summary>CPU contracts for simulation-clock gust travel. Texture generation remains Godot-owned.</summary>
public partial class GrassGustStateTests : RefCounted
{
    public bool Run()
    {
        try { Verify(); GD.Print("GRASS_GUST_STATE=PASS"); return true; }
        catch (Exception exception) { GD.PushError("GRASS_GUST_STATE=FAIL " + exception.Message); return false; }
    }

    public static void Verify()
    {
        var state = new RingGrassGustState();
        const double aroundPeriod = 599.4, acrossPeriod = 600;
        state.Advance(new Vector2(10, 2), 100, 1, aroundPeriod, acrossPeriod);
        Vector2 initial = state.OffsetMeters;
        state.Advance(new Vector2(10, 2), 101, 1, aroundPeriod, acrossPeriod);
        Require(state.OffsetMeters.DistanceTo(initial) > .1f, "integrated gust did not travel");
        Vector2 turning = state.OffsetMeters;
        state.Advance(new Vector2(-10, 2), 101.05, 1, aroundPeriod, acrossPeriod);
        Require(float.IsFinite(state.OffsetMeters.X) && state.OffsetMeters.X > turning.X - 2,
            "wind turn did not retain continuous smoothed travel");
        Vector2 frozen = state.OffsetMeters;
        state.Advance(new Vector2(-10, 2), 101.05, 1, aroundPeriod, acrossPeriod);
        Require(state.OffsetMeters.IsEqualApprox(frozen), "frozen simulation clock moved gust field");
        state.Advance(new Vector2(20, 0), 10_000_000, 1, aroundPeriod, acrossPeriod);
        Require(state.OffsetMeters.IsEqualApprox(frozen), "large simulation gap was not safely rebaselined");
        state.Advance(new Vector2(20, 0), 10_000_001, 4, aroundPeriod, acrossPeriod);
        Require(float.IsFinite(state.OffsetMeters.X) && float.IsFinite(state.OffsetMeters.Y)
            && state.OffsetMeters.X >= 0 && state.OffsetMeters.X < aroundPeriod,
            "long-time gust offset escaped bounded canonical domain");
        state.Advance(new Vector2(-200, -200), 10_000_002, 4, aroundPeriod, acrossPeriod);
        Require(state.OffsetMeters.X >= 0 && state.OffsetMeters.X < aroundPeriod
            && state.OffsetMeters.Y >= 0 && state.OffsetMeters.Y < acrossPeriod,
            "negative gust travel did not wrap independently by its effective periods");
        state.Advance(Vector2.Zero, 1, 1, aroundPeriod, acrossPeriod);
        Require(float.IsFinite(state.OffsetMeters.X), "rewinding simulation time produced invalid gust offset");

        var precision = new RingGrassGustState();
        precision.Advance(new Vector2(1, 0), 1_000_000_000_000d, 1, aroundPeriod, acrossPeriod);
        precision.Advance(new Vector2(1, 0), 1_000_000_000_000.001d, 1, aroundPeriod, acrossPeriod);
        Require(precision.OffsetMeters.X > .0005f, "large simulation timestamp lost sub-frame double-precision motion");

        // This is the production canonical tile-origin helper. A floating-origin
        // rebase changes renderer world origin only, never these canonical bounds.
        Vector2 left = RingStandingGrassLayer.CalculateGrassGustOriginUv(599.2, 20, 123.25, 87.5, aroundPeriod, acrossPeriod);
        Vector2 seam = RingStandingGrassLayer.CalculateGrassGustOriginUv(-.2, 20, 123.25, 87.5, aroundPeriod, acrossPeriod);
        Vector2 adjacent = RingStandingGrassLayer.CalculateGrassGustOriginUv(599.2 + 16, 20, 123.25, 87.5, aroundPeriod, acrossPeriod);
        Vector2 rebased = RingStandingGrassLayer.CalculateGrassGustOriginUv(599.2, 20, 123.25, 87.5, aroundPeriod, acrossPeriod);
        Require(left.IsEqualApprox(seam) && rebased.IsEqualApprox(left) && !adjacent.IsEqualApprox(left),
            "tile adjacency, ring seam, or floating-origin-independent gust UV contract failed");
        var basis = new Basis(new Vector3(1.4f, .2f, .1f), new Vector3(.3f, 2.2f, .4f), new Vector3(.2f, .5f, .8f));
        const double radius = 1000, anchorHeight = 80, center = 125;
        var geometry = new RingLodGeometrySnapshot(new Transform3D(basis, Vector3.Zero), Vector3.Zero, Vector3.Zero,
            radius, 0, 20, 6283, -100, 100, null);
        Require(RingStandingGrassLayer.CalculateGrassGroundCovectors(geometry, anchorHeight, center, out Vector3 aroundDual, out Vector3 acrossDual),
            "scaled/sheared gust covectors were rejected");
        double angle = center / radius, arcScale = (radius - 20 - anchorHeight) / radius;
        Vector3 worldAround = basis * new Vector3((float)(-Math.Sin(angle) * arcScale), 0, (float)(Math.Cos(angle) * arcScale));
        Vector3 worldAcross = basis * Vector3.Up;
        Require(Mathf.IsEqualApprox(aroundDual.Dot(worldAround), 1) && Mathf.IsZeroApprox(aroundDual.Dot(worldAcross))
            && Mathf.IsZeroApprox(acrossDual.Dot(worldAround)) && Mathf.IsEqualApprox(acrossDual.Dot(worldAcross), 1),
            "gust dual covectors do not invert scaled/sheared canonical ground axes");
        var rebasedGeometry = geometry with { FloatingOriginWorldOffset = new Vector3(10000, -5000, 3000) };
        Require(RingStandingGrassLayer.CalculateGrassGroundCovectors(rebasedGeometry, anchorHeight, center, out Vector3 rebasedAround, out Vector3 rebasedAcross)
            && rebasedAround.IsEqualApprox(aroundDual) && rebasedAcross.IsEqualApprox(acrossDual),
            "floating-origin rebase changed canonical gust covectors");
        var layer = new RingStandingGrassLayer();
        Require(layer.VerifyGrassGustMaterialToggleForTests(), "near/intermediate gust material toggle left stale shader state");
        layer.Free();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
