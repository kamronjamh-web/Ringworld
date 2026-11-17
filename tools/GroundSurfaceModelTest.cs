using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Terrain.Surface;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T4-3: shape checks of RingGroundSurfaceModel (T4 design D2 rock exposure, D3 strata filter, D4 scree, the slope-bias table) and the rock-mean constant against
/// materials/biomes/alpine_rock_albedo.png. Run via tools/ground_surface_model_test.gd. Prints GROUND_SURFACE_MODEL=PASS.
/// </summary>
public partial class GroundSurfaceModelTest : RefCounted
{
    public bool Run()
    {
        var failures = new List<string>();
        try { RunChecks(failures); }
        catch (Exception exception) { failures.Add(exception.ToString()); }
        foreach (string failure in failures) GD.PushError("GROUND_SURFACE_MODEL=FAIL " + failure);
        return failures.Count == 0;
    }

    static void Check(List<string> failures, bool ok, string message) { if (!ok) failures.Add(message); }

    static void RunChecks(List<string> failures)
    {
        // Slope-bias table: 0 at the 4 m reference, 1.5 deg per octave, capped at 12.
        double[] biasBaselines = { 2, 4, 8, 16, 32, 64, 128, 1024, 100000 };
        double[] biasExpected = { 0, 0, 1.5, 3, 4.5, 6, 7.5, 12, 12 };
        for (int i = 0; i < biasBaselines.Length; i++)
            Check(failures, Math.Abs(RingGroundSurfaceModel.SlopeBiasDegrees(biasBaselines[i]) - biasExpected[i]) < 1e-9, $"slope bias at {biasBaselines[i]} m is {RingGroundSurfaceModel.SlopeBiasDegrees(biasBaselines[i])}, expected {biasExpected[i]}");

        // D2: monotonic in slope, 0 below the start, 1 above start + ramp, 0.5 mid ramp; convex starts earlier, hollows later, forest later.
        double previous = -1;
        for (double slope = 0; slope <= 90; slope += 0.5)
        {
            double exposure = RingGroundSurfaceModel.RockExposure(slope, 4, 0, 0);
            Check(failures, exposure >= previous - 1e-12, $"rock exposure not monotonic at {slope} deg");
            previous = exposure;
        }
        Check(failures, RingGroundSurfaceModel.RockExposure(10, 4, 0, 0) == 0 && RingGroundSurfaceModel.RockExposure(37.9, 4, 0, 0) < 0.01, "rock exposure is not 0 below the start");
        Check(failures, RingGroundSurfaceModel.RockExposure(46, 4, 0, 0) > 0.999 && RingGroundSurfaceModel.RockExposure(50, 4, 0, 0) > 0.9999, "rock exposure is not 1 from start + ramp");
        Check(failures, Math.Abs(RingGroundSurfaceModel.RockExposure(42, 4, 0, 0) - 0.5) < 1e-9, "rock exposure is not 0.5 at mid ramp");
        Check(failures, Math.Abs(RingGroundSurfaceModel.RockExposure(32, 4, 1, 0) - RingGroundSurfaceModel.RockExposure(38, 4, 0, 0)) < 1e-9, "a convex crest (relief +1) does not start rock 6 degrees earlier");
        Check(failures, Math.Abs(RingGroundSurfaceModel.RockExposure(44, 4, -1, 0) - RingGroundSurfaceModel.RockExposure(38, 4, 0, 0)) < 1e-9, "a hollow (relief -1) does not start rock 6 degrees later");
        Check(failures, Math.Abs(RingGroundSurfaceModel.RockExposure(42, 4, 0, 1) - RingGroundSurfaceModel.RockExposure(38, 4, 0, 0)) < 1e-9, "dense forest does not hold soil 4 degrees longer");
        Check(failures, Math.Abs(RingGroundSurfaceModel.RockExposure(35, 16, 0, 0) - RingGroundSurfaceModel.RockExposure(38, 4, 0, 0)) < 1e-9, "the proxy slope bias is not added to the slope");
        Check(failures, Math.Abs(RingGroundSurfaceModel.RockExposure(30, 4, 0, 0, 30) - 0) < 1e-12 && RingGroundSurfaceModel.RockExposure(38, 4, 0, 0, 30) > 0.999, "the rock start parameter does not move the ramp");

        // D4: scree is a band, off below 26 and above 40 degrees, full between 30 and 36, convex ground and vegetation reduce it.
        Check(failures, RingGroundSurfaceModel.ScreeWeight(20, 4, 0, 0) == 0 && RingGroundSurfaceModel.ScreeWeight(41, 4, 0, 0) == 0, "scree outside 26-40 degrees");
        Check(failures, Math.Abs(RingGroundSurfaceModel.ScreeWeight(33, 4, 0, 0) - 1) < 1e-12, "scree is not 1 at 33 degrees on bare concave ground");
        Check(failures, Math.Abs(RingGroundSurfaceModel.ScreeWeight(33, 4, 0, 1) - 0.4) < 1e-12, "vegetated ground does not carry 40 % scree");
        Check(failures, RingGroundSurfaceModel.ScreeWeight(33, 4, 0.5, 0) < 1e-12 && RingGroundSurfaceModel.ScreeWeight(33, 4, 0.05, 0) > 0.999, "convex ground does not lose scree between relief 0.1 and 0.5");
        Check(failures, RingGroundSurfaceModel.ScreeWeight(30, 4, 0, 0) > 0.999 && RingGroundSurfaceModel.ScreeWeight(28, 4, 0, 0) is > 0.2 and < 0.8, "scree ramp in");
        Check(failures, Math.Abs(RingGroundSurfaceModel.Relief(2) - 0.5) < 1e-12 && Math.Abs(RingGroundSurfaceModel.Relief(-2) + 0.5) < 1e-12 && RingGroundSurfaceModel.Relief(0) == 0, "relief mapping");

        // D3: tones are in [-1, 1] with mean 0; the box filter keeps the mean, is continuous across a boundary and returns the band mean from a footprint of 0.9 bands up.
        double toneSum = 0, toneMin = 1, toneMax = -1;
        const int toneCount = 100000;
        for (int bed = -toneCount / 2; bed < toneCount / 2; bed++)
        {
            double tone = RingGroundSurfaceModel.Tone(bed, 1234);
            toneSum += tone; toneMin = Math.Min(toneMin, tone); toneMax = Math.Max(toneMax, tone);
        }
        Check(failures, Math.Abs(toneSum / toneCount) < 0.01 && toneMin >= -1 && toneMax <= 1 && toneMax - toneMin > 1.9, $"tone distribution mean {toneSum / toneCount:F4} range {toneMin:F3}..{toneMax:F3}");
        Check(failures, RingGroundSurfaceModel.Tone(7, 11) != RingGroundSurfaceModel.Tone(7, 12) || RingGroundSurfaceModel.Tone(8, 11) != RingGroundSurfaceModel.Tone(8, 12), "the seed does not change the tones");
        const double step = 40;
        foreach (double footprintBands in new[] { 0.001, 0.05, 0.2, 0.34 })
        {
            double fw = footprintBands * step;
            double sum = 0, tones = 0; const int beds = 120; const int samples = 24000;
            for (int s = 0; s < samples; s++)
            {
                double u = 10.0 + beds * (s + 0.5) / samples;
                sum += RingGroundSurfaceModel.StrataBand(u * step, fw, step, 77);
            }
            for (int bed = 10; bed < 10 + beds; bed++) tones += RingGroundSurfaceModel.Tone(bed, 77);
            Check(failures, Math.Abs(sum / samples - tones / beds) < 0.01 + footprintBands * 0.2, $"filter changes the band mean at footprint {footprintBands}: {sum / samples:F4} vs {tones / beds:F4}");
        }
        foreach (double boundary in new[] { 3 * step, -5 * step, 1234 * step })
            foreach (double footprintBands in new[] { 0.02, 0.2, 0.6 })
            {
                double fw = footprintBands * step;
                double left = RingGroundSurfaceModel.StrataBand(boundary - 1e-6 * step, fw, step, 5);
                double right = RingGroundSurfaceModel.StrataBand(boundary + 1e-6 * step, fw, step, 5);
                Check(failures, Math.Abs(left - right) < 1e-3, $"strata band is not continuous across the boundary at {boundary} for footprint {footprintBands}: {left:F5} vs {right:F5}");
            }
        for (double footprintBands = 0.9; footprintBands < 3.0; footprintBands += 0.3)
            Check(failures, RingGroundSurfaceModel.StrataBand(123.456, footprintBands * step, step, 5) == 0
                && Math.Abs(RingGroundSurfaceModel.StrataFactor(123.456, 40 * step, step, 5) - 1) < 1e-9, $"strata contrast is not 0 at a footprint of {footprintBands} bands");
        Check(failures, RingGroundSurfaceModel.StrataBand(100, 0.0, step, 5) == RingGroundSurfaceModel.Tone(2, 5), "a point sample is not the bed's flat tone");
        Check(failures, Math.Abs(RingGroundSurfaceModel.StrataFactor(100, 0.0, step, 5, 0.12) - (1 + 0.12 * RingGroundSurfaceModel.Tone(2, 5)) * (1 + 0.06 * RingGroundSurfaceModel.StrataBand(100, 0.0, 5, 5 ^ 40503u))) < 1e-12, "strata factor composition");

        // D7: the shipped near shaders are the frozen generation 1 shaders (no ground code); the ground variants define RING_GROUND_SURFACE.
        foreach (string shipped in new[] { "ring_terrain_proxy", "terrain3d_ring_surface" })
        {
            string plain = FileAccess.GetFileAsString($"res://materials/{shipped}.gdshader");
            string ground = FileAccess.GetFileAsString($"res://materials/{shipped}_ground.gdshader");
            Check(failures, plain.Length > 1000 && !plain.Contains("RING_GROUND_SURFACE") && !plain.Contains("ring_ground"), $"{shipped}.gdshader (generation 1) mentions the ground surface");
            Check(failures, ground.Contains("#define RING_GROUND_SURFACE") && ground.Contains("ring_ground_surface.gdshaderinc"), $"{shipped}_ground.gdshader does not define RING_GROUND_SURFACE and include the ground functions");
        }

        // D1: the rock-mean constant equals the texture's mean linear colour within 0.01.
        Image image = Image.LoadFromFile(ProjectSettings.GlobalizePath("res://materials/biomes/alpine_rock_albedo.png"));
        Check(failures, image != null && !image.IsEmpty(), "alpine_rock_albedo.png did not load");
        if (image != null && !image.IsEmpty())
        {
            image.Convert(Image.Format.Rgb8);
            byte[] data = image.GetData();
            var lut = new double[256];
            for (int i = 0; i < 256; i++) { double c = i / 255.0; lut[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            double r = 0, g = 0, b = 0; int pixels = data.Length / 3;
            for (int i = 0; i < pixels; i++) { r += lut[data[i * 3]]; g += lut[data[i * 3 + 1]]; b += lut[data[i * 3 + 2]]; }
            r /= pixels; g /= pixels; b /= pixels;
            var mean = RingGroundSurfaceModel.RockMeanLinear;
            Check(failures, Math.Abs(r - mean.R) < 0.01 && Math.Abs(g - mean.G) < 0.01 && Math.Abs(b - mean.B) < 0.01, $"rock mean linear constant {mean} differs from the texture mean ({r:F4}, {g:F4}, {b:F4})");
            GD.Print($"GROUND_SURFACE_MODEL rock mean linear measured ({r:F4}, {g:F4}, {b:F4}), constant ({mean.R}, {mean.G}, {mean.B})");
        }
    }
}
