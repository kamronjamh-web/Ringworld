using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO-E1 (docs/storm-revamp-s3-storm-arrival-design.md sections 5 and 11): the post-rain mist factors of RingAtmosphereModel. At M = 0 the physical haze is
/// bit-identical and nothing is published; at M = 1 the weather adds 2e-3 extinction (about 1.9 km ground visibility), height x0.4; slew <= 2/32 per publish; changes are quantised to 1/32, published at most every 2 s
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

            // M = 1: the weather adds 2e-3 of mist extinction and shrinks the height to x0.4, one Revision.
            Check(RingAtmosphereModel.SetWeatherMist(1f, 10.0, immediate: true), "M=1 must publish");
            Check(RingAtmosphereModel.Revision == baseRevision + 1, "one publish must bump Revision once");
            Check(Math.Abs(RingAtmosphereModel.HazeSigmaMist - baseSigma - RingAtmosphereModel.WeatherMistSigmaPerStrength * RingAtmosphereModel.Density) < 1e-5f, $"added mist sigma {RingAtmosphereModel.HazeSigmaMist - baseSigma}");
            Check(Math.Abs(RingAtmosphereModel.HazeMistHeightMeters / baseHeight - 0.4f) < 1e-3f, $"mist height factor {RingAtmosphereModel.HazeMistHeightMeters / baseHeight}");
            // Ground visibility at M = 1: the added 2e-3 plus the clear air gives mist (1-5 km by the WMO definition), not fog.
            float extinction = RingAtmosphereModel.HazeSigmaRayleigh.Y + RingAtmosphereModel.HazeSigmaAerosol + RingAtmosphereModel.HazeSigmaMist;
            float visibilityKm = 3.912f / extinction / 1000f;
            if (verbose) Console.WriteLine($"M=1 ground visibility {visibilityKm:0.0} km (mist sigma {RingAtmosphereModel.HazeSigmaMist * 1e6f:0.0}e-6)");
            Check(visibilityKm >= 1.2f && visibilityKm <= 3.0f, $"ground visibility at M=1 is {visibilityKm:0.00} km (target 1.2-3 km, mist not fog)");

            // A user mist of zero (aerosol control 0) must still fog: the weather mist is additive.
            RingAtmosphereModel.ClearWeatherMist();
            RingAtmosphereModel.ApplyArtDirection(RingAtmosphereModel.ArtPreset, RingAtmosphereModel.VisibilityFullKm, RingAtmosphereModel.Density, RingAtmosphereModel.AltitudeFalloff, 0.0f, 0.22f, 0.25f);
            Check(RingAtmosphereModel.HazeSigmaMist == 0f, "zero aerosol control gives zero base mist");
            Check(RingAtmosphereModel.SetWeatherMist(1f, 3000.0, immediate: true) && RingAtmosphereModel.HazeSigmaMist > 1e-3f, $"weather mist with a zero user mist: {RingAtmosphereModel.HazeSigmaMist}");
            RingAtmosphereModel.ClearWeatherMist();
            RingAtmosphereModel.ApplyArtDirection(RingAtmosphereModel.ArtPreset, RingAtmosphereModel.VisibilityFullKm, RingAtmosphereModel.Density, RingAtmosphereModel.AltitudeFalloff, 0.35f, 0.22f, 0.25f);
            RingAtmosphereModel.SetWeatherMist(1f, 10.0, immediate: true);

            // Rate limit and quantisation.
            Check(!RingAtmosphereModel.SetWeatherMist(0.5f, 11.0), "a second publish within 2 s must wait");
            Check(!RingAtmosphereModel.SetWeatherMist(0.99f, 13.0), "a sub-step change must not publish");
            Check(RingAtmosphereModel.SetWeatherMist(0.5f, 12.5), "a publish after 2 s must go through");
            Check(Math.Abs(RingAtmosphereModel.WeatherMist - (1f - 2f / 32f)) < 1e-6f, $"slew limits the first step to 2/32: {RingAtmosphereModel.WeatherMist}");
            {
                float prev = RingAtmosphereModel.WeatherMist; bool stepsOk = true; int publishes = 1;
                for (double t = 14.5; t < 60.0 && RingAtmosphereModel.WeatherMist != 0.5f; t += 2.0)
                {
                    if (!RingAtmosphereModel.SetWeatherMist(0.5f, t)) continue;
                    publishes++;
                    stepsOk &= Math.Abs(RingAtmosphereModel.WeatherMist - prev) <= 2f / 32f + 1e-6f;
                    prev = RingAtmosphereModel.WeatherMist;
                }
                Check(RingAtmosphereModel.WeatherMist == 0.5f && publishes == 8 && stepsOk, $"slew converges to 0.5 in 8 publishes: {RingAtmosphereModel.WeatherMist} after {publishes}");
            }
            // immediate jumps straight to the quantised target.
            Check(RingAtmosphereModel.SetWeatherMist(0.9f, 100.0, immediate: true) && Math.Abs(RingAtmosphereModel.WeatherMist - 29f / 32f) < 1e-6f, $"immediate jumps to the quantised target: {RingAtmosphereModel.WeatherMist}");
            RingAtmosphereModel.ClearWeatherMist();
            Check(BitConverter.SingleToInt32Bits(RingAtmosphereModel.HazeSigmaMist) == BitConverter.SingleToInt32Bits(baseSigma)
                && BitConverter.SingleToInt32Bits(RingAtmosphereModel.HazeMistHeightMeters) == BitConverter.SingleToInt32Bits(baseHeight), "ClearWeatherMist must restore the haze exactly");

            // Cap: a fast ramp up and a slow decay, called at 4 Hz for an hour, never exceeds 30 publishes per 10 minutes or spaces them under 2 s.
            ulong before = RingAtmosphereModel.Revision;
            var published = new List<double>();
            float lastValue = 0f, maxJump = 0f, valueAt150 = 0f;
            for (double t = 0.0; t < 3600.0; t += 0.25)
            {
                float target = t < 90.0 ? (float)(t / 90.0) : (float)Math.Exp(-(t - 90.0) / 1200.0);
                if (RingAtmosphereModel.SetWeatherMist(target, t))
                {
                    published.Add(t);
                    maxJump = Math.Max(maxJump, Math.Abs(RingAtmosphereModel.WeatherMist - lastValue));
                    lastValue = RingAtmosphereModel.WeatherMist;
                }
                if (t < 150.0) valueAt150 = Math.Max(valueAt150, RingAtmosphereModel.WeatherMist);
            }
            Check(maxJump <= 2f / 32f + 1e-6f, $"published steps must be <= 2/32: max {maxJump}");
            Check(valueAt150 >= 0.9f, $"mist must reach 0.9 (peak) by t=150 s: {valueAt150}");
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

            // Megastorm wake fog drives M with the post-rain setting 0 (no layer bound): 0 to 1 in <= 9 publishes (4 steps each), then releases to 0 within the cap.
            RingAtmosphereModel.ClearWeatherMist();
            var mist = new RingPostRainMist();
            float wakeValue = 1f;
            mist.WakeFogProvider = () => wakeValue;
            mist.Tick(0.0);
            Check(RingAtmosphereModel.WeatherMist == 4f / 32f && mist.LastWakeFog == 1f, $"first wake publish is 4/32: {RingAtmosphereModel.WeatherMist}");
            int wakeUp = 1; double tUp = 0.0;
            for (double t = 0.25; t < 40.0 && RingAtmosphereModel.WeatherMist < 1f; t += 0.25) { ulong r = RingAtmosphereModel.Revision; mist.Tick(t); if (RingAtmosphereModel.Revision != r) { wakeUp++; tUp = t; } }
            Check(RingAtmosphereModel.WeatherMist == 1f && wakeUp <= 9 && tUp <= 18.0, $"wake fog reaches 1 in {wakeUp} publishes at {tUp} s");
            wakeValue = 0f; int wakeDown = 0; double tDown = 0.0;
            for (double t = 20.0; t < 60.0 && RingAtmosphereModel.WeatherMist > 0f; t += 0.25) { ulong r = RingAtmosphereModel.Revision; mist.Tick(t); if (RingAtmosphereModel.Revision != r) { wakeDown++; tDown = t; } }
            Check(RingAtmosphereModel.WeatherMist == 0f && wakeDown <= 9, $"wake fog releases to 0 in {wakeDown} publishes at {tDown} s");
            Check(wakeUp + wakeDown <= RingAtmosphereModel.WeatherMistMaxPublishesPerWindow, $"wake round trip {wakeUp + wakeDown} publishes exceeds the cap");
            // Both zero: today's behaviour, no publish, nothing held.
            ulong revIdle = RingAtmosphereModel.Revision; mist.Tick(70.0);
            Check(RingAtmosphereModel.Revision == revIdle && RingAtmosphereModel.WeatherMist == 0f && mist.TargetMist == 0f, "both zero must not publish");
            // Default step limit unchanged for other callers.
            RingAtmosphereModel.ClearWeatherMist();
            RingAtmosphereModel.SetWeatherMist(1f, 1000.0);
            Check(RingAtmosphereModel.WeatherMist == 2f / 32f, $"default slew is still 2/32: {RingAtmosphereModel.WeatherMist}");
            mist.Free();
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        finally { RingAtmosphereModel.ClearWeatherMist(); }
        if (failures.Count != 0) { Console.Error.WriteLine("POST_RAIN_MIST=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("POST_RAIN_MIST=PASS");
        return 0;
    }
}
