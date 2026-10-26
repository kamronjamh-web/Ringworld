using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO-E1 (docs/storm-revamp-s3-storm-arrival-design.md sections 5 and 11): the post-rain mist factors of RingAtmosphereModel. At M = 0 the physical haze is
/// bit-identical and nothing is published; at M = 1 the mist density is x1801 and its height x0.4; changes are quantised to 1/32, published at most every 2 s
/// and at most 30 times per 10 minutes through Revision; the driver formula is calm x cool x Clearing x setting.
/// Run: tools/post_rain_mist_test.gd (headless). Prints POST_RAIN_MIST=PASS.
/// </summary>
public partial class PostRainMistTest : RefCounted
{
    public int Run(bool verbose)
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            RingAtmosphereModel.ClearWeatherMist();
            RingAtmosphereModel.ApplyArtDirection(RingAtmosphereModel.ArtPreset, RingAtmosphereModel.VisibilityFullKm, RingAtmosphereModel.Density, RingAtmosphereModel.AltitudeFalloff, 0.35f, 0.22f, 0.25f);
            float baseSigma = RingAtmosphereModel.HazeSigmaMist, baseHeight = RingAtmosphereModel.HazeMistHeightMeters;
            ulong baseRevision = RingAtmosphereModel.Revision;
            Check(baseSigma > 0f && baseHeight > 0f, "no base mist");

            // M = 0: nothing published, bit-identical.
            Check(!RingAtmosphereModel.SetWeatherMist(0f, 0.0) && RingAtmosphereModel.Revision == baseRevision, "M=0 must not publish");
            Check(!RingAtmosphereModel.SetWeatherMist(float.NaN, 1.0) && !RingAtmosphereModel.SetWeatherMist(0.01f, 2.0), "NaN and sub-step values must not publish");
            Check(BitConverter.SingleToInt32Bits(RingAtmosphereModel.HazeSigmaMist) == BitConverter.SingleToInt32Bits(baseSigma)
                && BitConverter.SingleToInt32Bits(RingAtmosphereModel.HazeMistHeightMeters) == BitConverter.SingleToInt32Bits(baseHeight), "M=0 must leave the mist bit-identical");

            // M = 1: the weather adds 1e-2 of mist extinction and shrinks the height to x0.4, one Revision.
            Check(RingAtmosphereModel.SetWeatherMist(1f, 10.0), "M=1 must publish");
            Check(RingAtmosphereModel.Revision == baseRevision + 1, "one publish must bump Revision once");
            Check(Math.Abs(RingAtmosphereModel.HazeSigmaMist - baseSigma - 1.0e-2f * RingAtmosphereModel.Density) < 1e-5f, $"added mist sigma {RingAtmosphereModel.HazeSigmaMist - baseSigma}");
            Check(Math.Abs(RingAtmosphereModel.HazeMistHeightMeters / baseHeight - 0.4f) < 1e-3f, $"mist height factor {RingAtmosphereModel.HazeMistHeightMeters / baseHeight}");
            // Ground visibility at M = 1: the added 1e-2 plus the clear air gives a few hundred metres.
            float extinction = RingAtmosphereModel.HazeSigmaRayleigh.Y + RingAtmosphereModel.HazeSigmaAerosol + RingAtmosphereModel.HazeSigmaMist;
            float visibilityKm = 3.912f / extinction / 1000f;
            if (verbose) Console.WriteLine($"M=1 ground visibility {visibilityKm:0.0} km (mist sigma {RingAtmosphereModel.HazeSigmaMist * 1e6f:0.0}e-6)");
            Check(visibilityKm >= 0.3f && visibilityKm <= 0.8f, $"ground visibility at M=1 is {visibilityKm:0.00} km (retuned target 300-800 m)");

            // A user mist of zero (aerosol control 0) must still fog: the weather mist is additive.
            RingAtmosphereModel.ClearWeatherMist();
            RingAtmosphereModel.ApplyArtDirection(RingAtmosphereModel.ArtPreset, RingAtmosphereModel.VisibilityFullKm, RingAtmosphereModel.Density, RingAtmosphereModel.AltitudeFalloff, 0.0f, 0.22f, 0.25f);
            Check(RingAtmosphereModel.HazeSigmaMist == 0f, "zero aerosol control gives zero base mist");
            Check(RingAtmosphereModel.SetWeatherMist(1f, 3000.0) && RingAtmosphereModel.HazeSigmaMist > 5e-3f, $"weather mist with a zero user mist: {RingAtmosphereModel.HazeSigmaMist}");
            RingAtmosphereModel.ClearWeatherMist();
            RingAtmosphereModel.ApplyArtDirection(RingAtmosphereModel.ArtPreset, RingAtmosphereModel.VisibilityFullKm, RingAtmosphereModel.Density, RingAtmosphereModel.AltitudeFalloff, 0.35f, 0.22f, 0.25f);
            RingAtmosphereModel.SetWeatherMist(1f, 10.0);

            // Rate limit and quantisation.
            Check(!RingAtmosphereModel.SetWeatherMist(0.5f, 11.0), "a second publish within 2 s must wait");
            Check(!RingAtmosphereModel.SetWeatherMist(0.99f, 13.0), "a sub-step change must not publish");
            Check(RingAtmosphereModel.SetWeatherMist(0.5f, 12.5), "a publish after 2 s must go through");
            Check(Math.Abs(RingAtmosphereModel.WeatherMist - 0.5f) < 1e-6f, "published value is quantised");
            RingAtmosphereModel.ClearWeatherMist();
            Check(BitConverter.SingleToInt32Bits(RingAtmosphereModel.HazeSigmaMist) == BitConverter.SingleToInt32Bits(baseSigma)
                && BitConverter.SingleToInt32Bits(RingAtmosphereModel.HazeMistHeightMeters) == BitConverter.SingleToInt32Bits(baseHeight), "ClearWeatherMist must restore the haze exactly");

            // Cap: a fast ramp up and a slow decay, called at 4 Hz for an hour, never exceeds 30 publishes per 10 minutes or spaces them under 2 s.
            ulong before = RingAtmosphereModel.Revision;
            var published = new List<double>();
            for (double t = 0.0; t < 3600.0; t += 0.25)
            {
                float target = t < 90.0 ? (float)(t / 90.0) : (float)Math.Exp(-(t - 90.0) / 1200.0);
                if (RingAtmosphereModel.SetWeatherMist(target, t)) published.Add(t);
            }
            Check((ulong)published.Count == RingAtmosphereModel.Revision - before, "Revision must count exactly the publishes");
            double minGap = double.MaxValue; int worstWindow = 0;
            for (int i = 0; i < published.Count; i++)
            {
                if (i > 0) minGap = Math.Min(minGap, published[i] - published[i - 1]);
                int inWindow = published.Count(p => p >= published[i] && p < published[i] + 600.0);
                worstWindow = Math.Max(worstWindow, inWindow);
            }
            if (verbose) Console.WriteLine($"publishes={published.Count} minGap={minGap:0.00}s worst per 10 min={worstWindow}");
            Check(minGap >= 2.0 && worstWindow <= 30, $"publish cap violated: minGap={minGap} worstWindow={worstWindow}");
            Check(published.Count >= 20, $"mist ramp published too rarely ({published.Count})");
            RingAtmosphereModel.ClearWeatherMist();

            // Driver formula.
            Check(RingPostRainMist.Target(1f, 1f, 0f, -0.12f) == 1f, "calm cool clearing air gives M = 1");
            Check(RingPostRainMist.Target(1f, 1f, 9f, -0.12f) == 0f, "wind above 8 m/s gives no mist");
            Check(RingPostRainMist.Target(1f, 1f, 0f, 0f) == 0f, "no cooling gives no mist");
            Check(RingPostRainMist.Target(1f, 0f, 0f, -0.12f) == 0f, "no Clearing gives no mist");
            Check(RingPostRainMist.Target(0f, 1f, 0f, -0.12f) == 0f, "setting 0 gives no mist");
            Check(Math.Abs(RingPostRainMist.Target(2f, 1f, 0f, -0.12f) - 2f) < 1e-6f, "setting 2 doubles the strength");
            Check(RingPostRainMist.Target(1f, 1f, 5.5f, -0.12f) > 0.4f && RingPostRainMist.Target(1f, 1f, 5.5f, -0.12f) < 0.6f, "wind 5.5 m/s halves the mist");
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        finally { RingAtmosphereModel.ClearWeatherMist(); }
        if (failures.Count != 0) { Console.Error.WriteLine("POST_RAIN_MIST=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("POST_RAIN_MIST=PASS");
        return 0;
    }
}
