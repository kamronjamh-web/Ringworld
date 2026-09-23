using System;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using Godot;

namespace RingworldTechDemo.Tests;

/// <summary>Allocation-free CPU contracts for the canonical player trail.</summary>
public static class GrassInteractionFieldTests
{
    public static void Verify()
    {
        var field = new RingGrassInteractionField(); const long wrap = 1000;
        field.Push(new RingCanonicalPosition(10, 0, 10), 1, wrap);
        for (int i = 1; i <= 4; i++) field.Push(new RingCanonicalPosition(10 + i * .3, 0, 10), 1, wrap);
        Require(field.Count == 2, "slow movement did not accumulate a spaced trail sample");
        field.Clear(); field.Push(new RingCanonicalPosition(999.8, 0, 0), 1, wrap); field.Push(new RingCanonicalPosition(.2, 0, 0), 1, wrap);
        Require(field.Count == 1, "wrapped seam travel was treated as a teleport");
        field.Push(new RingCanonicalPosition(.2, 30, 0), 1, wrap); Require(field.Count == 1, "vertical teleport did not clear the trail");
        for (int i = 0; i < 10; i++) { field.Age(.5f); field.Push(new RingCanonicalPosition(.2, 30, 0), 1, wrap); }
        Require(field.Count == 1, "stationary player did not refresh the occupied trail sample");
        RingLodGeometrySnapshot geometry = new(Transform3D.Identity, Vector3.Zero, Vector3.Zero, wrap / Math.Tau, 0, 0, wrap, -100, 100, new RingCanonicalPosition(0, 0, 0));
        Vector4[] projected = field.Project(geometry, 1, out int active);
        Require(active == 1 && projected[0].W > .9f && float.IsFinite(projected[0].X), "stationary occupied sample was not projected with strength");
        Vector4 originalPoint = projected[0]; // Project reuses its fixed-size upload array.
        RingLodGeometrySnapshot rebased = geometry with { FloatingOriginWorldOffset = new Vector3(20_000, 0, -20_000) };
        Vector4[] rebasedPoints = field.Project(rebased, 1, out int rebasedActive);
        Require(rebasedActive == active && float.IsFinite(rebasedPoints[0].X) && rebasedPoints[0].W == originalPoint.W
            && Math.Abs(rebasedPoints[0].X - originalPoint.X) > 1000,
            "canonical trail projection did not survive a frame rebase");
        field.Age(999); field.Project(geometry, 1, out active);
        Require(active == 0, "unrefreshed samples did not decay");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
