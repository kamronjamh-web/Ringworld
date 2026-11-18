using System;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>
/// C# mirror of materials/ring_ground_surface.gdshaderinc (T4 design D2 to D5): same formulas and constants in double precision, for tests,
/// the habitat grid and T5 scatter. Keep it in step with the include; ground_surface_model_test and terrain_rock_material_gpu_test compare them.
/// </summary>
public static class RingGroundSurfaceModel
{
    public const int SurfaceBit = 2;
    public const double RockRampDeg = 8.0;
    public const double ConvexShiftDeg = 6.0;
    public const double ForestShiftDeg = 4.0;
    public const double SlopeRefBaselineM = 4.0;
    public const double SlopeBiasPerOctaveDeg = 1.0; // calibrated in T4-5 (GroundSlopeCalibration)
    public const double SlopeBiasMaxDeg = 12.0;
    public const double FarTileBaselineM = 128.0;
    public const double ScreeInStart = 26.0, ScreeInEnd = 30.0, ScreeOutStart = 36.0, ScreeOutEnd = 40.0;
    public const double ScreeConvexFadeStart = 0.1, ScreeConvexFadeEnd = 0.5;
    public const double ScreeVegetated = 0.4;
    public const double StrataSubDiv = 8.0;
    public const double StrataSubRatio = 0.5;
    public const double StrataFadeWStart = 0.35, StrataFadeWEnd = 0.9;
    public const double ReliefRefM = 2.0;
    public const double ReliefFadeStartM = 16000.0, ReliefFadeEndM = 24000.0;
    public const double DetailNormalFadeStartM = 40.0, DetailNormalFadeEndM = 60.0;
    public const double DefaultColourVariation = 1.0;
    public const double DefaultRockStartDeg = 38.0;
    public const double DefaultStrataContrast = 0.12;
    public const double DefaultStrataStepM = 40.0;
    public static readonly (double R, double G, double B) RockTint = (0.92, 0.93, 0.96);
    public static readonly (double R, double G, double B) ScreeTint = (1.04, 1.02, 0.98);
    /// <summary>Mean linear colour of materials/biomes/alpine_rock_albedo.png.</summary>
    public static readonly (double R, double G, double B) RockMeanLinear = (0.2189, 0.2220, 0.2160);

    public static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    public static double SlopeDegrees(double upDot) => Math.Acos(Math.Clamp(upDot, -1.0, 1.0)) * 180.0 / Math.PI;

    public static double SlopeBiasDegrees(double baselineM, double perOctaveDeg = SlopeBiasPerOctaveDeg)
        => Math.Min(perOctaveDeg * Math.Max(Math.Log2(baselineM / SlopeRefBaselineM), 0.0), SlopeBiasMaxDeg);

    public static double RockExposure(double slopeDeg, double baselineM, double relief, double forestWeight, double rockStartDeg = DefaultRockStartDeg)
    {
        double s = slopeDeg + SlopeBiasDegrees(baselineM) + ConvexShiftDeg * relief - ForestShiftDeg * forestWeight;
        return SmoothStep(rockStartDeg, rockStartDeg + RockRampDeg, s);
    }

    /// <param name="vegetated">Grass weight plus forest weight.</param>
    public static double ScreeWeight(double slopeDeg, double baselineM, double relief, double vegetated)
    {
        double s = slopeDeg + SlopeBiasDegrees(baselineM);
        double band = SmoothStep(ScreeInStart, ScreeInEnd, s) * (1.0 - SmoothStep(ScreeOutStart, ScreeOutEnd, s));
        return band * (1.0 - SmoothStep(ScreeConvexFadeStart, ScreeConvexFadeEnd, relief))
            * (1.0 + (ScreeVegetated - 1.0) * Math.Clamp(vegetated, 0.0, 1.0));
    }

    /// <summary>D5: C64 metres to a value in (-1, 1).</summary>
    public static double Relief(double c64) => c64 / (Math.Abs(c64) + ReliefRefM);

    /// <summary>D5: the colour variation on a dry albedo (linear): convex ground paler and a little less saturated, hollows darker and greener. d is the distance to the camera in metres.</summary>
    public static (double R, double G, double B) ReliefColour((double R, double G, double B) col, double relief, double rock, double distanceM, double strength = DefaultColourVariation)
    {
        double s = strength * (1.0 - SmoothStep(ReliefFadeStartM, ReliefFadeEndM, distanceM)) * (1.0 - rock);
        double lum = col.R * 0.2126 + col.G * 0.7152 + col.B * 0.0722;
        double convex = s * Math.Max(relief, 0.0);
        double hollow = s * Math.Max(-relief, 0.0);
        double Pale(double c) => (c + (lum - c) * 0.25) * 1.12;
        double r = col.R + (Pale(col.R) - col.R) * convex, g = col.G + (Pale(col.G) - col.G) * convex, b = col.B + (Pale(col.B) - col.B) * convex;
        return (r + (r * 0.88 - r) * hollow, g + (g * 0.94 - g) * hollow, b + (b * 0.86 - b) * hollow);
    }

    /// <summary>D6: the detail normal weight of rock and scree at distance d (m).</summary>
    public static double DetailNormalWeight(double rock, double scree, double distanceM)
        => (rock + 0.6 * scree) * (1.0 - SmoothStep(DetailNormalFadeStartM, DetailNormalFadeEndM, distanceM));

    /// <summary>D5: the canonical lattice the convexity nodes sit on, and the tap distance, in metres.</summary>
    public const double ReliefLatticeM = 32.0, ReliefTapM = 64.0;

    /// <summary>D5: C64 at a lattice node (around and across on the 32 m lattice): the height less the mean of the four height taps 64 m away. Metres; positive on a crest. <paramref name="heightAt"/> clamps across itself.</summary>
    public static double NodeConvexity(Func<double, double, double> heightAt, double around, double across)
        => heightAt(around, across) - 0.25 * (heightAt(around + ReliefTapM, across) + heightAt(around - ReliefTapM, across)
            + heightAt(around, across + ReliefTapM) + heightAt(around, across - ReliefTapM));

    /// <summary>D5: C64 at any point, the bilinear value of the four nodes of its 32 m cell.</summary>
    public static double Convexity64(Func<double, double, double> heightAt, double around, double across)
    {
        double ua = around / ReliefLatticeM, uc = across / ReliefLatticeM;
        double a0 = Math.Floor(ua), c0 = Math.Floor(uc), fa = ua - a0, fc = uc - c0;
        double Node(double da, double dc) => NodeConvexity(heightAt, (a0 + da) * ReliefLatticeM, (c0 + dc) * ReliefLatticeM);
        double v00 = Node(0, 0);
        double v10 = fa > 0.0 ? Node(1, 0) : 0.0;
        double v01 = fc > 0.0 ? Node(0, 1) : 0.0;
        double v11 = fa > 0.0 && fc > 0.0 ? Node(1, 1) : 0.0;
        return (v00 * (1.0 - fa) + v10 * fa) * (1.0 - fc) + (v01 * (1.0 - fa) + v11 * fa) * fc;
    }

    private static uint Pcg(uint v)
    {
        unchecked
        {
            uint state = v * 747796405u + 2891336453u;
            uint word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
            return (word >> 22) ^ word;
        }
    }

    /// <summary>Flat tone of one bed, uniform in [-1, 1].</summary>
    public static double Tone(double bed, uint seed)
    {
        unchecked
        {
            uint k = Pcg((uint)(int)bed ^ (seed * 2654435761u));
            return (double)(float)(k >> 8) / 8388607.5 - 1.0;
        }
    }

    /// <summary>Box-filtered bed tone at absolute height h (m); fw is the height footprint of the pixel; period is the band period in metres.</summary>
    public static double StrataBand(double h, double fw, double period, uint seed)
    {
        double u = h / period;
        double w = Math.Max(fw / period, 1e-5);
        if (w >= 1.0) return 0.0;
        double n0 = Math.Floor(u - 0.5 * w);
        double f = Math.Clamp((u + 0.5 * w - (n0 + 1.0)) / w, 0.0, 1.0);
        double b = Tone(n0, seed) + (Tone(n0 + 1.0, seed) - Tone(n0, seed)) * f;
        return b * (1.0 - SmoothStep(StrataFadeWStart, StrataFadeWEnd, w));
    }

    public static double StrataFactor(double h, double fw, double stepM, uint seed, double contrast = DefaultStrataContrast)
    {
        double bed = StrataBand(h, fw, stepM, seed);
        double sub = StrataBand(h, fw, stepM / StrataSubDiv, seed ^ 40503u);
        return (1.0 + contrast * bed) * (1.0 + contrast * StrataSubRatio * sub);
    }
}
