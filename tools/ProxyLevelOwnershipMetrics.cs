using System;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Pixel metrics for tools/proxy_level_ownership_gpu_test.gd (docs/proxy-coverage-culling.md, "Resident coverage"). The proxy levels render with
/// diagnostic colours: the finest active level pure red, every coarser level pure green.
///   Poke-through: green pixels of a full frame where a frame with only the finest level drawn is red (coarse terrain in front of the finest level).
///   Holes: pixels where the frame with resident coverage on matches the frame with every proxy level hidden (sky or far terrain shows) while the frame
///   with resident coverage off (the former overlap, which never has holes) differs strongly from it. The three frames are of one frozen view.
/// </summary>
public partial class ProxyLevelOwnershipMetrics : RefCounted
{
    public const float HoleOffDifference = 0.15f;
    public const float HoleOnSimilarity = 0.03f;

    public Godot.Collections.Dictionary Holes(Image on, Image off, Image none)
    {
        Image a = Prepare(on), b = Prepare(off), c = Prepare(none);
        int width = a.GetWidth(), height = a.GetHeight();
        if (b.GetWidth() != width || c.GetWidth() != width || b.GetHeight() != height || c.GetHeight() != height)
            return new Godot.Collections.Dictionary { ["error"] = "frame sizes differ" };
        byte[] da = a.GetData(), db = b.GetData(), dc = c.GetData();
        long holes = 0, terrainOff = 0, onOffChanged = 0;
        for (int i = 0; i < width * height * 4; i += 4)
        {
            if (MaxDiff(da, db, i) > HoleOffDifference) onOffChanged++;
            float offDiff = MaxDiff(db, dc, i);
            if (offDiff <= HoleOffDifference) continue;
            terrainOff++;
            if (MaxDiff(da, dc, i) < HoleOnSimilarity) holes++;
        }
        return new Godot.Collections.Dictionary
        {
            ["holes"] = holes, ["proxy_pixels_off"] = terrainOff, ["pixels"] = (long)width * height, ["on_off_changed"] = onOffChanged,
            ["hole_fraction"] = holes / (double)Math.Max(1L, (long)width * height),
        };
    }

    /// <summary>
    /// Coarse terrain in front of the finest level: pixels green in <paramref name="frame"/> (all levels drawn) but red in <paramref name="finestOnly"/>
    /// (only the finest level drawn), so the finest level draws there and a coarser level covers it. Fraction of the finest level's pixels.
    /// </summary>
    public Godot.Collections.Dictionary Poke(Image frame, Image finestOnly)
    {
        Image a = Prepare(frame), f = Prepare(finestOnly);
        if (a.GetWidth() != f.GetWidth() || a.GetHeight() != f.GetHeight()) return new Godot.Collections.Dictionary { ["error"] = "frame sizes differ" };
        byte[] da = a.GetData(), df = f.GetData();
        long poke = 0, finest = 0;
        for (int i = 0; i < da.Length; i += 4)
        {
            if (!IsRed(df, i)) continue;
            finest++;
            if (IsGreen(da, i)) poke++;
        }
        return new Godot.Collections.Dictionary { ["poke"] = poke, ["finest_pixels"] = finest, ["poke_fraction"] = poke / (double)Math.Max(1L, finest) };
    }

    private static bool IsRed(byte[] d, int i) => d[i] > d[i + 1] + 30 && d[i] > d[i + 2] + 30;
    private static bool IsGreen(byte[] d, int i) => d[i + 1] > d[i] + 30 && d[i + 1] > d[i + 2] + 20;

    private static float MaxDiff(byte[] x, byte[] y, int i)
        => Math.Max(Math.Abs(x[i] - y[i]), Math.Max(Math.Abs(x[i + 1] - y[i + 1]), Math.Abs(x[i + 2] - y[i + 2]))) / 255f;

    private static Image Prepare(Image source)
    {
        Image img = (Image)source.Duplicate();
        if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
        return img;
    }
}
