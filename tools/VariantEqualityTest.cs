using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Rendering;
using RingworldTechDemo.Weather;
namespace RingworldTechDemo.Tools;

/// <summary>
/// Proves RingVariantEquality.Same compares Variants by value (Objects by reference), unlike Godot.Variant.Equals, and that RingWindField.SetCached
/// therefore skips re-writing an unchanged sampler global. Run: Godot --headless --path . --script res://tools/variant_equality_test.gd. Prints VARIANT_EQUALITY=PASS.
/// </summary>
public partial class VariantEqualityTest : RefCounted
{
    public int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            var tex = ImageTexture.CreateFromImage(Image.CreateEmpty(2, 2, false, Image.Format.Rgba8));
            var other = ImageTexture.CreateFromImage(Image.CreateEmpty(2, 2, false, Image.Format.Rgba8));
            Check(RingVariantEquality.Same(Variant.From(tex), Variant.From(tex)), "same texture instance must match");
            Check(!RingVariantEquality.Same(Variant.From(tex), Variant.From(other)), "different texture instances must not match");
            Check(RingVariantEquality.Same(Variant.From("abc"), Variant.From(new string("abc".ToCharArray()))), "equal strings must match");
            Check(!RingVariantEquality.Same(Variant.From("abc"), Variant.From("abd")), "different strings must not match");
            Check(RingVariantEquality.Same(Variant.From(new StringName("n")), Variant.From(new StringName("n"))), "equal StringNames must match");
            Check(RingVariantEquality.Same(Variant.From(new Vector2(1, 2)), Variant.From(new Vector2(1, 2))), "Vector2");
            Check(!RingVariantEquality.Same(Variant.From(new Vector2(1, 2)), Variant.From(new Vector2(1, 3))), "Vector2 differ");
            Check(RingVariantEquality.Same(Variant.From(new Vector3(1, 2, 3)), Variant.From(new Vector3(1, 2, 3))), "Vector3");
            Check(RingVariantEquality.Same(Variant.From(new Vector4(1, 2, 3, 4)), Variant.From(new Vector4(1, 2, 3, 4))), "Vector4");
            Check(!RingVariantEquality.Same(Variant.From(new Vector4(1, 2, 3, 4)), Variant.From(new Vector4(1, 2, 3, 5))), "Vector4 differ");
            Check(RingVariantEquality.Same(Variant.From(0.5f), Variant.From(0.5f)), "float");
            Check(!RingVariantEquality.Same(Variant.From(0.5f), Variant.From(0.25f)), "float differ");
            Check(RingVariantEquality.Same(Variant.From(7), Variant.From(7)), "int");
            Check(RingVariantEquality.Same(Variant.From(true), Variant.From(true)), "bool");
            Check(!RingVariantEquality.Same(Variant.From(true), Variant.From(false)), "bool differ");
            Check(!RingVariantEquality.Same(Variant.From(1.0f), Variant.From(1)), "float 1 vs int 1 must not match");

            bool engineEquals = Variant.From(tex).Equals(Variant.From(tex));
            if (engineEquals) Console.WriteLine("VARIANT_EQUALITY NOTE Variant.Equals now matches the same Object; the workaround may be unnecessary.");
            else Console.WriteLine("VARIANT_EQUALITY info Variant.From(tex).Equals(Variant.From(tex)) = false (the bug RingVariantEquality works around)");

            var f = new RingWindField();
            f.SetCached("ring_wind_gust_strength", 0.5f);
            f.SetCached("ring_wind_gust_strength", 0.5f);
            Check(f.GlobalWriteCount == 1, "repeat float write count " + f.GlobalWriteCount + " != 1");
            f.SetCached("ring_wind_gust_texture", Variant.From(tex));
            f.SetCached("ring_wind_gust_texture", Variant.From(tex));
            Check(f.GlobalWriteCount == 2, "repeat texture write count " + f.GlobalWriteCount + " != 2");
            f.SetCached("ring_wind_gust_texture", Variant.From(other));
            Check(f.GlobalWriteCount == 3, "new texture write count " + f.GlobalWriteCount + " != 3");
            f.Dispose();
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("VARIANT_EQUALITY=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("VARIANT_EQUALITY=PASS");
        return 0;
    }
}
