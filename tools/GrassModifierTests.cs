using System;
using System.Linq;
using System.Reflection;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tests;

/// <summary>Runnable pure CPU contract tests for canonical grass modifier data.</summary>
public partial class GrassModifierTests : RefCounted
{
    public bool Run()
    {
        try { Verify(); GD.Print("GRASS_MODIFIER_STATE=PASS"); return true; }
        catch (Exception exception) { GD.PushError("GRASS_MODIFIER_STATE=FAIL " + exception.Message); return false; }
    }

    public static void Verify()
    {
        const long ring = 1000;
        RingGrassModifierZone soft = new(100, 50, new Vector2(10, 10), 4, 0, Colors.Red, 1);
        RingGrassModifierZone overlap = new(100, 50, new Vector2(10, 10), 0, .5f, Colors.Blue, 1);
        var snapshot = new RingGrassModifierSnapshot(1, ring, new[] {
            new RingGrassModifierSnapshot.Entry("z", overlap), new RingGrassModifierSnapshot.Entry("a", soft) });
        Require(snapshot.Entries.Count == 2 && snapshot.Entries[0].Id == "a", "stable ordinal ordering");
        Require(snapshot.DensityAt(100, 50) == 0, "zero-density centre");
        Require(snapshot.DensityAt(111, 50) == 1, "outside ellipse");
        float feather = RingGrassModifierSnapshot.Weight(soft, 92, 50, ring);
        Require(feather > 0 && feather < 1, "soft feather");
        RingGrassModifierZone oversizedFeather = new(100, 50, new Vector2(8, 8), 128, 0, Colors.Red, 1);
        Require(Mathf.IsEqualApprox(RingGrassModifierSnapshot.Weight(oversizedFeather, 100, 50, ring), 1),
            "oversized normalized feather weakened modifier centre");
        Require(Mathf.IsZeroApprox(RingGrassModifierSnapshot.Weight(oversizedFeather, 108, 50, ring)),
            "oversized normalized feather leaked beyond ellipse edge");
        var seam = new RingGrassModifierSnapshot(2, ring, new[] { new RingGrassModifierSnapshot.Entry("seam", new RingGrassModifierZone(998, 20, new Vector2(8,8),0,.25f,Colors.White,0)) });
        Require(seam.DensityAt(2,20) < .5f, "wrapped seam distance");
        var duplicate = new RingGrassModifierSnapshot(3, ring, new[] { new RingGrassModifierSnapshot.Entry("same",soft), new RingGrassModifierSnapshot.Entry("same",overlap) });
        Require(duplicate.Entries.Count == 1, "duplicate IDs deterministically deduped");
        var repeated = new RingGrassModifierSnapshot(1, ring, new[] { new RingGrassModifierSnapshot.Entry("a",soft), new RingGrassModifierSnapshot.Entry("z",overlap) });
        Require(repeated.Entries[0] == snapshot.Entries[0] && repeated.Entries[1] == snapshot.Entries[1], "repeat determinism");
        Require(!new RingGrassModifierZone(0,0,new Vector2(float.NaN,1),0,1,Colors.White,0).IsValid, "finite validation");
        VerifyWorkerTintMaps();
    }

    private static void VerifyWorkerTintMaps()
    {
        var fullTile = new RingGrassTileBounds(0, 32, 0, 32);
        var halfRed = new RingGrassModifierSnapshot(4, 1000, new[]
        {
            new RingGrassModifierSnapshot.Entry("half-red", new RingGrassModifierZone(16, 16, new Vector2(100, 100), 0, 1, Colors.Red, .5f))
        });
        byte[] halfBytes = BuildTintMap(fullTile, halfRed) ?? throw new InvalidOperationException("half-red worker tint map was empty");
        Require(halfBytes[0] == 255 && halfBytes[1] == 0 && halfBytes[2] == 0 && halfBytes[3] is >= 127 and <= 128,
            "half-red worker tint map premultiplied RGB instead of preserving red with alpha");

        var seam = new RingGrassModifierSnapshot(5, 1000, new[]
        {
            new RingGrassModifierSnapshot.Entry("seam", new RingGrassModifierZone(998, 16, new Vector2(8, 20), 0, 1, Colors.Blue, 1))
        });
        byte[] seamBytes = BuildTintMap(fullTile, seam) ?? throw new InvalidOperationException("opposite seam tile lost its tint map");
        Require(seamBytes.Any(value => value != 0), "opposite seam tile tint map contained no tint");

        var transparent = new RingGrassModifierSnapshot(6, 1000, new[]
        {
            new RingGrassModifierSnapshot.Entry("transparent", new RingGrassModifierZone(16, 16, new Vector2(10, 10), 0, 1, Colors.Red, 0))
        });
        Require(BuildTintMap(fullTile, transparent) == null, "fully transparent modifier map allocated an atlas layer");
    }

    private static byte[]? BuildTintMap(RingGrassTileBounds bounds, RingGrassModifierSnapshot modifiers)
    {
        MethodInfo method = typeof(RingGrassTileBuilder).GetMethod("BuildTintMap", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("RingGrassTileBuilder.BuildTintMap");
        return method.Invoke(null, new object?[] { bounds, modifiers }) as byte[];
    }
    private static void Require(bool condition, string text) { if (!condition) throw new InvalidOperationException("Grass modifier test: " + text); }
}
