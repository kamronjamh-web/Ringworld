using System;
using Godot;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>Pure mapping checks shared by curved Terrain3D rendering and collision.</summary>
public partial class RingSurfaceFrameTest : RefCounted
{
    public bool Run()
    {
        try
        {
            VerifyAnchorAndCurvature(); VerifySeamAndOppositeWrap(); VerifyInverseAndRebase(); VerifyAcrossDirection(); VerifyLodGeometryAgreement();
            GD.Print("RING_SURFACE_FRAME=PASS"); return true;
        }
        catch (Exception exception) { GD.PushError("RING_SURFACE_FRAME=FAIL " + exception.Message); return false; }
    }

    private static void VerifyAnchorAndCurvature()
    {
        RingSurfaceFrame frame = new(100, 50, 12, 1000, 100, 2);
        Vector3 anchor = frame.MapSample(100, 50, 12);
        Require(anchor.DistanceTo(Vector3.Zero) < .0001f, "anchor does not map to local origin");
        Vector3 p = frame.MapSample(110, 56, 12);
        Require(Math.Abs(p.X - 88.0 * Math.Sin(.1)) < .001 && Math.Abs(p.Z - 6) < .001, "curved GPU/collision equation drifted");
    }

    private static void VerifySeamAndOppositeWrap()
    {
        RingSurfaceFrame frame = new(0, 0, 0, 1000, 100, 2);
        Require(frame.SignedAroundDeltaMeters(998) == -2, "seam signed delta is wrong");
        Require(frame.MapSample(998, 0, 0).DistanceTo(frame.MapSample(-2, 0, 0)) < .0001f, "wrapped coordinates disagree");
        Require(Math.Abs(frame.SignedAroundDeltaMeters(500)) == 500, "opposite-ring delta is wrong");
    }

    private static void VerifyInverseAndRebase()
    {
        RingSurfaceFrame a = new(10_000, 4_000, 7, 31_415_936, 5_000_000, 2);
        Vector3 point = a.MapSample(10_042, 4_014, 19);
        RingSurfaceCoordinates recovered = a.Inverse(point);
        Require(Math.Abs(recovered.AroundMeters - 10_042) < .001 && Math.Abs(recovered.AcrossMeters - 4_014) < .001 && Math.Abs(recovered.Height - 19) < .001, "inverse failed");
        Vector3 fractional = a.MapPosition(10_042.5, 4_014.25, 19.5);
        RingSurfaceCoordinates recoveredFractional = a.Inverse(fractional);
        Require(Math.Abs(recoveredFractional.AroundMeters - 10_042.5) < .001 && Math.Abs(recoveredFractional.AcrossMeters - 4_014.25) < .001, "fractional rebase precision failed");
        RingSurfaceFrame b = new(10_042, 4_014, 19, 31_415_936, 5_000_000, 2);
        Require(b.MapSample(10_042, 4_014, 19).DistanceTo(Vector3.Zero) < .0001f, "rebase anchor transform failed");
    }

    private static void VerifyAcrossDirection()
    {
        RingSurfaceFrame gameplay = new(7_853_984, 159_000, 4, 31_415_936, 5_000_000, 2, -1);
        Vector3 mapped = gameplay.MapSample(7_853_986, 159_002, 4);
        Require(Math.Abs(mapped.Z + 2) < .001, "gameplay across direction is not right-handed");
        RingSurfaceCoordinates inverse = gameplay.Inverse(mapped);
        Require(Math.Abs(inverse.AroundMeters - 7_853_986) < .001 && Math.Abs(inverse.AcrossMeters - 159_002) < .001, "gameplay direction inverse failed");
        RingSurfaceFrame seam = new(0, 0, 0, 31_415_936, 5_000_000, 2, -1);
        Require(seam.MapSample(31_415_934, 2, 0).DistanceTo(seam.MapSample(-2, 2, 0)) < .001f, "direction seam mapping drifted");
    }

    private static void VerifyLodGeometryAgreement()
    {
        const long circumference = 31_415_936;
        double radius = circumference / Math.Tau;
        const long anchorAround = circumference / 4;
        const int anchorAcross = 159_000;
        const float anchorHeight = 12;
        RingSurfaceFrame frame = new(anchorAround, anchorAcross, anchorHeight, circumference, radius, 2, -1);
        foreach ((double around, double across, double height) in new[]
        {
            ((double)anchorAround - 40, (double)anchorAcross - 40, 11.25),
            ((double)anchorAround + 40.5, (double)anchorAcross + 39.25, 14.5)
        })
        {
            RequireGeometryAgreement(frame, circumference, radius, around, across, height);
        }
        RingSurfaceFrame seam = new(0, anchorAcross, anchorHeight, circumference, radius, 2, -1);
        foreach ((double around, double across, double height) in new[] { (-40.5, (double)anchorAcross + 1.5, 9.75), ((double)circumference - 39.5, (double)anchorAcross - 1.25, 13.25) })
            RequireGeometryAgreement(seam, circumference, radius, around, across, height);
    }

    private static void RequireGeometryAgreement(RingSurfaceFrame frame, long circumference, double radius, double around, double across, double height)
    {
        RingLodGeometrySnapshot geometry = new(Transform3D.Identity, Vector3.Zero, Vector3.Zero, radius, 0, 0, circumference, -1_000, 1_000,
            new RingCanonicalPosition(frame.AnchorAroundMeters, frame.AnchorHeight, frame.AnchorAcrossMeters));
        double angle = frame.AnchorAroundMeters / radius;
        Transform3D worldFrame = new(new Basis(
            new Vector3((float)-Math.Sin(angle), 0, (float)Math.Cos(angle)),
            new Vector3((float)-Math.Cos(angle), 0, (float)-Math.Sin(angle)),
            new Vector3(0, -1, 0)), Vector3.Zero);
        Vector3 expected = geometry.CanonicalToWorld(around, height, across);
        Vector3 actual = worldFrame * frame.MapPosition(around, across, height);
        Require(actual.DistanceTo(expected) < .001f, "surface frame differs from LOD geometry beyond millimetre tolerance");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
